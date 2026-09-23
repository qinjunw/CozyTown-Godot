namespace CozyTown.Core.Town;

public sealed partial class TownWorld
{
    public const double ShopInteractionDistance = 42;
    private bool _economyCommitPending;

    private static EconomyState CreateInitialEconomy(TownContent content)
    {
        var definition = content.Data.Economy!;
        return new EconomyState
        {
            Player = new PlayerAssetState { Coins = definition.Player.Coins, SlotCapacity = definition.Player.SlotCapacity,
                Inventory = new(definition.Player.Inventory) },
            Shops = definition.Shops.Select(s => new ShopAssetState { Id = s.Id, Coins = s.Coins, Inventory = new(s.Inventory) }).ToList()
        };
    }

    public IReadOnlyList<InventorySlot> GetPlayerInventorySlots()
    {
        var slots = new List<InventorySlot>();
        foreach (var resource in _content.Data.Resources.OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            int remaining = _state.Economy.Player.Inventory.GetValueOrDefault(resource.Id);
            while (remaining > 0)
            {
                int amount = Math.Min(remaining, resource.StackLimit);
                slots.Add(new(slots.Count, resource.Id, amount));
                remaining -= amount;
            }
        }
        while (slots.Count < _state.Economy.Player.SlotCapacity) slots.Add(new(slots.Count, null, 0));
        return slots.ToArray();
    }

    public IReadOnlyList<InventorySlot> GetQuickbarSlots() => GetPlayerInventorySlots().Take(5).ToArray();

    public bool CanInteractWithShop(string shopId)
    {
        var shop = _content.Data.Economy!.Shops.SingleOrDefault(s => s.Id == shopId);
        if (shop is null) return false;
        var point = _content.Place(shop.PlaceId).Position;
        return _state.PlayerPosition.DistanceTo(point) <= ShopInteractionDistance && HasSight(_state.PlayerPosition, point);
    }

    public TradeQuote QuoteTrade(string shopId, string resourceId, string direction, int quantity)
        => EvaluateTrade(_state.Economy, shopId, resourceId, direction, quantity);

    private TradeQuote EvaluateTrade(EconomyState economy, string shopId, string resourceId, string direction, int quantity)
    {
        int unitPrice = 0;
        long totalPrice = 0;
        TradeQuote Reject(string code) => new(shopId, resourceId, direction, quantity, unitPrice, totalPrice, false, code);
        var definition = _content.Data.Economy!.Shops.SingleOrDefault(s => s.Id == shopId);
        if (definition is null) return Reject("unknown_shop");
        if (!CanInteractWithShop(shopId)) return Reject("shop_out_of_reach");
        if (direction is not ("buy" or "sell")) return Reject("invalid_direction");
        if (!_content.Data.Resources.Any(r => r.Id == resourceId)) return Reject("unknown_resource");
        if (quantity <= 0) return Reject("invalid_quantity");
        var price = definition.Prices.SingleOrDefault(p => p.ResourceId == resourceId);
        unitPrice = direction == "buy" ? price?.BuyPrice ?? 0 : price?.SellPrice ?? 0;
        if (unitPrice == 0) return Reject("direction_disabled");
        totalPrice = (long)unitPrice * quantity;
        if (totalPrice > int.MaxValue) return Reject("numeric_overflow");
        var player = economy.Player;
        var shop = economy.Shops.Single(s => s.Id == shopId);
        if (direction == "buy")
        {
            if (shop.Inventory.GetValueOrDefault(resourceId) < quantity) return Reject("insufficient_shop_stock");
            if (player.Coins < totalPrice) return Reject("insufficient_player_coins");
            if ((long)shop.Coins + totalPrice > int.MaxValue || (long)player.Inventory.GetValueOrDefault(resourceId) + quantity > int.MaxValue) return Reject("numeric_overflow");
            var inventory = new Dictionary<string, int>(player.Inventory) { [resourceId] = player.Inventory.GetValueOrDefault(resourceId) + quantity };
            if (TownContent.RequiredSlots(inventory, _content.Data.Resources) > player.SlotCapacity) return Reject("inventory_full");
        }
        else
        {
            if (player.Inventory.GetValueOrDefault(resourceId) < quantity) return Reject("insufficient_player_stock");
            if (shop.Coins < totalPrice) return Reject("insufficient_shop_coins");
            if ((long)player.Coins + totalPrice > int.MaxValue || (long)shop.Inventory.GetValueOrDefault(resourceId) + quantity > int.MaxValue) return Reject("numeric_overflow");
        }
        return new(shopId, resourceId, direction, quantity, unitPrice, totalPrice, true, "accepted");
    }

    // The synchronous callback may persist the record but must not mutate this world.
    public TradeResult Trade(TradeRequest request, Action<EconomyOperationRecord>? persistBeforeCommit = null)
    {
        EnsureEconomyNotCommitting();
        ArgumentNullException.ThrowIfNull(request);
        RequireCommandText(request.OperationId, request.ActorId, request.ShopId, request.ResourceId, request.Direction);
        var candidate = TownJson.Clone(_state);
        var economy = candidate.Economy;
        long sequence = checked(economy.InputSequence + 1);
        TradeResult result;
        if (economy.Receipts.TryGetValue(request.OperationId, out var prior))
            result = prior.Kind == "trade" && prior.Trade == request
                ? prior.TradeResult! with { IsDuplicate = true }
                : new(request.OperationId, false, "operation_id_conflict", 0, 0);
        else
        {
            if (!ValidOperationId(request.OperationId)) result = new(request.OperationId, false, "invalid_operation_id", 0, 0);
            else if (request.ActorId != "player") result = new(request.OperationId, false, "invalid_actor", 0, 0);
            else
            {
                var quote = EvaluateTrade(economy, request.ShopId, request.ResourceId, request.Direction, request.Quantity);
                string code = quote.CanTrade && request.ExpectedUnitPrice != quote.UnitPrice ? "price_changed" : quote.Code;
                result = new(request.OperationId, code == "accepted", code, quote.UnitPrice, quote.TotalPrice);
                if (result.Succeeded) ApplyAssetTransfer(economy, request, checked((int)result.TotalPrice));
            }
            if (ValidOperationId(request.OperationId)) economy.Receipts.Add(request.OperationId, new(sequence, "trade", request, result, null, null));
        }
        economy.InputSequence = sequence;
        var record = new EconomyOperationRecord(1, sequence, candidate.BranchId, candidate.WorldMinute, "trade", request, result, null, null, TownJson.Clone(economy));
        PublishEconomy(candidate, record, "economy_trade", result.Code, new { record.SchemaVersion, record.InputSequence, request, result }, persistBeforeCommit);
        return result;
    }

    public QuickbarSelectionResult SelectQuickbar(string operationId, int index, Action<EconomyOperationRecord>? persistBeforeCommit = null)
    {
        EnsureEconomyNotCommitting();
        RequireCommandText(operationId);
        var request = new QuickbarSelectionRequest(operationId, "player", index);
        var candidate = TownJson.Clone(_state);
        var economy = candidate.Economy;
        long sequence = checked(economy.InputSequence + 1);
        QuickbarSelectionResult result;
        if (economy.Receipts.TryGetValue(operationId, out var prior))
            result = prior.Kind == "quickbar_select" && prior.Selection == request
                ? prior.SelectionResult! with { IsDuplicate = true }
                : new(operationId, false, "operation_id_conflict", index);
        else
        {
            string code = !ValidOperationId(operationId) ? "invalid_operation_id" : index is < 0 or >= 5 ? "invalid_quickbar_index" : "accepted";
            result = new(operationId, code == "accepted", code, index);
            if (result.Succeeded) economy.Player.SelectedQuickbarIndex = index;
            if (ValidOperationId(operationId)) economy.Receipts.Add(operationId, new(sequence, "quickbar_select", null, null, request, result));
        }
        economy.InputSequence = sequence;
        var record = new EconomyOperationRecord(1, sequence, candidate.BranchId, candidate.WorldMinute, "quickbar_select", null, null, request, result, TownJson.Clone(economy));
        PublishEconomy(candidate, record, "quickbar_selected", result.Code, new { record.SchemaVersion, record.InputSequence, request, result }, persistBeforeCommit);
        return result;
    }

    private void PublishEconomy(TownState candidate, EconomyOperationRecord record, string kind, string code, object eventData, Action<EconomyOperationRecord>? persist)
    {
        candidate.Events.Add(new TownEvent(checked(++candidate.Sequence), candidate.BranchId, candidate.WorldMinute,
            kind, "player", "player", code, null, null, TownJson.Serialize(eventData)));
        _economyCommitPending = true;
        try
        {
            persist?.Invoke(record);
            _state = candidate;
        }
        finally { _economyCommitPending = false; }
    }

    private static void ApplyAssetTransfer(EconomyState economy, TradeRequest request, int total)
    {
        var player = economy.Player;
        var shop = economy.Shops.Single(s => s.Id == request.ShopId);
        int amount = request.Direction == "buy" ? request.Quantity : -request.Quantity;
        int coins = request.Direction == "buy" ? -total : total;
        player.Coins = checked(player.Coins + coins);
        shop.Coins = checked(shop.Coins - coins);
        player.Inventory[request.ResourceId] = checked(player.Inventory.GetValueOrDefault(request.ResourceId) + amount);
        shop.Inventory[request.ResourceId] = checked(shop.Inventory.GetValueOrDefault(request.ResourceId) - amount);
        if (player.Inventory[request.ResourceId] == 0) player.Inventory.Remove(request.ResourceId);
        if (shop.Inventory[request.ResourceId] == 0) shop.Inventory.Remove(request.ResourceId);
    }

    private static bool ValidOperationId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 200 && !id.Any(char.IsControl);
    private static void RequireCommandText(params string[] values)
    {
        if (values.Any(v => v is null || v.Length > 200 || v.Any(char.IsControl)))
            throw new ArgumentException("Economy command text must be non-null, at most 200 characters, and contain no control characters.");
    }
    private void EnsureEconomyNotCommitting()
    {
        if (_economyCommitPending) throw new InvalidOperationException("World mutation is not allowed from an economy persistence callback.");
    }
}
