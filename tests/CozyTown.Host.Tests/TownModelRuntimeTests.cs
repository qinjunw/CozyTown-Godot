using CozyTown.Core.Town;
using CozyTown.Game;
using CozyTown.Model;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

[TestFixture]
public sealed class TownModelRuntimeTests
{
    [Test]
    public void CompleteExperimentSelectsItsOwnIdentityAndBothReplayAndRequestStages()
    {
        var runtime = TownModelRuntimeProfile.Resolve("full-demo-v1", "experiment");
        Assert.That(runtime.BudgetId, Is.EqualTo("cozytown-godot-full-demo-20261010-v1"));
        Assert.That(runtime.ReplayProfile.Version, Is.EqualTo("full-demo-v1"));
        Assert.That(runtime.NewRequestStage("baseline", "npc_action"), Is.EqualTo(ModelStage.Baseline));
        Assert.That(runtime.NewRequestStage("counterfactual", "npc_action"), Is.EqualTo(ModelStage.Counterfactual));
        Assert.That(runtime.NewRequestStage("baseline", "player_text"), Is.EqualTo(ModelStage.PlayerDialogue));
        Assert.That(runtime.RealPlayerDialogueEnabled, Is.True);
    }

    [Test]
    public void FinalManualPlayRoutesBothKindsToReservedManualStage()
    {
        var runtime = TownModelRuntimeProfile.Resolve("full-demo-v1", "manual");
        Assert.That(runtime.NewRequestStage("baseline", "npc_action"), Is.EqualTo(ModelStage.Manual));
        Assert.That(runtime.NewRequestStage("counterfactual", "player_text"), Is.EqualTo(ModelStage.Manual));
    }

    [TestCase("full-demo-v1", "legacy")]
    [TestCase("first-slice-v1", "experiment")]
    [TestCase("first-slice-v1", "manual")]
    [TestCase("full-demo-v1", "other")]
    [TestCase("full-demo-v2", "experiment")]
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
