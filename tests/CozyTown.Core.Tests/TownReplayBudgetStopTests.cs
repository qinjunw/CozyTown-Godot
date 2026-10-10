using CozyTown.Core.Town;
using NUnit.Framework;

namespace CozyTown.Core.Tests;

[TestFixture]
public sealed class TownReplayBudgetStopTests
{
    private const string AttemptId = "00000000-0000-0000-0000-000000000001";

    [TestCase(false)]
    [TestCase(true)]
    public void StoppedBudgetRejectsAnExternalReservationAtItsFirstBoundary(bool fullDemo)
    {
        var profile = fullDemo ? TownReplayBudgetProfile.FullDemo : TownReplayBudgetProfile.FirstSlice;
        var initial = Empty(profile) with { StopReason = "authentication_failed" };
        var (world, recording) = Record(initial, profile);
        var after = initial with { Attempts = [Reserved()], TotalAttempts = 1, SlotsHeld = 1, ReservedMicros = 2_110_000 };
        recording.RecordExternal("budget_external", new { before = initial, after });
        world.Advance(0, .05);

        var report = TownReplay.Run(recording.Complete());

        Assert.That(report.Passed, Is.False);
        Assert.That(report.Code, Is.EqualTo("divergence"));
        Assert.That(report.Boundary, Is.EqualTo(1));
        Assert.That(report.Command, Is.EqualTo("budget_external"));
        Assert.That(report.Field, Is.EqualTo("budget.external.attempts.length"));
        Assert.That(report.Expected, Is.EqualTo("0"));
        Assert.That(report.Actual, Is.EqualTo("1"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StoppedBudgetAcceptsExistingSettlementAndRetainsItsStopReason(bool fullDemo)
    {
        var profile = fullDemo ? TownReplayBudgetProfile.FullDemo : TownReplayBudgetProfile.FirstSlice;
        var attempt = Reserved();
        var initial = Empty(profile) with { StopReason = "authentication_failed", Attempts = [attempt],
            TotalAttempts = 1, SlotsHeld = 1, ReservedMicros = 2_110_000 };
        var (_, recording) = Record(initial, profile);
        var after = initial with { SettledMicros = 28, ReservedMicros = 0, SlotsHeld = 0,
            Attempts = [attempt with { Status = "settled", SlotHeld = false, ReservedCostMicros = 0,
                SettledCostMicros = 28, PromptTokens = 10, CompletionTokens = 1 }] };
        recording.RecordExternal("budget_external", new { before = initial, after });
        recording.RecordExternal("budget_stop", new { reason = "later_stop", after });

        var report = TownReplay.Run(recording.Complete());

        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
        Assert.That(report.Boundary, Is.EqualTo(2));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void StoppedBudgetAcceptsExistingUnknownCostAndSlotState(bool fullDemo, bool generationEnded)
    {
        var profile = fullDemo ? TownReplayBudgetProfile.FullDemo : TownReplayBudgetProfile.FirstSlice;
        var attempt = Reserved();
        var initial = Empty(profile) with { StopReason = "authentication_failed", Attempts = [attempt],
            TotalAttempts = 1, SlotsHeld = 1, ReservedMicros = 2_110_000 };
        var (_, recording) = Record(initial, profile);
        var after = initial with { SlotsHeld = generationEnded ? 0 : 1,
            Attempts = [attempt with { Status = "unknown", SlotHeld = !generationEnded, FailureCode = "usage_missing" }] };
        recording.RecordExternal("budget_external", new { before = initial, after });
        recording.RecordExternal("budget_unknown", new { attemptId = AttemptId, reason = "usage_missing",
            generationEnded, stopFurther = true, after });

        var report = TownReplay.Run(recording.Complete());

        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
        Assert.That(report.Boundary, Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RunningBudgetAcceptsAnExternalReservation(bool fullDemo)
    {
        var profile = fullDemo ? TownReplayBudgetProfile.FullDemo : TownReplayBudgetProfile.FirstSlice;
        var initial = Empty(profile);
        var (_, recording) = Record(initial, profile);
        var after = initial with { Attempts = [Reserved()], TotalAttempts = 1, SlotsHeld = 1, ReservedMicros = 2_110_000 };
        recording.RecordExternal("budget_external", new { before = initial, after });
        recording.RecordExternal("budget_reserve", new { stage = "baseline", logicalRequestId = "external-request",
            attemptId = AttemptId, recovery = false, admission = "logical_request_already_sent", after });

        var report = TownReplay.Run(recording.Complete());

        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
        Assert.That(report.Boundary, Is.EqualTo(2));
    }

    private static TownReplayBudgetState Empty(TownReplayBudgetProfile profile) =>
        new(profile.BudgetId, "CNY", 5_000_000, 0, 0, 0, 0, null, []);

    private static TownReplayBudgetAttempt Reserved() =>
        new(AttemptId, "baseline", "external-request", false, "reserved", true, 2_110_000, 0, null, null, null);

    private static (TownWorld World, TownRecording Recording) Record(TownReplayBudgetState initial, TownReplayBudgetProfile profile)
    {
        var places = new[] { new PlaceDefinition("player.spawn", "Home", new(60, 60), "outside"), new PlaceDefinition("square", "Square", new(100, 60), "outside") };
        var residents = new[] { "mina", "eli", "ren", "sora" }.Select(id => new ResidentDefinition(id, id, "square", "square", "apple", new() { ["apple"] = 3 }, 5, [new("free", 0, 1440, "square", "free", true)])).ToArray();
        var content = TownContent.Create(new(256, 256, places, [], [new("outside", new(0, 0, 256, 256))]), new(residents, [new("apple", "Apple")], 540));
        var world = new TownWorld(content, budgetId: profile.BudgetId);
        var recording = new TownRecording(content, world);
        recording.SetInitialBudget(initial, profile);
        return (world, recording);
    }
}
