using CozyTown.Core.Town;
using NUnit.Framework;

namespace CozyTown.Core.Tests;

[TestFixture]
public sealed class TownReplayBudgetTests
{
    [Test]
    public void ManualPlayRecordingRecomputesItsIndependentManualAdmission()
    {
        var profile = new TownReplayBudgetProfile("manual-play-v1", "cozytown-godot-manual-play-20261017-v1", "CNY",
            5_000_000, 2_110_000, 96, 2, new() { ["manual"] = 96 });
        var initial = Empty() with { BudgetId = profile.BudgetId };
        var budget = new TownReplayBudget(initial, profile);
        var expected = initial with { TotalAttempts = 1, SlotsHeld = 1, ReservedMicros = 2_110_000,
            Attempts = [Reserved(FirstAttempt, "manual-npc") with { Stage = "manual" }] };

        budget.Reserve("manual", "manual-npc", FirstAttempt, false, "accepted", expected);

        Assert.That(TownJson.Serialize(budget.Snapshot()), Is.EqualTo(TownJson.Serialize(expected)));
        budget.Reserve("baseline", "experiment", SecondAttempt, false, "attempt_identity_invalid", expected);
    }

    [TestCase("full-demo-v1", "cozytown-godot-full-demo-20261010-v1")]
    [TestCase("full-demo-v2", "cozytown-godot-full-demo-20261011-v2")]
    public void FullDemoRecordingUsesItsOwnStageAndTotalLimits(string version, string budgetId)
    {
        var profile = new TownReplayBudgetProfile(version, budgetId, "CNY", 5_000_000, 2_110_000, 96, 2,
            new() { ["baseline"] = 40, ["counterfactual"] = 40, ["player_dialogue"] = 8, ["manual"] = 8 });
        var initial = Empty() with { BudgetId = profile.BudgetId };
        var budget = new TownReplayBudget(initial, profile);
        var expected = initial with { TotalAttempts = 1, SlotsHeld = 1, ReservedMicros = 2_110_000,
            Attempts = [Reserved(FirstAttempt, "player-request") with { Stage = "player_dialogue" }] };
        budget.Reserve("player_dialogue", "player-request", FirstAttempt, false, "accepted", expected);
        Assert.That(budget.Snapshot().BudgetId, Is.EqualTo(budgetId));
        Assert.Throws<InvalidDataException>(() => new TownReplayBudget(initial));
        var crossed = profile with { Version = version == "full-demo-v1" ? "full-demo-v2" : "full-demo-v1" };
        Assert.Throws<InvalidDataException>(() => new TownReplayBudget(initial, crossed));
    }

    private const string BudgetId = "cozytown-godot-first-slice-20260920-v1";
    private const string FirstAttempt = "00000000-0000-0000-0000-000000000001";
    private const string SecondAttempt = "00000000-0000-0000-0000-000000000002";
    private const string ThirdAttempt = "00000000-0000-0000-0000-000000000003";
    private static TownReplayBudgetState Empty() => new(BudgetId, "CNY", 5_000_000, 0, 0, 0, 0, null, []);
    private static TownReplayBudgetAttempt Reserved(string attemptId, string requestId, bool recovery = false) =>
        new(attemptId, "baseline", requestId, recovery, "reserved", true, 2_110_000, 0, null, null, null);

    [Test]
    public void AdmissionPreservesRecordedRequestAndAttemptIdentities()
    {
        var budget = new TownReplayBudget(Empty());
        var expected = new TownReplayBudgetState(BudgetId, "CNY", 5_000_000, 0, 2_110_000, 1, 1, null,
            [Reserved(FirstAttempt, "branch-a:decision:1")]);
        budget.Reserve("baseline", "branch-a:decision:1", FirstAttempt, false, "accepted", expected);
        Assert.That(TownJson.Serialize(budget.Snapshot()), Is.EqualTo(TownJson.Serialize(expected)));
    }

    [TestCase("money_limit", "budget.admission")]
    [TestCase("accepted", "budget.reservedMicros")]
    public void DivergentAdmissionOrRecordedStateStopsWithoutChangingTheBudget(string admission, string field)
    {
        var initial = Empty();
        var budget = new TownReplayBudget(initial);
        var expected = new TownReplayBudgetState(BudgetId, "CNY", 5_000_000, 0, 0, 1, 1, null,
            [Reserved(FirstAttempt, "branch-a:decision:1")]);
        var error = Assert.Throws<InvalidDataException>(() =>
            budget.Reserve("baseline", "branch-a:decision:1", FirstAttempt, false, admission, expected));
        Assert.That(error!.Data["field"], Is.EqualTo(field));
        Assert.That(TownJson.Serialize(budget.Snapshot()), Is.EqualTo(TownJson.Serialize(initial)));
    }

    [TestCase("concurrency_limit")]
    [TestCase("money_limit")]
    [TestCase("recovery_limit")]
    [TestCase("logical_request_already_sent")]
    [TestCase("recovery_original_missing_or_wrong_stage")]
    [TestCase("ledger_stopped")]
    [TestCase("attempt_identity_invalid")]
    public void RecordedAdmissionRejectionsFollowSharedBudgetAndRecoveryRules(string reason)
    {
        var attempts = new[] { Reserved(FirstAttempt, "branch-a:decision:1"), Reserved(SecondAttempt, "branch-b:decision:1") };
        var state = Empty() with { ReservedMicros = 4_220_000, TotalAttempts = 2, SlotsHeld = 2, Attempts = attempts };
        string requestId = "branch-c:decision:1";
        bool recovery = false;
        if (reason == "money_limit")
            state = state with { SlotsHeld = 0, Attempts = attempts.Select(a => a with { Status = "unknown", SlotHeld = false }).ToArray() };
        if (reason == "recovery_limit")
        {
            state = state with { Attempts = [attempts[0], attempts[1] with { LogicalRequestId = "branch-a:decision:1", Recovery = true }] };
            requestId = "branch-a:decision:1"; recovery = true;
        }
        if (reason == "logical_request_already_sent") requestId = "branch-a:decision:1";
        if (reason == "recovery_original_missing_or_wrong_stage") recovery = true;
        if (reason == "ledger_stopped") state = state with { StopReason = "account_check_rejected" };
        if (reason == "attempt_identity_invalid") requestId = "";
        var budget = new TownReplayBudget(state);
        budget.Reserve("baseline", requestId, ThirdAttempt, recovery, reason, state);
        Assert.That(TownJson.Serialize(budget.Snapshot()), Is.EqualTo(TownJson.Serialize(state)));
    }

    [TestCase("budget.budgetId")]
    [TestCase("budget.reservedMicros")]
    [TestCase("budget.attempts[0].slotHeld")]
    [TestCase("budget.attempts[0].settledCostMicros")]
    [TestCase("budget.attempts[0].stage")]
    [TestCase("budget.attempts[0].recoveryOriginal")]
    [TestCase("budget.attempts[1].attemptId")]
    public void InvalidStartingBudgetIsRejectedAtTheFirstInconsistentField(string field)
    {
        var attempt = Reserved(FirstAttempt, "branch-a:decision:1");
        var state = Empty() with { ReservedMicros = 2_110_000, TotalAttempts = 1, SlotsHeld = 1, Attempts = [attempt] };
        if (field == "budget.budgetId") state = state with { BudgetId = "different-budget" };
        if (field == "budget.reservedMicros") state = state with { ReservedMicros = 0 };
        if (field == "budget.attempts[0].slotHeld") state = state with { SlotsHeld = 0, Attempts = [attempt with { SlotHeld = false }] };
        if (field == "budget.attempts[0].settledCostMicros") state = state with { Attempts = [attempt with { SettledCostMicros = 1 }] };
        if (field == "budget.attempts[0].stage") state = state with { Attempts = [attempt with { Stage = null! }] };
        if (field == "budget.attempts[0].recoveryOriginal") state = state with { Attempts = [attempt with { Recovery = true }] };
        if (field == "budget.attempts[1].attemptId") state = state with { TotalAttempts = 2, SlotsHeld = 2, ReservedMicros = 4_220_000,
            Attempts = [attempt, attempt with { LogicalRequestId = "branch-b:decision:1" }] };
        var error = Assert.Throws<InvalidDataException>(() => new TownReplayBudget(state));
        Assert.That(error!.Data["field"], Is.EqualTo(field));
    }

    [Test]
    public void SettlementReleasesReservationAndSlotAndDuplicateUsageIsIdempotent()
    {
        var attempt = Reserved(FirstAttempt, "branch-a:decision:1");
        var budget = new TownReplayBudget(Empty() with { ReservedMicros = 2_110_000, TotalAttempts = 1, SlotsHeld = 1, Attempts = [attempt] });
        var settled = attempt with { Status = "settled", SlotHeld = false, ReservedCostMicros = 0,
            SettledCostMicros = 28, PromptTokens = 10, CompletionTokens = 1 };
        var expected = Empty() with { SettledMicros = 28, TotalAttempts = 1, Attempts = [settled] };
        budget.Settle(FirstAttempt, 10, 1, expected);
        budget.Settle(FirstAttempt, 10, 1, expected);
        Assert.That(TownJson.Serialize(budget.Snapshot()), Is.EqualTo(TownJson.Serialize(expected)));
        var error = Assert.Throws<InvalidDataException>(() => budget.Settle(FirstAttempt, 11, 1, expected));
        Assert.That(error!.Data["actual"], Is.EqualTo("settlement_conflict"));
    }

    [TestCase(false, false)]
    [TestCase(true, true)]
    public void UnknownGenerationKeepsCostAndReleasesSlotOnlyWithCompletionEvidence(bool generationEnded, bool stopFurther)
    {
        var attempt = Reserved(FirstAttempt, "branch-a:decision:1");
        var budget = new TownReplayBudget(Empty() with { ReservedMicros = 2_110_000, TotalAttempts = 1, SlotsHeld = 1, Attempts = [attempt] });
        var expected = Empty() with { ReservedMicros = 2_110_000, TotalAttempts = 1, SlotsHeld = generationEnded ? 0 : 1,
            StopReason = stopFurther ? "usage_invalid" : null,
            Attempts = [attempt with { Status = "unknown", SlotHeld = !generationEnded, FailureCode = "usage_invalid" }] };
        budget.MarkUnknown(FirstAttempt, "usage_invalid", generationEnded, stopFurther, expected);
        budget.MarkUnknown(FirstAttempt, "later_error", generationEnded, stopFurther, expected);
        Assert.That(TownJson.Serialize(budget.Snapshot()), Is.EqualTo(TownJson.Serialize(expected)));
        var stopped = expected with { StopReason = expected.StopReason ?? "experiment_stopped" };
        budget.Stop("experiment_stopped", stopped);
        budget.Reserve("baseline", "branch-b:decision:1", SecondAttempt, false, "ledger_stopped", stopped);
    }

    [Test]
    public void ExternalBranchCompletionChangesAdmissionWithoutConsultingAnExternalLedger()
    {
        var first = Reserved(FirstAttempt, "branch-a:decision:1");
        var second = Reserved(SecondAttempt, "branch-b:decision:1");
        var start = Empty() with { ReservedMicros = 4_220_000, TotalAttempts = 2, SlotsHeld = 2, Attempts = [first, second] };
        var budget = new TownReplayBudget(start);
        var released = start with { SlotsHeld = 1, Attempts = [first, second with { Status = "unknown", SlotHeld = false, FailureCode = "usage_missing" }] };
        budget.ApplyExternalState(start, released);
        budget.Reserve("baseline", "branch-c:decision:1", ThirdAttempt, false, "money_limit", released);
        var settled = released with { SettledMicros = 28, ReservedMicros = 2_110_000,
            Attempts = [first, second with { Status = "settled", SlotHeld = false, ReservedCostMicros = 0,
                SettledCostMicros = 28, PromptTokens = 10, CompletionTokens = 1, FailureCode = "usage_missing" }] };
        budget.ApplyExternalState(released, settled);
        var accepted = settled with { ReservedMicros = 4_220_000, TotalAttempts = 3, SlotsHeld = 2,
            Attempts = [settled.Attempts[0], settled.Attempts[1], Reserved(ThirdAttempt, "branch-c:decision:1")] };
        budget.Reserve("baseline", "branch-c:decision:1", ThirdAttempt, false, "accepted", accepted);
        Assert.That(TownJson.Serialize(budget.Snapshot()), Is.EqualTo(TownJson.Serialize(accepted)));
    }

    [Test]
    public void ExternalFactCannotEraseAnAttemptOrUndoASettlement()
    {
        var settled = Reserved(FirstAttempt, "branch-a:decision:1") with { Status = "settled", SlotHeld = false,
            ReservedCostMicros = 0, SettledCostMicros = 28, PromptTokens = 10, CompletionTokens = 1 };
        var start = Empty() with { SettledMicros = 28, TotalAttempts = 1, Attempts = [settled] };
        var budget = new TownReplayBudget(start);
        Assert.Throws<InvalidDataException>(() => budget.ApplyExternalState(start, Empty()));
        var rewritten = start with { SettledMicros = 30, Attempts = [settled with { SettledCostMicros = 30, PromptTokens = 11 }] };
        Assert.Throws<InvalidDataException>(() => budget.ApplyExternalState(start, rewritten));
        Assert.That(TownJson.Serialize(budget.Snapshot()), Is.EqualTo(TownJson.Serialize(start)));
    }
}
