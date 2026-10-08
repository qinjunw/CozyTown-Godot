using CozyTown.Core.Town;
using Godot;

namespace CozyTown.Game;

[Tool]
public partial class TownBackdrop : Node2D
{
    private Texture2D? _ground;
    private Texture2D? _decor;

    public void UpdateFarming(FarmingDefinition definition, FarmingState state)
    {
        var farm = GetNodeOrNull<Node2D>("Landmarks/Farm");
        if (farm == null) return;
        foreach (FarmPlotView view in farm.GetChildren().OfType<FarmPlotView>())
        {
            string placeId = view.GetMeta("id").AsString();
            FarmPlotDefinition? plot = definition.Plots.SingleOrDefault(p => p.PlaceId == placeId);
            if (plot == null) continue;
            FarmPlotState saved = state.Plots.Single(p => p.Id == plot.Id);
            view.SetState(definition.Crops.SingleOrDefault(c => c.Id == saved.CropId), saved);
        }
    }

    public void UpdateProduction(ProductionDefinition definition, ProductionState state)
    {
        var buildings = GetNodeOrNull<Node2D>("Buildings");
        if (buildings == null) return;
        foreach (ChickenView view in buildings.GetChildren().SelectMany(b => b.GetChildren()).OfType<ChickenView>())
        {
            string chickenId = view.GetMeta("chicken_id").AsString();
            ChickenDefinition? chicken = definition.Chickens.SingleOrDefault(c => c.Id == chickenId);
            if (chicken == null) continue;
            view.SetState(state.Chickens.Single(c => c.Id == chicken.Id));
        }
    }

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        _ground = GD.Load<Texture2D>("res://Art/Environment/Tiles/tile_town_base_16.png");
        _decor = GD.Load<Texture2D>("res://Art/Props/prop_town_decor_16x32.png");
        UpdateSceneryDepths();
        SetProcess(Engine.IsEditorHint());
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        UpdateSceneryDepths();
        QueueRedraw();
    }

    private void UpdateSceneryDepths()
    {
        foreach (string name in new[] { "Buildings", "Scenery" })
        {
            var parent = GetNodeOrNull<Node2D>(name);
            if (parent == null) continue;
            foreach (Node child in parent.GetChildren())
                if (child is Node2D item)
                    item.ZIndex = Mathf.Clamp(Mathf.RoundToInt(ToLocal(item.GlobalPosition).Y), 1, 1023);
        }
    }

    public override void _Draw()
    {
        if (_ground == null || _decor == null) return;
        int width = GetMeta("width", 768).AsInt32();
        int height = GetMeta("height", 512).AsInt32();
        var roads = FindRoads();
        var buildings = GetNodeOrNull<Node2D>("Buildings");
        var grassTint = new Color(0.89f, 0.94f, 0.85f);
        for (int y = 0; y < height; y += 16)
            for (int x = 0; x < width; x += 16)
            {
                int variant = ((x / 16 * 17) ^ (y / 16 * 31)) & 3;
                DrawTextureRectRegion(_ground, new Rect2(x, y, 16, 16),
                    new Rect2(variant * 16, 0, 16, 16), grassTint);
            }

        DrawRiver();
        DrawRect(new Rect2(20, 24, 676, 11), new Color(0.17f, 0.30f, 0.15f, 0.10f));
        if (buildings != null)
            foreach (Node child in buildings.GetChildren())
                if (child is Node2D building)
                {
                    Vector2 foot = ToLocal(building.GlobalPosition);
                    DrawRect(new Rect2(foot + new Vector2(-39, -7), new Vector2(82, 15)), new Color("72954c"));
                    DrawRect(new Rect2(foot + new Vector2(-32, 0), new Vector2(65, 5)), new Color(0.17f, 0.24f, 0.14f, 0.24f));
                    for (int i = 0; i < 5; i++)
                        DrawRect(new Rect2(foot + new Vector2(-25 + i * 12, 5), new Vector2(10, 5)), new Color("b9af76"));
                }

        foreach (var road in roads)
        {
            DrawPolyline(road, new Color("92a15b"), 20);
            DrawPolyline(road, new Color("b99865"), 16);
            DrawPolyline(road, new Color("d3b780"), 12);
            AddRoadGrain(road);
        }
        DrawPlaza();
        DrawBridge();
        DrawGardenDetails(roads);
        DrawRestSign();
    }

    private List<Vector2[]> FindRoads()
    {
        var result = new List<Vector2[]>();
        var parent = GetNodeOrNull<Node2D>("Roads");
        if (parent == null) return result;
        foreach (Node child in parent.GetChildren())
            if (child is Line2D line && line.Points.Length > 1)
                result.Add(line.Points.Select(p => ToLocal(line.ToGlobal(p))).ToArray());
        return result;
    }

    private void AddRoadGrain(Vector2[] points)
    {
        for (int i = 1; i < points.Length; i++)
        {
            Vector2 delta = points[i] - points[i - 1];
            float length = delta.Length();
            Vector2 tangent = delta.Normalized();
            Vector2 normal = new(-tangent.Y, tangent.X);
            for (int step = 7; step < length - 5; step += 13)
            {
                Vector2 point = (points[i - 1] + tangent * step + normal * ((step % 3) - 1) * 2).Round();
                DrawRect(new Rect2(point, new Vector2(2, 1)), new Color("bf9e68"));
            }
        }
    }

    private void DrawPlaza()
    {
        var plaza = GetNodeOrNull<Node2D>("Places/Plaza");
        if (plaza == null) return;
        Vector2 center = ToLocal(plaza.GlobalPosition);
        DrawRect(new Rect2(center + new Vector2(-70, -49), new Vector2(140, 98)), new Color("9a9e71"));
        DrawRect(new Rect2(center + new Vector2(-66, -45), new Vector2(132, 90)), new Color("c9c29c"));
        for (int y = -43; y < 43; y += 9)
            for (int x = -64; x < 64; x += 11)
            {
                int variation = ((x + 64) / 11 + (y + 43) / 9) % 4;
                Color color = variation == 0 ? new Color("d7cfaa") : new Color("c5bd95");
                DrawRect(new Rect2(center + new Vector2(x, y), new Vector2(10, 8)), color);
            }
        DrawRect(new Rect2(center + new Vector2(-8, -8), new Vector2(16, 16)), new Color("b2ad82"));
        DrawRect(new Rect2(center + new Vector2(-5, -5), new Vector2(10, 10)), new Color("ded3a9"));
        foreach (string name in new[] { "PlazaBenchWest", "PlazaBenchEast" })
        {
            var bench = GetNodeOrNull<Node2D>("Boundaries/" + name);
            if (bench != null) DrawBench(ToLocal(bench.GlobalPosition) - new Vector2(0, 3));
        }
    }

    private void DrawRiver()
    {
        Rect2 river = ObstacleRect("River");
        Rect2 canalWest = ObstacleRect("CanalWest");
        Rect2 canalEast = ObstacleRect("CanalEast");
        DrawRect(river, new Color("4f8d99"));
        DrawRect(new Rect2(river.Position + new Vector2(14, 0), river.Size - new Vector2(14, 0)), new Color("478594"));
        DrawRect(new Rect2(river.Position, new Vector2(3, river.Size.Y)), new Color("b8b27b"));
        var canal = new Rect2(canalWest.Position, new Vector2(canalEast.End.X - canalWest.Position.X, canalWest.Size.Y));
        DrawRect(canal, new Color("4f8d99"));
        DrawRect(new Rect2(canal.Position - new Vector2(0, 2), new Vector2(canal.Size.X, 3)), new Color("b8b27b"));
        DrawRect(new Rect2(canal.Position + new Vector2(0, canal.Size.Y - 1), new Vector2(canal.Size.X, 3)), new Color("b8b27b"));
        for (int y = 12; y < river.Size.Y - 4; y += 23)
        {
            float x = river.Position.X + 18 + (y * 7 % 14);
            DrawRect(new Rect2(x, river.Position.Y + y, 9, 1), new Color("80b2b3"));
            DrawRect(new Rect2(x - 3, river.Position.Y + y + 3, 5, 1), new Color("649eaa"));
        }
    }

    private void DrawBridge()
    {
        Rect2 west = ObstacleRect("CanalWest");
        Rect2 east = ObstacleRect("CanalEast");
        Vector2 top = new(west.End.X - 2, west.Position.Y - 5);
        float width = east.Position.X - west.End.X + 4;
        float height = west.Size.Y + 12;
        DrawRect(new Rect2(top, new Vector2(width, height)), new Color("5e4b35"));
        for (int i = 0; i < 7; i++)
        {
            DrawRect(new Rect2(top + new Vector2(2, 1 + i * 4), new Vector2(width - 4, 3)), new Color(i % 2 == 0 ? "bc925f" : "cba06d"));
            DrawRect(new Rect2(top + new Vector2(5, 2 + i * 4), new Vector2(12, 1)), new Color("dbb680"));
        }
        DrawRect(new Rect2(top + new Vector2(-1, -2), new Vector2(3, height + 3)), new Color("795939"));
        DrawRect(new Rect2(top + new Vector2(width - 2, -2), new Vector2(3, height + 3)), new Color("795939"));
        foreach (Vector2 point in new[] { top + new Vector2(-2, -4), top + new Vector2(width - 3, -4),
                     top + new Vector2(-2, height - 3), top + new Vector2(width - 3, height - 3) })
            DrawRect(new Rect2(point, new Vector2(5, 5)), new Color("dfbe83"));
    }

    private Rect2 ObstacleRect(string name)
    {
        var shape = GetNode<CollisionShape2D>("Boundaries/" + name + "/Shape");
        var rectangle = (RectangleShape2D)shape.Shape;
        return new Rect2(ToLocal(shape.GlobalPosition) - rectangle.Size / 2, rectangle.Size);
    }

    private void DrawGardenDetails(List<Vector2[]> roads)
    {
        if (_decor == null) return;
        for (int i = 0; i < 138; i++)
        {
            var point = new Vector2(35 + i * 71 % 650, 42 + i * 113 % 425);
            if (point.X > 610 && point.Y > 300 || new Rect2(278, 214, 154, 127).HasPoint(point)) continue;
            var farm = GetNodeOrNull<Node2D>("Landmarks/Farm");
            if (farm != null && new Rect2(ToLocal(farm.GlobalPosition) - new Vector2(50, 66), new Vector2(100, 68)).HasPoint(point)) continue;
            if (roads.Any(road => NearRoad(point, road))) continue;
            int column = i % 5 == 0 ? 2 + i % 2 : 1;
            Color tint = i % 3 == 0 ? new Color(0.89f, 0.94f, 0.85f) : Colors.White;
            DrawTextureRectRegion(_decor, new Rect2(point.X - 8, point.Y - 32, 16, 32),
                new Rect2(column * 16, 0, 16, 32), tint);
        }
        for (int x = 28; x < 694; x += 32)
        {
            DrawTextureRectRegion(_decor, new Rect2(x, 8, 16, 32), new Rect2(16, 0, 16, 32));
            DrawTextureRectRegion(_decor, new Rect2(x + 8, 473, 16, 32), new Rect2(16, 0, 16, 32));
        }
    }

    private void DrawRestSign()
    {
        var bed = GetNodeOrNull<Node2D>("Buildings/PlayerHome/BedEntrance");
        if (bed == null) return;
        Vector2 point = ToLocal(bed.GlobalPosition) + new Vector2(12, -2);
        DrawRect(new Rect2(point + new Vector2(5, 1), new Vector2(2, 7)), new Color("795939"));
        DrawRect(new Rect2(point + new Vector2(0, -9), new Vector2(13, 10)), new Color("614930"));
        DrawRect(new Rect2(point + new Vector2(1, -8), new Vector2(11, 8)), new Color("ecd6a1"));
        DrawRect(new Rect2(point + new Vector2(3, -6), new Vector2(2, 2)), new Color("658991"));
        DrawRect(new Rect2(point + new Vector2(5, -5), new Vector2(5, 3)), new Color("658991"));
        DrawRect(new Rect2(point + new Vector2(2, -4), new Vector2(1, 3)), new Color("658991"));
        DrawRect(new Rect2(point + new Vector2(9, -3), new Vector2(1, 2)), new Color("658991"));
    }

    private static bool NearRoad(Vector2 point, Vector2[] road)
    {
        for (int i = 1; i < road.Length; i++)
            if (Geometry2D.GetClosestPointToSegment(point, road[i - 1], road[i]).DistanceSquaredTo(point) < 22 * 22)
                return true;
        return false;
    }

    private void DrawBench(Vector2 point)
    {
        DrawRect(new Rect2(point + new Vector2(-11, 3), new Vector2(24, 4)), new Color(0.20f, 0.27f, 0.16f, 0.20f));
        DrawRect(new Rect2(point + new Vector2(-10, -1), new Vector2(3, 8)), new Color("76533b"));
        DrawRect(new Rect2(point + new Vector2(7, -1), new Vector2(3, 8)), new Color("76533b"));
        DrawRect(new Rect2(point + new Vector2(-12, -4), new Vector2(24, 3)), new Color("c59b66"));
        DrawRect(new Rect2(point + new Vector2(-12, 0), new Vector2(24, 3)), new Color("b38c59"));
    }
}
