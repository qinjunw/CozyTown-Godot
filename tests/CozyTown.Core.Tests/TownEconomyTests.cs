using System.Text.Json.Nodes;
using CozyTown.Core.Town;
using NUnit.Framework;

namespace CozyTown.Core.Tests;

[TestFixture]
public sealed class TownEconomyTests
{
    private static TownContent Content(int playerCoins = 100, int shopCoins = 100, int apples = 3,
        int shopApples = 20, int buyPrice = 4, bool wall = false)
    {
        PlaceDefinition[] places = [new("player.spawn", "Player", new(32, 32), "outside"),
            new("shop.entrance", "Entrance", new(wall ? 82 : 40, 32), "outside"),
            new("home", "Home", new(96, 96), "outside")];
        var residents = new[] { "mina", "eli", "ren", "sora" }.Select(id => new ResidentDefinition(id, id, "home", "home", "apple",
            new() { ["bread"] = 3 }, 5, [new("free", 0, 1440, "home", "free", true)])).ToArray();
        return TownContent.Create(new(256, 192, places, wall ? [new("wall", new(68, 24, 4, 16))] : [], [new("outside", new(0, 0, 256, 192))]),
            new(residents, [new("apple", "Apple", 10, 2), new("bread", "Bread", 10, 1), new("seed", "Seed", 99, 0)], 540,
                new(new(playerCoins, 5, new() { ["apple"] = apples }),
                    [new("shop.general", "General", "shop.entrance", shopCoins, new() { ["apple"] = shopApples, ["bread"] = 20, ["seed"] = 20 },
                        [new("apple", buyPrice, 2), new("bread", 3, 1), new("seed", 5, 0)])])));
    }

    private static TradeRequest Buy(string id = "buy-1", int quantity = 2, string resource = "apple", int price = 4)
        => new(id, "player", "shop.general", resource, "buy", quantity, price);
    private static TradeRequest Sell(string id = "sell-1", int quantity = 1, string resource = "apple", int price = 2)
        => new(id, "player", "shop.general", resource, "sell", quantity, price);
    private static string Assets(TownWorld world) => TownJson.Serialize(new { world.State.Economy.Player, world.State.Economy.Shops, world.State.Residents });

    [Test]
    public void BuyAndSellPreserveEveryAssetOwnerAndUseConfiguredQuotes()
    {
        var world = new TownWorld(Content());
        string residents = TownJson.Serialize(world.State.Residents);
        var quote = world.QuoteTrade("shop.general", "apple", "buy", 2);
        Assert.That(quote.CanTrade, Is.True);
        Assert.That(quote.UnitPrice, Is.EqualTo(4));
        Assert.That(quote.TotalPrice, Is.EqualTo(8));
        Assert.That(world.Trade(Buy()).Succeeded, Is.True);
        Assert.That(world.State.Economy.Player.Coins, Is.EqualTo(92));
        Assert.That(world.State.Economy.Player.Inventory["apple"], Is.EqualTo(5));
        Assert.That(world.State.Economy.Shops.Single().Coins, Is.EqualTo(108));
        Assert.That(world.State.Economy.Shops.Single().Inventory["apple"], Is.EqualTo(18));
        Assert.That(world.Trade(Sell()).Succeeded, Is.True);
        Assert.That(world.State.Economy.Player.Coins, Is.EqualTo(94));
        Assert.That(world.State.Economy.Player.Inventory["apple"], Is.EqualTo(4));
        Assert.That(world.State.Economy.Shops.Single().Coins, Is.EqualTo(106));
        Assert.That(world.State.Economy.Shops.Single().Inventory["apple"], Is.EqualTo(19));
        Assert.That(TownJson.Serialize(world.State.Residents), Is.EqualTo(residents));
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(Content(), world.CaptureJson()));
    }

    [TestCase("no_money", "insufficient_player_coins")]
    [TestCase("no_stock", "insufficient_shop_stock")]
    [TestCase("full", "inventory_full")]
    [TestCase("zero", "invalid_quantity")]
    [TestCase("negative", "invalid_quantity")]
    [TestCase("wrong_price", "price_changed")]
    [TestCase("no_player_stock", "insufficient_player_stock")]
    [TestCase("shop_no_money", "insufficient_shop_coins")]
    [TestCase("disabled", "direction_disabled")]
    [TestCase("wrong_actor", "invalid_actor")]
    [TestCase("wrong_shop", "unknown_shop")]
    [TestCase("wrong_resource", "unknown_resource")]
    [TestCase("wrong_direction", "invalid_direction")]
    [TestCase("total_overflow", "numeric_overflow")]
    [TestCase("shop_coins_overflow", "numeric_overflow")]
    [TestCase("player_coins_overflow", "numeric_overflow")]
    [TestCase("shop_quantity_overflow", "numeric_overflow")]
    public void RejectedTradesPersistTheirOutcomeWithoutChangingAssets(string scenario, string expected)
    {
        var content = scenario switch
        {
            "no_money" => Content(playerCoins: 0), "no_stock" => Content(shopApples: 0),
            "full" => Content(apples: 50), "shop_no_money" => Content(shopCoins: 0),
            "total_overflow" => Content(buyPrice: int.MaxValue),
            "shop_coins_overflow" => Content(shopCoins: int.MaxValue),
            "player_coins_overflow" => Content(playerCoins: int.MaxValue),
            "shop_quantity_overflow" => Content(shopApples: int.MaxValue), _ => Content()
        };
        var request = scenario switch
        {
            "full" => Buy(resource: "bread", price: 3), "zero" => Buy(quantity: 0), "negative" => Buy(quantity: -1),
            "wrong_price" => Buy(price: 1), "no_player_stock" => Sell(quantity: 4),
            "shop_no_money" or "player_coins_overflow" or "shop_quantity_overflow" => Sell(),
            "disabled" => Sell(resource: "seed", price: 0), "wrong_actor" => Buy() with { ActorId = "mina" },
            "wrong_shop" => Buy() with { ShopId = "missing" }, "wrong_resource" => Buy(resource: "missing"),
            "wrong_direction" => Buy() with { Direction = "give" }, "total_overflow" => Buy(price: int.MaxValue), _ => Buy()
        };
        var world = new TownWorld(content);
        string before = Assets(world);
        EconomyOperationRecord? logged = null;
        var result = world.Trade(request, record => logged = record);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Code, Is.EqualTo(expected));
        Assert.That(Assets(world), Is.EqualTo(before));
        Assert.That(logged!.Trade, Is.EqualTo(request));
        Assert.That(logged.TradeResult, Is.EqualTo(result));
        Assert.That(logged.Checkpoint.Receipts[request.OperationId].TradeResult, Is.EqualTo(result));
        var restored = TownWorld.RestoreCandidate(content, world.CaptureJson());
        Assert.That(restored.Trade(request), Is.EqualTo(result with { IsDuplicate = true }));
        Assert.That(Assets(restored), Is.EqualTo(before));
    }

    [Test]
    public void ShopDistanceAndLineOfSightAreCheckedByCore()
    {
        var world = new TownWorld(Content());
        Assert.That(world.SetPlayerPosition(new(160, 32)), Is.True);
        Assert.That(world.Trade(Buy()).Code, Is.EqualTo("shop_out_of_reach"));
        var wall = new TownWorld(Content(wall: true));
        Assert.That(wall.SetPlayerPosition(new(58, 32)), Is.True);
        Assert.That(wall.CanInteractWithShop("shop.general"), Is.False);
        Assert.That(wall.Trade(Buy()).Code, Is.EqualTo("shop_out_of_reach"));
    }

    [Test]
    public void PersistFailureLeavesAssetsReceiptsEventsAndInputSequenceUnchanged()
    {
        var world = new TownWorld(Content());
        string before = TownJson.Serialize(world.State);
        Assert.Throws<IOException>(() => world.Trade(Buy(), record =>
        {
            Assert.That(record.Checkpoint.Player.Coins, Is.EqualTo(92));
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
            throw new IOException("Injected record failure.");
        }));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
        Assert.Throws<IOException>(() => world.SelectQuickbar("select-1", 4, _ => throw new IOException("Injected record failure.")));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
        Assert.That(world.Trade(Buy()).Succeeded, Is.True);
    }

    [Test]
    public void PersistenceCallbackCannotMutateCandidateOrReenterWorld()
    {
        var world = new TownWorld(Content());
        Assert.That(world.Trade(Buy(), record => { record.Checkpoint.Player.Coins = 0; record.Checkpoint.Receipts.Clear(); }).Succeeded, Is.True);
        Assert.That(world.State.Economy.Player.Coins, Is.EqualTo(92));
        Assert.That(world.State.Economy.Receipts, Has.Count.EqualTo(1));
        string before = TownJson.Serialize(world.State);
        Assert.Throws<InvalidOperationException>(() => world.Trade(Buy("nested"), _ => world.Advance(1, 1)));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
        Assert.Throws<InvalidOperationException>(() => world.SelectQuickbar("nested-select", 1, _ => world.Trade(Buy("other"))));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
    }

    [Test]
    public void DuplicateAndConflictingOperationsDoNotRepeatTransferAfterRestore()
    {
        var content = Content();
        var world = new TownWorld(content);
        var result = world.Trade(Buy());
        var restored = TownWorld.RestoreCandidate(content, world.CaptureJson());
        string assets = Assets(restored);
        Assert.That(restored.Trade(Buy()), Is.EqualTo(result with { IsDuplicate = true }));
        Assert.That(restored.Trade(Buy(quantity: 1)).Code, Is.EqualTo("operation_id_conflict"));
        Assert.That(restored.SelectQuickbar("buy-1", 2).Code, Is.EqualTo("operation_id_conflict"));
        Assert.That(Assets(restored), Is.EqualTo(assets));
        Assert.That(restored.State.Economy.Receipts, Has.Count.EqualTo(1));
        Assert.That(restored.State.Economy.InputSequence, Is.EqualTo(4));
        Assert.That(restored.Trade(Buy("intentional-second-buy")).Succeeded, Is.True);
        Assert.That(restored.State.Economy.Player.Coins, Is.EqualTo(84));
        Assert.That(restored.State.Economy.Player.Inventory["apple"], Is.EqualTo(7));
    }

    [Test]
    public void InventoryProjectionUsesCatalogOrderSplitsStacksCompactsAndPreservesSelection()
    {
        var content = Content(apples: 12);
        var world = new TownWorld(content);
        world.Trade(Buy("buy-bread", 1, "bread", 3));
        Assert.That(world.GetQuickbarSlots(), Is.EqualTo(new InventorySlot[]
            { new(0,"bread",1),new(1,"apple",10),new(2,"apple",2),new(3,null,0),new(4,null,0) }));
        Assert.That(world.SelectQuickbar("select-last", 4).Succeeded, Is.True);
        world.Trade(Sell("sell-bread", 1, "bread", 1));
        Assert.That(world.GetQuickbarSlots()[0], Is.EqualTo(new InventorySlot(0, "apple", 10)));
        Assert.That(world.GetPlayerInventorySlots(), Has.Count.EqualTo(5));
        var restored = TownWorld.RestoreCandidate(content, world.CaptureJson());
        Assert.That(restored.State.Economy.Player.SelectedQuickbarIndex, Is.EqualTo(4));
        Assert.That(restored.GetQuickbarSlots(), Is.EqualTo(world.GetQuickbarSlots()));
        Assert.That(restored.SelectQuickbar("select-last", 4).IsDuplicate, Is.True);
        Assert.That(restored.SelectQuickbar("invalid", 5).Code, Is.EqualTo("invalid_quickbar_index"));
        Assert.That(restored.State.Economy.Player.SelectedQuickbarIndex, Is.EqualTo(4));
    }

    [Test]
    public void QuotesAndReturnedViewsDoNotMutateAuthoritativeState()
    {
        var world = new TownWorld(Content());
        string before = TownJson.Serialize(world.State);
        world.QuoteTrade("shop.general", "apple", "buy", 2);
        world.State.Economy.Player.Inventory.Clear();
        world.GetView().Economy.Shops[0].Coins = 0;
        world.GetPlayerInventorySlots();
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
    }

    [TestCase("economy")]
    [TestCase("economy.player")]
    [TestCase("economy.shops")]
    [TestCase("economy.inputSequence")]
    [TestCase("economy.receipts")]
    [TestCase("economy.player.coins")]
    [TestCase("economy.player.inventory")]
    [TestCase("economy.player.slotCapacity")]
    [TestCase("economy.player.selectedQuickbarIndex")]
    public void MissingEconomyFieldsRejectWithoutChangingCurrentWorld(string field)
    {
        var content = Content(); var world = new TownWorld(content); world.Trade(Buy());
        string before = TownJson.Serialize(world.State);
        var node = JsonNode.Parse(world.CaptureJson())!;
        JsonNode current = node["state"]!;
        var path = field.Split('.');
        foreach (string part in path.SkipLast(1)) current = current[part]!;
        current.AsObject().Remove(path[^1]);
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, node.ToJsonString()));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
    }

    [TestCase("coins")]
    [TestCase("quantity")]
    [TestCase("conserved_swap")]
    [TestCase("shop_identity")]
    [TestCase("selection")]
    [TestCase("receipt_price")]
    [TestCase("receipt_identity")]
    [TestCase("receipt_removed")]
    [TestCase("future_input_sequence")]
    [TestCase("old_schema")]
    public void CorruptEconomyAssetsAndReceiptReferencesAreRejected(string field)
    {
        var content = Content(); var world = new TownWorld(content); world.Trade(Buy());
        var node = JsonNode.Parse(world.CaptureJson())!;
        var economy = node["state"]!["economy"]!;
        switch (field)
        {
            case "coins": economy["player"]!["coins"] = -1; break;
            case "quantity": economy["player"]!["inventory"]!["apple"] = 999; break;
            case "conserved_swap": economy["player"]!["coins"] = 93; economy["shops"]![0]!["coins"] = 107; break;
            case "shop_identity": economy["shops"]![0]!["id"] = "mina"; break;
            case "selection": economy["player"]!["selectedQuickbarIndex"] = 4; break;
            case "receipt_price": economy["receipts"]!["buy-1"]!["tradeResult"]!["unitPrice"] = 1; break;
            case "receipt_identity": economy["receipts"]!["buy-1"]!["trade"]!["operationId"] = "other"; break;
            case "receipt_removed": economy["receipts"]!.AsObject().Clear(); break;
            case "future_input_sequence": economy["inputSequence"] = node["state"]!["sequence"]!.GetValue<long>() + 1; break;
            case "old_schema": node["schemaVersion"] = 1; break;
        }
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, node.ToJsonString()));
    }

    [Test]
    public void EconomyDefinitionsAreCanonicalAndChangesEnterCompatibilityFingerprint()
    {
        var content = Content(); var definition = content.Definitions;
        var reordered = definition with { Resources = definition.Resources.Reverse().ToArray(), Economy = definition.Economy! with
            { Shops = definition.Economy.Shops.Select(s => s with { Prices = s.Prices.Reverse().ToArray(), Inventory = s.Inventory.Reverse().ToDictionary(p => p.Key,p => p.Value) }).ToArray() } };
        Assert.That(TownContent.Create(content.Manifest, reordered).Fingerprint, Is.EqualTo(content.Fingerprint));
        var changed = definition with { Resources = definition.Resources.Select(r => r.Id == "apple" ? r with { StackLimit = 11 } : r).ToArray() };
        Assert.That(TownContent.Create(content.Manifest, changed).Fingerprint, Is.Not.EqualTo(content.Fingerprint));
        var snapshot = new TownWorld(content).CaptureJson();
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(TownContent.Create(content.Manifest, changed), snapshot));
    }

    [Test]
    public void InvalidEconomyDefinitionsRejectDuplicateOwnersReferencesAndInitialCapacity()
    {
        var content = Content(); var definition = content.Definitions; var economy = definition.Economy!;
        Assert.Throws<InvalidDataException>(() => TownContent.Create(content.Manifest, definition with { Economy = economy with { Shops = [economy.Shops[0], economy.Shops[0]] } }));
        Assert.Throws<InvalidDataException>(() => TownContent.Create(content.Manifest, definition with { Economy = economy with { Shops = [economy.Shops[0] with { Id = "mina" }] } }));
        Assert.Throws<InvalidDataException>(() => TownContent.Create(content.Manifest, definition with { Economy = economy with { Shops = [economy.Shops[0] with { PlaceId = "missing" }] } }));
        Assert.Throws<InvalidDataException>(() => TownContent.Create(content.Manifest, definition with { Economy = economy with { Player = economy.Player with { Inventory = new() { ["apple"] = 51 } } } }));
        Assert.Throws<InvalidDataException>(() => TownContent.Create(content.Manifest, definition with { Resources = [..definition.Resources.Where(r => r.Id != "apple")] }));
    }
}
