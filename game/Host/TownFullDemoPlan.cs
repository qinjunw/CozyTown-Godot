using CozyTown.Core.Town;

namespace CozyTown.Game;

public sealed record TownFullDemoCommand(string Kind, string? Target = null, string? Resource = null, int Quantity = 0);

public sealed record TownFullDemoPlan(string Version, double StepSeconds, double WindowSeconds,
    int OrdinaryNpcLimit, double DrainSeconds, double SaveAtSeconds, double WalkTimeoutSeconds,
    TownFullDemoCommand[] Commands)
{
    public static TownFullDemoPlan Read(string json)
    {
        var plan = TownJson.Deserialize<TownFullDemoPlan>(json);
        plan.Validate();
        return plan;
    }

    public void Validate()
    {
        if (Version != "full-demo-input-v1" || StepSeconds != .05 || WindowSeconds != 180
            || OrdinaryNpcLimit != 18 || DrainSeconds != 60 || SaveAtSeconds != 90 || WalkTimeoutSeconds != 60
            || Commands is null || !Commands.SequenceEqual(RequiredCommands))
            throw new InvalidDataException("Full-demo input differs from the registered two-day plan.");
    }

    private static readonly TownFullDemoCommand[] RequiredCommands =
    [
        new("walk", "shop"), new("buy", "shop.town.general", "feed.chicken", 2), new("buy", "shop.town.general", "seed.carrot", 1),
        new("walk", "farm.plot.01"), new("plant", "plot.01", "crop_definition.carrot"), new("water", "plot.01"),
        new("walk", "coop"), new("feed", "animal.hen_01"), new("window", Quantity: 1),
        new("walk", "bed.player"), new("sleep", Quantity: 12), new("sleep", Quantity: 12),
        new("walk", "coop"), new("collect", "animal.hen_01"), new("feed", "animal.hen_01"), new("window", Quantity: 2),
        new("player_dialogue", "mina"), new("player_dialogue", "eli"), new("player_dialogue", "ren"), new("player_dialogue", "sora"), new("finish")
    ];
}

public sealed class TownFullDemoWindow(TownFullDemoPlan plan, int day)
{
    public int Day { get; } = day;
    public double ElapsedSeconds { get; private set; }
    public int OrdinaryAdmissions { get; private set; }
    public string? StopReason { get; private set; }
    public bool SaveDue => Day == 2 && (ElapsedSeconds >= plan.SaveAtSeconds || StopReason is not null);
    public bool TryAdmit(bool recovery)
    {
        if (recovery) return true;
        if (StopReason is not null) return false;
        OrdinaryAdmissions++;
        if (OrdinaryAdmissions == plan.OrdinaryNpcLimit) Stop("ordinary_limit");
        return true;
    }
    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new InvalidDataException("Window elapsed seconds must be finite and nonnegative.");
        if (StopReason is not null) return;
        ElapsedSeconds = Math.Min(plan.WindowSeconds, ElapsedSeconds + seconds);
        if (ElapsedSeconds >= plan.WindowSeconds) Stop("duration_limit");
    }
    public void Stop(string reason) => StopReason ??= reason;
    public static bool StopsDispatch(string? code) => code is "money_limit" or "total_limit" or "stage_limit" or "ledger_stopped"
        or "authentication_failed" or "account_unavailable" or "account_currency_unverified" or "account_check_rejected"
        or "model_not_registered" or "fingerprint_not_registered" or "ledger_update_failed" or "ledger_read_failed"
        or "request_journal_failed" or "completion_journal_failed";
    public bool RejectAdmission(string? code)
    {
        if (!StopsDispatch(code)) return false;
        Stop(code!);
        return true;
    }
}
