using System.IO.Compression;
using System.Text.Json.Nodes;
using CozyTown.Core.Town;
using CozyTown.Game;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

[TestFixture]
public sealed class TownFarmingPersistenceTests
{
    private string _directory = null!;
    private string SavePath => Path.Combine(_directory, "save.json");

    [SetUp]
    public void SetUp() => _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "cozytown-farming-host-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    [TearDown]
    public void TearDown()
    {
        string full = Path.GetFullPath(_directory);
        if (!Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("cozytown-farming-host-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Temporary test directory is outside its cleanup boundary.");
        Directory.Delete(full, true);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void UnavailableJournalDoesNotPublishPlantOrSleep(bool interrupted, bool sleep)
    {
        var world = new TownWorld(Content());
        using var journal = Journal(world);
        PlantAndWater(world);
        journal.Append("confirmed", new { });
        if (interrupted)
        {
            Directory.CreateDirectory(Path.Combine(journal.DirectoryPath, "blocked.json"));
            Assert.Throws<IOException>(() => journal.WriteArtifact("blocked.json", new { }));
        }
        else journal.Dispose();
        string before = TownJson.Serialize(world.State);
        string prefix = ReadJournal(journal);
        int callbacks = 0;

        if (sleep)
            Assert.Throws<IOException>(() => world.Sleep(new("retryable", "player", 1), persistBeforeCommit: operation =>
            { callbacks++; AppendSleep(journal, operation); }));
        else
            Assert.Throws<IOException>(() => world.Farm(new("retryable", "player", "plot.two", "plant", "carrot"), operation =>
            { callbacks++; AppendFarm(journal, operation); }));

        Assert.Multiple(() =>
        {
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
            Assert.That(ReadJournal(journal), Is.EqualTo(prefix));
            Assert.That(journal.RecordCount, Is.EqualTo(1));
        });
        using var retryJournal = Journal(world, "retry");
        bool accepted = sleep
            ? world.Sleep(new("retryable", "player", 1), persistBeforeCommit: operation => AppendSleep(retryJournal, operation)).Succeeded
            : world.Farm(new("retryable", "player", "plot.two", "plant", "carrot"), operation => AppendFarm(retryJournal, operation)).Succeeded;
        Assert.That(accepted, Is.True, "Failed persistence must not consume the operation identity.");
        Assert.That(retryJournal.RecordCount, Is.EqualTo(1));
    }

    [Test]
    public void DurableSleepCheckpointPrecedesPublicationOfProductionAndShopReplacement()
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        PlantAndWater(world);
        string before = TownJson.Serialize(world.State);
        SleepOperationRecord? persisted = null;
        var request = new SleepRequest("sleep-one", "player", 1);

        var result = world.Sleep(request, persistBeforeCommit: operation =>
        {
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
            AppendSleep(journal, operation);
            persisted = TownJson.Deserialize<SleepOperationRecord>(JsonNode.Parse(ReadJournal(journal).Trim())!["data"]!["operation"]!.ToJsonString());
            Assert.That(persisted.Request, Is.EqualTo(request));
            Assert.That(persisted.Checkpoint.Farming.LastSettledDay, Is.EqualTo(0));
            Assert.That(persisted.Checkpoint.Farming.Plots[0].Mature, Is.True);
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(persisted, Is.Not.Null);
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(TownJson.Serialize(persisted!.Checkpoint)));
            Assert.That(world.WorldMinute, Is.EqualTo(359));
            Assert.That(world.State.Economy.Shops.Single().Coins, Is.EqualTo(100));
            Assert.That(world.State.Economy.Shops.Single().Inventory["seed"], Is.EqualTo(4));
        });
        TownSaveFile.Write(SavePath, Snapshot(world, journal), saved => Validate(content, saved));
    }

    [Test]
    public void DiskFailureWhilePersistingSleepPreservesBothPublishedSavesAndLiveWorld()
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        TownSaveFile.Write(SavePath, Snapshot(world, journal), saved => Validate(content, saved));
        PlantAndWater(world);
        TownSaveFile.Write(SavePath, Snapshot(world, journal), saved => Validate(content, saved));
        byte[] current = File.ReadAllBytes(SavePath), previous = File.ReadAllBytes(SavePath + ".previous");
        string before = TownJson.Serialize(world.State);
        Directory.CreateDirectory(Path.Combine(journal.DirectoryPath, "sleep.json"));

        Assert.Throws<IOException>(() => world.Sleep(new("sleep-one", "player", 1),
            persistBeforeCommit: operation => journal.WriteArtifact("sleep.json", operation)));

        Assert.Multiple(() =>
        {
            Assert.That(journal.Failed, Is.True);
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
            Assert.That(File.ReadAllBytes(SavePath), Is.EqualTo(current));
            Assert.That(File.ReadAllBytes(SavePath + ".previous"), Is.EqualTo(previous));
            Assert.That(Directory.GetFiles(journal.DirectoryPath, "*.tmp.*"), Is.Empty);
        });
    }

    [Test]
    public void BeforeAndAfterMorningSavesResumeGrowthAndHarvestExactlyOnce()
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        PlantAndWater(world);
        TownSaveFile.Write(SavePath, Snapshot(world, journal), saved => Validate(content, saved));
        var beforeMorning = TownSaveFile.Read(SavePath);
        Assert.That(world.Sleep(new("sleep-one", "player", 1)).Succeeded, Is.True);
        TownSaveFile.Write(SavePath, Snapshot(world, journal), saved => Validate(content, saved));
        var afterMorning = TownSaveFile.Read(SavePath);
        Assert.That(TownJson.Serialize(TownSaveFile.Read(SavePath + ".previous")), Is.EqualTo(TownJson.Serialize(beforeMorning)));

        var continuedBefore = TownWorld.RestoreCandidate(content, beforeMorning.WorldJson);
        continuedBefore.Advance(60, 0);
        var continuedAfter = TownWorld.RestoreCandidate(content, afterMorning.WorldJson);
        var beforeRepeat = continuedAfter.State;
        var repeat = continuedAfter.Sleep(new("sleep-one", "player", 1));
        Assert.Multiple(() =>
        {
            Assert.That(repeat.Succeeded && repeat.IsDuplicate, Is.True);
            Assert.That(continuedAfter.WorldMinute, Is.EqualTo(beforeRepeat.WorldMinute));
            Assert.That(continuedAfter.State.Farming.Mornings, Has.Count.EqualTo(1));
            Assert.That(TownJson.Serialize(continuedAfter.State.Farming.Plots), Is.EqualTo(TownJson.Serialize(continuedBefore.State.Farming.Plots)));
            Assert.That(TownJson.Serialize(continuedAfter.State.Economy.Shops), Is.EqualTo(TownJson.Serialize(continuedBefore.State.Economy.Shops)));
        });
        var harvest = new FarmRequest("harvest-one", "player", "plot.one", "harvest");
        Assert.That(continuedAfter.Farm(harvest).Succeeded, Is.True);
        using var resumedJournal = Journal(continuedAfter, "resumed");
        TownSaveFile.Write(SavePath, Snapshot(continuedAfter, resumedJournal), saved => Validate(content, saved));
        var restored = TownWorld.RestoreCandidate(content, TownSaveFile.Read(SavePath).WorldJson);
        var inventory = TownJson.Serialize(restored.State.Economy.Player.Inventory);
        Assert.That(restored.Farm(harvest).IsDuplicate, Is.True);
        Assert.That(TownJson.Serialize(restored.State.Economy.Player.Inventory), Is.EqualTo(inventory));
        Assert.That(restored.State.Economy.Player.Inventory["carrot"], Is.EqualTo(2));
        Assert.That(restored.State.Farming.Plots[0].HarvestedCount, Is.EqualTo(1));
    }

    [Test]
    public void SavedAcceptedTimeSurvivesPauseAndCrossesMorningOnlyOnce()
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        PlantAndWater(world);
        world.Advance(.75, 0);
        world.Advance(5e-9, 0);
        Assert.That(world.State.AcceptedWorldMinutes, Is.GreaterThan(0));
        TownSaveFile.Write(SavePath, Snapshot(world, journal), saved => Validate(content, saved));
        var restored = TownWorld.RestoreCandidate(content, TownSaveFile.Read(SavePath).WorldJson);
        double accepted = restored.State.AcceptedWorldMinutes;
        restored.Advance(60, 7, paused: true);
        Assert.Multiple(() =>
        {
            Assert.That(restored.WorldMinute, Is.EqualTo(299.75));
            Assert.That(restored.State.AcceptedWorldMinutes, Is.EqualTo(accepted));
            Assert.That(restored.State.Farming.LastSettledDay, Is.EqualTo(-1));
        });
        restored.Advance(.25, 0);
        Assert.That(restored.State.Farming.LastSettledDay, Is.EqualTo(0));
        Assert.That(restored.State.Farming.Mornings, Has.Count.EqualTo(1));
        restored.Advance(.25, 0);
        Assert.That(restored.State.Farming.Mornings, Has.Count.EqualTo(1));
        Assert.That(restored.State.AcceptedWorldMinutes, Is.EqualTo(accepted).Within(1e-15));
        using var resumedJournal = Journal(restored, "resumed");
        TownSaveFile.Write(SavePath, Snapshot(restored, resumedJournal), saved => Validate(content, saved));
    }

    [Test]
    public async Task SleepingDoesNotSpendWorldHoursAsModelWaitOrBypassPausedDispatch()
    {
        var world = new TownWorld(Content());
        world.Advance(.001, 0);
        double startMinute = world.WorldMinute;
        var request = world.GetDecisionRequests().First();
        using var gate = new ModelDispatchGate();
        int callbacks = 0;
        Task dispatch = gate.EnqueueAsync(world.SessionId, () =>
        { callbacks++; return world.MarkDispatched(request.LogicalRequestId, "attempt"); }, TimeSpan.FromSeconds(60));
        gate.Tick(world.SessionId, paused: true, unavailable: false);
        var blocked = world.Sleep(new("blocked-sleep", "player", 1), paused: true);
        Assert.That(blocked.Succeeded, Is.False);
        Assert.That(world.WorldMinute, Is.EqualTo(startMinute));
        Assert.That(world.Sleep(new("sleep-one", "player", 1)).Succeeded, Is.True);
        var pending = world.State.PendingDecisions.Single(p => p.Request.LogicalRequestId == request.LogicalRequestId);
        Assert.Multiple(() =>
        {
            Assert.That(world.WorldMinute, Is.EqualTo(startMinute + 60).Within(1e-8));
            Assert.That(pending.Request.RemainingWaitSeconds, Is.EqualTo(60));
            Assert.That(pending.WaitedSeconds, Is.Zero);
            Assert.That(dispatch.IsCompleted, Is.False);
            Assert.That(callbacks, Is.Zero);
            Assert.That(pending.Request.AttemptId, Is.Null);
        });
        world.Advance(0, 7, paused: true);
        gate.Tick(world.SessionId, paused: true, unavailable: false);
        Assert.That(world.State.PendingDecisions.Single(p => p.Request.LogicalRequestId == request.LogicalRequestId).Request.RemainingWaitSeconds, Is.EqualTo(53));
        Assert.That(callbacks, Is.Zero);
        gate.Tick(world.SessionId, paused: false, unavailable: false);
        await dispatch;
        Assert.That(callbacks, Is.EqualTo(1));
    }

    [TestCase("missing-farming")]
    [TestCase("missing-mornings")]
    [TestCase("missing-watered")]
    [TestCase("unknown-crop")]
    [TestCase("settlement-cursor")]
    [TestCase("maturity")]
    [TestCase("receipt-identity")]
    public void InvalidFarmingSavePreservesLiveStateAndBothPublishedFiles(string corruption)
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        PlantAndWater(world);
        TownSaveFile.Write(SavePath, Snapshot(world, journal), saved => Validate(content, saved));
        Assert.That(world.Sleep(new("sleep-one", "player", 1)).Succeeded, Is.True);
        var valid = Snapshot(world, journal);
        TownSaveFile.Write(SavePath, valid, saved => Validate(content, saved));
        byte[] current = File.ReadAllBytes(SavePath), previous = File.ReadAllBytes(SavePath + ".previous");
        string before = TownJson.Serialize(world.State);
        var json = JsonNode.Parse(valid.WorldJson)!;
        var farming = json["state"]!["farming"]!;
        switch (corruption)
        {
            case "missing-farming": json["state"]!.AsObject().Remove("farming"); break;
            case "missing-mornings": farming.AsObject().Remove("mornings"); break;
            case "missing-watered": farming["plots"]![0]!.AsObject().Remove("watered"); break;
            case "unknown-crop": farming["plots"]![0]!["cropId"] = "unknown"; break;
            case "settlement-cursor": farming["lastSettledDay"] = 1; break;
            case "maturity": farming["plots"]![0]!["mature"] = false; break;
            case "receipt-identity": farming["sleepReceipts"]!["sleep-one"]!["request"]!["operationId"] = "another"; break;
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
    public void ExportContainsRejectedFarmRequestsSleepAndWholeWorldCheckpoints()
    {
        var content = Content();
        var world = new TownWorld(content);
        using var journal = Journal(world);
        journal.WriteArtifact("content.json", new { manifest = content.Manifest, definitions = content.Definitions });
        journal.WriteArtifact("initial-state.json", Snapshot(world, journal));
        var requests = new[]
        {
            new FarmRequest("harvest-too-soon", "player", "plot.one", "harvest"),
            new FarmRequest("plant-one", "player", "plot.one", "plant", "carrot"),
            new FarmRequest("water-one", "player", "plot.one", "water")
        };
        var results = requests.Select(request => world.Farm(request, operation => AppendFarm(journal, operation))).ToArray();
        var sleep = world.Sleep(new("sleep-one", "player", 1), persistBeforeCommit: operation => AppendSleep(journal, operation));
        journal.WriteArtifact("final-state.json", Snapshot(world, journal));
        string zipPath = Path.Combine(_directory, "farming-run.zip");
        journal.ExportSnapshotZip(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var entries = ReadEntry(archive, "events.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
        var farms = entries.Take(3).Select(entry => TownJson.Deserialize<FarmOperationRecord>(entry["data"]!["operation"]!.ToJsonString())).ToArray();
        var sleepRecord = TownJson.Deserialize<SleepOperationRecord>(entries[3]["data"]!["operation"]!.ToJsonString());
        var final = TownJson.Deserialize<TownHostSnapshot>(ReadEntry(archive, "final-state.json"));
        Assert.Multiple(() =>
        {
            Assert.That(entries.Select(entry => entry["sequence"]!.GetValue<long>()), Is.EqualTo(new long[] { 1, 2, 3, 4 }));
            Assert.That(farms.Select(operation => operation.Request), Is.EqualTo(requests));
            Assert.That(farms.Select(operation => operation.Result), Is.EqualTo(results));
            Assert.That(results.Select(result => result.Succeeded), Is.EqualTo(new[] { false, true, true }));
            Assert.That(sleepRecord.Result, Is.EqualTo(sleep));
            Assert.That(sleepRecord.Checkpoint.Farming.Mornings, Has.Count.EqualTo(1));
            Assert.That(TownJson.Serialize(sleepRecord.Checkpoint), Is.EqualTo(TownJson.Serialize(TownJson.Deserialize<TownSnapshot>(final.WorldJson).State)));
            Assert.That(final.JournalRecordCount, Is.EqualTo(4));
            Assert.That(JsonNode.Parse(ReadEntry(archive, "journal-export.json"))!["recordCount"]!.GetValue<long>(), Is.EqualTo(4));
        });
        Validate(content, final);
    }

    [Test]
    public void CapacityFixtureRulesStageRoundTripsThroughPublishedSave()
    {
        var content = CapacityContent();
        var world = new TownWorld(content, "farming-capacity-fixture");
        using var journal = Journal(world);
        PlantAndWater(world);
        var snapshot = Snapshot(world, journal) with { Stage = "farming-capacity-fixture" };

        TownSaveFile.Write(SavePath, snapshot, saved => Validate(content, saved));
        var saved = TownSaveFile.Read(SavePath);
        var restored = TownWorld.RestoreCandidate(content, saved.WorldJson);

        Assert.Multiple(() =>
        {
            Assert.That(saved.Stage, Is.EqualTo("farming-capacity-fixture"));
            Assert.That(saved.DecisionMode, Is.EqualTo("rules"));
            Assert.That(saved.WorldJson, Is.EqualTo(snapshot.WorldJson));
            Assert.That(restored.State.InitialStateId, Is.EqualTo("farming-capacity-fixture"));
            Assert.That(restored.State.ParentBranchId, Is.EqualTo(world.State.BranchId));
            Assert.That(restored.State.BranchId, Is.Not.EqualTo(world.State.BranchId));
            Assert.That(restored.State.Economy.Player.SlotCapacity, Is.EqualTo(5));
            Assert.That(restored.State.Economy.Player.Coins, Is.EqualTo(300));
            Assert.That(restored.State.Economy.Player.Inventory["seed.carrot"], Is.EqualTo(1));
            Assert.That(restored.State.Economy.Player.Inventory.Keys, Has.Count.EqualTo(5));
            Assert.That(TownJson.Serialize(restored.State.Economy), Is.EqualTo(TownJson.Serialize(world.State.Economy)));
            Assert.That(TownJson.Serialize(restored.State.Farming), Is.EqualTo(TownJson.Serialize(world.State.Farming)));
        });
    }

    [Test]
    public void CapacityFixtureRejectsRealModeBeforePublishingOrReadingSave()
    {
        var content = CapacityContent();
        var world = new TownWorld(content, "farming-capacity-fixture");
        using var journal = Journal(world);
        var rules = Snapshot(world, journal) with { Stage = "farming-capacity-fixture" };
        TownSaveFile.Write(SavePath, rules, saved => Validate(content, saved));
        byte[] published = File.ReadAllBytes(SavePath);
        var real = rules with { DecisionMode = "real" };

        Assert.Throws<InvalidDataException>(() => TownSaveFile.Write(SavePath, real, saved => Validate(content, saved)));
        string realPath = Path.Combine(_directory, "real-fixture.json");
        File.WriteAllText(realPath, TownJson.Serialize(real));
        Assert.Throws<InvalidDataException>(() => TownSaveFile.Read(realPath));

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(SavePath), Is.EqualTo(published));
            Assert.That(File.Exists(SavePath + ".previous"), Is.False);
            Assert.That(Directory.GetFiles(_directory, "*.tmp.*"), Is.Empty);
        });
    }

    private static void PlantAndWater(TownWorld world)
    {
        Assert.That(world.Farm(new("plant-one", "player", "plot.one", "plant", "carrot")).Succeeded, Is.True);
        Assert.That(world.Farm(new("water-one", "player", "plot.one", "water")).Succeeded, Is.True);
    }

    private TownRunJournal Journal(TownWorld world, string root = "runs") => new(Path.Combine(_directory, root), world.State.BranchId,
        new { schemaVersion = 1, rulesVersion = TownWorld.RulesVersion, numericVersion = TownWorld.NumericVersion });
    private static TownHostSnapshot Snapshot(TownWorld world, TownRunJournal journal) =>
        new(1, world.CaptureJson(), world.State.BranchId, journal.RecordCount, .025, "rules", "baseline", []);
    private static void Validate(TownContent content, TownHostSnapshot snapshot) => _ = TownWorld.RestoreCandidate(content, snapshot.WorldJson);
    private static void AppendFarm(TownRunJournal journal, FarmOperationRecord operation) => journal.Append("player_farm_command", new { schemaVersion = 1, operation });
    private static void AppendSleep(TownRunJournal journal, SleepOperationRecord operation) => journal.Append("player_sleep_command", new { schemaVersion = 1, operation });
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
    private static TownContent CapacityContent()
    {
        var content = Content();
        var definitions = content.Definitions;
        var inventory = new Dictionary<string, int> { ["seed.carrot"] = 2, ["crop.potato"] = 1,
            ["feed.chicken"] = 1, ["ingredient.salt"] = 1, ["ingredient.flour"] = 1 };
        return TownContent.Create(content.Manifest, definitions with
        {
            Resources = definitions.Resources.Concat(inventory.Keys.Select(id => new ResourceDefinition(id, id))).ToArray(),
            Economy = definitions.Economy! with { Player = new(300, 5, inventory) },
            Farming = definitions.Farming! with { Crops = definitions.Farming.Crops.Select(c => c with { SeedResourceId = "seed.carrot" }).ToArray() }
        });
    }
    private static TownContent Content()
    {
        var places = new[] { new PlaceDefinition("a", "A", new(60, 60), "outside"), new PlaceDefinition("b", "B", new(100, 60), "outside"),
            new PlaceDefinition("c", "C", new(60, 100), "outside"), new PlaceDefinition("d", "D", new(100, 100), "outside") };
        var residents = new[] { "mina", "eli", "ren", "sora" }.Select((id, index) => new ResidentDefinition(id, id, places[index].Id, places[index].Id,
            "apple", new() { ["apple"] = 3 }, 5, [new("free", 0, 1440, places[index].Id, "free", true)])).ToArray();
        var economy = new EconomyDefinition(new(100, 5, new() { ["seed"] = 3 }),
            [new("shop.general", "General store", "a", 100, new() { ["seed"] = 8 }, [new("seed", 4, 0), new("carrot", 0, 2)])]);
        var farming = new FarmingDefinition([new("carrot", "Carrot", "seed", "carrot", 1, 2)],
            [new("plot.one", "Plot one", "a"), new("plot.two", "Plot two", "a")], "a",
            [new("shop.general", 12345, 1, [new("seed", 1000, 4, 4)])]);
        return TownContent.Create(new(256, 256, places, [], [new("outside", new(0, 0, 256, 256))]),
            new(residents, [new("apple", "Apple"), new("seed", "Carrot seed"), new("carrot", "Carrot")], 299, economy, farming));
    }
}
