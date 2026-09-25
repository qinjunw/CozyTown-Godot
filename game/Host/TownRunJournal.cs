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
        Append("model_arrived", completion);
        return Task.CompletedTask;
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
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, TownJson.Options));
                TownSaveFile.PublishBytes(Path.Combine(DirectoryPath, name), bytes, null);
            }
            catch { throw Interrupt(); }
        }
    }

    public void ExportSnapshotZip(string path)
    {
        string destination = Path.GetFullPath(path);
        if (destination.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Export destination must be outside the run directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".tmp." + Guid.NewGuid().ToString("N");
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

public sealed record TownHostSnapshot(int SchemaVersion, string WorldJson, string SourceRunDirectoryName,
    long JournalRecordCount, double UnconsumedSeconds, string DecisionMode, string Stage,
    Dictionary<string, FrozenModelRequest> ModelRequests, TownPoint[]? PlayerRoute = null, int PlayerRouteCursor = 0);

public static class TownSaveFile
{
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
            || snapshot.DecisionMode is not ("rules" or "real") || snapshot.Stage is not ("baseline" or "counterfactual" or "economy-fixture" or "farming-capacity-fixture")
            || (snapshot.Stage is "economy-fixture" or "farming-capacity-fixture" && snapshot.DecisionMode != "rules")
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
        if (world.State is not { } state || state.PendingDecisions is null || state.TerminalRequestIds is null || state.Residents is null
            || state.BranchId != snapshot.SourceRunDirectoryName || state.InitialStateId != snapshot.Stage)
            throw new InvalidDataException("Host snapshot branch or stage differs from its world snapshot.");
        if (snapshot.DecisionMode == "rules" && snapshot.ModelRequests.Count != 0)
            throw new InvalidDataException("Rules-mode snapshot contains real model requests.");
        if (snapshot.DecisionMode == "real" && state.PendingDecisions.Any(p => p?.Request is not null
            && (p.Request.AttemptId is not null || p.Request.IsRecovery) && !snapshot.ModelRequests.ContainsKey(p.Request.LogicalRequestId)))
            throw new InvalidDataException("Dispatched or recovering model decision has no frozen provider request.");
        ModelStage stage = snapshot.Stage == "baseline" ? ModelStage.Baseline : ModelStage.Counterfactual;
        foreach (var pair in snapshot.ModelRequests)
        {
            var request = pair.Value;
            if (request?.Identity is not { } identity || pair.Key != identity.LogicalRequestId
                || new[] { identity.RunId, identity.SessionId, identity.LogicalRequestId, identity.ResidentId }.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 200)
                || identity.ActivityRevision < 0 || request.Stage != stage || request.RemainingWait <= TimeSpan.Zero
                || request.RemainingWait > TimeSpan.FromSeconds(60) || string.IsNullOrWhiteSpace(request.InputJson)
                || Encoding.UTF8.GetByteCount(request.InputJson) > 16 * 1024 || !state.Residents.Any(r => r?.Id == identity.ResidentId))
                throw new InvalidDataException("Frozen model request identity, stage or input is invalid.");
            var pending = state.PendingDecisions.SingleOrDefault(p => p?.Request?.LogicalRequestId == pair.Key);
            if (pending is null && !state.TerminalRequestIds.Contains(pair.Key))
                throw new InvalidDataException("Frozen model request has no pending or terminal world identity.");
            if (pending is not null && (pending.Request.ResidentId != identity.ResidentId || pending.Request.ActivityRevision != identity.ActivityRevision))
                throw new InvalidDataException("Frozen model request differs from its world decision.");
            try
            {
                using var body = TownJson.Deserialize<JsonDocument>(request.InputJson);
                var messages = body.RootElement.GetProperty("messages");
                var input = messages.EnumerateArray().Where(m => m.GetProperty("role").GetString() == "user").ToArray();
                if (input.Length != 1 || (pending is not null && input[0].GetProperty("content").GetString() != pending.Request.InputJson))
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

    internal static void PublishBytes(string path, byte[] bytes, Action<string>? validate, string? backup = null)
    {
        string destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(bytes); file.Flush(true); }
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
