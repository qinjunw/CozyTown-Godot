using CozyTown.Core.Town;
using CozyTown.Model;

namespace CozyTown.Game;

// The Godot panel and offline tests use the same interaction, parsing and presentation boundary.
public sealed class TownDialogueHost(TownWorld world, IPlayerDialogueRetirements? retirements = null, Action<object>? recordTerminal = null)
{
    public PlayerDialogueState? Current
    {
        get
        {
            var view = world.GetView();
            return view.PlayerDialogues.SingleOrDefault(d => d.Id == view.CurrentPlayerDialogueId);
        }
    }

    public static bool TextAllowed(IReadOnlyCollection<string> pauseReasons) => pauseReasons.Count == 0
        || pauseReasons.Count == 1 && pauseReasons.Contains("dialogue");
    public bool Open(string residentId, IReadOnlyCollection<string> pauseReasons)
    {
        if (!TextAllowed(pauseReasons)) return false;
        if (!world.CanOpenPlayerDialogue(residentId)) return world.OpenPlayerDialogue(residentId) is not null;
        PersistTermination(Current, "closed");
        return world.OpenPlayerDialogue(residentId) is not null;
    }
    public PlayerDialogueRequest? Generate(IReadOnlyCollection<string> pauseReasons) => TextAllowed(pauseReasons) && Current is { } current
        ? world.RequestPlayerDialogue(current.Id) : null;
    public bool Present(IReadOnlyCollection<string> pauseReasons)
    {
        if (!TextAllowed(pauseReasons) || Current is not { SealedResult: not null, TerminalReason: null } current) return false;
        PersistTermination(current, "displayed");
        return world.DisplayPlayerDialogue(current.Id, true);
    }
    public bool Close()
    {
        if (Current is not { } current) return false;
        PersistTermination(current, "closed");
        return world.ClosePlayerDialogue(current.Id);
    }
    public void ReconcileRestored()
    {
        if (Current is { Request: not null, TerminalReason: null } current
            && retirements?.Find(world.GetView().BudgetId, current.Request.LogicalRequestId) is string reason)
            world.ApplyPlayerDialogueRetirement(current.Id, reason);
    }
    private void PersistTermination(PlayerDialogueState? current, string reason)
    {
        if (current is null) return;
        recordTerminal?.Invoke(new { world.GetView().BudgetId, playerSessionId = current.Id, current.ResidentId, current.Request, current.SealedResult, reason });
        if (current is { Request: not null, TerminalReason: null })
            retirements?.Retire(world.GetView().BudgetId, current.Request.LogicalRequestId, reason);
    }
    public string? PlayerSessionFor(string logicalRequestId) => world.GetView().PlayerDialogues
        .SingleOrDefault(d => d.Request?.LogicalRequestId == logicalRequestId)?.Id;

    public bool Acknowledge(string playerSessionId, string logicalRequestId, string attemptId)
    {
        ReconcileRestored();
        return world.MarkPlayerDialogueDispatched(playerSessionId, logicalRequestId, attemptId);
    }

    public void CompleteFixed(PlayerDialogueRequest request, string json, string? error = null)
    {
        string attempt = "fixed-" + request.LogicalRequestId;
        if (!Acknowledge(request.PlayerSessionId, request.LogicalRequestId, attempt)) return;
        Complete(request, world.SessionId, attempt, json, "fixed_model", error, 0);
    }

    public bool Complete(PlayerDialogueRequest request, string worldSessionId, string attemptId, string? json,
        string source, string? error, double arrivedAfter)
    {
        string? text = null;
        if (error is null)
        {
            try { text = TownPlayerDialogueProtocol.Parse(json ?? ""); }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException)
            { error = "player_text_schema_invalid"; }
        }
        return world.ReceivePlayerDialogueResult(new(worldSessionId, request.PlayerSessionId, request.LogicalRequestId,
            attemptId, request.ResidentId, text, source, error, arrivedAfter));
    }

    public FrozenModelRequest Freeze(PlayerDialogueRequest request, ModelStage stage, string? originalBody = null, bool originalRecorded = false) => new(
        new(world.GetView().BranchId, world.SessionId, request.LogicalRequestId, request.ResidentId, request.ActivityRevision, "player_text", request.PlayerSessionId), stage,
        originalBody ?? TownPlayerDialogueProtocol.BuildRequest(request), request.IsRecovery || originalRecorded, TimeSpan.FromSeconds(request.RemainingWaitSeconds));

    public static string FailureText(string? code) => code switch
    {
        null => "",
        "logical_timeout" or "logical_wait_expired" or "request_timeout" or "response_late" => "等待时间已用完，保留固定回应。",
        "player_text_schema_invalid" or "candidate_empty" or "candidate_not_object" => "回复格式不符合单段文本要求，保留固定回应。",
        "concurrency_limit" or "budget_concurrency_limit" => "正在等待共用模型槽位。",
        "recovery_limit" => "这次请求已经使用过恢复补发额度，保留固定回应。",
        "money_limit" or "total_limit" or "stage_limit" or "ledger_stopped" => "共用实验额度不可用，保留固定回应。",
        "player_dialogue_profile_unavailable" => "当前实验未启用玩家对话额度，保留固定回应。",
        "authentication_failed" or "account_unavailable" => "模型服务认证或账户不可用，保留固定回应。",
        _ => "模型回复未完成，保留固定回应。（" + code + "）"
    };
}
