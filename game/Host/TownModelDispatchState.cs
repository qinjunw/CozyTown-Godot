using CozyTown.Core.Town;
using CozyTown.Model;

namespace CozyTown.Game;

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
        if (budget.SlotsHeld >= profile.ConcurrentLimit)
            return budget.Attempts.Any(a => a.SlotHeld && a.Status == "unknown") ? "unknown_slots_held" : "concurrency_limit";
        if (budget.AvailableMicros < profile.ReserveMicros) return budget.SlotsHeld > 0 ? "money_wait" : "money_limit";
        return null;
    }
    public static bool Temporary(string? code) => code is "concurrency_limit" or "budget_concurrency_limit" or "unknown_slots_held" or "money_wait" or "ledger_busy";

    public static bool CompleteNpcFallback(TownWorld world, DecisionRequest request, string reason)
    {
        string attempt = "not-sent-" + Guid.NewGuid().ToString("N");
        return world.MarkDispatched(request.LogicalRequestId, attempt)
            && world.ReceiveResult(new(world.SessionId, request.LogicalRequestId, attempt, request.ResidentId, null, "rule_fallback", reason));
    }
}
