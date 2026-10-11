using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using CozyTown.Core.Town;
using CozyTown.Game;
using CozyTown.Model;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

[TestFixture]
public sealed class TownNpcChoiceContractTests
{
    private static TownContent Content()
    {
        string[] ids = ["mina", "eli", "ren", "sora"];
        var places = ids.Select((id, i) => new PlaceDefinition(id, id, new(80 + i * 16, 96), "outside")).ToArray();
        return TownContent.Create(new(256, 192, places, [], [new("outside", new(0, 0, 256, 192))]),
            new(ids.Select(id => new ResidentDefinition(id, id, id, id, "apple", new() { ["apple"] = 3 }, 5,
                [new("free", 0, 1440, id, "free", true)])).ToArray(), [new("apple", "Apple")]));
    }

    private static DecisionRequest Request(TownWorld world, string id) => world.GetDecisionRequests().Single(r => r.ResidentId == id);
    private static void Decide(TownWorld world, string id, ActionCandidate candidate)
    {
        var request = Request(world, id);
        Assert.That(world.MarkDispatched(request.LogicalRequestId, "fixed-" + request.LogicalRequestId), Is.True);
        Assert.That(world.ReceiveResult(new(world.SessionId, request.LogicalRequestId, "fixed-" + request.LogicalRequestId,
            id, candidate, "fixed_model", null)), Is.True);
        world.Advance(0, 0);
    }

    [Test]
    public void FullProviderBodyKeepsLongConversationFactsWithinTransportLimit()
    {
        var world = new TownWorld(Content()); world.Advance(.001, 0);
        Decide(world, "mina", new("invite", "eli", "mina", null, 0, 0, null, "一起聊聊。"));
        Decide(world, "eli", new("respond", "mina", null, null, 0, 0, true, "好呀。"));
        world.Advance(8, 0);
        string text = new('聊', 160);
        for (int i = 0; i < 6; i++)
            Decide(world, i % 2 == 0 ? "mina" : "eli", new("speak", i % 2 == 0 ? "eli" : "mina", null, null, 0, 0, null, text));
        var request = Request(world, "mina");
        string body = TownModelProtocol.BuildRequest(request);
        TestContext.WriteLine("Frozen provider body bytes: " + Encoding.UTF8.GetByteCount(body));
        Assert.That(Encoding.UTF8.GetByteCount(body), Is.LessThanOrEqualTo(16 * 1024));
        var provider = JsonNode.Parse(body)!;
        Assert.That(provider["messages"]![1]!["content"]!.GetValue<string>(), Is.EqualTo(request.InputJson));
        var context = JsonNode.Parse(request.InputJson)!;
        Assert.That(context["memories"]!.AsArray(), Has.Count.EqualTo(6));
        Assert.That(context["commitment"]!["dialogue"]!.AsArray(), Has.Count.EqualTo(6));
        Assert.That(context["actionConstraints"]!.AsArray(), Has.Count.EqualTo(3));
        Assert.That(provider["max_tokens"]!.GetValue<int>(), Is.EqualTo(512));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PendingNpcSaveAndReplayPreserveNewOrPreviouslyFrozenInput(bool legacy)
    {
        var content = Content(); var world = new TownWorld(content); world.Advance(.001, 0);
        var request = Request(world, "mina");
        if (legacy)
        {
            var snapshot = world.CaptureSnapshot();
            var pending = snapshot.State.PendingDecisions.Single(p => p.Request.LogicalRequestId == request.LogicalRequestId);
            var input = JsonNode.Parse(request.InputJson)!.AsObject();
            input.Remove("actionConstraints"); input.Remove("tradeConstraints");
            string original = TownJson.Serialize(input);
            pending.Request = request with { InputJson = original,
                InputFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original))) };
            world = TownWorld.OpenReplay(content, snapshot, world.SessionId);
            request = Request(world, "mina");
        }
        var host = new TownInputHost(content, world); var recording = new TownRecording(content, world, host);
        string body = TownModelProtocol.BuildRequest(request);
        if (legacy)
        {
            var provider = JsonNode.Parse(body)!;
            provider["messages"]![0]!["content"] = "Previously frozen NPC instructions.";
            body = provider.ToJsonString();
        }
        var frozen = new FrozenModelRequest(new(world.State.BranchId, world.SessionId, request.LogicalRequestId,
            request.ResidentId, request.ActivityRevision), ModelStage.Baseline, body, false, TimeSpan.FromSeconds(60));
        host.FreezeRequest(request.LogicalRequestId, TownJson.Serialize(frozen));
        Assert.That(world.MarkDispatched(request.LogicalRequestId, "original-fixed"), Is.True);
        var savedWorld = world.CaptureSnapshot(); var savedHost = host.State;
        var save = new TownHostSnapshot(1, TownJson.Serialize(savedWorld), world.State.BranchId, 0, 0, "real", "baseline",
            new() { [request.LogicalRequestId] = frozen });
        string path = Path.Combine(Path.GetTempPath(), "cozytown-npc-contract-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            TownSaveFile.Write(path, save, candidate => _ = TownWorld.RestoreCandidate(content, candidate.WorldJson));
            var read = TownSaveFile.Read(path);
            Assert.That(read.ModelRequests[request.LogicalRequestId].InputJson, Is.EqualTo(body));
            var restored = TownWorld.RestoreCandidate(content, read.WorldJson);
            var recovered = Request(restored, "mina");
            Assert.That(recovered.IsRecovery, Is.True);
            Assert.That(recovered.InputJson, Is.EqualTo(request.InputJson));
            var childHost = new TownInputHost(content, restored, savedHost);
            var child = new TownRecording(content, restored, childHost, new(recording.Complete(), savedWorld, savedHost));
            Assert.That(restored.MarkDispatched(recovered.LogicalRequestId, "recovered-fixed"), Is.True);
            Assert.That(restored.ReceiveResult(new(restored.SessionId, recovered.LogicalRequestId, "recovered-fixed", "mina",
                new("wait", null, null, null, 0, 0, null, "等待。"), "fixed_model", null)), Is.True);
            restored.Advance(0, 0); childHost.PruneTerminalRequests();
            var replay = TownReplay.Run(child.Complete());
            Assert.That(replay.Passed, Is.True, TownJson.Serialize(replay with { FinalStateJson = null }));
        }
        finally { File.Delete(path); File.Delete(path + ".previous"); }
    }
}
