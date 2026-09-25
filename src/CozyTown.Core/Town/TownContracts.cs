using System.Text.Json;
using System.Text.Json.Serialization;

namespace CozyTown.Core.Town;

public readonly record struct TownPoint([property: JsonRequired] double X, [property: JsonRequired] double Y)
{
    public double DistanceTo(TownPoint other) => Math.Sqrt(Math.Pow(X - other.X, 2) + Math.Pow(Y - other.Y, 2));
}

public readonly record struct TownRect([property: JsonRequired] double X, [property: JsonRequired] double Y, [property: JsonRequired] double Width, [property: JsonRequired] double Height)
{
    public bool Contains(TownPoint p, double padding = 0) => p.X >= X - padding && p.Y >= Y - padding && p.X <= X + Width + padding && p.Y <= Y + Height + padding;
}

public sealed record PlaceDefinition(string Id, string DisplayName, TownPoint Position, string RegionId);
public sealed record ObstacleDefinition(string Id, TownRect Bounds);
public sealed record ObservationRegionDefinition(string Id, TownRect Bounds);
public sealed record RoadDefinition(string Id, string FromPlaceId, string ToPlaceId, TownPoint[] Points);
public sealed record TownManifest(double Width, double Height, PlaceDefinition[] Places, ObstacleDefinition[] Obstacles, ObservationRegionDefinition[] Regions, RoadDefinition[]? Roads = null);
public sealed record ResourceDefinition(string Id, string DisplayName, int StackLimit = 99, int DisplayOrder = 0);
public sealed record ScheduleDefinition(string Id, int StartMinute, int EndMinute, string PlaceId, string Activity, bool IsFree);
public sealed record ResidentDefinition(string Id, string DisplayName, string HomePlaceId, string SpawnPlaceId, string DesiredResourceId, Dictionary<string, int> Inventory, int Coins, ScheduleDefinition[] Schedule, string Personality = "");
public sealed record TownDefinitions(ResidentDefinition[] Residents, ResourceDefinition[] Resources, int StartMinute = 540, EconomyDefinition? Economy = null, FarmingDefinition? Farming = null);
public sealed record VisibleResident(string Id, string DisplayName, TownPoint Position, string Activity, string DesiredResourceId, string[] OfferedResourceIds);
public sealed record LocalObservation(string ResidentId, double WorldMinute, TownPoint Position, string RegionId, VisibleResident[] Residents, PlaceDefinition[] Places);
public sealed record DecisionRequest(string LogicalRequestId, string ResidentId, int ActivityRevision, double WindowEndMinute, double RemainingWaitSeconds, string InputJson, string InputFingerprint, string[] AllowedActions, string[] AllowedTargets, bool IsRecovery, string? AttemptId);
public sealed record ActionCandidate(string Action, string? TargetId, string? PlaceId, string? ResourceId, int Quantity, int Price, bool? Accept, string Text);
public sealed record DecisionResult(string SessionId, string LogicalRequestId, string AttemptId, string ResidentId, ActionCandidate? Candidate, string Source, string? Error, double ReceivedAfterSeconds = 0);
public sealed record TownEvent(long Sequence, string BranchId, double WorldMinute, string Kind, string? ResidentId, string Source, string Reason, string? LogicalRequestId, string? ActivityId, string DataJson);
public sealed record ResidentMemory(long Sequence, double WorldMinute, string Source, string Kind, string Text);

public sealed class ResidentState
{
    [JsonRequired] public string Id { get; set; } = "";
    [JsonRequired] public TownPoint Position { get; set; }
    [JsonRequired] public TownPoint Facing { get; set; }
    [JsonRequired] public string RegionId { get; set; } = "";
    [JsonRequired] public Dictionary<string, int> Inventory { get; set; } = [];
    [JsonRequired] public int Coins { get; set; }
    [JsonRequired] public string ScheduleId { get; set; } = "";
    [JsonRequired] public long ScheduleDay { get; set; }
    [JsonRequired] public string Activity { get; set; } = "";
    [JsonRequired] public int ActivityRevision { get; set; }
    [JsonRequired] public string? DestinationPlaceId { get; set; }
    [JsonRequired] public List<TownPoint> Route { get; set; } = [];
    [JsonRequired] public int RouteCursor { get; set; }
    [JsonRequired] public string? BlockedReason { get; set; }
    [JsonRequired] public string? CommitmentId { get; set; }
    [JsonRequired] public string? PendingRequestId { get; set; }
    [JsonRequired] public double CooldownSeconds { get; set; }
    [JsonRequired] public string LastOpportunityFingerprint { get; set; } = "";
    [JsonRequired] public List<ResidentMemory> Memories { get; set; } = [];
}

public sealed class CommitmentState
{
    [JsonRequired] public string Id { get; set; } = "";
    [JsonRequired] public string InitiatorId { get; set; } = "";
    [JsonRequired] public string InviteeId { get; set; } = "";
    [JsonRequired] public string PlaceId { get; set; } = "";
    [JsonRequired] public double EndsAtMinute { get; set; }
    [JsonRequired] public string Stage { get; set; } = "offered";
    [JsonRequired] public string TurnResidentId { get; set; } = "";
    [JsonRequired] public int TurnNumber { get; set; }
    [JsonRequired] public HashSet<string> ArrivedResidentIds { get; set; } = [];
    [JsonRequired] public string? OfferResourceId { get; set; }
    [JsonRequired] public int OfferQuantity { get; set; }
    [JsonRequired] public int OfferPrice { get; set; }
    [JsonRequired] public string? OfferFromResidentId { get; set; }
    [JsonRequired] public string? TransferId { get; set; }
    [JsonRequired] public List<string> Dialogue { get; set; } = [];
}

public sealed class PendingDecisionState
{
    [JsonRequired] public DecisionRequest Request { get; set; } = null!;
    [JsonRequired] public LocalObservation Observation { get; set; } = null!;
    [JsonRequired] public double WaitedSeconds { get; set; }
    [JsonRequired] public double? ResponseArrivalSeconds { get; set; }
    [JsonRequired] public DecisionResult? SealedResult { get; set; }
}

public sealed class TownState
{
    [JsonRequired] public string BranchId { get; set; } = "";
    [JsonRequired] public string? ParentBranchId { get; set; }
    [JsonRequired] public string? SourceSnapshotId { get; set; }
    [JsonRequired] public long ForkSequence { get; set; }
    [JsonRequired] public string BudgetId { get; set; } = "";
    [JsonRequired] public string InitialStateId { get; set; } = "";
    [JsonRequired] public string InitialStateFingerprint { get; set; } = "";
    [JsonRequired] public double WorldMinute { get; set; }
    [JsonRequired] public double AcceptedWorldMinutes { get; set; }
    [JsonRequired] public TownPoint PlayerPosition { get; set; }
    [JsonRequired] public TownPoint PlayerFacing { get; set; }
    [JsonRequired] public EconomyState Economy { get; set; } = null!;
    [JsonRequired] public FarmingState Farming { get; set; } = null!;
    [JsonRequired] public long Sequence { get; set; }
    [JsonRequired] public long IdentitySequence { get; set; }
    [JsonRequired] public int DecisionCursor { get; set; }
    [JsonRequired] public List<ResidentState> Residents { get; set; } = [];
    [JsonRequired] public List<CommitmentState> Commitments { get; set; } = [];
    [JsonRequired] public List<PendingDecisionState> PendingDecisions { get; set; } = [];
    [JsonRequired] public HashSet<string> TerminalRequestIds { get; set; } = [];
    [JsonRequired] public HashSet<string> ExecutedTransferIds { get; set; } = [];
    [JsonRequired] public List<TownEvent> Events { get; set; } = [];
}

public sealed record TownSnapshot(int SchemaVersion, string SnapshotId, string ContentFingerprint, string RulesVersion, string NumericVersion, TownState State);

public static class TownJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        CheckProperties(document.RootElement);
        return JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("JSON root cannot be null.");
    }

    public static T Clone<T>(T value) => Deserialize<T>(Serialize(value));

    private static void CheckProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException($"Duplicate JSON field: {property.Name}");
                CheckProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckProperties(child);
    }
}
