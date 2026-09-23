using System.IO.Compression;
using System.Text.Json.Nodes;
using CozyTown.Core.Town;
using CozyTown.Game;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

[TestFixture]
public sealed class TownEconomyPersistenceTests
{
    private string _directory = null!;
    private string SavePath => Path.Combine(_directory, "save.json");

    [SetUp]
    public void SetUp() => _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "cozytown-economy-host-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    [TearDown]
    public void TearDown()
    {
        string full = Path.GetFullPath(_directory);
        if (!Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("cozytown-economy-host-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Temporary test directory is outside its cleanup boundary.");
        Directory.Delete(full, true);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void UnavailableJournalLeavesWholeStateAndOperationIdentityUnchanged(bool failedJournal, bool selection)
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        journal.Append("confirmed", new { });
        if (failedJournal)
        {
            Directory.CreateDirectory(Path.Combine(journal.DirectoryPath, "blocked.json"));
            Assert.Throws<IOException>(() => journal.WriteArtifact("blocked.json", new { }));
            Assert.That(journal.Failed, Is.True);
        }
        else journal.Dispose();

        string before = TownJson.Serialize(world.State);
        string prefix = ReadJournal(journal);
        var request = Request("retryable-operation", "buy", 2);
        int persistenceCalls = 0;
        void Persist(EconomyOperationRecord operation)
        {
            persistenceCalls++;
            AppendOperation(journal, world, content, operation);
        }

        if (selection) Assert.Throws<IOException>(() => world.SelectQuickbar("retryable-operation", 4, Persist));
        else Assert.Throws<IOException>(() => world.Trade(request, Persist));

        Assert.Multiple(() =>
        {
            Assert.That(persistenceCalls, Is.EqualTo(1));
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before), "Assets, receipts and both world sequences must all remain unchanged.");
            Assert.That(ReadJournal(journal), Is.EqualTo(prefix));
            Assert.That(journal.RecordCount, Is.EqualTo(1));
        });

        using var retryJournal = Journal(world, "retry");
        if (selection)
        {
            var retry = world.SelectQuickbar("retryable-operation", 4, operation => AppendOperation(retryJournal, world, content, operation));
            Assert.That(retry.Succeeded && !retry.IsDuplicate, Is.True);
        }
        else
        {
            var retry = world.Trade(request, operation => AppendOperation(retryJournal, world, content, operation));
            Assert.That(retry.Succeeded && !retry.IsDuplicate, Is.True);
        }
        Assert.That(retryJournal.RecordCount, Is.EqualTo(1));
        Assert.That(world.State.Economy.Receipts.Keys, Is.EquivalentTo(new[] { "retryable-operation" }));
    }

    [Test]
    public void DiskPublicationFailureDuringCallbackDoesNotCommitTrade()
    {
        var world = new TownWorld(Content());
        using var journal = Journal(world);
        journal.Append("confirmed", new { });
        string before = TownJson.Serialize(world.State);
        string prefix = ReadJournal(journal);
        Directory.CreateDirectory(Path.Combine(journal.DirectoryPath, "operation.json"));

        Assert.Throws<IOException>(() => world.Trade(Request("disk-failure", "buy", 2),
            operation => journal.WriteArtifact("operation.json", operation)));

        Assert.Multiple(() =>
        {
            Assert.That(journal.Failed, Is.True);
            Assert.That(journal.FailureCode, Is.EqualTo("journal_write_failed"));
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
            Assert.That(ReadJournal(journal), Is.EqualTo(prefix));
            Assert.That(Directory.GetFiles(journal.DirectoryPath, "*.tmp.*"), Is.Empty);
        });
    }

    [TestCase("buy")]
    [TestCase("sell")]
    public void DurableTradeRecordExistsBeforeLiveStateIsPublished(string direction)
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        string before = TownJson.Serialize(world.State);
        EconomyOperationRecord? persisted = null;
        var request = Request("durable-operation", direction, 2);

        var result = world.Trade(request, operation =>
        {
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
            AppendOperation(journal, world, content, operation);
            Assert.That(journal.RecordCount, Is.EqualTo(1));
            var durable = JsonNode.Parse(ReadJournal(journal).Trim())!["data"]!["operation"]!;
            persisted = TownJson.Deserialize<EconomyOperationRecord>(durable.ToJsonString());
            Assert.That(persisted.Trade, Is.EqualTo(request));
            Assert.That(persisted.TradeResult!.Succeeded, Is.True);
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(persisted, Is.Not.Null);
            Assert.That(TownJson.Serialize(world.State.Economy), Is.EqualTo(TownJson.Serialize(persisted!.Checkpoint)));
            Assert.That(persisted.TradeResult, Is.EqualTo(result));
            Assert.That(world.State.Economy.Player.Coins + world.State.Economy.Shops.Single().Coins, Is.EqualTo(200));
            Assert.That(world.State.Economy.Player.Inventory["apple"] + world.State.Economy.Shops.Single().Inventory["apple"], Is.EqualTo(23));
        });
    }

    [Test]
    public void SavedBuySellAndQuickbarRestoreTheirAssetsAndOperationDedupe()
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        TownSaveFile.Write(SavePath, Snapshot(world, journal), snapshot => ValidateWorld(content, snapshot));
        var initial = TownSaveFile.Read(SavePath);
        var buy = Request("buy-1", "buy", 2);
        var sell = Request("sell-1", "sell", 1);
        Assert.That(world.Trade(buy, operation => AppendOperation(journal, world, content, operation)).Succeeded, Is.True);
        Assert.That(world.Trade(sell, operation => AppendOperation(journal, world, content, operation)).Succeeded, Is.True);
        Assert.That(world.SelectQuickbar("select-1", 4, operation => AppendOperation(journal, world, content, operation)).Succeeded, Is.True);
        string expectedEconomy = TownJson.Serialize(world.State.Economy);

        TownSaveFile.Write(SavePath, Snapshot(world, journal), snapshot => ValidateWorld(content, snapshot));
        var saved = TownSaveFile.Read(SavePath);
        var restored = TownWorld.RestoreCandidate(content, saved.WorldJson);
        Assert.Multiple(() =>
        {
            Assert.That(saved.JournalRecordCount, Is.EqualTo(3));
            Assert.That(TownJson.Serialize(TownSaveFile.Read(SavePath + ".previous")), Is.EqualTo(TownJson.Serialize(initial)));
            Assert.That(TownJson.Serialize(restored.State.Economy), Is.EqualTo(expectedEconomy));
            Assert.That(restored.State.Economy.Player.Coins, Is.EqualTo(94));
            Assert.That(restored.State.Economy.Player.Inventory["apple"], Is.EqualTo(4));
            Assert.That(restored.State.Economy.Player.SelectedQuickbarIndex, Is.EqualTo(4));
            Assert.That(restored.State.BranchId, Is.Not.EqualTo(world.State.BranchId));
        });

        using var resumedJournal = Journal(restored, "resumed");
        var beforeDuplicates = restored.State.Economy;
        void PersistDuplicate(EconomyOperationRecord operation) => AppendOperation(resumedJournal, restored, content, operation);
        Assert.That(restored.Trade(buy, PersistDuplicate).IsDuplicate, Is.True);
        Assert.That(restored.Trade(sell, PersistDuplicate).IsDuplicate, Is.True);
        Assert.That(restored.SelectQuickbar("select-1", 4, PersistDuplicate).IsDuplicate, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(resumedJournal.RecordCount, Is.EqualTo(3), "Repeated input remains observable without repeating its asset transfer.");
            Assert.That(restored.State.Economy.InputSequence, Is.EqualTo(6));
            Assert.That(TownJson.Serialize(restored.State.Economy.Player), Is.EqualTo(TownJson.Serialize(beforeDuplicates.Player)));
            Assert.That(TownJson.Serialize(restored.State.Economy.Shops), Is.EqualTo(TownJson.Serialize(beforeDuplicates.Shops)));
            Assert.That(TownJson.Serialize(restored.State.Economy.Receipts), Is.EqualTo(TownJson.Serialize(beforeDuplicates.Receipts)));
        });

        var next = restored.Trade(buy with { OperationId = "buy-2" }, operation => AppendOperation(resumedJournal, restored, content, operation));
        Assert.That(next.Succeeded && !next.IsDuplicate, Is.True, "A fresh user operation must remain possible after recovery.");
        Assert.That(restored.State.Economy.InputSequence, Is.EqualTo(7));
        Assert.That(resumedJournal.RecordCount, Is.EqualTo(4));
    }

    [TestCase("missing-economy")]
    [TestCase("missing-receipts")]
    [TestCase("negative-coins")]
    [TestCase("unknown-shop")]
    [TestCase("receipt-identity")]
    [TestCase("input-sequence")]
    [TestCase("old-schema")]
    public void InvalidEconomySaveCannotReplaceEitherPublishedFile(string corruption)
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        TownSaveFile.Write(SavePath, Snapshot(world, journal), snapshot => ValidateWorld(content, snapshot));
        Assert.That(world.Trade(Request("buy-1", "buy", 2), operation => AppendOperation(journal, world, content, operation)).Succeeded, Is.True);
        var valid = Snapshot(world, journal);
        TownSaveFile.Write(SavePath, valid, snapshot => ValidateWorld(content, snapshot));
        byte[] current = File.ReadAllBytes(SavePath), previous = File.ReadAllBytes(SavePath + ".previous");
        string liveState = TownJson.Serialize(world.State);
        var json = JsonNode.Parse(valid.WorldJson)!;
        var economy = json["state"]!["economy"]!;
        switch (corruption)
        {
            case "missing-economy": json["state"]!.AsObject().Remove("economy"); break;
            case "missing-receipts": economy.AsObject().Remove("receipts"); break;
            case "negative-coins": economy["player"]!["coins"] = -1; break;
            case "unknown-shop": economy["shops"]![0]!["id"] = "other-shop"; break;
            case "receipt-identity": economy["receipts"]!["buy-1"]!["trade"]!["operationId"] = "other-operation"; break;
            case "input-sequence": economy["inputSequence"] = 0; break;
            case "old-schema": json["schemaVersion"] = 1; break;
        }
        var invalid = valid with { WorldJson = json.ToJsonString() };

        Assert.Throws<InvalidDataException>(() => TownSaveFile.Write(SavePath, invalid, snapshot => ValidateWorld(content, snapshot)));
        string corruptPath = Path.Combine(_directory, "corrupt.json");
        File.WriteAllText(corruptPath, TownJson.Serialize(invalid));
        Assert.Throws<InvalidDataException>(() => ValidateWorld(content, TownSaveFile.Read(corruptPath)));
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(SavePath), Is.EqualTo(current));
            Assert.That(File.ReadAllBytes(SavePath + ".previous"), Is.EqualTo(previous));
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(liveState));
            Assert.That(Directory.GetFiles(_directory, "*.tmp.*"), Is.Empty);
        });
    }

    [Test]
    public void ExportIncludesSuccessfulAndRejectedRequestsResultsAndEconomicCheckpoints()
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        journal.WriteArtifact("content.json", new { manifest = content.Manifest, definitions = content.Definitions });
        journal.WriteArtifact("initial-state.json", Snapshot(world, journal));
        var requests = new[] { Request("buy-1", "buy", 2), Request("sell-1", "sell", 1), Request("rejected-1", "buy", 1000) };
        var results = requests.Select(request => world.Trade(request, operation => AppendOperation(journal, world, content, operation))).ToArray();
        var selection = world.SelectQuickbar("select-1", 4, operation => AppendOperation(journal, world, content, operation));
        journal.WriteArtifact("final-state.json", Snapshot(world, journal));
        string zipPath = Path.Combine(_directory, "economy-run.zip");

        journal.ExportSnapshotZip(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        string[] lines = ReadEntry(archive, "events.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var entries = lines.Select(line => JsonNode.Parse(line)!).ToArray();
        var operations = entries.Select(entry => TownJson.Deserialize<EconomyOperationRecord>(entry["data"]!["operation"]!.ToJsonString())).ToArray();
        var metadata = JsonNode.Parse(ReadEntry(archive, "journal-export.json"))!;
        var final = TownJson.Deserialize<TownHostSnapshot>(ReadEntry(archive, "final-state.json"));
        var restored = TownWorld.RestoreCandidate(content, final.WorldJson);
        Assert.Multiple(() =>
        {
            Assert.That(entries, Has.Length.EqualTo(4));
            Assert.That(entries.All(entry => entry["kind"]!.GetValue<string>() == "player_economy_command"), Is.True);
            Assert.That(entries.Select(entry => entry["sequence"]!.GetValue<long>()), Is.EqualTo(new long[] { 1, 2, 3, 4 }));
            Assert.That(operations.Select(operation => operation.InputSequence), Is.EqualTo(new long[] { 1, 2, 3, 4 }));
            Assert.That(operations.Take(3).Select(operation => operation.Trade), Is.EqualTo(requests));
            Assert.That(operations.Take(3).Select(operation => operation.TradeResult), Is.EqualTo(results));
            Assert.That(results.Select(result => result.Succeeded), Is.EqualTo(new[] { true, true, false }));
            Assert.That(operations[3].SelectionResult, Is.EqualTo(selection));
            Assert.That(operations[3].Selection!.Index, Is.EqualTo(4));
            Assert.That(TownJson.Serialize(operations[3].Checkpoint), Is.EqualTo(TownJson.Serialize(restored.State.Economy)));
            Assert.That(metadata["recordCount"]!.GetValue<long>(), Is.EqualTo(4));
            Assert.That(final.JournalRecordCount, Is.EqualTo(4));
            Assert.That(archive.GetEntry("content.json"), Is.Not.Null);
            Assert.That(archive.GetEntry("initial-state.json"), Is.Not.Null);
        });
        for (int i = 0; i < operations.Length; i++)
        {
            Assert.That(operations[i].Checkpoint.InputSequence, Is.EqualTo(i + 1));
            Assert.That(operations[i].Checkpoint.Receipts.Count, Is.EqualTo(i + 1));
            Assert.That(entries[i]["data"]!["contentFingerprint"]!.GetValue<string>(), Is.EqualTo(content.Fingerprint));
        }
        using var resumedJournal = Journal(restored, "resumed");
        var originalReceipt = restored.State.Economy.Receipts[requests[2].OperationId];
        var repeatedRejection = restored.Trade(requests[2], operation => AppendOperation(resumedJournal, restored, content, operation));
        Assert.That(repeatedRejection.IsDuplicate && !repeatedRejection.Succeeded, Is.True);
        Assert.That(restored.State.Economy.Receipts[requests[2].OperationId], Is.EqualTo(originalReceipt));
        Assert.That(resumedJournal.RecordCount, Is.EqualTo(1));
    }

    private TownRunJournal Journal(TownWorld world, string root = "runs") => new(Path.Combine(_directory, root), world.State.BranchId,
        new { schemaVersion = 1, rulesVersion = TownWorld.RulesVersion, numericVersion = TownWorld.NumericVersion });

    private static TownHostSnapshot Snapshot(TownWorld world, TownRunJournal journal) =>
        new(1, world.CaptureJson(), world.State.BranchId, journal.RecordCount, 0, "rules", "baseline", []);

    private static void ValidateWorld(TownContent content, TownHostSnapshot snapshot) => _ = TownWorld.RestoreCandidate(content, snapshot.WorldJson);

    private static TradeRequest Request(string id, string direction, int quantity) => new(id, "player", "shop.general", "apple", direction, quantity, direction == "buy" ? 4 : 2);

    private static void AppendOperation(TownRunJournal journal, TownWorld world, TownContent content, EconomyOperationRecord operation) =>
        journal.Append("player_economy_command", new
        {
            schemaVersion = 1, operation, contentFingerprint = content.Fingerprint,
            worldSequenceBefore = world.State.Sequence, unconsumedSeconds = 0,
            playerRoute = (TownPoint[]?)null, playerRouteCursor = 0, pauseReasons = new[] { "shop" }
        });

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

    private static TownContent Content()
    {
        var places = new[] { new PlaceDefinition("a", "A", new(60, 60), "outside"), new PlaceDefinition("b", "B", new(100, 60), "outside"),
            new PlaceDefinition("c", "C", new(60, 100), "outside"), new PlaceDefinition("d", "D", new(100, 100), "outside") };
        var residents = new[] { "mina", "eli", "ren", "sora" }.Select((id, i) => new ResidentDefinition(id, id, places[i].Id, places[i].Id,
            "apple", new() { ["apple"] = 3 }, 5, [new("free", 0, 1440, places[i].Id, "free", true)])).ToArray();
        var economy = new EconomyDefinition(new(100, 5, new() { ["apple"] = 3 }),
            [new("shop.general", "General store", "a", 100, new() { ["apple"] = 20 }, [new("apple", 4, 2)])]);
        return TownContent.Create(new(256, 256, places, [], [new("outside", new(0, 0, 256, 256))]),
            new(residents, [new("apple", "Apple", 10)], 540, economy));
    }
}
