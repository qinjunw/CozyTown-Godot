using System.Diagnostics.CodeAnalysis;

namespace CozyTown.Core.Town;

public sealed partial class TownWorld
{
    private static void ValidateProductionStructure(TownContent content, ProductionState? production, double worldMinute)
    {
        void Require([DoesNotReturnIf(false)] bool condition, string field)
        {
            if (!condition) throw new InvalidDataException($"Invalid production snapshot: {field}");
        }
        Require(production is not null && production.Chickens is not null && production.Receipts is not null, "required fields");
        Require(production.FishingRandomVersion == FishingRandomVersion && production.FishingRandomState != 0 && production.FishingDrawCount >= 0, "fishing random version/state/count");
        Require(production.Chickens.All(c => c is not null) && production.Chickens.Select(c => c.Id).SequenceEqual(content.Data.Production!.Chickens.Select(c => c.Id)), "chicken bindings/order");
        foreach (var chicken in production.Chickens)
            Require(!(chicken.Fed && chicken.ProductReady) && chicken.Generation >= 0 && chicken.CollectedCount >= 0
                && chicken.CollectedCount <= chicken.Generation
                && chicken.Generation - chicken.CollectedCount == (chicken.Fed || chicken.ProductReady ? 1 : 0), $"chicken {chicken.Id} cycle");
        foreach (var (id, receipt) in production.Receipts)
        {
            Require(ValidOperationId(id) && receipt is not null && receipt.Request is not null && receipt.Result is not null, "receipt fields");
            var request = receipt.Request;
            var result = receipt.Result;
            Require(new[] { request.OperationId, request.ActorId, request.TargetId, request.Action, request.DefinitionId ?? "" }
                .All(v => v is not null && v.Length <= 200 && !v.Any(char.IsControl)), "request text");
            Require(request.OperationId == id && result.OperationId == id && !result.IsDuplicate && ProductionReceiptCodes.Contains(result.Code)
                && result.Succeeded == (result.Code is "accepted" or "no_catch") && TownContent.Finite(receipt.WorldMinute)
                && receipt.WorldMinute >= content.Data.StartMinute && receipt.WorldMinute <= worldMinute
                && receipt.RandomStateBefore != 0 && receipt.RandomStateAfter != 0, "receipt result/time/random");
            Require(result.Roll is null || result.Roll is >= 0 and < 100, "receipt roll");
            if (result.Succeeded && result.Code == "accepted")
                Require(result.ResourceId is not null && content.Data.Resources.Any(r => r.Id == result.ResourceId) && result.Quantity > 0, "receipt output");
            else Require(result.ResourceId is null && result.Quantity == 0, "empty/rejected output");
            if (result.Code == "no_catch") Require(request.Action == "fish" && result.Roll is not null, "empty catch");
        }
    }

    private static readonly HashSet<string> ProductionReceiptCodes =
    ["accepted", "no_catch", "invalid_actor", "unknown_production_target", "production_out_of_reach", "unexpected_definition",
        "target_action_mismatch", "product_pending", "already_fed", "insufficient_feed", "numeric_overflow", "product_not_ready",
        "inventory_full", "unknown_recipe", "insufficient_ingredients", "invalid_production_action", "production_failed"];
}
