using System;

namespace Overtake
{
    internal readonly struct PassingMotion
    {
        internal readonly float CruiseSpeedMetresPerSecond, CompletionSeconds, ClearSeconds, TransitionMetres;
        internal float ApproachSeconds => departureSeconds;
        private readonly float startingSpeed, accelerationFloor, departureSeconds;
        private PassingMotion(float cruiseSpeed, float completionSeconds, float clearSeconds,
            float transitionMetres, float start, float acceleration, float departure)
        {
            CruiseSpeedMetresPerSecond = cruiseSpeed;
            CompletionSeconds = completionSeconds;
            ClearSeconds = clearSeconds;
            TransitionMetres = transitionMetres;
            startingSpeed = start;
            accelerationFloor = acceleration;
            departureSeconds = departure;
        }
        internal float ArrivalSeconds(float distanceMetres)
        {
            if (!Finite(distanceMetres) || distanceMetres < 0 || CruiseSpeedMetresPerSecond <= 0) return float.PositiveInfinity;
            if (distanceMetres == 0) return 0;
            float departureDistance = startingSpeed * departureSeconds;
            if (distanceMetres <= departureDistance && startingSpeed > 0) return distanceMetres / startingSpeed;
            float distance = distanceMetres - departureDistance;
            float ramp = accelerationFloor > 0 ? (CruiseSpeedMetresPerSecond - startingSpeed) / accelerationFloor : 0;
            float rampDistance = startingSpeed * ramp + .5f * accelerationFloor * ramp * ramp;
            float seconds = distance <= rampDistance && accelerationFloor > 0
                ? 2 * distance / (startingSpeed + (float)Math.Sqrt(startingSpeed * startingSpeed + 2 * accelerationFloor * distance))
                : ramp + (distance - rampDistance) / CruiseSpeedMetresPerSecond;
            return departureSeconds + seconds;
        }
        // At leader clearance the passer is adjacent to that leader, not at maximum
        // speed for the entire conservative catch-up duration. Bound return travel separately.
        internal float RequiredObservationMetres(float leaderSpeed, float oncomingSpeedBound,
            float packageLength, float returnSpeedBound)
            => PassingGate.ReturnClearanceMetres + Math.Max(0, packageLength)
                + Math.Max(0, leaderSpeed) * ClearSeconds
                + Math.Max(0, oncomingSpeedBound) * CompletionSeconds
                + Math.Max(0, returnSpeedBound) * (CompletionSeconds - ClearSeconds);

        internal float DistanceAt(float seconds)
        {
            if (!Finite(seconds) || seconds < 0 || CruiseSpeedMetresPerSecond <= 0) return float.NaN;
            float departure = Math.Min(seconds, departureSeconds);
            float moving = Math.Max(0, seconds - departure);
            float ramp = accelerationFloor > 0 ? (CruiseSpeedMetresPerSecond - startingSpeed) / accelerationFloor : 0;
            float accelerating = Math.Min(moving, ramp);
            return startingSpeed * (departure + accelerating) + .5f * accelerationFloor * accelerating * accelerating
                + CruiseSpeedMetresPerSecond * (moving - accelerating);
        }

        internal float SpeedAt(float seconds)
            => Math.Min(CruiseSpeedMetresPerSecond,
                startingSpeed + accelerationFloor * Math.Max(0, seconds - departureSeconds));

        internal bool TryWithApproachDistance(float distanceMetres, float leaderSpeed, float followerSpan,
            out PassingMotion motion)
        {
            // The output may alias this value; read the estimate before assigning it.
            if (!Finite(distanceMetres) || distanceMetres < 0) { motion = default; return false; }
            float coast = startingSpeed > 0 ? distanceMetres / startingSpeed : float.PositiveInfinity;
            // Cap the coast estimate by the same acceleration model from rest
            // for every initial speed. The fit still credits only observed speed
            // during the approach. This is an estimate, not a native speed order.
            var fromRest = new PassingMotion(CruiseSpeedMetresPerSecond, 0, 0, TransitionMetres, 0,
                accelerationFloor, PassingGate.DepartureSeconds);
            float movingApproach = accelerationFloor > 0 ? fromRest.ArrivalSeconds(distanceMetres) : float.PositiveInfinity;
            float approachSeconds = Math.Min(coast, movingApproach);
            if (!Finite(approachSeconds)) { motion = default; return false; }
            return TryEstimateCore(startingSpeed, leaderSpeed, CruiseSpeedMetresPerSecond, accelerationFloor,
                followerSpan, TransitionMetres, Math.Max(PassingGate.DepartureSeconds, approachSeconds), out motion);
        }

        internal float ReturnCompletionSeconds(float exitDistanceMetres)
            => ArrivalSeconds(exitDistanceMetres + TransitionMetres) + PassingGate.ReturnSeconds;

        internal bool ClearsLeaderAt(float exitDistanceMetres, float leaderSpeed, float followerSpan)
        {
            float arrival = ArrivalSeconds(exitDistanceMetres);
            return Finite(arrival) && exitDistanceMetres - followerSpan
                > leaderSpeed * arrival + PassingGate.ReturnClearanceMetres;
        }

        // A native acceleration floor bounds the free-running ramp to this cruise
        // speed. Departure gets no credit for gaining on the leader.
        internal static bool TryEstimate(float startingSpeed, float leaderSpeed, float cruiseSpeed,
            float accelerationFloor, float followerSpan, out PassingMotion motion, float transitionSpeed = -1)
            => TryEstimateCore(startingSpeed, leaderSpeed, cruiseSpeed, accelerationFloor, followerSpan,
                Math.Max(15, (transitionSpeed < 0 ? startingSpeed : transitionSpeed) * 1.2f),
                PassingGate.DepartureSeconds, out motion);

        private static bool TryEstimateCore(float startingSpeed, float leaderSpeed, float cruiseSpeed,
            float accelerationFloor, float followerSpan, float transition, float departure, out PassingMotion motion)
        {
            motion = default;
            if (!Finite(startingSpeed) || !Finite(leaderSpeed) || !Finite(cruiseSpeed)
                || !Finite(accelerationFloor) || !Finite(followerSpan) || startingSpeed < 0
                || leaderSpeed < 0 || followerSpan < 0 || cruiseSpeed <= leaderSpeed || accelerationFloor < 0
                || !Finite(transition) || !Finite(departure) || transition <= 0 || departure < 0)
                return false;
            float start = Math.Min(startingSpeed, cruiseSpeed);
            float gain = followerSpan + PassingGate.ReturnClearanceMetres
                + Math.Max(0, leaderSpeed - start) * departure;
            float relative = start - leaderSpeed;
            float ramp = cruiseSpeed > start && accelerationFloor > 0 ? (cruiseSpeed - start) / accelerationFloor : 0;
            if (cruiseSpeed > start && accelerationFloor <= 0) return false;
            float rampGain = relative * ramp + .5f * accelerationFloor * ramp * ramp;
            float seconds = rampGain >= gain && accelerationFloor > 0
                ? (-relative + (float)Math.Sqrt(relative * relative + 2 * accelerationFloor * gain)) / accelerationFloor
                : ramp + (gain - rampGain) / (cruiseSpeed - leaderSpeed);
            float clear = departure + seconds;
            var estimate = new PassingMotion(cruiseSpeed, 0, clear, transition, start, accelerationFloor, departure);
            // Clearance starts the merge; completion includes travel through its
            // native lane-change segment and the return allowance.
            float completion = estimate.ReturnCompletionSeconds(estimate.DistanceAt(clear) + .01f);
            if (!Finite(completion) || completion <= 0) return false;
            motion = new PassingMotion(cruiseSpeed, completion, clear, transition, start, accelerationFloor, departure);
            return true;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
