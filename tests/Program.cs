using System;
using Overtake;
internal static class Program
{
    private static int checks;
    private static void Check(bool result, string name)
    {
        if (!result) throw new Exception(name);
        checks++; Console.WriteLine("PASS " + name);
    }
    private static GateDecision Gate(float road, float opposing, GateState previous = GateState.Red,
        RoadCoverageState coverage = RoadCoverageState.Complete, float opposingSpeed = 50)
        => PassingGate.EvaluateShared(road, opposing, opposingSpeed, 27.8f, true, previous, coverage);
    private static void PassCommitmentChecks()
    {
        static bool Near(float a, float b) => Math.Abs(a - b) < .01f;
        float ret = PassingGate.ReturnSeconds;
        Check(Near(PassCommitment.FinishSeconds(40, 20, 25, 2, 20), 2.5f + (40 - 6.25f) / 5 + ret),
            "finishing a pass ramps to cruise, then holds it until the leader is cleared");
        Check(Near(PassCommitment.FinishSeconds(9, 20, 30, 2, 20), 3 + ret),
            "a short remaining pass finishes during the acceleration ramp");
        Check(Near(PassCommitment.FinishSeconds(-5, 20, 25, 2, 20), ret),
            "a car already clear of the leader only needs to merge");
        Check(float.IsPositiveInfinity(PassCommitment.FinishSeconds(40, 20, 22, 2, 24)),
            "a pass can never finish once the leader is faster than the passing cruise");
        Check(float.IsPositiveInfinity(PassCommitment.FinishSeconds(40, 20, 30, 0, 20))
            && Near(PassCommitment.FinishSeconds(40, 22, 30, 0, 20), 20 + ret),
            "a car with no acceleration left is never assumed to reach its cruise speed");
        Check(float.IsPositiveInfinity(PassCommitment.FinishSeconds(float.NaN, 20, 25, 2, 20)),
            "invalid pass inputs never look finishable");
        Check(Near(PassCommitment.ConflictSeconds(300, 50), 6) && PassCommitment.ConflictSeconds(-1, 50) == 0
            && float.IsPositiveInfinity(PassCommitment.ConflictSeconds(100, 0)),
            "oncoming arrival time follows gap over closing speed");
        Check(!PassCommitment.Fits(10.55f, 6, 0) && PassCommitment.Fits(4, 6, 1) && !PassCommitment.Fits(5.5f, 6, 1),
            "a pass fits only when it finishes, with margin, before oncoming traffic arrives");
        bool hysteresis = true;
        for (float finish = 0; finish < 20; finish += .25f)
            for (float conflict = 0; conflict < 20; conflict += .25f)
                if (PassCommitment.Fits(finish, conflict, PassCommitment.EntryMarginSeconds) && !PassCommitment.Fits(finish, conflict, 0))
                    hysteresis = false;
        Check(hysteresis, "every admitted pass still fits the ongoing test, so it is never cancelled on the next tick");
        Check(PassCommitment.ShouldDropBack(30, 10, 20) && !PassCommitment.ShouldDropBack(10, 30, 20)
            && !PassCommitment.ShouldDropBack(30, 10, PassingDeparture.CrawlSpeed),
            "a car drops back only behind a moving leader and only when tucking in is closer than finishing");
        Check(PassCommitment.DropBackSpeed(20) == 16 && PassCommitment.DropBackSpeed(2) == 0 && PassCommitment.DropBackSpeed(float.NaN) == 0,
            "a car dropping back runs below the leader's speed, never in reverse");
        Check(PassCommitment.CrossesOpposing(1.4f) && !PassCommitment.CrossesOpposing(-.6f) && !PassCommitment.CrossesOpposing(.2f)
            && !PassCommitment.CrossesOpposing(float.NaN),
            "only an exit past the centre line crosses the lane a passer would use");
    }

    private static void OvertakeSpeedChecks()
    {
        Check(OvertakeSpeed.WorthPassing(40, 30) && !OvertakeSpeed.WorthPassing(32, 30) && !OvertakeSpeed.WorthPassing(30, 30),
            "a pass needs a real speed gap over the leader, not a marginal crawl past it");
        Check(OvertakeSpeed.WorthPassing(5, 2) && !OvertakeSpeed.WorthPassing(4, 2),
            "the minimum overtake gap has an absolute floor at low leader speeds");
        Check(OvertakeSpeed.Command(20, 23, 20, 23, false) == 23,
            "a passer held only at the plain lane speed opens to the overtake speed");
        Check(OvertakeSpeed.Command(20, 21, 20, 23, false) == 21,
            "the overtake speed never exceeds the native reachable acceleration step");
        Check(OvertakeSpeed.Command(15, 23, 20, 23, false) == 15,
            "a native braking command is never raised");
        Check(OvertakeSpeed.Command(20, 23, 20, 23, true) == 20,
            "a native blocker ahead keeps the native command");
        Check(OvertakeSpeed.Command(12, 12, 20, 23, false) == 12,
            "while still accelerating toward the lane speed the native step is kept");
        float ceiling = OvertakeSpeed.Ceiling(20, 23, 8, NativeTiming.StepSeconds);
        Check(ceiling > 20 && ceiling < 20 + 8 * NativeTiming.StepSeconds && OvertakeSpeed.Ceiling(20, 20.5f, 8, NativeTiming.StepSeconds) == 20.5f,
            "the overtake ceiling stays within one native braking step of the lane speed");
        // At the ceiling, native braking for a slower piece ahead yields speed - braking * step.
        Check(OvertakeSpeed.Command(ceiling - 8 * NativeTiming.StepSeconds, 30, 20, ceiling, false) == ceiling - 8 * NativeTiming.StepSeconds,
            "a native braking command issued at overtake speed is never mistaken for a lane-speed hold");
        Check(OvertakeSpeed.Command(20, 30, 20, ceiling, false) == ceiling,
            "a lane-speed hold at overtake speed keeps the overtake ceiling");
        Check(OvertakeSpeed.Command(-3, 23, 20, 23, false) == -3 && OvertakeSpeed.Command(float.NaN, 23, 20, 23, false) is float nan && float.IsNaN(nan),
            "reverse and invalid native commands pass through unchanged");
    }
    private static void Main()
    {
        Check(ReturnClearance.MinimumGapDuringMerge(25, -10, 2, 4) == -15,
            "an open current front gap can close before the selected merge finishes");
        Check(ReturnClearance.MinimumGapDuringMerge(20, -8, 1, 3) == -4,
            "a faster rear vehicle can consume the selected merge gap");
        Check(ReturnClearance.MinimumGapDuringMerge(-5, 10, 2, 4) == 15,
            "the selected merge interval uses its opening gap rather than the current overlap");
        Check(ReturnClearance.MinimumGapDuringMerge(30, 0, 2, 4) == 30,
            "equal observed speeds preserve the body gap throughout merging");
        Check(ReturnClearance.MinimumGapDuringMerge(30, -5, 2, 4)
            == ReturnClearance.MinimumGapDuringMerge(100030 - 100000, -5, 2, 4),
            "merge gap depends on road-relative separation rather than world origin");
        Check(ReturnClearance.MinimumGapDuringMerge(-5, 10, 0, 4) == -5,
            "native steering anticipation does not credit a gap that opens only later");
        Check(ReturnClearance.TryMergeDuration(140, 80, 20, out float mergeSeconds) && mergeSeconds == 3,
            "merge duration includes the linked package rear reaching the selected landing");
        Check(ReturnClearance.TryMergeDuration(140, 100, 20, out float shorterMerge) && shorterMerge < mergeSeconds,
            "a longer package reserves more merge observation time");
        Check(!ReturnClearance.TryMergeDuration(140, 80, 0, out _)
            && !ReturnClearance.TryMergeDuration(140, 80, -10, out _),
            "stationary or reversing packages have no invented forward merge arrival");
        Check(ReturnClearance.IsWithinLane(0, .9f, 1.5f)
            && ReturnClearance.IsWithinLane(.4f, .9f, 1.5f),
            "whole-body clearance uses the vehicle's actual lane-relative position");
        Check(!ReturnClearance.IsWithinLane(.7f, .9f, 1.5f)
            && !ReturnClearance.IsWithinLane(0, 1.5f, 1.5f),
            "touching or crossing the lane edge is not physical clearance");
        Check(!ReturnClearance.IsWithinLane(float.NaN, .9f, 1.5f)
            && !ReturnClearance.IsWithinLane(0, float.PositiveInfinity, 1.5f)
            && !ReturnClearance.IsWithinLane(0, .9f, float.PositiveInfinity),
            "invalid body or lane bounds cannot release a lease");
        Check(!ReturnClearance.HasClearedLeader(1273.858f, 1332.066f)
            && !ReturnClearance.HasClearedLeader(1332.066f, 1332.066f),
            "returning behind or alongside the leader is not a completed pass");
        Check(ReturnClearance.HasClearedLeader(1362.77f, 1332.066f)
            && !ReturnClearance.HasClearedLeader(float.NaN, 1332.066f),
            "completed passes require observed full-package clearance");
        var membership = new PoolMembership<int>();
        Check(membership.TryClaim(1, 2), "first follower claims canonical leader");
        Check(membership.TryClaim(1, 3), "rear follower joins same obstruction");
        Check(!membership.TryClaim(2, 4), "claimed follower cannot lead overlapping pool");
        Check(membership.TryOwner(3, out int owner) && owner == 1, "rear membership resolves canonical leader");
        membership.Release(1, 2);
        Check(membership.TryOwner(3, out owner) && owner == 1, "front completion preserves rear ownership");
        membership.Dissolve(1);
        Check(!membership.TryOwner(1, out _) && !membership.TryOwner(3, out _), "dissolution releases all membership");
        membership.TryClaim(7, 8);
        membership.Reset();
        Check(!membership.TryOwner(8, out _), "reset clears ownership");
        DiscoveryEligibilityChecks.Run(Check);
        OvertakeSpeedChecks();
        PassCommitmentChecks();
        CheckPoolTransfer();
        Check(TerminalRecovery.ApplyCap(42, 40, 13, 0) >= 40 - 13 * NativeTiming.StepSeconds,
            "terminal braking respects the native one-step limit");
        Check(TerminalRecovery.ApplyCap(-2, 40, 13, 0) == -2,
            "terminal cap preserves native sign and stronger restriction");
        Check(!TerminalRecovery.ShouldSlow(5, 40, 13) && TerminalRecovery.ShouldSlow(100, 40, 13),
            "terminal hold needs sufficient runway to brake");
        Check(TerminalRecovery.TargetSpeed(0, 13) == 0,
            "terminal command stops before consuming the protected tail");
        Check(TerminalRecovery.TryRestart(25, 2, 8, out float restartTime) && restartTime > 5
            && !TerminalRecovery.TryRestart(25, 0, 8, out _),
            "restart requires a finite native acceleration envelope");
        TerminalRecovery.RestartGaps(50, 20, 10, 20, 25, restartTime, out float front, out float rear);
        Check(front == 25 && rear < 0, "restart does not borrow front or rear movement clearance");
        Check(PassingDeparture.TryPlan(103, 100, 1, 5, 1, 115, out float cut, out float opposingTarget)
            && cut == 104 && opposingTarget == 114,
            "crawl departure keeps native frontier and fits inside live home gap");
        Check(!PassingDeparture.TryPlan(120, 100, 1, 5, 1, 115, out _, out _)
            && !PassingDeparture.TryPlan(103, 100, 1, 5, 1, 110, out _, out _),
            "blocked crawl departure waits rather than truncating a reservation or borrowing space");
        Check(PassingDeparture.TryPlan(120, 100, 25, 5, 25, 110, out cut, out opposingTarget)
            && cut == 128 && opposingTarget == 158,
            "moving departure retains its original transition");
        Check(TerminalRecovery.CanRetryBehind(30, true) && !TerminalRecovery.CanRetryBehind(30, false),
            "stopped terminal lease can check a safe return before its guarded intervals are consumed");
        CheckMotion();
        CheckSharedGate();
        Console.WriteLine(checks + " deterministic checks passed.");
    }
    private static void CheckMotion()
    {
        Check(PassingMotion.TryEstimate(10, 10, 18, 1, 30, out var ramp) && ramp.CompletionSeconds > 11,
            "individual clearance accounts for starting speed and acceleration");
        bool unrestricted = PassingMotion.TryEstimate(14, 14, 25, 3, 60, out var entrant);
        bool following = PassingMotion.TryEstimate(14, 14, 16, 1, 60, out var truck);
        Check(unrestricted && following && truck.CompletionSeconds > entrant.CompletionSeconds * 2,
            "a slower predecessor changes individual execution budget");
        Check(truck.RequiredObservationMetres(14, 25, 5, 25) > entrant.RequiredObservationMetres(14, 25, 5, 25),
            "following constraints require a longer individual window without repainting the pool");
        Check(!PassingMotion.TryEstimate(10, 10, 18, 0, 30, out _),
            "zero acceleration cannot be treated as an instant speed increase");
        Check(PassingMotion.TryEstimate(20, 10, 20, 0, 30, out var momentum)
            && Math.Abs(momentum.ClearSeconds - 5.7f) < .001f && Math.Abs(momentum.CompletionSeconds - 8.2005f) < .001f,
            "existing speed clears a pass without invented acceleration");
        Check(Math.Abs(momentum.ArrivalSeconds(100) - 5) < .001f, "buffer arrival uses existing physical speed");
        Check(PassingMotion.TryEstimate(10, 10, 20, 2, 30, out var mergeBudget)
            && Math.Abs(mergeBudget.ClearSeconds - 8.2f) < .001f && Math.Abs(mergeBudget.CompletionSeconds - 10.2505f) < .001f,
            "individual completion includes the native merge segment");
        float exit = mergeBudget.DistanceAt(mergeBudget.ClearSeconds) + .01f;
        Check(Math.Abs(mergeBudget.ReturnCompletionSeconds(exit) - mergeBudget.CompletionSeconds) < .001f,
            "clearance and return use the same completion duration");
        Check(mergeBudget.TryWithApproachDistance(45, 10, 30, out var delayed)
            && Math.Abs(delayed.ArrivalSeconds(45) - 4.5f) < .001f && delayed.SpeedAt(4.5f) == 10,
            "reserved native approach earns no premature passing acceleration");
        var alias = mergeBudget;
        Check(alias.TryWithApproachDistance(45, 10, 30, out alias)
            && Math.Abs(alias.CompletionSeconds - delayed.CompletionSeconds) < .001f,
            "approach assignment preserves a receiver aliased with its output");
        alias = mergeBudget;
        Check(!alias.TryWithApproachDistance(float.NaN, 10, 30, out alias) && alias.CompletionSeconds == 0,
            "invalid approach clears an aliased output without stale admission");
        alias = mergeBudget;
        Check(!alias.TryWithApproachDistance(-1, 10, 30, out alias) && alias.CompletionSeconds == 0,
            "negative approach cannot reuse an earlier successful fit");
        Check(!delayed.ClearsLeaderAt(exit, 10, 30) && delayed.ClearSeconds > mergeBudget.ClearSeconds,
            "the original clear exit is unsafe after postponed departure");
        float delayedExit = delayed.DistanceAt(delayed.ClearSeconds) + .01f;
        Check(delayed.ClearsLeaderAt(delayedExit, 10, 30)
            && delayed.ReturnCompletionSeconds(delayedExit) > mergeBudget.CompletionSeconds,
            "postponed departure recomputes physical leader clearance");
        Check(PassingMotion.TryEstimate(20, 10, 20, 0, 30, out var coast)
            && coast.ClearsLeaderAt(coast.DistanceAt(coast.ClearSeconds) + .01f, 10, 30),
            "already faster vehicles need no invented acceleration");
        Check(PassingMotion.TryEstimate(0, 10, 20, 2, 30, out var stationary)
            && stationary.TryWithApproachDistance(20, 10, 30, out var stationaryDelay)
            && stationaryDelay.CompletionSeconds > stationary.CompletionSeconds,
            "stationary departure includes retained native approach");
        Check(PassingMotion.TryEstimate(0, 0, 20, 2, 30, out var stopped)
            && stopped.TryWithApproachDistance(38, 0, 30, out var stoppedDelay)
            && stoppedDelay.CompletionSeconds > stopped.CompletionSeconds,
            "stopped queues do not consume retained travel instantly");
        Check(PassingMotion.TryEstimate(0, 0, 30, 2, 25, out var atRest)
            && atRest.TryWithApproachDistance(58, 0, 25, out atRest)
            && PassingMotion.TryEstimate(.01f, .01f, 30, 2, 25, out var creeping)
            && creeping.TryWithApproachDistance(58, .01f, 25, out creeping)
            && Math.Abs(atRest.CompletionSeconds - creeping.CompletionSeconds) < .25f,
            "retained approach is continuous from rest into crawl");
        Check(ramp.ArrivalSeconds(100) > 5
            && Math.Abs(ramp.DistanceAt(ramp.ArrivalSeconds(100)) - 100) < .001f
            && Math.Abs(momentum.DistanceAt(momentum.ArrivalSeconds(100)) - 100) < .001f,
            "ramp and coast distance/arrival conversions agree");
        Check(!momentum.ClearsLeaderAt(80, 10, 30) && momentum.ClearsLeaderAt(120, 10, 30),
            "exit must clear the moving leader rather than its old position");
        Check(PassingMotion.TryEstimate(0, 10, 18, 1, 30, out var startFromRest)
            && startFromRest.ArrivalSeconds(0) == 0 && !float.IsNaN(startFromRest.ArrivalSeconds(1)),
            "stationary initial arrival avoids division by zero");
        float lowCap = mergeBudget.RequiredObservationMetres(10, 20, 5, 20);
        float highCap = mergeBudget.RequiredObservationMetres(10, 20, 5, 80);
        Check(Math.Abs(highCap - lowCap - 60 * (mergeBudget.CompletionSeconds - mergeBudget.ClearSeconds)) < .001f,
            "maximum return speed covers the entire post-clear transition instead of slow catch-up");
        Check(delayed.RequiredObservationMetres(10, 20, 5, 20) > lowCap,
            "native departure delay still consumes individual observation budget");
        Check(mergeBudget.RequiredObservationMetres(10, 20, 63, 20) > lowCap,
            "a longer body package requires more observation clearance");
        foreach (float leader in new[] { 0f, 10f, 28f })
            foreach (float start in new[] { 0f, 20f, 80f })
                foreach (float oncoming in new[] { 0f, 60f })
                {
                    Check(PassingMotion.TryEstimate(start, leader, 45, 1, 30, out var fit, 80),
                        "constructed return-transition prerequisite");
                    float required = fit.RequiredObservationMetres(leader, oncoming, 5, 80);
                    float trajectoryEnd = fit.DistanceAt(fit.CompletionSeconds) - 30 + 5
                        + oncoming * fit.CompletionSeconds;
                    Check(required + .01f >= trajectoryEnd,
                        "observation budget covers actual production trajectory through full return transition");
                }
    }
    private static void CheckSharedGate()
    {
        var green = Gate(2000, float.PositiveInfinity);
        Check(green.State == GateState.Green, "empty verified road opens the shared opportunity");
        Check(Gate(800, float.PositiveInfinity).State == GateState.Green,
            "800m clear-road regression is independent of a follower's full pass forecast");
        Check(!PassingGate.CanGainOnLeader(28, 28) && !PassingGate.CanGainOnLeader(27, 28),
            "physical inability to gain remains individual capability");
        Check(PassingGate.CanGainOnLeader(30, 25), "native capability has no arbitrary percentage penalty");
        var before = green;
        PassingGate.CanGainOnLeader(0, 27.8f);
        Check(green.State == before.State && green.Reason == before.Reason,
            "incapable follower leaves the shared window unchanged");
        var close = Gate(2000, green.RedDistanceMetres - 1);
        Check(close.State == GateState.Red && close.Reason == GateReason.Oncoming,
            "approaching traffic closes the shared window without individual preparation");
        Check(Gate(2000, float.PositiveInfinity, close.State).State == GateState.Green,
            "cleared opposing traffic reopens without a new individual motion estimate");
        Check(PassingGate.EvaluateShared(2000, float.PositiveInfinity, 50, 27, false, GateState.Green).State == GateState.Red,
            "incomplete shared traffic cannot retain GREEN");
        Check(Gate(0, float.PositiveInfinity, GateState.Green, RoadCoverageState.StructuralBoundary).State == GateState.Purple,
            "lost structural road closes an existing opportunity PURPLE");
        Check(green.PassingTravelMetres > 0 && green.PassingTravelMetres < green.YellowDistanceMetres,
            "forward field is positive and distinct from incoming observation horizon");
        Check(green.YellowDistanceMetres > green.RedDistanceMetres,
            "new entry closes before the hard conflict boundary");
        Check(green.EffectiveYellowDistanceMetres == green.YellowDistanceMetres + PassingGate.HysteresisMetres
            && green.EffectiveRedDistanceMetres == green.RedDistanceMetres + PassingGate.HysteresisMetres,
            "opening thresholds include the actual hysteresis");
        var yellow = Gate(2000, (green.YellowDistanceMetres + green.RedDistanceMetres) / 2, GateState.Green);
        Check(yellow.State == GateState.Yellow && !PassingGate.AllowsCommitment(yellow.State),
            "YELLOW closes new commitment");
        Check(yellow.EffectiveYellowDistanceMetres == yellow.YellowDistanceMetres
            && yellow.EffectiveRedDistanceMetres == yellow.RedDistanceMetres,
            "closing thresholds match previous GREEN state");
        Check(Gate(2000, green.RedDistanceMetres - 1, GateState.Yellow).State == GateState.Red,
            "near threat advances YELLOW to RED");
        Check(Gate(2000, green.YellowDistanceMetres + 5, GateState.Red).State == GateState.Yellow,
            "hysteresis prevents immediate reopening");
        Check(Gate(2000, green.YellowDistanceMetres + 5, GateState.Green).State == GateState.Green,
            "hysteresis retains a valid GREEN window");
        Check(Gate(green.RedDistanceMetres - 1, float.PositiveInfinity, coverage: RoadCoverageState.StructuralBoundary).State == GateState.Purple,
            "actual road end closes PURPLE");
        Check(Gate(green.RedDistanceMetres - 1, float.PositiveInfinity).Reason == GateReason.IncompleteCoverage,
            "short cached coverage does not claim a structural road end");
        Check(Gate(800, float.PositiveInfinity, coverage: RoadCoverageState.Pending).State == GateState.Green,
            "pending extension does not close an already sufficient verified entry prefix");
        Check(Gate(20, float.PositiveInfinity, coverage: RoadCoverageState.Pending).State == GateState.Red,
            "insufficient pending coverage closes entry RED");
        Check(Gate(2000, green.RedDistanceMetres - 1, opposingSpeed: 100).State != GateState.Green,
            "faster approaching traffic cannot reopen an unsafe window");
        Check(!PassingGate.AllowsCommitment(GateState.Purple) && !PassingGate.AllowsCommitment(GateState.Red),
            "PURPLE and RED prevent commitment");
        int eligible = 0;
        for (int i = 0; i < 10; i++) if (PassingGate.AllowsCommitment(green.State)) eligible++;
        Check(eligible == 10, "shared GREEN imposes no artificial one-car capacity");
        Check(!PassingGate.IsOpposingThreat(64, true) && PassingGate.IsOpposingThreat(-64, true),
            "known passers remain following traffic but approaching motion remains a threat");
        Check(PassingGate.IsOpposingThreat(64, false) && PassingGate.IsOpposingThreat(0, true),
            "untracked and stalled opposing occupants remain threats");
        var membership = new PoolMembership<int>(); membership.TryClaim(1, 2); membership.TryClaim(1, 3);
        Gate(2000, green.RedDistanceMetres - 1);
        Check(membership.TryOwner(2, out int owner) && owner == 1 && membership.TryOwner(3, out owner) && owner == 1,
            "gate closure does not revoke existing affiliation");
        float landing = ReturnClearance.SelectLanding(120, 20);
        Check(landing == 140 && ReturnClearance.SelectLanding(120, 40) == 152,
            "return candidate selection uses the actual boundary and station speed");
    }
    private static void CheckPoolTransfer()
    {
        var membership = new PoolMembership<int>();
        membership.TryClaim(1, 2);
        membership.TryClaim(1, 3);
        membership.TryClaim(4, 5);
        Check(membership.Count == 5, "two obstruction chains claim five unique vehicles");
        Check(!membership.Transfer(1, 1), "transfer requires distinct leaders");
        Check(!membership.Transfer(1, 9) && !membership.Transfer(9, 4), "transfer requires both existing leaders");
        Check(!membership.Transfer(1, 5) && !membership.Transfer(2, 4), "claimed followers cannot be transfer leaders");
        Check(membership.Count == 5 && membership.TryOwner(2, out int owner) && owner == 1
            && membership.TryOwner(5, out owner) && owner == 4, "rejected transfers leave ownership unchanged");
        Check(membership.Transfer(1, 4), "contiguous chains transfer to forward canonical leader");
        for (int vehicle = 1; vehicle <= 5; vehicle++)
            Check(membership.TryOwner(vehicle, out owner) && owner == 4, "merged vehicle has one canonical owner " + vehicle);
        Check(membership.Count == 5 && !membership.Transfer(1, 4), "merge counts each vehicle once and cannot repeat");
        Check(!membership.TryClaim(1, 6) && !membership.TryClaim(7, 2)
            && membership.Count == 5 && !membership.TryOwner(7, out _), "merged ownership prevents duplicate pools without orphan claims");
        membership.Dissolve(1);
        membership.Release(1, 2);
        Check(membership.Count == 5 && membership.TryOwner(2, out owner) && owner == 4,
            "retired leader cannot dissolve or release transferred ownership");
        membership.Release(4, 2);
        membership.Release(4, 3);
        Check(membership.Count == 3 && !membership.TryOwner(2, out _) && !membership.TryOwner(3, out _),
            "split removes disconnected waiting ownership");
        Check(membership.TryClaim(2, 3) && membership.Count == 5
            && membership.TryOwner(3, out owner) && owner == 2,
            "normal discovery creates independent chain without inherited ownership");
        membership.Dissolve(4);
        Check(membership.Count == 2 && membership.TryOwner(2, out owner) && owner == 2
            && membership.TryOwner(3, out owner) && owner == 2, "old episode dissolution preserves rediscovered chain");
        membership.Reset();
        Check(membership.Count == 0 && !membership.TryOwner(2, out _) && !membership.TryOwner(3, out _),
            "reset releases merged and rediscovered ownership");
    }
}
