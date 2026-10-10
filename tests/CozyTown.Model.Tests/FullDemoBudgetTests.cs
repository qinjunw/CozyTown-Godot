using NUnit.Framework;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CozyTown.Model.Tests;

[TestFixture]
public sealed class FullDemoBudgetTests
{
    [Test]
    public void ExplicitInitializationCreatesAnIndependentFullDemoBudget()
    {
        using var fixture = new ModelFixture();
        string path = Path.Combine(fixture.Root, "full-demo.json");
        var budget = PersistentBudgetLedger.InitializeNew(path, "full-demo-v1");
        var first = budget.Reserve(ModelStage.Baseline, "first");
        budget.Settle(first.AttemptId, 100, 10);
        var snapshot = PersistentBudgetLedger.OpenExisting(path, "full-demo-v1").Snapshot();
        Assert.That(snapshot.BudgetId, Is.EqualTo("cozytown-godot-full-demo-20261010-v1"));
        Assert.That(snapshot.LimitMicros, Is.EqualTo(5_000_000));
        Assert.That(snapshot.TotalAttempts, Is.EqualTo(1));
        Assert.That(snapshot.SettledMicros, Is.EqualTo(280));
        Assert.That(PersistentBudgetLedger.OpenExisting(fixture.LedgerPath).Snapshot().TotalAttempts, Is.EqualTo(4));
    }

    [Test]
    public void FullDemoStagesStopAtTheirOwnLimitsAndNinetySixTotalAttempts()
    {
        using var fixture = new ModelFixture();
        string path = Path.Combine(fixture.Root, "full-demo.json");
        var budget = PersistentBudgetLedger.InitializeNew(path, "full-demo-v1");
        foreach (var (stage, limit) in new[] { (ModelStage.Baseline, 40), (ModelStage.Counterfactual, 40),
                     (ModelStage.PlayerDialogue, 8), (ModelStage.Manual, 8) })
        {
            for (int i = 0; i < limit; i++)
            {
                var attempt = budget.Reserve(stage, $"{stage}-{i}");
                budget.Settle(attempt.AttemptId, 1, 1);
            }
            BudgetLedgerTests.AssertCode(stage == ModelStage.Manual ? "total_limit" : "stage_limit",
                () => budget.Reserve(stage, "excess-" + stage));
        }
        var restored = PersistentBudgetLedger.OpenExisting(path, "full-demo-v1").Snapshot();
        Assert.That(restored.TotalAttempts, Is.EqualTo(96));
        Assert.That(restored.SettledMicros, Is.EqualTo(960));
        Assert.That(restored.Attempts.Count(a => a.Stage == "player_dialogue"), Is.EqualTo(8));
        Assert.That(restored.Attempts.Count(a => a.Stage == "manual"), Is.EqualTo(8));
        BudgetLedgerTests.AssertCode("attempt_identity_invalid", () =>
            PersistentBudgetLedger.OpenExisting(fixture.LedgerPath).Reserve(ModelStage.Manual, "old-manual"));
    }

    [Test]
    public void InitializationAndOpeningNeverReplaceExistingOrInterruptedBudgetFiles()
    {
        using var fixture = new ModelFixture();
        string path = Path.Combine(fixture.Root, "full-demo.json");
        BudgetLedgerTests.AssertCode("ledger_missing", () => PersistentBudgetLedger.OpenExisting(path, "full-demo-v1"));
        Assert.That(File.Exists(path + ".lock"), Is.False);
        BudgetLedgerTests.AssertCode("ledger_initialization_profile_invalid", () =>
            PersistentBudgetLedger.InitializeNew(path, "first-slice-v1"));
        BudgetLedgerTests.AssertCode("budget_profile_not_registered", () =>
            PersistentBudgetLedger.InitializeNew(path, "full-demo-v2"));
        File.WriteAllText(path + ".lock", "cozytown-godot-full-demo-20261010-v1");
        BudgetLedgerTests.AssertCode("ledger_already_exists", () => PersistentBudgetLedger.InitializeNew(path, "full-demo-v1"));
        BudgetLedgerTests.AssertCode("ledger_missing", () => PersistentBudgetLedger.OpenExisting(path, "full-demo-v1"));
        File.Delete(path + ".lock");
        File.WriteAllText(path + ".previous", "retained-backup");
        BudgetLedgerTests.AssertCode("ledger_already_exists", () => PersistentBudgetLedger.InitializeNew(path, "full-demo-v1"));
        Assert.That(File.ReadAllText(path + ".previous"), Is.EqualTo("retained-backup"));
        File.Delete(path + ".previous");
        var budget = PersistentBudgetLedger.InitializeNew(path, "full-demo-v1");
        var attempt = budget.Reserve(ModelStage.Manual, "spent"); budget.Settle(attempt.AttemptId, 100, 10);
        BudgetLedgerTests.AssertCode("ledger_already_exists", () => PersistentBudgetLedger.InitializeNew(path, "full-demo-v1"));
        Assert.That(budget.Snapshot().SettledMicros, Is.EqualTo(280));
        File.WriteAllText(path, "{partial");
        BudgetLedgerTests.AssertCode("ledger_invalid", () => PersistentBudgetLedger.OpenExisting(path, "full-demo-v1"));
        Assert.That(File.ReadAllText(path), Is.EqualTo("{partial"));
    }

    [TestCase("profileVersion", "full-demo-v2")]
    [TestCase("budgetId", "other-budget")]
    [TestCase("currency", "USD")]
    [TestCase("schemaVersion", 1)]
    [TestCase("totalLimit", 97)]
    [TestCase("limitMicros", 5_000_001)]
    [TestCase("reserveMicros", 1)]
    [TestCase("concurrentLimit", 3)]
    [TestCase("manual", 9)]
    public void FullDemoConfigurationDriftFailsClosed(string field, object value)
    {
        using var fixture = new ModelFixture(); string path = Path.Combine(fixture.Root, "full-demo.json");
        PersistentBudgetLedger.InitializeNew(path, "full-demo-v1");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        if (field == "manual") document["stageLimits"]![field] = (int)value;
        else document[field] = value is string text ? JsonValue.Create(text) : JsonValue.Create((int)value);
        File.WriteAllText(path, document.ToJsonString());
        BudgetLedgerTests.AssertCode("ledger_contract_mismatch", () => PersistentBudgetLedger.OpenExisting(path, "full-demo-v1"));
    }

    [Test]
    public void ProfilesCannotOpenEachOthersLedgerOrSilentlySelectLargerLimits()
    {
        using var fixture = new ModelFixture(); string path = Path.Combine(fixture.Root, "full-demo.json");
        PersistentBudgetLedger.InitializeNew(path, "full-demo-v1");
        BudgetLedgerTests.AssertCode("ledger_lock_invalid", () => PersistentBudgetLedger.OpenExisting(path));
        BudgetLedgerTests.AssertCode("ledger_lock_invalid", () => PersistentBudgetLedger.OpenExisting(fixture.LedgerPath, "full-demo-v1"));
        BudgetLedgerTests.AssertCode("budget_profile_not_registered", () => PersistentBudgetLedger.OpenExisting(path, "arbitrary"));
        File.WriteAllText(path + ".lock", "cozytown-godot-first-slice-20260920-v1");
        BudgetLedgerTests.AssertCode("ledger_contract_mismatch", () => PersistentBudgetLedger.OpenExisting(path));
        Assert.That(PersistentBudgetLedger.OpenExisting(fixture.LedgerPath).Snapshot().TotalAttempts, Is.EqualTo(4));
    }

    [Test]
    public void FullDemoUnknownAttemptsShareMoneyAndPhysicalSlotsAcrossAllStages()
    {
        using var fixture = new ModelFixture(); string path = Path.Combine(fixture.Root, "full-demo.json");
        var budget = PersistentBudgetLedger.InitializeNew(path, "full-demo-v1");
        var npc = budget.Reserve(ModelStage.Baseline, "npc"); budget.MarkUnknown(npc.AttemptId, "transport_timeout", false);
        var player = budget.Reserve(ModelStage.PlayerDialogue, "player");
        BudgetLedgerTests.AssertCode("concurrency_limit", () => budget.Reserve(ModelStage.Manual, "manual"));
        var reopened = PersistentBudgetLedger.OpenExisting(path, "full-demo-v1");
        Assert.That(reopened.Snapshot().ReservedMicros, Is.EqualTo(4_220_000));
        Assert.That(reopened.Snapshot().SlotsHeld, Is.EqualTo(2));
        reopened.MarkUnknown(player.AttemptId, "usage_invalid_or_missing", true, stopFurther: true);
        Assert.That(budget.Snapshot().SlotsHeld, Is.EqualTo(1));
        Assert.That(budget.Snapshot().ReservedMicros, Is.EqualTo(4_220_000));
        BudgetLedgerTests.AssertCode("ledger_stopped", () => budget.Reserve(ModelStage.Counterfactual, "later"));
        budget.Settle(npc.AttemptId, 100, 10);
        Assert.That(reopened.Snapshot().SettledMicros, Is.EqualTo(280));
        Assert.That(reopened.Snapshot().ReservedMicros, Is.EqualTo(2_110_000));
        Assert.That(reopened.Snapshot().StopReason, Is.EqualTo("usage_invalid_or_missing"));
    }

    [Test]
    public void FullDemoMoneyAndRecoverySurviveReopeningWithoutTransferringStages()
    {
        using var fixture = new ModelFixture(); string path = Path.Combine(fixture.Root, "full-demo.json");
        var budget = PersistentBudgetLedger.InitializeNew(path, "full-demo-v1");
        var original = budget.Reserve(ModelStage.PlayerDialogue, "same-player");
        budget.Settle(original.AttemptId, 1_048_576, 512);
        BudgetLedgerTests.AssertCode("recovery_original_missing_or_wrong_stage", () => budget.Reserve(ModelStage.Manual, "same-player", true));
        var restored = PersistentBudgetLedger.OpenExisting(path, "full-demo-v1");
        var recovery = restored.Reserve(ModelStage.PlayerDialogue, "same-player", true);
        BudgetLedgerTests.AssertCode("money_limit", () => restored.Reserve(ModelStage.Manual, "manual"));
        budget.Settle(recovery.AttemptId, 100, 10);
        BudgetLedgerTests.AssertCode("recovery_limit", () => restored.Reserve(ModelStage.PlayerDialogue, "same-player", true));
        Assert.That(restored.Snapshot().TotalAttempts, Is.EqualTo(2));
        Assert.That(restored.Snapshot().SettledMicros, Is.EqualTo(2_101_528));
        Assert.That(restored.Snapshot().Attempts.All(a => a.Stage == "player_dialogue"), Is.True);
    }

    [TestCase("initialize")]
    [TestCase("manual-original")]
    [TestCase("recovery")]
    public async Task TwoProcessesCannotDuplicateInitializationSlotsOrRecovery(string mode)
    {
        using var fixture = new ModelFixture(); string path = Path.Combine(fixture.Root, "full-demo.json");
        if (mode != "initialize")
        {
            var budget = PersistentBudgetLedger.InitializeNew(path, "full-demo-v1");
            var original = budget.Reserve(ModelStage.PlayerDialogue, "shared-original");
            budget.MarkUnknown(original.AttemptId, "process_lost", false);
        }
        string gate = Path.Combine(fixture.Root, "gate");
        using var one = StartChild(fixture, path, "one", gate, mode);
        using var two = StartChild(fixture, path, "two", gate, mode);
        try
        {
            await WaitForFile(Path.Combine(fixture.Root, "one"));
            await WaitForFile(Path.Combine(fixture.Root, "two"));
            File.WriteAllText(gate, "go");
            await Task.WhenAll(one.WaitForExitAsync(), two.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.That(new[] { one.ExitCode, two.ExitCode }, Is.EquivalentTo(new[] { 0, 10 }));
            var snapshot = PersistentBudgetLedger.OpenExisting(path, "full-demo-v1").Snapshot();
            Assert.That(snapshot.TotalAttempts, Is.EqualTo(mode == "initialize" ? 0 : 2));
            Assert.That(snapshot.SlotsHeld, Is.EqualTo(mode == "initialize" ? 0 : 2));
            Assert.That(snapshot.Attempts.Count(a => a.Recovery), Is.EqualTo(mode == "recovery" ? 1 : 0));
        }
        finally { Kill(one); Kill(two); }
    }

    [Test]
    public async Task KilledFullDemoRecoveryRetainsItsStageCostAndSlot()
    {
        using var fixture = new ModelFixture(); string path = Path.Combine(fixture.Root, "full-demo.json");
        var budget = PersistentBudgetLedger.InitializeNew(path, "full-demo-v1");
        var original = budget.Reserve(ModelStage.PlayerDialogue, "shared-original"); budget.Settle(original.AttemptId, 100, 10);
        string gate = Path.Combine(fixture.Root, "gate"); File.WriteAllText(gate, "go");
        using var child = StartChild(fixture, path, "killed", gate, "hold");
        try { await WaitForFile(Path.Combine(fixture.Root, "killed.reserved")); }
        finally { Kill(child); }
        await child.WaitForExitAsync();
        var restored = PersistentBudgetLedger.OpenExisting(path, "full-demo-v1");
        Assert.That(restored.Snapshot().ReservedMicros, Is.EqualTo(2_110_000));
        Assert.That(restored.Snapshot().SlotsHeld, Is.EqualTo(1));
        Assert.That(restored.Snapshot().SettledMicros, Is.EqualTo(280));
        BudgetLedgerTests.AssertCode("recovery_limit", () => restored.Reserve(ModelStage.PlayerDialogue, "shared-original", true));
    }

    private static Process StartChild(ModelFixture fixture, string path, string ready, string gate, string mode)
    {
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (string argument in new[] { "--child-reserve", path, Path.Combine(fixture.Root, ready), gate, mode, "full-demo-v1" })
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
}
