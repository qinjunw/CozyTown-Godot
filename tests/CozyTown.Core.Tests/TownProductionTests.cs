using System.Text.Json;
using System.Text.Json.Nodes;
using CozyTown.Core.Town;
using NUnit.Framework;

namespace CozyTown.Core.Tests;

[TestFixture]
public sealed class TownProductionTests
{
    private static TownContent Content(Dictionary<string, int>? inventory = null, int capacity = 24, uint seed = 12345,
        int eggQuantity = 1, int cookedQuantity = 1)
    {
        string[] ids = ["feed.chicken", "animal_product.egg", "crop.potato", "crop.carrot", "crop.tomato", "ingredient.salt", "ingredient.flour",
            "fish.carp", "fish.trout", "fish.bass", "food.baked_potato", "food.vegetable_soup", "food.grilled_fish", "food.tomato_egg", "food.fish_pie"];
        PlaceDefinition[] places = [new("player.spawn", "Player", new(32, 32), "outside"), new("coop", "Coop", new(40, 32), "outside"),
            new("pond", "Pond", new(32, 40), "outside"), new("kitchen", "Kitchen", new(40, 40), "outside"), new("home", "Home", new(96, 96), "outside")];
        var residents = new[] { "mina", "eli", "ren", "sora" }.Select(id => new ResidentDefinition(id, id, "home", "home", "crop.potato",
            new() { ["crop.carrot"] = 3 }, 5, [new("free", 0, 1440, "home", "free", true)])).ToArray();
        var economy = new EconomyDefinition(new(300, capacity, inventory ?? new() { ["feed.chicken"] = 3, ["crop.potato"] = 3,
            ["crop.carrot"] = 3, ["crop.tomato"] = 3, ["ingredient.salt"] = 3, ["ingredient.flour"] = 3, ["animal_product.egg"] = 3,
            ["fish.carp"] = 3, ["fish.trout"] = 3 }), [new("shop", "Shop", "kitchen", 10000, [], ids.Select(id => new ShopPriceDefinition(id, 1, 10)).ToArray())]);
        RecipeDefinition[] recipes = [new("baked_potato", "Baked Potato", [new("crop.potato", 1), new("ingredient.salt", 1)], "food.baked_potato", cookedQuantity),
            new("vegetable_soup", "Soup", [new("crop.carrot", 1), new("crop.tomato", 1)], "food.vegetable_soup", 1),
            new("grilled_fish", "Grilled Fish", [new("fish.carp", 1), new("ingredient.salt", 1)], "food.grilled_fish", 1),
            new("tomato_egg", "Tomato Egg", [new("crop.tomato", 1), new("animal_product.egg", 1)], "food.tomato_egg", 1),
            new("fish_pie", "Fish Pie", [new("fish.trout", 1), new("animal_product.egg", 1), new("ingredient.flour", 1)], "food.fish_pie", 1)];
        var production = new ProductionDefinition([new("hen", "Hen", "coop", "feed.chicken", "animal_product.egg", eggQuantity)],
            [new("fishing", "Fishing", "pond")], [new("carp", "fish.carp", 0, 40), new("trout", "fish.trout", 40, 70), new("bass", "fish.bass", 70, 90)],
            [new("cooking", "Cooking", "kitchen")], recipes, seed);
        return TownContent.Create(new(192, 160, places, [], [new("outside", new(0, 0, 192, 160))]),
            new(residents, ids.Select(id => new ResourceDefinition(id, id)).ToArray(), 299, economy,
                new([], [], "player.spawn", []), production));
    }

    private static ProductionRequest Feed(string id = "feed") => new(id, "player", "hen", "feed");
    private static ProductionRequest Collect(string id = "collect") => new(id, "player", "hen", "collect");
    private static ProductionRequest Fish(string id = "fish") => new(id, "player", "fishing", "fish");
    private static ProductionRequest Cook(string recipe, string id = "cook") => new(id, "player", "cooking", "cook", recipe);
    private static string Assets(TownWorld world) => TownJson.Serialize(new { world.State.Economy.Player, world.State.Economy.Shops,
        world.State.Production.Chickens, world.State.Production.FishingRandomState, world.State.Production.FishingDrawCount });

    [Test]
    public void FedChickenCreatesOnePendingEggAcrossManyMorningsAndCanRestartAfterCollection()
    {
        var content = Content(); var world = new TownWorld(content);
        int feed = world.State.Economy.Player.Inventory["feed.chicken"];
        int eggs = world.State.Economy.Player.Inventory["animal_product.egg"];
        Assert.That(world.Produce(Feed()).Succeeded, Is.True);
        Assert.That(world.Produce(Feed("again")).Code, Is.EqualTo("already_fed"));
        Assert.That(world.State.Economy.Player.Inventory["feed.chicken"], Is.EqualTo(feed - 1));
        world = TownWorld.RestoreCandidate(content, world.CaptureJson());
        world.Advance(1 + 3 * 1440, 0);
        var hen = world.State.Production.Chickens.Single();
        Assert.That(hen.ProductReady, Is.True); Assert.That(hen.Fed, Is.False); Assert.That(hen.Generation, Is.EqualTo(1));
        Assert.That(world.Events.Count(e => e.Kind == "chicken_product_ready"), Is.EqualTo(1));
        Assert.That(world.Produce(Feed("pending")).Code, Is.EqualTo("product_pending"));
        Assert.That(world.Produce(Collect()).Succeeded, Is.True);
        Assert.That(world.State.Economy.Player.Inventory["animal_product.egg"], Is.EqualTo(eggs + 1));
        world = TownWorld.RestoreCandidate(content, world.CaptureJson());
        Assert.That(world.Produce(Collect()).IsDuplicate, Is.True);
        Assert.That(world.Produce(Collect("empty")).Code, Is.EqualTo("product_not_ready"));
        Assert.That(world.Produce(Feed("next-cycle")).Succeeded, Is.True);
        world.Advance(1440, 0); Assert.That(world.Produce(Collect("next-egg")).Succeeded, Is.True);
        Assert.That(world.State.Production.Chickens.Single().CollectedCount, Is.EqualTo(2));
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [TestCase(72u, 0, "fish.carp")]
    [TestCase(150u, 39, "fish.carp")]
    [TestCase(17u, 40, "fish.trout")]
    [TestCase(1u, 69, "fish.trout")]
    [TestCase(160u, 70, "fish.bass")]
    [TestCase(24u, 89, "fish.bass")]
    [TestCase(10u, 90, null)]
    [TestCase(56u, 99, null)]
    public void ReferenceFishIntervalsAndEmptyCatchConsumeOneRecordedRandomDraw(uint seed, int roll, string? resource)
    {
        var content = Content(seed: seed); var world = new TownWorld(content);
        var result = world.Produce(Fish());
        Assert.That(result.Succeeded, Is.True); Assert.That(result.Roll, Is.EqualTo(roll)); Assert.That(result.ResourceId, Is.EqualTo(resource));
        Assert.That(result.Quantity, Is.EqualTo(resource is null ? 0 : 1)); Assert.That(result.Code, Is.EqualTo(resource is null ? "no_catch" : "accepted"));
        Assert.That(world.State.Production.FishingDrawCount, Is.EqualTo(1));
        Assert.That(world.State.Production.Receipts["fish"].RandomStateBefore, Is.EqualTo(seed));
        Assert.That(world.State.Production.Receipts["fish"].RandomStateAfter, Is.EqualTo(world.State.Production.FishingRandomState));
        var restored = TownWorld.RestoreCandidate(content, world.CaptureJson());
        Assert.That(restored.Produce(Fish()).IsDuplicate, Is.True);
        Assert.That(restored.State.Production.FishingDrawCount, Is.EqualTo(1));
    }

    [TestCase("baked_potato")]
    [TestCase("vegetable_soup")]
    [TestCase("grilled_fish")]
    [TestCase("tomato_egg")]
    [TestCase("fish_pie")]
    public void FiveReferenceRecipesConsumeOnlyDeclaredMaterialsAndTheirProductCanBeSold(string recipeId)
    {
        var content = Content(); var world = new TownWorld(content);
        var recipe = content.Definitions.Production!.Recipes.Single(r => r.Id == recipeId);
        var before = world.State.Economy.Player.Inventory;
        int coins = world.State.Economy.Player.Coins;
        var result = world.Produce(Cook(recipeId));
        Assert.That(result.Succeeded, Is.True);
        foreach (var resource in content.Definitions.Resources)
        {
            int expected = before.GetValueOrDefault(resource.Id) - (recipe.Ingredients.SingleOrDefault(i => i.ResourceId == resource.Id)?.Quantity ?? 0)
                + (resource.Id == recipe.OutputResourceId ? recipe.OutputQuantity : 0);
            Assert.That(world.State.Economy.Player.Inventory.GetValueOrDefault(resource.Id), Is.EqualTo(expected), resource.Id);
        }
        Assert.That(world.State.Economy.Player.Coins, Is.EqualTo(coins));
        Assert.That(world.Trade(new("sell", "player", "shop", recipe.OutputResourceId, "sell", 1, 10)).Succeeded, Is.True);
        Assert.That(world.State.Economy.Player.Coins, Is.EqualTo(coins + 10));
        Assert.That(world.State.Economy.Shops.Single().Inventory[recipe.OutputResourceId], Is.EqualTo(1));
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [TestCase("collect")]
    [TestCase("fish")]
    [TestCase("cook")]
    public void FullInventoryRejectsWholeProductionAndKeepsPendingOutputAndRandomState(string action)
    {
        var inventory = new Dictionary<string, int> { ["feed.chicken"] = 2, ["crop.potato"] = 2, ["ingredient.salt"] = 2, ["crop.carrot"] = 2, ["ingredient.flour"] = 2 };
        var content = Content(inventory, capacity: 5, seed: 1); var world = new TownWorld(content);
        if (action == "collect") { world.Produce(Feed()); world.Advance(1, 0); }
        string before = Assets(world);
        var request = action == "collect" ? Collect() : action == "fish" ? Fish() : Cook("baked_potato");
        Assert.That(world.Produce(request).Code, Is.EqualTo("inventory_full"));
        Assert.That(Assets(world), Is.EqualTo(before));
        if (action == "collect") Assert.That(world.State.Production.Chickens.Single().ProductReady, Is.True);
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [Test]
    public void CookingUsesSpaceFreedByConsumedIngredients()
    {
        var content = Content(new() { ["crop.potato"] = 1, ["ingredient.salt"] = 1, ["feed.chicken"] = 2, ["crop.carrot"] = 2, ["ingredient.flour"] = 2 }, 5);
        var world = new TownWorld(content);
        Assert.That(world.Produce(Cook("baked_potato")).Succeeded, Is.True);
        Assert.That(world.State.Economy.Player.Inventory, Has.Count.EqualTo(4));
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [TestCase("feed")]
    [TestCase("ingredients")]
    [TestCase("target")]
    [TestCase("distance")]
    [TestCase("action")]
    [TestCase("mismatch")]
    [TestCase("definition")]
    public void InvalidProductionConditionsRecordRejectionsWithoutAssetOrRandomChanges(string condition)
    {
        var content = Content(condition is "feed" or "ingredients" ? [] : null);
        var world = new TownWorld(content);
        if (condition == "distance") world.SetPlayerPosition(new(170, 130));
        var request = condition switch { "feed" => Feed(), "ingredients" => Cook("fish_pie"), "target" => Fish() with { TargetId = "missing" },
            "action" => Fish() with { Action = "invent" }, "mismatch" => Fish() with { TargetId = "hen" },
            "definition" => Fish() with { DefinitionId = "carp" }, _ => Fish() };
        string before = Assets(world);
        var result = world.Produce(request);
        Assert.That(result.Succeeded, Is.False); Assert.That(Assets(world), Is.EqualTo(before));
        Assert.That(world.State.Production.Receipts[request.OperationId].Result, Is.EqualTo(result));
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [TestCase("collect")]
    [TestCase("cook")]
    public void OutputOverflowRejectsWithoutDiscardingIngredientsOrPendingEgg(string action)
    {
        var content = Content(new() { ["feed.chicken"] = 2, ["animal_product.egg"] = 1, ["crop.potato"] = 2, ["ingredient.salt"] = 2, ["food.baked_potato"] = 1 },
            eggQuantity: action == "collect" ? int.MaxValue : 1, cookedQuantity: action == "cook" ? int.MaxValue : 1);
        var world = new TownWorld(content);
        if (action == "collect") { world.Produce(Feed()); world.Advance(1, 0); }
        string before = Assets(world);
        Assert.That(world.Produce(action == "collect" ? Collect() : Cook("baked_potato")).Code, Is.EqualTo("numeric_overflow"));
        Assert.That(Assets(world), Is.EqualTo(before));
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [TestCase("after_consume:crop.potato")]
    [TestCase("after_consume:ingredient.salt")]
    [TestCase("before_output:food.baked_potato")]
    public void CookingMutationFailureDoesNotKeepPartialConsumption(string failureStage)
    {
        var content = Content(); var world = new TownWorld(content); string before = Assets(world);
        var result = world.Produce(Cook("baked_potato"), mutationGuard: stage => { if (stage == failureStage) throw new IOException("Injected material/output failure."); });
        Assert.That(result.Code, Is.EqualTo("production_failed")); Assert.That(Assets(world), Is.EqualTo(before));
        Assert.That(world.Produce(Cook("baked_potato")).IsDuplicate, Is.True);
        Assert.That(world.Produce(Cook("baked_potato", "retry-new-operation")).Succeeded, Is.True);
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [Test]
    public void RandomOutputAndPersistenceFailuresPreserveDrawAndPreventCallbackReentry()
    {
        var content = Content(seed: 1); var world = new TownWorld(content); string assets = Assets(world);
        Assert.That(world.Produce(Fish(), mutationGuard: _ => throw new IOException("Injected output failure.")).Code, Is.EqualTo("production_failed"));
        Assert.That(Assets(world), Is.EqualTo(assets));
        string before = TownJson.Serialize(world.State);
        Assert.Throws<IOException>(() => world.Produce(Fish("journal"), _ => throw new IOException("Injected journal failure.")));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
        Assert.Throws<InvalidOperationException>(() => world.Produce(Fish("reentry"), _ => world.Advance(1, 0)));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
        Assert.That(world.Produce(Fish("next")).Roll, Is.EqualTo(69));
        Assert.That(world.State.Production.FishingRandomState, Is.EqualTo(270369u));
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [Test]
    public void SameOrderedInputsContinueSameFishSequenceAcrossSnapshotRestoreAndEmptyCatch()
    {
        var content = Content(seed: 10); var original = new TownWorld(content);
        Assert.That(original.Produce(Fish("first")).Code, Is.EqualTo("no_catch"));
        var restored = TownWorld.RestoreCandidate(content, original.CaptureJson());
        for (int i = 0; i < 30; i++)
        {
            var request = Fish("draw-" + i);
            Assert.That(restored.Produce(request), Is.EqualTo(original.Produce(request)));
            Assert.That(Assets(restored), Is.EqualTo(Assets(original)));
            if (i == 12) restored = TownWorld.RestoreCandidate(content, restored.CaptureJson());
        }
        Assert.That(restored.State.Production.FishingDrawCount, Is.EqualTo(31));
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, restored.CaptureJson()));
    }

    [Test]
    public void SecondMorningFailureRollsBackChickenProductionWithFarmTimeAndSources()
    {
        var world = new TownWorld(Content()); world.Produce(Feed()); string before = TownJson.Serialize(world.State);
        Assert.Throws<IOException>(() => world.Advance(1442, 0, settlementGuard: day => { if (day == 1) throw new IOException("Injected second morning failure."); }));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
        world.Advance(1442, 0);
        Assert.That(world.State.Production.Chickens.Single().ProductReady, Is.True);
        Assert.That(world.Events.Count(e => e.Kind == "chicken_product_ready"), Is.EqualTo(1));
    }

    [Test]
    public void ProductionIdsShareDeduplicationWithTradeFarmSleepAndSelection()
    {
        var world = new TownWorld(Content()); world.Produce(Feed("same")); string before = Assets(world);
        Assert.That(world.Produce(Collect("same")).Code, Is.EqualTo("operation_id_conflict"));
        Assert.That(world.SelectQuickbar("same", 3).Code, Is.EqualTo("operation_id_conflict"));
        Assert.That(world.Farm(new("same", "player", "missing", "water")).Code, Is.EqualTo("operation_id_conflict"));
        Assert.That(world.Sleep(new("same", "player", 1)).Code, Is.EqualTo("operation_id_conflict"));
        Assert.That(world.Trade(new("same", "player", "shop", "feed.chicken", "sell", 1, 10)).Code, Is.EqualTo("operation_id_conflict"));
        Assert.That(Assets(world), Is.EqualTo(before));
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(Content(), world.CaptureJson()));
    }

    [Test]
    public void SourceEventsSeparatePendingCreationCollectionConversionAndTrade()
    {
        var world = new TownWorld(Content()); world.Produce(Feed()); world.Advance(1, 0); world.Produce(Collect()); world.Produce(Cook("tomato_egg"));
        world.Trade(new("sell", "player", "shop", "food.tomato_egg", "sell", 1, 10));
        var pending = world.Events.Single(e => e.Kind == "chicken_product_ready");
        using var pendingJson = JsonDocument.Parse(pending.DataJson);
        Assert.That(pendingJson.RootElement.GetProperty("quantity").GetInt32(), Is.EqualTo(1));
        var claim = world.Events.Single(e => e.Kind == "production_source" && e.Reason == "collect");
        using var claimJson = JsonDocument.Parse(claim.DataJson);
        Assert.That(claimJson.RootElement.GetProperty("created").EnumerateObject().Count(), Is.Zero);
        Assert.That(claimJson.RootElement.GetProperty("claimed").GetProperty("animal_product.egg").GetInt32(), Is.EqualTo(1));
        Assert.That(world.Events.Count(e => e.Kind == "economy_trade"), Is.EqualTo(1));
    }

    [TestCase("missing")]
    [TestCase("rng_version")]
    [TestCase("rng_state")]
    [TestCase("rng_count")]
    [TestCase("chicken_ready")]
    [TestCase("receipt_random")]
    [TestCase("receipt_output")]
    [TestCase("receipt_order")]
    public void ProductionSnapshotsRejectTamperedRandomStateOutputAndSourceHistory(string change)
    {
        var content = Content(seed: 1); var world = new TownWorld(content); world.Produce(Feed()); world.Produce(Fish()); world.Advance(1, 0);
        var root = JsonNode.Parse(world.CaptureJson())!; var production = root["state"]!["production"]!;
        switch (change)
        {
            case "missing": root["state"]!.AsObject().Remove("production"); break;
            case "rng_version": production["fishingRandomVersion"] = "unknown"; break;
            case "rng_state": production["fishingRandomState"] = 7; break;
            case "rng_count": production["fishingDrawCount"] = 9; break;
            case "chicken_ready": production["chickens"]![0]!["productReady"] = false; break;
            case "receipt_random": production["receipts"]!["fish"]!["randomStateBefore"] = 7; break;
            case "receipt_output": production["receipts"]!["fish"]!["result"]!["resourceId"] = "fish.carp"; break;
            case "receipt_order": production["receipts"]!["fish"]!["inputSequence"] = 1; break;
        }
        string before = TownJson.Serialize(world.State);
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, root.ToJsonString()));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
    }

    [Test]
    public void KitchenWithNoRecipesIsRejectedBeforeItsSelectionInterfaceCanOpen()
    {
        var content = Content(); var definitions = content.Definitions;
        var error = Assert.Throws<InvalidDataException>(() => TownContent.Create(content.Manifest, definitions with
            { Production = definitions.Production! with { Recipes = [] } }));
        Assert.That(error!.Message, Does.Contain("kitchen cooking requires recipes"));
    }

    [Test]
    public void ProductionContentRejectsOverlapsReferencesAndChangesSimulationFingerprint()
    {
        var content = Content(); var definitions = content.Definitions; var production = definitions.Production!;
        Assert.Throws<InvalidDataException>(() => TownContent.Create(content.Manifest, definitions with { Production = production with
            { Fish = [new("carp", "fish.carp", 0, 50), new("trout", "fish.trout", 40, 90)] } }));
        Assert.Throws<InvalidDataException>(() => TownContent.Create(content.Manifest, definitions with { Production = production with
            { Chickens = [production.Chickens[0] with { FeedResourceId = "missing" }] } }));
        Assert.Throws<InvalidDataException>(() => TownContent.Create(content.Manifest, definitions with { Production = production with
            { FishingSeed = 0 } }));
        var reordered = TownContent.Create(content.Manifest, definitions with { Production = production with
            { Fish = production.Fish.Reverse().ToArray(), Recipes = production.Recipes.Reverse().ToArray() } });
        Assert.That(reordered.Fingerprint, Is.EqualTo(content.Fingerprint));
        var changed = TownContent.Create(content.Manifest, definitions with { Production = production with { FishingSeed = 2 } });
        Assert.That(changed.Fingerprint, Is.Not.EqualTo(content.Fingerprint));
        var world = new TownWorld(content); Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(changed, world.CaptureJson()));
        var old = JsonNode.Parse(world.CaptureJson())!; old["schemaVersion"] = 3;
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, old.ToJsonString()));
    }
}
