using System.Numerics;
using Dalamud.Bindings.ImGui;
using VideoSyncPrototype.Phone.Apps.Games.Framework;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone.Apps.Games.Chocobo;

// Original, hand-drawn vector chocobo in the same flat/rounded style as the other games —
// no sprite assets. Built from capsules, circles, triangles and stroked legs so it stays
// crisp at any size and matches the arcade look.
internal static class ChocoboRenderer
{
    private static readonly Vector4 LegColor = new(0.87f, 0.66f, 0.26f, 1f);
    private static readonly Vector4 BeakColor = new(0.98f, 0.74f, 0.26f, 1f);
    private static readonly Vector4 BeakShade = new(0.86f, 0.55f, 0.16f, 1f);
    private static readonly Vector4 EyeWhite = new(0.98f, 0.98f, 1f, 1f);
    private static readonly Vector4 Pupil = new(0.12f, 0.12f, 0.16f, 1f);

    // Draws a chocobo facing right. `center` is the body core; `s` is the overall size unit.
    // `stride` advances the running-legs cycle; when `running` is false the legs stand still.
    public static void Draw(ImDrawListPtr dl, Vector2 center, float s, Vector4 body, Vector4 jockey, float stride,
        bool running, float bob)
    {
        center.Y += bob;
        var bodyHi = GamePalette.Lighten(body, 0.22f);
        var bodyLo = GamePalette.Darken(body, 0.30f);
        var plume = GamePalette.Lighten(body, 0.36f);
        var feetY = center.Y + s * 0.74f;

        // Ground shadow.
        Capsule(dl, new Vector2(center.X - s * 0.52f, feetY - s * 0.05f),
            new Vector2(center.X + s * 0.5f, feetY + s * 0.05f), Col(new Vector4(0f, 0f, 0f, 0.22f)));

        // Plume tail (behind the body).
        DrawTail(dl, new Vector2(center.X - s * 0.4f, center.Y + s * 0.04f), s, plume, bodyHi);

        // Back leg (behind body), then body, then front leg for depth.
        DrawLeg(dl, new Vector2(center.X - s * 0.04f, center.Y + s * 0.28f), feetY, s, stride, running);

        // Body: an egg-shaped capsule with a soft vertical gradient.
        var bodyMin = new Vector2(center.X - s * 0.52f, center.Y - s * 0.36f);
        var bodyMax = new Vector2(center.X + s * 0.42f, center.Y + s * 0.48f);
        var bodyRadius = (bodyMax.Y - bodyMin.Y) * 0.5f;
        Squircle.FillVerticalGradient(dl, bodyMin, bodyMax, bodyRadius, Col(bodyHi), Col(bodyLo));
        // Belly highlight.
        Capsule(dl, new Vector2(center.X - s * 0.34f, center.Y + s * 0.02f),
            new Vector2(center.X + s * 0.28f, center.Y + s * 0.42f), Col(bodyHi with { W = 0.5f }));

        // Wing folded on the flank.
        Capsule(dl, new Vector2(center.X - s * 0.24f, center.Y - s * 0.06f),
            new Vector2(center.X + s * 0.2f, center.Y + s * 0.24f), Col(bodyLo with { W = 0.9f }));

        // Jockey saddle blanket across the back.
        Squircle.Fill(dl, new Vector2(center.X - s * 0.2f, center.Y - s * 0.28f),
            new Vector2(center.X + s * 0.08f, center.Y - s * 0.02f), s * 0.09f, Col(jockey));
        Squircle.Stroke(dl, new Vector2(center.X - s * 0.2f, center.Y - s * 0.28f),
            new Vector2(center.X + s * 0.08f, center.Y - s * 0.02f), s * 0.09f,
            Col(GamePalette.Lighten(jockey, 0.3f) with { W = 0.7f }), MathF.Max(1f, s * 0.02f));

        // Front leg (in front of body).
        DrawLeg(dl, new Vector2(center.X + s * 0.16f, center.Y + s * 0.3f), feetY, s, stride + MathF.PI, running);

        // Neck connecting body to head.
        Squircle.Fill(dl, new Vector2(center.X + s * 0.12f, center.Y - s * 0.62f),
            new Vector2(center.X + s * 0.34f, center.Y + s * 0.06f), s * 0.11f, Col(body));

        // Head.
        var head = new Vector2(center.X + s * 0.34f, center.Y - s * 0.64f);
        var headR = s * 0.28f;
        dl.AddCircleFilled(head, headR, Col(body));
        dl.AddCircleFilled(head - new Vector2(0f, headR * 0.3f), headR * 0.8f, Col(bodyHi with { W = 0.85f }));

        // Head crest feathers.
        for (var i = 0; i < 3; i++)
        {
            var baseX = head.X - s * 0.02f + (i - 1) * s * 0.11f;
            var baseY = head.Y - headR * 0.65f;
            dl.AddTriangleFilled(new Vector2(baseX - s * 0.06f, baseY + s * 0.05f),
                new Vector2(baseX + (i - 1) * s * 0.03f, baseY - s * 0.2f),
                new Vector2(baseX + s * 0.07f, baseY + s * 0.05f), Col(i == 1 ? plume : bodyHi));
        }

        // Beak.
        var beakBase = head + new Vector2(headR * 0.72f, s * 0.0f);
        dl.AddTriangleFilled(beakBase + new Vector2(-s * 0.02f, -s * 0.11f),
            beakBase + new Vector2(s * 0.28f, -s * 0.01f), beakBase + new Vector2(-s * 0.02f, s * 0.02f), Col(BeakColor));
        dl.AddTriangleFilled(beakBase + new Vector2(-s * 0.02f, s * 0.02f),
            beakBase + new Vector2(s * 0.24f, s * 0.02f), beakBase + new Vector2(-s * 0.02f, s * 0.11f), Col(BeakShade));

        // Eye.
        var eye = head + new Vector2(headR * 0.34f, -headR * 0.16f);
        dl.AddCircleFilled(eye, s * 0.11f, Col(EyeWhite));
        dl.AddCircleFilled(eye + new Vector2(s * 0.015f, s * 0.005f), s * 0.06f, Col(Pupil));
        dl.AddCircleFilled(eye + new Vector2(-s * 0.02f, -s * 0.03f), s * 0.022f, Col(EyeWhite));
    }

    private static void DrawTail(ImDrawListPtr dl, Vector2 baseP, float s, Vector4 plume, Vector4 bodyHi)
    {
        for (var i = 0; i < 3; i++)
        {
            var tip = baseP + new Vector2(-s * 0.28f - i * s * 0.05f, -s * 0.36f + i * s * 0.22f);
            dl.AddTriangleFilled(baseP + new Vector2(0f, -s * 0.14f), tip, baseP + new Vector2(0f, s * 0.16f),
                Col(i % 2 == 0 ? plume : bodyHi));
        }
    }

    private static void DrawLeg(ImDrawListPtr dl, Vector2 hip, float feetY, float s, float phase, bool running)
    {
        var swing = running ? MathF.Sin(phase) : 0f;
        var lift = running ? MathF.Max(0f, MathF.Sin(phase)) : 0f;
        var foot = new Vector2(hip.X + swing * s * 0.24f, feetY - lift * s * 0.16f);
        var knee = new Vector2(((hip.X + foot.X) * 0.5f) + s * 0.05f, (hip.Y + foot.Y) * 0.5f);
        var col = Col(LegColor);
        dl.AddLine(hip, knee, col, MathF.Max(1.5f, s * 0.1f));
        dl.AddLine(knee, foot, col, MathF.Max(1.3f, s * 0.085f));
        dl.AddCircleFilled(knee, s * 0.05f, col);
        // Three toes.
        dl.AddLine(foot, foot + new Vector2(s * 0.13f, -s * 0.02f), col, MathF.Max(1f, s * 0.05f));
        dl.AddLine(foot, foot + new Vector2(s * 0.1f, s * 0.04f), col, MathF.Max(1f, s * 0.05f));
        dl.AddLine(foot, foot + new Vector2(-s * 0.06f, s * 0.03f), col, MathF.Max(1f, s * 0.05f));
    }

    private static void Capsule(ImDrawListPtr dl, Vector2 min, Vector2 max, uint color)
    {
        var radius = MathF.Min(max.X - min.X, max.Y - min.Y) * 0.5f;
        Squircle.Fill(dl, min, max, radius, color);
    }

    private static uint Col(Vector4 color) => ImGui.GetColorU32(color);
}
