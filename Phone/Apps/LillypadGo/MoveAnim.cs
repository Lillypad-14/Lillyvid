using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using VideoSyncPrototype.Phone.Core;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// Playback engine for Pokémon Showdown's move animations. The choreography is traced from
// Showdown's own animation code (battle-animations-moves.js, CC0-1.0; engine MIT) by
// tools/trace_anims.js into Assets/pokemon/moveanims.json: per move and per orientation
// (player attacking vs wild attacking) a list of fx-sprite keyframe segments, creature-sprite
// movement segments, background flashes, and screen-shake tracks, all in Showdown scene units.
//
// This file replicates the relevant parts of Showdown's BattleScene rendering model:
//  - pos(): perspective projection where z:0 (near mon, bottom-left) -> z:200 (far mon,
//    top-right), scale(z) = 1.5 - 0.5*z/200, anchors (210,245) near and (430,135) far.
//  - posT(): easing applied per projected CSS property (left/top/width/height/opacity),
//    with ballistic arcs implemented as overshooting eases on `top` only.
//  - animateEffect(): 'fade' (100ms) and 'explode' (3x scale, 200ms) finishers.
// A SceneMap affine-maps the two Showdown anchors onto the actual on-screen creature
// positions, so all effects stay anchored to the battlers at any layout size.

// Transition codes (must match trace_anims.js TR): 0 linear, 1 ballistic, 2 ballisticUnder,
// 3 ballistic2, 4 ballistic2Back, 5 ballistic2Under, 6 swing, 7 accel, 8 decel.
// After codes: 0 none, 1 fade, 2 explode.

internal readonly struct AnimLoc
{
    public AnimLoc(float x, float y, float z, float xs, float ys, float opacity, float time)
    {
        X = x;
        Y = y;
        Z = z;
        XS = xs;
        YS = ys;
        Opacity = opacity;
        Time = time;
    }

    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public float XS { get; }
    public float YS { get; }
    public float Opacity { get; }
    public float Time { get; }
}

internal sealed class AnimFxSeg
{
    public string Sprite = string.Empty;
    public byte Tr;
    public byte After;
    public AnimLoc A;
    public AnimLoc B;
}

internal sealed class AnimMonSeg
{
    public float T0;
    public float T1;
    public AnimLoc From;
    public AnimLoc To;
    public byte Tr;
}

internal sealed class AnimBgFx
{
    public uint C0;
    public uint C1;
    public string? Img;
    public float Delay;
    public float Dur;
    public float Opacity;
}

internal sealed class AnimShakeSeg
{
    public float T0;
    public float T1;
    public float Y0;
    public float Y1;
}

internal sealed class MoveAnimVariant
{
    public float DurationMs;
    public AnimFxSeg[] Fx = Array.Empty<AnimFxSeg>();
    public AnimMonSeg[] AttMon = Array.Empty<AnimMonSeg>();
    public AnimMonSeg[] DefMon = Array.Empty<AnimMonSeg>();
    public AnimBgFx[] Bg = Array.Empty<AnimBgFx>();
    public AnimShakeSeg[] Shake = Array.Empty<AnimShakeSeg>();
}

internal sealed class MoveAnimEntry
{
    public MoveAnimVariant Near = new();
    public MoveAnimVariant Far = new();
}

// An anim selected for playback: the orientation variant plus which mon is the attacker.
internal readonly struct MoveAnimPlayback
{
    public MoveAnimPlayback(MoveAnimVariant variant, bool fromPlayer)
    {
        Variant = variant;
        FromPlayer = fromPlayer;
    }

    public MoveAnimVariant Variant { get; }
    public bool FromPlayer { get; }
    public float DurationMs => Variant.DurationMs;
}

// Affine map from Showdown scene pixels onto the battle screen, anchored so that the near
// anchor (210,245) lands on the player's mon and the far anchor (430,135) on the wild mon.
internal readonly struct SceneMap
{
    public SceneMap(Vector2 playerPos, Vector2 wildPos)
    {
        Player = playerPos;
        Sx = (wildPos.X - playerPos.X) / 220f;
        Sy = (wildPos.Y - playerPos.Y) / -110f;
        SUniform = (MathF.Abs(Sx) + MathF.Abs(Sy)) * 0.5f;
    }

    public Vector2 Player { get; }
    public float Sx { get; }
    public float Sy { get; }
    public float SUniform { get; } // uniform pixel factor for effect sizes

    public Vector2 Map(float left, float top) =>
        new(Player.X + (left - 210f) * Sx, Player.Y + (top - 245f) * Sy);
}

// Resolves the '$attacker'/'$defender' pseudo effect sprites to the creatures' current
// spritesheet frames (used by Double Team, Quick Attack, and other self-copy effects).
internal delegate bool MonFrameResolver(bool attackerSprite, out ImTextureID tex, out Vector2 uv0, out Vector2 uv1);

internal static class MoveAnims
{
    private sealed class FxMeta
    {
        public float W;
        public float H;
        public float YOff;
    }

    private static readonly object Gate = new();
    private static Dictionary<string, MoveAnimEntry>? moves;
    private static Dictionary<string, FxMeta> fxMeta = new(StringComparer.Ordinal);
    private static bool loadStarted;

    public static void Preload()
    {
        lock (Gate)
        {
            if (loadStarted)
            {
                return;
            }

            loadStarted = true;
        }

        _ = Task.Run(Load);
    }

    private static void Load()
    {
        try
        {
            var path = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty,
                "Assets", "pokemon", "moveanims.json");
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;

            var meta = new Dictionary<string, FxMeta>(StringComparer.Ordinal);
            foreach (var p in root.GetProperty("sprites").EnumerateObject())
            {
                meta[p.Name] = new FxMeta
                {
                    W = p.Value.GetProperty("w").GetSingle(),
                    H = p.Value.GetProperty("h").GetSingle(),
                    YOff = p.Value.GetProperty("y").GetSingle(),
                };
            }

            var parsed = new Dictionary<string, MoveAnimEntry>(StringComparer.Ordinal);
            var aliases = new List<(string Id, string Target)>();
            foreach (var p in root.GetProperty("moves").EnumerateObject())
            {
                if (p.Value.TryGetProperty("alias", out var alias))
                {
                    aliases.Add((p.Name, alias.GetString() ?? "tackle"));
                    continue;
                }

                parsed[p.Name] = new MoveAnimEntry
                {
                    Near = ParseVariant(p.Value.GetProperty("n")),
                    Far = ParseVariant(p.Value.GetProperty("f")),
                };
            }

            foreach (var (id, target) in aliases)
            {
                if (parsed.TryGetValue(target, out var entry))
                {
                    parsed[id] = entry;
                }
            }

            fxMeta = meta;
            moves = parsed;
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[LillypadGo] failed to load moveanims.json: {exception.Message}");
            moves = new Dictionary<string, MoveAnimEntry>(StringComparer.Ordinal);
        }
    }

    private static MoveAnimVariant ParseVariant(JsonElement v)
    {
        var fx = new List<AnimFxSeg>();
        foreach (var e in v.GetProperty("fx").EnumerateArray())
        {
            fx.Add(new AnimFxSeg
            {
                Sprite = e.GetProperty("s").GetString() ?? string.Empty,
                Tr = (byte)e.GetProperty("tr").GetInt32(),
                After = (byte)e.GetProperty("af").GetInt32(),
                A = ReadLoc(e.GetProperty("a")),
                B = ReadLoc(e.GetProperty("b")),
            });
        }

        var bg = new List<AnimBgFx>();
        foreach (var e in v.GetProperty("bg").EnumerateArray())
        {
            bg.Add(new AnimBgFx
            {
                C0 = ParseHex(e.GetProperty("c0").GetString()),
                C1 = ParseHex(e.GetProperty("c1").GetString()),
                Img = e.GetProperty("img").ValueKind == JsonValueKind.String ? e.GetProperty("img").GetString() : null,
                Delay = e.GetProperty("delay").GetSingle(),
                Dur = e.GetProperty("dur").GetSingle(),
                Opacity = e.GetProperty("o").GetSingle(),
            });
        }

        var shake = new List<AnimShakeSeg>();
        foreach (var e in v.GetProperty("sh").EnumerateArray())
        {
            shake.Add(new AnimShakeSeg
            {
                T0 = e[0].GetSingle(),
                T1 = e[1].GetSingle(),
                Y0 = e[2].GetSingle(),
                Y1 = e[3].GetSingle(),
            });
        }

        return new MoveAnimVariant
        {
            DurationMs = v.GetProperty("d").GetSingle(),
            Fx = fx.ToArray(),
            AttMon = ReadMonSegs(v.GetProperty("am")),
            DefMon = ReadMonSegs(v.GetProperty("dm")),
            Bg = bg.ToArray(),
            Shake = shake.ToArray(),
        };
    }

    private static AnimLoc ReadLoc(JsonElement a) => new(
        a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle(),
        a[3].GetSingle(), a[4].GetSingle(), a[5].GetSingle(), a[6].GetSingle());

    private static AnimMonSeg[] ReadMonSegs(JsonElement arr)
    {
        var segs = new List<AnimMonSeg>();
        foreach (var e in arr.EnumerateArray())
        {
            segs.Add(new AnimMonSeg
            {
                T0 = e[0].GetSingle(),
                T1 = e[1].GetSingle(),
                From = new AnimLoc(e[2].GetSingle(), e[3].GetSingle(), e[4].GetSingle(),
                    e[5].GetSingle(), e[6].GetSingle(), e[7].GetSingle(), 0f),
                To = new AnimLoc(e[8].GetSingle(), e[9].GetSingle(), e[10].GetSingle(),
                    e[11].GetSingle(), e[12].GetSingle(), e[13].GetSingle(), 0f),
                Tr = (byte)e[14].GetInt32(),
            });
        }

        return segs.ToArray();
    }

    private static uint ParseHex(string? hex)
    {
        if (hex is null || hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return 0;
        }

        return rgb;
    }

    // ---- Lookup ----

    public static bool TryGet(string moveName, bool fromPlayer, out MoveAnimPlayback playback)
    {
        playback = default;
        var table = moves;
        if (table is null)
        {
            Preload();
            return false;
        }

        Span<char> id = stackalloc char[moveName.Length];
        var n = 0;
        foreach (var c in moveName)
        {
            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                id[n++] = lower;
            }
        }

        if (!table.TryGetValue(new string(id[..n]), out var entry))
        {
            return false;
        }

        playback = new MoveAnimPlayback(fromPlayer ? entry.Near : entry.Far, fromPlayer);
        return true;
    }

    // ---- Projection (Showdown BattleScene.pos) ----

    private readonly struct Proj
    {
        public Proj(float left, float top, float w, float h, float opacity)
        {
            Left = left;
            Top = top;
            W = w;
            H = h;
            Opacity = opacity;
        }

        public float Left { get; }
        public float Top { get; }
        public float W { get; }
        public float H { get; }
        public float Opacity { get; }
    }

    private static Proj Project(in AnimLoc l, float objW, float objH, float objY, float xsMul = 1f, float ysMul = 1f)
    {
        var scale = 1.5f - 0.5f * (l.Z / 200f);
        if (scale < 0.1f)
        {
            scale = 0.1f;
        }

        var anchorX = 210f + 220f * (l.Z / 200f) + l.X * scale;
        var anchorY = 245f - 110f * (l.Z / 200f) - l.Y * scale;
        var w = objW * scale * l.XS * xsMul;
        var h = objH * scale * l.YS * ysMul;
        var hoff = (objH - objY * 2f) * scale * l.YS * ysMul;
        return new Proj(anchorX - w / 2f, anchorY - hoff / 2f, w, h, l.Opacity);
    }

    // ---- Easing (Showdown's jQuery easing extensions) ----

    private const byte EaseLinear = 0;
    private const byte EaseSwing = 1;
    private const byte EaseQuadUp = 2;
    private const byte EaseQuadDown = 3;
    private const byte EaseBallisticUp = 4;
    private const byte EaseBallisticDown = 5;

    private static float Ease(byte e, float t) => e switch
    {
        EaseSwing => 0.5f - MathF.Cos(t * MathF.PI) * 0.5f,
        EaseQuadUp => 1f - (1f - t) * (1f - t),
        EaseQuadDown => t * t,
        EaseBallisticUp => -3f * t * t + 4f * t,
        EaseBallisticDown => 1f - (-3f * (1f - t) * (1f - t) + 4f * (1f - t)),
        _ => t,
    };

    // Per-property easing per Showdown's posT. `topEndBelowRef` = projected end top greater
    // than the reference top (reference: the start state for effects, the base pos for mons).
    private static (byte Pos, byte Size, byte Top) TransitionEases(byte tr, bool topEndBelowRef, float endZ)
    {
        return tr switch
        {
            1 => (EaseLinear, EaseLinear, topEndBelowRef ? EaseBallisticDown : EaseBallisticUp), // ballistic
            2 => (EaseLinear, EaseLinear, topEndBelowRef ? EaseBallisticUp : EaseBallisticDown), // ballisticUnder
            3 => (EaseLinear, EaseLinear, topEndBelowRef ? EaseQuadDown : EaseQuadUp), // ballistic2
            4 => (EaseLinear, EaseLinear, endZ > 0f ? EaseQuadUp : EaseQuadDown), // ballistic2Back
            5 => (EaseLinear, EaseLinear, topEndBelowRef ? EaseQuadUp : EaseQuadDown), // ballistic2Under
            6 => (EaseSwing, EaseSwing, EaseSwing), // swing
            7 => (EaseQuadDown, EaseQuadDown, EaseQuadDown), // accel
            8 => (EaseQuadUp, EaseQuadUp, EaseQuadUp), // decel
            _ => (EaseLinear, EaseLinear, EaseLinear),
        };
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // ---- Background flashes (drawn behind the creatures) ----

    public static void DrawBackground(ImDrawListPtr dl, in MoveAnimPlayback playback, float ms, Rect arena)
    {
        foreach (var bg in playback.Variant.Bg)
        {
            var t = ms - bg.Delay;
            if (t < 0f || t > bg.Dur + 250f)
            {
                continue;
            }

            // Showdown: 250ms fade in, hold, 250ms fade out (jQuery default swing easing).
            float alpha;
            if (t < 250f)
            {
                alpha = bg.Opacity * Ease(EaseSwing, t / 250f);
            }
            else if (t <= bg.Dur)
            {
                alpha = bg.Opacity;
            }
            else
            {
                alpha = bg.Opacity * (1f - Ease(EaseSwing, (t - bg.Dur) / 250f));
            }

            alpha = Math.Clamp(alpha, 0f, 1f);
            if (alpha <= 0f)
            {
                continue;
            }

            if (bg.Img is not null && MoveFxSprites.TryGet(bg.Img, out var tex, out _))
            {
                dl.AddImage(tex, arena.Min, arena.Max, Vector2.Zero, Vector2.One,
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)));
                continue;
            }

            var c0 = ImGui.GetColorU32(new Vector4(((bg.C0 >> 16) & 0xFF) / 255f, ((bg.C0 >> 8) & 0xFF) / 255f,
                (bg.C0 & 0xFF) / 255f, alpha));
            var c1 = ImGui.GetColorU32(new Vector4(((bg.C1 >> 16) & 0xFF) / 255f, ((bg.C1 >> 8) & 0xFF) / 255f,
                (bg.C1 & 0xFF) / 255f, alpha));
            dl.AddRectFilledMultiColor(arena.Min, arena.Max, c0, c0, c1, c1);
        }
    }

    // ---- Screen shake (Showdown jiggles the field bg; we jiggle the whole arena) ----

    public static float ShakeY(in MoveAnimPlayback playback, float ms)
    {
        var y = 0f;
        foreach (var seg in playback.Variant.Shake)
        {
            if (ms >= seg.T1)
            {
                y = seg.Y1;
            }
            else if (ms >= seg.T0)
            {
                var span = MathF.Max(1f, seg.T1 - seg.T0);
                y = Lerp(seg.Y0, seg.Y1, (ms - seg.T0) / span);
                break;
            }
            else
            {
                break;
            }
        }

        return y;
    }

    // ---- Creature sprite movement ----

    public readonly struct MonPoseState
    {
        public MonPoseState(Vector2 offset, float scaleMul, float alpha)
        {
            Offset = offset;
            ScaleMul = scaleMul;
            Alpha = alpha;
        }

        public Vector2 Offset { get; }
        public float ScaleMul { get; }
        public float Alpha { get; }
    }

    // The attacker flag refers to the anim role; the caller decides which on-screen mon that is.
    public static MonPoseState MonPose(in MoveAnimPlayback playback, float ms, bool attacker, in SceneMap map)
    {
        var segs = attacker ? playback.Variant.AttMon : playback.Variant.DefMon;
        // Base: near mon (z=0) for (attacker==fromPlayer... ) — the trace stores absolute coords,
        // so the base is simply the first segment's From when idle, else derive from role.
        var isNearMon = attacker == playback.FromPlayer;
        var baseLoc = new AnimLoc(0f, 0f, isNearMon ? 0f : 200f, 1f, 1f, 1f, 0f);
        if (segs.Length == 0)
        {
            return new MonPoseState(Vector2.Zero, 1f, 1f);
        }

        var baseProj = Project(baseLoc, 96f, 96f, 0f);
        var state = InterpolateMon(segs, ms, baseProj);
        var baseCenter = map.Map(baseProj.Left + baseProj.W / 2f, baseProj.Top + baseProj.H / 2f);
        var center = map.Map(state.Left + state.W / 2f, state.Top + state.H / 2f);
        var scaleMul = baseProj.W > 0f ? state.W / baseProj.W : 1f;
        return new MonPoseState(center - baseCenter, MathF.Max(0f, scaleMul), Math.Clamp(state.Opacity, 0f, 1f));
    }

    private static Proj InterpolateMon(AnimMonSeg[] segs, float ms, in Proj baseProj)
    {
        var last = Project(segs[0].From, 96f, 96f, 0f);
        foreach (var seg in segs)
        {
            if (ms < seg.T0)
            {
                return last;
            }

            var from = Project(seg.From, 96f, 96f, 0f);
            var to = Project(seg.To, 96f, 96f, 0f);
            if (ms <= seg.T1)
            {
                var span = MathF.Max(1f, seg.T1 - seg.T0);
                var lt = Math.Clamp((ms - seg.T0) / span, 0f, 1f);
                // Sprite.anim compares the end top against the sprite's BASE pos top.
                var eases = TransitionEases(seg.Tr, to.Top > baseProj.Top, seg.To.Z);
                return LerpProj(from, to, lt, eases);
            }

            last = to;
        }

        return last;
    }

    private static Proj LerpProj(in Proj a, in Proj b, float lt, (byte Pos, byte Size, byte Top) eases)
    {
        var left = Lerp(a.Left, b.Left, Ease(eases.Pos, lt));
        var top = Lerp(a.Top, b.Top, Ease(eases.Top, lt));
        var w = Lerp(a.W, b.W, Ease(eases.Size, lt));
        var h = Lerp(a.H, b.H, Ease(eases.Size, lt));
        var o = Lerp(a.Opacity, b.Opacity, lt);
        return new Proj(left, top, w, h, o);
    }

    // ---- Effects (drawn above the creatures, like Showdown's $fx layer) ----

    public static void DrawEffects(ImDrawListPtr dl, in MoveAnimPlayback playback, float ms, in SceneMap map,
        float effectScale, MonFrameResolver? monFrames)
    {
        foreach (var seg in playback.Variant.Fx)
        {
            if (ms < seg.A.Time)
            {
                continue;
            }

            float objW = 40f, objH = 40f, objY = 0f;
            var isMon = seg.Sprite.Length > 0 && seg.Sprite[0] == '$';
            if (!isMon)
            {
                if (!fxMeta.TryGetValue(seg.Sprite, out var meta))
                {
                    continue;
                }

                objW = meta.W;
                objH = meta.H;
                objY = meta.YOff;
            }
            else
            {
                objW = objH = 96f;
            }

            var a = Project(seg.A, objW, objH, objY);
            var b = Project(seg.B, objW, objH, objY);
            Proj cur;
            var afterDur = seg.After switch { 1 => 100f, 2 => 200f, _ => 0f };
            if (ms <= seg.B.Time)
            {
                var span = seg.B.Time - seg.A.Time;
                var lt = span <= 0f ? 1f : Math.Clamp((ms - seg.A.Time) / span, 0f, 1f);
                var eases = TransitionEases(seg.Tr, b.Top > a.Top, seg.B.Z);
                cur = LerpProj(a, b, lt, eases);
            }
            else if (afterDur > 0f && ms <= seg.B.Time + afterDur)
            {
                // fade: opacity -> 0; explode: 3x scale + opacity -> 0. jQuery default swing.
                var lt = Math.Clamp((ms - seg.B.Time) / afterDur, 0f, 1f);
                var swung = Ease(EaseSwing, lt);
                if (seg.After == 1)
                {
                    cur = new Proj(b.Left, b.Top, b.W, b.H, Lerp(b.Opacity, 0f, swung));
                }
                else
                {
                    var b3 = Project(seg.B, objW, objH, objY, 3f, 3f);
                    cur = new Proj(
                        Lerp(b.Left, b3.Left, swung),
                        Lerp(b.Top, b3.Top, swung),
                        Lerp(b.W, b3.W, swung),
                        Lerp(b.H, b3.H, swung),
                        Lerp(b.Opacity, 0f, swung));
                }
            }
            else if (afterDur > 0f)
            {
                continue; // faded/exploded out
            }
            else
            {
                cur = b; // effects with no finisher hold their end state until the anim ends
            }

            var alpha = Math.Clamp(cur.Opacity, 0f, 1f);
            if (alpha <= 0.004f || cur.W <= 0.01f || cur.H <= 0.01f)
            {
                continue;
            }

            ImTextureID tex;
            Vector2 uv0 = Vector2.Zero, uv1 = Vector2.One;
            if (isMon)
            {
                if (monFrames is null || !monFrames(seg.Sprite == "$attacker", out tex, out uv0, out uv1))
                {
                    continue;
                }
            }
            else if (!MoveFxSprites.TryGet(seg.Sprite, out tex, out _))
            {
                continue;
            }

            var center = map.Map(cur.Left + cur.W / 2f, cur.Top + cur.H / 2f);
            var half = new Vector2(cur.W, cur.H) * (0.5f * map.SUniform * effectScale);
            dl.AddImage(tex, center - half, center + half, uv0, uv1,
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)));
        }
    }
}
