using CozyTown.Core.Town;
using Godot;
using System.Text.Json;

namespace CozyTown.Game;

public partial class TownGame
{
    private Control _economyPanel = null!;
    private Label _wallet = null!;
    private Label _bagSummary = null!;
    private Label _shopSummary = null!;
    private Label _tradeDetail = null!;
    private Label _tradeFeedback = null!;
    private Label _economyTitle = null!;
    private Label _economySubtitle = null!;
    private VBoxContainer _shopColumn = null!;
    private VBoxContainer _stockList = null!;
    private GridContainer _bagGrid = null!;
    private HBoxContainer _hotbar = null!;
    private Button _buyTab = null!;
    private Button _sellTab = null!;
    private Button _tradeButton = null!;
    private SpinBox _tradeQuantity = null!;
    private readonly List<Button> _quickbarButtons = [];
    private readonly List<Label> _bagSlots = [];
    private readonly List<TextureRect> _bagIcons = [];
    private readonly Dictionary<string, Button> _stockButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AtlasTexture> _itemIcons = new(StringComparer.Ordinal);
    private string? _activeShopId;
    private string? _tradeResourceId;
    private string _tradeDirection = "buy";
    private bool _refreshingEconomy;

    private void CreateEconomyInterface(HBoxContainer top, HBoxContainer bottom)
    {
        using (var catalog = JsonDocument.Parse(TownContentLoader.ReadText("res://Art/catalog.json")))
        {
            var items = catalog.RootElement.GetProperty("items");
            var atlas = ResourceLoader.Load<Texture2D>(items.GetProperty("atlas").GetString()!);
            int index = 0;
            foreach (var resource in items.GetProperty("resourceIds").EnumerateArray())
            {
                _itemIcons.Add(resource.GetString()!, new AtlasTexture { Atlas = atlas, Region = new Rect2(index % 6 * 16, index / 6 * 16, 16, 16) });
                index++;
            }
        }
        var walletPanel = Panel();
        top.AddChild(walletPanel);
        _wallet = Label("", 17, _accent);
        walletPanel.AddChild(Margin(_wallet, 12, 6));
        bottom.AddChild(Button("背包  B", ToggleInventory));

        _hotbar = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _hotbar.AddThemeConstantOverride("separation", 8);
        _ui.AddChild(_hotbar);
        Place(_hotbar, -296, -153, 296, -87, .5f, .5f, 1, 1);
        for (int index = 0; index < 5; index++)
        {
            int slot = index;
            var button = Button("", () => SelectQuickbarSlot(slot));
            button.CustomMinimumSize = new Vector2(112, 64);
            button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            button.ExpandIcon = true;
            button.AddThemeConstantOverride("icon_max_width", 24);
            button.AddThemeFontSizeOverride("font_size", 13);
            _hotbar.AddChild(button);
            _quickbarButtons.Add(button);
        }

        _economyPanel = new Control { Name = "EconomyPanel" };
        _ui.AddChild(_economyPanel);
        _economyPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var shade = new ColorRect { Color = new Color(.06f, .14f, .1f, .57f) };
        _economyPanel.AddChild(shade);
        shade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var paper = Panel();
        _economyPanel.AddChild(paper);
        Place(paper, -478, -301, 478, 301, .5f, .5f, .5f, .5f);
        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 14);
        paper.AddChild(Margin(layout, 22, 17));

        var header = new HBoxContainer();
        layout.AddChild(header);
        var titles = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        header.AddChild(titles);
        _economyTitle = Label("随身行囊", 26, _ink);
        _economySubtitle = Label("在这里清点旅途中的收获。", 14, _muted);
        titles.AddChild(_economyTitle);
        titles.AddChild(_economySubtitle);
        header.AddChild(Button("收起  Esc", CloseEconomy));

        var columns = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 24);
        layout.AddChild(columns);
        var bag = new VBoxContainer { CustomMinimumSize = new Vector2(418, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        bag.AddThemeConstantOverride("separation", 10);
        columns.AddChild(bag);
        _bagSummary = Label("", 17, _accent);
        bag.AddChild(_bagSummary);
        var bagScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        bag.AddChild(bagScroll);
        _bagGrid = new GridContainer { Columns = 5, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _bagGrid.AddThemeConstantOverride("h_separation", 6);
        _bagGrid.AddThemeConstantOverride("v_separation", 7);
        bagScroll.AddChild(_bagGrid);
        bag.AddChild(Label("前五格同步到快捷栏 · 1—5 选择\n背包只读，选择不会消耗物品。", 13, _muted));

        _shopColumn = new VBoxContainer { CustomMinimumSize = new Vector2(406, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _shopColumn.AddThemeConstantOverride("separation", 9);
        columns.AddChild(_shopColumn);
        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 8);
        _shopColumn.AddChild(tabs);
        _buyTab = Button("购买物资", () => SetTradeDirection("buy"));
        _sellTab = Button("出售物品", () => SetTradeDirection("sell"));
        _buyTab.SizeFlagsHorizontal = _sellTab.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        tabs.AddChild(_buyTab); tabs.AddChild(_sellTab);
        _shopSummary = Label("", 13, _muted);
        _shopColumn.AddChild(_shopSummary);
        var stockScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _shopColumn.AddChild(stockScroll);
        _stockList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _stockList.AddThemeConstantOverride("separation", 5);
        stockScroll.AddChild(_stockList);
        _tradeDetail = Label("", 15, _ink);
        _tradeDetail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _tradeDetail.CustomMinimumSize = new Vector2(0, 45);
        _shopColumn.AddChild(_tradeDetail);
        var order = new HBoxContainer();
        order.AddThemeConstantOverride("separation", 10);
        _shopColumn.AddChild(order);
        order.AddChild(Label("数量", 15, _muted));
        _tradeQuantity = new SpinBox { MinValue = 1, MaxValue = 9999, Step = 1, Value = 1, CustomMinimumSize = new Vector2(112, 40) };
        _tradeQuantity.GetLineEdit().AddThemeColorOverride("font_color", _ink);
        _tradeQuantity.GetLineEdit().AddThemeStyleboxOverride("normal", Box(new Color("f5efdb"), new Color("c8cbb0"), 7, 1));
        _tradeQuantity.GetLineEdit().AddThemeStyleboxOverride("focus", Box(new Color("e9eed9"), _accent, 7, 2));
        _tradeQuantity.ValueChanged += _ => { if (!_refreshingEconomy) RefreshEconomyInterface(); };
        order.AddChild(_tradeQuantity);
        _tradeButton = Button("确认购买", SubmitTrade);
        _tradeButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        order.AddChild(_tradeButton);
        _tradeFeedback = Label("", 14, _accent);
        _tradeFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _tradeFeedback.CustomMinimumSize = new Vector2(0, 22);
        layout.AddChild(_tradeFeedback);
        _economyPanel.Visible = false;
    }

    private ShopDefinition? NearbyShop() => _loaded.Definitions.Economy?.Shops
        .Where(shop => _world.CanInteractWithShop(shop.Id))
        .OrderBy(shop => _loaded.Content.Place(shop.PlaceId).Position.DistanceTo(_view.PlayerPosition)).FirstOrDefault();

    private void InteractNearby()
    {
        if (_pauseReasons.Count > 0 || _fatal) return;
        var shop = NearbyShop();
        if (shop is not null) OpenEconomy(shop.Id);
        else if (NearbyPlot() is { } plot) OpenFarming(plot.Id);
        else if (_world.CanSleep()) OpenFarming(null);
        else if (NearbyProductionId() is { } productionId) OpenProduction(productionId);
        else TalkNearby();
    }

    private void ToggleInventory()
    {
        if (_economyPanel.Visible) { CloseEconomy(); return; }
        if (_pauseReasons.Count == 0 && !_fatal) OpenEconomy(null);
    }

    private void OpenEconomy(string? shopId)
    {
        if (_fatal || _pauseReasons.Count > 0 || _view.Economy is null) return;
        _activeShopId = shopId;
        _tradeDirection = "buy";
        _tradeResourceId = null;
        _tradeQuantity.Value = 1;
        _tradeFeedback.Text = "";
        _toast.Visible = false;
        _selectedResident = null;
        _playerRoute = null;
        _inspector.Visible = false;
        _economyPanel.Visible = true;
        RebuildStockList();
        SetPause(shopId is null ? "inventory" : "shop", true);
        RefreshEconomyInterface();
    }

    private void CloseEconomy()
    {
        if (!_economyPanel.Visible) return;
        _economyPanel.Visible = false;
        _tradeQuantity.GetLineEdit().ReleaseFocus();
        SetPause(_activeShopId is null ? "inventory" : "shop", false);
        _activeShopId = null;
    }

    private void SetTradeDirection(string direction)
    {
        _tradeDirection = direction;
        _tradeResourceId = null;
        _tradeQuantity.Value = 1;
        _tradeFeedback.Text = "";
        RebuildStockList();
        RefreshEconomyInterface();
    }

    private void RebuildStockList()
    {
        foreach (Node child in _stockList.GetChildren()) { _stockList.RemoveChild(child); child.QueueFree(); }
        _stockButtons.Clear();
        if (_activeShopId is null) return;
        var shop = _loaded.Definitions.Economy!.Shops.Single(s => s.Id == _activeShopId);
        foreach (var price in shop.Prices.OrderBy(p => _loaded.Definitions.Resources.Single(r => r.Id == p.ResourceId).DisplayOrder).ThenBy(p => p.ResourceId, StringComparer.Ordinal))
        {
            if ((_tradeDirection == "buy" ? price.BuyPrice : price.SellPrice) <= 0) continue;
            string id = price.ResourceId;
            _tradeResourceId ??= id;
            var button = Button("", () => { _tradeResourceId = id; _tradeFeedback.Text = ""; RefreshEconomyInterface(); });
            button.Icon = _itemIcons.GetValueOrDefault(id);
            button.ExpandIcon = true;
            button.AddThemeConstantOverride("icon_max_width", 24);
            button.Alignment = HorizontalAlignment.Left;
            button.CustomMinimumSize = new Vector2(0, 39);
            _stockList.AddChild(button);
            _stockButtons.Add(id, button);
        }
    }

    private bool EconomyInputAllowed => !_fatal && _pauseReasons.All(reason => reason is "shop" or "inventory");

    private void SelectQuickbarSlot(int index)
    {
        if (!EconomyInputAllowed || _world.GetView().Economy is null) return;
        try
        {
            FlushWorldEvents();
            _world.SelectQuickbar(NewEconomyOperationId(), index, PersistEconomyOperation);
            FlushWorldEvents();
            _view = _world.GetView();
            RefreshEconomyInterface();
        }
        catch (Exception error) { Fail("economy_record_failed", error); }
    }

    private string NewEconomyOperationId() => _world.GetView().BranchId + ":player:" + Guid.NewGuid().ToString("N");

    private void PersistEconomyOperation(EconomyOperationRecord operation)
    {
        _journal.Append("player_economy_command", new
        {
            schemaVersion = 1, operation, contentFingerprint = _loaded.Content.Fingerprint,
            worldSequenceBefore = _world.GetView().Sequence, unconsumedSeconds = _accumulator,
            playerRoute = _playerRoute?.ToArray(), playerRouteCursor = _playerRoute is null ? 0 : _playerRouteCursor,
            pauseReasons = _pauseReasons.Order(StringComparer.Ordinal).ToArray()
        });
    }

    private void SubmitTrade()
    {
        if (!EconomyInputAllowed || !_economyPanel.Visible || _activeShopId is null || _tradeResourceId is null) return;
        try { _tradeQuantity.Apply(); SubmitTrade((int)_tradeQuantity.Value); }
        catch (Exception error) { Fail("economy_record_failed", error); }
    }

    private void SubmitTrade(int quantity)
    {
        if (!EconomyInputAllowed || !_economyPanel.Visible || _activeShopId is null || _tradeResourceId is null) return;
        try
        {
            var quote = _world.QuoteTrade(_activeShopId, _tradeResourceId, _tradeDirection, quantity);
            var request = new TradeRequest(NewEconomyOperationId(), "player", _activeShopId, _tradeResourceId, _tradeDirection, quantity, quote.UnitPrice);
            FlushWorldEvents();
            var result = _world.Trade(request, PersistEconomyOperation);
            FlushWorldEvents();
            _view = _world.GetView();
            _tradeFeedback.Text = result.Succeeded
                ? $"已{(_tradeDirection == "buy" ? "买入" : "售出")} {ResourceName(_tradeResourceId)} ×{quantity}。"
                : EconomyReason(result.Code);
            _tradeFeedback.Modulate = result.Succeeded ? Colors.White : new Color("ad593d");
            RefreshEconomyInterface();
        }
        catch (Exception error) { Fail("economy_record_failed", error); }
    }

    private void RefreshEconomyInterface()
    {
        if (_wallet is null || _view.Economy is null) return;
        _refreshingEconomy = true;
        try
        {
            var economy = _view.Economy;
            var slots = _world.GetPlayerInventorySlots().ToArray();
            _wallet.Text = $"零钱  {economy.Player.Coins:N0}";
            for (int index = 0; index < _quickbarButtons.Count; index++)
            {
                var slot = slots[index];
                var button = _quickbarButtons[index];
                button.Text = slot.ResourceId is null ? $"{index + 1}\n空格" : $"{index + 1}  {ResourceName(slot.ResourceId)}\n×{slot.Quantity}";
                button.Icon = slot.ResourceId is null ? null : _itemIcons.GetValueOrDefault(slot.ResourceId);
                bool selected = economy.Player.SelectedQuickbarIndex == index;
                button.Modulate = selected ? new Color("d8e6bd") : Colors.White;
                button.TooltipText = "快捷栏只读 · 数字键选择，不消耗物品";
                button.Disabled = !EconomyInputAllowed;
            }
            _hotbar.Visible = !_dialogue.Visible && !_economyPanel.Visible && !_menu.Visible && !_inspector.Visible && !(_farmingPanel?.Visible ?? false) && !(_productionPanel?.Visible ?? false);
            if (!_economyPanel.Visible) return;
            while (_bagSlots.Count < slots.Length)
            {
                var cell = Panel();
                cell.CustomMinimumSize = new Vector2(75, 69);
                cell.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                var stack = new VBoxContainer();
                stack.AddThemeConstantOverride("separation", 1);
                cell.AddChild(stack);
                var icon = new TextureRect { CustomMinimumSize = new Vector2(0, 24), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.Nearest, MouseFilter = Control.MouseFilterEnum.Ignore };
                stack.AddChild(icon);
                var label = Label("", 11, _ink);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                stack.AddChild(label);
                _bagGrid.AddChild(cell);
                _bagSlots.Add(label);
                _bagIcons.Add(icon);
            }
            for (int index = 0; index < _bagSlots.Count; index++)
            {
                var slot = slots[index];
                _bagSlots[index].Text = slot.ResourceId is null ? "·" : $"{ResourceName(slot.ResourceId)}\n×{slot.Quantity}";
                _bagIcons[index].Texture = slot.ResourceId is null ? null : _itemIcons.GetValueOrDefault(slot.ResourceId);
                _bagSlots[index].GetParent().GetParent<PanelContainer>().Modulate = index == economy.Player.SelectedQuickbarIndex ? new Color("d8e6bd") : Colors.White;
            }
            _bagSummary.Text = $"随身行囊   {slots.Count(s => s.ResourceId is not null)} / {slots.Length} 格     ·     零钱 {economy.Player.Coins:N0}";
            _shopColumn.Visible = _activeShopId is not null;
            _bagGrid.Columns = _activeShopId is null ? 8 : 5;
            _economyTitle.Text = _activeShopId is null ? "随身行囊" : _loaded.Definitions.Economy!.Shops.Single(s => s.Id == _activeShopId).DisplayName;
            _economySubtitle.Text = _scenario is "farming-capacity-fixture" or "production-capacity-fixture" ? "满包验证初态 · 五格背包与预置物品。" : _scenario == "economy-fixture" ? "交易验证初态 · 背包含测试产物，不影响普通新档。" : _activeShopId is null ? "物品按目录排列，前五格同步到快捷栏。" : "买一点种子，带回下一季的期待。";
            if (_activeShopId is null) return;
            var shop = economy.Shops.Single(s => s.Id == _activeShopId);
            var definition = _loaded.Definitions.Economy!.Shops.Single(s => s.Id == _activeShopId);
            _shopSummary.Text = $"{(_tradeDirection == "buy" ? "店内货架" : "可收购品目")}     ·     店铺零钱 {shop.Coins:N0}";
            _buyTab.Modulate = _tradeDirection == "buy" ? new Color("d8e6bd") : Colors.White;
            _sellTab.Modulate = _tradeDirection == "sell" ? new Color("d8e6bd") : Colors.White;
            foreach (var (id, button) in _stockButtons)
            {
                var price = definition.Prices.Single(p => p.ResourceId == id);
                int count = (_tradeDirection == "buy" ? shop.Inventory : economy.Player.Inventory).GetValueOrDefault(id);
                button.Text = $"{ResourceName(id)}    {(_tradeDirection == "buy" ? price.BuyPrice : price.SellPrice)} 币    ·    {(_tradeDirection == "buy" ? "库存" : "持有")} {count}";
                button.Modulate = _tradeResourceId == id ? new Color("d8e6bd") : Colors.White;
            }
            _tradeButton.Disabled = !EconomyInputAllowed || _tradeResourceId is null;
            _tradeButton.Text = _tradeDirection == "buy" ? "确认购买" : "确认出售";
            if (_tradeResourceId is not null)
            {
                var quote = _world.QuoteTrade(_activeShopId, _tradeResourceId, _tradeDirection, (int)_tradeQuantity.Value);
                _tradeDetail.Text = quote.CanTrade
                    ? $"{ResourceName(_tradeResourceId)} ×{(int)_tradeQuantity.Value}   ·   {(_tradeDirection == "buy" ? "支付" : "获得")} {quote.TotalPrice:N0} 币"
                    : EconomyReason(quote.Code);
                _tradeDetail.Modulate = quote.CanTrade ? Colors.White : new Color("ad593d");
            }
        }
        finally { _refreshingEconomy = false; }
    }

    private static string EconomyReason(string code) => code switch
    {
        "insufficient_player_coins" => "零钱不够，减少数量再试试。",
        "insufficient_shop_coins" => "店铺零钱不足，暂时无法收购。",
        "insufficient_shop_stock" => "店内库存不足，请减少数量。",
        "insufficient_player_stock" => "背包里没有这么多物品。",
        "inventory_full" => "背包空间不足，先腾出一些位置。",
        "shop_out_of_reach" => "请走近杂货铺门口后再交易。",
        "direction_disabled" => "店铺不提供这项买卖。",
        "invalid_quantity" => "数量须为大于零的整数。",
        "price_changed" => "报价已变化，请重新选择物品。",
        "numeric_overflow" => "数量或金额超过可交易范围。",
        "operation_id_conflict" => "操作记录不一致，这次买卖没有执行。",
        _ => "这次买卖没有完成，请重新选择物品或数量。"
    };
}
