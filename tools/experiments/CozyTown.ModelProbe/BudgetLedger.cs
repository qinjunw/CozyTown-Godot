using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModelProbe;

internal sealed class BudgetLedger
{
    public const string BudgetId = "cozytown-godot-first-slice-20260920-v1";
    public const long LimitMicros = 5_000_000;
    public const long ReserveMicros = 2_110_000;
    public const int TotalLimit = 48;
    public const int ConcurrentLimit = 2;
    private static readonly Dictionary<string, int> StageLimits = new(StringComparer.Ordinal)
        { ["probe"] = 4, ["baseline"] = 22, ["counterfactual"] = 22 };
    private readonly string _path;

    public BudgetLedger(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var held = AcquireLock();
        if (!File.Exists(_path))
        {
            if (held.Length != 0) throw new ProbeException("ledger_missing_existing_lock");
            byte[] marker = System.Text.Encoding.UTF8.GetBytes(BudgetId);
            held.Write(marker);
            held.Flush(true);
            Write(new LedgerDocument());
        }
        _ = Read();
    }

    public LedgerDocument Snapshot()
    {
        using var held = AcquireLock();
        return Read();
    }

    public AttemptRecord Reserve(string stage, string logicalRequestId, bool recovery = false)
    {
        using var held = AcquireLock();
        var ledger = Read();
        if (ledger.StopReason != null) throw new ProbeException("ledger_stopped");
        if (!StageLimits.ContainsKey(stage) || string.IsNullOrWhiteSpace(logicalRequestId))
            throw new ProbeException("attempt_identity_invalid");
        if (ledger.Attempts.Count >= TotalLimit) throw new ProbeException("total_limit");
        if (ledger.Attempts.Count(a => a.Stage == stage) >= StageLimits[stage])
            throw new ProbeException("stage_limit");
        if (recovery && ledger.Attempts.Any(a => a.LogicalRequestId == logicalRequestId && a.Recovery))
            throw new ProbeException("recovery_limit");
        if (ledger.Attempts.Count(a => a.SlotHeld) >= ConcurrentLimit)
            throw new ProbeException("concurrency_limit");
        long occupied = ledger.Attempts.Sum(a => checked(a.SettledCostMicros + a.ReservedCostMicros));
        if (checked(occupied + ReserveMicros) > LimitMicros) throw new ProbeException("money_limit");
        var attempt = new AttemptRecord
        {
            AttemptId = Guid.NewGuid().ToString("D"), Stage = stage, LogicalRequestId = logicalRequestId,
            Recovery = recovery, CreatedAtUtc = DateTimeOffset.UtcNow,
            ReservedCostMicros = ReserveMicros, SlotHeld = true, Status = "reserved"
        };
        ledger.Attempts.Add(attempt);
        Write(ledger);
        return attempt;
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
            throw new ProbeException("settlement_conflict");
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

    public void MarkUnknown(string attemptId, string reason, bool completeGeneration)
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
        ledger.StopReason ??= reason;
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
            throw new ProbeException("usage_out_of_bounds");
        return checked(promptTokens * 2 + completionTokens * 8);
    }

    private static AttemptRecord Find(LedgerDocument ledger, string id) =>
        ledger.Attempts.SingleOrDefault(a => a.AttemptId == id) ?? throw new ProbeException("attempt_missing");

    private FileStream AcquireLock()
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(20); }
        }
    }

    private LedgerDocument Read()
    {
        try
        {
            using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 1024 * 1024) throw new ProbeException("ledger_too_large");
            using var doc = JsonDocument.Parse(input);
            JsonData.RequireUniqueProperties(doc.RootElement);
            var ledger = doc.RootElement.Deserialize<LedgerDocument>(JsonData.Options)
                ?? throw new ProbeException("ledger_invalid");
            Validate(ledger);
            return ledger;
        }
        catch (ProbeException) { throw; }
        catch { throw new ProbeException("ledger_invalid"); }
    }

    private static void Validate(LedgerDocument ledger)
    {
        if (ledger.SchemaVersion != 1 || ledger.BudgetId != BudgetId || ledger.Currency != "CNY"
            || ledger.LimitMicros != LimitMicros || ledger.ReserveMicros != ReserveMicros
            || ledger.TotalLimit != TotalLimit || ledger.ConcurrentLimit != ConcurrentLimit
            || ledger.StageLimits == null || ledger.StageLimits.Count != StageLimits.Count
            || StageLimits.Any(kv => !ledger.StageLimits.TryGetValue(kv.Key, out int value) || value != kv.Value)
            || ledger.Attempts == null || ledger.Attempts.Count > TotalLimit)
            throw new ProbeException("ledger_contract_mismatch");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in ledger.Attempts)
        {
            if (a == null || !Guid.TryParse(a.AttemptId, out _) || !ids.Add(a.AttemptId)
                || !StageLimits.ContainsKey(a.Stage) || string.IsNullOrWhiteSpace(a.LogicalRequestId)
                || a.CreatedAtUtc == default || a.SettledCostMicros < 0 || a.SettledCostMicros > ReserveMicros)
                throw new ProbeException("ledger_attempt_invalid");
            if (a.Status == "settled")
            {
                if (a.SlotHeld || a.ReservedCostMicros != 0 || a.PromptTokens == null || a.CompletionTokens == null
                    || CalculateCost(a.PromptTokens.Value, a.CompletionTokens.Value) != a.SettledCostMicros)
                    throw new ProbeException("ledger_settlement_invalid");
            }
            else if ((a.Status != "reserved" && a.Status != "unknown") || a.ReservedCostMicros != ReserveMicros
                || a.SettledCostMicros != 0 || a.PromptTokens != null || a.CompletionTokens != null
                || (a.Status == "reserved" && !a.SlotHeld))
                throw new ProbeException("ledger_reservation_invalid");
        }
        if (StageLimits.Any(kv => ledger.Attempts.Count(a => a.Stage == kv.Key) > kv.Value)
            || ledger.Attempts.Count(a => a.SlotHeld) > ConcurrentLimit
            || ledger.Attempts.Sum(a => checked(a.SettledCostMicros + a.ReservedCostMicros)) > LimitMicros
            || ledger.Attempts.Where(a => a.Recovery).GroupBy(a => a.LogicalRequestId).Any(g => g.Count() > 1))
            throw new ProbeException("ledger_limit_invalid");
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
        if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".previous", true);
        else File.Move(temporary, _path);
    }
}

internal sealed class LedgerDocument
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public string BudgetId { get; set; } = BudgetLedger.BudgetId;
    [JsonRequired] public string Currency { get; set; } = "CNY";
    [JsonRequired] public long LimitMicros { get; set; } = BudgetLedger.LimitMicros;
    [JsonRequired] public long ReserveMicros { get; set; } = BudgetLedger.ReserveMicros;
    [JsonRequired] public int TotalLimit { get; set; } = BudgetLedger.TotalLimit;
    [JsonRequired] public int ConcurrentLimit { get; set; } = BudgetLedger.ConcurrentLimit;
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
