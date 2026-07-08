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
    // ---- Arena: Training tiers + Gym challenges --------------------------------------

    private int arenaTab; // 0 = Training, 1 = Gyms
    private float arenaTabIndicator = -1f;
    private float arenaScroll;

    private void DrawArena(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        BiomeBackdrop.Draw(drawList, content, State.CurrentBiome, time, false);
        LgUi.Header(content, theme, Accent, "Battle Arena", $"{State.BadgeCount}/{Gyms.All.Count} badges earned",
            scale);

        var toggle = new Rect(new Vector2(content.Min.X + 14f * scale, content.Min.Y + 64f * scale),
            new Vector2(content.Max.X - 14f * scale, content.Min.Y + 94f * scale));
        var clicked = LgUi.Segmented(toggle, new[] { "Training", "Gyms" }, arenaTab, Accent, theme, scale,
            ref arenaTabIndicator);
        if (clicked >= 0 && clicked != arenaTab)
        {
            arenaTab = clicked;
            arenaScroll = 0f;
        }

        var wiped = State.AllMonstersFainted;
        if (wiped)
        {
            Typography.DrawCentered(new Vector2(content.Center.X, content.Min.Y + 108f * scale),
                FitLabel("Your team has fainted — revive at a town Marketboard first.", content.Width - 24f * scale,
                    TextStyles.Caption1), theme.Danger with { W = 0.92f }, TextStyles.Caption1);
        }
        else
        {
            Typography.DrawCentered(new Vector2(content.Center.X, content.Min.Y + 108f * scale),
                FitLabel(arenaTab == 0
                    ? "Battle randomized trainers near your level to grind XP."
                    : "Beat a gym leader to earn a badge and unlock the next training tier.",
                    content.Width - 24f * scale, TextStyles.Caption1), theme.TextStrong with { W = 0.86f },
                TextStyles.Caption1);
        }

        // On the Gyms tab, a badge case showcases the collectable badges earned so far.
        var listTop = content.Min.Y + 122f * scale;
        if (arenaTab == 1)
        {
            DrawBadgeCase(new Rect(new Vector2(content.Min.X + 12f * scale, content.Min.Y + 124f * scale),
                new Vector2(content.Max.X - 12f * scale, content.Min.Y + 170f * scale)), theme, scale);
            listTop = content.Min.Y + 180f * scale;
        }

        var listArea = new Rect(new Vector2(content.Min.X + 12f * scale, listTop),
            new Vector2(content.Max.X - 12f * scale, content.Max.Y - 46f * scale));

        if (arenaTab == 0)
        {
            DrawScrollList(listArea, 96f * scale, 8f * scale, Training.Tiers.Count, ref arenaScroll, scale,
                (i, rowRect) => DrawTierRow(Training.Tiers[i], rowRect, theme, scale, !wiped));
        }
        else
        {
            DrawScrollList(listArea, 82f * scale, 8f * scale, Gyms.All.Count, ref arenaScroll, scale,
                (i, rowRect) => DrawGymRow(Gyms.All[i], rowRect, theme, scale, !wiped));
        }

        DrawNavigation(content, theme, scale);
    }

    // Resolves a gym's badge texture: the real extracted badge (badges/gym/<Leader>.png), falling
    // back to the Showdown type icon (badges/<Type>.png) until a leader's badge art exists.
    private static bool GymBadge(GymDef gym, out Dalamud.Bindings.ImGui.ImTextureID handle, out float aspect) =>
        AssetTextures.TryGet($"badges/gym/{gym.Leader}.png", out handle, out aspect) ||
        AssetTextures.TryGet($"badges/{gym.Type}.png", out handle, out aspect);

    // A trophy strip of the six gym badges: earned ones glow in their type colour, the rest are
    // greyed and locked. Reads as a "badge case" at the top of the Gyms tab.
    private void DrawBadgeCase(Rect rect, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        LgUi.Card(drawList, rect.Min, rect.Max, 12f * scale, scale);
        var count = Gyms.All.Count;
        var cellW = rect.Width / count;
        for (var i = 0; i < count; i++)
        {
            var gym = Gyms.All[i];
            var earned = State.HasBadge(gym.Index);
            var center = new Vector2(rect.Min.X + cellW * (i + 0.5f), rect.Center.Y - 4f * scale);
            var typeColor = Elements.Color(gym.Type);
            if (earned)
            {
                ProgressRing.Glow(center, 16f * scale, typeColor, 0.5f);
            }

            var w = 30f * scale;
            if (GymBadge(gym, out var badgeTex, out var badgeAspect))
            {
                var h = w / MathF.Max(0.01f, badgeAspect);
                var half = new Vector2(w * 0.5f, h * 0.5f);
                var tint = earned ? Vector4.One : new Vector4(0.5f, 0.5f, 0.55f, 0.5f);
                drawList.AddImage(badgeTex, center - half, center + half, Vector2.Zero, Vector2.One,
                    ImGui.GetColorU32(tint));
            }
            else
            {
                drawList.AddCircleFilled(center, 13f * scale,
                    ImGui.GetColorU32(earned ? typeColor with { W = 0.5f } : GamePalette.CellSunken));
                ProgressRing.CenterIcon(drawList, center,
                    earned ? FontAwesomeIcon.Certificate : FontAwesomeIcon.Lock,
                    earned ? typeColor : theme.TextMuted, 12f * scale);
            }

            Typography.DrawCentered(new Vector2(center.X, rect.Max.Y - 8f * scale),
                FitLabel(gym.Type.ToString(), cellW - 4f * scale, TextStyles.Caption2),
                earned ? typeColor with { W = 0.95f } : theme.TextMuted, TextStyles.Caption2);
        }
    }

    private void DrawTierRow(Training.Tier tier, Rect rect, PhoneTheme theme, float scale, bool canBattle)
    {
        var drawList = ImGui.GetWindowDrawList();
        var badges = State.BadgeCount;
        var unlocked = Training.IsUnlocked(tier, badges);
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        LgUi.Card(drawList, rect.Min, rect.Max, 12f * scale, scale, hovered && unlocked, sunken: !unlocked);
        drawList.AddRectFilled(rect.Min, new Vector2(rect.Min.X + 4f * scale, rect.Max.Y),
            ImGui.GetColorU32(Accent with { W = unlocked ? 0.85f : 0.3f }), 3f * scale);

        // Numbered medallion so tiers read as a clear ladder.
        var medallion = new Vector2(rect.Min.X + 30f * scale, rect.Min.Y + 26f * scale);
        drawList.AddCircleFilled(medallion, 15f * scale,
            ImGui.GetColorU32(unlocked ? GamePalette.Darken(Accent, 0.25f) : GamePalette.CellSunken));
        drawList.AddCircle(medallion, 15f * scale,
            ImGui.GetColorU32(Accent with { W = unlocked ? 0.85f : 0.35f }), 24, 1.4f * scale);
        Typography.DrawCentered(medallion, tier.Index.ToString(),
            unlocked ? GamePalette.InkOn(Accent) : theme.TextMuted, TextStyles.Title3);

        var textColor = unlocked ? theme.TextStrong : theme.TextStrong with { W = 0.55f };
        var textX = rect.Min.X + 56f * scale;
        var textWidth = rect.Max.X - 104f * scale - textX;
        Typography.Draw(new Vector2(textX, rect.Min.Y + 9f * scale),
            FitLabel(tier.Name, textWidth, TextStyles.Headline), textColor, TextStyles.Headline);
        Typography.Draw(new Vector2(textX, rect.Min.Y + 30f * scale),
            FitLabel($"Lv {tier.MinLevel}-{tier.MaxLevel}   ·   up to {tier.MaxTeam} Pokémon", textWidth,
                TextStyles.Caption1), theme.TextStrong with { W = unlocked ? 0.82f : 0.5f }, TextStyles.Caption1);
        var gym = Gyms.All[tier.Index - 1];
        Typography.Draw(new Vector2(textX, rect.Min.Y + 47f * scale),
            FitLabel($"Prep for {gym.Leader}'s {Elements.Name(gym.Type)} gym", textWidth, TextStyles.Caption2),
            theme.TextStrong with { W = unlocked ? 0.72f : 0.45f }, TextStyles.Caption2);

        var buttonRect = CenteredAt(new Vector2(rect.Max.X - 52f * scale, rect.Min.Y + 24f * scale),
            new Vector2(90f * scale, 30f * scale));
        if (unlocked)
        {
            if (LgUi.Button(buttonRect, "Train", theme.Accent, theme, canBattle))
            {
                StartTraining(tier);
            }

            // Level-range picker: choose the sub-band of trainer levels to fight within this tier.
            var (curMin, curMax) = State.TrainingRange(tier);
            var editY = rect.Min.Y + 70f * scale;
            Typography.Draw(new Vector2(rect.Min.X + 16f * scale, editY - 8f * scale), "Battle Lv",
                theme.TextStrong with { W = 0.72f }, TextStyles.Caption2);
            var newMin = DrawStepper(drawList, new Vector2(rect.Min.X + 78f * scale, editY), curMin,
                tier.MinLevel, curMax, theme, scale);
            Typography.DrawCentered(new Vector2(rect.Min.X + 172f * scale, editY), "to",
                theme.TextStrong with { W = 0.6f }, TextStyles.Caption1);
            var newMax = DrawStepper(drawList, new Vector2(rect.Min.X + 190f * scale, editY), curMax,
                curMin, tier.MaxLevel, theme, scale);
            if (newMin != curMin || newMax != curMax)
            {
                State.SetTrainingRange(tier, newMin, newMax);
            }
        }
        else
        {
            var need = Training.RequiredBadges(tier);
            LgUi.Button(buttonRect, "Locked", GamePalette.CellSunken, theme, false);
            Typography.Draw(new Vector2(rect.Min.X + 16f * scale, rect.Min.Y + 66f * scale),
                $"Earn {need} badge{(need == 1 ? "" : "s")} to unlock  ({badges}/{need})",
                theme.TextStrong with { W = 0.6f }, TextStyles.Caption2);
        }
    }

    // A compact [-] value [+] stepper; returns the adjusted value.
    private int DrawStepper(ImDrawListPtr drawList, Vector2 leftCenter, int value, int lo, int hi, PhoneTheme theme,
        float scale)
    {
        var size = new Vector2(20f * scale, 20f * scale);
        var decRect = CenteredAt(new Vector2(leftCenter.X + 10f * scale, leftCenter.Y), size);
        if (LgUi.Button(decRect, "-", GamePalette.Cell, theme, value > lo))
        {
            value = Math.Max(lo, value - 1);
        }

        Typography.DrawCentered(new Vector2(leftCenter.X + 40f * scale, leftCenter.Y), value.ToString(),
            theme.TextStrong, TextStyles.Headline);

        var incRect = CenteredAt(new Vector2(leftCenter.X + 70f * scale, leftCenter.Y), size);
        if (LgUi.Button(incRect, "+", GamePalette.Cell, theme, value < hi))
        {
            value = Math.Min(hi, value + 1);
        }

        return value;
    }

    private void DrawGymRow(GymDef gym, Rect rect, PhoneTheme theme, float scale, bool canBattle)
    {
        var drawList = ImGui.GetWindowDrawList();
        var earned = State.HasBadge(gym.Index);
        var here = Array.IndexOf(gym.Territories, State.Territory) >= 0;
        var typeColor = Elements.Color(gym.Type);
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        LgUi.Card(drawList, rect.Min, rect.Max, 12f * scale, scale, hovered);
        // Subtle type wash so each gym reads as its element at a glance.
        Squircle.Fill(drawList, rect.Min, rect.Max, 12f * scale, ImGui.GetColorU32(typeColor with { W = 0.07f }));
        drawList.AddRectFilled(rect.Min, new Vector2(rect.Min.X + 4f * scale, rect.Max.Y),
            ImGui.GetColorU32(typeColor with { W = 0.85f }), 3f * scale);

        // Badge emblem: the Showdown type icon for this gym. Earned badges show in full colour with
        // a glow + green check; unearned ones are greyed out and locked.
        var badgeCenter = new Vector2(rect.Min.X + 32f * scale, rect.Min.Y + 32f * scale);
        if (earned)
        {
            ProgressRing.Glow(badgeCenter, 22f * scale, typeColor, 0.5f);
        }

        var badgeW = 40f * scale;
        if (GymBadge(gym, out var badgeTex, out var badgeAspect))
        {
            var w = badgeW;
            var h = badgeW / MathF.Max(0.01f, badgeAspect);
            var half = new Vector2(w * 0.5f, h * 0.5f);
            var tint = earned ? Vector4.One : new Vector4(0.5f, 0.5f, 0.55f, 0.55f);
            drawList.AddImage(badgeTex, badgeCenter - half, badgeCenter + half, Vector2.Zero, Vector2.One,
                ImGui.GetColorU32(tint));
        }
        else
        {
            drawList.AddCircleFilled(badgeCenter, 16f * scale,
                ImGui.GetColorU32(earned ? typeColor with { W = 0.4f } : GamePalette.CellSunken));
            ProgressRing.CenterIcon(drawList, badgeCenter,
                earned ? FontAwesomeIcon.Certificate : FontAwesomeIcon.Trophy,
                earned ? typeColor : theme.TextMuted, 14f * scale);
        }

        var markCenter = badgeCenter + new Vector2(17f * scale, 11f * scale);
        if (earned)
        {
            drawList.AddCircleFilled(markCenter, 8f * scale, ImGui.GetColorU32(new Vector4(0.16f, 0.62f, 0.32f, 1f)));
            drawList.AddCircle(markCenter, 8f * scale, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.85f)), 16,
                1f * scale);
            ProgressRing.CenterIcon(drawList, markCenter, FontAwesomeIcon.Check, Vector4.One, 8f * scale);
        }
        else
        {
            ProgressRing.CenterIcon(drawList, markCenter, FontAwesomeIcon.Lock, theme.TextMuted with { W = 0.85f },
                9f * scale);
        }

        Typography.DrawCentered(new Vector2(badgeCenter.X, rect.Max.Y - 12f * scale),
            $"Gym {gym.Index + 1}", theme.TextStrong with { W = 0.6f }, TextStyles.Caption2);

        var textX = rect.Min.X + 62f * scale;
        var textWidth = rect.Max.X - 108f * scale - textX;
        Typography.Draw(new Vector2(textX, rect.Min.Y + 11f * scale),
            FitLabel($"{gym.Leader}", textWidth, TextStyles.Headline), theme.TextStrong, TextStyles.Headline);
        Typography.Draw(new Vector2(textX, rect.Min.Y + 32f * scale),
            FitLabel($"{gym.City}", textWidth, TextStyles.Caption1), theme.TextStrong with { W = 0.7f },
            TextStyles.Caption1);
        Typography.Draw(new Vector2(textX, rect.Min.Y + 50f * scale),
            FitLabel($"{Elements.Name(gym.Type)}  ·  {gym.LevelLabel}  ·  {gym.Team.Length} Pokémon", textWidth,
                TextStyles.Caption2), theme.TextStrong with { W = 0.78f }, TextStyles.Caption2);
        Typography.Draw(new Vector2(textX, rect.Min.Y + 66f * scale),
            FitLabel(earned ? $"✓ {gym.Badge} earned" : gym.Badge, textWidth, TextStyles.Caption2),
            earned ? new Vector4(0.42f, 0.86f, 0.5f, 1f) : theme.TextStrong with { W = 0.6f }, TextStyles.Caption2);

        // The leader's ace (their strongest team member) as a small preview, tagged below.
        if (Dex.Find(gym.Team[^1].Species) is { } ace)
        {
            var aceCenter = new Vector2(rect.Max.X - 48f * scale, rect.Min.Y + 54f * scale);
            drawList.AddCircleFilled(aceCenter, 15f * scale, ImGui.GetColorU32(GamePalette.CellSunken));
            drawList.AddCircle(aceCenter, 15f * scale, ImGui.GetColorU32(typeColor with { W = 0.55f }), 24,
                1f * scale);
            MonsterArt.Draw(drawList, aceCenter, 24f * scale, ace, -1f, MonsterPose.Idle(time));
            Typography.DrawCentered(new Vector2(aceCenter.X, rect.Max.Y - 8f * scale), "ACE",
                typeColor with { W = 0.85f }, TextStyles.Caption2);
        }

        var buttonRect = CenteredAt(new Vector2(rect.Max.X - 48f * scale, rect.Min.Y + 20f * scale),
            new Vector2(86f * scale, 28f * scale));
        if (here)
        {
            if (LgUi.Button(buttonRect, earned ? "Rematch" : "Challenge", typeColor, theme, canBattle))
            {
                StartGym(gym);
            }
        }
        else
        {
            LgUi.Button(buttonRect, "Travel", GamePalette.CellSunken, theme, false);
            if (hovered)
            {
                ShowTooltip($"Challenge {gym.Leader} in {gym.City}. Travel there to fight this gym.");
            }
        }
    }
}
