using Game;
using Game.Simulation;
using System.Diagnostics;

namespace Overtake
{
    // Runs at the start of a simulation tick, before discovery, and observes
    // the shard the previous tick moved. Reading positions here instead of
    // directly after native movement means the movement job has finished and
    // the main thread never waits for it.
    public partial class PassingObservationSystem : GameSystemBase
    {
        private PassingPoolSystem pools;
        private uint lastFrame = uint.MaxValue;

        protected override void OnCreate()
        {
            base.OnCreate();
            pools = World.GetOrCreateSystemManaged<PassingPoolSystem>();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            lastFrame = uint.MaxValue;
            base.OnGamePreload(purpose, mode);
        }

        protected override void OnUpdate()
        {
            uint frame = World.GetOrCreateSystemManaged<SimulationSystem>().frameIndex;
            if (frame == lastFrame || pools.HeldTraversalCount == 0) return;
            lastFrame = frame;
            long started = Stopwatch.GetTimestamp();
            int slot = (int)((frame - 1) & 15);
            for (int i = pools.Traversals.Count - 1; i >= 0; i--)
            {
                var traversal = pools.Traversals[i];
                if (!traversal.InUpdateSlot(EntityManager, slot)) continue;
                traversal.Observe(EntityManager);
                if (!traversal.LeaseHeld) pools.FinishTraversal(traversal);
            }
            pools.Metrics.Add(frame, (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
        }
    }
}
