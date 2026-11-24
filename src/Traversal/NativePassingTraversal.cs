using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using LaneFlags = Game.Vehicles.CarLaneFlags;
using VehicleTransform = Game.Objects.Transform;

namespace Overtake
{
    internal enum PassingEntryResult { Unavailable, CannotGain, WindowClosed, InsufficientClearance, EntryOccupied, Committed, NoRouteHome, DepartureBlocked, LeaderTurning }
    internal enum NativeReturnBlock
    {
        // Saved as an int: append new values only.
        None, LeaderNotCleared, IncompleteTraffic, NativeReservation, ForwardGap, Ownership, EnteringOpposing
    }
    internal enum TraversalState { Committed, Passing, Returning, Completed, Aborted }
    internal enum TraversalFailureReason
    {
        None, VehicleRemoved, NativeStateUnavailable, RoadChanged, RouteChanged,
        DetourConsumedBeforeEntry, ReturnedBeforeClearingLeader, NativeRecoveryRequested,
        // The pass was abandoned and the car merged back behind its leader.
        MergedBehindLeader,
        // The pass no longer fitted before the car left its lane; the native route was restored.
        PassCancelled
    }

    // Owns a temporary lane buffer. Native navigation owns movement, following and reservations.
    internal sealed partial class NativePassingTraversal
    {
        // Both supported navigators refill at indices 0..12. Real intervals beyond
        // that frontier keep the original route dormant until an explicit handback.
        private const int RefillFrontier = 13;
        internal Entity Vehicle { get; private set; }
        internal TraversalState State { get; private set; } = TraversalState.Committed;
        internal bool LeaseHeld { get; private set; } = true;
        internal TraversalFailureReason FailureReason { get; private set; }
        internal NativeReturnBlock ReturnBlock { get; private set; }
        internal PassingCorridor Road => corridor;
        internal float BufferedEndMetres { get; private set; }
        internal float MaximumPassingSpeedMetresPerSecond { get; private set; }
        internal float RequiredRoadEndMetres => homeCount > 0 ? homeEnd : BufferedEndMetres;
        private PassingCorridor corridor;
        private Entity passedLeader, destination;
        private Entity[] parts;
        private CarNavigationLane[] continuation;
        private PathElement pathCursor;
        private bool hasPathCursor;
        private CarNavigationLane[] owned;
        private bool returnIssued, enteredOpposing;
        // A contiguous prefix of the original native continuation. The remaining
        // entries are kept verbatim for vanilla, including turns after the landing.
        private int homeCount, originalPathIndex;
        private float homeEnd, opposingLimit;
        private bool terminalGuard, holdingReturn;
        // Oncoming traffic left no room to finish: the car eases off to tuck in behind the leader.
        private bool yielding;
        private float plannedCruise;
        private uint capFrame = uint.MaxValue;
        private float desiredRecoverySpeed;
        internal bool HoldingReturn => LeaseHeld && (holdingReturn || yielding) && !returnIssued && State == TraversalState.Passing;
        internal bool Yielding => yielding && HoldingReturn;

        // Native navigation and movement step each car on its own sixteenth of
        // the simulation. A missing car is handled immediately so cleanup is not delayed.
        internal bool InUpdateSlot(EntityManager em, int slot)
            => !em.Exists(Vehicle) || !em.HasComponent<UpdateFrame>(Vehicle)
                || em.GetSharedComponent<UpdateFrame>(Vehicle).m_Index == slot;

        private NativePassingTraversal(EntityManager em, Entity vehicle, PassingPool pool,
            Entity[] parts, CarNavigationLane[] continuation, CarNavigationLane[] owned, float bufferedEnd, float passingSpeed,
            int homeCount, float homeEnd, float opposingLimit, bool terminalGuard)
        {
            Vehicle = vehicle; passedLeader = pool.Leader; corridor = pool.Corridor;
            destination = em.GetComponentData<Target>(vehicle).m_Target;
            this.parts = parts; this.continuation = continuation; this.owned = owned;
            this.homeCount = homeCount; this.homeEnd = homeEnd; this.opposingLimit = opposingLimit;
            this.terminalGuard = terminalGuard;
            BufferedEndMetres = bufferedEnd;
            MaximumPassingSpeedMetresPerSecond = passingSpeed;
            var owner = em.GetComponentData<PathOwner>(vehicle);
            var path = em.GetBuffer<PathElement>(vehicle, true);
            originalPathIndex = owner.m_ElementIndex;
            hasPathCursor = owner.m_ElementIndex < path.Length;
            if (hasPathCursor) pathCursor = path[owner.m_ElementIndex];
        }

        internal static PassingEntryResult TryCommit(EntityManager em, Entity vehicle, PassingPoolSystem pools, PassingPool pool, uint frame,
            out NativePassingTraversal traversal)
        {
            traversal = null;
            var road = pool.Corridor;
            if (!PassingGate.AllowsCommitment(pool.Gate.State)) return PassingEntryResult.WindowClosed;
            if (pool.Dissolving || pool.ObservationFrame != frame || road == null || !Ready(em, vehicle)
                || !pool.Traffic.Complete || pool.Traffic.Corridor != road
                || !pool.Members.TryGetValue(vehicle, out var self)
                || !NativeVehicle.TryCarLimits(em, vehicle, out var car)) return PassingEntryResult.Unavailable;
            var current = em.GetComponentData<CarCurrentLane>(vehicle);
            var owner = em.GetComponentData<PathOwner>(vehicle);
            int first = FindLane(road, current.m_Lane, false);
            if (first < 0 || (owner.m_State & ~PathFlags.Debug) != 0 || current.m_ChangeLane != Entity.Null
                || current.m_ChangeProgress != 0 || current.m_CurvePosition.x >= current.m_CurvePosition.z
                || !math.all(math.isfinite(current.m_CurvePosition)) || !PlainLane(current.m_LaneFlags)
                || owner.m_ElementIndex < 0 || owner.m_ElementIndex > em.GetBuffer<PathElement>(vehicle, true).Length) return PassingEntryResult.Unavailable;
            var nav = em.GetBuffer<CarNavigationLane>(vehicle);
            if (nav.Length == 0 || nav.Length > NativeVehicle.MaximumNavigationEntries) return PassingEntryResult.Unavailable;
            var piece = road.Lanes[first];
            if (!road.TryStation(current.m_Lane, em.GetComponentData<CarNavigation>(vehicle).m_TargetPosition, out float target, out _)
                || current.m_CurvePosition.x < piece.ForwardStart || current.m_CurvePosition.z > piece.ForwardEnd
                || current.m_CurvePosition.y > current.m_CurvePosition.z)
                return PassingEntryResult.Unavailable;
            float reserved = piece.StartMetres + piece.DistanceAt(math.max(current.m_CurvePosition.x, current.m_CurvePosition.y));
            float frontier = math.max(target, reserved);
            int skipped = 0;
            while (skipped < nav.Length && nav[skipped].m_Lane == current.m_Lane)
            {
                if (!PlainLane(nav[skipped].m_Flags) || (nav[skipped].m_Flags & LaneFlags.Reserved) != 0) return PassingEntryResult.Unavailable;
                skipped++;
            }
            // Native reservations are retained in full; they are never erased to make a splice fit.
            for (int i = skipped; i < nav.Length; i++)
            {
                var entry = nav[i];
                if ((entry.m_Flags & LaneFlags.Reserved) == 0) continue;
                if (!PlainLane(entry.m_Flags)) return PassingEntryResult.Unavailable;
                int index = FindLane(road, entry.m_Lane, false);
                if (index < 0) return PassingEntryResult.Unavailable;
                var lane = road.Lanes[index];
                if (!math.all(math.isfinite(entry.m_CurvePosition))
                    || entry.m_CurvePosition.y < lane.ForwardStart || entry.m_CurvePosition.y > lane.ForwardEnd)
                    return PassingEntryResult.Unavailable;
                frontier = math.max(frontier, lane.StartMetres + lane.DistanceAt(entry.m_CurvePosition.y));
            }
            float speed = math.max(0, self.SpeedMetresPerSecond);
            float nextHomeRear = float.PositiveInfinity;
            if (speed <= PassingDeparture.CrawlSpeed || pool.LeaderSpeedMetresPerSecond <= PassingDeparture.CrawlSpeed)
            {
                foreach (var other in pool.Traffic.Observations)
                    if (other.Root != vehicle && other.FrontMetres > self.FrontMetres
                        && other.OccupiesForwardPath(self.LateralRadiusMetres))
                        nextHomeRear = math.min(nextHomeRear, other.RearMetres);
            }
            if (!PassingDeparture.TryPlan(frontier, self.FrontMetres, speed, self.LengthMetres,
                pool.LeaderSpeedMetresPerSecond, nextHomeRear, out float departure, out float enter)) return PassingEntryResult.DepartureBlocked;
            var route = pools.HomeRouteScratch;
            int routeLength = nav.Length - skipped + 1;
            route[0] = new CarNavigationLane { m_Lane = current.m_Lane,
                m_CurvePosition = current.m_CurvePosition.xz,
                m_Flags = LaneFlags.FixedLane | (current.m_LaneFlags & LaneFlags.HighBeams) };
            for (int i = 1; i < routeLength; i++) route[i] = nav[skipped + i - 1];
            if (!TryRouteHome(road, first, route, routeLength, departure, out int homeCount, out float homeEnd))
                return PassingEntryResult.NoRouteHome;
            homeEnd = math.min(homeEnd, road.PassingEndFrom(departure));
            float cap = PassingSpeed(em, vehicle, road, self.RearMetres, homeEnd, car);
            if (cap <= 0) return PassingEntryResult.Unavailable;
            float progressScale = math.min(1, road.MinimumOpposingStationScale);
            if (progressScale <= 0) return PassingEntryResult.Unavailable;
            // A marginal speed gap leaves the passer alongside the leader for far
            // longer than a real driver would accept; leave it in the queue.
            if (!OvertakeSpeed.WorthPassing(cap * progressScale, pool.LeaderSpeedMetresPerSecond))
                return PassingEntryResult.CannotGain;
            float stationUpperScale = road.MaximumOpposingStationScale;
            float opposingSpeed = road.OpposingClosingSpeed(pool.ThreatSpeedMetresPerSecond);
            float speedUpperBound = cap * stationUpperScale;
            // The forecast cruise does not control native speed. Leave a
            // merge interval for the higher physical capability as well.
            float mergeSpeedBound = math.max(speed, car.m_MaxSpeed) * stationUpperScale;
            float latestBoundary = homeEnd - ReturnClearance.SelectLanding(0, mergeSpeedBound) - self.LengthMetres;
            float available = math.min(road.PassingEndFrom(pool.LeaderFrontMetres) - pool.LeaderFrontMetres,
                pool.ThreatDistanceMetres);
            // Nobody overtakes a car that is about to turn across the oncoming lane.
            if (AnyTurnsAcross(em, pools, pool, self, latestBoundary)) return PassingEntryResult.LeaderTurning;
            if (!TryEstimateMotion(em, pools, pool, self, cap, progressScale, enter, car,
                latestBoundary, available, opposingSpeed, speedUpperBound, out var motion, out float required))
                return PassingEntryResult.Unavailable;
            pool.RequestedLookaheadMetres = math.max(pool.RequestedLookaheadMetres, required);
            if (required > available) return PassingEntryResult.InsufficientClearance;
            // The same test an ongoing pass re-runs, with spare time, so a fresh
            // commitment is never cancelled by the check that admitted it.
            if (!pool.Members.TryGetValue(pool.Leader, out var leaderNow)
                || !PassFits(road, pool.Traffic, vehicle, self, true, leaderNow, motion.CruiseSpeedMetresPerSecond,
                    NativeVehicle.StationAccelerationFloor(car, motion.CruiseSpeedMetresPerSecond, progressScale),
                    PassCommitment.EntryMarginSeconds))
                return PassingEntryResult.InsufficientClearance;
            foreach (var other in pool.Traffic.Observations)
            {
                if (other.Root == vehicle || !other.OccupiesOpposing) continue;
                if (other.FrontMetres <= self.RearMetres)
                {
                    // A same-direction car already using the opposing lane behind
                    // this one would be cut off by a departure in front of it.
                    if (!other.Oncoming && other.FrontMetres + math.max(0, other.MaximumStationProgressMetresPerSecond)
                        * (motion.ApproachSeconds + PassingGate.DepartureSeconds) + 10 > self.RearMetres)
                        return PassingEntryResult.EntryOccupied;
                    continue;
                }
                // Convoy: a committed passer ahead is moving the same way and
                // already caps this fit. Native following keeps the spacing.
                if (ConvoyLeader(pools, other, self)) continue;
                // The shared gate starts at the leader. Check the follower's
                // approach against opposing traffic inside that blind interval.
                float rear = other.Oncoming && other.FrontMetres < pool.LeaderFrontMetres
                    ? other.RearMetres - opposingSpeed * motion.CompletionSeconds : other.RearMetres;
                if (rear < enter + self.LengthMetres) return PassingEntryResult.EntryOccupied;
            }
            float clearAt = self.FrontMetres + motion.DistanceAt(motion.ClearSeconds);
            if (!math.isfinite(clearAt) || math.max(enter, clearAt) >= latestBoundary
                || !TryFindHomeEntry(road, route, homeCount,
                    ReturnClearance.SelectLanding(latestBoundary, mergeSpeedBound), out _, out _))
                return PassingEntryResult.NoRouteHome;

            var replacement = new List<CarNavigationLane>(NativeVehicle.MaximumNavigationEntries);
            var after = current;
            bool departureCovered = false;
            for (int i = 0; i < homeCount; i++)
            {
                var entry = route[i];
                var lane = road.Lanes[first + i];
                float end = lane.StartMetres + lane.DistanceAt(entry.m_CurvePosition.y);
                departureCovered = end >= departure;
                if (departureCovered)
                {
                    if ((entry.m_Flags & LaneFlags.Reserved) != 0 && end != departure) return PassingEntryResult.Unavailable;
                    entry.m_CurvePosition.y = lane.ForwardParameterAt(departure);
                }
                if (i == 0) after.m_CurvePosition.z = entry.m_CurvePosition.y;
                else replacement.Add(entry);
                if (departureCovered) break;
            }
            float endMetres = math.min(latestBoundary, math.max(clearAt, enter + Lookahead(car, speed)));
            bool terminalGuard = SnapTerminalEnd(enter, latestBoundary, ref endMetres);
            if (!departureCovered || !AppendDetour(road, enter, endMetres, replacement,
                math.min(10, (endMetres - enter) / (RefillFrontier + 1)), terminalGuard)
                || replacement.Count < RefillFrontier) return PassingEntryResult.Unavailable;
            var linked = ReadParts(em, vehicle);
            if (linked == null) return PassingEntryResult.Unavailable;
            var buffer = replacement.ToArray();
            var original = Copy(nav);
            var continuation = new CarNavigationLane[routeLength];
            Array.Copy(route, continuation, routeLength);
            var created = new NativePassingTraversal(em, vehicle, pool, linked, continuation, buffer, endMetres, cap,
                homeCount, homeEnd, latestBoundary, terminalGuard) { plannedCruise = motion.CruiseSpeedMetresPerSecond };
            after.m_CurvePosition.y = math.clamp(after.m_CurvePosition.y, after.m_CurvePosition.x, after.m_CurvePosition.z);
            after.m_LaneFlags |= LaneFlags.FixedLane;
            nav.EnsureCapacity(math.max(original.Length, buffer.Length));
            try { Replace(nav, buffer); em.SetComponentData(vehicle, after); }
            catch (Exception) { Replace(nav, original); em.SetComponentData(vehicle, current); return PassingEntryResult.Unavailable; }
            traversal = created;
            return PassingEntryResult.Committed;
        }

        private static bool ConvoyLeader(PassingPoolSystem pools, in VehicleObservation other, in VehicleObservation self)
            => !other.Oncoming && other.RearMetres > self.FrontMetres && other.MinimumStationProgressMetresPerSecond >= 0
                && pools.TryTraversal(other.Root, out var lead) && lead.LeaseHeld && !lead.HoldingReturn
                && lead.FailureReason == TraversalFailureReason.None && lead.State != TraversalState.Aborted;

        // Only the native ordered route prefix can justify a merge. A lane
        // repeated after a turn is never evidence of continuity through the turn.
        private static bool TryRouteHome(PassingCorridor road, int first, CarNavigationLane[] route,
            int routeLength, float departure, out int count, out float end)
        {
            count = 0;
            end = float.NegativeInfinity;
            for (int i = 0; i < routeLength && first + i < road.Lanes.Count; i++)
            {
                var lane = road.Lanes[first + i];
                var entry = route[i];
                var interval = entry.m_CurvePosition;
                if (entry.m_Lane != lane.ForwardLane || !PlainLane(entry.m_Flags)
                    || !math.all(math.isfinite(interval)) || interval.x < lane.ForwardStart
                    || interval.y > lane.ForwardEnd || interval.x >= interval.y
                    || (i != 0 && interval.x != lane.ForwardStart)) break;
                count++;
                end = lane.StartMetres + lane.DistanceAt(interval.y);
                if (interval.y != lane.ForwardEnd) break;
            }
            return count > 0 && end > departure;
        }

        internal void Advance(EntityManager em, CorridorTraffic traffic, bool roadValid, uint frame)
        {
            if (!LeaseHeld || returnIssued || State == TraversalState.Aborted || !Ready(em, Vehicle)) return;
            if (!RoutePreserved(em) || !NavigationOwned(em.GetBuffer<CarNavigationLane>(Vehicle, true)))
            { Abort(TraversalFailureReason.RouteChanged); return; }
            if (!roadValid) { RequestRecovery(em, TraversalFailureReason.RoadChanged); return; }
            if (!CorridorTraffic.TryObserve(em, corridor, Vehicle, out var self))
            { RequestRecovery(em, TraversalFailureReason.NativeStateUnavailable); return; }
            if (traffic != null && State == TraversalState.Passing && TryReturn(em, traffic, self, frame)) return;
            if (traffic != null && !yielding && !holdingReturn && !returnIssued)
            {
                var outlook = Reassess(em, traffic, self);
                if (outlook == PassOutlook.Cancel && State == TraversalState.Committed && TryRestoreRoute(em))
                { Abort(TraversalFailureReason.PassCancelled); return; }
                if (outlook == PassOutlook.DropBack && State == TraversalState.Passing) yielding = true;
            }
            if (Yielding && TryDropBack(em, traffic, self, frame)) return;
            var nav = em.GetBuffer<CarNavigationLane>(Vehicle);
            bool terminalRoad = terminalGuard || BufferedEndMetres >= opposingLimit
                || corridor.CoverageState == RoadCoverageState.StructuralBoundary;
            bool stoppedInHold = HoldingReturn && self.MaximumSpeedMetresPerSecond <= TerminalRecovery.RestSpeed;
            if (TerminalRecovery.CanRetryBehind(nav.Length, stoppedInHold) && terminalRoad && traffic != null && State == TraversalState.Passing
                && TryReturn(em, traffic, self, frame, requireLeaderClearance: false))
            {
                FailureReason = TraversalFailureReason.MergedBehindLeader;
                return;
            }
            if (terminalGuard && !yielding && State == TraversalState.Passing && nav.Length >= RefillFrontier
                && PrepareRecoveryCap(em, traffic, self, frame)) return;
            if (nav.Length >= TerminalRecovery.ReplenishBelow) return;
            float end = math.min(opposingLimit, math.min(corridor.PassingEndFrom(BufferedEndMetres), BufferedEndMetres + 200));
            bool addGuard = homeCount > 0 && end > BufferedEndMetres && SnapTerminalEnd(BufferedEndMetres, opposingLimit, ref end);
            var replacement = new List<CarNavigationLane>(NativeVehicle.MaximumNavigationEntries);
            for (int i = 0; i < nav.Length; i++) replacement.Add(nav[i]);
            if (end <= BufferedEndMetres || !AppendDetour(corridor, BufferedEndMetres, end, replacement, 10, addGuard)
                || replacement.Count < RefillFrontier)
            {
                if (nav.Length < RefillFrontier)
                    RequestRecovery(em, TraversalFailureReason.NativeRecoveryRequested);
                return;
            }
            var next = replacement.ToArray();
            var before = Copy(nav);
            nav.EnsureCapacity(math.max(before.Length, next.Length));
            try { Replace(nav, next); }
            catch (Exception) { Replace(nav, before); return; }
            owned = next;
            BufferedEndMetres = end;
            terminalGuard = addGuard;
            if (terminalGuard && !yielding && State == TraversalState.Passing)
                PrepareRecoveryCap(em, traffic, self, frame);
        }

        private static bool SnapTerminalEnd(float start, float limit, ref float end)
        {
            float guardStart = limit - TerminalRecovery.GuardMetres;
            if (start > guardStart || end < guardStart) return false;
            end = limit;
            return true;
        }

        private static bool AppendDetour(PassingCorridor road, float start, float end, List<CarNavigationLane> result,
            float maximumInterval, bool guard)
        {
            if (!guard) return AppendOpposingIntervals(road, start, end, result, maximumInterval);
            float guardStart = end - TerminalRecovery.GuardMetres;
            return (start >= guardStart || AppendOpposingIntervals(road, start, guardStart, result, maximumInterval))
                && AppendOpposingIntervals(road, guardStart, end, result, TerminalRecovery.GuardIntervalMetres);
        }

        private bool PrepareRecoveryCap(EntityManager em, CorridorTraffic traffic, in VehicleObservation self, uint frame)
        {
            if (!NativeVehicle.TryCarLimits(em, Vehicle, out var car)) return false;
            float remaining = opposingLimit - TerminalRecovery.GuardMetres - 5 - self.FrontMetres;
            if (!holdingReturn)
            {
                // Stopping is not an escape from an observed opposing conflict.
                // A held car remains owned and continues checking its home gap.
                if (!TerminalRecovery.ShouldSlow(remaining, self.MaximumSpeedMetresPerSecond, car.m_Braking)
                    || traffic == null || !traffic.Complete) return false;
                foreach (var other in traffic.Observations)
                    if (other.Root != Vehicle && other.OccupiesOpposing && other.FrontMetres >= self.RearMetres
                        && other.Oncoming) return false;
                holdingReturn = true;
            }
            desiredRecoverySpeed = TerminalRecovery.TargetSpeed(remaining, car.m_Braking);
            capFrame = frame;
            return true;
        }

        internal bool WantsRecoveryCap(uint frame) => HoldingReturn && capFrame == frame && corridor != null;
        internal float RecoverySpeedMetresPerSecond => desiredRecoverySpeed;

        internal bool WantsOvertakeSpeed => LeaseHeld && State == TraversalState.Passing && !returnIssued && !holdingReturn && !yielding
            && FailureReason == TraversalFailureReason.None && corridor != null;

        private bool TryReturn(EntityManager em, CorridorTraffic traffic, in VehicleObservation self, uint frame,
            bool requireLeaderClearance = true)
        {
            ReturnBlock = NativeReturnBlock.IncompleteTraffic;
            if (!traffic.Complete || traffic.Corridor != corridor) return false;
            if (requireLeaderClearance)
            {
                // A leader that turned off or parked no longer needs clearing;
                // the live gap check below still covers everything on the road.
                var seen = ObserveLeader(em, out var leader);
                if (seen == LeaderSighting.Unknown) return false;
                if (seen == LeaderSighting.Observed && !ReturnClearance.HasClearedLeader(self.RearMetres, leader.FrontMetres))
                { ReturnBlock = NativeReturnBlock.LeaderNotCleared; return false; }
            }
            var current = em.GetComponentData<CarCurrentLane>(Vehicle);
            int index = FindLane(corridor, current.m_Lane, true);
            ReturnBlock = NativeReturnBlock.EnteringOpposing;
            if (index < 0 || current.m_ChangeLane != Entity.Null || current.m_CurvePosition.x <= current.m_CurvePosition.z) return false;
            ReturnBlock = NativeReturnBlock.NativeReservation;
            var piece = corridor.Lanes[index];
            var nav = em.GetBuffer<CarNavigationLane>(Vehicle);
            if (!corridor.TryStation(em.GetComponentData<CarNavigation>(Vehicle).m_TargetPosition, out float target)
                || !corridor.TryStation(MathUtils.Position(piece.OpposingCurve, current.m_CurvePosition.y), out float reserved)) return false;
            float horizon = math.max(math.max(target, reserved), self.FrontMetres) + 5;
            for (int i = 0; i < nav.Length; i++)
                if ((nav[i].m_Flags & LaneFlags.Reserved) != 0)
                {
                    int next = FindLane(corridor, nav[i].m_Lane, true);
                    if (next < 0) return false;
                    if (!corridor.TryStation(MathUtils.Position(corridor.Lanes[next].OpposingCurve,
                        nav[i].m_CurvePosition.y), out float end)) return false;
                    horizon = math.max(horizon, end);
                }
            float boundary = piece.StartMetres + piece.DistanceAt(piece.ForwardParameterForOpposing(current.m_CurvePosition.z));
            int cut = 0;
            while (boundary < horizon && cut < nav.Length)
            {
                var entry = nav[cut++];
                int next = FindLane(corridor, entry.m_Lane, true);
                if (next < 0 || !corridor.TryStation(MathUtils.Position(corridor.Lanes[next].OpposingCurve,
                    entry.m_CurvePosition.y), out boundary)) return false;
            }
            if (boundary < horizon) return false;
            for (int i = cut; i < nav.Length; i++) if ((nav[i].m_Flags & LaneFlags.Reserved) != 0) return false;
            float landing = ReturnClearance.SelectLanding(boundary, self.StationSpeedMetresPerSecond);
            if (homeCount > 0 && (boundary > opposingLimit || landing >= homeEnd)) return false;
            float restartTravel = 0, restartSpeed = 0;
            bool restart = HoldingReturn && self.MaximumSpeedMetresPerSecond <= TerminalRecovery.RestSpeed;
            float mergeSeconds;
            if (restart)
            {
                if (!NativeVehicle.TryCarLimits(em, Vehicle, out var car)) return false;
                float progressScale = math.min(1, corridor.MinimumOpposingStationScale);
                if (progressScale <= 0) return false;
                float cruise = math.min(TerminalRecovery.RestartSpeedCap, car.m_MaxSpeed * .5f) * progressScale;
                restartTravel = landing - self.RearMetres;
                float floor = NativeVehicle.StationAccelerationFloor(car, cruise, progressScale);
                if (!TerminalRecovery.TryRestart(restartTravel, floor, cruise, out mergeSeconds)) return false;
                restartSpeed = math.min(car.m_MaxSpeed, math.sqrt(2 * car.m_Acceleration * restartTravel / progressScale));
                foreach (var other in traffic.Observations)
                    if (other.Root != Vehicle && other.OccupiesOpposing && other.FrontMetres >= self.FrontMetres
                        && (other.Oncoming || other.RearMetres < landing + self.LengthMetres)) return false;
            }
            else if (!self.TryMergeDuration(landing, out mergeSeconds)) return false;
            if (!LiveGapClear(em, traffic, self, frame, mergeSeconds, restartTravel, restartSpeed))
            { ReturnBlock = NativeReturnBlock.ForwardGap; return false; }
            var replacement = new List<CarNavigationLane>(NativeVehicle.MaximumNavigationEntries);
            for (int i = 0; i < cut; i++) replacement.Add(nav[i]);
            var owner = em.GetComponentData<PathOwner>(Vehicle);
            var afterOwner = owner;
            ReturnBlock = NativeReturnBlock.Ownership;
            if (!AppendHome(em, landing, replacement, ref afterOwner)
                || replacement.Count > NativeVehicle.MaximumNavigationEntries) return false;
            var before = Copy(nav);
            var nextBuffer = replacement.ToArray();
            nav.EnsureCapacity(math.max(before.Length, nextBuffer.Length));
            try { Replace(nav, nextBuffer); em.SetComponentData(Vehicle, afterOwner); }
            catch (Exception) { Replace(nav, before); em.SetComponentData(Vehicle, owner); return false; }
            owned = nextBuffer; returnIssued = true; holdingReturn = yielding = false; State = TraversalState.Returning; ReturnBlock = NativeReturnBlock.None;
            return true;
        }

        private bool AppendHome(EntityManager em, float landing, List<CarNavigationLane> result, ref PathOwner owner)
        {
            if (TryFindHomeEntry(corridor, continuation, homeCount > 0 ? homeCount : continuation.Length,
                landing, out int index, out float parameter))
            {
                var entry = continuation[index];
                entry.m_CurvePosition.x = parameter;
                result.Add(entry);
                for (int next = index + 1; next < continuation.Length; next++) result.Add(continuation[next]);
                return true;
            }
            if (homeCount > 0 || !TryHomeLane(corridor, landing, out var lane, out parameter)) return false;
            // Legacy saved detours may already be beyond their original native buffer. Resume
            // at the matching live route element, never by restoring a stale path index.
            var path = em.GetBuffer<PathElement>(Vehicle, true);
            for (int i = owner.m_ElementIndex; i < path.Length; i++)
            {
                var entry = path[i];
                if (entry.m_Target != lane
                    || parameter < entry.m_TargetDelta.x || parameter >= entry.m_TargetDelta.y) continue;
                result.Add(new CarNavigationLane { m_Lane = lane,
                    m_CurvePosition = new float2(parameter, entry.m_TargetDelta.y), m_Flags = LaneFlags.FixedLane });
                owner.m_ElementIndex = i + 1;
                return true;
            }
            return false;
        }

        private static bool TryFindHomeEntry(PassingCorridor road, CarNavigationLane[] route, int count,
            float landing, out int index, out float parameter)
        {
            index = -1;
            if (!TryHomeLane(road, landing, out var lane, out parameter)) return false;
            for (int i = 0; i < count; i++)
            {
                var entry = route[i];
                if (entry.m_Lane != lane || parameter < entry.m_CurvePosition.x
                    || parameter >= entry.m_CurvePosition.y) continue;
                index = i;
                return true;
            }
            return false;
        }

        private static bool TryHomeLane(PassingCorridor road, float landing, out Entity lane, out float parameter)
        {
            foreach (var candidate in road.Lanes)
                if (landing >= candidate.StartMetres && landing < candidate.StartMetres + candidate.LengthMetres)
                {
                    lane = candidate.ForwardLane;
                    parameter = candidate.ForwardParameterAt(landing);
                    return true;
                }
            lane = Entity.Null;
            parameter = 0;
            return false;
        }

        private bool LiveGapClear(EntityManager em, CorridorTraffic traffic, in VehicleObservation self, uint frame,
            float mergeSeconds, float restartTravel, float restartSpeed)
        {
            if (!NativeVehicle.TryCarLimits(em, Vehicle, out var car) || !em.HasComponent<PseudoRandomSeed>(Vehicle)) return false;
            float speed = math.max(restartSpeed, self.MaximumSpeedMetresPerSecond);
            VehicleUtils.GetDrivingStyle(frame, em.GetComponentData<PseudoRandomSeed>(Vehicle),
                em.HasComponent<Bicycle>(Vehicle), out float safety);
            foreach (var cached in traffic.Observations)
            {
                if (cached.Root == Vehicle) continue;
                if (!CorridorTraffic.TryObserve(em, corridor, cached.Root, out var other))
                {
                    // Gone, or no longer on this road since the snapshot: not in the gap.
                    if (!OnRoad(em, cached.Root)) continue;
                    return false;
                }
                if (!other.OccupiesForwardPath(self.LateralRadiusMetres)) continue;
                if (other.Oncoming || !NativeVehicle.TryCarLimits(em, other.Root, out var otherCar)
                    || !em.HasComponent<PseudoRandomSeed>(other.Root)) return false;
                float otherSpeed = math.max(0, other.MaximumSpeedMetresPerSecond);
                // Steering can anticipate the next endpoint. Check from now until
                // the linked rear clears this landing, not just the root's arrival.
                // Observed-speed projection is a local fit; native driving still owns braking.
                float ahead, behind;
                if (restartTravel > 0)
                    TerminalRecovery.RestartGaps(other.RearMetres - self.FrontMetres, other.MinimumStationProgressMetresPerSecond,
                        self.RearMetres - other.FrontMetres, other.MaximumStationProgressMetresPerSecond,
                        restartTravel, mergeSeconds, out ahead, out behind);
                else self.MinimumMergeGaps(other, mergeSeconds, out ahead, out behind);
                if (ahead > .9f)
                {
                    if (speed > NativeFollowingSpeed(car, otherCar, math.max(0, other.MinimumSpeedMetresPerSecond), ahead - .9f, NativeTiming.StepSeconds + safety)) return false;
                }
                else if (behind > .9f)
                {
                    VehicleUtils.GetDrivingStyle(frame, em.GetComponentData<PseudoRandomSeed>(other.Root),
                        em.HasComponent<Bicycle>(other.Root), out float rearSafety);
                    if (otherSpeed > NativeFollowingSpeed(otherCar, car, math.max(0, self.MinimumSpeedMetresPerSecond), behind - .9f, NativeTiming.StepSeconds + rearSafety)) return false;
                }
                else return false;
            }
            return true;
        }

        // These are the native moving-car braking branches, applied to live package gaps.
        internal static float NativeFollowingSpeed(CarData self, CarData ahead, float aheadSpeed, float gap, float safeStep)
        {
            if (aheadSpeed <= .001f) return VehicleUtils.GetMaxBrakingSpeed(self, gap, safeStep);
            float afterBraking = math.max(0, aheadSpeed - ahead.m_Braking * NativeTiming.StepSeconds * 2);
            return self.m_Braking >= ahead.m_Braking
                ? VehicleUtils.GetMaxBrakingSpeed(self, gap + afterBraking * safeStep, afterBraking, safeStep)
                : VehicleUtils.GetMaxBrakingSpeed(self, gap + VehicleUtils.GetBrakingDistance(ahead, afterBraking, safeStep), safeStep);
        }

        private enum PassOutlook { Continue, Cancel, DropBack }
        private enum LeaderSighting { Gone, Observed, Unknown }

        // Re-run the entry test with the latest traffic. Before the car has
        // left its lane a failed test cancels the pass; once it is out, the
        // car either finishes or, behind a moving leader, drops back.
        private PassOutlook Reassess(EntityManager em, CorridorTraffic traffic, in VehicleObservation self)
        {
            if (State != TraversalState.Committed && State != TraversalState.Passing) return PassOutlook.Continue;
            if (!NativeVehicle.TryCarLimits(em, Vehicle, out var car)) return PassOutlook.Continue;
            float scale = math.min(1, corridor.MinimumOpposingStationScale);
            if (scale <= 0) return PassOutlook.Continue;
            var seen = ObserveLeader(em, out var leader);
            if (seen == LeaderSighting.Unknown) return PassOutlook.Continue;
            bool hasLeader = seen == LeaderSighting.Observed;
            // Before pulling out, a leader that turned off or one about to turn
            // across the oncoming lane is reason enough to stay home.
            if (State == TraversalState.Committed
                && (!hasLeader || TurnsAcross(em, passedLeader, corridor, opposingLimit))) return PassOutlook.Cancel;
            float cruise = plannedCruise > 0 ? plannedCruise : MaximumPassingSpeedMetresPerSecond * scale;
            if (PassFits(corridor, traffic, Vehicle, self, hasLeader, leader, cruise,
                NativeVehicle.StationAccelerationFloor(car, cruise, scale), 0)) return PassOutlook.Continue;
            if (State == TraversalState.Committed) return PassOutlook.Cancel;
            if (!hasLeader) return PassOutlook.Continue;
            return PassCommitment.ShouldDropBack(leader.FrontMetres + PassingGate.ReturnClearanceMetres - self.RearMetres,
                self.FrontMetres - leader.RearMetres + PassCommitment.DropBackGapMetres,
                math.max(0, leader.MaximumStationProgressMetresPerSecond)) ? PassOutlook.DropBack : PassOutlook.Continue;
        }

        // Ease off below the leader's speed and take the first safe gap behind it.
        // Returns true once the car has been handed back home.
        private bool TryDropBack(EntityManager em, CorridorTraffic traffic, in VehicleObservation self, uint frame)
        {
            var seen = ObserveLeader(em, out var leader);
            if (seen == LeaderSighting.Gone)
            {
                // Nothing left to tuck in behind: the normal return takes over.
                yielding = false;
                return false;
            }
            if (traffic != null && TryReturn(em, traffic, self, frame, requireLeaderClearance: false))
            {
                if (seen != LeaderSighting.Observed || !ReturnClearance.HasClearedLeader(self.RearMetres, leader.FrontMetres))
                    FailureReason = TraversalFailureReason.MergedBehindLeader;
                return true;
            }
            if (seen == LeaderSighting.Observed)
                desiredRecoverySpeed = PassCommitment.DropBackSpeed(math.max(0, leader.MinimumSpeedMetresPerSecond));
            capFrame = frame;
            return false;
        }

        // Puts the original native route back while the car is still in its own lane.
        private bool TryRestoreRoute(EntityManager em)
        {
            if (enteredOpposing || returnIssued || !Ready(em, Vehicle)) return false;
            var current = em.GetComponentData<CarCurrentLane>(Vehicle);
            if (current.m_ChangeLane != Entity.Null || current.m_ChangeProgress != 0
                || current.m_CurvePosition.x >= current.m_CurvePosition.z) return false;
            var nav = em.GetBuffer<CarNavigationLane>(Vehicle);
            if (!NavigationOwned(nav)) return false;
            int index = -1;
            for (int i = 0; i < continuation.Length && index < 0; i++)
                if (continuation[i].m_Lane == current.m_Lane) index = i;
            if (index < 0 || current.m_CurvePosition.x > continuation[index].m_CurvePosition.y) return false;
            var restored = new CarNavigationLane[continuation.Length - index - 1];
            Array.Copy(continuation, index + 1, restored, 0, restored.Length);
            // Keep any reservation native made since the commitment.
            for (int i = 0; i < restored.Length && i < nav.Length; i++)
                if (nav[i].m_Lane == restored[i].m_Lane) restored[i].m_Flags |= nav[i].m_Flags & LaneFlags.Reserved;
            var after = current;
            after.m_CurvePosition.z = continuation[index].m_CurvePosition.y;
            var before = Copy(nav);
            nav.EnsureCapacity(math.max(before.Length, restored.Length));
            try { Replace(nav, restored); em.SetComponentData(Vehicle, after); }
            catch (Exception) { Replace(nav, before); em.SetComponentData(Vehicle, current); return false; }
            owned = restored;
            return true;
        }

        // Hand a lease that can no longer be supervised back to native driving.
        internal void Detach(EntityManager em)
        {
            if (!LeaseHeld || !Ready(em, Vehicle)) return;
            if (TryRestoreRoute(em)) Abort(TraversalFailureReason.PassCancelled);
            else if (enteredOpposing && !returnIssued && NavigationOwned(em.GetBuffer<CarNavigationLane>(Vehicle, true)))
                RequestRecovery(em, TraversalFailureReason.NativeRecoveryRequested);
        }

        private LeaderSighting ObserveLeader(EntityManager em, out VehicleObservation leader)
        {
            leader = default;
            if (!OnRoad(em, passedLeader)) return LeaderSighting.Gone;
            return CorridorTraffic.TryObserve(em, corridor, passedLeader, out leader) ? LeaderSighting.Observed : LeaderSighting.Unknown;
        }

        // True while any body of the vehicle is on, or changing onto, a lane of this road.
        private bool OnRoad(EntityManager em, Entity vehicle)
        {
            if (!PassingCorridor.Live(em, vehicle) || em.HasComponent<ParkedCar>(vehicle) || !em.HasComponent<CarCurrentLane>(vehicle))
                return false;
            var current = em.GetComponentData<CarCurrentLane>(vehicle);
            if (OnCorridor(current.m_Lane) || OnCorridor(current.m_ChangeLane)) return true;
            if (!em.HasBuffer<LayoutElement>(vehicle)) return false;
            foreach (var part in em.GetBuffer<LayoutElement>(vehicle, true))
            {
                if (part.m_Vehicle == vehicle || !em.HasComponent<CarTrailerLane>(part.m_Vehicle)) continue;
                var trailer = em.GetComponentData<CarTrailerLane>(part.m_Vehicle);
                if (OnCorridor(trailer.m_Lane) || OnCorridor(trailer.m_NextLane)) return true;
            }
            return false;
        }

        private bool OnCorridor(Entity lane)
            => lane != Entity.Null && corridor != null && (FindLane(corridor, lane, false) >= 0 || FindLane(corridor, lane, true) >= 0);

        // Time to clear the leader and merge, against the nearest oncoming arrival.
        private static bool PassFits(PassingCorridor road, CorridorTraffic traffic, Entity vehicle, in VehicleObservation self,
            bool hasLeader, in VehicleObservation leader, float cruise, float acceleration, float marginSeconds)
        {
            float speed = math.max(0, self.StationSpeedMetresPerSecond);
            float top = math.max(speed, cruise);
            float clear = hasLeader ? leader.FrontMetres + PassingGate.ReturnClearanceMetres - self.RearMetres : 0;
            float leaderSpeed = hasLeader ? math.max(0, leader.MaximumStationProgressMetresPerSecond) : 0;
            float finish = PassCommitment.FinishSeconds(clear, speed, top, acceleration, leaderSpeed);
            float conflict = float.PositiveInfinity;
            foreach (var other in traffic.Observations)
            {
                if (other.Root == vehicle || !other.Oncoming || !other.OccupiesOpposing || other.FrontMetres <= self.RearMetres) continue;
                float closing = top + road.OpposingClosingSpeed(math.max(0, -other.MinimumSpeedMetresPerSecond));
                conflict = math.min(conflict, PassCommitment.ConflictSeconds(other.RearMetres - self.FrontMetres, closing));
            }
            return PassCommitment.Fits(finish, conflict, marginSeconds);
        }

        // Every car between this one and the leader, the leader included.
        private static bool AnyTurnsAcross(EntityManager em, PassingPoolSystem pools, PassingPool pool,
            in VehicleObservation self, float horizonMetres)
        {
            foreach (var ahead in pool.Members.Values)
                if (ahead.Root != self.Root && ahead.FrontMetres > self.FrontMetres && ahead.RearMetres <= pool.LeaderFrontMetres
                    && !ahead.Oncoming && !ahead.OccupiesOpposing && !pools.TryTraversal(ahead.Root, out _)
                    && TurnsAcross(em, ahead.Root, pool.Corridor, horizonMetres)) return true;
            return false;
        }

        // Reads the vehicle's own native route: the first lane that leaves this
        // road shows which way it turns, as a driver reads a turn signal.
        private static bool TurnsAcross(EntityManager em, Entity vehicle, PassingCorridor road, float horizonMetres)
        {
            if (road == null || !em.HasComponent<CarCurrentLane>(vehicle) || !em.HasBuffer<CarNavigationLane>(vehicle)) return false;
            Entity last = em.GetComponentData<CarCurrentLane>(vehicle).m_Lane;
            if (FindLane(road, last, false) < 0) return false;
            var nav = em.GetBuffer<CarNavigationLane>(vehicle, true);
            for (int i = 0; i < nav.Length; i++)
            {
                Entity lane = nav[i].m_Lane;
                if (lane == last) continue;
                if (FindLane(road, lane, false) >= 0) { last = lane; continue; }
                return ExitCrosses(em, road, last, lane, nav[i].m_CurvePosition.y, horizonMetres);
            }
            if (!em.HasComponent<PathOwner>(vehicle) || !em.HasBuffer<PathElement>(vehicle)) return false;
            var path = em.GetBuffer<PathElement>(vehicle, true);
            int start = math.max(0, em.GetComponentData<PathOwner>(vehicle).m_ElementIndex);
            for (int i = start; i < path.Length && i < start + 32; i++)
            {
                Entity lane = path[i].m_Target;
                if (lane == last) continue;
                if (FindLane(road, lane, false) >= 0) { last = lane; continue; }
                return ExitCrosses(em, road, last, lane, path[i].m_TargetDelta.y, horizonMetres);
            }
            return false;
        }

        private static bool ExitCrosses(EntityManager em, PassingCorridor road, Entity from, Entity exit, float exitEnd, float horizonMetres)
        {
            int index = FindLane(road, from, false);
            if (index < 0 || !em.HasComponent<Curve>(exit) || !math.isfinite(exitEnd)) return false;
            var piece = road.Lanes[index];
            var curve = em.GetComponentData<Curve>(exit).m_Bezier;
            if (!road.TryStation(curve.a, out float station) || station > horizonMetres) return false;
            MathUtils.Distance(piece.ForwardCurve, curve.a, out float home);
            MathUtils.Distance(piece.OpposingCurve, curve.a, out float across);
            float3 homePoint = MathUtils.Position(piece.ForwardCurve, home);
            float3 side = MathUtils.Position(piece.OpposingCurve, across) - homePoint;
            side.y = 0;
            float width = math.lengthsq(side);
            if (!(width > .25f)) return false;
            float3 end = MathUtils.Position(curve, math.saturate(exitEnd)) - homePoint;
            end.y = 0;
            return PassCommitment.CrossesOpposing(math.dot(end, side) / width);
        }

        private void Abort(TraversalFailureReason reason) { FailureReason = reason; State = TraversalState.Aborted; }
        private void RequestRecovery(EntityManager em, TraversalFailureReason reason)
        {
            Abort(reason);
            var owner = em.GetComponentData<PathOwner>(Vehicle);
            owner.m_State |= PathFlags.Obsolete;
            em.SetComponentData(Vehicle, owner);
        }
        internal void Observe(EntityManager em)
        {
            if (!LeaseHeld) return;
            if (!PassingCorridor.Live(em, Vehicle))
            { Abort(TraversalFailureReason.VehicleRemoved); TryRelease(em); return; }
            if (!Ready(em, Vehicle))
            {
                // Still alive but no longer a natively driven car (parked or
                // despawning): its navigation is no longer ours to protect.
                Abort(TraversalFailureReason.NativeStateUnavailable);
                LeaseHeld = false;
                return;
            }
            if (!PartsUnchanged(em))
            {
                // A trailer was attached or swapped: the package this lease
                // measured no longer exists. Hand the car back to native driving.
                Detach(em);
                Abort(TraversalFailureReason.NativeStateUnavailable);
                LeaseHeld = false;
                return;
            }
            if (!RoutePreserved(em) && !returnIssued && State != TraversalState.Aborted) Abort(TraversalFailureReason.RouteChanged);
            var current = em.GetComponentData<CarCurrentLane>(Vehicle);
            int opposing = corridor == null ? -1 : FindLane(corridor, current.m_Lane, true);
            if (opposing >= 0 && current.m_CurvePosition.x > current.m_CurvePosition.z
                && Aligned(em.GetComponentData<VehicleTransform>(Vehicle), corridor.Lanes[opposing].OpposingCurve, true))
            {
                enteredOpposing = true;
                if (State == TraversalState.Committed) State = TraversalState.Passing;
            }
            if (Clear(em))
            {
                if (!enteredOpposing && State != TraversalState.Aborted) Abort(TraversalFailureReason.DetourConsumedBeforeEntry);
                TryRelease(em);
            }
        }
        internal bool TryRelease(EntityManager em)
        {
            if (!LeaseHeld) return true;
            if (!Clear(em)) return false;
            LeaseHeld = false;
            if (FailureReason != TraversalFailureReason.None) State = TraversalState.Aborted;
            if (State == TraversalState.Aborted) return true;
            if (!enteredOpposing || !CorridorTraffic.TryObserve(em, corridor, Vehicle, out var self))
                Abort(TraversalFailureReason.NativeStateUnavailable);
            else
            {
                var seen = ObserveLeader(em, out var leader);
                if (seen == LeaderSighting.Unknown
                    || seen == LeaderSighting.Observed && !ReturnClearance.HasClearedLeader(self.RearMetres, leader.FrontMetres))
                    Abort(TraversalFailureReason.ReturnedBeforeClearingLeader);
                else State = TraversalState.Completed;
            }
            return true;
        }
        private bool Clear(EntityManager em)
        {
            if (!PartsUnchanged(em)) return false;
            foreach (Entity part in parts)
            {
                if (!PassingCorridor.Live(em, part)) continue;
                if (!em.HasComponent<VehicleTransform>(part)) return false;
                Entity lane;
                if (part == Vehicle)
                {
                    if (!Ready(em, part)) return false;
                    var current = em.GetComponentData<CarCurrentLane>(part);
                    if (current.m_ChangeLane != Entity.Null || current.m_ChangeProgress != 0
                        || current.m_CurvePosition.x >= current.m_CurvePosition.z) return false;
                    lane = current.m_Lane;
                    var nav = em.GetBuffer<CarNavigationLane>(part, true);
                    for (int i = 0; i < nav.Length; i++)
                        if (!math.all(math.isfinite(nav[i].m_CurvePosition))
                            || nav[i].m_CurvePosition.x > nav[i].m_CurvePosition.y) return false;
                }
                else
                {
                    if (!em.HasComponent<CarTrailerLane>(part)) return false;
                    var trailer = em.GetComponentData<CarTrailerLane>(part);
                    if (trailer.m_NextLane != Entity.Null || trailer.m_CurvePosition.x >= trailer.m_CurvePosition.y) return false;
                    lane = trailer.m_Lane;
                }
                if (!PassingCorridor.Live(em, lane) || !em.HasComponent<Curve>(lane)
                    || !em.HasComponent<PrefabRef>(lane)
                    || !Aligned(em.GetComponentData<VehicleTransform>(part), em.GetComponentData<Curve>(lane).m_Bezier, false)) return false;
                var lanePrefab = em.GetComponentData<PrefabRef>(lane).m_Prefab;
                if (!em.Exists(lanePrefab) || !em.HasComponent<NetLaneData>(lanePrefab)
                    || !em.HasComponent<PrefabRef>(part)) return false;
                var bodyPrefab = em.GetComponentData<PrefabRef>(part).m_Prefab;
                if (!em.Exists(bodyPrefab) || !em.HasComponent<ObjectGeometryData>(bodyPrefab)) return false;
                var pose = em.GetComponentData<VehicleTransform>(part);
                var bounds = em.GetComponentData<ObjectGeometryData>(bodyPrefab).m_Bounds;
                float3 size = bounds.max - bounds.min;
                if (!math.all(math.isfinite(bounds.min)) || !math.all(math.isfinite(bounds.max))
                    || !math.all(size > .01f)) return false;
                float3 centre = pose.m_Position + math.rotate(pose.m_Rotation, (bounds.min + bounds.max) * .5f);
                var curve = em.GetComponentData<Curve>(lane).m_Bezier;
                MathUtils.Distance(curve, centre, out float parameter);
                float3 tangent = MathUtils.Tangent(curve, parameter);
                float3 lateral = math.normalizesafe(new float3(-tangent.z, 0, tangent.x));
                if (math.lengthsq(lateral) < .99f) return false;
                float offset = math.dot(centre - MathUtils.Position(curve, parameter), lateral);
                float halfBodyWidth = NativeVehicle.HalfExtent(pose.m_Rotation, lateral, size);
                // Release proves each part is inside its current native lane;
                // the historical passing corridor need not contain its new route.
                if (!ReturnClearance.IsWithinLane(offset, halfBodyWidth,
                    em.GetComponentData<NetLaneData>(lanePrefab).m_Width * .5f)) return false;
            }
            return true;
        }
        private bool RoutePreserved(EntityManager em)
        {
            if (em.GetComponentData<Target>(Vehicle).m_Target != destination) return false;
            var owner = em.GetComponentData<PathOwner>(Vehicle);
            if ((owner.m_State & ~PathFlags.Debug) != 0) return false;
            if (returnIssued) return true;
            if (homeCount > 0 && owner.m_ElementIndex != originalPathIndex) return false;
            var path = em.GetBuffer<PathElement>(Vehicle, true);
            if (!hasPathCursor) return owner.m_ElementIndex >= path.Length;
            if (owner.m_ElementIndex < 0 || owner.m_ElementIndex >= path.Length) return false;
            var live = path[owner.m_ElementIndex];
            return live.m_Target == pathCursor.m_Target && live.m_TargetDelta.Equals(pathCursor.m_TargetDelta)
                && live.m_Flags == pathCursor.m_Flags;
        }
        private bool NavigationOwned(DynamicBuffer<CarNavigationLane> nav)
        {
            if (nav.Length == 0 || nav.Length > owned.Length) return false;
            int offset = owned.Length - nav.Length;
            for (int i = 0; i < nav.Length; i++)
                if (nav[i].m_Lane != owned[offset + i].m_Lane
                    || !nav[i].m_CurvePosition.Equals(owned[offset + i].m_CurvePosition)) return false;
            return true;
        }

        internal bool TryGetRetentionStart(EntityManager em, out float station)
        {
            station = float.PositiveInfinity;
            if (!LeaseHeld) return true;
            if (!PartsUnchanged(em)) return false;
            foreach (var part in parts)
            {
                if (!PassingCorridor.Live(em, part)) continue;
                Entity lane, next;
                if (part == Vehicle)
                {
                    if (!em.HasComponent<CarCurrentLane>(part)) return false;
                    var current = em.GetComponentData<CarCurrentLane>(part); lane = current.m_Lane; next = current.m_ChangeLane;
                    if (em.HasBuffer<CarNavigationLane>(part))
                        foreach (var entry in em.GetBuffer<CarNavigationLane>(part, true))
                            if (FindLane(corridor, entry.m_Lane, true) >= 0 && !RetainLane(entry.m_Lane, ref station)) return false;
                }
                else
                {
                    if (!em.HasComponent<CarTrailerLane>(part)) return false;
                    var trailer = em.GetComponentData<CarTrailerLane>(part); lane = trailer.m_Lane; next = trailer.m_NextLane;
                }
                if (!RetainLane(lane, ref station) || next != Entity.Null && !RetainLane(next, ref station)) return false;
            }
            return math.isfinite(station);
        }
        private bool RetainLane(Entity lane, ref float station)
        {
            int index = FindLane(corridor, lane, false);
            if (index < 0) index = FindLane(corridor, lane, true);
            if (index < 0) return false;
            station = math.min(station, corridor.Lanes[index].StartMetres); return true;
        }
        internal bool TryBindRoad(EntityManager em, PassingCorridor shared)
        {
            if (shared == corridor) return true;
            if (!LeaseHeld || shared == null || !TryGetRetentionStart(em, out float retained)) return false;
            bool anchored = false;
            float offset = 0;
            foreach (var saved in corridor.Lanes)
            {
                if (saved.StartMetres + saved.LengthMetres <= retained || saved.StartMetres >= RequiredRoadEndMetres) continue;
                int index = FindLane(shared, saved.ForwardLane, false);
                if (index < 0) return false;
                var next = shared.Lanes[index];
                if (saved.OpposingLane != next.OpposingLane || saved.Owner != next.Owner
                    || !saved.ForwardCurve.Equals(next.ForwardCurve) || !saved.OpposingCurve.Equals(next.OpposingCurve)
                    || !saved.ForwardNodes.Equals(next.ForwardNodes) || !saved.OpposingNodes.Equals(next.OpposingNodes)
                    || saved.ForwardStart != next.ForwardStart || saved.ForwardEnd != next.ForwardEnd) return false;
                float delta = next.StartMetres - saved.StartMetres;
                if (anchored && math.abs(delta - offset) > .01f) return false;
                offset = delta; anchored = true;
            }
            if (!anchored) return false;
            BufferedEndMetres += offset;
            opposingLimit += offset;
            homeEnd += offset;
            corridor = shared;
            return true;
        }
        private static float PassingSpeed(EntityManager em, Entity vehicle, PassingCorridor corridor,
            float rearMetres, float routeEndMetres, in Game.Prefabs.CarData car)
        {
            float speedFactor = VehicleUtils.GetSpeedLimitFactor(em.GetComponentData<Car>(vehicle));
            float bound = car.m_MaxSpeed;
            foreach (var piece in corridor.Lanes)
            {
                if (piece.StartMetres + piece.LengthMetres <= rearMetres) continue;
                if (piece.StartMetres >= routeEndMetres) break;
                // The same ceiling PassingSpeedSystem applies after native navigation.
                var lane = piece.OpposingRules;
                lane.m_SpeedLimit *= speedFactor;
                float drive = NativeVehicle.LaneDriveSpeed(em, car, lane, piece.OpposingLane);
                lane.m_SpeedLimit *= OvertakeSpeed.LimitFactor;
                float cap = OvertakeSpeed.Ceiling(drive, NativeVehicle.LaneDriveSpeed(em, car, lane, piece.OpposingLane),
                    car.m_Braking, NativeTiming.StepSeconds);
                if (!math.isfinite(cap) || cap <= 0)
                    return 0;
                bound = math.min(bound, cap);
            }
            return bound;
        }
        private static bool TryEstimateMotion(EntityManager em, PassingPoolSystem pools, PassingPool pool,
            in VehicleObservation self, float cap, float progressScale, float enter, in CarData car,
            float latestBoundary, float available, float opposingSpeed, float speedUpperBound,
            out PassingMotion best, out float required)
        {
            best = default;
            required = float.PositiveInfinity;
            float start = math.max(0, self.MinimumStationProgressMetresPerSecond);
            float leaderSpeed = pool.LeaderSpeedMetresPerSecond;
            float span = math.max(0, pool.LeaderFrontMetres - self.RearMetres);
            Entity vehicle = self.Root;
            if (!em.HasComponent<Moving>(vehicle)
                || !em.HasComponent<VehicleTransform>(vehicle)) return false;
            float transitionSpeed = math.max(0, math.dot(em.GetComponentData<Moving>(vehicle).m_Velocity,
                math.forward(em.GetComponentData<VehicleTransform>(vehicle).m_Rotation)));
            if (!math.isfinite(transitionSpeed)) return false;
            var accelerationLimits = car;
            // Committed predecessors already own the same passing path, even
            // before they physically leave home. No entrant may forecast through them.
            foreach (var ahead in pool.Members.Values)
            {
                if (ahead.Root == vehicle || ahead.FrontMetres <= self.FrontMetres
                    || !pools.TryTraversal(ahead.Root, out var traversal)
                    || !traversal.LeaseHeld
                    || (!ahead.OccupiesOpposing && traversal.State != TraversalState.Committed)) continue;
                if (traversal.HoldingReturn || traversal.FailureReason != TraversalFailureReason.None) return false;
                if (!NativeVehicle.TryCarLimits(em, ahead.Root, out var aheadCar)) return false;
                accelerationLimits.m_Acceleration = math.min(accelerationLimits.m_Acceleration, aheadCar.m_Acceleration);
                accelerationLimits.m_MaxSpeed = math.min(accelerationLimits.m_MaxSpeed, aheadCar.m_MaxSpeed);
                cap = math.min(cap, traversal.MaximumPassingSpeedMetresPerSecond);
                start = math.min(start, math.max(0, ahead.MinimumStationProgressMetresPerSecond));
            }
            // Try the common midpoint first. Other cruise profiles are scalar
            // work and share the same one-time native predecessor reduction.
            bool bestFitsHome = false;
            for (int i = 0; i < 8; i++)
            {
                int sample = (i + 3) % 8 + 1;
                float cruise = leaderSpeed + (cap * progressScale - leaderSpeed) * sample / 8;
                float floor = NativeVehicle.StationAccelerationFloor(accelerationLimits, cruise, progressScale);
                if (!PassingMotion.TryEstimate(start, leaderSpeed, cruise, floor, span, out var motion, transitionSpeed)
                    || !motion.TryWithApproachDistance(enter - self.FrontMetres, leaderSpeed, span, out motion)) continue;
                float distance = motion.RequiredObservationMetres(leaderSpeed, opposingSpeed, self.LengthMetres, speedUpperBound);
                if (!math.isfinite(distance)) continue;
                bool fitsHome = math.max(enter, self.FrontMetres + motion.DistanceAt(motion.ClearSeconds)) < latestBoundary;
                if (fitsHome && distance <= available)
                {
                    best = motion;
                    required = distance;
                    return true;
                }
                if ((fitsHome && !bestFitsHome) || (fitsHome == bestFitsHome && distance < required))
                {
                    best = motion;
                    required = distance;
                    bestFitsHome = fitsHome;
                }
            }
            return best.CompletionSeconds > 0;
        }
        private static float Lookahead(CarData car, float speed)
            => math.max(400, 2 * VehicleUtils.GetBrakingDistance(car, speed, NativeTiming.StepSeconds) + speed * 2);
        private static bool AppendOpposingIntervals(PassingCorridor road, float start, float end,
            List<CarNavigationLane> result, float maximumInterval)
        {
            if (end <= start || maximumInterval <= .0001f) return false;
            float covered = 0;
            foreach (var piece in road.Lanes)
            {
                float from = math.max(start, piece.StartMetres), to = math.min(end, piece.StartMetres + piece.LengthMetres);
                if (to <= from) continue;
                if (!piece.AllowsPassing) return false;
                while (to - from > .0001f)
                {
                    if (result.Count >= NativeVehicle.MaximumNavigationEntries) return false;
                    float next = math.min(to, from + maximumInterval);
                    if (next <= from) return false;
                    result.Add(new CarNavigationLane { m_Lane = piece.OpposingLane,
                        m_CurvePosition = new float2(piece.OpposingParameterAt(from), piece.OpposingParameterAt(next)),
                        m_Flags = LaneFlags.FixedLane });
                    covered += next - from; from = next;
                }
            }
            return math.abs(covered - (end - start)) < .01f;
        }
        private static Entity[] ReadParts(EntityManager em, Entity vehicle)
        {
            var parts = new List<Entity> { vehicle };
            if (em.HasBuffer<LayoutElement>(vehicle))
                foreach (var part in em.GetBuffer<LayoutElement>(vehicle, true))
                {
                    if (parts.Count >= NativeVehicle.MaximumParts || NativeVehicle.Root(em, part.m_Vehicle) != vehicle) return null;
                    if (!parts.Contains(part.m_Vehicle)) parts.Add(part.m_Vehicle);
                }
            return parts.ToArray();
        }
        private bool PartsUnchanged(EntityManager em)
        {
            if (!em.Exists(Vehicle) || !em.HasBuffer<LayoutElement>(Vehicle)) return true;
            var layout = em.GetBuffer<LayoutElement>(Vehicle, true);
            if (layout.Length > NativeVehicle.MaximumParts) return false;
            for (int i = 0; i < layout.Length; i++)
                if (Array.IndexOf(parts, layout[i].m_Vehicle) < 0) return false;
            return true;
        }
        private static int FindLane(PassingCorridor road, Entity lane, bool opposing)
        {
            for (int i = 0; i < road.Lanes.Count; i++)
                if ((opposing ? road.Lanes[i].OpposingLane : road.Lanes[i].ForwardLane) == lane) return i;
            return -1;
        }
        private static bool Aligned(VehicleTransform transform, Bezier4x3 curve, bool reverse)
        {
            if (!math.all(math.isfinite(transform.m_Position)) || !math.all(math.isfinite(transform.m_Rotation.value))) return false;
            float distance = MathUtils.Distance(curve, transform.m_Position, out float t);
            float3 direction = math.normalizesafe(MathUtils.Tangent(curve, t)) * (reverse ? -1 : 1);
            return math.isfinite(distance) && distance < .9f && math.dot(math.forward(transform.m_Rotation), direction) > .9f;
        }
        private static bool PlainLane(LaneFlags flags)
            => (flags & ~(LaneFlags.FixedLane | LaneFlags.Reserved | LaneFlags.UpdateOptimalLane
                | LaneFlags.Checked | LaneFlags.Validated | LaneFlags.HighBeams | LaneFlags.FixedStart)) == 0;
        private static bool Ready(EntityManager em, Entity vehicle)
            => PassingCorridor.Live(em, vehicle) && em.HasComponent<Car>(vehicle) && em.HasComponent<CarCurrentLane>(vehicle)
                && em.HasComponent<CarNavigation>(vehicle) && em.HasComponent<VehicleTransform>(vehicle)
                && em.HasComponent<PathOwner>(vehicle) && em.HasComponent<Target>(vehicle)
                && em.HasBuffer<PathElement>(vehicle) && em.HasBuffer<CarNavigationLane>(vehicle);
        private static CarNavigationLane[] Copy(DynamicBuffer<CarNavigationLane> nav)
        {
            var entries = new CarNavigationLane[nav.Length];
            for (int i = 0; i < entries.Length; i++) entries[i] = nav[i];
            return entries;
        }
        private static void Replace(DynamicBuffer<CarNavigationLane> nav, CarNavigationLane[] entries)
        { nav.Clear(); for (int i = 0; i < entries.Length; i++) nav.Add(entries[i]); }
    }
}
