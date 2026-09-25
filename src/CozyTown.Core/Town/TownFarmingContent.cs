using System.Text;

namespace CozyTown.Core.Town;

public sealed partial class TownContent
{
    private static FarmingDefinition NormalizeFarming(TownManifest manifest, TownDefinitions definitions)
    {
        var farming = definitions.Farming ?? new FarmingDefinition([], [], null, []);
        if (farming.Crops is null || farming.Plots is null || farming.Restocks is null
            || farming.Crops.Any(c => c is null) || farming.Plots.Any(p => p is null) || farming.Restocks.Any(r => r is null)) Fail("farming fields");
        Unique(farming.Crops.Select(c => c.Id), "crops");
        Unique(farming.Plots.Select(p => p.Id), "farm plots");
        Unique(farming.Restocks.Select(r => r.ShopId), "shop restocks");
        foreach (var crop in farming.Crops)
            if (string.IsNullOrWhiteSpace(crop.DisplayName) || crop.GrowthDays is < 1 or > 365 || crop.HarvestQuantity is < 1 or > 100000
                || !definitions.Resources.Any(r => r.Id == crop.SeedResourceId) || !definitions.Resources.Any(r => r.Id == crop.HarvestResourceId)) Fail($"crop {crop.Id} conditions/resources");
        foreach (var plot in farming.Plots)
            if (string.IsNullOrWhiteSpace(plot.DisplayName) || !manifest.Places.Any(p => p.Id == plot.PlaceId)) Fail($"farm plot {plot.Id} place");
        if (farming.BedPlaceId is not null && !manifest.Places.Any(p => p.Id == farming.BedPlaceId)) Fail("bed place");
        foreach (var restock in farming.Restocks)
        {
            if (!definitions.Economy!.Shops.Any(s => s.Id == restock.ShopId) || restock.Entries is null || restock.Entries.Any(e => e is null)
                || restock.MinimumDistinctItems < 0 || restock.MinimumDistinctItems > restock.Entries.Length) Fail($"restock {restock.ShopId} fields");
            Unique(restock.Entries.Select(e => e.ResourceId), $"restock {restock.ShopId} resources");
            foreach (var entry in restock.Entries)
                if (!definitions.Resources.Any(r => r.Id == entry.ResourceId) || entry.ProbabilityPermille is < 0 or > 1000
                    || entry.MinQuantity < 1 || entry.MaxQuantity < entry.MinQuantity || entry.MaxQuantity > 100000) Fail($"restock {restock.ShopId} entry");
        }
        return farming with { Crops = farming.Crops.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray(),
            Plots = farming.Plots.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray(), Restocks = farming.Restocks.OrderBy(r => r.ShopId, StringComparer.Ordinal).ToArray() };
    }

    public Dictionary<string, int> GetRestockInventory(string shopId, long day)
    {
        var definition = Data.Farming!.Restocks.SingleOrDefault(r => r.ShopId == shopId)
            ?? throw new InvalidDataException($"Unknown shop restock: {shopId}");
        if (day is < 0 or >= 1_000_000) throw new ArgumentOutOfRangeException(nameof(day));
        uint hash = 2166136261;
        void Add(byte value) => hash = unchecked((hash ^ value) * 16777619);
        void AddInt(int value) { for (int i = 0; i < 4; i++) Add((byte)((uint)value >> (i * 8))); }
        AddInt(definition.WorldSeed);
        var id = Encoding.UTF8.GetBytes(shopId);
        AddInt(id.Length);
        foreach (byte value in id) Add(value);
        AddInt(checked((int)day + 1));
        AddInt(1);
        uint random = hash == 0 ? 0x6D2B79F5 : hash;
        int Next(int maximum)
        {
            random ^= random << 13; random ^= random >> 17; random ^= random << 5;
            return (int)(random % (uint)maximum);
        }
        var result = new Dictionary<string, int>();
        var missing = new List<(ShopRestockEntry Entry, int Quantity)>();
        foreach (var entry in definition.Entries)
        {
            bool selected = Next(1000) < entry.ProbabilityPermille;
            int quantity = entry.MinQuantity + Next(entry.MaxQuantity - entry.MinQuantity + 1);
            if (selected) result.Add(entry.ResourceId, quantity); else missing.Add((entry, quantity));
        }
        while (result.Count < definition.MinimumDistinctItems)
        {
            int index = Next(missing.Count);
            var item = missing[index]; missing.RemoveAt(index);
            result.Add(item.Entry.ResourceId, item.Quantity);
        }
        return result.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value);
    }
}
