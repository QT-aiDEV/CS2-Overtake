using System.Collections.Generic;
using Game.Vehicles;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Simulation;
using Colossal.Mathematics;
using Unity.Entities;
using Unity.Mathematics;

namespace Overtake
{
    // Native speed blockers are transient. Route-local occupancy supplies queue links between updates.
    internal sealed class NativeFollowingQueue
    {
        private readonly Dictionary<Entity, Entity> resolved = new Dictionary<Entity, Entity>();
        private readonly HashSet<Entity> visited = new HashSet<Entity>();
        private readonly HashSet<Entity> occupantRoots = new HashSet<Entity>();
        private readonly HashSet<Entity> measuredLanes = new HashSet<Entity>();
        private readonly List<Entity> chain = new List<Entity>();
        private readonly Dictionary<Entity, List<QueueVehicle>> laneOccupants = new Dictionary<Entity, List<QueueVehicle>>();
        private readonly Stack<List<QueueVehicle>> spareLists = new Stack<List<QueueVehicle>>();
        private uint frame = uint.MaxValue;
        private readonly struct QueueVehicle
        {
            internal readonly Entity Root;
            // Parameter and body length are populated only by MeasuredOccupants.
            internal readonly float Parameter, SpeedMetresPerSecond, FreeSpeedMetresPerSecond, LengthMetres;
            internal QueueVehicle(Entity root, float parameter, float speed, float freeSpeed, float length = 0)
            { Root = root; Parameter = parameter; SpeedMetresPerSecond = speed; FreeSpeedMetresPerSecond = freeSpeed; LengthMetres = length; }
        }
        internal IReadOnlyList<Entity> Chain => chain;

        private bool HasPotentialObstruction(EntityManager em, PassingPoolSystem pools, Entity follower)
        {
            if (!em.HasComponent<CarCurrentLane>(follower))
                return false;
            var current = em.GetComponentData<CarCurrentLane>(follower);
            if (!math.all(math.isfinite(current.m_CurvePosition)) || current.m_CurvePosition.x >= current.m_CurvePosition.z
                || !SupportedPassingRoads.QueueLane(em, current.m_Lane)) return false;
            Entity lane = current.m_Lane;
            var first = Occupants(em, lane);
            bool foundSelf = false;
            float speed = 0, freeSpeed = 0;
            foreach (var occupant in first)
                if (occupant.Root == follower)
                { foundSelf = true; speed = occupant.SpeedMetresPerSecond; freeSpeed = occupant.FreeSpeedMetresPerSecond; break; }
            // An incomplete native occupancy publication cannot prove irrelevance.
            // The exact route-local fallback remains available in this case.
            if (!foundSelf) return true;
            int navigationIndex = 0;
            for (int step = 0; step < NativeVehicle.MaximumNavigationEntries; step++)
            {
                foreach (var ahead in Occupants(em, lane))
                {
                    if (ahead.Root == follower || pools.TryTraversal(ahead.Root, out _)) continue;
                    // Native lane intervals are navigation targets, not body positions.
                    // A possible obstruction anywhere in the lane warrants exact ordering.
                    if (DiscoveryEligibility.NeedsOccupancy(speed, freeSpeed,
                        ahead.SpeedMetresPerSecond, ahead.FreeSpeedMetresPerSecond)) return true;
                }
                if (!em.HasBuffer<CarNavigationLane>(follower)) return false;
                var navigation = em.GetBuffer<CarNavigationLane>(follower, true);
                Entity next = Entity.Null;
                while (navigationIndex < math.min(navigation.Length, NativeVehicle.MaximumNavigationEntries))
                {
                    var entry = navigation[navigationIndex++];
                    if (entry.m_Lane == lane) continue;
                    if (!math.all(math.isfinite(entry.m_CurvePosition)) || entry.m_CurvePosition.x > entry.m_CurvePosition.y
                        || !Connected(em, lane, entry.m_Lane) || !SupportedPassingRoads.QueueLane(em, entry.m_Lane)) return false;
                    next = entry.m_Lane;
                    break;
                }
                if (next == Entity.Null) return false;
                lane = next;
            }
            return false;
        }
        internal void Invalidate()
        {
            resolved.Clear();
            measuredLanes.Clear();
            foreach (var occupants in laneOccupants.Values) { occupants.Clear(); spareLists.Push(occupants); }
            laneOccupants.Clear();
        }
        internal void BeginFrame(uint currentFrame)
        {
            if (frame == currentFrame) return;
            frame = currentFrame;
            Invalidate();
        }
        internal Entity Resolve(EntityManager em, PassingPoolSystem pools, Entity vehicle)
        {
            chain.Clear();
            visited.Clear();
            Entity current = vehicle, canonical = Entity.Null;
            for (int depth = 0; depth < 256; depth++)
            {
                if (!visited.Add(current)) break;
                if (pools.TryTraversal(current, out _))
                {
                    if (pools.Membership.TryOwner(current, out var owner) && pools.TryGet(owner, out _)) canonical = owner;
                    break;
                }
                if (!pools.Roads.TryVehicle(em, current)) break;
                chain.Add(current);
                if (resolved.TryGetValue(current, out canonical)) break;
                canonical = current;
                bool hasBlocker = NativeVehicle.TryContinuingBlocker(em, current, out var next);
                bool inspectOccupancy = !hasBlocker && (pools.Membership.TryOwner(current, out _)
                    || HasPotentialObstruction(em, pools, current));
                if (!hasBlocker && (!inspectOccupancy || !TryOccupancyBlocker(em, pools, current, out next)))
                {
                    if (TryRetainWaveOwner(em, pools, current, out var waveOwner)) canonical = waveOwner;
                    break;
                }
                // A continuous queue keeps its real blocker across a road-support boundary.
                // Unsupported obstruction geometry cannot promote a follower into a new leader.
                Entity roadVehicle = next;
                if (pools.TryTraversal(next, out _) && pools.Membership.TryOwner(next, out var blockerOwner)
                    && pools.TryGet(blockerOwner, out _)) roadVehicle = blockerOwner;
                if (!Contiguous(em, current, roadVehicle)) break;
                if (!pools.Roads.TryVehicle(em, roadVehicle))
                {
                    canonical = Entity.Null;
                    break;
                }
                current = next;
                canonical = Entity.Null;
            }
            foreach (var member in chain) resolved[member] = canonical;
            return canonical;
        }
        private static bool TryRetainWaveOwner(EntityManager em, PassingPoolSystem pools, Entity follower, out Entity owner)
        {
            owner = Entity.Null;
            if (!pools.Membership.TryOwner(follower, out var leader) || leader == follower
                || !pools.TryGet(leader, out var pool) || pool.Dissolving || pool.Corridor == null
                || pool.Traversals.Count == 0 || !Contiguous(em, follower, leader)
                || !NativeVehicle.TryMeasure(em, follower, out var followerPosition, out _, out float followerLength)
                || !NativeVehicle.TryMeasure(em, leader, out var leaderPosition, out _, out float leaderLength)) return false;
            var road = pool.Corridor;
            Entity followerLane = em.GetComponentData<CarCurrentLane>(follower).m_Lane;
            Entity leaderLane = em.GetComponentData<CarCurrentLane>(leader).m_Lane;
            if (!road.ContainsForwardLane(followerLane) || !road.ContainsForwardLane(leaderLane)
                || !road.TryStation(followerLane, followerPosition, out float followerStation, out _)
                || !road.TryStation(leaderLane, leaderPosition, out float leaderStation, out _)) return false;
            float followerFront = followerStation + followerLength * .5f;
            float leaderRear = leaderStation - leaderLength * .5f;
            if (followerFront >= leaderRear) return false;
            // Peeling members temporarily remove the native blocker chain. Keep
            // its owner only while a leased member still bridges that physical gap.
            foreach (var traversal in pool.Traversals)
            {
                if (!traversal.LeaseHeld || traversal.Road != road || traversal.State == TraversalState.Aborted
                    || !em.HasComponent<CarCurrentLane>(traversal.Vehicle)
                    || !NativeVehicle.TryMeasure(em, traversal.Vehicle, out var position, out _, out float length)
                    || !road.TryStation(em.GetComponentData<CarCurrentLane>(traversal.Vehicle).m_Lane,
                        position, out float station, out _)) continue;
                if (station + length * .5f <= followerFront || station - length * .5f >= leaderRear) continue;
                owner = leader;
                return true;
            }
            return false;
        }
        private bool TryOccupancyBlocker(EntityManager em, PassingPoolSystem pools, Entity follower, out Entity blocker)
        {
            blocker = Entity.Null;
            if (!NativeVehicle.TryMeasure(em, follower, out var position, out float speed, out float length)
                || speed < 0 || !NativeVehicle.TryCarLimits(em, follower, out var car)
                || !em.HasComponent<PseudoRandomSeed>(follower)) return false;
            var current = em.GetComponentData<CarCurrentLane>(follower);
            var rules = em.GetComponentData<Game.Net.CarLane>(current.m_Lane);
            rules.m_SpeedLimit *= VehicleUtils.GetSpeedLimitFactor(em.GetComponentData<Car>(follower));
            float desiredSpeed = NativeVehicle.LaneDriveSpeed(em, car, rules, current.m_Lane);
            VehicleUtils.GetDrivingStyle(frame, em.GetComponentData<PseudoRandomSeed>(follower),
                em.HasComponent<Bicycle>(follower), out float safetyTime);
            float reactionSeconds = NativeTiming.StepSeconds + safetyTime;
            float maximumDistance = VehicleUtils.GetBrakingDistance(car, speed, reactionSeconds) + length;
            if (!math.isfinite(desiredSpeed) || !math.isfinite(maximumDistance)) return false;
            Entity lane = current.m_Lane;
            MathUtils.Distance(em.GetComponentData<Game.Net.Curve>(lane).m_Bezier, position, out float parameter);
            float distance = 0;
            int navigationIndex = 0;
            for (int step = 0; step < NativeVehicle.MaximumNavigationEntries; step++)
            {
                var occupants = MeasuredOccupants(em, lane);
                int low = 0, high = occupants.Count;
                while (low < high)
                {
                    int middle = (low + high) / 2;
                    if (occupants[middle].Parameter < parameter) low = middle + 1;
                    else high = middle;
                }
                for (int i = low; i < occupants.Count; i++)
                {
                    var ahead = occupants[i];
                    if (ahead.Root == follower || pools.TryTraversal(ahead.Root, out _)) continue;
                    var curve = em.GetComponentData<Game.Net.Curve>(lane).m_Bezier;
                    float centreDistance = distance + MathUtils.Length(curve, new Bounds1(parameter, ahead.Parameter));
                    float bodyGap = math.max(0, centreDistance - (length + ahead.LengthMetres) * .5f);
                    // A queue link requires a real restriction on native free drive speed,
                    // within the driver's following/braking horizon, on its own route.
                    float followingDistance = math.max(0, VehicleUtils.GetBrakingDistance(car, speed, reactionSeconds)
                        - VehicleUtils.GetBrakingDistance(car, math.max(0, ahead.SpeedMetresPerSecond), 0));
                    if (bodyGap <= math.max(2, followingDistance) && ahead.SpeedMetresPerSecond < desiredSpeed)
                    { blocker = ahead.Root; return true; }
                    return false;
                }
                distance += MathUtils.Length(em.GetComponentData<Game.Net.Curve>(lane).m_Bezier, new Bounds1(parameter, 1));
                if (distance > maximumDistance || !em.HasBuffer<CarNavigationLane>(follower)) return false;
                var navigation = em.GetBuffer<CarNavigationLane>(follower, true);
                Entity next = Entity.Null;
                while (navigationIndex < math.min(navigation.Length, NativeVehicle.MaximumNavigationEntries))
                {
                    var entry = navigation[navigationIndex++];
                    if (entry.m_Lane == lane) continue;
                    if (!math.all(math.isfinite(entry.m_CurvePosition)) || entry.m_CurvePosition.x > entry.m_CurvePosition.y
                        || !Connected(em, lane, entry.m_Lane) || !SupportedPassingRoads.QueueLane(em, entry.m_Lane)) return false;
                    next = entry.m_Lane;
                    parameter = entry.m_CurvePosition.x;
                    break;
                }
                if (next == Entity.Null) return false;
                lane = next;
            }
            return false;
        }
        private List<QueueVehicle> Occupants(EntityManager em, Entity lane)
        {
            if (laneOccupants.TryGetValue(lane, out var occupants)) return occupants;
            occupants = spareLists.Count > 0 ? spareLists.Pop() : new List<QueueVehicle>();
            laneOccupants.Add(lane, occupants);
            if (!em.HasBuffer<LaneObject>(lane)) return occupants;
            var references = em.GetBuffer<LaneObject>(lane, true);
            occupantRoots.Clear();
            foreach (var reference in references)
            {
                Entity root = NativeVehicle.Root(em, reference.m_LaneObject);
                if (root == Entity.Null || !occupantRoots.Add(root)
                    || !em.HasComponent<CarCurrentLane>(root)) continue;
                var current = em.GetComponentData<CarCurrentLane>(root);
                if (current.m_Lane != lane || current.m_ChangeLane != Entity.Null
                    || !math.all(math.isfinite(current.m_CurvePosition)) || current.m_CurvePosition.x > current.m_CurvePosition.z
                    || !TryRootMotion(em, root, out float speed, out float freeSpeed)) continue;
                occupants.Add(new QueueVehicle(root, 0, speed, freeSpeed));
            }
            return occupants;
        }
        private List<QueueVehicle> MeasuredOccupants(EntityManager em, Entity lane)
        {
            var occupants = Occupants(em, lane);
            if (!measuredLanes.Add(lane)) return occupants;
            var curve = em.GetComponentData<Game.Net.Curve>(lane).m_Bezier;
            int count = 0;
            for (int i = 0; i < occupants.Count; i++)
            {
                var hint = occupants[i];
                if (!NativeVehicle.TryMeasure(em, hint.Root, out var position, out float speed, out float length)) continue;
                MathUtils.Distance(curve, position, out float parameter);
                occupants[count++] = new QueueVehicle(hint.Root, parameter, speed, hint.FreeSpeedMetresPerSecond, length);
            }
            if (count < occupants.Count) occupants.RemoveRange(count, occupants.Count - count);
            occupants.Sort((a, b) => a.Parameter.CompareTo(b.Parameter));
            return occupants;
        }
        private static bool TryRootMotion(EntityManager em, Entity vehicle, out float speed, out float freeSpeed)
        {
            speed = freeSpeed = 0;
            if (!em.HasComponent<Moving>(vehicle) || !em.HasComponent<Transform>(vehicle)
                || !em.HasComponent<CarCurrentLane>(vehicle)
                || !em.HasComponent<Car>(vehicle)) return false;
            Entity lane = em.GetComponentData<CarCurrentLane>(vehicle).m_Lane;
            if (!SupportedPassingRoads.QueueLane(em, lane) || !NativeVehicle.TryCarLimits(em, vehicle, out var car)) return false;
            var rules = em.GetComponentData<Game.Net.CarLane>(lane);
            rules.m_SpeedLimit *= VehicleUtils.GetSpeedLimitFactor(em.GetComponentData<Car>(vehicle));
            freeSpeed = NativeVehicle.LaneDriveSpeed(em, car, rules, lane);
            var transform = em.GetComponentData<Transform>(vehicle);
            speed = math.dot(em.GetComponentData<Moving>(vehicle).m_Velocity, math.forward(transform.m_Rotation));
            return math.isfinite(speed) && speed >= 0 && math.isfinite(freeSpeed) && freeSpeed > 0;
        }

        private static bool Contiguous(EntityManager em, Entity follower, Entity blocker)
        {
            if (!PassingCorridor.Live(em, follower) || !PassingCorridor.Live(em, blocker)
                || !em.HasComponent<CarCurrentLane>(follower) || !em.HasComponent<CarCurrentLane>(blocker)) return false;
            var from = em.GetComponentData<CarCurrentLane>(follower);
            var to = em.GetComponentData<CarCurrentLane>(blocker);
            if (!math.all(math.isfinite(from.m_CurvePosition)) || !math.all(math.isfinite(to.m_CurvePosition))
                || from.m_CurvePosition.x > from.m_CurvePosition.z || to.m_CurvePosition.x > to.m_CurvePosition.z
                || !LiveLane(em, from.m_Lane) || !LiveLane(em, to.m_Lane)) return false;
            if (from.m_Lane == to.m_Lane)
            {
                if (!em.HasComponent<Transform>(follower) || !em.HasComponent<Transform>(blocker)) return false;
                var fromPosition = em.GetComponentData<Transform>(follower).m_Position;
                var toPosition = em.GetComponentData<Transform>(blocker).m_Position;
                if (!math.all(math.isfinite(fromPosition)) || !math.all(math.isfinite(toPosition))) return false;
                var curve = em.GetComponentData<Game.Net.Curve>(from.m_Lane).m_Bezier;
                MathUtils.Distance(curve, fromPosition, out float fromParameter);
                MathUtils.Distance(curve, toPosition, out float toParameter);
                return toParameter >= fromParameter;
            }

            // Follow only the follower's native driving reservations. A bend can
            // change pose and world-space gap without changing queue membership.
            Entity previous = from.m_Lane;
            bool hasNextReservation = false;
            if (em.HasBuffer<CarNavigationLane>(follower))
            {
                var navigation = em.GetBuffer<CarNavigationLane>(follower, true);
                int count = math.min(navigation.Length, NativeVehicle.MaximumNavigationEntries);
                for (int i = 0; i < count; i++)
                {
                    var entry = navigation[i];
                    if (!math.all(math.isfinite(entry.m_CurvePosition))
                        || entry.m_CurvePosition.x > entry.m_CurvePosition.y) return false;
                    if (entry.m_Lane == previous) continue;
                    if (!Connected(em, previous, entry.m_Lane)) return false;
                    if (entry.m_Lane == to.m_Lane) return true;
                    hasNextReservation = true;
                    previous = entry.m_Lane;
                }
            }
            // Short native buffers may not yet reserve the next lane. Exact
            // endpoint identity still proves an immediately connected blocker.
            return !hasNextReservation && Connected(em, from.m_Lane, to.m_Lane);
        }
        private static bool LiveLane(EntityManager em, Entity lane)
            => PassingCorridor.Live(em, lane) && em.HasComponent<Game.Net.Lane>(lane)
                && em.HasComponent<Game.Net.CarLane>(lane);
        private static bool Connected(EntityManager em, Entity from, Entity to)
            => LiveLane(em, from) && LiveLane(em, to)
                && em.GetComponentData<Game.Net.Lane>(from).m_EndNode.Equals(
                    em.GetComponentData<Game.Net.Lane>(to).m_StartNode);
    }
}
