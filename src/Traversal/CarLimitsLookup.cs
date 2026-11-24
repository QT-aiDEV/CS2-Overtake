using Game.Common;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace Overtake
{
    // Job-side counterpart of NativeVehicle.TryCarLimits. Both use the same
    // validation and layout combination, so jobs and the main thread agree.
    internal struct CarLimitsLookup
    {
        [ReadOnly] private ComponentLookup<PrefabRef> prefabs;
        [ReadOnly] private ComponentLookup<CarData> cars;
        [ReadOnly] private BufferLookup<LayoutElement> layouts;
        [ReadOnly] private ComponentLookup<Deleted> deleted;

        internal CarLimitsLookup(SystemBase system)
        {
            prefabs = system.GetComponentLookup<PrefabRef>(true);
            cars = system.GetComponentLookup<CarData>(true);
            layouts = system.GetBufferLookup<LayoutElement>(true);
            deleted = system.GetComponentLookup<Deleted>(true);
        }

        internal void Update(SystemBase system)
        {
            prefabs.Update(system);
            cars.Update(system);
            layouts.Update(system);
            deleted.Update(system);
        }

        internal bool TryGet(Entity vehicle, out CarData limits)
        {
            if (!TryPrefab(vehicle, out limits)) return false;
            if (!layouts.TryGetBuffer(vehicle, out var layout)) return true;
            if (!NativeVehicle.LayoutRootedAt(layout, vehicle)) return false;
            for (int i = 1; i < layout.Length; i++)
            {
                if (!TryPrefab(layout[i].m_Vehicle, out var member)) return false;
                NativeVehicle.CombineLimits(ref limits, member);
            }
            return true;
        }

        private bool TryPrefab(Entity vehicle, out CarData limits)
        {
            limits = default;
            return vehicle != Entity.Null && !deleted.HasComponent(vehicle)
                && prefabs.TryGetComponent(vehicle, out var prefab)
                && cars.TryGetComponent(prefab.m_Prefab, out limits) && NativeVehicle.ValidLimits(limits);
        }
    }
}
