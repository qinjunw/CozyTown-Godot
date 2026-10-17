using CozyTown.Core.Town;
using CozyTown.Game;
using CozyTown.Model;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

[TestFixture]
public sealed class TownModelRuntimeTests
{
    [TestCase("manual", "manual")]
    [TestCase("experiment", "experiment")]
    public void RuntimePurposeSelectsProviderPolicyWithoutAcceptingItFromLocalConfiguration(string purpose, string expected)
    {
        string untrusted = """
            {"credentialFile":"C:\\external\\credentials.json","ledgerFile":"C:\\external\\ledger.json",
             "budgetProfileVersion":"full-demo-v2","executionPurpose":"manual"}
            """;
        Assert.Throws<System.Text.Json.JsonException>(() => TownJson.Deserialize<TownModelRuntimeSettings>(untrusted));
        var local = new TownModelRuntimeSettings("C:\\external\\credentials.json", "C:\\external\\ledger.json", BudgetProfileVersion: "full-demo-v2");
        Assert.That(local.ToOptions().ExecutionPurpose, Is.EqualTo("legacy"));
        Assert.That(TownModelRuntimeProfile.Resolve("full-demo-v2", purpose).ConfigureProvider(local.ToOptions()).ExecutionPurpose, Is.EqualTo(expected));
    }

    [Test]
    public void DispatchAvailabilityRetainsStopCauseAndDistinguishesTemporaryOccupancyFromExhaustion()
    {
        var profile = ModelBudgetProfile.FullDemoV2;
        var gate = new TownModelDispatchState();
        var empty = new BudgetSnapshot(profile.BudgetId, "CNY", profile.LimitMicros, 0, 0, 0, 0, null, []);
        Assert.That(gate.UnavailableReason(empty with { StopReason = "fingerprint_not_registered" }, profile, ModelStage.Manual), Is.EqualTo("fingerprint_not_registered"));
        var held = new BudgetAttempt("one", "manual", "request", false, "reserved", true, 2110000, 0, null, null, null);
        Assert.That(gate.UnavailableReason(empty with { ReservedMicros = 4220000, SlotsHeld = 2, Attempts = [held] }, profile, ModelStage.Manual), Is.EqualTo("concurrency_limit"));
        Assert.That(gate.UnavailableReason(empty with { SettledMicros = 1000000, ReservedMicros = 2110000, SlotsHeld = 1, Attempts = [held] }, profile, ModelStage.Manual), Is.EqualTo("money_wait"));
        Assert.That(gate.UnavailableReason(empty, profile, ModelStage.Manual), Is.Null);
        var spent = held with { SlotHeld = false, ReservedCostMicros = 0, Status = "settled" };
        Assert.That(gate.UnavailableReason(empty with { Attempts = Enumerable.Repeat(spent, 8).ToArray(), TotalAttempts = 8 }, profile, ModelStage.Manual), Is.EqualTo("stage_limit"));
        gate.ObserveFailure("account_currency_unverified");
        Assert.That(gate.UnavailableReason(empty, profile, ModelStage.Manual), Is.EqualTo("account_currency_unverified"));
        Assert.That(TownModelDispatchState.Temporary("concurrency_limit"), Is.True);
        Assert.That(TownModelDispatchState.Temporary("money_wait"), Is.True);
        Assert.That(TownModelDispatchState.Temporary("stage_limit"), Is.False);
    }

    [Test]
    public void StoppedNpcOpportunityUsesExplicitRulesOnceAndKeepsWorldReplayable()
    {
        var content = TownPlayerDialogueReplayTests.TownPlayerDialogueTestsContent();
        var world = new TownWorld(content); var input = new TownInputHost(content, world);
        var recording = new TownRecording(content, world, input);
        world.Advance(.01, 0); var request = world.GetDecisionRequests().First();
        Assert.That(TownModelDispatchState.CompleteNpcFallback(world, request, "fingerprint_not_registered"), Is.True);
        world.Advance(.01, 0);
        Assert.That(TownModelDispatchState.CompleteNpcFallback(world, request, "fingerprint_not_registered"), Is.False);
        Assert.That(world.GetView().PendingDecisions.Any(p => p.Request.LogicalRequestId == request.LogicalRequestId), Is.False);
        Assert.That(TownReplay.Run(recording.Complete()).Passed, Is.True);
    }
    [TestCase("full-demo-v1", "cozytown-godot-full-demo-20261010-v1")]
    [TestCase("full-demo-v2", "cozytown-godot-full-demo-20261011-v2")]
    public void CompleteExperimentSelectsItsOwnIdentityAndBothReplayAndRequestStages(string version, string budgetId)
    {
        var runtime = TownModelRuntimeProfile.Resolve(version, "experiment");
        Assert.That(runtime.BudgetId, Is.EqualTo(budgetId));
        Assert.That(runtime.ReplayProfile.Version, Is.EqualTo(version));
        Assert.That(runtime.NewRequestStage("baseline", "npc_action"), Is.EqualTo(ModelStage.Baseline));
        Assert.That(runtime.NewRequestStage("counterfactual", "npc_action"), Is.EqualTo(ModelStage.Counterfactual));
        Assert.That(runtime.NewRequestStage("baseline", "player_text"), Is.EqualTo(ModelStage.PlayerDialogue));
        Assert.That(runtime.RealPlayerDialogueEnabled, Is.True);
    }

    [TestCase("full-demo-v1")]
    [TestCase("full-demo-v2")]
    public void FinalManualPlayRoutesBothKindsToReservedManualStage(string version)
    {
        var runtime = TownModelRuntimeProfile.Resolve(version, "manual");
        Assert.That(runtime.NewRequestStage("baseline", "npc_action"), Is.EqualTo(ModelStage.Manual));
        Assert.That(runtime.NewRequestStage("counterfactual", "player_text"), Is.EqualTo(ModelStage.Manual));
    }

    [TestCase("full-demo-v1", "legacy")]
    [TestCase("full-demo-v2", "legacy")]
    [TestCase("full-demo-v2", "other")]
    [TestCase("first-slice-v1", "experiment")]
    [TestCase("first-slice-v1", "manual")]
    [TestCase("full-demo-v1", "other")]
    [TestCase("full-demo-v3", "experiment")]
    [TestCase("first-slice-budget-v1", "legacy")]
    public void UnregisteredOrCrossedProfileAndPurposeAreRejected(string profile, string purpose)
    {
        Assert.Throws<InvalidDataException>(() => TownModelRuntimeProfile.Resolve(profile, purpose));
    }

    [TestCase("baseline", "other")]
    [TestCase("baseline", "")]
    [TestCase("economy-fixture", "npc_action")]
    [TestCase("other", "player_text")]
    public void InvalidRequestKindOrScenarioCannotAcquireAnAuthorizedStage(string scenario, string kind)
    {
        var runtime = TownModelRuntimeProfile.Resolve("full-demo-v1", "manual");
        Assert.Throws<InvalidDataException>(() => runtime.NewRequestStage(scenario, kind));
    }

    [Test]
    public void LegacyConfigurationKeepsOldReplayMappingAndUnavailableRealPlayerGuard()
    {
        var runtime = TownModelRuntimeProfile.Resolve("first-slice-v1", "legacy");
        Assert.That(runtime.ReplayProfile.Version, Is.EqualTo("first-slice-budget-v1"));
        Assert.That(runtime.RealPlayerDialogueEnabled, Is.False);
        Assert.That(runtime.NewRequestStage("counterfactual", "player_text"), Is.EqualTo(ModelStage.Counterfactual));
    }

    [Test]
    public void ExplicitModelConfigurationPreservesFingerprintAndProfileWithoutOpeningFiles()
    {
        var local = TownJson.Deserialize<TownModelRuntimeSettings>("""
            {"credentialFile":"C:\\external\\credentials.json","ledgerFile":"C:\\external\\full-demo-ledger.json",
             "expectedSystemFingerprint":"frozen-provider","budgetProfileVersion":"full-demo-v1"}
            """);
        Assert.That(local.ToOptions(), Is.EqualTo(new DeepSeekOptions("C:\\external\\credentials.json",
            "C:\\external\\full-demo-ledger.json", "frozen-provider", "full-demo-v1")));
        var old = TownJson.Deserialize<TownModelRuntimeSettings>("""
            {"credentialFile":"C:\\external\\credentials.json","ledgerFile":"C:\\external\\old-ledger.json"}
            """);
        Assert.That(old.ToOptions().BudgetProfileVersion, Is.EqualTo("first-slice-v1"));
        Assert.That(old.ToOptions().ExpectedSystemFingerprint, Is.EqualTo("aeb56401ca74e127821c4f9126dcb669"));
    }

    [TestCase(null, "C:\\external\\ledger.json", "frozen")]
    [TestCase("C:\\external\\credentials.json", null, "frozen")]
    [TestCase("credentials.json", "C:\\external\\ledger.json", "frozen")]
    [TestCase("C:\\external\\credentials.json", "ledger.json", "frozen")]
    [TestCase("C:\\external\\credentials.json", "C:\\external\\ledger.json", " ")]
    public void InvalidLocalConfigurationIsRejectedBeforeAnyFileIsOpened(string? credential, string? ledger, string fingerprint)
    {
        Assert.Throws<InvalidDataException>(() => new TownModelRuntimeSettings(credential!, ledger!, fingerprint, "full-demo-v1").ToOptions());
    }
}
