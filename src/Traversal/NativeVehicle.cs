using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Vehicles;
using Colossal.Mathematics;
using Unity.Entities;
using Unity.Mathematics;
using VehicleTransform = Game.Objects.Transform;

namespace Overtake
{
    internal static class NativeVehicle
    {
        // Mod work bounds apply consistently to admission, ownership and cleanup.
        internal const int MaximumParts = 16, MaximumNavigationEntries = 64;

        // Main-thread form: reads the lane's optional wear condition.
        internal static float LaneDriveSpeed(EntityManager em, CarData car, Game.Net.CarLane rules, Entity lane)
        {
            bool worn = em.HasComponent<Game.Net.LaneCondition>(lane);
            return DriveSpeed(car, rules, worn, worn ? em.GetComponentData<Game.Net.LaneCondition>(lane) : default);
        }

        internal static float DriveSpeed(CarData car, Game.Net.CarLane lane, bool hasCondition, Game.Net.LaneCondition condition)
        {
            float speed = VehicleUtils.GetMaxDriveSpeed(car, lane);
            if (hasCondition) VehicleUtils.ModifyDriveSpeed(ref speed, condition);
            return math.min(car.m_MaxSpeed, speed);
        }

        internal static float StationAccelerationFloor(CarData car, float stationCruise, float stationScale)
        {
            float cruise = stationCruise / stationScale;
            var reachable = VehicleUtils.CalculateSpeedRange(car, cruise, NativeTiming.StepSeconds);
            return math.max(0, (reachable.max - cruise) / NativeTiming.StepSeconds) * stationScale;
        }

        internal static bool TryCarLimits(EntityManager em, Entity vehicle, out CarData limits)
        {
            if (!TryPrefabLimits(em, vehicle, out limits)) return false;
            if (!em.HasBuffer<LayoutElement>(vehicle)) return true;
            var layout = em.GetBuffer<LayoutElement>(vehicle, true);
            if (!LayoutRootedAt(layout, vehicle)) return false;
            for (int i = 1; i < layout.Length; i++)
            {
                if (!TryPrefabLimits(em, layout[i].m_Vehicle, out var member)) return false;
                CombineLimits(ref limits, member);
            }
            return true;
        }

        private static bool TryPrefabLimits(EntityManager em, Entity vehicle, out CarData limits)
        {
            limits = default;
            if (!Alive(em, vehicle) || !em.HasComponent<PrefabRef>(vehicle)) return false;
            Entity prefab = em.GetComponentData<PrefabRef>(vehicle).m_Prefab;
            if (!Alive(em, prefab) || !em.HasComponent<CarData>(prefab)) return false;
            limits = em.GetComponentData<CarData>(prefab);
            return ValidLimits(limits);
        }

        // Half the extent of an oriented box of the given size along a world axis.
        internal static float HalfExtent(quaternion rotation, float3 axis, float3 size)
            => math.dot(math.abs(math.rotate(math.inverse(rotation), axis)), size) * .5f;

        internal static bool LayoutRootedAt(DynamicBuffer<LayoutElement> layout, Entity root)
            => layout.Length <= MaximumParts && (layout.Length == 0 || layout[0].m_Vehicle == root);

        // CarNavigationSystem.UpdateCarLimits combines the native layout's dynamics.
        // The controlling root still supplies pivot and other geometry fields.
        internal static void CombineLimits(ref CarData limits, in CarData member)
        {
            limits.m_Acceleration = math.min(limits.m_Acceleration, member.m_Acceleration);
            limits.m_Braking = math.min(limits.m_Braking, member.m_Braking);
            limits.m_MaxSpeed = math.min(limits.m_MaxSpeed, member.m_MaxSpeed);
            limits.m_Turning = math.min(limits.m_Turning, member.m_Turning);
        }

        internal static bool ValidLimits(in CarData limits)
            => math.isfinite(limits.m_MaxSpeed) && limits.m_MaxSpeed > 0
                && math.isfinite(limits.m_Braking) && limits.m_Braking > 0
                && math.isfinite(limits.m_Acceleration) && limits.m_Acceleration >= 0
                && math.all(math.isfinite(limits.m_Turning)) && math.all(limits.m_Turning >= 0);

        internal static bool TryContinuingBlocker(EntityManager em, Entity vehicle, out Entity blocker)
        {
            blocker = Entity.Null;
            if (!Alive(em, vehicle) || !em.HasComponent<Blocker>(vehicle))
                return false;
            var value = em.GetComponentData<Blocker>(vehicle);
            if (value.m_Type != BlockerType.Continuing)
                return false;
            blocker = Root(em, value.m_Blocker);
            return blocker != Entity.Null && blocker != vehicle;
        }

        // Measures the whole linked footprint along the root's heading. Speeds remain native metres/second.
        internal static bool TryMeasure(EntityManager em, Entity vehicle, out float3 position,
            out float speedMetresPerSecond, out float lengthMetres)
        {
            position = default;
            speedMetresPerSecond = lengthMetres = 0;
            if (Root(em, vehicle) != vehicle || !em.HasComponent<Car>(vehicle)
                || !em.HasComponent<Moving>(vehicle) || !em.HasComponent<VehicleTransform>(vehicle))
                return false;
            var transform = em.GetComponentData<VehicleTransform>(vehicle);
            if (!ValidTransform(transform))
                return false;
            float3 heading = math.forward(transform.m_Rotation);
            var velocity = em.GetComponentData<Moving>(vehicle).m_Velocity;
            if (!math.all(math.isfinite(velocity)))
                return false;
            speedMetresPerSecond = math.dot(velocity, heading);
            float rear = float.PositiveInfinity, front = float.NegativeInfinity;
            if (!Include(em, vehicle, transform.m_Position, heading, ref rear, ref front))
                return false;
            if (em.HasBuffer<LayoutElement>(vehicle))
            {
                var layout = em.GetBuffer<LayoutElement>(vehicle, true);
                if (layout.Length > MaximumParts || (layout.Length > 0 && layout[0].m_Vehicle != vehicle))
                    return false;
                for (int i = 1; i < layout.Length; i++)
                    if (Root(em, layout[i].m_Vehicle) != vehicle
                        || !Include(em, layout[i].m_Vehicle, transform.m_Position, heading, ref rear, ref front))
                        return false;
            }
            lengthMetres = front - rear;
            position = transform.m_Position + heading * ((rear + front) * 0.5f);
            return math.isfinite(speedMetresPerSecond) && math.isfinite(lengthMetres)
                && lengthMetres > 0 && math.all(math.isfinite(position));
        }

        internal static Entity Root(EntityManager em, Entity part)
        {
            for (int depth = 0; depth < MaximumParts; depth++)
            {
                if (!Alive(em, part))
                    return Entity.Null;
                if (!em.HasComponent<Controller>(part))
                    return em.HasComponent<CarTrailer>(part) ? Entity.Null : part;
                Entity parent = em.GetComponentData<Controller>(part).m_Controller;
                if (parent == part)
                    return em.HasComponent<CarTrailer>(part) ? Entity.Null : part;
                if (!Alive(em, parent) || !em.HasBuffer<LayoutElement>(parent))
                    return Entity.Null;
                var layout = em.GetBuffer<LayoutElement>(parent, true);
                if (layout.Length > MaximumParts)
                    return Entity.Null;
                bool found = false;
                for (int i = 0; i < layout.Length; i++)
                    found |= layout[i].m_Vehicle == part;
                if (!found)
                    return Entity.Null;
                part = parent;
            }
            return Entity.Null;
        }

        private static bool Include(EntityManager em, Entity part, float3 origin, float3 heading,
            ref float rear, ref float front)
        {
            if (!Alive(em, part) || !em.HasComponent<VehicleTransform>(part)
                || !em.HasComponent<PrefabRef>(part))
                return false;
            Entity prefab = em.GetComponentData<PrefabRef>(part).m_Prefab;
            if (!Alive(em, prefab) || !em.HasComponent<ObjectGeometryData>(prefab))
                return false;
            var transform = em.GetComponentData<VehicleTransform>(part);
            var bounds = em.GetComponentData<ObjectGeometryData>(prefab).m_Bounds;
            float3 size = bounds.max - bounds.min;
            if (!ValidTransform(transform) || !math.all(math.isfinite(bounds.min))
                || !math.all(math.isfinite(bounds.max)) || !math.all(size > 0.01f))
                return false;
            float3 centre = transform.m_Position + math.rotate(transform.m_Rotation, (bounds.min + bounds.max) * 0.5f);
            float at = math.dot(centre - origin, heading);
            float halfLength = HalfExtent(transform.m_Rotation, heading, size);
            rear = math.min(rear, at - halfLength);
            front = math.max(front, at + halfLength);
            return math.isfinite(at) && math.isfinite(halfLength);
        }

        private static bool Alive(EntityManager em, Entity entity) => PassingCorridor.Live(em, entity);
        private static bool ValidTransform(VehicleTransform transform)
            => math.all(math.isfinite(transform.m_Position)) && math.all(math.isfinite(transform.m_Rotation.value))
                && math.abs(math.lengthsq(transform.m_Rotation.value) - 1) < 0.02f;
    }
}
