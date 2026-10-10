using System.Text.Json.Serialization;

namespace CozyTown.Core.Town;

public sealed record PlayerDialogueRequest(string PlayerSessionId, string LogicalRequestId, string ResidentId,
    int ActivityRevision, double RemainingWaitSeconds, string InputJson, string InputFingerprint, bool IsRecovery, string? AttemptId);
public sealed record PlayerDialogueResult(string SessionId, string PlayerSessionId, string LogicalRequestId,
    string AttemptId, string ResidentId, string? Text, string Source, string? Error, double ReceivedAfterSeconds = 0);
internal sealed record PlayerDialogueResident(string Id, string DisplayName, string Personality, string Activity,
    string DesiredResourceId, Dictionary<string, int> Inventory, int Coins);
internal sealed record PlayerDialogueContext(string Protocol, string RequestKind, string PlayerSessionId,
    PlayerDialogueResident Resident, LocalObservation LocalObservation, ResidentMemory[] Memories,
    ScheduleDefinition Schedule, double WorldMinute, string Rules);

public sealed class PlayerDialogueDisplay
{
    [JsonRequired] public string Id { get; set; } = "";
    [JsonRequired] public string Text { get; set; } = "";
    [JsonRequired] public string Source { get; set; } = "";
    [JsonRequired] public string? Error { get; set; }
    [JsonRequired] public bool MemoryCommitted { get; set; }
    [JsonRequired] public long? MemorySequence { get; set; }
}

public sealed class PlayerDialogueState
{
    [JsonRequired] public string Id { get; set; } = "";
    [JsonRequired] public string ResidentId { get; set; } = "";
    [JsonRequired] public bool IsOpen { get; set; }
    [JsonRequired] public string FixedText { get; set; } = "";
    [JsonRequired] public PlayerDialogueRequest? Request { get; set; }
    [JsonRequired] public double WaitedSeconds { get; set; }
    [JsonRequired] public double? ResponseArrivalSeconds { get; set; }
    [JsonRequired] public PlayerDialogueResult? SealedResult { get; set; }
    [JsonRequired] public List<PlayerDialogueDisplay> Displays { get; set; } = [];
    [JsonRequired] public string? TerminalReason { get; set; }
}
