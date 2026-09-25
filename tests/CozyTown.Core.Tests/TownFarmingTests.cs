using System.Text.Json.Nodes;
using CozyTown.Core.Town;
using NUnit.Framework;

namespace CozyTown.Core.Tests;

[TestFixture]
public sealed class TownFarmingTests
{
    private static TownContent Content(int start = 299, Dictionary<string, int>? inventory = null, int capacity = 24)
    {
        PlaceDefinition[] places = [new("player.spawn", "Player", new(32, 32), "outside"), new("farm", "Farm", new(40, 32), "outside"),
            new("bed", "Bed", new(32, 40), "outside"), new("shop", "Shop", new(40, 40), "outside"),
            new("a", "A", new(80, 96), "outside"), new("b", "B", new(120, 96), "outside"),
            new("c", "C", new(80, 144), "outside"), new("d", "D", new(120, 144), "outside"),
            new("meeting", "Meeting", new(120, 160), "outside"), new("work", "Work", new(300, 80), "outside")];
        var resources = new[] { "seed.potato", "seed.carrot", "seed.tomato", "crop.potato", "crop.carrot", "crop.tomato", "feed.chicken", "ingredient.salt", "ingredient.flour" }
            .Select((id, index) => new ResourceDefinition(id, id, 99, index)).ToArray();
        var residents = new[] { "mina", "eli", "ren", "sora" }.Select((id, index) => new ResidentDefinition(id, id, places[index + 4].Id, places[index + 4].Id, "crop.potato",
            new() { ["crop.carrot"] = 3 }, 5, [new("free", 0, 300, places[index + 4].Id, "free", true),
                new("work", 300, 660, "work", "work", false), new("evening", 660, 1440, places[index + 4].Id, "free", true)])).ToArray();
        ShopRestockEntry[] restock = [new("seed.potato", 700, 3, 6), new("seed.carrot", 700, 3, 6), new("seed.tomato", 700, 3, 6),
            new("feed.chicken", 1000, 6, 12), new("ingredient.salt", 750, 3, 8), new("ingredient.flour", 750, 3, 8)];
        var economy = new EconomyDefinition(new(300, capacity, inventory ?? new() { ["seed.potato"] = 3, ["seed.carrot"] = 3, ["seed.tomato"] = 3 }),
            [new("shop.town.general", "Shop", "shop", 10000, new() { ["seed.potato"] = 5, ["crop.potato"] = 7 },
                [new("seed.potato", 20, 10), new("crop.potato", 40, 20)])]);
        var farming = new FarmingDefinition([new("potato", "Potato", "seed.potato", "crop.potato", 2, 2),
            new("carrot", "Carrot", "seed.carrot", "crop.carrot", 3, 2), new("tomato", "Tomato", "seed.tomato", "crop.tomato", 4, 3)],
            [new("plot.1", "Plot", "farm"), new("plot.2", "Plot", "farm"), new("plot.3", "Plot", "farm")], "bed", [new("shop.town.general", 12345, 4, restock)]);
        return TownContent.Create(new(384, 256, places, [new("wall", new(168, 24, 24, 152))], [new("outside", new(0, 0, 384, 256))]),
            new(residents, resources, start, economy, farming));
    }

    private static FarmRequest Command(string operation, string action, string? crop = null, string plot = "plot.1") => new(operation, "player", plot, action, crop);
    private static FarmPlotState Plot(TownWorld world, string id = "plot.1") => world.State.Farming.Plots.Single(p => p.Id == id);
    private static string Assets(TownWorld world) => TownJson.Serialize(new { world.State.Economy.Player, world.State.Economy.Shops, world.State.Farming.Plots });

    [TestCase("potato", 2, 2)]
    [TestCase("carrot", 3, 2)]
    [TestCase("tomato", 4, 3)]
    public void ReferenceCropsRequireWateredMorningsAndHarvestExactlyOnce(string crop, int days, int yield)
    {
        var content = Content(); var world = new TownWorld(content);
        Assert.That(world.Farm(Command("plant", "plant", crop)).Succeeded, Is.True);
        Assert.That(world.State.Economy.Player.Inventory["seed." + crop], Is.EqualTo(2));
        for (int day = 0; day < days; day++)
        {
            Assert.That(world.Farm(Command("water-" + day, "water")).Succeeded, Is.True);
            world.Advance(day == 0 ? 1 : 1440, 0);
            Assert.That(Plot(world).GrowthDays, Is.EqualTo(day + 1));
            Assert.That(Plot(world).Watered, Is.False);
            Assert.That(Plot(world).Mature, Is.EqualTo(day + 1 == days));
            world = TownWorld.RestoreCandidate(content, world.CaptureJson());
        }
        var harvest = Command("harvest", "harvest");
        Assert.That(world.Farm(harvest).Succeeded, Is.True);
        Assert.That(world.State.Economy.Player.Inventory["crop." + crop], Is.EqualTo(yield));
        Assert.That(Plot(world).CropId, Is.Null);
        Assert.That(Plot(world).Generation, Is.EqualTo(1));
        Assert.That(Plot(world).HarvestedCount, Is.EqualTo(1));
        world = TownWorld.RestoreCandidate(content, world.CaptureJson());
        Assert.That(world.Farm(harvest).IsDuplicate, Is.True);
        Assert.That(world.State.Economy.Player.Inventory["crop." + crop], Is.EqualTo(yield));
        Assert.That(world.Farm(Command("harvest-again", "harvest")).Code, Is.EqualTo("empty_plot"));
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [Test]
    public void MissingWaterBlocksOnlyItsPlotWhileTimeCursorAndShopAdvance()
    {
        var content = Content(); var world = new TownWorld(content);
        world.Farm(Command("dry", "plant", "potato"));
        world.Farm(Command("wet", "plant", "carrot", "plot.2"));
        world.Farm(Command("water", "water", plot: "plot.2"));
        world.Advance(2881, 0);
        Assert.That(world.WorldMinute, Is.EqualTo(3180));
        Assert.That(world.State.Farming.LastSettledDay, Is.EqualTo(2));
        Assert.That(Plot(world).GrowthDays, Is.Zero);
        Assert.That(Plot(world, "plot.2").GrowthDays, Is.EqualTo(1));
        Assert.That(world.State.Farming.Mornings.Select(m => m.Day), Is.EqualTo(new long[] { 0, 1, 2 }));
        Assert.That(world.Events.Count(e => e.Kind == "crop_growth" && e.Reason == "needs_water"), Is.EqualTo(5));
        Assert.That(world.State.Economy.Shops.Single().Inventory, Is.EqualTo(content.GetRestockInventory("shop.town.general", 2)));
        Assert.That(world.State.Economy.Shops.Single().Coins, Is.EqualTo(10000));
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [Test]
    public void RestockMatchesFixedReferenceReplacesSoldStockAndKeepsWallets()
    {
        var content = Content(540); var world = new TownWorld(content);
        Assert.That(content.GetRestockInventory("shop.town.general", 1), Is.EqualTo(new Dictionary<string, int>
            { ["seed.potato"] = 3, ["seed.carrot"] = 3, ["seed.tomato"] = 3, ["feed.chicken"] = 9, ["ingredient.salt"] = 7 }));
        world.Trade(new("buy", "player", "shop.town.general", "seed.potato", "buy", 1, 20));
        int coins = world.State.Economy.Shops.Single().Coins;
        world.Advance(1200, 0);
        Assert.That(world.State.Economy.Shops.Single().Coins, Is.EqualTo(coins));
        Assert.That(world.State.Economy.Shops.Single().Inventory.ContainsKey("crop.potato"), Is.False);
        var after = world.State.Economy.Shops.Single().Inventory;
        world.Trade(new("buy-after", "player", "shop.town.general", "seed.potato", "buy", 1, 20));
        world.Advance(0, 0);
        Assert.That(world.State.Economy.Shops.Single().Inventory["seed.potato"], Is.EqualTo(after["seed.potato"] - 1));
        Assert.That(world.State.Farming.Mornings, Has.Count.EqualTo(1));
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [TestCase("seed", "insufficient_seed")]
    [TestCase("immature", "not_mature")]
    [TestCase("full", "inventory_full")]
    public void FarmRejectionsPreserveAssetsAndHarvestableState(string condition, string code)
    {
        var content = condition == "seed" ? Content(inventory: []) : condition == "full" ? Content(inventory: new()
            { ["seed.potato"] = 2, ["seed.carrot"] = 99, ["feed.chicken"] = 99, ["ingredient.salt"] = 99, ["ingredient.flour"] = 99 }, capacity: 5) : Content();
        var world = new TownWorld(content);
        if (condition != "seed") world.Farm(Command("plant", "plant", "potato"));
        if (condition == "full")
        {
            world.Farm(Command("water1", "water")); world.Advance(1, 0);
            world.Farm(Command("water2", "water")); world.Advance(1440, 0);
        }
        var request = condition == "seed" ? Command("failure", "plant", "potato") : Command("failure", "harvest");
        string before = Assets(world);
        Assert.That(world.Farm(request).Code, Is.EqualTo(code));
        Assert.That(Assets(world), Is.EqualTo(before));
        Assert.That(world.State.Farming.Receipts["failure"].Result.Succeeded, Is.False);
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [Test]
    public void SecondMorningFailureRejectsWholeRequestAndPreservesEarlierIndependentAdvance()
    {
        var world = new TownWorld(Content(298));
        world.Farm(Command("plant", "plant", "potato")); world.Farm(Command("water", "water"));
        world.Advance(1, 2);
        string before = TownJson.Serialize(world.State);
        var reached = new List<long>();
        Assert.Throws<IOException>(() => world.Advance(1442, 4, settlementGuard: day =>
        {
            reached.Add(day);
            if (day == 1) throw new IOException("Injected second morning failure.");
        }));
        Assert.That(reached, Is.EqualTo(new long[] { 0, 1 }));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
        Assert.That(world.WorldMinute, Is.EqualTo(299));
        world.Advance(1442, 4);
        Assert.That(world.State.Farming.Mornings, Has.Count.EqualTo(2));
        Assert.That(Plot(world).GrowthDays, Is.EqualTo(1));
    }

    [Test]
    public void RejectedSleepRecordsFailureWithoutPartialMorningOrPositionCommit()
    {
        var content = Content(); var world = new TownWorld(content);
        world.Farm(Command("plant", "plant", "potato")); world.Farm(Command("water", "water"));
        string before = Assets(world);
        var residents = TownJson.Serialize(world.State.Residents);
        SleepOperationRecord? recorded = null;
        var result = world.Sleep(new("sleep", "player", 1), persistBeforeCommit: r => recorded = r,
            settlementGuard: _ => throw new IOException("Injected morning failure."));
        Assert.That(result.Code, Is.EqualTo("advance_failed"));
        Assert.That(world.WorldMinute, Is.EqualTo(299));
        Assert.That(Assets(world), Is.EqualTo(before));
        Assert.That(TownJson.Serialize(world.State.Residents), Is.EqualTo(residents));
        Assert.That(world.State.Farming.LastSettledDay, Is.EqualTo(-1));
        Assert.That(recorded!.Result, Is.EqualTo(result));
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [Test]
    public void SleepAndMultiDayAdvanceFollowSameRoutesQueriesSchedulesAndCommitments()
    {
        var content = Content(240); var initial = new TownWorld(content);
        initial.Advance(.25, 0);
        var mina = initial.GetDecisionRequests().Single(r => r.ResidentId == "mina");
        initial.MarkDispatched(mina.LogicalRequestId, "invite-attempt");
        initial.ReceiveResult(new(initial.SessionId, mina.LogicalRequestId, "invite-attempt", "mina", new("invite", "eli", "meeting", null, 0, 0, null, "Meet"), "fixed_model", null));
        initial.Advance(0, 0);
        var eli = initial.GetDecisionRequests().Single(r => r.ResidentId == "eli");
        initial.MarkDispatched(eli.LogicalRequestId, "accept-attempt");
        initial.ReceiveResult(new(initial.SessionId, eli.LogicalRequestId, "accept-attempt", "eli", new("respond", "mina", null, null, 0, 0, true, "Yes"), "fixed_model", null));
        string snapshot = initial.CaptureJson();
        var continuousQueries = new List<(string, TownPoint, TownPoint)>();
        var sleepQueries = new List<(string, TownPoint, TownPoint)>();
        var continuous = TownWorld.RestoreCandidate(content, snapshot, (id, from, to) => { continuousQueries.Add((id, from, to)); return true; });
        var sleeping = TownWorld.RestoreCandidate(content, snapshot, (id, from, to) => { sleepQueries.Add((id, from, to)); return true; });
        var bulk = TownWorld.RestoreCandidate(content, snapshot);
        continuousQueries.Clear(); sleepQueries.Clear();
        for (int i = 0; i < 3 * 24 * 4; i++) continuous.Advance(15, 0);
        for (int i = 0; i < 6; i++) Assert.That(sleeping.Sleep(new("sleep-" + i, "player", 12)).Succeeded, Is.True);
        bulk.Advance(3 * 1440, 0);
        Assert.That(Comparable(sleeping), Is.EqualTo(Comparable(continuous)));
        Assert.That(Comparable(bulk), Is.EqualTo(Comparable(continuous)));
        Assert.That(continuousQueries, Is.EqualTo(sleepQueries));
        Assert.That(sleepQueries, Has.Count.GreaterThan(100));
        Assert.That(sleepQueries.All(q => q.Item2.DistanceTo(q.Item3) <= 4.000001), Is.True);
        Assert.That(sleeping.State.Commitments.Single().Stage, Is.EqualTo("ended"));
        Assert.That(sleeping.Events.Any(e => e.Kind == "conversation_started"), Is.True);
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, sleeping.CaptureJson()));
    }

    private static string Comparable(TownWorld world)
    {
        var state = world.State;
        return TownJson.Serialize(new { state.WorldMinute, state.AcceptedWorldMinutes, state.Farming.Plots, state.Farming.LastSettledDay,
            state.Economy.Player, state.Economy.Shops, Residents = state.Residents.Select(r => new { r.Id, r.Position, r.Facing, r.RegionId,
                r.Route, r.RouteCursor, r.DestinationPlaceId, r.ScheduleId, r.ScheduleDay, r.Activity, r.ActivityRevision, r.BlockedReason,
                r.CommitmentId, r.PendingRequestId, r.CooldownSeconds }),
            Commitments = state.Commitments.Select(c => new { c.Id, c.Stage, c.EndsAtMinute, c.ArrivedResidentIds, c.TurnNumber, c.Dialogue }) });
    }

    [Test]
    public void FractionalContinuousStepsMatchSleepPositionAndDoNotChargeWorldHoursAsRealWait()
    {
        var content = Content(660); var initial = new TownWorld(content); initial.Advance(.025, 0);
        initial.GetDecisionRequests();
        var snapshot = initial.CaptureJson();
        var continuous = TownWorld.RestoreCandidate(content, snapshot);
        var sleeping = TownWorld.RestoreCandidate(content, snapshot);
        for (int i = 0; i < 2400; i++) { continuous.Advance(.025, 0); if (i % 100 == 0) continuous.AcknowledgeEvents(continuous.State.Sequence); }
        Assert.That(sleeping.Sleep(new("sleep", "player", 1)).Succeeded, Is.True);
        Assert.That(continuous.WorldMinute, Is.EqualTo(sleeping.WorldMinute).Within(1e-7));
        foreach (var resident in sleeping.State.Residents)
        {
            var other = continuous.State.Residents.Single(r => r.Id == resident.Id);
            Assert.That(resident.Position.DistanceTo(other.Position), Is.LessThan(1e-6));
            Assert.That(resident.RouteCursor, Is.EqualTo(other.RouteCursor));
            Assert.That(resident.Activity, Is.EqualTo(other.Activity));
        }
        Assert.That(sleeping.State.PendingDecisions.All(p => p.WaitedSeconds == 0 && p.Request.RemainingWaitSeconds == 60), Is.True);
    }

    [Test]
    public void NormalHostStepsAndSleepStopAtSameQueriedCollisionBoundary()
    {
        var content = Content();
        bool Motion(string id, TownPoint from, TownPoint to) => id == "player" || to.X <= 150;
        var continuous = new TownWorld(content, movementQuery: Motion);
        var sleeping = new TownWorld(content, movementQuery: Motion);
        var bulk = new TownWorld(content, movementQuery: Motion);
        for (int i = 0; i < 2400; i++)
        {
            continuous.Advance(.025, 0);
            if (i % 100 == 0) continuous.AcknowledgeEvents(continuous.State.Sequence);
        }
        sleeping.Sleep(new("sleep", "player", 1)); bulk.Advance(60, 0);
        foreach (var resident in sleeping.State.Residents)
        {
            var other = continuous.State.Residents.Single(r => r.Id == resident.Id);
            var unsegmented = bulk.State.Residents.Single(r => r.Id == resident.Id);
            Assert.That(resident.Position.DistanceTo(other.Position), Is.LessThan(1e-6), resident.Id);
            Assert.That(resident.Position.DistanceTo(unsegmented.Position), Is.LessThan(1e-6), resident.Id);
            Assert.That(resident.Position.X, Is.EqualTo(150).Within(1e-6));
            Assert.That(resident.Position.X, Is.LessThanOrEqualTo(150));
            Assert.That(other.BlockedReason, Is.EqualTo("body_blocked"));
            Assert.That(resident.BlockedReason, Is.EqualTo(other.BlockedReason));
            Assert.That(resident.RouteCursor, Is.EqualTo(other.RouteCursor));
            Assert.That(resident.Activity, Is.EqualTo(other.Activity));
        }
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, sleeping.CaptureJson(), Motion));
    }

    [Test]
    public void SleepSnapshotRejectsOverlappingRecordedIntervals()
    {
        var content = Content(); var world = new TownWorld(content);
        world.Sleep(new("sleep-1", "player", 1)); world.Sleep(new("sleep-2", "player", 1));
        var node = JsonNode.Parse(world.CaptureJson())!;
        var result = node["state"]!["farming"]!["sleepReceipts"]!["sleep-1"]!["result"]!;
        result["fromMinute"] = 359; result["toMinute"] = 419;
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, node.ToJsonString()));
    }

    [Test]
    public void MorningRestoreConsumesAcceptedTimeOnceAndSleepRespectsAdvanceBound()
    {
        var content = Content(); var world = new TownWorld(content);
        world.Farm(Command("plant", "plant", "potato")); world.Farm(Command("water", "water"));
        var before = JsonNode.Parse(world.CaptureJson())!;
        before["state"]!["acceptedWorldMinutes"] = 1;
        var resumed = TownWorld.RestoreCandidate(content, before.ToJsonString());
        resumed.Advance(0, 0);
        Assert.That(resumed.WorldMinute, Is.EqualTo(300));
        Assert.That(Plot(resumed).GrowthDays, Is.EqualTo(1));
        var after = TownWorld.RestoreCandidate(content, resumed.CaptureJson()); after.Advance(0, 0);
        Assert.That(after.State.Farming.Mornings, Has.Count.EqualTo(1));
        Assert.That(Plot(after).GrowthDays, Is.EqualTo(1));
        var pending = JsonNode.Parse(world.CaptureJson())!;
        pending["state"]!["acceptedWorldMinutes"] = TownWorld.MaximumAdvanceMinutes;
        var limit = TownWorld.RestoreCandidate(content, pending.ToJsonString());
        Assert.That(limit.Sleep(new("sleep", "player", 1)).Code, Is.EqualTo("advance_limit"));
        Assert.That(limit.WorldMinute, Is.EqualTo(299));
        Assert.That(limit.State.AcceptedWorldMinutes, Is.EqualTo(TownWorld.MaximumAdvanceMinutes));
        Assert.That(limit.State.Farming.Mornings, Is.Empty);
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, limit.CaptureJson()));
        string unchanged = TownJson.Serialize(limit.State);
        Assert.Throws<ArgumentOutOfRangeException>(() => limit.Advance(1, 0));
        Assert.That(TownJson.Serialize(limit.State), Is.EqualTo(unchanged));
    }

    [Test]
    public void SettlementCallbackCannotMutateWorldAndRejectedInputLeavesWholeState()
    {
        var world = new TownWorld(Content()); string before = TownJson.Serialize(world.State);
        Assert.Throws<InvalidOperationException>(() => world.Advance(1, 0, settlementGuard: _ => world.SelectQuickbar("nested", 1)));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.Advance(TownWorld.MaximumAdvanceMinutes + 1, 0));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
    }

    [Test]
    public void InvalidCropRestockAndPlotReferencesFailContentBeforeSimulation()
    {
        var content = Content(); var definitions = content.Definitions; var farming = definitions.Farming!;
        Assert.Throws<InvalidDataException>(() => TownContent.Create(content.Manifest, definitions with { Farming = farming with
            { Crops = [farming.Crops[0] with { SeedResourceId = "missing" }] } }));
        Assert.Throws<InvalidDataException>(() => TownContent.Create(content.Manifest, definitions with { Farming = farming with
            { Plots = [farming.Plots[0] with { PlaceId = "missing" }] } }));
        Assert.Throws<InvalidDataException>(() => TownContent.Create(content.Manifest, definitions with { Farming = farming with
            { Restocks = [farming.Restocks[0] with { MinimumDistinctItems = 100 }] } }));
        var reordered = TownContent.Create(content.Manifest, definitions with { Farming = farming with
            { Crops = farming.Crops.Reverse().ToArray(), Plots = farming.Plots.Reverse().ToArray() } });
        Assert.That(reordered.Fingerprint, Is.EqualTo(content.Fingerprint));
        var changed = TownContent.Create(content.Manifest, definitions with { Farming = farming with
            { Restocks = [farming.Restocks[0] with { Entries = farming.Restocks[0].Entries.Reverse().ToArray() }] } });
        Assert.That(changed.Fingerprint, Is.Not.EqualTo(content.Fingerprint));
    }

    [Test]
    public void FarmAndSleepPersistenceFailuresKeepEntireWorldAndPreventReentry()
    {
        var world = new TownWorld(Content()); string before = TownJson.Serialize(world.State);
        Assert.Throws<IOException>(() => world.Farm(Command("plant", "plant", "potato"), _ => throw new IOException("Injected journal failure.")));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
        Assert.Throws<IOException>(() => world.Sleep(new("sleep", "player", 1), persistBeforeCommit: _ => throw new IOException("Injected journal failure.")));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
        Assert.Throws<InvalidOperationException>(() => world.Farm(Command("plant", "plant", "potato"), _ => world.Advance(1, 0)));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
    }

    [Test]
    public void FarmOperationIdentityCannotBeReusedForTradeSleepOrChangedCrop()
    {
        var content = Content(); var world = new TownWorld(content);
        world.Farm(Command("same", "plant", "potato"));
        Assert.That(world.Farm(Command("same", "plant", "carrot")).Code, Is.EqualTo("operation_id_conflict"));
        Assert.That(world.Sleep(new("same", "player", 1)).Code, Is.EqualTo("operation_id_conflict"));
        Assert.That(world.Trade(new("same", "player", "shop.town.general", "seed.potato", "buy", 1, 20)).Code, Is.EqualTo("operation_id_conflict"));
        Assert.That(world.SelectQuickbar("same", 1).Code, Is.EqualTo("operation_id_conflict"));
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [TestCase("cursor")]
    [TestCase("growth")]
    [TestCase("mature")]
    [TestCase("generation")]
    [TestCase("receipts")]
    [TestCase("source")]
    [TestCase("unknown_crop")]
    public void FarmingSnapshotsRejectTamperedProductionStateAndSources(string field)
    {
        var content = Content(); var world = new TownWorld(content);
        world.Farm(Command("plant", "plant", "potato")); world.Farm(Command("water", "water")); world.Advance(1, 0);
        string before = TownJson.Serialize(world.State);
        var node = JsonNode.Parse(world.CaptureJson())!;
        var farming = node["state"]!["farming"]!;
        switch (field)
        {
            case "cursor": farming["lastSettledDay"] = -1; break;
            case "growth": farming["plots"]![0]!["growthDays"] = 2; break;
            case "mature": farming["plots"]![0]!["mature"] = true; break;
            case "generation": farming["plots"]![0]!["generation"] = 9; break;
            case "receipts": farming["receipts"] = new JsonObject(); break;
            case "source": farming["mornings"] = new JsonArray(); break;
            case "unknown_crop": farming["plots"]![0]!["cropId"] = "missing"; break;
        }
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, node.ToJsonString()));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
    }

    [Test]
    public void CropAndRestockRulesAffectFingerprintAndMissingStateRejectsPreviousSaveSchema()
    {
        var content = Content(); var world = new TownWorld(content);
        var farming = content.Definitions.Farming!;
        var changed = TownContent.Create(content.Manifest, content.Definitions with { Farming = farming with
            { Crops = farming.Crops.Select(c => c with { GrowthDays = c.GrowthDays + 1 }).ToArray() } });
        Assert.That(changed.Fingerprint, Is.Not.EqualTo(content.Fingerprint));
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(changed, world.CaptureJson()));
        var old = JsonNode.Parse(world.CaptureJson())!; old["schemaVersion"] = 2; old["state"]!.AsObject().Remove("farming");
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, old.ToJsonString()));
    }
}
