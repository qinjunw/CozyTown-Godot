using System.Text.Json.Serialization;

namespace CozyTown.Core.Town;

public sealed record PlayerDefinition(int Coins, int SlotCapacity, Dictionary<string, int> Inventory);
public sealed record ShopPriceDefinition(string ResourceId, int BuyPrice, int SellPrice);
public sealed record ShopDefinition(string Id, string DisplayName, string PlaceId, int Coins,
    Dictionary<string, int> Inventory, ShopPriceDefinition[] Prices);
public sealed record EconomyDefinition(PlayerDefinition Player, ShopDefinition[] Shops);
public sealed record InventorySlot(int Index, string? ResourceId, int Quantity);
public sealed record TradeRequest(string OperationId, string ActorId, string ShopId, string ResourceId,
    string Direction, int Quantity, int ExpectedUnitPrice);
public sealed record TradeQuote(string ShopId, string ResourceId, string Direction, int Quantity,
    int UnitPrice, long TotalPrice, bool CanTrade, string Code);
public sealed record TradeResult(string OperationId, bool Succeeded, string Code, int UnitPrice,
    long TotalPrice, bool IsDuplicate = false);
public sealed record QuickbarSelectionRequest(string OperationId, string ActorId, int Index);
public sealed record QuickbarSelectionResult(string OperationId, bool Succeeded, string Code, int Index,
    bool IsDuplicate = false);
public sealed record EconomyReceipt(long InputSequence, string Kind, TradeRequest? Trade,
    TradeResult? TradeResult, QuickbarSelectionRequest? Selection, QuickbarSelectionResult? SelectionResult);
public sealed record EconomyOperationRecord(int SchemaVersion, long InputSequence, string BranchId,
    double WorldMinute, string Kind, TradeRequest? Trade, TradeResult? TradeResult,
    QuickbarSelectionRequest? Selection, QuickbarSelectionResult? SelectionResult, EconomyState Checkpoint);

public sealed class PlayerAssetState
{
    [JsonRequired] public int Coins { get; set; }
    [JsonRequired] public int SlotCapacity { get; set; }
    [JsonRequired] public Dictionary<string, int> Inventory { get; set; } = [];
    [JsonRequired] public int SelectedQuickbarIndex { get; set; }
}

public sealed class ShopAssetState
{
    [JsonRequired] public string Id { get; set; } = "";
    [JsonRequired] public int Coins { get; set; }
    [JsonRequired] public Dictionary<string, int> Inventory { get; set; } = [];
}

public sealed class EconomyState
{
    [JsonRequired] public PlayerAssetState Player { get; set; } = null!;
    [JsonRequired] public List<ShopAssetState> Shops { get; set; } = [];
    [JsonRequired] public long InputSequence { get; set; }
    [JsonRequired] public Dictionary<string, EconomyReceipt> Receipts { get; set; } = [];
}
