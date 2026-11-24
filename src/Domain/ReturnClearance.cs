using System;

namespace Overtake
{
    internal static class ReturnClearance
    {
        internal static float MinimumGapDuringMerge(float currentGapMetres, float relativeSpeedMetresPerSecond,
            float startSeconds, float endSeconds)
            => Math.Min(currentGapMetres + relativeSpeedMetresPerSecond * startSeconds,
                currentGapMetres + relativeSpeedMetresPerSecond * endSeconds);

        internal static float SelectLanding(float boundaryMetres, float stationSpeedMetresPerSecond)
            => boundaryMetres + Math.Max(20, Math.Max(0, stationSpeedMetresPerSecond) * .8f);

        internal static bool TryMergeDuration(float landingMetres, float packageRearMetres,
            float progressMetresPerSecond, out float seconds)
        {
            seconds = (landingMetres - packageRearMetres) / progressMetresPerSecond;
            return progressMetresPerSecond > 0 && seconds > 0
                && !float.IsNaN(seconds) && !float.IsInfinity(seconds);
        }

        internal static bool IsWithinLane(float lateralCentreMetres, float bodyHalfWidthMetres, float laneHalfWidthMetres)
            => !float.IsNaN(lateralCentreMetres) && !float.IsInfinity(lateralCentreMetres)
                && bodyHalfWidthMetres > 0 && !float.IsInfinity(bodyHalfWidthMetres)
                && laneHalfWidthMetres > 0 && !float.IsInfinity(laneHalfWidthMetres)
                && Math.Abs(lateralCentreMetres) + bodyHalfWidthMetres < laneHalfWidthMetres;

        internal static bool HasClearedLeader(float packageRearMetres, float leaderFrontMetres)
            => !float.IsNaN(packageRearMetres) && !float.IsInfinity(packageRearMetres)
                && !float.IsNaN(leaderFrontMetres) && !float.IsInfinity(leaderFrontMetres)
                && packageRearMetres > leaderFrontMetres;
    }
}
