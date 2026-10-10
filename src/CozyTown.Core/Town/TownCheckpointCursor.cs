using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CozyTown.Core.Town;

// This cursor reconstructs expected checkpoints only; the simulation never receives checkpoint patches.
public sealed class TownCheckpointCursor
{
    private JsonNode? _world, _host;
    private int _worldBoundary, _hostBoundary;

    public JsonNode? ReadWorld(TownTrace trace)
    {
        JsonNode? full = trace.CheckpointData is null ? null : TownCheckpointEncoding.Decode(trace.CheckpointData);
        return Read(full, trace.CheckpointDeltaData, trace.CheckpointBaseBoundary, trace.Sequence, ref _world, ref _worldBoundary);
    }

    public JsonNode? ReadHost(TownTrace trace) => Read(trace.HostCheckpoint is null ? null : JsonSerializer.SerializeToNode(trace.HostCheckpoint, TownJson.Options),
        trace.HostCheckpointDeltaData, trace.HostCheckpointBaseBoundary, trace.Sequence, ref _host, ref _hostBoundary);

    private static JsonNode? Read(JsonNode? full, string? delta, int basis, int boundary, ref JsonNode? expected, ref int previous)
    {
        if (full is null && delta is null) return null;
        if (full is not null)
        {
            if (delta is not null || basis != 0) throw new InvalidDataException("Full checkpoint has an unexpected delta or base boundary.");
            expected = full;
        }
        else
        {
            if (expected is null || basis != previous || basis <= 0) throw new InvalidDataException("Checkpoint delta is missing its preceding base boundary.");
            var changes = TownCheckpointEncoding.Decode(delta!)!.AsArray();
            foreach (var item in changes)
            {
                var change = item!.AsObject();
                string[] path = change["path"]!.AsArray().Select(p => p!.GetValue<string>()).ToArray();
                if (path.Length == 0) throw new InvalidDataException("Checkpoint delta cannot replace the root.");
                JsonNode? owner = expected;
                for (int i = 0; i < path.Length - 1; i++)
                    owner = owner is JsonArray array ? array[int.Parse(path[i], System.Globalization.CultureInfo.InvariantCulture)] : owner![path[i]];
                JsonNode? value = change["value"]?.DeepClone();
                string key = path[^1];
                if (owner is JsonArray target) target[int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)] = value;
                else
                {
                    if (owner is not JsonObject targetObject || !targetObject.ContainsKey(key)) throw new InvalidDataException("Checkpoint delta references an absent field.");
                    targetObject[key] = value;
                }
            }
        }
        previous = boundary;
        return expected;
    }
}

internal static class TownCheckpointEncoding
{
    internal static string Encode(JsonNode value)
    {
        using var output = new MemoryStream();
        using (var compressed = new BrotliStream(output, CompressionLevel.Fastest, true))
            JsonSerializer.Serialize(compressed, value, new JsonSerializerOptions(TownJson.Options) { WriteIndented = false });
        return Convert.ToBase64String(output.ToArray());
    }

    internal static JsonNode? Decode(string value)
    {
        using var bytes = new MemoryStream(Convert.FromBase64String(value));
        using var input = new BrotliStream(bytes, CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            if (output.Length + count > 64L * 1024 * 1024) throw new InvalidDataException("Replay checkpoint exceeds 64 MiB.");
            output.Write(buffer, 0, count);
        }
        return JsonNode.Parse(output.GetBuffer().AsSpan(0, (int)output.Length), documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false });
    }

    internal static string Difference(JsonNode previous, JsonNode next)
    {
        var changes = new JsonArray();
        void Visit(JsonNode? before, JsonNode? after, string[] path)
        {
            if (JsonNode.DeepEquals(before, after)) return;
            if (before is JsonObject first && after is JsonObject second && first.Count == second.Count && first.All(p => second.ContainsKey(p.Key)))
            {
                foreach (var pair in first) Visit(pair.Value, second[pair.Key], [.. path, pair.Key]);
                return;
            }
            if (before is JsonArray a && after is JsonArray b && a.Count == b.Count)
            {
                for (int i = 0; i < a.Count; i++) Visit(a[i], b[i], [.. path, i.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                return;
            }
            changes.Add(new JsonObject { ["path"] = JsonSerializer.SerializeToNode(path), ["value"] = after?.DeepClone() });
        }
        Visit(previous, next, []);
        return Encode(changes);
    }
}
