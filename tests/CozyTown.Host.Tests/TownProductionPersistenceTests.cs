using System.IO.Compression;
using System.Text.Json.Nodes;
using CozyTown.Core.Town;
using CozyTown.Game;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

[TestFixture]
public sealed class TownProductionPersistenceTests
{
    private string _directory = null!;
    private string SavePath => Path.Combine(_directory, "save.json");

    [SetUp]
    public void SetUp() => _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "cozytown-production-host-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    [TearDown]
    public void TearDown()
    {
        string full = Path.GetFullPath(_directory);
        if (!Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("cozytown-production-host-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Temporary test directory is outside its cleanup boundary.");
        Directory.Delete(full, true);
    }

    [TestCase("feed")]
    [TestCase("collect")]
    [TestCase("fish")]
    [TestCase("cook")]
    public void ClosedJournalPreservesWholeWorldAndAllowsTheSameOperationToRetry(string action)
    {
        var world = new TownWorld(Content());
        using var journal = Journal(world);
        if (action == "collect")
        {
            Assert.That(world.Produce(Request("prepare-feed", "feed")).Succeeded, Is.True);
            world.Advance(1, 0);
        }
        journal.Append("confirmed", new { });
        journal.Dispose();
        string before = TownJson.Serialize(world.State);
        string prefix = ReadJournal(journal);
        var request = Request("retryable-production", action);
        int callbacks = 0;

        Assert.Throws<IOException>(() => world.Produce(request, operation =>
        {
            callbacks++;
            Append(journal, operation);
        }));

        Assert.Multiple(() =>
        {
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
            Assert.That(ReadJournal(journal), Is.EqualTo(prefix));
            Assert.That(journal.RecordCount, Is.EqualTo(1));
        });
        using var retryJournal = Journal(world, "retry");
        var retry = world.Produce(request, operation => Append(retryJournal, operation));
        Assert.That(retry.Succeeded && !retry.IsDuplicate, Is.True);
        Assert.That(retryJournal.RecordCount, Is.EqualTo(1));
        Assert.That(world.State.Production.Receipts.ContainsKey(request.OperationId), Is.True);
    }

    [TestCase("fish")]
    [TestCase("cook")]
    public void DiskFailureDuringProductionPreservesBothPublishedFilesAndRandomState(string action)
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        Save(world, journal, content);
        Assert.That(world.Produce(Request("feed-one", "feed")).Succeeded, Is.True);
        Save(world, journal, content);
        byte[] current = File.ReadAllBytes(SavePath), previous = File.ReadAllBytes(SavePath + ".previous");
        string before = TownJson.Serialize(world.State);
        Directory.CreateDirectory(Path.Combine(journal.DirectoryPath, "production.json"));
        var request = Request("write-failure", action);

        Assert.Throws<IOException>(() => world.Produce(request,
            operation => journal.WriteArtifact("production.json", operation)));

        Assert.Multiple(() =>
        {
            Assert.That(journal.Failed, Is.True);
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
            Assert.That(File.ReadAllBytes(SavePath), Is.EqualTo(current));
            Assert.That(File.ReadAllBytes(SavePath + ".previous"), Is.EqualTo(previous));
            Assert.That(Directory.GetFiles(journal.DirectoryPath, "*.tmp.*"), Is.Empty);
        });
        using var retryJournal = Journal(world, "retry");
        var retried = world.Produce(request, operation => Append(retryJournal, operation));
        Assert.That(retried.Succeeded && !retried.IsDuplicate, Is.True);
        if (action == "fish")
        {
            Assert.That(retried.Roll, Is.EqualTo(69));
            Assert.That(world.State.Production.FishingRandomState, Is.EqualTo(270369U));
            Assert.That(world.State.Production.FishingDrawCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void CookingCheckpointIsDurableBeforeIngredientsAndResultBecomeVisible()
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        string before = TownJson.Serialize(world.State);
        ProductionOperationRecord? persisted = null;
        var request = Request("cook-one", "cook");

        var result = world.Produce(request, operation =>
        {
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
            Append(journal, operation);
            persisted = ReadOperations(ReadJournal(journal)).Single();
            Assert.Multiple(() =>
            {
                Assert.That(persisted.Request, Is.EqualTo(request));
                Assert.That(persisted.SchemaVersion, Is.EqualTo(1));
                Assert.That(persisted.Checkpoint.Economy.Player.Inventory["potato"], Is.EqualTo(1));
                Assert.That(persisted.Checkpoint.Economy.Player.Inventory["salt"], Is.EqualTo(1));
                Assert.That(persisted.Checkpoint.Economy.Player.Inventory["meal"], Is.EqualTo(1));
                Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
            });
        });

        Assert.That(result.Succeeded, Is.True);
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(TownJson.Serialize(persisted!.Checkpoint)));
        Assert.That(persisted.RandomStateAfter, Is.EqualTo(persisted.RandomStateBefore));
        Save(world, journal, content);
    }

    [Test]
    public void FedAndUnclaimedFileRestoresCrossMorningWithoutConsumingMoreFeedOrAddingEggs()
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        Assert.That(world.Produce(Request("feed-one", "feed")).Succeeded, Is.True);
        var duplicateFeed = world.Produce(Request("feed-again", "feed"));
        Assert.That(duplicateFeed.Succeeded, Is.False);
        Assert.That(world.State.Economy.Player.Inventory["feed"], Is.EqualTo(1));
        Save(world, journal, content);
        var fed = TownSaveFile.Read(SavePath);
        var fedWorld = TownWorld.RestoreCandidate(content, fed.WorldJson);
        Assert.That(fedWorld.State.Production.Chickens.Single().Fed, Is.True);
        fedWorld.Advance(1, 0);
        world.Advance(1, 0);
        Assert.That(TownJson.Serialize(fedWorld.State.Production), Is.EqualTo(TownJson.Serialize(world.State.Production)));
        Save(world, journal, content);
        var ready = TownSaveFile.Read(SavePath);
        var readyWorld = TownWorld.RestoreCandidate(content, ready.WorldJson);
        readyWorld.Advance(2 * 1440, 0);
        var pendingFeed = readyWorld.Produce(Request("feed-while-pending", "feed"));

        Assert.Multiple(() =>
        {
            Assert.That(pendingFeed.Succeeded, Is.False);
            Assert.That(readyWorld.State.Economy.Player.Inventory["feed"], Is.EqualTo(1));
            Assert.That(readyWorld.State.Production.Chickens.Single().ProductReady, Is.True);
            Assert.That(readyWorld.State.Production.Chickens.Single().Fed, Is.False);
            Assert.That(readyWorld.State.Production.Chickens.Single().Generation, Is.EqualTo(1));
            Assert.That(readyWorld.State.Production.Chickens.Single().CollectedCount, Is.Zero);
            Assert.That(readyWorld.State.Economy.Player.Inventory.ContainsKey("egg"), Is.False);
        });
        var collect = Request("collect-one", "collect");
        Assert.That(readyWorld.Produce(collect).Succeeded, Is.True);
        using var resumedJournal = Journal(readyWorld, "resumed");
        Save(readyWorld, resumedJournal, content);
        var collectedWorld = TownWorld.RestoreCandidate(content, TownSaveFile.Read(SavePath).WorldJson);
        Assert.That(collectedWorld.Produce(collect).IsDuplicate, Is.True);
        Assert.That(collectedWorld.State.Economy.Player.Inventory["egg"], Is.EqualTo(1));
        Assert.That(collectedWorld.State.Production.Chickens.Single().CollectedCount, Is.EqualTo(1));
        Assert.That(collectedWorld.State.Production.Chickens.Single().ProductReady, Is.False);
    }

    [Test]
    public void FullInventorySaveKeepsTheEggUntilARestoredPlayerFreesASlot()
    {
        var content = Content(capacity: 5);
        var world = new TownWorld(content);
        using var journal = Journal(world);
        Assert.That(world.Produce(Request("feed-one", "feed")).Succeeded, Is.True);
        world.Advance(1, 0);
        var assets = TownJson.Serialize(world.State.Economy.Player);
        var chicken = TownJson.Serialize(world.State.Production.Chickens);
        var rejected = world.Produce(Request("collect-full", "collect"));
        Assert.That(rejected.Code, Is.EqualTo("inventory_full"));
        Save(world, journal, content);
        var restored = TownWorld.RestoreCandidate(content, TownSaveFile.Read(SavePath).WorldJson);

        Assert.Multiple(() =>
        {
            Assert.That(TownJson.Serialize(restored.State.Economy.Player), Is.EqualTo(assets));
            Assert.That(TownJson.Serialize(restored.State.Production.Chickens), Is.EqualTo(chicken));
            Assert.That(restored.Produce(Request("collect-full", "collect")).IsDuplicate, Is.True);
        });
        Assert.That(restored.Trade(new("sell-potatoes", "player", "shop", "potato", "sell", 2, 2)).Succeeded, Is.True);
        Assert.That(restored.Produce(Request("collect-after-sale", "collect")).Succeeded, Is.True);
        Assert.That(restored.State.Economy.Player.Inventory["egg"], Is.EqualTo(1));
        Assert.That(restored.State.Production.Chickens.Single().ProductReady, Is.False);
        using var resumedJournal = Journal(restored, "resumed");
        Save(restored, resumedJournal, content);
    }

    [Test]
    public void CookingBeforeAndAfterFilesRestoreTheSameConversionAndReceipt()
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        Save(world, journal, content);
        var request = Request("cook-one", "cook");
        Assert.That(world.Produce(request, operation => Append(journal, operation)).Succeeded, Is.True);
        Save(world, journal, content);
        var before = TownWorld.RestoreCandidate(content, TownSaveFile.Read(SavePath + ".previous").WorldJson);
        var after = TownWorld.RestoreCandidate(content, TownSaveFile.Read(SavePath).WorldJson);
        Assert.That(before.Produce(request).Succeeded, Is.True);
        Assert.That(after.Produce(request).IsDuplicate, Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(TownJson.Serialize(before.State.Economy.Player), Is.EqualTo(TownJson.Serialize(after.State.Economy.Player)));
            Assert.That(TownJson.Serialize(before.State.Production), Is.EqualTo(TownJson.Serialize(after.State.Production)));
            Assert.That(after.State.Economy.Player.Inventory["meal"], Is.EqualTo(1));
            Assert.That(after.State.Economy.Player.Coins, Is.EqualTo(100));
        });
    }

    [Test]
    public void RejectedFishingSurvivesSaveWithoutAdvancingRandomSelection()
    {
        var content = Content(capacity: 5);
        var world = new TownWorld(content);
        using var journal = Journal(world);
        var invalid = new ProductionRequest("invalid-fish", "player", "pond", "fish", "unexpected");
        Assert.That(world.Produce(invalid).Succeeded, Is.False);
        Assert.That(world.Produce(Request("fish-full", "fish")).Code, Is.EqualTo("inventory_full"));
        Save(world, journal, content);
        var restored = TownWorld.RestoreCandidate(content, TownSaveFile.Read(SavePath).WorldJson);
        Assert.Multiple(() =>
        {
            Assert.That(restored.State.Production.FishingRandomState, Is.EqualTo(1U));
            Assert.That(restored.State.Production.FishingDrawCount, Is.Zero);
            Assert.That(restored.Produce(invalid).IsDuplicate, Is.True);
        });
        Assert.That(restored.Trade(new("sell-potatoes", "player", "shop", "potato", "sell", 2, 2)).Succeeded, Is.True);
        var caught = restored.Produce(Request("fish-after-sale", "fish"));
        Assert.That(caught.Succeeded, Is.True);
        Assert.That(caught.Roll, Is.EqualTo(69));
        Assert.That(caught.ResourceId, Is.EqualTo("trout"));
        Assert.That(restored.State.Production.FishingDrawCount, Is.EqualTo(1));
        using var resumedJournal = Journal(restored, "resumed");
        Save(restored, resumedJournal, content);
    }

    [Test]
    public void SavedEmptyCatchConsumesOneDrawAndResumesTheNextCatchExactlyOnce()
    {
        var content = Content(seed: 10);
        var world = new TownWorld(content);
        using var journal = Journal(world);
        var emptyRequest = Request("empty-cast", "fish");
        var empty = world.Produce(emptyRequest, operation => Append(journal, operation));
        Assert.Multiple(() =>
        {
            Assert.That(empty.Succeeded, Is.True);
            Assert.That(empty.Code, Is.EqualTo("no_catch"));
            Assert.That(empty.Roll, Is.EqualTo(90));
            Assert.That(empty.Quantity, Is.Zero);
            Assert.That(world.State.Production.FishingDrawCount, Is.EqualTo(1));
        });
        Save(world, journal, content);
        var restored = TownWorld.RestoreCandidate(content, TownSaveFile.Read(SavePath).WorldJson);
        Assert.That(restored.Produce(emptyRequest).IsDuplicate, Is.True);
        Assert.That(restored.State.Production.FishingRandomState, Is.EqualTo(2703690U));
        var next = Request("next-cast", "fish");
        var expected = world.Produce(next);
        var actual = restored.Produce(next);
        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(actual.Roll, Is.EqualTo(50));
            Assert.That(actual.ResourceId, Is.EqualTo("trout"));
            Assert.That(restored.State.Production.FishingDrawCount, Is.EqualTo(2));
            Assert.That(restored.State.Production.FishingRandomState, Is.EqualTo(671267850U));
            Assert.That(TownJson.Serialize(restored.State.Economy.Player), Is.EqualTo(TownJson.Serialize(world.State.Economy.Player)));
        });
        using var resumedJournal = Journal(restored, "resumed");
        Save(restored, resumedJournal, content);
    }

    [TestCase("missing-production")]
    [TestCase("missing-chickens")]
    [TestCase("missing-fed")]
    [TestCase("missing-random-version")]
    [TestCase("missing-random-state")]
    [TestCase("missing-draw-count")]
    [TestCase("missing-receipts")]
    [TestCase("random-version")]
    [TestCase("zero-random-state")]
    [TestCase("random-state-mismatch")]
    [TestCase("draw-count-mismatch")]
    [TestCase("fed-and-ready")]
    [TestCase("unknown-chicken")]
    [TestCase("receipt-identity")]
    [TestCase("receipt-sequence")]
    [TestCase("receipt-random-state")]
    [TestCase("old-schema")]
    [TestCase("content-fingerprint")]
    public void InvalidProductionSavePreservesLiveWorldAndBothPublishedFiles(string corruption)
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        Save(world, journal, content);
        Assert.That(world.Produce(Request("feed-one", "feed")).Succeeded, Is.True);
        Assert.That(world.Produce(Request("fish-one", "fish")).Succeeded, Is.True);
        var valid = Snapshot(world, journal);
        TownSaveFile.Write(SavePath, valid, saved => Validate(content, saved));
        byte[] current = File.ReadAllBytes(SavePath), previous = File.ReadAllBytes(SavePath + ".previous");
        string before = TownJson.Serialize(world.State);
        var json = JsonNode.Parse(valid.WorldJson)!;
        var production = json["state"]!["production"]!;
        switch (corruption)
        {
            case "missing-production": json["state"]!.AsObject().Remove("production"); break;
            case "missing-chickens": production.AsObject().Remove("chickens"); break;
            case "missing-fed": production["chickens"]![0]!.AsObject().Remove("fed"); break;
            case "missing-random-version": production.AsObject().Remove("fishingRandomVersion"); break;
            case "missing-random-state": production.AsObject().Remove("fishingRandomState"); break;
            case "missing-draw-count": production.AsObject().Remove("fishingDrawCount"); break;
            case "missing-receipts": production.AsObject().Remove("receipts"); break;
            case "random-version": production["fishingRandomVersion"] = "unknown-random-version"; break;
            case "zero-random-state": production["fishingRandomState"] = 0; break;
            case "random-state-mismatch": production["fishingRandomState"] = 270370; break;
            case "draw-count-mismatch": production["fishingDrawCount"] = 2; break;
            case "fed-and-ready": production["chickens"]![0]!["productReady"] = true; break;
            case "unknown-chicken": production["chickens"]![0]!["id"] = "unknown"; break;
            case "receipt-identity": production["receipts"]!["fish-one"]!["request"]!["operationId"] = "another"; break;
            case "receipt-sequence": production["receipts"]!["fish-one"]!["inputSequence"] = 1; break;
            case "receipt-random-state": production["receipts"]!["fish-one"]!["randomStateBefore"] = 2; break;
            case "old-schema": json["schemaVersion"] = 3; break;
            case "content-fingerprint": json["contentFingerprint"] = new string('0', 64); break;
        }
        var invalid = valid with { WorldJson = json.ToJsonString() };
        Assert.Throws<InvalidDataException>(() => TownSaveFile.Write(SavePath, invalid, saved => Validate(content, saved)));
        string corruptPath = Path.Combine(_directory, "invalid.json");
        File.WriteAllText(corruptPath, TownJson.Serialize(invalid));
        Assert.Throws<InvalidDataException>(() => Validate(content, TownSaveFile.Read(corruptPath)));
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(SavePath), Is.EqualTo(current));
            Assert.That(File.ReadAllBytes(SavePath + ".previous"), Is.EqualTo(previous));
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
            Assert.That(Directory.GetFiles(_directory, "*.tmp.*"), Is.Empty);
        });
    }

    [Test]
    public void ExportRetainsRequestsRejectionsRandomTransitionsAndCompleteVersionedCheckpoints()
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        journal.WriteArtifact("content.json", new { manifest = content.Manifest, definitions = content.Definitions });
        journal.WriteArtifact("initial-state.json", Snapshot(world, journal));
        var requests = new[] { Request("collect-too-soon", "collect"), Request("feed-one", "feed"),
            Request("fish-one", "fish"), Request("cook-one", "cook"), Request("fish-one", "fish") };
        var results = new List<ProductionResult>();
        var checkpoints = new List<string>();
        foreach (var request in requests)
        {
            results.Add(world.Produce(request, operation => Append(journal, operation)));
            checkpoints.Add(TownJson.Serialize(world.State));
        }
        journal.WriteArtifact("final-state.json", Snapshot(world, journal));
        string zipPath = Path.Combine(_directory, "production-run.zip");
        journal.ExportSnapshotZip(zipPath);
        using var archive = ZipFile.OpenRead(zipPath);
        var records = ReadOperations(ReadEntry(archive, "events.jsonl"));
        var final = TownJson.Deserialize<TownHostSnapshot>(ReadEntry(archive, "final-state.json"));

        Assert.Multiple(() =>
        {
            Assert.That(records.Select(record => record.SchemaVersion), Is.All.EqualTo(1));
            Assert.That(records.Select(record => record.InputSequence), Is.EqualTo(new long[] { 1, 2, 3, 4, 5 }));
            Assert.That(records.Select(record => record.Request), Is.EqualTo(requests));
            Assert.That(records.Select(record => record.Result), Is.EqualTo(results));
            Assert.That(results.Select(result => result.Succeeded), Is.EqualTo(new[] { false, true, true, true, true }));
            Assert.That(results.Last().IsDuplicate, Is.True);
            Assert.That(records.Select(record => TownJson.Serialize(record.Checkpoint)), Is.EqualTo(checkpoints));
            Assert.That(records.Select(record => record.RandomStateBefore), Is.EqualTo(new uint[] { 1, 1, 1, 270369, 270369 }));
            Assert.That(records.Select(record => record.RandomStateAfter), Is.EqualTo(new uint[] { 1, 1, 270369, 270369, 270369 }));
            Assert.That(final.JournalRecordCount, Is.EqualTo(5));
            Assert.That(JsonNode.Parse(ReadEntry(archive, "journal-export.json"))!["recordCount"]!.GetValue<long>(), Is.EqualTo(5));
        });
        foreach (var record in records)
        {
            Assert.That(record.BranchId, Is.EqualTo(record.Checkpoint.BranchId));
            Assert.That(record.WorldMinute, Is.EqualTo(record.Checkpoint.WorldMinute));
            Assert.That(record.Checkpoint.Production.FishingRandomVersion, Is.EqualTo("xorshift32-mod100-v1"));
            Assert.That(record.Checkpoint.Production.FishingRandomState, Is.EqualTo(record.RandomStateAfter));
            Assert.That(record.Checkpoint.Economy.InputSequence, Is.EqualTo(record.InputSequence));
            var checkpoint = world.CaptureSnapshot() with { State = record.Checkpoint };
            Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, TownJson.Serialize(checkpoint)));
        }
        Validate(content, final);
    }

    private void Save(TownWorld world, TownRunJournal journal, TownContent content) =>
        TownSaveFile.Write(SavePath, Snapshot(world, journal), saved => Validate(content, saved));
    private TownRunJournal Journal(TownWorld world, string root = "runs") => new(Path.Combine(_directory, root), world.State.BranchId,
        new { schemaVersion = 1, rulesVersion = TownWorld.RulesVersion, numericVersion = TownWorld.NumericVersion });
    private static TownHostSnapshot Snapshot(TownWorld world, TownRunJournal journal) =>
        new(1, world.CaptureJson(), world.State.BranchId, journal.RecordCount, .025, "rules", "baseline", []);
    private static void Validate(TownContent content, TownHostSnapshot snapshot) => _ = TownWorld.RestoreCandidate(content, snapshot.WorldJson);
    private static void Append(TownRunJournal journal, ProductionOperationRecord operation) =>
        journal.Append("player_production_command", new { schemaVersion = 1, operation });
    private static ProductionRequest Request(string operationId, string action) =>
        new(operationId, "player", action is "feed" or "collect" ? "hen" : action == "fish" ? "pond" : "kitchen", action,
            action == "cook" ? "baked-potato" : null);
    private static ProductionOperationRecord[] ReadOperations(string jsonl) => jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => TownJson.Deserialize<ProductionOperationRecord>(JsonNode.Parse(line)!["data"]!["operation"]!.ToJsonString())).ToArray();
    private static string ReadJournal(TownRunJournal journal)
    {
        using var stream = new FileStream(Path.Combine(journal.DirectoryPath, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static string ReadEntry(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
    private static TownContent Content(int capacity = 12, uint seed = 1)
    {
        var places = new[] { new PlaceDefinition("a", "A", new(60, 60), "outside"), new PlaceDefinition("b", "B", new(100, 60), "outside"),
            new PlaceDefinition("c", "C", new(60, 100), "outside"), new PlaceDefinition("d", "D", new(100, 100), "outside") };
        var residents = new[] { "mina", "eli", "ren", "sora" }.Select((id, index) => new ResidentDefinition(id, id, places[index].Id, places[index].Id,
            "apple", new() { ["apple"] = 3 }, 5, [new("free", 0, 1440, places[index].Id, "free", true)])).ToArray();
        var resources = new[] { "apple", "feed", "egg", "carp", "trout", "bass", "potato", "salt", "flour", "meal" }
            .Select(id => new ResourceDefinition(id, id)).ToArray();
        var economy = new EconomyDefinition(new(100, capacity, new() { ["feed"] = 2, ["potato"] = 2, ["salt"] = 2, ["apple"] = 1, ["flour"] = 1 }),
            [new("shop", "General store", "a", 100, [], [new("potato", 0, 2)])]);
        var production = new ProductionDefinition([new("hen", "Hen", "a", "feed", "egg", 1)],
            [new("pond", "Pond", "a")], [new("carp", "carp", 0, 40), new("trout", "trout", 40, 70), new("bass", "bass", 70, 90)],
            [new("kitchen", "Kitchen", "a")], [new("baked-potato", "Baked potato", [new("potato", 1), new("salt", 1)], "meal", 1)], seed);
        return TownContent.Create(new(256, 256, places, [], [new("outside", new(0, 0, 256, 256))]),
            new(residents, resources, 299, economy, new([], [], "a", []), production));
    }
}
