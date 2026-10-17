using CozyTown.Core.Town;
using NUnit.Framework;

namespace CozyTown.Core.Tests;

[TestFixture]
public sealed class TownReplayTests
{
    [Test]
    public void EventAppendAndAcknowledgedTrimmingKeepEveryBoundaryInACompactReplay()
    {
        var content = Content(); var world = new TownWorld(content); var recording = new TownRecording(content, world);
        for (int i = 0; i < 1000; i++)
        {
            world.Advance(0, .05, true);
            world.AcknowledgeEvents(world.State.Sequence);
        }
        var package = recording.Complete();
        Assert.That(package.BoundaryCount, Is.EqualTo(2000));
        Assert.That(package.Boundaries.Count(t => t.Command == "advance"), Is.EqualTo(1000));
        Assert.That(package.Boundaries.Count(t => t.Command == "acknowledge"), Is.EqualTo(1000));
        Assert.That(System.Text.Encoding.UTF8.GetByteCount(TownJson.Serialize(package)), Is.LessThan(2000000));
        var cursor = new TownCheckpointCursor();
        System.Text.Json.Nodes.JsonNode? expected = null;
        foreach (var trace in package.Boundaries) expected = cursor.ReadWorld(trace) ?? expected;
        Assert.That(System.Text.Json.Nodes.JsonNode.DeepEquals(expected,
            System.Text.Json.JsonSerializer.SerializeToNode(world.State, TownJson.Options)), Is.True);
        var replay = TownReplay.Run(package);
        Assert.That(replay.Passed, Is.True, TownJson.Serialize(replay with { FinalStateJson = null }));
        Assert.That(TownJson.Deserialize<TownState>(replay.FinalStateJson!).Events.Count, Is.EqualTo(128));
    }

    [Test]
    public void SealingKeepsTheInputPrefixFixedUntilItsExportCallbackFinishes()
    {
        var content = Content(); var world = new TownWorld(content); var recording = new TownRecording(content, world);
        using var attempted = new ManualResetEventSlim();
        Task? writer = null;
        recording.WithSnapshot(package =>
        {
            writer = Task.Factory.StartNew(() => { attempted.Set(); recording.RecordExternal("external", new { }); },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            attempted.Wait();
            Assert.That(writer.Wait(50), Is.False);
            Assert.That(package.BoundaryCount, Is.Zero);
        });
        Assert.That(writer!.Wait(1000), Is.True);
        Assert.That(recording.Complete().BoundaryCount, Is.EqualTo(1));
    }

    [Test]
    public void RecordingCapacityStopsAtAnExplicitCompletePrefix()
    {
        var content = Content(); var world = new TownWorld(content);
        var recording = new TownRecording(content, world, maximumBytes: 100000);
        Assert.Throws<InvalidDataException>(() => { for (int i = 0; i < 1000; i++) world.Advance(0, .05, true); });
        var prefix = recording.Complete();
        Assert.That(prefix.BoundaryCount, Is.GreaterThan(0));
        var report = TownReplay.Run(prefix);
        Assert.That(report.Passed, Is.False);
        Assert.That(report.Code, Is.EqualTo("recording_interrupted"));
        Assert.That(report.Boundary, Is.EqualTo(prefix.BoundaryCount));
    }

    [Test]
    public void TerminalProviderInputsLeaveTheActiveHostViewButRemainInTheRecording()
    {
        var content = Content(); var world = new TownWorld(content); world.Advance(.001, 0);
        var host = new TownInputHost(content, world); var recording = new TownRecording(content, world, host);
        var request = world.GetDecisionRequests()[0];
        string provider = TownJson.Serialize(new { messages = new[] { new { role = "user", content = request.InputJson } } });
        host.FreezeRequest(request.LogicalRequestId, TownJson.Serialize(new { inputJson = provider }));
        world.MarkDispatched(request.LogicalRequestId, "rules-attempt");
        world.ReceiveResult(new(world.SessionId, request.LogicalRequestId, "rules-attempt", request.ResidentId,
            world.CreateRuleCandidate(request), "rules", null));
        world.Advance(.01, 0);
        host.PruneTerminalRequests();
        Assert.That(host.State.FrozenRequests, Is.Empty);
        Assert.That(recording.Complete().Boundaries.Any(b => b.Command == "host_freeze_request" && b.InputJson.Contains(request.LogicalRequestId)), Is.True);
        Assert.That(TownReplay.Run(recording.Complete()).Passed, Is.True);
    }

    [Test]
    public void CompleteCheckpointsRemainAvailableInABoundedCompressedRecording()
    {
        var content = Content(); var world = new TownWorld(content); var recording = new TownRecording(content, world);
        for (int i = 0; i < 100; i++) world.Advance(0, .05, true);
        var package = recording.Complete();
        Assert.That(TownJson.Serialize(package).Length, Is.LessThan(300000));
        Assert.That(package.Boundaries[1].CheckpointData, Is.Null);
        Assert.That(package.Boundaries[1].CheckpointDeltaData, Is.Not.Null);
        var cursor = new TownCheckpointCursor();
        System.Text.Json.Nodes.JsonNode? expected = null;
        foreach (var trace in package.Boundaries) expected = cursor.ReadWorld(trace) ?? expected;
        Assert.That(TownJson.Deserialize<TownState>(expected!.ToJsonString()).Events.Count, Is.EqualTo(world.State.Events.Count));
        Assert.That(TownReplay.Run(package).Passed, Is.True);
        var wrongBase = TownJson.Clone(package);
        wrongBase.Boundaries[1] = wrongBase.Boundaries[1] with { CheckpointBaseBoundary = 0 };
        Assert.That(TownReplay.Run(wrongBase).Boundary, Is.EqualTo(2));
        Assert.That(TownReplay.Run(wrongBase).Passed, Is.False);
    }

    private static TownContent Content()
    {
        var places = new[] { new PlaceDefinition("player.spawn", "Home", new(60, 60), "outside"), new PlaceDefinition("square", "Square", new(100, 60), "outside") };
        var residents = new[] { "mina", "eli", "ren", "sora" }.Select(id => new ResidentDefinition(id, id, "square", "square", "apple", new() { ["apple"] = 3 }, 5, [new("free", 0, 1440, "square", "free", true)])).ToArray();
        return TownContent.Create(new(256, 256, places, [], [new("outside", new(0, 0, 256, 256))]), new(residents, [new("apple", "Apple")], 540));
    }

    [Test]
    public void SceneReplayStopsAtTheFirstActualCollisionAndChecksTheSavedParentWithTheSameQuery()
    {
        var content = Content(); var world = new TownWorld(content); var recording = new TownRecording(content, world);
        world.SetPlayerPosition(new(65, 60));
        var saved = world.CaptureSnapshot();
        var child = TownWorld.RestoreCandidate(content, TownJson.Serialize(saved));
        var childRecording = new TownRecording(content, child, parent: new(recording.Complete(), saved, null));
        child.SetPlayerPosition(new(66, 60));
        int queries = 0;
        var adapter = new TownReplaySceneAdapter((id, from, to, radius) =>
        {
            queries++;
            return new(false, false, 0, TownSpatialConfiguration.Current);
        });
        var report = TownReplay.Run(childRecording.Complete(), adapter);
        Assert.That(report.Passed, Is.False);
        Assert.That(report.Field, Does.Contain("queries[0].clear"));
        Assert.That(report.Boundary, Is.EqualTo(1));
        Assert.That(report.Command, Is.EqualTo("move"));
        Assert.That(queries, Is.EqualTo(1));
        Assert.That(TownReplay.Run(childRecording.Complete()).Passed, Is.True);
    }

    [Test]
    public void SceneReplayComparesActualMotionEvidenceInsteadOfTrustingRecordedFractions()
    {
        var content = Content();
        var world = new TownWorld(content)
        {
            DetailedMovementQuery = (_, _, _) => new(true, true, 1, TownSpatialConfiguration.Current)
        };
        var recording = new TownRecording(content, world);
        world.SetPlayerPosition(new(65, 60));
        var package = recording.Complete();
        var adapter = new TownReplaySceneAdapter((_, _, _, _) => new(true, true, 1, TownSpatialConfiguration.Current));
        Assert.That(TownReplay.Run(package, adapter).Passed, Is.True);
        package.Boundaries[0].Queries[0] = package.Boundaries[0].Queries[0] with
        { Evidence = package.Boundaries[0].Queries[0].Evidence! with { SafeFraction = .25 } };
        Assert.That(TownReplay.Run(package).Passed, Is.True);
        var report = TownReplay.Run(package, adapter);
        Assert.That(report.Field, Is.EqualTo("queries[0].evidence.safeFraction"));
        Assert.That(report.Boundary, Is.EqualTo(1));
        Assert.That(report.Mode, Is.EqualTo("godot_scene_replay"));
    }

    [Test]
    public void RecordedSuccessfulAndRejectedMovesReplayWithExactIdentities()
    {
        var content = Content();
        var world = new TownWorld(content, movementQuery: (_, _, to) => to.X < 70);
        var recording = new TownRecording(content, world);
        Assert.That(world.SetPlayerPosition(new(65, 60)), Is.True);
        Assert.That(world.SetPlayerPosition(new(75, 60)), Is.False);
        world.Advance(.1, .2);
        var package = recording.Complete();
        var first = TownReplay.Run(package);
        var second = TownReplay.Run(package);
        Assert.That(first.Passed, Is.True, TownJson.Serialize(first));
        Assert.That(second.FinalStateJson, Is.EqualTo(first.FinalStateJson));
        Assert.That(TownJson.Deserialize<TownState>(first.FinalStateJson!).BranchId, Is.EqualTo(world.State.BranchId));
    }

    [Test]
    public void KeyboardMovementCancelsClickRouteAndPauseReasonsRemainIndependent()
    {
        var content = Content();
        var world = new TownWorld(content);
        var host = new TownInputHost(content, world);
        var recording = new TownRecording(content, world, host);
        host.RouteTo(new(160, 60));
        host.Move(.05, new(1, 0));
        host.Pause("menu", true);
        host.Pause("focus", true);
        host.Pause("menu", false);
        host.AcceptElapsed(.077);
        host.TakeStep();
        world.Advance(0, .05, host.State.PauseReasons.Length > 0);
        Assert.That(host.State.PlayerRoute, Is.Null);
        Assert.That(host.State.PauseReasons, Is.EqualTo(new[] { "focus" }));
        Assert.That(TownReplay.Run(recording.Complete()).Passed, Is.True);
        var package = recording.Complete();
        Assert.That(TownReplay.Run(package with { Boundaries = [], BoundaryCount = 0, InitialHost = package.InitialHost! with { UnconsumedSeconds = 2 } }).Passed, Is.False);
    }

    [Test]
    public void RestoreReplaysOnlyTheSavedParentPrefixAndRejectsMissingParent()
    {
        var content = Content();
        var parent = new TownWorld(content);
        var recording = new TownRecording(content, parent);
        parent.SetPlayerPosition(new(65, 60));
        var snapshot = parent.CaptureSnapshot();
        var prefix = new TownReplayParent(recording.Complete(), snapshot, null);
        parent.SetPlayerPosition(new(80, 60));
        var child = TownWorld.RestoreCandidate(content, TownJson.Serialize(snapshot));
        var childRecording = new TownRecording(content, child, parent: prefix);
        child.Advance(.1, .2);
        var package = childRecording.Complete();
        var report = TownReplay.Run(package);
        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
        Assert.That(TownJson.Deserialize<TownState>(report.FinalStateJson!).PlayerPosition, Is.EqualTo(new TownPoint(65, 60)));
        Assert.That(TownReplay.Run(package with { Parent = null }).Code, Is.EqualTo("missing_parent_prefix"));
    }

    [Test]
    public void QueryOrderAndUnusedQueriesStopAtTheFirstBoundary()
    {
        var content = Content();
        var world = new TownWorld(content);
        var recording = new TownRecording(content, world);
        world.SetPlayerPosition(new(65, 60));
        var package = recording.Complete();
        var wrongObject = TownJson.Clone(package);
        wrongObject.Boundaries[0].Queries[0] = wrongObject.Boundaries[0].Queries[0] with { EntityId = "mina" };
        var report = TownReplay.Run(wrongObject);
        Assert.That(report.Boundary, Is.EqualTo(1));
        Assert.That(report.Field, Is.EqualTo("queries[0].entityId"));
        var leftover = TownJson.Clone(package);
        leftover.Boundaries[0] = leftover.Boundaries[0] with { Queries = [..leftover.Boundaries[0].Queries, leftover.Boundaries[0].Queries[0]] };
        Assert.That(TownReplay.Run(leftover).Field, Is.EqualTo("queries"));
        var missing = TownJson.Clone(package);
        missing.Boundaries[0] = missing.Boundaries[0] with { Queries = [] };
        Assert.That(TownReplay.Run(missing).Field, Is.EqualTo("queries[0]"));
    }

    [Test]
    public void TamperedCommandCheckpointAndRecordGapNeverReportSuccess()
    {
        var content = Content(); var world = new TownWorld(content); var recording = new TownRecording(content, world);
        world.SetPlayerPosition(new(65, 60)); world.Advance(.25, .1); world.SelectQuickbar("select-1", 3);
        var original = recording.Complete();
        var command = TownJson.Clone(original); command.Boundaries[0] = command.Boundaries[0] with { InputJson = "{\"position\":{\"x\":66,\"y\":60}}" };
        Assert.That(TownReplay.Run(command).Boundary, Is.EqualTo(1));
        var checkpoint = TownJson.Clone(original); var cursor = new TownCheckpointCursor();
        cursor.ReadWorld(checkpoint.Boundaries[0]); var state = TownJson.Deserialize<TownState>(cursor.ReadWorld(checkpoint.Boundaries[1])!.ToJsonString()); state.TerminalRequestIds.Add("invented");
        checkpoint.Boundaries[1] = checkpoint.Boundaries[1] with { Checkpoint = state };
        Assert.That(TownReplay.Run(checkpoint).Field, Is.EqualTo("state.terminalRequestIds.length"));
        var gap = original with { Boundaries = [original.Boundaries[0], original.Boundaries[2]], BoundaryCount = 2 };
        Assert.That(TownReplay.Run(gap).Field, Is.EqualTo("sequence"));
        Assert.That(TownReplay.Run(original with { Boundaries = original.Boundaries[..^1] }).Code, Is.EqualTo("record_gap"));
        Assert.That(TownReplay.Run(original with { Definitions = original.Definitions with { StartMinute = 600 } }).Passed, Is.False);
        var missingCheckpoint = TownJson.Clone(original);
        missingCheckpoint.Boundaries[0] = missingCheckpoint.Boundaries[0] with { CheckpointData = null };
        Assert.That(TownReplay.Run(missingCheckpoint).Passed, Is.False);
        var corruptCheckpoint = TownJson.Clone(original);
        corruptCheckpoint.Boundaries[0] = corruptCheckpoint.Boundaries[0] with { CheckpointData = "not-base64" };
        Assert.That(TownReplay.Run(corruptCheckpoint).Code, Is.EqualTo("invalid_recording"));
    }

    [Test]
    public void TimelyArrivalSurvivesDelayedPersistenceWhilePausedAndDuplicateRestoreKeepsBusinessIds()
    {
        var content = Content(); var world = new TownWorld(content); var recording = new TownRecording(content, world);
        world.Advance(.001, 0);
        var request = world.GetDecisionRequests()[0];
        world.MarkDispatched(request.LogicalRequestId, "rules-attempt");
        world.MarkResponseArrived(world.SessionId, request.LogicalRequestId, "rules-attempt", 2);
        world.Advance(0, 100, true);
        world.ReceiveResult(new(world.SessionId, request.LogicalRequestId, "rules-attempt", request.ResidentId, world.CreateRuleCandidate(request), "rules", null, 2));
        world.Advance(0, 0);
        Assert.That(TownReplay.Run(recording.Complete()).Passed, Is.True);
        var snapshot = world.CaptureSnapshot();
        var prefix = new TownReplayParent(recording.Complete(), snapshot, null);
        foreach (int _ in new[] { 1, 2 })
        {
            var restored = TownWorld.RestoreCandidate(content, TownJson.Serialize(snapshot));
            var child = new TownRecording(content, restored, parent: prefix);
            restored.ReceiveResult(new(world.SessionId, request.LogicalRequestId, "rules-attempt", request.ResidentId, null, "rules", null, 2));
            restored.Advance(.1, .2);
            Assert.That(TownReplay.Run(child.Complete()).Passed, Is.True);
            Assert.That(restored.State.TerminalRequestIds, Does.Contain(request.LogicalRequestId));
        }
    }
}
