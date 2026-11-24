using Game;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Simulation;
using Game.Vehicles;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Overtake
{
    // Adjusts native speed commands for leased passers between native
    // navigation and movement: the terminal recovery cap or the overtake speed.
    // The work is a job chained after native navigation, so the main thread
    // never waits for navigation to finish.
    public partial class PassingSpeedSystem : GameSystemBase
    {
        private PassingPoolSystem pools;
        private NativeList<SpeedRequest> requests;
        private NativeParallelHashMap<Entity, OpposingLane> opposingLanes;
        private JobHandle previous;
        private ComponentLookup<CarNavigation> navigation;
        private ComponentLookup<Moving> motion;
        private ComponentLookup<CarCurrentLane> currentLanes;
        private ComponentLookup<Blocker> blockers;
        private ComponentLookup<PathOwner> pathOwners;
        private ComponentLookup<Car> cars;
        private ComponentLookup<LaneCondition> laneConditions;
        private CarLimitsLookup limits;

        private struct SpeedRequest
        {
            public Entity Vehicle;
            public float RecoverySpeed;
            public bool Recovery;
        }

        private struct OpposingLane
        {
            public Game.Net.CarLane Rules;
            public bool AllowsPassing;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            pools = World.GetOrCreateSystemManaged<PassingPoolSystem>();
            requests = new NativeList<SpeedRequest>(16, Allocator.Persistent);
            opposingLanes = new NativeParallelHashMap<Entity, OpposingLane>(256, Allocator.Persistent);
            navigation = GetComponentLookup<CarNavigation>();
            motion = GetComponentLookup<Moving>(true);
            currentLanes = GetComponentLookup<CarCurrentLane>(true);
            blockers = GetComponentLookup<Blocker>(true);
            pathOwners = GetComponentLookup<PathOwner>(true);
            cars = GetComponentLookup<Car>(true);
            laneConditions = GetComponentLookup<LaneCondition>(true);
            limits = new CarLimitsLookup(this);
        }

        protected override void OnDestroy()
        {
            previous.Complete();
            requests.Dispose();
            opposingLanes.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            if (pools.HeldTraversalCount == 0) return;
            uint frame = World.GetOrCreateSystemManaged<SimulationSystem>().frameIndex;
            int slot = (int)(frame & 15);
            // The previous tick's job finished long ago; this only guards reuse
            // of the request list and lane map.
            previous.Complete();
            requests.Clear();
            opposingLanes.Clear();
            foreach (var traversal in pools.Traversals)
            {
                bool recovery = traversal.WantsRecoveryCap(frame);
                if (!recovery && !(traversal.WantsOvertakeSpeed && traversal.InUpdateSlot(EntityManager, slot))) continue;
                requests.Add(new SpeedRequest
                {
                    Vehicle = traversal.Vehicle, Recovery = recovery, RecoverySpeed = traversal.RecoverySpeedMetresPerSecond
                });
                foreach (var piece in traversal.Road.Lanes)
                    opposingLanes.TryAdd(piece.OpposingLane, new OpposingLane { Rules = piece.OpposingRules, AllowsPassing = piece.AllowsPassing });
            }
            if (requests.Length == 0) return;
            navigation.Update(this);
            motion.Update(this);
            currentLanes.Update(this);
            blockers.Update(this);
            pathOwners.Update(this);
            cars.Update(this);
            laneConditions.Update(this);
            limits.Update(this);
            previous = new SpeedJob
            {
                Requests = requests.AsArray(),
                OpposingLanes = opposingLanes,
                Navigation = navigation,
                Motion = motion,
                CurrentLanes = currentLanes,
                Blockers = blockers,
                PathOwners = pathOwners,
                Cars = cars,
                LaneConditions = laneConditions,
                Limits = limits
            }.Schedule(requests.Length, 4, Dependency);
            Dependency = previous;
        }

        [BurstCompile]
        private struct SpeedJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<SpeedRequest> Requests;
            [ReadOnly] public NativeParallelHashMap<Entity, OpposingLane> OpposingLanes;
            // Requests name distinct vehicles, so parallel writes never alias.
            [NativeDisableParallelForRestriction] public ComponentLookup<CarNavigation> Navigation;
            [ReadOnly] public ComponentLookup<Moving> Motion;
            [ReadOnly] public ComponentLookup<CarCurrentLane> CurrentLanes;
            [ReadOnly] public ComponentLookup<Blocker> Blockers;
            [ReadOnly] public ComponentLookup<PathOwner> PathOwners;
            [ReadOnly] public ComponentLookup<Car> Cars;
            [ReadOnly] public ComponentLookup<LaneCondition> LaneConditions;
            [ReadOnly] public CarLimitsLookup Limits;

            public void Execute(int index)
            {
                var request = Requests[index];
                Entity vehicle = request.Vehicle;
                if (!Navigation.TryGetComponent(vehicle, out var command) || !Motion.TryGetComponent(vehicle, out var moving)
                    || !CurrentLanes.TryGetComponent(vehicle, out var current) || !Blockers.TryGetComponent(vehicle, out var blocker)
                    || !Cars.TryGetComponent(vehicle, out var car) || !Limits.TryGet(vehicle, out var limits)
                    || !PathOwners.TryGetComponent(vehicle, out var owner) || (owner.m_State & ~PathFlags.Debug) != 0
                    || !OpposingLanes.TryGetValue(current.m_Lane, out var lane)) return;
                float speed = math.length(moving.m_Velocity);
                float next;
                if (request.Recovery)
                {
                    // Opposing-lane travel runs against the lane's parameter direction.
                    if (current.m_CurvePosition.x <= current.m_CurvePosition.z) return;
                    next = TerminalRecovery.ApplyCap(command.m_MaxSpeed, speed, limits.m_Braking, request.RecoverySpeed);
                }
                else
                {
                    if (current.m_ChangeLane != Entity.Null || !lane.AllowsPassing) return;
                    speed = math.min(speed, limits.m_MaxSpeed);
                    float reachable = VehicleUtils.CalculateSpeedRange(limits, speed, NativeTiming.StepSeconds).max;
                    var rules = lane.Rules;
                    rules.m_SpeedLimit *= VehicleUtils.GetSpeedLimitFactor(car);
                    bool hasCondition = LaneConditions.TryGetComponent(current.m_Lane, out var condition);
                    float drive = NativeVehicle.DriveSpeed(limits, rules, hasCondition, condition);
                    rules.m_SpeedLimit *= OvertakeSpeed.LimitFactor;
                    float ceiling = OvertakeSpeed.Ceiling(drive, NativeVehicle.DriveSpeed(limits, rules, hasCondition, condition),
                        limits.m_Braking, NativeTiming.StepSeconds);
                    next = OvertakeSpeed.Command(command.m_MaxSpeed, reachable, drive, ceiling, blocker.m_Blocker != Entity.Null);
                }
                if (!math.isfinite(next) || next == command.m_MaxSpeed) return;
                command.m_MaxSpeed = next;
                Navigation[vehicle] = command;
            }
        }
    }
}
