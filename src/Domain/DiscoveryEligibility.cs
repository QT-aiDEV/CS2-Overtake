using System;

namespace Overtake
{
    internal static class DiscoveryEligibility
    {
        // Speed-matched traffic below free drive is ambiguous without a blocker.
        // Preserve exact fallback for that case rather than assuming acceleration.
        internal static bool NeedsOccupancy(float followerSpeedMetresPerSecond, float followerFreeSpeedMetresPerSecond,
            float aheadSpeedMetresPerSecond, float aheadFreeSpeedMetresPerSecond)
            => FiniteNonnegative(followerSpeedMetresPerSecond) && FiniteNonnegative(followerFreeSpeedMetresPerSecond)
                && FiniteNonnegative(aheadSpeedMetresPerSecond) && FiniteNonnegative(aheadFreeSpeedMetresPerSecond)
                // Even an opening gap can be inside the exact fallback's
                // two-metre floor. Only free-flow speed proves irrelevance here.
                && aheadSpeedMetresPerSecond < followerFreeSpeedMetresPerSecond;

        // Fraction of its own free-drive speed at which a car is treated as
        // unobstructed. Below it the car may be following something slower.
        internal const float FreeFlowFraction = .92f;

        // Cheap rejection before any lane occupancy is read: a car without a
        // native continuing blocker (checked by the caller) that is already at
        // its free-drive speed cannot be queued behind a slow leader worth passing.
        internal static bool IsFreeFlowing(float speedMetresPerSecond, float freeSpeedMetresPerSecond)
            => FiniteNonnegative(speedMetresPerSecond)
                && FiniteNonnegative(freeSpeedMetresPerSecond) && freeSpeedMetresPerSecond > 0
                && speedMetresPerSecond >= freeSpeedMetresPerSecond * FreeFlowFraction;

        private static bool FiniteNonnegative(float value)
            => value >= 0 && !float.IsInfinity(value);
    }
}
