using System.Text.Json;
using CozyTown.Core.Town;
using Godot;
using File = System.IO.File;

namespace CozyTown.Game;

public partial class TownGame
{
    private async Task RunEconomySmokeAsync()
    {
        _finishing = true;
        var checks = new List<object>();
        var expectedOperations = new List<EconomyOperationRecord>();
        void Check(string name, bool passed, object? evidence = null)
        {
            checks.Add(new { name, passed, evidence });
            if (!passed) throw new InvalidDataException("Economy verification failed: " + name);
        }
        void RememberOperation()
        {
            if (_fatal) throw new InvalidDataException("Economy operation interrupted: " + _failure);
            var view = _world.GetView();
            var receipt = view.Economy.Receipts.Values.Single(r => r.InputSequence == view.Economy.InputSequence);
            expectedOperations.Add(new(1, receipt.InputSequence, view.BranchId, view.WorldMinute, receipt.Kind,
                receipt.Trade, receipt.TradeResult, receipt.Selection, receipt.SelectionResult, view.Economy));
        }
        TradeResult Submit(string direction, string resourceId, int quantity)
        {
            SetTradeDirection(direction);
            _stockButtons[resourceId].EmitSignal(Godot.Button.SignalName.Pressed);
            // Match typing followed by a click, without Enter or a focus change.
            _tradeQuantity.GetLineEdit().Text = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            long before = _world.GetView().Economy.InputSequence;
            if (_tradeButton.Disabled) throw new InvalidDataException("The trade confirmation button is disabled.");
            _tradeButton.EmitSignal(Godot.Button.SignalName.Pressed);
            if (_world.GetView().Economy.InputSequence != before + 1)
                throw new InvalidDataException("The trade button did not publish exactly one command.");
            RememberOperation();
            return expectedOperations[^1].TradeResult!;
        }
        try
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var initial = _world.GetView();
            var fixtureInventory = new Dictionary<string, int>
            {
                ["crop.potato"] = 2, ["animal_product.egg"] = 3, ["fish.carp"] = 1, ["food.baked_potato"] = 1
            };
            Check("economy_fixture_is_explicit", _scenario == "economy-fixture" && _mode == "rules" && _model is null
                && _modelOptions is null && initial.Economy.Player.Coins == 300 && initial.Economy.Player.SlotCapacity == 24
                && SameInventory(initial.Economy.Player.Inventory, fixtureInventory), new { scenario = _scenario, initial.Economy.Player });
            var baseline = TownContentLoader.Load(_map, "baseline");
            Check("baseline_player_inventory_empty", baseline.Definitions.Economy!.Player.Inventory.Count == 0
                && baseline.Definitions.Economy.Player.Coins == 300 && baseline.Definitions.Economy.Player.SlotCapacity == 24);
            var shopDefinition = _loaded.Definitions.Economy!.Shops.Single();
            string shopId = shopDefinition.Id;
            TownPoint entrance = _loaded.Content.Place(shopDefinition.PlaceId).Position;
            _playerRoute = _loaded.Content.FindRoute(initial.PlayerPosition, entrance)
                ?? throw new InvalidDataException("No walkable route to the shop entrance.");
            _playerRouteCursor = 0;
            int routePoints = _playerRoute.Count;
            for (int step = 0; step < 1200 && _playerRoute is not null && _playerRouteCursor < _playerRoute.Count; step++)
                AdvancePlayer(StepSeconds);
            FlushWorldEvents();
            _view = _world.GetView();
            SyncActors(); RefreshInterface();
            Check("player_route_reaches_shop", _view.PlayerPosition.DistanceTo(entrance) < .01
                && _view.PlayerPosition != initial.PlayerPosition && _world.CanInteractWithShop(shopId),
                new { routePoints, start = initial.PlayerPosition, end = _view.PlayerPosition, entrance });

            await SendEconomyKeyAsync(Key.E);
            Check("logical_e_opens_shop", _economyPanel.Visible && _activeShopId == shopId && _shopColumn.Visible
                && _pauseReasons.Contains("shop") && !_dialogue.Visible, new { injection = "Godot.Input.ParseInputEvent", osInput = false });
            double pausedMinute = _world.WorldMinute;
            _world.Advance(10, 0, _pauseReasons.Count > 0);
            FlushWorldEvents();
            Check("shop_pause_preserves_world", _world.WorldMinute == pausedMinute && _pauseReasons.Contains("shop"));

            var beforeBuy = _world.GetView().Economy;
            TradeResult bought = Submit("buy", "seed.carrot", 2);
            var afterBuy = _world.GetView().Economy;
            Check("ui_buy_conserves_assets", bought.Succeeded && bought.UnitPrice == 25 && bought.TotalPrice == 50
                && afterBuy.Player.Coins == beforeBuy.Player.Coins - 50
                && afterBuy.Shops.Single(s => s.Id == shopId).Coins == beforeBuy.Shops.Single(s => s.Id == shopId).Coins + 50
                && afterBuy.Player.Inventory.GetValueOrDefault("seed.carrot") == beforeBuy.Player.Inventory.GetValueOrDefault("seed.carrot") + 2
                && EconomyAssetsConserved(beforeBuy, afterBuy), new { bought, playerCoins = afterBuy.Player.Coins });
            var beforeSell = afterBuy;
            TradeResult sold = Submit("sell", "crop.potato", 1);
            var afterSell = _world.GetView().Economy;
            Check("ui_sell_conserves_assets", sold.Succeeded && sold.UnitPrice == 20 && sold.TotalPrice == 20
                && afterSell.Player.Coins == beforeSell.Player.Coins + 20
                && afterSell.Shops.Single(s => s.Id == shopId).Coins == beforeSell.Shops.Single(s => s.Id == shopId).Coins - 20
                && afterSell.Player.Inventory.GetValueOrDefault("crop.potato") == 1
                && EconomyAssetsConserved(beforeSell, afterSell), new { sold, playerCoins = afterSell.Player.Coins });

            string assetsBeforeFailure = EconomyAssetsJson(afterSell);
            TradeResult rejectedBuy = Submit("buy", "seed.carrot", 9999);
            Check("ui_failed_buy_preserves_assets", !rejectedBuy.Succeeded && rejectedBuy.Code == "insufficient_shop_stock"
                && EconomyAssetsJson(_world.GetView().Economy) == assetsBeforeFailure
                && _tradeFeedback.Text == EconomyReason(rejectedBuy.Code) && _tradeFeedback.Text.Contains("库存", StringComparison.Ordinal), rejectedBuy);
            string? failureScreenshot = await CaptureEconomyScreenshotAsync("failure");
            TradeResult rejectedSell = Submit("sell", "crop.potato", 9999);
            Check("ui_failed_sell_preserves_assets", !rejectedSell.Succeeded && rejectedSell.Code == "insufficient_player_stock"
                && EconomyAssetsJson(_world.GetView().Economy) == assetsBeforeFailure
                && _tradeFeedback.Text == EconomyReason(rejectedSell.Code), rejectedSell);

            await SendEconomyKeyAsync(Key.Escape);
            Check("logical_escape_closes_shop", !_economyPanel.Visible && !_pauseReasons.Contains("shop") && !_menu.Visible);
            bool everySelection = true;
            for (int index = 0; index < 5; index++)
            {
                long sequence = _world.GetView().Economy.InputSequence;
                await SendEconomyKeyAsync((Key)((int)Key.Key1 + index));
                everySelection &= _world.GetView().Economy.Player.SelectedQuickbarIndex == index
                    && _world.GetView().Economy.InputSequence == sequence + 1;
                RememberOperation();
            }
            Check("logical_number_keys_select_quickbar", everySelection && _quickbarButtons.Count == 5
                && EconomyAssetsJson(_world.GetView().Economy) == assetsBeforeFailure,
                new { keys = new[] { "1", "2", "3", "4", "5" }, injection = "Godot.Input.ParseInputEvent", osInput = false });
            string beforeBag = TownJson.Serialize(_world.GetView().Economy);
            await SendEconomyKeyAsync(Key.B);
            Check("logical_b_opens_readonly_inventory", _economyPanel.Visible && _activeShopId is null && !_shopColumn.Visible
                && _pauseReasons.Contains("inventory") && _bagSlots.Count == 24
                && TownJson.Serialize(_world.GetView().Economy) == beforeBag);
            string? bagScreenshot = await CaptureEconomyScreenshotAsync("bag");
            await SendEconomyKeyAsync(Key.Escape);
            Check("logical_escape_closes_inventory", !_economyPanel.Visible && !_pauseReasons.Contains("inventory") && !_menu.Visible);

            var saved = _world.GetView();
            SaveWorld();
            var diskSave = TownSaveFile.Read(_savePath);
            var savedOnDisk = TownJson.Deserialize<TownSnapshot>(diskSave.WorldJson).State;
            Check("economy_save_published", savedOnDisk.BranchId == saved.BranchId
                && TownJson.Serialize(savedOnDisk.Economy) == TownJson.Serialize(saved.Economy));
            await SendEconomyKeyAsync(Key.E);
            TradeResult laterBuy = Submit("buy", "seed.carrot", 1);
            await SendEconomyKeyAsync(Key.Escape);
            await SendEconomyKeyAsync(Key.Key1);
            RememberOperation();
            string previousSession = _world.SessionId;
            Check("economy_changed_after_save", laterBuy.Succeeded && _world.GetView().Economy.Player.SelectedQuickbarIndex == 0
                && TownJson.Serialize(_world.GetView().Economy) != TownJson.Serialize(saved.Economy));
            LoadWorld();
            _view = _world.GetView();
            Check("save_reload_preserves_economy", !_fatal && _world.SessionId != previousSession
                && _view.ParentBranchId == saved.BranchId && _view.PlayerPosition == saved.PlayerPosition
                && _view.Economy.Player.SelectedQuickbarIndex == 4
                && TownJson.Serialize(_view.Economy) == TownJson.Serialize(saved.Economy),
                new { sourceBranch = saved.BranchId, restoredBranch = _view.BranchId, receipts = _view.Economy.Receipts.Count });
            Check("npc_assets_unchanged", _view.Residents.All(r =>
            {
                var original = initial.Residents.Single(s => s.Id == r.Id);
                return r.Coins == original.Coins && SameInventory(r.Inventory, original.Inventory);
            }));
            var records = ReadEconomyJournalRecords();
            bool completeRecords = records.Count == expectedOperations.Count && records.Select((record, index) =>
            {
                var operation = TownJson.Deserialize<EconomyOperationRecord>(record.GetProperty("operation").GetRawText());
                return record.GetProperty("schemaVersion").GetInt32() == 1
                    && record.GetProperty("contentFingerprint").GetString() == _loaded.Content.Fingerprint
                    && record.GetProperty("worldSequenceBefore").GetInt64() >= 0
                    && record.GetProperty("unconsumedSeconds").GetDouble() >= 0
                    && record.TryGetProperty("playerRoute", out _)
                    && record.GetProperty("playerRouteCursor").GetInt32() >= 0
                    && record.GetProperty("pauseReasons").ValueKind == JsonValueKind.Array
                    && TownJson.Serialize(operation) == TownJson.Serialize(expectedOperations[index]);
            }).All(passed => passed);
            Check("economy_journal_records_inputs_and_checkpoints", completeRecords,
                new { records = records.Count, successfulTrades = expectedOperations.Count(o => o.TradeResult?.Succeeded == true),
                    failedTrades = expectedOperations.Count(o => o.TradeResult?.Succeeded == false),
                    quickbarSelections = expectedOperations.Count(o => o.Kind == "quickbar_select") });
            string economyBeforeConversation = TownJson.Serialize(_world.GetView().Economy);
            for (int step = 0; step < 4960; step++)
                _world.Advance(StepSeconds * .5, StepSeconds);
            FlushWorldEvents();
            _view = _world.GetView();
            SyncActors(); RefreshInterface();
            SaveWorld();
            var afterBoundarySave = TownJson.Deserialize<TownSnapshot>(TownSaveFile.Read(_savePath).WorldJson).State;
            Check("fixed_step_boundary_preserves_save", Math.Abs(_view.WorldMinute - 664) < .000001
                && Math.Abs(afterBoundarySave.WorldMinute - _view.WorldMinute) < .000001
                && TownJson.Serialize(afterBoundarySave.Economy) == economyBeforeConversation);
            var shopNeighbor = _view.Residents.Single(r => r.Id == "mina");
            Check("shop_neighbor_button_available", shopNeighbor.ScheduleId == "mina.work"
                && shopNeighbor.Position.DistanceTo(entrance) < .01 && shopNeighbor.RouteCursor == shopNeighbor.Route.Count
                && _view.PlayerPosition.DistanceTo(entrance) < .01 && _residentTalkButton.IsVisibleInTree()
                && !_residentTalkButton.Disabled && !_economyPanel.Visible && _pauseReasons.Count == 0,
                new { _view.WorldMinute, shopNeighbor.Id, shopNeighbor.Position, button = _residentTalkButton.Text });
            int previousConversations = _experimentEvents.Count(e => e.Kind == "player_conversation");
            _residentTalkButton.EmitSignal(Godot.Button.SignalName.Pressed);
            double conversationMinute = _world.WorldMinute;
            _world.Advance(1, 0, _pauseReasons.Count > 0);
            FlushWorldEvents();
            Check("shop_neighbor_dialogue_pauses_world", _dialogue.Visible && !_economyPanel.Visible
                && _pauseReasons.Contains("dialogue") && _world.WorldMinute == conversationMinute
                && _dialogueName.Text == _loaded.Definitions.Residents.Single(r => r.Id == "mina").DisplayName
                && _experimentEvents.Count(e => e.Kind == "player_conversation") == previousConversations + 1
                && _experimentEvents.Last(e => e.Kind == "player_conversation").ResidentId == "mina"
                && TownJson.Serialize(_world.GetView().Economy) == economyBeforeConversation);
            await SendEconomyKeyAsync(Key.Escape);
            bool neighborDialogueClosed = !_dialogue.Visible && !_pauseReasons.Contains("dialogue") && !_menu.Visible;
            await SendEconomyKeyAsync(Key.E);
            Check("shop_reopens_after_neighbor_dialogue", neighborDialogueClosed && _economyPanel.Visible
                && _activeShopId == shopId && _pauseReasons.Contains("shop") && !_dialogue.Visible
                && TownJson.Serialize(_world.GetView().Economy) == economyBeforeConversation);
            _stockButtons["seed.carrot"].EmitSignal(Godot.Button.SignalName.Pressed);
            SyncActors(); RefreshInterface();
            Check("final_shop_visible", _economyPanel.Visible && _activeShopId == shopId && _shopColumn.Visible
                && _pauseReasons.Contains("shop") && !_fatal);
            string? shopScreenshot = await CaptureEconomyScreenshotAsync(null);
            string screenshotStatus = shopScreenshot is not null ? "captured" : _headless && Argument("--screenshot=") is not null ? "unsupported_headless" : "not_requested";
            var report = new
            {
                schemaVersion = 1, test = "godot_economy_smoke", passed = true, scenario = _scenario,
                headless = _headless, mode = _mode, residents = _actors.Count,
                runtimeAssemblyLocation = typeof(object).Assembly.Location,
                engine = Engine.GetVersionInfo()["string"].AsString(), contentFingerprint = _loaded.Content.Fingerprint,
                checks, screenshotStatus, screenshotPath = shopScreenshot,
                screenshots = new { shop = shopScreenshot, bag = bagScreenshot, failure = failureScreenshot },
                runDirectories = _journals.Select(j => j.DirectoryPath).ToArray(),
                coverage = "Actual Godot route movement, shop button callbacks, synthetic Godot key-event routing, asset conservation, file save/restore and durable economy commands. Input uses Godot.Input.ParseInputEvent, not OS keyboard injection. No model requests are dispatched."
            };
            _journal.WriteArtifact("economy-verification-report.json", report);
            WriteReport(report);
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            try
            {
                WriteReport(new { schemaVersion = 1, test = "godot_economy_smoke", passed = false, scenario = _scenario,
                    checks, error = error.GetType().Name, message = error.Message });
            }
            finally { GD.PushError("Economy smoke failed: " + error.Message); GetTree().Quit(1); }
        }
    }

    private async Task SendEconomyKeyAsync(Key logical)
    {
        using var press = new InputEventKey { Keycode = logical, PhysicalKeycode = Key.Pause, Pressed = true, Echo = false };
        using var release = new InputEventKey { Keycode = logical, PhysicalKeycode = Key.Pause, Pressed = false, Echo = false };
        Input.ParseInputEvent(press);
        Input.FlushBufferedEvents();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Input.ParseInputEvent(release);
        Input.FlushBufferedEvents();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task<string?> CaptureEconomyScreenshotAsync(string? suffix)
    {
        string? path = Argument("--screenshot=");
        if (path is null) return null;
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Screenshot requires an absolute path.");
        if (_headless) return null;
        if (suffix is not null) path = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-" + suffix + ".png");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using Image screenshot = GetViewport().GetTexture().GetImage();
        Error result = screenshot.SavePng(path);
        if (result != Error.Ok) throw new IOException("Economy viewport screenshot write failed: " + result);
        return path;
    }

    private List<JsonElement> ReadEconomyJournalRecords()
    {
        var records = new List<JsonElement>();
        foreach (var journal in _journals)
        {
            using var stream = new FileStream(Path.Combine(journal.DirectoryPath, "events.jsonl"), FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.GetProperty("kind").GetString() == "player_economy_command")
                    records.Add(document.RootElement.GetProperty("data").Clone());
            }
        }
        return records;
    }

    private static bool SameInventory(Dictionary<string, int> left, Dictionary<string, int> right)
        => left.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(right.OrderBy(p => p.Key, StringComparer.Ordinal));

    private static string EconomyAssetsJson(EconomyState state) => TownJson.Serialize(new
    {
        state.Player.Coins, state.Player.SlotCapacity, inventory = state.Player.Inventory.OrderBy(p => p.Key, StringComparer.Ordinal),
        shops = state.Shops.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => new { s.Id, s.Coins, inventory = s.Inventory.OrderBy(p => p.Key, StringComparer.Ordinal) })
    });

    private static bool EconomyAssetsConserved(EconomyState before, EconomyState after)
    {
        if ((long)before.Player.Coins + before.Shops.Sum(s => (long)s.Coins) != (long)after.Player.Coins + after.Shops.Sum(s => (long)s.Coins)) return false;
        var resources = before.Player.Inventory.Keys.Concat(after.Player.Inventory.Keys)
            .Concat(before.Shops.SelectMany(s => s.Inventory.Keys)).Concat(after.Shops.SelectMany(s => s.Inventory.Keys)).Distinct();
        return resources.All(id => (long)before.Player.Inventory.GetValueOrDefault(id) + before.Shops.Sum(s => (long)s.Inventory.GetValueOrDefault(id))
            == (long)after.Player.Inventory.GetValueOrDefault(id) + after.Shops.Sum(s => (long)s.Inventory.GetValueOrDefault(id)));
    }
}
