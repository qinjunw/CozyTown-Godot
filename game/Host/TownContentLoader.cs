using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CozyTown.Core.Town;
using Godot;

namespace CozyTown.Game;

public sealed record LoadedTownContent(TownContent Content, TownManifest Manifest, TownDefinitions Definitions,
    string InputDigest, string PresentationDigest, IReadOnlyDictionary<string, string> Dependencies);

public static class TownContentLoader
{
    public static LoadedTownContent Load(Node2D map, string initialState)
    {
        if (!map.GlobalTransform.IsEqualApprox(Transform2D.Identity))
            throw new InvalidDataException("Map and host roots require an identity world transform; move tagged child nodes instead.");
        var places = new List<PlaceDefinition>();
        var obstacles = new List<ObstacleDefinition>();
        var regions = new List<ObservationRegionDefinition>();
        var roads = new List<RoadDefinition>();
        var allIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (Node node in Descendants(map))
        {
            if (!node.HasMeta("role"))
            {
                if (node is PhysicsBody2D body && (body.CollisionLayer & 1) != 0)
                    throw new InvalidDataException($"World collision body has no registered obstacle role: {node.GetPath()}.");
                continue;
            }
            string role = Metadata(node, "role");
            string id = Metadata(node, "id");
            if (string.IsNullOrWhiteSpace(id) || !allIds.Add(id))
                throw new InvalidDataException($"Scene identity is missing or duplicated: {node.GetPath()} ({id}).");
            if (node is not Node2D spatial) throw new InvalidDataException($"Spatial marker must be Node2D: {id}.");
            RequireTransform(spatial, id);
            TownPoint position = Point(map.ToLocal(spatial.GlobalPosition));
            switch (role)
            {
                case "place":
                    places.Add(new(id, Metadata(node, "display_name"), position, Metadata(node, "region_id")));
                    break;
                case "obstacle":
                    if (node is not StaticBody2D obstacle || obstacle.CollisionLayer != 1)
                        throw new InvalidDataException($"Obstacle requires StaticBody2D on collision layer 1: {id}.");
                    obstacles.Add(new(id, Bounds(map, spatial, id)));
                    break;
                case "region":
                    if (node is not Area2D) throw new InvalidDataException($"Region requires Area2D: {id}.");
                    regions.Add(new(id, Bounds(map, spatial, id)));
                    break;
                case "road":
                    if (node is not Line2D line || line.Points.Length < 2)
                        throw new InvalidDataException($"Road requires a Line2D with at least two points: {id}.");
                    roads.Add(new(id, Metadata(node, "from_id"), Metadata(node, "to_id"),
                        line.Points.Select(p => Point(map.ToLocal(line.ToGlobal(p)))).ToArray()));
                    break;
                default: throw new InvalidDataException($"Unsupported scene marker role: {id} / {role}.");
            }
        }
        var manifest = new TownManifest(map.GetMeta("width").AsDouble(), map.GetMeta("height").AsDouble(),
            places.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray(),
            obstacles.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray(),
            regions.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray(),
            roads.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray());
        var definitions = TownJson.Deserialize<TownDefinitions>(ReadText("res://Content/town.json"));
        if (initialState == "counterfactual")
        {
            var inventories = TownJson.Deserialize<Dictionary<string, Dictionary<string, int>>>(ReadText("res://Content/counterfactual.json"));
            if (!inventories.Keys.Order().SequenceEqual(definitions.Residents.Select(r => r.Id).Order()))
                throw new InvalidDataException("Counterfactual inventory identities do not match the resident definitions.");
            definitions = definitions with { Residents = definitions.Residents.Select(r => r with { Inventory = inventories[r.Id] }).ToArray() };
        }
        else if (initialState == "economy-fixture")
        {
            var inventory = TownJson.Deserialize<Dictionary<string, int>>(ReadText("res://Content/economy-fixture.json"));
            if (definitions.Economy is null) throw new InvalidDataException("Economy fixture requires an economy definition.");
            definitions = definitions with { Economy = definitions.Economy with { Player = definitions.Economy.Player with { Inventory = inventory } } };
        }
        else if (initialState == "farming-capacity-fixture")
        {
            var player = TownJson.Deserialize<PlayerDefinition>(ReadText("res://Content/farming-capacity-fixture.json"));
            if (definitions.Economy is null) throw new InvalidDataException("Farming capacity fixture requires an economy definition.");
            definitions = definitions with { Economy = definitions.Economy with { Player = player } };
        }
        else if (initialState == "production-capacity-fixture")
        {
            var player = TownJson.Deserialize<PlayerDefinition>(ReadText("res://Content/production-capacity-fixture.json"));
            if (definitions.Economy is null) throw new InvalidDataException("Production capacity fixture requires an economy definition.");
            definitions = definitions with { Economy = definitions.Economy with { Player = player } };
        }
        else if (initialState != "baseline") throw new InvalidDataException("Initial state must be baseline, counterfactual, economy-fixture, farming-capacity-fixture or production-capacity-fixture.");
        TownContent content = TownContent.Create(manifest, definitions);
        var dependencies = new SortedDictionary<string, string>(StringComparer.Ordinal);
        dependencies["res://World/TownMap.tscn#extracted-geometry-v1"] = Hash(TownJson.Serialize(manifest));
        foreach (string path in new[] { "res://Content/town.json", "res://Content/counterfactual.json", "res://Content/economy-fixture.json", "res://Content/farming-capacity-fixture.json", "res://Content/production-capacity-fixture.json", "res://Art/catalog.json" })
        {
            dependencies[path] = Convert.ToHexString(SHA256.HashData(Godot.FileAccess.GetFileAsBytes(path))).ToLowerInvariant();
        }
        using var catalog = JsonDocument.Parse(ReadText("res://Art/catalog.json"));
        foreach (JsonElement entry in catalog.RootElement.GetProperty("textures").EnumerateArray())
        {
            string path = entry.GetString() ?? throw new InvalidDataException("Art texture path is null.");
            if (!path.StartsWith("res://Art/", StringComparison.Ordinal)) throw new InvalidDataException("Art paths must be inside the content package.");
            Texture2D texture = ResourceLoader.Load<Texture2D>(path) ?? throw new InvalidDataException($"Texture is missing: {path}.");
            using Image pixels = texture.GetImage();
            pixels.Convert(Image.Format.Rgba8);
            dependencies[path + "#rgba8-pixels"] = Convert.ToHexString(SHA256.HashData(pixels.GetData())).ToLowerInvariant();
        }
        string digest = Hash(JsonSerializer.Serialize(dependencies));
        string presentation = Hash(JsonSerializer.Serialize(dependencies.Where(p => p.Key.StartsWith("res://Art/", StringComparison.Ordinal))));
        return new(content, manifest, definitions, digest, presentation, dependencies);
    }

    public static string ReadText(string path)
    {
        if (!Godot.FileAccess.FileExists(path)) throw new InvalidDataException($"Required content file is missing: {path}.");
        return Godot.FileAccess.GetFileAsString(path);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Metadata(Node node, string name)
    {
        if (!node.HasMeta(name)) throw new InvalidDataException($"Missing scene field {name}: {node.GetPath()}.");
        return node.GetMeta(name).AsString();
    }
    private static TownPoint Point(Vector2 point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) throw new InvalidDataException("Scene position must be finite.");
        return new(point.X, point.Y);
    }
    private static void RequireTransform(Node2D node, string id)
    {
        if (!node.GlobalScale.IsEqualApprox(Vector2.One) || !Mathf.IsZeroApprox(node.GlobalRotation))
            throw new InvalidDataException($"Unsupported rotation/scale on spatial marker {id}; use translation and rectangle geometry.");
    }
    private static TownRect Bounds(Node2D map, Node2D marker, string id)
    {
        CollisionShape2D[] shapes = marker.GetChildren().OfType<CollisionShape2D>().ToArray();
        if (shapes.Length != 1 || shapes[0].Disabled || shapes[0].Shape is not RectangleShape2D rectangle)
            throw new InvalidDataException($"Spatial marker {id} requires exactly one enabled RectangleShape2D.");
        RequireTransform(shapes[0], id);
        Vector2 center = map.ToLocal(shapes[0].GlobalPosition);
        return new(center.X - rectangle.Size.X / 2, center.Y - rectangle.Size.Y / 2, rectangle.Size.X, rectangle.Size.Y);
    }
    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node descendant in Descendants(child)) yield return descendant;
        }
    }
}
