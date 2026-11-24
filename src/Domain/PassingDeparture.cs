using System;

namespace Overtake
{
    internal static class PassingDeparture
    {
        internal const float CrawlSpeed = 3;
        private const float CompactApproachSpeed = 8;

        // A legal home cut follows every issued target and reservation. At
        // crawl speed, require the entire shorter crossing to fit before the
        // live blocker instead of expecting the queue to pull away.
        internal static bool TryPlan(float nativeFrontier, float front, float speed, float length,
            float leaderSpeed, float nextHomeRear, out float departure, out float opposingTarget)
        {
            bool slowQueue = leaderSpeed <= CrawlSpeed;
            bool crawling = speed <= CrawlSpeed || (slowQueue && speed <= CompactApproachSpeed);
            departure = Math.Max(nativeFrontier, front) + (crawling ? 1 : 8);
            float transition = crawling ? Math.Max(10, length + 5) : 30;
            opposingTarget = departure + Math.Max(transition, speed * PassingGate.DepartureSeconds);
            return !float.IsInfinity(opposingTarget) && !float.IsNaN(opposingTarget)
                && (!(crawling || slowQueue) || opposingTarget + .9f < nextHomeRear);
        }
    }
}
