using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using RoadFlags = Game.Net.CarLaneFlags;
using NavigationFlags = Game.Vehicles.CarLaneFlags;
using SubLane = Game.Net.SubLane;
using Edge = Game.Net.Edge;

namespace Overtake
{
    internal enum CorridorTermination
    {
        PieceBudget, UnsupportedLane, UnsupportedOwner,
        RestrictedLane, InvalidGeometry, UnpairedLane, Junction, Discontinuity, RouteLoop,
        LaneEnded, AmbiguousFork, LaneCountChanged, DividedCarriageway, CoverageUnavailable, HorizonCovered
    }

    internal sealed class CorridorLane
    {
        internal Entity ForwardLane, OpposingLane;
        internal float StartMetres, LengthMetres, ForwardStart, ForwardEnd, OpposingStart, OpposingEnd;
        internal Bezier4x3 ForwardCurve, OpposingCurve;
        internal Entity Owner;
        internal Lane ForwardNodes, OpposingNodes;
        internal Game.Net.CarLane ForwardRules, OpposingRules;
        internal bool AllowsPassing;
        internal bool SharedEdgeParameter;
        internal float OpposingStationScale = 1;
        internal float OpposingMinimumStationScale;
        private const int ArcSamples = 32;
        private float[] forwardDistances;
        private float[] opposingParameters;
        private float[] opposingArcDistances;
        private float forwardArcLength;
        internal Bezier4x3 ForwardInterval, OpposingInterval;
        internal float ForwardChordRadius, OpposingChordRadius;

        internal CorridorLane CopyAt(float station)
        {
            // Captured geometry maps are immutable after PrepareGeometry.
            var copy = (CorridorLane)MemberwiseClone();
            copy.StartMetres = station;
            return copy;
        }

        internal bool PrepareGeometry()
        {
            PrepareCorrespondence();
            ForwardInterval = MathUtils.Cut(ForwardCurve, new float2(ForwardStart, ForwardEnd));
            OpposingInterval = MathUtils.Cut(OpposingCurve, new float2(OpposingEnd, OpposingStart));
            ForwardChordRadius = ChordRadius(ForwardInterval);
            OpposingChordRadius = ChordRadius(OpposingInterval);
            forwardDistances = new float[ArcSamples + 1];
            opposingParameters = new float[ArcSamples + 1];
            opposingParameters[0] = OpposingStart;
            for (int i = 1; i <= ArcSamples; i++)
            {
                float parameter = math.lerp(ForwardStart, ForwardEnd, (float)i / ArcSamples);
                forwardDistances[i] = MathUtils.Length(ForwardCurve,
                    new Bounds1(ForwardStart, parameter));
                opposingParameters[i] = OpposingParameter(parameter);
                if (!math.isfinite(forwardDistances[i]) || forwardDistances[i] <= forwardDistances[i - 1]
                    || !math.isfinite(opposingParameters[i]) || opposingParameters[i] >= opposingParameters[i - 1])
                    return false;
            }
            LengthMetres = forwardDistances[ArcSamples];
            return true;
        }

        private static float ChordRadius(Bezier4x3 curve)
        {
            float3 direction = math.normalizesafe(curve.d - curve.a);
            return math.max(math.length(curve.b - curve.a - direction * math.dot(curve.b - curve.a, direction)),
                math.length(curve.c - curve.a - direction * math.dot(curve.c - curve.a, direction)));
        }

        internal float ForwardParameterAt(float distanceMetres)
        {
            float distance = math.clamp(distanceMetres - StartMetres, 0, LengthMetres);
            if (distance <= 0) return ForwardStart;
            if (distance >= LengthMetres) return ForwardEnd;
            int sample = 0;
            while (sample + 1 < ArcSamples && forwardDistances[sample + 1] < distance) sample++;
            float low = math.lerp(ForwardStart, ForwardEnd, (float)sample / ArcSamples);
            float high = math.lerp(ForwardStart, ForwardEnd, (float)(sample + 1) / ArcSamples);
            float parameter = math.lerp(low, high, (distance - forwardDistances[sample])
                / math.max(.000001f, forwardDistances[sample + 1] - forwardDistances[sample]));
            // Shared native-length brackets and two bounded Newton refinements
            // avoid twenty arc integrations for each presentation vertex.
            for (int i = 0; i < 2; i++)
            {
                float error = DistanceAt(parameter) - distance;
                float speed = 3 * math.length(Tangent(ForwardCurve, parameter));
                if (speed <= .000001f) break;
                parameter = math.clamp(parameter - error / speed, low, high);
            }
            return parameter;
        }
        internal float OpposingParameterAt(float distanceMetres)
            => OpposingParameter(ForwardParameterAt(distanceMetres));

        internal float OpposingStationSpeed(float parameter, float3 velocity)
        {
            float3 tangent = Tangent(OpposingCurve, parameter);
            float derivative = math.length(tangent);
            if (derivative <= 0) return 0;
            float scale;
            if (SharedEdgeParameter)
                scale = math.length(Tangent(ForwardCurve, 1 - parameter)) / derivative;
            else
            {
                // Different node curves share normalized arc progress. Use the
                // slope of the same LUT that maps their native parameters.
                int sample = math.clamp((int)(parameter * ArcSamples), 0, ArcSamples - 1);
                scale = forwardArcLength / opposingArcDistances[ArcSamples]
                    * (opposingArcDistances[sample + 1] - opposingArcDistances[sample]) * ArcSamples / (3 * derivative);
            }
            return -math.dot(velocity, tangent / derivative) * scale;
        }

        internal float DistanceAt(float parameter)
            => MathUtils.Length(ForwardCurve, new Bounds1(ForwardStart, math.clamp(parameter, ForwardStart, ForwardEnd)));

        internal float OpposingParameter(float forwardParameter)
        {
            if (forwardParameter <= 0) return 1;
            if (forwardParameter >= 1) return 0;
            if (SharedEdgeParameter) return 1 - forwardParameter;
            PrepareCorrespondence();
            float fraction = MathUtils.Length(ForwardCurve, new Bounds1(0, forwardParameter)) / forwardArcLength;
            float distance = (1 - fraction) * opposingArcDistances[ArcSamples];
            int sample = 0;
            while (sample + 1 < ArcSamples && opposingArcDistances[sample + 1] < distance) sample++;
            return (sample + (distance - opposingArcDistances[sample])
                / (opposingArcDistances[sample + 1] - opposingArcDistances[sample])) / ArcSamples;
        }

        internal void PrepareCorrespondence()
        {
            if (opposingArcDistances != null) return;
            forwardArcLength = MathUtils.Length(ForwardCurve, new Bounds1(0, 1));
            opposingArcDistances = new float[ArcSamples + 1];
            for (int i = 1; i <= ArcSamples; i++)
                opposingArcDistances[i] = MathUtils.Length(OpposingCurve, new Bounds1(0, (float)i / ArcSamples));
            // Nodes have independently fitted curves. Arc progress joins their
            // native endpoints without the ill-conditioned lateral nearest-point solve.
            OpposingStationScale = SharedEdgeParameter ? EdgeStationScale() : ArcStationScale();
            OpposingMinimumStationScale = MinimumStationScale();
        }

        private float MinimumStationScale()
        {
            float total = opposingArcDistances[ArcSamples];
            if (!SharedEdgeParameter && (!math.isfinite(forwardArcLength) || forwardArcLength <= 0
                || !math.isfinite(total) || total <= 0)) return 0;
            // Cut in local coordinates so sub-metre connectors retain their derivative precision.
            var forward = new Bezier4x3(float3.zero, ForwardCurve.b - ForwardCurve.a, ForwardCurve.c - ForwardCurve.a, ForwardCurve.d - ForwardCurve.a);
            var opposing = new Bezier4x3(float3.zero, OpposingCurve.b - OpposingCurve.a, OpposingCurve.c - OpposingCurve.a, OpposingCurve.d - OpposingCurve.a);
            float scale = float.PositiveInfinity;
            for (int i = 0; i < ArcSamples; i++)
            {
                float2 interval = new float2((float)i / ArcSamples, (float)(i + 1) / ArcSamples);
                var reverse = MathUtils.Cut(opposing, interval);
                float maximum = 3 * math.max(math.length(reverse.b - reverse.a),
                    math.max(math.length(reverse.c - reverse.b), math.length(reverse.d - reverse.c)));
                float minimum = opposingArcDistances[i + 1] - opposingArcDistances[i];
                if (SharedEdgeParameter)
                {
                    var home = MathUtils.Cut(forward, new float2(1 - interval.y, 1 - interval.x));
                    float3 direction = math.normalizesafe(home.d - home.a);
                    minimum = 3 * math.min(math.dot(home.b - home.a, direction),
                        math.min(math.dot(home.c - home.b, direction), math.dot(home.d - home.c, direction)));
                }
                // Both cut derivatives carry the same interval factor. For nodes,
                // the LUT distance increment carries that factor instead.
                float localScale = minimum / maximum;
                if (minimum <= 0 || maximum <= 0 || !math.isfinite(localScale) || localScale <= 0) return 0;
                scale = math.min(scale, localScale);
            }
            if (!SharedEdgeParameter) scale *= forwardArcLength / total;
            return math.isfinite(scale) && scale > 0 ? scale : 0;
        }

        private float MinimumOpposingDerivative()
        {
            float3 direction = math.normalizesafe(OpposingCurve.d - OpposingCurve.a);
            return 3 * math.min(math.dot(OpposingCurve.b - OpposingCurve.a, direction),
                math.min(math.dot(OpposingCurve.c - OpposingCurve.b, direction), math.dot(OpposingCurve.d - OpposingCurve.c, direction)));
        }

        private float ArcStationScale()
        {
            float maximum = 0;
            for (int i = 1; i <= ArcSamples; i++)
                maximum = math.max(maximum, (opposingArcDistances[i] - opposingArcDistances[i - 1]) * ArcSamples);
            float minimum = MinimumOpposingDerivative();
            return minimum > 0 ? forwardArcLength / opposingArcDistances[ArcSamples]
                * math.max(1, maximum / minimum) : float.PositiveInfinity;
        }

        private float EdgeStationScale()
        {
            float minimum = MinimumOpposingDerivative();
            float maximum = 3 * math.max(math.length(ForwardCurve.b - ForwardCurve.a),
                math.max(math.length(ForwardCurve.c - ForwardCurve.b), math.length(ForwardCurve.d - ForwardCurve.c)));
            // Bezier derivative weights are nonnegative and sum to one.
            // This bounds opposing physical speed in forward-station units.
            return minimum > 0 ? maximum / minimum : float.PositiveInfinity;
        }

        internal float ForwardParameterForOpposing(float parameter)
        {
            if (parameter >= OpposingStart) return ForwardStart;
            if (parameter <= OpposingEnd) return ForwardEnd;
            int sample = 0;
            while (sample + 1 < ArcSamples && opposingParameters[sample + 1] > parameter) sample++;
            float low = math.lerp(ForwardStart, ForwardEnd, (float)sample / ArcSamples);
            float high = math.lerp(ForwardStart, ForwardEnd, (float)(sample + 1) / ArcSamples);
            // Invert the same captured cross-road correspondence. Projecting back
            // independently can shift stations on bends because nearest-point is asymmetric.
            for (int i = 0; i < 8; i++)
            {
                float mid = (low + high) * .5f;
                if (OpposingParameter(mid) > parameter) low = mid;
                else high = mid;
            }
            return (low + high) * .5f;
        }

        internal static float3 Heading(Bezier4x3 curve, float parameter)
            => math.normalizesafe(Tangent(curve, parameter), math.normalizesafe(curve.d - curve.a));

        private static float3 Tangent(Bezier4x3 curve, float parameter)
        {
            float inverse = 1 - parameter;
            return inverse * inverse * (curve.b - curve.a)
                + 2 * inverse * parameter * (curve.c - curve.b) + parameter * parameter * (curve.d - curve.c);
        }
    }

    // Shared road geometry uses stable stations. Forward extension never moves
    // an existing station or changes geometry held by a native traversal lease.
    internal sealed class PassingCorridor
    {
        private const int InitialPieceCapacity = 16;
        private readonly List<CorridorLane> lanes = new List<CorridorLane>(InitialPieceCapacity);
        private readonly HashSet<Entity> successorChoices = new HashSet<Entity>();
        internal IReadOnlyList<CorridorLane> Lanes => lanes;
        internal float RetainedStartMetres => lanes.Count == 0 ? 0 : lanes[0].StartMetres;
        private float minimumOpposingStationScale = float.NaN;
        internal float MinimumOpposingStationScale
        {
            get
            {
                if (lanes.Count == 0) return 0;
                if (!float.IsNaN(minimumOpposingStationScale)) return minimumOpposingStationScale;
                float minimum = float.PositiveInfinity;
                foreach (var piece in lanes) minimum = math.min(minimum, piece.OpposingMinimumStationScale);
                return minimumOpposingStationScale = math.isfinite(minimum) && minimum > 0 ? minimum : 0;
            }
        }

        // The pool resolves every leader, follower and traversal-lease pin before
        // pruning. Keep the last consumed piece for projection at the rear bound;
        // retained pieces and future appends keep their absolute stations.
        internal void PruneBefore(float oldestRequiredStation)
        {
            if (!math.isfinite(oldestRequiredStation)) return;
            int consumed = 0;
            while (consumed < lanes.Count
                && lanes[consumed].StartMetres + lanes[consumed].LengthMetres <= oldestRequiredStation)
                consumed++;
            int removeCount = consumed - 1;
            if (removeCount <= 0) return;
            lanes.RemoveRange(0, removeCount);
            minimumOpposingStationScale = float.NaN;
            OpposingSpeedMetresPerSecond = 0;
            foreach (var piece in lanes)
                OpposingSpeedMetresPerSecond = math.max(OpposingSpeedMetresPerSecond, piece.OpposingRules.m_SpeedLimit * math.max(1, piece.OpposingStationScale));
        }

        internal bool TryAbsorb(PassingCorridor other)
        {
            if (other == null || lanes.Count == 0 || other.lanes.Count == 0) return false;
            if (ReferenceEquals(this, other)) return true;
            int homeAnchor = -1, donorAnchor = -1;
            for (int i = 0; i < lanes.Count && homeAnchor < 0; i++)
                for (int j = 0; j < other.lanes.Count; j++)
                    if (lanes[i].ForwardLane == other.lanes[j].ForwardLane)
                    {
                        if (!SamePiece(lanes[i], other.lanes[j])) return false;
                        homeAnchor = i; donorAnchor = j;
                        break;
                    }
            if (homeAnchor < 0) return false;
            float offset = lanes[homeAnchor].StartMetres - other.lanes[donorAnchor].StartMetres;
            if (!math.isfinite(offset)) return false;
            int first = math.min(-homeAnchor, -donorAnchor);
            int end = math.max(lanes.Count - homeAnchor, other.lanes.Count - donorAnchor);
            var union = new List<CorridorLane>(end - first);
            var nativeLanes = new HashSet<Entity>();
            for (int index = first; index < end; index++)
            {
                int homeIndex = index + homeAnchor, donorIndex = index + donorAnchor;
                var home = homeIndex >= 0 && homeIndex < lanes.Count ? lanes[homeIndex] : null;
                var donor = donorIndex >= 0 && donorIndex < other.lanes.Count ? other.lanes[donorIndex] : null;
                if (home != null && donor != null
                    && (!SamePiece(home, donor) || !Aligned(home.StartMetres, donor.StartMetres + offset)))
                    return false;
                var piece = home ?? donor.CopyAt(donor.StartMetres + offset);
                if (!math.isfinite(piece.StartMetres) || !nativeLanes.Add(piece.ForwardLane)
                    || !nativeLanes.Add(piece.OpposingLane)) return false;
                if (union.Count > 0)
                {
                    var previous = union[union.Count - 1];
                    if (!Aligned(previous.StartMetres + previous.LengthMetres, piece.StartMetres)
                        || previous.ForwardEnd != 1 || piece.ForwardStart != 0
                        || previous.OpposingEnd != 0 || piece.OpposingStart != 1
                        || !previous.ForwardNodes.m_EndNode.Equals(piece.ForwardNodes.m_StartNode)
                        || !previous.OpposingNodes.m_StartNode.Equals(piece.OpposingNodes.m_EndNode))
                        return false;
                }
                union.Add(piece);
            }
            // All overlap and seams passed. Canonical pieces retain their identity;
            // donor prefix/suffix copies leave donor stations and leases untouched.
            lanes.Clear();
            lanes.AddRange(union);
            minimumOpposingStationScale = float.NaN;
            var last = lanes[lanes.Count - 1];
            LengthMetres = last.StartMetres + last.LengthMetres;
            OpposingSpeedMetresPerSecond = 0;
            foreach (var piece in lanes)
                OpposingSpeedMetresPerSecond = math.max(OpposingSpeedMetresPerSecond, piece.OpposingRules.m_SpeedLimit * math.max(1, piece.OpposingStationScale));
            Incomplete(CorridorTermination.CoverageUnavailable);
            return true;
        }

        // Native inputs must match exactly. Only station arithmetic permits a
        // centimetre of floating-point accumulation error across shifted origins.
        private static bool Aligned(float first, float second)
            => math.isfinite(first) && math.isfinite(second) && math.abs(first - second) <= .01f;

        private static bool SamePiece(CorridorLane first, CorridorLane second)
            => first.ForwardLane == second.ForwardLane && first.OpposingLane == second.OpposingLane
                && first.Owner == second.Owner && first.ForwardCurve.Equals(second.ForwardCurve)
                && first.OpposingCurve.Equals(second.OpposingCurve)
                && first.ForwardNodes.Equals(second.ForwardNodes) && first.OpposingNodes.Equals(second.OpposingNodes)
                && SameRules(first.ForwardRules, second.ForwardRules) && SameRules(first.OpposingRules, second.OpposingRules)
                && first.AllowsPassing == second.AllowsPassing && first.LengthMetres == second.LengthMetres
                && first.ForwardStart == second.ForwardStart && first.ForwardEnd == second.ForwardEnd
                && first.OpposingStart == second.OpposingStart && first.OpposingEnd == second.OpposingEnd;

        internal float3 Direction
        {
            get;
        }
        internal float LengthMetres
        {
            get; private set;
        }
        internal float PassingEndFrom(float station)
        {
            foreach (var lane in lanes)
            {
                if (lane.StartMetres + lane.LengthMetres <= station) continue;
                if (!lane.AllowsPassing) return math.max(station, lane.StartMetres);
            }
            return LengthMetres;
        }
        internal bool ContainsForwardLane(Entity lane)
        {
            foreach (var piece in lanes)
                if (piece.ForwardLane == lane) return true;
            return false;
        }
        internal float OpposingSpeedMetresPerSecond
        {
            get; private set;
        }
        internal CorridorTermination Termination
        {
            get; private set;
        }
        internal RoadCoverageState CoverageState { get; private set; } = RoadCoverageState.Pending;

        // requiredEndMetres is in this corridor's stable station coordinates.
        // A work yield is incomplete coverage, never a physical road boundary.
        internal RoadCoverageState RefreshAhead(EntityManager em, float requiredEndMetres, float startMetres = 0)
        {
            if (!math.isfinite(requiredEndMetres) || lanes.Count == 0)
                return Incomplete(CorridorTermination.CoverageUnavailable);
            if (PassingEndFrom(startMetres) >= requiredEndMetres)
            {
                Termination = CorridorTermination.HorizonCovered;
                return CoverageState = RoadCoverageState.Complete;
            }
            foreach (var piece in lanes)
                if (!piece.AllowsPassing && piece.StartMetres + piece.LengthMetres > startMetres)
                {
                    Termination = CorridorTermination.RestrictedLane;
                    return CoverageState = RoadCoverageState.StructuralBoundary;
                }
            const int extensionBudget = 16;
            int appended = 0;
            while (LengthMetres < requiredEndMetres)
            {
                if (appended >= extensionBudget)
                    return Incomplete(CorridorTermination.PieceBudget);
                var previous = lanes[lanes.Count - 1];
                if (previous.ForwardEnd != 1)
                    return Incomplete(CorridorTermination.CoverageUnavailable);
                if (!TrySuccessor(em, previous.ForwardLane, out Entity next, out var failure, out bool incomplete))
                {
                    Termination = failure;
                    return CoverageState = incomplete ? RoadCoverageState.Pending : RoadCoverageState.StructuralBoundary;
                }
                if (!em.HasComponent<Owner>(next) || !em.HasComponent<Curve>(next))
                    return Incomplete(CorridorTermination.CoverageUnavailable);
                Entity nextOwner = em.GetComponentData<Owner>(next).m_Owner;
                if (!Live(em, nextOwner) || !em.HasBuffer<SubLane>(nextOwner)
                    || em.GetBuffer<SubLane>(nextOwner, true).Length > 128)
                    return Incomplete(CorridorTermination.CoverageUnavailable);
                if (!Append(em, new CarNavigationLane
                {
                    m_Lane = next, m_CurvePosition = new float2(0, 1), m_Flags = NavigationFlags.FixedLane
                }))
                    return CoverageState = RoadCoverageState.StructuralBoundary;
                appended++;
                if (!lanes[lanes.Count - 1].AllowsPassing)
                {
                    Termination = CorridorTermination.RestrictedLane;
                    return CoverageState = RoadCoverageState.StructuralBoundary;
                }
            }
            Termination = CorridorTermination.HorizonCovered;
            return CoverageState = RoadCoverageState.Complete;
        }

        private RoadCoverageState Incomplete(CorridorTermination reason)
        {
            Termination = reason;
            return CoverageState = RoadCoverageState.Pending;
        }

        private bool TrySuccessor(EntityManager em, Entity lane, out Entity next,
            out CorridorTermination failure, out bool incomplete)
        {
            next = Entity.Null;
            failure = CorridorTermination.CoverageUnavailable;
            incomplete = true;
            if (!Live(em, lane) || !em.HasComponent<Lane>(lane) || !em.HasComponent<Owner>(lane)) return false;
            Entity owner = em.GetComponentData<Owner>(lane).m_Owner;
            if (!Live(em, owner)) return false;
            PathNode end = em.GetComponentData<Lane>(lane).m_EndNode;
            Entity searchOwner = owner;
            // This is the native NetUtils.FindConnectedLane neighbourhood. Its
            // first-match policy is unsuitable here: count every directed choice.
            if (em.HasComponent<Edge>(owner))
            {
                if (!em.HasComponent<EdgeLane>(lane)) return false;
                float delta = em.GetComponentData<EdgeLane>(lane).m_EdgeDelta.y;
                var edge = em.GetComponentData<Edge>(owner);
                if (delta == 0) searchOwner = edge.m_Start;
                else if (delta == 1) searchOwner = edge.m_End;
            }
            else if (!em.HasComponent<Node>(owner)) return false;
            int count = 0;
            successorChoices.Clear();
            if (!CountSuccessors(em, searchOwner, lane, end, ref next, ref count)) return false;
            bool adjacentEdges = searchOwner != owner
                ? end.OwnerEquals(new PathNode(searchOwner, (ushort)0))
                : em.HasComponent<Node>(owner);
            if (adjacentEdges)
            {
                if (!Live(em, searchOwner) || !em.HasBuffer<ConnectedEdge>(searchOwner)) return false;
                var edges = em.GetBuffer<ConnectedEdge>(searchOwner, true);
                if (edges.Length > 16) return false;
                foreach (var connected in edges)
                {
                    if (connected.m_Edge == owner) continue;
                    if (!CountSuccessors(em, connected.m_Edge, lane, end, ref next, ref count)) return false;
                }
            }
            incomplete = false;
            failure = count == 0 ? CorridorTermination.LaneEnded : CorridorTermination.AmbiguousFork;
            return count == 1;
        }

        private bool CountSuccessors(EntityManager em, Entity owner, Entity current,
            PathNode end, ref Entity next, ref int count)
        {
            if (!Live(em, owner) || !em.HasBuffer<SubLane>(owner)) return false;
            var siblings = em.GetBuffer<SubLane>(owner, true);
            if (siblings.Length > 128) return false;
            foreach (var sibling in siblings)
            {
                Entity candidate = sibling.m_SubLane;
                if (candidate == current || !Live(em, candidate)
                    || !em.HasComponent<Game.Net.CarLane>(candidate) || !em.HasComponent<Lane>(candidate)
                    || em.HasComponent<MasterLane>(candidate)) continue;
                // Native U-turn connectors share a source port with the through lane.
                // They are reversals, not branches of the logical forward road.
                if ((em.GetComponentData<Game.Net.CarLane>(candidate).m_Flags & (RoadFlags.UTurnLeft | RoadFlags.UTurnRight)) != 0)
                    continue;
                if (em.GetComponentData<Lane>(candidate).m_StartNode.Equals(end))
                {
                    if (!successorChoices.Add(candidate)) continue;
                    next = candidate;
                    count++;
                }
            }
            return true;
        }

        private PassingCorridor(Bezier4x3 first)
        {
            Direction = math.normalizesafe(first.d - first.a);
        }

        // Geometry can start at the rear queue vehicle; pool ownership remains
        // attached to the canonical leader independently of this route anchor.
        internal static bool TryCreate(EntityManager em, Entity routeAnchor, out PassingCorridor corridor)
        {
            corridor = null;
            if (!Live(em, routeAnchor) || !em.HasComponent<CarCurrentLane>(routeAnchor))
                return false;
            var current = em.GetComponentData<CarCurrentLane>(routeAnchor);
            if (current.m_ChangeLane != Entity.Null || !math.all(math.isfinite(current.m_CurvePosition))
                || current.m_CurvePosition.x >= current.m_CurvePosition.z
                || !Live(em, current.m_Lane) || !em.HasComponent<Curve>(current.m_Lane))
                return false;
            var result = new PassingCorridor(em.GetComponentData<Curve>(current.m_Lane).m_Bezier);
            // Navigation buffers describe driving reservations, not the physical
            // road horizon. Seed one complete home-lane piece, then walk the graph.
            if (!result.Append(em, new CarNavigationLane
            {
                m_Lane = current.m_Lane, m_CurvePosition = new float2(0, 1), m_Flags = NavigationFlags.FixedLane
            })) return false;
            result.Termination = CorridorTermination.CoverageUnavailable;
            corridor = result;
            return true;
        }

        internal static bool TryRestore(EntityManager em, float startMetres, CorridorLane[] saved,
            out PassingCorridor corridor)
        {
            corridor = null;
            if (!math.isfinite(startMetres) || saved.Length == 0) return false;
            var restored = new PassingCorridor(saved[0].ForwardCurve) { LengthMetres = startMetres };
            foreach (var lane in saved)
            {
                if (!Live(em, lane.ForwardLane) || !Live(em, lane.OpposingLane)
                    || !em.HasComponent<Curve>(lane.ForwardLane) || !em.HasComponent<Curve>(lane.OpposingLane)
                    || !em.GetComponentData<Curve>(lane.ForwardLane).m_Bezier.Equals(lane.ForwardCurve)
                    || !em.GetComponentData<Curve>(lane.OpposingLane).m_Bezier.Equals(lane.OpposingCurve)
                    || !restored.Append(em, new CarNavigationLane { m_Lane = lane.ForwardLane,
                        m_CurvePosition = new float2(lane.ForwardStart, lane.ForwardEnd), m_Flags = NavigationFlags.FixedLane })
                    || restored.lanes[restored.lanes.Count - 1].OpposingLane != lane.OpposingLane) return false;
            }
            corridor = restored;
            return true;
        }

        private bool Append(EntityManager em, CarNavigationLane route)
        {
            foreach (var existing in lanes)
                if (existing.ForwardLane == route.m_Lane)
                    return Stop(CorridorTermination.RouteLoop);
            const NavigationFlags allowed = NavigationFlags.FixedLane | NavigationFlags.FixedStart
                | NavigationFlags.Reserved | NavigationFlags.UpdateOptimalLane | NavigationFlags.HighBeams;
            if ((route.m_Flags & ~allowed) != 0 || !math.all(math.isfinite(route.m_CurvePosition))
                || route.m_CurvePosition.x < 0 || route.m_CurvePosition.y > 1
                || route.m_CurvePosition.y <= route.m_CurvePosition.x)
                return Stop(CorridorTermination.UnsupportedLane);
            if (!TryPair(em, route.m_Lane, out var pair, out var failure))
                return Stop(failure);
            pair.ForwardStart = route.m_CurvePosition.x;
            pair.ForwardEnd = route.m_CurvePosition.y;
            // Both intervals use the same endpoint-anchored road coordinate.
            pair.OpposingStart = pair.OpposingParameter(pair.ForwardStart);
            pair.OpposingEnd = pair.OpposingParameter(pair.ForwardEnd);
            if (pair.OpposingStart <= pair.OpposingEnd)
                return Stop(CorridorTermination.InvalidGeometry);
            if (lanes.Count > 0)
            {
                var previous = lanes[lanes.Count - 1];
                if (previous.ForwardEnd != 1 || pair.ForwardStart != 0
                    || !previous.ForwardNodes.m_EndNode.Equals(pair.ForwardNodes.m_StartNode)
                    || !previous.OpposingNodes.m_StartNode.Equals(pair.OpposingNodes.m_EndNode))
                    return Stop(CorridorTermination.Discontinuity);
            }
            pair.StartMetres = LengthMetres;
            if (!pair.PrepareGeometry())
                return Stop(CorridorTermination.InvalidGeometry);
            if (!math.isfinite(pair.LengthMetres) || pair.LengthMetres <= 0.01f)
                return Stop(CorridorTermination.InvalidGeometry);
            lanes.Add(pair);
            minimumOpposingStationScale = float.NaN;
            LengthMetres += pair.LengthMetres;
            OpposingSpeedMetresPerSecond = math.max(OpposingSpeedMetresPerSecond, pair.OpposingRules.m_SpeedLimit * math.max(1, pair.OpposingStationScale));
            return true;
        }

        internal bool IsValid(EntityManager em)
        {
            foreach (var saved in lanes)
            {
                // Cached geometry remains valid only while these captured inputs match.
                // Revalidate live eligibility and owner composition, then compare
                // inputs directly instead of allocating and recertifying a pair.
                if (!SupportedPassingRoads.QueueLane(em, saved.ForwardLane) || !SupportedPassingRoads.QueueLane(em, saved.OpposingLane)
                    || !SupportedOwner(em, saved.Owner)
                    || em.GetComponentData<Owner>(saved.ForwardLane).m_Owner != saved.Owner
                    || em.GetComponentData<Owner>(saved.OpposingLane).m_Owner != saved.Owner
                    || !em.GetComponentData<Curve>(saved.ForwardLane).m_Bezier.Equals(saved.ForwardCurve)
                    || !em.GetComponentData<Curve>(saved.OpposingLane).m_Bezier.Equals(saved.OpposingCurve)
                    || !em.GetComponentData<Lane>(saved.ForwardLane).Equals(saved.ForwardNodes)
                    || !em.GetComponentData<Lane>(saved.OpposingLane).Equals(saved.OpposingNodes)
                    || !SameRules(em.GetComponentData<Game.Net.CarLane>(saved.ForwardLane), saved.ForwardRules)
                    || !SameRules(em.GetComponentData<Game.Net.CarLane>(saved.OpposingLane), saved.OpposingRules))
                    return false;
            }
            return lanes.Count > 0;
        }

        private static bool SameRules(Game.Net.CarLane current, Game.Net.CarLane saved)
        {
            // TrafficFlowSystem updates m_FlowOffset from moving traffic without
            // changing this road's geometry or passing eligibility. Every other
            // native CarLane field remains part of the captured road contract.
            return current.m_AccessRestriction == saved.m_AccessRestriction
                && current.m_Flags == saved.m_Flags
                && current.m_DefaultSpeedLimit == saved.m_DefaultSpeedLimit
                && current.m_SpeedLimit == saved.m_SpeedLimit
                && current.m_Curviness == saved.m_Curviness
                && current.m_CarriagewayGroup == saved.m_CarriagewayGroup
                && current.m_BlockageStart == saved.m_BlockageStart
                && current.m_BlockageEnd == saved.m_BlockageEnd
                && current.m_CautionStart == saved.m_CautionStart
                && current.m_CautionEnd == saved.m_CautionEnd
                && current.m_LaneCrossCount == saved.m_LaneCrossCount;
        }

        // Signed station extends beyond route endpoints for observation; coverage
        // checks belong to admission, so a follower behind the origin is measurable.
        internal bool TryStation(float3 position, out float distanceMetres)
            => TryStation(position, out distanceMetres, out _);

        // Largest opposing-to-forward station scale over the captured road.
        internal float MaximumOpposingStationScale => OpposingScaleFor(Entity.Null, out _);

        // Conservative oncoming closing speed in forward-station units: the
        // observed threat or any unseen car at the opposing speed limit.
        internal float OpposingClosingSpeed(float observedThreatSpeedMetresPerSecond)
            => math.max(observedThreatSpeedMetresPerSecond * MaximumOpposingStationScale, OpposingSpeedMetresPerSecond);

        internal float OpposingScaleFor(Entity currentLane, out bool opposingCoordinate)
        {
            opposingCoordinate = true;
            float maximum = 1;
            foreach (var piece in lanes)
            {
                if (piece.ForwardLane == currentLane)
                {
                    opposingCoordinate = false;
                    return 1;
                }
                maximum = math.max(maximum, piece.OpposingStationScale);
            }
            return maximum;
        }

        internal float StationSpeed(Entity currentLane, float3 position, float3 velocity, float3 forwardDirection)
        {
            foreach (var piece in lanes)
                if (piece.OpposingLane == currentLane)
                {
                    MathUtils.Distance(piece.OpposingCurve, position, out float parameter);
                    return piece.OpposingStationSpeed(parameter, velocity);
                }
            return math.dot(velocity, forwardDirection);
        }

        internal bool TryStation(float3 position, out float distanceMetres, out float3 direction)
            => TryStation(Entity.Null, position, out distanceMetres, out direction);

        internal bool TryStation(Entity currentLane, float3 position, out float distanceMetres, out float3 direction)
        {
            distanceMetres = 0;
            direction = Direction;
            if (!math.all(math.isfinite(position)) || lanes.Count == 0) return false;
            if (currentLane != Entity.Null)
                for (int i = 0; i < lanes.Count; i++)
                {
                    var piece = lanes[i];
                    if (piece.ForwardLane == currentLane || piece.OpposingLane == currentLane)
                    {
                        ProjectFromHint(i, position, piece.OpposingLane == currentLane, out distanceMetres, out direction);
                        return math.isfinite(distanceMetres);
                    }
                }
            // Bezier convex hulls bound deviation from the chord. Project the
            // closest chord first, then only pieces whose bound can beat that result.
            int first = -1;
            float firstError = float.PositiveInfinity;
            for (int i = 0; i < lanes.Count; i++)
            {
                var piece = lanes[i];
                float error = math.min(ChordError(piece.ForwardInterval, position), ChordError(piece.OpposingInterval, position));
                if (error < firstError)
                {
                    first = i; firstError = error;
                }
            }
            float nearest = float.PositiveInfinity;
            for (int candidate = -1; candidate < lanes.Count; candidate++)
            {
                int index = candidate < 0 ? first : candidate;
                if (index < 0 || candidate == first) continue;
                var piece = lanes[index];
                float forwardBound = math.max(0, math.sqrt(ChordError(piece.ForwardInterval, position)) - piece.ForwardChordRadius);
                float opposingBound = math.max(0, math.sqrt(ChordError(piece.OpposingInterval, position)) - piece.OpposingChordRadius);
                if (math.min(forwardBound * forwardBound, opposingBound * opposingBound) >= nearest) continue;
                MathUtils.Distance(piece.ForwardInterval, position, out float forwardT);
                MathUtils.Distance(piece.OpposingInterval, position, out float opposingT);
                float forwardError = math.lengthsq(position - MathUtils.Position(piece.ForwardInterval, forwardT));
                float opposingError = math.lengthsq(position - MathUtils.Position(piece.OpposingInterval, opposingT));
                float error = math.min(forwardError, opposingError);
                if (error >= nearest) continue;
                nearest = error;
                StationAt(index, position, opposingError < forwardError, opposingError < forwardError ? opposingT : forwardT,
                    out distanceMetres, out direction);
            }
            return math.isfinite(nearest) && math.isfinite(distanceMetres);
        }

        private static float ChordError(Bezier4x3 curve, float3 position)
        {
            float3 delta = curve.d - curve.a;
            float t = math.saturate(math.dot(position - curve.a, delta) / math.max(.000001f, math.lengthsq(delta)));
            return math.lengthsq(position - (curve.a + delta * t));
        }

        private void ProjectFromHint(int index, float3 position, bool opposing, out float station, out float3 direction)
        {
            float nearest = MathUtils.Distance(opposing ? lanes[index].OpposingInterval : lanes[index].ForwardInterval,
                position, out float t);
            // Linked-package centres and rendered poses can trail the root's
            // native lane. Follow adjacent captured pieces toward the physical point.
            while (true)
            {
                int next = index;
                float nextT = t;
                for (int neighbour = index - 1; neighbour <= index + 1; neighbour += 2)
                {
                    if (neighbour < 0 || neighbour >= lanes.Count) continue;
                    var piece = lanes[neighbour];
                    float error = MathUtils.Distance(opposing ? piece.OpposingInterval : piece.ForwardInterval,
                        position, out float parameter);
                    if (error >= nearest) continue;
                    nearest = error;
                    next = neighbour;
                    nextT = parameter;
                }
                if (next == index) break;
                index = next;
                t = nextT;
            }
            StationAt(index, position, opposing, t, out station, out direction);
        }

        private void StationAt(int index, float3 position, bool opposing, float t, out float station, out float3 direction)
        {
            var piece = lanes[index];
            float parameter = opposing
                ? piece.ForwardParameterForOpposing(math.lerp(piece.OpposingEnd, piece.OpposingStart, t))
                : math.lerp(piece.ForwardStart, piece.ForwardEnd, t);
            direction = CorridorLane.Heading(piece.ForwardCurve, parameter);
            float along = piece.DistanceAt(parameter);
            // Signed extrapolation only outside the whole captured prefix.
            float offset = math.dot(position - MathUtils.Position(piece.ForwardCurve, parameter), direction);
            if (index == 0 && parameter <= piece.ForwardStart + .00001f && offset < 0
                || index == lanes.Count - 1 && parameter >= piece.ForwardEnd - .00001f && offset > 0) along += offset;
            station = piece.StartMetres + along;
        }

        internal float3 PositionAt(float distanceMetres)
        {
            float station = math.clamp(distanceMetres, RetainedStartMetres, LengthMetres);
            foreach (var lane in lanes)
                if (station <= lane.StartMetres + lane.LengthMetres)
                    return MathUtils.Position(lane.ForwardCurve, lane.ForwardParameterAt(station));
            return lanes[lanes.Count - 1].ForwardCurve.d;
        }

        private bool Stop(CorridorTermination failure)
        {
            Termination = failure;
            return false;
        }

        internal static bool TryPair(EntityManager em, Entity lane, out CorridorLane pair, out CorridorTermination failure)
        {
            pair = null;
            failure = CorridorTermination.UnsupportedLane;
            if (!SupportedPassingRoads.QueueLane(em, lane))
                return false;
            Entity owner = em.GetComponentData<Owner>(lane).m_Owner;
            failure = CorridorTermination.UnsupportedOwner;
            if (!SupportedOwner(em, owner))
            {
                failure = OwnerFailure(em, owner);
                return false;
            }
            var curve = em.GetComponentData<Curve>(lane).m_Bezier;
            failure = CorridorTermination.InvalidGeometry;
            if (!FiniteGeometry(curve))
                return false;
            var siblings = em.GetBuffer<SubLane>(owner, true);
            if (siblings.Length > 128)
                return false;
            Entity opposing = Entity.Null;
            int opposingCount = 0;
            var forwardNodes = em.GetComponentData<Lane>(lane);
            var forwardRules = em.GetComponentData<Game.Net.CarLane>(lane);
            bool edgeLane = em.HasComponent<EdgeLane>(lane);
            float2 edgeDelta = edgeLane ? em.GetComponentData<EdgeLane>(lane).m_EdgeDelta : default;
            for (int i = 0; i < siblings.Length; i++)
            {
                Entity candidate = siblings[i].m_SubLane;
                if (candidate == lane || !SupportedPassingRoads.QueueLane(em, candidate))
                    continue;
                var otherNodes = em.GetComponentData<Lane>(candidate);
                var otherRules = em.GetComponentData<Game.Net.CarLane>(candidate);
                if (forwardRules.m_CarriagewayGroup != otherRules.m_CarriagewayGroup) continue;
                if (edgeLane)
                {
                    if (!em.HasComponent<EdgeLane>(candidate)
                        || !math.all(em.GetComponentData<EdgeLane>(candidate).m_EdgeDelta == edgeDelta.yx)
                        || ((forwardRules.m_Flags ^ otherRules.m_Flags) & RoadFlags.Invert) == 0) continue;
                }
                else if (!forwardNodes.m_StartNode.OwnerEquals(otherNodes.m_EndNode)
                    || !forwardNodes.m_EndNode.OwnerEquals(otherNodes.m_StartNode)) continue;
                opposingCount++;
                opposing = candidate;
            }
            failure = CorridorTermination.UnpairedLane;
            if (opposingCount != 1)
                return false;
            var reverseCurve = em.GetComponentData<Curve>(opposing).m_Bezier;
            if (!FiniteGeometry(reverseCurve) || em.GetComponentData<Owner>(opposing).m_Owner != owner)
                return false;
            pair = new CorridorLane
            {
                ForwardLane = lane,
                OpposingLane = opposing,
                Owner = owner,
                ForwardCurve = curve,
                OpposingCurve = reverseCurve,
                SharedEdgeParameter = edgeLane,
                ForwardNodes = forwardNodes,
                OpposingNodes = em.GetComponentData<Lane>(opposing),
                ForwardRules = forwardRules,
                OpposingRules = em.GetComponentData<Game.Net.CarLane>(opposing)
            };
            pair.AllowsPassing = UsableLane(em, lane, true) && UsableLane(em, opposing, false);
            // Reversed EdgeDelta uses the same native road parameter. Node
            // connectors use normalized arc progress between their paired ports.
            pair.PrepareCorrespondence();
            float previousOpposing = 1.001f;
            for (int i = 0; i <= 8; i++)
            {
                float t = i / 8f;
                float reverseT = pair.OpposingParameter(t);
                float3 forward = MathUtils.Position(curve, t);
                float3 backward = MathUtils.Position(reverseCurve, reverseT);
                if (!math.isfinite(reverseT) || reverseT >= previousOpposing
                    || !math.all(math.isfinite(forward)) || !math.all(math.isfinite(backward))
                    || math.lengthsq(forward - backward) <= .000001f
                    || math.dot(CorridorLane.Heading(curve, t), -CorridorLane.Heading(reverseCurve, reverseT)) <= 0)
                {
                    pair = null;
                    return false;
                }
                previousOpposing = reverseT;
            }
            return true;
        }

        internal static bool UsableLane(EntityManager em, Entity lane, bool passing)
        {
            if (!Live(em, lane) || !em.HasComponent<Curve>(lane) || !em.HasComponent<Lane>(lane)
                || !em.HasComponent<Owner>(lane) || !em.HasComponent<Game.Net.CarLane>(lane)
                || em.HasComponent<MasterLane>(lane) || em.HasComponent<SlaveLane>(lane))
                return false;
            var car = em.GetComponentData<Game.Net.CarLane>(lane);
            const RoadFlags excluded = RoadFlags.Forbidden | RoadFlags.Twoway | RoadFlags.Unsafe | RoadFlags.Roundabout
                | RoadFlags.LevelCrossing | RoadFlags.ParkingLeft | RoadFlags.ParkingRight | RoadFlags.PublicOnly
                | RoadFlags.Runway | RoadFlags.Approach | RoadFlags.SideConnection | RoadFlags.Stop | RoadFlags.Yield | RoadFlags.TrafficLights;
            return (car.m_Flags & excluded) == 0 && (!passing || (car.m_Flags & RoadFlags.ForbidPassing) == 0)
                && car.m_BlockageEnd < car.m_BlockageStart && math.isfinite(car.m_SpeedLimit) && car.m_SpeedLimit > 0;
        }

        internal static bool SupportedOwner(EntityManager em, Entity owner)
        {
            if (!Live(em, owner) || !em.HasBuffer<SubLane>(owner) || !UsableOwner(em, owner))
                return false;
            var siblings = em.GetBuffer<SubLane>(owner, true);
            if (siblings.Length > 128)
                return false;
            int count = 0;
            uint firstIndex = 0, secondIndex = 0;
            bool edgeOwner = em.HasComponent<Edge>(owner);
            foreach (var sibling in siblings)
            {
                Entity lane = sibling.m_SubLane;
                if (!Live(em, lane) || !em.HasComponent<Game.Net.CarLane>(lane)
                    || em.HasComponent<MasterLane>(lane))
                    continue;
                if ((em.GetComponentData<Game.Net.CarLane>(lane).m_Flags & (RoadFlags.UTurnLeft | RoadFlags.UTurnRight)) != 0)
                    continue;
                // An edge can split one composition lane into two serial entities.
                // Count physical lanes, not longitudinal pieces; extra lane indices still reject.
                if (edgeOwner)
                {
                    if (!em.HasComponent<Lane>(lane)) return false;
                    uint index = (uint)em.GetComponentData<Lane>(lane).m_MiddleNode.GetLaneIndex() & 0xff;
                    if (count > 0 && index == firstIndex || count > 1 && index == secondIndex) continue;
                    if (count == 0) firstIndex = index;
                    else if (count == 1) secondIndex = index;
                }
                if (++count > 2)
                    return false;
            }
            return count == 2;
        }

        private static bool UsableOwner(EntityManager em, Entity owner)
        {
            if (em.HasComponent<Edge>(owner))
                return em.HasComponent<Composition>(owner) && SafeComposition(em, em.GetComponentData<Composition>(owner).m_Edge);
            if (!em.HasComponent<Node>(owner) || !em.HasBuffer<ConnectedEdge>(owner))
                return false;
            var edges = em.GetBuffer<ConnectedEdge>(owner, true);
            if (edges.Length != 2 || edges[0].m_Edge == edges[1].m_Edge)
                return false;
            for (int i = 0; i < 2; i++)
            {
                Entity edge = edges[i].m_Edge;
                if (!Live(em, edge) || !em.HasComponent<Edge>(edge) || !em.HasComponent<Composition>(edge))
                    return false;
                var input = em.GetComponentData<Edge>(edge);
                if (input.m_Start != owner && input.m_End != owner)
                    return false;
                var composition = em.GetComponentData<Composition>(edge);
                if (!SafeComposition(em, input.m_Start == owner ? composition.m_StartNode : composition.m_EndNode))
                    return false;
            }
            return true;
        }

        private static bool SafeComposition(EntityManager em, Entity entity)
        {
            if (!Live(em, entity) || !em.HasComponent<NetCompositionData>(entity))
                return false;
            const CompositionFlags.General excluded = CompositionFlags.General.Intersection | CompositionFlags.General.Roundabout
                | CompositionFlags.General.LevelCrossing | CompositionFlags.General.TrafficLights | CompositionFlags.General.Crosswalk
                | CompositionFlags.General.AllWayStop | CompositionFlags.General.DeadEnd;
            var composition = em.GetComponentData<NetCompositionData>(entity);
            return (composition.m_Flags.m_General & excluded) == 0
                && (composition.m_State & (CompositionState.SeparatedCarriageways | CompositionState.Multilane)) == 0;
        }

        private static CorridorTermination OwnerFailure(EntityManager em, Entity owner)
        {
            if (!Live(em, owner)) return CorridorTermination.CoverageUnavailable;
            if (em.HasComponent<Node>(owner)) return CorridorTermination.Junction;
            if (em.HasComponent<Composition>(owner))
            {
                Entity composition = em.GetComponentData<Composition>(owner).m_Edge;
                if (Live(em, composition) && em.HasComponent<NetCompositionData>(composition))
                {
                    var state = em.GetComponentData<NetCompositionData>(composition).m_State;
                    if ((state & CompositionState.SeparatedCarriageways) != 0) return CorridorTermination.DividedCarriageway;
                    if ((state & CompositionState.Multilane) != 0) return CorridorTermination.LaneCountChanged;
                }
            }
            return CorridorTermination.UnsupportedOwner;
        }

        private static bool FiniteGeometry(Bezier4x3 curve)
            => math.all(math.isfinite(curve.a)) && math.all(math.isfinite(curve.b))
                && math.all(math.isfinite(curve.c)) && math.all(math.isfinite(curve.d))
                && math.lengthsq(curve.d - curve.a) > .000001f;

        internal static bool Live(EntityManager em, Entity entity)
            => entity != Entity.Null && em.Exists(entity) && !em.HasComponent<Deleted>(entity) && !em.HasComponent<Game.Tools.Temp>(entity);
    }
}
