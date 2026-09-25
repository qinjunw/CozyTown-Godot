using System.Text.Json;
using CozyTown.Core.Town;
using Godot;

namespace CozyTown.Game;

public partial class TownGame
{
    private async Task RunFarmingSmokeAsync()
    {
        _finishing = true;
        var checks = new List<object>();
        void Check(string name, bool passed, object? evidence = null)
        {
            checks.Add(new { name, passed, evidence });
            if (!passed) throw new InvalidDataException("Farming verification failed: " + name);
        }
        void WalkTo(string placeId)
        {
            var target = _loaded.Content.Place(placeId).Position;
            _playerRoute = _loaded.Content.FindRoute(_world.GetView().PlayerPosition, target)
                ?? throw new InvalidDataException("No route to " + placeId);
            _playerRouteCursor = 0;
            for (int i = 0; i < 1600 && _playerRouteCursor < _playerRoute.Count; i++) AdvancePlayer(StepSeconds);
            FlushWorldEvents();
            _view = _world.GetView(); SyncActors(); RefreshInterface();
            if (_view.PlayerPosition.DistanceTo(target) > .01) throw new InvalidDataException("Route did not reach " + placeId);
        }
        void BuyOrSell(string direction, string resourceId, int quantity)
        {
            SetTradeDirection(direction);
            _stockButtons[resourceId].EmitSignal(Godot.Button.SignalName.Pressed);
            _tradeQuantity.GetLineEdit().Text = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _tradeButton.EmitSignal(Godot.Button.SignalName.Pressed);
            if (_fatal) throw new InvalidDataException(_failure);
        }
        async Task RestForDay()
        {
            WalkTo(_loaded.Definitions.Farming!.BedPlaceId!);
            await SendEconomyKeyAsync(Key.E);
            if (!_farmingPanel.Visible || _activePlotId is not null) throw new InvalidDataException("Bed interaction missing.");
            _sleepHours.GetLineEdit().Text = "12";
            _sleepConfirm.EmitSignal(Godot.Button.SignalName.Pressed);
            _sleepConfirm.EmitSignal(Godot.Button.SignalName.Pressed);
            if (_fatal) throw new InvalidDataException(_failure);
            await SendEconomyKeyAsync(Key.Escape);
        }
        try
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var initial = _world.GetView();
            var farming = _loaded.Definitions.Farming!;
            var plot = farming.Plots[0];
            var crop = farming.Crops.Single(c => c.SeedResourceId == "seed.carrot");
            Check("normal_new_game", _scenario == "baseline" && _mode == "rules" && _model is null
                && initial.Economy.Player.Inventory.Count == 0 && initial.Economy.Player.Coins == 300
                && farming.Crops.Length == 3 && farming.Plots.Length == 6);
            WalkTo(plot.PlaceId);
            await SendEconomyKeyAsync(Key.E);
            Check("plot_interaction_and_pause", _farmingPanel.Visible && _activePlotId == plot.Id && _pauseReasons.Contains("farming"));
            _plantButtons[crop.Id].EmitSignal(Godot.Button.SignalName.Pressed);
            Check("missing_seed_feedback", _view.Farming.Plots[0].CropId is null && _view.Economy.Player.Inventory.Count == 0
                && _farmingFeedback.Text.Contains("种子不足"));
            await SendEconomyKeyAsync(Key.Escape);
            WalkTo("shop");
            await SendEconomyKeyAsync(Key.E);
            BuyOrSell("buy", crop.SeedResourceId, 1);
            Check("buy_seed", _view.Economy.Player.Coins == 275 && _view.Economy.Player.Inventory.GetValueOrDefault(crop.SeedResourceId) == 1);
            await SendEconomyKeyAsync(Key.Escape);
            WalkTo(plot.PlaceId);
            await SendEconomyKeyAsync(Key.E);
            _plantButtons[crop.Id].EmitSignal(Godot.Button.SignalName.Pressed);
            Check("plant_consumes_one_seed", _view.Farming.Plots[0].CropId == crop.Id && !_view.Economy.Player.Inventory.ContainsKey(crop.SeedResourceId));
            _harvestButton.EmitSignal(Godot.Button.SignalName.Pressed);
            Check("immature_harvest_preserves_crop", _view.Farming.Plots[0].CropId == crop.Id
                && _view.Farming.Plots[0].GrowthDays == 0 && _farmingFeedback.Text.Contains("还没成熟"));
            string? dryScreenshot = await CaptureEconomyScreenshotAsync("dry");
            await SendEconomyKeyAsync(Key.Escape);
            await RestForDay();
            Check("dry_plot_does_not_block_morning", _view.Farming.Plots[0].GrowthDays == 0
                && _view.WorldMinute == initial.WorldMinute + 1440 && _view.Farming.LastSettledDay == 1
                && _view.Economy.Shops.Single().Coins == 10025);
            var dayTwoStock = new Dictionary<string, int> { ["seed.potato"] = 3, ["seed.carrot"] = 3, ["seed.tomato"] = 3, ["feed.chicken"] = 9, ["ingredient.salt"] = 7 };
            Check("reference_day_two_stock", SameInventory(_view.Economy.Shops.Single().Inventory, dayTwoStock));
            for (int day = 0; day < crop.GrowthDays; day++)
            {
                WalkTo(plot.PlaceId);
                await SendEconomyKeyAsync(Key.E);
                _waterButton.EmitSignal(Godot.Button.SignalName.Pressed);
                Check("water_day_" + day, _view.Farming.Plots[0].Watered);
                await SendEconomyKeyAsync(Key.Escape);
                await RestForDay();
                Check("growth_day_" + day, _view.Farming.Plots[0].GrowthDays == day + 1);
            }
            WalkTo(plot.PlaceId);
            await SendEconomyKeyAsync(Key.E);
            Check("mature_crop_visible", _view.Farming.Plots[0].Mature && _farmingStatus.Text.Contains("已成熟"));
            string? matureScreenshot = await CaptureEconomyScreenshotAsync("mature");
            await SendEconomyKeyAsync(Key.Escape);
            WalkTo("farm");
            string? fieldScreenshot = await CaptureEconomyScreenshotAsync("field");
            WalkTo(plot.PlaceId);
            await SendEconomyKeyAsync(Key.E);
            SaveWorld();
            var farmSave = TownSaveFile.Read(_savePath);
            var saved = _world.GetView();
            if (TownJson.Deserialize<TownSnapshot>(farmSave.WorldJson).State.BranchId != saved.BranchId)
                throw new InvalidDataException("Mature crop save was not published.");
            _harvestButton.EmitSignal(Godot.Button.SignalName.Pressed);
            Check("harvest_receives_produce", _view.Economy.Player.Inventory.GetValueOrDefault(crop.HarvestResourceId) == crop.HarvestQuantity
                && _view.Farming.Plots[0].CropId is null);
            LoadWorld();
            Check("restore_unclaimed_mature_crop", !_fatal && _view.BranchId != saved.BranchId
                && _view.ParentBranchId == saved.BranchId && _view.Farming.Plots[0].Mature
                && !_view.Economy.Player.Inventory.ContainsKey(crop.HarvestResourceId)
                && _view.Farming.LastSettledDay == saved.Farming.LastSettledDay);
            _harvestButton.EmitSignal(Godot.Button.SignalName.Pressed);
            _harvestButton.EmitSignal(Godot.Button.SignalName.Pressed);
            Check("second_harvest_does_not_duplicate", _view.Economy.Player.Inventory.GetValueOrDefault(crop.HarvestResourceId) == crop.HarvestQuantity
                && _farmingFeedback.Text.Contains("还没有种"));
            await SendEconomyKeyAsync(Key.Escape);
            WalkTo("shop");
            await SendEconomyKeyAsync(Key.E);
            BuyOrSell("sell", crop.HarvestResourceId, crop.HarvestQuantity);
            Check("sell_produce", _view.Economy.Player.Coins == 325 && !_view.Economy.Player.Inventory.ContainsKey(crop.HarvestResourceId));
            string seed = _view.Economy.Shops.Single().Inventory.Keys.First(id => id.StartsWith("seed.", StringComparison.Ordinal));
            BuyOrSell("buy", seed, 1);
            Check("reinvest_in_next_crop", _view.Economy.Player.Inventory.GetValueOrDefault(seed) == 1 && _view.Economy.Player.Coins < 325);
            await SendEconomyKeyAsync(Key.Escape);
            WalkTo(farming.BedPlaceId!);
            await SendEconomyKeyAsync(Key.E);
            double beforePausedSleep = _world.WorldMinute;
            SetPause("focus", true);
            SubmitSleep();
            Check("external_pause_blocks_sleep", _world.WorldMinute == beforePausedSleep && _sleepConfirm.Disabled);
            SetPause("focus", false);
            string? sleepScreenshot = await CaptureEconomyScreenshotAsync(null);
            Check("four_residents_followed_routes", _experimentEvents.Count(e => e.Kind == "space_query") > 100
                && _view.Residents.All(r => r.ScheduleDay == (long)_view.WorldMinute / 1440)
                && _experimentEvents.Any(e => e.Kind == "schedule_started" && e.WorldMinute >= 1440));
            Check("production_and_restock_recorded", _experimentEvents.Any(e => e.Kind == "production_source" && e.Reason == "harvest")
                && _experimentEvents.Any(e => e.Kind.Contains("restock", StringComparison.Ordinal)));
            var inputs = new List<JsonElement>();
            foreach (var journal in _journals)
            {
                using var stream = new FileStream(Path.Combine(journal.DirectoryPath, "events.jsonl"), FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    using var parsed = JsonDocument.Parse(line);
                    string kind = parsed.RootElement.GetProperty("kind").GetString()!;
                    if (kind is "player_farm_command" or "player_sleep_command" or "player_economy_command") inputs.Add(parsed.RootElement.Clone());
                }
            }
            Check("versioned_commands_and_checkpoints", inputs.Count > 20 && inputs.All(i => i.GetProperty("data").GetProperty("schemaVersion").GetInt32() == 1)
                && inputs.Any(i => i.GetProperty("kind").GetString() == "player_sleep_command"));
            _journal.WriteArtifact("farming-input-checkpoints.json", inputs);
            var report = new
            {
                schemaVersion = 1, test = "godot_farming_smoke", passed = true, scenario = _scenario,
                headless = _headless, mode = _mode, residents = _actors.Count,
                runtimeAssemblyLocation = typeof(object).Assembly.Location,
                contentFingerprint = _loaded.Content.Fingerprint, checks,
                screenshots = new { sleep = sleepScreenshot, dry = dryScreenshot, mature = matureScreenshot, field = fieldScreenshot },
                runDirectories = _journals.Select(j => j.DirectoryPath).ToArray(),
                coverage = "Godot route movement, farm/shop/sleep button callbacks, synthetic key routing, production loop, dry morning, reference restock, mature save/restore and durable command checkpoints. OS input is verified separately. No model calls."
            };
            _journal.WriteArtifact("farming-verification-report.json", report);
            WriteReport(report);
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            WriteReport(new { schemaVersion = 1, test = "godot_farming_smoke", passed = false, checks, error = error.GetType().Name, message = error.Message });
            GD.PushError("Farming verification failed: " + error.Message);
            GetTree().Quit(1);
        }
    }
}
