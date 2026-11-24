using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using VehicleTransform = Game.Objects.Transform;

namespace Overtake
{
    // Scans the shard native movement just stepped, on worker threads, and
    // keeps only cars that might be queued behind a slow leader: on a lane
    // known to support passing, and either natively blocked or held below
    // their own free-drive speed. SlowLeaderDiscoverySystem consumes the
    // result on the next simulation tick, after this job has long finished.
    public partial class DiscoveryScanSystem : GameSystemBase
    {
        // Lane support is a discovery hint only. Every pool decision re-checks
        // live eligibility, so a stale entry can at worst delay one discovery.
        private const uint LaneSupportLifetimeMask = 255;
        private EntityQuery vehicles;
        private PassingPoolSystem pools;
        private NativeQueue<Entity> candidates, unknownLanes;
        private NativeParallelHashMap<Entity, bool> laneSupport;
        private JobHandle pending;
        private bool scheduled;
        private uint lastFrame = uint.MaxValue;
        private ComponentLookup<Game.Net.CarLane> laneRules;
        private ComponentLookup<LaneCondition> laneConditions;
        private CarLimitsLookup limits;

        protected override void OnCreate()
        {
            base.OnCreate();
            vehicles = GetEntityQuery(ComponentType.ReadOnly<Car>(), ComponentType.ReadOnly<CarCurrentLane>(),
                ComponentType.ReadOnly<Moving>(), ComponentType.ReadOnly<Blocker>(), ComponentType.ReadOnly<VehicleTransform>(),
                ComponentType.ReadOnly<UpdateFrame>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<CarTrailer>(), ComponentType.Exclude<ParkedCar>());
            pools = World.GetOrCreateSystemManaged<PassingPoolSystem>();
            candidates = new NativeQueue<Entity>(Allocator.Persistent);
            unknownLanes = new NativeQueue<Entity>(Allocator.Persistent);
            laneSupport = new NativeParallelHashMap<Entity, bool>(4096, Allocator.Persistent);
            laneRules = GetComponentLookup<Game.Net.CarLane>(true);
            laneConditions = GetComponentLookup<LaneCondition>(true);
            limits = new CarLimitsLookup(this);
        }

        protected override void OnDestroy()
        {
            pending.Complete();
            candidates.Dispose();
            unknownLanes.Dispose();
            laneSupport.Dispose();
            base.OnDestroy();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            Reset();
            base.OnGamePreload(purpose, mode);
        }

        internal void Reset()
        {
            pending.Complete();
            scheduled = false;
            candidates.Clear();
            unknownLanes.Clear();
            laneSupport.Clear();
            lastFrame = uint.MaxValue;
        }

        protected override void OnUpdate()
        {
            uint frame = World.GetOrCreateSystemManaged<SimulationSystem>().frameIndex;
            // A result that was not consumed yet is still owned by the reader.
            if (frame == lastFrame || scheduled || !pools.EnabledPassing) return;
            lastFrame = frame;
            vehicles.ResetFilter();
            vehicles.SetSharedComponentFilter(new UpdateFrame(frame & 15));
            laneRules.Update(this);
            laneConditions.Update(this);
            limits.Update(this);
            var job = new ScanJob
            {
                Entities = GetEntityTypeHandle(),
                CurrentLanes = GetComponentTypeHandle<CarCurrentLane>(true),
                Blockers = GetComponentTypeHandle<Blocker>(true),
                Motion = GetComponentTypeHandle<Moving>(true),
                Poses = GetComponentTypeHandle<VehicleTransform>(true),
                Cars = GetComponentTypeHandle<Car>(true),
                LaneRules = laneRules,
                LaneConditions = laneConditions,
                Limits = limits,
                LaneSupport = laneSupport,
                Candidates = candidates.AsParallelWriter(),
                UnknownLanes = unknownLanes.AsParallelWriter()
            };
            pending = job.ScheduleParallel(vehicles, Dependency);
            Dependency = pending;
            scheduled = true;
        }

        // Main thread only. Completes the scan (normally already finished),
        // classifies newly seen lanes with the authoritative road rules and
        // hands over the candidates. The caller must drain the queue.
        internal bool TryTake(PassingPoolSystem owner, uint frame, out NativeQueue<Entity> result)
        {
            result = candidates;
            if (!scheduled) return false;
            pending.Complete();
            scheduled = false;
            if ((frame & LaneSupportLifetimeMask) == 0) laneSupport.Clear();
            while (unknownLanes.TryDequeue(out var lane))
                if (!laneSupport.ContainsKey(lane))
                    laneSupport.TryAdd(lane, owner.Roads.TryLane(EntityManager, lane));
            return true;
        }

        [BurstCompile]
        private struct ScanJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle Entities;
            [ReadOnly] public ComponentTypeHandle<CarCurrentLane> CurrentLanes;
            [ReadOnly] public ComponentTypeHandle<Blocker> Blockers;
            [ReadOnly] public ComponentTypeHandle<Moving> Motion;
            [ReadOnly] public ComponentTypeHandle<VehicleTransform> Poses;
            [ReadOnly] public ComponentTypeHandle<Car> Cars;
            [ReadOnly] public ComponentLookup<Game.Net.CarLane> LaneRules;
            [ReadOnly] public ComponentLookup<LaneCondition> LaneConditions;
            [ReadOnly] public CarLimitsLookup Limits;
            [ReadOnly] public NativeParallelHashMap<Entity, bool> LaneSupport;
            public NativeQueue<Entity>.ParallelWriter Candidates, UnknownLanes;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var entities = chunk.GetNativeArray(Entities);
                var lanes = chunk.GetNativeArray(ref CurrentLanes);
                var blockers = chunk.GetNativeArray(ref Blockers);
                var motion = chunk.GetNativeArray(ref Motion);
                var poses = chunk.GetNativeArray(ref Poses);
                var cars = chunk.GetNativeArray(ref Cars);
                for (int i = 0; i < chunk.Count; i++)
                {
                    Entity vehicle = entities[i];
                    var current = lanes[i];
                    if (!math.all(math.isfinite(current.m_CurvePosition)) || current.m_CurvePosition.x >= current.m_CurvePosition.z
                        || !Supported(current.m_Lane) || current.m_ChangeLane != Entity.Null && !Supported(current.m_ChangeLane))
                        continue;
                    var blocker = blockers[i];
                    bool blocked = blocker.m_Type == BlockerType.Continuing && blocker.m_Blocker != Entity.Null
                        && blocker.m_Blocker != vehicle;
                    if (!blocked)
                    {
                        if (!LaneRules.TryGetComponent(current.m_Lane, out var rules) || !Limits.TryGet(vehicle, out var car))
                            continue;
                        rules.m_SpeedLimit *= VehicleUtils.GetSpeedLimitFactor(cars[i]);
                        bool hasCondition = LaneConditions.TryGetComponent(current.m_Lane, out var condition);
                        float freeSpeed = NativeVehicle.DriveSpeed(car, rules, hasCondition, condition);
                        float speed = math.dot(motion[i].m_Velocity, math.forward(poses[i].m_Rotation));
                        if (!math.isfinite(speed) || speed < 0 || !math.isfinite(freeSpeed) || freeSpeed <= 0
                            || DiscoveryEligibility.IsFreeFlowing(speed, freeSpeed))
                            continue;
                    }
                    Candidates.Enqueue(vehicle);
                }
            }

            private bool Supported(Entity lane)
            {
                if (LaneSupport.TryGetValue(lane, out bool supported)) return supported;
                UnknownLanes.Enqueue(lane);
                return false;
            }
        }
    }
}
