using System;

namespace Overtake
{
    internal static class TerminalRecovery
    {
        private const float Step = NativeTiming.StepSeconds;
        internal const float GuardMetres = 36;
        internal const float GuardIntervalMetres = 2;
        // Upper speed for a held car's slow approach and restart from rest.
        internal const float RestartSpeedCap = 12;
        internal const float RestSpeed = .1f;
        internal const float StartBrakingBeforeMetres = 45;

        internal static bool ShouldSlow(float remainingMetres, float speed, float braking)
        {
            if (!Finite(remainingMetres) || !Finite(speed) || !Finite(braking) || speed < 0 || braking <= 0)
                return false;
            float stoppingDistance = speed * speed / (2 * braking);
            return remainingMetres >= stoppingDistance
                && remainingMetres <= StartBrakingBeforeMetres + speed * Step + stoppingDistance;
        }

        // A lease's navigation buffer is replenished below this many entries.
        internal const int ReplenishBelow = 24;

        internal static bool CanRetryBehind(int remainingEntries, bool stoppedInHold)
            => remainingEntries < ReplenishBelow || stoppedInHold;

        // A speed cap for the current native movement step. The stop line sits
        // ahead of the body and before the protected refill tail.
        internal static float TargetSpeed(float remainingMetres, float braking)
        {
            float reaction = braking * Step;
            float stopSpeed = (float)Math.Sqrt(reaction * reaction + 2 * braking * Math.Max(0, remainingMetres)) - reaction;
            return Math.Min(RestartSpeedCap, Math.Max(0, stopSpeed));
        }

        // Time to travel the merge envelope from rest at a conservative
        // native acceleration floor and a low reference speed.
        internal static bool TryRestart(float distance, float accelerationFloor, float speedBound,
            out float seconds)
        {
            seconds = 0;
            if (!Finite(distance) || !Finite(accelerationFloor) || !Finite(speedBound)
                || distance <= 0 || accelerationFloor <= 0 || speedBound <= 0) return false;
            float rampDistance = speedBound * speedBound / (2 * accelerationFloor);
            seconds = (distance <= rampDistance ? (float)Math.Sqrt(2 * distance / accelerationFloor)
                : speedBound / accelerationFloor + (distance - rampDistance) / speedBound) + Step * 2;
            return Finite(seconds);
        }

        // The front can reach its entire landing envelope. Do not count any
        // ego movement to protect it from a closing rear vehicle.
        internal static void RestartGaps(float frontGap, float aheadRate, float rearGap, float behindRate,
            float travel, float seconds, out float ahead, out float behind)
        {
            ahead = Math.Min(frontGap, frontGap + Math.Min(0, aheadRate) * seconds - travel);
            behind = Math.Min(rearGap, rearGap - Math.Max(0, behindRate) * seconds);
        }

        internal static float ApplyCap(float nativeCommand, float measuredSpeed, float braking, float desired)
        {
            if (!Finite(nativeCommand) || !Finite(measuredSpeed) || !Finite(braking) || !Finite(desired)
                || measuredSpeed < 0 || braking <= 0 || desired < 0)
                return nativeCommand;
            float reachable = Math.Max(0, measuredSpeed - braking * Step);
            float magnitude = Math.Min(Math.Abs(nativeCommand), Math.Max(desired, reachable));
            return nativeCommand < 0 ? -magnitude : magnitude;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
