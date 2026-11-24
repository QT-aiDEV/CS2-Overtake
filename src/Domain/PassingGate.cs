using System;
namespace Overtake
{
    internal enum RoadCoverageState
    {
        Complete, Pending, StructuralBoundary
    }
    internal enum GateState
    {
        Red, Yellow, Green, Purple
    }
    internal enum GateReason
    {
        None, IncompleteTraffic, RoadEnd, Oncoming, RestrictedRoad, IncompleteCoverage
    }
    internal readonly struct GateDecision
    {
        internal readonly GateState State;
        internal readonly GateReason Reason;
        internal readonly float YellowDistanceMetres, RedDistanceMetres, AvailableDistanceMetres;
        internal readonly float EffectiveYellowDistanceMetres, EffectiveRedDistanceMetres;
        internal readonly float PassingTravelMetres;
        internal GateDecision(GateState state, GateReason reason, float yellow, float red, float available,
            float effectiveYellow = 0, float effectiveRed = 0, float passingTravel = 0)
        {
            State = state;
            Reason = reason;
            YellowDistanceMetres = yellow;
            RedDistanceMetres = red;
            AvailableDistanceMetres = available;
            EffectiveYellowDistanceMetres = effectiveYellow;
            EffectiveRedDistanceMetres = effectiveRed;
            PassingTravelMetres = passingTravel;
        }
    }
    internal static class PassingGate
    {
        // Closed until a fresh shared observation has been evaluated.
        internal static readonly GateDecision Closed = new GateDecision(GateState.Red, GateReason.IncompleteCoverage, 0, 0, 0);
        internal static bool CanGainOnLeader(float passingSpeedMetresPerSecond, float leaderSpeedMetresPerSecond)
            => Finite(passingSpeedMetresPerSecond) && Finite(leaderSpeedMetresPerSecond)
                && leaderSpeedMetresPerSecond >= 0 && passingSpeedMetresPerSecond > leaderSpeedMetresPerSecond;
        internal const float ReturnClearanceMetres = 15;
        internal const float DepartureSeconds = 1.2f, ReturnSeconds = 1.3f, HysteresisMetres = 10;
        internal static bool AllowsCommitment(GateState state) => state == GateState.Green;
        internal static bool IsOpposingThreat(float signedSpeedMetresPerSecond, bool knownPasser)
            => !knownPasser || !Finite(signedSpeedMetresPerSecond) || signedSpeedMetresPerSecond <= .05f;
        // Opportunity only: no follower capability, forecast or reservation can repaint this decision.
        // Individual feasibility must still cover its complete pass before a native lease is installed.
        internal static GateDecision EvaluateShared(float verifiedDistanceMetres, float oncomingDistanceMetres,
            float oncomingSpeedMetresPerSecond, float leaderSpeedMetresPerSecond, bool complete,
            GateState previous, RoadCoverageState coverage = RoadCoverageState.Complete)
        {
            if (!Finite(verifiedDistanceMetres) || !Finite(oncomingSpeedMetresPerSecond)
                || !Finite(leaderSpeedMetresPerSecond) || float.IsNaN(oncomingDistanceMetres)
                || leaderSpeedMetresPerSecond < 0 || oncomingSpeedMetresPerSecond < 0 || !complete)
                return new GateDecision(GateState.Red, GateReason.IncompleteTraffic, 0, 0, 0);
            float available = Math.Max(0, Math.Min(verifiedDistanceMetres, oncomingDistanceMetres));
            float closing = leaderSpeedMetresPerSecond + oncomingSpeedMetresPerSecond;
            float red = ReturnClearanceMetres + closing * DepartureSeconds;
            float yellow = ReturnClearanceMetres + closing * (DepartureSeconds + ReturnSeconds);
            float effectiveYellow = yellow + (previous == GateState.Green ? 0 : HysteresisMetres);
            float effectiveRed = red + (previous == GateState.Red ? HysteresisMetres : 0);
            GateState state = available > effectiveYellow ? GateState.Green
                : available > effectiveRed ? GateState.Yellow : GateState.Red;
            GateReason reason = state == GateState.Green ? GateReason.None : GateReason.Oncoming;
            if (verifiedDistanceMetres <= oncomingDistanceMetres && state != GateState.Green)
            {
                state = coverage == RoadCoverageState.StructuralBoundary ? GateState.Purple : GateState.Red;
                reason = coverage == RoadCoverageState.StructuralBoundary ? GateReason.RoadEnd : GateReason.IncompleteCoverage;
            }
            return new GateDecision(state, reason, yellow, red, available, effectiveYellow, effectiveRed,
                leaderSpeedMetresPerSecond * (DepartureSeconds + ReturnSeconds) + ReturnClearanceMetres);
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
