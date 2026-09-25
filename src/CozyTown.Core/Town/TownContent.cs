using System.Security.Cryptography;
using System.Text;
using System.Diagnostics.CodeAnalysis;

namespace CozyTown.Core.Town;

public sealed partial class TownContent
{
    internal TownManifest Geometry { get; }
    internal TownDefinitions Data { get; }
    public TownManifest Manifest => TownJson.Clone(Geometry);
    public TownDefinitions Definitions => TownJson.Clone(Data);
    public string Fingerprint { get; }
    public const double BodyRadius = 5;
    public const double GridSize = 16;
    public const double MovementPerMinute = 64;
    public const double ObservationDistance = 224;

    private TownContent(TownManifest manifest, TownDefinitions definitions)
    {
        Geometry = manifest;
        Data = definitions;
        Fingerprint = Hash(TownJson.Serialize(new { manifest, definitions }));
    }

    public static TownContent Create(TownManifest manifest, TownDefinitions definitions)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(definitions);
        manifest = TownJson.Clone(manifest);
        definitions = TownJson.Clone(definitions);
        if (!Finite(manifest.Width) || !Finite(manifest.Height) || manifest.Width < 32 || manifest.Height < 32 || manifest.Width > 8192 || manifest.Height > 8192) Fail("manifest dimensions");
        if (manifest.Places is null || manifest.Obstacles is null || manifest.Regions is null || definitions.Residents is null || definitions.Resources is null) Fail("content collections");
        if (manifest.Places.Any(p => p is null) || manifest.Obstacles.Any(p => p is null) || manifest.Regions.Any(p => p is null) || (manifest.Roads?.Any(p => p is null) ?? false) || definitions.Residents.Any(r => r is null || r.Inventory is null || r.Schedule is null || r.Schedule.Any(s => s is null)) || definitions.Resources.Any(r => r is null)) Fail("null content entry");
        manifest = manifest with
        {
            Places = manifest.Places.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray(),
            Obstacles = manifest.Obstacles.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray(),
            Regions = manifest.Regions.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray(),
            Roads = (manifest.Roads ?? []).OrderBy(p => p.Id, StringComparer.Ordinal).ToArray()
        };
        definitions = definitions with
        {
            Residents = definitions.Residents.OrderBy(p => p.Id, StringComparer.Ordinal).Select(r => r with
            {
                Inventory = r.Inventory.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
                Schedule = r.Schedule.OrderBy(s => s.StartMinute).ToArray()
            }).ToArray(),
            Resources = definitions.Resources.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray()
        };
        Unique(manifest.Places.Select(p => p.Id), "places");
        Unique(manifest.Obstacles.Select(p => p.Id), "obstacles");
        Unique(manifest.Regions.Select(p => p.Id), "regions");
        Unique(manifest.Roads.Select(p => p.Id), "roads");
        Unique(definitions.Residents.Select(p => p.Id), "residents");
        Unique(definitions.Resources.Select(p => p.Id), "resources");
        foreach (var resource in definitions.Resources)
            if (string.IsNullOrWhiteSpace(resource.DisplayName) || resource.StackLimit is < 1 or > 100000 || resource.DisplayOrder < 0)
                Fail($"resource {resource.Id} inventory definition");
        definitions = definitions with { Economy = NormalizeEconomy(manifest, definitions) };
        definitions = definitions with { Farming = NormalizeFarming(manifest, definitions) };
        if (manifest.Places.Length == 0 || manifest.Regions.Length == 0 || definitions.Residents.Length == 0 || definitions.Resources.Length == 0) Fail("empty required content");
        if (definitions.StartMinute is < 0 or >= 1440) Fail("startMinute");
        foreach (var region in manifest.Regions) ValidateRect(region.Bounds, $"region {region.Id}");
        foreach (var obstacle in manifest.Obstacles) ValidateRect(obstacle.Bounds, $"obstacle {obstacle.Id}");
        var content = new TownContent(manifest, definitions);
        foreach (var place in manifest.Places)
        {
            if (!content.IsWalkable(place.Position)) Fail($"place {place.Id} is blocked or outside map");
            var region = manifest.Regions.SingleOrDefault(r => r.Id == place.RegionId);
            if (region is null || !region.Bounds.Contains(place.Position)) Fail($"place {place.Id} region reference");
        }
        foreach (var road in manifest.Roads)
        {
            if (!manifest.Places.Any(p => p.Id == road.FromPlaceId) || !manifest.Places.Any(p => p.Id == road.ToPlaceId) || road.Points is null || road.Points.Length < 2 || road.Points.Any(p => !Finite(p.X) || !Finite(p.Y))) Fail($"road {road.Id} references or geometry");
            if (road.Points[0].DistanceTo(content.Place(road.FromPlaceId).Position) > .01 || road.Points[^1].DistanceTo(content.Place(road.ToPlaceId).Position) > .01) Fail($"road {road.Id} endpoint position");
        }
        foreach (var resident in definitions.Residents)
        {
            if (string.IsNullOrWhiteSpace(resident.DisplayName) || resident.Personality is null || resident.Inventory is null || resident.Schedule is null) Fail($"resident {resident.Id} fields");
            if (!manifest.Places.Any(p => p.Id == resident.HomePlaceId) || !manifest.Places.Any(p => p.Id == resident.SpawnPlaceId)) Fail($"resident {resident.Id} place reference");
            if (!definitions.Resources.Any(r => r.Id == resident.DesiredResourceId)) Fail($"resident {resident.Id} desiredResourceId");
            if (resident.Coins < 0 || resident.Coins > 1_000_000 || resident.Inventory.Any(kv => kv.Value < 0 || kv.Value > 100_000 || !definitions.Resources.Any(r => r.Id == kv.Key))) Fail($"resident {resident.Id} initial assets");
            Unique(resident.Schedule.Select(s => s.Id), $"resident {resident.Id} schedules");
            int end = 0;
            foreach (var schedule in resident.Schedule)
            {
                if (schedule.StartMinute != end || schedule.EndMinute <= schedule.StartMinute || schedule.EndMinute > 1440 || string.IsNullOrWhiteSpace(schedule.Activity)) Fail($"resident {resident.Id} schedule coverage");
                if (!manifest.Places.Any(p => p.Id == schedule.PlaceId)) Fail($"resident {resident.Id} schedule {schedule.Id} place reference");
                if (content.FindRoute(content.Place(resident.SpawnPlaceId).Position, content.Place(schedule.PlaceId).Position) is null) Fail($"resident {resident.Id} schedule {schedule.Id} unreachable");
                end = schedule.EndMinute;
            }
            if (end != 1440) Fail($"resident {resident.Id} schedule must cover one day");
        }
        return content;
    }

    public PlaceDefinition Place(string id) => Geometry.Places.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException($"Unknown place: {id}");
    public ResidentDefinition Resident(string id) => TownJson.Clone(ResidentData(id));
    internal ResidentDefinition ResidentData(string id) => Data.Residents.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException($"Unknown resident: {id}");
    public bool IsWalkable(TownPoint point) => Finite(point.X) && Finite(point.Y) && point.X >= BodyRadius && point.Y >= BodyRadius && point.X <= Geometry.Width - BodyRadius && point.Y <= Geometry.Height - BodyRadius && !Geometry.Obstacles.Any(o => o.Bounds.Contains(point, BodyRadius));
    public bool CanMove(TownPoint from, TownPoint to)
    {
        var count = Math.Max(1, (int)Math.Ceiling(from.DistanceTo(to) / 2));
        for (int i = 0; i <= count; i++)
            if (!IsWalkable(new TownPoint(from.X + (to.X - from.X) * i / count, from.Y + (to.Y - from.Y) * i / count))) return false;
        return true;
    }

    public List<TownPoint>? FindRoute(TownPoint from, TownPoint to)
    {
        if (!IsWalkable(from) || !IsWalkable(to)) return null;
        if (CanMove(from, to)) return [to];
        int columns = (int)Math.Ceiling(Geometry.Width / GridSize), rows = (int)Math.Ceiling(Geometry.Height / GridSize);
        TownPoint Point(int n) => new((n % columns + .5) * GridSize, (n / columns + .5) * GridSize);
        var starts = Enumerable.Range(0, columns * rows).Where(n => Point(n).DistanceTo(from) <= GridSize * 2 && CanMove(from, Point(n))).OrderBy(n => Point(n).DistanceTo(from)).ToArray();
        if (starts.Length == 0) return null;
        var frontier = new PriorityQueue<int, (double, int)>();
        var costs = new Dictionary<int, double>();
        var previous = new Dictionary<int, int>();
        foreach (var start in starts) { costs[start] = Point(start).DistanceTo(from); previous[start] = -1; frontier.Enqueue(start, (costs[start] + Point(start).DistanceTo(to), start)); }
        while (frontier.TryDequeue(out var current, out _))
        {
            var position = Point(current);
            if (position.DistanceTo(to) <= GridSize * 2 && CanMove(position, to))
            {
                var route = new List<TownPoint> { to };
                for (var n = current; n >= 0; n = previous[n]) route.Add(Point(n));
                route.Reverse();
                return route;
            }
            foreach (var (dx, dy) in new[] { (0, -1), (-1, 0), (1, 0), (0, 1) })
            {
                var x = current % columns + dx; var y = current / columns + dy;
                if (x < 0 || y < 0 || x >= columns || y >= rows) continue;
                var next = y * columns + x;
                if (!CanMove(position, Point(next))) continue;
                var candidateCost = costs[current] + GridSize;
                if (costs.TryGetValue(next, out var oldCost) && oldCost <= candidateCost) continue;
                costs[next] = candidateCost; previous[next] = current;
                frontier.Enqueue(next, (candidateCost + Point(next).DistanceTo(to), next));
            }
        }
        return null;
    }

    public string RegionAt(TownPoint point) => Geometry.Regions.FirstOrDefault(r => r.Bounds.Contains(point))?.Id ?? "";
    internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Unique(IEnumerable<string> ids, string field)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids) if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || !set.Add(id)) Fail($"{field} duplicate or invalid identity: {id}");
    }
    private static void ValidateRect(TownRect rect, string field)
    {
        if (!Finite(rect.X) || !Finite(rect.Y) || !Finite(rect.Width) || !Finite(rect.Height) || rect.Width <= 0 || rect.Height <= 0) Fail(field);
    }
    [DoesNotReturn]
    private static void Fail(string field) => throw new InvalidDataException($"Invalid town content: {field}");
}
