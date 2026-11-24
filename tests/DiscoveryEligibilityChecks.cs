using System;
using Overtake;

internal static class DiscoveryEligibilityChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(!DiscoveryEligibility.NeedsOccupancy(30, 30, 30, 30), "free-flowing equal-speed traffic requires no occupancy fit");
        check(DiscoveryEligibility.NeedsOccupancy(10, 30, 10, 30), "speed-matched equal-capability traffic below free drive retains missing-blocker discovery");
        check(DiscoveryEligibility.NeedsOccupancy(0, 30, 0, 30), "stopped equal-capability traffic cannot be dismissed as free acceleration");
        check(DiscoveryEligibility.NeedsOccupancy(10, 30, 10, 10), "a speed-matched follower can discover a slower-capability vehicle without a native blocker");
        check(DiscoveryEligibility.NeedsOccupancy(20, 30, 10, 30), "a closing follower retains missing-blocker discovery");
        check(DiscoveryEligibility.NeedsOccupancy(10, 30, 20, 30), "a slightly faster car can obstruct within the exact two-metre gap");
        check(!DiscoveryEligibility.NeedsOccupancy(20, 30, 35, 40), "a faster-than-free-drive vehicle does not obstruct the follower");
        check(DiscoveryEligibility.NeedsOccupancy(0, 30, 0, 10), "stopped slower-capability obstruction remains discoverable");
        check(!DiscoveryEligibility.NeedsOccupancy(float.NaN, 30, 10, 20)
            && !DiscoveryEligibility.NeedsOccupancy(20, float.PositiveInfinity, 10, 20)
            && !DiscoveryEligibility.NeedsOccupancy(20, 30, -1, 20), "invalid or reversed motion cannot seed occupancy fallback");
        check(DiscoveryEligibility.IsFreeFlowing(30, 30) && DiscoveryEligibility.IsFreeFlowing(28, 30),
            "a car at or near its own free-drive speed skips occupancy discovery");
        check(!DiscoveryEligibility.IsFreeFlowing(20, 30) && !DiscoveryEligibility.IsFreeFlowing(0, 30),
            "a car held below its free-drive speed remains a discovery candidate");
        check(!DiscoveryEligibility.IsFreeFlowing(float.NaN, 30) && !DiscoveryEligibility.IsFreeFlowing(30, 0),
            "invalid motion is never dismissed as free flow");
        foreach (int count in new[] { 1, 2, 5, 10, 20 })
        {
            int fits = 0;
            for (int i = 0; i < count; i++) if (DiscoveryEligibility.NeedsOccupancy(30, 30, 30, 30)) fits++;
            check(fits == 0, "adding " + count + " nonparticipating vehicles adds no occupancy fits");
        }
    }
}
