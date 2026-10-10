namespace CozyTown.Core.Town;

public sealed partial class TownWorld
{
    public bool CanOpenPlayerDialogue(string residentId)
    {
        var resident = _state.Residents.SingleOrDefault(r => r.Id == residentId);
        return resident is not null && resident.Position.DistanceTo(_state.PlayerPosition) <= 42 && HasSight(resident.Position, _state.PlayerPosition);
    }

    public PlayerDialogueState? OpenPlayerDialogue(string residentId)
    {
        using var trace = TraceInput("player_dialogue_open", new { residentId });
        EnsureEconomyNotCommitting();
        var resident = _state.Residents.SingleOrDefault(r => r.Id == residentId);
        if (resident is null || resident.Position.DistanceTo(_state.PlayerPosition) > 42 || !HasSight(resident.Position, _state.PlayerPosition))
        {
            Log("player_dialogue_open_rejected", residentId, "player", "resident_not_nearby");
            return null;
        }
        if (_state.CurrentPlayerDialogueId is string previous) ClosePlayerDialogue(previous);
        var dialogue = new PlayerDialogueState { Id = NewIdentity("player-dialogue"), ResidentId = residentId, IsOpen = true, FixedText = FixedPlayerDialogue(resident) };
        _state.PlayerDialogues.Add(dialogue);
        _state.CurrentPlayerDialogueId = dialogue.Id;
        Log("player_dialogue_opened", residentId, "player", "nearby", activityId: dialogue.Id);
        AddPlayerDisplay(dialogue, dialogue.FixedText, "fixed_dialogue", null);
        return TownJson.Clone(dialogue);
    }

    public PlayerDialogueRequest? RequestPlayerDialogue(string playerSessionId)
    {
        using var trace = TraceInput("player_dialogue_request", new { playerSessionId });
        EnsureEconomyNotCommitting();
        var dialogue = CurrentPlayerDialogue(playerSessionId);
        if (dialogue is null || dialogue.Request is not null)
        {
            Log("player_dialogue_request_rejected", dialogue?.ResidentId, "player", dialogue is null ? "retired_player_session" : "one_generation_per_session", activityId: playerSessionId);
            return null;
        }
        var resident = FindResident(dialogue.ResidentId);
        string logical = NewIdentity("player-text");
        var definition = _content.ResidentData(resident.Id);
        string input = TownJson.Serialize(new PlayerDialogueContext("town-player-text-v1", "player_text", playerSessionId,
            new(resident.Id, definition.DisplayName, definition.Personality, resident.Activity, definition.DesiredResourceId, resident.Inventory, resident.Coins),
            Observe(resident.Id), resident.Memories.TakeLast(6).ToArray(), CurrentSchedule(resident), _state.WorldMinute,
            "Reply to the nearby player in one brief Chinese paragraph using only this resident's known facts. Speech does not execute any action or transfer assets. Return exactly one JSON string field named text, at most 160 characters."));
        dialogue.Request = new(playerSessionId, logical, resident.Id, resident.ActivityRevision, 60, input, TownContent.Hash(input), false, null);
        _state.ModelQueue.Add(logical);
        Log("player_dialogue_requested", resident.Id, "player", "explicit_generation", logical, playerSessionId, new { request = dialogue.Request });
        return TownJson.Clone(dialogue.Request);
    }

    public PlayerDialogueRequest? GetPlayerDialogueRequest()
    {
        var dialogue = _state.CurrentPlayerDialogueId is string id ? CurrentPlayerDialogue(id) : null;
        return dialogue?.Request is { AttemptId: null } request && dialogue.TerminalReason is null && dialogue.SealedResult is null
            ? TownJson.Clone(request) : null;
    }

    public bool MarkPlayerDialogueDispatched(string playerSessionId, string logicalRequestId, string attemptId)
    {
        using var trace = TraceInput("player_dialogue_dispatch", new { playerSessionId, logicalRequestId, attemptId });
        EnsureEconomyNotCommitting();
        var dialogue = CurrentPlayerDialogue(playerSessionId);
        if (dialogue?.Request is not { } request || request.LogicalRequestId != logicalRequestId || request.AttemptId is not null
            || dialogue.TerminalReason is not null || dialogue.SealedResult is not null || string.IsNullOrWhiteSpace(attemptId)) return false;
        dialogue.Request = request with { AttemptId = attemptId };
        Log("player_dialogue_dispatched", dialogue.ResidentId, "host", "attempt_registered", logicalRequestId, playerSessionId, new { attemptId, request.IsRecovery });
        return true;
    }

    public bool MarkPlayerDialogueResponseArrived(string sessionId, string playerSessionId, string logicalRequestId, string attemptId, double receivedAfterSeconds)
    {
        using var trace = TraceInput("player_dialogue_arrival", new { sessionId, playerSessionId, logicalRequestId, attemptId, receivedAfterSeconds });
        EnsureEconomyNotCommitting();
        var dialogue = CurrentPlayerDialogue(playerSessionId);
        string? reason = PlayerResultIdentityError(sessionId, dialogue, logicalRequestId, attemptId)
            ?? (!TownContent.Finite(receivedAfterSeconds) || receivedAfterSeconds < 0 || receivedAfterSeconds >= 60 ? "arrival_outside_deadline" : null)
            ?? (dialogue!.SealedResult is { Error: not "logical_timeout" } ? "response_already_sealed" : null)
            ?? (dialogue.ResponseArrivalSeconds is double arrival && Math.Abs(arrival - receivedAfterSeconds) > .000001 ? "arrival_time_conflict" : null);
        if (reason is not null) { Log("player_dialogue_arrival_rejected", dialogue?.ResidentId, "host", reason, logicalRequestId, playerSessionId); return false; }
        if (dialogue!.ResponseArrivalSeconds is not null) return true;
        dialogue.SealedResult = null;
        dialogue.ResponseArrivalSeconds = receivedAfterSeconds;
        dialogue.WaitedSeconds = receivedAfterSeconds;
        dialogue.Request = dialogue.Request! with { RemainingWaitSeconds = 60 - receivedAfterSeconds };
        Log("player_dialogue_arrived", dialogue.ResidentId, "host", "awaiting_durable_completion", logicalRequestId, playerSessionId, new { attemptId, receivedAfterSeconds });
        return true;
    }

    public bool ReceivePlayerDialogueResult(PlayerDialogueResult result)
    {
        using var trace = TraceInput("player_dialogue_result", new { result });
        EnsureEconomyNotCommitting();
        var dialogue = CurrentPlayerDialogue(result.PlayerSessionId);
        string? reason = PlayerResultIdentityError(result.SessionId, dialogue, result.LogicalRequestId, result.AttemptId)
            ?? (dialogue!.ResidentId != result.ResidentId ? "wrong_resident" : null)
            ?? (dialogue.SealedResult is not null ? "duplicate_result" : null)
            ?? (dialogue.ResponseArrivalSeconds is double arrival && Math.Abs(arrival - result.ReceivedAfterSeconds) > .000001 ? "arrival_time_conflict" : null);
        if (reason is not null) { Log("player_dialogue_response_rejected", result.ResidentId, result.Source, reason, result.LogicalRequestId, result.PlayerSessionId); return false; }
        if (!TownContent.Finite(result.ReceivedAfterSeconds) || result.ReceivedAfterSeconds < 0) throw new ArgumentOutOfRangeException(nameof(result));
        dialogue!.SealedResult = TownJson.Clone(result);
        dialogue.ResponseArrivalSeconds = null;
        Log("player_dialogue_response_received", result.ResidentId, result.Source, result.Error ?? "complete", result.LogicalRequestId, result.PlayerSessionId, new { result });
        return true;
    }

    public bool DisplayPlayerDialogue(string playerSessionId, bool allowed)
    {
        using var trace = TraceInput("player_dialogue_display", new { playerSessionId, allowed });
        EnsureEconomyNotCommitting();
        var dialogue = CurrentPlayerDialogue(playerSessionId);
        if (!allowed || dialogue?.SealedResult is not { } result || dialogue.TerminalReason is not null) return false;
        string? error = result.Error ?? (result.ReceivedAfterSeconds >= 60 ? "logical_timeout" : !ValidPlayerText(result.Text) ? "player_text_schema_invalid" : null);
        string text = error is null ? result.Text! : dialogue.FixedText;
        string source = error is null ? result.Source : "rule_fallback";
        Log("player_dialogue_text_validated", dialogue.ResidentId, source, error ?? "accepted", result.LogicalRequestId, playerSessionId);
        AddPlayerDisplay(dialogue, text, source, error);
        dialogue.TerminalReason = error ?? "displayed";
        _state.TerminalRequestIds.Add(result.LogicalRequestId);
        _state.ModelQueue.Remove(result.LogicalRequestId);
        return true;
    }

    public bool ClosePlayerDialogue(string playerSessionId)
    {
        using var trace = TraceInput("player_dialogue_close", new { playerSessionId });
        EnsureEconomyNotCommitting();
        var dialogue = CurrentPlayerDialogue(playerSessionId);
        if (dialogue is null) return false;
        dialogue.IsOpen = false;
        _state.CurrentPlayerDialogueId = null;
        dialogue.TerminalReason ??= "closed_before_display";
        dialogue.ResponseArrivalSeconds = null;
        if (dialogue.Request is { } request)
        {
            _state.TerminalRequestIds.Add(request.LogicalRequestId);
            _state.ModelQueue.Remove(request.LogicalRequestId);
        }
        Log("player_dialogue_closed", dialogue.ResidentId, "player", dialogue.TerminalReason, dialogue.Request?.LogicalRequestId, dialogue.Id);
        return true;
    }

    public bool ApplyPlayerDialogueRetirement(string playerSessionId, string reason)
    {
        using var trace = TraceInput("player_dialogue_retire", new { playerSessionId, reason });
        EnsureEconomyNotCommitting();
        var dialogue = CurrentPlayerDialogue(playerSessionId);
        if (dialogue?.Request is not { } request || dialogue.TerminalReason is not null) return false;
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 160 || reason.Any(char.IsControl)) throw new ArgumentException("Persisted dialogue retirement reason is invalid.", nameof(reason));
        dialogue.TerminalReason = "persisted_" + reason;
        dialogue.SealedResult = null;
        dialogue.ResponseArrivalSeconds = null;
        _state.TerminalRequestIds.Add(request.LogicalRequestId);
        _state.ModelQueue.Remove(request.LogicalRequestId);
        Log("player_dialogue_retired", dialogue.ResidentId, "host", dialogue.TerminalReason, request.LogicalRequestId, dialogue.Id);
        return true;
    }

    private PlayerDialogueState? CurrentPlayerDialogue(string id) => _state.CurrentPlayerDialogueId == id
        ? _state.PlayerDialogues.SingleOrDefault(d => d.Id == id && d.IsOpen) : null;
    private string? PlayerResultIdentityError(string session, PlayerDialogueState? dialogue, string logical, string attempt) => session != SessionId ? "retired_session"
        : _state.TerminalRequestIds.Contains(logical) ? "terminal_request"
        : dialogue?.Request is not { } request ? "retired_player_session"
        : request.LogicalRequestId != logical ? "wrong_logical_request"
        : request.AttemptId != attempt ? "wrong_attempt" : null;
    private string FixedPlayerDialogue(ResidentState resident)
    {
        var definition = _content.ResidentData(resident.Id);
        return IsFree(resident) ? $"你好！我正在留意{_content.Data.Resources.Single(r => r.Id == definition.DesiredResourceId).DisplayName}，也想和附近的朋友聊聊。"
            : $"你好，我现在要去{_content.Place(CurrentSchedule(resident).PlaceId).DisplayName}忙一会儿。";
    }
    private static bool ValidPlayerText(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 160 && !text.Any(char.IsControl);
    private void AddPlayerDisplay(PlayerDialogueState dialogue, string text, string source, string? error)
    {
        var display = new PlayerDialogueDisplay { Id = NewIdentity("player-display"), Text = text, Source = source, Error = error };
        dialogue.Displays.Add(display);
        Log("player_dialogue_displayed", dialogue.ResidentId, source, error ?? "displayed", dialogue.Request?.LogicalRequestId, dialogue.Id, new { display });
    }
    private void AdvancePlayerDialogueWait(double realSeconds)
    {
        foreach (var dialogue in _state.PlayerDialogues.Where(d => d.IsOpen && d.Request is not null && d.TerminalReason is null && d.SealedResult is null && d.ResponseArrivalSeconds is null))
        {
            dialogue.WaitedSeconds += realSeconds;
            dialogue.Request = dialogue.Request! with { RemainingWaitSeconds = Math.Max(0, 60 - dialogue.WaitedSeconds) };
            if (dialogue.Request.RemainingWaitSeconds <= 0)
            {
                dialogue.SealedResult = new(SessionId, dialogue.Id, dialogue.Request.LogicalRequestId, dialogue.Request.AttemptId ?? "", dialogue.ResidentId, null, "rule_fallback", "logical_timeout", dialogue.WaitedSeconds);
                Log("player_dialogue_timeout", dialogue.ResidentId, "rule_fallback", "logical_timeout", dialogue.Request.LogicalRequestId, dialogue.Id);
            }
        }
    }
    private void CommitPlayerDialogueMemories()
    {
        foreach (var dialogue in _state.PlayerDialogues)
        foreach (var display in dialogue.Displays.Where(d => !d.MemoryCommitted))
        {
            display.MemoryCommitted = true;
            display.MemorySequence = _state.Sequence + 1;
            AddMemory(FindResident(dialogue.ResidentId), display.Source, "player_conversation", display.Text);
            Log("player_dialogue_memory_committed", dialogue.ResidentId, display.Source, "displayed_once", dialogue.Request?.LogicalRequestId, dialogue.Id, new { display.Id, display.Text });
        }
    }
}
