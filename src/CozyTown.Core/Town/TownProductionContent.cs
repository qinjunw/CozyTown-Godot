namespace CozyTown.Core.Town;

public sealed partial class TownContent
{
    private static ProductionDefinition NormalizeProduction(TownManifest manifest, TownDefinitions definitions)
    {
        var production = definitions.Production ?? new ProductionDefinition([], [], [], [], [], 12345);
        if (production.Chickens is null || production.FishingSpots is null || production.Fish is null || production.Kitchens is null || production.Recipes is null
            || production.Chickens.Any(c => c is null) || production.FishingSpots.Any(s => s is null) || production.Fish.Any(f => f is null)
            || production.Kitchens.Any(k => k is null) || production.Recipes.Any(r => r is null)) Fail("production collections");
        Unique(production.Chickens.Select(c => c.Id).Concat(production.FishingSpots.Select(s => s.Id)).Concat(production.Kitchens.Select(k => k.Id))
            .Concat(definitions.Farming!.Plots.Select(p => p.Id)).Concat(definitions.Economy!.Shops.Select(s => s.Id)), "production interaction identities");
        Unique(production.Fish.Select(f => f.Id), "fish");
        Unique(production.Recipes.Select(r => r.Id), "recipes");
        bool Resource(string id) => definitions.Resources.Any(r => r.Id == id);
        bool Place(string id) => manifest.Places.Any(p => p.Id == id);
        foreach (var chicken in production.Chickens)
            if (string.IsNullOrWhiteSpace(chicken.DisplayName) || !Place(chicken.PlaceId) || !Resource(chicken.FeedResourceId)
                || !Resource(chicken.ProductResourceId) || chicken.ProductQuantity < 1) Fail($"chicken {chicken.Id} conditions/resources/place");
        foreach (var spot in production.FishingSpots)
            if (string.IsNullOrWhiteSpace(spot.DisplayName) || !Place(spot.PlaceId)) Fail($"fishing spot {spot.Id} place");
        if (production.FishingSeed == 0 || (production.FishingSpots.Length > 0 && production.Fish.Length == 0)) Fail("fishing seed/table");
        var fish = production.Fish.OrderBy(f => f.MinRollInclusive).ThenBy(f => f.Id, StringComparer.Ordinal).ToArray();
        int end = 0;
        foreach (var entry in fish)
        {
            if (!Resource(entry.ResourceId) || entry.MinRollInclusive < end || entry.MinRollInclusive < 0
                || entry.MaxRollExclusive <= entry.MinRollInclusive || entry.MaxRollExclusive > 100) Fail($"fish {entry.Id} range/resource");
            end = entry.MaxRollExclusive;
        }
        foreach (var kitchen in production.Kitchens)
            if (string.IsNullOrWhiteSpace(kitchen.DisplayName) || !Place(kitchen.PlaceId)) Fail($"kitchen {kitchen.Id} place");
        if (production.Kitchens.Length > 0 && production.Recipes.Length == 0) Fail($"kitchen {production.Kitchens[0].Id} requires recipes");
        foreach (var recipe in production.Recipes)
        {
            if (string.IsNullOrWhiteSpace(recipe.DisplayName) || recipe.Ingredients is null || recipe.Ingredients.Length == 0
                || recipe.Ingredients.Any(i => i is null || i.Quantity < 1 || !Resource(i.ResourceId)) || !Resource(recipe.OutputResourceId) || recipe.OutputQuantity < 1)
                Fail($"recipe {recipe.Id} ingredients/output");
            Unique(recipe.Ingredients.Select(i => i.ResourceId), $"recipe {recipe.Id} ingredients");
        }
        return production with { Chickens = production.Chickens.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray(),
            FishingSpots = production.FishingSpots.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray(), Fish = fish,
            Kitchens = production.Kitchens.OrderBy(k => k.Id, StringComparer.Ordinal).ToArray(),
            Recipes = production.Recipes.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => r with
                { Ingredients = r.Ingredients.OrderBy(i => i.ResourceId, StringComparer.Ordinal).ToArray() }).ToArray() };
    }
}
