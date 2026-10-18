using System.Text.Json;
using System.Text.Json.Nodes;
using CozyTown.Budget;
using NUnit.Framework;

namespace CozyTown.Model.Tests;

public sealed class ManualPlayBudgetTests
{
    [Test]
    public void ExplicitBudgetCommandCreatesAnIndependentEmptyManualPool()
    {
        using var fixture = new ModelFixture();
        string path = Path.Combine(fixture.Root, "manual-play.json");
        using var output = new StringWriter();

        Assert.That(BudgetCommand.Run(["--initialize", "--profile", "manual-play-v1", "--ledger-file", path], output), Is.Zero,
            output.ToString());

        using var receipt = JsonDocument.Parse(output.ToString());
        Assert.That(receipt.RootElement.GetProperty("profileVersion").GetString(), Is.EqualTo("manual-play-v1"));
        var budget = PersistentBudgetLedger.OpenExisting(path, "manual-play-v1");
        var snapshot = budget.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.BudgetId, Is.EqualTo("cozytown-godot-manual-play-20261017-v1"));
            Assert.That(snapshot.Currency, Is.EqualTo("CNY"));
            Assert.That(snapshot.LimitMicros, Is.EqualTo(5_000_000));
            Assert.That(snapshot.TotalAttempts, Is.Zero);
            Assert.That(snapshot.SettledMicros, Is.Zero);
            Assert.That(snapshot.ReservedMicros, Is.Zero);
            Assert.That(snapshot.SlotsHeld, Is.Zero);
            Assert.That(snapshot.StopReason, Is.Null);
            Assert.That(budget.Profile.TotalLimit, Is.EqualTo(96));
            Assert.That(budget.Profile.ReserveMicros, Is.EqualTo(2_110_000));
            Assert.That(budget.Profile.ConcurrentLimit, Is.EqualTo(2));
            Assert.That(budget.Profile.StageLimits, Is.EquivalentTo(new Dictionary<string, int> { ["manual"] = 96 }));
        });
    }

    [TestCase("")]
    [TestCase(".lock")]
    [TestCase(".previous")]
    public void ManualInitializationRetainsAnyExistingLedgerLockOrBackup(string suffix)
    {
        using var fixture = new ModelFixture();
        string path = Path.Combine(fixture.Root, "manual-play.json");
        File.WriteAllText(path + suffix, "retained-file");
        using var output = new StringWriter();

        Assert.That(BudgetCommand.Run(["--initialize", "--profile", "manual-play-v1", "--ledger-file", path], output), Is.EqualTo(1));

        Assert.That(output.ToString(), Does.Contain("ledger_already_exists"));
        Assert.That(File.ReadAllText(path + suffix), Is.EqualTo("retained-file"));
    }

    [Test]
    public async Task ConcurrentManualInitializationCreatesOnlyOneEmptyPool()
    {
        using var fixture = new ModelFixture();
        string path = Path.Combine(fixture.Root, "manual-play.json");
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            try { PersistentBudgetLedger.InitializeNew(path, "manual-play-v1"); return "created"; }
            catch (ModelException error) { return error.Code; }
        })));

        Assert.That(results.Count(result => result == "created"), Is.EqualTo(1));
        Assert.That(results.Single(result => result != "created"), Is.AnyOf("ledger_already_exists", "ledger_initialization_failed"));
        Assert.That(PersistentBudgetLedger.OpenExisting(path, "manual-play-v1").Snapshot().TotalAttempts, Is.Zero);
    }

    [TestCase("schemaVersion", 1)]
    [TestCase("profileVersion", "full-demo-v2")]
    [TestCase("budgetId", "cozytown-godot-full-demo-20261011-v2")]
    [TestCase("currency", "USD")]
    [TestCase("limitMicros", 5_000_001)]
    [TestCase("reserveMicros", 1)]
    [TestCase("totalLimit", 97)]
    [TestCase("concurrentLimit", 3)]
    [TestCase("manual", 97)]
    public void ManualLedgerRejectsContractDrift(string field, object value)
    {
        using var fixture = new ModelFixture();
        string path = Path.Combine(fixture.Root, "manual-play.json");
        PersistentBudgetLedger.InitializeNew(path, "manual-play-v1");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        var owner = field == "manual" ? document["stageLimits"]! : document;
        owner[field] = value is string text ? JsonValue.Create(text) : JsonValue.Create((int)value);
        File.WriteAllText(path, document.ToJsonString());

        BudgetLedgerTests.AssertCode("ledger_contract_mismatch", () => PersistentBudgetLedger.OpenExisting(path, "manual-play-v1"));
    }

    [Test]
    public void ManualIdentityCannotBorrowOrReleaseAStoppedHistoricalBudget()
    {
        using var fixture = new ModelFixture();
        string oldPath = Path.Combine(fixture.Root, "old.json"), path = Path.Combine(fixture.Root, "manual-play.json");
        var old = PersistentBudgetLedger.InitializeNew(oldPath, "full-demo-v2");
        var unknown = old.Reserve(ModelStage.Manual, "old-unknown");
        old.MarkUnknown(unknown.AttemptId, "transport_timeout", false);
        old.Stop("fingerprint_not_registered");
        string before = JsonSerializer.Serialize(old.Snapshot());
        var fresh = PersistentBudgetLedger.InitializeNew(path, "manual-play-v1");

        BudgetLedgerTests.AssertCode("ledger_lock_invalid", () => PersistentBudgetLedger.OpenExisting(oldPath, "manual-play-v1"));
        BudgetLedgerTests.AssertCode("ledger_lock_invalid", () => PersistentBudgetLedger.OpenExisting(path, "full-demo-v2"));
        BudgetLedgerTests.AssertCode("ledger_stopped", () => old.Reserve(ModelStage.Manual, "old-later"));

        Assert.That(JsonSerializer.Serialize(old.Snapshot()), Is.EqualTo(before));
        Assert.That(fresh.Snapshot().TotalAttempts, Is.Zero);
        Assert.That(fresh.Snapshot().StopReason, Is.Null);
    }
}
