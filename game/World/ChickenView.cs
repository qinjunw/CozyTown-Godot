using CozyTown.Core.Town;
using Godot;

namespace CozyTown.Game;

[Tool]
public partial class ChickenView : Node2D
{
    private Texture2D? _states;
    private int _frame;

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        _states = GD.Load<Texture2D>("res://Art/Props/prop_hen_states_16.png");
        QueueRedraw();
    }

    public void SetState(ChickenState state)
    {
        int frame = state.ProductReady ? 2 : state.Fed ? 1 : 0;
        if (_frame == frame) return;
        _frame = frame;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_states == null) return;
        DrawTextureRectRegion(_states, new Rect2(-8, -16, 16, 16), new Rect2(_frame * 16, 0, 16, 16));
    }
}
