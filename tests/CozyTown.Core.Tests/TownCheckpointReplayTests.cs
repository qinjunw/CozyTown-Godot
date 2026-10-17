using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CozyTown.Core.Town;
using NUnit.Framework;

namespace CozyTown.Core.Tests;

[TestFixture]
public sealed class TownCheckpointReplayTests
{
    [TestCase("{\"version\":2,\"changes\":[{\"path\":[\"currentPlayerDialogueId\"]}]}")]
    [TestCase("{\"version\":2,\"changes\":[{\"path\":[\"currentPlayerDialogueId\"],\"value\":null,\"unknown\":0}]}")]
    [TestCase("{\"version\":2,\"changes\":[{\"path\":[\"currentPlayerDialogueId\"],\"value\":null},{\"path\":[\"currentPlayerDialogueId\"],\"value\":null}]}")]
    [TestCase("{\"version\":2,\"changes\":[],\"unknown\":0}")]
    [TestCase("{\"version\":2,\"changes\":[{\"path\":[\"residents\",\"00\",\"coins\"],\"value\":5}]}")]
    [TestCase("[{\"path\":[\"currentPlayerDialogueId\"]}]")]
    [TestCase("{\"version\":3,\"changes\":[]}")]
    [TestCase("{\"version\":2,\"changes\":[{\"path\":[],\"value\":null}]}")]
    [TestCase("{\"version\":2,\"changes\":[{\"path\":[\"absent\"],\"value\":null}]}")]
    [TestCase("{\"version\":2,\"changes\":[{\"path\":[\"residents\",\"-1\"],\"value\":null}]}")]
    [TestCase("{\"version\":2,\"changes\":[{\"path\":[\"events\"],\"splice\":{\"baseLength\":-1,\"index\":0,\"removeCount\":0,\"items\":[]}}]}")]
    [TestCase("{\"version\":2,\"changes\":[{\"path\":[\"events\"],\"splice\":{\"baseLength\":3,\"index\":-1,\"removeCount\":0,\"items\":[]}}]}")]
    [TestCase("{\"version\":2,\"changes\":[{\"path\":[\"events\"],\"splice\":{\"baseLength\":3,\"index\":0,\"removeCount\":999,\"items\":[]}}]}")]
    [TestCase("{\"version\":2,\"changes\":[{\"path\":[\"events\"],\"splice\":{\"baseLength\":3,\"index\":0,\"removeCount\":0}}]}")]
    [TestCase("{\"version\":2,\"changes\":[{\"path\":[\"events\"],\"value\":[],\"splice\":{\"baseLength\":3,\"index\":0,\"removeCount\":0,\"items\":[]}}]}")]
    [TestCase("{\"version\":2,\"changes\":[{\"path\":[\"sequence\"],\"value\":0},{\"path\":[\"sequence\",\"absent\"],\"value\":0}]}")]
    [TestCase("{\"version\":2,\"version\":2,\"changes\":[]}")]
    public void MalformedCheckpointOperationsAreRejectedAtTheirFirstReplayBoundary(string delta)
    {
        var package = TwoAdvances();
        package.Boundaries[1] = package.Boundaries[1] with { CheckpointDeltaData = Encode(delta) };
        var report = TownReplay.Run(package);
        Assert.That(report.Code, Is.EqualTo("invalid_recording"));
        Assert.That(report.Boundary, Is.EqualTo(2));
    }

    [Test]
    public void LegacyWholeArrayReplacementStillReplaysThroughThePackageReader()
    {
        var content = Content(); var world = new TownWorld(content); var recording = new TownRecording(content, world);
        world.Advance(0, .05, true); world.Advance(0, .05, true);
        var package = recording.Complete();
        var changes = new JsonArray(
            new JsonObject { ["path"] = new JsonArray("sequence"), ["value"] = world.State.Sequence },
            new JsonObject { ["path"] = new JsonArray("events"), ["value"] = JsonSerializer.SerializeToNode(world.State.Events, TownJson.Options) },
            new JsonObject { ["path"] = new JsonArray("currentPlayerDialogueId"), ["value"] = null });
        package.Boundaries[1] = package.Boundaries[1] with { CheckpointDeltaData = Encode(changes.ToJsonString()) };
        var report = RoundTrip(package);
        Assert.That(report.Passed, Is.True, TownJson.Serialize(report with { FinalStateJson = null }));
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(report.FinalStateJson!), JsonSerializer.SerializeToNode(world.State, TownJson.Options)), Is.True);
    }

    [Test]
    public void ASplicedEventTamperStopsAtTheFirstAffectedBoundary()
    {
        var package = TwoAdvances();
        var patch = Decode(package.Boundaries[1].CheckpointDeltaData!);
        var eventEdit = patch["changes"]!.AsArray().Single(c => c!["path"]!.ToJsonString() == "[\"events\"]")!;
        eventEdit["splice"]!["items"]![0]!["reason"] = "invented-checkpoint";
        package.Boundaries[1] = package.Boundaries[1] with { CheckpointDeltaData = Encode(patch.ToJsonString()) };
        var report = RoundTrip(package);
        Assert.That(report.Code, Is.EqualTo("divergence"));
        Assert.That(report.Boundary, Is.EqualTo(2));
        Assert.That(report.Field, Does.EndWith(".reason"));
        Assert.That(report.Command, Is.EqualTo("advance"));
    }

    [Test]
    public void AppendAndTrimCheckpointsSurviveSaveRestoreWithTheirExactParentPrefix()
    {
        var content = Content(); var world = new TownWorld(content); var recording = new TownRecording(content, world);
        for (int i = 0; i < 150; i++) { world.Advance(0, .05, true); world.AcknowledgeEvents(world.State.Sequence); }
        var snapshot = world.CaptureSnapshot();
        var parent = new TownReplayParent(recording.Complete(), snapshot, null);
        var child = TownWorld.RestoreCandidate(content, TownJson.Serialize(snapshot));
        var childRecording = new TownRecording(content, child, parent: parent);
        for (int i = 0; i < 10; i++) { child.Advance(0, .05, true); child.AcknowledgeEvents(child.State.Sequence); }
        var report = RoundTrip(childRecording.Complete());
        Assert.That(report.Passed, Is.True, TownJson.Serialize(report with { FinalStateJson = null }));
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(report.FinalStateJson!), JsonSerializer.SerializeToNode(child.State, TownJson.Options)), Is.True);
    }

    [Test]
    public void ARejectedPatchLeavesThePublicCursorAtItsPrecedingCheckpoint()
    {
        var package = TwoAdvances(); var cursor = new TownCheckpointCursor();
        var before = cursor.ReadWorld(package.Boundaries[0])!.DeepClone();
        var invalid = package.Boundaries[1] with { CheckpointDeltaData = Encode("{\"version\":2,\"changes\":[{\"path\":[\"sequence\"],\"value\":999},{\"path\":[\"absent\"],\"value\":0}]}") };
        Assert.Throws<InvalidDataException>(() => cursor.ReadWorld(invalid));
        var noChange = package.Boundaries[1] with { CheckpointDeltaData = Encode("[]") };
        Assert.That(JsonNode.DeepEquals(cursor.ReadWorld(noChange), before), Is.True);
    }

    [Test]
    public void HostPauseArraysKeepIndependentReasonsAcrossAppendAndRemoval()
    {
        var content = Content(); var world = new TownWorld(content); var host = new TownInputHost(content, world);
        var recording = new TownRecording(content, world, host);
        host.Pause("menu", true); host.Pause("focus", true); host.Pause("menu", false);
        var package = recording.Complete(); var cursor = new TownCheckpointCursor();
        JsonNode? expected = null;
        foreach (var trace in package.Boundaries) expected = cursor.ReadHost(trace) ?? expected;
        Assert.That(expected!["pauseReasons"]!.AsArray().Select(p => p!.GetValue<string>()), Is.EqualTo(new[] { "focus" }));
        Assert.That(RoundTrip(package).Passed, Is.True);
    }

    private static TownReplayReport RoundTrip(TownReplayPackage package)
    {
        string directory = Path.Combine(Path.GetTempPath(), "cozytown-checkpoint-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "recording.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var output = new StreamWriter(zip.CreateEntry("replay.json").Open())) output.Write(TownJson.Serialize(package));
            return TownReplay.Run(TownReplay.ReadPackage(path));
        }
        finally { File.Delete(Path.Combine(directory, "recording.zip")); Directory.Delete(directory); }
    }

    private static TownReplayPackage TwoAdvances()
    {
        var content = Content(); var world = new TownWorld(content); var recording = new TownRecording(content, world);
        world.Advance(0, .05, true); world.Advance(0, .05, true);
        return recording.Complete();
    }

    private static string Encode(string json)
    {
        using var output = new MemoryStream();
        using (var compression = new BrotliStream(output, CompressionLevel.Fastest, true))
            compression.Write(Encoding.UTF8.GetBytes(json));
        return Convert.ToBase64String(output.ToArray());
    }

    private static JsonNode Decode(string encoded)
    {
        using var input = new MemoryStream(Convert.FromBase64String(encoded));
        using var decompressed = new BrotliStream(input, CompressionMode.Decompress);
        return JsonNode.Parse(decompressed)!;
    }

    private static TownContent Content() => TownContent.Create(
        new(256, 256, [new("player.spawn", "Home", new(60, 60), "outside")], [], [new("outside", new(0, 0, 256, 256))]),
        new([new("mina", "Mina", "player.spawn", "player.spawn", "apple", new() { ["apple"] = 3 }, 5,
            [new("free", 0, 1440, "player.spawn", "free", true)])], [new("apple", "Apple")], 540));
}
