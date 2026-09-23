namespace CozyTown.Core.Town;

public sealed partial class TownContent
{
    public ShopDefinition Shop(string id) => TownJson.Clone(ShopData(id));
    internal ShopDefinition ShopData(string id) => Data.Economy!.Shops.SingleOrDefault(s => s.Id == id)
        ?? throw new InvalidDataException($"Unknown shop: {id}");

    private static EconomyDefinition NormalizeEconomy(TownManifest manifest, TownDefinitions definitions)
    {
        var economy = definitions.Economy ?? new EconomyDefinition(new PlayerDefinition(0, 24, []), []);
        if (economy.Player is null || economy.Shops is null || economy.Shops.Any(s => s is null)) Fail("economy fields");
        var player = economy.Player;
        if (player.Coins < 0 || player.SlotCapacity is < 5 or > 128 || !ValidInventory(player.Inventory, definitions.Resources)) Fail("player initial assets");
        if (RequiredSlots(player.Inventory, definitions.Resources) > player.SlotCapacity) Fail("player initial inventory exceeds capacity");
        Unique(economy.Shops.Select(s => s.Id), "shops");
        foreach (var shop in economy.Shops)
        {
            if (shop.Id == "player" || shop.Id == shop.PlaceId || definitions.Residents.Any(r => r.Id == shop.Id)
                || string.IsNullOrWhiteSpace(shop.DisplayName) || !manifest.Places.Any(p => p.Id == shop.PlaceId)) Fail($"shop {shop.Id} identity/place reference");
            if (shop.Coins < 0 || !ValidInventory(shop.Inventory, definitions.Resources) || shop.Prices is null || shop.Prices.Any(p => p is null)) Fail($"shop {shop.Id} assets/prices");
            Unique(shop.Prices.Select(p => p.ResourceId), $"shop {shop.Id} prices");
            if (shop.Prices.Any(p => !definitions.Resources.Any(r => r.Id == p.ResourceId) || p.BuyPrice < 0 || p.SellPrice < 0)) Fail($"shop {shop.Id} price reference/amount");
        }
        return new EconomyDefinition(player with { Inventory = SortInventory(player.Inventory) }, economy.Shops.OrderBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => s with { Inventory = SortInventory(s.Inventory), Prices = s.Prices.OrderBy(p => p.ResourceId, StringComparer.Ordinal).ToArray() }).ToArray());
    }

    internal static bool ValidInventory(Dictionary<string, int>? inventory, ResourceDefinition[] resources) => inventory is not null
        && inventory.All(p => p.Value >= 0 && resources.Any(r => r.Id == p.Key));
    internal static long RequiredSlots(Dictionary<string, int> inventory, ResourceDefinition[] resources) => inventory.Sum(p =>
        ((long)p.Value + resources.Single(r => r.Id == p.Key).StackLimit - 1) / resources.Single(r => r.Id == p.Key).StackLimit);
    private static Dictionary<string, int> SortInventory(Dictionary<string, int> inventory) => inventory.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value);
}
