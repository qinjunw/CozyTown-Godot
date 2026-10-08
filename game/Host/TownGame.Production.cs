using CozyTown.Core.Town;
using Godot;

namespace CozyTown.Game;

public partial class TownGame
{
    private Control _productionPanel = null!;
    private PanelContainer _productionCard = null!;
    private Label _productionTitle = null!;
    private Label _productionStatus = null!;
    private Label _productionFeedback = null!;
    private Label _recipeDetail = null!;
    private HBoxContainer _productionActions = null!;
    private HBoxContainer _cookingColumns = null!;
    private Button _feedButton = null!;
    private Button _collectButton = null!;
    private Button _fishButton = null!;
    private Button _cookButton = null!;
    private readonly Dictionary<string, Button> _recipeButtons = new(StringComparer.Ordinal);
    private string? _activeProductionId;
    private string? _activeRecipeId;

    private void CreateProductionInterface()
    {
        _productionPanel = new Control { Name = "ProductionPanel", Visible = false };
        _ui.AddChild(_productionPanel);
        _productionPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var shade = new ColorRect { Color = new Color(.06f, .14f, .1f, .46f) };
        _productionPanel.AddChild(shade);
        shade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _productionCard = Panel();
        _productionPanel.AddChild(_productionCard);
        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 14);
        _productionCard.AddChild(Margin(stack, 25, 20));
        var header = new HBoxContainer();
        stack.AddChild(header);
        _productionTitle = Label("小镇生产", 26, _ink);
        _productionTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(_productionTitle);
        header.AddChild(Button("×", CloseProduction));
        _productionStatus = Label("", 17, _ink);
        _productionStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _productionStatus.CustomMinimumSize = new Vector2(0, 94);
        stack.AddChild(_productionStatus);
        _productionActions = new HBoxContainer();
        _productionActions.AddThemeConstantOverride("separation", 12);
        stack.AddChild(_productionActions);
        _feedButton = Button("喂一份饲料", () => SubmitProduction("feed"));
        _collectButton = Button("领取鸡蛋", () => SubmitProduction("collect"));
        _fishButton = Button("抛竿钓鱼", () => SubmitProduction("fish"));
        foreach (var button in new[] { _feedButton, _collectButton, _fishButton })
        {
            button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            button.CustomMinimumSize = new Vector2(0, 46);
            _productionActions.AddChild(button);
        }
        _cookingColumns = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _cookingColumns.AddThemeConstantOverride("separation", 22);
        stack.AddChild(_cookingColumns);
        var recipes = new VBoxContainer { CustomMinimumSize = new Vector2(284, 0) };
        recipes.AddThemeConstantOverride("separation", 9);
        _cookingColumns.AddChild(recipes);
        recipes.AddChild(Label("厨房配方", 16, _muted));
        foreach (var recipe in _loaded.Definitions.Production!.Recipes)
        {
            string id = recipe.Id;
            var button = Button(recipe.DisplayName, () =>
            { _activeRecipeId = id; _productionFeedback.Text = ""; RefreshProductionInterface(); });
            button.Icon = _itemIcons.GetValueOrDefault(recipe.OutputResourceId);
            button.ExpandIcon = true;
            button.AddThemeConstantOverride("icon_max_width", 28);
            button.Alignment = HorizontalAlignment.Left;
            button.CustomMinimumSize = new Vector2(0, 51);
            recipes.AddChild(button);
            _recipeButtons.Add(id, button);
        }
        var detail = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        detail.AddThemeConstantOverride("separation", 14);
        _cookingColumns.AddChild(detail);
        detail.AddChild(Label("所需材料与成品", 16, _muted));
        _recipeDetail = Label("", 18, _ink);
        _recipeDetail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _recipeDetail.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        detail.AddChild(_recipeDetail);
        _cookButton = Button("制作一份", () => SubmitProduction("cook", _activeRecipeId));
        _cookButton.CustomMinimumSize = new Vector2(0, 46);
        detail.AddChild(_cookButton);
        stack.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        _productionFeedback = Label("", 15, _accent);
        _productionFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _productionFeedback.CustomMinimumSize = new Vector2(0, 46);
        stack.AddChild(_productionFeedback);
        stack.AddChild(Button("回到小镇  Esc", CloseProduction));
    }

    private IEnumerable<(string Id, string Name, string PlaceId, string Kind)> ProductionTargets()
    {
        var definition = _loaded.Definitions.Production!;
        foreach (var chicken in definition.Chickens) yield return (chicken.Id, chicken.DisplayName, chicken.PlaceId, "chicken");
        foreach (var spot in definition.FishingSpots) yield return (spot.Id, spot.DisplayName, spot.PlaceId, "fish");
        foreach (var kitchen in definition.Kitchens) yield return (kitchen.Id, kitchen.DisplayName, kitchen.PlaceId, "kitchen");
    }

    private string? NearbyProductionId() => ProductionTargets().Where(t => _world.CanInteractWithProductionTarget(t.Id))
        .OrderBy(t => _loaded.Content.Place(t.PlaceId).Position.DistanceTo(_view.PlayerPosition))
        .Select(t => t.Id).FirstOrDefault();

    private void OpenProduction(string targetId)
    {
        if (_fatal || _pauseReasons.Count > 0) return;
        _activeProductionId = targetId;
        _activeRecipeId ??= _loaded.Definitions.Production!.Recipes.FirstOrDefault()?.Id;
        _playerRoute = null;
        _selectedResident = null;
        _inspector.Visible = false;
        _toast.Visible = false;
        _productionFeedback.Text = "";
        _productionPanel.Visible = true;
        SetPause("production", true);
        RefreshProductionInterface();
    }

    private void CloseProduction()
    {
        _productionPanel.Visible = false;
        SetPause("production", false);
    }

    private bool ProductionInputAllowed => !_fatal && _pauseReasons.All(r => r == "production");

    private void RefreshProductionInterface()
    {
        if (_productionPanel is null || !_productionPanel.Visible || _activeProductionId is null) return;
        var target = ProductionTargets().Single(t => t.Id == _activeProductionId);
        bool cooking = target.Kind == "kitchen";
        Place(_productionCard, cooking ? -430 : -310, cooking ? -305 : -235,
            cooking ? 430 : 310, cooking ? 305 : 235, .5f, .5f, .5f, .5f);
        _productionTitle.Text = target.Name;
        _cookingColumns.Visible = cooking;
        _productionActions.Visible = !cooking;
        _feedButton.Visible = _collectButton.Visible = target.Kind == "chicken";
        _fishButton.Visible = target.Kind == "fish";
        foreach (var button in new[] { _feedButton, _collectButton, _fishButton, _cookButton }) button.Disabled = !ProductionInputAllowed;
        if (target.Kind == "chicken")
        {
            var definition = _loaded.Definitions.Production!.Chickens.Single(c => c.Id == target.Id);
            var chicken = _view.Production.Chickens.Single(c => c.Id == target.Id);
            string state = chicken.ProductReady ? $"鸡蛋已备好 · 可领取 {ResourceName(definition.ProductResourceId)} ×{definition.ProductQuantity}"
                : chicken.Fed ? "已喂食 · 等待下一个 05:00 产蛋" : "还未喂食 · 喂一份饲料，等到清晨产蛋";
            _productionStatus.Text = $"{state}\n背包饲料：{_view.Economy.Player.Inventory.GetValueOrDefault(definition.FeedResourceId)}\n有待领鸡蛋时，鸡舍保留这一份产出。";
        }
        else if (target.Kind == "fish")
        {
            string fish = string.Join(" · ", _loaded.Definitions.Production!.Fish.Select(f =>
                $"{ResourceName(f.ResourceId)} {f.MaxRollExclusive - f.MinRollInclusive}%"));
            _productionStatus.Text = $"选好位置，抛竿试试今天的运气。\n{fish} · 空钩 10%\n钓到的鱼放进背包，可出售或带去厨房。";
        }
        else
        {
            _productionStatus.Text = $"随身行囊 · {_view.Economy.Player.Coins} 金币\n选择一道菜，核对材料后制作。成品可带到杂货铺出售。";
            foreach (var pair in _recipeButtons)
            {
                pair.Value.Disabled = !ProductionInputAllowed;
                pair.Value.Modulate = pair.Key == _activeRecipeId ? new Color("d8e6bd") : Colors.White;
            }
            var recipe = _loaded.Definitions.Production!.Recipes.Single(r => r.Id == _activeRecipeId);
            _recipeDetail.Text = recipe.DisplayName + "\n\n" + string.Join("\n", recipe.Ingredients.Select(i =>
                $"{ResourceName(i.ResourceId)}  ·  需要 {i.Quantity} / 持有 {_view.Economy.Player.Inventory.GetValueOrDefault(i.ResourceId)}"))
                + $"\n\n成品：{ResourceName(recipe.OutputResourceId)} ×{recipe.OutputQuantity}\n持有：{_view.Economy.Player.Inventory.GetValueOrDefault(recipe.OutputResourceId)}";
        }
    }

    private void SubmitProduction(string action, string? definitionId = null)
    {
        if (!ProductionInputAllowed || !_productionPanel.Visible || _activeProductionId is null) return;
        try
        {
            FlushWorldEvents();
            var result = _world.Produce(new ProductionRequest(NewEconomyOperationId(), "player", _activeProductionId, action, definitionId),
                record => PersistFarmingOperation("player_production_command", record));
            FlushWorldEvents();
            _view = _world.GetView();
            SyncActors();
            _productionFeedback.Text = result.Code == "no_catch" ? "这次是空钩。换个心情，再抛一竿吧。"
                : result.Succeeded ? action == "feed" ? "喂好了，下一次清晨会准备一份鸡蛋。"
                : $"{(action == "cook" ? "已做好" : "已放入背包")}：{ResourceName(result.ResourceId!)} ×{result.Quantity}。"
                : ProductionReason(result.Code);
            _productionFeedback.Modulate = result.Succeeded ? Colors.White : new Color("ad593d");
            RefreshInterface();
        }
        catch (Exception error) { Fail("production_record_failed", error); }
    }

    private static string ProductionReason(string code) => code switch
    {
        "insufficient_feed" => "饲料不足，先去杂货铺购买鸡饲料。",
        "already_fed" => "这一轮已经喂过，等下一个清晨就好。",
        "product_pending" => "鸡蛋还在等你领取，这次没有消耗饲料。",
        "no_product" or "product_not_ready" => "还没有待领鸡蛋。先喂食，再等清晨产蛋。",
        "insufficient_ingredients" => "材料不足，请核对配方中的需要数量与持有数量。",
        "inventory_full" => "背包装不下。先出售一些物品，再回来领取或制作。",
        "production_out_of_reach" => "请走近鸡舍、池边或厨房再操作。",
        "numeric_overflow" => "物品或生产数量已达上限，本次操作未改变资产。",
        "production_failed" => "生产未完成，材料和产物保持原样。请稍后再试。",
        _ => "操作未完成：" + code
    };
}
