using System.Text.Json;
using CozyTown.Budget;
using NUnit.Framework;

namespace CozyTown.Model.Tests;

public sealed class BudgetCommandTests
{
    [Test]
    public void InitializeCommandCreatesV2OnceAndRejectsIncompleteStopWithoutChangingIt()
    {
        using var fixture = new ModelFixture();
        string path = Path.Combine(fixture.Root, "v2.json");
        using var output = new StringWriter();
        string[] args = ["--initialize", "--profile", "full-demo-v2", "--ledger-file", path];
        Assert.That(BudgetCommand.Run(args, output), Is.Zero);
        using var receipt = JsonDocument.Parse(output.ToString());
        Assert.That(receipt.RootElement.GetProperty("budget").GetProperty("budgetId").GetString(),
            Is.EqualTo("cozytown-godot-full-demo-20261011-v2"));
        output.GetStringBuilder().Clear();
        Assert.That(BudgetCommand.Run(args, output), Is.EqualTo(1));
        Assert.That(output.ToString(), Does.Contain("ledger_already_exists"));
        output.GetStringBuilder().Clear(); args[0] = "--stop";
        Assert.That(BudgetCommand.Run(args, output), Is.EqualTo(1));
        Assert.That(output.ToString(), Does.Contain("budget_arguments_required"));
        var budget = PersistentBudgetLedger.OpenExisting(path, "full-demo-v2").Snapshot();
        Assert.That(budget.TotalAttempts, Is.Zero);
        Assert.That(budget.StopReason, Is.Null);
    }

    [Test]
    public void StopReceiptPreservesSettledAndUnknownAttemptsAndCannotStopAnotherProfile()
    {
        using var fixture = new ModelFixture();
        string oldPath = Path.Combine(fixture.Root, "old.json"), newPath = Path.Combine(fixture.Root, "new.json");
        var old = PersistentBudgetLedger.InitializeNew(oldPath, "full-demo-v1");
        var settled = old.Reserve(ModelStage.Baseline, "settled"); old.Settle(settled.AttemptId, 100, 10);
        var unknown = old.Reserve(ModelStage.Counterfactual, "unknown"); old.MarkUnknown(unknown.AttemptId, "timeout", false);
        var before = old.Snapshot();
        var fresh = PersistentBudgetLedger.InitializeNew(newPath, "full-demo-v2");
        using var output = new StringWriter();
        string[] args = ["--stop", "--profile", "full-demo-v1", "--ledger-file", oldPath, "--reason", "superseded_by_full_demo_v2"];
        Assert.That(BudgetCommand.Run(args, output), Is.Zero);
        using var receipt = JsonDocument.Parse(output.ToString());
        var root = receipt.RootElement;
        Assert.That(root.GetProperty("operation").GetString(), Is.EqualTo("stop"));
        Assert.That(root.GetProperty("profileVersion").GetString(), Is.EqualTo("full-demo-v1"));
        Assert.That(root.GetProperty("before").GetProperty("totalAttempts").GetInt32(), Is.EqualTo(2));
        Assert.That(root.GetProperty("after").GetProperty("settledMicros").GetInt64(), Is.EqualTo(280));
        var after = old.Snapshot();
        Assert.That(JsonSerializer.Serialize(after), Is.EqualTo(JsonSerializer.Serialize(before with { StopReason = after.StopReason })));
        Assert.That(after.StopReason, Is.EqualTo("superseded_by_full_demo_v2"));
        output.GetStringBuilder().Clear(); args[6] = "another_reason";
        Assert.That(BudgetCommand.Run(args, output), Is.Zero);
        Assert.That(JsonSerializer.Serialize(old.Snapshot()), Is.EqualTo(JsonSerializer.Serialize(after)));
        BudgetLedgerTests.AssertCode("ledger_stopped", () => old.Reserve(ModelStage.Manual, "blocked"));
        output.GetStringBuilder().Clear(); args[2] = "full-demo-v2";
        Assert.That(BudgetCommand.Run(args, output), Is.EqualTo(1));
        Assert.That(JsonSerializer.Serialize(old.Snapshot()), Is.EqualTo(JsonSerializer.Serialize(after)));
        Assert.That(fresh.Snapshot().TotalAttempts, Is.Zero);
        Assert.That(fresh.Snapshot().StopReason, Is.Null);
    }
}
