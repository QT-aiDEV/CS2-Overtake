using System.Globalization;
using Colossal.UI.Binding;
using Game.UI;
using Game.UI.InGame;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace Overtake
{
    public partial class VehicleControlsUISystem : UISystemBase
    {
        private const string Domain = "Overtake";
        private PassingPoolSystem pools;
        private SelectedInfoUISystem selection;
        private ValueBinding<bool> roadVehicle, passingEnabled, debugOverlayEnabled;
        private ValueBinding<string> selectedName;
        private ValueBinding<PanelState> panelState;
        private Entity identityEntity = Entity.Null;
        private string identity = "";

        protected override void OnCreate()
        {
            base.OnCreate();
            pools = World.GetOrCreateSystemManaged<PassingPoolSystem>();
            selection = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            AddBinding(roadVehicle = new ValueBinding<bool>(Domain, "roadVehicle", false));
            AddBinding(passingEnabled = new ValueBinding<bool>(Domain, "passingEnabled", false));
            AddBinding(debugOverlayEnabled = new ValueBinding<bool>(Domain, "debugOverlayEnabled", false));
            AddBinding(selectedName = new ValueBinding<string>(Domain, "selectedName", ""));
            AddBinding(panelState = new ValueBinding<PanelState>(Domain, "panelState", PanelState.Empty, new PanelStateWriter()));
            AddBinding(new TriggerBinding(Domain, "togglePassing", () => pools.SetPassingEnabled(!pools.EnabledPassing)));
            AddBinding(new TriggerBinding<bool>(Domain, "setPassingEnabled", pools.SetPassingEnabled));
            AddBinding(new TriggerBinding(Domain, "toggleDebugOverlay", () => pools.DebugOverlayEnabled = !pools.DebugOverlayEnabled));
        }

        protected override void OnUpdate()
        {
            var entity = selection.selectedEntity;
            bool vehicle = entity != Entity.Null && EntityManager.Exists(entity)
                && EntityManager.HasComponent<Car>(entity) && EntityManager.HasComponent<CarCurrentLane>(entity);
            roadVehicle.Update(vehicle);
            passingEnabled.Update(pools.EnabledPassing);
            debugOverlayEnabled.Update(pools.DebugOverlayEnabled);
            // Identity resets the native tab selection and is never a displayed vehicle name.
            if (entity != identityEntity)
            {
                identityEntity = entity;
                identity = entity == Entity.Null ? "" : entity.Index + ":" + entity.Version;
            }
            selectedName.Update(vehicle ? identity : "");
            var state = PanelState.Empty;
            state.ModCost = pools.Metrics.AverageMilliseconds.ToString("0.00", CultureInfo.InvariantCulture) + " ms/tick";
            if (!vehicle) { panelState.Update(state); return; }
            state.CurrentSpeed = CurrentSpeed(entity);
            bool leased = pools.TryTraversal(entity, out var lease);
            if (pools.TryGet(entity, out var pool)
                || pools.Membership.TryOwner(entity, out var owner) && pools.TryGet(owner, out pool))
            {
                string role = pool.Leader == entity ? "Pool leader"
                    : !leased ? "Queued follower"
                    : lease.State == TraversalState.Committed ? "Pass committed"
                    : lease.Yielding ? "Dropping back behind the leader"
                    : lease.State == TraversalState.Aborted ? "Awaiting clearance" : lease.State.ToString();
                state.IsLeader = pool.Leader == entity;
                state.Role = state.IsLeader ? "Slow leader" : role;
                state.Completed = pool.CompletedPasses;
                state.Gate = pool.Gate.State.ToString();
                state.Reason = DescribeReason(pool.Gate.Reason);
                if (!state.IsLeader && pool.Refusals.TryGetValue(entity, out var refusal))
                    state.EntryReason = DescribeEntry(refusal);
                state.LeaderSpeed = Speed(pool.LeaderSpeedMetresPerSecond);
                state.Queued = pool.Followers.Count;
                foreach (var traversal in pool.Traversals)
                {
                    if (!traversal.LeaseHeld) continue;
                    if (traversal.State == TraversalState.Committed || traversal.State == TraversalState.Passing) state.Active++;
                    else if (traversal.State == TraversalState.Returning) state.Returning++;
                    if (traversal.State != TraversalState.Passing || traversal.ReturnBlock == NativeReturnBlock.None) continue;
                    state.WaitingReturn++;
                    if (state.ReturnReason.Length == 0) state.ReturnReason = DescribeReturnBlock(traversal.ReturnBlock);
                }
                state.PoolStatus = state.Active > 0 ? "Passing wave underway"
                    : state.Returning > 0 ? "Vehicles returning"
                    : pool.Gate.State == GateState.Green ? "Window open; entry checks pending" : "Waiting for a window";
                state.Corridor = pool.Corridor == null ? "Not verified"
                    : Distance(math.max(0, pool.Corridor.PassingEndFrom(pool.LeaderFrontMetres) - pool.LeaderFrontMetres));
                state.YellowBoundary = Distance(pool.Gate.YellowDistanceMetres);
                state.RedBoundary = Distance(pool.Gate.RedDistanceMetres);
                state.AvailableDistance = Distance(pool.Gate.AvailableDistanceMetres);
                state.Coverage = pool.Gate.Reason == GateReason.RestrictedRoad ? "Road closed to passing"
                    : pool.Gate.State == GateState.Purple && pool.Corridor != null ? DescribeRoadEnd(pool.Corridor.Termination)
                    : pool.Traffic.Complete ? "Verified" : "Incomplete; no new passes";
                state.Oncoming = pool.ThreatVehicle != Entity.Null
                    ? Distance(pool.ThreatDistanceMetres) + " away at " + Speed(pool.ThreatSpeedMetresPerSecond)
                    : pool.Gate.Reason == GateReason.RestrictedRoad ? "Not evaluated while road is closed"
                    : pool.Traffic.Complete ? "None observed" : "Not fully observed";
            }
            else if (leased)
            {
                state.Role = lease.State == TraversalState.Returning ? "Returning" : "Recovery in progress";
                state.PoolStatus = "Passing queue released";
                state.Reason = "Returning to normal traffic";
                state.ReturnReason = DescribeReturnBlock(lease.ReturnBlock);
            }
            panelState.Update(state);
        }

        private static string Distance(float value) => math.isfinite(value)
            ? value.ToString("0", CultureInfo.InvariantCulture) + " m" : "Unavailable";
        // The game's display scale is applied only at the presentation boundary.
        private static string Speed(float value) => math.isfinite(value)
            ? (math.abs(value) * 1.8f).ToString("0", CultureInfo.InvariantCulture) + " km/h" : "Unavailable";
        private string CurrentSpeed(Entity entity)
        {
            if (!EntityManager.HasComponent<Game.Objects.Moving>(entity)) return "Unavailable";
            var velocity = EntityManager.GetComponentData<Game.Objects.Moving>(entity).m_Velocity;
            return math.all(math.isfinite(velocity)) ? Speed(math.length(velocity)) : "Unavailable";
        }
        private static string DescribeReason(GateReason reason)
        {
            switch (reason)
            {
                case GateReason.None: return "Road and traffic window clear";
                case GateReason.IncompleteTraffic: return "Traffic ahead is not fully observed";
                case GateReason.IncompleteCoverage: return "Verifying road ahead";
                case GateReason.RoadEnd: return "Verified road ahead is ending";
                case GateReason.Oncoming: return "Oncoming traffic is closing the window";
                case GateReason.RestrictedRoad: return "Passing is restricted on this road";
                default: return "Passing unavailable";
            }
        }
        private static string DescribeRoadEnd(CorridorTermination end)
        {
            switch (end)
            {
                case CorridorTermination.Junction: case CorridorTermination.AmbiguousFork: return "Junction ahead";
                case CorridorTermination.RestrictedLane: return "No-passing zone ahead";
                case CorridorTermination.DividedCarriageway: return "Divided road ahead";
                case CorridorTermination.LaneCountChanged: return "Lane count changes ahead";
                case CorridorTermination.LaneEnded: return "Road ends ahead";
                default: return "Unsupported road ahead";
            }
        }
        private static string DescribeReturnBlock(NativeReturnBlock reason)
        {
            switch (reason)
            {
                case NativeReturnBlock.LeaderNotCleared: return "Still clearing the leader";
                case NativeReturnBlock.IncompleteTraffic: return "Waiting for traffic coverage";
                case NativeReturnBlock.NativeReservation: return "Waiting for the game to release a lane reservation";
                case NativeReturnBlock.ForwardGap: return "Waiting for a gap to return";
                case NativeReturnBlock.Ownership: return "Waiting for a landing on the original route";
                case NativeReturnBlock.EnteringOpposing: return "Still moving into the opposing lane";
                default: return "No return wait recorded";
            }
        }
        private static string DescribeEntry(PassingEntryResult result)
        {
            switch (result)
            {
                case PassingEntryResult.CannotGain: return "Vehicle cannot clear the leader at its available speed";
                case PassingEntryResult.InsufficientClearance: return "This vehicle needs more verified passing distance";
                case PassingEntryResult.EntryOccupied: return "Opposing traffic blocks this entry";
                case PassingEntryResult.DepartureBlocked: return "Waiting for room to leave the home lane";
                case PassingEntryResult.NoRouteHome: return "This route has no verified return before it diverges";
                case PassingEntryResult.Unavailable: return "Native route or vehicle prerequisites unavailable";
                case PassingEntryResult.WindowClosed: return "Shared window closed";
                case PassingEntryResult.LeaderTurning: return "A car ahead is turning across the oncoming lane";
                case PassingEntryResult.Committed: return "Pass committed";
                default: return "";
            }
        }

        // A presentation snapshot of one canonical pool, never a second traffic model.
        private struct PanelState
        {
            internal string Gate, Role, CurrentSpeed, LeaderSpeed, Reason, EntryReason, ReturnReason, PoolStatus, ModCost;
            internal string Corridor, YellowBoundary, RedBoundary, AvailableDistance, Coverage, Oncoming;
            internal int Queued, Active, Returning, WaitingReturn, Completed;
            internal bool IsLeader;
            internal static PanelState Empty => new PanelState {
                Gate = "None", Role = "No passing pool", CurrentSpeed = "Unavailable", LeaderSpeed = "",
                Reason = "", EntryReason = "", ReturnReason = "", Corridor = "", YellowBoundary = "", RedBoundary = "",
                AvailableDistance = "", Coverage = "", Oncoming = "", PoolStatus = "No passing queue", ModCost = ""
            };
        }
        private sealed class PanelStateWriter : IWriter<PanelState>
        {
            public void Write(IJsonWriter writer, PanelState value)
            {
                writer.TypeBegin("Overtake.PanelState");
                writer.PropertyName("gate"); writer.Write(value.Gate);
                writer.PropertyName("role"); writer.Write(value.Role);
                writer.PropertyName("currentSpeed"); writer.Write(value.CurrentSpeed);
                writer.PropertyName("leaderSpeed"); writer.Write(value.LeaderSpeed);
                writer.PropertyName("reason"); writer.Write(value.Reason);
                writer.PropertyName("entryReason"); writer.Write(value.EntryReason);
                writer.PropertyName("returnReason"); writer.Write(value.ReturnReason);
                writer.PropertyName("corridor"); writer.Write(value.Corridor);
                writer.PropertyName("yellowBoundary"); writer.Write(value.YellowBoundary);
                writer.PropertyName("redBoundary"); writer.Write(value.RedBoundary);
                writer.PropertyName("availableDistance"); writer.Write(value.AvailableDistance);
                writer.PropertyName("coverage"); writer.Write(value.Coverage);
                writer.PropertyName("oncoming"); writer.Write(value.Oncoming);
                writer.PropertyName("queued"); writer.Write(value.Queued);
                writer.PropertyName("active"); writer.Write(value.Active);
                writer.PropertyName("returning"); writer.Write(value.Returning);
                writer.PropertyName("waitingReturn"); writer.Write(value.WaitingReturn);
                writer.PropertyName("isLeader"); writer.Write(value.IsLeader);
                writer.PropertyName("completed"); writer.Write(value.Completed);
                writer.PropertyName("poolStatus"); writer.Write(value.PoolStatus);
                writer.PropertyName("modCost"); writer.Write(value.ModCost);
                writer.TypeEnd();
            }
        }
    }
}
