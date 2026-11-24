using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.Json;
using Overtake;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly List<object> Contracts = new();
    private static int failures;
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));

    private static int Main(string[] args)
    {
        try
        {
            string? Value(string key) { int at = Array.IndexOf(args, key); return at >= 0 && at + 1 < args.Length ? args[at + 1] : null; }
            if (Value("--queue-control") is string control)
            {
                string managed = Value("--managed") ?? throw new ArgumentException("--managed is required for the queue replay.");
                string candidate = Value("--native-assembly") ?? throw new ArgumentException("--native-assembly is required for the queue replay.");
                string queueOutput = Path.GetFullPath(Value("--output") ?? Path.Combine(Root, "bin/queue-replay.json"));
                var result = QueueReplay.Compare(managed, control, candidate,
                    Value("--queue-fixture") ?? Path.Combine(Root, "fixtures/historical-queue-release.json"),
                    args.Contains("--legacy-approach"));
                Directory.CreateDirectory(Path.GetDirectoryName(queueOutput)!);
                File.WriteAllText(queueOutput, JsonSerializer.Serialize(result, Json) + Environment.NewLine);
                Console.WriteLine("Queue replay complete. " + queueOutput);
                return 0;
            }
            string fixture = Value("--scenarios") ?? Path.Combine(Root, "fixtures/constructed-scenarios.json");
            string trace = Value("--trace") ?? Path.Combine(Root, "fixtures/recorded-return-trace.json");
            string output = Value("--output") ?? Path.Combine(Root, "bin/range-results.json");
            int iterations = int.Parse(Value("--iterations") ?? "10000");
            if (iterations < 100) throw new ArgumentOutOfRangeException(nameof(iterations), "At least 100 iterations are required for a microbenchmark.");
            var definitions = JsonSerializer.Deserialize<ScenarioSet>(File.ReadAllText(fixture), Json) ?? throw new InvalidDataException(fixture);
            NativeGeometry? geometry = Value("--native-assembly") is string nativePath
                ? new NativeGeometry(nativePath, Value("--managed") ?? Environment.GetEnvironmentVariable("CS2_MANAGED_PATH") ?? throw new ArgumentException("Managed game directory required")) : null;
            var scenarios = definitions.Scenarios.Select(s => RunScenarioChecked(s, geometry)).ToArray();
            var replay = Replay(trace);
            MetamorphicContracts();
            var benchmarks = new List<object>();
            foreach (int n in new[] { 0, 1, 2, 5, 10, 20 })
            {
                int followers = n;
                var capabilities = Enumerable.Repeat(55.55556f, n).ToArray();
                benchmarks.Add(Measure("shared-gate-and-cheap-capability", n, iterations, () =>
                {
                    if (followers == 0) return 0;
                    var gate = Shared(2000, float.PositiveInfinity, 48.88889f, 27.77778f);
                    int feasible = 0;
                    foreach (float capability in capabilities)
                        if (PassingGate.AllowsCommitment(gate.State) && PassingGate.CanGainOnLeader(capability, 27.77778f)) feasible++;
                    return feasible;
                }));
                var membership = new PoolMembership<int>();
                if (n > 0) for (int i = 0; i < n; i++) membership.TryClaim(1, i + 2);
                benchmarks.Add(Measure("existing-membership-lookup", n, iterations, () =>
                { int found = 0; for (int i = 0; i < followers; i++) if (membership.TryOwner(i + 2, out int owner) && owner == 1) found++; return found; }));
            }
            benchmarks.Add(Measure("candidate-production-scalar-gate", 0, iterations, () => (int)Shared(2000, float.PositiveInfinity, 55, 28).YellowDistanceMetres));
            benchmarks.Add(Measure("return-clearance-supplied-candidate", 1, iterations, () =>
            {
                float landing = ReturnClearance.SelectLanding(120, 20);
                return ReturnClearance.TryMergeDuration(landing, 100, 20, out float time)
                    && ReturnClearance.MinimumGapDuringMerge(30, -2, 0, time) > 0 ? 1 : 0;
            }));
            foreach (int repeats in new[] { 1, 8 })
            {
                int fits = repeats;
                benchmarks.Add(Measure("reconstructed-fit-call-workload-" + fits, 1, iterations, () =>
                {
                    int result = 0;
                    for (int i = 0; i < fits; i++)
                        if (PassingMotion.TryEstimate(28, 28, 50, 1, 30, out var motion, 28)
                            && motion.TryWithApproachDistance(80, 28, 30, out motion))
                            result ^= (int)motion.RequiredObservationMetres(28, 55, 5, 83.333336f);
                    return result;
                }));
            }
            if (geometry != null)
                foreach (var workload in geometry.MeasurementWorkloads())
                    benchmarks.Add(Measure(workload.Stage, 1, Math.Min(iterations, 1000), workload.Operation));
            object? baseline = Value("--baseline-assembly") is string baselinePath ? Baseline(baselinePath, iterations) : null;
            object? inflation = Value("--regression-baseline-assembly") is string regressionAssembly ? RegressionComparison(regressionAssembly) : null;
            object? native = Value("--native-assembly") is string nativeAssembly
                ? NativeChecks(nativeAssembly, Value("--managed") ?? Environment.GetEnvironmentVariable("CS2_MANAGED_PATH")) : null;
            var report = new
            {
                schemaVersion = 1, utc = DateTimeOffset.UtcNow,
                runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount,
                tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime-default",
                status = failures == 0 ? "PASS" : "FAIL", failures,
                scope = "Actual linked production decisions; no ECS world, traffic movement, collision, live reservation or save/load simulation.",
                measurementScope = "Warm offline managed microbenchmarks, including call overhead; not citywide frame time or game FPS. Shared-evaluation counts count range calls, not an instrumented ECS system.",
                scenarios, recordedTrace = replay, contracts = Contracts, benchmarks, baselineComparison = baseline, inflationRegression = inflation, nativeContracts = native,
                nativeOnly = new[] { "Physical simultaneous passing/return and induced braking", "Reservation and current-navigation mutation in a live world", "Blocked return recovery PathStart/directional continuity", "Native save/load callbacks, entity remapping and physical continuation", "Citywide CPU/update, tail spikes and allocations with real traffic" }
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(report, Json) + Environment.NewLine);
            Console.WriteLine($"{report.status}: {scenarios.Length} scenario definitions, {Contracts.Count} contract observations; report: {Path.GetFullPath(output)}");
            return failures == 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "FAIL", firstContract = "range-initialization", error = error.ToString() }, Json));
            return 1;
        }
    }

    private static GateDecision Shared(float road, float oncoming, float oncomingSpeed, float leader,
        bool complete = true, GateState previous = GateState.Red, RoadCoverageState coverage = RoadCoverageState.Complete)
        => PassingGate.EvaluateShared(road, oncoming, oncomingSpeed, leader, complete, previous, coverage);

    private static object RunScenarioChecked(Scenario s, NativeGeometry? geometry)
    {
        try { return RunScenario(s, geometry); }
        catch (InvalidDataException error)
        {
            failures++;
            return new { s.Name, passed = false, firstFailure = error.Message, sourceInputs = s,
                skipped = "Dependent checks stopped at the first broken prerequisite" };
        }
    }
    private static object RunScenario(Scenario s, NativeGeometry? geometry)
    {
        var violated = new List<string>();
        void Require(bool value, string contract) { if (!value) throw new InvalidDataException(contract); }
        var membership = new PoolMembership<int>();
        float leader = s.LeaderDisplayKmh / 1.8f;
        if (s.Followers > 0)
        {
            for (int i = 0; i < s.Followers; i++) Require(membership.TryClaim(1, i + 2), "canonical-membership-claim");
            Require(!membership.TryClaim(2, 999), "a-follower-cannot-create-an-overlapping-pool");
        }
        float opposing = s.OpposingDistances.Length > 0 ? s.OpposingDistances.Min() : s.OncomingMetres ?? float.PositiveInfinity;
        GeometryResult? nativeGeometry = s.GeometryProfile == null || geometry == null ? null : geometry.Evaluate(s.GeometryProfile, s.RoadMetres);
        if (nativeGeometry != null && !nativeGeometry.Passed)
        {
            failures++;
            return new { s.Name, passed = false, firstFailure = "native-geometry-route-contract", sourceInputs = s, geometry = nativeGeometry,
                skipped = "Gate, capability and return checks skipped because corridor construction failed" };
        }
        float verifiedRoad = nativeGeometry?.VerifiedMetres ?? s.VerifiedRoadMetres ?? s.RoadMetres;
        RoadCoverageState coverage = Enum.Parse<RoadCoverageState>(s.Coverage);
        long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
        GateDecision? result = s.Followers == 0 ? null : Shared(verifiedRoad, opposing, s.OncomingSpeed, leader, s.Complete, GateState.Red, coverage);
        int sharedEvaluations = result.HasValue ? 1 : 0;
        string actualGate = result?.State.ToString() ?? "None";
        Require(actualGate == s.ExpectedGate, "expected-gate-" + s.ExpectedGate + "-actual-" + actualGate);
        var candidates = new List<object>();
        int fits = 0;
        foreach (int id in Enumerable.Range(2, s.Followers))
        {
            bool capable = PassingGate.CanGainOnLeader(s.CapabilityMetresPerSecond, leader);
            bool open = result.HasValue && PassingGate.AllowsCommitment(result.Value.State);
            float span = s.FrontFollowerSpanMetres + (id - 2) * s.FollowerSpacingMetres;
            PassingMotion motion = default;
            bool fitted = open && capable && PassingMotion.TryEstimate(s.StartingSpeedMetresPerSecond ?? leader, leader,
                s.CruiseSpeedMetresPerSecond, s.AccelerationFloorMetresPerSecondSquared, span, out motion)
                && motion.TryWithApproachDistance(s.ApproachDistanceMetres, leader, span, out motion);
            float? requiredObservation = fitted ? motion.RequiredObservationMetres(leader, s.OncomingSpeed, s.BodyLength, s.CapabilityMetresPerSecond) : null;
            if (fitted && requiredObservation <= Math.Min(verifiedRoad, opposing)) fits++;
            candidates.Add(new { role = "follower-" + (id - 1), capable,
                decisionEligible = open && capable,
                individualMotion = new { attempted = open && capable, fitted, spanMetres = span, s.ApproachDistanceMetres,
                    s.CruiseSpeedMetresPerSecond, s.AccelerationFloorMetresPerSecondSquared,
                    completionSeconds = fitted ? motion.CompletionSeconds : (float?)null,
                    requiredObservationMetres = requiredObservation,
                    scalarDecisionFit = fitted && requiredObservation <= Math.Min(verifiedRoad, opposing),
                    limitation = "Production scalar fit with supplied inputs; ECS TryEstimateMotion dynamics, predecessor and native entry selection not exercised" },
                nativeEntryFeasibility = "NOT_EXERCISED: current route, reservations and native ECS inputs required",
                refusal = !capable ? "CannotGainOnLeader" : !open ? "SharedGateClosed" : !fitted ? "ScalarMotionUnavailable" : requiredObservation > Math.Min(verifiedRoad, opposing) ? "IndividualObservationInsufficient" : "NoneAtThisFidelity" });
        }
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (s.MinimumScalarFits.HasValue) Require(fits >= s.MinimumScalarFits.Value,
            "minimum-individual-scalar-fits-" + s.MinimumScalarFits.Value + "-actual-" + fits);
        if (s.MaximumScalarFits.HasValue) Require(fits <= s.MaximumScalarFits.Value,
            "maximum-individual-scalar-fits-" + s.MaximumScalarFits.Value + "-actual-" + fits);
        Require(sharedEvaluations == (s.Followers > 0 ? 1 : 0), "one-shared-evaluation-per-obstruction");
        Require(s.Followers == 0 || membership.Count == s.Followers + 1, "all-followers-have-one-canonical-owner");
        float packageRear = s.ReturnRearMetres ?? s.ReturnFrontMetres - s.BodyLength;
        float selectedLanding = ReturnClearance.SelectLanding(s.ReturnBoundaryMetres, s.ReturnProgress);
        bool durationAvailable = ReturnClearance.TryMergeDuration(selectedLanding, packageRear, s.ReturnProgress, out float mergeSeconds);
        float? gap = durationAvailable ? ReturnClearance.MinimumGapDuringMerge(s.ReturnGapMetres, s.ReturnRelativeSpeed, 0, mergeSeconds) : null;
        Require((durationAvailable && gap > 0) == s.ExpectedPureReturn,
            "pure-return-clearance-expected-" + s.ExpectedPureReturn + "-actual-" + (durationAvailable && gap > 0));
        return new
        {
            s.Name, fidelity = 1, sourceInputs = s, expectedGate = s.ExpectedGate, actualGate,
            canonicalPoolCount = Enumerable.Range(2, s.Followers).Select(id => membership.TryOwner(id, out int actualOwner) ? actualOwner : 0).Where(owner => owner != 0).Distinct().Count(), followerCount = s.Followers,
            corridorCoverage = coverage.ToString(), verifiedDistanceMetres = verifiedRoad,
            geometry = nativeGeometry,
            geometryFidelity = nativeGeometry != null ? "Level3 native curve calculations" : "Level1 supplied verified scalar distance; geometry not reconstructed",
            sharedTrafficEvaluationCount = sharedEvaluations,
            requiredClearance = result.HasValue ? new { yellowMetres = result.Value.YellowDistanceMetres, redMetres = result.Value.RedDistanceMetres, forwardFieldMetres = result.Value.PassingTravelMetres } : null,
            candidates, gateRejection = result?.Reason.ToString(),
            returnCandidate = new { selectedLandingMetres = selectedLanding, boundaryInputMetres = s.ReturnBoundaryMetres,
                selection = "Production SelectLanding formula on supplied boundary; native reserved-prefix selection is not exercised",
                packageLengthMetres = s.BodyLength, packageRearMetres = packageRear, mergeDurationSeconds = durationAvailable ? mergeSeconds : (float?)null,
                minimumGapMetres = gap, pureClearance = durationAvailable && gap > 0,
                rejection = !durationAvailable ? "NoForwardMergeDuration" : gap <= 0 ? "GapClosesOrIsOccupied" : "NativeFollowingAndReservationChecksStillRequired" },
            elapsedMicroseconds = elapsed, allocatedBytesIncludingResultConstruction = bytes,
            passed = violated.Count == 0, firstFailure = violated.FirstOrDefault(), invariantViolations = violated
        };
    }

    private static object Replay(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var frames = new List<object>();
        foreach (var f in document.RootElement.GetProperty("frames").EnumerateArray())
        {
            float rear = f.GetProperty("passer").GetProperty("rearMetres").GetSingle();
            float leaderFront = f.GetProperty("leader").GetProperty("frontMetres").GetSingle();
            bool cleared = ReturnClearance.HasClearedLeader(rear, leaderFront);
            string? recordedBlock = f.GetProperty("recordedReturnBlock").GetString();
            bool? expectedClearance = f.TryGetProperty("expectedLeaderClearance", out var expected) ? expected.GetBoolean() : null;
            bool pass = !expectedClearance.HasValue || expectedClearance.Value == cleared;
            if (!pass) failures++;
            frames.Add(new { frame = f.GetProperty("frame").GetUInt32(), passerRearMetres = rear, leaderFrontMetres = leaderFront,
                recordedReturnBlock = recordedBlock, recordedFlagDisagreesWithGeometry = recordedBlock == "LeaderNotCleared" && cleared, expectedLeaderClearance = expectedClearance, actualLeaderClearance = cleared,
                nativeReservationResult = "NOT_REPLAYABLE_FROM_INTERVAL_SNAPSHOT_ALONE", passed = pass,
                firstFailure = pass ? null : "recorded-leader-clearance-contract" });
        }
        return new { fidelity = 2, provenance = document.RootElement.GetProperty("provenance").GetString(),
            source = document.RootElement.GetProperty("source").GetString(), sourceSha256 = document.RootElement.GetProperty("sourceSha256").GetString(), frames };
    }

    private static void Contract(bool condition, string name, object inputs)
    {
        if (!condition) failures++;
        Contracts.Add(new { name, status = condition ? "PASS" : "FAIL", inputs, firstFailure = condition ? null : name });
    }

    private static void MetamorphicContracts()
    {
        var open = Shared(2000, float.PositiveInfinity, 50, 50 / 1.8f);
        Contract(open.State == GateState.Green, "long-empty-road-opens-a-shared-window", new { road = 2000, oncoming = "none", leaderDisplayKmh = 50 });
        var before = open;
        bool incapable = PassingGate.CanGainOnLeader(10, 50 / 1.8f);
        Contract(!incapable && open.State == before.State && open.YellowDistanceMetres == before.YellowDistanceMetres,
            "incapable-follower-does-not-repaint-shared-gate", new { capability = 10, before = before.State.ToString(), after = open.State.ToString() });
        var close = Shared(2000, open.RedDistanceMetres - 1, 50, 50 / 1.8f);
        var fast = Shared(2000, open.RedDistanceMetres - 1, 80, 50 / 1.8f);
        Contract(close.State != GateState.Green && fast.State != GateState.Green,
            "increasing-oncoming-speed-cannot-open-an-unsafe-window", new { distance = open.RedDistanceMetres - 1, speedBefore = 50, speedAfter = 80 });
        var yellow = Shared(2000, (open.YellowDistanceMetres + open.RedDistanceMetres) / 2, 50, 50 / 1.8f, true, GateState.Green);
        Contract(yellow.State == GateState.Yellow && !PassingGate.AllowsCommitment(yellow.State),
            "yellow-closes-new-commitment", new { distance = yellow.AvailableDistanceMetres });
        Contract(Shared(0, float.PositiveInfinity, 50, 28, true, GateState.Red, RoadCoverageState.StructuralBoundary).State == GateState.Purple,
            "unsupported-road-cannot-open-with-empty-traffic", new { verifiedRoad = 0, coverage = "StructuralBoundary" });
        var membership = new PoolMembership<int>();
        membership.TryClaim(1, 2); membership.TryClaim(1, 3);
        Contract(!membership.TryClaim(2, 4) && membership.Count == 3, "middle-follower-cannot-own-nested-pool", new { leader = 1, members = new[] { 2, 3 } });
        var reversed = new PoolMembership<int>(); reversed.TryClaim(1, 3); reversed.TryClaim(1, 2);
        Contract(reversed.TryOwner(2, out int owner) && owner == 1 && reversed.TryOwner(3, out owner) && owner == 1,
            "membership-claim-order-does-not-change-canonical-owner", new { order = new[] { 3, 2 } });
        membership.Dissolve(1);
        Contract(membership.Count == 0, "pool-dissolution-clears-waiting-membership", new { leader = 1 });
        Contract(ReturnClearance.MinimumGapDuringMerge(-5, 10, 0, 4) == -5,
            "future-open-gap-cannot-erase-present-overlap", new { initialGap = -5, relativeSpeed = 10, mergeSeconds = 4 });
        Contract(!ReturnClearance.TryMergeDuration(140, 80, 0, out _), "stationary-passer-has-no-invented-arrival", new { landing = 140, rear = 80, progress = 0 });
        Contract(!ReturnClearance.HasClearedLeader(384.2204f, 438.726654f), "recorded-alongside-passer-cannot-return-ahead", new { rear = 384.2204f, leaderFront = 438.726654f });
        bool estimated = PassingMotion.TryEstimate(10, 10, 20, 2, 30, out var aliased);
        bool adjusted = estimated && aliased.TryWithApproachDistance(45, 10, 30, out aliased);
        Contract(adjusted && aliased.CompletionSeconds > 0, "admission-alias-preserves-receiver-before-output", new { start = 10, leader = 10, cruise = 20, acceleration = 2, span = 30, approach = 45 });
    }

    private static object Measure(string stage, int followers, int iterations, Func<int> operation)
    {
        for (int i = 0; i < 1000; i++) sink ^= operation();
        const int batch = 100;
        var times = new double[(iterations + batch - 1) / batch];
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        int index = 0;
        for (int i = 0; i < iterations; i += batch)
        {
            int count = Math.Min(batch, iterations - i); long start = Stopwatch.GetTimestamp();
            for (int j = 0; j < count; j++) sink ^= operation();
            times[index++] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / count;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Array.Sort(times);
        return new { stage, followers, iterations, warmup = 1000, timing = "Nanoseconds/op; quantiles of 100-call batch means",
            averageNanoseconds = times.Average(), medianNanoseconds = times[times.Length / 2],
            p95Nanoseconds = times[(int)Math.Ceiling(times.Length * .95) - 1], worstBatchMeanNanoseconds = times[^1],
            allocatedBytesPerOperation = (double)allocated / iterations, sharedEvaluationsPerOperation = stage.StartsWith("shared-gate") && followers > 0 ? 1 : 0,
            note = "No engine scheduling, job waits, native traffic discovery or physical movement in this measurement" };
    }
    private static int sink;

    private static object Baseline(string path, int iterations)
    {
        var context = new AssemblyLoadContext("historical-control", isCollectible: true);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
        var type = assembly.GetType("TwoLanePassing.PassingGate") ?? assembly.GetType("HistoricalPassing.PassingGate") ?? throw new TypeLoadException("Baseline must contain the actual archived PassingGate type.");
        var method = type.GetMethod("Evaluate", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMethodException(type.FullName, "Evaluate");
        var dynamic = new DynamicMethod("ActualBaselineGate", typeof(float), new[] { typeof(float) }, typeof(Program).Module, true);
        var il = dynamic.GetILGenerator();
        foreach (var parameter in method.GetParameters())
        {
            if (parameter.Name == "verifiedDistanceMetres") il.Emit(OpCodes.Ldarg_0);
            else if (parameter.ParameterType == typeof(float))
            {
                float value = parameter.Name switch { "oncomingDistanceMetres" => float.PositiveInfinity, "oncomingSpeedMetresPerSecond" => 55, "leaderSpeedMetresPerSecond" => 28, "passingSpeedMetresPerSecond" => 55.55556f, "frontFollowerSpanMetres" => 30, _ => 0 };
                il.Emit(OpCodes.Ldc_R4, value);
            }
            else if (parameter.ParameterType == typeof(bool)) il.Emit(OpCodes.Ldc_I4_1);
            else if (parameter.ParameterType.IsEnum) il.Emit(OpCodes.Ldc_I4_0);
            else throw new InvalidOperationException("Unmapped baseline parameter " + parameter.Name);
        }
        il.Emit(OpCodes.Call, method); var local = il.DeclareLocal(method.ReturnType); il.Emit(OpCodes.Stloc, local); il.Emit(OpCodes.Ldloca, local);
        il.Emit(OpCodes.Ldfld, method.ReturnType.GetField("YellowDistanceMetres", BindingFlags.Instance | BindingFlags.NonPublic)!); il.Emit(OpCodes.Ret);
        var gate = (Func<float, float>)dynamic.CreateDelegate(typeof(Func<float, float>));
        return new { assembly = Path.GetFullPath(path), assemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
            scope = "Archived production scalar gate only, fixed comparable inputs; no historical scheduler, discovery or traffic engine reconstructed",
            result = Measure("archived-production-scalar-gate", 0, iterations, () => (int)gate(2000)) };
    }


    private static object RegressionComparison(string path)
    {
        // Baseline and candidate intentionally share their production assembly identity.
        var baselineContext = new AssemblyLoadContext("regression-baseline", isCollectible: true);
        var assembly = baselineContext.LoadFromAssemblyPath(Path.GetFullPath(path));
        var gateType = assembly.GetType("TwoLanePassing.PassingGate", true)!;
        var evaluate = gateType.GetMethod("Evaluate", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool fit = PassingMotion.TryEstimate(28, 28, 50, 1, 30, out var motion, 28)
            && motion.TryWithApproachDistance(80, 28, 30, out motion);
        if (!fit) { failures++; return new { passed = false, firstFailure = "configured-regression-motion-fit", inputs = new { start = 28, leader = 28, cruise = 50, acceleration = 1, span = 30, approach = 80 } }; }
        object Old(float completion, float upper)
        {
            var values = evaluate.GetParameters().Select(p => p.Name switch
            {
                "verifiedDistanceMetres" => (object)800f, "oncomingDistanceMetres" => float.PositiveInfinity,
                "oncomingSpeedMetresPerSecond" => 55f, "leaderSpeedMetresPerSecond" => 28f,
                "passingSpeedMetresPerSecond" => 50f, "frontFollowerSpanMetres" => 30f,
                "complete" => true, "completionSeconds" => completion, "speedUpperBound" => upper,
                "previous" => Enum.ToObject(p.ParameterType, 0), "coverage" => Enum.ToObject(p.ParameterType, 0),
                _ => throw new InvalidOperationException("Unmapped regression input " + p.Name)
            }).ToArray();
            return evaluate.Invoke(null, values)!;
        }
        object Describe(object value) { var type = value.GetType(); object Field(string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
            return new { state = Field("State").ToString(), reason = Field("Reason").ToString(), yellowMetres = Field("YellowDistanceMetres") }; }
        return new { provenance = "Constructed regression-review-20261021 inputs, not physical traffic observation", baselineAssembly = Path.GetFullPath(path),
            inputs = new { verifiedMetres = 800, leaderNativeSpeed = 28, cruise = 50, span = 30, accelerationFloor = 1, approachMetres = 80, returnSpeedBound = 83.333336f, oncomingSpeedBound = 55, packageLengthMetres = 5 },
            baselineScalar = Describe(Old(0, 0)), baselineMotion = Describe(Old(motion.CompletionSeconds, 83.333336f)),
            completionSeconds = motion.CompletionSeconds,
            candidateShared = Shared(800, float.PositiveInfinity, 55, 28).State.ToString(),
            candidateSharedYellowMetres = Shared(800, float.PositiveInfinity, 55, 28).YellowDistanceMetres,
            candidateIndividualRequiredMetres = motion.RequiredObservationMetres(28, 55, 5, 83.333336f),
            scope = "Same scalar cruise held at 50 for comparison. Production midpoint cruise selection and ECS dynamics are not exercised here." };
    }

    private static object NativeChecks(string assembly, string? managed)
    {
        if (managed == null || !File.Exists(Path.Combine(managed, "Game.dll"))) throw new ArgumentException("--managed or CS2_MANAGED_PATH is required for native calculations.");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { "run", "--project", Path.Combine(Root, "../tests/NativeInputs/Overtake.NativeInputs.csproj"), "-c", "Release", "--", Path.GetFullPath(assembly), Path.GetFullPath(managed) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated native calculation checker.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) { process.Kill(true); throw new TimeoutException("Native calculation checker exceeded two minutes. No game process was started."); }
        Task.WaitAll(stdout, stderr); if (process.ExitCode != 0) failures++;
        return new { fidelity = 3, status = process.ExitCode == 0 ? "PASS" : "FAIL", assembly = Path.GetFullPath(assembly),
            contracts = stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries), firstFailure = process.ExitCode == 0 ? null : stderr.Result,
            limitation = "Local managed/native value calculations only; Unity EntityManager operations and physical game behavior are not initialized or emulated" };
    }
}

internal sealed class ScenarioSet
{
    public List<Scenario> Scenarios { get; set; } = new();
}
internal sealed class Scenario
{
    public string Name { get; set; } = "unnamed";
    public int Followers { get; set; } = 10;
    public float RoadMetres { get; set; } = 2000;
    public float? VerifiedRoadMetres { get; set; }
    public float LeaderDisplayKmh { get; set; } = 50;
    public float CapabilityMetresPerSecond { get; set; } = 55.55556f;
    public float? OncomingMetres { get; set; }
    public float[] OpposingDistances { get; set; } = Array.Empty<float>();
    public float OncomingSpeed { get; set; } = 48.88889f;
    public bool Complete { get; set; } = true;
    public string Coverage { get; set; } = "Complete";
    public string ExpectedGate { get; set; } = "Green";
    public int? MinimumScalarFits { get; set; }
    public int? MaximumScalarFits { get; set; }
    public bool ExpectedPureReturn { get; set; } = true;
    public float BodyLength { get; set; } = 5;
    public float? StartingSpeedMetresPerSecond { get; set; }
    public float CruiseSpeedMetresPerSecond { get; set; } = 50;
    public float AccelerationFloorMetresPerSecondSquared { get; set; } = 1;
    public float ApproachDistanceMetres { get; set; } = 80;
    public float FrontFollowerSpanMetres { get; set; } = 30;
    public float FollowerSpacingMetres { get; set; } = 10;
    public float ReturnBoundaryMetres { get; set; } = 120;
    public float? ReturnRearMetres { get; set; }
    public float ReturnFrontMetres { get; set; } = 105;
    public string? GeometryProfile { get; set; }
    public float ReturnProgress { get; set; } = 20;
    public float ReturnGapMetres { get; set; } = 30;
    public float ReturnRelativeSpeed { get; set; }
}
