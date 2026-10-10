namespace CozyTown.Core.Town;

public sealed partial class TownWorld
{
    public const double MaximumAdvanceMinutes = 7 * 1440;
    public const int MorningMinute = 300;
    public const double FarmingInteractionDistance = 42;

    private static long SettledDayAt(double minute) => (long)Math.Floor((minute - MorningMinute) / 1440);
    private static FarmingState CreateInitialFarming(TownContent content) => new()
    {
        LastSettledDay = SettledDayAt(content.Data.StartMinute),
        Plots = content.Data.Farming!.Plots.Select(p => new FarmPlotState { Id = p.Id }).ToList()
    };

    public bool CanInteractWithPlot(string plotId)
    {
        var plot = _content.Data.Farming!.Plots.SingleOrDefault(p => p.Id == plotId);
        return plot is not null && CanReachFarmingPlace(plot.PlaceId);
    }

    public bool CanSleep() => _content.Data.Farming!.BedPlaceId is string bed && CanReachFarmingPlace(bed);

    private bool CanReachFarmingPlace(string placeId)
    {
        var point = _content.Place(placeId).Position;
        return _state.PlayerPosition.DistanceTo(point) <= FarmingInteractionDistance && HasSight(_state.PlayerPosition, point);
    }

    public FarmResult Farm(FarmRequest request, Action<FarmOperationRecord>? persistBeforeCommit = null)
    {
        using var trace = TraceInput("farm", new { request });
        EnsureEconomyNotCommitting();
        ArgumentNullException.ThrowIfNull(request);
        RequireCommandText(request.OperationId, request.ActorId, request.PlotId, request.Action);
        if (request.CropId is not null) RequireCommandText(request.CropId);
        var priorPlot = _state.Farming.Plots.SingleOrDefault(p => p.Id == request.PlotId);
        var sourceCrop = _content.Data.Farming!.Crops.SingleOrDefault(c => c.Id == (request.Action == "plant" ? request.CropId : priorPlot?.CropId));
        var candidate = TownJson.Clone(_state);
        long sequence = checked(++candidate.Economy.InputSequence);
        FarmResult result;
        if (candidate.Farming.Receipts.TryGetValue(request.OperationId, out var prior))
            result = prior.Request == request ? prior.Result with { IsDuplicate = true }
                : new(request.OperationId, false, "operation_id_conflict");
        else if (candidate.Economy.Receipts.ContainsKey(request.OperationId) || candidate.Farming.SleepReceipts.ContainsKey(request.OperationId) || candidate.Production.Receipts.ContainsKey(request.OperationId))
            result = new(request.OperationId, false, "operation_id_conflict");
        else
        {
            string code = !ValidOperationId(request.OperationId) ? "invalid_operation_id"
                : request.ActorId != "player" ? "invalid_actor"
                : !_content.Data.Farming!.Plots.Any(p => p.Id == request.PlotId) ? "unknown_plot"
                : !CanInteractWithPlot(request.PlotId) ? "plot_out_of_reach"
                : EvaluateFarm(_content, candidate.Economy, candidate.Farming, request);
            result = new(request.OperationId, code == "accepted", code);
            if (result.Succeeded) ApplyFarm(_content, candidate.Economy, candidate.Farming, request);
            if (ValidOperationId(request.OperationId)) candidate.Farming.Receipts.Add(request.OperationId, new(sequence, candidate.WorldMinute, request, result));
        }
        AddCandidateEvent(candidate, "farming_command", result.Code, new { schemaVersion = 1, inputSequence = sequence, request, result });
        if (result.Succeeded && !result.IsDuplicate && request.Action is "plant" or "harvest")
        {
            AddCandidateEvent(candidate, "production_source", request.Action, new { inputSequence = sequence, request.OperationId, request.PlotId,
                cropId = sourceCrop!.Id, generation = candidate.Farming.Plots.Single(p => p.Id == request.PlotId).Generation,
                resourceId = request.Action == "plant" ? sourceCrop.SeedResourceId : sourceCrop.HarvestResourceId,
                quantity = request.Action == "plant" ? -1 : sourceCrop.HarvestQuantity });
        }
        var record = new FarmOperationRecord(1, sequence, candidate.BranchId, candidate.WorldMinute, request, result, TownJson.Clone(candidate));
        PublishFarming(candidate, () => persistBeforeCommit?.Invoke(record));
        return result;
    }

    private static string EvaluateFarm(TownContent content, EconomyState economy, FarmingState farming, FarmRequest request)
    {
        var plot = farming.Plots.Single(p => p.Id == request.PlotId);
        if (request.Action == "plant")
        {
            var crop = content.Data.Farming!.Crops.SingleOrDefault(c => c.Id == request.CropId);
            if (crop is null) return "unknown_crop";
            if (plot.CropId is not null) return "plot_occupied";
            if (economy.Player.Inventory.GetValueOrDefault(crop.SeedResourceId) < 1) return "insufficient_seed";
            if (plot.Generation == long.MaxValue) return "numeric_overflow";
            return "accepted";
        }
        if (request.CropId is not null) return "unexpected_crop";
        if (request.Action is not ("water" or "harvest")) return "invalid_action";
        if (plot.CropId is null) return "empty_plot";
        if (request.Action == "water") return plot.Mature ? "already_mature" : "accepted";
        if (!plot.Mature) return "not_mature";
        var definition = content.Data.Farming!.Crops.Single(c => c.Id == plot.CropId);
        long quantity = (long)economy.Player.Inventory.GetValueOrDefault(definition.HarvestResourceId) + definition.HarvestQuantity;
        if (quantity > int.MaxValue || plot.HarvestedCount == int.MaxValue) return "numeric_overflow";
        var inventory = new Dictionary<string, int>(economy.Player.Inventory) { [definition.HarvestResourceId] = (int)quantity };
        return TownContent.RequiredSlots(inventory, content.Data.Resources) > economy.Player.SlotCapacity ? "inventory_full" : "accepted";
    }

    private static void ApplyFarm(TownContent content, EconomyState economy, FarmingState farming, FarmRequest request)
    {
        var plot = farming.Plots.Single(p => p.Id == request.PlotId);
        if (request.Action == "plant")
        {
            var crop = content.Data.Farming!.Crops.Single(c => c.Id == request.CropId);
            int remaining = checked(economy.Player.Inventory[crop.SeedResourceId] - 1);
            if (remaining == 0) economy.Player.Inventory.Remove(crop.SeedResourceId); else economy.Player.Inventory[crop.SeedResourceId] = remaining;
            plot.CropId = crop.Id; plot.GrowthDays = 0; plot.Watered = false; plot.Mature = false; plot.Generation++;
        }
        else if (request.Action == "water") plot.Watered = true;
        else
        {
            var crop = content.Data.Farming!.Crops.Single(c => c.Id == plot.CropId);
            economy.Player.Inventory[crop.HarvestResourceId] = checked(economy.Player.Inventory.GetValueOrDefault(crop.HarvestResourceId) + crop.HarvestQuantity);
            plot.CropId = null; plot.GrowthDays = 0; plot.Mature = false; plot.Watered = false; plot.HarvestedCount++;
        }
    }

    public SleepResult Sleep(SleepRequest request, double realSeconds = 0, bool paused = false, Action<SleepOperationRecord>? persistBeforeCommit = null, Action<long>? settlementGuard = null)
    {
        using var trace = TraceInput("sleep", new { request, realSeconds, paused });
        EnsureEconomyNotCommitting();
        ArgumentNullException.ThrowIfNull(request);
        RequireCommandText(request.OperationId, request.ActorId);
        if (!TownContent.Finite(realSeconds) || realSeconds is < 0 or > 86400) throw new ArgumentOutOfRangeException(nameof(realSeconds));
        var original = _state;
        var candidate = TownJson.Clone(original);
        SleepResult result;
        if (candidate.Farming.SleepReceipts.TryGetValue(request.OperationId, out var prior))
            result = prior.Request == request ? prior.Result with { IsDuplicate = true } : new(request.OperationId, false, "operation_id_conflict", original.WorldMinute, original.WorldMinute);
        else if (candidate.Economy.Receipts.ContainsKey(request.OperationId) || candidate.Farming.Receipts.ContainsKey(request.OperationId) || candidate.Production.Receipts.ContainsKey(request.OperationId))
            result = new(request.OperationId, false, "operation_id_conflict", original.WorldMinute, original.WorldMinute);
        else
        {
            string code = !ValidOperationId(request.OperationId) ? "invalid_operation_id" : request.ActorId != "player" ? "invalid_actor"
                : request.Hours is < 1 or > 12 ? "invalid_sleep_hours" : paused ? "paused" : !CanSleep() ? "bed_out_of_reach"
                : original.AcceptedWorldMinutes + request.Hours * 60 > MaximumAdvanceMinutes
                    || original.WorldMinute + original.AcceptedWorldMinutes + request.Hours * 60 >= 1440 * 1_000_000 ? "advance_limit" : "accepted";
            if (code == "accepted")
            {
                _state = candidate;
                try { AdvanceCore(request.Hours * 60, realSeconds, false, settlementGuard); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or OverflowException)
                {
                    candidate = TownJson.Clone(original);
                    code = "advance_failed";
                }
                finally { _state = original; }
            }
            result = new(request.OperationId, code == "accepted", code, original.WorldMinute, candidate.WorldMinute);
        }
        long sequence = checked(++candidate.Economy.InputSequence);
        if (ValidOperationId(request.OperationId) && !candidate.Farming.SleepReceipts.ContainsKey(request.OperationId)
            && !candidate.Economy.Receipts.ContainsKey(request.OperationId) && !candidate.Farming.Receipts.ContainsKey(request.OperationId) && !candidate.Production.Receipts.ContainsKey(request.OperationId))
            candidate.Farming.SleepReceipts.Add(request.OperationId, new(sequence, request, result));
        AddCandidateEvent(candidate, "sleep_command", result.Code, new { schemaVersion = 1, inputSequence = sequence, request, result, realSeconds, paused });
        var record = new SleepOperationRecord(1, sequence, candidate.BranchId, candidate.WorldMinute, request, result, TownJson.Clone(candidate));
        PublishFarming(candidate, () => persistBeforeCommit?.Invoke(record));
        return result;
    }

    private void SettleMorning(long day, Action<long>? settlementGuard)
    {
        if (day != _state.Farming.LastSettledDay + 1) throw new InvalidDataException("Morning settlement must advance exactly one day.");
        if (settlementGuard is not null)
        {
            _economyCommitPending = true;
            try { settlementGuard(day); }
            finally { _economyCommitPending = false; }
        }
        long sequence = checked(++_state.Economy.InputSequence);
        foreach (var restock in _content.Data.Farming!.Restocks)
        {
            var shop = _state.Economy.Shops.Single(s => s.Id == restock.ShopId);
            var replacement = _content.GetRestockInventory(shop.Id, day);
            Log("shop_restock_source", null, "morning", "inventory_replaced", data: new { inputSequence = sequence, day, shopId = shop.Id, previous = shop.Inventory, replacement, coins = shop.Coins });
        }
        foreach (var plot in _state.Farming.Plots.Where(p => p.CropId is not null))
        {
            var crop = _content.Data.Farming!.Crops.Single(c => c.Id == plot.CropId);
            int nextGrowth = plot.GrowthDays + (plot.Watered && !plot.Mature ? 1 : 0);
            Log("crop_growth", null, "morning", plot.Mature ? "already_mature" : plot.Watered ? "watered_growth" : "needs_water",
                data: new { inputSequence = sequence, day, plot.Id, plot.CropId, plot.Generation, plot.GrowthDays, plot.Watered,
                    nextGrowthDays = nextGrowth, mature = nextGrowth == crop.GrowthDays,
                    unclaimedQuantity = nextGrowth == crop.GrowthDays ? crop.HarvestQuantity : 0, crop.HarvestResourceId });
        }
        ApplyMorning(_content, _state.Economy, _state.Farming, day);
        foreach (var chicken in _state.Production.Chickens.Where(c => c.Fed))
        {
            var definition = _content.Data.Production!.Chickens.Single(c => c.Id == chicken.Id);
            Log("chicken_product_ready", null, "morning", "production_created", data: new { inputSequence = sequence, day,
                chicken.Id, chicken.Generation, resourceId = definition.ProductResourceId, quantity = definition.ProductQuantity });
        }
        ApplyChickenMorning(_state.Production);
        _state.Farming.Mornings.Add(new(sequence, day));
        Log("morning_settled", null, "morning", "committed", data: new { inputSequence = sequence, day });
    }

    private static void ApplyMorning(TownContent content, EconomyState economy, FarmingState farming, long day)
    {
        foreach (var plot in farming.Plots)
        {
            if (plot.CropId is not null && !plot.Mature && plot.Watered)
            {
                var crop = content.Data.Farming!.Crops.Single(c => c.Id == plot.CropId);
                plot.GrowthDays++;
                plot.Mature = plot.GrowthDays >= crop.GrowthDays;
            }
            plot.Watered = false;
        }
        foreach (var restock in content.Data.Farming!.Restocks)
            economy.Shops.Single(s => s.Id == restock.ShopId).Inventory = content.GetRestockInventory(restock.ShopId, day);
        farming.LastSettledDay = day;
    }

    private static void AddCandidateEvent(TownState candidate, string kind, string code, object data)
        => candidate.Events.Add(new(checked(++candidate.Sequence), candidate.BranchId, candidate.WorldMinute, kind, "player", "player", code, null, null, TownJson.Serialize(data)));

    private void PublishFarming(TownState candidate, Action persist)
    {
        _economyCommitPending = true;
        try { persist(); _state = candidate; }
        finally { _economyCommitPending = false; }
    }
}
