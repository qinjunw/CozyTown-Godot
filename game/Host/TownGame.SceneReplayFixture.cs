using System.Text.Json;
using CozyTown.Core.Town;
using File = System.IO.File;

namespace CozyTown.Game;

public partial class TownGame
{
    private void GenerateSceneReplayFixtures(string output)
    {
        if (!Path.IsPathFullyQualified(output)) throw new InvalidDataException("Fixture output must be an absolute directory.");
        Directory.CreateDirectory(output);
        var content = _loaded.Content;
        var world = new TownWorld(content, _scenario) { DetailedMovementQuery = QuerySceneMotion };
        var host = new TownInputHost(content, world);
        var recording = new TownRecording(content, world, host);
        recording.SetSceneDependencies(SceneDependencies());
        var shop = content.Definitions.Economy!.Shops.First();
        var firstRoute = content.FindRoute(world.State.PlayerPosition, content.Place(shop.PlaceId).Position)!;
        foreach (var point in firstRoute.Take(3)) world.SetPlayerPosition(point);
        host.RouteTo(content.Place(shop.PlaceId).Position);
        host.Move(.2, new(0, 0));
        var saved = world.CaptureSnapshot();
        var savedHost = host.State;
        var prefix = new TownReplayParent(recording.Complete(), saved, savedHost);
        world = TownWorld.RestoreCandidate(content, TownJson.Serialize(saved), CheckMotion);
        world.DetailedMovementQuery = QuerySceneMotion;
        host = new TownInputHost(content, world, savedHost);
        recording = new TownRecording(content, world, host, prefix);
        recording.SetSceneDependencies(SceneDependencies());
        host.Move(.2, new(0, 0));
        host.Move(.05, new(1, 0));
        if (host.State.PlayerRoute is not null) throw new InvalidDataException("Keyboard movement did not cancel the restored route.");
        world.SetPlayerPosition(new(-10, -10));
        host.Pause("menu", true); host.Pause("focus", true); host.Pause("menu", false);
        world.Advance(5, .5, true); host.Pause("focus", false);
        var socialEvents = new List<TownEvent>();
        for (int step = 0; step < 180; step++)
        {
            foreach (var request in world.GetDecisionRequests())
            {
                string attempt = "scene-fixed-" + request.LogicalRequestId;
                world.MarkDispatched(request.LogicalRequestId, attempt);
                world.ReceiveResult(new(world.SessionId, request.LogicalRequestId, attempt, request.ResidentId,
                    world.CreateRuleCandidate(request), "rules", null));
            }
            world.Advance(.1, .2);
            socialEvents.AddRange(world.Events);
            world.AcknowledgeEvents(world.State.Sequence);
        }
        if (!socialEvents.Any(e => e.Kind == "conversation_started")
            || socialEvents.Where(e => e.Kind == "candidate_executed").Select(e => e.ResidentId).Distinct().Count() != 4)
            throw new InvalidDataException("Fixed scene input did not produce four resident decisions and a shared conversation.");

        long operation = 0;
        string Id() => world.State.BranchId + ":scene-fixture:" + ++operation;
        void Walk(string place)
        {
            var target = content.Place(place).Position;
            foreach (var point in content.FindRoute(world.State.PlayerPosition, target)
                ?? throw new InvalidDataException("Fixture route is unavailable: " + place))
            {
                if (!world.SetPlayerPosition(point)) throw new InvalidDataException("Actual Godot movement rejected fixture route: " + place);
                world.AcknowledgeEvents(world.State.Sequence);
            }
        }
        Walk(shop.PlaceId);
        foreach (var price in shop.Prices.Where(p => p.BuyPrice > 0 && (p.ResourceId.Contains("seed", StringComparison.Ordinal)
            || p.ResourceId.StartsWith("ingredient.", StringComparison.Ordinal) || p.ResourceId == "feed.chicken")))
            world.Trade(new(Id(), "player", shop.Id, price.ResourceId, "buy", 1, price.BuyPrice));
        var crop = content.Definitions.Farming!.Crops.First(c => world.State.Economy.Player.Inventory.GetValueOrDefault(c.SeedResourceId) > 0);
        var plot = content.Definitions.Farming.Plots.First();
        Walk(plot.PlaceId);
        if (!world.Farm(new(Id(), "player", plot.Id, "plant", crop.Id)).Succeeded) throw new InvalidDataException("Fixture planting failed.");
        world.Farm(new(Id(), "player", plot.Id, "water"));
        var hen = content.Definitions.Production!.Chickens.First();
        Walk(hen.PlaceId); world.Produce(new(Id(), "player", hen.Id, "feed"));
        var pond = content.Definitions.Production.FishingSpots.First();
        Walk(pond.PlaceId); world.Produce(new(Id(), "player", pond.Id, "fish"));
        for (int day = 0; day < crop.GrowthDays; day++)
        {
            Walk(content.Definitions.Farming.BedPlaceId!);
            world.Sleep(new(Id(), "player", 12)); world.AcknowledgeEvents(world.State.Sequence);
            world.Sleep(new(Id(), "player", 12)); world.AcknowledgeEvents(world.State.Sequence);
            if (day < crop.GrowthDays - 1) { Walk(plot.PlaceId); world.Farm(new(Id(), "player", plot.Id, "water")); }
        }
        Walk(hen.PlaceId); world.Produce(new(Id(), "player", hen.Id, "collect"));
        Walk(plot.PlaceId);
        if (!world.Farm(new(Id(), "player", plot.Id, "harvest")).Succeeded) throw new InvalidDataException("Fixture harvest failed.");
        Walk(shop.PlaceId);
        foreach (var price in shop.Prices.Where(p => p.BuyPrice > 0 && p.ResourceId.StartsWith("ingredient.", StringComparison.Ordinal)))
            world.Trade(new(Id(), "player", shop.Id, price.ResourceId, "buy", 1, price.BuyPrice));
        var kitchen = content.Definitions.Production.Kitchens.First(); Walk(kitchen.PlaceId);
        var recipe = content.Definitions.Production.Recipes.First(r => r.Ingredients.All(i => world.State.Economy.Player.Inventory.GetValueOrDefault(i.ResourceId) >= i.Quantity));
        if (!world.Produce(new(Id(), "player", kitchen.Id, "cook", recipe.Id)).Succeeded) throw new InvalidDataException("Fixture cooking failed.");

        var package = recording.Complete();
        var entries = new List<object>();
        void Write(string name, TownReplayPackage value, string sceneCode = "passed", string coreCode = "passed", string[]? arguments = null)
        {
            File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(value,
                new JsonSerializerOptions(TownJson.Options) { WriteIndented = false }));
            entries.Add(new { file = name + ".json", expectedCoreCode = coreCode, expectedSceneCode = sceneCode, sceneArguments = arguments ?? [] });
        }
        Write("gameplay", package);
        Write("missing-scene", package with { Scene = null }, "missing_dependency");
        Write("wrong-engine", package with { Scene = package.Scene! with { EngineVersion = "missing-engine" } }, "incompatible_dependency");
        Write("wrong-radius", package with { Scene = package.Scene! with { BodyRadius = 6 } }, "incompatible_dependency");
        Write("wrong-query", package with { Scene = package.Scene! with { Query = package.Scene.Query with { Mask = 2 } } }, "incompatible_dependency");
        Write("wrong-geometry", package with { Scene = package.Scene! with { GeometryFingerprint = new string('0', 64) } }, "incompatible_dependency");
        Write("wrong-source", package with { Scene = package.Scene! with { SimulationSourceDigest = new string('0', 64) } }, "incompatible_dependency");
        Write("missing-resource", package with { Scene = package.Scene! with { MapPath = "res://World/Missing.tscn" } }, "missing_dependency");
        var queryTamper = TownJson.Clone(package);
        var trace = queryTamper.Boundaries.First(t => t.Queries.Length > 0);
        trace.Queries[0] = trace.Queries[0] with { Evidence = trace.Queries[0].Evidence! with { SafeFraction = .25 } };
        Write("tampered-motion-fraction", queryTamper, "divergence");
        Write("missing-parent", package with { Parent = null }, "missing_parent_prefix", "missing_parent_prefix");
        Write("runtime-obstacle", prefix.Prefix, "divergence", arguments: ["--scene-replay-obstruct"]);
        File.WriteAllText(Path.Combine(output, "manifest.json"), TownJson.Serialize(new { packages = entries }));
        WriteReport(new { passed = true, mode = "rules", test = "scene_replay_fixture", packages = entries.Count,
            boundaries = package.BoundaryCount, parentBoundaries = prefix.Prefix.BoundaryCount,
            recordedQueries = package.Boundaries.Sum(t => t.Queries.Length) + prefix.Prefix.Boundaries.Sum(t => t.Queries.Length),
            conversationStarted = socialEvents.Count(e => e.Kind == "conversation_started"),
            residentsWithDecisions = socialEvents.Where(e => e.Kind == "candidate_executed").Select(e => e.ResidentId).Distinct().Order().ToArray(),
            dependencies = SceneDependencies() });
    }
}
