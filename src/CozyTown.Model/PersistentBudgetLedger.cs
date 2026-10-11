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
    public ModelBudgetProfile Profile { get; }
    private readonly string _path;

    private PersistentBudgetLedger(string path, ModelBudgetProfile profile)
    {
        _path = Path.GetFullPath(path);
        Profile = profile;
        using var held = AcquireLock();
        _ = Read();
    }

    public static PersistentBudgetLedger OpenExisting(string path, string profileVersion = ModelBudgetProfile.FirstSliceVersion) =>
        new(path, ModelBudgetProfile.Get(profileVersion));

    public static PersistentBudgetLedger InitializeNew(string path, string profileVersion)
    {
        var profile = ModelBudgetProfile.Get(profileVersion);
        if (profile != ModelBudgetProfile.FullDemo && profile != ModelBudgetProfile.FullDemoV2)
            throw new ModelException("ledger_initialization_profile_invalid");
        path = Path.GetFullPath(path);
        if (File.Exists(path) || File.Exists(path + ".lock") || File.Exists(path + ".previous"))
            throw new ModelException("ledger_already_exists");
        try
        {
            // The durable marker remains after an interrupted initialization; ordinary opens never repair it.
            using var held = new FileStream(path + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                4096, FileOptions.WriteThrough);
            held.Write(System.Text.Encoding.UTF8.GetBytes(profile.BudgetId));
            held.Flush(true);
            var document = new LedgerDocument
            {
                SchemaVersion = profile.SchemaVersion, ProfileVersion = profile.Version, BudgetId = profile.BudgetId,
                LimitMicros = profile.LimitMicros, ReserveMicros = profile.ReserveMicros,
                TotalLimit = profile.TotalLimit, ConcurrentLimit = profile.ConcurrentLimit,
                StageLimits = new(profile.StageLimits, StringComparer.Ordinal)
            };
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough);
            output.Write(JsonSerializer.SerializeToUtf8Bytes(document, JsonData.Options));
            output.Flush(true);
        }
        catch (IOException) { throw new ModelException("ledger_initialization_failed"); }
        catch (UnauthorizedAccessException) { throw new ModelException("ledger_access_failed"); }
        return new(path, profile);
    }

    public BudgetSnapshot Snapshot()
    {
        using var held = AcquireLock();
        var ledger = Read();
        return ToBudgetSnapshot(ledger);
    }

    private BudgetSnapshot ToBudgetSnapshot(LedgerDocument ledger)
    {
        var attempts = ledger.Attempts.Select(ToSnapshot).ToArray();
        return new(Profile.BudgetId, "CNY", Profile.LimitMicros,
            attempts.Sum(a => a.SettledCostMicros), attempts.Sum(a => a.ReservedCostMicros),
            attempts.Length, attempts.Count(a => a.SlotHeld), ledger.StopReason, Array.AsReadOnly(attempts));
    }

    public BudgetAttempt Reserve(ModelStage stage, string logicalRequestId, bool recovery = false, Action<BudgetAdmission>? recordAdmission = null) =>
        ReserveCore(ModelBudgetProfile.StageName(stage), logicalRequestId, recovery, recordAdmission);

    private BudgetAttempt ReserveCore(string stage, string logicalRequestId, bool recovery, Action<BudgetAdmission>? recordAdmission)
    {
        using var held = AcquireLock();
        var ledger = Read();
        var before = ToBudgetSnapshot(ledger);
        void Reject(string reason)
        {
            recordAdmission?.Invoke(new(stage, logicalRequestId, recovery, null, reason, before, before));
            throw new ModelException(reason);
        }
        if (ledger.StopReason != null) Reject("ledger_stopped");
        if (!Profile.StageLimits.ContainsKey(stage) || string.IsNullOrWhiteSpace(logicalRequestId))
            Reject("attempt_identity_invalid");
        var originals = ledger.Attempts.Where(a => a.LogicalRequestId == logicalRequestId && !a.Recovery).ToArray();
        if (!recovery && originals.Length != 0) Reject("logical_request_already_sent");
        if (recovery && (originals.Length != 1 || originals[0].Stage != stage))
            Reject("recovery_original_missing_or_wrong_stage");
        if (ledger.Attempts.Count >= Profile.TotalLimit) Reject("total_limit");
        if (ledger.Attempts.Count(a => a.Stage == stage) >= Profile.StageLimits[stage])
            Reject("stage_limit");
        if (recovery && ledger.Attempts.Any(a => a.LogicalRequestId == logicalRequestId && a.Recovery))
            Reject("recovery_limit");
        if (ledger.Attempts.Count(a => a.SlotHeld) >= Profile.ConcurrentLimit)
            Reject("concurrency_limit");
        long occupied = ledger.Attempts.Sum(a => checked(a.SettledCostMicros + a.ReservedCostMicros));
        if (checked(occupied + Profile.ReserveMicros) > Profile.LimitMicros) Reject("money_limit");
        var attempt = new AttemptRecord
        {
            AttemptId = Guid.NewGuid().ToString("D"), Stage = stage, LogicalRequestId = logicalRequestId,
            Recovery = recovery, CreatedAtUtc = DateTimeOffset.UtcNow,
            ReservedCostMicros = Profile.ReserveMicros, SlotHeld = true, Status = "reserved"
        };
        ledger.Attempts.Add(attempt);
        Write(ledger);
        recordAdmission?.Invoke(new(stage, logicalRequestId, recovery, attempt.AttemptId, "accepted", before, ToBudgetSnapshot(ledger)));
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
            if (!File.Exists(_path + ".lock"))
                throw new ModelException(File.Exists(_path) ? "ledger_lock_missing" : "ledger_missing");
            try
            {
                var held = new FileStream(_path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    if (!File.Exists(_path)) throw new ModelException("ledger_missing");
                    using var reader = new StreamReader(held, System.Text.Encoding.UTF8, leaveOpen: true);
                    if (held.Length > 1024 || reader.ReadToEnd() != Profile.BudgetId)
                        throw new ModelException("ledger_lock_invalid");
                    return held;
                }
                catch { held.Dispose(); throw; }
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

    private void Validate(LedgerDocument ledger)
    {
        if (ledger.SchemaVersion != Profile.SchemaVersion || ledger.BudgetId != Profile.BudgetId
            || ledger.ProfileVersion != (Profile == ModelBudgetProfile.FirstSlice ? null : Profile.Version) || ledger.Currency != "CNY"
            || ledger.LimitMicros != Profile.LimitMicros || ledger.ReserveMicros != Profile.ReserveMicros
            || ledger.TotalLimit != Profile.TotalLimit || ledger.ConcurrentLimit != Profile.ConcurrentLimit
            || ledger.StageLimits == null || ledger.StageLimits.Count != Profile.StageLimits.Count
            || Profile.StageLimits.Any(kv => !ledger.StageLimits.TryGetValue(kv.Key, out int value) || value != kv.Value)
            || ledger.Attempts == null || ledger.Attempts.Count > Profile.TotalLimit)
            throw new ModelException("ledger_contract_mismatch");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in ledger.Attempts)
        {
            if (a == null || !Guid.TryParse(a.AttemptId, out _) || !ids.Add(a.AttemptId)
                || !Profile.StageLimits.ContainsKey(a.Stage) || string.IsNullOrWhiteSpace(a.LogicalRequestId)
                || a.CreatedAtUtc == default || a.SettledCostMicros < 0 || a.SettledCostMicros > Profile.ReserveMicros)
                throw new ModelException("ledger_attempt_invalid");
            if (a.Status == "settled")
            {
                if (a.SlotHeld || a.ReservedCostMicros != 0 || a.PromptTokens == null || a.CompletionTokens == null
                    || CalculateCost(a.PromptTokens.Value, a.CompletionTokens.Value) != a.SettledCostMicros)
                    throw new ModelException("ledger_settlement_invalid");
            }
            else if ((a.Status != "reserved" && a.Status != "unknown") || a.ReservedCostMicros != Profile.ReserveMicros
                || a.SettledCostMicros != 0 || a.PromptTokens != null || a.CompletionTokens != null
                || (a.Status == "reserved" && !a.SlotHeld))
                throw new ModelException("ledger_reservation_invalid");
        }
        if (Profile.StageLimits.Any(kv => ledger.Attempts.Count(a => a.Stage == kv.Key) > kv.Value)
            || ledger.Attempts.Count(a => a.SlotHeld) > Profile.ConcurrentLimit
            || ledger.Attempts.Sum(a => checked(a.SettledCostMicros + a.ReservedCostMicros)) > Profile.LimitMicros
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProfileVersion { get; set; }
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
