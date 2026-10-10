using CozyTown.Core.Town;
using CozyTown.Game;
using CozyTown.Model;
using NUnit.Framework;

namespace CozyTown.Host.Tests;

[TestFixture]
public sealed class TownDialogueHostTests
{
    private static TownContent Content()
    {
        string[] ids = ["mina", "eli", "ren", "sora"];
        var places = ids.Select((id, i) => new PlaceDefinition(id, id, new(80 + i * 16, 96), "outside")).ToArray();
        return TownContent.Create(new(256, 192, places, [], [new("outside", new(0, 0, 256, 192))]),
            new(ids.Select((id, i) => new ResidentDefinition(id, id, id, id, "apple", new() { ["apple"] = 1 }, i,
                [new("free", 0, 1440, id, "free", true)])).ToArray(), [new("apple", "苹果")]));
    }

    [TestCase("mina")]
    [TestCase("eli")]
    [TestCase("ren")]
    [TestCase("sora")]
    public void NormalInteractionShowsFixedContentThenExplicitModelTextAndOverlayDelaysPresentation(string resident)
    {
        var world = new TownWorld(Content());
        world.SetPlayerPosition(world.State.Residents.Single(r => r.Id == resident).Position);
        var host = new TownDialogueHost(world);
        Assert.That(host.Open(resident, []), Is.True);
        Assert.That(host.Current!.Displays.Single().Source, Is.EqualTo("fixed_dialogue"));
        var request = host.Generate(["dialogue"])!;
        host.CompleteFixed(request, "{\"text\":\"今天也想慢慢走一走。\"}");
        host.Present(["dialogue", "focus"]);
        Assert.That(host.Current!.Displays.Count, Is.EqualTo(1));
        host.Present(["dialogue"]);
        Assert.That(host.Current!.Displays.Last().Text, Is.EqualTo("今天也想慢慢走一走。"));
        Assert.That(host.Generate(["dialogue"]), Is.Null);
        host.Close();
        world.Advance(0, 0);
        Assert.That(world.State.Residents.Single(r => r.Id == resident).Memories.Count(m => m.Text == "今天也想慢慢走一走。"), Is.EqualTo(1));
    }

    [Test]
    public void HostSaveValidatesFrozenPlayerInputAndRestoresWaitingOrSealedPanel()
    {
        var content = Content(); var world = new TownWorld(content); var host = new TownDialogueHost(world);
        host.Open("mina", []);
        var request = host.Generate(["dialogue"])!;
        var frozen = host.Freeze(request, ModelStage.Baseline);
        Assert.That(frozen.Identity.Kind, Is.EqualTo("player_text"));
        Assert.That(frozen.Identity.PlayerSessionId, Is.EqualTo(request.PlayerSessionId));
        world.MarkPlayerDialogueDispatched(request.PlayerSessionId, request.LogicalRequestId, "attempt");
        world.Advance(0, 10, true);
        var saved = new TownHostSnapshot(1, world.CaptureJson(), world.State.BranchId, 0, .02, "real", "baseline", new() { [request.LogicalRequestId] = frozen });
        Assert.DoesNotThrow(() => TownSaveFile.ValidateHostSnapshot(saved));
        var resumed = TownWorld.RestoreCandidate(content, saved.WorldJson);
        Assert.That(new TownDialogueHost(resumed).Current!.Request!.RemainingWaitSeconds, Is.EqualTo(50));
        var missing = saved with { ModelRequests = [] };
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(missing));
        host.Complete(request, world.SessionId, "attempt", "{\"text\":\"同一段封存回复。\"}", "fixed_model", null, 11);
        var sealedSave = saved with { WorldJson = world.CaptureJson() };
        Assert.DoesNotThrow(() => TownSaveFile.ValidateHostSnapshot(sealedSave));
        resumed = TownWorld.RestoreCandidate(content, sealedSave.WorldJson);
        var restoredHost = new TownDialogueHost(resumed);
        Assert.That(resumed.GetPlayerDialogueRequest(), Is.Null);
        Assert.That(restoredHost.Present(["dialogue", "menu"]), Is.False);
        Assert.That(restoredHost.Present(["dialogue"]), Is.True);
        Assert.That(restoredHost.Current!.Displays.Last().Text, Is.EqualTo("同一段封存回复。"));
    }

    [TestCase("menu")]
    [TestCase("focus")]
    [TestCase("loading")]
    [TestCase("fault")]
    public void EveryOverlayBlocksGenerationAndPresentationButSealedReplySurvives(string reason)
    {
        var world = new TownWorld(Content()); var host = new TownDialogueHost(world);
        host.Open("mina", []);
        Assert.That(host.Generate(["dialogue", reason]), Is.Null);
        var request = host.Generate(["dialogue"])!;
        host.CompleteFixed(request, "{\"text\":\"稍后再显示这一段。\"}");
        Assert.That(host.Present(["dialogue", reason]), Is.False);
        world.Advance(0, 4, true);
        Assert.That(world.State.Residents.SelectMany(r => r.Memories), Is.Empty);
        Assert.That(host.Present(["dialogue"]), Is.True);
        Assert.That(host.Current!.Displays.Last().Text, Is.EqualTo("稍后再显示这一段。"));
    }

    [TestCase("{}", null, "player_text_schema_invalid")]
    [TestCase("{\"text\":\"回应\",\"action\":\"trade\"}", null, "player_text_schema_invalid")]
    [TestCase("{\"text\":\"甲\",\"text\":\"乙\"}", null, "player_text_schema_invalid")]
    [TestCase("{\"text\":null}", null, "player_text_schema_invalid")]
    [TestCase("{\"text\":\"\"}", null, "player_text_schema_invalid")]
    [TestCase("{\"text\":\"句一\\n句二\"}", null, "player_text_schema_invalid")]
    [TestCase("{}", "total_limit", "total_limit")]
    [TestCase("{}", "request_timeout", "request_timeout")]
    [TestCase("{}", "player_dialogue_profile_unavailable", "player_dialogue_profile_unavailable")]
    public void InvalidOrUnavailableGenerationShowsReasonAndFixedFeedbackWithoutNewRequest(string json, string? error, string expected)
    {
        var world = new TownWorld(Content()); var host = new TownDialogueHost(world);
        host.Open("mina", []); var request = host.Generate(["dialogue"])!;
        host.CompleteFixed(request, json, error); host.Present(["dialogue"]);
        var display = host.Current!.Displays.Last();
        Assert.That(display.Source, Is.EqualTo("rule_fallback"));
        Assert.That(display.Error, Is.EqualTo(expected));
        Assert.That(display.Text, Is.EqualTo(host.Current.FixedText));
        Assert.That(TownDialogueHost.FailureText(display.Error), Is.Not.Empty);
        Assert.That(host.Generate(["dialogue"]), Is.Null);
        Assert.That(world.GetPlayerDialogueRequest(), Is.Null);
    }

    [Test]
    public void FrozenPlayerRequestCannotBeRestoredAsNpcOrWithDifferentContext()
    {
        var world = new TownWorld(Content()); var host = new TownDialogueHost(world);
        host.Open("mina", []); var request = host.Generate(["dialogue"])!;
        var frozen = host.Freeze(request, ModelStage.Baseline);
        world.MarkPlayerDialogueDispatched(request.PlayerSessionId, request.LogicalRequestId, "attempt");
        var saved = new TownHostSnapshot(1, world.CaptureJson(), world.State.BranchId, 0, 0, "real", "baseline", new() { [request.LogicalRequestId] = frozen });
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(saved with
        { ModelRequests = new() { [request.LogicalRequestId] = frozen with { Identity = frozen.Identity with { Kind = "npc_action", PlayerSessionId = null } } } }));
        Assert.Throws<InvalidDataException>(() => TownSaveFile.ValidateHostSnapshot(saved with
        { ModelRequests = new() { [request.LogicalRequestId] = frozen with { InputJson = TownPlayerDialogueProtocol.BuildRequest(request with { InputJson = "{}" }) } } }));
    }

    [Test]
    public void PlayerAndRecoveryShareNpcReservationsAndReadingSaveNeverRefundsLedger()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cozytown-dialogue-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "ledger.json");
            File.WriteAllText(path, TownJson.Serialize(new
            {
                schemaVersion = 1, budgetId = PersistentBudgetLedger.BudgetId, currency = "CNY", limitMicros = 5_000_000,
                reserveMicros = 2_110_000, totalLimit = 48, concurrentLimit = 2,
                stageLimits = new Dictionary<string, int> { ["probe"] = 4, ["baseline"] = 22, ["counterfactual"] = 22 },
                stopReason = (string?)null, attempts = Array.Empty<object>()
            }));
            File.WriteAllText(path + ".lock", PersistentBudgetLedger.BudgetId);
            var ledger = PersistentBudgetLedger.OpenExisting(path);
            var npcA = ledger.Reserve(ModelStage.Baseline, "npc-a");
            var npcB = ledger.Reserve(ModelStage.Baseline, "npc-b");
            var content = Content(); var world = new TownWorld(content); var host = new TownDialogueHost(world);
            host.Open("mina", []); var request = host.Generate(["dialogue"])!;
            Assert.That(Assert.Throws<ModelException>(() => ledger.Reserve(ModelStage.Baseline, request.LogicalRequestId))!.Code, Is.EqualTo("concurrency_limit"));
            Assert.That(ledger.Snapshot().ReservedMicros, Is.EqualTo(4_220_000));
            Assert.That(ledger.Snapshot().TotalAttempts, Is.EqualTo(2));
            ledger.Settle(npcA.AttemptId, 100, 10); ledger.Settle(npcB.AttemptId, 100, 10);
            var playerAttempt = ledger.Reserve(ModelStage.Baseline, request.LogicalRequestId);
            world.MarkPlayerDialogueDispatched(request.PlayerSessionId, request.LogicalRequestId, playerAttempt.AttemptId);
            string save = world.CaptureJson();
            world.ClosePlayerDialogue(request.PlayerSessionId);
            ledger.Settle(playerAttempt.AttemptId, 100, 10);
            long fee = ledger.Snapshot().SettledMicros;
            var resumed = TownWorld.RestoreCandidate(content, save).GetPlayerDialogueRequest()!;
            Assert.That(resumed.IsRecovery, Is.True);
            Assert.That(ledger.Snapshot().SettledMicros, Is.EqualTo(fee));
            var recovery = ledger.Reserve(ModelStage.Baseline, resumed.LogicalRequestId, resumed.IsRecovery);
            ledger.Settle(recovery.AttemptId, 100, 10);
            var duplicateRestore = TownWorld.RestoreCandidate(content, save).GetPlayerDialogueRequest()!;
            var reopened = PersistentBudgetLedger.OpenExisting(path);
            Assert.That(Assert.Throws<ModelException>(() => reopened.Reserve(ModelStage.Baseline, duplicateRestore.LogicalRequestId, duplicateRestore.IsRecovery))!.Code, Is.EqualTo("recovery_limit"));
            Assert.That(reopened.Snapshot().TotalAttempts, Is.EqualTo(4));
            Assert.That(reopened.Snapshot().SettledMicros, Is.EqualTo(1120));
        }
        finally
        {
            string full = Path.GetFullPath(directory);
            if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(full).StartsWith("cozytown-dialogue-budget-", StringComparison.Ordinal))
                throw new InvalidOperationException("Budget fixture cleanup is outside its temporary directory.");
            Directory.Delete(full, true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PersistentTerminalFactPreservesSavedFixedDisplayButCannotGenerateOrImportFutureModelMemory(bool displayed)
    {
        string path = Path.Combine(Path.GetTempPath(), "cozytown-dialogue-terminal-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var content = Content(); var world = new TownWorld(content);
            var host = new TownDialogueHost(world, new TownPlayerDialogueRegistry(path));
            host.Open("mina", []); var request = host.Generate(["dialogue"])!;
            string earlierSave = world.CaptureJson();
            if (displayed) { host.CompleteFixed(request, "{\"text\":\"已经显示的旧分支文字。\"}"); host.Present(["dialogue"]); }
            else host.Close();
            world.Advance(0, 0);
            var restored = TownWorld.RestoreCandidate(content, earlierSave);
            var freshProcessHost = new TownDialogueHost(restored, new TownPlayerDialogueRegistry(path));
            freshProcessHost.ReconcileRestored();
            Assert.That(freshProcessHost.Current!.ResidentId, Is.EqualTo("mina"));
            Assert.That(freshProcessHost.Current.IsOpen, Is.True);
            Assert.That(freshProcessHost.Current.TerminalReason, Does.StartWith("persisted_"));
            Assert.That(restored.GetPlayerDialogueRequest(), Is.Null);
            Assert.That(freshProcessHost.Generate(["dialogue"]), Is.Null);
            string fixedText = freshProcessHost.Current.FixedText;
            freshProcessHost.Close(); restored.Advance(0, 0);
            restored.Advance(0, 0);
            var memories = restored.State.Residents.Single(r => r.Id == "mina").Memories;
            Assert.That(memories.Count(m => m.Text == fixedText), Is.EqualTo(1));
            Assert.That(memories.Count(m => m.Source == "fixed_dialogue"), Is.EqualTo(1));
            Assert.That(memories.Any(m => m.Text == "已经显示的旧分支文字。"), Is.False);
            Assert.DoesNotThrow(() => TownWorld.RestoreCandidate(content, restored.CaptureJson()));
        }
        finally
        {
            File.Delete(path); File.Delete(path + ".previous"); File.Delete(path + ".lock");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailedTerminalJournalProtectsCurrentPanelBeforeCloseOrDisplay(bool display)
    {
        var world = new TownWorld(Content()); var host = new TownDialogueHost(world, recordTerminal: _ => throw new IOException("Injected journal failure."));
        host.Open("mina", []); var request = host.Generate(["dialogue"])!;
        if (display) host.CompleteFixed(request, "{\"text\":\"不能提前显示。\"}");
        string before = TownJson.Serialize(world.State);
        Assert.Throws<IOException>(() => { if (display) host.Present(["dialogue"]); else host.Close(); });
        Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
    }

    [Test]
    public void RegistryWriteFailureProtectsPanelAndMissingRegistryIsNeverRecreated()
    {
        string path = Path.Combine(Path.GetTempPath(), "cozytown-dialogue-terminal-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var registry = new TownPlayerDialogueRegistry(path);
            var world = new TownWorld(Content()); var host = new TownDialogueHost(world, registry);
            host.Open("mina", []); host.Generate(["dialogue"]);
            string before = TownJson.Serialize(world.State);
            File.Delete(path);
            Assert.Throws<InvalidDataException>(() => host.Close());
            Assert.That(TownJson.Serialize(world.State), Is.EqualTo(before));
            Assert.Throws<InvalidDataException>(() => new TownPlayerDialogueRegistry(path));
            Assert.That(File.Exists(path), Is.False);
        }
        finally { File.Delete(path); File.Delete(path + ".previous"); File.Delete(path + ".lock"); }
    }
}
