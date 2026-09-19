using System.Text.Json;
using System.Text.Json.Serialization;

namespace CozyTown.Core;

/// <summary>Counts toolchain validation steps; these ticks do not represent world time.</summary>
public sealed class ToolchainProbe
{
    private const int SnapshotSchemaVersion = 1;
    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public long TotalTicks { get; private set; }

    public void Advance(long tickCount)
    {
        if (tickCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tickCount), "Tick count must be positive.");
        }

        TotalTicks = checked(TotalTicks + tickCount);
    }

    public string CaptureSnapshotJson() => JsonSerializer.Serialize(new Snapshot
    {
        SchemaVersion = SnapshotSchemaVersion,
        TotalTicks = TotalTicks
    }, SnapshotOptions);

    public static ToolchainProbe RestoreSnapshotJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var snapshot = JsonSerializer.Deserialize<Snapshot>(json, SnapshotOptions)
            ?? throw new JsonException("A toolchain snapshot must be a JSON object.");

        if (snapshot.SchemaVersion != SnapshotSchemaVersion)
        {
            throw new JsonException($"Unsupported toolchain snapshot schema version: {snapshot.SchemaVersion}.");
        }

        if (snapshot.TotalTicks < 0)
        {
            throw new JsonException("Snapshot totalTicks must be nonnegative.");
        }

        return new ToolchainProbe { TotalTicks = snapshot.TotalTicks };
    }

    private sealed class Snapshot
    {
        public Snapshot() { }

        [JsonRequired]
        public int SchemaVersion { get; init; }

        [JsonRequired]
        public long TotalTicks { get; init; }
    }
}
