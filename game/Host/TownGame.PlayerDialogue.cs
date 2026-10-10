using System.Diagnostics;
using CozyTown.Core.Town;
using CozyTown.Model;

namespace CozyTown.Game;

public partial class TownGame
{
    private TownPlayerDialogueRegistry? _dialogueRetirements;
    private TownDialogueHost DialogueHost => new(_world, _dialogueRetirements, input => _journal.Append("player_dialogue_terminal_input", input));

    private void GeneratePlayerDialogue()
    {
        if (_fatal) return;
        DialogueHost.Generate(_pauseReasons);
        FlushWorldEvents();
        RefreshPlayerDialogue();
    }

    private void ProcessPlayerDialogue()
    {
        if (!TownDialogueHost.TextAllowed(_pauseReasons) || _fatal) return;
        DialogueHost.ReconcileRestored();
        var request = _world.GetPlayerDialogueRequest();
        if (request is not null)
        {
            string key = _world.SessionId + ":" + request.LogicalRequestId;
            if (!_activeModelRequests.Contains(key))
            {
                if (_mode != "real" || _model is null)
                {
                    string fixture = Argument("--dialogue-fixture=") ?? "success";
                    if (fixture == "delayed")
                    {
                        string attempt = "fixed-" + request.LogicalRequestId;
                        if (DialogueHost.Acknowledge(request.PlayerSessionId, request.LogicalRequestId, attempt))
                        {
                            _activeModelRequests.Add(key);
                            _ = SendDelayedFixedDialogueAsync(request, key, _world.SessionId, attempt);
                        }
                    }
                    else if (fixture != "timeout")
                        DialogueHost.CompleteFixed(request, fixture == "invalid" ? "{\"action\":\"trade\"}" : TownPlayerDialogueProtocol.FixedResponse(request.ResidentId),
                            fixture == "budget" ? "total_limit" : null);
                }
                else
                {
                    // The exhausted first-slice profile has no player-dialogue stage. Full-demo evaluation binds that stage separately.
                    DialogueHost.CompleteFixed(request, "{}", "player_dialogue_profile_unavailable");
                }
            }
        }
        DialogueHost.Present(_pauseReasons);
        FlushWorldEvents();
    }

    private async Task SendDelayedFixedDialogueAsync(PlayerDialogueRequest request, string key, string session, string attempt)
    {
        var clock = Stopwatch.StartNew();
        await Task.Delay(2000);
        double arrivedAfter = 60 - request.RemainingWaitSeconds + clock.Elapsed.TotalSeconds;
        string json = TownPlayerDialogueProtocol.FixedResponse(request.ResidentId);
        _mainThread.Enqueue(() =>
        {
            _activeModelRequests.Remove(key);
            _world.MarkPlayerDialogueResponseArrived(session, request.PlayerSessionId, request.LogicalRequestId, attempt, arrivedAfter);
            _journal.Append("fixed_player_dialogue_completion", new { session, request, attempt, json, arrivedAfter });
            DialogueHost.Complete(request, session, attempt, json, "fixed_model", null, arrivedAfter);
        });
    }

    private async Task SendPlayerDialogueAsync(DeepSeekClient client, PlayerDialogueRequest request, FrozenModelRequest frozen, string key)
    {
        var clock = Stopwatch.StartNew();
        _admissionClocks[key] = clock;
        try
        {
            ModelCompletion result = await client.SendAsync(frozen);
            double arrivedAfter = 60 - frozen.RemainingWait.TotalSeconds + (result.LogicalElapsedToArrival ?? clock.Elapsed).TotalSeconds;
            _mainThread.Enqueue(() =>
            {
                _activeModelRequests.Remove(key);
                _modelCompletions.Add(result);
                if (result.DispatchedAtUtc is null && _world.SessionId == frozen.Identity.SessionId)
                    _world.MarkPlayerDialogueDispatched(request.PlayerSessionId, request.LogicalRequestId, result.Dispatch.AttemptId);
                DialogueHost.Complete(request, frozen.Identity.SessionId, result.Dispatch.AttemptId,
                    result.CandidateJson, "real_model", result.FailureCode, arrivedAfter);
            });
        }
        catch (Exception error)
        {
            string code = error is ModelException modelError ? modelError.Code : "model_unavailable";
            _mainThread.Enqueue(() =>
            {
                _activeModelRequests.Remove(key);
                if (_world.SessionId != frozen.Identity.SessionId) return;
                if (code is "concurrency_limit" or "budget_concurrency_limit") return;
                string attempt = "not-sent-" + request.LogicalRequestId;
                if (_world.MarkPlayerDialogueDispatched(request.PlayerSessionId, request.LogicalRequestId, attempt))
                    DialogueHost.Complete(request, frozen.Identity.SessionId, attempt, null, "rule_fallback", code, 60 - request.RemainingWaitSeconds);
                _journal.Append("player_dialogue_dispatch_rejected", new { request.PlayerSessionId, request.LogicalRequestId, request.ResidentId, code });
            });
        }
        finally { _admissionClocks.TryRemove(key, out _); }
    }

    private void RefreshPlayerDialogue()
    {
        if (_dialogue is null) return;
        var current = DialogueHost.Current;
        _dialogue.Visible = current is not null;
        if (current is null) return;
        _dialogueName.Text = _loaded.Definitions.Residents.Single(r => r.Id == current.ResidentId).DisplayName;
        _dialoguePortrait.Texture = Portrait(current.ResidentId);
        var display = current.Displays.Last();
        _dialogueText.Text = display.Text;
        _generateDialogue.Text = _mode == "real" ? "生成一段反馈" : "生成一段 · 固定示例";
        _generateDialogue.Disabled = current.Request is not null || !TownDialogueHost.TextAllowed(_pauseReasons) || _fatal;
        bool waiting = current.Request is not null && current.TerminalReason is null;
        _dialogueStatus.Text = waiting
            ? !TownDialogueHost.TextAllowed(_pauseReasons) ? "其他暂停生效中 · 回复会封存，恢复后继续显示。"
                : $"{(current.SealedResult is not null ? "回复已封存" : current.Request!.AttemptId is null ? "排队等待共用模型槽位" : "等待这一段回复")} · 剩余 {current.Request!.RemainingWaitSeconds:0} 秒"
            : current.TerminalReason?.StartsWith("persisted_", StringComparison.Ordinal) == true ? "这次生成已在先前分支终结，保留存档中的固定回应。"
            : display.Error is not null ? "规则回退 · " + TownDialogueHost.FailureText(display.Error)
            : display.Source == "fixed_dialogue" ? "固定回应 · 可选择生成一段反馈。"
            : display.Source == "fixed_model" ? "固定客户端示例 · 本次交谈已生成一段。" : "模型反馈 · 本次交谈已生成一段。";
    }
}
