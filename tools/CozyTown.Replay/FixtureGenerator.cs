using CozyTown.Core.Town;
using System.Text.Json;
using System.IO.Compression;

namespace CozyTown.Replay;

public static class FixtureGenerator
{
    public static void Generate(string contentPath, string outputDirectory)
    {
        using var source = JsonDocument.Parse(File.ReadAllText(contentPath));
        var content = TownContent.Create(TownJson.Deserialize<TownManifest>(source.RootElement.GetProperty("manifest").GetRawText()),
            TownJson.Deserialize<TownDefinitions>(source.RootElement.GetProperty("definitions").GetRawText()));
        Directory.CreateDirectory(outputDirectory);
        var world = new TownWorld(content, movementQuery: (_, _, _) => true);
        var host = new TownInputHost(content, world);
        var recording = new TownRecording(content, world, host);
        long operation = 0;
        string Id() => world.State.BranchId + ":fixture:" + ++operation;
        void Walk(string place)
        {
            var target = content.Place(place).Position;
            var route = content.FindRoute(world.State.PlayerPosition, target) ?? throw new InvalidDataException("Fixture route unavailable: " + place);
            foreach (var point in route)
            {
                if (!world.SetPlayerPosition(point)) throw new InvalidDataException("Fixture movement rejected: " + place);
                world.AcknowledgeEvents(world.State.Sequence);
            }
            if (world.State.PlayerPosition.DistanceTo(target) > .01) throw new InvalidDataException("Fixture route could not reach " + place);
        }
        host.RouteTo(new(-10, -10));
        world.SetPlayerPosition(new(-10, -10));
        host.RouteTo(content.Manifest.Places[0].Position);
        host.Move(.05, new(1, 0));
        host.Pause("menu", true); host.Pause("focus", true); host.Pause("menu", false);
        world.Advance(0, .25, true); host.Pause("focus", false);
        for (int i = 0; i < 10; i++)
        {
            foreach (var request in world.GetDecisionRequests())
            {
                string attempt = "fixed-" + Id();
                world.MarkDispatched(request.LogicalRequestId, attempt);
                world.ReceiveResult(new(world.SessionId, request.LogicalRequestId, attempt, request.ResidentId, world.CreateRuleCandidate(request), "rules", null));
            }
            world.Advance(.25, .05);
        }
        var definitions = content.Definitions;
        var shop = definitions.Economy!.Shops.First();
        Walk(shop.PlaceId);
        foreach (var price in shop.Prices.Where(p => p.BuyPrice > 0 && (p.ResourceId.Contains("seed", StringComparison.Ordinal)
            || p.ResourceId.StartsWith("ingredient.", StringComparison.Ordinal) || definitions.Production!.Chickens.Any(c => c.FeedResourceId == p.ResourceId))))
            world.Trade(new(Id(), "player", shop.Id, price.ResourceId, "buy", 1, price.BuyPrice));
        var crop = definitions.Farming!.Crops.First(c => world.State.Economy.Player.Inventory.GetValueOrDefault(c.SeedResourceId) > 0);
        var plot = definitions.Farming.Plots.First();
        Walk(plot.PlaceId);
        world.Farm(new(Id(), "player", plot.Id, "harvest"));
        if (!world.Farm(new(Id(), "player", plot.Id, "plant", crop.Id)).Succeeded) throw new InvalidDataException("Fixture planting failed.");
        world.Farm(new(Id(), "player", plot.Id, "water"));
        var hen = definitions.Production!.Chickens.First();
        Walk(hen.PlaceId); world.Produce(new(Id(), "player", hen.Id, "feed"));
        var pond = definitions.Production.FishingSpots.First();
        Walk(pond.PlaceId); world.Produce(new(Id(), "player", pond.Id, "fish"));
        for (int day = 0; day < crop.GrowthDays; day++)
        {
            Walk(definitions.Farming.BedPlaceId!);
            var sleep = new SleepRequest(Id(), "player", 12);
            world.Sleep(sleep, .123); world.AcknowledgeEvents(world.State.Sequence);
            world.Sleep(sleep, .123);
            world.Sleep(new(Id(), "player", 12)); world.AcknowledgeEvents(world.State.Sequence);
            if (day < crop.GrowthDays - 1)
            {
                Walk(plot.PlaceId); world.Farm(new(Id(), "player", plot.Id, "water"));
            }
        }
        Walk(hen.PlaceId); var collect = new ProductionRequest(Id(), "player", hen.Id, "collect");
        if (!world.Produce(collect).Succeeded) throw new InvalidDataException("Fixture egg collection failed.");
        world.Produce(collect);
        Walk(plot.PlaceId);
        if (!world.Farm(new(Id(), "player", plot.Id, "harvest")).Succeeded) throw new InvalidDataException("Fixture harvest failed.");
        Walk(shop.PlaceId);
        foreach (var price in shop.Prices.Where(p => p.BuyPrice > 0 && p.ResourceId.StartsWith("ingredient.", StringComparison.Ordinal)))
            world.Trade(new(Id(), "player", shop.Id, price.ResourceId, "buy", 1, price.BuyPrice));
        var kitchen = definitions.Production.Kitchens.First();
        Walk(kitchen.PlaceId);
        var recipe = definitions.Production.Recipes.First(r => r.Ingredients.All(i => world.State.Economy.Player.Inventory.GetValueOrDefault(i.ResourceId) >= i.Quantity));
        if (!world.Produce(new(Id(), "player", kitchen.Id, "cook", recipe.Id)).Succeeded) throw new InvalidDataException("Fixture cooking failed.");
        var package = recording.Complete();
        var report = TownReplay.Run(package);
        if (!report.Passed) throw new InvalidDataException(TownJson.Serialize(report));
        WritePackage(Path.Combine(outputDirectory, "current-gameplay.zip"), package);
        File.WriteAllText(Path.Combine(outputDirectory, "current-gameplay.report.json"), TownJson.Serialize(report with { FinalStateJson = null }));
        var tampered = TownJson.Clone(package);
        tampered.Boundaries[1] = tampered.Boundaries[1] with { InputJson = TownJson.Serialize(new { position = package.InitialSnapshot.State.PlayerPosition }) };
        var failed = TownReplay.Run(tampered);
        if (failed.Passed) throw new InvalidDataException("Tampered fixture did not diverge.");
        WritePackage(Path.Combine(outputDirectory, "tampered-command.zip"), tampered);
        File.WriteAllText(Path.Combine(outputDirectory, "tampered-command.report.json"), TownJson.Serialize(failed with { FinalStateJson = null }));
    }

    private static void WritePackage(string path, TownReplayPackage package)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        using var entry = zip.CreateEntry("replay.json", CompressionLevel.SmallestSize).Open();
        JsonSerializer.Serialize(entry, package, new JsonSerializerOptions(TownJson.Options) { WriteIndented = false });
    }
}
