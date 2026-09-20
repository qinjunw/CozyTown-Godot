using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using CozyTown.Core.Town;
using CozyTown.Game;
using CozyTown.Model;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

[TestFixture]
public sealed class TownPersistenceTests
{
    private string _directory = null!;
    private string SavePath => Path.Combine(_directory, "save.json");

    [SetUp]
    public void SetUp() => _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cozytown-host-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    [TearDown]
    public void TearDown()
    {
        string full = Path.GetFullPath(_directory);
        if (!Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("cozytown-host-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Temporary test directory is outside its cleanup boundary.");
        Directory.Delete(full, true);
    }

    private static TownContent Content()
    {
        var places = new[] { new PlaceDefinition("a", "A", new(60, 60), "outside"), new PlaceDefinition("b", "B", new(100, 60), "outside"),
            new PlaceDefinition("c", "C", new(60, 100), "outside"), new PlaceDefinition("d", "D", new(100, 100), "outside") };
        var residents = new[] { "mina", "eli", "ren", "sora" }.Select((id, i) => new ResidentDefinition(id, id, places[i].Id, places[i].Id,
            "apple", new() { ["apple"] = 3 }, 5, [new("free", 0, 1440, places[i].Id, "free", true)])).ToArray();
        return TownContent.Create(new(256, 256, places, [], [new("outside", new(0, 0, 256, 256))]), new(residents, [new("apple", "Apple")], 540));
    }

    private static TownHostSnapshot Snapshot(bool real = false)
    {
        var world = new TownWorld(Content());
        world.Advance(.001, 0);
        var requests = new Dictionary<string, FrozenModelRequest>();
        if (real)
        {
            var request = world.GetDecisionRequests().First();
            string body = TownJson.Serialize(new { model = "deepseek-flash", messages = new[] { new { role = "user", content = request.InputJson } } });
            requests.Add(request.LogicalRequestId, new(new(world.State.BranchId, world.SessionId, request.LogicalRequestId, request.ResidentId, request.ActivityRevision),
                ModelStage.Baseline, body, false, TimeSpan.FromSeconds(60)));
        }
        return new(1, world.CaptureJson(), world.State.BranchId, 3, .025, real ? "real" : "rules", "baseline", requests);
    }

    private static void ValidateWorld(TownHostSnapshot snapshot) => _ = TownWorld.RestoreCandidate(Content(), snapshot.WorldJson);

    private static string ReadOpenJournal(TownRunJournal journal)
    {
        using var stream = new FileStream(Path.Combine(journal.DirectoryPath, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Test]
    public void AtomicSaveRoundTripsAndKeepsExactlyThePreviousPublishedSave()
    {
        var first = Snapshot();
        TownSaveFile.Write(SavePath, first, ValidateWorld);
        Assert.That(TownSaveFile.Read(SavePath), Is.EqualTo(first).Using<TownHostSnapshot>((a, b) => TownJson.Serialize(a) == TownJson.Serialize(b)));
        Assert.That(File.Exists(SavePath + ".previous"), Is.False);
        TownSaveFile.Write(SavePath, first with { JournalRecordCount = 4 }, ValidateWorld);
        Assert.That(TownSaveFile.Read(SavePath + ".previous").JournalRecordCount, Is.EqualTo(3));
        TownSaveFile.Write(SavePath, first with { JournalRecordCount = 5 }, ValidateWorld);
        Assert.That(TownSaveFile.Read(SavePath).JournalRecordCount, Is.EqualTo(5));
        Assert.That(TownSaveFile.Read(SavePath + ".previous").JournalRecordCount, Is.EqualTo(4));
        Assert.That(Directory.GetFiles(_directory, "*.tmp.*"), Is.Empty);
    }

    [Test]
    public void FailedValidationDoesNotReplaceSaveOrPrevious()
    {
        var snapshot = Snapshot();
        TownSaveFile.Write(SavePath, snapshot, ValidateWorld);
        TownSaveFile.Write(SavePath, snapshot with { JournalRecordCount = 4 }, ValidateWorld);
        byte[] original = File.ReadAllBytes(SavePath), previous = File.ReadAllBytes(SavePath + ".previous");
        Assert.Throws<InvalidDataException>(() => TownSaveFile.Write(SavePath, snapshot with { JournalRecordCount = 5 }, _ => throw new InvalidDataException("Injected validation failure.")));
        Assert.That(File.ReadAllBytes(SavePath), Is.EqualTo(original));
        Assert.That(File.ReadAllBytes(SavePath + ".previous"), Is.EqualTo(previous));
        Assert.That(Directory.GetFiles(_directory, "*.tmp.*"), Is.Empty);
    }

    [Test, Platform("Win")]
    public void DeniedWindowsReplacePreservesSaveAndPrevious()
    {
        var snapshot = Snapshot();
        TownSaveFile.Write(SavePath, snapshot, ValidateWorld);
        TownSaveFile.Write(SavePath, snapshot with { JournalRecordCount = 4 }, ValidateWorld);
        byte[] original = File.ReadAllBytes(SavePath), previous = File.ReadAllBytes(SavePath + ".previous");
        using (var held = new FileStream(SavePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => TownSaveFile.Write(SavePath, snapshot with { JournalRecordCount = 5 }, ValidateWorld));
        Assert.That(File.ReadAllBytes(SavePath), Is.EqualTo(original));
        Assert.That(File.ReadAllBytes(SavePath + ".previous"), Is.EqualTo(previous));
        Assert.That(Directory.GetFiles(_directory, "*.tmp.*"), Is.Empty);
    }

    [Test]
    public void TruncatedCurrentSaveDoesNotSilentlyLoadPrevious()
    {
        var snapshot = Snapshot();
        TownSaveFile.Write(SavePath, snapshot, ValidateWorld);
        TownSaveFile.Write(SavePath, snapshot with { JournalRecordCount = 4 }, ValidateWorld);
        File.WriteAllText(SavePath, "{\"schemaVersion\":1,");
        Assert.Throws<InvalidDataException>(() => TownSaveFile.Read(SavePath));
        Assert.That(TownSaveFile.Read(SavePath + ".previous").JournalRecordCount, Is.EqualTo(3));
    }

    [Test, Platform("Win")]
    public void TemporaryWriteFailureLeavesBothPublishedFilesUntouched()
    {
        string path = Path.Combine(_directory, new string('s', 235) + ".json");
        string current = TownJson.Serialize(Snapshot());
        string previous = TownJson.Serialize(Snapshot());
        File.WriteAllText(path, current);
        File.WriteAllText(path + ".previous", previous);
        // The existing names fit Windows' component limit; the unique temporary suffix exceeds it.
        Assert.Catch<IOException>(() => TownSaveFile.Write(path, Snapshot(), ValidateWorld));
        Assert.That(File.ReadAllText(path), Is.EqualTo(current));
        Assert.That(File.ReadAllText(path + ".previous"), Is.EqualTo(previous));
    }

    [TestCase("schemaVersion")]
    [TestCase("worldJson")]
    [TestCase("sourceRunDirectoryName")]
    [TestCase("journalRecordCount")]
    [TestCase("unconsumedSeconds")]
    [TestCase("decisionMode")]
    [TestCase("stage")]
    [TestCase("modelRequests")]
    public void MissingHostFieldsAreRejected(string field)
    {
        var json = JsonNode.Parse(TownJson.Serialize(Snapshot()))!.AsObject();
        json.Remove(field);
        File.WriteAllText(SavePath, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => TownSaveFile.Read(SavePath));
    }

    [TestCase("../branch")]
    [TestCase("..\\branch")]
    [TestCase("C:\\branch")]
    [TestCase("branch:alternate")]
    [TestCase(".")]
    [TestCase("..")]
    public void RunDirectoryNamesCannotEscapeTheirRoot(string name)
    {
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(Snapshot() with { SourceRunDirectoryName = name }));
        Assert.Throws<InvalidDataException>(() => new TownRunJournal(_directory, name, new { }));
    }

    [Test]
    public void InvalidUtf8AndDuplicateHostFieldsAreRejected()
    {
        File.WriteAllBytes(SavePath, [0xff]);
        Assert.Throws<InvalidDataException>(() => TownSaveFile.Read(SavePath));
        File.WriteAllText(SavePath, "{\"schemaVersion\":1," + TownJson.Serialize(Snapshot()).TrimStart()[1..]);
        Assert.Throws<InvalidDataException>(() => TownSaveFile.Read(SavePath));
    }

    [Test]
    public void ModeStageAndWorldBranchMustAgree()
    {
        var snapshot = Snapshot();
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(snapshot with { DecisionMode = "silent-fallback" }));
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(snapshot with { Stage = "counterfactual" }));
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(snapshot with { SourceRunDirectoryName = "other-branch" }));
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(snapshot with { UnconsumedSeconds = double.NaN }));
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(Snapshot(true) with { DecisionMode = "rules" }));
    }

    [Test]
    public void FrozenRequestMustMatchItsLogicalIdentityStageAndPendingInput()
    {
        var snapshot = Snapshot(true);
        var pair = snapshot.ModelRequests.Single();
        TownSaveFile.Write(SavePath, snapshot, ValidateWorld);
        Assert.That(TownSaveFile.Read(SavePath).ModelRequests.Keys, Is.EquivalentTo(snapshot.ModelRequests.Keys));
        foreach (FrozenModelRequest invalid in new[]
        {
            pair.Value with { Identity = pair.Value.Identity with { LogicalRequestId = "other" } },
            pair.Value with { Identity = pair.Value.Identity with { ResidentId = pair.Value.Identity.ResidentId == "eli" ? "mina" : "eli" } },
            pair.Value with { Identity = pair.Value.Identity with { ActivityRevision = 999 } },
            pair.Value with { Stage = ModelStage.Counterfactual },
            pair.Value with { Stage = (ModelStage)99 },
            pair.Value with { RemainingWait = TimeSpan.Zero },
            pair.Value with { InputJson = "{}" },
            pair.Value with { InputJson = "{\"messages\":[{\"role\":\"user\",\"content\":\"other input\"}]}" }
        })
            Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(snapshot with { ModelRequests = new() { [pair.Key] = invalid } }));
    }

    [Test]
    public void RequestHistoryCanKeepOldSessionAndRunIdentityAfterRestore()
    {
        var snapshot = Snapshot(true);
        var world = TownWorld.RestoreCandidate(Content(), snapshot.WorldJson);
        var restored = snapshot with { WorldJson = world.CaptureJson(), SourceRunDirectoryName = world.State.BranchId };
        Assert.That(restored.SourceRunDirectoryName, Is.Not.EqualTo(snapshot.SourceRunDirectoryName));
        Assert.DoesNotThrow(() => TownSaveFile.ValidateHostSnapshot(restored));
    }

    [Test]
    public void PlayerRouteRoundTripsAndRejectsInvalidCoordinatesOrCursor()
    {
        var snapshot = Snapshot() with { PlayerRoute = [new(60, 60), new(100, 60)], PlayerRouteCursor = 1 };
        TownSaveFile.Write(SavePath, snapshot, ValidateWorld);
        Assert.That(TownSaveFile.Read(SavePath).PlayerRoute, Is.EqualTo(snapshot.PlayerRoute));
        Assert.That(TownSaveFile.Read(SavePath).PlayerRouteCursor, Is.EqualTo(1));
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(snapshot with { PlayerRouteCursor = -1 }));
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(snapshot with { PlayerRouteCursor = 3 }));
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(snapshot with { PlayerRoute = null }));
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(snapshot with { PlayerRoute = [new(double.NaN, 60)] }));
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(snapshot with { PlayerRoute = [new(60, double.PositiveInfinity)] }));
    }

    [Test]
    public void DispatchedRealDecisionRequiresItsFrozenProviderBody()
    {
        var snapshot = Snapshot(true);
        var world = TownWorld.RestoreCandidate(Content(), snapshot.WorldJson);
        var logicalId = snapshot.ModelRequests.Keys.Single();
        Assert.That(world.MarkDispatched(logicalId, "attempt"), Is.True);
        var missing = snapshot with { WorldJson = world.CaptureJson(), SourceRunDirectoryName = world.State.BranchId, ModelRequests = [] };
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(missing));
    }

    [Test]
    public void ExistingRunDirectoryCannotBeOverwritten()
    {
        using var journal = new TownRunJournal(_directory, "branch", new { version = "original" });
        journal.Append("first", new { value = 1 });
        Assert.Throws<IOException>(() => new TownRunJournal(_directory, "branch", new { version = "replacement" }));
        Assert.That(File.ReadAllText(Path.Combine(journal.DirectoryPath, "manifest.json")), Does.Contain("original"));
        Assert.That(journal.RecordCount, Is.EqualTo(1));
    }

    [Test]
    public void ArtifactDiskFailureInterruptsAllFutureWrites()
    {
        using var journal = new TownRunJournal(_directory, "branch", new { });
        journal.Append("confirmed", new { value = 1 });
        string prefix = ReadOpenJournal(journal);
        Directory.CreateDirectory(Path.Combine(journal.DirectoryPath, "blocked.json"));
        Assert.Throws<IOException>(() => journal.WriteArtifact("blocked.json", new { value = 2 }));
        Assert.That(journal.Failed, Is.True);
        Assert.That(journal.FailureCode, Is.EqualTo("journal_write_failed"));
        Assert.Throws<IOException>(() => journal.Append("must-not-append", new { }));
        Assert.That(journal.RecordCount, Is.EqualTo(1));
        Assert.That(ReadOpenJournal(journal), Is.EqualTo(prefix));
    }

    [Test]
    public void FailedSerializationDoesNotPublishPartialJournalRecord()
    {
        using var journal = new TownRunJournal(_directory, "branch", new { });
        journal.Append("confirmed", new { });
        Assert.Throws<IOException>(() => journal.Append("invalid", new { value = double.NaN }));
        Assert.That(journal.Failed, Is.True);
        Assert.That(ReadOpenJournal(journal).Split('\n', StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(1));
        Assert.That(journal.RecordCount, Is.EqualTo(1));
    }

    [Test]
    public async Task RequestJournalFlushesBeforeHostAcknowledgement()
    {
        using var journal = new TownRunJournal(_directory, "branch", new { });
        var request = Snapshot(true).ModelRequests.Values.Single();
        var dispatch = new ModelDispatch(request, "attempt", "fingerprint", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30),
            new("test-budget", "CNY", 5_000_000, 0, 0, 0, 0, null, []));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        journal.BeforeSendOnHost = _ => release.Task;
        Task pending = journal.WriteRequestAsync(dispatch, CancellationToken.None);
        Assert.That(pending.IsCompleted, Is.False);
        Assert.That(journal.RecordCount, Is.EqualTo(1));
        Assert.That(ReadOpenJournal(journal), Does.Contain("model_dispatched"));
        release.SetResult();
        await pending;
    }

    [Test]
    public async Task ConcurrentExportsContainAnExactCompleteJournalPrefix()
    {
        using var journal = new TownRunJournal(_directory, "branch", new { version = 1 });
        journal.WriteArtifact("content.json", new { frozen = true });
        journal.Append("first", new { index = 0 });
        using var start = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            start.Wait();
            for (int i = 1; i <= 60; i++) { journal.Append("concurrent", new { index = i, text = new string('x', 2048) }); Thread.Yield(); }
        });
        start.Set();
        var prefixes = new List<string[]>();
        for (int i = 0; i < 6; i++)
        {
            string path = Path.Combine(_directory, $"export-{i}.zip");
            journal.ExportSnapshotZip(path);
            using var archive = ZipFile.OpenRead(path);
            using var metadata = new StreamReader(archive.GetEntry("journal-export.json")!.Open());
            var info = JsonNode.Parse(metadata.ReadToEnd())!;
            using var events = new StreamReader(archive.GetEntry("events.jsonl")!.Open());
            string contents = events.ReadToEnd();
            string[] lines = contents.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.That(contents, Does.EndWith("\n"));
            Assert.That(lines.LongLength, Is.EqualTo(info["recordCount"]!.GetValue<long>()));
            Assert.That(Encoding.UTF8.GetByteCount(contents), Is.EqualTo(info["journalByteLength"]!.GetValue<long>()));
            Assert.That(lines.Select(l => JsonNode.Parse(l)!["sequence"]!.GetValue<long>()), Is.EqualTo(Enumerable.Range(1, lines.Length).Select(n => (long)n)));
            Assert.That(archive.GetEntry("content.json"), Is.Not.Null);
            prefixes.Add(lines);
        }
        await writer;
        string[] full = ReadOpenJournal(journal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(full, Has.Length.EqualTo(61));
        foreach (string[] prefix in prefixes) Assert.That(full.Take(prefix.Length), Is.EqualTo(prefix));
    }

    [Test]
    public void FailedExportPreservesExistingDestinationAndJournalCanContinue()
    {
        using var journal = new TownRunJournal(_directory, "branch", new { });
        string destination = Path.Combine(_directory, "export.zip");
        File.WriteAllText(destination, "existing export");
        Assert.Throws<IOException>(() => journal.ExportSnapshotZip(destination));
        Assert.That(File.ReadAllText(destination), Is.EqualTo("existing export"));
        Assert.That(journal.Failed, Is.False);
        journal.Append("continued", new { });
        Assert.That(journal.RecordCount, Is.EqualTo(1));
        Assert.Throws<InvalidDataException>(() => journal.ExportSnapshotZip(Path.Combine(journal.DirectoryPath, "recursive.zip")));
        Assert.Throws<InvalidDataException>(() => journal.WriteArtifact("../outside.json", new { }));
        Assert.That(Directory.GetFiles(_directory, "*.tmp.*"), Is.Empty);
    }
}
