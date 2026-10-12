using NUnit.Framework;

namespace CozyTown.Model.Tests;

public sealed class BudgetLockTests
{
    [TestCase("first-slice-v1")]
    [TestCase("full-demo-v1")]
    [TestCase("full-demo-v2")]
    public async Task LedgerStillMissingAfterTheWriterReleasesItsLockIsRejectedWithoutRestoringHistory(string profile)
    {
        using var fixture = new ModelFixture();
        string path = profile == "first-slice-v1" ? fixture.LedgerPath : Path.Combine(fixture.Root, "full.json");
        var ledger = profile == "first-slice-v1" ? PersistentBudgetLedger.OpenExisting(path, profile)
            : PersistentBudgetLedger.InitializeNew(path, profile);
        var attempt = ledger.Reserve(ModelStage.Baseline, "unknown");
        ledger.MarkUnknown(attempt.AttemptId, "transport_timeout", false);
        byte[] previous = File.ReadAllBytes(path + ".previous");
        using var writerLock = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        File.Move(path, path + ".lost");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Task.Run(() =>
        {
            started.SetResult();
            ledger.Reserve(ModelStage.Counterfactual, "must-not-be-created");
        });
        bool waited = false;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            waited = await Task.WhenAny(operation, Task.Delay(200)) != operation;
        }
        finally { writerLock.Dispose(); }
        var error = Assert.ThrowsAsync<ModelException>(async () => await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.That(waited, Is.True);
        Assert.That(error!.Code, Is.EqualTo("ledger_missing"));
        Assert.That(File.Exists(path), Is.False);
        Assert.That(File.ReadAllBytes(path + ".previous"), Is.EqualTo(previous));
        using (new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        BudgetLedgerTests.AssertCode("ledger_missing", () => PersistentBudgetLedger.OpenExisting(path, profile));
        Assert.That(File.Exists(path), Is.False);
    }

    [TestCase("first-slice-v1", false)]
    [TestCase("first-slice-v1", true)]
    [TestCase("full-demo-v1", false)]
    [TestCase("full-demo-v1", true)]
    [TestCase("full-demo-v2", false)]
    [TestCase("full-demo-v2", true)]
    public async Task ReaderWaitsForTheWriterLockBeforeJudgingTheLiveLedgerMissing(string profile, bool reserve)
    {
        using var fixture = new ModelFixture();
        string path = profile == "first-slice-v1" ? fixture.LedgerPath : Path.Combine(fixture.Root, "full.json");
        var ledger = profile == "first-slice-v1" ? PersistentBudgetLedger.OpenExisting(path, profile)
            : PersistentBudgetLedger.InitializeNew(path, profile);
        var original = ledger.Reserve(ModelStage.Baseline, "existing-unknown");
        ledger.MarkUnknown(original.AttemptId, "transport_timeout", false);
        var before = ledger.Snapshot();
        string replacement = path + ".replacement-in-progress";
        using var writerLock = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        File.Move(path, replacement);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Task.Run(() =>
        {
            started.SetResult();
            var reader = reserve ? ledger : PersistentBudgetLedger.OpenExisting(path, profile);
            if (reserve) reader.Reserve(ModelStage.Counterfactual, "after-replacement");
            return reader.Snapshot();
        });
        bool waited = false;
        Exception? failure = null;
        BudgetSnapshot? after = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            waited = await Task.WhenAny(operation, Task.Delay(200)) != operation;
        }
        finally
        {
            File.Move(replacement, path);
            writerLock.Dispose();
            try { after = await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { failure = error; }
        }
        Assert.That(waited, Is.True, failure?.Message);
        Assert.That(failure, Is.Null);
        Assert.That(after!.TotalAttempts, Is.EqualTo(before.TotalAttempts + (reserve ? 1 : 0)));
        Assert.That(after.SettledMicros, Is.EqualTo(before.SettledMicros));
        Assert.That(after.ReservedMicros, Is.EqualTo(reserve ? 4_220_000 : 2_110_000));
        Assert.That(after.SlotsHeld, Is.EqualTo(reserve ? 2 : 1));
        Assert.That(after.Attempts.Single(a => a.AttemptId == original.AttemptId), Is.EqualTo(before.Attempts.Last()));
    }
}
