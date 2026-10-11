using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace CozyTown.Core.Town;

public sealed partial class TownWorld
{
    public const string RulesVersion = "town-social-player-dialogue-v5";
    public const string NumericVersion = "pixels-double-grid16-radius5-morning-motion-prefix-v3";
    public const string DefaultBudgetId = "cozytown-godot-first-slice-20260920-v1";
    private static readonly JsonSerializerOptions DecisionInputJson = new(TownJson.Options)
        { WriteIndented = false, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
    private readonly TownContent _content;
    private readonly Func<string, TownPoint, TownPoint, bool>? _movementQuery;
    private TownState _state;
    public string SessionId { get; private set; } = Guid.NewGuid().ToString("N");
    public TownState State => TownJson.Clone(_state);
    public IReadOnlyList<TownEvent> Events => _state.Events.ToArray();
    public double WorldMinute => _state.WorldMinute;
    public bool HasUncommittedResponses => _state.PendingDecisions.Any(p => p.ResponseArrivalSeconds is not null && p.SealedResult is null)
        || _state.PlayerDialogues.Any(d => d.ResponseArrivalSeconds is not null && d.SealedResult is null);
    public TownState GetView()
    {
        var view = new TownState
        {
            BranchId = _state.BranchId, ParentBranchId = _state.ParentBranchId, SourceSnapshotId = _state.SourceSnapshotId,
            ForkSequence = _state.ForkSequence, BudgetId = _state.BudgetId, InitialStateId = _state.InitialStateId,
            InitialStateFingerprint = _state.InitialStateFingerprint, WorldMinute = _state.WorldMinute,
            AcceptedWorldMinutes = _state.AcceptedWorldMinutes, PlayerPosition = _state.PlayerPosition,
            PlayerFacing = _state.PlayerFacing, Economy = _state.Economy, Farming = _state.Farming, Production = _state.Production, Sequence = _state.Sequence, IdentitySequence = _state.IdentitySequence,
            DecisionCursor = _state.DecisionCursor, Residents = _state.Residents, Commitments = _state.Commitments.Where(IsActive).ToList(),
            PendingDecisions = _state.PendingDecisions, ModelQueue = _state.ModelQueue,
            CurrentPlayerDialogueId = _state.CurrentPlayerDialogueId, PlayerDialogues = _state.PlayerDialogues, Events = _state.Events.TakeLast(20).ToList()
        };
        return TownJson.Clone(view);
    }

    public TownWorld(TownContent content, string initialStateId = "baseline", string budgetId = DefaultBudgetId, Func<string, TownPoint, TownPoint, bool>? movementQuery = null)
    {
        _content = content;
        _movementQuery = movementQuery;
        if (string.IsNullOrWhiteSpace(initialStateId) || string.IsNullOrWhiteSpace(budgetId)) throw new ArgumentException("Initial state and budget identities are required.");
        _state = new TownState
        {
            BranchId = Guid.NewGuid().ToString("N"), BudgetId = budgetId, InitialStateId = initialStateId,
            InitialStateFingerprint = content.Fingerprint, WorldMinute = content.Data.StartMinute,
            PlayerPosition = content.Geometry.Places.FirstOrDefault(p => p.Id == "player.spawn")?.Position ?? content.Geometry.Places[0].Position,
            PlayerFacing = new TownPoint(0, 1),
            Economy = CreateInitialEconomy(content),
            Farming = CreateInitialFarming(content),
            Production = CreateInitialProduction(content),
            Residents = content.Data.Residents.Select(r => new ResidentState
            {
                Id = r.Id, Position = content.Place(r.SpawnPlaceId).Position, Facing = new TownPoint(0, 1),
                RegionId = content.Place(r.SpawnPlaceId).RegionId,
                Inventory = new Dictionary<string, int>(r.Inventory), Coins = r.Coins, ScheduleDay = -1
            }).ToList()
        };
        UpdateSchedules();
        Log("world_started", null, "world", initialStateId, data: new { contentFingerprint = content.Fingerprint, budgetId });
    }

    private TownWorld(TownContent content, TownState state, Func<string, TownPoint, TownPoint, bool>? movementQuery)
    {
        _content = content; _state = state; _movementQuery = movementQuery;
    }

    public LocalObservation Observe(string residentId)
    {
        var resident = FindResident(residentId);
        var visible = _state.Residents.Where(other => other.Id != residentId && other.RegionId == resident.RegionId && resident.Position.DistanceTo(other.Position) <= TownContent.ObservationDistance && HasSight(resident.Position, other.Position))
            .Select(other => new VisibleResident(other.Id, _content.ResidentData(other.Id).DisplayName, other.Position, other.Activity, _content.ResidentData(other.Id).DesiredResourceId,
                other.Inventory.Where(kv => kv.Value > 1).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToArray())).ToArray();
        var places = _content.Geometry.Places.Where(p => p.RegionId == resident.RegionId && p.Position.DistanceTo(resident.Position) <= TownContent.ObservationDistance && HasSight(resident.Position, p.Position)).ToArray();
        return new LocalObservation(residentId, _state.WorldMinute, resident.Position, resident.RegionId, visible, places);
    }

    public void Advance(double worldMinutes, double realSeconds, bool paused = false, Action<long>? settlementGuard = null)
    {
        using var trace = TraceInput("advance", new { worldMinutes, realSeconds, paused });
        EnsureEconomyNotCommitting();
        if (!TownContent.Finite(worldMinutes) || worldMinutes < 0 || worldMinutes > MaximumAdvanceMinutes || !TownContent.Finite(realSeconds) || realSeconds < 0 || realSeconds > 86400) throw new ArgumentOutOfRangeException(nameof(worldMinutes));
        if (!paused && (_state.AcceptedWorldMinutes + worldMinutes > MaximumAdvanceMinutes || _state.WorldMinute + _state.AcceptedWorldMinutes + worldMinutes >= 1440 * 1_000_000)) throw new ArgumentOutOfRangeException(nameof(worldMinutes));
        bool crossesMorning = !paused && _state.WorldMinute + _state.AcceptedWorldMinutes + worldMinutes >= (_state.Farming.LastSettledDay + 1) * 1440 + MorningMinute;
        var original = _state;
        if (crossesMorning) _state = TownJson.Clone(original);
        try { AdvanceCore(worldMinutes, realSeconds, paused, settlementGuard); }
        catch { if (crossesMorning) _state = original; throw; }
    }

    private void AdvanceCore(double worldMinutes, double realSeconds, bool paused, Action<long>? settlementGuard)
    {
        AdvancePlayerDialogueWait(realSeconds);
        foreach (var resident in _state.Residents) resident.CooldownSeconds = Math.Max(0, resident.CooldownSeconds - realSeconds);
        foreach (var pending in _state.PendingDecisions.Where(p => p.SealedResult is null && p.ResponseArrivalSeconds is null))
        {
            pending.WaitedSeconds += realSeconds;
            pending.Request = pending.Request with { RemainingWaitSeconds = Math.Max(0, 60 - pending.WaitedSeconds) };
            if (pending.Request.RemainingWaitSeconds <= 0)
                pending.SealedResult = new DecisionResult(SessionId, pending.Request.LogicalRequestId, pending.Request.AttemptId ?? "", pending.Request.ResidentId, null, "rule_fallback", "logical_timeout", pending.WaitedSeconds);
        }
        Log("time_input", null, "host", paused ? "paused" : "advance", data: new { worldMinutes, realSeconds, paused });
        if (paused) return;
        CommitPlayerDialogueMemories();
        UpdateSchedules();
        foreach (var commitment in _state.Commitments.Where(c => IsActive(c) && c.EndsAtMinute <= _state.WorldMinute).ToArray()) EndCommitment(commitment, "window_ended", "world");
        ConsumeMailbox();
        UpdateArrivals();
        _state.AcceptedWorldMinutes += worldMinutes;
        while (_state.AcceptedWorldMinutes > .00000001)
        {
            var morningBoundary = (_state.Farming.LastSettledDay + 1) * 1440 + MorningMinute;
            var nextBoundary = _state.Residents.Select(r => ScheduleEnd(r)).Concat(_state.Commitments.Where(IsActive).Select(c => c.EndsAtMinute)).Append(morningBoundary).Min();
            var boundaryDistance = Math.Max(0, nextBoundary - _state.WorldMinute);
            var step = Math.Min(.25, Math.Min(_state.AcceptedWorldMinutes, boundaryDistance));
            // A positive boundary remainder must be consumed even when it is smaller than the accumulation threshold.
            if (step > 0)
            {
                foreach (var resident in _state.Residents) Move(resident, step * TownContent.MovementPerMinute);
                _state.WorldMinute = step == boundaryDistance ? nextBoundary : _state.WorldMinute + step;
                _state.AcceptedWorldMinutes = Math.Max(0, _state.AcceptedWorldMinutes - step);
            }
            UpdateSchedules();
            foreach (var commitment in _state.Commitments.Where(c => IsActive(c) && c.EndsAtMinute <= _state.WorldMinute).ToArray()) EndCommitment(commitment, "window_ended", "world");
            if (_state.WorldMinute >= morningBoundary) SettleMorning(_state.Farming.LastSettledDay + 1, settlementGuard);
            ConsumeMailbox();
            UpdateArrivals();
        }
        UpdateSchedules();
        ConsumeMailbox();
        UpdateArrivals();
    }

    public bool SetPlayerPosition(TownPoint position)
    {
        using var trace = TraceInput("move", new { position });
        EnsureEconomyNotCommitting();
        if (!_content.CanMove(_state.PlayerPosition, position) || !QueryMotion("player", _state.PlayerPosition, position)) return false;
        var distance = _state.PlayerPosition.DistanceTo(position);
        if (distance > .00001) _state.PlayerFacing = new TownPoint((position.X - _state.PlayerPosition.X) / distance, (position.Y - _state.PlayerPosition.Y) / distance);
        Log("player_moved", "player", "player", "accepted", data: new { from = _state.PlayerPosition, to = position });
        _state.PlayerPosition = position; return true;
    }

    public string TalkToResident(string residentId)
    {
        using var trace = TraceInput("talk", new { residentId });
        EnsureEconomyNotCommitting();
        var resident = FindResident(residentId);
        if (resident.Position.DistanceTo(_state.PlayerPosition) > 42 || !HasSight(resident.Position, _state.PlayerPosition)) return "请走近一些再交谈。";
        var definition = _content.ResidentData(residentId);
        var text = IsFree(resident) ? $"你好！我正在留意{_content.Data.Resources.Single(r => r.Id == definition.DesiredResourceId).DisplayName}，也想和附近的朋友聊聊。" : $"你好，我现在要去{_content.Place(CurrentSchedule(resident).PlaceId).DisplayName}忙一会儿。";
        AddMemory(resident, "player", "player_conversation", text);
        Log("player_conversation", residentId, "player", "spoken", data: new { text });
        return text;
    }

    public IReadOnlyList<DecisionRequest> GetDecisionRequests()
    {
        using var trace = TraceInput("decisions", new { });
        EnsureEconomyNotCommitting();
        UpdateSchedules();
        var order = Enumerable.Range(0, _state.Residents.Count).Select(i => _state.Residents[(_state.DecisionCursor + i) % _state.Residents.Count]).ToArray();
        foreach (var resident in order)
        {
            if (resident.PendingRequestId is not null || !IsFree(resident) || resident.RouteCursor < resident.Route.Count) continue;
            var commitment = ActiveCommitment(resident);
            var responseTurn = commitment is not null && commitment.TurnResidentId == resident.Id && commitment.Stage is "offered" or "conversation" or "exchange_offered";
            if (commitment is not null && !responseTurn) continue;
            if (!responseTurn && resident.CooldownSeconds > 0) continue;
            var observation = Observe(resident.Id);
            var opportunity = OpportunityFingerprint(resident, observation, commitment);
            if (!responseTurn && resident.LastOpportunityFingerprint == opportunity) continue;
            var allowed = commitment?.Stage switch
            {
                "offered" => new[] { "respond", "wait" },
                "exchange_offered" => new[] { "respond", "end" },
                "conversation" => new[] { "speak", "propose_exchange", "end" },
                _ when observation.Residents.Length > 0 => new[] { "wait", "approach", "invite" },
                _ => new[] { "wait" }
            };
            var targets = commitment is null ? observation.Residents.Select(r => r.Id).ToArray() : new[] { Other(commitment, resident.Id) };
            var windowEnd = Math.Min(ScheduleEnd(resident), commitment?.EndsAtMinute ?? double.MaxValue);
            var id = NewIdentity("decision");
            var input = JsonSerializer.Serialize(new
            {
                protocol = "town-candidate-v1", resident = new { resident.Id, definition = _content.ResidentData(resident.Id).Personality, desiredResourceId = _content.ResidentData(resident.Id).DesiredResourceId, inventory = resident.Inventory, resident.Coins },
                localObservation = observation, memories = resident.Memories.TakeLast(6),
                freeWindow = new { startsAtMinute = _state.WorldMinute, endsAtMinute = windowEnd },
                commitment = commitment is null ? null : new { commitment.Id, commitment.Stage, commitment.InitiatorId, commitment.InviteeId, commitment.PlaceId, commitment.TurnNumber, commitment.OfferResourceId, commitment.OfferQuantity, commitment.OfferPrice, commitment.OfferFromResidentId, dialogue = commitment.Dialogue.TakeLast(6) },
                allowedActions = allowed, allowedTargets = targets,
                actionConstraints = allowed.Select(action => new
                {
                    action,
                    nullFields = action switch
                    {
                        "wait" => new[] { "targetId", "placeId", "resourceId", "accept" },
                        "invite" => ["resourceId", "accept"],
                        "respond" => ["placeId", "resourceId"],
                        "propose_exchange" => ["placeId", "accept"],
                        _ => ["placeId", "resourceId", "accept"]
                    },
                    zeroFields = action == "propose_exchange" ? Array.Empty<string>() : ["quantity", "price"]
                }),
                tradeConstraints = new
                {
                    proposingSellerId = resident.Id,
                    priceIsTotalCoinsPaidByBuyer = true,
                    barterAllowed = false,
                    availableSales = resident.Inventory.Where(p => p.Value > 0).OrderBy(p => p.Key, StringComparer.Ordinal)
                        .Select(p => new { resourceId = p.Key, maximumQuantity = p.Value }),
                    pendingOffer = commitment?.Stage == "exchange_offered" ? new
                    {
                        sellerId = commitment.OfferFromResidentId,
                        buyerId = Other(commitment, commitment.OfferFromResidentId!),
                        resourceId = commitment.OfferResourceId,
                        quantity = commitment.OfferQuantity,
                        totalCoinPrice = commitment.OfferPrice,
                        selfCanPay = resident.Id != commitment.OfferFromResidentId && resident.Coins >= commitment.OfferPrice
                    } : null,
                    previousOfferIsHistorical = commitment?.OfferResourceId is not null && commitment.Stage != "exchange_offered"
                },
                rules = "Choose your own useful social action using only these observations. Public offered resource kinds mean that person has announced a surplus, not permission to take it. Invite a visible person to a visible place, or approach first. You may decline or wait. During a conversation you may offer one of your own resources for a nonnegative coin price; the other person must accept. Schedules take priority. Never invent identities or assets.",
                candidateFields = new { action = "one allowed action", targetId = "legal resident ID or null", placeId = "visible place ID for invite, otherwise null", resourceId = "own resource ID for propose_exchange, otherwise null", quantity = "positive integer for exchange, otherwise 0", price = "nonnegative total coins for exchange, otherwise 0", accept = "boolean for respond, otherwise null", text = "brief in-character Chinese speech, maximum 160 characters" }
            }, DecisionInputJson);
            var request = new DecisionRequest(id, resident.Id, resident.ActivityRevision, windowEnd, 60, input, TownContent.Hash(input), allowed, targets, false, null);
            _state.PendingDecisions.Add(new PendingDecisionState { Request = request, Observation = observation });
            _state.ModelQueue.Add(id);
            resident.PendingRequestId = id; resident.LastOpportunityFingerprint = opportunity;
            Log("decision_created", resident.Id, "rules", "opportunity", id, resident.CommitmentId, new { request, observation });
        }
        return _state.PendingDecisions.Where(p => p.SealedResult is null && p.Request.AttemptId is null).Select(p => TownJson.Clone(p.Request)).ToArray();
    }

    public bool MarkDispatched(string logicalRequestId, string attemptId)
    {
        using var trace = TraceInput("dispatch", new { logicalRequestId, attemptId });
        EnsureEconomyNotCommitting();
        var pending = _state.PendingDecisions.SingleOrDefault(p => p.Request.LogicalRequestId == logicalRequestId);
        if (pending is null || pending.Request.AttemptId is not null || pending.SealedResult is not null || string.IsNullOrWhiteSpace(attemptId)) return false;
        pending.Request = pending.Request with { AttemptId = attemptId };
        var resident = FindResident(pending.Request.ResidentId);
        resident.CooldownSeconds = 30;
        _state.DecisionCursor = (_state.Residents.IndexOf(resident) + 1) % _state.Residents.Count;
        Log("decision_dispatched", resident.Id, "host", "attempt_registered", logicalRequestId, resident.CommitmentId, new { attemptId, pending.Request.IsRecovery });
        return true;
    }

    public bool MarkResponseArrived(string sessionId, string logicalRequestId, string attemptId, double receivedAfterSeconds)
    {
        using var trace = TraceInput("arrival", new { sessionId, logicalRequestId, attemptId, receivedAfterSeconds });
        EnsureEconomyNotCommitting();
        var pending = _state.PendingDecisions.SingleOrDefault(p => p.Request.LogicalRequestId == logicalRequestId);
        string? reason = sessionId != SessionId ? "retired_session"
            : _state.TerminalRequestIds.Contains(logicalRequestId) ? "terminal_request"
            : pending is null ? "unknown_request"
            : pending.Request.AttemptId != attemptId ? "wrong_attempt"
            : !TownContent.Finite(receivedAfterSeconds) || receivedAfterSeconds < 0 || receivedAfterSeconds >= 60 ? "arrival_outside_deadline"
            : pending.SealedResult is not null && pending.SealedResult.Error != "logical_timeout" ? "response_already_sealed"
            : pending.ResponseArrivalSeconds is double existing && Math.Abs(existing - receivedAfterSeconds) > .000001 ? "arrival_time_conflict" : null;
        if (reason is not null)
        {
            Log("response_arrival_rejected", pending?.Request.ResidentId, "host", reason, logicalRequestId, data: new { attemptId, receivedAfterSeconds = TownContent.Finite(receivedAfterSeconds) ? (double?)receivedAfterSeconds : null });
            return false;
        }
        if (pending!.ResponseArrivalSeconds is not null) return true;
        pending.SealedResult = null;
        pending.ResponseArrivalSeconds = receivedAfterSeconds;
        pending.WaitedSeconds = receivedAfterSeconds;
        pending.Request = pending.Request with { RemainingWaitSeconds = 60 - receivedAfterSeconds };
        Log("response_arrived", pending.Request.ResidentId, "host", "awaiting_durable_completion", logicalRequestId, data: new { attemptId, receivedAfterSeconds });
        return true;
    }

    public bool ReceiveResult(DecisionResult result)
    {
        using var trace = TraceInput("result", new { result });
        EnsureEconomyNotCommitting();
        var pending = _state.PendingDecisions.SingleOrDefault(p => p.Request.LogicalRequestId == result.LogicalRequestId);
        var reason = result.SessionId != SessionId ? "retired_session" : _state.TerminalRequestIds.Contains(result.LogicalRequestId) ? "terminal_request" : pending is null ? "unknown_request" : pending.SealedResult is not null ? "duplicate_result" : pending.Request.ResidentId != result.ResidentId ? "wrong_resident" : pending.Request.AttemptId != result.AttemptId ? "wrong_attempt" : pending.ResponseArrivalSeconds is double arrived && Math.Abs(arrived - result.ReceivedAfterSeconds) > .000001 ? "arrival_time_conflict" : null;
        if (reason is not null) { Log("response_rejected", result.ResidentId, result.Source, reason, result.LogicalRequestId, data: new { result.AttemptId }); return false; }
        if (result.ReceivedAfterSeconds < 0 || !TownContent.Finite(result.ReceivedAfterSeconds)) throw new ArgumentOutOfRangeException(nameof(result));
        pending!.SealedResult = TownJson.Clone(result);
        pending.ResponseArrivalSeconds = null;
        Log("response_received", result.ResidentId, result.Source, result.Error ?? "complete", result.LogicalRequestId, data: new { result });
        return true;
    }

    public ActionCandidate CreateRuleCandidate(DecisionRequest request)
    {
        var resident = FindResident(request.ResidentId); var commitment = ActiveCommitment(resident);
        if (request.AllowedActions.Contains("respond"))
        {
            bool accept = commitment?.Stage == "offered" || (commitment?.Stage == "exchange_offered" && commitment.OfferResourceId == _content.ResidentData(resident.Id).DesiredResourceId && resident.Coins >= commitment.OfferPrice);
            return new ActionCandidate("respond", commitment is null ? null : Other(commitment, resident.Id), null, null, 0, 0, accept, accept ? "好呀，我们碰个面。" : "谢谢，不过这次先不了。");
        }
        if (request.AllowedActions.Contains("propose_exchange") && commitment is not null)
        {
            var partner = FindResident(Other(commitment, resident.Id)); var desired = _content.ResidentData(partner.Id).DesiredResourceId;
            if (resident.Inventory.GetValueOrDefault(desired) > 1)
                return new ActionCandidate("propose_exchange", partner.Id, null, desired, 1, 1, null, "我带了多余的一份，你需要吗？");
            if (commitment.TurnNumber == 0)
                return new ActionCandidate("speak", partner.Id, null, null, 0, 0, null, "我想听听你最近有什么需要，也看看我们能不能互相帮忙。");
            return new ActionCandidate("end", partner.Id, null, null, 0, 0, null, "聊得很开心，下次见。");
        }
        if (request.AllowedActions.Contains("invite"))
        {
            var observation = _state.PendingDecisions.Single(p => p.Request.LogicalRequestId == request.LogicalRequestId).Observation;
            var ownDesire = _content.ResidentData(resident.Id).DesiredResourceId;
            var target = observation.Residents.OrderByDescending(p => p.OfferedResourceIds.Contains(ownDesire)).ThenBy(p => resident.Position.DistanceTo(p.Position)).ThenBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault();
            var place = observation.Places.OrderBy(p => resident.Position.DistanceTo(p.Position)).ThenBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault();
            if (target is not null && place is not null) return new ActionCandidate("invite", target.Id, place.Id, null, 0, 0, null, "有空聊聊彼此最近需要什么吗？");
        }
        return new ActionCandidate("wait", null, null, null, 0, 0, null, "我先看看周围。");
    }

    private void ConsumeMailbox()
    {
        foreach (var pending in _state.PendingDecisions.Where(p => p.SealedResult is not null).ToArray())
        {
            var request = pending.Request; var result = pending.SealedResult!; var resident = FindResident(request.ResidentId);
            var invalid = request.ActivityRevision != resident.ActivityRevision ? "activity_changed" : _state.WorldMinute >= request.WindowEndMinute ? "window_ended" : !IsFree(resident) ? "schedule_busy" : null;
            if (invalid is null && result.Error is null && result.ReceivedAfterSeconds >= 60) invalid = "logical_timeout";
            if (invalid is not null) Log("candidate_rejected", resident.Id, result.Source, invalid, request.LogicalRequestId, resident.CommitmentId);
            else if (result.Error is not null || result.Candidate is null)
            {
                var candidate = CreateRuleCandidate(request);
                Log("rule_fallback", resident.Id, "rule_fallback", result.Error ?? "missing_candidate", request.LogicalRequestId, resident.CommitmentId, new { candidate });
                ApplyCandidate(resident, request, candidate, "rule_fallback");
            }
            else ApplyCandidate(resident, request, result.Candidate, result.Source);
            CompletePending(pending, "consumed");
        }
    }

    private void ApplyCandidate(ResidentState resident, DecisionRequest request, ActionCandidate candidate, string source)
    {
        string? invalid = !request.AllowedActions.Contains(candidate.Action) ? "action_not_allowed" : candidate.Text is null || candidate.Text.Length > 160 || candidate.Text.Any(char.IsControl) ? "invalid_text" : candidate.Quantity < 0 || candidate.Quantity > 1000 || candidate.Price < 0 || candidate.Price > 1000000 ? "invalid_amount" : null;
        if (invalid is null && !CandidateFieldsMatchAction(candidate)) invalid = "unexpected_action_fields";
        if (invalid is null && candidate.Action != "wait" && (candidate.TargetId is null || !request.AllowedTargets.Contains(candidate.TargetId))) invalid = "target_not_allowed";
        if (invalid is null)
        {
            invalid = candidate.Action switch
            {
                "wait" => Wait(resident),
                "approach" => Approach(resident, candidate),
                "invite" => Invite(resident, candidate, source),
                "respond" => Respond(resident, candidate, source),
                "speak" => Speak(resident, candidate, source),
                "propose_exchange" => OfferExchange(resident, candidate, source),
                "end" => End(resident, candidate, source),
                _ => "unknown_action"
            };
        }
        Log(invalid is null ? "candidate_executed" : "candidate_rejected", resident.Id, source, invalid ?? candidate.Action, request.LogicalRequestId, resident.CommitmentId, new { candidate });
        if (invalid is not null)
        {
            resident.CooldownSeconds = 30;
            Log("rule_fallback", resident.Id, "rule_fallback", "wait_after_" + invalid, request.LogicalRequestId, resident.CommitmentId);
        }
    }

    private static bool CandidateFieldsMatchAction(ActionCandidate c) => c.Action switch
    {
        "wait" => c.TargetId is null && c.PlaceId is null && c.ResourceId is null && c.Accept is null && c.Quantity == 0 && c.Price == 0,
        "approach" or "speak" or "end" => c.PlaceId is null && c.ResourceId is null && c.Accept is null && c.Quantity == 0 && c.Price == 0,
        "invite" => c.PlaceId is not null && c.ResourceId is null && c.Accept is null && c.Quantity == 0 && c.Price == 0,
        "respond" => c.PlaceId is null && c.ResourceId is null && c.Accept is not null && c.Quantity == 0 && c.Price == 0,
        "propose_exchange" => c.PlaceId is null && c.ResourceId is not null && c.Accept is null && c.Quantity > 0,
        _ => false
    };

    private string? Wait(ResidentState resident) { resident.CooldownSeconds = 30; return null; }
    private string? Approach(ResidentState resident, ActionCandidate candidate)
    {
        var observed = Observe(resident.Id).Residents.SingleOrDefault(r => r.Id == candidate.TargetId);
        if (observed is null) return "target_not_visible";
        var target = MeetingPosition(observed.Position, resident.Id, 24);
        var route = _content.FindRoute(resident.Position, target);
        if (route is null) return "target_unreachable";
        resident.Route = route; resident.RouteCursor = 0; resident.DestinationPlaceId = null; resident.Activity = "approaching"; resident.ActivityRevision++;
        return null;
    }

    private string? Invite(ResidentState resident, ActionCandidate candidate, string source)
    {
        var target = FindResident(candidate.TargetId!); var observation = Observe(resident.Id);
        if (!observation.Residents.Any(r => r.Id == target.Id)) return "target_not_visible";
        if (candidate.PlaceId is null || !observation.Places.Any(p => p.Id == candidate.PlaceId)) return "place_not_observed";
        if (!IsFree(target)) return "target_schedule_busy";
        if (ActiveCommitment(resident) is not null || ActiveCommitment(target) is not null) return "participant_busy";
        var end = Math.Min(Math.Min(ScheduleEnd(resident), ScheduleEnd(target)), _state.WorldMinute + 45);
        if (end - _state.WorldMinute < 2) return "insufficient_window";
        var commitment = new CommitmentState { Id = NewIdentity("meeting"), InitiatorId = resident.Id, InviteeId = target.Id, PlaceId = candidate.PlaceId, EndsAtMinute = end, TurnResidentId = target.Id };
        _state.Commitments.Add(commitment);
        resident.CommitmentId = target.CommitmentId = commitment.Id;
        resident.Activity = "awaiting_response"; target.Activity = "considering_invitation";
        resident.ActivityRevision++; target.ActivityRevision++;
        if (target.PendingRequestId is not null) TerminateResidentPending(target, "invitation_received");
        AddDialogue(commitment, resident, candidate.Text, source);
        Log("invitation_created", resident.Id, source, "awaiting_response", activityId: commitment.Id, data: new { targetId = target.Id, commitment.PlaceId, end });
        return null;
    }

    private string? Respond(ResidentState resident, ActionCandidate candidate, string source)
    {
        var commitment = ActiveCommitment(resident);
        if (commitment is null || commitment.TurnResidentId != resident.Id || candidate.TargetId != Other(commitment, resident.Id)) return "not_response_turn";
        if (candidate.Accept is null) return "missing_accept";
        if (commitment.Stage is not ("offered" or "exchange_offered")) return "no_pending_offer";
        if (!candidate.Accept.Value)
        {
            AddDialogue(commitment, resident, candidate.Text, source); EndCommitment(commitment, "declined", source); return null;
        }
        if (commitment.Stage == "exchange_offered")
        {
            var seller = FindResident(commitment.OfferFromResidentId!);
            if (resident.Id == seller.Id || !BothArrived(commitment)) return "participants_not_arrived";
            if (commitment.OfferResourceId is null || seller.Inventory.GetValueOrDefault(commitment.OfferResourceId) < commitment.OfferQuantity || resident.Coins < commitment.OfferPrice) return "insufficient_assets";
            if (commitment.TransferId is null || _state.ExecutedTransferIds.Contains(commitment.TransferId)) return "duplicate_transfer";
            if (resident.Inventory.GetValueOrDefault(commitment.OfferResourceId) > 100000 - commitment.OfferQuantity || seller.Coins > 1000000 - commitment.OfferPrice) return "asset_limit";
            seller.Inventory[commitment.OfferResourceId] -= commitment.OfferQuantity;
            resident.Inventory[commitment.OfferResourceId] = resident.Inventory.GetValueOrDefault(commitment.OfferResourceId) + commitment.OfferQuantity;
            resident.Coins -= commitment.OfferPrice; seller.Coins += commitment.OfferPrice;
            _state.ExecutedTransferIds.Add(commitment.TransferId);
            Log("resource_transferred", resident.Id, source, "atomic_transfer", activityId: commitment.Id, data: new { commitment.TransferId, from = seller.Id, to = resident.Id, commitment.OfferResourceId, commitment.OfferQuantity, commitment.OfferPrice });
            AddMemory(seller, source, "resource_transferred", $"Gave {commitment.OfferQuantity} {commitment.OfferResourceId} to {resident.Id}.");
            AddMemory(resident, source, "resource_received", $"Received {commitment.OfferQuantity} {commitment.OfferResourceId} from {seller.Id}.");
            AddDialogue(commitment, resident, candidate.Text, source); commitment.Stage = "conversation";
            commitment.TurnResidentId = seller.Id; commitment.TurnNumber++;
            return null;
        }
        var initiator = FindResident(commitment.InitiatorId);
        var meeting = _content.Place(commitment.PlaceId).Position;
        var firstRoute = _content.FindRoute(initiator.Position, MeetingPosition(meeting, initiator.Id, 12));
        var secondRoute = _content.FindRoute(resident.Position, MeetingPosition(meeting, resident.Id, 12));
        if (firstRoute is null || secondRoute is null) return "meeting_unreachable";
        SetRoute(initiator, firstRoute, commitment.PlaceId, "meeting_travel");
        SetRoute(resident, secondRoute, commitment.PlaceId, "meeting_travel");
        commitment.Stage = "travelling"; commitment.TurnResidentId = initiator.Id;
        AddDialogue(commitment, resident, candidate.Text, source);
        Log("meeting_accepted", resident.Id, source, "travelling", activityId: commitment.Id);
        return null;
    }

    private string? Speak(ResidentState resident, ActionCandidate candidate, string source)
    {
        var commitment = ActiveCommitment(resident);
        if (commitment is null || commitment.Stage != "conversation" || commitment.TurnResidentId != resident.Id || candidate.TargetId != Other(commitment, resident.Id) || !BothArrived(commitment)) return "not_conversation_turn";
        AddDialogue(commitment, resident, candidate.Text, source);
        commitment.TurnResidentId = Other(commitment, resident.Id); commitment.TurnNumber++;
        if (commitment.TurnNumber >= 8) EndCommitment(commitment, "conversation_complete", "world");
        return null;
    }

    private string? OfferExchange(ResidentState resident, ActionCandidate candidate, string source)
    {
        var commitment = ActiveCommitment(resident);
        if (commitment is null || commitment.Stage != "conversation" || commitment.TurnResidentId != resident.Id || candidate.TargetId != Other(commitment, resident.Id) || !BothArrived(commitment)) return "not_conversation_turn";
        if (candidate.ResourceId is null || candidate.Quantity <= 0 || resident.Inventory.GetValueOrDefault(candidate.ResourceId) < candidate.Quantity) return "insufficient_assets";
        commitment.OfferResourceId = candidate.ResourceId; commitment.OfferQuantity = candidate.Quantity; commitment.OfferPrice = candidate.Price;
        commitment.OfferFromResidentId = resident.Id; commitment.TransferId = NewIdentity("transfer");
        commitment.Stage = "exchange_offered"; commitment.TurnResidentId = Other(commitment, resident.Id); commitment.TurnNumber++;
        AddDialogue(commitment, resident, candidate.Text, source);
        return null;
    }

    private string? End(ResidentState resident, ActionCandidate candidate, string source)
    {
        var commitment = ActiveCommitment(resident);
        if (commitment is null || candidate.TargetId != Other(commitment, resident.Id)) return "no_commitment";
        AddDialogue(commitment, resident, candidate.Text, source); EndCommitment(commitment, "resident_ended", source); return null;
    }

    private void EndCommitment(CommitmentState commitment, string reason, string source)
    {
        if (!IsActive(commitment)) return;
        commitment.Stage = "ended";
        foreach (var resident in _state.Residents.Where(r => r.CommitmentId == commitment.Id))
        {
            resident.CommitmentId = null; resident.ActivityRevision++; resident.CooldownSeconds = 30;
            TerminateResidentPending(resident, reason);
            var route = _content.FindRoute(resident.Position, _content.Place(CurrentSchedule(resident).PlaceId).Position);
            if (route is not null) SetRoute(resident, route, CurrentSchedule(resident).PlaceId, IsFree(resident) ? "free_travel" : "schedule_travel");
            else { resident.Route = []; resident.RouteCursor = 0; resident.BlockedReason = "schedule_unreachable"; }
            AddMemory(resident, source, "meeting_ended", reason);
        }
        Log("meeting_ended", commitment.InitiatorId, source, reason, activityId: commitment.Id);
    }

    private void UpdateSchedules()
    {
        foreach (var resident in _state.Residents)
        {
            var day = (long)Math.Floor(_state.WorldMinute / 1440); var schedule = CurrentSchedule(resident);
            if (resident.ScheduleDay == day && resident.ScheduleId == schedule.Id) continue;
            var commitment = ActiveCommitment(resident);
            if (commitment is not null) EndCommitment(commitment, "schedule_boundary", "world");
            TerminateResidentPending(resident, "schedule_boundary");
            resident.ScheduleDay = day; resident.ScheduleId = schedule.Id; resident.ActivityRevision++;
            resident.LastOpportunityFingerprint = "";
            var route = _content.FindRoute(resident.Position, _content.Place(schedule.PlaceId).Position);
            if (route is null) { resident.Route = []; resident.RouteCursor = 0; resident.Activity = "blocked"; resident.BlockedReason = "schedule_unreachable"; }
            else SetRoute(resident, route, schedule.PlaceId, schedule.IsFree ? "free_travel" : "schedule_travel");
            Log("schedule_started", resident.Id, "world", schedule.Activity, activityId: schedule.Id, data: new { day, schedule });
        }
    }

    private void Move(ResidentState resident, double distance)
    {
        if (resident.RouteCursor >= resident.Route.Count) return;
        while (distance > .000001 && resident.RouteCursor < resident.Route.Count)
        {
            var waypoint = resident.Route[resident.RouteCursor]; var remaining = resident.Position.DistanceTo(waypoint);
            if (remaining <= .000001) { resident.RouteCursor++; continue; }
            var fraction = Math.Min(1, Math.Min(distance, 4) / remaining);
            var next = new TownPoint(resident.Position.X + (waypoint.X - resident.Position.X) * fraction, resident.Position.Y + (waypoint.Y - resident.Position.Y) * fraction);
            if (!QueryMovement(resident, next))
            {
                var origin = resident.Position;
                double length = origin.DistanceTo(next);
                const double tolerance = .0000001;
                TownPoint At(double distance) => new(origin.X + (next.X - origin.X) * distance / length, origin.Y + (next.Y - origin.Y) * distance / length);
                if (length > tolerance && QueryMovement(resident, At(tolerance)))
                {
                    double low = tolerance, high = length;
                    while (high - low > tolerance)
                    {
                        double middle = (low + high) / 2;
                        if (QueryMovement(resident, At(middle))) low = middle; else high = middle;
                    }
                    resident.Position = At(low);
                    resident.Facing = new((next.X - origin.X) / length, (next.Y - origin.Y) / length);
                    resident.RegionId = _content.RegionAt(resident.Position);
                }
                resident.BlockedReason = "body_blocked";
                return;
            }
            resident.BlockedReason = null;
            resident.Facing = new TownPoint((next.X - resident.Position.X) / (remaining * fraction), (next.Y - resident.Position.Y) / (remaining * fraction));
            distance -= remaining * fraction; resident.Position = next; resident.RegionId = _content.RegionAt(next);
            if (fraction >= 1) resident.RouteCursor++;
        }
        if (resident.RouteCursor == resident.Route.Count && resident.CommitmentId is null) resident.Activity = IsFree(resident) ? "free" : "scheduled";
    }

    private bool QueryMovement(ResidentState resident, TownPoint next)
    {
        bool allowed = _content.CanMove(resident.Position, next) && QueryMotion(resident.Id, resident.Position, next);
        Log("space_query", resident.Id, "world", allowed ? "clear" : "blocked", data: new { from = resident.Position, to = next, radius = TownContent.BodyRadius, allowed });
        return allowed;
    }

    private void UpdateArrivals()
    {
        foreach (var commitment in _state.Commitments.Where(c => c.Stage == "travelling"))
        {
            foreach (var resident in _state.Residents.Where(r => r.CommitmentId == commitment.Id && r.RouteCursor == r.Route.Count))
                if (resident.Position.DistanceTo(_content.Place(commitment.PlaceId).Position) <= 24 && commitment.ArrivedResidentIds.Add(resident.Id))
                { resident.Activity = "awaiting_partner"; Log("resident_arrived", resident.Id, "world", "meeting_place", activityId: commitment.Id); }
            if (BothArrived(commitment))
            {
                commitment.Stage = "conversation";
                foreach (var resident in _state.Residents.Where(r => r.CommitmentId == commitment.Id)) { resident.Activity = "conversation"; resident.ActivityRevision++; }
                Log("conversation_started", commitment.InitiatorId, "world", "both_arrived", activityId: commitment.Id);
            }
        }
    }

    private void SetRoute(ResidentState resident, List<TownPoint> route, string placeId, string activity)
    {
        resident.Route = route; resident.RouteCursor = 0; resident.DestinationPlaceId = placeId; resident.Activity = activity; resident.ActivityRevision++; resident.BlockedReason = null;
    }
    private void CompletePending(PendingDecisionState pending, string reason)
    {
        if (!_state.PendingDecisions.Remove(pending)) return;
        _state.TerminalRequestIds.Add(pending.Request.LogicalRequestId);
        _state.ModelQueue.Remove(pending.Request.LogicalRequestId);
        var resident = FindResident(pending.Request.ResidentId);
        if (resident.PendingRequestId == pending.Request.LogicalRequestId) resident.PendingRequestId = null;
        resident.CooldownSeconds = Math.Max(resident.CooldownSeconds, 30);
        Log("decision_terminal", resident.Id, "world", reason, pending.Request.LogicalRequestId);
    }
    private void TerminateResidentPending(ResidentState resident, string reason)
    {
        var pending = _state.PendingDecisions.SingleOrDefault(p => p.Request.LogicalRequestId == resident.PendingRequestId);
        if (pending is null) return;
        Log("candidate_rejected", resident.Id, pending.SealedResult?.Source ?? "world", reason, pending.Request.LogicalRequestId, resident.CommitmentId);
        CompletePending(pending, reason);
    }
    private ResidentState FindResident(string id) => _state.Residents.SingleOrDefault(r => r.Id == id) ?? throw new InvalidDataException($"Unknown resident: {id}");
    private ScheduleDefinition CurrentSchedule(ResidentState resident) => _content.ResidentData(resident.Id).Schedule.Single(s => (_state.WorldMinute % 1440) >= s.StartMinute && (_state.WorldMinute % 1440) < s.EndMinute);
    private double ScheduleEnd(ResidentState resident) => Math.Floor(_state.WorldMinute / 1440) * 1440 + CurrentSchedule(resident).EndMinute;
    private bool IsFree(ResidentState resident) => CurrentSchedule(resident).IsFree;
    private CommitmentState? ActiveCommitment(ResidentState resident) => resident.CommitmentId is null ? null : _state.Commitments.SingleOrDefault(c => c.Id == resident.CommitmentId && IsActive(c));
    private static bool IsActive(CommitmentState commitment) => commitment.Stage != "ended";
    private static string Other(CommitmentState commitment, string id) => commitment.InitiatorId == id ? commitment.InviteeId : commitment.InitiatorId;
    private bool BothArrived(CommitmentState commitment) => commitment.ArrivedResidentIds.Contains(commitment.InitiatorId) && commitment.ArrivedResidentIds.Contains(commitment.InviteeId) && FindResident(commitment.InitiatorId).Position.DistanceTo(FindResident(commitment.InviteeId).Position) <= 48;
    private bool HasSight(TownPoint from, TownPoint to)
    {
        var count = Math.Max(1, (int)Math.Ceiling(from.DistanceTo(to) / 4));
        for (int i = 0; i <= count; i++) if (_content.Geometry.Obstacles.Any(o => o.Bounds.Contains(new TownPoint(from.X + (to.X - from.X) * i / count, from.Y + (to.Y - from.Y) * i / count)))) return false;
        return true;
    }
    private TownPoint MeetingPosition(TownPoint point, string residentId, double offset)
    {
        var index = _state.Residents.FindIndex(r => r.Id == residentId);
        var directions = new[] { new TownPoint(-1, 0), new TownPoint(1, 0), new TownPoint(0, -1), new TownPoint(0, 1) };
        for (int i = 0; i < 4; i++) { var d = directions[(index + i) % 4]; var p = new TownPoint(point.X + offset * d.X, point.Y + offset * d.Y); if (_content.IsWalkable(p)) return p; }
        return point;
    }
    private string OpportunityFingerprint(ResidentState resident, LocalObservation observation, CommitmentState? commitment) => TownContent.Hash(TownJson.Serialize(new { resident.ScheduleDay, resident.ScheduleId, resident.Inventory, resident.Coins, residents = observation.Residents.Select(r => new { r.Id, r.Activity, r.OfferedResourceIds }), commitment = commitment?.Id, stage = commitment?.Stage, turn = commitment?.TurnNumber }));
    private string NewIdentity(string kind) => $"{_state.BranchId}:{kind}:{++_state.IdentitySequence}";
    private void AddDialogue(CommitmentState commitment, ResidentState resident, string text, string source)
    {
        commitment.Dialogue.Add($"{resident.Id}: {text}");
        foreach (var id in new[] { commitment.InitiatorId, commitment.InviteeId }) AddMemory(FindResident(id), source, "dialogue", $"{resident.Id}: {text}");
        Log("dialogue_spoken", resident.Id, source, "accepted", activityId: commitment.Id, data: new { text });
    }
    private void AddMemory(ResidentState resident, string source, string kind, string text) => resident.Memories.Add(new ResidentMemory(_state.Sequence + 1, _state.WorldMinute, source, kind, text));
    private void Log(string kind, string? residentId, string source, string reason, string? requestId = null, string? activityId = null, object? data = null)
    {
        _state.Events.Add(new TownEvent(++_state.Sequence, _state.BranchId, _state.WorldMinute, kind, residentId, source, reason, requestId, activityId, data is null ? "{}" : TownJson.Serialize(data)));
    }

    public void AcknowledgeEvents(long persistedSequence)
    {
        using var trace = TraceInput("acknowledge", new { persistedSequence });
        EnsureEconomyNotCommitting();
        if (persistedSequence < 0 || persistedSequence > _state.Sequence) throw new ArgumentOutOfRangeException(nameof(persistedSequence));
        if (_state.Events.Count > 128) _state.Events.RemoveAll(e => e.Sequence <= persistedSequence && e.Sequence <= _state.Sequence - 128);
    }
}
