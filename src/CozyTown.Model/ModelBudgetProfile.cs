using System.Collections.ObjectModel;

namespace CozyTown.Model;

// Profiles fix ledger identities and limits; creating a ledger requires explicit initialization.
public sealed class ModelBudgetProfile
{
    public const string FirstSliceVersion = "first-slice-v1";
    public const string FullDemoVersion = "full-demo-v1";
    public const string FullDemoV2Version = "full-demo-v2";
    public const string ManualPlayVersion = "manual-play-v1";
    public static ModelBudgetProfile FirstSlice { get; } = new(FirstSliceVersion,
        "cozytown-godot-first-slice-20260920-v1", 1, 48,
        new() { ["probe"] = 4, ["baseline"] = 22, ["counterfactual"] = 22 });
    public static ModelBudgetProfile FullDemo { get; } = new(FullDemoVersion,
        "cozytown-godot-full-demo-20261010-v1", 2, 96,
        new() { ["baseline"] = 40, ["counterfactual"] = 40, ["player_dialogue"] = 8, ["manual"] = 8 });
    public static ModelBudgetProfile FullDemoV2 { get; } = new(FullDemoV2Version,
        "cozytown-godot-full-demo-20261011-v2", 2, 96,
        new() { ["baseline"] = 40, ["counterfactual"] = 40, ["player_dialogue"] = 8, ["manual"] = 8 });
    public static ModelBudgetProfile ManualPlay { get; } = new(ManualPlayVersion,
        "cozytown-godot-manual-play-20261017-v1", 2, 96,
        new() { ["manual"] = 96 });

    private ModelBudgetProfile(string version, string budgetId, int schemaVersion, int totalLimit,
        Dictionary<string, int> stageLimits)
    {
        Version = version;
        BudgetId = budgetId;
        SchemaVersion = schemaVersion;
        TotalLimit = totalLimit;
        StageLimits = new ReadOnlyDictionary<string, int>(stageLimits);
    }

    public string Version { get; }
    public string BudgetId { get; }
    public long LimitMicros => 5_000_000;
    public long ReserveMicros => 2_110_000;
    public int TotalLimit { get; }
    public int ConcurrentLimit => 2;
    public IReadOnlyDictionary<string, int> StageLimits { get; }
    internal int SchemaVersion { get; }

    public static ModelBudgetProfile Get(string version) => version switch
    {
        FirstSliceVersion => FirstSlice,
        FullDemoVersion => FullDemo,
        FullDemoV2Version => FullDemoV2,
        ManualPlayVersion => ManualPlay,
        _ => throw new ModelException("budget_profile_not_registered")
    };

    public static string StageName(ModelStage stage) => stage switch
    {
        ModelStage.Baseline => "baseline", ModelStage.Counterfactual => "counterfactual",
        ModelStage.PlayerDialogue => "player_dialogue", ModelStage.Manual => "manual",
        _ => throw new ModelException("stage_invalid")
    };
}
