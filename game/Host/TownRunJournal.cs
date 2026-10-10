using System.Text;
using System.Text.Json;
using System.IO.Compression;
using CozyTown.Core.Town;
using CozyTown.Model;

namespace CozyTown.Game;

public sealed class TownRunJournal : IModelJournal, IDisposable
{
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private long _recordCount;
    private bool _disposed;
    private bool _failed;
    private string? _failureCode;
    public string DirectoryPath { get; }
    public long RecordCount { get { lock (_gate) return _recordCount; } }
    public bool Failed { get { lock (_gate) return _failed; } }
    public string? FailureCode { get { lock (_gate) return _failureCode; } }
    public Func<ModelDispatch, Task>? BeforeSendOnHost { get; set; }
    private TownRecording? _recording;
    private readonly object _budgetGate = new();
    private TownReplayBudgetState? _lastBudget;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _arrivalInputs = new(StringComparer.Ordinal);
    public void SetReplayBudget(BudgetSnapshot budget, TownReplayBudgetProfile? profile = null)
    {
        lock (_budgetGate)
        {
            _lastBudget = TownJson.Deserialize<TownReplayBudgetState>(TownJson.Serialize(budget));
            _recording?.SetInitialBudget(_lastBudget, profile);
            if (_recording is not null) WriteArtifact("replay.json", _recording.Complete());
        }
    }
    public void StartReplay(TownContent content, TownWorld world, TownInputHost host, TownReplayParent? parent = null)
    {
        if (_recording is not null) throw new InvalidOperationException("Replay recording has already started.");
        _recording = new TownRecording(content, world, host, parent);
        _recording.BoundaryCommitted = trace => Append("replay_boundary", trace);
        WriteArtifact("replay.json", _recording.Complete());
    }
    public TownReplayPackage CaptureReplay() => _recording?.Complete() ?? throw new InvalidOperationException("Replay recording has not started.");
    public void SetSceneDependencies(TownSceneReplayDependencies dependencies) =>
        (_recording ?? throw new InvalidOperationException("Replay recording has not started.")).SetSceneDependencies(dependencies);
    public void RecordReplayInput(string command, object input) => _recording?.RecordExternal(command, input);

    public TownRunJournal(string root, string branchId, object manifest)
    {
        TownSaveFile.RequireBaseName(branchId);
        DirectoryPath = Path.GetFullPath(Path.Combine(root, branchId));
        if (Directory.Exists(DirectoryPath) && Directory.EnumerateFileSystemEntries(DirectoryPath).Any())
            throw new IOException("Run directory already contains records.");
        Directory.CreateDirectory(DirectoryPath);
        _stream = new FileStream(Path.Combine(DirectoryPath, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            16384, FileOptions.WriteThrough);
        try { WriteArtifact("manifest.json", manifest); }
        catch { _stream.Dispose(); throw; }
    }

    public void Append(string kind, object data)
    {
        lock (_gate)
        {
            RequireWritable();
            try
            {
                byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    sequence = _recordCount + 1, receivedAtUtc = DateTimeOffset.UtcNow, kind, data
                }, CompactJson) + "\n");
                _stream.Write(line);
                _stream.Flush(true);
                _recordCount++;
            }
            catch
            {
                throw Interrupt();
            }
        }
    }

    public async Task WriteRequestAsync(ModelDispatch dispatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Append("model_dispatched", dispatch);
        if (BeforeSendOnHost != null) await BeforeSendOnHost(dispatch);
    }

    public Task WriteCompletionAsync(ModelCompletion completion, CancellationToken cancellationToken)
    {
        RecordCompletionBudget(completion);
        Append("model_arrived", completion);
        RecordReplayInput("model_completion_persisted", completion);
        Append("model_completion_persisted", new { completion.Dispatch.AttemptId });
        return Task.CompletedTask;
    }

    public Task WriteBudgetAdmissionAsync(BudgetAdmission admission, CancellationToken cancellationToken)
    {
        lock (_budgetGate)
        {
            ObserveBudget(TownJson.Deserialize<TownReplayBudgetState>(TownJson.Serialize(admission.Before)));
            RecordReplayInput("budget_reserve", new { stage = admission.Stage, logicalRequestId = admission.LogicalRequestId,
                attemptId = admission.AttemptId, recovery = admission.Recovery, admission = admission.Admission, after = admission.After });
            _lastBudget = TownJson.Deserialize<TownReplayBudgetState>(TownJson.Serialize(admission.After));
            Append("budget_admission", admission);
        }
        return Task.CompletedTask;
    }

    private void ObserveBudget(TownReplayBudgetState observed)
    {
        if (_lastBudget is null) { SetReplayBudget(new(observed.BudgetId, observed.Currency, observed.LimitMicros, observed.SettledMicros,
            observed.ReservedMicros, observed.TotalAttempts, observed.SlotsHeld, observed.StopReason,
            observed.Attempts.Select(a => new BudgetAttempt(a.AttemptId, a.Stage, a.LogicalRequestId, a.Recovery, a.Status, a.SlotHeld,
                a.ReservedCostMicros, a.SettledCostMicros, a.PromptTokens, a.CompletionTokens, a.FailureCode)).ToArray())); return; }
        if (TownJson.Serialize(_lastBudget) == TownJson.Serialize(observed)) return;
        RecordReplayInput("budget_external", new { before = _lastBudget, after = observed });
        _lastBudget = observed;
    }

    private void RecordCompletionBudget(ModelCompletion completion)
    {
        if (completion.BudgetAfter is null || _lastBudget is null) return;
        lock (_budgetGate)
        {
            var after = TownJson.Deserialize<TownReplayBudgetState>(TownJson.Serialize(completion.BudgetAfter));
            string id = completion.Dispatch.AttemptId;
            var prior = _lastBudget.Attempts.SingleOrDefault(a => a.AttemptId == id);
            if (prior is null) throw new InvalidDataException("Completion has no recorded budget reservation.");
            var otherAttempts = after.Attempts.Select(a => a.AttemptId == id ? prior : a).ToArray();
            var before = after with { Attempts = otherAttempts, SettledMicros = otherAttempts.Sum(a => a.SettledCostMicros),
                ReservedMicros = otherAttempts.Sum(a => a.ReservedCostMicros), SlotsHeld = otherAttempts.Count(a => a.SlotHeld), StopReason = _lastBudget.StopReason };
            ObserveBudget(before);
            if (completion.BudgetSettled)
            {
                var actual = after.Attempts.Single(a => a.AttemptId == id);
                var withoutStop = after with { StopReason = _lastBudget!.StopReason };
                RecordReplayInput("budget_settle", new { attemptId = id, promptTokens = actual.PromptTokens ?? 0,
                    completionTokens = actual.CompletionTokens ?? 0, after = withoutStop });
                if (after.StopReason != withoutStop.StopReason) RecordReplayInput("budget_stop", new { reason = after.StopReason, after });
            }
            else RecordReplayInput("budget_unknown", new { attemptId = id, reason = completion.FailureCode ?? "unknown",
                generationEnded = !after.Attempts.Single(a => a.AttemptId == id).SlotHeld,
                stopFurther = after.StopReason != _lastBudget!.StopReason, after });
            _lastBudget = after;
        }
    }

    public Task WriteResponseArrivalAsync(ModelDispatch dispatch, string? responseBody, TimeSpan elapsed, CancellationToken cancellationToken)
    {
        var arrival = new { dispatch, responseBody, elapsed, redaction = responseBody is null ? "credential_echo" : null };
        Append("model_response_arrived", arrival);
        RecordReplayInput("model_response_arrived", arrival);
        _arrivalInputs[dispatch.AttemptId] = arrival;
        return Task.CompletedTask;
    }

    public void ImportRetiredCompletion(TownRunJournal source, ModelCompletion completion)
    {
        if (ReferenceEquals(source, this)) return;
        if (source._arrivalInputs.TryGetValue(completion.Dispatch.AttemptId, out var arrival)) RecordReplayInput("model_response_arrived", arrival);
        RecordCompletionBudget(completion);
        RecordReplayInput("model_completion_persisted", completion);
        RecordReplayInput("model_mailbox_enqueued", new { dispatch = completion.Dispatch });
        Append("retired_model_completion", completion);
    }

    public void WriteArtifact(string name, object data)
    {
        lock (_gate)
        {
            RequireWritable();
            TownSaveFile.RequireBaseName(name);
            if (name is "events.jsonl" or "journal-export.json") throw new InvalidDataException("Artifact name is reserved for journal records.");
            try
            {
                TownSaveFile.PublishJson(Path.Combine(DirectoryPath, name), data);
            }
            catch { throw Interrupt(); }
        }
    }

    public void ExportSnapshotZip(string path)
    {
        if (_recording is null) ExportSnapshotZipCore(path, null);
        else _recording.WithSnapshot(package => ExportSnapshotZipCore(path, package));
    }

    private void ExportSnapshotZipCore(string path, TownReplayPackage? package)
    {
        string destination = Path.GetFullPath(path);
        if (destination.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Export destination must be outside the run directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".tmp." + Guid.NewGuid().ToString("N");
        if (package is not null) WriteArtifact("replay.json", package);
        lock (_gate)
        {
            RequireWritable();
            try { _stream.Flush(true); }
            catch { throw Interrupt(); }
            try
            {
                // The same gate excludes appends and artifact replacement for the whole archive copy.
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                    {
                        foreach (string source in Directory.EnumerateFiles(DirectoryPath).Order(StringComparer.Ordinal))
                        {
                            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var entry = archive.CreateEntry(Path.GetFileName(source), CompressionLevel.Optimal).Open();
                            input.CopyTo(entry);
                        }
                        using var metadata = archive.CreateEntry("journal-export.json").Open();
                        JsonSerializer.Serialize(metadata, new { schemaVersion = 1, sourceRunDirectoryName = Path.GetFileName(DirectoryPath),
                            recordCount = _recordCount, journalByteLength = _stream.Length }, TownJson.Options);
                    }
                    output.Flush(true);
                }
                File.Move(temporary, destination);
            }
            finally { TownSaveFile.RemoveTemporary(temporary); }
        }
    }

    private void RequireWritable()
    {
        if (Failed || _disposed) throw new IOException("Run journal is interrupted or closed; world updates and model dispatch must stop.");
    }

    private IOException Interrupt()
    {
        _failed = true;
        _failureCode = "journal_write_failed";
        return new IOException("Run record write failed; world updates and model dispatch must stop.");
    }

    public void Dispose() { lock (_gate) { _disposed = true; _stream.Dispose(); } }
    private static readonly JsonSerializerOptions CompactJson = new(TownJson.Options) { WriteIndented = false };
}

// Missing runtime fields retain the first-slice save contract; full-demo saves must persist both.
public sealed record TownHostSnapshot(int SchemaVersion, string WorldJson, string SourceRunDirectoryName,
    long JournalRecordCount, double UnconsumedSeconds, string DecisionMode, string Stage,
    Dictionary<string, FrozenModelRequest> ModelRequests, TownPoint[]? PlayerRoute = null, int PlayerRouteCursor = 0,
    string BudgetProfileVersion = ModelBudgetProfile.FirstSliceVersion, string ExecutionPurpose = TownModelRuntimeProfile.Legacy);

public static class TownSaveFile
{
    public static void ValidateForRuntime(TownHostSnapshot snapshot, TownModelRuntimeProfile runtime)
    {
        ValidateHostSnapshot(snapshot);
        if (snapshot.BudgetProfileVersion != runtime.Profile.Version || snapshot.ExecutionPurpose != runtime.ExecutionPurpose)
            throw new InvalidDataException("Saved budget profile or execution purpose differs from the current runtime.");
    }

    public static void Write(string path, TownHostSnapshot snapshot, Action<TownHostSnapshot> validate)
    {
        ValidateHostSnapshot(snapshot);
        byte[] bytes = Encoding.UTF8.GetBytes(TownJson.Serialize(snapshot));
        PublishBytes(path, bytes, temporary => validate(Read(temporary)), path + ".previous");
    }

    public static TownHostSnapshot Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 32 * 1024 * 1024) throw new InvalidDataException("Save file exceeds the supported size.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true), false);
        try
        {
            var snapshot = TownJson.Deserialize<TownHostSnapshot>(reader.ReadToEnd());
            ValidateHostSnapshot(snapshot);
            return snapshot;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw new InvalidDataException("Host snapshot JSON is incomplete or invalid.", error); }
    }

    public static void ValidateHostSnapshot(TownHostSnapshot snapshot)
    {
        if (snapshot is null || snapshot.SchemaVersion != 1 || snapshot.JournalRecordCount < 0 || !double.IsFinite(snapshot.UnconsumedSeconds)
            || snapshot.UnconsumedSeconds < 0 || snapshot.UnconsumedSeconds > 1 || snapshot.ModelRequests == null
            || snapshot.DecisionMode is not ("rules" or "real") || snapshot.Stage is not ("baseline" or "counterfactual" or "economy-fixture" or "farming-capacity-fixture" or "production-capacity-fixture")
            || (snapshot.Stage is "economy-fixture" or "farming-capacity-fixture" or "production-capacity-fixture" && snapshot.DecisionMode != "rules")
            || string.IsNullOrWhiteSpace(snapshot.WorldJson))
            throw new InvalidDataException("Host snapshot fields are invalid or unsupported.");
        RequireBaseName(snapshot.SourceRunDirectoryName);
        if (snapshot.PlayerRouteCursor < 0 || snapshot.PlayerRouteCursor > (snapshot.PlayerRoute?.Length ?? 0)
            || snapshot.PlayerRoute?.Length > 32768
            || (snapshot.PlayerRoute?.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)) ?? false))
            throw new InvalidDataException("Player route coordinates or cursor are invalid.");
        TownSnapshot world;
        try { world = TownJson.Deserialize<TownSnapshot>(snapshot.WorldJson); }
        catch (JsonException error) { throw new InvalidDataException("World snapshot fields are missing or invalid.", error); }
        if (world.State is not { } state || state.PendingDecisions is null || state.PlayerDialogues is null || state.TerminalRequestIds is null || state.Residents is null
            || state.BranchId != snapshot.SourceRunDirectoryName || state.InitialStateId != snapshot.Stage)
            throw new InvalidDataException("Host snapshot branch or stage differs from its world snapshot.");
        var runtime = TownModelRuntimeProfile.Resolve(snapshot.BudgetProfileVersion, snapshot.ExecutionPurpose);
        if (state.BudgetId != runtime.BudgetId
            || runtime.Profile.Version == ModelBudgetProfile.FullDemoVersion && snapshot.Stage is not ("baseline" or "counterfactual"))
            throw new InvalidDataException("Host snapshot budget identity or scenario differs from its registered profile.");
        if (snapshot.DecisionMode == "rules" && snapshot.ModelRequests.Count != 0)
            throw new InvalidDataException("Rules-mode snapshot contains real model requests.");
        if (snapshot.DecisionMode == "real" && state.PendingDecisions.Any(p => p?.Request is not null
            && (p.Request.AttemptId is not null || p.Request.IsRecovery) && !snapshot.ModelRequests.ContainsKey(p.Request.LogicalRequestId)))
            throw new InvalidDataException("Dispatched or recovering model decision has no frozen provider request.");
        if (snapshot.DecisionMode == "real" && state.PlayerDialogues.Any(d => d?.Request is { } request
            && d.TerminalReason is null && (request.AttemptId is not null || request.IsRecovery) && !snapshot.ModelRequests.ContainsKey(request.LogicalRequestId)))
            throw new InvalidDataException("Dispatched or recovering player dialogue has no frozen provider request.");
        foreach (var pair in snapshot.ModelRequests)
        {
            var request = pair.Value;
            if (request?.Identity is not { } identity || pair.Key != identity.LogicalRequestId
                || new[] { identity.RunId, identity.SessionId, identity.LogicalRequestId, identity.ResidentId }.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 200)
                || identity.ActivityRevision < 0 || request.RemainingWait <= TimeSpan.Zero
                || request.RemainingWait > TimeSpan.FromSeconds(60) || string.IsNullOrWhiteSpace(request.InputJson)
                || Encoding.UTF8.GetByteCount(request.InputJson) > 16 * 1024 || !state.Residents.Any(r => r?.Id == identity.ResidentId))
                throw new InvalidDataException("Frozen model request identity, stage or input is invalid.");
            var pending = state.PendingDecisions.SingleOrDefault(p => p?.Request?.LogicalRequestId == pair.Key);
            var player = state.PlayerDialogues.SingleOrDefault(d => d?.Request?.LogicalRequestId == pair.Key)?.Request;
            if (identity.Kind is not ("npc_action" or "player_text")
                || identity.Kind == "npc_action" && identity.PlayerSessionId is not null
                || identity.Kind == "player_text" && player is null
                || player is not null && (identity.Kind != "player_text" || identity.PlayerSessionId != player.PlayerSessionId)
                || pending is not null && (identity.Kind != "npc_action" || identity.PlayerSessionId is not null))
                throw new InvalidDataException("Frozen request kind or player session differs from its world request.");
            if (request.Stage != runtime.NewRequestStage(snapshot.Stage, identity.Kind))
                throw new InvalidDataException("Frozen request stage differs from its saved profile, purpose or kind.");
            if (pending is null && player is null && !state.TerminalRequestIds.Contains(pair.Key))
                throw new InvalidDataException("Frozen model request has no pending or terminal world identity.");
            if (pending is not null && (pending.Request.ResidentId != identity.ResidentId || pending.Request.ActivityRevision != identity.ActivityRevision))
                throw new InvalidDataException("Frozen model request differs from its world decision.");
            if (player is not null && (player.ResidentId != identity.ResidentId || player.ActivityRevision != identity.ActivityRevision))
                throw new InvalidDataException("Frozen model request differs from its player dialogue.");
            try
            {
                using var body = TownJson.Deserialize<JsonDocument>(request.InputJson);
                var messages = body.RootElement.GetProperty("messages");
                var input = messages.EnumerateArray().Where(m => m.GetProperty("role").GetString() == "user").ToArray();
                string? expectedInput = pending?.Request.InputJson ?? player?.InputJson;
                if (input.Length != 1 || (expectedInput is not null && input[0].GetProperty("content").GetString() != expectedInput))
                    throw new InvalidDataException("Frozen provider input differs from its world decision.");
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            { throw new InvalidDataException("Frozen provider input is invalid.", error); }
        }
    }

    internal static void RequireBaseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 200
            || name.IndexOfAny(['/', '\\', ':', '<', '>', '"', '|', '?', '*']) >= 0 || name.Any(char.IsControl)
            || name.EndsWith('.') || name.EndsWith(' '))
            throw new InvalidDataException("Run and artifact names must be single file names.");
    }

    internal static void PublishBytes(string path, byte[] bytes, Action<string>? validate, string? backup = null) =>
        PublishFile(path, file => file.Write(bytes), validate, backup);

    internal static void PublishJson(string path, object data) =>
        PublishFile(path, file => JsonSerializer.Serialize(file, data, TownJson.Options), null, null);

    private static void PublishFile(string path, Action<FileStream> write, Action<string>? validate, string? backup)
    {
        string destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { write(file); file.Flush(true); }
            validate?.Invoke(temporary);
            if (File.Exists(destination)) File.Replace(temporary, destination, backup, true);
            else File.Move(temporary, destination);
        }
        finally { RemoveTemporary(temporary); }
    }

    internal static void RemoveTemporary(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
