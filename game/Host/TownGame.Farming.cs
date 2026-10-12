using CozyTown.Core.Town;
using Godot;

namespace CozyTown.Game;

public partial class TownGame
{
    private Control _farmingPanel = null!;
    private PanelContainer _farmingCard = null!;
    private Label _farmingTitle = null!;
    private Label _farmingStatus = null!;
    private Label _farmingFeedback = null!;
    private VBoxContainer _cropChoices = null!;
    private GridContainer _plotChoices = null!;
    private HBoxContainer _farmActions = null!;
    private VBoxContainer _sleepChoices = null!;
    private SpinBox _sleepHours = null!;
    private Button _sleepConfirm = null!;
    private Button _waterButton = null!;
    private Button _harvestButton = null!;
    private string? _activePlotId;
    private readonly Dictionary<string, Button> _plantButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _plotButtons = new(StringComparer.Ordinal);

    private void CreateFarmingInterface()
    {
        _farmingPanel = new Control { Visible = false };
        _ui.AddChild(_farmingPanel);
        _farmingPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var shade = new ColorRect { Color = new Color(.06f, .14f, .1f, .46f) };
        _farmingPanel.AddChild(shade);
        shade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var panel = Panel();
        _farmingCard = panel;
        _farmingPanel.AddChild(panel);
        Place(panel, -300, -318, 300, 318, .5f, .5f, .5f, .5f);
        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 10);
        panel.AddChild(Margin(stack, 24, 18));
        var header = new HBoxContainer();
        stack.AddChild(header);
        _farmingTitle = Label("照看菜园", 25, _ink);
        _farmingTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(_farmingTitle);
        header.AddChild(Button("×", CloseFarming));
        _plotChoices = new GridContainer { Columns = 3 };
        _plotChoices.AddThemeConstantOverride("separation", 8);
        stack.AddChild(_plotChoices);
        foreach (var plot in _loaded.Definitions.Farming!.Plots)
        {
            string id = plot.Id;
            var button = Button(plot.DisplayName, () => { _activePlotId = id; _farmingFeedback.Text = ""; RefreshFarmingInterface(); });
            button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            button.CustomMinimumSize = new Vector2(0, 32);
            _plotChoices.AddChild(button);
            _plotButtons.Add(id, button);
        }
        _farmingStatus = Label("", 17, _ink);
        _farmingStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _farmingStatus.CustomMinimumSize = new Vector2(0, 80);
        stack.AddChild(_farmingStatus);
        _cropChoices = new VBoxContainer();
        _cropChoices.AddThemeConstantOverride("separation", 8);
        stack.AddChild(_cropChoices);
        foreach (var crop in _loaded.Definitions.Farming.Crops)
        {
            string id = crop.Id;
            var button = Button("", () => SubmitFarm("plant", id));
            button.Icon = _itemIcons.GetValueOrDefault(crop.SeedResourceId);
            button.ExpandIcon = true;
            button.AddThemeConstantOverride("icon_max_width", 24);
            button.Alignment = HorizontalAlignment.Left;
            button.CustomMinimumSize = new Vector2(0, 34);
            _cropChoices.AddChild(button);
            _plantButtons.Add(id, button);
        }
        _farmActions = new HBoxContainer();
        _farmActions.AddThemeConstantOverride("separation", 12);
        stack.AddChild(_farmActions);
        _waterButton = Button("浇水", () => SubmitFarm("water"));
        _harvestButton = Button("收获", () => SubmitFarm("harvest"));
        _waterButton.SizeFlagsHorizontal = _harvestButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _farmActions.AddChild(_waterButton);
        _farmActions.AddChild(_harvestButton);
        _sleepChoices = new VBoxContainer();
        _sleepChoices.AddThemeConstantOverride("separation", 14);
        stack.AddChild(_sleepChoices);
        var hourRow = new HBoxContainer();
        hourRow.AddThemeConstantOverride("separation", 16);
        _sleepChoices.AddChild(hourRow);
        hourRow.AddChild(Label("休息小时", 17, _ink));
        _sleepHours = new SpinBox { MinValue = 1, MaxValue = 12, Step = 1, Value = 8, CustomMinimumSize = new Vector2(160, 42) };
        _sleepHours.GetLineEdit().AddThemeColorOverride("font_color", _ink);
        _sleepHours.GetLineEdit().AddThemeStyleboxOverride("normal", Box(new Color("f5efdb"), new Color("c8cbb0"), 7, 1));
        _sleepHours.GetLineEdit().AddThemeStyleboxOverride("focus", Box(new Color("e9eed9"), _accent, 7, 2));
        _sleepHours.ValueChanged += _ => RefreshFarmingInterface();
        hourRow.AddChild(_sleepHours);
        _sleepConfirm = Button("确认休息", SubmitSleep);
        _sleepChoices.AddChild(_sleepConfirm);
        stack.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        _farmingFeedback = Label("", 15, _accent);
        _farmingFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _farmingFeedback.CustomMinimumSize = new Vector2(0, 46);
        stack.AddChild(_farmingFeedback);
        stack.AddChild(Button("回到小镇  Esc", CloseFarming));
    }

    private FarmPlotDefinition? NearbyPlot() => _loaded.Definitions.Farming?.Plots
        .Where(p => _world.CanInteractWithPlot(p.Id))
        .OrderBy(p => _loaded.Content.Place(p.PlaceId).Position.DistanceTo(_view.PlayerPosition)).FirstOrDefault();

    private void OpenFarming(string? plotId)
    {
        if (_fatal || _pauseReasons.Count > 0) return;
        _activePlotId = plotId;
        _playerRoute = null;
        _selectedResident = null;
        _inspector.Visible = false;
        _toast.Visible = false;
        _farmingFeedback.Text = "";
        _farmingPanel.Visible = true;
        SetPause("farming", true);
        RefreshFarmingInterface();
    }

    private void CloseFarming()
    {
        _farmingPanel.Visible = false;
        _sleepHours.GetLineEdit().ReleaseFocus();
        SetPause("farming", false);
    }

    private bool FarmingInputAllowed => !_fatal && _pauseReasons.All(r => r == "farming");

    private void RefreshFarmingInterface()
    {
        if (_farmingPanel is null || !_farmingPanel.Visible) return;
        bool sleeping = _activePlotId is null;
        Place(_farmingCard, -300, sleeping ? -210 : -318, 300, sleeping ? 210 : 318, .5f, .5f, .5f, .5f);
        _plotChoices.Visible = _cropChoices.Visible = _farmActions.Visible = !sleeping;
        _sleepChoices.Visible = sleeping;
        _farmingTitle.Text = sleeping ? "回家休息" : "照看菜园";
        if (sleeping)
        {
            double waking = _view.WorldMinute + _sleepHours.Value * 60;
            _farmingStatus.Text = $"休息后：第 {(long)waking / 1440 + 1} 天  {(int)waking % 1440 / 60:00}:{(int)waking % 60:00}\n每日 05:00，浇过水的作物成长一次。\n邻居会继续按各自的日程活动。";
            _sleepConfirm.Disabled = !FarmingInputAllowed;
            return;
        }
        foreach (var pair in _plotButtons)
        {
            pair.Value.Disabled = !FarmingInputAllowed || !_world.CanInteractWithPlot(pair.Key);
            pair.Value.Modulate = pair.Key == _activePlotId ? new Color("d8e6bd") : Colors.White;
        }
        var state = _view.Farming.Plots.Single(p => p.Id == _activePlotId);
        var crop = _loaded.Definitions.Farming!.Crops.SingleOrDefault(c => c.Id == state.CropId);
        _farmingStatus.Text = crop is null ? "空地 · 选一袋种子开始种植。\n每次种植消耗 1 颗种子。\n种下后记得浇水。"
            : state.Mature ? $"{crop.DisplayName} · 已成熟\n可收获 {ResourceName(crop.HarvestResourceId)} ×{crop.HarvestQuantity}\n背包装不下时，作物会留在田里。"
            : $"{crop.DisplayName} · 成长 {state.GrowthDays} / {crop.GrowthDays}\n{(state.Watered ? "已浇水 · 等待清晨成长" : "缺水 · 请先浇水") }\n每次 05:00 成长后，需要重新浇水。";
        foreach (var definition in _loaded.Definitions.Farming.Crops)
        {
            var button = _plantButtons[definition.Id];
            button.Text = $"种植{definition.DisplayName}  ·  种子 {_view.Economy.Player.Inventory.GetValueOrDefault(definition.SeedResourceId)}  ·  {definition.GrowthDays} 次成长";
            button.Disabled = !FarmingInputAllowed;
        }
        _waterButton.Disabled = _harvestButton.Disabled = !FarmingInputAllowed;
    }

    private void SubmitFarm(string action, string? cropId = null)
    {
        if (!FarmingInputAllowed || !_farmingPanel.Visible || _activePlotId is null) return;
        try
        {
            FlushWorldEvents();
            var result = _world.Farm(new FarmRequest(NewEconomyOperationId(), "player", _activePlotId, action, cropId),
                record => PersistFarmingOperation("player_farm_command", record));
            FlushWorldEvents();
            _view = _world.GetView();
            SyncActors();
            _farmingFeedback.Text = result.Succeeded ? action switch
            { "plant" => "已经种下。给新苗浇一次水吧。", "water" => "浇好了，等待下一个清晨。", _ => "收获已放入背包，可以去杂货铺出售。" } : FarmingReason(result.Code);
            _farmingFeedback.Modulate = result.Succeeded ? Colors.White : new Color("ad593d");
            RefreshInterface();
        }
        catch (Exception error) { Fail("farming_record_failed", error); }
    }

    private void SubmitSleep()
    {
        if (_fatal || !_farmingPanel.Visible || _activePlotId is not null) return;
        try { _sleepHours.Apply(); SubmitSleep((int)_sleepHours.Value); }
        catch (Exception error) { Fail("sleep_record_failed", error); }
    }

    private void SubmitSleep(int hours)
    {
        if (_fatal || !_farmingPanel.Visible || _activePlotId is not null) return;
        try
        {
            FlushWorldEvents();
            var result = _world.Sleep(new SleepRequest(NewEconomyOperationId(), "player", hours),
                realSeconds: 0, paused: _pauseReasons.Any(r => r != "farming"),
                persistBeforeCommit: record => PersistFarmingOperation("player_sleep_command", record));
            FlushWorldEvents();
            _view = _world.GetView();
            SyncActors();
            _farmingFeedback.Text = result.Succeeded ? $"休息了 {hours} 小时。现在是第 {(long)_view.WorldMinute / 1440 + 1} 天 {(int)_view.WorldMinute % 1440 / 60:00}:{(int)_view.WorldMinute % 60:00}。" : FarmingReason(result.Code);
            RefreshInterface();
        }
        catch (Exception error) { Fail("sleep_record_failed", error); }
    }

    private void PersistFarmingOperation(string kind, object operation) => _journal.Append(kind, new
    {
        schemaVersion = 1, operation, contentFingerprint = _loaded.Content.Fingerprint,
        worldSequenceBefore = _world.GetView().Sequence, unconsumedSeconds = _accumulator,
        playerRoute = _playerRoute?.ToArray(), playerRouteCursor = _playerRoute is null ? 0 : _playerRouteCursor,
        pauseReasons = _pauseReasons.Order(StringComparer.Ordinal).ToArray()
    });

    private static string FarmingReason(string code) => code switch
    {
        "insufficient_seed" or "insufficient_seeds" => "种子不足，先去杂货铺购买种子。",
        "plot_occupied" => "这块地已经种了作物，请先收获。",
        "plot_empty" or "empty_plot" => "这块地还没有种作物。",
        "already_watered" => "已经浇过水，等下次清晨成长后再浇。",
        "already_mature" => "作物已经成熟，可以收获了。",
        "not_mature" => "作物还没成熟，请按时浇水，等待清晨成长。",
        "inventory_full" => "背包装不下，作物会留在田里。先去出售一些物品。",
        "plot_out_of_reach" => "请走近这块地再操作。",
        "bed_out_of_reach" => "请回到家门口再休息。",
        "paused" => "世界正因其他操作暂停，请先关闭其他面板。",
        "invalid_hours" or "invalid_sleep_hours" => "请选择 1～12 个整小时。",
        "advance_failed" or "settlement_failed" => "清晨结算未完成，本次休息的时间和资产保持原样。",
        "advance_limit" => "待推进时间超过本次上限，请先恢复正常运行。",
        "numeric_overflow" => "物品数量已达上限，本次操作未改变资产。",
        _ => "操作未完成：" + code
    };
}
