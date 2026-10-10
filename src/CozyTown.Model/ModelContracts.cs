namespace CozyTown.Model;

public enum ModelStage { Baseline, Counterfactual }

public sealed record ModelRequestIdentity(
    string RunId, string SessionId, string LogicalRequestId, string ResidentId, long ActivityRevision,
    string Kind = "npc_action", string? PlayerSessionId = null);

// InputJson is the complete, frozen Chat Completions request body, not a mutable world view.
public sealed record FrozenModelRequest(
    ModelRequestIdentity Identity, ModelStage Stage, string InputJson, bool IsRecovery, TimeSpan RemainingWait);

public sealed record ModelDispatch(
    FrozenModelRequest Request, string AttemptId, string InputFingerprint,
    DateTimeOffset ReservedAtUtc, TimeSpan Timeout, BudgetSnapshot Budget);

public sealed record ModelUsage(long PromptTokens, long CompletionTokens, long TotalTokens,
    long CacheHitTokens, long CacheMissTokens);

public sealed record ModelCompletion(
    ModelDispatch Dispatch, DateTimeOffset? DispatchedAtUtc, DateTimeOffset? ArrivedAtUtc,
    TimeSpan Elapsed, int? HttpStatus, string? ProviderRequestId, string? ReturnedModel,
    string? SystemFingerprint, string? FinishReason, string? CandidateJson, ModelUsage? Usage,
    string? FailureCode, bool GenerationEnded, bool BudgetSettled, bool SlotHeld,
    long? ConservativeCostMicros, TimeSpan? LogicalElapsedToArrival = null)
{
    public bool Succeeded => FailureCode is null;
}

// A journal must persist the request before returning. Completion writes must not mutate the world.
public interface IModelJournal
{
    Task WriteRequestAsync(ModelDispatch dispatch, CancellationToken cancellationToken);
    Task WriteCompletionAsync(ModelCompletion completion, CancellationToken cancellationToken);
}

public sealed record DeepSeekOptions(string CredentialFile, string LedgerFile,
    string ExpectedSystemFingerprint = "aeb56401ca74e127821c4f9126dcb669");

public sealed record BudgetAttempt(string AttemptId, string Stage, string LogicalRequestId,
    bool Recovery, string Status, bool SlotHeld, long ReservedCostMicros,
    long SettledCostMicros, long? PromptTokens, long? CompletionTokens, string? FailureCode);

public sealed record BudgetSnapshot(string BudgetId, string Currency, long LimitMicros,
    long SettledMicros, long ReservedMicros, int TotalAttempts, int SlotsHeld,
    string? StopReason, IReadOnlyList<BudgetAttempt> Attempts)
{
    public long AvailableMicros => LimitMicros - SettledMicros - ReservedMicros;
}

public sealed class ModelException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
