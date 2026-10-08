using System.Text.Json;
using CozyTown.Core.Town;
using Godot;

namespace CozyTown.Game;

public partial class TownGame
{
    private async Task RunProductionSmokeAsync()
    {
        _finishing = true;
        var checks = new List<object>();
        void Check(string name, bool passed, object? evidence = null)
        {
            checks.Add(new { name, passed, evidence });
            if (!passed) throw new InvalidDataException("Production verification failed: " + name);
        }
        void WalkTo(string placeId)
        {
            var target = _loaded.Content.Place(placeId).Position;
            _playerRoute = _loaded.Content.FindRoute(_world.GetView().PlayerPosition, target)
                ?? throw new InvalidDataException("No route to " + placeId);
            _playerRouteCursor = 0;
            for (int i = 0; i < 1600 && _playerRouteCursor < _playerRoute.Count; i++) AdvancePlayer(StepSeconds);
            FlushWorldEvents(); _view = _world.GetView(); SyncActors(); RefreshInterface();
            if (_view.PlayerPosition.DistanceTo(target) > .01) throw new InvalidDataException("Route did not reach " + placeId);
        }
        async Task Open(string placeId)
        {
            WalkTo(placeId); await SendEconomyKeyAsync(Key.E);
            if (_fatal) throw new InvalidDataException(_failure);
        }
        void Trade(string direction, string resourceId, int quantity)
        {
            SetTradeDirection(direction);
            _stockButtons[resourceId].EmitSignal(Godot.Button.SignalName.Pressed);
            _tradeQuantity.GetLineEdit().Text = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _tradeButton.EmitSignal(Godot.Button.SignalName.Pressed);
            if (_fatal) throw new InvalidDataException(_failure);
        }
        void Press(Button button)
        {
            button.EmitSignal(Godot.Button.SignalName.Pressed);
            if (_fatal) throw new InvalidDataException(_failure);
        }
        async Task RestDay()
        {
            await Open(_loaded.Definitions.Farming!.BedPlaceId!);
            if (!_farmingPanel.Visible || _activePlotId is not null) throw new InvalidDataException("Bed interaction missing.");
            _sleepHours.GetLineEdit().Text = "12";
            Press(_sleepConfirm); Press(_sleepConfirm);
            await SendEconomyKeyAsync(Key.Escape);
        }
        TownState SaveCheckpoint(string name)
        {
            SaveWorld();
            var saved = TownSaveFile.Read(_savePath);
            var current = _world.GetView();
            var state = TownJson.Deserialize<TownSnapshot>(saved.WorldJson).State;
            if (saved.Stage != _scenario || state.BranchId != current.BranchId ||
                TownJson.Serialize(state.Production) != TownJson.Serialize(current.Production))
                throw new InvalidDataException("Production save was not published.");
            _journal.WriteArtifact(name + "-save.json", saved);
            return current;
        }
        void RestoreCheckpoint(TownState saved)
        {
            LoadWorld(); RefreshInterface();
            if (_fatal || _view.BranchId == saved.BranchId || _view.ParentBranchId != saved.BranchId)
                throw new InvalidDataException("Production restore did not create a branch.");
        }
        try
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var production = _loaded.Definitions.Production!;
            var farm = _loaded.Definitions.Farming!;
            var hen = production.Chickens.Single();
            var pond = production.FishingSpots.Single();
            var kitchen = production.Kitchens.Single();
            Check("normal_new_game", _scenario == "baseline" && _mode == "rules" && _model is null &&
                _view.Economy.Player.Coins == 300 && _view.Economy.Player.Inventory.Count == 0 &&
                _view.Economy.Player.SlotCapacity == 24 && production.Fish.Length == 3 && production.Recipes.Length == 5);
            await Open(hen.PlaceId);
            string emptyAssets = EconomyAssetsJson(_view.Economy);
            Press(_feedButton);
            Check("missing_feed_preserves_assets", _productionPanel.Visible && _activeProductionId == hen.Id &&
                _productionFeedback.Text.Contains("饲料不足") && emptyAssets == EconomyAssetsJson(_view.Economy));
            await SendEconomyKeyAsync(Key.Escape);
            await Open(kitchen.PlaceId); Press(_cookButton);
            Check("missing_material_preserves_assets", _productionPanel.Visible && _productionFeedback.Text.Contains("材料不足") &&
                emptyAssets == EconomyAssetsJson(_view.Economy));
            await SendEconomyKeyAsync(Key.Escape);
            await Open("shop");
            foreach (var id in new[] { "seed.carrot", "seed.tomato", "ingredient.flour" }) Trade("buy", id, 1);
            Trade("buy", hen.FeedResourceId, 2);
            Check("buy_initial_materials", _view.Economy.Player.Coins == 215 && _view.Economy.Player.Inventory.GetValueOrDefault(hen.FeedResourceId) == 2);
            await SendEconomyKeyAsync(Key.Escape);
            for (int i = 0; i < 2; i++)
            {
                await Open(farm.Plots[i].PlaceId);
                Press(_plantButtons[farm.Crops.Single(c => c.SeedResourceId == (i == 0 ? "seed.carrot" : "seed.tomato")).Id]);
                await SendEconomyKeyAsync(Key.Escape);
            }
            Check("plant_carrot_and_tomato", _view.Farming.Plots.Take(2).All(p => p.CropId is not null));
            await Open(hen.PlaceId); Press(_feedButton);
            Check("feed_once", _view.Production.Chickens.Single().Fed && _view.Production.Chickens.Single().Generation == 1 &&
                _view.Economy.Player.Inventory.GetValueOrDefault(hen.FeedResourceId) == 1 && _pauseReasons.Contains("production"));
            string fedAssets = EconomyAssetsJson(_view.Economy);
            Press(_feedButton);
            Check("already_fed_preserves_assets", fedAssets == EconomyAssetsJson(_view.Economy) && _productionFeedback.Text.Contains("已经喂过"));
            var fedSave = SaveCheckpoint("production-fed"); RestoreCheckpoint(fedSave);
            Check("restore_fed_chicken", _view.Production.Chickens.Single().Fed && fedAssets == EconomyAssetsJson(_view.Economy));
            string? chickenScreenshot = await CaptureEconomyScreenshotAsync(null);
            await SendEconomyKeyAsync(Key.Escape);
            for (int day = 0; day < 4; day++)
            {
                foreach (var plot in _view.Farming.Plots.Where(p => p.CropId is not null && !p.Mature).ToArray())
                {
                    await Open(farm.Plots.Single(p => p.Id == plot.Id).PlaceId); Press(_waterButton);
                    await SendEconomyKeyAsync(Key.Escape);
                }
                await RestDay();
                if (day == 0)
                {
                    Check("first_morning_egg", _view.Production.Chickens.Single().ProductReady && !_view.Production.Chickens.Single().Fed);
                    await Open(hen.PlaceId); Press(_collectButton);
                    Check("collect_first_egg", _view.Economy.Player.Inventory.GetValueOrDefault(hen.ProductResourceId) == 1);
                    Press(_feedButton);
                    Check("feed_second_cycle", _view.Production.Chickens.Single().Fed && _view.Production.Chickens.Single().Generation == 2 &&
                        !_view.Economy.Player.Inventory.ContainsKey(hen.FeedResourceId));
                    await SendEconomyKeyAsync(Key.Escape);
                    await Open("shop"); Trade("buy", "seed.potato", 1); Trade("buy", "ingredient.salt", 2);
                    Check("buy_restocked_materials", _view.Economy.Player.Coins == 185 && _view.Economy.Player.Inventory.GetValueOrDefault("ingredient.salt") == 2);
                    await SendEconomyKeyAsync(Key.Escape);
                    await Open(farm.Plots[2].PlaceId); Press(_plantButtons["crop_definition.potato"]);
                    await SendEconomyKeyAsync(Key.Escape);
                }
            }
            Check("three_crops_mature", _view.Farming.Plots.Take(3).All(p => p.Mature));
            Check("pending_egg_does_not_stack", _view.Production.Chickens.Single().ProductReady && _view.Production.Chickens.Single().Generation == 2 &&
                _view.Production.Chickens.Single().CollectedCount == 1 && _view.Economy.Player.Inventory.GetValueOrDefault(hen.ProductResourceId) == 1);
            await Open(hen.PlaceId);
            string readyAssets = EconomyAssetsJson(_view.Economy); Press(_feedButton);
            Check("feed_with_pending_egg_preserves_assets", readyAssets == EconomyAssetsJson(_view.Economy) && _productionFeedback.Text.Contains("等你领取"));
            var readySave = SaveCheckpoint("production-ready"); RestoreCheckpoint(readySave);
            Check("restore_unclaimed_egg", _view.Production.Chickens.Single().ProductReady && readyAssets == EconomyAssetsJson(_view.Economy));
            string? readyScreenshot = await CaptureEconomyScreenshotAsync("ready");
            Press(_collectButton);
            Check("collect_second_egg", _view.Economy.Player.Inventory.GetValueOrDefault(hen.ProductResourceId) == 2 && !_view.Production.Chickens.Single().ProductReady);
            string collectedAssets = EconomyAssetsJson(_view.Economy); Press(_collectButton);
            Check("repeated_collect_preserves_assets", collectedAssets == EconomyAssetsJson(_view.Economy) && _productionFeedback.Text.Contains("还没有待领"));
            await SendEconomyKeyAsync(Key.Escape);
            foreach (var plot in farm.Plots.Take(3))
            { await Open(plot.PlaceId); Press(_harvestButton); await SendEconomyKeyAsync(Key.Escape); }
            Check("harvest_three_crops", _view.Economy.Player.Inventory.GetValueOrDefault("crop.potato") == 2 &&
                _view.Economy.Player.Inventory.GetValueOrDefault("crop.carrot") == 2 && _view.Economy.Player.Inventory.GetValueOrDefault("crop.tomato") == 3);
            await Open(pond.PlaceId);
            var fishSave = SaveCheckpoint("production-before-fishing"); Press(_fishButton);
            var firstCatch = _view.Production.Receipts.Values.OrderBy(r => r.InputSequence).Last().Result;
            uint firstState = _view.Production.FishingRandomState;
            RestoreCheckpoint(fishSave); Press(_fishButton);
            var repeatedCatch = _view.Production.Receipts.Values.OrderBy(r => r.InputSequence).Last().Result;
            Check("restore_fishing_random", firstCatch.Roll == repeatedCatch.Roll && firstCatch.ResourceId == repeatedCatch.ResourceId &&
                firstState == _view.Production.FishingRandomState && _view.Production.FishingDrawCount == 1);
            for (int cast = 1; cast < 64 && !(production.Fish.All(f => _view.Economy.Player.Inventory.GetValueOrDefault(f.ResourceId) > 0) &&
                _view.Production.Receipts.Values.Any(r => r.Result.Code == "no_catch")); cast++) Press(_fishButton);
            Check("three_fish_and_empty_roll", production.Fish.All(f => _view.Economy.Player.Inventory.GetValueOrDefault(f.ResourceId) > 0) &&
                _view.Production.Receipts.Values.Any(r => r.Result.Code == "no_catch"));
            string? fishingScreenshot = await CaptureEconomyScreenshotAsync("fishing");
            await SendEconomyKeyAsync(Key.Escape);
            await Open(kitchen.PlaceId);
            var materialSave = SaveCheckpoint("production-materials");
            string? kitchenScreenshot = await CaptureEconomyScreenshotAsync("kitchen");
            foreach (var recipe in production.Recipes) { Press(_recipeButtons[recipe.Id]); Press(_cookButton); }
            string cookedAssets = EconomyAssetsJson(_view.Economy);
            Check("cook_all_five_recipes", production.Recipes.All(r => _view.Economy.Player.Inventory.GetValueOrDefault(r.OutputResourceId) == 1) &&
                _view.Economy.Player.Coins == 185 && _view.Economy.Player.Inventory.GetValueOrDefault(hen.ProductResourceId) == 0 &&
                _view.Economy.Player.Inventory.GetValueOrDefault("ingredient.salt") == 0 && _view.Economy.Player.Inventory.GetValueOrDefault("ingredient.flour") == 0);
            var cookedSave = SaveCheckpoint("production-cooked"); RestoreCheckpoint(cookedSave);
            Check("restore_cooked_assets", cookedAssets == EconomyAssetsJson(_view.Economy));
            string? cookedScreenshot = await CaptureEconomyScreenshotAsync("cooked");
            await SendEconomyKeyAsync(Key.Escape);
            await Open("shop");
            foreach (var recipe in production.Recipes) Trade("sell", recipe.OutputResourceId, 1);
            Check("sell_processed_outputs", _view.Economy.Player.Coins == 525 && production.Recipes.All(r => !_view.Economy.Player.Inventory.ContainsKey(r.OutputResourceId)));
            string? soldScreenshot = await CaptureEconomyScreenshotAsync("sold");
            string seed = _view.Economy.Shops.Single().Inventory.Keys.First(id => id.StartsWith("seed.", StringComparison.Ordinal));
            Trade("buy", seed, 1);
            Check("reinvest_production_income", _view.Economy.Player.Coins < 525 && _view.Economy.Player.Inventory.GetValueOrDefault(seed) == 1);
            await SendEconomyKeyAsync(Key.Escape);
            await Open(pond.PlaceId);
            string beforePause = TownJson.Serialize(_view.Production);
            SetPause("focus", true); SubmitProduction("fish");
            Check("external_pause_blocks_production", beforePause == TownJson.Serialize(_world.GetView().Production) && _fishButton.Disabled);
            SetPause("focus", false);
            Check("four_residents_followed_routes", _experimentEvents.Count(e => e.Kind == "space_query") > 100 &&
                _view.Residents.All(r => r.ScheduleDay == (long)_view.WorldMinute / 1440));
            var commands = new List<JsonElement>(); var spaces = new List<JsonElement>();
            foreach (var journal in _journals)
            {
                using var stream = new FileStream(Path.Combine(journal.DirectoryPath, "events.jsonl"), FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    using var document = JsonDocument.Parse(line);
                    string kind = document.RootElement.GetProperty("kind").GetString()!;
                    if (kind is "player_production_command" or "player_farm_command" or "player_sleep_command" or "player_economy_command") commands.Add(document.RootElement.Clone());
                    if (kind == "world_boundary" && document.RootElement.GetProperty("data").GetProperty("spaceQueries").GetArrayLength() > 0) spaces.Add(document.RootElement.Clone());
                }
            }
            var inputs = commands.Where(c => c.GetProperty("kind").GetString() == "player_production_command").ToArray();
            Check("versioned_production_checkpoints", inputs.Length > 15 && inputs.All(i =>
            {
                var data = i.GetProperty("data"); var operation = data.GetProperty("operation");
                return data.GetProperty("schemaVersion").GetInt32() == 1 && operation.GetProperty("schemaVersion").GetInt32() == 1 &&
                    operation.TryGetProperty("randomStateBefore", out _) && operation.TryGetProperty("randomStateAfter", out _) &&
                    operation.GetProperty("checkpoint").TryGetProperty("production", out _);
            }));
            Check("shared_space_samples", spaces.Count > 10 && new[] { hen.PlaceId, pond.PlaceId, kitchen.PlaceId }.All(id =>
                _loaded.Manifest.Places.Any(p => p.Id == id)));
            _journal.WriteArtifact("production-input-checkpoints.json", commands);
            _journal.WriteArtifact("production-space-samples.json", spaces);
            var report = new
            {
                schemaVersion = 1, test = "godot_production_smoke", passed = true, scenario = _scenario, headless = _headless,
                mode = _mode, residents = _actors.Count, runtimeAssemblyLocation = typeof(object).Assembly.Location,
                contentFingerprint = _loaded.Content.Fingerprint, checks,
                screenshots = new { chicken = chickenScreenshot, ready = readyScreenshot, fishing = fishingScreenshot,
                    kitchen = kitchenScreenshot, cooked = cookedScreenshot, sold = soldScreenshot },
                runDirectories = _journals.Select(j => j.DirectoryPath).ToArray(),
                coverage = "Normal empty new game: purchases, three crops, two chicken cycles, retained egg, saved fishing random state, three fish and empty roll, five recipes, save/restore, sales and reinvestment through Godot routes/UI callbacks and synthetic keys. Versioned inputs and shared scene queries are samples for future replay adapters. OS input is verified separately. No model calls."
            };
            _journal.WriteArtifact("production-verification-report.json", report); WriteReport(report); GetTree().Quit(0);
        }
        catch (Exception error)
        {
            WriteReport(new { schemaVersion = 1, test = "godot_production_smoke", passed = false, checks, error = error.GetType().Name, message = error.Message });
            GD.PushError("Production verification failed: " + error.Message); GetTree().Quit(1);
        }
    }
}
