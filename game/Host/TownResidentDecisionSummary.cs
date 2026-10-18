using System.Text.Json;
using CozyTown.Core.Town;
using CozyTown.Model;

namespace CozyTown.Game;

public static class TownResidentDecisionSummary
{
    public static string Format(ResidentState resident, string branchId, string sessionId,
        IReadOnlyList<TownEvent> events, IEnumerable<ModelCompletion> completions)
    {
        var decisions = events.Where(e => e.BranchId == branchId && e.ResidentId == resident.Id
            && e.LogicalRequestId is not null && e.Kind is "decision_created" or "decision_dispatched" or "response_arrived"
                or "response_received" or "candidate_executed" or "candidate_rejected" or "rule_fallback" or "decision_terminal")
            .OrderBy(e => e.Sequence).ToArray();
        var matchingCompletions = completions.Where(c => c.Dispatch.Request.Identity is var identity
            && identity.Kind == "npc_action" && identity.ResidentId == resident.Id
            && identity.SessionId == sessionId && identity.RunId == branchId)
            .OrderBy(c => c.ArrivedAtUtc ?? c.Dispatch.ReservedAtUtc).ToArray();
        string? requestId = resident.PendingRequestId ?? decisions.LastOrDefault()?.LogicalRequestId
            ?? matchingCompletions.LastOrDefault()?.Dispatch.Request.Identity.LogicalRequestId;
        string activity = $"当前活动：{Identifier(resident.Activity)}";
        if (requestId is null) return activity + "\n最近决定：暂无记录";
        var related = decisions.Where(e => e.LogicalRequestId == requestId).ToArray();
        var decision = related.LastOrDefault(e => e.Kind is "candidate_executed" or "candidate_rejected");
        var received = related.LastOrDefault(e => e.Kind == "response_received");
        var completion = matchingCompletions.LastOrDefault(c => c.Dispatch.Request.Identity.LogicalRequestId == requestId);
        var candidate = ReadCandidate(decision?.DataJson) ?? ReadCandidate(received?.DataJson)
            ?? (completion?.FailureCode is null ? ReadCandidate(completion?.CandidateJson, false) : null);
        string source = (decision?.Source ?? received?.Source ?? (completion is null ? "host" : "real_model")) switch
        {
            "real_model" => decision is null && (completion?.FailureCode is not null || received is { Reason: not "complete" })
                ? "真实模型失败" : "真实模型",
            "rule_fallback" => "规则回退", "fixed_model" => "固定替身",
            "host" => "等待模型", "world" or "rules" => "世界规则", _ => "其他来源"
        };
        string outcome = decision?.Kind == "candidate_executed"
            ? "已执行 " + CandidateAction(candidate) + (candidate?.Action switch
            {
                "invite" => "（等待对方回应）", "propose_exchange" => "（等待对方接受报价）", _ => ""
            })
            : decision is null ? resident.PendingRequestId == requestId ? "尚未执行" : "记录不足"
                : "已拒绝" + RejectionReason(decision.Reason);
        string fallback = source != "规则回退" && related.Any(e => e.Kind == "rule_fallback") ? "\n回退：规则回退" : "";
        return activity + $"\n最近决定 {ShortRequest(requestId)}\n来源：{source}；候选：{CandidateText(candidate)}\n世界：{outcome}" + fallback;
    }

    private static ActionCandidate? ReadCandidate(string? json, bool envelope = true)
    {
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!envelope) return TownModelProtocol.Parse(root.GetRawText());
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object) root = result;
            return root.TryGetProperty("candidate", out var candidate) && candidate.ValueKind == JsonValueKind.Object
                ? TownModelProtocol.Parse(candidate.GetRawText()) : null;
        }
        catch (JsonException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    private static string CandidateAction(ActionCandidate? candidate) => candidate?.Action is
        "wait" or "approach" or "invite" or "respond" or "speak" or "propose_exchange" or "end" ? candidate.Action : "不可展示";

    private static string CandidateText(ActionCandidate? candidate)
    {
        string action = CandidateAction(candidate);
        if (candidate is null || action == "不可展示") return "不可展示";
        string target = candidate.Action == "wait" ? "" : " → " + Identifier(candidate.TargetId);
        string detail = candidate.Action switch
        {
            "invite" => " @" + Identifier(candidate.PlaceId),
            "respond" => candidate.Accept is true ? " 接受" : candidate.Accept is false ? " 拒绝" : "",
            "propose_exchange" => $" {Identifier(candidate.ResourceId)} ×{candidate.Quantity} / 总价{candidate.Price}",
            _ => ""
        };
        return action + target + detail;
    }

    private static string ShortRequest(string value) => Identifier(string.Join(':', value.Split(':').TakeLast(2)));

    private static string RejectionReason(string reason) => reason is
        "activity_changed" or "window_ended" or "schedule_busy" or "logical_timeout" or "action_not_allowed"
        or "invalid_text" or "invalid_amount" or "unexpected_action_fields" or "target_not_allowed" or "unknown_action"
        or "target_not_visible" or "target_unreachable" or "place_not_observed" or "target_schedule_busy"
        or "participant_busy" or "insufficient_window" or "not_response_turn" or "missing_accept" or "no_pending_offer"
        or "participants_not_arrived" or "insufficient_assets" or "duplicate_transfer" or "asset_limit"
        or "meeting_unreachable" or "not_conversation_turn" or "no_commitment" or "invitation_received"
        or "schedule_boundary" or "declined" or "resident_ended" or "conversation_complete"
        ? "（" + reason + "）" : "";

    private static string Identifier(string? value) => !string.IsNullOrEmpty(value)
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':')
        ? value[..Math.Min(32, value.Length)] : "—";
}
