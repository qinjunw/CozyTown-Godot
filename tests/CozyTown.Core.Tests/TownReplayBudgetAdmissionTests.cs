using CozyTown.Core.Town;
using NUnit.Framework;

namespace CozyTown.Core.Tests;

[TestFixture]
public sealed class TownReplayBudgetAdmissionTests
{
    private const string AttemptId = "00000000-0000-0000-0000-000000000001";
    private const string OtherAttemptId = "00000000-0000-0000-0000-000000000002";

    [TestCase(false, "ledger_stopped")]
    [TestCase(true, "ledger_stopped")]
    [TestCase(false, "concurrency_limit")]
    [TestCase(true, "concurrency_limit")]
    public void RejectionWithNullAttemptPreservesExistingBudgetAndStopReason(bool fullDemo, string admission)
    {
        var profile = fullDemo ? TownReplayBudgetProfile.FullDemo : TownReplayBudgetProfile.FirstSlice;
        var initial = Empty(profile) with { Attempts = [Reserved(AttemptId, "first-request")],
            TotalAttempts = 1, SlotsHeld = 1, ReservedMicros = 2_110_000, StopReason = "authentication_failed" };
        if (admission == "concurrency_limit")
            initial = initial with { Attempts = [initial.Attempts[0], Reserved(OtherAttemptId, "other-request")],
                TotalAttempts = 2, SlotsHeld = 2, ReservedMicros = 4_220_000, StopReason = null };
        var recording = Record(initial, profile);
        recording.RecordExternal("budget_reserve", new { stage = "baseline", logicalRequestId = "rejected-request",
            attemptId = (string?)null, recovery = false, admission, after = initial });
        recording.RecordExternal("budget_external", new { before = initial, after = initial });

        var report = TownReplay.Run(recording.Complete());

        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
        Assert.That(report.Boundary, Is.EqualTo(2));
    }

    [TestCase(false, null)]
    [TestCase(true, null)]
    [TestCase(false, "")]
    [TestCase(true, "")]
    public void AcceptedAdmissionStillRequiresAnAttemptIdentity(bool fullDemo, string? attemptId)
    {
        var profile = fullDemo ? TownReplayBudgetProfile.FullDemo : TownReplayBudgetProfile.FirstSlice;
        var initial = Empty(profile);
        var recording = Record(initial, profile);
        var after = initial with { Attempts = [Reserved(AttemptId, "accepted-request")],
            TotalAttempts = 1, SlotsHeld = 1, ReservedMicros = 2_110_000 };
        recording.RecordExternal("budget_reserve", new { stage = "baseline", logicalRequestId = "accepted-request",
            attemptId, recovery = false, admission = "accepted", after });

        var report = TownReplay.Run(recording.Complete());

        Assert.That(report.Passed, Is.False);
        Assert.That(report.Code, Is.EqualTo("divergence"));
        Assert.That(report.Boundary, Is.EqualTo(1));
        Assert.That(report.Field, Is.EqualTo("budget.attempts[0].attemptId"));
    }

    [TestCase(false, "missing")]
    [TestCase(true, "missing")]
    [TestCase(false, "number")]
    [TestCase(true, "number")]
    [TestCase(false, "object")]
    [TestCase(true, "object")]
    public void RejectedAdmissionStillRequiresTheAttemptFieldWithAValidJsonType(bool fullDemo, string shape)
    {
        var profile = fullDemo ? TownReplayBudgetProfile.FullDemo : TownReplayBudgetProfile.FirstSlice;
        var initial = Empty(profile) with { StopReason = "authentication_failed" };
        var recording = Record(initial, profile);
        var input = new Dictionary<string, object?> { ["stage"] = "baseline", ["logicalRequestId"] = "rejected-request",
            ["recovery"] = false, ["admission"] = "ledger_stopped", ["after"] = initial };
        if (shape == "number") input["attemptId"] = 42;
        if (shape == "object") input["attemptId"] = new { value = AttemptId };
        recording.RecordExternal("budget_reserve", input);

        var report = TownReplay.Run(recording.Complete());

        Assert.That(report.Passed, Is.False);
        Assert.That(report.Code, Is.EqualTo("invalid_recording"));
        Assert.That(report.Boundary, Is.EqualTo(1));
        Assert.That(report.Field, Is.EqualTo("input"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NullAttemptDoesNotMakeAnIncorrectAdmissionValid(bool fullDemo)
    {
        var profile = fullDemo ? TownReplayBudgetProfile.FullDemo : TownReplayBudgetProfile.FirstSlice;
        var initial = Empty(profile);
        var recording = Record(initial, profile);
        recording.RecordExternal("budget_reserve", new { stage = "baseline", logicalRequestId = "available-request",
            attemptId = (string?)null, recovery = false, admission = "ledger_stopped", after = initial });

        var report = TownReplay.Run(recording.Complete());

        Assert.That(report.Passed, Is.False);
        Assert.That(report.Code, Is.EqualTo("divergence"));
        Assert.That(report.Boundary, Is.EqualTo(1));
        Assert.That(report.Field, Is.EqualTo("budget.admission"));
        Assert.That(report.Expected, Is.EqualTo("ledger_stopped"));
        Assert.That(report.Actual, Is.EqualTo("accepted"));
    }

    private static TownReplayBudgetState Empty(TownReplayBudgetProfile profile) =>
        new(profile.BudgetId, "CNY", 5_000_000, 0, 0, 0, 0, null, []);

    private static TownReplayBudgetAttempt Reserved(string attemptId, string requestId) =>
        new(attemptId, "baseline", requestId, false, "reserved", true, 2_110_000, 0, null, null, null);

    private static TownRecording Record(TownReplayBudgetState initial, TownReplayBudgetProfile profile)
    {
        var places = new[] { new PlaceDefinition("player.spawn", "Home", new(60, 60), "outside"), new PlaceDefinition("square", "Square", new(100, 60), "outside") };
        var residents = new[] { "mina", "eli", "ren", "sora" }.Select(id => new ResidentDefinition(id, id, "square", "square", "apple", new() { ["apple"] = 3 }, 5, [new("free", 0, 1440, "square", "free", true)])).ToArray();
        var content = TownContent.Create(new(256, 256, places, [], [new("outside", new(0, 0, 256, 256))]), new(residents, [new("apple", "Apple")], 540));
        var recording = new TownRecording(content, new TownWorld(content, budgetId: profile.BudgetId));
        recording.SetInitialBudget(initial, profile);
        return recording;
    }
}
