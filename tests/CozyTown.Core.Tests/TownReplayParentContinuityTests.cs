using CozyTown.Core.Town;
using NUnit.Framework;

namespace CozyTown.Core.Tests;

[TestFixture]
public sealed class TownReplayParentContinuityTests
{
    [TestCase("menu")]
    [TestCase("focus")]
    [TestCase("loading")]
    [TestCase("fault")]
    [TestCase("dialogue")]
    public void RestoredHostRecomputesOnlyKnownCurrentHostPauseReasons(string reason)
    {
        var package = HostFork(freeze: true);
        var restored = package with { InitialHost = package.InitialHost! with { PauseReasons = [reason] } };
        Assert.That(TownReplay.Run(restored).Passed, Is.True);
        var unexplained = package with { InitialHost = package.InitialHost! with { PauseReasons = ["unrecorded-cause"] } };
        Assert.That(TownReplay.Run(unexplained).Field, Is.EqualTo("parent.host.pauseReasons.length"));
    }

    [Test]
    public void SavedPauseReasonsMustMatchTheReexecutedParentBeforeCurrentHostRecalculation()
    {
        var package = HostFork();
        var invented = package.InitialHost! with { PauseReasons = ["menu"] };
        var altered = package with { InitialHost = invented, Parent = package.Parent! with { SavedHost = invented } };
        Assert.That(TownReplay.Run(altered).Field, Is.EqualTo("parent.savedHost.pauseReasons.length"));
    }

    [Test]
    public void FrozenRequestsAndRouteCursorCannotBeReplacedDuringRestore()
    {
        var package = HostFork(freeze: true);
        Assert.That(TownReplay.Run(package).Passed, Is.True);
        var noRequest = package.InitialHost! with { FrozenRequests = [] };
        Assert.That(TownReplay.Run(package with { InitialHost = noRequest }).Field, Does.StartWith("parent.host.frozenRequests."));
        var noRoute = package.InitialHost! with { PlayerRoute = null };
        Assert.That(TownReplay.Run(package with { InitialHost = noRoute }).Field, Is.EqualTo("parent.host.playerRoute"));
        var changedCursor = package.InitialHost! with { PlayerRouteCursor = 1 };
        Assert.That(TownReplay.Run(package with { InitialHost = changedCursor }).Field, Is.EqualTo("parent.host.playerRouteCursor"));
    }

    [Test]
    public void MultilevelHostForkRetainsFrozenInputsAndReportsTheOriginalParentBoundary()
    {
        var child = HostFork(freeze: true);
        var grandchildWorld = TownWorld.RestoreCandidate(Content(), TownJson.Serialize(child.InitialSnapshot));
        var grandchildHost = new TownInputHost(Content(), grandchildWorld, child.InitialHost);
        var grandchild = new TownRecording(Content(), grandchildWorld, grandchildHost,
            new(child, child.InitialSnapshot, child.InitialHost)).Complete();
        Assert.That(TownReplay.Run(grandchild).Passed, Is.True);
        var invented = child.InitialHost! with { UnconsumedSeconds = .7 };
        var changedChild = child with { InitialHost = invented, Parent = child.Parent! with { SavedHost = invented } };
        var report = TownReplay.Run(grandchild with { Parent = grandchild.Parent! with { Prefix = changedChild } });
        Assert.That(report.Passed, Is.False);
        Assert.That(report.Field, Is.EqualTo("parent.parent.savedHost.unconsumedSeconds"));
        Assert.That(report.Boundary, Is.EqualTo(4));
        Assert.That(report.Command, Is.EqualTo("host_freeze_request"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StoppedParentCannotGainAnExternalAttemptAtTheFork(bool fullDemo)
    {
        var profile = fullDemo ? TownReplayBudgetProfile.FullDemo : TownReplayBudgetProfile.FirstSlice;
        var stopped = Empty(profile) with { StopReason = "authentication_failed" };
        var report = TownReplay.Run(BudgetFork(profile, stopped, State(stopped, [Unknown("extra", false)])));
        Assert.That(report.Field, Is.EqualTo("parent.budget.external.attempts.length"));
        Assert.That(report.Passed, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MultilevelBudgetForkAcceptsSettlementThenExternalProgressAndRejectsAncestorRewind(bool fullDemo)
    {
        var profile = fullDemo ? TownReplayBudgetProfile.FullDemo : TownReplayBudgetProfile.FirstSlice;
        var original = Unknown("logical", false); var empty = Empty(profile);
        var initial = State(empty, [original]);
        var settled = original with { Status = "settled", SlotHeld = false, ReservedCostMicros = 0,
            SettledCostMicros = 280, PromptTokens = 100, CompletionTokens = 10 };
        var child = BudgetFork(profile, initial, State(empty, [settled]));
        var grandchildWorld = TownWorld.RestoreCandidate(Content(), TownJson.Serialize(child.InitialSnapshot));
        var recording = new TownRecording(Content(), grandchildWorld, parent: new(child, child.InitialSnapshot, null));
        recording.SetInitialBudget(State(empty, [settled, Unknown("logical", true)]), profile);
        var package = recording.Complete();
        Assert.That(TownReplay.Run(package).Passed, Is.True);
        var rewindChild = child with { InitialBudget = empty };
        var report = TownReplay.Run(package with { Parent = package.Parent! with { Prefix = rewindChild } });
        Assert.That(report.Passed, Is.False);
        Assert.That(report.Field, Is.EqualTo("parent.parent.budget.external.attempts.length"));
        Assert.That(report.Boundary, Is.EqualTo(1));
        Assert.That(report.Command, Is.EqualTo("budget_external"));
    }

    [TestCase(false, "missing", "parent.initialBudget")]
    [TestCase(true, "missing", "parent.initialBudget")]
    [TestCase(false, "attempt", "parent.budget.external.attempts.length")]
    [TestCase(true, "attempt", "parent.budget.external.attempts.length")]
    [TestCase(false, "cost", "parent.budget.external.attempts[0]")]
    [TestCase(true, "cost", "parent.budget.external.attempts[0]")]
    [TestCase(false, "unknown", "parent.budget.external.attempts[1].status")]
    [TestCase(true, "unknown", "parent.budget.external.attempts[1].status")]
    [TestCase(false, "slot", "parent.budget.external.attempts[1].slotHeld")]
    [TestCase(true, "slot", "parent.budget.external.attempts[1].slotHeld")]
    [TestCase(false, "recovery", "parent.budget.external.attempts[2].logicalRequestId")]
    [TestCase(true, "recovery", "parent.budget.external.attempts[2].logicalRequestId")]
    public void ForkRejectsMissingOrRewoundBudgetFacts(bool fullDemo, string changed, string field)
    {
        var profile = fullDemo ? TownReplayBudgetProfile.FullDemo : TownReplayBudgetProfile.FirstSlice;
        var empty = Empty(profile);
        var attempts = new[] { Settled("first", 100, 10), Unknown("second", false), Unknown("second", true) };
        if (changed == "slot") attempts[1] = attempts[1] with { SlotHeld = false };
        var parent = State(empty, attempts);
        var childAttempts = TownJson.Clone(attempts);
        switch (changed)
        {
            case "attempt": childAttempts = childAttempts[..^1]; break;
            case "cost": childAttempts[0] = childAttempts[0] with { PromptTokens = 0, CompletionTokens = 0, SettledCostMicros = 0 }; break;
            case "unknown": childAttempts[1] = childAttempts[1] with { Status = "reserved" }; break;
            case "slot": childAttempts[1] = childAttempts[1] with { SlotHeld = true }; break;
            case "recovery": childAttempts[2] = childAttempts[2] with { LogicalRequestId = "first" }; break;
        }
        var package = BudgetFork(profile, parent, changed == "missing" ? null : State(empty, childAttempts));
        var report = TownReplay.Run(package);
        Assert.That(report.Passed, Is.False, TownJson.Serialize(report));
        Assert.That(report.Field, Is.EqualTo(field));
        Assert.That(report.Boundary, Is.EqualTo(1));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void ForkAcceptsExistingSettlementAndOtherBranchProgressWithoutDroppingStop(bool fullDemo, bool stopped)
    {
        var profile = fullDemo ? TownReplayBudgetProfile.FullDemo : TownReplayBudgetProfile.FirstSlice;
        var empty = Empty(profile);
        var original = Unknown("same-request", false);
        var before = State(empty, [original]) with { StopReason = stopped ? "authentication_failed" : null };
        var settled = original with { Status = "settled", SlotHeld = false, ReservedCostMicros = 0, SettledCostMicros = 280,
            PromptTokens = 100, CompletionTokens = 10 };
        var after = State(before, stopped ? [settled] : [settled, Unknown("same-request", true), Unknown("another-branch", false)]);
        Assert.That(TownReplay.Run(BudgetFork(profile, before, after)).Passed, Is.True);
    }

    private static TownReplayBudgetAttempt Settled(string logical, long prompt, long completion) => new(Guid.NewGuid().ToString(),
        "baseline", logical, false, "settled", false, 0, prompt * 2 + completion * 8, prompt, completion, null);
    private static TownReplayBudgetAttempt Unknown(string logical, bool recovery) => new(Guid.NewGuid().ToString(),
        "baseline", logical, recovery, "unknown", true, 2_110_000, 0, null, null, "disconnected");

    private static TownReplayPackage BudgetFork(TownReplayBudgetProfile profile, TownReplayBudgetState parentBudget, TownReplayBudgetState? childBudget)
    {
        var content = Content(); var world = new TownWorld(content, budgetId: profile.BudgetId);
        var recording = new TownRecording(content, world); recording.SetInitialBudget(Empty(profile), profile);
        recording.RecordExternal("budget_external", new { before = Empty(profile), after = parentBudget });
        var saved = world.CaptureSnapshot(); var child = TownWorld.RestoreCandidate(content, TownJson.Serialize(saved));
        var childRecording = new TownRecording(content, child, parent: new(recording.Complete(), saved, null));
        if (childBudget is not null) childRecording.SetInitialBudget(childBudget, profile);
        return childRecording.Complete();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ForkCannotRemoveTheParentBudgetStopAndAdmitAnotherSend(bool fullDemo)
    {
        var profile = fullDemo ? TownReplayBudgetProfile.FullDemo : TownReplayBudgetProfile.FirstSlice;
        var empty = Empty(profile);
        var content = Content(); var world = new TownWorld(content, budgetId: profile.BudgetId);
        var recording = new TownRecording(content, world); recording.SetInitialBudget(empty, profile);
        recording.RecordExternal("budget_stop", new { reason = "authentication_failed", after = empty with { StopReason = "authentication_failed" } });
        var saved = world.CaptureSnapshot(); var child = TownWorld.RestoreCandidate(content, TownJson.Serialize(saved));
        var childRecording = new TownRecording(content, child, parent: new(recording.Complete(), saved, null));
        childRecording.SetInitialBudget(empty, profile);
        var attempt = new TownReplayBudgetAttempt(Guid.NewGuid().ToString(), "baseline", "new-request", false, "reserved", true,
            profile.ReserveMicros, 0, null, null, null);
        childRecording.RecordExternal("budget_reserve", new { stage = "baseline", logicalRequestId = "new-request", attemptId = attempt.AttemptId,
            recovery = false, admission = "accepted", after = State(empty, [attempt]) });
        var report = TownReplay.Run(childRecording.Complete());
        Assert.That(report.Passed, Is.False);
        Assert.That(report.Field, Is.EqualTo("parent.budget.external.stopReason"));
        Assert.That(report.Boundary, Is.EqualTo(1));
        Assert.That(report.Command, Is.EqualTo("budget_stop"));
    }

    private static TownReplayBudgetState Empty(TownReplayBudgetProfile profile) => new(profile.BudgetId, profile.Currency,
        profile.LimitMicros, 0, 0, 0, 0, null, []);
    private static TownReplayBudgetState State(TownReplayBudgetState basis, TownReplayBudgetAttempt[] attempts) => basis with
    {
        Attempts = attempts, TotalAttempts = attempts.Length, SettledMicros = attempts.Sum(a => a.SettledCostMicros),
        ReservedMicros = attempts.Sum(a => a.ReservedCostMicros), SlotsHeld = attempts.Count(a => a.SlotHeld)
    };

    [TestCase("saved", "parent.savedHost")]
    [TestCase("initial", "parent.initialHost")]
    public void HostForkRequiresBothSavedAndRestoredHostDependencies(string missing, string field)
    {
        var package = HostFork();
        var altered = missing == "saved" ? package with { Parent = package.Parent! with { SavedHost = null } }
            : package with { InitialHost = null };
        var report = TownReplay.Run(altered);
        Assert.That(report.Passed, Is.False);
        Assert.That(report.Code, Is.EqualTo("missing_parent_dependency"));
        Assert.That(report.Field, Is.EqualTo(field));
    }

    [Test]
    public void ForkRejectsSavedAndInitialHostInventedTogetherAfterTheParentInputs()
    {
        var package = HostFork();
        Assert.That(TownReplay.Run(package).Passed, Is.True);
        var invented = package.InitialHost! with { UnconsumedSeconds = .7, PlayerRoute = null, PlayerRouteCursor = 0 };
        var altered = package with { InitialHost = invented, Parent = package.Parent! with { SavedHost = invented } };
        var report = TownReplay.Run(altered);
        Assert.That(report.Passed, Is.False);
        Assert.That(report.Field, Is.EqualTo("parent.savedHost.unconsumedSeconds"));
        Assert.That(report.Boundary, Is.EqualTo(2));
        Assert.That(report.Command, Is.EqualTo("host_elapsed"));
        Assert.That(report.InputJson, Does.Contain("0.2"));
    }

    private static TownReplayPackage HostFork(bool freeze = false)
    {
        var content = Content(); var parent = new TownWorld(content);
        if (freeze) parent.Advance(.001, 0);
        var host = new TownInputHost(content, parent);
        var recording = new TownRecording(content, parent, host);
        host.RouteTo(new(160, 60)); host.AcceptElapsed(.2);
        if (freeze)
        {
            var request = parent.GetDecisionRequests()[0];
            string provider = TownJson.Serialize(new { messages = new[] { new { role = "user", content = request.InputJson } } });
            host.FreezeRequest(request.LogicalRequestId, TownJson.Serialize(new { inputJson = provider }));
        }
        var saved = parent.CaptureSnapshot();
        var child = TownWorld.RestoreCandidate(content, TownJson.Serialize(saved));
        var childHost = new TownInputHost(content, child, host.State);
        return new TownRecording(content, child, childHost, new(recording.Complete(), saved, host.State)).Complete();
    }

    internal static TownContent Content() => TownContent.Create(
        new(256, 256, [new("player.spawn", "Home", new(60, 60), "outside"), new("square", "Square", new(100, 60), "outside")],
            [], [new("outside", new(0, 0, 256, 256))]),
        new([new("mina", "Mina", "square", "square", "apple", new() { ["apple"] = 3 }, 5,
            [new("free", 0, 1440, "square", "free", true)])], [new("apple", "Apple")]));
}
