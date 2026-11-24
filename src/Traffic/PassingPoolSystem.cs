using System;
using System.Collections.Generic;
using System.Diagnostics;
using Game;
using Game.Net;
using Game.Simulation;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
namespace Overtake
{
    // Smoothed main-thread cost of the mod per simulation tick, for the
    // vehicle panel. Includes waits for native jobs the mod's reads complete.
    internal sealed class PassingMetrics
    {
        private const double Smoothing = .05;
        private uint frame = uint.MaxValue;
        private double tick;
        internal double AverageMilliseconds { get; private set; }

        internal void Add(uint simulationFrame, double milliseconds)
        {
            if (simulationFrame != frame)
            {
                if (frame != uint.MaxValue) AverageMilliseconds += (tick - AverageMilliseconds) * Smoothing;
                frame = simulationFrame;
                tick = 0;
            }
            tick += milliseconds;
        }
    }
    public partial class PassingPoolSystem : GameSystemBase
    {
        private readonly List<PassingPool> pools = new List<PassingPool>();
        private readonly Dictionary<Entity, PassingPool> leaders = new Dictionary<Entity, PassingPool>();
        private readonly List<NativePassingTraversal> traversals = new List<NativePassingTraversal>();
        private readonly Dictionary<Entity, NativePassingTraversal> leases = new Dictionary<Entity, NativePassingTraversal>();
        internal readonly SupportedPassingRoads Roads = new SupportedPassingRoads();
        internal readonly NativeFollowingQueue Queue = new NativeFollowingQueue();
        internal readonly PoolMembership<Entity> Membership = new PoolMembership<Entity>();
        internal readonly PassingMetrics Metrics = new PassingMetrics();
        // The wave system admits serially. A committed lease takes its own copy.
        internal readonly CarNavigationLane[] HomeRouteScratch = new CarNavigationLane[NativeVehicle.MaximumNavigationEntries + 1];
        internal IReadOnlyList<PassingPool> Pools => pools;
        internal IReadOnlyList<NativePassingTraversal> Traversals => traversals;
        internal int HeldTraversalCount => traversals.Count;
        internal bool TryTraversal(Entity vehicle, out NativePassingTraversal traversal) => leases.TryGetValue(vehicle, out traversal);
        internal void RegisterTraversal(PassingPool pool, NativePassingTraversal traversal)
        {
            leases.Add(traversal.Vehicle, traversal);
            traversals.Add(traversal);
            pool.Traversals.Add(traversal);
        }
        internal void FinishTraversal(NativePassingTraversal traversal)
        {
            bool completed = traversal.State == TraversalState.Completed;
            if (Membership.TryOwner(traversal.Vehicle, out var leader) && TryGet(leader, out var pool))
            {
                if (completed) pool.CompletedPasses++;
                pool.Traversals.Remove(traversal);
                Membership.Release(leader, traversal.Vehicle);
            }
            leases.Remove(traversal.Vehicle);
            traversals.Remove(traversal);
        }
        internal void Merge(PassingPool previous, PassingPool canonical)
        {
            if (!Membership.Transfer(previous.Leader, canonical.Leader)) return;
            foreach (var member in previous.Followers)
                if (!canonical.Followers.Contains(member)) canonical.Followers.Add(member);
            if (!leases.ContainsKey(previous.Leader) && !canonical.Followers.Contains(previous.Leader))
                canonical.Followers.Add(previous.Leader);
            canonical.Traversals.AddRange(previous.Traversals);
            canonical.CompletedPasses += previous.CompletedPasses;
            Queue.Invalidate();
            // Preserve one coordinate frame; leases join it only after exact native overlap is verified.
            if (canonical.Corridor == null) canonical.Corridor = previous.Corridor;
            else if (previous.Corridor != null) canonical.Corridor.TryAbsorb(previous.Corridor);
            canonical.LastBuildLane = Entity.Null;
            canonical.ObservationFrame = uint.MaxValue;
            canonical.Gate = PassingGate.Closed;
            leaders.Remove(previous.Leader);
            pools.Remove(previous);
        }
        internal bool EnabledPassing = true, DebugOverlayEnabled;
        internal void SetPassingEnabled(bool enabled)
        {
            if (EnabledPassing == enabled) return;
            EnabledPassing = enabled;
            if (enabled)
            {
                foreach (var pool in pools)
                    if (Roads.TryVehicle(EntityManager, pool.Leader)) pool.Dissolving = false;
                return;
            }
            for (int i = pools.Count - 1; i >= 0; i--)
            {
                Retire(pools[i]);
                if (pools[i].Traversals.Count == 0) Dissolve(i, pools[i]);
            }
            Queue.Invalidate();
            Roads.Clear();
        }
        private uint lastFrame = uint.MaxValue;
        private int nextSlot;
        internal bool TryGet(Entity leader, out PassingPool pool) => leaders.TryGetValue(leader, out pool);
        internal bool TryCreate(Entity leader, out PassingPool pool)
        {
            // A retiring pool only waits for its leases to clear; it never takes
            // new waiting members. A fresh pool forms after it dissolves.
            if (TryGet(leader, out pool)) return !pool.Dissolving;
            if (!Membership.TryClaim(leader, leader)) return false;
            pool = new PassingPool(leader) { Slot = nextSlot++ & 15 };
            leaders.Add(leader, pool);
            pools.Add(pool);
            return true;
        }
        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            pools.Clear();
            leaders.Clear();
            Membership.Reset();
            traversals.Clear();
            leases.Clear();
            savedPools = null;
            savedCorridors = null;
            Roads.Clear();
            Queue.Invalidate();
            lastFrame = uint.MaxValue;
            base.OnGamePreload(purpose, mode);
        }
        protected override void OnUpdate()
        {
            uint frame = World.GetOrCreateSystemManaged<SimulationSystem>().frameIndex;
            if (frame == lastFrame)
                return;
            lastFrame = frame;
            Roads.BeginFrame(frame);
            // Each pool is evaluated once per sixteen ticks, in its own slot, so a
            // city with many pools does not concentrate all of them in one frame.
            // EntityManager reads complete only the jobs writing the types they
            // touch; no whole-world job fence is needed here.
            if (pools.Count == 0)
                return;
            int slot = (int)(frame & 15);
            long started = Stopwatch.GetTimestamp();
            Queue.BeginFrame(frame);
            // Normalize actual obstructions before either pool builds or observes its world.
            for (int i = pools.Count - 1; i >= 0; i--)
            {
                var previousPool = pools[i];
                if (previousPool.Dissolving || previousPool.Slot != slot) continue;
                Entity canonical = Queue.Resolve(EntityManager, this, previousPool.Leader);
                if (canonical == Entity.Null) { Dissolve(i, previousPool); continue; }
                if (canonical == previousPool.Leader) continue;
                if (!TryCreate(canonical, out var canonicalPool)) continue;
                Merge(previousPool, canonicalPool);
                i = math.min(i, pools.Count);
            }
            for (int i = pools.Count - 1; i >= 0; i--)
            {
                var pool = pools[i];
                if (pool.Slot != slot) continue;
                pool.Refusals.Clear();
                if (!Roads.TryVehicle(EntityManager, pool.Leader)
                    || !NativeVehicle.TryMeasure(EntityManager, pool.Leader, out var leaderPosition, out float speed, out float leaderLength))
                {
                    Dissolve(i, pool);
                    if (pool.Traversals.Count != 0) ObserveRetiringPool(pool, frame);
                    continue;
                }
                pool.LeaderSpeedMetresPerSecond = math.max(0, speed);
                for (int member = pool.Followers.Count - 1; member >= 0; member--)
                {
                    Entity follower = pool.Followers[member];
                    if (Queue.Resolve(EntityManager, this, follower) == pool.Leader && Roads.TryVehicle(EntityManager, follower)) continue;
                    Membership.Release(pool.Leader, follower);
                    pool.Followers.RemoveAt(member);
                }
                if (pool.Followers.Count == 0 && pool.Traversals.Count == 0)
                {
                    Dissolve(i, pool);
                    continue;
                }
                var leaderLane = EntityManager.GetComponentData<CarCurrentLane>(pool.Leader).m_Lane;
                if (!pool.Dissolving && pool.Corridor != null
                    && pool.Corridor.CoverageState == RoadCoverageState.StructuralBoundary
                    && !pool.Corridor.ContainsForwardLane(leaderLane) && pool.LastBuildLane != leaderLane)
                {
                    // A crossed restriction or junction belongs to the previous
                    // road horizon. Active leases retain that geometry until clear.
                    pool.LastBuildLane = leaderLane;
                    if (PassingCorridor.TryCreate(EntityManager, pool.Leader, out var currentRoad))
                    {
                        pool.Corridor = currentRoad;
                        pool.ObservationFrame = uint.MaxValue;
                        pool.Gate = PassingGate.Closed;
                    }
                }
                if (pool.Corridor == null && !pool.Dissolving)
                {
                    // Seed geometry at the rearmost follower so the whole queue is covered.
                    Entity anchor = pool.Leader;
                    float back = float.PositiveInfinity;
                    float3 leaderForward = math.forward(EntityManager.GetComponentData<Game.Objects.Transform>(pool.Leader).m_Rotation);
                    foreach (var follower in pool.Followers)
                    {
                        if (!NativeVehicle.TryMeasure(EntityManager, follower, out var position, out _, out _)) continue;
                        float along = math.dot(position - leaderPosition, leaderForward);
                        if (along >= back) continue;
                        anchor = follower;
                        back = along;
                    }
                    if (EntityManager.HasComponent<CarCurrentLane>(anchor))
                    {
                        var lane = EntityManager.GetComponentData<CarCurrentLane>(anchor).m_Lane;
                        if (lane != pool.LastBuildLane)
                        {
                            if (PassingCorridor.TryCreate(EntityManager, anchor, out pool.Corridor))
                                pool.LastBuildLane = lane;
                        }
                    }
                }
                if (pool.Corridor == null)
                {
                    if (pool.Dissolving || pool.Followers.Count == 0)
                        Dissolve(i, pool);
                    continue;
                }
                bool valid = pool.Corridor.IsValid(EntityManager);
                pool.Corridor.TryStation(leaderPosition, out float leaderStation);
                pool.LeaderFrontMetres = leaderStation + leaderLength * .5f;
                if (!valid)
                {
                    pool.Corridor = null;
                    pool.LastBuildLane = Entity.Null;
                    pool.ObservationFrame = uint.MaxValue;
                    pool.Gate = PassingGate.Closed;
                    continue;
                }
                bool leasesBound = true;
                foreach (var traversal in pool.Traversals)
                {
                    if (traversal.Road == null) { leasesBound = false; continue; }
                    if (traversal.Road == pool.Corridor) continue;
                    if (!traversal.Road.IsValid(EntityManager) || !pool.Corridor.TryAbsorb(traversal.Road)
                        || !traversal.TryBindRoad(EntityManager, pool.Corridor)) leasesBound = false;
                }
                float required = math.max(400, pool.RequestedLookaheadMetres);
                pool.RequestedLookaheadMetres = 400;
                foreach (var traversal in pool.Traversals)
                    if (traversal.LeaseHeld)
                        required = math.max(required, traversal.RequiredRoadEndMetres - pool.LeaderFrontMetres);
                var coverage = pool.Corridor.RefreshAhead(EntityManager, pool.LeaderFrontMetres + required, pool.LeaderFrontMetres);
                if (!CorridorTraffic.TryObserve(EntityManager, pool.Corridor, pool.Leader, out var observedLeader))
                {
                    pool.ObservationFrame = uint.MaxValue;
                    pool.Gate = new GateDecision(GateState.Red, GateReason.IncompleteTraffic, 0, 0, 0);
                    continue;
                }
                pool.LeaderFrontMetres = observedLeader.FrontMetres;
                pool.LeaderSpeedMetresPerSecond = math.max(0, observedLeader.MaximumStationProgressMetresPerSecond);
                // Entry can use only the verified prefix. Shared observation also
                // serves committed returns while the horizon extends.
                float passingAhead = math.max(0, pool.Corridor.PassingEndFrom(pool.LeaderFrontMetres) - pool.LeaderFrontMetres);
                if (passingAhead > 0 || pool.Traversals.Count != 0)
                {
                    pool.Traffic.Refresh(EntityManager, pool.Corridor, frame, pool.LeaderFrontMetres);
                }
                pool.ObservationFrame = valid && passingAhead > 0 && leasesBound && pool.Traffic.Complete
                    ? frame : uint.MaxValue;
                if (passingAhead <= 0)
                {
                    pool.ThreatVehicle = Entity.Null;
                    pool.Gate = new GateDecision(GateState.Purple,
                        pool.LeaderFrontMetres < pool.Corridor.LengthMetres ? GateReason.RestrictedRoad : GateReason.RoadEnd,
                        0, 0, 0);
                    continue;
                }
                pool.Members.Clear();
                foreach (var observation in pool.Traffic.Observations)
                    pool.Members[observation.Root] = observation;
                pool.Members[pool.Leader] = observedLeader;
                foreach (var follower in pool.Followers)
                    if (!pool.Members.ContainsKey(follower)
                        && CorridorTraffic.TryObserve(EntityManager, pool.Corridor, follower, out var memberObservation))
                        pool.Members[follower] = memberObservation;
                sortPool = pool;
                pool.Followers.Sort(byStationDescending);
                sortPool = null;
                for (int member = 0; member < pool.Followers.Count; member++)
                {
                    Entity follower = pool.Followers[member];
                    if (!pool.Members.TryGetValue(follower, out var observed) || observed.RearMetres > pool.LeaderFrontMetres)
                    {
                        Membership.Release(pool.Leader, follower);
                        pool.Followers.RemoveAt(member--);
                        continue;
                    }
                }
                ObserveThreat(pool);
                float threatDistance = pool.ThreatDistanceMetres;
                var previous = pool.Gate.State;
                pool.Gate = PassingGate.EvaluateShared(passingAhead, threatDistance,
                    pool.Corridor.OpposingClosingSpeed(pool.ThreatSpeedMetresPerSecond),
                    pool.LeaderSpeedMetresPerSecond,
                    valid && leasesBound && pool.Traffic.Complete && !pool.Dissolving && EnabledPassing,
                    previous, coverage);
                if (previous != pool.Gate.State)
                PruneRoadHistory(pool);
                if (pool.Followers.Count == 0 && pool.Traversals.Count == 0)
                    pool.Dissolving = true;
                if (pool.Dissolving && pool.Traversals.Count == 0)
                {
                    Dissolve(i, pool);
                }
            }
            double elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            Metrics.Add(frame, elapsed);
        }
        // Reused comparer: a capturing lambda here allocated on every evaluation.
        private PassingPool sortPool;
        private Comparison<Entity> byStationDescending;
        private int CompareStationDescending(Entity a, Entity b) => Station(sortPool, b).CompareTo(Station(sortPool, a));
        protected override void OnCreate()
        {
            base.OnCreate();
            byStationDescending = CompareStationDescending;
        }
        private float Station(PassingPool pool, Entity vehicle)
        {
            return pool.Members.TryGetValue(vehicle, out var observed) ? observed.FrontMetres : float.NegativeInfinity;
        }
        private void PruneRoadHistory(PassingPool pool)
        {
            var road = pool.Corridor;
            if (!pool.Members.TryGetValue(pool.Leader, out var leader)) return;
            float oldest = leader.RearMetres;
            foreach (var follower in pool.Followers)
            {
                if (!EntityManager.HasComponent<CarCurrentLane>(follower)
                    || !road.ContainsForwardLane(EntityManager.GetComponentData<CarCurrentLane>(follower).m_Lane)
                    || !pool.Members.TryGetValue(follower, out var observed)) return;
                oldest = math.min(oldest, observed.RearMetres);
            }
            foreach (var traversal in pool.Traversals)
            {
                if (traversal.Road != road || !traversal.TryGetRetentionStart(EntityManager, out float retained)) return;
                oldest = math.min(oldest, retained);
            }
            road.PruneBefore(oldest);
        }
        private void Dissolve(int index, PassingPool pool)
        {
            if (pool.Traversals.Count != 0)
            {
                Retire(pool);
                return;
            }
            Membership.Dissolve(pool.Leader);
            leaders.Remove(pool.Leader);
            pools.RemoveAt(index);
        }
        private void Retire(PassingPool pool)
        {
            pool.Dissolving = true;
            foreach (var follower in pool.Followers) Membership.Release(pool.Leader, follower);
            pool.Followers.Clear();
            pool.Refusals.Clear();
            pool.Gate = PassingGate.Closed;
        }
        private void ObserveRetiringPool(PassingPool pool, uint frame)
        {
            if (pool.Corridor == null || !pool.Corridor.IsValid(EntityManager))
            {
                pool.ObservationFrame = uint.MaxValue;
                return;
            }
            pool.Traffic.Refresh(EntityManager, pool.Corridor, frame, pool.LeaderFrontMetres);
            pool.ObservationFrame = frame;
            pool.Members.Clear();
            foreach (var observation in pool.Traffic.Observations) pool.Members[observation.Root] = observation;
            ObserveThreat(pool);
        }
        private void ObserveThreat(PassingPool pool)
        {
            pool.ThreatDistanceMetres = pool.Traffic.NearestOncomingDistanceMetres;
            pool.ThreatSpeedMetresPerSecond = pool.Traffic.NearestOncomingSpeedMetresPerSecond;
            pool.ThreatVehicle = pool.Traffic.NearestOncoming;
            foreach (var observation in pool.Traffic.Observations)
            {
                if (!OpposingThreat(pool, observation)) continue;
                float distance = math.max(0, observation.RearMetres - pool.LeaderFrontMetres);
                if (distance >= pool.ThreatDistanceMetres) continue;
                pool.ThreatDistanceMetres = distance;
                pool.ThreatSpeedMetresPerSecond = math.max(0, -observation.MinimumSpeedMetresPerSecond);
                pool.ThreatVehicle = observation.Root;
            }
        }

        private bool OpposingThreat(PassingPool pool, VehicleObservation observed)
        {
            if (!observed.OccupiesOpposing || observed.FrontMetres < pool.LeaderFrontMetres) return false;
            bool knownPasser = Membership.TryOwner(observed.Root, out var owner) && owner == pool.Leader
                || leases.TryGetValue(observed.Root, out var lease) && lease.LeaseHeld
                    && (lease.State == TraversalState.Passing || lease.State == TraversalState.Returning);
            return PassingGate.IsOpposingThreat(observed.SpeedMetresPerSecond, knownPasser);
        }

    }
}
