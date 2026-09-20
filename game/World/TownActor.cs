using Godot;

namespace CozyTown.Game;

public partial class TownActor : Node2D
{
    private Texture2D? _texture;
    private Font? _font;
    private string _displayName = "";
    private bool _walking;
    private bool _highlighted;
    private int _direction;
    private int _frame;
    private double _walkSeconds;
    private double _speechSeconds;
    private readonly List<string> _speechLines = [];

    public string ActorId { get; private set; } = "";

    public bool Highlighted
    {
        get => _highlighted;
        set { if (_highlighted != value) { _highlighted = value; QueueRedraw(); } }
    }

    public void Configure(string id, string displayName, string texturePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(texturePath);
        ActorId = id;
        _displayName = displayName;
        _texture = GD.Load<Texture2D>(texturePath)
            ?? throw new InvalidOperationException($"Actor texture could not be loaded: {texturePath}");
        if (_texture.GetWidth() != 72 || _texture.GetHeight() != 128)
            throw new InvalidOperationException("Actor textures must contain 3 by 4 cells of 24 by 32 pixels.");
        TextureFilter = TextureFilterEnum.Nearest;
        _font = new SystemFont { FontNames = ["Microsoft YaHei", "Arial"] };
        QueueRedraw();
    }

    public void SetPose(Vector2 position, Vector2 direction, bool walking)
    {
        Position = position.Round();
        ZIndex = Mathf.Clamp(Mathf.RoundToInt(position.Y), 1, 1023);
        if (direction.LengthSquared() > 0.001f)
            _direction = Mathf.Abs(direction.X) > Mathf.Abs(direction.Y)
                ? direction.X < 0 ? 1 : 2
                : direction.Y < 0 ? 3 : 0;
        if (_walking != walking) { _walkSeconds = 0; _frame = walking ? 1 : 0; }
        _walking = walking;
        SetProcess(walking || _speechSeconds > 0);
        QueueRedraw();
    }

    public void Say(string text)
    {
        _speechLines.Clear();
        if (_font == null || string.IsNullOrWhiteSpace(text)) return;
        string line = "";
        foreach (var rune in text.Trim().EnumerateRunes().Take(160))
        {
            string next = System.Text.Rune.IsControl(rune) ? " " : rune.ToString();
            if (_font.GetStringSize(line + next, fontSize: 8).X > 140 && line.Length > 0)
            {
                _speechLines.Add(line);
                line = "";
                if (_speechLines.Count == 2) { line = "…"; break; }
            }
            line += next;
        }
        if (line.Length > 0) _speechLines.Add(line);
        _speechSeconds = 4;
        SetProcess(true);
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_walking)
        {
            _walkSeconds += delta;
            int frame = 1 + (int)(_walkSeconds * 6) % 2;
            if (frame != _frame) { _frame = frame; QueueRedraw(); }
        }
        if (_speechSeconds > 0)
        {
            _speechSeconds -= delta;
            if (_speechSeconds <= 0) { _speechLines.Clear(); QueueRedraw(); }
        }
        if (!_walking && _speechSeconds <= 0) SetProcess(false);
    }

    public override void _Draw()
    {
        if (_texture == null) return;
        DrawRect(new Rect2(-7, -2, 14, 3), new Color(0.16f, 0.22f, 0.15f, 0.28f));
        DrawRect(new Rect2(-5, -3, 10, 5), new Color(0.16f, 0.22f, 0.15f, 0.12f));
        if (_highlighted)
        {
            var color = new Color("fff1b8");
            DrawLine(new Vector2(-11, 0), new Vector2(-7, 3), color, 1);
            DrawLine(new Vector2(-7, 3), new Vector2(7, 3), color, 1);
            DrawLine(new Vector2(7, 3), new Vector2(11, 0), color, 1);
            DrawRect(new Rect2(-2, -39, 4, 3), color);
            DrawRect(new Rect2(-1, -36, 2, 1), color);
        }
        DrawTextureRectRegion(_texture, new Rect2(-12, -32, 24, 32),
            new Rect2(_frame * 24, _direction * 32, 24, 32));
        if (_font != null && _displayName.Length != 0)
        {
            const int fontSize = 8;
            float width = _font.GetStringSize(_displayName, fontSize: fontSize).X;
            var label = new Vector2(Mathf.Round(-width / 2), _highlighted ? -42 : -35);
            DrawStringOutline(_font, label, _displayName, fontSize: fontSize, size: 1,
                modulate: _highlighted ? new Color("fff1b8") : new Color("fff6d9"));
            DrawString(_font, label, _displayName, fontSize: fontSize,
                modulate: new Color("344136"));
        }
        if (_font != null && _speechLines.Count > 0)
        {
            float width = Math.Max(28, _speechLines.Max(s => _font.GetStringSize(s, fontSize: 8).X) + 12);
            float height = _speechLines.Count * 11 + 9;
            var box = new Rect2(Mathf.Round(-width / 2), -49 - height, width, height);
            DrawRect(box.Grow(1), new Color("6b6248"));
            DrawRect(box, new Color("fff3d4"));
            DrawRect(new Rect2(-2, -49, 5, 2), new Color("6b6248"));
            DrawRect(new Rect2(-1, -49, 3, 3), new Color("fff3d4"));
            for (int i = 0; i < _speechLines.Count; i++)
                DrawString(_font, box.Position + new Vector2(6, 12 + i * 11), _speechLines[i],
                    fontSize: 8, modulate: new Color("4c4639"));
        }
    }
}
