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

        var listArea = new Rect(new Vector2(content.Min.X + 12f * scale, content.Min.Y + 122f * scale),
            new Vector2(content.Max.X - 12f * scale, content.Max.Y - 46f * scale));

        if (arenaTab == 0)
        {
            DrawScrollList(listArea, 66f * scale, 8f * scale, Training.Tiers.Count, ref arenaScroll, scale,
                (i, rowRect) => DrawTierRow(Training.Tiers[i], rowRect, theme, scale, !wiped));
        }
        else
        {
            DrawScrollList(listArea, 68f * scale, 8f * scale, Gyms.All.Count, ref arenaScroll, scale,
                (i, rowRect) => DrawGymRow(Gyms.All[i], rowRect, theme, scale, !wiped));
        }

        DrawNavigation(content, theme, scale);
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

        var textColor = unlocked ? theme.TextStrong : theme.TextStrong with { W = 0.55f };
        var textX = rect.Min.X + 16f * scale;
        var textWidth = rect.Max.X - 104f * scale - textX;
        Typography.Draw(new Vector2(textX, rect.Min.Y + 9f * scale),
            FitLabel($"Tier {tier.Index} · {tier.Name}", textWidth, TextStyles.Headline), textColor,
            TextStyles.Headline);
        Typography.Draw(new Vector2(textX, rect.Min.Y + 30f * scale),
            FitLabel($"Lv {tier.MinLevel}-{tier.MaxLevel}   ·   up to {tier.MaxTeam} Pokémon", textWidth,
                TextStyles.Caption1), theme.TextStrong with { W = unlocked ? 0.82f : 0.5f }, TextStyles.Caption1);
        var gym = Gyms.All[tier.Index - 1];
        Typography.Draw(new Vector2(textX, rect.Min.Y + 47f * scale),
            FitLabel($"Prep for {gym.Leader}'s {Elements.Name(gym.Type)} gym", textWidth, TextStyles.Caption2),
            theme.TextStrong with { W = unlocked ? 0.72f : 0.45f }, TextStyles.Caption2);

        var buttonRect = CenteredAt(new Vector2(rect.Max.X - 52f * scale, rect.Center.Y),
            new Vector2(90f * scale, 32f * scale));
        if (unlocked)
        {
            if (LgUi.Button(buttonRect, "Train", theme.Accent, theme, canBattle))
            {
                StartTraining(tier);
            }
        }
        else
        {
            var need = Training.RequiredBadges(tier);
            LgUi.Button(buttonRect, "Locked", GamePalette.CellSunken, theme, false);
            if (hovered)
            {
                ImGui.SetTooltip($"Earn {need} badge{(need == 1 ? "" : "s")} to unlock ({badges}/{need}).");
            }
        }
    }

    private void DrawGymRow(GymDef gym, Rect rect, PhoneTheme theme, float scale, bool canBattle)
    {
        var drawList = ImGui.GetWindowDrawList();
        var earned = State.HasBadge(gym.Index);
        var here = Array.IndexOf(gym.Territories, State.Territory) >= 0;
        var typeColor = Elements.Color(gym.Type);
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        LgUi.Card(drawList, rect.Min, rect.Max, 12f * scale, scale, hovered);
        drawList.AddRectFilled(rect.Min, new Vector2(rect.Min.X + 4f * scale, rect.Max.Y),
            ImGui.GetColorU32(typeColor with { W = 0.85f }), 3f * scale);

        // Badge emblem: the Showdown type icon for this gym. Earned badges show in full colour with
        // a glow + green check; unearned ones are greyed out and locked.
        var badgeCenter = new Vector2(rect.Min.X + 30f * scale, rect.Center.Y);
        if (earned)
        {
            ProgressRing.Glow(badgeCenter, 22f * scale, typeColor, 0.5f);
        }

        var badgeW = 40f * scale;
        if (AssetTextures.TryGet($"badges/{gym.Type}.png", out var badgeTex, out var badgeAspect))
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

        var textX = rect.Min.X + 54f * scale;
        var textWidth = rect.Max.X - 104f * scale - textX;
        Typography.Draw(new Vector2(textX, rect.Min.Y + 9f * scale),
            FitLabel($"{gym.Leader} · {gym.City}", textWidth, TextStyles.Headline), theme.TextStrong,
            TextStyles.Headline);
        Typography.Draw(new Vector2(textX, rect.Min.Y + 30f * scale),
            FitLabel($"{Elements.Name(gym.Type)}   ·   {gym.LevelLabel}   ·   {gym.Team.Length} Pokémon", textWidth,
                TextStyles.Caption1), theme.TextStrong with { W = 0.82f }, TextStyles.Caption1);
        Typography.Draw(new Vector2(textX, rect.Min.Y + 47f * scale),
            FitLabel(earned ? $"✓ {gym.Badge}" : gym.Badge, textWidth, TextStyles.Caption2),
            earned ? new Vector4(0.42f, 0.86f, 0.5f, 1f) : theme.TextStrong with { W = 0.78f }, TextStyles.Caption2);

        var buttonRect = CenteredAt(new Vector2(rect.Max.X - 52f * scale, rect.Center.Y),
            new Vector2(92f * scale, 32f * scale));
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
                ImGui.SetTooltip($"Challenge {gym.Leader} in {gym.City}. Travel there to fight this gym.");
            }
        }
    }
}
