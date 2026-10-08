using System.Diagnostics.CodeAnalysis;

namespace CozyTown.Core.Town;

public sealed partial class TownWorld
{
    private static void ValidateEconomy(TownContent content, EconomyState? economy, FarmingState? farming, ProductionState? production, double worldMinute)
    {
        void Require([DoesNotReturnIf(false)] bool condition, string field)
        {
            if (!condition) throw new InvalidDataException($"Invalid economy snapshot: {field}");
        }
        var definition = content.Data.Economy!;
        Require(economy is not null && economy.Player is not null && economy.Shops is not null && economy.Receipts is not null, "required fields");
        Require(economy.InputSequence >= 0 && economy.Player.SlotCapacity == definition.Player.SlotCapacity
            && economy.Player.SelectedQuickbarIndex is >= 0 and < 5, "player capacity/selection/input sequence");
        Require(economy.Shops.All(s => s is not null) && economy.Shops.Select(s => s.Id).Order(StringComparer.Ordinal)
            .SequenceEqual(definition.Shops.Select(s => s.Id)), "shop bindings");
        Require(economy.Player.Coins >= 0 && TownContent.ValidInventory(economy.Player.Inventory, content.Data.Resources), "player assets");
        Require(TownContent.RequiredSlots(economy.Player.Inventory, content.Data.Resources) <= economy.Player.SlotCapacity, "player inventory capacity");
        foreach (var shop in economy.Shops)
            Require(shop.Coins >= 0 && TownContent.ValidInventory(shop.Inventory, content.Data.Resources), $"shop {shop.Id} assets");
        Require(economy.Player.Coins + economy.Shops.Sum(s => (long)s.Coins)
            == definition.Player.Coins + definition.Shops.Sum(s => (long)s.Coins), "coin conservation");
        Require(economy.Receipts.All(p => ValidOperationId(p.Key) && p.Value is not null), "receipt identities");
        Require(economy.Receipts.Values.All(r => r.InputSequence > 0 && r.InputSequence <= economy.InputSequence)
            && economy.Receipts.Values.Select(r => r.InputSequence).Distinct().Count() == economy.Receipts.Count, "receipt input sequence");
        ValidateFarmingStructure(content, farming, worldMinute);
        ValidateProductionStructure(content, production, worldMinute);
        var farm = farming!;
        var productionState = production!;
        var allIdentities = economy.Receipts.Keys.Concat(farm.Receipts.Keys).Concat(farm.SleepReceipts.Keys).Concat(productionState.Receipts.Keys).ToArray();
        Require(allIdentities.Distinct(StringComparer.Ordinal).Count() == allIdentities.Length, "command identity collision");
        var sequences = economy.Receipts.Values.Select(r => r.InputSequence).Concat(farm.Receipts.Values.Select(r => r.InputSequence))
            .Concat(farm.SleepReceipts.Values.Select(r => r.InputSequence)).Concat(farm.Mornings.Select(r => r.InputSequence))
            .Concat(productionState.Receipts.Values.Select(r => r.InputSequence)).ToArray();
        Require(sequences.All(s => s > 0 && s <= economy.InputSequence) && sequences.Distinct().Count() == sequences.Length, "operation/source input sequence");
        var reconstructed = CreateInitialEconomy(content);
        var reconstructedFarm = CreateInitialFarming(content);
        var reconstructedProduction = CreateInitialProduction(content);
        var economyBySequence = economy.Receipts.ToDictionary(p => p.Value.InputSequence);
        var farmBySequence = farm.Receipts.ToDictionary(p => p.Value.InputSequence);
        var morningBySequence = farm.Mornings.ToDictionary(r => r.InputSequence);
        var sleepBySequence = farm.SleepReceipts.Values.ToDictionary(r => r.InputSequence);
        var productionBySequence = productionState.Receipts.Values.ToDictionary(r => r.InputSequence);
        double lastFarmingMinute = content.Data.StartMinute;
        double lastCommandMinute = content.Data.StartMinute;
        foreach (long sequence in sequences.Order())
        {
            if (morningBySequence.TryGetValue(sequence, out var morning))
            {
                double at = morning.Day * 1440 + MorningMinute;
                Require(morning.Day == reconstructedFarm.LastSettledDay + 1 && at >= lastFarmingMinute && at <= worldMinute, "morning source day/order");
                ApplyMorning(content, reconstructed, reconstructedFarm, morning.Day);
                ApplyChickenMorning(reconstructedProduction);
                lastFarmingMinute = at;
                continue;
            }
            if (farmBySequence.TryGetValue(sequence, out var farmPair))
            {
                var operation = farmPair.Value;
                Require(operation.WorldMinute >= lastFarmingMinute && operation.WorldMinute <= worldMinute, "farm operation time/order");
                lastFarmingMinute = operation.WorldMinute;
                lastCommandMinute = operation.WorldMinute;
                if (operation.Result.Succeeded)
                {
                    Require(operation.Request.ActorId == "player" && reconstructedFarm.Plots.Any(p => p.Id == operation.Request.PlotId)
                        && EvaluateFarm(content, reconstructed, reconstructedFarm, operation.Request) == "accepted", "farm history conditions/assets");
                    ApplyFarm(content, reconstructed, reconstructedFarm, operation.Request);
                }
                continue;
            }
            if (sleepBySequence.TryGetValue(sequence, out var sleep))
            {
                Require(sleep.Result.FromMinute >= lastCommandMinute - 1e-7 && sleep.Result.ToMinute >= lastFarmingMinute - 1e-7, "sleep source interval/order");
                lastCommandMinute = sleep.Result.ToMinute;
                lastFarmingMinute = sleep.Result.ToMinute;
                continue;
            }
            if (productionBySequence.TryGetValue(sequence, out var productionOperation))
            {
                Require(productionOperation.WorldMinute >= lastFarmingMinute && productionOperation.WorldMinute <= worldMinute, "production operation time/order");
                lastFarmingMinute = productionOperation.WorldMinute;
                lastCommandMinute = productionOperation.WorldMinute;
                Require(productionOperation.RandomStateBefore == reconstructedProduction.FishingRandomState, "production random state before operation");
                if (productionOperation.Result.Succeeded)
                {
                    Require(productionOperation.Request.ActorId == "player" && ProductionTargetExists(content, productionOperation.Request.TargetId)
                        && EvaluateProduction(content, reconstructed, reconstructedProduction, productionOperation.Request) == productionOperation.Result, "production history conditions/output");
                    ApplyProduction(content, reconstructed, reconstructedProduction, productionOperation.Request, productionOperation.Result);
                }
                else if (productionOperation.Result.Roll is int roll)
                    Require(productionOperation.Request.Action == "fish" && roll == NextFishingRandom(reconstructedProduction.FishingRandomState) % 100, "rejected fishing roll");
                Require(productionOperation.RandomStateAfter == reconstructedProduction.FishingRandomState, "production random state after operation");
                continue;
            }
            if (!economyBySequence.TryGetValue(sequence, out var pair)) continue;
            var receipt = pair.Value;
            if (receipt.Kind == "trade")
            {
                Require(receipt.Trade is not null && receipt.TradeResult is not null && receipt.Selection is null && receipt.SelectionResult is null, "trade receipt fields");
                var request = receipt.Trade;
                var result = receipt.TradeResult;
                Require(new[] { request.OperationId, request.ActorId, request.ShopId, request.ResourceId, request.Direction }
                    .All(v => v is not null && v.Length <= 200 && !v.Any(char.IsControl)), "trade request text");
                Require(request.OperationId == pair.Key && result.OperationId == pair.Key && !result.IsDuplicate
                    && result.UnitPrice >= 0 && result.TotalPrice >= 0 && TradeReceiptCodes.Contains(result.Code)
                    && result.Succeeded == (result.Code == "accepted"), "trade receipt result");
                if (result.Succeeded)
                {
                    var shop = definition.Shops.SingleOrDefault(s => s.Id == request.ShopId);
                    var price = shop?.Prices.SingleOrDefault(p => p.ResourceId == request.ResourceId);
                    Require(request.ActorId == "player" && shop is not null && price is not null
                        && request.Direction is "buy" or "sell" && request.Quantity > 0, "accepted trade references");
                    int unit = request.Direction == "buy" ? price.BuyPrice : price.SellPrice;
                    Require(unit > 0 && unit == request.ExpectedUnitPrice && unit == result.UnitPrice
                        && result.TotalPrice == (long)unit * request.Quantity && result.TotalPrice <= int.MaxValue, "accepted trade price");
                    try { ApplyAssetTransfer(reconstructed, request, (int)result.TotalPrice); }
                    catch (OverflowException) { throw new InvalidDataException("Invalid economy snapshot: trade history overflow."); }
                    Require(reconstructed.Player.Coins >= 0 && reconstructed.Shops.All(s => s.Coins >= 0)
                        && reconstructed.Player.Inventory.Values.All(q => q >= 0) && reconstructed.Shops.All(s => s.Inventory.Values.All(q => q >= 0))
                        && TownContent.RequiredSlots(reconstructed.Player.Inventory, content.Data.Resources) <= reconstructed.Player.SlotCapacity, "trade history assets/capacity");
                }
            }
            else
            {
                Require(receipt.Kind == "quickbar_select" && receipt.Selection is not null && receipt.SelectionResult is not null
                    && receipt.Trade is null && receipt.TradeResult is null, "selection receipt fields");
                var request = receipt.Selection;
                var result = receipt.SelectionResult;
                Require(request.OperationId == pair.Key && request.ActorId == "player" && result.OperationId == pair.Key
                    && !result.IsDuplicate && result.Index == request.Index && result.Succeeded == (request.Index is >= 0 and < 5)
                    && result.Code == (result.Succeeded ? "accepted" : "invalid_quickbar_index"), "selection receipt result");
                if (result.Succeeded) reconstructed.Player.SelectedQuickbarIndex = request.Index;
            }
        }
        Require(reconstructedFarm.LastSettledDay == farm.LastSettledDay
            && TownJson.Serialize(reconstructedFarm.Plots) == TownJson.Serialize(farm.Plots), "farming state differs from recorded operations/sources");
        Require(reconstructedProduction.FishingRandomState == productionState.FishingRandomState
            && reconstructedProduction.FishingDrawCount == productionState.FishingDrawCount
            && TownJson.Serialize(reconstructedProduction.Chickens) == TownJson.Serialize(productionState.Chickens), "production state differs from recorded operations/sources");
        bool SameInventory(Dictionary<string, int> first, Dictionary<string, int> second) => content.Data.Resources.All(r => first.GetValueOrDefault(r.Id) == second.GetValueOrDefault(r.Id));
        Require(reconstructed.Player.Coins == economy.Player.Coins && SameInventory(reconstructed.Player.Inventory, economy.Player.Inventory)
            && reconstructed.Player.SelectedQuickbarIndex == economy.Player.SelectedQuickbarIndex, "player assets/selection differ from recorded operations");
        foreach (var shop in economy.Shops)
        {
            var expected = reconstructed.Shops.Single(s => s.Id == shop.Id);
            Require(shop.Coins == expected.Coins && SameInventory(shop.Inventory, expected.Inventory), $"shop {shop.Id} assets differ from recorded operations");
        }
    }

    private static readonly HashSet<string> TradeReceiptCodes =
    [
        "accepted", "invalid_actor", "unknown_shop", "shop_out_of_reach", "invalid_direction", "unknown_resource",
        "invalid_quantity", "direction_disabled", "numeric_overflow", "insufficient_shop_stock", "insufficient_player_coins",
        "inventory_full", "insufficient_player_stock", "insufficient_shop_coins", "price_changed"
    ];
}
