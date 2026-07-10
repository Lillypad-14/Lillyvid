using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using VideoSyncPrototype.Phone.Apps.Games.Framework;
using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Core.Animation;
using VideoSyncPrototype.Phone.Core.Apps;
using VideoSyncPrototype.Phone.Core.Theme;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal sealed partial class LillypadGoApp
{
    // ---- Battle: floating popups + hit/move visual effects ----

    private static void DrawFieldEffects(ImDrawListPtr drawList, Rect arena, BattleWeather weather,
        BattleTerrain terrain, float clock, float scale)
    {
        DrawTerrainOverlay(drawList, arena, terrain, clock, scale);
        switch (weather)
        {
            case BattleWeather.Sun:
                DrawSunOverlay(drawList, arena, clock, scale);
                break;
            case BattleWeather.Rain:
                DrawRainOverlay(drawList, arena, clock, scale);
                break;
            case BattleWeather.Sandstorm:
                DrawSandOverlay(drawList, arena, clock, scale);
                break;
            case BattleWeather.Snow:
                DrawSnowOverlay(drawList, arena, clock, scale);
                break;
        }
    }

    // Draws the capture ball at its current position, with an opening flash, wobble rotation, and —
    // on a successful catch — a click ring and expanding sparkles.
    private void DrawCaptureBall(ImDrawListPtr dl, Vector2 pos, float angle, float flash, CaptureFx cap, float scale)
    {
        var size = 24f * scale;

        // Soft ground shadow while the ball rests.
        if (cap.Phase is CaptureFx.Stage.Wait or CaptureFx.Stage.Success)
        {
            dl.AddCircleFilled(pos + new Vector2(0f, size * 0.6f), size * 0.55f,
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.25f)));
        }

        // Opening flash at the hit / burst on break free.
        if (flash > 0.01f)
        {
            dl.AddCircleFilled(pos, size * (0.5f + flash * 1.3f),
                ImGui.GetColorU32(new Vector4(1f, 1f, 0.92f, flash * 0.65f)));
        }

        // Successful catch: a click ring and a ring of sparkles.
        if (cap.Phase == CaptureFx.Stage.Success)
        {
            var t = cap.StageAge;
            if (t < 0.35f)
            {
                dl.AddCircle(pos, size * (0.7f + t * 5f),
                    ImGui.GetColorU32(new Vector4(1f, 0.9f, 0.45f, Math.Clamp(1f - t / 0.35f, 0f, 1f))), 24,
                    2f * scale);
            }

            for (var i = 0; i < 4; i++)
            {
                var a = t * 2.5f + i * MathF.PI * 0.5f;
                var r = (10f + t * 26f) * scale;
                var sp = pos + new Vector2(MathF.Cos(a) * r, MathF.Sin(a) * r - 8f * scale);
                dl.AddCircleFilled(sp, 2.2f * scale,
                    ImGui.GetColorU32(new Vector4(1f, 0.95f, 0.55f, Math.Clamp(1f - t / 0.8f, 0f, 1f))));
            }
        }

        if (AssetTextures.TryGet($"items/{cap.BallId}.png", out var tex, out var aspect))
        {
            DrawRotatedImage(dl, tex, pos, new Vector2(size * MathF.Max(0.3f, aspect), size), angle);
        }
        else
        {
            dl.AddCircleFilled(pos, size * 0.5f, ImGui.GetColorU32(new Vector4(0.86f, 0.22f, 0.22f, 1f)));
        }
    }

    // Trainer/gym send-out ball: it lands on the opponent's side, opens in a bright flash, then
    // hands the field back to the released creature.
    private static void DrawSendOutBall(ImDrawListPtr dl, Vector2 pos, float angle, float flash, float scale)
    {
        var size = 26f * scale;
        if (flash > 0.01f)
        {
            dl.AddCircleFilled(pos, size * (0.55f + flash * 1.35f),
                ImGui.GetColorU32(new Vector4(0.95f, 0.98f, 1f, flash * 0.72f)));
            dl.AddCircle(pos, size * (0.75f + flash * 1.8f),
                ImGui.GetColorU32(new Vector4(0.55f, 0.86f, 1f, flash * 0.8f)), 24, 2f * scale);
        }

        dl.AddCircleFilled(pos + new Vector2(0f, size * 0.62f), size * 0.52f,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.22f)));
        if (AssetTextures.TryGet("items/pokeball.png", out var tex, out var aspect))
        {
            DrawRotatedImage(dl, tex, pos, new Vector2(size * MathF.Max(0.3f, aspect), size), angle);
        }
        else
        {
            dl.AddCircleFilled(pos, size * 0.5f, ImGui.GetColorU32(new Vector4(0.86f, 0.22f, 0.22f, 1f)));
            dl.AddLine(pos - new Vector2(size * 0.46f, 0f), pos + new Vector2(size * 0.46f, 0f),
                ImGui.GetColorU32(new Vector4(0.08f, 0.1f, 0.14f, 1f)), 2f * scale);
            dl.AddCircleFilled(pos, size * 0.14f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)));
        }
    }

    private static void DrawRotatedImage(ImDrawListPtr dl, ImTextureID tex, Vector2 center, Vector2 size, float angle)
    {
        var c = MathF.Cos(angle);
        var s = MathF.Sin(angle);
        var hx = size.X * 0.5f;
        var hy = size.Y * 0.5f;
        Vector2 R(float x, float y) => center + new Vector2(x * c - y * s, x * s + y * c);
        dl.AddImageQuad(tex, R(-hx, -hy), R(hx, -hy), R(hx, hy), R(-hx, hy),
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            ImGui.GetColorU32(Vector4.One));
    }

    private static void DrawTerrainOverlay(ImDrawListPtr dl, Rect arena, BattleTerrain terrain, float clock,
        float scale)
    {
        if (terrain == BattleTerrain.None)
        {
            return;
        }

        var tone = terrain switch
        {
            BattleTerrain.Electric => Elements.Color(Element.Electric),
            BattleTerrain.Grassy => Elements.Color(Element.Grass),
            BattleTerrain.Misty => Elements.Color(Element.Fairy),
            BattleTerrain.Psychic => Elements.Color(Element.Psychic),
            _ => new Vector4(1f),
        };
        var horizon = arena.Min.Y + arena.Height * 0.58f;
        var floor = new Rect(new Vector2(arena.Min.X, horizon), arena.Max);
        dl.AddRectFilled(floor.Min, floor.Max, ImGui.GetColorU32(tone with { W = 0.1f }));
        for (var i = 0; i < 8; i++)
        {
            var y = horizon + (i + 1) * floor.Height / 9f;
            var pulse = 0.35f + 0.2f * MathF.Sin(clock * 2.4f + i);
            dl.AddLine(new Vector2(arena.Min.X + 18f * scale, y),
                new Vector2(arena.Max.X - 18f * scale, y), ImGui.GetColorU32(tone with { W = pulse * 0.22f }),
                1.2f * scale);
        }
    }

    private static void DrawSunOverlay(ImDrawListPtr dl, Rect arena, float clock, float scale)
    {
        // Warm golden wash + a radiant sun disc with pulsing rings and a slow ray sweep.
        dl.AddRectFilled(arena.Min, arena.Max, ImGui.GetColorU32(new Vector4(1f, 0.68f, 0.18f, 0.12f)));
        var center = arena.Min + new Vector2(arena.Width * 0.8f, arena.Height * 0.15f);
        dl.AddCircleFilled(center, 16f * scale, ImGui.GetColorU32(new Vector4(1f, 0.86f, 0.36f, 0.5f)));
        for (var i = 0; i < 4; i++)
        {
            var radius = (24f + i * 16f + MathF.Sin(clock * 1.2f + i) * 4f) * scale;
            dl.AddCircle(center, radius, ImGui.GetColorU32(new Vector4(1f, 0.8f, 0.24f, 0.22f - i * 0.045f)),
                40, 2f * scale);
        }

        for (var i = 0; i < 8; i++)
        {
            var ang = clock * 0.15f + i * MathF.PI / 4f;
            var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
            dl.AddLine(center + dir * 20f * scale, center + dir * 44f * scale,
                ImGui.GetColorU32(new Vector4(1f, 0.82f, 0.3f, 0.26f)), 2f * scale);
        }
    }

    private static void DrawRainOverlay(ImDrawListPtr dl, Rect arena, float clock, float scale)
    {
        // Cool darkening wash, dense wind-blown streaks, and the odd lightning flash — Showdown rain.
        dl.AddRectFilled(arena.Min, arena.Max, ImGui.GetColorU32(new Vector4(0.16f, 0.24f, 0.44f, 0.14f)));
        var rain = Elements.Color(Element.Water);
        for (var i = 0; i < 70; i++)
        {
            var seed = i * 37.91f;
            var x = arena.Min.X + ((seed * 19f) % arena.Width);
            var speed = 300f + (i % 5) * 40f;
            var y = arena.Min.Y + ((seed * 7f + clock * speed * scale) % (arena.Height + 40f * scale)) -
                40f * scale;
            var len = (20f + i % 3 * 6f) * scale;
            dl.AddLine(new Vector2(x, y), new Vector2(x - len * 0.42f, y + len),
                ImGui.GetColorU32(rain with { W = 0.30f + (i % 3) * 0.05f }), 1.3f * scale);
        }

        var flash = MathF.Sin(clock * 0.7f);
        if (flash > 0.985f)
        {
            dl.AddRectFilled(arena.Min, arena.Max,
                ImGui.GetColorU32(new Vector4(0.8f, 0.86f, 1f, (flash - 0.985f) * 18f)));
        }
    }

    private static void DrawSandOverlay(ImDrawListPtr dl, Rect arena, float clock, float scale)
    {
        // Showdown's sandstorm is a tan haze that streams sideways: a warm wash, a few translucent
        // scrolling bands, wind streaks, then fine fast grains — all confined to the arena.
        var sand = new Vector4(0.80f, 0.58f, 0.31f, 1f);
        dl.AddRectFilled(arena.Min, arena.Max, ImGui.GetColorU32(sand with { W = 0.16f }));
        for (var i = 0; i < 4; i++)
        {
            var h = arena.Height * (0.26f + i * 0.03f);
            var y = arena.Min.Y + (((i * 0.27f + clock * 0.05f) % 1f) * (arena.Height + h)) - h;
            dl.AddRectFilled(new Vector2(arena.Min.X, y), new Vector2(arena.Max.X, y + h),
                ImGui.GetColorU32(sand with { W = 0.05f }));
        }

        for (var i = 0; i < 30; i++)
        {
            var seed = i * 61.3f;
            var y = arena.Min.Y + (seed * 13f) % arena.Height;
            var x = arena.Min.X + ((seed * 9f + clock * 460f * scale) % (arena.Width + 80f * scale)) - 80f * scale;
            var len = (30f + i % 4 * 12f) * scale;
            dl.AddLine(new Vector2(x, y), new Vector2(x + len, y + MathF.Sin(clock + i) * 2f * scale),
                ImGui.GetColorU32(sand with { W = 0.20f }), 1.5f * scale);
        }

        for (var i = 0; i < 48; i++)
        {
            var seed = i * 53.17f;
            var x = arena.Min.X + ((seed * 13f + clock * 320f * scale) % (arena.Width + 20f * scale)) - 10f * scale;
            var y = arena.Min.Y + ((seed * 5f + MathF.Sin(clock + i) * 18f) % arena.Height);
            dl.AddCircleFilled(new Vector2(x, y), (0.8f + i % 3) * scale, ImGui.GetColorU32(sand with { W = 0.22f }));
        }
    }

    private static void DrawSnowOverlay(ImDrawListPtr dl, Rect arena, float clock, float scale)
    {
        var ice = Elements.Color(Element.Ice);
        dl.AddRectFilled(arena.Min, arena.Max, ImGui.GetColorU32(new Vector4(0.72f, 0.9f, 1f, 0.1f)));
        for (var i = 0; i < 54; i++)
        {
            var seed = i * 29.3f;
            var sway = MathF.Sin(clock * (0.8f + (i % 3) * 0.2f) + i) * 16f * scale;
            var x = arena.Min.X + ((seed * 11f) % arena.Width) + sway;
            var y = arena.Min.Y + ((seed * 17f + clock * (60f + i % 4 * 16f) * scale) %
                (arena.Height + 12f * scale)) - 12f * scale;
            dl.AddCircleFilled(new Vector2(x, y), (1.4f + i % 3) * scale, ImGui.GetColorU32(ice with { W = 0.4f }));
        }
    }

    private void AddBattlePopup(bool onWild, int hpDelta, BattleMessage battleMessage)
    {
        if (hpDelta == 0)
        {
            return;
        }

        var healing = hpDelta > 0;
        var label = healing ? "HEAL" : battleMessage.Critical ? "CRITICAL" :
            battleMessage.Effectiveness > 1f ? "STRONG" :
            battleMessage.Effectiveness < 1f ? "RESISTED" : string.Empty;
        var color = healing ? new Vector4(0.38f, 0.92f, 0.52f, 1f) :
            battleMessage.Critical ? new Vector4(1f, 0.82f, 0.28f, 1f) :
            battleMessage.Effectiveness > 1f ? new Vector4(1f, 0.66f, 0.25f, 1f) :
            battleMessage.Effectiveness < 1f ? new Vector4(0.58f, 0.76f, 0.92f, 1f) :
            onWild ? new Vector4(1f, 0.95f, 0.86f, 1f) : new Vector4(1f, 0.48f, 0.42f, 1f);
        battlePopups.Add(new BattlePopup
        {
            OnWild = onWild,
            Value = hpDelta > 0 ? "+" + hpDelta : hpDelta.ToString(),
            Label = label,
            Color = color,
            HorizontalOffset = ((battlePopups.Count % 3) - 1) * 10f,
        });
    }

    private void UpdateBattlePopups(float dt)
    {
        for (var i = battlePopups.Count - 1; i >= 0; i--)
        {
            battlePopups[i].Age += dt;
            if (battlePopups[i].Age >= 1.15f)
            {
                battlePopups.RemoveAt(i);
            }
        }
    }

    private void DrawBattlePopups(Vector2 wildPos, Vector2 playerPos, PhoneTheme theme, float scale)
    {
        foreach (var popup in battlePopups)
        {
            var progress = Math.Clamp(popup.Age / 1.15f, 0f, 1f);
            var rise = (1f - MathF.Pow(1f - progress, 2f)) * 40f * scale;
            var fade = 1f - Math.Clamp((progress - 0.68f) / 0.32f, 0f, 1f);
            var pop = popup.Age < 0.12f
                ? 0.72f + popup.Age / 0.12f * 0.5f
                : 1.22f - Math.Clamp((popup.Age - 0.12f) / 0.28f, 0f, 1f) * 0.22f;
            var anchor = popup.OnWild ? wildPos : playerPos;
            var position = anchor + new Vector2(popup.HorizontalOffset * scale, -30f * scale - rise);
            var shadow = new Vector4(0f, 0f, 0f, 0.75f * fade);
            Typography.DrawCentered(position + new Vector2(1.5f * scale), popup.Value, shadow,
                TextStyles.Title2.Scale * pop, TextStyles.Title2.Weight);
            Typography.DrawCentered(position, popup.Value, popup.Color with { W = fade },
                TextStyles.Title2.Scale * pop, TextStyles.Title2.Weight);
            if (popup.Label.Length > 0)
            {
                Typography.DrawCentered(new Vector2(position.X, position.Y + 18f * scale), popup.Label,
                    theme.TextStrong with { W = fade * 0.88f }, TextStyles.Caption2);
            }
        }
    }

    private static void DrawImpactFx(ImDrawListPtr drawList, Vector2 center, float intensity, Vector4 color,
        float scale)
    {
        if (intensity <= 0f)
        {
            return;
        }

        var expansion = 1f - intensity;
        var inner = (18f + expansion * 10f) * scale;
        var outer = inner + (8f + intensity * 8f) * scale;
        var stroke = ImGui.GetColorU32(color with { W = intensity * 0.72f });
        drawList.AddCircle(center, inner, stroke, 28, (1f + intensity) * scale);
        for (var i = 0; i < 8; i++)
        {
            var angle = i * MathF.PI / 4f + expansion * 0.35f;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            drawList.AddLine(center + direction * inner, center + direction * outer, stroke,
                (1f + intensity * 0.8f) * scale);
        }
    }

    private static void DrawStatusFx(ImDrawListPtr drawList, Vector2 center, Status status, float clock, float scale)
    {
        if (status == Status.None)
        {
            return;
        }

        if (status == Status.Burn)
        {
            var fire = Elements.Color(Element.Fire);
            for (var i = 0; i < 6; i++)
            {
                var phase = (clock * 0.75f + i / 6f) % 1f;
                var angle = i * MathF.PI * 0.62f + clock * 0.35f;
                var x = MathF.Cos(angle) * (18f + i % 2 * 8f) * scale;
                var y = (18f - phase * 52f) * scale;
                var position = center + new Vector2(x, y);
                var radius = (3.5f + MathF.Sin(phase * MathF.PI) * 2.5f) * scale;
                var alpha = MathF.Sin(phase * MathF.PI) * 0.8f;
                drawList.AddCircleFilled(position, radius, ImGui.GetColorU32(fire with { W = alpha }));
                drawList.AddCircleFilled(position - new Vector2(0f, radius * 0.45f), radius * 0.45f,
                    ImGui.GetColorU32(new Vector4(1f, 0.82f, 0.28f, alpha * 0.9f)));
            }

            return;
        }

        if (status == Status.Freeze)
        {
            var ice = Elements.Color(Element.Ice);
            for (var i = 0; i < 6; i++)
            {
                var angle = i * MathF.PI / 3f + clock * 0.28f;
                var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                var position = center + direction * (28f + MathF.Sin(clock * 2f + i) * 4f) * scale;
                var side = new Vector2(-direction.Y, direction.X) * 3.5f * scale;
                drawList.AddTriangleFilled(position + direction * 7f * scale, position - direction * 5f * scale + side,
                    position - direction * 5f * scale - side, ImGui.GetColorU32(ice with { W = 0.72f }));
            }

            return;
        }

        if (status == Status.Sleep)
        {
            var psychic = Elements.Color(Element.Psychic);
            for (var i = 0; i < 4; i++)
            {
                var phase = (clock * 0.35f + i * 0.22f) % 1f;
                var position = center + new Vector2((18f + i * 8f) * scale, (-18f - phase * 34f) * scale);
                var alpha = MathF.Sin(phase * MathF.PI) * 0.85f;
                Typography.Draw(position, "Z", psychic with { W = alpha }, TextStyles.Caption1);
            }

            return;
        }

        if (status == Status.Poison)
        {
            var poison = Elements.Color(Element.Poison);
            for (var i = 0; i < 7; i++)
            {
                var phase = (clock * 0.45f + i * 0.17f) % 1f;
                var x = MathF.Sin(i * 2.1f + clock) * (16f + i % 3 * 5f) * scale;
                var position = center + new Vector2(x, (20f - phase * 48f) * scale);
                var radius = (2.5f + i % 2 * 1.5f) * scale;
                drawList.AddCircle(position, radius, ImGui.GetColorU32(poison with { W = 0.72f }), 12,
                    1.5f * scale);
            }

            return;
        }

        var lightning = Elements.Color(Element.Electric);
        for (var i = 0; i < 5; i++)
        {
            var angle = i * MathF.PI * 0.4f + clock * 1.8f;
            var origin = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 27f * scale;
            var middle = origin + new Vector2(i % 2 == 0 ? 5f : -5f, -8f) * scale;
            var end = middle + new Vector2(i % 2 == 0 ? -4f : 4f, -7f) * scale;
            var color = ImGui.GetColorU32(lightning with { W = 0.55f + MathF.Sin(clock * 7f + i) * 0.2f });
            drawList.AddLine(origin, middle, color, 2f * scale);
            drawList.AddLine(middle, end, color, 2f * scale);
        }
    }

    // Confusion: little stars orbiting an ellipse above the head (Showdown's "confused" cue).
    private static void DrawConfusionFx(ImDrawListPtr drawList, Vector2 center, float clock, float scale)
    {
        var pivot = center - new Vector2(0f, 36f * scale);
        var gold = new Vector4(1f, 0.86f, 0.32f, 1f);
        for (var i = 0; i < 3; i++)
        {
            var angle = clock * 3.4f + i * MathF.PI * 2f / 3f;
            var position = pivot + new Vector2(MathF.Cos(angle) * 19f * scale, MathF.Sin(angle) * 7.5f * scale);
            var depth = 0.55f + 0.45f * (0.5f + 0.5f * MathF.Sin(angle)); // dim on the far side
            DrawStar(drawList, position, 4.4f * scale, gold with { W = depth });
        }
    }

    private static void DrawStar(ImDrawListPtr drawList, Vector2 center, float radius, Vector4 color)
    {
        Span<Vector2> points = stackalloc Vector2[10];
        for (var i = 0; i < 10; i++)
        {
            var r = (i % 2 == 0) ? radius : radius * 0.44f;
            var angle = -MathF.PI / 2f + i * MathF.PI / 5f;
            points[i] = center + new Vector2(MathF.Cos(angle) * r, MathF.Sin(angle) * r);
        }

        var packed = ImGui.GetColorU32(color);
        for (var i = 0; i < 10; i++)
        {
            drawList.AddTriangleFilled(center, points[i], points[(i + 1) % 10], packed);
        }
    }

    private static void DrawGroundShadow(ImDrawListPtr drawList, Vector2 center, float width)
    {
        var height = width * 0.22f;
        Squircle.Fill(drawList, center - new Vector2(width, height) * 0.5f,
            center + new Vector2(width, height) * 0.5f, height * 0.5f,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.34f)));
    }

    // Starts a move's visual effect. Returns true when a traced Showdown animation drives it
    // (including the attacker's own lunge/movement), so the caller should skip the procedural
    // lunge pose; false means the legacy pattern fallback plays instead.
    private bool BeginMoveFx(BattleMessage battleMessage, bool fromPlayer)
    {
        if (battleMessage.Move is not { } move)
        {
            return false;
        }

        if (MoveAnims.TryGet(move.Name, fromPlayer, out var playback))
        {
            moveFx = new MoveFx
            {
                Move = move,
                FromPlayer = fromPlayer,
                Playback = playback,
                Duration = Math.Clamp(playback.DurationMs / 1000f + 0.15f, 0.4f, 3f),
            };
            return true;
        }

        moveFx = new MoveFx { Move = move, FromPlayer = fromPlayer, Duration = 0.9f };
        return false;
    }

    // Background flashes (Night Shade's darkness, Solar Beam's sun, …) sit behind the
    // creatures, matching Showdown's $bgEffect layer.
    private void DrawMoveBgFx(ImDrawListPtr drawList, Rect arena)
    {
        if (moveFx is { Playback: { } playback } fx)
        {
            MoveAnims.DrawBackground(drawList, playback, fx.Age * 1000f, arena);
        }
    }

    // Screen-shake offset (Earthquake and friends) in screen pixels for this frame.
    private float MoveFxShakeY(in SceneMap map)
    {
        if (moveFx is { Playback: { } playback } fx)
        {
            return MoveAnims.ShakeY(playback, fx.Age * 1000f) * map.Sy;
        }

        return 0f;
    }

    // Where the anim currently wants a creature: `nearMon` selects the player's battler.
    private MoveAnims.MonPoseState MonAnimPose(bool nearMon, in SceneMap map)
    {
        if (moveFx is { Playback: { } playback } fx)
        {
            var attackerRole = nearMon == fx.FromPlayer;
            return MoveAnims.MonPose(playback, fx.Age * 1000f, attackerRole, map);
        }

        return new MoveAnims.MonPoseState(Vector2.Zero, 1f, 1f);
    }

    // Resolves the '$attacker'/'$defender' pseudo sprites (Double Team clones, …) to the
    // creatures' current spritesheet frames.
    private bool ResolveMonFrame(bool attackerSprite, out ImTextureID tex, out Vector2 uv0, out Vector2 uv1)
    {
        tex = default;
        uv0 = Vector2.Zero;
        uv1 = Vector2.One;
        var fromPlayer = moveFx?.FromPlayer ?? true;
        var isPlayerMon = attackerSprite == fromPlayer;
        var mon = isPlayerMon ? displayedPlayer ?? battle?.Active : battle?.Wild;
        if (mon is null || !PokemonSprites.TryGetFrame(mon.Species.Id, isPlayerMon, time, out var frame))
        {
            return false;
        }

        tex = frame.Handle;
        uv0 = frame.Uv0;
        uv1 = frame.Uv1;
        return true;
    }

    private void UpdateMoveFx(float dt)
    {
        if (moveFx is null)
        {
            return;
        }

        moveFx.Age += dt;
        if (moveFx.Age >= moveFx.Duration)
        {
            moveFx = null;
        }
    }

    // Plays a move's animation: every move gets the traced Showdown choreography (exact
    // keyframes executed from Showdown's own animation code); the pattern system below is a
    // safety net for when the data asset is missing.
    private void DrawMoveFx(ImDrawListPtr drawList, Rect content, Vector2 playerPos, Vector2 wildPos,
        in SceneMap sceneMap, float scale)
    {
        if (moveFx is not { } fx)
        {
            return;
        }

        var battleEffectScale = Math.Clamp(State.BattleEffectScale, MinBattleEffectScale, MaxBattleEffectScale);
        if (fx.Playback is { } playback)
        {
            MoveAnims.DrawEffects(drawList, playback, fx.Age * 1000f, sceneMap, battleEffectScale,
                ResolveMonFrame);
            return;
        }

        var from = fx.FromPlayer ? playerPos : wildPos;
        var to = fx.FromPlayer ? wildPos : playerPos;
        var visual = MoveVisuals.For(fx.Move.Name);
        var hasSprite = MoveFxSprites.TryGet(visual.Sprite, out var tex, out var aspect);
        var progress = Math.Clamp(fx.Age / 0.9f, 0f, 1f);
        var effectScale = scale * battleEffectScale;
        var tone = fx.Move.Effect == MoveEffect.HealUser
            ? new Vector4(0.4f, 0.94f, 0.58f, 1f)
            : Elements.Color(fx.Move.Element);
        var power = 0.8f + Math.Clamp(fx.Move.Power / 120f, 0f, 1f) * 0.5f;

        switch (visual.Pattern)
        {
            case MoveFxPattern.Beam:
                DrawBeamFx(drawList, hasSprite, tex, aspect, from, to, progress, tone, power, effectScale);
                break;
            case MoveFxPattern.Contact:
                DrawContactFx(drawList, hasSprite, tex, aspect, to, progress, tone, power, effectScale);
                break;
            case MoveFxPattern.SelfBuff:
                DrawSelfBuffFx(drawList, hasSprite, tex, aspect, from, progress, tone, effectScale);
                break;
            case MoveFxPattern.Cloud:
                DrawCloudFx(drawList, hasSprite, tex, aspect, to, progress, tone, effectScale);
                break;
            default:
                DrawProjectileFx(drawList, hasSprite, tex, aspect, from, to, progress, tone, power, effectScale);
                break;
        }
    }

    private static uint FxTint(Vector4 element, float alpha) => ImGui.GetColorU32(
        new Vector4(0.55f + 0.45f * element.X, 0.55f + 0.45f * element.Y, 0.55f + 0.45f * element.Z, alpha));

    // Draws an fx sprite as an axis-aligned quad centred at `center` (uses the same AddImage path
    // as the creature sprites, which is known to render).
    private static void DrawSprite(ImDrawListPtr dl, ImTextureID tex, Vector2 center, float height, float aspect,
        uint color)
    {
        var hw = height * (aspect <= 0f ? 1f : aspect) * 0.5f;
        var hh = height * 0.5f;
        dl.AddImage(tex, center - new Vector2(hw, hh), center + new Vector2(hw, hh),
            new Vector2(0f, 0f), new Vector2(1f, 1f), color);
    }

    private static void DrawProjectileFx(ImDrawListPtr dl, bool hasSprite, ImTextureID tex, float aspect, Vector2 from,
        Vector2 to, float t, Vector4 tone, float power, float scale)
    {
        var travel = t * t * (3f - 2f * t);
        var point = Vector2.Lerp(from, to, travel);
        var dir = Vector2.Normalize(to - from);
        var fade = 1f - Math.Clamp((t - 0.72f) / 0.28f, 0f, 1f);
        var h = 34f * power * scale;
        // Geometric core (always visible): a glowing head with a fading trail.
        for (var i = 5; i >= 1; i--)
        {
            var trail = point - dir * (i * 6f * scale);
            dl.AddCircleFilled(trail, (h * 0.28f) * (1f - i * 0.13f), FxTint(tone, fade * 0.12f * (6 - i)));
        }

        dl.AddCircleFilled(point, h * 0.3f, FxTint(tone, fade * 0.9f));
        if (hasSprite)
        {
            for (var i = 3; i >= 1; i--)
            {
                DrawSprite(dl, tex, point - dir * (i * 8f * scale), h * (1f - i * 0.14f), aspect,
                    FxTint(tone, fade * 0.16f * (4 - i)));
            }

            DrawSprite(dl, tex, point, h, aspect, FxTint(tone, fade));
        }

        if (travel > 0.72f)
        {
            var impact = (travel - 0.72f) / 0.28f;
            dl.AddCircle(to, (12f + impact * 30f) * scale, FxTint(tone, (1f - impact) * 0.8f), 28, 3f * scale);
        }
    }

    private static void DrawBeamFx(ImDrawListPtr dl, bool hasSprite, ImTextureID tex, float aspect, Vector2 from,
        Vector2 to, float t, Vector4 tone, float power, float scale)
    {
        var dir = Vector2.Normalize(to - from);
        var length = Vector2.Distance(from, to);
        var reach = Math.Clamp(t / 0.45f, 0f, 1f);
        reach = reach * reach * (3f - 2f * reach);
        var fade = 1f - Math.Clamp((t - 0.7f) / 0.3f, 0f, 1f);
        var h = 26f * power * scale;
        var muzzle = 18f * scale;
        var end = muzzle + (length - muzzle) * reach;
        // Geometric core: a thick glowing beam (always visible).
        dl.AddLine(from + dir * muzzle, from + dir * end, FxTint(tone, fade * 0.4f), 10f * power * scale);
        dl.AddLine(from + dir * muzzle, from + dir * end, FxTint(tone, fade * 0.9f), 4f * power * scale);
        if (hasSprite)
        {
            for (var d = muzzle; d <= end; d += h * 0.45f)
            {
                DrawSprite(dl, tex, from + dir * d, h, aspect, FxTint(tone, fade * 0.9f));
            }
        }

        if (reach >= 1f)
        {
            dl.AddCircle(to, (14f + (1f - fade) * 22f) * scale, FxTint(tone, fade * 0.7f), 24, 3f * scale);
        }
    }

    private static void DrawContactFx(ImDrawListPtr dl, bool hasSprite, ImTextureID tex, float aspect, Vector2 to,
        float t, Vector4 tone, float power, float scale)
    {
        if (t < 0.42f)
        {
            return; // the attacker's lunge (pose) carries the first half; burst on contact.
        }

        var it = (t - 0.42f) / 0.58f;
        var fade = 1f - it;
        var pop = 0.7f + it * 1.0f;
        // Geometric core: shockwave ring + spokes (always visible).
        dl.AddCircle(to, (16f + it * 34f) * scale, FxTint(tone, fade * 0.85f), 24, 3f * scale);
        for (var i = 0; i < 6; i++)
        {
            var a = i * MathF.PI / 3f + it;
            var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
            dl.AddLine(to + d * 16f * scale, to + d * (16f + 22f * it) * scale, FxTint(tone, fade * 0.85f), 2.5f * scale);
        }

        if (hasSprite)
        {
            DrawSprite(dl, tex, to, 40f * power * pop * scale, aspect, FxTint(tone, fade));
        }
        else
        {
            dl.AddCircleFilled(to, 12f * pop * scale, FxTint(tone, fade * 0.8f));
        }
    }

    private static void DrawSelfBuffFx(ImDrawListPtr dl, bool hasSprite, ImTextureID tex, float aspect, Vector2 source,
        float t, Vector4 tone, float scale)
    {
        var fade = 1f - Math.Clamp((t - 0.7f) / 0.3f, 0f, 1f);
        for (var ring = 0; ring < 3; ring++)
        {
            var phase = (t + ring * 0.24f) % 1f;
            dl.AddCircle(source, (18f + phase * 42f) * scale, FxTint(tone, (1f - phase) * fade * 0.6f), 32, 3f * scale);
        }

        for (var i = 0; i < 6; i++)
        {
            var a = i * MathF.PI / 3f + t * 5f;
            var r = (24f + MathF.Sin(t * 8f + i) * 7f) * scale;
            var p = source + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r - new Vector2(0f, t * 18f * scale);
            if (hasSprite)
            {
                DrawSprite(dl, tex, p, 20f * scale, aspect, FxTint(tone, fade * 0.95f));
            }
            else
            {
                dl.AddCircleFilled(p, 5f * scale, FxTint(tone, fade * 0.9f));
            }
        }
    }

    private static void DrawCloudFx(ImDrawListPtr dl, bool hasSprite, ImTextureID tex, float aspect, Vector2 target,
        float t, Vector4 tone, float scale)
    {
        var fade = 1f - Math.Clamp((t - 0.55f) / 0.45f, 0f, 1f);
        for (var i = 0; i < 8; i++)
        {
            var a = i * 2.399f;
            var rad = (10f + i % 3 * 8f) * scale;
            var jitter = new Vector2(MathF.Sin(t * 6f + i) * 7f, MathF.Cos(t * 5f + i) * 5f - t * 12f) * scale;
            var p = target + new Vector2(MathF.Cos(a), MathF.Sin(a)) * rad + jitter;
            if (hasSprite)
            {
                DrawSprite(dl, tex, p, (18f + i % 2 * 8f) * scale, aspect, FxTint(tone, fade * 0.9f));
            }
            else
            {
                dl.AddCircleFilled(p, (5f + i % 2 * 2f) * scale, FxTint(tone, fade * 0.85f));
            }
        }
    }

}
