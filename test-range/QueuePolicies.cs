using System.Reflection;

internal readonly record struct QueueGate(bool Open, string Reason);
internal readonly record struct QueueEntry(bool Accepted, string Reason, float Enter, float Limit,
    float? ProgrammedExit, float? ProgrammedLanding, float? ApproachSeconds = null, float? RequiredMetres = null, float? Departure = null);

// Gates call the supplied binaries. Motion and gap kernels use production code.
// Admission, route continuity and occupancy selection take supplied scalar inputs
// in place of ECS and native reservations. The runner owns the reduced movement model.
internal sealed class QueuePolicy
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const float NativeStep = 4f / 15f;
    private const float FixtureBraking = 13;
    private readonly QueueNative native;
    private readonly MethodInfo gate;
    private readonly MethodInfo? earliestLaunch;
    private readonly MethodInfo? historicalStoppingMargin;
    private readonly MethodInfo? nativeFollowingSpeed;
    private readonly MethodInfo? estimate, adjustApproach, arrival, distanceAt, observationDistance;
    private readonly MethodInfo? selectLanding, mergeDuration, minimumGap;
    private readonly MethodInfo? shouldSlow, targetSpeed, applyCap, tryRestart, restartGaps, departurePlan, canRetryBehind;
    private readonly PropertyInfo? plannedApproach;
    internal float? GuardMetres { get; }
    internal int MaximumOutstanding { get; } = int.MaxValue;
    internal float RestSpeed { get; }
    private float RecoveryCrawlSpeed { get; }
    private QueueSituation? lastSituation;
    private object? lastGate;

    internal QueuePolicy(QueueNative native, string dll, bool historical)
    {
        this.native = native;
        Historical = historical;
        var assembly = native.LoadMod(dll);
        Identity = assembly.GetName().Version?.ToString() ?? throw new InvalidDataException("Passing assembly has no version.");
        if (historical && assembly.GetName().Version != new Version(0, 1, 1, 0))
            throw new InvalidDataException("Historical queue control requires the archived 0.1.1 DLL.");
        var gateType = Type_(assembly, "PassingGate", true)!;
        gate = gateType.GetMethod(historical ? "Evaluate" : "EvaluateShared", Static)
            ?? throw new MissingMethodException(gateType.FullName, historical ? "Evaluate" : "EvaluateShared");
        var traversal = Type_(assembly, "NativePassingTraversal", true)!;
        if (historical)
        {
            earliestLaunch = gateType.GetMethod("EarliestLaunchRearMetres", Static)
                ?? throw new MissingMethodException(gateType.FullName, "EarliestLaunchRearMetres");
            historicalStoppingMargin = traversal.GetMethod("StoppingMargin", Static)
                ?? throw new MissingMethodException(traversal.FullName, "StoppingMargin");
        }
        else
        {
            var guard = Type_(assembly, "NativeCompatibility", true)!.GetMethod("TryVerify", Static);
            object?[] probe = { null };
            if (guard == null || !(bool)guard.Invoke(null, probe)!)
                throw new InvalidDataException("The candidate rejects the installed native vehicle contract: " + probe[0]);
            var motion = Type_(assembly, "PassingMotion", true)!;
            var returns = Type_(assembly, "ReturnClearance", true)!;
            estimate = Method(motion, "TryEstimate", Static);
            adjustApproach = Method(motion, "TryWithApproachDistance", Instance);
            arrival = Method(motion, "ArrivalSeconds", Instance);
            distanceAt = Method(motion, "DistanceAt", Instance);
            observationDistance = Method(motion, "RequiredObservationMetres", Instance);
            selectLanding = Method(returns, "SelectLanding", Static);
            mergeDuration = Method(returns, "TryMergeDuration", Static);
            minimumGap = Method(returns, "MinimumGapDuringMerge", Static);
            nativeFollowingSpeed = Method(traversal, "NativeFollowingSpeed", Static);
            plannedApproach = motion.GetProperty("ApproachSeconds", Instance);
            var wave = Type_(assembly, "PassingWaveSystem", true)!;
            MaximumOutstanding = (int?)wave.GetField("MaximumOutstandingPasses", Static)?.GetRawConstantValue() ?? int.MaxValue;
            var departure = Type_(assembly, "PassingDeparture");
            if (departure != null) departurePlan = Method(departure, "TryPlan", Static);
            var recovery = Type_(assembly, "TerminalRecovery");
            if (recovery != null)
            {
                GuardMetres = (float)recovery.GetField("GuardMetres", Static)!.GetRawConstantValue()!;
                shouldSlow = Method(recovery, "ShouldSlow", Static);
                targetSpeed = Method(recovery, "TargetSpeed", Static);
                applyCap = Method(recovery, "ApplyCap", Static);
                tryRestart = Method(recovery, "TryRestart", Static);
                restartGaps = Method(recovery, "RestartGaps", Static);
                canRetryBehind = recovery.GetMethod("CanRetryBehind", Static);
                RestSpeed = (float)recovery.GetField("RestSpeed", Static)!.GetRawConstantValue()!;
                RecoveryCrawlSpeed = (float)recovery.GetField("CrawlSpeed", Static)!.GetRawConstantValue()!;
            }
        }
    }

    internal bool Historical { get; }
    internal string Identity { get; }

    internal QueueGate Gate(in QueueSituation situation)
    {
        var decision = GateDecision(situation);
        var state = ReadField(decision, "State");
        string reason = ReadField(decision, "Reason");
        return new QueueGate(state == "Green", reason);
    }

    private object GateDecision(in QueueSituation situation)
    {
        if (lastGate != null && lastSituation == situation)
            return lastGate;

        float road = Math.Max(0, situation.RoadEnd - situation.LeaderFront);
        float opposing = Math.Max(situation.OncomingSpeed, situation.OpposingRoadSpeed);
        var parameters = gate.GetParameters();
        object previous = Enum.Parse(parameters[Historical ? 7 : 5].ParameterType, situation.WasGreen ? "Green" : "Red");
        object[] args = Historical
            ? new object[] { road, situation.OncomingDistance, opposing, situation.LeaderSpeed,
                situation.FrontCapability * .8f, situation.FrontSpan, true, previous }
            : new object[] { road, situation.OncomingDistance, opposing, situation.LeaderSpeed, true,
                previous, Enum.Parse(parameters[6].ParameterType, "StructuralBoundary") };
        lastGate = gate.Invoke(null, args)!;
        lastSituation = situation;
        return lastGate;
    }

    internal QueueEntry Entry(in QueueSituation situation, in QueueCar car, float? predecessorSpeed)
    {
        if (!Gate(situation).Open)
            return Refuse("window-closed");
        return Historical ? HistoricalEntry(situation, car) : RepairedEntry(situation, car, predecessorSpeed);
    }

    private QueueEntry HistoricalEntry(in QueueSituation situation, in QueueCar car)
    {
        float passingSpeed = situation.FrontCapability * .8f;
        float opposing = Math.Max(situation.OncomingSpeed, situation.OpposingRoadSpeed);
        float earliest = (float)earliestLaunch!.Invoke(null, new object[] { GateDecision(situation),
            situation.LeaderFront, situation.FrontSpan, passingSpeed - situation.LeaderSpeed, passingSpeed + opposing })!;
        if (car.Front - car.Length < earliest)
            return Refuse("queue-headway");
        // Both native lane free-speed bounds are supplied as Capability in this fixture.
        if (car.Capability * .8f - situation.LeaderSpeed < 5)
            return Refuse("cannot-gain");
        if (car.Speed <= .1f)
            return Refuse("not-moving");
        float departure = car.Front + Math.Max(0, car.ReservedApproach) + Math.Max(5, car.Speed * .3f);
        float transition = Math.Max(15, car.Speed * 1.2f);
        float enter = departure + transition;
        float landing = Math.Min(situation.RoadEnd, car.HomeEnd) - Math.Max(25, car.Length);
        float exit = landing - transition;
        if (enter + 30 >= exit || exit <= situation.LeaderFront + car.Length + 5)
            return Refuse("no-route-home");
        // 0.1.1 wrote the home splice in the original buffer at commitment.
        return new QueueEntry(true, "programmed-home", enter, exit, exit, landing, (enter - car.Front) / car.Speed, Departure: departure);
    }

    private QueueEntry RepairedEntry(in QueueSituation situation, in QueueCar car, float? predecessorSpeed)
    {
        float homeEnd = Math.Min(situation.RoadEnd, car.HomeEnd);
        float departure = car.Front + Math.Max(0, car.ReservedApproach) + 8;
        float enter = departure + Math.Max(30, car.Speed * 1.2f);
        if (departurePlan != null)
        {
            object[] plan = { car.Front + Math.Max(0, car.ReservedApproach), car.Front,
                car.Speed, car.Length, situation.LeaderSpeed, car.NextHomeRear, 0f, 0f };
            if (!(bool)departurePlan.Invoke(null, plan)!) return Refuse("departure-blocked");
            departure = (float)plan[6];
            enter = (float)plan[7];
        }
        if (homeEnd <= departure) return Refuse("no-route-home");
        float span = Math.Max(0, situation.LeaderFront - (car.Front - car.Length));
        float opposing = Math.Max(situation.OncomingSpeed, situation.OpposingRoadSpeed);
        float available = Math.Min(situation.RoadEnd - situation.LeaderFront, situation.OncomingDistance);
        float latestBoundary = homeEnd - (float)selectLanding!.Invoke(null,
            new object[] { 0f, Math.Max(car.Speed, car.PrefabCap) })! - car.Length;
        if (car.Capability <= situation.LeaderSpeed)
            return Refuse("cannot-gain");

        // Every fixture follower has the same road capability and prefab. An
        // active predecessor constrains initial progress, not its cruise ceiling.
        float cap = car.Capability;
        float start = Math.Max(0, Math.Min(car.Speed, predecessorSpeed ?? car.Speed));
        float approachDistance = enter - car.Front;
        QueueEntry best = Refuse("motion-unavailable");
        bool bestFitsHome = false;
        for (int i = 0; i < 8; i++)
        {
            int sample = (i + 3) % 8 + 1;
            float cruise = situation.LeaderSpeed + (cap - situation.LeaderSpeed) * sample / 8;
            object?[] inputs = { start, situation.LeaderSpeed, cruise, native.AccelerationFloor(car.PrefabCap, cruise),
                span, null, car.Speed };
            if (!(bool)estimate!.Invoke(null, inputs)!) continue;
            object motion = inputs[5]!;
            float effectiveStart = Math.Min(start, cruise);
            float approach = Math.Max(1.2f, effectiveStart > 0 ? approachDistance / effectiveStart
                : (float)arrival!.Invoke(motion, new object[] { approachDistance })!);
            object?[] adjusted = { approachDistance, situation.LeaderSpeed, span, null };
            if (!(bool)adjustApproach!.Invoke(motion, adjusted)!) continue;
            motion = adjusted[3]!;
            if (plannedApproach != null) approach = (float)plannedApproach.GetValue(motion)!;
            float required = (float)observationDistance!.Invoke(motion,
                new object[] { situation.LeaderSpeed, opposing, car.Length, car.Capability })!;
            if (!float.IsFinite(required)) continue;
            float clearDistance = (float)distanceAt!.Invoke(motion, new object[] { Number(motion, "ClearSeconds") })!;
            bool fitsHome = Math.Max(enter, car.Front + clearDistance) < latestBoundary;
            if (fitsHome && required <= available)
            {
                if (situation.LocalOncomingRear is float localRear && localRear > car.Rear
                    && localRear - opposing * Number(motion, "CompletionSeconds") < enter + car.Length)
                    return Refuse("entry-occupied");
                return new QueueEntry(true, "rolling-home", enter, latestBoundary, null, null, approach, required, departure);
            }
            if ((fitsHome && !bestFitsHome) || (fitsHome == bestFitsHome && required < (best.RequiredMetres ?? float.PositiveInfinity)))
            {
                best = new QueueEntry(false, required > available ? "insufficient-clearance" : "no-route-home",
                    enter, latestBoundary, null, null, approach, required, departure);
                bestFitsHome = fitsHome;
            }
        }
        return best;
    }

    internal bool ShouldHold(float remaining, float speed)
        => shouldSlow != null && (bool)shouldSlow.Invoke(null, new object[] { remaining, speed, FixtureBraking })!;

    internal bool CanRetryBehind(int entries, bool stoppedInHold)
        => canRetryBehind == null ? entries < 24
            : (bool)canRetryBehind.Invoke(null, new object[] { entries, stoppedInHold })!;

    internal float LimitRecoverySpeed(float remaining, float nativeCommand, float measuredSpeed)
    {
        float target = (float)targetSpeed!.Invoke(null, new object[] { remaining, FixtureBraking })!;
        return (float)applyCap!.Invoke(null, new object[] { nativeCommand, measuredSpeed, FixtureBraking, target })!;
    }

    internal bool CanReturn(in QueueCar car, float boundary, float landing, in QueueGap gap, bool restart = false)
    {
        if (landing >= car.HomeEnd)
            return false;
        if (Historical)
        {
            // 0.1.1 ForwardClear measures other cars against a static whole-merge
            // footprint. The fixture gives nearest home-lane neighbors, not the ECS scan.
            float ahead = gap.FrontGap + car.Front - landing - car.Length * .5f;
            float behind = gap.RearGap + boundary - car.Front + car.Length * .5f;
            return ahead > HistoricalMargin(car.Speed, gap.FrontSpeed)
                || behind > HistoricalMargin(gap.RearSpeed, car.Speed);
        }

        float front, rear, selfSpeed = car.Speed;
        if (restart)
        {
            float travel = landing - car.Rear;
            float cruise = Math.Min(RecoveryCrawlSpeed, car.PrefabCap * .5f);
            object[] duration = { travel, native.AccelerationFloor(car.PrefabCap, cruise), cruise, 0f };
            if (!(bool)tryRestart!.Invoke(null, duration)!) return false;
            object[] gaps = { gap.FrontGap, gap.FrontSpeed, gap.RearGap, gap.RearSpeed, travel, duration[3], 0f, 0f };
            restartGaps!.Invoke(null, gaps);
            front = (float)gaps[6];
            rear = (float)gaps[7];
            selfSpeed = Math.Min(car.PrefabCap, MathF.Sqrt(2 * 7 * travel));
        }
        else
        {
            object[] durationArgs = { landing, car.Rear, car.Speed, 0f };
            if (!(bool)mergeDuration!.Invoke(null, durationArgs)!) return false;
            float duration = (float)durationArgs[3];
            front = (float)minimumGap!.Invoke(null, new object[] { gap.FrontGap, gap.FrontSpeed - car.Speed, 0f, duration })!;
            rear = (float)minimumGap.Invoke(null, new object[] { gap.RearGap, car.Speed - gap.RearSpeed, 0f, duration })!;
        }
        return front > .9f ? ClearsFollowing(selfSpeed, car.PrefabCap, gap.FrontSpeed, front, car.Safety)
            : rear > .9f && ClearsFollowing(gap.RearSpeed, car.PrefabCap, car.Speed, rear, gap.OtherSafety);
    }

    private bool ClearsFollowing(float speed, float capability, float aheadSpeed, float gap, float safety)
    {
        if (float.IsPositiveInfinity(gap)) return true;
        if (gap <= .9f) return false;
        float allowed = (float)nativeFollowingSpeed!.Invoke(null, new object[]
        {
            native.Car(capability), native.Car(Math.Max(capability, aheadSpeed)), Math.Max(0, aheadSpeed),
            gap - .9f, NativeStep + safety
        })!;
        return speed <= allowed;
    }

    private float HistoricalMargin(float speed, float aheadSpeed)
        => (float)historicalStoppingMargin!.Invoke(null, new object[] { speed, FixtureBraking, aheadSpeed, FixtureBraking })!;

    // Releases up to 0.1.7 used the TwoLanePassing namespace; later ones use Overtake.
    private static Type? Type_(Assembly assembly, string name, bool required = false)
        => assembly.GetType("Overtake." + name) ?? assembly.GetType("TwoLanePassing." + name, required);

    private static MethodInfo Method(Type type, string name, BindingFlags flags)
        => type.GetMethod(name, flags) ?? throw new MissingMethodException(type.FullName, name);

    private static float Number(object value, string name) => (float)value.GetType().GetField(name, Instance)!.GetValue(value)!;

    private static string ReadField(object value, string name)
        => value.GetType().GetField(name, Instance)!.GetValue(value)!.ToString()!;

    private static QueueEntry Refuse(string reason) => new(false, reason, 0, 0, null, null);
}
