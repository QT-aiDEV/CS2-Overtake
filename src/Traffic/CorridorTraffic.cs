using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace Overtake
{
    internal struct VehicleObservation
    {
        internal Entity Root;
        // Front/Rear are the maximum/minimum corridor stations, regardless of heading.
        internal float FrontMetres, RearMetres, LengthMetres;
        // Signed native physical speed remains separate from station progress.
        internal float SpeedMetresPerSecond, MinimumSpeedMetresPerSecond, MaximumSpeedMetresPerSecond;
        internal float StationSpeedMetresPerSecond, MinimumStationProgressMetresPerSecond, MaximumStationProgressMetresPerSecond;
        internal float MinimumLateralMetres, MaximumLateralMetres, LateralCentreMetres;
        internal float LateralRadiusMetres => math.max(LateralCentreMetres - MinimumLateralMetres,
            MaximumLateralMetres - LateralCentreMetres);

        // Signed lateral intervals include every linked body's rotated geometry.
        internal bool OccupiesForwardPath(float pathHalfWidthMetres)
            => MinimumLateralMetres <= pathHalfWidthMetres && MaximumLateralMetres >= -pathHalfWidthMetres;
        internal float3 Position;
        internal bool Oncoming, OccupiesOpposing;

        internal bool TryMergeDuration(float landingMetres, out float seconds)
            => ReturnClearance.TryMergeDuration(landingMetres, RearMetres,
                MinimumStationProgressMetresPerSecond, out seconds);

        // Package bounds can be owned by different linked bodies. Keep both rate
        // extrema so a faster tractor cannot conceal its slower rear's closing gap.
        internal void MinimumMergeGaps(in VehicleObservation other, float seconds, out float ahead, out float behind)
        {
            ahead = ReturnClearance.MinimumGapDuringMerge(other.RearMetres - FrontMetres,
                other.MinimumStationProgressMetresPerSecond - MaximumStationProgressMetresPerSecond, 0, seconds);
            behind = ReturnClearance.MinimumGapDuringMerge(RearMetres - other.FrontMetres,
                MinimumStationProgressMetresPerSecond - other.MaximumStationProgressMetresPerSecond, 0, seconds);
        }
    }

    internal sealed class CorridorTraffic
    {
        private const int MaximumReferences = 4096, MaximumVehicles = 256;
        private readonly List<VehicleObservation> observations = new List<VehicleObservation>(64);
        private readonly HashSet<Entity> roots = new HashSet<Entity>();
        private readonly HashSet<Entity> scannedLanes = new HashSet<Entity>();
        private readonly Dictionary<Entity, int> observationIndices = new Dictionary<Entity, int>();
        internal IReadOnlyList<VehicleObservation> Observations => observations;
        internal PassingCorridor Corridor { get; private set; }
        internal uint Frame { get; private set; } = uint.MaxValue;
        internal bool Complete
        {
            get; private set;
        }
        internal Entity NearestOncoming
        {
            get; private set;
        }
        internal float NearestOncomingDistanceMetres
        {
            get; private set;
        }
        internal float NearestOncomingSpeedMetresPerSecond
        {
            get; private set;
        }

        // Geometry uses forward-lane metres. The gate converts physical closing
        // speeds into that shared coordinate at the decision boundary.
        // Only UI display multiplies native speed by 1.8 for displayed km/h.
        // Caller validates the immutable corridor first. Occupancy includes
        // stopped cars and other pools' passers.
        internal void Refresh(EntityManager em, PassingCorridor corridor, uint frame, float leaderFrontMetres = 0)
        {
            Corridor = corridor;
            Frame = frame;
            observations.Clear();
            roots.Clear();
            scannedLanes.Clear();
            observationIndices.Clear();
            Complete = false;
            NearestOncoming = Entity.Null;
            NearestOncomingDistanceMetres = float.PositiveInfinity;
            NearestOncomingSpeedMetresPerSecond = 0;
            if (corridor == null || corridor.Lanes.Count == 0 || !math.isfinite(leaderFrontMetres))
                return;
            int references = 0;
            foreach (var lane in corridor.Lanes)
                if (!ReadLane(em, corridor, lane.ForwardLane, false, ref references)
                    || !ReadLane(em, corridor, lane.OpposingLane, true, ref references))
                    return;
            observations.Sort(CompareStation);
            foreach (var observation in observations)
            {
                if (!observation.Oncoming || observation.FrontMetres < leaderFrontMetres)
                    continue;
                float distance = math.max(0, observation.RearMetres - leaderFrontMetres);
                if (distance >= NearestOncomingDistanceMetres)
                    continue;
                NearestOncoming = observation.Root;
                NearestOncomingDistanceMetres = distance;
                NearestOncomingSpeedMetresPerSecond = -observation.MinimumSpeedMetresPerSecond;
            }
            Complete = true;
        }

        private static int CompareStation(VehicleObservation a, VehicleObservation b)
        {
            int station = a.RearMetres.CompareTo(b.RearMetres);
            return station != 0 ? station : a.Root.Index.CompareTo(b.Root.Index);
        }

        private bool ReadLane(EntityManager em, PassingCorridor corridor, Entity lane, bool opposing, ref int references)
        {
            if (!scannedLanes.Add(lane))
                return true;
            if (!PassingCorridor.Live(em, lane) || !em.HasBuffer<LaneObject>(lane))
                return false;
            var occupants = em.GetBuffer<LaneObject>(lane, true);
            if (occupants.Length > MaximumReferences - references)
                return false;
            references += occupants.Length;
            foreach (var occupant in occupants)
            {
                Entity root = NativeVehicle.Root(em, occupant.m_LaneObject);
                if (root == Entity.Null)
                    return false;
                if (roots.Contains(root))
                {
                    if (opposing)
                    {
                        int index = observationIndices[root];
                        var existing = observations[index];
                        existing.OccupiesOpposing = true;
                        observations[index] = existing;
                    }
                    continue;
                }
                if (!TryObserve(em, corridor, root, out var observation))
                {
                    // Lane buffers also contain static roadside objects. Only a
                    // complete geometric separation proves they are irrelevant.
                    if (StaticOutsideRoad(em, corridor, root, lane)) continue;
                    return false;
                }
                if (roots.Count >= MaximumVehicles) return false;
                roots.Add(root);
                observation.OccupiesOpposing = opposing;
                observationIndices.Add(root, observations.Count);
                observations.Add(observation);
            }
            return true;
        }

        internal static bool TryObserve(EntityManager em, PassingCorridor corridor, Entity entity, out VehicleObservation observation)
        {
            observation = default;
            Entity root = NativeVehicle.Root(em, entity);
            Entity rootLane = root != Entity.Null && em.HasComponent<CarCurrentLane>(root)
                ? em.GetComponentData<CarCurrentLane>(root).m_Lane : Entity.Null;
            if (root == Entity.Null || !em.HasComponent<Car>(root) || !TryPart(em, corridor, root, rootLane, out observation))
                return false;
            if (em.HasBuffer<LayoutElement>(root))
            {
                var layout = em.GetBuffer<LayoutElement>(root, true);
                if (layout.Length > NativeVehicle.MaximumParts || (layout.Length > 0 && layout[0].m_Vehicle != root))
                    return false;
                for (int i = 1; i < layout.Length; i++)
                {
                    Entity member = layout[i].m_Vehicle;
                    if (NativeVehicle.Root(em, member) != root || !TryPart(em, corridor, member, rootLane, out var part))
                        return false;
                    observation.FrontMetres = math.max(observation.FrontMetres, part.FrontMetres);
                    observation.RearMetres = math.min(observation.RearMetres, part.RearMetres);
                    observation.MinimumLateralMetres = math.min(observation.MinimumLateralMetres, part.MinimumLateralMetres);
                    observation.MaximumLateralMetres = math.max(observation.MaximumLateralMetres, part.MaximumLateralMetres);
                    observation.MinimumStationProgressMetresPerSecond = math.min(
                        observation.MinimumStationProgressMetresPerSecond, part.MinimumStationProgressMetresPerSecond);
                    observation.MaximumStationProgressMetresPerSecond = math.max(
                        observation.MaximumStationProgressMetresPerSecond, part.MaximumStationProgressMetresPerSecond);
                    observation.MinimumSpeedMetresPerSecond = math.min(observation.MinimumSpeedMetresPerSecond, part.MinimumSpeedMetresPerSecond);
                    observation.MaximumSpeedMetresPerSecond = math.max(observation.MaximumSpeedMetresPerSecond, part.MaximumSpeedMetresPerSecond);
                }
            }
            observation.Root = root;
            observation.LengthMetres = observation.FrontMetres - observation.RearMetres;
            // Classification follows signed road-relative motion, including vehicles
            // that temporarily occupy the other lane during a native maneuver.
            observation.Oncoming = observation.SpeedMetresPerSecond < -0.05f;
            return math.isfinite(observation.LengthMetres) && observation.LengthMetres > 0.01f;
        }

        private static bool StaticOutsideRoad(EntityManager em, PassingCorridor corridor, Entity entity, Entity lane)
        {
            foreach (var piece in corridor.Lanes)
                if (piece.ForwardLane == lane || piece.OpposingLane == lane)
                    return StaticOutsideLane(em, entity, piece.ForwardLane)
                        && StaticOutsideLane(em, entity, piece.OpposingLane);
            return false;
        }

        private static bool StaticOutsideLane(EntityManager em, Entity entity, Entity lane)
        {
            if (em.HasComponent<Car>(entity) || em.HasComponent<CarTrailer>(entity) || em.HasComponent<Moving>(entity)
                || !em.HasComponent<Transform>(entity) || !em.HasComponent<PrefabRef>(entity)
                || !em.HasComponent<Curve>(lane) || !em.HasComponent<PrefabRef>(lane)) return false;
            Entity lanePrefab = em.GetComponentData<PrefabRef>(lane).m_Prefab;
            if (!em.Exists(lanePrefab) || !em.HasComponent<NetLaneData>(lanePrefab)) return false;
            float laneWidth = em.GetComponentData<NetLaneData>(lanePrefab).m_Width;
            if (em.HasComponent<NodeLane>(lane))
            {
                float2 offsets = em.GetComponentData<NodeLane>(lane).m_WidthOffset;
                if (!math.all(math.isfinite(offsets))) return false;
                laneWidth += math.cmax(offsets);
            }
            float laneHalfWidth = laneWidth * .5f;
            if (!math.isfinite(laneHalfWidth) || laneHalfWidth <= 0) return false;
            Entity prefab = em.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (!em.Exists(prefab) || !em.HasComponent<ObjectGeometryData>(prefab)) return false;
            var bounds = em.GetComponentData<ObjectGeometryData>(prefab).m_Bounds;
            var transform = em.GetComponentData<Transform>(entity);
            float3 size = bounds.max - bounds.min;
            if (!math.all(math.isfinite(bounds.min)) || !math.all(math.isfinite(bounds.max)) || !math.all(size > .01f)
                || !math.all(math.isfinite(transform.m_Position)) || !math.all(math.isfinite(transform.m_Rotation.value))
                || math.abs(math.lengthsq(transform.m_Rotation.value) - 1) > .02f) return false;
            float3 centre = transform.m_Position + math.rotate(transform.m_Rotation, (bounds.min + bounds.max) * .5f);
            var curve = em.GetComponentData<Curve>(lane).m_Bezier;
            MathUtils.Distance(curve, centre, out float parameter);
            // A sphere around the complete static body remains conservative on
            // curved lane geometry; centre-only lateral distance does not.
            float distance = math.distance(centre, MathUtils.Position(curve, parameter));
            float radius = math.length(size) * .5f;
            return math.isfinite(distance) && math.isfinite(radius) && distance - radius > laneHalfWidth;
        }

        private static bool TryPart(EntityManager em, PassingCorridor corridor, Entity entity, Entity rootLane,
            out VehicleObservation observation)
        {
            observation = default;
            if (corridor == null || !PassingCorridor.Live(em, entity)
                || (!em.HasComponent<Car>(entity) && !em.HasComponent<CarTrailer>(entity))
                || !em.HasComponent<Transform>(entity) || !em.HasComponent<Moving>(entity)
                || !em.HasComponent<PrefabRef>(entity))
                return false;
            Entity prefab = em.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (!em.Exists(prefab) || !em.HasComponent<ObjectGeometryData>(prefab))
                return false;
            var bounds = em.GetComponentData<ObjectGeometryData>(prefab).m_Bounds;
            var transform = em.GetComponentData<Transform>(entity);
            var motion = em.GetComponentData<Moving>(entity);
            float3 size = bounds.max - bounds.min;
            if (!math.all(math.isfinite(bounds.min)) || !math.all(math.isfinite(bounds.max)) || !math.all(size > 0.01f)
                || !math.all(math.isfinite(transform.m_Position)) || !math.all(math.isfinite(transform.m_Rotation.value))
                || math.abs(math.lengthsq(transform.m_Rotation.value) - 1) > 0.02f || !math.all(math.isfinite(motion.m_Velocity)))
                return false;
            float3 center = transform.m_Position + math.rotate(transform.m_Rotation, (bounds.min + bounds.max) * 0.5f);
            Entity currentLane = em.HasComponent<CarCurrentLane>(entity)
                ? em.GetComponentData<CarCurrentLane>(entity).m_Lane
                : em.HasComponent<CarTrailerLane>(entity) ? em.GetComponentData<CarTrailerLane>(entity).m_Lane : rootLane;
            if (currentLane == Entity.Null) currentLane = rootLane;
            if (!corridor.TryStation(currentLane, center, out float station, out var direction))
                return false;
            float length = 2 * NativeVehicle.HalfExtent(transform.m_Rotation, direction, size);
            float3 lateral = math.normalizesafe(new float3(-direction.z, 0, direction.x));
            if (math.lengthsq(lateral) < .99f) return false;
            float lateralCentre = math.dot(center - corridor.PositionAt(station), lateral);
            float lateralHalfExtent = NativeVehicle.HalfExtent(transform.m_Rotation, lateral, size);
            float speed = math.dot(motion.m_Velocity, direction) < 0
                ? -math.length(motion.m_Velocity) : math.length(motion.m_Velocity);
            float stationProgress = corridor.StationSpeed(currentLane, center, motion.m_Velocity, direction);
            float stationScale = corridor.OpposingScaleFor(currentLane, out bool opposingCoordinate);
            // Opposing and forward tangents can differ. The physical diagonal
            // bounds the package on either tangent before station conversion.
            if (opposingCoordinate) length = math.length(size);
            length *= stationScale;
            if (!math.isfinite(length) || length <= 0.01f || !math.isfinite(speed) || !math.isfinite(stationProgress))
                return false;
            observation = new VehicleObservation
            {
                Root = entity,
                FrontMetres = station + length * 0.5f,
                RearMetres = station - length * 0.5f,
                LengthMetres = length,
                Position = center,
                SpeedMetresPerSecond = speed,
                MinimumSpeedMetresPerSecond = speed,
                MaximumSpeedMetresPerSecond = speed,
                StationSpeedMetresPerSecond = stationProgress,
                MinimumStationProgressMetresPerSecond = opposingCoordinate && stationProgress > 0
                    ? math.min(stationProgress, math.max(0, speed) * math.min(1, corridor.MinimumOpposingStationScale))
                    : stationProgress,
                MaximumStationProgressMetresPerSecond = opposingCoordinate && stationProgress > 0
                    ? math.max(stationProgress, math.max(0, speed) * stationScale)
                    : stationProgress,
                LateralCentreMetres = lateralCentre,
                MinimumLateralMetres = lateralCentre - lateralHalfExtent,
                MaximumLateralMetres = lateralCentre + lateralHalfExtent
            };
            return true;
        }
    }
}
