using System.Collections.Generic;
using Unity.Entities;
namespace Overtake
{
    internal sealed class PassingPool
    {
        internal readonly Entity Leader;
        internal readonly List<Entity> Followers = new List<Entity>();
        internal readonly List<NativePassingTraversal> Traversals = new List<NativePassingTraversal>();
        internal readonly CorridorTraffic Traffic = new CorridorTraffic();
        internal readonly Dictionary<Entity, VehicleObservation> Members = new Dictionary<Entity, VehicleObservation>();
        internal readonly Dictionary<Entity, PassingEntryResult> Refusals = new Dictionary<Entity, PassingEntryResult>();
        internal PassingCorridor Corridor;
        internal GateDecision Gate = PassingGate.Closed;
        internal Entity ThreatVehicle;
        internal float ThreatDistanceMetres = float.PositiveInfinity, ThreatSpeedMetresPerSecond;
        internal int CompletedPasses;
        internal float LeaderFrontMetres, LeaderSpeedMetresPerSecond;
        internal float RequestedLookaheadMetres = 400;
        internal uint ObservationFrame = uint.MaxValue;
        // One of sixteen simulation sub-frames; spreads pool evaluation evenly.
        internal int Slot;
        internal Entity LastBuildLane;
        internal bool Dissolving;
        internal PassingPool(Entity leader)
        {
            Leader = leader;
        }
    }
}
