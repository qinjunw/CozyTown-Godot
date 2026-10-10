using System.Text.Json;

namespace CozyTown.Core.Town;

public sealed partial class TownWorld
{
    private static void ValidatePlayerDialogues(TownContent content, TownState state)
    {
        void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid, string field) { if (!valid) throw new InvalidDataException("Invalid player dialogue snapshot: " + field); }
        bool Id(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200;
        Require(state.PlayerDialogues is not null && state.ModelQueue is not null, "collections");
        Require(state.PlayerDialogues!.All(d => d is not null && Id(d.Id) && state.Residents.Any(r => r.Id == d.ResidentId))
            && state.PlayerDialogues.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() == state.PlayerDialogues.Count, "session bindings");
        var open = state.PlayerDialogues.Where(d => d.IsOpen).ToArray();
        Require(open.Length == (state.CurrentPlayerDialogueId is null ? 0 : 1)
            && (open.Length == 0 || open[0].Id == state.CurrentPlayerDialogueId), "current session");
        var requestIds = new HashSet<string>(state.PendingDecisions.Select(p => p.Request.LogicalRequestId), StringComparer.Ordinal);
        foreach (var dialogue in state.PlayerDialogues)
        {
            Require(ValidPlayerText(dialogue.FixedText) && dialogue.Displays is not null && dialogue.Displays.Count is >= 1 and <= 2, "fixed/display text");
            Require(dialogue.Displays![0].Text == dialogue.FixedText && dialogue.Displays[0].Source == "fixed_dialogue"
                && dialogue.Displays[0].Error is null, "fixed display");
            Require(dialogue.Displays.All(d => d is not null && Id(d.Id) && ValidPlayerText(d.Text) && Id(d.Source)
                && (d.Error is null || Id(d.Error))), "display records");
            foreach (var display in dialogue.Displays)
                Require(display.MemoryCommitted == (display.MemorySequence is not null)
                    && (display.MemorySequence is not long sequence || state.Residents.Single(r => r.Id == dialogue.ResidentId).Memories
                        .Count(m => m.Sequence == sequence && m.Kind == "player_conversation" && m.Source == display.Source && m.Text == display.Text) == 1), "display memory commit evidence");
            Require(dialogue.IsOpen || dialogue.TerminalReason is not null, "closed session terminal reason");
            Require(dialogue.TerminalReason is null || Id(dialogue.TerminalReason), "terminal reason");
            Require(dialogue.ResponseArrivalSeconds is null, "response is not durably sealed");
            Require(TownContent.Finite(dialogue.WaitedSeconds) && dialogue.WaitedSeconds >= 0, "waiting time");
            if (dialogue.Request is not { } request)
            {
                Require(dialogue.SealedResult is null && dialogue.Displays.Count == 1 && dialogue.WaitedSeconds == 0, "session without generation");
                continue;
            }
            Require(requestIds.Add(request.LogicalRequestId) && Id(request.LogicalRequestId) && request.PlayerSessionId == dialogue.Id
                && request.ResidentId == dialogue.ResidentId && request.ActivityRevision >= 0
                && (request.AttemptId is null || Id(request.AttemptId)), "request binding");
            Require(TownContent.Finite(request.RemainingWaitSeconds) && request.RemainingWaitSeconds is >= 0 and <= 60
                && Math.Abs(Math.Max(0, 60 - dialogue.WaitedSeconds) - request.RemainingWaitSeconds) < .00001, "remaining waiting budget");
            Require(request.InputJson is not null && request.InputJson.Length <= 64 * 1024
                && request.InputFingerprint == TownContent.Hash(request.InputJson), "frozen input");
            try
            {
                var input = TownJson.Deserialize<PlayerDialogueContext>(request.InputJson!);
                Require(input.Protocol == "town-player-text-v1" && input.RequestKind == "player_text" && input.PlayerSessionId == dialogue.Id
                    && input.Resident is not null && input.Resident.Id == dialogue.ResidentId, "frozen identity");
                var definition = content.ResidentData(dialogue.ResidentId);
                Require(input.Resident.DisplayName == definition.DisplayName && input.Resident.Personality == definition.Personality
                    && input.Resident.DesiredResourceId == definition.DesiredResourceId && Id(input.Resident.Activity)
                    && input.Resident.Coins is >= 0 and <= 1000000 && input.Resident.Inventory is not null
                    && input.Resident.Inventory.All(p => p.Value is >= 0 and <= 100000 && content.Data.Resources.Any(r => r.Id == p.Key)), "frozen own state");
                var observation = input.LocalObservation;
                Require(observation is not null && observation.Residents is not null && observation.Places is not null, "frozen observation fields");
                Require(observation.ResidentId == dialogue.ResidentId && TownContent.Finite(observation.WorldMinute)
                    && observation.WorldMinute <= state.WorldMinute && input.WorldMinute == observation.WorldMinute
                    && content.IsWalkable(observation.Position) && observation.RegionId == content.RegionAt(observation.Position)
                    && observation.Residents.All(r => r is not null && r.Id != dialogue.ResidentId && content.Data.Residents.Any(d => d.Id == r.Id))
                    && observation.Places.All(p => p is not null && content.Geometry.Places.Contains(p)), "frozen observation");
                Require(input.Schedule is not null && definition.Schedule.Contains(input.Schedule)
                    && input.WorldMinute % 1440 >= input.Schedule.StartMinute && input.WorldMinute % 1440 < input.Schedule.EndMinute,
                    "frozen schedule");
                Require(input.Memories is not null && input.Memories.Length <= 6 && input.Memories.All(m => m is not null
                    && m.Sequence >= 0 && m.Sequence <= state.Sequence && TownContent.Finite(m.WorldMinute) && m.WorldMinute <= input.WorldMinute
                    && m.Source is not null && m.Kind is not null && m.Text is not null) && !string.IsNullOrWhiteSpace(input.Rules), "frozen memories/rules");
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            { throw new InvalidDataException("Invalid player dialogue frozen input.", error); }
            Require(state.TerminalRequestIds.Contains(request.LogicalRequestId) == (dialogue.TerminalReason is not null), "terminal request binding");
            Require(dialogue.Displays.Count == 1 || dialogue.TerminalReason is not null && dialogue.SealedResult is not null, "generated display stage");
            if (dialogue.SealedResult is { } result)
                Require(result.PlayerSessionId == dialogue.Id && result.ResidentId == dialogue.ResidentId && result.LogicalRequestId == request.LogicalRequestId
                    && Id(result.SessionId) && (result.AttemptId == request.AttemptId || request.AttemptId is null && result.AttemptId == "" && result.Error == "logical_timeout")
                    && TownContent.Finite(result.ReceivedAfterSeconds) && result.ReceivedAfterSeconds >= 0 && Id(result.Source)
                    && (result.Error is null || Id(result.Error)), "sealed response binding");
            else Require(dialogue.TerminalReason is not null || request.RemainingWaitSeconds > 0, "expired unsealed generation");
        }
        var displayIds = state.PlayerDialogues.SelectMany(d => d.Displays).Select(d => d.Id).ToArray();
        Require(displayIds.Distinct(StringComparer.Ordinal).Count() == displayIds.Length, "display identities");
        var expectedQueue = state.PendingDecisions.Select(p => p.Request.LogicalRequestId)
            .Concat(state.PlayerDialogues.Where(d => d.Request is not null && d.TerminalReason is null).Select(d => d.Request!.LogicalRequestId)).ToArray();
        Require(state.ModelQueue!.Count == expectedQueue.Length && state.ModelQueue.Distinct(StringComparer.Ordinal).Count() == state.ModelQueue.Count
            && state.ModelQueue.Order(StringComparer.Ordinal).SequenceEqual(expectedQueue.Order(StringComparer.Ordinal)), "shared dispatch queue");
    }
}
