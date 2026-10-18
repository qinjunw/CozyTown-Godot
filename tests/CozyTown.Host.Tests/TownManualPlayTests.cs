using CozyTown.Core.Town;
using CozyTown.Game;
using CozyTown.Model;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

public sealed class TownManualPlayTests
{
    [Test]
    public void ManualPlayAdmitsNpcActionsAndFourResidentTextsUnderOneManualIdentity()
    {
        var runtime = TownModelRuntimeProfile.Resolve("manual-play-v1", "manual");
        var world = new TownWorld(TownPlayerDialogueReplayTests.TownPlayerDialogueTestsContent(), budgetId: runtime.BudgetId);
        var dialogue = new TownDialogueHost(world);
        foreach (string resident in new[] { "mina", "eli", "ren", "sora" })
        {
            world.SetPlayerPosition(world.State.Residents.Single(r => r.Id == resident).Position);
            Assert.That(dialogue.Open(resident, []), Is.True);
            var request = dialogue.Generate(["dialogue"])!;
            var frozen = dialogue.Freeze(request, runtime.NewRequestStage("baseline", "player_text"));
            Assert.That(frozen.Identity.ResidentId, Is.EqualTo(resident));
            Assert.That(frozen.Stage, Is.EqualTo(ModelStage.Manual));
            dialogue.Close();
        }
        world.Advance(.001, 0);
        Assert.That(world.GetDecisionRequests(), Is.Not.Empty);
        Assert.Multiple(() =>
        {
            Assert.That(runtime.BudgetId, Is.EqualTo("cozytown-godot-manual-play-20261017-v1"));
            Assert.That(runtime.ReplayProfile.Version, Is.EqualTo("manual-play-v1"));
            Assert.That(runtime.ReplayProfile.BudgetId, Is.EqualTo(world.GetView().BudgetId));
            Assert.That(runtime.NewRequestStage("counterfactual", "npc_action"), Is.EqualTo(ModelStage.Manual));
            Assert.That(runtime.RealPlayerDialogueEnabled, Is.True);
            Assert.That(runtime.IsFullDemo, Is.False);
        });
    }

    [Test]
    public void ManualPlaySaveRejectsAnOfflineFixtureScenario()
    {
        var runtime = TownModelRuntimeProfile.Resolve("manual-play-v1", "manual");
        var world = new TownWorld(TownPlayerDialogueReplayTests.TownPlayerDialogueTestsContent(), budgetId: runtime.BudgetId);
        var snapshot = world.CaptureSnapshot();
        snapshot.State.InitialStateId = "economy-fixture";
        var saved = new TownHostSnapshot(1, TownJson.Serialize(snapshot), world.State.BranchId, 0, 0, "rules", "economy-fixture", [],
            BudgetProfileVersion: "manual-play-v1", ExecutionPurpose: "manual");
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(saved));
    }

    [TestCase("legacy")]
    [TestCase("experiment")]
    [TestCase("")]
    public void ManualPlayRequiresExplicitManualPurpose(string purpose)
    {
        Assert.Throws<InvalidDataException>(() => TownModelRuntimeProfile.Resolve("manual-play-v1", purpose));
    }

    [TestCase("npc_action")]
    [TestCase("player_text")]
    public void WaitingManualRequestRoundTripsAndRecoversUnderItsOwnStage(string kind)
    {
        var content = TownPlayerDialogueReplayTests.TownPlayerDialogueTestsContent();
        var runtime = TownModelRuntimeProfile.Resolve("manual-play-v1", "manual");
        var world = new TownWorld(content, budgetId: runtime.BudgetId);
        FrozenModelRequest frozen;
        if (kind == "player_text")
        {
            var host = new TownDialogueHost(world);
            host.Open("mina", []);
            var request = host.Generate(["dialogue"])!;
            frozen = host.Freeze(request, runtime.NewRequestStage("baseline", kind));
            host.Acknowledge(request.PlayerSessionId, request.LogicalRequestId, "original-attempt");
        }
        else
        {
            world.Advance(.001, 0);
            var request = world.GetDecisionRequests().First();
            frozen = new(new(world.State.BranchId, world.SessionId, request.LogicalRequestId, request.ResidentId, request.ActivityRevision),
                runtime.NewRequestStage("baseline", kind), TownModelProtocol.BuildRequest(request), false, TimeSpan.FromSeconds(60));
            world.MarkDispatched(request.LogicalRequestId, "original-attempt");
        }
        var saved = new TownHostSnapshot(1, world.CaptureJson(), world.State.BranchId, 0, 0, "real", "baseline",
            new() { [frozen.Identity.LogicalRequestId] = frozen }, BudgetProfileVersion: "manual-play-v1", ExecutionPurpose: "manual");
        string path = Path.Combine(Path.GetTempPath(), "cozytown-manual-save-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            TownSaveFile.Write(path, saved, s => _ = TownWorld.RestoreCandidate(content, s.WorldJson));
            var read = TownSaveFile.Read(path);
            TownSaveFile.ValidateForRuntime(read, runtime);
            var restored = TownWorld.RestoreCandidate(content, read.WorldJson);
            Assert.That(restored.GetView().BudgetId, Is.EqualTo("cozytown-godot-manual-play-20261017-v1"));
            Assert.That(read.ModelRequests[frozen.Identity.LogicalRequestId].Stage, Is.EqualTo(ModelStage.Manual));
            Assert.That(kind == "player_text" ? restored.GetPlayerDialogueRequest()!.IsRecovery
                : restored.GetDecisionRequests().Single(r => r.LogicalRequestId == frozen.Identity.LogicalRequestId).IsRecovery, Is.True);
            foreach (ModelStage wrong in new[] { ModelStage.Baseline, ModelStage.Counterfactual, ModelStage.PlayerDialogue })
                Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(read with
                { ModelRequests = new() { [frozen.Identity.LogicalRequestId] = frozen with { Stage = wrong } } }));
            Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(read with { ExecutionPurpose = "experiment" }));
        }
        finally { File.Delete(path); File.Delete(path + ".previous"); }
    }

    [TestCase("first-slice-v1", "legacy")]
    [TestCase("full-demo-v1", "manual")]
    [TestCase("full-demo-v2", "manual")]
    public void CrossBudgetSaveIsRejectedBeforeCurrentWorldCanBeReplaced(string oldVersion, string oldPurpose)
    {
        var content = TownPlayerDialogueReplayTests.TownPlayerDialogueTestsContent();
        var manual = TownModelRuntimeProfile.Resolve("manual-play-v1", "manual");
        var older = TownModelRuntimeProfile.Resolve(oldVersion, oldPurpose);
        var current = new TownWorld(content, budgetId: manual.BudgetId);
        current.Advance(.5, 0);
        var oldWorld = new TownWorld(content, budgetId: older.BudgetId);
        var oldSave = new TownHostSnapshot(1, oldWorld.CaptureJson(), oldWorld.State.BranchId, 0, 0, "rules", "baseline", [],
            BudgetProfileVersion: oldVersion, ExecutionPurpose: oldPurpose);
        var currentSave = new TownHostSnapshot(1, current.CaptureJson(), current.State.BranchId, 0, 0, "rules", "baseline", [],
            BudgetProfileVersion: "manual-play-v1", ExecutionPurpose: "manual");
        string before = TownJson.Serialize(current.GetView());
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateForRuntime(oldSave, manual));
        Assert.That(TownJson.Serialize(current.GetView()), Is.EqualTo(before));
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateForRuntime(currentSave, older));
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(currentSave with { BudgetProfileVersion = oldVersion, ExecutionPurpose = oldPurpose }));
        var isolated = TownWorld.RestoreCandidate(content, currentSave.WorldJson);
        isolated.Advance(.5, 0);
        Assert.That(TownJson.Serialize(current.GetView()), Is.EqualTo(before));
    }
}
