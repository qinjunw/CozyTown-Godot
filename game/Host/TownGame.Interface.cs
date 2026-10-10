using System.Text;
using CozyTown.Core.Town;
using Godot;

namespace CozyTown.Game;

public partial class TownGame
{
    private Control _ui = null!;
    private PanelContainer _inspector = null!;
    private Control _menu = null!;
    private PanelContainer _toast = null!;
    private Label _toastText = null!;
    private Label _clock = null!;
    private Label _modeLabel = null!;
    private Button _talkButton = null!;
    private Button _residentTalkButton = null!;
    private Label _inspectorTitle = null!;
    private RichTextLabel _inspectorBody = null!;
    private PanelContainer _dialogue = null!;
    private Label _dialogueName = null!;
    private Label _dialogueText = null!;
    private Label _dialogueStatus = null!;
    private Button _generateDialogue = null!;
    private TextureRect _dialoguePortrait = null!;
    private readonly Dictionary<string, Button> _residentButtons = new(StringComparer.Ordinal);
    private readonly Color _ink = new("364a43");
    private readonly Color _muted = new("728476");
    private readonly Color _paper = new("fcf8e9");
    private readonly Color _accent = new("637f5c");
    private SystemFont _font = null!;

    private void CreateInterface()
    {
        _font = new SystemFont { FontNames = ["Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans CJK SC", "Segoe UI"] };
        var layer = new CanvasLayer { Layer = 10 };
        AddChild(layer);
        _ui = new Control { Name = "Interface", MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(_ui);
        _ui.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _ui.Theme = new Theme { DefaultFont = _font, DefaultFontSize = 17 };

        var top = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _ui.AddChild(top);
        Place(top, 24, 20, -24, 84, rightAnchor: 1);
        top.AddThemeConstantOverride("separation", 14);
        var titlePanel = Panel();
        top.AddChild(titlePanel);
        var titleStack = new VBoxContainer();
        titlePanel.AddChild(Margin(titleStack, 18, 9));
        titleStack.AddThemeConstantOverride("separation", 2);
        titleStack.AddChild(Label("榆溪小镇", 21, _ink));
        _clock = Label("春日 · 第 1 天    09:00", 14, _muted);
        titleStack.AddChild(_clock);
        top.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        foreach (var definition in _loaded.Definitions.Residents)
        {
            string id = definition.Id;
            var button = Button(definition.DisplayName, () => SelectResident(id));
            button.CustomMinimumSize = new Vector2(112, 60);
            button.Icon = Portrait(id);
            button.ExpandIcon = true;
            button.AddThemeConstantOverride("icon_max_width", 38);
            button.AddThemeConstantOverride("h_separation", 7);
            top.AddChild(button);
            _residentButtons.Add(id, button);
        }

        var subtitle = Label("河风，树影，还有各自忙碌的邻居。", 15, new Color("fdf5da"));
        subtitle.AddThemeColorOverride("font_shadow_color", new Color(0.15f, .25f, .18f, .8f));
        subtitle.AddThemeConstantOverride("shadow_offset_x", 1);
        subtitle.AddThemeConstantOverride("shadow_offset_y", 2);
        _ui.AddChild(subtitle);
        Place(subtitle, 29, 96, 470, 122);
        subtitle.MouseFilter = Control.MouseFilterEnum.Ignore;

        var bottom = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _ui.AddChild(bottom);
        Place(bottom, 24, -76, -24, -20, rightAnchor: 1, topAnchor: 1, bottomAnchor: 1);
        bottom.AddThemeConstantOverride("separation", 12);
        var guidePanel = Panel();
        bottom.AddChild(guidePanel);
        var guide = new HBoxContainer();
        guide.AddThemeConstantOverride("separation", 16);
        guidePanel.AddChild(Margin(guide, 17, 8));
        guide.AddChild(Label("W A S D  /  点击地面移动", 15, _ink));
        _talkButton = Button("E  交互", InteractNearby);
        guide.AddChild(_talkButton);
        _residentTalkButton = Button("和邻居聊聊", TalkNearby);
        guide.AddChild(_residentTalkButton);
        _modeLabel = Label("规则模拟", 13, _muted);
        guide.AddChild(_modeLabel);
        bottom.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        bottom.AddChild(Button("回到玩家", () => { _selectedResident = null; _inspector.Visible = false; }));
        bottom.AddChild(Button("观察手册  Tab", ToggleInspector));
        bottom.AddChild(Button("菜单  Esc", ToggleMenu));

        _inspector = Panel();
        _ui.AddChild(_inspector);
        Place(_inspector, -362, 99, -24, -96, leftAnchor: 1, rightAnchor: 1, bottomAnchor: 1);
        var inspectorLayout = new VBoxContainer();
        _inspector.AddChild(Margin(inspectorLayout, 20, 18));
        inspectorLayout.AddThemeConstantOverride("separation", 12);
        var inspectorHeader = new HBoxContainer();
        inspectorLayout.AddChild(inspectorHeader);
        _inspectorTitle = Label("观察手册", 22, _ink);
        _inspectorTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        inspectorHeader.AddChild(_inspectorTitle);
        inspectorHeader.AddChild(Button("×", () => _inspector.Visible = false));
        inspectorLayout.AddChild(Label("全局观察视角 · 角色只知道各自所见", 12, _muted));
        _inspectorBody = new RichTextLabel
        {
            BbcodeEnabled = false, ScrollActive = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SelectionEnabled = true
        };
        _inspectorBody.AddThemeColorOverride("default_color", _ink);
        _inspectorBody.AddThemeFontSizeOverride("normal_font_size", 15);
        _inspectorBody.AddThemeConstantOverride("line_separation", 5);
        inspectorLayout.AddChild(_inspectorBody);
        inspectorLayout.AddChild(Button("导出本次观察记录", ExportRun));
        _inspector.Visible = false;

        _toast = Panel();
        _ui.AddChild(_toast);
        Place(_toast, -305, -215, 305, -164, leftAnchor: .5f, rightAnchor: .5f, topAnchor: 1, bottomAnchor: 1);
        _toast.MouseFilter = Control.MouseFilterEnum.Ignore;
        _toastText = Label("", 15, _ink);
        _toastText.HorizontalAlignment = HorizontalAlignment.Center;
        _toastText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _toast.AddChild(Margin(_toastText, 14, 9));
        _toast.Visible = false;

        _dialogue = Panel();
        _ui.AddChild(_dialogue);
        Place(_dialogue, -360, -346, 360, -94, leftAnchor: .5f, rightAnchor: .5f, topAnchor: 1, bottomAnchor: 1);
        var dialogueRow = new HBoxContainer();
        dialogueRow.AddThemeConstantOverride("separation", 18);
        _dialogue.AddChild(Margin(dialogueRow, 22, 18));
        _dialoguePortrait = new TextureRect { CustomMinimumSize = new Vector2(88, 100), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.Nearest };
        dialogueRow.AddChild(_dialoguePortrait);
        var dialogueColumn = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        dialogueRow.AddChild(dialogueColumn);
        _dialogueName = Label("", 22, _accent);
        _dialogueText = Label("", 18, _ink);
        _dialogueText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _dialogueText.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        dialogueColumn.AddChild(_dialogueName);
        dialogueColumn.AddChild(_dialogueText);
        _dialogueStatus = Label("固定回应", 13, _muted);
        _dialogueStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        dialogueColumn.AddChild(_dialogueStatus);
        var dialogueActions = new HBoxContainer();
        dialogueActions.AddThemeConstantOverride("separation", 10);
        _generateDialogue = Button("生成一段反馈", GeneratePlayerDialogue);
        _generateDialogue.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        dialogueActions.AddChild(_generateDialogue);
        dialogueActions.AddChild(Button("继续散步", CloseDialogue));
        dialogueColumn.AddChild(dialogueActions);
        _dialogue.Visible = false;
        CreateEconomyInterface(top, bottom);
        CreateFarmingInterface();
        CreateProductionInterface();
        CreateMenu();
    }

    private void CreateMenu()
    {
        _menu = new Control();
        _ui.AddChild(_menu);
        _menu.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var shade = new ColorRect { Color = new Color(.06f, .14f, .1f, .56f) };
        _menu.AddChild(shade);
        shade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var panel = Panel();
        _menu.AddChild(panel);
        Place(panel, -210, -230, 210, 230, leftAnchor: .5f, rightAnchor: .5f, topAnchor: .5f, bottomAnchor: .5f);
        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 12);
        panel.AddChild(Margin(stack, 30, 24));
        var title = Label("在榆溪歇一会儿", 25, _ink);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        stack.AddChild(title);
        var detail = Label("世界已暂停，邻居会等你回来。", 14, _muted);
        detail.HorizontalAlignment = HorizontalAlignment.Center;
        stack.AddChild(detail);
        stack.AddChild(new Control { CustomMinimumSize = new Vector2(0, 7) });
        stack.AddChild(Button("继续散步", ToggleMenu));
        stack.AddChild(Button("保存这一刻   F5", SaveWorld));
        stack.AddChild(Button("读取上次存档   F9", LoadWorld));
        stack.AddChild(Button("导出观察记录", ExportRun));
        stack.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        stack.AddChild(Button("离开小镇", () => GetTree().Quit()));
        _menu.Visible = false;
    }

    private void ToggleMenu()
    {
        if (_productionPanel.Visible) { CloseProduction(); return; }
        if (_farmingPanel.Visible) { CloseFarming(); return; }
        if (_economyPanel.Visible) { CloseEconomy(); return; }
        if (_dialogue.Visible && !_menu.Visible) { _menu.Visible = true; SetPause("menu", true); return; }
        _menu.Visible = !_menu.Visible;
        SetPause("menu", _menu.Visible);
    }

    private void ToggleInspector()
    {
        if (_economyPanel.Visible || _dialogue.Visible || _menu.Visible || _farmingPanel.Visible || _productionPanel.Visible) return;
        _inspector.Visible = !_inspector.Visible;
        if (_inspector.Visible && _selectedResident == null) _selectedResident = _view.Residents[0].Id;
        RefreshInterface();
    }

    private void SelectResident(string id)
    {
        if (_fatal) return;
        if (_dialogue.Visible && TownDialogueHost.TextAllowed(_pauseReasons))
        {
            try
            {
                if (!DialogueHost.Open(id, _pauseReasons)) { Toast("走近这位邻居后再交谈。"); return; }
                FlushWorldEvents();
                RefreshPlayerDialogue();
            }
            catch (Exception error) { Fail("dialogue_persistence_failed", error); }
            return;
        }
        if (_pauseReasons.Count > 0) return;
        _selectedResident = id;
        _inspector.Visible = true;
        RefreshInterface();
    }

    private void TalkNearby()
    {
        if (_pauseReasons.Count > 0 || _fatal) return;
        ResidentState? resident = _view.Residents.OrderBy(r => r.Position.DistanceTo(_view.PlayerPosition)).FirstOrDefault();
        if (resident == null || resident.Position.DistanceTo(_view.PlayerPosition) > 42) { Toast("走近一位邻居，再按 E 交谈。"); return; }
        TalkResident(resident.Id);
    }

    private void TalkResident(string residentId)
    {
        if (_pauseReasons.Count > 0 || _fatal) return;
        if (!DialogueHost.Open(residentId, _pauseReasons)) { Toast("走近这位邻居后再交谈。"); return; }
        FlushWorldEvents();
        _dialogue.Visible = true;
        SetPause("dialogue", true);
        RefreshPlayerDialogue();
    }

    private void CloseDialogue()
    {
        try
        {
            DialogueHost.Close();
            FlushWorldEvents();
            _dialogue.Visible = false;
            SetPause("dialogue", false);
        }
        catch (Exception error) { Fail("dialogue_persistence_failed", error); }
    }

    private void RefreshInterface()
    {
        int minute = (int)_view.WorldMinute % 1440;
        _clock.Text = $"春日 · 第 {(long)_view.WorldMinute / 1440 + 1} 天    {minute / 60:00}:{minute % 60:00}" + (_pauseReasons.Count > 0 ? "  · 暂停" : "");
        _modeLabel.Text = _scenario is "farming-capacity-fixture" or "production-capacity-fixture" ? "满包验证初态" : _mode == "real" ? "真实模型" : "规则模拟";
        ResidentState? nearest = _view.Residents.Where(r => r.Position.DistanceTo(_view.PlayerPosition) <= 42)
            .OrderBy(r => r.Position.DistanceTo(_view.PlayerPosition)).FirstOrDefault();
        string? productionId = NearbyProductionId();
        _talkButton.Text = NearbyShop() is not null ? "E  逛杂货铺" : NearbyPlot() is not null ? "E  照看菜园" : _world.CanSleep() ? "E  回家休息" : productionId is not null ? "E  " + ProductionTargets().Single(t => t.Id == productionId).Name : nearest == null ? "E  交互" : "E  和" + _loaded.Definitions.Residents.Single(r => r.Id == nearest.Id).DisplayName + "交谈";
        _talkButton.Disabled = _pauseReasons.Count > 0;
        _residentTalkButton.Visible = (NearbyShop() is not null || NearbyPlot() is not null || _world.CanSleep() || productionId is not null) && nearest is not null;
        _residentTalkButton.Disabled = _pauseReasons.Count > 0;
        foreach (var pair in _residentButtons)
            pair.Value.Modulate = _selectedResident == pair.Key ? new Color("d8e9c8") : Colors.White;
        RefreshEconomyInterface();
        RefreshFarmingInterface();
        RefreshProductionInterface();
        RefreshPlayerDialogue();
        if (!_inspector.Visible || _selectedResident == null) return;
        ResidentState resident = _view.Residents.Single(r => r.Id == _selectedResident);
        ResidentDefinition definition = _loaded.Definitions.Residents.Single(r => r.Id == resident.Id);
        _inspectorTitle.Text = definition.DisplayName + "的一天";
        var text = new StringBuilder();
        text.AppendLine("现在\n" + ActivityLabel(resident.Activity));
        if (resident.RouteCursor < resident.Route.Count) text.AppendLine("正在路上 · 剩余 " + (resident.Route.Count - resident.RouteCursor) + " 个路点");
        if (resident.BlockedReason != null) text.AppendLine("行路受阻：" + resident.BlockedReason);
        text.AppendLine("\n今天的打算\n" + definition.Personality);
        var schedule = definition.Schedule.Single(s => minute >= s.StartMinute && minute < s.EndMinute);
        text.AppendLine($"\n日程\n{schedule.StartMinute / 60:00}:{schedule.StartMinute % 60:00}—{schedule.EndMinute / 60:00}:{schedule.EndMinute % 60:00}  {schedule.Activity}");
        LocalObservation observation = _world.Observe(resident.Id);
        text.AppendLine("\n此刻能看见\n" + (observation.Residents.Length == 0 ? "附近暂时没有其他居民。" : string.Join("、", observation.Residents.Select(r => r.DisplayName))));
        text.AppendLine("\n随身物品 · 仅观察者可见");
        text.AppendLine(string.Join("  ", resident.Inventory.Where(p => p.Value > 0).Select(p => ResourceName(p.Key) + " ×" + p.Value)) + "\n零钱 " + resident.Coins);
        PendingDecisionState? pending = _view.PendingDecisions.SingleOrDefault(p => p.Request.ResidentId == resident.Id);
        if (pending != null) text.AppendLine($"\n正在考虑\n可选：{string.Join("、", pending.Request.AllowedActions.Select(ActivityLabel))}\n等待余量 {pending.Request.RemainingWaitSeconds:0.0} 秒");
        CommitmentState? meeting = _view.Commitments.LastOrDefault(c => c.InitiatorId == resident.Id || c.InviteeId == resident.Id);
        if (meeting != null)
        {
            text.AppendLine("\n最近的约定\n" + ActivityLabel(meeting.Stage));
            foreach (string line in meeting.Dialogue.TakeLast(4)) text.AppendLine(line);
        }
        text.AppendLine("\n最近发生");
        foreach (TownEvent entry in _recentEvents.Where(e => e.ResidentId == resident.Id).TakeLast(6))
            text.AppendLine($"{(int)entry.WorldMinute % 1440 / 60:00}:{(int)entry.WorldMinute % 60:00}  {SourceLabel(entry.Source)} · {ActivityLabel(entry.Reason)}");
        if (_model != null)
        {
            var budget = _model.Budget;
            text.AppendLine($"\n共用实验额度\n{budget.TotalAttempts} / 48 次 · 预留 {budget.ReservedMicros / 1_000_000m:0.######} 元\n保守扣减 {budget.SettledMicros / 1_000_000m:0.######} 元");
            if (budget.StopReason != null) text.AppendLine("派发已停：" + budget.StopReason);
        }
        double scroll = _inspectorBody.GetVScrollBar().Value;
        _inspectorBody.Text = text.ToString();
        _inspectorBody.GetVScrollBar().Value = scroll;
    }

    private string ResourceName(string id) => _loaded.Definitions.Resources.FirstOrDefault(r => r.Id == id)?.DisplayName ?? id;
    private static string SourceLabel(string source) => source switch { "real_model" => "真实模型", "rules" => "独立规则", "rule_fallback" => "规则回退", "player" => "玩家", _ => "世界" };
    private static string ActivityLabel(string activity) => activity switch
    {
        "wait" => "稍作等待", "approach" => "走近邻居", "invite" => "发出邀请", "respond" => "回应提议",
        "propose_exchange" => "提出交换", "speak" => "交谈", "end" => "告别", "offered" => "等待回应",
        "exchange_offered" => "等待交换确认", "conversation" => "正在交谈", "travelling" => "赴约途中",
        "awaiting_partner" => "等待同伴到场", "ended" => "已经结束", "free" => "自由活动", "schedule" => "按日程活动",
        "atomic_transfer" => "交换完成", "both_arrived" => "双方到场", "activity_changed" => "活动已变化",
        "window_ended" => "空闲时间结束", "target_busy" => "对方正在忙", "target_not_visible" => "已经看不见对方",
        "opportunity" => "发现行动机会", "attempt_registered" => "开始考虑", "consumed" => "决定已处理",
        _ => activity
    };

    private AtlasTexture Portrait(string id)
    {
        string[] order = ["mina", "eli", "ren", "sora"];
        return new AtlasTexture { Atlas = ResourceLoader.Load<Texture2D>("res://Art/Characters/npc_portraits_48.png"), Region = new Rect2(Math.Max(0, Array.IndexOf(order, id)) * 48, 0, 48, 48) };
    }
    private void Toast(string text, double seconds = 4)
    {
        if (_toast == null) return;
        if (_productionPanel is not null && _productionPanel.Visible)
        {
            _productionFeedback.Text = text;
            _productionFeedback.Modulate = _fatal ? new Color("ad593d") : Colors.White;
            _toast.Visible = false;
            return;
        }
        if (_farmingPanel is not null && _farmingPanel.Visible)
        {
            _farmingFeedback.Text = text;
            _farmingFeedback.Modulate = _fatal ? new Color("ad593d") : Colors.White;
            _toast.Visible = false;
            return;
        }
        if (_economyPanel is not null && _economyPanel.Visible)
        {
            _tradeFeedback.Text = text;
            _tradeFeedback.Modulate = _fatal ? new Color("ad593d") : Colors.White;
            _toast.Visible = false;
            return;
        }
        _toastText.Text = text;
        _toast.Visible = true;
        _ui.MoveChild(_toast, _ui.GetChildCount() - 1);
        _toastSeconds = seconds;
    }
    private Label Label(string text, int size, Color color)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }
    private Button Button(string text, Action pressed)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(0, 40), FocusMode = Control.FocusModeEnum.None };
        button.AddThemeColorOverride("font_color", _ink);
        button.AddThemeColorOverride("font_hover_color", _ink);
        button.AddThemeColorOverride("font_pressed_color", _ink);
        button.AddThemeFontSizeOverride("font_size", 16);
        button.AddThemeStyleboxOverride("normal", Box(new Color("f5efdb"), new Color("dedbc5"), 8, 1));
        button.AddThemeStyleboxOverride("hover", Box(new Color("e9eed9"), new Color("b6c6a5"), 8, 1));
        button.AddThemeStyleboxOverride("pressed", Box(new Color("d3dfc1"), new Color("9eaf8f"), 8, 1));
        button.Pressed += pressed;
        return button;
    }
    private PanelContainer Panel()
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Box(_paper, new Color("c8cbb0"), 10, 1));
        return panel;
    }
    private static StyleBoxFlat Box(Color background, Color border, int radius, int width)
    {
        var style = new StyleBoxFlat { BgColor = background, BorderColor = border, ShadowColor = new Color(0.08f, .18f, .1f, .2f), ShadowSize = 4, ShadowOffset = new Vector2(0, 3), ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 4, ContentMarginBottom = 4 };
        style.SetBorderWidthAll(width);
        style.SetCornerRadiusAll(radius);
        return style;
    }
    private static MarginContainer Margin(Control child, int horizontal, int vertical)
    {
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", horizontal);
        margin.AddThemeConstantOverride("margin_right", horizontal);
        margin.AddThemeConstantOverride("margin_top", vertical);
        margin.AddThemeConstantOverride("margin_bottom", vertical);
        margin.AddChild(child);
        return margin;
    }
    private static void Place(Control control, float left, float top, float right, float bottom,
        float leftAnchor = 0, float rightAnchor = 0, float topAnchor = 0, float bottomAnchor = 0)
    {
        control.AnchorLeft = leftAnchor; control.AnchorRight = rightAnchor;
        control.AnchorTop = topAnchor; control.AnchorBottom = bottomAnchor;
        control.OffsetLeft = left; control.OffsetRight = right;
        control.OffsetTop = top; control.OffsetBottom = bottom;
    }
}
