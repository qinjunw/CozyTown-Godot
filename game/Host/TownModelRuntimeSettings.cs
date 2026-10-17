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

