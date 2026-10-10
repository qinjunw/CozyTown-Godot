using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Compression;

namespace CozyTown.Core.Town;

public sealed record TownSpatialQuery(string EntityId, string Kind, TownPoint From, TownPoint To, double Radius, bool Clear);
public sealed record TownTrace
{
    [JsonRequired] public string Command { get; init; } = "";
    [JsonRequired] public string InputJson { get; init; } = "";
    [JsonRequired] public TownSpatialQuery[] Queries { get; init; } = [];
    [JsonRequired] public string? CheckpointData { get; init; }
    [JsonRequired] public string? CheckpointDeltaData { get; init; }
    [JsonRequired] public int CheckpointBaseBoundary { get; init; }
    [JsonRequired] public TownReplayHostState? HostCheckpoint { get; init; }
    [JsonRequired] public string? HostCheckpointDeltaData { get; init; }
    [JsonRequired] public int HostCheckpointBaseBoundary { get; init; }
    [JsonRequired] public int Sequence { get; init; }
    [JsonRequired] public string InputFingerprint { get; init; } = "";
    [JsonIgnore] public TownState? Checkpoint
    {
        get
        {
            if (CheckpointData is null) return null;
            using var bytes = new MemoryStream(Convert.FromBase64String(CheckpointData));
            using var input = new BrotliStream(bytes, CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] buffer = new byte[16384];
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                if (output.Length + count > 64 * 1024 * 1024) throw new InvalidDataException("Replay checkpoint exceeds 64 MiB.");
                output.Write(buffer, 0, count);
            }
            return TownJson.Deserialize<TownState>(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        }
        init
        {
            CheckpointDeltaData = null; CheckpointBaseBoundary = 0;
            if (value is null) { CheckpointData = null; return; }
            using var output = new MemoryStream();
            using (var compression = new BrotliStream(output, CompressionLevel.Fastest, true))
                JsonSerializer.Serialize(compression, value, new JsonSerializerOptions(TownJson.Options) { WriteIndented = false });
            CheckpointData = Convert.ToBase64String(output.ToArray());
        }
    }
    public TownTrace() { }
    public TownTrace(string command, string inputJson, TownSpatialQuery[] queries, TownState? checkpoint,
        TownReplayHostState? hostCheckpoint = null, int sequence = 0, string inputFingerprint = "")
    {
        Command = command; InputJson = inputJson; Queries = queries; Checkpoint = checkpoint;
        HostCheckpoint = hostCheckpoint; Sequence = sequence; InputFingerprint = inputFingerprint;
    }
}

public sealed partial class TownWorld
{
    public Action<TownTrace>? TraceCommitted { get; set; }
    private TraceScope? _trace;

    private IDisposable TraceInput(string command, object input)
    {
        if (TraceCommitted is null || _trace is not null) return EmptyTrace.Instance;
        _trace = new TraceScope(this, command, TownJson.Serialize(input));
        return _trace;
    }

    private bool QueryMotion(string entityId, TownPoint from, TownPoint to)
    {
        bool clear = _movementQuery?.Invoke(entityId, from, to) ?? true;
        _trace?.Queries.Add(new(entityId, "body-motion-v1", from, to, TownContent.BodyRadius, clear));
        return clear;
    }

    private sealed class TraceScope(TownWorld world, string command, string input) : IDisposable
    {
        public List<TownSpatialQuery> Queries { get; } = [];
        public void Dispose()
        {
            world._trace = null;
            world.TraceCommitted?.Invoke(new(command, input, Queries.ToArray(), world._state));
        }
    }

    private sealed class EmptyTrace : IDisposable
    {
        public static EmptyTrace Instance { get; } = new();
        public void Dispose() { }
    }

    // Replay hydration preserves identities and performs no restore, recovery, or event publication.
    public static TownWorld OpenReplay(TownContent content, TownSnapshot snapshot, string sessionId,
        Func<string, TownPoint, TownPoint, bool>? movementQuery = null)
    {
        var copy = TownJson.Clone(snapshot);
        ValidateSnapshot(content, copy, null);
        if (string.IsNullOrWhiteSpace(sessionId)) throw new InvalidDataException("Replay session identity is required.");
        return new TownWorld(content, copy.State, movementQuery) { SessionId = sessionId };
    }
}
