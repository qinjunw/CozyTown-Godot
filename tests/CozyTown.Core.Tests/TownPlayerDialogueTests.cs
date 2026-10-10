using CozyTown.Core.Town;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace CozyTown.Core.Tests;

[TestFixture]
public sealed class TownPlayerDialogueTests
{
    public static TownContent Content()
    {
        string[] ids = ["mina", "eli", "ren", "sora"];
        var places = ids.Select((id, i) => new PlaceDefinition(id + ".home", id, new(80 + i * 16, 96), "outside")).ToArray();
        var residents = ids.Select((id, i) => new ResidentDefinition(id, id, places[i].Id, places[i].Id, "apple",
            new() { ["apple"] = i + 1 }, i + 5, [new("free", 0, 1440, places[i].Id, "free", true)], "今天想照顾自己的生活。")).ToArray();
        return TownContent.Create(new(256, 192, places, [], [new("outside", new(0, 0, 256, 192))]), new(residents, [new("apple", "苹果")], 540));
    }

    [TestCase("mina")]
    [TestCase("eli")]
    [TestCase("ren")]
    [TestCase("sora")]
    public void NearbyPlayerChoosesOneTextRequestAndDisplayedMemoriesCommitAfterPause(string residentId)
    {
        var world = new TownWorld(Content());
        world.Advance(.001, 0);
        world.SetPlayerPosition(world.State.Residents.Single(r => r.Id == residentId).Position);
        var dialogue = world.OpenPlayerDialogue(residentId)!;
        var npc = world.GetDecisionRequests().Single(r => r.ResidentId == residentId);
        var request = world.RequestPlayerDialogue(dialogue.Id)!;
        Assert.That(request.PlayerSessionId, Is.EqualTo(dialogue.Id));
        Assert.That(request.LogicalRequestId, Is.Not.EqualTo(npc.LogicalRequestId));
        Assert.That(world.RequestPlayerDialogue(dialogue.Id), Is.Null);
        Assert.That(world.MarkPlayerDialogueDispatched(dialogue.Id, request.LogicalRequestId, "fixed-attempt"), Is.True);
        Assert.That(world.ReceivePlayerDialogueResult(new(world.SessionId, dialogue.Id, request.LogicalRequestId,
            "fixed-attempt", residentId, "今天的风很舒服。", "fixed_model", null, 1)), Is.True);
        Assert.That(world.DisplayPlayerDialogue(dialogue.Id, true), Is.True);
        world.Advance(0, 5, paused: true);
        Assert.That(world.State.Residents.Single(r => r.Id == residentId).Memories, Is.Empty);
        Assert.That(world.ClosePlayerDialogue(dialogue.Id), Is.True);
        world.Advance(0, 0);
        world.Advance(0, 0);
        Assert.That(world.State.Residents.Single(r => r.Id == residentId).Memories.Count(m => m.Text == "今天的风很舒服。"), Is.EqualTo(1));
        Assert.That(world.State.PendingDecisions.Single(p => p.Request.ResidentId == residentId).Request.LogicalRequestId, Is.EqualTo(npc.LogicalRequestId));
        Assert.That(world.State.Residents.Single(r => r.Id == residentId).Inventory["apple"], Is.EqualTo(Array.IndexOf(new[] { "mina", "eli", "ren", "sora" }, residentId) + 1));
    }

    [Test]
    public void WaitingSaveRestoresSamePlayerPanelAndFrozenRequestWithRemainingBudget()
    {
        var content = Content(); var world = new TownWorld(content);
        var dialogue = world.OpenPlayerDialogue("mina")!;
        var request = world.RequestPlayerDialogue(dialogue.Id)!;
        world.MarkPlayerDialogueDispatched(dialogue.Id, request.LogicalRequestId, "original-attempt");
        world.Advance(0, 13, true);
        var restored = TownWorld.RestoreCandidate(content, world.CaptureJson());
        var resumed = restored.GetPlayerDialogueRequest()!;
        Assert.That(restored.State.CurrentPlayerDialogueId, Is.EqualTo(dialogue.Id));
        Assert.That(resumed.InputJson, Is.EqualTo(request.InputJson));
        Assert.That(resumed.LogicalRequestId, Is.EqualTo(request.LogicalRequestId));
        Assert.That(resumed.RemainingWaitSeconds, Is.EqualTo(47));
        Assert.That(resumed.IsRecovery, Is.True);
        Assert.That(resumed.AttemptId, Is.Null);
        Assert.That(restored.ReceivePlayerDialogueResult(new(world.SessionId, dialogue.Id, request.LogicalRequestId,
            "original-attempt", "mina", "旧分支回复。", "fixed_model", null, 15)), Is.False);
    }

    [Test]
    public void MissingDisplayCommitEvidenceIsRejectedWithoutChangingLiveWorld()
    {
        var content = Content(); var world = new TownWorld(content);
        world.OpenPlayerDialogue("mina");
        string before = TownJson.Serialize(world.State);
        var saved = JsonNode.Parse(world.CaptureJson())!;
        saved["state"]!["playerDialogues"]![0]!["displays"]![0]!["memoryCommitted"] = true;
        Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, saved.ToJsonString()));
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void ClosingQueuedInFlightSealedOrDisplayedSessionCannotLeakIntoNextResident(int phase)
    {
        var content = Content(); var world = new TownWorld(content);
        var old = world.OpenPlayerDialogue("mina")!; var request = world.RequestPlayerDialogue(old.Id)!;
        var result = new PlayerDialogueResult(world.SessionId, old.Id, request.LogicalRequestId, "attempt", "mina", "只属于旧会话。", "fixed_model", null, 1);
        if (phase >= 1) world.MarkPlayerDialogueDispatched(old.Id, request.LogicalRequestId, "attempt");
        if (phase >= 2) world.ReceivePlayerDialogueResult(result);
        if (phase >= 3) world.DisplayPlayerDialogue(old.Id, true);
        world.ClosePlayerDialogue(old.Id);
        var next = world.OpenPlayerDialogue("eli")!;
        Assert.That(next.Id, Is.Not.EqualTo(old.Id));
        Assert.That(next.Request, Is.Null);
        Assert.That(world.ReceivePlayerDialogueResult(result), Is.False);
        Assert.That(world.DisplayPlayerDialogue(old.Id, true), Is.False);
        Assert.That(world.MarkPlayerDialogueDispatched(old.Id, request.LogicalRequestId, "late-attempt"), Is.False);
        world.Advance(0, 1, true);
        Assert.That(world.State.Residents.SelectMany(r => r.Memories), Is.Empty);
        world.ClosePlayerDialogue(next.Id); world.Advance(0, 0); world.Advance(0, 0);
        Assert.That(world.State.Residents.SelectMany(r => r.Memories).Count(m => m.Text == "只属于旧会话。"), Is.EqualTo(phase == 3 ? 1 : 0));
        Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, world.CaptureJson()));
    }

    [Test]
    public void TimelyCompleteArrivalSurvivesPausedPersistenceDelayAndDisplayedSaveCommitsOnce()
    {
        var content = Content(); var world = new TownWorld(content);
        var dialogue = world.OpenPlayerDialogue("mina")!; var request = world.RequestPlayerDialogue(dialogue.Id)!;
        world.MarkPlayerDialogueDispatched(dialogue.Id, request.LogicalRequestId, "attempt");
        world.Advance(0, 59, true);
        Assert.That(world.MarkPlayerDialogueResponseArrived(world.SessionId, dialogue.Id, request.LogicalRequestId, "attempt", 59), Is.True);
        world.Advance(0, 5, true);
        Assert.Throws<InvalidOperationException>(() => world.CaptureJson());
        var result = new PlayerDialogueResult(world.SessionId, dialogue.Id, request.LogicalRequestId, "attempt", "mina", "完整到达时仍在额度内。", "fixed_model", null, 59);
        Assert.That(world.ReceivePlayerDialogueResult(result), Is.True);
        Assert.That(world.DisplayPlayerDialogue(dialogue.Id, false), Is.False);
        Assert.That(world.DisplayPlayerDialogue(dialogue.Id, true), Is.True);
        Assert.That(world.DisplayPlayerDialogue(dialogue.Id, true), Is.False);
        Assert.That(world.ReceivePlayerDialogueResult(result), Is.False);
        var restored = TownWorld.RestoreCandidate(content, world.CaptureJson());
        Assert.That(restored.GetPlayerDialogueRequest(), Is.Null);
        Assert.That(restored.State.PlayerDialogues.Single().Displays.Last().Text, Is.EqualTo(result.Text));
        restored.ClosePlayerDialogue(dialogue.Id); restored.Advance(0, 0); restored.Advance(0, 0);
        restored = TownWorld.RestoreCandidate(content, restored.CaptureJson()); restored.Advance(0, 0);
        Assert.That(restored.State.Residents.Single(r => r.Id == "mina").Memories.Count(m => m.Text == result.Text), Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void QueueAndTransferWaitingExpireDuringPauseAndCannotReviveAfterRestore(bool sent)
    {
        var content = Content(); var world = new TownWorld(content);
        var dialogue = world.OpenPlayerDialogue("mina")!; var request = world.RequestPlayerDialogue(dialogue.Id)!;
        if (sent) world.MarkPlayerDialogueDispatched(dialogue.Id, request.LogicalRequestId, "attempt");
        world.Advance(0, 60, true);
        Assert.That(world.WorldMinute, Is.EqualTo(540));
        var restored = TownWorld.RestoreCandidate(content, world.CaptureJson());
        Assert.That(restored.GetPlayerDialogueRequest(), Is.Null);
        restored.DisplayPlayerDialogue(dialogue.Id, true);
        Assert.That(restored.State.PlayerDialogues.Single().Displays.Last().Error, Is.EqualTo("logical_timeout"));
        Assert.That(restored.State.PlayerDialogues.Single().Displays.Last().Source, Is.EqualTo("rule_fallback"));
        Assert.That(restored.MarkPlayerDialogueResponseArrived(restored.SessionId, dialogue.Id, request.LogicalRequestId, "attempt", 60), Is.False);
    }

    [Test]
    public void SnapshotRejectsMissingFieldsWrongSessionBindingAndOldVersion()
    {
        var content = Content(); var world = new TownWorld(content);
        var dialogue = world.OpenPlayerDialogue("mina")!; world.RequestPlayerDialogue(dialogue.Id);
        string original = world.CaptureJson();
        foreach (string mutation in new[] { "missing", "binding", "queue", "version", "input" })
        {
            var saved = JsonNode.Parse(original)!;
            if (mutation == "missing") saved["state"]!["playerDialogues"]![0]!.AsObject().Remove("displays");
            if (mutation == "binding") saved["state"]!["currentPlayerDialogueId"] = "unrelated";
            if (mutation == "queue") saved["state"]!["modelQueue"]!.AsArray().Clear();
            if (mutation == "version") saved["schemaVersion"] = 4;
            if (mutation == "input")
            {
                var request = saved["state"]!["playerDialogues"]![0]!["request"]!;
                var input = JsonNode.Parse(request["inputJson"]!.GetValue<string>())!;
                input.AsObject().Remove("schedule");
                request["inputJson"] = input.ToJsonString();
                request["inputFingerprint"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input.ToJsonString()))).ToLowerInvariant();
            }
            Assert.Throws<InvalidDataException>(() => TownWorld.RestoreCandidate(content, saved.ToJsonString()), mutation);
        }
    }
}
