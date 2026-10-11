using CozyTown.Core.Town;
using CozyTown.Game;
using CozyTown.Model;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

[TestFixture]
public sealed class TownPlayerDialogueReplayTests
{
    [Test]
    public void FourResidentsReplayThePublicDialogueAndFrozenProviderBoundaries()
    {
        var content = TownPlayerDialogueTestsContent(); var world = new TownWorld(content);
        var input = new TownInputHost(content, world); var recording = new TownRecording(content, world, input);
        var dialogue = new TownDialogueHost(world);
        foreach (string id in new[] { "mina", "eli", "ren", "sora" })
        {
            world.SetPlayerPosition(content.Place(id + ".home").Position);
            Assert.That(dialogue.Open(id, input.State.PauseReasons), Is.True);
            input.Pause("dialogue", true);
            var request = dialogue.Generate(input.State.PauseReasons)!;
            input.FreezeRequest(request.LogicalRequestId, TownJson.Serialize(dialogue.Freeze(request, ModelStage.Baseline)));
            input.Pause("menu", true); world.Advance(0, 2, true);
            dialogue.CompleteFixed(request, TownPlayerDialogueProtocol.FixedResponse(id));
            Assert.That(dialogue.Present(input.State.PauseReasons), Is.False);
            input.Pause("menu", false); Assert.That(dialogue.Present(input.State.PauseReasons), Is.True);
            dialogue.Close(); input.Pause("dialogue", false); world.Advance(0, 0);
            input.PruneTerminalRequests();
        }
        var package = recording.Complete();
        Assert.That(package.Boundaries.Count(b => b.Command == "player_dialogue_open"), Is.EqualTo(4));
        var report = TownReplay.Run(package);
        Assert.That(report.Passed, Is.True, TownJson.Serialize(report with { FinalStateJson = null }));
        Assert.That(report.FinalStateJson, Is.EqualTo(TownJson.Serialize(world.State)));
    }

    [Test]
    public void RequestDoesNotInheritElapsedTimeBeforeItsCreation()
    {
        var content = TownPlayerDialogueTestsContent(); var world = new TownWorld(content);
        var input = new TownInputHost(content, world); var recording = new TownRecording(content, world, input);
        var dialogue = new TownDialogueHost(world); dialogue.Open("mina", []); input.Pause("dialogue", true);
        input.AcceptElapsed(.03);
        input.FlushPausedElapsed(90);
        var request = dialogue.Generate(input.State.PauseReasons)!;
        Assert.That(input.State.UnconsumedSeconds, Is.Zero);
        Assert.That(request.RemainingWaitSeconds, Is.EqualTo(60));
        input.FlushPausedElapsed(2);
        Assert.That(dialogue.Current!.Request!.RemainingWaitSeconds, Is.EqualTo(58));
        Assert.That(TownReplay.Run(recording.Complete()).Passed, Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RealPlayerTextReplayChecksArrivalPersistenceMailboxAndProtocolBindings(bool worldArrivalFirst)
    {
        var content = TownPlayerDialogueTestsContent(); var world = new TownWorld(content);
        var input = new TownInputHost(content, world); var recording = new TownRecording(content, world, input);
        var empty = new BudgetSnapshot(TownWorld.DefaultBudgetId, "CNY", 5000000, 0, 0, 0, 0, null, []);
        recording.SetInitialBudget(TownJson.Deserialize<TownReplayBudgetState>(TownJson.Serialize(empty)));
        var dialogue = new TownDialogueHost(world); dialogue.Open("mina", []); input.Pause("dialogue", true);
        var request = dialogue.Generate(input.State.PauseReasons)!; var frozen = dialogue.Freeze(request, ModelStage.Baseline);
        input.FreezeRequest(request.LogicalRequestId, TownJson.Serialize(frozen));
        string attemptId = "00000000-0000-0000-0000-000000000001";
        var admitted = new BudgetAttempt(attemptId, "baseline", request.LogicalRequestId, false, "reserved", true, 2110000, 0, null, null, null);
        var reserved = empty with { Attempts = [admitted], TotalAttempts = 1, SlotsHeld = 1, ReservedMicros = 2110000 };
        recording.RecordExternal("budget_reserve", new { stage = "baseline", logicalRequestId = request.LogicalRequestId,
            attemptId, recovery = false, admission = "accepted", after = reserved });
        var dispatch = new ModelDispatch(frozen, attemptId, "fixture", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(60), reserved);
        dialogue.Acknowledge(request.PlayerSessionId, request.LogicalRequestId, dispatch.AttemptId);
        string json = TownPlayerDialogueProtocol.FixedResponse("mina");
        if (worldArrivalFirst) world.MarkPlayerDialogueResponseArrived(world.SessionId, request.PlayerSessionId, request.LogicalRequestId, dispatch.AttemptId, 2);
        recording.RecordExternal("model_response_arrived", new { dispatch, responseBody = TownJson.Serialize(new { choices = new[] { new { message = new { content = json } } } }), elapsed = TimeSpan.FromSeconds(2) });
        if (!worldArrivalFirst) world.MarkPlayerDialogueResponseArrived(world.SessionId, request.PlayerSessionId, request.LogicalRequestId, dispatch.AttemptId, 2);
        input.Pause("menu", true); world.Advance(0, 100, true);
        var completion = new ModelCompletion(dispatch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(2), TimeSpan.FromSeconds(2), 200,
            "fixture", "fixed", "fixed", "stop", json, new(100, 10, 110, 0, 100), null, true, true, false, 280, TimeSpan.FromSeconds(2), null);
        var settled = reserved with { Attempts = [admitted with { Status = "settled", SlotHeld = false, ReservedCostMicros = 0,
            SettledCostMicros = 280, PromptTokens = 100, CompletionTokens = 10 }], SlotsHeld = 0, ReservedMicros = 0, SettledMicros = 280 };
        recording.RecordExternal("budget_settle", new { attemptId, promptTokens = 100L, completionTokens = 10L, after = settled });
        recording.RecordExternal("model_completion_persisted", completion);
        recording.RecordExternal("model_mailbox_enqueued", new { dispatch }); recording.RecordExternal("model_mailbox_entered", new { dispatch });
        dialogue.Complete(request, world.SessionId, dispatch.AttemptId, json, "real_model", null, 2);
        input.Pause("menu", false); dialogue.Present(input.State.PauseReasons);
        var package = recording.Complete();
        Assert.That(TownReplay.Run(package).Passed, Is.True, TownJson.Serialize(TownReplay.Run(package)));
        var changedText = Alter(package, "player_dialogue_result", node => node["result"]!["text"] = "changed text");
        Assert.That(TownReplay.Run(changedText).Field, Is.EqualTo("model.playerText"));
        var wrongIdentity = Alter(package, "player_dialogue_result", node => node["result"]!["playerSessionId"] = "wrong-session");
        Assert.That(TownReplay.Run(wrongIdentity).Field, Is.EqualTo("model.playerSessionId"));
        var wrongArrival = Alter(package, "player_dialogue_arrival", node => node["receivedAfterSeconds"] = 3);
        Assert.That(TownReplay.Run(wrongArrival).Field, Is.EqualTo(worldArrivalFirst
            ? "state.playerDialogues[0].request.remainingWaitSeconds" : "model.playerArrivalSeconds"));
        var invalidProtocol = Alter(package, "model_completion_persisted", node => node["candidateJson"] = "{\"text\":\"ok\",\"action\":\"trade\"}");
        Assert.That(TownReplay.Run(invalidProtocol).Passed, Is.False);
    }

    [Test]
    public void RestoredRetirementIsRecordedAfterTheSavedParentPrefix()
    {
        var content = TownPlayerDialogueTestsContent(); var world = new TownWorld(content);
        var input = new TownInputHost(content, world); var recording = new TownRecording(content, world, input);
        var dialogue = new TownDialogueHost(world); dialogue.Open("mina", []); input.Pause("dialogue", true); dialogue.Generate(input.State.PauseReasons);
        var saved = world.CaptureSnapshot(); var prefix = new TownReplayParent(recording.Complete(), saved, input.State);
        dialogue.Close();
        var restored = TownWorld.RestoreCandidate(content, TownJson.Serialize(saved));
        var restoredInput = new TownInputHost(content, restored, input.State);
        var child = new TownRecording(content, restored, restoredInput, prefix);
        restored.ApplyPlayerDialogueRetirement(restored.State.CurrentPlayerDialogueId!, "closed");
        new TownDialogueHost(restored).Close(); restoredInput.Pause("dialogue", false); restored.Advance(0, 0);
        var report = TownReplay.Run(child.Complete());
        Assert.That(report.Passed, Is.True, TownJson.Serialize(report));
        Assert.That(restored.State.Residents.Single(r => r.Id == "mina").Memories.Count(m => m.Source == "fixed_dialogue"), Is.EqualTo(1));
    }

    private static TownReplayPackage Alter(TownReplayPackage package, string command, Action<System.Text.Json.Nodes.JsonNode> change)
    {
        var copy = TownJson.Clone(package); int index = Array.FindIndex(copy.Boundaries, b => b.Command == command);
        var node = System.Text.Json.Nodes.JsonNode.Parse(copy.Boundaries[index].InputJson)!; change(node);
        string json = node.ToJsonString();
        copy.Boundaries[index] = copy.Boundaries[index] with { InputJson = json, InputFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(command + "\n" + json))).ToLowerInvariant() };
        return copy;
    }

    internal static TownContent TownPlayerDialogueTestsContent()
    {
        var ids = new[] { "mina", "eli", "ren", "sora" };
        var places = ids.Select((id, i) => new PlaceDefinition(id + ".home", id, new(80 + i * 16, 96), "outside")).ToArray();
        return TownContent.Create(new(256, 256, places, [], [new("outside", new(0, 0, 256, 256))]),
            new(ids.Select(id => new ResidentDefinition(id, id, id + ".home", id + ".home", "apple", new() { ["apple"] = 3 }, 5,
                [new("free", 0, 1440, id + ".home", "free", true)])).ToArray(), [new("apple", "Apple")]));
    }
}
