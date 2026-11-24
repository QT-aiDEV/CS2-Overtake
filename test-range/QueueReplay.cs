using System.Text.Json;

internal readonly record struct QueueSituation(float LeaderFront, float LeaderSpeed, float RoadEnd, float OncomingDistance,
    float OncomingSpeed, float FrontSpan, bool WasGreen, float FrontCapability = 55.55556f,
    float OpposingRoadSpeed = 55.55556f, float? LocalOncomingRear = null);
internal readonly record struct QueueCar(float Front, float Speed, float Length, float Capability, float HomeEnd,
    float ReservedApproach, float PrefabCap = 95.55556f, float Safety = .3f, float NextHomeRear = float.PositiveInfinity)
{
    internal float Rear => Front - Length;
}
internal readonly record struct QueueGap(float FrontGap, float FrontSpeed, float RearGap, float RearSpeed, float OtherSafety = .3f);

internal static class QueueReplay
{
    private const int Frames = 2000;
    private const float FrameStep = 1f / 60;
    private const float RoadSpeed = 55.5555573f, LeaderStart = 410, VerifiedAhead = 2453.5127f, Body = 5;
    private sealed record RecordedQueues(string Source, string Sha256, RecordedQueue[] Queues);
    private sealed record RecordedQueue(int Followers, int SourceLine, float LeaderDisplayKmh, float[] Stations,
        float[] DisplayKmh, int PhysicalEntries, int PhysicalReturns, int PhysicalPeak, int[] LaunchOpportunityFrames);

    private enum Phase { Waiting, Approach, Crossing, Opposing, Merging, Returned, Unresolved }
    private sealed class Actor
    {
        internal readonly int Index;
        internal readonly int Shard;
        internal float Front, Speed, MergeLanding, HomeEnd, Safety = .3f;
        internal float? CommittedAt, CrossingAt, FirstSafeReturn, EnteredAt, ReturnedAt;
        internal QueueEntry Entry;
        internal Phase Phase;
        internal bool WasUnsafe, HoldingReturn, RestartedFromHold;
        internal int RecoveryCappedSteps;
        internal Actor(int index, int shard, float front, float speed) { Index = index; Shard = shard; Front = front; Speed = speed; }
        internal float Rear => Front - Body;
        internal bool OnHome => Phase is Phase.Waiting or Phase.Approach or Phase.Merging or Phase.Returned
            || Phase == Phase.Crossing && Front < Entry.Departure!.Value + .75f * (Entry.Enter - Entry.Departure.Value);
        internal bool OnOpposing => Phase is Phase.Crossing or Phase.Opposing or Phase.Merging or Phase.Unresolved;
        internal QueueCar Inputs(float homeEnd, float reserved, float nextHomeRear = float.PositiveInfinity)
            => new(Front, Speed, Body, RoadSpeed, homeEnd, reserved, Safety: Safety, NextHomeRear: nextHomeRear);
    }

    private sealed record Case(string Name, int Followers, float LeaderSpeed, float? StartSpeed = null, float Buffer = 720,
        int BlockFrom = 0, int BlockUntil = 0, bool RearBlocked = false, int ShardOffset = 0, float Reserved = 20,
        float? TurnAfterLeader = null, bool Opposing = false, int RestartAt = -1, bool ReservationHeld = false, bool RecordedPace = false, bool RetryPacedSlots = false);
    private sealed record Run(string Policy, int Admissions, int Entries, int Returns, int Remaining,
        int ReturnedBehind, int Unresolved, int HeldAtEnd, int RestartsFromHold, int PeakOutstanding, int PeakOpposing, int BlockedReturnChecks, int RecoveryRequests, int ProgrammedUnsafeAttempts,
        int SameLaneOverlapFrames, float? MinimumSameLaneGap, float OpposingVehicleSeconds,
        int GateOpenFrames, string? FirstRefusal, Dictionary<string, int> Refusals, object[] Actors);

    internal static object Compare(string managed, string historical, string repaired, string fixture, bool legacyApproach)
    {
        var native = new QueueNative(managed);
        if (Math.Abs(native.RoadSpeed - RoadSpeed) > .0001f)
            throw new InvalidDataException("Native road and wear limit differs from the recorded fixture.");
        const string historicalHash = "95644D712ADDCB4B8F4932A1965A5A0D3617C982D87FAD5A6A4AFDAE050299C8";
        if (QueueNative.Hash(historical) != historicalHash)
            throw new InvalidDataException("Historical queue control must be the physically exercised 0.1.1 DLL.");
        var old = new QueuePolicy(native, historical, true);
        var current = new QueuePolicy(native, repaired, false);
        var recorded = JsonSerializer.Deserialize<RecordedQueues>(File.ReadAllText(fixture),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException(fixture);
        var queues = recorded.Queues.ToDictionary(q => q.Followers);
        foreach (var queue in recorded.Queues)
            if (queue.Followers != queue.Stations.Length || queue.Followers != queue.DisplayKmh.Length)
                throw new InvalidDataException("Historical queue fixture has missing actor inputs.");
        var cases = new List<Case>();
        foreach (int count in new[] { 1, 2, 5, 10, 20 })
            cases.Add(new($"clear-{count}", count, queues[count].LeaderDisplayKmh / 1.8f));
        foreach (float crawl in new[] { 0, .001f, .01f, .1f, .5f, 1f, 3f })
            cases.Add(new($"crawl-{crawl:G}", 10, crawl, crawl));
        cases.Add(new("crawl-restart", 10, .01f, .01f, RestartAt: 480));
        cases.Add(new("closing-oncoming", 10, 50f / 1.8f, Opposing: true));
        cases.Add(new("front-block-releases", 10, 50f / 1.8f, BlockFrom: 0, BlockUntil: 720));
        cases.Add(new("rear-block-releases", 10, 50f / 1.8f, BlockFrom: 0, BlockUntil: 720, RearBlocked: true));
        cases.Add(new("front-block-persists", 10, 50f / 1.8f, BlockFrom: 0, BlockUntil: Frames));
        cases.Add(new("rear-block-persists", 10, 50f / 1.8f, BlockFrom: 0, BlockUntil: Frames, RearBlocked: true));
        cases.Add(new("reservation-held", 10, 50f / 1.8f, BlockUntil: Frames, ReservationHeld: true));
        foreach (int seconds in new[] { 4, 8, 16, 20 })
        {
            cases.Add(new($"front-block-{seconds}s", 10, 50f / 1.8f, BlockUntil: seconds * 60));
            cases.Add(new($"rear-block-{seconds}s", 10, 50f / 1.8f, BlockUntil: seconds * 60, RearBlocked: true));
        }
        cases.Add(new("route-turn", 10, 50f / 1.8f, TurnAfterLeader: 220));
        foreach (float buffer in new[] { 240, 420, 1100 })
            cases.Add(new($"clear-10-buffer-{buffer:G}", 10, 50f / 1.8f, Buffer: buffer));
        foreach (int offset in new[] { 4, 8, 12 })
        {
            cases.Add(new($"clear-10-shard-{offset}", 10, 50f / 1.8f, ShardOffset: offset));
            cases.Add(new($"crawl-shard-{offset}", 10, .01f, .01f, ShardOffset: offset));
            cases.Add(new($"blocked-shard-{offset}", 10, 50f / 1.8f, BlockUntil: 720, ShardOffset: offset));
        }

        cases.Add(new("paced-clear-10", 10, queues[10].LeaderDisplayKmh / 1.8f, RecordedPace: true));
        cases.Add(new("paced-clear-20", 20, queues[20].LeaderDisplayKmh / 1.8f, RecordedPace: true));
        cases.Add(new("paced-crawl-0.01", 10, .01f, .01f, RecordedPace: true));
        cases.Add(new("paced-crawl-1", 10, 1, 1, RecordedPace: true));
        cases.Add(new("paced-front-block-12s", 10, 50f / 1.8f, BlockUntil: 720, RecordedPace: true));
        cases.Add(new("paced-rear-block-16s", 10, 50f / 1.8f, BlockUntil: 960, RearBlocked: true, RecordedPace: true));

        cases.Add(new("paced-retry-clear-10", 10, queues[10].LeaderDisplayKmh / 1.8f, RecordedPace: true, RetryPacedSlots: true));
        cases.Add(new("paced-retry-clear-20", 20, queues[20].LeaderDisplayKmh / 1.8f, RecordedPace: true, RetryPacedSlots: true));
        cases.Add(new("paced-retry-crawl-0.01", 10, .01f, .01f, RecordedPace: true, RetryPacedSlots: true));
        cases.Add(new("paced-retry-crawl-1", 10, 1, 1, RecordedPace: true, RetryPacedSlots: true));
        cases.Add(new("paced-retry-rear-block-16s", 10, 50f / 1.8f, BlockUntil: 960,
            RearBlocked: true, RecordedPace: true, RetryPacedSlots: true));

        cases.Add(new("paced-retry-rear-block-16s-buffer-1100", 10, 50f / 1.8f, Buffer: 1100, BlockUntil: 960,
            RearBlocked: true, RecordedPace: true, RetryPacedSlots: true));

        cases.Add(new("single-rest-restart", 1, queues[1].LeaderDisplayKmh / 1.8f,
            BlockUntil: 1200, ReservationHeld: true));
        cases.Add(new("single-rest-stays-blocked", 1, queues[1].LeaderDisplayKmh / 1.8f,
            BlockUntil: Frames, ReservationHeld: true));

        var results = cases.Select(c =>
        {
            var queue = queues[c.Followers];
            var baseline = Replay(native, old, c, queue, legacyApproach);
            var candidate = Replay(native, current, c, queue, legacyApproach);
            foreach (var run in new[] { baseline, candidate })
            {
                if (c.RecordedPace && run.Admissions > queue.LaunchOpportunityFrames.Length)
                    throw new InvalidDataException($"Replay exceeded the historical launch opportunity cap for {c.Name}.");
                if (run.Returns + run.ReturnedBehind > run.Entries || run.Entries > run.Admissions
                    || run.Admissions + run.Remaining != c.Followers)
                    throw new InvalidDataException($"Queue accounting failed for {c.Name}.");
                int limit = ReferenceEquals(run, candidate) ? current.MaximumOutstanding : old.MaximumOutstanding;
                if (run.PeakOutstanding > limit)
                    throw new InvalidDataException($"Replay exceeded the outstanding pass limit for {c.Name}.");
                if (c.ReservationHeld && c.BlockFrom == 0 && c.BlockUntil == Frames && run.Returns + run.ReturnedBehind != 0)
                    throw new InvalidDataException($"Permanent obstruction allowed a modeled return for {c.Name}.");
            }
            if (current.GuardMetres.HasValue && c.Name == "single-rest-restart"
                && (candidate.Returns + candidate.ReturnedBehind != 1 || candidate.RecoveryRequests != 0 || candidate.RestartsFromHold != 1))
                throw new InvalidDataException("A released terminal gap did not permit the single held car to return.");
            if (current.GuardMetres.HasValue && c.Name == "single-rest-stays-blocked"
                && (candidate.Entries != 1 || candidate.RecoveryRequests != 0 || candidate.HeldAtEnd != 1))
                throw new InvalidDataException("A persistent terminal reservation did not retain the opposing car.");
            return new { inputs = c, baseline, candidate, returnDelta = candidate.Returns - baseline.Returns };
        }).ToArray();
        var crawlProbes = new[] { 0, .001f, .01f, .1f, .5f, 1f, 3f, 50f / 1.8f }.Select(speed =>
        {
            var s = new QueueSituation(LeaderStart, speed, LeaderStart + VerifiedAhead,
                float.PositiveInfinity, 0, 25, false);
            float reserved = legacyApproach ? 20 : native.FrontierReach(speed, 20);
            var c = new QueueCar(LeaderStart + queues[10].Stations[0], speed, Body, RoadSpeed, LeaderStart + 720,
                reserved, NextHomeRear: LeaderStart - Body);
            return new { speed, baseline = old.Entry(s, c, null), candidate = current.Entry(s, c, null) };
        }).ToArray();
        return new
        {
            status = "REPLAY_COMPLETE",
            gameSha256 = native.GameHash,
            baseline = new { sha256 = historicalHash, identity = old.Identity, fixtureSource = recorded.Source, fixtureSha256 = recorded.Sha256,
                physicalControls = recorded.Queues.Select(q => new { q.Followers, q.PhysicalEntries, q.PhysicalReturns, q.PhysicalPeak, q.LaunchOpportunityFrames }) },
            candidate = new { sha256 = QueueNative.Hash(repaired), identity = current.Identity },
            fidelity = new
            {
                executed = "Compiled mod gate and available pure policy kernels, installed Game.dll speed range and braking kernels.",
                modeled = "One-dimensional station movement, 60 Hz integration with staggered 16-frame navigation, native driving-style safety with deterministic supplied seeds, native reachable speed and braking.",
                approachModel = legacyApproach ? "Legacy fixed frontier and home following to the opposing target."
                    : "Native brake distance and steering reach estimate the frontier. Crossing starts at the cut, occupies both lanes and keeps home following through 75 percent of the transition.",
                approximated = "ECS-bound entry and return inputs, buffer and reservation geometry, lane transitions, queue ownership and lane occupancy.",
                inputs = "Initial x deltas and release speeds from the five 0.1.1 radar-OFF runs. Body length, acceleration, straight-road station proxy, rolling buffer and target reservation are supplied. Oncoming uses the archived 936.8992 m spawn offset and supplied road speed.",
                pacing = "Paced cases cap launches at the archived 0.1.1 commitment offsets normalized to first commitment. Both policies independently decide at each slot. Refused slots expire in paced cases and carry forward in paced-retry cases. Unpaced cases expose policy differences under dense launch opportunities.",
                exclusions = "No Unity ECS, Burst, steering, collision resolution, real reservations or repath. Unsafe or terminal blocked attempts remain unresolved. Model counts cannot establish game throughput."
            },
            frames = Frames, frameSeconds = FrameStep, crawlProbes, results
        };
    }

    private static Run Replay(QueueNative native, QueuePolicy policy, Case c, RecordedQueue queue, bool legacyApproach)
    {
        var actors = Enumerable.Range(0, c.Followers).Select(i =>
            new Actor(i, (i * 5 + c.ShardOffset) & 15, LeaderStart + queue.Stations[i],
                c.StartSpeed ?? queue.DisplayKmh[i] / 1.8f)).ToArray();
        var refusals = new Dictionary<string, int>();
        int entries = 0, admitted = 0, returns = 0, behind = 0, peak = 0, peakOutstanding = 0, blocked = 0, recovery = 0, unsafeProgrammed = 0, overlapFrames = 0, openFrames = 0;
        float leader = LeaderStart, roadEnd = LeaderStart + VerifiedAhead, opposingTime = 0, minimumGap = float.PositiveInfinity;
        float opposingFront = LeaderStart + 936.8992f, opposingSpeed = 0;
        bool green = false;
        string? firstRefusal = null;
        int pendingSlots = 0;

        for (int frame = 0; frame < Frames; frame++)
        {
            float time = frame * FrameStep;
            float leaderSpeed = c.RestartAt >= 0 && frame >= c.RestartAt
                ? Math.Min(50f / 1.8f, c.LeaderSpeed + (frame - c.RestartAt) * FrameStep * 3) : c.LeaderSpeed;
            bool scriptedBlock = frame >= c.BlockFrom && frame < c.BlockUntil;
            if ((frame & 15) == 0)
            {
                int newSlots = c.RecordedPace ? queue.LaunchOpportunityFrames.Count(t => t == frame) : actors.Length;
                int launchSlots = newSlots + pendingSlots;
                var front = actors.FirstOrDefault(a => a.Phase == Phase.Waiting);
                if (front != null)
                {
                    float threat = c.Opposing && opposingFront >= leader ? opposingFront - leader : float.PositiveInfinity;
                    var situation = new QueueSituation(leader, leaderSpeed, roadEnd, threat, opposingSpeed,
                        leader - front.Rear, green, LocalOncomingRear: c.Opposing && opposingFront < leader ? opposingFront : null);
                    var gate = policy.Gate(situation);
                    green = gate.Open;
                    if (green) openFrames += 16;
                    if (green && launchSlots > 0)
                    {
                        int outstanding = actors.Count(x => x.Phase is Phase.Approach or Phase.Crossing or Phase.Opposing or Phase.Merging or Phase.Unresolved);
                        foreach (var a in actors)
                        {
                            if (launchSlots == 0 || outstanding >= policy.MaximumOutstanding) break;
                            if (a.Phase != Phase.Waiting) continue;
                            float homeEnd = Math.Min(roadEnd, a.Front + c.Buffer);
                            if (a.Index == 0 && c.TurnAfterLeader is float turn)
                                homeEnd = Math.Min(homeEnd, LeaderStart + turn);
                            // The repaired controller considers active predecessors in the same wave.
                            float? predecessor = actors.Where(other => other.Front > a.Front && other.Phase is Phase.Approach or Phase.Crossing or Phase.Opposing)
                                .OrderBy(other => other.Front).Select(other => (float?)other.Speed).FirstOrDefault();
                            var homeGap = HomeGap(actors, a, leader, leaderSpeed);
                            float reserved = legacyApproach ? c.Reserved : native.FrontierReach(a.Speed, c.Reserved);
                            var decision = policy.GuardMetres.HasValue && actors.Any(other =>
                                other.Front > a.Front && other.Phase == Phase.Opposing && other.HoldingReturn)
                                ? new QueueEntry(false, "recovery-ahead", 0, 0, null, null)
                                : policy.Entry(situation, a.Inputs(homeEnd, reserved, a.Front + homeGap.FrontGap), predecessor);
                            if (decision.Accepted && !policy.Historical && actors.Any(other => other != a && other.OnOpposing
                                && other.Rear < decision.Enter + Body && other.Front > a.Rear))
                                decision = decision with { Accepted = false, Reason = "entry-occupied" };
                            if (!decision.Accepted)
                            {
                                refusals[decision.Reason] = refusals.GetValueOrDefault(decision.Reason) + 1;
                                firstRefusal ??= decision.Reason;
                                if (!policy.Historical && decision.Reason is not ("cannot-gain" or "no-route-home" or "departure-blocked")) break;
                                continue;
                            }
                            a.HomeEnd = homeEnd;
                            a.Entry = decision;
                            a.CommittedAt = time;
                            a.Phase = Phase.Approach;
                            admitted++;
                            outstanding++;
                            peakOutstanding = Math.Max(peakOutstanding, outstanding);
                            launchSlots--;
                        }
                    }
                    else if (!green && launchSlots > 0)
                    {
                        refusals[gate.Reason] = refusals.GetValueOrDefault(gate.Reason) + 1;
                        firstRefusal ??= gate.Reason;
                    }
                }
                pendingSlots = c.RetryPacedSlots ? launchSlots : 0;
            }

            foreach (var a in actors)
            {
                if (a.Shard != (frame & 15) || a.Phase == Phase.Unresolved) continue;
                a.Safety = native.Safety((uint)frame, a.Index + 1);
                if (a.Phase == Phase.Opposing)
                {
                    float boundary = MathF.Ceiling((a.Front + c.Reserved + 5 - a.Entry.Enter) / 10) * 10 + a.Entry.Enter;
                    float landing = boundary + (policy.Historical ? Math.Max(15, a.Speed * .65f) : Math.Max(20, a.Speed * .8f));
                    bool cleared = a.Rear > leader + (policy.Historical ? 15 : 0);
                    bool reservationHeld = scriptedBlock && c.ReservationHeld;
                    bool restart = a.HoldingReturn && a.Speed <= policy.RestSpeed;
                    bool oppositeClear = !restart || ((!c.Opposing || opposingFront < a.Front)
                        && !actors.Any(other => other != a && other.OnOpposing && other.Front >= a.Front
                            && other.Rear < landing + Body));
                    bool canMerge = boundary <= a.Entry.Limit && landing < a.HomeEnd && !reservationHeld && oppositeClear
                        && ReturnNeighbors(actors, a, leader, leaderSpeed, landing, scriptedBlock, c.RearBlocked).All(gap =>
                            policy.CanReturn(a.Inputs(a.HomeEnd, c.Reserved), boundary, landing, gap, restart));
                    if (cleared && canMerge)
                    {
                        a.FirstSafeReturn ??= time;
                        a.RestartedFromHold |= restart;
                        a.MergeLanding = landing;
                        a.Phase = Phase.Merging;
                    }
                    else if (cleared) blocked++;


                    if (a.Phase == Phase.Opposing && a.Entry.ProgrammedExit is float programmed && a.Front >= programmed)
                    {
                        float programmedLanding = a.Entry.ProgrammedLanding!.Value;
                        // Historical fallback is already in the native buffer.
                        // Score modeled overlap separately from its early-return policy.
                        float crossingSeconds = (programmedLanding - a.Rear) / Math.Max(.01f, a.Speed);
                        bool overlapping = ReturnNeighbors(actors, a, leader, leaderSpeed, programmedLanding,
                            scriptedBlock, c.RearBlocked).Any(gap =>
                            Math.Min(gap.FrontGap, gap.FrontGap + (gap.FrontSpeed - a.Speed) * crossingSeconds) <= 0
                            && Math.Min(gap.RearGap, gap.RearGap + (a.Speed - gap.RearSpeed) * crossingSeconds) <= 0);
                        if (reservationHeld || overlapping)
                        {
                            unsafeProgrammed++;
                            a.WasUnsafe = true;
                            a.Phase = Phase.Unresolved;
                            a.Speed = 0;
                            continue;
                        }
                        a.MergeLanding = programmedLanding;
                        a.Phase = Phase.Merging;
                    }
                    else if (a.Phase == Phase.Opposing && a.Entry.ProgrammedExit == null)
                    {
                        float guard = policy.GuardMetres ?? 0;
                        bool guardFits = guard > 0 && a.Entry.Limit - guard >= a.Entry.Enter;
                        float guardedDistance = guardFits ? guard : 0;
                        float unguarded = a.Entry.Limit - guardedDistance;
                        int intervals = (int)MathF.Ceiling(Math.Max(0, unguarded - a.Front) / 10)
                            + (int)MathF.Ceiling(Math.Max(0, a.Entry.Limit - Math.Max(a.Front, unguarded)) / 2);
                        if (policy.CanRetryBehind(intervals, restart) && canMerge)
                        {
                            a.MergeLanding = landing;
                            a.RestartedFromHold |= restart;
                            a.Phase = Phase.Merging;
                        }
                        else if (intervals < 13)
                        {
                            recovery++;
                            a.Phase = Phase.Unresolved;
                            a.Speed = 0;
                            continue;
                        }
                        else if (guardFits && (!c.Opposing || opposingFront < a.Rear)
                            && policy.ShouldHold(unguarded - 5 - a.Front, a.Speed))
                            a.HoldingReturn = true;
                    }
                }
                float wanted = RoadSpeed;
                if (a.OnHome)
                {
                    var sameLane = HomeGap(actors, a, leader, leaderSpeed);
                    if (float.IsFinite(sameLane.FrontGap))
                        wanted = Math.Min(wanted, native.Following(95.55556f, sameLane.FrontSpeed,
                            Math.Max(0, sameLane.FrontGap - .9f), QueueNative.Step + a.Safety));
                }
                if (a.OnOpposing)
                {
                    foreach (var ahead in actors)
                        if (ahead != a && ahead.OnOpposing && ahead.Front > a.Front)
                            wanted = Math.Min(wanted, native.Following(95.55556f, ahead.Speed,
                                Math.Max(0, ahead.Rear - a.Front - .9f), QueueNative.Step + a.Safety));
                }
                float next = native.Reachable(95.55556f, a.Speed, wanted);
                if (a.Phase == Phase.Opposing && a.HoldingReturn)
                {
                    float remaining = a.Entry.Limit - policy.GuardMetres!.Value - 5 - a.Front;
                    next = policy.LimitRecoverySpeed(remaining, next, a.Speed);
                    a.RecoveryCappedSteps++;
                }
                a.Speed = next;
            }

            foreach (var a in actors)
            {
                a.Front += a.Speed * FrameStep;
                if (!legacyApproach && a.Phase == Phase.Approach && a.Front >= a.Entry.Departure!.Value)
                {
                    a.Phase = Phase.Crossing;
                    a.CrossingAt = time;
                }
                if ((a.Phase == Phase.Crossing || legacyApproach && a.Phase == Phase.Approach) && a.Front >= a.Entry.Enter)
                {
                    a.Phase = Phase.Opposing;
                    a.EnteredAt = time;
                    entries++;
                }
                if (a.Phase == Phase.Merging && a.Rear >= a.MergeLanding)
                {
                    a.Phase = Phase.Returned;
                    a.ReturnedAt = time;
                    if (a.Rear > leader) returns++;
                    else behind++;
                }
            }
            int exposed = actors.Count(a => a.OnOpposing);
            peak = Math.Max(peak, exposed);
            opposingTime += exposed * FrameStep;
            float frameGap = float.PositiveInfinity;
            for (int i = 0; i < actors.Length; i++)
            {
                var a = actors[i];
                for (int j = i + 1; j < actors.Length; j++)
                {
                    var b = actors[j];
                    if (a.OnOpposing && b.OnOpposing || a.OnHome && b.OnHome)
                        frameGap = Math.Min(frameGap, Math.Abs(a.Front - b.Front) - Body);
                }
                if (a.OnHome) frameGap = Math.Min(frameGap, Math.Abs(a.Front - leader) - Body);
                if (a.OnOpposing && c.Opposing) frameGap = Math.Min(frameGap, Math.Abs(a.Front - opposingFront) - Body);
            }
            minimumGap = Math.Min(minimumGap, frameGap);
            if (frameGap < 0) overlapFrames++;
            leader += leaderSpeed * FrameStep;
            if (c.Opposing)
            {
                if ((frame & 15) == 0) opposingSpeed = native.Reachable(95.55556f, opposingSpeed, RoadSpeed);
                opposingFront -= opposingSpeed * FrameStep;
            }
        }

        return new Run(policy.Identity, admitted, entries, returns, actors.Count(a => a.Phase == Phase.Waiting), behind,
            actors.Count(a => a.Phase == Phase.Unresolved), actors.Count(a => a.Phase == Phase.Opposing && a.HoldingReturn),
            actors.Count(a => a.RestartedFromHold), peakOutstanding, peak, blocked, recovery,
            unsafeProgrammed, overlapFrames, float.IsFinite(minimumGap) ? minimumGap : null, opposingTime, openFrames,
            firstRefusal, refusals, actors.Select(a => (object)new { a.Index, state = a.Phase.ToString(), a.CommittedAt, a.CrossingAt, a.EnteredAt,
                plannedDeparture = a.CommittedAt.HasValue ? a.Entry.Departure : null,
                plannedOpposingTarget = a.CommittedAt.HasValue ? a.Entry.Enter : (float?)null,
                a.FirstSafeReturn, a.ReturnedAt, a.WasUnsafe, a.RecoveryCappedSteps, a.RestartedFromHold, finalFront = a.Front, finalSpeed = a.Speed,
                heldAtEnd = a.Phase == Phase.Opposing && a.HoldingReturn }).ToArray());
    }

    private static IEnumerable<QueueGap> ReturnNeighbors(Actor[] actors, Actor self, float leader, float leaderSpeed,
        float landing, bool scripted, bool rearBlocked)
    {
        QueueGap FromBody(float front, float speed, float safety = .3f)
            => new(front - Body - self.Front, speed, self.Rear - front, speed, safety);
        yield return FromBody(leader, leaderSpeed);
        foreach (var other in actors)
            if (other != self && other.OnHome)
                yield return FromBody(other.Front, other.Speed, other.Safety);
        // A supplied merge-only neighbor exercises closing gaps independently
        // of the rest of the queue. It is not a physical simulated vehicle.
        if (scripted && rearBlocked) yield return FromBody(self.Rear - 3, self.Speed + 10);
        if (scripted && !rearBlocked) yield return FromBody(landing + .25f, Math.Max(0, self.Speed - 12));
    }

    private static QueueGap HomeGap(Actor[] actors, Actor self, float leader, float leaderSpeed)
    {
        float front = float.PositiveInfinity, rear = float.PositiveInfinity, frontSpeed = 0, rearSpeed = 0;
        void Consider(float frontStation, float speed)
        {
            if (frontStation >= self.Front)
            {
                float gap = frontStation - Body - self.Front;
                if (gap < front) { front = gap; frontSpeed = speed; }
            }
            else
            {
                float gap = self.Rear - frontStation;
                if (gap < rear) { rear = gap; rearSpeed = speed; }
            }
        }
        Consider(leader, leaderSpeed);
        foreach (var other in actors)
            if (other != self && other.OnHome) Consider(other.Front, other.Speed);
        return new QueueGap(front, frontSpeed, rear, rearSpeed);
    }
}
