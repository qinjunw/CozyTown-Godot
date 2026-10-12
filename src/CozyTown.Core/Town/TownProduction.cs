namespace CozyTown.Core.Town;

public sealed partial class TownWorld
{
    public const string FishingRandomVersion = "xorshift32-mod100-v1";

    private static ProductionState CreateInitialProduction(TownContent content) => new()
    {
        Chickens = content.Data.Production!.Chickens.Select(c => new ChickenState { Id = c.Id }).ToList(),
        FishingRandomVersion = FishingRandomVersion, FishingRandomState = content.Data.Production.FishingSeed
    };

    public bool CanInteractWithProductionTarget(string targetId)
    {
        var definition = _content.Data.Production!;
        string? place = definition.Chickens.SingleOrDefault(c => c.Id == targetId)?.PlaceId
            ?? definition.FishingSpots.SingleOrDefault(s => s.Id == targetId)?.PlaceId
            ?? definition.Kitchens.SingleOrDefault(k => k.Id == targetId)?.PlaceId;
        return place is not null && CanReachFarmingPlace(place);
    }

    // Mutation guards inject failures into isolated candidate changes; persistence runs only after the complete result exists.
    public ProductionResult Produce(ProductionRequest request, Action<ProductionOperationRecord>? persistBeforeCommit = null,
        Action<string>? mutationGuard = null)
    {
        using var trace = TraceInput("produce", new { request });
        EnsureEconomyNotCommitting();
        ArgumentNullException.ThrowIfNull(request);
        RequireCommandText(request.OperationId, request.ActorId, request.TargetId, request.Action);
        if (request.DefinitionId is not null) RequireCommandText(request.DefinitionId);
        var candidate = TownJson.Clone(_state);
        uint before = candidate.Production.FishingRandomState;
        long sequence = checked(++candidate.Economy.InputSequence);
        ProductionResult result;
        if (candidate.Production.Receipts.TryGetValue(request.OperationId, out var prior))
            result = prior.Request == request ? prior.Result with { IsDuplicate = true }
                : new(request.OperationId, false, "operation_id_conflict");
        else if (candidate.Economy.Receipts.ContainsKey(request.OperationId) || candidate.Farming.Receipts.ContainsKey(request.OperationId)
            || candidate.Farming.SleepReceipts.ContainsKey(request.OperationId)) result = new(request.OperationId, false, "operation_id_conflict");
        else
        {
            result = !ValidOperationId(request.OperationId) ? new(request.OperationId, false, "invalid_operation_id")
                : request.ActorId != "player" ? new(request.OperationId, false, "invalid_actor")
                : !ProductionTargetExists(_content, request.TargetId) ? new(request.OperationId, false, "unknown_production_target")
                : !CanInteractWithProductionTarget(request.TargetId) ? new(request.OperationId, false, "production_out_of_reach")
                : EvaluateProduction(_content, candidate.Economy, candidate.Production, request);
            if (result.Succeeded)
            {
                try
                {
                    ApplyProduction(_content, candidate.Economy, candidate.Production, request, result, stage =>
                    {
                        if (mutationGuard is null) return;
                        _economyCommitPending = true;
                        try { mutationGuard(stage); }
                        finally { _economyCommitPending = false; }
                    });
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or OverflowException)
                {
                    candidate = TownJson.Clone(_state);
                    candidate.Economy.InputSequence = sequence;
                    result = new(request.OperationId, false, "production_failed", Roll: result.Roll);
                }
            }
            if (ValidOperationId(request.OperationId)) candidate.Production.Receipts.Add(request.OperationId,
                new(sequence, candidate.WorldMinute, request, result, before, candidate.Production.FishingRandomState));
        }
        AddCandidateEvent(candidate, "production_command", result.Code, new { schemaVersion = 1, inputSequence = sequence, request, result,
            randomVersion = FishingRandomVersion, randomStateBefore = before, randomStateAfter = candidate.Production.FishingRandomState });
        if (result.Succeeded && !result.IsDuplicate) RecordProductionSources(candidate, sequence, request, result);
        var record = new ProductionOperationRecord(1, sequence, candidate.BranchId, candidate.WorldMinute, request, result, before,
            candidate.Production.FishingRandomState, TownJson.Clone(candidate));
        PublishFarming(candidate, () => persistBeforeCommit?.Invoke(record));
        return result;
    }

    private static bool ProductionTargetExists(TownContent content, string id) => content.Data.Production!.Chickens.Any(c => c.Id == id)
        || content.Data.Production.FishingSpots.Any(s => s.Id == id) || content.Data.Production.Kitchens.Any(k => k.Id == id);

    private static uint NextFishingRandom(uint value)
    {
        value ^= value << 13; value ^= value >> 17; value ^= value << 5;
        return value;
    }

    private static ProductionResult EvaluateProduction(TownContent content, EconomyState economy, ProductionState production, ProductionRequest request)
    {
        ProductionResult Reject(string code, int? roll = null) => new(request.OperationId, false, code, Roll: roll);
        var definition = content.Data.Production!;
        if (request.Action != "cook" && request.DefinitionId is not null) return Reject("unexpected_definition");
        if (request.Action is "feed" or "collect")
        {
            var animal = definition.Chickens.SingleOrDefault(c => c.Id == request.TargetId);
            if (animal is null) return Reject("target_action_mismatch");
            var chicken = production.Chickens.Single(c => c.Id == animal.Id);
            if (request.Action == "feed")
            {
                if (chicken.ProductReady) return Reject("product_pending");
                if (chicken.Fed) return Reject("already_fed");
                if (economy.Player.Inventory.GetValueOrDefault(animal.FeedResourceId) < 1) return Reject("insufficient_feed");
                if (chicken.Generation == long.MaxValue) return Reject("numeric_overflow");
                return new(request.OperationId, true, "accepted", animal.FeedResourceId, 1);
            }
            if (!chicken.ProductReady) return Reject("product_not_ready");
            if (chicken.CollectedCount == long.MaxValue) return Reject("numeric_overflow");
            string code = CanAddProducedItem(content, economy.Player.Inventory, economy.Player.SlotCapacity, animal.ProductResourceId, animal.ProductQuantity);
            return code == "accepted" ? new(request.OperationId, true, code, animal.ProductResourceId, animal.ProductQuantity) : Reject(code);
        }
        if (request.Action == "fish")
        {
            if (!definition.FishingSpots.Any(s => s.Id == request.TargetId)) return Reject("target_action_mismatch");
            if (production.FishingDrawCount == long.MaxValue) return Reject("numeric_overflow");
            int roll = (int)(NextFishingRandom(production.FishingRandomState) % 100);
            var fish = definition.Fish.SingleOrDefault(f => roll >= f.MinRollInclusive && roll < f.MaxRollExclusive);
            if (fish is null) return new(request.OperationId, true, "no_catch", Roll: roll);
            string code = CanAddProducedItem(content, economy.Player.Inventory, economy.Player.SlotCapacity, fish.ResourceId, 1);
            return code == "accepted" ? new(request.OperationId, true, code, fish.ResourceId, 1, roll) : Reject(code, roll);
        }
        if (request.Action == "cook")
        {
            if (!definition.Kitchens.Any(k => k.Id == request.TargetId)) return Reject("target_action_mismatch");
            var recipe = definition.Recipes.SingleOrDefault(r => r.Id == request.DefinitionId);
            if (recipe is null) return Reject("unknown_recipe");
            if (recipe.Ingredients.Any(i => economy.Player.Inventory.GetValueOrDefault(i.ResourceId) < i.Quantity)) return Reject("insufficient_ingredients");
            var inventory = new Dictionary<string, int>(economy.Player.Inventory);
            foreach (var ingredient in recipe.Ingredients) RemoveProductionMaterial(inventory, ingredient.ResourceId, ingredient.Quantity);
            string code = CanAddProducedItem(content, inventory, economy.Player.SlotCapacity, recipe.OutputResourceId, recipe.OutputQuantity);
            return code == "accepted" ? new(request.OperationId, true, code, recipe.OutputResourceId, recipe.OutputQuantity) : Reject(code);
        }
        return Reject("invalid_production_action");
    }

    private static string CanAddProducedItem(TownContent content, Dictionary<string, int> inventory, int capacity, string resourceId, int quantity)
    {
        long amount = (long)inventory.GetValueOrDefault(resourceId) + quantity;
        if (amount > int.MaxValue) return "numeric_overflow";
        var proposed = new Dictionary<string, int>(inventory) { [resourceId] = (int)amount };
        return TownContent.RequiredSlots(proposed, content.Data.Resources) > capacity ? "inventory_full" : "accepted";
    }

    private static void ApplyProduction(TownContent content, EconomyState economy, ProductionState production, ProductionRequest request,
        ProductionResult result, Action<string>? mutationGuard = null)
    {
        var definition = content.Data.Production!;
        var inventory = economy.Player.Inventory;
        if (request.Action == "feed")
        {
            var chicken = production.Chickens.Single(c => c.Id == request.TargetId);
            RemoveProductionMaterial(inventory, result.ResourceId!, 1);
            mutationGuard?.Invoke("after_consume:" + result.ResourceId);
            chicken.Generation = checked(chicken.Generation + 1); chicken.Fed = true;
        }
        else if (request.Action == "collect")
        {
            mutationGuard?.Invoke("before_output:" + result.ResourceId);
            AddProducedItem(inventory, result.ResourceId!, result.Quantity);
            var chicken = production.Chickens.Single(c => c.Id == request.TargetId);
            chicken.ProductReady = false; chicken.CollectedCount = checked(chicken.CollectedCount + 1);
        }
        else if (request.Action == "fish")
        {
            mutationGuard?.Invoke("before_output:" + (result.ResourceId ?? "no_catch"));
            if (result.ResourceId is not null) AddProducedItem(inventory, result.ResourceId, result.Quantity);
            production.FishingRandomState = NextFishingRandom(production.FishingRandomState);
            production.FishingDrawCount = checked(production.FishingDrawCount + 1);
        }
        else
        {
            var recipe = definition.Recipes.Single(r => r.Id == request.DefinitionId);
            foreach (var ingredient in recipe.Ingredients)
            {
                RemoveProductionMaterial(inventory, ingredient.ResourceId, ingredient.Quantity);
                mutationGuard?.Invoke("after_consume:" + ingredient.ResourceId);
            }
            mutationGuard?.Invoke("before_output:" + result.ResourceId);
            AddProducedItem(inventory, result.ResourceId!, result.Quantity);
        }
    }

    private static void RemoveProductionMaterial(Dictionary<string, int> inventory, string resourceId, int quantity)
    {
        int amount = checked(inventory.GetValueOrDefault(resourceId) - quantity);
        if (amount < 0) throw new InvalidDataException("Production material quantity became negative.");
        if (amount == 0) inventory.Remove(resourceId); else inventory[resourceId] = amount;
    }
    private static void AddProducedItem(Dictionary<string, int> inventory, string resourceId, int quantity)
        => inventory[resourceId] = checked(inventory.GetValueOrDefault(resourceId) + quantity);

    private void RecordProductionSources(TownState candidate, long sequence, ProductionRequest request, ProductionResult result)
    {
        var consumed = new Dictionary<string, int>();
        var created = new Dictionary<string, int>();
        var claimed = new Dictionary<string, int>();
        if (request.Action == "feed") consumed[result.ResourceId!] = 1;
        else if (request.Action == "cook")
        {
            foreach (var ingredient in _content.Data.Production!.Recipes.Single(r => r.Id == request.DefinitionId).Ingredients)
                consumed[ingredient.ResourceId] = ingredient.Quantity;
        }
        if (request.Action == "collect") claimed[result.ResourceId!] = result.Quantity;
        else if (request.Action != "feed" && result.ResourceId is not null) created[result.ResourceId] = result.Quantity;
        var chicken = candidate.Production.Chickens.SingleOrDefault(c => c.Id == request.TargetId);
        AddCandidateEvent(candidate, "production_source", request.Action, new { inputSequence = sequence, request.OperationId, request.TargetId,
            request.DefinitionId, consumed, created, claimed, generation = chicken?.Generation, result.Roll, candidate.Production.FishingRandomVersion,
            candidate.Production.FishingRandomState, candidate.Production.FishingDrawCount });
    }

    private static void ApplyChickenMorning(ProductionState production)
    {
        foreach (var chicken in production.Chickens)
        {
            chicken.ProductReady |= chicken.Fed;
            chicken.Fed = false;
        }
    }
}
