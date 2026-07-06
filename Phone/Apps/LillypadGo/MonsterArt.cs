using System.Numerics;
using Dalamud.Bindings.ImGui;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// Pose passed to the renderer so the app can animate: idle time, an attack lunge, a hurt
// flash, fade alpha, and a fainted droop.
internal readonly struct MonsterPose
{
    public MonsterPose(float time, float lunge, float hurt, float alpha, bool fainted)
    {
        Time = time;
        Lunge = lunge;
        Hurt = hurt;
        Alpha = alpha;
        Fainted = fainted;
    }

    public float Time { get; }
    public float Lunge { get; }
    public float Hurt { get; }
    public float Alpha { get; }
    public bool Fainted { get; }

    public static MonsterPose Idle(float time) => new(time, 0f, 0f, 1f, false);
}

// Draws a creature: the bundled animated Pokémon spritesheet when available, else a procedural
// silhouette from the species' ArtSpec (used while a texture streams in, or if assets are missing).
internal static class MonsterArt
{
    // `back` selects the back-facing sprite for the player's own battler; everything else is front.
    public static void Draw(ImDrawListPtr dl, Vector2 center, float size, MonsterSpecies species, float facing,
        in MonsterPose pose, bool back = false)
    {
        if (TryDrawSprite(dl, center, size, species.Id, facing, pose, back))
        {
            return;
        }

        DrawProcedural(dl, center, size, species.Art, facing, pose);
    }

    private static bool TryDrawSprite(ImDrawListPtr dl, Vector2 center, float size, string id, float facing,
        in MonsterPose pose, bool back)
    {
        if (!PokemonSprites.TryGetFrame(id, back, pose.Time, out var frame))
        {
            return false;
        }

        var a = pose.Alpha;
        var c = center;
        if (pose.Fainted)
        {
            c.Y += size * 0.35f;
            a *= 0.55f;
        }
        else
        {
            c.Y += MathF.Sin(pose.Time * 2.2f) * size * 0.04f;
        }

        c.X += facing * pose.Lunge * size * 0.55f;

        // Ground shadow (matches the procedural renderer).
        Ellipse(dl, c + new Vector2(0f, size * 0.92f), size * 0.7f, size * 0.16f,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.20f * a)));

        // Fit the frame so its feet sit near the shadow and it keeps its aspect ratio.
        var h = size * 2.1f;
        var w = h * (frame.Aspect <= 0f ? 1f : frame.Aspect);
        var baseY = c.Y + size * 0.95f;
        var min = new Vector2(c.X - w * 0.5f, baseY - h);
        var max = new Vector2(c.X + w * 0.5f, baseY);

        // Hurt reads as a red flash (AddImage tint multiplies, so we can only darken channels).
        var flash = Math.Clamp(pose.Hurt, 0f, 1f);
        var tint = ImGui.GetColorU32(new Vector4(1f, 1f - flash * 0.4f, 1f - flash * 0.4f, a));
        dl.AddImage(frame.Handle, min, max, frame.Uv0, frame.Uv1, tint);
        return true;
    }

    private static void DrawProcedural(ImDrawListPtr dl, Vector2 center, float size, in ArtSpec spec, float facing,
        in MonsterPose pose)
    {
        var a = pose.Alpha;
        if (pose.Fainted)
        {
            center.Y += size * 0.35f;
            a *= 0.55f;
        }
        else
        {
            center.Y += MathF.Sin(pose.Time * 2.2f) * size * 0.05f;
        }

        center.X += facing * pose.Lunge * size * 0.55f;

        var primary = Flash(spec.Primary, pose.Hurt, a);
        var secondary = Flash(spec.Secondary, pose.Hurt, a);
        var belly = Flash(spec.Belly, pose.Hurt, a);

        // Ground shadow.
        Ellipse(dl, center + new Vector2(0f, size * 0.92f), size * 0.7f, size * 0.16f,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.20f * a)));

        switch (spec.Shape)
        {
            case Archetype.Quadruped:
                DrawQuadruped(dl, center, size, spec, facing, primary, secondary, belly, a);
                break;
            case Archetype.Avian:
                DrawAvian(dl, center, size, spec, facing, primary, secondary, belly, a);
                break;
            case Archetype.Serpent:
                DrawSerpent(dl, center, size, spec, facing, primary, secondary, belly, a, pose.Time);
                break;
            case Archetype.Blob:
            case Archetype.Wisp:
                DrawBlob(dl, center, size, spec, facing, primary, secondary, belly, a, pose.Time);
                break;
            case Archetype.Insectoid:
                DrawSerpent(dl, center, size, spec, facing, primary, secondary, belly, a, pose.Time);
                break;
        }
    }

    private static void DrawQuadruped(ImDrawListPtr dl, Vector2 c, float s, in ArtSpec spec, float facing, uint primary,
        uint secondary, uint belly, float a)
    {
        var legCol = secondary;
        // Legs.
        foreach (var ox in new[] { -0.34f, 0.02f, 0.34f, 0.7f * facing })
        {
            var lx = c.X + ox * s;
            dl.AddLine(new Vector2(lx, c.Y + s * 0.25f), new Vector2(lx, c.Y + s * 0.85f), legCol,
                MathF.Max(2f, s * 0.16f));
        }

        // Tail.
        if (spec.Tail)
        {
            var t = new Vector2(c.X - facing * s * 0.62f, c.Y - s * 0.05f);
            dl.AddLine(new Vector2(c.X - facing * s * 0.5f, c.Y), t, primary, MathF.Max(2f, s * 0.12f));
            dl.AddCircleFilled(t, s * 0.12f, primary);
        }

        // Body.
        Ellipse(dl, c + new Vector2(0f, s * 0.05f), s * 0.62f, s * 0.44f, primary);
        Ellipse(dl, c + new Vector2(0f, s * 0.24f), s * 0.42f, s * 0.24f, belly);

        // Head.
        var head = new Vector2(c.X + facing * s * 0.52f, c.Y - s * 0.3f);
        dl.AddCircleFilled(head, s * 0.36f, primary);
        Ellipse(dl, head + new Vector2(0f, s * 0.1f), s * 0.22f, s * 0.16f, belly);
        if (spec.Horns)
        {
            Horn(dl, head + new Vector2(-s * 0.12f, -s * 0.28f), s, secondary);
            Horn(dl, head + new Vector2(s * 0.12f, -s * 0.28f), s, secondary);
        }

        Eyes(dl, head + new Vector2(facing * s * 0.08f, -s * 0.02f), s, facing, spec, a);
        Snout(dl, head + new Vector2(facing * s * 0.28f, s * 0.06f), s, facing, secondary);
    }

    private static void DrawAvian(ImDrawListPtr dl, Vector2 c, float s, in ArtSpec spec, float facing, uint primary,
        uint secondary, uint belly, float a)
    {
        // Feet.
        dl.AddLine(new Vector2(c.X - s * 0.12f, c.Y + s * 0.4f), new Vector2(c.X - s * 0.12f, c.Y + s * 0.72f),
            secondary, MathF.Max(1.5f, s * 0.08f));
        dl.AddLine(new Vector2(c.X + s * 0.12f, c.Y + s * 0.4f), new Vector2(c.X + s * 0.12f, c.Y + s * 0.72f),
            secondary, MathF.Max(1.5f, s * 0.08f));

        if (spec.Wings)
        {
            Wing(dl, c + new Vector2(-facing * s * 0.4f, -s * 0.05f), s, -facing, secondary);
        }

        // Body (rounder, upright).
        Ellipse(dl, c, s * 0.5f, s * 0.56f, primary);
        Ellipse(dl, c + new Vector2(0f, s * 0.12f), s * 0.32f, s * 0.36f, belly);

        // Tail feathers.
        if (spec.Tail)
        {
            for (var i = -1; i <= 1; i++)
            {
                var baseP = new Vector2(c.X - facing * s * 0.42f, c.Y + s * 0.1f);
                var tip = baseP + new Vector2(-facing * s * 0.32f, i * s * 0.2f);
                dl.AddTriangleFilled(baseP + new Vector2(0f, -s * 0.08f), tip, baseP + new Vector2(0f, s * 0.08f),
                    i == 0 ? primary : secondary);
            }
        }

        if (spec.Wings)
        {
            Wing(dl, c + new Vector2(facing * s * 0.4f, -s * 0.05f), s, facing, secondary);
        }

        var head = new Vector2(c.X + facing * s * 0.05f, c.Y - s * 0.52f);
        dl.AddCircleFilled(head, s * 0.3f, primary);
        Eyes(dl, head + new Vector2(facing * s * 0.06f, 0f), s, facing, spec, a);
        Beak(dl, head + new Vector2(facing * s * 0.28f, s * 0.02f), s, facing);
    }

    private static void DrawBlob(ImDrawListPtr dl, Vector2 c, float s, in ArtSpec spec, float facing, uint primary,
        uint secondary, uint belly, float a, float time)
    {
        var squash = 1f + MathF.Sin(time * 2.6f) * 0.06f;
        if (spec.Fins)
        {
            Wing(dl, c + new Vector2(-s * 0.5f, s * 0.1f), s, -1f, secondary);
            Wing(dl, c + new Vector2(s * 0.5f, s * 0.1f), s, 1f, secondary);
        }

        Ellipse(dl, c, s * 0.62f / squash, s * 0.6f * squash, primary);
        Ellipse(dl, c + new Vector2(0f, s * 0.2f), s * 0.4f, s * 0.28f, belly);
        // Glossy highlight.
        Ellipse(dl, c + new Vector2(-s * 0.2f, -s * 0.24f), s * 0.16f, s * 0.1f,
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.35f * a)));

        if (spec.Spikes)
        {
            for (var i = -1; i <= 1; i++)
            {
                var baseP = new Vector2(c.X + i * s * 0.26f, c.Y - s * 0.52f);
                dl.AddTriangleFilled(baseP + new Vector2(-s * 0.1f, s * 0.12f), baseP + new Vector2(0f, -s * 0.24f),
                    baseP + new Vector2(s * 0.1f, s * 0.12f), secondary);
            }
        }

        Eyes(dl, c + new Vector2(facing * s * 0.08f, -s * 0.08f), s, facing, spec, a);
        // Little mouth.
        dl.AddLine(c + new Vector2(-s * 0.08f, s * 0.16f), c + new Vector2(s * 0.08f, s * 0.16f),
            ImGui.GetColorU32(new Vector4(0.1f, 0.1f, 0.14f, 0.6f * a)), MathF.Max(1f, s * 0.03f));
    }

    private static void DrawSerpent(ImDrawListPtr dl, Vector2 c, float s, in ArtSpec spec, float facing, uint primary,
        uint secondary, uint belly, float a, float time)
    {
        // Wavy body of shrinking segments.
        for (var i = 5; i >= 0; i--)
        {
            var t = i / 5f;
            var x = c.X - facing * (t * s * 0.9f);
            var y = c.Y + s * 0.1f + MathF.Sin(time * 3f + i * 0.9f) * s * 0.12f + t * s * 0.1f;
            dl.AddCircleFilled(new Vector2(x, y), s * (0.34f - t * 0.2f), i % 2 == 0 ? primary : secondary);
        }

        if (spec.Fins)
        {
            Wing(dl, c + new Vector2(0f, -s * 0.2f), s, 1f, secondary);
        }

        var head = new Vector2(c.X + facing * s * 0.34f, c.Y - s * 0.05f);
        dl.AddCircleFilled(head, s * 0.32f, primary);
        Ellipse(dl, head + new Vector2(0f, s * 0.08f), s * 0.2f, s * 0.14f, belly);
        if (spec.Spikes)
        {
            Horn(dl, head + new Vector2(0f, -s * 0.26f), s, secondary);
        }

        Eyes(dl, head + new Vector2(facing * s * 0.08f, -s * 0.04f), s, facing, spec, a);
        Snout(dl, head + new Vector2(facing * s * 0.26f, s * 0.04f), s, facing, secondary);
    }

    // ---- Shared feature primitives --------------------------------------------------

    private static void Eyes(ImDrawListPtr dl, Vector2 anchor, float s, float facing, in ArtSpec spec, float a)
    {
        var count = Math.Clamp(spec.Eyes, 1, 2);
        var eyeCol = ImGui.GetColorU32(spec.Eye with { W = spec.Eye.W * a });
        var white = ImGui.GetColorU32(new Vector4(0.98f, 0.98f, 1f, a));
        var offsets = count == 1 ? new[] { 0f } : new[] { -0.14f, 0.14f };
        foreach (var ox in offsets)
        {
            var e = anchor + new Vector2(ox * s, 0f);
            dl.AddCircleFilled(e, s * 0.12f, white);
            dl.AddCircleFilled(e + new Vector2(facing * s * 0.02f, s * 0.01f), s * 0.07f, eyeCol);
            dl.AddCircleFilled(e + new Vector2(-facing * s * 0.03f, -s * 0.03f), s * 0.025f, white);
        }
    }

    private static void Snout(ImDrawListPtr dl, Vector2 p, float s, float facing, uint col)
    {
        dl.AddTriangleFilled(p + new Vector2(0f, -s * 0.08f), p + new Vector2(facing * s * 0.14f, 0f),
            p + new Vector2(0f, s * 0.08f), col);
    }

    private static void Beak(ImDrawListPtr dl, Vector2 p, float s, float facing)
    {
        var beak = ImGui.GetColorU32(new Vector4(0.97f, 0.72f, 0.24f, 1f));
        dl.AddTriangleFilled(p + new Vector2(0f, -s * 0.08f), p + new Vector2(facing * s * 0.2f, 0f),
            p + new Vector2(0f, s * 0.08f), beak);
    }

    private static void Horn(ImDrawListPtr dl, Vector2 baseP, float s, uint col)
    {
        dl.AddTriangleFilled(baseP + new Vector2(-s * 0.07f, s * 0.06f), baseP + new Vector2(0f, -s * 0.22f),
            baseP + new Vector2(s * 0.07f, s * 0.06f), col);
    }

    private static void Wing(ImDrawListPtr dl, Vector2 baseP, float s, float dir, uint col)
    {
        dl.AddTriangleFilled(baseP, baseP + new Vector2(dir * s * 0.5f, -s * 0.24f),
            baseP + new Vector2(dir * s * 0.44f, s * 0.24f), col);
    }

    private static void Ellipse(ImDrawListPtr dl, Vector2 center, float rx, float ry, uint color)
    {
        const int segments = 28;
        Span<Vector2> points = stackalloc Vector2[segments];
        for (var i = 0; i < segments; i++)
        {
            var t = i / (float)segments * MathF.PI * 2f;
            points[i] = new Vector2(center.X + MathF.Cos(t) * rx, center.Y + MathF.Sin(t) * ry);
        }

        dl.AddConvexPolyFilled(ref points[0], segments, color);
    }

    private static uint Flash(Vector4 color, float hurt, float alpha)
    {
        var mixed = Vector4.Lerp(color, new Vector4(1f, 1f, 1f, color.W), Math.Clamp(hurt, 0f, 1f) * 0.7f);
        mixed.W = color.W * alpha;
        return ImGui.GetColorU32(mixed);
    }
}
