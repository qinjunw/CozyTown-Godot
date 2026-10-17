using CozyTown.Core.Town;
using CozyTown.Model;

namespace CozyTown.Game;

public sealed record TownModelRuntimeSettings(string CredentialFile, string LedgerFile,
    string ExpectedSystemFingerprint = "aeb56401ca74e127821c4f9126dcb669",
    string BudgetProfileVersion = ModelBudgetProfile.FirstSliceVersion)
{
    public DeepSeekOptions ToOptions()
    {
        if (string.IsNullOrWhiteSpace(CredentialFile) || string.IsNullOrWhiteSpace(LedgerFile)
            || !Path.IsPathFullyQualified(CredentialFile) || !Path.IsPathFullyQualified(LedgerFile)
            || string.IsNullOrWhiteSpace(ExpectedSystemFingerprint))
            throw new InvalidDataException("Local model configuration requires absolute credential and ledger paths and a provider fingerprint.");
        _ = ModelBudgetProfile.Get(BudgetProfileVersion);
        return new(CredentialFile, LedgerFile, ExpectedSystemFingerprint, BudgetProfileVersion);
    }
}

public sealed class TownModelRuntimeProfile
{
    public const string Legacy = "legacy";
    public const string Experiment = "experiment";
    public const string Manual = "manual";

    private TownModelRuntimeProfile(ModelBudgetProfile profile, string executionPurpose)
    { Profile = profile; ExecutionPurpose = executionPurpose; }

    public ModelBudgetProfile Profile { get; }
    public string ExecutionPurpose { get; }
    public string BudgetId => Profile.BudgetId;
    public bool IsFullDemo => Profile.Version is ModelBudgetProfile.FullDemoVersion or ModelBudgetProfile.FullDemoV2Version;
    public TownReplayBudgetProfile ReplayProfile => Profile.Version switch
    {
        ModelBudgetProfile.FirstSliceVersion => TownReplayBudgetProfile.FirstSlice,
        ModelBudgetProfile.FullDemoVersion => TownReplayBudgetProfile.FullDemo,
        ModelBudgetProfile.FullDemoV2Version => TownReplayBudgetProfile.FullDemoV2,
        _ => throw new InvalidDataException("Replay budget profile is not registered.")
    };
    public bool RealPlayerDialogueEnabled => IsFullDemo;
    public DeepSeekOptions ConfigureProvider(DeepSeekOptions options)
    {
        if (options.BudgetProfileVersion != Profile.Version) throw new InvalidDataException("Provider and runtime budget profiles differ.");
        return options with { ExecutionPurpose = ExecutionPurpose };
    }

    public static TownModelRuntimeProfile Resolve(string profileVersion, string executionPurpose)
    {
        ModelBudgetProfile profile;
        try { profile = ModelBudgetProfile.Get(profileVersion); }
        catch (ModelException error) { throw new InvalidDataException("Model budget profile is not registered.", error); }
        if (profile.Version == ModelBudgetProfile.FirstSliceVersion ? executionPurpose != Legacy
            : executionPurpose is not (Experiment or Manual))
            throw new InvalidDataException("Execution purpose does not belong to the selected model budget profile.");
        return new(profile, executionPurpose);
    }

    public ModelStage NewRequestStage(string scenario, string kind)
    {
        if (scenario is not ("baseline" or "counterfactual") || kind is not ("npc_action" or "player_text"))
            throw new InvalidDataException("Model request requires a registered scenario and request kind.");
        return ExecutionPurpose == Manual ? ModelStage.Manual
            : IsFullDemo && kind == "player_text"
                ? ModelStage.PlayerDialogue : scenario == "baseline" ? ModelStage.Baseline : ModelStage.Counterfactual;
    }
}

// This is an advisory host gate. The durable ledger still decides every actual admission.
public sealed class TownModelDispatchState
{
    private string? _terminalReason;
    public void ObserveFailure(string? code)
    {
        if (code is not null && code is not ("money_limit" or "stage_limit" or "total_limit") && (TownFullDemoWindow.StopsDispatch(code)
            || code.StartsWith("ledger_", StringComparison.Ordinal) && code != "ledger_busy"))
            _terminalReason ??= code;
    }
    public string? UnavailableReason(BudgetSnapshot budget, ModelBudgetProfile profile, ModelStage stage)
    {
        if (budget.StopReason is not null) return budget.StopReason;
        if (_terminalReason is not null) return _terminalReason;
        if (budget.TotalAttempts >= profile.TotalLimit) return "total_limit";
        string name = ModelBudgetProfile.StageName(stage);
        if (!profile.StageLimits.TryGetValue(name, out int limit)) return "stage_invalid";
        if (budget.Attempts.Count(a => a.Stage == name) >= limit) return "stage_limit";
        if (budget.SlotsHeld >= profile.ConcurrentLimit) return "concurrency_limit";
        if (budget.AvailableMicros < profile.ReserveMicros) return budget.SlotsHeld > 0 ? "money_wait" : "money_limit";
        return null;
    }
    public static bool Temporary(string? code) => code is "concurrency_limit" or "budget_concurrency_limit" or "money_wait" or "ledger_busy";

    public static bool CompleteNpcFallback(TownWorld world, DecisionRequest request, string reason)
    {
        string attempt = "not-sent-" + Guid.NewGuid().ToString("N");
        return world.MarkDispatched(request.LogicalRequestId, attempt)
            && world.ReceiveResult(new(world.SessionId, request.LogicalRequestId, attempt, request.ResidentId, null, "rule_fallback", reason));
    }
}
