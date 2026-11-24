using Game;
using Game.Simulation;
using System.Diagnostics;
namespace Overtake
{
    public partial class PassingWaveSystem : GameSystemBase
    {
        // Keep pending departures, opposing cars and merges in the same
        // budget until observation releases their whole vehicle package.
        internal const int MaximumOutstandingPasses = 5;
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
            if (frame == lastFrame || !pools.EnabledPassing || pools.Pools.Count == 0)
                return;
            lastFrame = frame;
            long started = Stopwatch.GetTimestamp();
            foreach (var pool in pools.Pools)
            {
                if (pool.Dissolving || pool.ObservationFrame != frame || !pool.Traffic.Complete
                    || !PassingGate.AllowsCommitment(pool.Gate.State))
                    continue;
                for (int i = 0; i < pool.Followers.Count && pool.Traversals.Count < MaximumOutstandingPasses; i++)
                {
                    var follower = pool.Followers[i];
                    if (pools.TryTraversal(follower, out _))
                        continue;
                    var entry = NativePassingTraversal.TryCommit(EntityManager, follower, pools, pool, frame, out var traversal);
                    pool.Refusals[follower] = entry;
                    // Each follower must prove its own departure and return.
                    // A local refusal can leave room for an independent splice.
                    if (entry == PassingEntryResult.CannotGain || entry == PassingEntryResult.NoRouteHome
                        || entry == PassingEntryResult.DepartureBlocked) continue;
                    // Shared traffic or unknown native state closes this wave's attempt.
                    if (entry != PassingEntryResult.Committed) break;
                    pools.RegisterTraversal(pool, traversal);
                    pool.Followers.RemoveAt(i--);
                }
            }
            double elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            pools.Metrics.Add(frame, elapsed);
        }
    }
}
