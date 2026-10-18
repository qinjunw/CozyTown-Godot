using CozyTown.Game;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

public sealed class TownFullDemoPlanTests
{
    private static TownFullDemoPlan Plan() => new("full-demo-input-v1", .05, 180, 18, 60, 90, 60,
        [new("window", Quantity: 1), new("window", Quantity: 2)]);

    [Test]
    public void Window_reserves_eighteen_ordinary_requests_before_async_dispatch_and_keeps_recovery_available()
    {
        var window = new TownFullDemoWindow(Plan(), 1);
        for (int i = 0; i < 18; i++) Assert.That(window.TryAdmit(false), Is.True);
        Assert.That(window.TryAdmit(false), Is.False);
        Assert.That(window.TryAdmit(true), Is.True);
        Assert.That(window.OrdinaryAdmissions, Is.EqualTo(18));
        Assert.That(window.StopReason, Is.EqualTo("ordinary_limit"));
    }

    [Test]
    public void Second_window_saves_at_ninety_seconds_or_early_stop_and_caps_duration()
    {
        var window = new TownFullDemoWindow(Plan(), 2);
        window.Advance(89.95);
        Assert.That(window.SaveDue, Is.False);
        window.Advance(.05);
        Assert.That(window.SaveDue, Is.True);
        window.Advance(100);
        Assert.That(window.ElapsedSeconds, Is.EqualTo(180));
        Assert.That(window.StopReason, Is.EqualTo("duration_limit"));
        Assert.That(window.TryAdmit(false), Is.False);
        var early = new TownFullDemoWindow(Plan(), 2);
        early.Stop("budget_stopped");
        Assert.That(early.SaveDue, Is.True);
    }

    [Test]
    public void Plan_rejects_a_sequence_that_omits_normal_production_and_player_inputs()
        => Assert.Throws<InvalidDataException>(() => Plan().Validate());

    [TestCase("money_limit")]
    [TestCase("total_limit")]
    [TestCase("stage_limit")]
    public void Budget_admission_rejection_stops_new_logical_admissions_with_the_original_reason(string reason)
    {
        var window = new TownFullDemoWindow(Plan(), 1);
        Assert.That(window.RejectAdmission(reason), Is.True);
        Assert.That(window.TryAdmit(false), Is.False);
        Assert.That(window.StopReason, Is.EqualTo(reason));
        Assert.That(window.OrdinaryAdmissions, Is.Zero);
    }

    [Test]
    public void KnownLedgerStopEndsTheWindowWithItsCauseWhileTemporaryOccupancyDoesNot()
    {
        var window = new TownFullDemoWindow(Plan(), 1);
        Assert.That(window.RejectAdmission("concurrency_limit"), Is.False);
        Assert.That(window.RejectAdmission("money_wait"), Is.False);
        Assert.That(window.StopReason, Is.Null);
        Assert.That(window.RejectAdmission("usage_invalid_or_missing", ledgerStopped: true), Is.True);
        Assert.That(window.StopReason, Is.EqualTo("usage_invalid_or_missing"));
        Assert.That(window.TryAdmit(false), Is.False);
    }
}
