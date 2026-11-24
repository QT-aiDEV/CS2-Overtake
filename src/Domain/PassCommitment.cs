using System;

namespace Overtake
{
    // A pass keeps checking that it still fits after it starts. A driver who
    // misjudges one either finishes it promptly or eases off and tucks back in
    // behind the slow vehicle; nobody stays nose to nose with oncoming traffic.
    internal static class PassCommitment
    {
        // Entry demands this much spare time. An ongoing pass gives up only
        // when the margin is gone, so a fresh commitment never flips straight back.
        internal const float EntryMarginSeconds = 1;
        // A car dropping back drives this much slower than the leader.
        internal const float DropBackMetresPerSecond = 4;
        // Room left behind the leader before the car tucks back in.
        internal const float DropBackGapMetres = 10;

        // Seconds until the passer is clear of the leader and back home:
        // accelerate from speed toward cruise, then hold cruise.
        internal static float FinishSeconds(float clearMetres, float speed, float cruise, float acceleration, float leaderSpeed)
        {
            if (!Finite(clearMetres) || !Finite(speed) || !Finite(cruise) || !Finite(acceleration) || !Finite(leaderSpeed))
                return float.PositiveInfinity;
            if (clearMetres <= 0) return PassingGate.ReturnSeconds;
            // A car with no acceleration left stays at its current speed.
            float top = acceleration > 0 ? Math.Max(speed, cruise) : speed;
            float gain = speed - leaderSpeed;
            float rampSeconds = acceleration > 0 ? (top - speed) / acceleration : 0;
            float rampMetres = gain * rampSeconds + .5f * acceleration * rampSeconds * rampSeconds;
            float seconds;
            if (rampSeconds > 0 && rampMetres >= clearMetres)
                seconds = (-gain + (float)Math.Sqrt(gain * gain + 2 * acceleration * clearMetres)) / acceleration;
            else
            {
                float cruiseGain = top - leaderSpeed;
                if (cruiseGain <= 0) return float.PositiveInfinity;
                seconds = rampSeconds + (clearMetres - rampMetres) / cruiseGain;
            }
            return Finite(seconds) && seconds >= 0 ? seconds + PassingGate.ReturnSeconds : float.PositiveInfinity;
        }

        // Seconds until an oncoming vehicle reaches the passer's front.
        internal static float ConflictSeconds(float gapMetres, float closingSpeed)
        {
            if (float.IsNaN(gapMetres) || float.IsNaN(closingSpeed)) return 0;
            if (gapMetres <= 0) return 0;
            return closingSpeed > 0 ? gapMetres / closingSpeed : float.PositiveInfinity;
        }

        internal static bool Fits(float finishSeconds, float conflictSeconds, float marginSeconds)
            => !float.IsNaN(finishSeconds) && !float.IsNaN(conflictSeconds)
                && finishSeconds + marginSeconds < conflictSeconds;

        // Easing off helps only behind a moving leader, and only while the
        // passer is closer to tucking in behind than to clearing it.
        internal static bool ShouldDropBack(float toFinishMetres, float toDropBackMetres, float leaderSpeed)
            => Finite(toFinishMetres) && Finite(toDropBackMetres) && Finite(leaderSpeed)
                && leaderSpeed > PassingDeparture.CrawlSpeed && toDropBackMetres < toFinishMetres;

        internal static float DropBackSpeed(float leaderSpeed)
            => Finite(leaderSpeed) ? Math.Max(0, leaderSpeed - DropBackMetresPerSecond) : 0;

        // Where a leader's exit ends, across the road: 0 is the home lane's
        // centre and 1 the opposing lane's. Past the middle, its turn crosses
        // the lane a passer would be using.
        internal static bool CrossesOpposing(float lateralReach) => Finite(lateralReach) && lateralReach > .5f;

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
