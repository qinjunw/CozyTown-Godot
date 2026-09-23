using System.Diagnostics.CodeAnalysis;

namespace CozyTown.Core.Town;

public sealed partial class TownWorld
{
    private static void ValidateEconomy(TownContent content, EconomyState? economy)
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
        foreach (var resource in content.Data.Resources)
            Require(economy.Player.Inventory.GetValueOrDefault(resource.Id) + economy.Shops.Sum(s => (long)s.Inventory.GetValueOrDefault(resource.Id))
                == definition.Player.Inventory.GetValueOrDefault(resource.Id) + definition.Shops.Sum(s => (long)s.Inventory.GetValueOrDefault(resource.Id)), $"resource conservation {resource.Id}");
        Require(economy.Player.Coins + economy.Shops.Sum(s => (long)s.Coins)
            == definition.Player.Coins + definition.Shops.Sum(s => (long)s.Coins), "coin conservation");
        Require(economy.Receipts.All(p => ValidOperationId(p.Key) && p.Value is not null), "receipt identities");
        Require(economy.Receipts.Values.All(r => r.InputSequence > 0 && r.InputSequence <= economy.InputSequence)
            && economy.Receipts.Values.Select(r => r.InputSequence).Distinct().Count() == economy.Receipts.Count, "receipt input sequence");
        var reconstructed = CreateInitialEconomy(content);
        foreach (var pair in economy.Receipts.OrderBy(p => p.Value.InputSequence))
        {
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
