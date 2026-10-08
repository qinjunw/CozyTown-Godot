using System.Text.Json.Serialization;

namespace CozyTown.Core.Town;

public sealed record ChickenDefinition(string Id, string DisplayName, string PlaceId, string FeedResourceId, string ProductResourceId, int ProductQuantity);
public sealed record FishingSpotDefinition(string Id, string DisplayName, string PlaceId);
public sealed record FishDefinition(string Id, string ResourceId, int MinRollInclusive, int MaxRollExclusive);
public sealed record KitchenDefinition(string Id, string DisplayName, string PlaceId);
public sealed record RecipeIngredientDefinition(string ResourceId, int Quantity);
public sealed record RecipeDefinition(string Id, string DisplayName, RecipeIngredientDefinition[] Ingredients, string OutputResourceId, int OutputQuantity);
public sealed record ProductionDefinition(ChickenDefinition[] Chickens, FishingSpotDefinition[] FishingSpots, FishDefinition[] Fish,
    KitchenDefinition[] Kitchens, RecipeDefinition[] Recipes, uint FishingSeed);
public sealed record ProductionRequest(string OperationId, string ActorId, string TargetId, string Action, string? DefinitionId = null);
public sealed record ProductionResult(string OperationId, bool Succeeded, string Code, string? ResourceId = null, int Quantity = 0, int? Roll = null, bool IsDuplicate = false);
public sealed record ProductionReceipt(long InputSequence, double WorldMinute, ProductionRequest Request, ProductionResult Result,
    uint RandomStateBefore, uint RandomStateAfter);
public sealed record ProductionOperationRecord(int SchemaVersion, long InputSequence, string BranchId, double WorldMinute, ProductionRequest Request,
    ProductionResult Result, uint RandomStateBefore, uint RandomStateAfter, TownState Checkpoint);

public sealed class ChickenState
{
    [JsonRequired] public string Id { get; set; } = "";
    [JsonRequired] public bool Fed { get; set; }
    [JsonRequired] public bool ProductReady { get; set; }
    [JsonRequired] public long Generation { get; set; }
    [JsonRequired] public long CollectedCount { get; set; }
}

public sealed class ProductionState
{
    [JsonRequired] public List<ChickenState> Chickens { get; set; } = [];
    [JsonRequired] public string FishingRandomVersion { get; set; } = "";
    [JsonRequired] public uint FishingRandomState { get; set; }
    [JsonRequired] public long FishingDrawCount { get; set; }
    [JsonRequired] public Dictionary<string, ProductionReceipt> Receipts { get; set; } = [];
}
