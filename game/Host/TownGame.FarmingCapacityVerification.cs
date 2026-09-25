using System.Text.Json;
using CozyTown.Core.Town;
using Godot;

namespace CozyTown.Game;

public partial class TownGame
{
    private async Task RunFarmingCapacitySmokeAsync()
    {
        _finishing = true;
        var checks = new List<object>();
        void Check(string name, bool passed, object? evidence = null)
        {
            checks.Add(new { name, passed, evidence });
            if (!passed) throw new InvalidDataException("Farming capacity verification failed: " + name);
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
        try
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var farming = _loaded.Definitions.Farming!;
            var plot = farming.Plots[0];
            var crop = farming.Crops.Single(c => c.SeedResourceId == "seed.carrot");
            Check("explicit_offline_capacity_fixture", _scenario == "farming-capacity-fixture" && _mode == "rules" && _model is null
                && _view.Economy.Player.SlotCapacity == 5 && _view.Economy.Player.Coins == 300
                && _view.Economy.Player.Inventory.GetValueOrDefault(crop.SeedResourceId) == 2
                && _world.GetPlayerInventorySlots().Count(s => s.ResourceId is not null) == 5);
            WalkTo(plot.PlaceId);
            await SendEconomyKeyAsync(Key.E);
            Check("plot_opened_with_fixture", _farmingPanel.Visible && _activePlotId == plot.Id && _pauseReasons.Contains("farming"));
            _plantButtons[crop.Id].EmitSignal(Godot.Button.SignalName.Pressed);
            Check("plant_keeps_all_five_slots_occupied", _view.Farming.Plots[0].CropId == crop.Id
                && _view.Economy.Player.Inventory.GetValueOrDefault(crop.SeedResourceId) == 1
                && _world.GetPlayerInventorySlots().Count(s => s.ResourceId is not null) == 5);
            for (int day = 0; day < crop.GrowthDays; day++)
            {
                _waterButton.EmitSignal(Godot.Button.SignalName.Pressed);
                Check("water_before_morning_" + day, _view.Farming.Plots[0].Watered);
                await SendEconomyKeyAsync(Key.Escape);
                WalkTo(farming.BedPlaceId!);
                await SendEconomyKeyAsync(Key.E);
                _sleepHours.GetLineEdit().Text = "12";
                _sleepConfirm.EmitSignal(Godot.Button.SignalName.Pressed);
                _sleepConfirm.EmitSignal(Godot.Button.SignalName.Pressed);
                Check("growth_after_morning_" + day, !_fatal && _view.Farming.Plots[0].GrowthDays == day + 1);
                await SendEconomyKeyAsync(Key.Escape);
                WalkTo(plot.PlaceId);
                await SendEconomyKeyAsync(Key.E);
            }
            Check("mature_before_rejected_harvest", _view.Farming.Plots[0].Mature && _farmingStatus.Text.Contains("已成熟"));
            string beforeAssets = EconomyAssetsJson(_view.Economy);
            string beforePlot = TownJson.Serialize(_view.Farming.Plots[0]);
            _harvestButton.EmitSignal(Godot.Button.SignalName.Pressed);
            Check("capacity_failure_explains_and_preserves_assets", _farmingFeedback.Text.Contains("背包装不下")
                && _view.Farming.Plots[0].Mature && beforeAssets == EconomyAssetsJson(_view.Economy)
                && beforePlot == TownJson.Serialize(_view.Farming.Plots[0]));
            string? failureScreenshot = await CaptureEconomyScreenshotAsync(null);
            SaveWorld();
            var capacitySave = TownSaveFile.Read(_savePath);
            string branchBeforeRestore = _world.GetView().BranchId;
            if (capacitySave.Stage != "farming-capacity-fixture") throw new InvalidDataException("Capacity save was not published.");
            LoadWorld();
            Check("rejected_harvest_state_survives_restore", !_fatal && _view.BranchId != branchBeforeRestore
                && _view.ParentBranchId == branchBeforeRestore && _view.Farming.Plots[0].Mature
                && beforeAssets == EconomyAssetsJson(_view.Economy) && beforePlot == TownJson.Serialize(_view.Farming.Plots[0]));
            await SendEconomyKeyAsync(Key.Escape);
            WalkTo("shop");
            await SendEconomyKeyAsync(Key.E);
            SetTradeDirection("sell");
            _stockButtons["crop.potato"].EmitSignal(Godot.Button.SignalName.Pressed);
            _tradeQuantity.GetLineEdit().Text = "1";
            _tradeButton.EmitSignal(Godot.Button.SignalName.Pressed);
            Check("selling_potato_frees_one_slot", !_view.Economy.Player.Inventory.ContainsKey("crop.potato")
                && _view.Economy.Player.Coins == 320 && _world.GetPlayerInventorySlots().Count(s => s.ResourceId is not null) == 4);
            await SendEconomyKeyAsync(Key.Escape);
            WalkTo(plot.PlaceId);
            await SendEconomyKeyAsync(Key.E);
            _harvestButton.EmitSignal(Godot.Button.SignalName.Pressed);
            Check("harvest_succeeds_after_freeing_capacity", _view.Farming.Plots[0].CropId is null
                && _view.Economy.Player.Inventory.GetValueOrDefault(crop.HarvestResourceId) == crop.HarvestQuantity
                && _world.GetPlayerInventorySlots().Count(s => s.ResourceId is not null) == 5);
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
                    if (kind is "player_farm_command" or "player_sleep_command" or "player_economy_command") inputs.Add(parsed.RootElement.Clone());
                }
            }
            Check("rejected_command_has_durable_checkpoint", inputs.Any(i =>
            {
                if (i.GetProperty("kind").GetString() != "player_farm_command") return false;
                var operation = i.GetProperty("data").GetProperty("operation");
                return operation.GetProperty("result").GetProperty("code").GetString() == "inventory_full"
                    && operation.GetProperty("checkpoint").GetProperty("farming").GetProperty("plots")[0].GetProperty("mature").GetBoolean();
            }));
            _journal.WriteArtifact("farming-capacity-input-checkpoints.json", inputs);
            var report = new
            {
                schemaVersion = 1, test = "godot_farming_capacity_smoke", passed = true, scenario = _scenario,
                headless = _headless, mode = _mode, residents = _actors.Count,
                runtimeAssemblyLocation = typeof(object).Assembly.Location,
                contentFingerprint = _loaded.Content.Fingerprint, checks,
                screenshots = new { failure = failureScreenshot, success = successScreenshot },
                runDirectories = _journals.Select(j => j.DirectoryPath).ToArray(),
                coverage = "Explicit five-slot offline fixture. Godot routes and UI callbacks plant, water and sleep through three mornings; full-inventory harvest retains the mature crop and assets across restore, then selling one item permits harvest. OS input is verified separately. No model calls."
            };
            _journal.WriteArtifact("farming-capacity-verification-report.json", report);
            WriteReport(report);
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            WriteReport(new { schemaVersion = 1, test = "godot_farming_capacity_smoke", passed = false, checks,
                error = error.GetType().Name, message = error.Message });
            GD.PushError("Farming capacity verification failed: " + error.Message);
            GetTree().Quit(1);
        }
    }
}
