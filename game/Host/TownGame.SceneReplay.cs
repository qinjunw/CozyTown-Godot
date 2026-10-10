using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using CozyTown.Core.Town;
using Godot;
using File = System.IO.File;

namespace CozyTown.Game;

public partial class TownGame
{
    private bool _sceneReplayCommand;
    private int _sceneMotionQueries, _sceneBodyChecks, _sceneBodyBoundaries;
    private const string SceneDependencyVersion = "godot-town-scene-v1";
    private static string SceneFileHash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    private static string SceneAssemblyHash(Assembly assembly, string path)
    {
        using var file = File.OpenRead(path);
        using var executable = new PEReader(file, PEStreamOptions.LeaveOpen);
        var metadata = executable.GetMetadataReader();
        if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != assembly.ManifestModule.ModuleVersionId)
            throw new InvalidDataException("The assembly file differs from the loaded game module; restart the frozen build before recording.");
        file.Position = 0;
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    private TownSceneReplayDependencies SceneDependencies()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string source = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "SimulationSourceDigest").Value ?? throw new InvalidDataException("Build lacks its simulation source digest.");
        string assemblyPath = assembly.Location;
        if (string.IsNullOrEmpty(assemblyPath))
        {
            string exported = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "CozyTown.Game.dll");
            assemblyPath = File.Exists(exported) ? exported : ProjectSettings.GlobalizePath("res://.godot/mono/temp/bin/Debug/CozyTown.Game.dll");
        }
        return new(SceneDependencyVersion, _scenario, Engine.GetVersionInfo()["string"].AsString(),
            "res://Town.tscn", "res://World/TownMap.tscn", _loaded.Content.Fingerprint,
            _loaded.Dependencies["res://World/TownMap.tscn#extracted-geometry-v1"], TownContent.BodyRadius,
            TownSpatialConfiguration.Current, source.ToLowerInvariant(),
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            SceneAssemblyHash(assembly, assemblyPath),
            Engine.GetVersionInfo()["hash"].AsString(), ProjectSettings.GetSetting("physics/2d/physics_engine", "GodotPhysics2D").AsString());
    }

    private static void SceneFailure(string code, string field, object? expected, object? actual)
    {
        var error = new InvalidDataException(field);
        error.Data["code"] = code; error.Data["field"] = field;
        error.Data["expected"] = expected?.ToString(); error.Data["actual"] = actual?.ToString();
        throw error;
    }

    private void ValidateSceneDependencies(TownReplayPackage package)
    {
        var expected = package.Scene;
        if (expected is null) SceneFailure("missing_dependency", "scene", "versioned Godot scene dependencies", null);
        var actual = SceneDependencies();
        void Match(string field, object? a, object? b)
        { if (!Equals(a, b)) SceneFailure("incompatible_dependency", "scene." + field, a, b); }
        Match("version", expected!.Version, actual.Version);
        foreach (string path in new[] { expected.ScenePath, expected.MapPath })
            if (!ResourceLoader.Exists(path)) SceneFailure("missing_dependency", "scene.resource", path, "missing from loaded scene bundle");
        Match("scenePath", expected.ScenePath, actual.ScenePath); Match("mapPath", expected.MapPath, actual.MapPath);
        Match("scenario", expected.Scenario, actual.Scenario); Match("engineVersion", expected.EngineVersion, actual.EngineVersion);
        Match("engineBuild", expected.EngineBuild, actual.EngineBuild); Match("physicsBackend", expected.PhysicsBackend, actual.PhysicsBackend);
        Match("contentFingerprint", expected.ContentFingerprint, actual.ContentFingerprint);
        Match("geometryFingerprint", expected.GeometryFingerprint, actual.GeometryFingerprint);
        Match("bodyRadius", expected.BodyRadius, actual.BodyRadius); Match("query", expected.Query, actual.Query);
        Match("simulationSourceDigest", expected.SimulationSourceDigest, actual.SimulationSourceDigest);
        if (package.InitialSnapshot.ContentFingerprint != actual.ContentFingerprint)
            SceneFailure("incompatible_dependency", "scene.snapshotContent", actual.ContentFingerprint, package.InitialSnapshot.ContentFingerprint);
        foreach (var trace in package.Boundaries)
            if (trace.Queries.Any(q => q.Evidence is null))
                SceneFailure("missing_dependency", "scene.queryEvidence", "IntersectShape and CastMotion facts for every recorded query", trace.Sequence);
    }

    private TownMotionResult ReplaySceneMotion(string id, TownPoint from, TownPoint to, double radius)
    {
        if (radius != TownContent.BodyRadius) SceneFailure("divergence", "query.radius", TownContent.BodyRadius, radius);
        if (id != "player" && !_actors.ContainsKey(id)) SceneFailure("divergence", "query.entityId", "bound actor", id);
        _sceneMotionQueries++;
        return QuerySceneMotion(id, from, to);
    }

    private void ValidateReplayBodies(TownState state, int boundary)
    {
        _sceneBodyBoundaries++;
        var poses = state.Residents.Select(r => (r.Id, r.Position, r.Facing))
            .Append(("player", state.PlayerPosition, state.PlayerFacing)).ToArray();
        if (_actors.Count != state.Residents.Count || poses.Select(p => p.Item1).Distinct().Count() != poses.Length)
            SceneFailure("divergence", "bodies.identities", "one node per stable entity", "missing or duplicated binding");
        foreach (var (id, position, facing) in poses)
        {
            var node = id == "player" ? _player : _actors[id];
            if (node.ActorId != id) SceneFailure("divergence", "bodies." + id + ".identity", id, node.ActorId);
            node.SetPose(Vector(position), Vector(facing), false);
            // Nodes render on integer pixels; collision queries retain the unrounded authoritative coordinates.
            if (node.Position != Vector(position).Round())
                SceneFailure("divergence", "bodies." + id + ".displayPosition", Vector(position).Round(), node.Position);
            if (boundary == 0)
            {
                _sceneBodyChecks++;
                if (!QuerySceneMotion(id, position, position).Clear)
                    SceneFailure("divergence", "bodies." + id + ".initialOverlap", "clear", "blocked");
            }
        }
    }

    private async Task RunSceneReplayCommandAsync()
    {
        _sceneReplayCommand = true;
        _automated = true;
        if (Argument("--model-config=") is not null || _arguments.Contains("--real-experiment"))
            throw new InvalidDataException("Scene replay requires an isolated offline command without model configuration.");
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        CreateActors();
        if (_arguments.Contains("--scene-replay-fixture"))
        {
            GenerateSceneReplayFixtures(Argument("--output=") ?? throw new InvalidDataException("Fixture generation requires --output=<absolute directory>."));
            GetTree().Quit(0);
            return;
        }
        string input = Argument("--scene-replay=")!;
        TownReplayReport replay;
        TownReplayPackage? package = null;
        try
        {
            package = TownReplay.ReadPackage(input);
            if (_arguments.Contains("--scene-replay-obstruct"))
            {
                ValidateSceneDependencies(package);
                var query = package.Boundaries.SelectMany(t => t.Queries).First(q => q.From.DistanceTo(q.To) > 10);
                var blocker = new StaticBody2D { Position = Vector(query.To), CollisionLayer = 1 };
                blocker.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = Vector2.One } });
                AddChild(blocker);
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            }
            replay = TownReplay.Run(package, new(ReplaySceneMotion, ValidateReplayBodies, ValidateSceneDependencies));
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            replay = new(false, error.Data["code"]?.ToString() ?? "invalid_recording", 0,
                error.Data["field"]?.ToString() ?? "package", error.Data["expected"]?.ToString(), error.Data["actual"]?.ToString() ?? error.Message,
                TownWorld.RulesVersion, TownWorld.NumericVersion, Mode: "godot_scene_replay");
        }
        WriteReport(new
        {
            replay.Passed, replay.Code, replay.Mode, headless = _headless, test = "scene_replay",
            command = OS.GetCmdlineUserArgs(), inputSha256 = File.Exists(input) ? SceneFileHash(input) : null,
            expectedDependencies = package?.Scene, actualDependencies = SceneDependencies(),
            motionQueries = _sceneMotionQueries, initialBodyQueries = _sceneBodyChecks, bodyBoundaries = _sceneBodyBoundaries,
            intersectShapeCalls = _sceneMotionQueries + _sceneBodyChecks, castMotionCalls = _sceneMotionQueries + _sceneBodyChecks,
            bodyRepresentation = "authoritative double coordinates, circle queries, rounded Node2D presentation",
            runtimeAssemblyLocation = typeof(object).Assembly.Location, replay = replay with { FinalStateJson = null }
        });
        GetTree().Quit(replay.Passed ? 0 : replay.Code is "divergence" or "invalid_parent_prefix" ? 1 : 2);
    }
}
