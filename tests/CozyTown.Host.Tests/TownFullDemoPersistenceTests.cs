using CozyTown.Core.Town;
using CozyTown.Game;
using CozyTown.Model;
using NUnit.Framework;
using System.Text.Json.Nodes;

namespace CozyTown.Host.Tests;

[TestFixture("full-demo-v1")]
[TestFixture("full-demo-v2")]
public sealed class TownFullDemoPersistenceTests(string profileVersion)
{
    [Test]
    public void OtherFullDemoProfileIsRejectedBeforePublishingASave()
    {
        var saved = Snapshot("experiment", "player_text");
        string other = profileVersion == "full-demo-v1" ? "full-demo-v2" : "full-demo-v1";
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateForRuntime(saved, TownModelRuntimeProfile.Resolve(other, "experiment")));
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(saved with { BudgetProfileVersion = other }));
    }

    [Test]
    public void FullDemoSaveCannotImportAnOfflineFixtureScenario()
    {
        var runtime = TownModelRuntimeProfile.Resolve(profileVersion, "experiment");
        var world = new TownWorld(Content(), budgetId: runtime.BudgetId);
        var snapshot = world.CaptureSnapshot();
        snapshot.State.InitialStateId = "economy-fixture";
        var saved = new TownHostSnapshot(1, TownJson.Serialize(snapshot), world.State.BranchId, 0, 0, "rules", "economy-fixture", [],
            BudgetProfileVersion: profileVersion, ExecutionPurpose: "experiment");
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(saved));
    }

    private string ExpectedBudgetId => profileVersion == "full-demo-v1"
        ? "cozytown-godot-full-demo-20261010-v1" : "cozytown-godot-full-demo-20261011-v2";

    private static TownContent Content()
    {
        string[] ids = ["mina", "eli", "ren", "sora"];
        var places = ids.Select((id, i) => new PlaceDefinition(id, id, new(80 + i * 16, 96), "outside")).ToArray();
        return TownContent.Create(new(256, 192, places, [], [new("outside", new(0, 0, 256, 192))]),
            new(ids.Select((id, i) => new ResidentDefinition(id, id, id, id, "apple", new() { ["apple"] = 1 }, i,
                [new("free", 0, 1440, id, "free", true)])).ToArray(), [new("apple", "Apple")]));
    }

    private TownHostSnapshot Snapshot(string purpose, string kind, string scenario = "baseline")
    {
        var runtime = TownModelRuntimeProfile.Resolve(profileVersion, purpose);
        var world = new TownWorld(Content(), scenario, runtime.BudgetId);
        FrozenModelRequest frozen;
        if (kind == "player_text")
        {
            var host = new TownDialogueHost(world);
            world.SetPlayerPosition(world.State.Residents.Single(r => r.Id == "mina").Position);
            host.Open("mina", []);
            var request = host.Generate(["dialogue"])!;
            frozen = host.Freeze(request, runtime.NewRequestStage(scenario, kind));
            world.MarkPlayerDialogueDispatched(request.PlayerSessionId, request.LogicalRequestId, "player-attempt");
        }
        else
        {
            world.Advance(.001, 0);
            var request = world.GetDecisionRequests().First();
            string body = TownJson.Serialize(new { messages = new[] { new { role = "user", content = request.InputJson } } });
            frozen = new(new(world.State.BranchId, world.SessionId, request.LogicalRequestId, request.ResidentId, request.ActivityRevision),
                runtime.NewRequestStage(scenario, kind), body, false, TimeSpan.FromSeconds(60));
            world.MarkDispatched(request.LogicalRequestId, "npc-attempt");
        }
        return new(1, world.CaptureJson(), world.State.BranchId, 0, 0, "real", scenario,
            new() { [frozen.Identity.LogicalRequestId] = frozen }, BudgetProfileVersion: runtime.Profile.Version, ExecutionPurpose: purpose);
    }

    [TestCase("experiment", "player_text", "baseline")]
    [TestCase("experiment", "player_text", "counterfactual")]
    [TestCase("experiment", "npc_action", "baseline")]
    [TestCase("experiment", "npc_action", "counterfactual")]
    [TestCase("manual", "player_text", "baseline")]
    [TestCase("manual", "npc_action", "counterfactual")]
    public void FullDemoFrozenRequestsRoundTripOnlyUnderTheirOwnPurpose(string purpose, string kind, string scenario)
    {
        var saved = Snapshot(purpose, kind, scenario);
        string path = Path.Combine(Path.GetTempPath(), "cozytown-full-demo-save-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            TownSaveFile.Write(path, saved, s => _ = TownWorld.RestoreCandidate(Content(), s.WorldJson));
            var read = TownSaveFile.Read(path);
            Assert.That(TownJson.Serialize(read), Is.EqualTo(TownJson.Serialize(saved)));
            TownSaveFile.ValidateForRuntime(read, TownModelRuntimeProfile.Resolve(profileVersion, purpose));
            var restored = TownWorld.RestoreCandidate(Content(), read.WorldJson);
            Assert.That(restored.State.BudgetId, Is.EqualTo(ExpectedBudgetId));
            if (kind == "player_text") Assert.That(restored.GetPlayerDialogueRequest()!.IsRecovery, Is.True);
            else Assert.That(restored.GetDecisionRequests().Single(r => r.LogicalRequestId == read.ModelRequests.Keys.Single()).IsRecovery, Is.True);
        }
        finally { File.Delete(path); File.Delete(path + ".previous"); }
    }

    [TestCase("experiment", "manual")]
    [TestCase("manual", "experiment")]
    [TestCase("experiment", "legacy")]
    public void CandidateFromAnotherRuntimeIsRejectedBeforePublication(string savedPurpose, string runtimePurpose)
    {
        var saved = Snapshot(savedPurpose, "npc_action");
        var runtime = TownModelRuntimeProfile.Resolve(runtimePurpose == "legacy" ? "first-slice-v1" : profileVersion, runtimePurpose);
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateForRuntime(saved, runtime));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TerminalNpcRequestCannotBecomeAnUnrelatedPlayerSession(bool changeKind)
    {
        var saved = Snapshot("experiment", "npc_action");
        var pair = saved.ModelRequests.Single();
        var world = TownWorld.RestoreCandidate(Content(), saved.WorldJson);
        world.MarkDispatched(pair.Key, "settled-attempt");
        world.ReceiveResult(new(world.SessionId, pair.Key, "settled-attempt", pair.Value.Identity.ResidentId,
            new("wait", null, null, null, 0, 0, null, "wait"), "fixed_model", null));
        world.Advance(0, 0);
        Assert.That(world.State.TerminalRequestIds, Does.Contain(pair.Key));
        saved = saved with { WorldJson = world.CaptureJson(), SourceRunDirectoryName = world.State.BranchId };
        Assert.DoesNotThrow(() => TownSaveFile.ValidateHostSnapshot(saved));
        var invalid = pair.Value with { Stage = changeKind ? ModelStage.PlayerDialogue : pair.Value.Stage,
            Identity = pair.Value.Identity with { Kind = changeKind ? "player_text" : "npc_action", PlayerSessionId = "unrelated-player-session" } };
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(saved with { ModelRequests = new() { [pair.Key] = invalid } }));
    }

    [TestCase("experiment", "npc_action", ModelStage.PlayerDialogue)]
    [TestCase("experiment", "npc_action", ModelStage.Manual)]
    [TestCase("experiment", "npc_action", ModelStage.Counterfactual)]
    [TestCase("experiment", "player_text", ModelStage.Baseline)]
    [TestCase("experiment", "player_text", ModelStage.Manual)]
    [TestCase("manual", "npc_action", ModelStage.Baseline)]
    [TestCase("manual", "player_text", ModelStage.PlayerDialogue)]
    [TestCase("manual", "player_text", (ModelStage)99)]
    public void FrozenRequestCannotBorrowAnotherKindOrPurposeStage(string purpose, string kind, ModelStage wrongStage)
    {
        var saved = Snapshot(purpose, kind);
        var pair = saved.ModelRequests.Single();
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(saved with
        { ModelRequests = new() { [pair.Key] = pair.Value with { Stage = wrongStage } } }));
    }

    [Test]
    public void FullDemoSaveRequiresItsOwnBudgetIdentityAndExplicitProfileAndPurpose()
    {
        var saved = Snapshot("experiment", "player_text");
        var world = TownJson.Deserialize<TownSnapshot>(saved.WorldJson);
        world.State.BudgetId = "cozytown-godot-first-slice-20260920-v1";
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(saved with { WorldJson = TownJson.Serialize(world) }));
        var json = JsonNode.Parse(TownJson.Serialize(saved))!.AsObject();
        json.Remove("budgetProfileVersion");
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(TownJson.Deserialize<TownHostSnapshot>(json.ToJsonString())));
        json = JsonNode.Parse(TownJson.Serialize(saved))!.AsObject();
        json.Remove("executionPurpose");
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(TownJson.Deserialize<TownHostSnapshot>(json.ToJsonString())));
    }

    [Test]
    public void LegacySaveWithoutNewOptionalFieldsRetainsItsOldContract()
    {
        var world = new TownWorld(Content());
        var saved = new TownHostSnapshot(1, world.CaptureJson(), world.State.BranchId, 0, 0, "rules", "baseline", []);
        var json = JsonNode.Parse(TownJson.Serialize(saved))!.AsObject();
        json.Remove("budgetProfileVersion"); json.Remove("executionPurpose");
        var read = TownJson.Deserialize<TownHostSnapshot>(json.ToJsonString());
        Assert.That(read.BudgetProfileVersion, Is.EqualTo("first-slice-v1"));
        Assert.That(read.ExecutionPurpose, Is.EqualTo("legacy"));
        TownSaveFile.ValidateForRuntime(read, TownModelRuntimeProfile.Resolve("first-slice-v1", "legacy"));
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateForRuntime(read, TownModelRuntimeProfile.Resolve(profileVersion, "experiment")));
    }

    [TestCase("experiment")]
    [TestCase("manual")]
    public void FixedFullDemoSaveKeepsTheSameProfileAndPurposeWithoutProviderRequests(string purpose)
    {
        var world = new TownWorld(Content(), budgetId: ExpectedBudgetId);
        var saved = new TownHostSnapshot(1, world.CaptureJson(), world.State.BranchId, 0, 0, "rules", "baseline", [],
            BudgetProfileVersion: profileVersion, ExecutionPurpose: purpose);
        TownSaveFile.ValidateForRuntime(saved, TownModelRuntimeProfile.Resolve(profileVersion, purpose));
    }
}
