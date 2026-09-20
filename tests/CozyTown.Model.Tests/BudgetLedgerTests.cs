using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace CozyTown.Model.Tests;

[TestFixture]
public sealed class BudgetLedgerTests
{
    [Test]
    public void ExistingProbeConsumptionSurvivesOpeningAndSettlement()
    {
        using var fixture = new ModelFixture();
        var ledger = PersistentBudgetLedger.OpenExisting(fixture.LedgerPath);
        Assert.That(ledger.Snapshot().SettledMicros, Is.EqualTo(2212));
        Assert.That(ledger.Snapshot().TotalAttempts, Is.EqualTo(4));
        var attempt = ledger.Reserve(ModelStage.Baseline, "test-original");
        ledger.Settle(attempt.AttemptId, 100, 10);
        ledger.Settle(attempt.AttemptId, 100, 10);
        Assert.That(ledger.Snapshot().SettledMicros, Is.EqualTo(2492));
        Assert.That(ledger.Snapshot().TotalAttempts, Is.EqualTo(5));
        Assert.That(ledger.Snapshot().SlotsHeld, Is.Zero);
        AssertCode("settlement_conflict", () => ledger.Settle(attempt.AttemptId, 101, 10));
        AssertCode("logical_request_already_sent", () => ledger.Reserve(ModelStage.Baseline, "test-original"));
    }

    [Test]
    public void MissingOrCorruptLedgerAndMissingMarkerNeverInitialize()
    {
        using var fixture = new ModelFixture();
        string missing = Path.Combine(fixture.Root, "missing.json");
        AssertCode("ledger_missing", () => PersistentBudgetLedger.OpenExisting(missing));
        Assert.That(File.Exists(missing + ".lock"), Is.False);
        File.Delete(fixture.LedgerPath + ".lock");
        AssertCode("ledger_lock_missing", () => PersistentBudgetLedger.OpenExisting(fixture.LedgerPath));
        File.WriteAllText(fixture.LedgerPath + ".lock", PersistentBudgetLedger.BudgetId);
        File.WriteAllText(fixture.LedgerPath, "{broken");
        AssertCode("ledger_invalid", () => PersistentBudgetLedger.OpenExisting(fixture.LedgerPath));
        Assert.That(File.ReadAllText(fixture.LedgerPath), Is.EqualTo("{broken"));
    }

    [Test]
    public void UnknownTransportRetainsCostAndSlotButAllowsOneOtherAttempt()
    {
        using var fixture = new ModelFixture();
        var ledger = PersistentBudgetLedger.OpenExisting(fixture.LedgerPath);
        var first = ledger.Reserve(ModelStage.Baseline, "first");
        ledger.MarkUnknown(first.AttemptId, "transport_timeout", false);
        var reopened = PersistentBudgetLedger.OpenExisting(fixture.LedgerPath);
        Assert.That(reopened.Snapshot().StopReason, Is.Null);
        reopened.Reserve(ModelStage.Counterfactual, "second");
        Assert.That(reopened.Snapshot().ReservedMicros, Is.EqualTo(4_220_000));
        AssertCode("concurrency_limit", () => ledger.Reserve(ModelStage.Baseline, "third"));
        ledger.Settle(first.AttemptId, 10, 1);
        Assert.That(reopened.Snapshot().SlotsHeld, Is.EqualTo(1));
        Assert.That(reopened.Snapshot().ReservedMicros, Is.EqualTo(2_110_000));
    }

    [Test]
    public void GenerationTerminalWithoutUsageReleasesOnlySlotAndStops()
    {
        using var fixture = new ModelFixture();
        var ledger = PersistentBudgetLedger.OpenExisting(fixture.LedgerPath);
        var attempt = ledger.Reserve(ModelStage.Baseline, "first");
        ledger.MarkUnknown(attempt.AttemptId, "usage_invalid_or_missing", true, stopFurther: true);
        var snapshot = ledger.Snapshot();
        Assert.That(snapshot.SlotsHeld, Is.Zero);
        Assert.That(snapshot.ReservedMicros, Is.EqualTo(2_110_000));
        AssertCode("ledger_stopped", () => ledger.Reserve(ModelStage.Baseline, "second"));
    }

    [Test]
    public void StageAndTotalLimitsCarryForwardFromFourProbeCalls()
    {
        using var fixture = new ModelFixture();
        var ledger = PersistentBudgetLedger.OpenExisting(fixture.LedgerPath);
        foreach (var stage in new[] { ModelStage.Baseline, ModelStage.Counterfactual })
        {
            for (int i = 0; i < 22; i++)
            {
                var item = ledger.Reserve(stage, $"{stage}-{i}");
                ledger.Settle(item.AttemptId, 1, 1);
            }
            AssertCode(stage == ModelStage.Baseline ? "stage_limit" : "total_limit",
                () => ledger.Reserve(stage, "excess-" + stage));
        }
        Assert.That(ledger.Snapshot().TotalAttempts, Is.EqualTo(48));
        Assert.That(ledger.Snapshot().SettledMicros, Is.EqualTo(2652));
    }

    [Test]
    public void SettlementAndOutstandingReserveTogetherEnforceMoneyLimit()
    {
        using var fixture = new ModelFixture();
        var ledger = PersistentBudgetLedger.OpenExisting(fixture.LedgerPath);
        var first = ledger.Reserve(ModelStage.Baseline, "first");
        ledger.Settle(first.AttemptId, 1_048_576, 512);
        ledger.Reserve(ModelStage.Baseline, "second");
        AssertCode("money_limit", () => ledger.Reserve(ModelStage.Counterfactual, "third"));
    }

    [Test]
    public void RecoveryMustUseOriginalStageAndCannotRefreshAcrossBranches()
    {
        using var fixture = new ModelFixture();
        var ledger = PersistentBudgetLedger.OpenExisting(fixture.LedgerPath);
        AssertCode("recovery_original_missing_or_wrong_stage", () => ledger.Reserve(ModelStage.Baseline, "missing", true));
        var first = ledger.Reserve(ModelStage.Baseline, "same-logical-id");
        ledger.Settle(first.AttemptId, 10, 1);
        AssertCode("recovery_original_missing_or_wrong_stage", () => ledger.Reserve(ModelStage.Counterfactual, "same-logical-id", true));
        var recovery = ledger.Reserve(ModelStage.Baseline, "same-logical-id", true);
        ledger.Settle(recovery.AttemptId, 10, 1);
        AssertCode("recovery_limit", () => PersistentBudgetLedger.OpenExisting(fixture.LedgerPath).Reserve(ModelStage.Baseline, "same-logical-id", true));
    }

    [Test]
    public async Task TwoProcessesCompetingForRecoveryReserveExactlyOneAttempt()
    {
        using var fixture = new ModelFixture();
        var ledger = PersistentBudgetLedger.OpenExisting(fixture.LedgerPath);
        var first = ledger.Reserve(ModelStage.Baseline, "shared-original");
        ledger.MarkUnknown(first.AttemptId, "process_lost", false);
        string gate = Path.Combine(fixture.Root, "gate");
        using var one = StartChild(fixture, "one", gate, "recovery");
        using var two = StartChild(fixture, "two", gate, "recovery");
        try
        {
            await WaitForFile(Path.Combine(fixture.Root, "one"));
            await WaitForFile(Path.Combine(fixture.Root, "two"));
            File.WriteAllText(gate, "go");
            await Task.WhenAll(one.WaitForExitAsync(), two.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.That(new[] { one.ExitCode, two.ExitCode }, Is.EquivalentTo(new[] { 0, 10 }));
            var result = PersistentBudgetLedger.OpenExisting(fixture.LedgerPath).Snapshot();
            Assert.That(result.TotalAttempts, Is.EqualTo(6));
            Assert.That(result.Attempts.Count(a => a.Recovery), Is.EqualTo(1));
            Assert.That(result.ReservedMicros, Is.EqualTo(4_220_000));
            Assert.That(result.SettledMicros, Is.EqualTo(2212));
        }
        finally { Kill(one); Kill(two); }
    }

    [Test]
    public async Task KilledProcessKeepsReservationAndRecoveryQuotaAfterRestart()
    {
        using var fixture = new ModelFixture();
        var ledger = PersistentBudgetLedger.OpenExisting(fixture.LedgerPath);
        var first = ledger.Reserve(ModelStage.Baseline, "shared-original");
        ledger.Settle(first.AttemptId, 10, 1);
        string gate = Path.Combine(fixture.Root, "gate");
        File.WriteAllText(gate, "go");
        using var child = StartChild(fixture, "crash", gate, "hold");
        try { await WaitForFile(Path.Combine(fixture.Root, "crash.reserved")); }
        finally { Kill(child); }
        await child.WaitForExitAsync();
        var reopened = PersistentBudgetLedger.OpenExisting(fixture.LedgerPath);
        Assert.That(reopened.Snapshot().ReservedMicros, Is.EqualTo(2_110_000));
        Assert.That(reopened.Snapshot().SlotsHeld, Is.EqualTo(1));
        AssertCode("recovery_limit", () => reopened.Reserve(ModelStage.Baseline, "shared-original", true));
    }

    private static Process StartChild(ModelFixture fixture, string ready, string gate, string mode)
    {
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (string argument in new[] { "--child-reserve", fixture.LedgerPath, Path.Combine(fixture.Root, ready), gate, mode })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("child_start_failed");
    }

    private static async Task WaitForFile(string path)
    {
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(20)) Assert.Fail("Child process did not reach barrier.");
            await Task.Delay(10);
        }
    }
    private static void Kill(Process process) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    internal static void AssertCode(string code, TestDelegate action) =>
        Assert.That(Assert.Throws<ModelException>(action)!.Code, Is.EqualTo(code));
}

internal sealed class ModelFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "CozyTown.Model.Tests", Guid.NewGuid().ToString("N"));
    internal string LedgerPath => Path.Combine(Root, "budget.json");
    internal string CredentialPath => Path.Combine(Root, "fake-credentials.json");
    internal const string FakeKey = "fake-offline-key";

    internal ModelFixture()
    {
        Directory.CreateDirectory(Root);
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "probe-report.json")));
        File.WriteAllText(LedgerPath, source.RootElement.GetProperty("ledger").GetRawText());
        File.WriteAllText(LedgerPath + ".lock", PersistentBudgetLedger.BudgetId);
        File.WriteAllText(CredentialPath, JsonSerializer.Serialize(new { entry = new { Todo = "CozyTown", testingAPIKey = FakeKey } }));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(Root);
        string expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CozyTown.Model.Tests")) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("test_cleanup_scope_invalid");
        Directory.Delete(full, recursive: true);
    }
}
