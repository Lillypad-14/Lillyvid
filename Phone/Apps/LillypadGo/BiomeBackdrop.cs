using System.Numerics;
using Dalamud.Bindings.ImGui;
using VideoSyncPrototype.Phone.Core;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal static class BiomeBackdrop
{
    private const float BattleArenaHeight = 0.68f;

    private readonly record struct Colors(Vector4 Sky, Vector4 Horizon, Vector4 Ground, Vector4 Accent,
        Vector4 Silhouette);

    public static void Draw(ImDrawListPtr drawList, Rect area, Biome biome, float clock, bool battle)
    {
        var colors = Palette(biome);
        var alpha = battle ? 0.88f : 0.58f;
        var horizonY = area.Min.Y + area.Height * (battle ? 0.58f : 0.64f);
        drawList.PushClipRect(area.Min, area.Max, true);

        // Prefer the imported Showdown arena photo for this biome; fall back to the procedural scene.
        if (DrawPhoto(drawList, area, biome, battle, colors))
        {
            drawList.PopClipRect();
            return;
        }

        drawList.AddRectFilled(area.Min, new Vector2(area.Max.X, horizonY), U32(colors.Sky, alpha));
        drawList.AddRectFilled(new Vector2(area.Min.X, horizonY),
            new Vector2(area.Max.X, horizonY + area.Height * 0.1f), U32(colors.Horizon, alpha));
        drawList.AddRectFilled(new Vector2(area.Min.X, horizonY + area.Height * 0.1f), area.Max,
            U32(colors.Ground, alpha));

        var light = Point(area, 0.82f, 0.18f);
        drawList.AddCircleFilled(light, area.Width * 0.075f, U32(colors.Accent, alpha * 0.18f), 40);
        drawList.AddCircleFilled(light, area.Width * 0.04f, U32(colors.Accent, alpha * 0.46f), 32);

        switch (biome)
        {
            case Biome.Forest:
                DrawForest(drawList, area, colors, horizonY, alpha);
                break;
            case Biome.Grassland:
                DrawGrassland(drawList, area, colors, horizonY, alpha);
                break;
            case Biome.Desert:
                DrawDesert(drawList, area, colors, horizonY, alpha);
                break;
            case Biome.Coast:
                DrawCoast(drawList, area, colors, horizonY, clock, alpha);
                break;
            case Biome.Snow:
                DrawSnow(drawList, area, colors, horizonY, clock, alpha);
                break;
            case Biome.Volcanic:
                DrawVolcanic(drawList, area, colors, horizonY, clock, alpha);
                break;
            case Biome.Cave:
                DrawCave(drawList, area, colors, alpha);
                break;
            case Biome.Wetland:
                DrawWetland(drawList, area, colors, horizonY, clock, alpha);
                break;
        }

        if (battle)
        {
            var stageY = area.Min.Y + area.Height * 0.66f;
            drawList.AddQuadFilled(new Vector2(area.Min.X, stageY), new Vector2(area.Max.X, stageY), area.Max,
                new Vector2(area.Min.X, area.Max.Y), U32(colors.Ground, 0.44f));
            drawList.AddLine(new Vector2(area.Min.X, stageY), new Vector2(area.Max.X, stageY),
                U32(colors.Accent, 0.2f), 2f);
        }

        drawList.PopClipRect();
    }

    private static bool DrawPhoto(ImDrawListPtr dl, Rect a, Biome biome, bool battle, Colors c)
    {
        if (!BiomeBgTextures.TryGet(biome, out var tex, out var aspect))
        {
            return false;
        }

        dl.AddRectFilled(a.Min, a.Max, U32(c.Ground, 1f));
        if (battle)
        {
            var arenaBottom = a.Min.Y + a.Height * BattleArenaHeight;
            var arena = new Rect(a.Min, new Vector2(a.Max.X, arenaBottom));
            CoverUvs(arena, aspect, out var uv0, out var uv1);
            dl.AddImage(tex, arena.Min, arena.Max, uv0, uv1,
                ImGui.GetColorU32(Vector4.One));
        }
        else
        {
            CoverUvs(a, aspect, out var uv0, out var uv1);
            dl.AddImage(tex, a.Min, a.Max, uv0, uv1, ImGui.GetColorU32(Vector4.One));
        }

        // Contrast scrim so creatures, panels, and text stay legible over the photo.
        var flat = battle ? 0.16f : 0.42f;
        dl.AddRectFilled(a.Min, a.Max, U32(new Vector4(0.03f, 0.04f, 0.06f, 1f), flat));
        var gradTop = a.Min.Y + a.Height * 0.5f;
        var maxA = battle ? 0.5f : 0.64f;
        const int bands = 6;
        for (var i = 0; i < bands; i++)
        {
            var y0 = gradTop + (a.Max.Y - gradTop) * (i / (float)bands);
            var y1 = gradTop + (a.Max.Y - gradTop) * ((i + 1) / (float)bands);
            dl.AddRectFilled(new Vector2(a.Min.X, y0), new Vector2(a.Max.X, y1),
                U32(new Vector4(0.02f, 0.03f, 0.05f, 1f), maxA * (i + 1) / bands));
        }

        return true;
    }

    public static Vector2 BattlePoint(Rect area, float x, float sourceY)
    {
        return new Vector2(area.Min.X + area.Width * x,
            area.Min.Y + area.Height * BattleArenaHeight * sourceY);
    }

    private static void CoverUvs(Rect area, float imageAspect, out Vector2 uv0, out Vector2 uv1)
    {
        var areaAspect = area.Width / MathF.Max(1f, area.Height);
        uv0 = Vector2.Zero;
        uv1 = Vector2.One;
        if (imageAspect > areaAspect)
        {
            var inset = (1f - areaAspect / imageAspect) * 0.5f;
            uv0.X = inset;
            uv1.X = 1f - inset;
        }
        else
        {
            var inset = (1f - imageAspect / areaAspect) * 0.5f;
            uv0.Y = inset;
            uv1.Y = 1f - inset;
        }
    }

    private static void DrawForest(ImDrawListPtr dl, Rect a, Colors c, float horizon, float alpha)
    {
        var xs = new[] { 0.05f, 0.22f, 0.66f, 0.88f };
        for (var i = 0; i < xs.Length; i++)
        {
            var x = a.Min.X + a.Width * xs[i];
            var width = a.Width * (0.035f + i % 2 * 0.012f);
            dl.AddRectFilled(new Vector2(x - width, a.Min.Y + a.Height * 0.16f),
                new Vector2(x + width, horizon + a.Height * 0.2f), U32(c.Silhouette, alpha * 0.72f));
            var canopy = new Vector2(x, a.Min.Y + a.Height * (0.2f + i % 2 * 0.08f));
            dl.AddCircleFilled(canopy, a.Width * 0.13f, U32(c.Silhouette, alpha * 0.78f), 28);
            dl.AddCircleFilled(canopy + new Vector2(a.Width * 0.08f, a.Height * 0.03f), a.Width * 0.1f,
                U32(c.Silhouette, alpha * 0.72f), 26);
        }
    }

    private static void DrawGrassland(ImDrawListPtr dl, Rect a, Colors c, float horizon, float alpha)
    {
        dl.AddCircleFilled(new Vector2(a.Min.X + a.Width * 0.18f, horizon + a.Height * 0.18f), a.Width * 0.38f,
            U32(c.Silhouette, alpha * 0.45f), 48);
        dl.AddCircleFilled(new Vector2(a.Min.X + a.Width * 0.78f, horizon + a.Height * 0.2f), a.Width * 0.44f,
            U32(c.Silhouette, alpha * 0.38f), 48);
        for (var i = 0; i < 18; i++)
        {
            var x = a.Min.X + a.Width * (i + 0.5f) / 18f;
            var y = horizon + a.Height * (0.13f + i % 3 * 0.018f);
            dl.AddLine(new Vector2(x, y), new Vector2(x + (i % 2 == 0 ? -3f : 3f), y - 10f),
                U32(c.Accent, alpha * 0.38f), 1.5f);
        }
    }

    private static void DrawDesert(ImDrawListPtr dl, Rect a, Colors c, float horizon, float alpha)
    {
        dl.AddCircleFilled(new Vector2(a.Min.X + a.Width * 0.16f, horizon + a.Height * 0.17f), a.Width * 0.42f,
            U32(c.Silhouette, alpha * 0.5f), 48);
        dl.AddCircleFilled(new Vector2(a.Min.X + a.Width * 0.84f, horizon + a.Height * 0.22f), a.Width * 0.52f,
            U32(c.Accent, alpha * 0.14f), 48);
        dl.AddLine(new Vector2(a.Min.X, horizon + a.Height * 0.13f),
            new Vector2(a.Max.X, horizon + a.Height * 0.18f), U32(c.Accent, alpha * 0.22f), 2f);
    }

    private static void DrawCoast(ImDrawListPtr dl, Rect a, Colors c, float horizon, float clock, float alpha)
    {
        dl.AddTriangleFilled(new Vector2(a.Min.X + a.Width * 0.05f, horizon + a.Height * 0.04f),
            new Vector2(a.Min.X + a.Width * 0.26f, horizon - a.Height * 0.12f),
            new Vector2(a.Min.X + a.Width * 0.42f, horizon + a.Height * 0.04f), U32(c.Silhouette, alpha * 0.45f));
        for (var i = 0; i < 7; i++)
        {
            var y = horizon + a.Height * (0.08f + i * 0.035f);
            var offset = MathF.Sin(clock * 1.2f + i) * a.Width * 0.025f;
            dl.AddLine(new Vector2(a.Min.X + offset, y), new Vector2(a.Max.X + offset, y),
                U32(c.Accent, alpha * (0.18f + i % 2 * 0.08f)), 1.5f);
        }
    }

    private static void DrawSnow(ImDrawListPtr dl, Rect a, Colors c, float horizon, float clock, float alpha)
    {
        for (var i = 0; i < 4; i++)
        {
            var x = a.Min.X + a.Width * (0.05f + i * 0.28f);
            var width = a.Width * (0.22f + i % 2 * 0.05f);
            dl.AddTriangleFilled(new Vector2(x - width, horizon + a.Height * 0.08f),
                new Vector2(x, horizon - a.Height * (0.2f + i % 2 * 0.08f)),
                new Vector2(x + width, horizon + a.Height * 0.08f), U32(c.Silhouette, alpha * 0.55f));
        }
        for (var i = 0; i < 18; i++)
        {
            var x = a.Min.X + a.Width * ((i * 0.173f + 0.1f) % 1f);
            var fall = (clock * 0.06f + i * 0.083f) % 1f;
            var y = a.Min.Y + a.Height * fall;
            dl.AddCircleFilled(new Vector2(x, y), 1.8f + i % 2, U32(c.Accent, alpha * 0.6f), 10);
        }
    }

    private static void DrawVolcanic(ImDrawListPtr dl, Rect a, Colors c, float horizon, float clock, float alpha)
    {
        var peak = Point(a, 0.58f, 0.23f);
        dl.AddTriangleFilled(new Vector2(a.Min.X + a.Width * 0.18f, horizon + a.Height * 0.12f), peak,
            new Vector2(a.Max.X, horizon + a.Height * 0.12f), U32(c.Silhouette, alpha * 0.78f));
        dl.AddLine(peak, new Vector2(peak.X - a.Width * 0.08f, horizon), U32(c.Accent, alpha * 0.65f), 4f);
        dl.AddLine(peak, new Vector2(peak.X + a.Width * 0.12f, horizon + a.Height * 0.08f),
            U32(c.Accent, alpha * 0.52f), 3f);
        for (var i = 0; i < 10; i++)
        {
            var phase = (clock * 0.12f + i * 0.097f) % 1f;
            var x = peak.X + MathF.Sin(i * 2.2f) * a.Width * 0.14f * phase;
            var y = peak.Y - phase * a.Height * 0.22f;
            dl.AddCircleFilled(new Vector2(x, y), 2f + i % 3, U32(c.Accent, (1f - phase) * alpha * 0.7f), 10);
        }
    }

    private static void DrawCave(ImDrawListPtr dl, Rect a, Colors c, float alpha)
    {
        for (var i = 0; i < 9; i++)
        {
            var x = a.Min.X + a.Width * i / 8f;
            var width = a.Width * 0.09f;
            var depth = a.Height * (0.1f + i % 3 * 0.05f);
            dl.AddTriangleFilled(new Vector2(x - width, a.Min.Y), new Vector2(x, a.Min.Y + depth),
                new Vector2(x + width, a.Min.Y), U32(c.Silhouette, alpha * 0.82f));
        }
        for (var i = 0; i < 5; i++)
        {
            var basePoint = Point(a, 0.12f + i * 0.2f, 0.86f);
            dl.AddTriangleFilled(basePoint + new Vector2(-8f, 0f), basePoint - new Vector2(0f, 28f + i % 2 * 12f),
                basePoint + new Vector2(8f, 0f), U32(c.Accent, alpha * 0.5f));
        }
    }

    private static void DrawWetland(ImDrawListPtr dl, Rect a, Colors c, float horizon, float clock, float alpha)
    {
        for (var i = 0; i < 14; i++)
        {
            var x = a.Min.X + a.Width * (i + 0.5f) / 14f;
            var baseY = horizon + a.Height * (0.16f + i % 3 * 0.02f);
            var sway = MathF.Sin(clock + i) * 4f;
            dl.AddLine(new Vector2(x, baseY), new Vector2(x + sway, baseY - a.Height * 0.12f),
                U32(c.Silhouette, alpha * 0.65f), 2f);
        }
        for (var i = 0; i < 5; i++)
        {
            var y = horizon + a.Height * (0.08f + i * 0.04f);
            dl.AddLine(new Vector2(a.Min.X, y), new Vector2(a.Max.X, y), U32(c.Accent, alpha * 0.16f), 1.5f);
        }
    }

    private static Colors Palette(Biome biome) => biome switch
    {
        Biome.Forest => new Colors(new(0.08f, 0.18f, 0.17f, 1f), new(0.18f, 0.34f, 0.25f, 1f),
            new(0.08f, 0.13f, 0.10f, 1f), new(0.52f, 0.84f, 0.48f, 1f), new(0.04f, 0.09f, 0.07f, 1f)),
        Biome.Grassland => new Colors(new(0.13f, 0.25f, 0.34f, 1f), new(0.38f, 0.48f, 0.30f, 1f),
            new(0.12f, 0.20f, 0.12f, 1f), new(0.88f, 0.74f, 0.34f, 1f), new(0.08f, 0.15f, 0.09f, 1f)),
        Biome.Desert => new Colors(new(0.30f, 0.19f, 0.22f, 1f), new(0.58f, 0.38f, 0.24f, 1f),
            new(0.24f, 0.15f, 0.10f, 1f), new(0.95f, 0.66f, 0.30f, 1f), new(0.16f, 0.09f, 0.08f, 1f)),
        Biome.Coast => new Colors(new(0.08f, 0.22f, 0.34f, 1f), new(0.18f, 0.42f, 0.52f, 1f),
            new(0.05f, 0.15f, 0.22f, 1f), new(0.42f, 0.82f, 0.88f, 1f), new(0.05f, 0.11f, 0.14f, 1f)),
        Biome.Snow => new Colors(new(0.15f, 0.22f, 0.34f, 1f), new(0.46f, 0.62f, 0.72f, 1f),
            new(0.16f, 0.22f, 0.26f, 1f), new(0.80f, 0.94f, 1f, 1f), new(0.09f, 0.14f, 0.22f, 1f)),
        Biome.Volcanic => new Colors(new(0.18f, 0.07f, 0.10f, 1f), new(0.42f, 0.12f, 0.08f, 1f),
            new(0.10f, 0.06f, 0.07f, 1f), new(1f, 0.34f, 0.14f, 1f), new(0.05f, 0.04f, 0.05f, 1f)),
        Biome.Cave => new Colors(new(0.06f, 0.07f, 0.12f, 1f), new(0.13f, 0.12f, 0.20f, 1f),
            new(0.05f, 0.05f, 0.08f, 1f), new(0.52f, 0.72f, 0.94f, 1f), new(0.025f, 0.025f, 0.04f, 1f)),
        Biome.Wetland => new Colors(new(0.08f, 0.20f, 0.22f, 1f), new(0.22f, 0.38f, 0.30f, 1f),
            new(0.07f, 0.14f, 0.13f, 1f), new(0.44f, 0.82f, 0.72f, 1f), new(0.04f, 0.09f, 0.08f, 1f)),
        _ => new Colors(new(0.1f, 0.14f, 0.2f, 1f), new(0.2f, 0.28f, 0.34f, 1f),
            new(0.08f, 0.1f, 0.13f, 1f), new(0.7f, 0.8f, 0.9f, 1f), new(0.04f, 0.06f, 0.08f, 1f)),
    };

    private static Vector2 Point(Rect area, float x, float y) =>
        new(area.Min.X + area.Width * x, area.Min.Y + area.Height * y);

    private static uint U32(Vector4 color, float alpha) => ImGui.GetColorU32(color with { W = alpha });
}
