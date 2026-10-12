using CozyTown.Core.Town;
using CozyTown.Game;
using CozyTown.Model;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

[TestFixture]
public sealed class TownReplayExportTests
{
    [Test]
    public async Task RetiredRequestCreatedAfterTheSavedParentPrefixUsesItsContinuedBudgetAttempt()
    {
        string root = Path.Combine(Path.GetTempPath(), "cozytown-retired-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var content = TownPlayerDialogueReplayTests.TownPlayerDialogueTestsContent();
            var world = new TownWorld(content); var host = new TownInputHost(content, world);
            using var journal = new TownRunJournal(Path.Combine(root, "runs"), world.State.BranchId, new { schemaVersion = 2 });
            journal.StartReplay(content, world, host);
            var empty = new BudgetSnapshot(TownWorld.DefaultBudgetId, "CNY", 5000000, 0, 0, 0, 0, null, []);
            journal.SetReplayBudget(empty);
            var saved = world.CaptureSnapshot(); var prefix = journal.CaptureReplay();
            world.Advance(.001, 0); var request = world.GetDecisionRequests()[0];
            string attemptId = Guid.NewGuid().ToString();
            var attempt = new BudgetAttempt(attemptId, "baseline", request.LogicalRequestId, false, "reserved", true, 2110000, 0, null, null, null);
            var reserved = empty with { Attempts = [attempt], TotalAttempts = 1, SlotsHeld = 1, ReservedMicros = 2110000 };
            await journal.WriteBudgetAdmissionAsync(new("baseline", request.LogicalRequestId, false, attemptId, "accepted", empty, reserved), CancellationToken.None);
            var frozen = new FrozenModelRequest(new(world.State.BranchId, world.SessionId, request.LogicalRequestId, request.ResidentId, request.ActivityRevision),
                ModelStage.Baseline, "{}", false, TimeSpan.FromSeconds(60));
            var dispatch = new ModelDispatch(frozen, attemptId, "fixture", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(60), reserved);
            string json = TownJson.Serialize(world.CreateRuleCandidate(request));
            await journal.WriteResponseArrivalAsync(dispatch, TownJson.Serialize(new { choices = new[] { new { message = new { content = json } } } }), TimeSpan.FromSeconds(2), CancellationToken.None);
            var settled = reserved with { Attempts = [attempt with { Status = "settled", SlotHeld = false, ReservedCostMicros = 0,
                SettledCostMicros = 280, PromptTokens = 100, CompletionTokens = 10 }], SlotsHeld = 0, ReservedMicros = 0, SettledMicros = 280 };
            var completion = new ModelCompletion(dispatch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(2), TimeSpan.FromSeconds(2), 200,
                "fixture", "fixed", "fixed", "stop", json, new(100, 10, 110, 0, 100), null, true, true, false, 280, TimeSpan.FromSeconds(2), settled);
            await journal.WriteCompletionAsync(completion, CancellationToken.None);
            var child = TownWorld.RestoreCandidate(content, TownJson.Serialize(saved));
            var childHost = new TownInputHost(content, child, host.State);
            using var childJournal = new TownRunJournal(Path.Combine(root, "runs"), child.State.BranchId, new { schemaVersion = 2 });
            childJournal.StartReplay(content, child, childHost, new(prefix, saved, host.State));
            childJournal.SetReplayBudget(settled);
            childJournal.ImportRetiredCompletion(journal, completion);
            childJournal.RecordReplayInput("model_mailbox_entered", new { dispatch });
            child.ReceiveResult(new(world.SessionId, request.LogicalRequestId, attemptId, request.ResidentId,
                TownJson.Deserialize<ActionCandidate>(json), "real_model", null, 2));
            var report = TownReplay.Run(childJournal.CaptureReplay());
            Assert.That(report.Passed, Is.True, TownJson.Serialize(report with { FinalStateJson = null }));
            Assert.That(child.State.Events.Any(e => e.Kind == "response_rejected" && e.Reason == "retired_session"), Is.True);
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(root).StartsWith("cozytown-retired-replay-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected cleanup target.");
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ExportSealsTheSameReplayPrefixWhileResponsesKeepArriving()
    {
        string root = Path.Combine(Path.GetTempPath(), "cozytown-replay-concurrent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var content = TownContent.Create(new(256, 256, [new("home", "Home", new(60, 60), "outside")], [], [new("outside", new(0, 0, 256, 256))]),
                new([new("mina", "Mina", "home", "home", "apple", new() { ["apple"] = 3 }, 5, [new("free", 0, 1440, "home", "free", true)])], [new("apple", "Apple")]));
            var world = new TownWorld(content); var host = new TownInputHost(content, world);
            using var journal = new TownRunJournal(Path.Combine(root, "runs"), world.State.BranchId, new { schemaVersion = 2 });
            journal.StartReplay(content, world, host);
            var attempts = Enumerable.Range(0, 44).Select(i => new BudgetAttempt(Guid.NewGuid().ToString(),
                i < 22 ? "baseline" : "counterfactual", "retired-request-" + i, false, "settled", false, 0, 0, 0, 0, null)).ToArray();
            var budget = new BudgetSnapshot(TownWorld.DefaultBudgetId, "CNY", 5000000, 0, 0, 44, 0, "experiment_finished", attempts);
            journal.SetReplayBudget(budget);
            var dispatches = attempts.Select(a => new ModelDispatch(new(new("retired-run", "retired-session", a.LogicalRequestId, "mina", 2),
                a.Stage == "baseline" ? ModelStage.Baseline : ModelStage.Counterfactual, "{}", false, TimeSpan.FromSeconds(60)),
                a.AttemptId, "fixture", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(60), budget)).ToArray();
            string body = new('x', 64000);
            for (int i = 0; i < 30; i++) journal.RecordReplayInput("model_response_arrived", new { dispatch = dispatches[i], responseBody = body, elapsed = TimeSpan.Zero });
            using var stop = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writer = Task.Factory.StartNew(() =>
            {
                int id = 30;
                while (!stop.IsCancellationRequested && id < dispatches.Length)
                {
                    journal.RecordReplayInput("model_response_arrived", new { dispatch = dispatches[id++], responseBody = body, elapsed = TimeSpan.Zero });
                    started.TrySetResult();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                await started.Task;
                for (int i = 0; i < 8; i++)
                {
                    string path = Path.Combine(root, "export-" + i + ".zip");
                    journal.ExportSnapshotZip(path);
                    var package = TownReplay.ReadPackage(path);
                    Assert.That(package.UnsealedPrefix, Is.False);
                    Assert.That(TownReplay.Run(package).Passed, Is.True);
                }
            }
            finally { stop.Cancel(); await writer; }
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(root).StartsWith("cozytown-replay-concurrent-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected cleanup target.");
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task OfflinePackageReplaysTimelyResponseDelayedPersistenceAndBudgetWithoutTouchingFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "cozytown-replay-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string ledger = Path.Combine(root, "real-budget.json"), credential = Path.Combine(root, "credential.txt");
            File.WriteAllText(ledger, "ledger must remain byte-identical"); File.WriteAllText(credential, "credential must never be used");
            var places = new[] { new PlaceDefinition("home", "Home", new(60, 60), "outside") };
            var resident = new ResidentDefinition("mina", "Mina", "home", "home", "apple", new() { ["apple"] = 3 }, 5, [new("free", 0, 1440, "home", "free", true)]);
            var content = TownContent.Create(new(256, 256, places, [], [new("outside", new(0, 0, 256, 256))]), new([resident], [new("apple", "Apple")]));
            var world = new TownWorld(content); world.Advance(.001, 0);
            var host = new TownInputHost(content, world);
            using var journal = new TownRunJournal(Path.Combine(root, "runs"), world.State.BranchId, new { schemaVersion = 2 });
            journal.StartReplay(content, world, host);
            var empty = new BudgetSnapshot(TownWorld.DefaultBudgetId, "CNY", 5000000, 0, 0, 0, 0, null, []);
            journal.SetReplayBudget(empty);
            var request = world.GetDecisionRequests().Single();
            string attempt = "00000000-0000-0000-0000-000000000001";
            var reservedAttempt = new BudgetAttempt(attempt, "baseline", request.LogicalRequestId, false, "reserved", true, 2110000, 0, null, null, null);
            var reserved = empty with { Attempts = new[] { reservedAttempt }, TotalAttempts = 1, SlotsHeld = 1, ReservedMicros = 2110000 };
            await journal.WriteBudgetAdmissionAsync(new("baseline", request.LogicalRequestId, false, attempt, "accepted", empty, reserved), CancellationToken.None);
            var frozen = new FrozenModelRequest(new(world.State.BranchId, world.SessionId, request.LogicalRequestId, request.ResidentId, request.ActivityRevision), ModelStage.Baseline, "{}", false, TimeSpan.FromSeconds(60));
            var dispatch = new ModelDispatch(frozen, attempt, "fingerprint", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(60), reserved);
            world.MarkDispatched(request.LogicalRequestId, attempt);
            var candidate = world.CreateRuleCandidate(request);
            string candidateJson = TownJson.Serialize(candidate);
            string body = TownJson.Serialize(new { choices = new[] { new { message = new { content = candidateJson } } } });
            await journal.WriteResponseArrivalAsync(dispatch, body, TimeSpan.FromSeconds(2), CancellationToken.None);
            world.MarkResponseArrived(world.SessionId, request.LogicalRequestId, attempt, 2);
            host.Pause("menu", true); world.Advance(0, 100, true);
            var settled = reserved with { Attempts = new[] { reservedAttempt with { Status = "settled", SlotHeld = false, ReservedCostMicros = 0, SettledCostMicros = 280, PromptTokens = 100, CompletionTokens = 10 } }, SlotsHeld = 0, ReservedMicros = 0, SettledMicros = 280 };
            var completion = new ModelCompletion(dispatch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(2), TimeSpan.FromSeconds(2), 200, "provider", "fixed", "fixed", "stop", candidateJson, new(100, 10, 110, 0, 100), null, true, true, false, 280, TimeSpan.FromSeconds(2), settled);
            await journal.WriteCompletionAsync(completion, CancellationToken.None);
            journal.RecordReplayInput("model_mailbox_enqueued", new { dispatch }); journal.RecordReplayInput("model_mailbox_entered", new { dispatch });
            world.ReceiveResult(new(world.SessionId, request.LogicalRequestId, attempt, request.ResidentId, candidate, "real_model", null, 2));
            host.Pause("menu", false); world.Advance(.1, 0);
            var package = journal.CaptureReplay();
            var report = TownReplay.Run(package);
            Assert.That(report.Passed, Is.True, TownJson.Serialize(report with { FinalStateJson = null }));
            Assert.That(File.ReadAllText(ledger), Is.EqualTo("ledger must remain byte-identical"));
            Assert.That(File.ReadAllText(credential), Is.EqualTo("credential must never be used"));
            Assert.That(typeof(TownReplay).Assembly.GetReferencedAssemblies().Any(a => a.Name == "CozyTown.Model"), Is.False);
            var tampered = TownJson.Clone(package);
            int persisted = Array.FindIndex(tampered.Boundaries, b => b.Command == "model_completion_persisted");
            var modified = System.Text.Json.Nodes.JsonNode.Parse(tampered.Boundaries[persisted].InputJson)!;
            modified["candidateJson"] = TownJson.Serialize(candidate with { Text = "changed response" });
            tampered.Boundaries[persisted] = tampered.Boundaries[persisted] with { InputJson = modified.ToJsonString() };
            Assert.That(TownReplay.Run(tampered).Passed, Is.False);
            Assert.That(TownReplay.Run(package with { InitialBudget = null }).Passed, Is.False);
            var delayed = TownJson.Clone(package);
            var alteredArrival = System.Text.Json.Nodes.JsonNode.Parse(delayed.Boundaries.First(b => b.Command == "model_response_arrived").InputJson)!;
            alteredArrival["elapsed"] = "00:00:03";
            int arrivalIndex = Array.FindIndex(delayed.Boundaries, b => b.Command == "model_response_arrived");
            delayed.Boundaries[arrivalIndex] = delayed.Boundaries[arrivalIndex] with { InputJson = alteredArrival.ToJsonString(),
                InputFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("model_response_arrived\n" + alteredArrival.ToJsonString()))).ToLowerInvariant() };
            Assert.That(TownReplay.Run(delayed).Field, Is.EqualTo("model.npcArrivalSeconds"));
            var snapshot = world.CaptureSnapshot();
            var child = TownWorld.RestoreCandidate(content, TownJson.Serialize(snapshot));
            var childHost = new TownInputHost(content, child, host.State);
            using var childJournal = new TownRunJournal(Path.Combine(root, "runs"), child.State.BranchId, new { schemaVersion = 2 });
            childJournal.StartReplay(content, child, childHost, new(package, snapshot, host.State));
            childJournal.SetReplayBudget(settled);
            childJournal.ImportRetiredCompletion(journal, completion);
            childJournal.RecordReplayInput("model_mailbox_entered", new { dispatch });
            child.ReceiveResult(new(world.SessionId, request.LogicalRequestId, attempt, request.ResidentId, candidate, "real_model", null, 2));
            child.Advance(.1, 0);
            Assert.That(TownReplay.Run(childJournal.CaptureReplay()).Passed, Is.True);
            Assert.That(childJournal.CaptureReplay().Boundaries.Any(b => b.Command == "model_response_arrived" && b.InputJson.Contains("responseBody")), Is.True);
            var changedDispatch = TownJson.Clone(childJournal.CaptureReplay());
            for (int i = 0; i < changedDispatch.Boundaries.Length; i++)
            {
                var trace = changedDispatch.Boundaries[i];
                if (!trace.Command.StartsWith("model_", StringComparison.Ordinal)) continue;
                var changed = System.Text.Json.Nodes.JsonNode.Parse(trace.InputJson)!;
                changed["dispatch"]!["inputFingerprint"] = "changed after fork";
                string json = changed.ToJsonString();
                changedDispatch.Boundaries[i] = trace with { InputJson = json,
                    InputFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(trace.Command + "\n" + json))).ToLowerInvariant() };
            }
            Assert.That(TownReplay.Run(changedDispatch).Field, Is.EqualTo("model.dispatch.inputFingerprint"));
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(root).StartsWith("cozytown-replay-offline-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected cleanup target.");
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ArrivalBodyAndPersistenceAreSeparateRecordedInputs()
    {
        string root = Path.Combine(Path.GetTempPath(), "cozytown-replay-arrival-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var journal = new TownRunJournal(root, "arrival", new { schemaVersion = 2 });
            var budget = new BudgetSnapshot(TownWorld.DefaultBudgetId, "CNY", 5000000, 0, 0, 0, 0, null, []);
            var request = new FrozenModelRequest(new("run", "session", "logical", "mina", 0), ModelStage.Baseline, "{}", false, TimeSpan.FromSeconds(60));
            var dispatch = new ModelDispatch(request, "attempt", "fingerprint", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(60), budget);
            await journal.WriteResponseArrivalAsync(dispatch, "{\"complete\":\"body\"}", TimeSpan.FromSeconds(2), CancellationToken.None);
            using var stream = new FileStream(Path.Combine(journal.DirectoryPath, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string recorded = await reader.ReadToEndAsync();
            Assert.That(recorded, Does.Contain("model_response_arrived").And.Contain("complete").And.Not.Contain("model_completion_persisted"));
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(root).StartsWith("cozytown-replay-arrival-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected cleanup target.");
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void ExportContainsExecutableCoreInputsAndRequiresNoLiveProvider()
    {
        string root = Path.Combine(Path.GetTempPath(), "cozytown-replay-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var places = new[] { new PlaceDefinition("home", "Home", new(60, 60), "outside") };
            var resident = new ResidentDefinition("mina", "Mina", "home", "home", "apple", new() { ["apple"] = 3 }, 5, [new("free", 0, 1440, "home", "free", true)]);
            var content = TownContent.Create(new(256, 256, places, [], [new("outside", new(0, 0, 256, 256))]), new([resident], [new("apple", "Apple")]));
            var world = new TownWorld(content);
            var host = new TownInputHost(content, world);
            using var journal = new TownRunJournal(Path.Combine(root, "runs"), world.State.BranchId, new { schemaVersion = 2 });
            journal.StartReplay(content, world, host);
            host.Pause("menu", true);
            world.Advance(10, .25, true);
            host.Pause("menu", false);
            world.SetPlayerPosition(new(70, 60));
            var direct = TownReplay.Run(TownReplay.ReadPackage(journal.DirectoryPath));
            Assert.That(direct.Code, Is.EqualTo("unsealed_recording"));
            Assert.That(direct.FinalStateJson, Is.EqualTo(TownJson.Serialize(world.State)));
            string export = Path.Combine(root, "export.zip");
            journal.ExportSnapshotZip(export);
            var report = TownReplay.Run(TownReplay.ReadPackage(export));
            Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
            Assert.That(report.FinalStateJson, Is.EqualTo(TownJson.Serialize(world.State)));
            string journalPath = Path.Combine(journal.DirectoryPath, "events.jsonl");
            journal.Dispose();
            using (var tamper = new FileStream(journalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) tamper.SetLength(tamper.Length - 5);
            Assert.Catch<System.Text.Json.JsonException>(() => TownReplay.ReadPackage(journal.DirectoryPath));
        }
        finally
        {
            string full = Path.GetFullPath(root);
            if (!Path.GetFileName(full).StartsWith("cozytown-replay-tests-", StringComparison.Ordinal) || Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)) throw new InvalidOperationException("Unexpected cleanup target.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
