using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace CozyTown.Core.Town;

public sealed partial class TownWorld
{
    public TownSnapshot CaptureSnapshot()
    {
        if (HasUncommittedResponses) throw new InvalidOperationException("A received model response is still being persisted; save after its completion is sealed.");
        return new(1, Guid.NewGuid().ToString("N"), _content.Fingerprint, RulesVersion, NumericVersion, TownJson.Clone(_state));
    }
    public string CaptureJson() => TownJson.Serialize(CaptureSnapshot());

    public static TownWorld RestoreCandidate(TownContent content, string json, Func<string, TownPoint, TownPoint, bool>? movementQuery = null)
    {
        if (json.Length > 64 * 1024 * 1024) throw new InvalidDataException("Snapshot exceeds 64 MiB.");
        TownSnapshot snapshot;
        try { snapshot = TownJson.Deserialize<TownSnapshot>(json); }
        catch (JsonException ex) { throw new InvalidDataException("Snapshot JSON fields are missing or invalid.", ex); }
        ValidateSnapshot(content, snapshot, movementQuery);
        var state = snapshot.State;
        var parent = state.BranchId;
        state.ParentBranchId = parent; state.BranchId = Guid.NewGuid().ToString("N");
        state.SourceSnapshotId = snapshot.SnapshotId; state.ForkSequence = state.Sequence;
        var world = new TownWorld(content, state, movementQuery);
        foreach (var pending in state.PendingDecisions)
        {
            if (pending.SealedResult is not null)
                pending.SealedResult = pending.SealedResult with { SessionId = world.SessionId };
            else if (pending.Request.AttemptId is not null)
                pending.Request = pending.Request with { AttemptId = null, IsRecovery = true };
        }
        world.Log("world_restored", null, "host", "snapshot_validated", data: new { snapshot.SnapshotId, parentBranchId = parent, snapshotSequence = state.ForkSequence });
        return world;
    }

    private static void ValidateSnapshot(TownContent content, TownSnapshot snapshot, Func<string, TownPoint, TownPoint, bool>? movementQuery)
    {
        void Require([DoesNotReturnIf(false)] bool condition, string field) { if (!condition) throw new InvalidDataException($"Invalid town snapshot: {field}"); }
        bool Id(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 200;
        Require(snapshot.SchemaVersion == 1, "schemaVersion");
        Require(snapshot.ContentFingerprint == content.Fingerprint, "contentFingerprint");
        Require(snapshot.RulesVersion == RulesVersion && snapshot.NumericVersion == NumericVersion, "simulation versions");
        Require(Id(snapshot.SnapshotId) && snapshot.State is not null, "snapshot identity/state");
        var state = snapshot.State!;
        Require(Id(state.BranchId) && Id(state.BudgetId) && Id(state.InitialStateId) && state.InitialStateFingerprint == content.Fingerprint, "state identities");
        Require(TownContent.Finite(state.WorldMinute) && state.WorldMinute >= 0 && state.WorldMinute < 1440 * 1_000_000 && TownContent.Finite(state.AcceptedWorldMinutes) && state.AcceptedWorldMinutes >= 0 && state.AcceptedWorldMinutes <= 1440, "world time");
        Require(state.Residents is not null && state.Commitments is not null && state.PendingDecisions is not null && state.TerminalRequestIds is not null && state.ExecutedTransferIds is not null && state.Events is not null, "collections");
        Require(state.Residents.All(r => r is not null) && state.Commitments.All(c => c is not null) && state.PendingDecisions.All(p => p is not null), "null state entry");
        Require(state.Sequence >= 0 && state.IdentitySequence >= 0 && state.ForkSequence >= 0 && state.ForkSequence <= state.Sequence, "sequences");
        Require(state.DecisionCursor >= 0 && state.DecisionCursor < content.Data.Residents.Length, "decision cursor");
        Require(content.IsWalkable(state.PlayerPosition) && (movementQuery?.Invoke("player", state.PlayerPosition, state.PlayerPosition) ?? true), "player position");
        Require(TownContent.Finite(state.PlayerFacing.X) && TownContent.Finite(state.PlayerFacing.Y), "player facing");
        Require(state.Residents!.Select(r => r.Id).Order(StringComparer.Ordinal).SequenceEqual(content.Data.Residents.Select(r => r.Id)), "resident bindings");
        Require(state.Commitments!.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() == state.Commitments.Count && state.Commitments.All(c => Id(c.Id)), "commitment identities");
        Require(state.PendingDecisions!.All(p => p.Request is not null && p.Observation is not null), "pending fields");
        Require(state.PendingDecisions.Select(p => p.Request.LogicalRequestId).Distinct(StringComparer.Ordinal).Count() == state.PendingDecisions.Count && state.PendingDecisions.Select(p => p.Request.ResidentId).Distinct(StringComparer.Ordinal).Count() == state.PendingDecisions.Count, "pending uniqueness");
        foreach (var resident in state.Residents)
        {
            Require(resident is not null && content.IsWalkable(resident.Position) && (movementQuery?.Invoke(resident.Id, resident.Position, resident.Position) ?? true), "resident position");
            Require(resident!.RegionId == content.RegionAt(resident.Position), $"{resident.Id} region");
            Require(TownContent.Finite(resident.Facing.X) && TownContent.Finite(resident.Facing.Y), $"{resident.Id} facing");
            Require(resident.Inventory is not null && resident.Coins is >= 0 and <= 1000000 && resident.Inventory.All(kv => kv.Value is >= 0 and <= 100000 && content.Data.Resources.Any(r => r.Id == kv.Key)), $"{resident.Id} assets");
            Require(resident.Route is not null && resident.RouteCursor >= 0 && resident.RouteCursor <= resident.Route.Count && resident.Route.Count <= 32768, $"{resident.Id} route cursor");
            var previous = resident.Position;
            foreach (var point in resident.Route!.Skip(resident.RouteCursor)) { Require(content.CanMove(previous, point), $"{resident.Id} route segment"); previous = point; }
            Require(resident.Route.All(content.IsWalkable), $"{resident.Id} route points");
            var schedule = content.ResidentData(resident.Id).Schedule.Single(s => state.WorldMinute % 1440 >= s.StartMinute && state.WorldMinute % 1440 < s.EndMinute);
            Require(resident.ScheduleId == schedule.Id && resident.ScheduleDay == (long)Math.Floor(state.WorldMinute / 1440), $"{resident.Id} schedule");
            Require(new[] { "free", "scheduled", "free_travel", "schedule_travel", "blocked", "approaching", "awaiting_response", "considering_invitation", "meeting_travel", "awaiting_partner", "conversation" }.Contains(resident.Activity) && resident.ActivityRevision >= 0, $"{resident.Id} activity");
            Require(resident.DestinationPlaceId is null || content.Geometry.Places.Any(p => p.Id == resident.DestinationPlaceId), $"{resident.Id} destination");
            Require(TownContent.Finite(resident.CooldownSeconds) && resident.CooldownSeconds is >= 0 and <= 30 && resident.LastOpportunityFingerprint is not null, $"{resident.Id} opportunity");
            Require(resident.Memories is not null && resident.Memories.All(m => m is not null && m.Sequence >= 0 && m.Sequence <= state.Sequence && TownContent.Finite(m.WorldMinute) && m.WorldMinute <= state.WorldMinute && m.Source is not null && m.Kind is not null && m.Text is not null), $"{resident.Id} memories");
            Require(resident.PendingRequestId is null ? !state.PendingDecisions.Any(p => p.Request.ResidentId == resident.Id) : state.PendingDecisions.Any(p => p.Request.ResidentId == resident.Id && p.Request.LogicalRequestId == resident.PendingRequestId), $"{resident.Id} pending reference");
            Require(resident.CommitmentId is null || state.Commitments.Any(c => c.Id == resident.CommitmentId && c.Stage != "ended" && (c.InitiatorId == resident.Id || c.InviteeId == resident.Id)), $"{resident.Id} commitment reference");
        }
        foreach (var resource in content.Data.Resources)
            Require(state.Residents.Sum(r => (long)r.Inventory.GetValueOrDefault(resource.Id)) == content.Data.Residents.Sum(r => (long)r.Inventory.GetValueOrDefault(resource.Id)), $"resource conservation {resource.Id}");
        Require(state.Residents.Sum(r => (long)r.Coins) == content.Data.Residents.Sum(r => (long)r.Coins), "coin conservation");
        foreach (var commitment in state.Commitments)
        {
            Require(commitment.InitiatorId != commitment.InviteeId && state.Residents.Any(r => r.Id == commitment.InitiatorId) && state.Residents.Any(r => r.Id == commitment.InviteeId), $"{commitment.Id} participants");
            Require(content.Geometry.Places.Any(p => p.Id == commitment.PlaceId) && TownContent.Finite(commitment.EndsAtMinute) && commitment.EndsAtMinute >= 0, $"{commitment.Id} place/window");
            Require(new[] { "offered", "travelling", "conversation", "exchange_offered", "ended" }.Contains(commitment.Stage), $"{commitment.Id} stage");
            Require(commitment.TurnResidentId == commitment.InitiatorId || commitment.TurnResidentId == commitment.InviteeId, $"{commitment.Id} turn");
            Require(commitment.TurnNumber >= 0 && commitment.ArrivedResidentIds is not null && commitment.ArrivedResidentIds.All(id => id == commitment.InitiatorId || id == commitment.InviteeId) && commitment.Dialogue is not null && commitment.Dialogue.All(t => t is not null), $"{commitment.Id} conversation");
            Require(commitment.Stage == "ended" || (commitment.EndsAtMinute > state.WorldMinute && state.Residents.Count(r => r.CommitmentId == commitment.Id) == 2), $"{commitment.Id} active references");
            if (commitment.Stage != "ended")
            {
                var participants = state.Residents.Where(r => r.CommitmentId == commitment.Id).ToArray();
                Require(participants.All(r => content.ResidentData(r.Id).Schedule.Single(s => s.Id == r.ScheduleId).IsFree), $"{commitment.Id} schedule availability");
                Require(participants.All(r => commitment.EndsAtMinute <= Math.Floor(state.WorldMinute / 1440) * 1440 + content.ResidentData(r.Id).Schedule.Single(s => s.Id == r.ScheduleId).EndMinute), $"{commitment.Id} schedule deadline");
                if (commitment.Stage == "offered")
                    Require(participants.Single(r => r.Id == commitment.InitiatorId).Activity == "awaiting_response" && participants.Single(r => r.Id == commitment.InviteeId).Activity == "considering_invitation", $"{commitment.Id} invitation activities");
                else if (commitment.Stage == "travelling")
                    Require(participants.All(r => r.Activity is "meeting_travel" or "awaiting_partner"), $"{commitment.Id} travel activities");
                else Require(participants.All(r => r.Activity == "conversation"), $"{commitment.Id} conversation activities");
            }
            if (commitment.Stage is "conversation" or "exchange_offered")
                Require(commitment.ArrivedResidentIds!.Count == 2 && state.Residents.Where(r => r.CommitmentId == commitment.Id).All(r => r.Position.DistanceTo(content.Place(commitment.PlaceId).Position) <= 24), $"{commitment.Id} arrival");
            if (commitment.OfferResourceId is not null)
                Require(content.Data.Resources.Any(r => r.Id == commitment.OfferResourceId) && commitment.OfferQuantity > 0 && commitment.OfferQuantity <= 1000 && commitment.OfferPrice is >= 0 and <= 1000000 && (commitment.OfferFromResidentId == commitment.InitiatorId || commitment.OfferFromResidentId == commitment.InviteeId) && Id(commitment.TransferId), $"{commitment.Id} resource offer");
            if (commitment.Stage == "exchange_offered") Require(commitment.OfferResourceId is not null && !state.ExecutedTransferIds!.Contains(commitment.TransferId!), $"{commitment.Id} pending transfer");
        }
        foreach (var pending in state.PendingDecisions)
        {
            var request = pending.Request;
            Require(pending.ResponseArrivalSeconds is null, "response body has not been durably sealed");
            var resident = state.Residents.SingleOrDefault(r => r.Id == request.ResidentId);
            Require(resident is not null && Id(request.LogicalRequestId) && !state.TerminalRequestIds!.Contains(request.LogicalRequestId), "pending identity");
            Require(request.InputJson is not null && request.InputFingerprint == TownContent.Hash(request.InputJson) && request.InputJson.Length <= 64 * 1024, "pending frozen input");
            var scheduleEnd = Math.Floor(state.WorldMinute / 1440) * 1440 + content.ResidentData(resident!.Id).Schedule.Single(s => s.Id == resident.ScheduleId).EndMinute;
            Require(request.ActivityRevision == resident.ActivityRevision && TownContent.Finite(request.WindowEndMinute) && request.WindowEndMinute > state.WorldMinute && request.WindowEndMinute <= scheduleEnd, "pending activity/window");
            Require(TownContent.Finite(pending.WaitedSeconds) && pending.WaitedSeconds >= 0 && TownContent.Finite(request.RemainingWaitSeconds) && request.RemainingWaitSeconds is >= 0 and <= 60 && Math.Abs(Math.Max(0, 60 - pending.WaitedSeconds) - request.RemainingWaitSeconds) < .00001, "pending waiting budget");
            Require(request.AllowedActions is not null && request.AllowedActions.Length > 0 && request.AllowedActions.All(a => new[] { "wait", "approach", "invite", "respond", "speak", "propose_exchange", "end" }.Contains(a)) && request.AllowedTargets is not null && request.AllowedTargets.All(id => state.Residents.Any(r => r.Id == id && id != resident.Id)), "pending allowed candidates");
            Require(pending.Observation.ResidentId == request.ResidentId && TownContent.Finite(pending.Observation.WorldMinute) && pending.Observation.WorldMinute <= state.WorldMinute && content.IsWalkable(pending.Observation.Position) && pending.Observation.Residents is not null && pending.Observation.Places is not null, "pending observation");
            Require(request.AttemptId is null || Id(request.AttemptId), "pending attempt identity");
            Require(FrozenInputMatches(pending), "pending frozen input references");
            if (pending.SealedResult is not null)
                Require(pending.SealedResult.LogicalRequestId == request.LogicalRequestId && pending.SealedResult.ResidentId == request.ResidentId && (pending.SealedResult.AttemptId == request.AttemptId || (request.AttemptId is null && pending.SealedResult.AttemptId == "" && pending.SealedResult.Error == "logical_timeout")) && TownContent.Finite(pending.SealedResult.ReceivedAfterSeconds) && pending.SealedResult.ReceivedAfterSeconds >= 0, "sealed result reference");
            else Require(request.RemainingWaitSeconds > 0, "expired request without sealed timeout");
        }
        Require(state.TerminalRequestIds!.All(Id) && state.ExecutedTransferIds!.All(Id), "terminal identities");
        Require(state.Events!.Count > 0 && state.Events.Count <= state.Sequence && state.Events.Select((e, i) => e is not null && e.Sequence == state.Sequence - state.Events.Count + i + 1 && e.WorldMinute <= state.WorldMinute && TownContent.Finite(e.WorldMinute) && Id(e.BranchId) && e.Kind is not null && e.Source is not null && e.Reason is not null && e.DataJson is not null).All(valid => valid), "event suffix");
    }

    private static bool FrozenInputMatches(PendingDecisionState pending)
    {
        try
        {
            using var document = JsonDocument.Parse(pending.Request.InputJson);
            var root = document.RootElement;
            if (root.GetProperty("resident").GetProperty("id").GetString() != pending.Request.ResidentId) return false;
            var observation = TownJson.Deserialize<LocalObservation>(root.GetProperty("localObservation").GetRawText());
            var actions = root.GetProperty("allowedActions").EnumerateArray().Select(e => e.GetString()).ToArray();
            var targets = root.GetProperty("allowedTargets").EnumerateArray().Select(e => e.GetString()).ToArray();
            return TownJson.Serialize(observation) == TownJson.Serialize(pending.Observation)
                && actions.SequenceEqual(pending.Request.AllowedActions)
                && targets.SequenceEqual(pending.Request.AllowedTargets)
                && root.GetProperty("freeWindow").GetProperty("endsAtMinute").GetDouble() == pending.Request.WindowEndMinute;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException) { return false; }
    }
}
