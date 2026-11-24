using System.Diagnostics;
using Game;
using Game.Simulation;
using Unity.Entities;

namespace Overtake
{
    // Turns the worker-thread scan into pools. Only scan candidates reach the
    // exact native queue resolution, which still re-validates live road support.
    public partial class SlowLeaderDiscoverySystem : GameSystemBase
    {
        private PassingPoolSystem pools;
        private DiscoveryScanSystem scan;
        private uint lastFrame = uint.MaxValue;

        protected override void OnCreate()
        {
            base.OnCreate();
            pools = World.GetOrCreateSystemManaged<PassingPoolSystem>();
            scan = World.GetOrCreateSystemManaged<DiscoveryScanSystem>();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            lastFrame = uint.MaxValue;
            base.OnGamePreload(purpose, mode);
        }

        protected override void OnUpdate()
        {
            uint frame = World.GetOrCreateSystemManaged<SimulationSystem>().frameIndex;
            if (frame == lastFrame) return;
            lastFrame = frame;
            pools.Roads.BeginFrame(frame);
            if (!scan.TryTake(pools, frame, out var candidates)) return;
            if (!pools.EnabledPassing)
            {
                candidates.Clear();
                return;
            }
            pools.Queue.BeginFrame(frame);
            long started = Stopwatch.GetTimestamp();
            while (candidates.TryDequeue(out var follower))
            {
                if (pools.TryTraversal(follower, out _) || pools.Membership.TryOwner(follower, out _)) continue;
                Entity leader = pools.Queue.Resolve(EntityManager, pools, follower);
                if (leader == Entity.Null || leader == follower
                    || !NativeVehicle.TryMeasure(EntityManager, leader, out _, out float speed, out _) || speed < 0
                    || !pools.TryCreate(leader, out var pool)) continue;
                foreach (var member in pools.Queue.Chain)
                    if (member != leader && !pools.TryTraversal(member, out _)
                        && pools.Membership.TryClaim(leader, member) && !pool.Followers.Contains(member))
                        pool.Followers.Add(member);
            }
            double elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            pools.Metrics.Add(frame, elapsed);
        }
    }
}
