using CozyTown.Core.Town;
using Godot;

namespace CozyTown.Game;

[Tool]
public partial class FarmPlotView : Node2D
{
    private Texture2D? _items;
    private CropDefinition? _crop;
    private int _growthDays;
    private bool _watered;
    private bool _mature;

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        _items = GD.Load<Texture2D>("res://Art/Items/item_mvp_16.png");
        QueueRedraw();
    }

    public void SetState(CropDefinition? crop, FarmPlotState state)
    {
        if (_crop == crop && _growthDays == state.GrowthDays && _watered == state.Watered && _mature == state.Mature) return;
        _crop = crop;
        _growthDays = state.GrowthDays;
        _watered = state.Watered;
        _mature = state.Mature;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_crop == null) return;
        if (_watered)
        {
            DrawRect(new Rect2(-9, -7, 18, 15), new Color("664932"));
            foreach (Vector2 point in new[] { new Vector2(-6, -3), new Vector2(5, -2), new Vector2(-4, 5) })
                DrawRect(new Rect2(point, new Vector2(2, 1)), new Color("6599ac"));
        }
        if (_mature && _items != null)
        {
            int index = _crop.HarvestResourceId switch { "crop.potato" => 3, "crop.carrot" => 4, "crop.tomato" => 5, _ => -1 };
            if (index >= 0)
            {
                DrawTextureRectRegion(_items, new Rect2(-8, -12, 16, 16), new Rect2(index * 16, 0, 16, 16));
                DrawRect(new Rect2(7, -13, 3, 3), new Color("ffe7a0"));
                DrawRect(new Rect2(8, -14, 1, 5), new Color("fff8d4"));
                return;
            }
        }
        bool leafy = _growthDays > 0;
        DrawRect(new Rect2(-1, leafy ? -8 : -4, 2, leafy ? 12 : 8), new Color("3b6636"));
        DrawRect(new Rect2(-6, leafy ? -8 : -5, 5, 3), new Color("6f9a43"));
        DrawRect(new Rect2(1, leafy ? -10 : -6, 5, 3), new Color("8ab74b"));
        if (leafy)
        {
            DrawRect(new Rect2(-8, -3, 7, 3), new Color("527d37"));
            DrawRect(new Rect2(1, -5, 7, 3), new Color("79a944"));
            DrawRect(new Rect2(-4, -10, 3, 2), new Color("a7c568"));
        }
    }
}
