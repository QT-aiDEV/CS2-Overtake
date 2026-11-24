using System;

namespace Overtake
{
    // Real drivers overtake decisively: they open a clear speed gap and briefly
    // exceed the posted limit rather than idling alongside the slow vehicle.
    internal static class OvertakeSpeed
    {
        // Multiplies the posted limit only. Native curve and wear limits still
        // apply, and Ceiling bounds the result by what native braking can undo.
        internal const float LimitFactor = 1.15f;
        // A pass must open at least this station-speed gap over the leader.
        internal const float MinimumAdvantageMetresPerSecond = 2.5f;
        internal const float MinimumAdvantageFraction = .1f;

        internal static bool WorthPassing(float passingSpeedMetresPerSecond, float leaderSpeedMetresPerSecond)
            => PassingGate.CanGainOnLeader(passingSpeedMetresPerSecond, leaderSpeedMetresPerSecond)
                && passingSpeedMetresPerSecond - leaderSpeedMetresPerSecond
                    >= Math.Max(MinimumAdvantageMetresPerSecond, leaderSpeedMetresPerSecond * MinimumAdvantageFraction);

        private const float CommandTolerance = .01f, CeilingMargin = .05f;

        // Native braking lowers speed by at most braking * step per native
        // update, so its command is max(speed - braking * step, constraint).
        // Keeping the overtake speed below laneDrive + braking * step means a
        // car slowing toward the plain lane speed always reaches it in one
        // step. Any native command below the lane speed is then unambiguously
        // a real braking request (curve, lower limit, buffer end) and is kept.
        internal static float Ceiling(float laneDrive, float boostedDrive, float braking, float stepSeconds)
            => Math.Max(laneDrive, Math.Min(boostedDrive, laneDrive + braking * stepSeconds - CeilingMargin));

        // nativeCommand is CarNavigation.m_MaxSpeed after native navigation: the
        // next-step speed, already acceleration- and braking-limited. Raise it
        // only when native held the car at the plain lane speed with nothing
        // ahead limiting it.
        internal static float Command(float nativeCommand, float reachable, float laneDrive, float ceiling, bool blocked)
        {
            if (blocked || !Finite(nativeCommand) || !Finite(reachable) || !Finite(laneDrive) || !Finite(ceiling)
                || nativeCommand < 0 || reachable <= 0 || laneDrive <= 0 || ceiling < laneDrive)
                return nativeCommand;
            if (nativeCommand < Math.Min(reachable, laneDrive) - CommandTolerance) return nativeCommand;
            return Math.Max(nativeCommand, Math.Min(reachable, ceiling));
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
