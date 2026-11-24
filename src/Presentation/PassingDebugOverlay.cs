using Colossal.Mathematics;
using System.Collections.Generic;
using Game;
using Game.Rendering;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Overtake
{
    // Presentation reads only canonical pools and their already identified vehicles.
    public partial class PassingDebugOverlay : GameSystemBase
    {
        private static readonly Color Green = new Color(.18f, .72f, .48f, .86f);
        private static readonly Color Yellow = new Color(.95f, .74f, .24f, .90f);
        private static readonly Color Red = new Color(.94f, .24f, .22f, .92f);
        private static readonly Color Purple = new Color(.76f, .36f, .95f, .95f);
        private PassingPoolSystem pools;
        private Game.UI.InGame.SelectedInfoUISystem selection;
        private OverlayRenderSystem overlay;
        private NativeList<DrawCommand> commands;
        private readonly Dictionary<PassingPool, RadarState> radar = new Dictionary<PassingPool, RadarState>();
        private readonly List<PassingPool> retired = new List<PassingPool>();
        private bool wasEnabled;
        private Material fieldMaterial;
        private bool materialAttempted;

        protected override void OnCreate()
        {
            base.OnCreate();
            pools = World.GetOrCreateSystemManaged<PassingPoolSystem>();
            selection = World.GetOrCreateSystemManaged<Game.UI.InGame.SelectedInfoUISystem>();
            overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            commands = new NativeList<DrawCommand>(16, Allocator.Persistent);
            RenderPipelineManager.beginContextRendering += RenderFields;
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            ClearFields();
            retired.Clear();
            wasEnabled = false;
            base.OnGamePreload(purpose, mode);
        }

        protected override void OnDestroy()
        {
            Dependency.Complete();
            RenderPipelineManager.beginContextRendering -= RenderFields;
            ClearFields();
            if (fieldMaterial != null) UnityEngine.Object.Destroy(fieldMaterial);
            if (commands.IsCreated) commands.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            // OFF does no interpolation, geometry preparation, entity reads or draw work.
            if (!pools.DebugOverlayEnabled)
            {
                if (wasEnabled) { ClearFields(); retired.Clear(); wasEnabled = false; }
                return;
            }
            wasEnabled = true;
            if (!materialAttempted)
            {
                materialAttempted = true;
                var shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
                if (shader != null) fieldMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            Dependency.Complete();
            commands.Clear();
            foreach (var state in radar.Values) state.Seen = false;
            float blend = 1 - math.exp(-math.clamp(UnityEngine.Time.unscaledDeltaTime, 0, .2f) / .08f);
            foreach (var pool in pools.Pools)
            {
                if (!pools.Membership.TryOwner(selection.selectedEntity, out var selectedLeader)
                    || selectedLeader != pool.Leader) continue;
                var corridor = pool.Corridor;
                if (pool.Dissolving || corridor == null || corridor.Lanes.Count == 0
                    || !LeaderPosition(pool.Leader, corridor, out var leaderPosition, out float front))
                    continue;
                bool closedRoad = pool.Gate.State == GateState.Purple;
                float available = closedRoad ? math.max(0, corridor.LengthMetres - front)
                    : math.min(math.max(0, pool.Gate.AvailableDistanceMetres), corridor.LengthMetres - front);
                if (!math.isfinite(available)) available = 0;
                Color gate = GateColor(pool.Gate.State);
                if (!radar.TryGetValue(pool, out var state))
                {
                    state = new RadarState();
                    radar.Add(pool, state);
                }
                bool reset = state.Corridor != corridor || math.abs(state.Front - front) > 20;
                bool policyChanged = reset || state.Gate != pool.Gate.State;
                state.Corridor = corridor;
                state.Seen = true;
                state.Front = front;
                // Colors show the shared observation window within the forward passing
                // footprint. Incoming travel is not drawn as kilometres of passing road.
                float yellow = math.max(0, pool.Gate.EffectiveYellowDistanceMetres);
                float red = math.max(0, pool.Gate.EffectiveRedDistanceMetres);
                // Blend captured values only while the same gate holds. A gate transition
                // snaps its partition so smoothing cannot keep a vanished green window.
                state.Available = policyChanged ? available : math.lerp(state.Available, available, blend);
                state.Yellow = policyChanged ? yellow : math.lerp(state.Yellow, yellow, blend);
                state.Red = policyChanged ? red : math.lerp(state.Red, red, blend);
                float fieldTravel = closedRoad
                    ? pool.LeaderSpeedMetresPerSecond * (PassingGate.DepartureSeconds + PassingGate.ReturnSeconds) + PassingGate.ReturnClearanceMetres
                    : pool.Gate.PassingTravelMetres;
                state.Footprint = policyChanged ? fieldTravel : math.lerp(state.Footprint, fieldTravel, blend);
                state.Gate = pool.Gate.State;
                available = math.min(state.Available, corridor.LengthMetres - state.Front);
                yellow = state.Yellow;
                red = state.Red;
                float footprint = math.min(available, state.Footprint);
                float scale = available > .01f ? footprint / available : 0;
                float greenLength = pool.Gate.State == GateState.Green
                    ? math.max(0, available - yellow) * scale : 0;
                float yellowLength = pool.Gate.State == GateState.Green || pool.Gate.State == GateState.Yellow
                    ? math.max(0, math.min(available, yellow) - red) * scale : 0;
                PrepareField(state, corridor, state.Front, footprint, greenLength, yellowLength,
                    pool.Gate.State == GateState.Purple);
                if (pool.ThreatVehicle != Entity.Null && pool.ThreatSpeedMetresPerSecond > .05f
                    && footprint > .01f)
                {
                    // This crest represents window pressure, not the vehicle position.
                    float pressure = math.saturate(available / math.max(.01f, yellow));
                    EdgeCap(corridor, state.Front + footprint * pressure, new Color(.94f, .96f, 1, .65f));
                }
                Circle(leaderPosition, gate, 5);
            }
            retired.Clear();
            foreach (var entry in radar) if (!entry.Value.Seen) retired.Add(entry.Key);
            foreach (var pool in retired)
            {
                UnityEngine.Object.Destroy(radar[pool].Mesh);
                radar.Remove(pool);
            }
            if (commands.Length != 0)
            {
                var buffer = overlay.GetBuffer(out JobHandle bufferDependency);
                Dependency = new DrawJob { Buffer = buffer, Commands = commands.AsArray() }
                    .Schedule(JobHandle.CombineDependencies(Dependency, bufferDependency));
                overlay.AddBufferWriter(Dependency);
            }
        }

        private static Color GateColor(GateState gate)
            => gate == GateState.Green ? Green : gate == GateState.Yellow ? Yellow
                : gate == GateState.Purple ? Purple : Red;

        private void ClearFields()
        {
            foreach (var state in radar.Values) UnityEngine.Object.Destroy(state.Mesh);
            radar.Clear();
        }

        private void RenderFields(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (!pools.DebugOverlayEnabled || fieldMaterial == null) return;
            foreach (var camera in cameras)
            {
                if (camera.cameraType != CameraType.Game) continue;
                foreach (var state in radar.Values)
                {
                    if (!state.Seen || !state.HasField) continue;
                    Graphics.DrawMesh(state.Mesh, Matrix4x4.identity, fieldMaterial, 0, camera, 0, null,
                        castShadows: false, receiveShadows: false);
                }
            }
        }

        private void PrepareField(RadarState state, PassingCorridor corridor, float front,
            float length, float green, float yellow, bool purple)
        {
            state.HasField = length > .01f && fieldMaterial != null;
            if (!state.HasField) return;
            int piece = 0;
            for (int row = 0; row <= RadarState.Samples; row++)
            {
                float distance = length * row / RadarState.Samples;
                float station = front + distance;
                while (piece + 1 < corridor.Lanes.Count
                    && station > corridor.Lanes[piece].StartMetres + corridor.Lanes[piece].LengthMetres) piece++;
                var lane = corridor.Lanes[piece];
                float parameter = lane.ForwardParameterAt(station);
                float3 center = MathUtils.Position(lane.ForwardCurve, parameter) + new float3(0, .15f, 0);
                float3 lateral = Lateral(lane.ForwardCurve, parameter);
                var color = (purple ? Purple : GradientColor(distance, length, green, yellow)).linear;
                float feather = math.min(12, length * .06f);
                color.a *= .16f * math.smoothstep(0, feather, distance)
                    * math.smoothstep(0, feather, length - distance);
                for (int column = 0; column < 4; column++)
                {
                    float offset = column == 0 ? -2.5f : column == 1 ? -1.6f : column == 2 ? 1.6f : 2.5f;
                    int vertex = row * 4 + column;
                    state.Vertices[vertex] = center + lateral * offset;
                    var edgeColor = color;
                    if (column == 0 || column == 3) edgeColor.a = 0;
                    state.Colors[vertex] = edgeColor;
                }
            }
            state.Mesh.vertices = state.Vertices;
            state.Mesh.colors = state.Colors;
            if (!state.IndicesSet) { state.Mesh.triangles = state.Indices; state.IndicesSet = true; }
            state.Mesh.RecalculateBounds();
        }

        private static Color GradientColor(float distance, float available, float green, float yellow)
        {
            float red = math.max(0, available - green - yellow);
            float softness = available / 8;
            Color color = green > .001f ? Green : yellow > .001f ? Yellow : Red;
            if (green > .001f)
            {
                float width = math.min(softness, math.min(green, yellow > .001f ? yellow : red) * .5f);
                float mix = width > .001f ? math.smoothstep(green - width, green + width, distance)
                    : distance >= green ? 1 : 0;
                color = Color.Lerp(Green, yellow > .001f ? Yellow : Red, mix);
            }
            if (yellow > .001f && red > .001f)
            {
                float boundary = green + yellow;
                float width = math.min(softness, math.min(yellow, red) * .5f);
                float mix = width > .001f ? math.smoothstep(boundary - width, boundary + width, distance)
                    : distance >= boundary ? 1 : 0;
                color = Color.Lerp(color, Red, mix);
            }
            return color;
        }

        private static float3 Lateral(Bezier4x3 curve, float t)
        {
            float inverse = 1 - t;
            float3 tangent = inverse * inverse * (curve.b - curve.a)
                + 2 * inverse * t * (curve.c - curve.b) + t * t * (curve.d - curve.c);
            if (math.lengthsq(tangent) < .000001f) tangent = curve.d - curve.a;
            return math.normalizesafe(new float3(-tangent.z, 0, tangent.x));
        }

        private void EdgeCap(PassingCorridor corridor, float station, Color color)
        {
            foreach (var lane in corridor.Lanes)
            {
                if (station > lane.StartMetres + lane.LengthMetres) continue;
                float parameter = lane.ForwardParameterAt(station);
                float3 center = MathUtils.Position(lane.ForwardCurve, parameter) + new float3(0, .08f, 0);
                float3 lateral = Lateral(lane.ForwardCurve, parameter) * 1.6f;
                commands.Add(new DrawCommand { Connector = true, Color = color, Diameter = .6f,
                    Line = new Line3.Segment(center - lateral, center + lateral) });
                return;
            }
        }

        private bool LeaderPosition(Entity vehicle, PassingCorridor corridor, out float3 position, out float front)
        {
            position = default;
            front = 0;
            if (!PassingCorridor.Live(EntityManager, vehicle)
                || !EntityManager.HasComponent<Game.Prefabs.PrefabRef>(vehicle)) return false;
            quaternion rotation;
            // Native vehicle rendering interpolates behind the simulation transform.
            // Presentation must share that position or the envelope floats ahead of its owner.
            if (EntityManager.HasComponent<InterpolatedTransform>(vehicle))
            {
                var transform = EntityManager.GetComponentData<InterpolatedTransform>(vehicle);
                position = transform.m_Position;
                rotation = transform.m_Rotation;
            }
            else if (EntityManager.HasComponent<Game.Objects.Transform>(vehicle))
            {
                var transform = EntityManager.GetComponentData<Game.Objects.Transform>(vehicle);
                position = transform.m_Position;
                rotation = transform.m_Rotation;
            }
            else return false;
            var prefab = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(vehicle).m_Prefab;
            if (!EntityManager.Exists(prefab)
                || !EntityManager.HasComponent<Game.Prefabs.ObjectGeometryData>(prefab)
                || !math.all(math.isfinite(position)) || !math.all(math.isfinite(rotation.value))) return false;
            var bounds = EntityManager.GetComponentData<Game.Prefabs.ObjectGeometryData>(prefab).m_Bounds;
            float3 size = bounds.max - bounds.min;
            if (!math.all(math.isfinite(bounds.min)) || !math.all(math.isfinite(bounds.max))
                || !math.all(size > .01f)) return false;
            // Linked extent includes trailers behind the root. Anchor the field to
            // the rendered root's own bounds, rather than half the whole combination.
            float3 center = position + math.rotate(rotation, (bounds.min + bounds.max) * .5f);
            Entity currentLane = EntityManager.HasComponent<Game.Vehicles.CarCurrentLane>(vehicle)
                ? EntityManager.GetComponentData<Game.Vehicles.CarCurrentLane>(vehicle).m_Lane : Entity.Null;
            if (!corridor.TryStation(currentLane, center, out float station, out var direction)) return false;
            float half = NativeVehicle.HalfExtent(rotation, direction, size);
            front = math.clamp(station + half, 0, corridor.LengthMetres);
            return math.isfinite(front);
        }

        private void Circle(float3 position, Color color, float diameter)
            => commands.Add(new DrawCommand { Circle = true, Color = color, Position = position, Diameter = diameter });

        private sealed class RadarState
        {
            internal const int Samples = 32;
            internal readonly Mesh Mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            internal readonly Vector3[] Vertices = new Vector3[(Samples + 1) * 4];
            internal readonly Color[] Colors = new Color[(Samples + 1) * 4];
            internal readonly int[] Indices = new int[Samples * 3 * 6];
            internal PassingCorridor Corridor;
            internal float Front, Available, Yellow, Red, Footprint;
            internal GateState Gate;
            internal bool Seen, HasField, IndicesSet;
            internal RadarState()
            {
                Mesh.MarkDynamic();
                int index = 0;
                for (int row = 0; row < Samples; row++)
                    for (int column = 0; column < 3; column++)
                    {
                        int a = row * 4 + column, b = a + 4;
                        Indices[index++] = a; Indices[index++] = b; Indices[index++] = a + 1;
                        Indices[index++] = a + 1; Indices[index++] = b; Indices[index++] = b + 1;
                    }
            }
        }

        private struct DrawCommand
        {
            internal bool Circle, Connector;
            internal Color Color;
            internal Line3.Segment Line;
            internal float3 Position;
            internal float Diameter;
        }

        private struct DrawJob : IJob
        {
            [ReadOnly] public NativeArray<DrawCommand> Commands;
            public OverlayRenderSystem.Buffer Buffer;
            public void Execute()
            {
                foreach (var command in Commands)
                {
                    if (command.Circle)
                        Buffer.DrawCircle(command.Color, Color.clear, .18f,
                            OverlayRenderSystem.StyleFlags.Projected, math.forward().xz, command.Position, command.Diameter);
                    else if (command.Connector)
                    {
                        var color = command.Color;
                        color.a *= .55f;
                        Buffer.DrawLine(color, color, 0, OverlayRenderSystem.StyleFlags.Projected,
                            command.Line, command.Diameter > 0 ? command.Diameter : .25f, float2.zero);
                    }
                }
            }
        }
    }
}
