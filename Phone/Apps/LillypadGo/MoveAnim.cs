using System.Numerics;
using Dalamud.Bindings.ImGui;
using VideoSyncPrototype.Phone.Core;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// A keyframe port of Pokémon Showdown's move animations. Each move is a list of fx-sprite
// keyframes (showEffect) expressed relative to the attacker/defender, plus background flashes.
// The runtime maps Showdown's scene coordinates onto the battle screen and tweens them, so the
// real Showdown choreography (bolts, arcs, bursts) plays with the real fx art.

internal enum AnimBase : byte { Attacker, Defender, Mid, Scene }

internal enum AnimEase : byte { Linear, Accel, Decel, Swing, Ballistic, BallisticUp, BallisticDown }

// A coordinate: an anchor (attacker/defender/…) plus an offset in Showdown scene units.
internal readonly struct AnimCoord
{
    public AnimCoord(AnimBase anchor, float off)
    {
        Anchor = anchor;
        Off = off;
    }

    public AnimBase Anchor { get; }
    public float Off { get; }
}

internal readonly struct AnimState
{
    public AnimState(AnimCoord x, AnimCoord y, AnimCoord z, float scale, float opacity, float time)
    {
        X = x;
        Y = y;
        Z = z;
        Scale = scale;
        Opacity = opacity;
        Time = time;
    }

    public AnimCoord X { get; }
    public AnimCoord Y { get; }
    public AnimCoord Z { get; }
    public float Scale { get; }
    public float Opacity { get; }
    public float Time { get; } // ms
}

internal readonly struct AnimEffect
{
    public AnimEffect(string sprite, int w, int h, AnimState start, AnimState end, AnimEase ease)
    {
        Sprite = sprite;
        W = w;
        H = h;
        Start = start;
        End = end;
        Ease = ease;
    }

    public string Sprite { get; }
    public int W { get; }
    public int H { get; }
    public AnimState Start { get; }
    public AnimState End { get; }
    public AnimEase Ease { get; }
}

internal readonly struct AnimBg
{
    public AnimBg(uint rgb, float opacity, float time, float dur)
    {
        Rgb = rgb;
        Opacity = opacity;
        Time = time;
        Dur = dur;
    }

    public uint Rgb { get; }
    public float Opacity { get; }
    public float Time { get; }
    public float Dur { get; }
}

internal sealed class MoveAnim
{
    public MoveAnim(float durationMs, AnimEffect[] fx, AnimBg[] bg)
    {
        DurationMs = durationMs;
        Fx = fx;
        Bg = bg;
    }

    public float DurationMs { get; }
    public AnimEffect[] Fx { get; }
    public AnimBg[] Bg { get; }
}

internal static partial class MoveAnims
{
    // Showdown scene-unit -> screen-pixel factor (before GlobalScale). Tune if effects read too
    // large/small or offsets sit wrong relative to the creatures.
    private const float CoordScale = 0.5f;

    private static readonly Dictionary<string, MoveAnim> ByName = new(StringComparer.OrdinalIgnoreCase);

    static MoveAnims() => Populate();

    static partial void Populate();

    // ---- Builders used by the generated table ----
    private static AnimCoord A(float off) => new(AnimBase.Attacker, off);
    private static AnimCoord D(float off) => new(AnimBase.Defender, off);
    private static AnimCoord M(float off) => new(AnimBase.Mid, off);
    private static AnimCoord K(float off) => new(AnimBase.Scene, off);

    private static AnimState St(AnimCoord x, AnimCoord y, AnimCoord z, float scale, float opacity, float time) =>
        new(x, y, z, scale, opacity, time);

    private static AnimEffect Fx(string sprite, int w, int h, AnimState start, AnimState end, AnimEase ease) =>
        new(sprite, w, h, start, end, ease);

    private static AnimBg Bg(uint rgb, float opacity, float time, float dur) => new(rgb, opacity, time, dur);

    private static void Add(string name, float durationMs, AnimEffect[] fx, AnimBg[] bg) =>
        ByName[name] = new MoveAnim(durationMs, fx, bg);

    public static MoveAnim? For(string moveName) => ByName.TryGetValue(moveName, out var anim) ? anim : null;

    // ---- Runtime ----
    public static void Play(ImDrawListPtr dl, MoveAnim anim, Vector2 attacker, Vector2 defender, Rect scene,
        float elapsedMs, float scale, float effectScale)
    {
        foreach (var bg in anim.Bg)
        {
            if (elapsedMs < bg.Time || elapsedMs > bg.Time + bg.Dur)
            {
                continue;
            }

            var lt = (elapsedMs - bg.Time) / MathF.Max(1f, bg.Dur);
            var a = bg.Opacity * (1f - MathF.Abs(lt * 2f - 1f)); // ease in then out
            var color = new Vector4(((bg.Rgb >> 16) & 0xFF) / 255f, ((bg.Rgb >> 8) & 0xFF) / 255f,
                (bg.Rgb & 0xFF) / 255f, Math.Clamp(a, 0f, 1f));
            dl.AddRectFilled(scene.Min, scene.Max, ImGui.GetColorU32(color));
        }

        var k = CoordScale * scale;
        foreach (var fx in anim.Fx)
        {
            if (elapsedMs < fx.Start.Time || elapsedMs > fx.End.Time)
            {
                continue;
            }

            if (!MoveFxSprites.TryGet(fx.Sprite, out var tex, out _))
            {
                continue;
            }

            var span = fx.End.Time - fx.Start.Time;
            var lt = span <= 0f ? 1f : Math.Clamp((elapsedMs - fx.Start.Time) / span, 0f, 1f);
            var ev = Ease(fx.Ease, lt);
            var start = Resolve(fx.Start, attacker, defender, k);
            var end = Resolve(fx.End, attacker, defender, k);
            var pos = Vector2.Lerp(start, end, ev);
            if (fx.Ease is AnimEase.Ballistic or AnimEase.BallisticUp or AnimEase.BallisticDown)
            {
                pos.Y -= Vector2.Distance(start, end) * 0.22f * MathF.Sin(lt * MathF.PI);
            }

            var sc = MathF.Max(0f, Lerp(fx.Start.Scale, fx.End.Scale, ev));
            var op = Math.Clamp(Lerp(fx.Start.Opacity, fx.End.Opacity, ev), 0f, 1f);
            var zOff = Lerp(fx.Start.Z.Off, fx.End.Z.Off, ev);
            var zf = Math.Clamp(1f - zOff * 0.004f, 0.55f, 1.6f);
            var spriteScale = Math.Clamp(effectScale, 0.75f, 2f);
            var hw = Math.Clamp(fx.W * sc * k * zf * spriteScale * 0.5f, 3f, 260f);
            var hh = Math.Clamp(fx.H * sc * k * zf * spriteScale * 0.5f, 3f, 260f);
            var color = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, op));
            dl.AddImage(tex, pos - new Vector2(hw, hh), pos + new Vector2(hw, hh), Vector2.Zero, Vector2.One, color);
        }
    }

    private static Vector2 Resolve(AnimState s, Vector2 att, Vector2 def, float k)
    {
        var x = AnchorX(s.X.Anchor, att, def) + s.X.Off * k;
        var y = AnchorY(s.Y.Anchor, att, def) - s.Y.Off * k + s.Z.Off * k * 0.2f; // Showdown +y is up
        return new Vector2(x, y);
    }

    private static float AnchorX(AnimBase b, Vector2 att, Vector2 def) => b switch
    {
        AnimBase.Attacker => att.X,
        AnimBase.Defender => def.X,
        _ => (att.X + def.X) * 0.5f,
    };

    private static float AnchorY(AnimBase b, Vector2 att, Vector2 def) => b switch
    {
        AnimBase.Attacker => att.Y,
        AnimBase.Defender => def.Y,
        _ => (att.Y + def.Y) * 0.5f,
    };

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Ease(AnimEase e, float t) => e switch
    {
        AnimEase.Accel => t * t,
        AnimEase.Decel => 1f - (1f - t) * (1f - t),
        AnimEase.Swing => (1f - MathF.Cos(t * MathF.PI)) * 0.5f,
        _ => t, // Linear + ballistics (position is linear; the arc is added separately)
    };
}
