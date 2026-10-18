using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace CozyTown.Core.Town;

public sealed record TownReplayBudgetAttempt(string AttemptId, string Stage, string LogicalRequestId,
    bool Recovery, string Status, bool SlotHeld, long ReservedCostMicros, long SettledCostMicros,
    long? PromptTokens, long? CompletionTokens, string? FailureCode);

public sealed record TownReplayBudgetState(string BudgetId, string Currency, long LimitMicros,
    long SettledMicros, long ReservedMicros, int TotalAttempts, int SlotsHeld, string? StopReason,
    IReadOnlyList<TownReplayBudgetAttempt> Attempts)
{
    public long AvailableMicros => LimitMicros - SettledMicros - ReservedMicros;
}

public sealed record TownReplayBudgetProfile(string Version, string BudgetId, string Currency, long LimitMicros,
    long ReserveMicros, int TotalLimit, int ConcurrentLimit, Dictionary<string, int> StageLimits)
{
    public static TownReplayBudgetProfile FirstSlice => new("first-slice-budget-v1", "cozytown-godot-first-slice-20260920-v1", "CNY",
        5_000_000, 2_110_000, 48, 2, new() { ["probe"] = 4, ["baseline"] = 22, ["counterfactual"] = 22 });
    public static TownReplayBudgetProfile FullDemo => new("full-demo-v1", "cozytown-godot-full-demo-20261010-v1", "CNY",
        5_000_000, 2_110_000, 96, 2, new() { ["baseline"] = 40, ["counterfactual"] = 40, ["player_dialogue"] = 8, ["manual"] = 8 });
    public static TownReplayBudgetProfile FullDemoV2 => new("full-demo-v2", "cozytown-godot-full-demo-20261011-v2", "CNY",
        5_000_000, 2_110_000, 96, 2, new() { ["baseline"] = 40, ["counterfactual"] = 40, ["player_dialogue"] = 8, ["manual"] = 8 });
    public static TownReplayBudgetProfile ManualPlay => new("manual-play-v1", "cozytown-godot-manual-play-20261017-v1", "CNY",
        5_000_000, 2_110_000, 96, 2, new() { ["manual"] = 96 });
}

public sealed class TownReplayBudget
{
    public const string ContractVersion = "first-slice-budget-v1";
    public const string BudgetId = "cozytown-godot-first-slice-20260920-v1";
    public const long LimitMicros = 5_000_000;
    public const long ReserveMicros = 2_110_000;
    public const int TotalLimit = 48;
    public const int ConcurrentLimit = 2;
    private readonly TownReplayBudgetProfile _profile;
    private TownReplayBudgetState _state;
    public TownReplayBudget(TownReplayBudgetState initialState, TownReplayBudgetProfile? profile = null)
    {
        _profile = TownJson.Clone(profile ?? TownReplayBudgetProfile.FirstSlice);
        var known = _profile.Version switch { "first-slice-budget-v1" => TownReplayBudgetProfile.FirstSlice, "full-demo-v1" => TownReplayBudgetProfile.FullDemo,
            "full-demo-v2" => TownReplayBudgetProfile.FullDemoV2,
            "manual-play-v1" => TownReplayBudgetProfile.ManualPlay,
            _ => throw new InvalidDataException("Unsupported replay budget profile: " + _profile.Version) };
        Require("budget.profile", JsonElement.DeepEquals(JsonSerializer.SerializeToElement(known, TownJson.Options), JsonSerializer.SerializeToElement(_profile, TownJson.Options)), "registered budget limits", _profile.Version);
        Validate(initialState);
        _state = TownJson.Clone(initialState);
    }
    public TownReplayBudgetState Snapshot() => TownJson.Clone(_state);

    public void Reserve(string stage, string logicalRequestId, string attemptId, bool recovery,
        string expectedAdmission, TownReplayBudgetState expectedState)
    {
        string admission = Admission(stage, logicalRequestId, recovery);
        Match("budget.admission", expectedAdmission, admission);
        if (admission != "accepted") { MatchState(expectedState, _state); return; }
        var attempt = new TownReplayBudgetAttempt(attemptId, stage, logicalRequestId, recovery,
            "reserved", true, _profile.ReserveMicros, 0, null, null, null);
        var attempts = _state.Attempts.Append(attempt).ToArray();
        var candidate = _state with { Attempts = attempts, TotalAttempts = attempts.Length,
            ReservedMicros = attempts.Sum(a => a.ReservedCostMicros), SlotsHeld = attempts.Count(a => a.SlotHeld) };
        Validate(candidate);
        MatchState(expectedState, candidate);
        _state = candidate;
    }

    public void Settle(string attemptId, long promptTokens, long completionTokens, TownReplayBudgetState expectedState)
    {
        long cost = Cost(promptTokens, completionTokens);
        var attempt = Find(attemptId);
        if (attempt.Status == "settled")
        {
            Require("budget.settlement", attempt.PromptTokens == promptTokens && attempt.CompletionTokens == completionTokens,
                "same recorded usage", "settlement_conflict");
            MatchState(expectedState, _state);
            return;
        }
        var settled = attempt with { Status = "settled", SlotHeld = false, ReservedCostMicros = 0,
            SettledCostMicros = cost, PromptTokens = promptTokens, CompletionTokens = completionTokens };
        Commit(_state.Attempts.Select(a => a.AttemptId == attemptId ? settled : a).ToArray(), _state.StopReason, expectedState);
    }

    public void MarkUnknown(string attemptId, string reason, bool generationEnded, bool stopFurther, TownReplayBudgetState expectedState)
    {
        var attempt = Find(attemptId);
        var unknown = attempt.Status == "settled" ? attempt : attempt with { Status = "unknown",
            SlotHeld = attempt.SlotHeld && !generationEnded, FailureCode = attempt.FailureCode ?? reason };
        Commit(_state.Attempts.Select(a => a.AttemptId == attemptId ? unknown : a).ToArray(),
            _state.StopReason ?? (stopFurther ? reason : null), expectedState);
    }

    public void Stop(string reason, TownReplayBudgetState expectedState) =>
        Commit(_state.Attempts, _state.StopReason ?? reason, expectedState);

    public void ApplyExternalState(TownReplayBudgetState recordedBefore, TownReplayBudgetState recordedAfter)
    {
        MatchState(recordedBefore, _state);
        Validate(recordedAfter);
        Require("budget.external.attempts.length", recordedAfter.Attempts.Count >= _state.Attempts.Count,
            "existing attempts retained", recordedAfter.Attempts.Count);
        if (_state.StopReason is not null)
        {
            Match("budget.external.stopReason", _state.StopReason, recordedAfter.StopReason);
            Match("budget.external.attempts.length", _state.Attempts.Count, recordedAfter.Attempts.Count);
        }
        for (int i = 0; i < _state.Attempts.Count; i++)
        {
            var before = _state.Attempts[i];
            var after = recordedAfter.Attempts[i];
            string field = "budget.external.attempts[" + i + "]";
            Match(field + ".attemptId", before.AttemptId, after.AttemptId);
            Match(field + ".stage", before.Stage, after.Stage);
            Match(field + ".logicalRequestId", before.LogicalRequestId, after.LogicalRequestId);
            Match(field + ".recovery", before.Recovery, after.Recovery);
            if (before.Status == "settled") Match(field, before, after);
            if (before.Status == "unknown") Require(field + ".status", after.Status != "reserved", "unknown or settled", after.Status);
            if (!before.SlotHeld) Match(field + ".slotHeld", false, after.SlotHeld);
            if (before.FailureCode is not null) Match(field + ".failureCode", before.FailureCode, after.FailureCode);
        }
        _state = TownJson.Clone(recordedAfter);
    }

    private TownReplayBudgetAttempt Find(string attemptId)
    {
        var attempt = _state.Attempts.SingleOrDefault(a => a.AttemptId == attemptId);
        Require("budget.attemptId", attempt is not null, "recorded attempt", attemptId);
        return attempt;
    }

    private void Commit(IReadOnlyList<TownReplayBudgetAttempt> attempts, string? stopReason, TownReplayBudgetState expectedState)
    {
        var candidate = _state with { Attempts = attempts, TotalAttempts = attempts.Count, StopReason = stopReason,
            SettledMicros = attempts.Sum(a => a.SettledCostMicros), ReservedMicros = attempts.Sum(a => a.ReservedCostMicros),
            SlotsHeld = attempts.Count(a => a.SlotHeld) };
        Validate(candidate);
        MatchState(expectedState, candidate);
        _state = candidate;
    }

    private string Admission(string stage, string logicalRequestId, bool recovery)
    {
        if (_state.StopReason is not null) return "ledger_stopped";
        if (stage is null || !_profile.StageLimits.ContainsKey(stage) || string.IsNullOrWhiteSpace(logicalRequestId)) return "attempt_identity_invalid";
        var originals = _state.Attempts.Where(a => a.LogicalRequestId == logicalRequestId && !a.Recovery).ToArray();
        if (!recovery && originals.Length != 0) return "logical_request_already_sent";
        if (recovery && (originals.Length != 1 || originals[0].Stage != stage)) return "recovery_original_missing_or_wrong_stage";
        if (_state.TotalAttempts >= _profile.TotalLimit) return "total_limit";
        if (_state.Attempts.Count(a => a.Stage == stage) >= _profile.StageLimits[stage]) return "stage_limit";
        if (recovery && _state.Attempts.Any(a => a.LogicalRequestId == logicalRequestId && a.Recovery)) return "recovery_limit";
        if (_state.SlotsHeld >= _profile.ConcurrentLimit) return "concurrency_limit";
        if (_state.SettledMicros + _state.ReservedMicros + _profile.ReserveMicros > _profile.LimitMicros) return "money_limit";
        return "accepted";
    }

    private void Validate(TownReplayBudgetState state)
    {
        Require("budget", state is not null, "recorded state", null);
        Match("budget.budgetId", _profile.BudgetId, state.BudgetId);
        Match("budget.currency", _profile.Currency, state.Currency);
        Match("budget.limitMicros", _profile.LimitMicros, state.LimitMicros);
        Require("budget.attempts", state.Attempts is not null, "attempt list", null);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var originals = new HashSet<string>(StringComparer.Ordinal);
        var recoveries = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < state.Attempts.Count; i++)
        {
            var attempt = state.Attempts[i];
            string field = "budget.attempts[" + i + "]";
            Require(field, attempt is not null, "attempt", null);
            Require(field + ".attemptId", Guid.TryParse(attempt.AttemptId, out _) && ids.Add(attempt.AttemptId), "unique GUID", attempt.AttemptId);
            Require(field + ".stage", attempt.Stage is not null && _profile.StageLimits.ContainsKey(attempt.Stage), string.Join(", ", _profile.StageLimits.Keys), attempt.Stage);
            Require(field + ".logicalRequestId", !string.IsNullOrWhiteSpace(attempt.LogicalRequestId), "logical request identity", attempt.LogicalRequestId);
            Require(field + ".status", attempt.Status is "reserved" or "unknown" or "settled", "reserved, unknown or settled", attempt.Status);
            if (attempt.Status == "settled")
            {
                Match(field + ".slotHeld", false, attempt.SlotHeld);
                Match(field + ".reservedCostMicros", 0L, attempt.ReservedCostMicros);
                Require(field + ".usage", attempt.PromptTokens is not null && attempt.CompletionTokens is not null, "token usage", null);
                Match(field + ".settledCostMicros", Cost(attempt.PromptTokens!.Value, attempt.CompletionTokens!.Value), attempt.SettledCostMicros);
            }
            else
            {
                if (attempt.Status == "reserved") Match(field + ".slotHeld", true, attempt.SlotHeld);
                Match(field + ".reservedCostMicros", _profile.ReserveMicros, attempt.ReservedCostMicros);
                Match(field + ".settledCostMicros", 0L, attempt.SettledCostMicros);
                Match(field + ".promptTokens", null, attempt.PromptTokens);
                Match(field + ".completionTokens", null, attempt.CompletionTokens);
            }
            var seen = attempt.Recovery ? recoveries : originals;
            Require(field + ".logicalRequestId", seen.Add(attempt.LogicalRequestId), "one original and one recovery", attempt.LogicalRequestId);
            if (attempt.Recovery)
                Require(field + ".recoveryOriginal", state.Attempts.Take(i).Any(a => !a.Recovery && a.LogicalRequestId == attempt.LogicalRequestId && a.Stage == attempt.Stage), "earlier original in same stage", attempt.LogicalRequestId);
        }
        Match("budget.totalAttempts", state.Attempts.Count, state.TotalAttempts);
        Match("budget.slotsHeld", state.Attempts.Count(a => a.SlotHeld), state.SlotsHeld);
        Match("budget.settledMicros", state.Attempts.Sum(a => a.SettledCostMicros), state.SettledMicros);
        Match("budget.reservedMicros", state.Attempts.Sum(a => a.ReservedCostMicros), state.ReservedMicros);
        Require("budget.totalAttempts", state.TotalAttempts <= _profile.TotalLimit, "at most " + _profile.TotalLimit, state.TotalAttempts);
        Require("budget.slotsHeld", state.SlotsHeld <= _profile.ConcurrentLimit, "at most " + _profile.ConcurrentLimit, state.SlotsHeld);
        Require("budget.occupiedMicros", state.SettledMicros + state.ReservedMicros <= _profile.LimitMicros, "at most " + _profile.LimitMicros, state.SettledMicros + state.ReservedMicros);
        foreach (var stage in _profile.StageLimits)
            Require("budget.stageAttempts." + stage.Key, state.Attempts.Count(a => a.Stage == stage.Key) <= stage.Value, stage.Value, state.Attempts.Count(a => a.Stage == stage.Key));
    }

    private static long Cost(long promptTokens, long completionTokens)
    {
        Require("budget.usage.promptTokens", promptTokens is >= 0 and <= 1_048_576, "0..1048576", promptTokens);
        Require("budget.usage.completionTokens", completionTokens is >= 0 and <= 512, "0..512", completionTokens);
        return promptTokens * 2 + completionTokens * 8;
    }

    private static void MatchState(TownReplayBudgetState expected, TownReplayBudgetState actual)
    {
        using var left = JsonDocument.Parse(TownJson.Serialize(expected));
        using var right = JsonDocument.Parse(TownJson.Serialize(actual));
        Compare(left.RootElement, right.RootElement, "budget");
    }

    private static void Compare(JsonElement expected, JsonElement actual, string field)
    {
        if (expected.ValueKind == JsonValueKind.Object && actual.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in expected.EnumerateObject())
                Compare(property.Value, actual.GetProperty(property.Name), field + "." + property.Name);
        }
        else if (expected.ValueKind == JsonValueKind.Array && actual.ValueKind == JsonValueKind.Array)
        {
            Match(field + ".length", expected.GetArrayLength(), actual.GetArrayLength());
            for (int i = 0; i < expected.GetArrayLength(); i++) Compare(expected[i], actual[i], field + "[" + i + "]");
        }
        else Match(field, expected.GetRawText(), actual.GetRawText());
    }

    private static void Match<T>(string field, T expected, T actual)
    {
        Require(field, EqualityComparer<T>.Default.Equals(expected, actual), expected, actual);
    }

    private static void Require(string field, [DoesNotReturnIf(false)] bool condition, object? expected, object? actual)
    {
        if (condition) return;
        var error = new InvalidDataException($"{field}: expected {expected}; actual {actual}.");
        error.Data["field"] = field; error.Data["expected"] = expected; error.Data["actual"] = actual;
        throw error;
    }
}
