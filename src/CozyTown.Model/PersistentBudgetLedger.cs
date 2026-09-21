// Adapted from tools/experiments/CozyTown.ModelProbe at a672eb58c8b1d0e8db35272677dc17cf55c66ead.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CozyTown.Model;

public sealed class PersistentBudgetLedger
{
    public const string BudgetId = "cozytown-godot-first-slice-20260920-v1";
    public const long LimitMicros = 5_000_000;
    public const long ReserveMicros = 2_110_000;
    public const int TotalLimit = 48;
    public const int ConcurrentLimit = 2;
    private static readonly Dictionary<string, int> StageLimits = new(StringComparer.Ordinal)
        { ["probe"] = 4, ["baseline"] = 22, ["counterfactual"] = 22 };
    private readonly string _path;

    private PersistentBudgetLedger(string path)
    {
        _path = Path.GetFullPath(path);
        using var held = AcquireLock();
        _ = Read();
    }

    public static PersistentBudgetLedger OpenExisting(string path) => new(path);

    public BudgetSnapshot Snapshot()
    {
        using var held = AcquireLock();
        var ledger = Read();
        var attempts = ledger.Attempts.Select(ToSnapshot).ToArray();
        return new(BudgetId, "CNY", LimitMicros,
            attempts.Sum(a => a.SettledCostMicros), attempts.Sum(a => a.ReservedCostMicros),
            attempts.Length, attempts.Count(a => a.SlotHeld), ledger.StopReason, Array.AsReadOnly(attempts));
    }

    public BudgetAttempt Reserve(ModelStage stage, string logicalRequestId, bool recovery = false) =>
        ReserveCore(stage switch
        {
            ModelStage.Baseline => "baseline", ModelStage.Counterfactual => "counterfactual",
            _ => throw new ModelException("stage_invalid")
        }, logicalRequestId, recovery);

    private BudgetAttempt ReserveCore(string stage, string logicalRequestId, bool recovery)
    {
        using var held = AcquireLock();
        var ledger = Read();
        if (ledger.StopReason != null) throw new ModelException("ledger_stopped");
        if (!StageLimits.ContainsKey(stage) || string.IsNullOrWhiteSpace(logicalRequestId))
            throw new ModelException("attempt_identity_invalid");
        var originals = ledger.Attempts.Where(a => a.LogicalRequestId == logicalRequestId && !a.Recovery).ToArray();
        if (!recovery && originals.Length != 0) throw new ModelException("logical_request_already_sent");
        if (recovery && (originals.Length != 1 || originals[0].Stage != stage))
            throw new ModelException("recovery_original_missing_or_wrong_stage");
        if (ledger.Attempts.Count >= TotalLimit) throw new ModelException("total_limit");
        if (ledger.Attempts.Count(a => a.Stage == stage) >= StageLimits[stage])
            throw new ModelException("stage_limit");
        if (recovery && ledger.Attempts.Any(a => a.LogicalRequestId == logicalRequestId && a.Recovery))
            throw new ModelException("recovery_limit");
        if (ledger.Attempts.Count(a => a.SlotHeld) >= ConcurrentLimit)
            throw new ModelException("concurrency_limit");
        long occupied = ledger.Attempts.Sum(a => checked(a.SettledCostMicros + a.ReservedCostMicros));
        if (checked(occupied + ReserveMicros) > LimitMicros) throw new ModelException("money_limit");
        var attempt = new AttemptRecord
        {
            AttemptId = Guid.NewGuid().ToString("D"), Stage = stage, LogicalRequestId = logicalRequestId,
            Recovery = recovery, CreatedAtUtc = DateTimeOffset.UtcNow,
            ReservedCostMicros = ReserveMicros, SlotHeld = true, Status = "reserved"
        };
        ledger.Attempts.Add(attempt);
        Write(ledger);
        return ToSnapshot(attempt);
    }

    public void Settle(string attemptId, long promptTokens, long completionTokens)
    {
        long cost = CalculateCost(promptTokens, completionTokens);
        using var held = AcquireLock();
        var ledger = Read();
        var attempt = Find(ledger, attemptId);
        if (attempt.Status == "settled")
        {
            if (attempt.PromptTokens == promptTokens && attempt.CompletionTokens == completionTokens) return;
            throw new ModelException("settlement_conflict");
        }
        attempt.PromptTokens = promptTokens;
        attempt.CompletionTokens = completionTokens;
        attempt.SettledCostMicros = cost;
        attempt.ReservedCostMicros = 0;
        attempt.SlotHeld = false;
        attempt.Status = "settled";
        attempt.UpdatedAtUtc = DateTimeOffset.UtcNow;
        Write(ledger);
    }

    public void MarkUnknown(string attemptId, string reason, bool completeGeneration, bool stopFurther = false)
    {
        using var held = AcquireLock();
        var ledger = Read();
        var attempt = Find(ledger, attemptId);
        if (attempt.Status != "settled")
        {
            attempt.Status = "unknown";
            attempt.SlotHeld = attempt.SlotHeld && !completeGeneration;
            attempt.UpdatedAtUtc = DateTimeOffset.UtcNow;
            attempt.FailureCode ??= reason;
        }
        if (stopFurther) ledger.StopReason ??= reason;
        Write(ledger);
    }

    public void Stop(string reason)
    {
        using var held = AcquireLock();
        var ledger = Read();
        ledger.StopReason ??= reason;
        Write(ledger);
    }

    public static long CalculateCost(long promptTokens, long completionTokens)
    {
        if (promptTokens < 0 || promptTokens > 1_048_576 || completionTokens < 0 || completionTokens > 512)
            throw new ModelException("usage_out_of_bounds");
        return checked(promptTokens * 2 + completionTokens * 8);
    }

    private static AttemptRecord Find(LedgerDocument ledger, string id) =>
        ledger.Attempts.SingleOrDefault(a => a.AttemptId == id) ?? throw new ModelException("attempt_missing");

    private static BudgetAttempt ToSnapshot(AttemptRecord a) => new(a.AttemptId, a.Stage,
        a.LogicalRequestId, a.Recovery, a.Status, a.SlotHeld, a.ReservedCostMicros,
        a.SettledCostMicros, a.PromptTokens, a.CompletionTokens, a.FailureCode);

    private FileStream AcquireLock()
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (!File.Exists(_path)) throw new ModelException("ledger_missing");
            if (!File.Exists(_path + ".lock")) throw new ModelException("ledger_lock_missing");
            try
            {
                var held = new FileStream(_path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                using var reader = new StreamReader(held, System.Text.Encoding.UTF8, leaveOpen: true);
                if (held.Length > 1024 || reader.ReadToEnd() != BudgetId)
                {
                    held.Dispose();
                    throw new ModelException("ledger_lock_invalid");
                }
                return held;
            }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(20); }
            catch (IOException) { throw new ModelException("ledger_busy"); }
            catch (UnauthorizedAccessException) { throw new ModelException("ledger_access_failed"); }
        }
    }

    private LedgerDocument Read()
    {
        try
        {
            using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 1024 * 1024) throw new ModelException("ledger_too_large");
            using var doc = JsonDocument.Parse(input);
            JsonData.RequireUniqueProperties(doc.RootElement);
            var ledger = doc.RootElement.Deserialize<LedgerDocument>(JsonData.Options)
                ?? throw new ModelException("ledger_invalid");
            Validate(ledger);
            return ledger;
        }
        catch (ModelException) { throw; }
        catch { throw new ModelException("ledger_invalid"); }
    }

    private static void Validate(LedgerDocument ledger)
    {
        if (ledger.SchemaVersion != 1 || ledger.BudgetId != BudgetId || ledger.Currency != "CNY"
            || ledger.LimitMicros != LimitMicros || ledger.ReserveMicros != ReserveMicros
            || ledger.TotalLimit != TotalLimit || ledger.ConcurrentLimit != ConcurrentLimit
            || ledger.StageLimits == null || ledger.StageLimits.Count != StageLimits.Count
            || StageLimits.Any(kv => !ledger.StageLimits.TryGetValue(kv.Key, out int value) || value != kv.Value)
            || ledger.Attempts == null || ledger.Attempts.Count > TotalLimit)
            throw new ModelException("ledger_contract_mismatch");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in ledger.Attempts)
        {
            if (a == null || !Guid.TryParse(a.AttemptId, out _) || !ids.Add(a.AttemptId)
                || !StageLimits.ContainsKey(a.Stage) || string.IsNullOrWhiteSpace(a.LogicalRequestId)
                || a.CreatedAtUtc == default || a.SettledCostMicros < 0 || a.SettledCostMicros > ReserveMicros)
                throw new ModelException("ledger_attempt_invalid");
            if (a.Status == "settled")
            {
                if (a.SlotHeld || a.ReservedCostMicros != 0 || a.PromptTokens == null || a.CompletionTokens == null
                    || CalculateCost(a.PromptTokens.Value, a.CompletionTokens.Value) != a.SettledCostMicros)
                    throw new ModelException("ledger_settlement_invalid");
            }
            else if ((a.Status != "reserved" && a.Status != "unknown") || a.ReservedCostMicros != ReserveMicros
                || a.SettledCostMicros != 0 || a.PromptTokens != null || a.CompletionTokens != null
                || (a.Status == "reserved" && !a.SlotHeld))
                throw new ModelException("ledger_reservation_invalid");
        }
        if (StageLimits.Any(kv => ledger.Attempts.Count(a => a.Stage == kv.Key) > kv.Value)
            || ledger.Attempts.Count(a => a.SlotHeld) > ConcurrentLimit
            || ledger.Attempts.Sum(a => checked(a.SettledCostMicros + a.ReservedCostMicros)) > LimitMicros
            || ledger.Attempts.Where(a => a.Recovery).GroupBy(a => a.LogicalRequestId).Any(g => g.Count() > 1)
            || ledger.Attempts.Where(a => !a.Recovery).GroupBy(a => a.LogicalRequestId).Any(g => g.Count() > 1)
            || ledger.Attempts.Where(a => a.Recovery).Any(a => !ledger.Attempts.Any(o =>
                !o.Recovery && o.LogicalRequestId == a.LogicalRequestId && o.Stage == a.Stage)))
            throw new ModelException("ledger_limit_invalid");
    }

    private void Write(LedgerDocument ledger)
    {
        Validate(ledger);
        string temporary = _path + ".tmp." + Guid.NewGuid().ToString("N");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(ledger, JsonData.Options);
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            output.Write(bytes);
            output.Flush(true);
        }
        // Never recreate a missing live ledger or restore its older backup.
        File.Replace(temporary, _path, _path + ".previous", true);
    }
}

internal sealed class LedgerDocument
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public string BudgetId { get; set; } = PersistentBudgetLedger.BudgetId;
    [JsonRequired] public string Currency { get; set; } = "CNY";
    [JsonRequired] public long LimitMicros { get; set; } = PersistentBudgetLedger.LimitMicros;
    [JsonRequired] public long ReserveMicros { get; set; } = PersistentBudgetLedger.ReserveMicros;
    [JsonRequired] public int TotalLimit { get; set; } = PersistentBudgetLedger.TotalLimit;
    [JsonRequired] public int ConcurrentLimit { get; set; } = PersistentBudgetLedger.ConcurrentLimit;
    [JsonRequired] public Dictionary<string, int> StageLimits { get; set; } =
        new() { ["probe"] = 4, ["baseline"] = 22, ["counterfactual"] = 22 };
    public string? StopReason { get; set; }
    [JsonRequired] public List<AttemptRecord> Attempts { get; set; } = [];
}

internal sealed class AttemptRecord
{
    [JsonRequired] public string AttemptId { get; set; } = "";
    [JsonRequired] public string Stage { get; set; } = "";
    [JsonRequired] public string LogicalRequestId { get; set; } = "";
    [JsonRequired] public bool Recovery { get; set; }
    [JsonRequired] public DateTimeOffset CreatedAtUtc { get; set; }
    [JsonRequired] public string Status { get; set; } = "";
    [JsonRequired] public bool SlotHeld { get; set; }
    [JsonRequired] public long ReservedCostMicros { get; set; }
    [JsonRequired] public long SettledCostMicros { get; set; }
    public long? PromptTokens { get; set; }
    public long? CompletionTokens { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
    public string? FailureCode { get; set; }
}
