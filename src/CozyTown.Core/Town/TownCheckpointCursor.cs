using System.IO.Compression;
using System.Globalization;
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
            ApplyChanges(expected, TownCheckpointEncoding.Decode(delta!));
        }
        previous = boundary;
        return expected;
    }

    private static void ApplyChanges(JsonNode expected, JsonNode? decoded)
    {
        bool versioned = decoded is JsonObject;
        JsonArray changes;
        if (decoded is JsonArray legacy) changes = legacy;
        else if (decoded is JsonObject envelope)
        {
            Fields(envelope, "version", "changes");
            if (envelope["version"]?.GetValue<int>() != 2) throw new InvalidDataException("Unsupported checkpoint delta version.");
            changes = envelope["changes"] as JsonArray ?? throw new InvalidDataException("Checkpoint delta changes must be an array.");
        }
        else throw new InvalidDataException("Checkpoint delta must be a legacy array or versioned object.");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var parents = new HashSet<string>(StringComparer.Ordinal);
        var operations = new List<(JsonObject Change, JsonNode Owner, string Key)>();
        foreach (var item in changes)
        {
            var change = item as JsonObject ?? throw new InvalidDataException("Checkpoint change must be an object.");
            bool splice = versioned && change.ContainsKey("splice");
            Fields(change, "path", splice ? "splice" : "value");
            var parts = change["path"] as JsonArray ?? throw new InvalidDataException("Checkpoint path must be an array.");
            string[] path = parts.Select(p => p?.GetValue<string>() ?? throw new InvalidDataException("Checkpoint path components must be strings.")).ToArray();
            if (path.Length == 0) throw new InvalidDataException("Checkpoint delta cannot replace the root.");
            string identity = JsonSerializer.Serialize(path);
            if (!paths.Add(identity) || parents.Contains(identity)) throw new InvalidDataException("Checkpoint paths must not overlap or repeat.");
            for (int i = 1; i < path.Length; i++)
            {
                string parent = JsonSerializer.Serialize(path[..i]);
                if (paths.Contains(parent)) throw new InvalidDataException("Checkpoint paths must not overlap or repeat.");
                parents.Add(parent);
            }
            JsonNode owner = expected;
            for (int i = 0; i < path.Length - 1; i++)
                owner = Child(owner, path[i]) ?? throw new InvalidDataException("Checkpoint path traverses null.");
            var target = Child(owner, path[^1]);
            if (splice)
            {
                var edit = change["splice"] as JsonObject ?? throw new InvalidDataException("Checkpoint splice must be an object.");
                Fields(edit, "baseLength", "index", "removeCount", "items");
                var array = target as JsonArray ?? throw new InvalidDataException("Checkpoint splice target must be an array.");
                int index = edit["index"]!.GetValue<int>(), remove = edit["removeCount"]!.GetValue<int>();
                if (edit["baseLength"]!.GetValue<int>() != array.Count || index < 0 || index > array.Count || remove < 0 || remove > array.Count - index)
                    throw new InvalidDataException("Checkpoint splice has an invalid base length or range.");
                if (edit["items"] is not JsonArray) throw new InvalidDataException("Checkpoint splice items must be an array.");
            }
            operations.Add((change, owner, path[^1]));
        }
        // Validate the entire patch before changing the cursor's preceding checkpoint.
        foreach (var (change, owner, key) in operations)
        {
            if (change["splice"] is JsonObject edit)
            {
                var array = (JsonArray)Child(owner, key)!;
                int index = edit["index"]!.GetValue<int>(), remove = edit["removeCount"]!.GetValue<int>();
                for (int i = 0; i < remove; i++) array.RemoveAt(index);
                var items = (JsonArray)edit["items"]!;
                for (int i = 0; i < items.Count; i++) array.Insert(index + i, items[i]?.DeepClone());
            }
            else if (owner is JsonArray array) array[Index(key, array.Count)] = change["value"]?.DeepClone();
            else owner[key] = change["value"]?.DeepClone();
        }
    }

    private static JsonNode? Child(JsonNode owner, string key) => owner switch
    {
        JsonArray array => array[Index(key, array.Count)],
        JsonObject obj when obj.ContainsKey(key) => obj[key],
        _ => throw new InvalidDataException("Checkpoint path references an absent field or non-container.")
    };

    private static int Index(string value, int count)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            || index.ToString(CultureInfo.InvariantCulture) != value || index >= count)
            throw new InvalidDataException("Checkpoint path has an invalid array index.");
        return index;
    }

    private static void Fields(JsonObject value, params string[] names)
    {
        if (value.Count != names.Length || names.Any(name => !value.ContainsKey(name)))
            throw new InvalidDataException("Checkpoint operation fields are missing or unsupported.");
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
        bool hasSplice = false;
        void Visit(JsonNode? before, JsonNode? after, string[] path)
        {
            if (JsonNode.DeepEquals(before, after)) return;
            if (before is JsonObject first && after is JsonObject second && first.Count == second.Count && first.All(p => second.ContainsKey(p.Key)))
            {
                foreach (var pair in first) Visit(pair.Value, second[pair.Key], [.. path, pair.Key]);
                return;
            }
            if (before is JsonArray a && after is JsonArray b)
            {
                if (a.Count == b.Count)
                    for (int i = 0; i < a.Count; i++) Visit(a[i], b[i], [.. path, i.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                else
                {
                    int prefix = 0, suffix = 0;
                    while (prefix < Math.Min(a.Count, b.Count) && JsonNode.DeepEquals(a[prefix], b[prefix])) prefix++;
                    while (suffix < Math.Min(a.Count, b.Count) - prefix && JsonNode.DeepEquals(a[a.Count - suffix - 1], b[b.Count - suffix - 1])) suffix++;
                    var items = new JsonArray();
                    for (int i = prefix; i < b.Count - suffix; i++) items.Add(b[i]?.DeepClone());
                    changes.Add(new JsonObject { ["path"] = JsonSerializer.SerializeToNode(path), ["splice"] = new JsonObject
                    {
                        ["baseLength"] = a.Count, ["index"] = prefix, ["removeCount"] = a.Count - prefix - suffix, ["items"] = items
                    } });
                    hasSplice = true;
                }
                return;
            }
            changes.Add(new JsonObject { ["path"] = JsonSerializer.SerializeToNode(path), ["value"] = after?.DeepClone() });
        }
        Visit(previous, next, []);
        return Encode(hasSplice ? new JsonObject { ["version"] = 2, ["changes"] = changes } : changes);
    }
}
