using System.Text.Json.Serialization;

namespace CozyTown.Core.Town;

public sealed record CropDefinition(string Id, string DisplayName, string SeedResourceId, string HarvestResourceId, int GrowthDays, int HarvestQuantity);
public sealed record FarmPlotDefinition(string Id, string DisplayName, string PlaceId);
public sealed record ShopRestockEntry(string ResourceId, int ProbabilityPermille, int MinQuantity, int MaxQuantity);
public sealed record ShopRestockDefinition(string ShopId, int WorldSeed, int MinimumDistinctItems, ShopRestockEntry[] Entries);
public sealed record FarmingDefinition(CropDefinition[] Crops, FarmPlotDefinition[] Plots, string? BedPlaceId, ShopRestockDefinition[] Restocks);
public sealed record FarmRequest(string OperationId, string ActorId, string PlotId, string Action, string? CropId = null);
public sealed record FarmResult(string OperationId, bool Succeeded, string Code, bool IsDuplicate = false);
public sealed record SleepRequest(string OperationId, string ActorId, int Hours);
public sealed record SleepResult(string OperationId, bool Succeeded, string Code, double FromMinute, double ToMinute, bool IsDuplicate = false);
public sealed record FarmReceipt(long InputSequence, double WorldMinute, FarmRequest Request, FarmResult Result);
public sealed record SleepReceipt(long InputSequence, SleepRequest Request, SleepResult Result);
public sealed record MorningReceipt(long InputSequence, long Day);
public sealed record FarmOperationRecord(int SchemaVersion, long InputSequence, string BranchId, double WorldMinute, FarmRequest Request, FarmResult Result, TownState Checkpoint);
public sealed record SleepOperationRecord(int SchemaVersion, long InputSequence, string BranchId, double WorldMinute, SleepRequest Request, SleepResult Result, TownState Checkpoint);

public sealed class FarmPlotState
{
    [JsonRequired] public string Id { get; set; } = "";
    [JsonRequired] public string? CropId { get; set; }
    [JsonRequired] public int GrowthDays { get; set; }
    [JsonRequired] public bool Watered { get; set; }
    [JsonRequired] public bool Mature { get; set; }
    [JsonRequired] public long Generation { get; set; }
    [JsonRequired] public int HarvestedCount { get; set; }
}

public sealed class FarmingState
{
    [JsonRequired] public List<FarmPlotState> Plots { get; set; } = [];
    [JsonRequired] public long LastSettledDay { get; set; }
    [JsonRequired] public Dictionary<string, FarmReceipt> Receipts { get; set; } = [];
    [JsonRequired] public Dictionary<string, SleepReceipt> SleepReceipts { get; set; } = [];
    [JsonRequired] public List<MorningReceipt> Mornings { get; set; } = [];
}
