using Godot;

namespace CozyTown.Game;

public partial class TownSpeechOverlay : Node2D
{
    private TownActor[] _actors = [];
    private Control _interface = null!;
    private readonly SystemFont _font = new() { FontNames = ["Microsoft YaHei UI", "Microsoft YaHei", "Arial"] };
    private readonly Dictionary<string, Rect2> _bubbleBounds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector2> _offsets = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, Rect2> BubbleBounds => _bubbleBounds;

    public static TownSpeechOverlay Install(Node owner, IEnumerable<TownActor> actors, Control interfaceRoot)
    {
        var layer = new CanvasLayer { Name = "SpeechLayer", Layer = 8 };
        owner.AddChild(layer);
        var overlay = new TownSpeechOverlay { _actors = actors.ToArray(), _interface = interfaceRoot };
        layer.AddChild(overlay);
        return overlay;
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        _bubbleBounds.Clear();
        Rect2 viewport = GetViewportRect().Grow(-16);
        var visible = _actors.Where(actor => IsInstanceValid(actor) && actor.IsVisibleInTree()
            && viewport.HasPoint(actor.GetGlobalTransformWithCanvas() * Vector2.Zero)).ToArray();
        var blocked = visible.Select(actor => actor.GetGlobalTransformWithCanvas()
            * new Rect2(-17, -52, 34, 58)).ToList();
        foreach (Control control in _interface.GetChildren().OfType<Control>())
        {
            if (control.IsVisibleInTree() && control.Size.X > 0 && control.Size.Y > 0)
                blocked.Add(control.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, control.Size));
        }
        var speakers = visible.Where(actor => actor.SpeechText.Length > 0)
            .OrderBy(actor => actor.ActorId, StringComparer.Ordinal).ToArray();
        var bubbles = Layout(speakers, viewport, blocked, compact: false);
        if (bubbles.Count != speakers.Length)
            bubbles = Layout(speakers, viewport, blocked, compact: true);
        foreach (var bubble in bubbles)
        {
            _bubbleBounds[bubble.Actor.ActorId] = bubble.Bounds;
            _offsets[bubble.Actor.ActorId] = bubble.Bounds.Position - bubble.Anchor;
            Vector2 nearest = bubble.Anchor.Clamp(bubble.Bounds.Position, bubble.Bounds.End);
            DrawLine(nearest, bubble.Anchor, new Color("f8eed3"), 3);
            DrawLine(nearest, bubble.Anchor, new Color("718466"), 1);
            DrawCircle(bubble.Anchor, 3, new Color("718466"));
        }
        foreach (var bubble in bubbles)
        {
            DrawRect(bubble.Bounds.Grow(1), new Color("718466"));
            DrawRect(bubble.Bounds, new Color("fff8e8"));
            DrawString(_font, bubble.Bounds.Position + new Vector2(12, 20), bubble.Actor.DisplayName,
                fontSize: 14, modulate: new Color("4d684d"));
            DrawLine(bubble.Bounds.Position + new Vector2(12, 28),
                bubble.Bounds.Position + new Vector2(bubble.Bounds.Size.X - 12, 28), new Color("dedec4"));
            for (int i = 0; i < bubble.Lines.Length; i++)
                DrawString(_font, bubble.Bounds.Position + new Vector2(12, 48 + i * 21), bubble.Lines[i],
                    fontSize: 16, modulate: new Color("344136"));
        }
    }

    private List<Bubble> Layout(TownActor[] speakers, Rect2 viewport, List<Rect2> blocked, bool compact)
    {
        var result = new List<Bubble>();
        var occupied = new List<Rect2>(blocked);
        foreach (TownActor actor in speakers)
        {
            Vector2 anchor = actor.GetGlobalTransformWithCanvas() * new Vector2(0, -52);
            string[] lines = Wrap(actor.SpeechText, compact ? 180 : 256, compact ? 1 : 3);
            float width = Math.Max(120, Math.Max(_font.GetStringSize(actor.DisplayName, fontSize: 14).X,
                lines.Max(line => _font.GetStringSize(line, fontSize: 16).X)) + 24);
            var size = new Vector2(Mathf.Ceil(width), 38 + lines.Length * 21);
            Rect2? bounds = FindSpace(actor.ActorId, anchor, size, viewport, occupied);
            if (bounds == null) continue;
            result.Add(new Bubble(actor, anchor, bounds.Value, lines));
            occupied.Add(bounds.Value);
        }
        return result;
    }

    private Rect2? FindSpace(string actorId, Vector2 anchor, Vector2 size, Rect2 viewport, List<Rect2> blocked)
    {
        bool Fits(Rect2 rect) => viewport.Encloses(rect) && !blocked.Any(other => rect.Grow(8).Intersects(other));
        if (_offsets.TryGetValue(actorId, out Vector2 offset))
        {
            var previous = new Rect2((anchor + offset).Round(), size);
            if (Fits(previous)) return previous;
        }
        var xs = new List<float> { anchor.X - size.X / 2, viewport.Position.X, viewport.End.X - size.X };
        var ys = new List<float> { anchor.Y - size.Y - 12, anchor.Y + 120, viewport.Position.Y, viewport.End.Y - size.Y };
        foreach (Rect2 obstacle in blocked)
        {
            xs.Add(obstacle.Position.X - size.X - 9);
            xs.Add(obstacle.End.X + 9);
            ys.Add(obstacle.Position.Y - size.Y - 9);
            ys.Add(obstacle.End.Y + 9);
        }
        Rect2? best = null;
        float distance = float.MaxValue;
        foreach (float x in xs)
        foreach (float y in ys)
        {
            var candidate = new Rect2(new Vector2(x, y).Round(), size);
            float candidateDistance = candidate.GetCenter().DistanceSquaredTo(anchor);
            if (candidateDistance >= distance || !Fits(candidate)) continue;
            best = candidate;
            distance = candidateDistance;
        }
        return best;
    }

    private string[] Wrap(string text, float width, int maxLines)
    {
        var lines = new List<string>();
        string line = "";
        foreach (var rune in text.EnumerateRunes())
        {
            string next = rune.ToString();
            if (_font.GetStringSize(line + next, fontSize: 16).X > width && line.Length > 0)
            {
                if (lines.Count == maxLines - 1)
                {
                    while (_font.GetStringSize(line + "…", fontSize: 16).X > width && line.Length > 0)
                        line = string.Concat(line.EnumerateRunes().SkipLast(1).Select(value => value.ToString()));
                    lines.Add(line + "…");
                    return lines.ToArray();
                }
                lines.Add(line);
                line = "";
            }
            line += next;
        }
        if (line.Length > 0) lines.Add(line);
        return lines.ToArray();
    }

    private sealed record Bubble(TownActor Actor, Vector2 Anchor, Rect2 Bounds, string[] Lines);
}
