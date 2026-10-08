using System.Text.Json;
using CozyTown.Core.Town;
using Godot;

namespace CozyTown.Game;

public partial class TownGame
{
    private async Task RunProductionCapacitySmokeAsync()
    {
        _finishing = true;
        var checks = new List<object>();
        var checkNames = new HashSet<string>(StringComparer.Ordinal);
        var expectedOperations = new List<ProductionOperationRecord>();
        var spatialSamples = new List<object>();
        var sampledTargets = new HashSet<string>(StringComparer.Ordinal);
        bool salesConserved = true;
        void Check(string name, bool passed, object? evidence = null)
        {
            if (!checkNames.Add(name)) throw new InvalidDataException("Duplicate production capacity check: " + name);
            checks.Add(new { name, passed, evidence });
            if (!passed) throw new InvalidDataException("Production capacity verification failed: " + name);
        }
        void WalkTo(string placeId, string? targetId = null)
        {
            if (_pauseReasons.Count != 0) throw new InvalidDataException("Close the active panel before following a route.");
            var start = _world.GetView().PlayerPosition;
            var target = _loaded.Content.Place(placeId).Position;
            _playerRoute = _loaded.Content.FindRoute(start, target)
                ?? throw new InvalidDataException("No route to " + placeId);
            var route = _playerRoute.ToArray();
            _playerRouteCursor = 0;
            int firstQuery = _spaceQueries.Count;
            int steps = 0;
            for (; steps < 1600 && _playerRoute is not null && _playerRouteCursor < _playerRoute.Count; steps++) AdvancePlayer(StepSeconds);
            var queries = _spaceQueries.Skip(firstQuery).ToArray();
            FlushWorldEvents();
            _view = _world.GetView(); SyncActors(); RefreshInterface();
            if (_view.PlayerPosition.DistanceTo(target) > .01) throw new InvalidDataException("Route did not reach " + placeId);
            if (targetId is not null)
            {
                bool canInteract = _world.CanInteractWithProductionTarget(targetId);
                if (!canInteract || queries.Length == 0) throw new InvalidDataException("Production route lacks scene queries: " + targetId);
                spatialSamples.Add(new { schemaVersion = 1, targetId, placeId, branchId = _view.BranchId,
                    worldMinute = _view.WorldMinute, start, target, end = _view.PlayerPosition, route,
                    fixedStepSeconds = StepSeconds, steps, canInteract, spaceQueries = queries });
                sampledTargets.Add(targetId);
            }
        }
        ProductionResult PressProduction(Button button)
        {
            if (button.Disabled || !button.IsVisibleInTree()) throw new InvalidDataException("Production button is unavailable.");
            long before = _world.GetView().Economy.InputSequence;
            button.EmitSignal(Godot.Button.SignalName.Pressed);
            if (_fatal) throw new InvalidDataException(_failure);
            var state = _world.State;
            if (state.Economy.InputSequence != before + 1) throw new InvalidDataException("Production button did not publish exactly one command.");
            var receipt = state.Production.Receipts.Values.Single(r => r.InputSequence == state.Economy.InputSequence);
            expectedOperations.Add(new(1, receipt.InputSequence, state.BranchId, state.WorldMinute, receipt.Request, receipt.Result,
                receipt.RandomStateBefore, receipt.RandomStateAfter, state));
            return receipt.Result;
        }
        async Task RestForDay()
        {
            WalkTo(_loaded.Definitions.Farming!.BedPlaceId!);
            await SendEconomyKeyAsync(Key.E);
            if (!_farmingPanel.Visible || _activePlotId is not null || _sleepConfirm.Disabled)
                throw new InvalidDataException("Bed interaction is unavailable.");
            double before = _world.WorldMinute;
            _sleepHours.GetLineEdit().Text = "12";
            _sleepConfirm.EmitSignal(Godot.Button.SignalName.Pressed);
            _sleepConfirm.EmitSignal(Godot.Button.SignalName.Pressed);
            if (_fatal || _world.WorldMinute != before + 1440) throw new InvalidDataException("Sleep did not advance exactly one day.");
            await SendEconomyKeyAsync(Key.Escape);
        }
        void SellTwo(string resourceId)
        {
            var before = _world.GetView().Economy;
            SetTradeDirection("sell");
            _stockButtons[resourceId].EmitSignal(Godot.Button.SignalName.Pressed);
            _tradeQuantity.GetLineEdit().Text = "2";
            if (_tradeButton.Disabled) throw new InvalidDataException("Sale confirmation is unavailable.");
            long sequence = before.InputSequence;
            _tradeButton.EmitSignal(Godot.Button.SignalName.Pressed);
            if (_fatal || _view.Economy.InputSequence != sequence + 1) throw new InvalidDataException("Sale did not publish exactly one command.");
            var result = _view.Economy.Receipts.Values.Single(r => r.InputSequence == _view.Economy.InputSequence).TradeResult!;
            if (!result.Succeeded) throw new InvalidDataException("Fixture sale failed: " + result.Code);
            salesConserved &= EconomyAssetsConserved(before, _view.Economy);
        }
        try
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var definition = _loaded.Definitions.Production!;
            var hen = definition.Chickens.Single();
            var pond = definition.FishingSpots.Single();
            var kitchen = definition.Kitchens.Single();
            var soup = definition.Recipes.Single(r => r.Id == "recipe.vegetable_soup");
            var fixture = new Dictionary<string, int> { ["feed.chicken"] = 2, ["crop.carrot"] = 2,
                ["crop.tomato"] = 2, ["ingredient.salt"] = 2, ["ingredient.flour"] = 2 };
            Check("explicit_offline_capacity_fixture", _scenario == "production-capacity-fixture" && _mode == "rules"
                && _model is null && _modelOptions is null && _view.Economy.Player.Coins == 300 && _view.Economy.Player.SlotCapacity == 5
                && SameInventory(_view.Economy.Player.Inventory, fixture) && _world.GetPlayerInventorySlots().Count(s => s.ResourceId is not null) == 5
                && _view.Production.FishingRandomState == 12345 && _view.Production.FishingDrawCount == 0,
                new { scenario = _scenario, _view.Economy.Player, _view.Production.FishingRandomVersion, _view.Production.FishingRandomState });
            WalkTo(hen.PlaceId, hen.Id);
            await SendEconomyKeyAsync(Key.E);
            Check("chicken_ui_and_route", _productionPanel.Visible && _activeProductionId == hen.Id && _pauseReasons.Contains("production")
                && _feedButton.Visible && _collectButton.Visible && _world.CanInteractWithProductionTarget(hen.Id));
            var fed = PressProduction(_feedButton);
            Check("feeding_keeps_five_slots", fed.Succeeded && _view.Production.Chickens.Single().Fed
                && !_view.Production.Chickens.Single().ProductReady && _view.Economy.Player.Inventory[hen.FeedResourceId] == 1
                && _world.GetPlayerInventorySlots().Count(s => s.ResourceId is not null) == 5);
            string fedAssets = EconomyAssetsJson(_view.Economy);
            string fedChicken = TownJson.Serialize(_view.Production.Chickens);
            var fedAgain = PressProduction(_feedButton);
            Check("already_fed_preserves_assets", !fedAgain.Succeeded && fedAgain.Code == "already_fed"
                && fedAssets == EconomyAssetsJson(_view.Economy) && fedChicken == TownJson.Serialize(_view.Production.Chickens)
                && _productionFeedback.Text.Contains("已经喂过"));
            await SendEconomyKeyAsync(Key.Escape);
            double startMinute = _world.WorldMinute;
            await RestForDay();
            Check("morning_produces_one_egg", _view.WorldMinute == startMinute + 1440 && _view.Farming.LastSettledDay == 1
                && _view.Production.Chickens.Single().ProductReady && !_view.Production.Chickens.Single().Fed
                && _view.Production.Chickens.Single().Generation == 1 && _view.Production.Chickens.Single().CollectedCount == 0
                && !_view.Economy.Player.Inventory.ContainsKey(hen.ProductResourceId));
            string pendingChicken = TownJson.Serialize(_view.Production.Chickens);
            await RestForDay();
            await RestForDay();
            Check("unclaimed_egg_does_not_stack", _view.WorldMinute == startMinute + 3 * 1440 && _view.Farming.LastSettledDay == 3
                && pendingChicken == TownJson.Serialize(_view.Production.Chickens) && !_view.Economy.Player.Inventory.ContainsKey(hen.ProductResourceId));
            WalkTo(hen.PlaceId, hen.Id);
            await SendEconomyKeyAsync(Key.E);
            string fullAssets = EconomyAssetsJson(_view.Economy);
            var pendingFeed = PressProduction(_feedButton);
            Check("pending_feed_preserves_assets", !pendingFeed.Succeeded && pendingFeed.Code == "product_pending"
                && fullAssets == EconomyAssetsJson(_view.Economy) && pendingChicken == TownJson.Serialize(_view.Production.Chickens));
            var fullCollect = PressProduction(_collectButton);
            Check("full_collect_preserves_egg_and_assets", !fullCollect.Succeeded && fullCollect.Code == "inventory_full"
                && _productionFeedback.Text.Contains("背包装不下") && fullAssets == EconomyAssetsJson(_view.Economy)
                && pendingChicken == TownJson.Serialize(_view.Production.Chickens));
            string? failureScreenshot = await CaptureEconomyScreenshotAsync(null);
            SaveWorld();
            var diskSave = TownSaveFile.Read(_savePath);
            var saved = TownJson.Deserialize<TownSnapshot>(diskSave.WorldJson);
            var beforeRestore = _world.State;
            Check("rejected_collect_save_published", !_fatal && diskSave.Stage == "production-capacity-fixture"
                && diskSave.DecisionMode == "rules" && diskSave.SourceRunDirectoryName == beforeRestore.BranchId
                && saved.State.BranchId == beforeRestore.BranchId && saved.State.WorldMinute == beforeRestore.WorldMinute
                && TownJson.Serialize(saved.State.Economy) == TownJson.Serialize(beforeRestore.Economy)
                && TownJson.Serialize(saved.State.Production) == TownJson.Serialize(beforeRestore.Production)
                && TownJson.Serialize(saved.State.Farming) == TownJson.Serialize(beforeRestore.Farming));
            LoadWorld();
            Check("rejected_collect_restored_on_new_branch", !_fatal && _view.BranchId != beforeRestore.BranchId
                && _view.ParentBranchId == beforeRestore.BranchId && _view.SourceSnapshotId == saved.SnapshotId
                && _view.WorldMinute == beforeRestore.WorldMinute && _view.AcceptedWorldMinutes == beforeRestore.AcceptedWorldMinutes
                && _view.PlayerPosition == beforeRestore.PlayerPosition
                && TownJson.Serialize(_view.Economy) == TownJson.Serialize(beforeRestore.Economy)
                && TownJson.Serialize(_view.Production) == TownJson.Serialize(beforeRestore.Production)
                && TownJson.Serialize(_view.Farming) == TownJson.Serialize(beforeRestore.Farming));
            await SendEconomyKeyAsync(Key.Escape);
            WalkTo(kitchen.PlaceId, kitchen.Id);
            await SendEconomyKeyAsync(Key.E);
            _recipeButtons[soup.Id].EmitSignal(Godot.Button.SignalName.Pressed);
            Check("kitchen_ui_and_route", _productionPanel.Visible && _activeProductionId == kitchen.Id && _activeRecipeId == soup.Id
                && _cookingColumns.Visible && _pauseReasons.Contains("production") && _recipeDetail.Text.Contains("蔬菜汤"));
            var fullCook = PressProduction(_cookButton);
            Check("full_cooking_preserves_ingredients", !fullCook.Succeeded && fullCook.Code == "inventory_full"
                && _productionFeedback.Text.Contains("背包装不下") && fullAssets == EconomyAssetsJson(_view.Economy)
                && !_view.Economy.Player.Inventory.ContainsKey(soup.OutputResourceId));
            await SendEconomyKeyAsync(Key.Escape);
            WalkTo(pond.PlaceId, pond.Id);
            await SendEconomyKeyAsync(Key.E);
            Check("pond_ui_and_route", _productionPanel.Visible && _activeProductionId == pond.Id && _fishButton.Visible
                && _pauseReasons.Contains("production") && _world.CanInteractWithProductionTarget(pond.Id));
            var fullFish = PressProduction(_fishButton);
            Check("full_fishing_preserves_random_and_assets", !fullFish.Succeeded && fullFish.Code == "inventory_full" && fullFish.Roll == 30
                && fullAssets == EconomyAssetsJson(_view.Economy) && _view.Production.FishingRandomState == 12345
                && _view.Production.FishingDrawCount == 0 && _productionFeedback.Text.Contains("背包装不下"));
            await SendEconomyKeyAsync(Key.Escape);
            WalkTo("shop");
            await SendEconomyKeyAsync(Key.E);
            SellTwo("crop.carrot");
            Check("selling_carrots_frees_one_slot", !_view.Economy.Player.Inventory.ContainsKey("crop.carrot")
                && _view.Economy.Player.Coins == 350 && _world.GetPlayerInventorySlots().Count(s => s.ResourceId is not null) == 4);
            await SendEconomyKeyAsync(Key.Escape);
            WalkTo(hen.PlaceId, hen.Id);
            await SendEconomyKeyAsync(Key.E);
            var collected = PressProduction(_collectButton);
            Check("collect_succeeds_after_freeing_slot", collected.Succeeded && collected.Quantity == 1 && collected.ResourceId == hen.ProductResourceId
                && _view.Economy.Player.Inventory.GetValueOrDefault(hen.ProductResourceId) == 1
                && !_view.Production.Chickens.Single().ProductReady && _view.Production.Chickens.Single().CollectedCount == 1
                && _world.GetPlayerInventorySlots().Count(s => s.ResourceId is not null) == 5);
            await SendEconomyKeyAsync(Key.Escape);
            WalkTo("shop");
            await SendEconomyKeyAsync(Key.E);
            SellTwo("crop.tomato");
            Check("selling_tomatoes_frees_one_slot", !_view.Economy.Player.Inventory.ContainsKey("crop.tomato")
                && _view.Economy.Player.Coins == 410 && _world.GetPlayerInventorySlots().Count(s => s.ResourceId is not null) == 4);
            await SendEconomyKeyAsync(Key.Escape);
            WalkTo(pond.PlaceId, pond.Id);
            await SendEconomyKeyAsync(Key.E);
            var caught = PressProduction(_fishButton);
            Check("fishing_succeeds_after_freeing_slot", caught.Succeeded && caught.Roll == 30 && caught.ResourceId == "fish.carp" && caught.Quantity == 1
                && _view.Economy.Player.Inventory.GetValueOrDefault("fish.carp") == 1 && _view.Production.FishingRandomState == 3336926330U
                && _view.Production.FishingDrawCount == 1 && _world.GetPlayerInventorySlots().Count(s => s.ResourceId is not null) == 5);
            string? successScreenshot = await CaptureEconomyScreenshotAsync("success");
            var inputs = new List<JsonElement>();
            foreach (var journal in _journals)
            {
                using var stream = new FileStream(Path.Combine(journal.DirectoryPath, "events.jsonl"), FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    using var parsed = JsonDocument.Parse(line);
                    string kind = parsed.RootElement.GetProperty("kind").GetString()!;
                    if (kind is "player_production_command" or "player_sleep_command" or "player_economy_command") inputs.Add(parsed.RootElement.Clone());
                }
            }
            var productionRecords = inputs.Where(i => i.GetProperty("kind").GetString() == "player_production_command")
                .Select(i => TownJson.Deserialize<ProductionOperationRecord>(i.GetProperty("data").GetProperty("operation").GetRawText())).ToArray();
            bool checkpointsMatch = productionRecords.Length == expectedOperations.Count;
            for (int i = 0; i < productionRecords.Length && i < expectedOperations.Count; i++)
            {
                var operation = productionRecords[i];
                operation.Checkpoint.Events = operation.Checkpoint.Events.TakeLast(128).ToList();
                checkpointsMatch &= TownJson.Serialize(operation) == TownJson.Serialize(expectedOperations[i]);
                var snapshot = _world.CaptureSnapshot() with { State = operation.Checkpoint };
                _ = TownWorld.RestoreCandidate(_loaded.Content, TownJson.Serialize(snapshot));
            }
            Check("versioned_commands_and_checkpoints", inputs.Count == 16 && productionRecords.Length == 8 && checkpointsMatch
                && inputs.All(i => i.GetProperty("data").GetProperty("schemaVersion").GetInt32() == 1)
                && productionRecords.All(r => r.SchemaVersion == 1 && r.InputSequence == r.Checkpoint.Economy.InputSequence
                    && r.RandomStateAfter == r.Checkpoint.Production.FishingRandomState
                    && r.Checkpoint.Production.FishingRandomVersion == TownWorld.FishingRandomVersion),
                new { commandCount = inputs.Count, productionCount = productionRecords.Length, expectedCount = expectedOperations.Count });
            var capacityRejections = productionRecords.Where(r => r.Result.Code == "inventory_full").ToArray();
            Check("rejected_production_checkpoints_preserve_state", capacityRejections.Length == 3
                && capacityRejections.Select(r => r.Request.Action).Order(StringComparer.Ordinal).SequenceEqual(new[] { "collect", "cook", "fish" })
                && capacityRejections.All(r => !r.Result.Succeeded && r.RandomStateBefore == r.RandomStateAfter
                    && r.RandomStateBefore == 12345 && fullAssets == EconomyAssetsJson(r.Checkpoint.Economy)
                    && pendingChicken == TownJson.Serialize(r.Checkpoint.Production.Chickens)));
            Check("production_targets_have_scene_samples", sampledTargets.SetEquals(new[] { hen.Id, pond.Id, kitchen.Id }) && spatialSamples.Count == 6);
            Check("production_sources_and_transfers_are_separate", salesConserved
                && inputs.Count(i => i.GetProperty("kind").GetString() == "player_economy_command") == 2
                && _experimentEvents.Any(e => e.Kind == "chicken_product_ready" && e.Reason == "production_created")
                && new[] { "feed", "collect", "fish" }.All(action => _experimentEvents.Any(e => e.Kind == "production_source" && e.Reason == action))
                && !_experimentEvents.Any(e => e.Kind == "production_source" && e.Reason == "cook"));
            _journal.WriteArtifact("production-capacity-input-checkpoints.json", inputs);
            _journal.WriteArtifact("production-capacity-spatial-samples.json", new { schemaVersion = 1,
                engineVersion = Engine.GetVersionInfo()["string"].AsString(), numericVersion = TownWorld.NumericVersion,
                contentFingerprint = _loaded.Content.Fingerprint, samples = spatialSamples });
            _journal.WriteArtifact("production-capacity-final-state.json", CaptureHostSnapshot());
            var report = new
            {
                schemaVersion = 1, test = "godot_production_capacity_smoke", passed = true, scenario = _scenario,
                headless = _headless, mode = _mode, residents = _actors.Count,
                runtimeAssemblyLocation = typeof(object).Assembly.Location,
                contentFingerprint = _loaded.Content.Fingerprint, checks,
                screenshots = new { failure = failureScreenshot, success = successScreenshot },
                runDirectories = _journals.Select(j => j.DirectoryPath).ToArray(),
                coverage = "Explicit five-slot offline fixture. Godot route queries and UI callbacks verify single unclaimed egg production, atomic full-inventory rejection for collection/cooking/fishing, saved-state restore and production after selling items. Versioned commands include RNG transitions and whole-world checkpoints. OS input is verified separately. No model calls."
            };
            _journal.WriteArtifact("production-capacity-verification-report.json", report);
            WriteReport(report);
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            WriteReport(new { schemaVersion = 1, test = "godot_production_capacity_smoke", passed = false, checks,
                error = error.GetType().Name, message = error.Message });
            GD.PushError("Production capacity verification failed: " + error.Message);
            GetTree().Quit(1);
        }
    }
}
