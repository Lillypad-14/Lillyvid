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
    // Navy/cream chrome per Ideas/UI Update/Arena.png + Gyms.png: outlined BATTLE ARENA header with
    // the badge-count pill, TRAINING/GYMS folder tabs, and navy ladder/gym cards on a cream panel.

    private int arenaTab; // 0 = Training, 1 = Gyms
    private float arenaScroll;

    private void DrawArena(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(RosterUi.NavyBottom));
        var headerBottom = RosterUi.ScreenHeader(content, "BATTLE ARENA", "nav_arena", new[]
        {
            ($"{State.BadgeCount}/{Gyms.All.Count}", RosterUi.CountGreen),
            ("badges earned", new Vector4(1f, 1f, 1f, 1f)),
        }, scale);

        // The cream panel goes down first so the folder tabs can sit on its top edge.
        var navTop = content.Max.Y - NavBarHeight * scale;
        var panel = new Rect(new Vector2(content.Min.X + 7f * scale, headerBottom + 32f * scale),
            new Vector2(content.Max.X - 7f * scale, navTop - 7f * scale));
        RosterUi.CreamPanel(drawList, panel, scale);

        var toggle = new Rect(new Vector2(content.Min.X + 12f * scale, headerBottom + 8f * scale),
            new Vector2(content.Max.X - 12f * scale, headerBottom + 36f * scale));
        var clicked = RosterUi.FolderTabs(toggle, new[] { "TRAINING", "GYMS" }, arenaTab, scale);
        if (clicked >= 0 && clicked != arenaTab)
        {
            arenaTab = clicked;
            arenaScroll = 0f;
        }

        // Info strip: what this tab is for (or the team-wipe warning), on the panel's top edge.
        var infoY = panel.Min.Y + 18f * scale;
        var wiped = State.AllMonstersFainted;
        var infoText = wiped
            ? "Your team has fainted — revive at a town Marketboard first."
            : arenaTab == 0
                ? "Battle randomized trainers near your level to grind XP."
                : "Beat a gym leader to earn a badge.";
        var infoColor = wiped ? GamePalette.Darken(RosterUi.Red, 0.1f) : RosterUi.InkTan;
        ProgressRing.CenterIcon(drawList, new Vector2(panel.Min.X + 18f * scale, infoY),
            FontAwesomeIcon.InfoCircle, infoColor with { W = 0.85f }, 11f * scale);
        Typography.Draw(new Vector2(panel.Min.X + 30f * scale, infoY - 8f * scale),
            FitLabel(infoText, panel.Width - 44f * scale, TextStyles.Caption1), infoColor, TextStyles.Caption1);

        // On the Gyms tab, a badge case showcases the collectable badges earned so far.
        var listTop = infoY + 14f * scale;
        if (arenaTab == 1)
        {
            DrawBadgeCase(new Rect(new Vector2(panel.Min.X + 9f * scale, listTop),
                new Vector2(panel.Max.X - 9f * scale, listTop + 46f * scale)), theme, scale);
            listTop += 54f * scale;
        }

        var listArea = new Rect(new Vector2(panel.Min.X + 9f * scale, listTop),
            new Vector2(panel.Max.X - 8f * scale, panel.Max.Y - 8f * scale));

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
        RosterUi.DarkCard(drawList, rect, 10f * scale, scale);
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
                    ImGui.GetColorU32(earned ? typeColor with { W = 0.5f } : RosterUi.NavyInset));
                ProgressRing.CenterIcon(drawList, center,
                    earned ? FontAwesomeIcon.Certificate : FontAwesomeIcon.Lock,
                    earned ? typeColor : RosterUi.CardMuted, 12f * scale);
            }

            Typography.DrawCentered(new Vector2(center.X, rect.Max.Y - 8f * scale),
                FitLabel(gym.Type.ToString(), cellW - 4f * scale, TextStyles.Caption2),
                earned ? GamePalette.Lighten(typeColor, 0.2f) : RosterUi.CardMuted with { W = 0.7f },
                TextStyles.Caption2);
        }
    }

    private void DrawTierRow(Training.Tier tier, Rect rect, PhoneTheme theme, float scale, bool canBattle)
    {
        var drawList = ImGui.GetWindowDrawList();
        var badges = State.BadgeCount;
        var unlocked = Training.IsUnlocked(tier, badges);
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        RosterUi.DarkCard(drawList, rect, 11f * scale, scale, hovered && unlocked, sunken: !unlocked);
        if (unlocked)
        {
            // Available tiers get the bright green frame from the mockup.
            Squircle.Stroke(drawList, rect.Min, rect.Max, 11f * scale,
                ImGui.GetColorU32(RosterUi.GreenBright with { W = 0.9f }), 2f * scale);
        }

        // Numbered medallion so tiers read as a clear ladder.
        var medallion = new Vector2(rect.Min.X + 28f * scale, rect.Min.Y + 26f * scale);
        if (unlocked)
        {
            drawList.AddCircleFilled(medallion, 14f * scale, ImGui.GetColorU32(RosterUi.Green));
            drawList.AddCircle(medallion, 14f * scale, ImGui.GetColorU32(RosterUi.GreenBright), 24, 1.6f * scale);
        }
        else
        {
            drawList.AddCircleFilled(medallion, 14f * scale, ImGui.GetColorU32(RosterUi.NavyInset));
            drawList.AddCircle(medallion, 14f * scale, ImGui.GetColorU32(RosterUi.CardEdge with { W = 0.5f }), 24,
                1.4f * scale);
        }

        Typography.DrawCentered(medallion, tier.Index.ToString(),
            unlocked ? new Vector4(1f, 1f, 1f, 1f) : RosterUi.CardMuted, TextStyles.Title3);

        var textColor = unlocked ? RosterUi.CardInk : RosterUi.CardMuted;
        var textX = rect.Min.X + 52f * scale;
        var textWidth = rect.Max.X - 104f * scale - textX;
        Typography.Draw(new Vector2(textX, rect.Min.Y + 9f * scale),
            FitLabel(tier.Name, textWidth, TextStyles.Headline), textColor, TextStyles.Headline);
        Typography.Draw(new Vector2(textX, rect.Min.Y + 30f * scale),
            FitLabel($"Lv {tier.MinLevel}-{tier.MaxLevel}   ·   up to {tier.MaxTeam} Pokémon", textWidth,
                TextStyles.Caption1), unlocked ? RosterUi.CardMuted : RosterUi.CardMuted with { W = 0.6f },
            TextStyles.Caption1);

        // "Prep for <leader>'s <Type> gym", with the gym part tinted its element colour.
        var gym = Gyms.All[tier.Index - 1];
        var prepY = rect.Min.Y + 47f * scale;
        const string prefix = "Prep for ";
        var gymText = $"{gym.Leader}'s {Elements.Name(gym.Type)} gym";
        var prefixW = Typography.Measure(prefix, TextStyles.Caption2).X;
        var gymColor = GamePalette.Lighten(Elements.Color(gym.Type), 0.22f) with { W = unlocked ? 1f : 0.55f };
        Typography.Draw(new Vector2(textX, prepY), prefix,
            unlocked ? RosterUi.CardMuted : RosterUi.CardMuted with { W = 0.6f }, TextStyles.Caption2);
        Typography.Draw(new Vector2(textX + prefixW, prepY),
            FitLabel(gymText, textWidth - prefixW, TextStyles.Caption2), gymColor, TextStyles.Caption2);

        var buttonRect = CenteredAt(new Vector2(rect.Max.X - 52f * scale, rect.Min.Y + 24f * scale),
            new Vector2(90f * scale, 30f * scale));
        if (unlocked)
        {
            if (RosterUi.ColorButton(buttonRect, "TRAIN", RosterUi.Purple, scale, canBattle))
            {
                StartTraining(tier);
            }

            // Level-range picker: choose the sub-band of trainer levels to fight within this tier.
            // Laid out left-to-right from the label so the whole strip fits the narrowest phone.
            var (curMin, curMax) = State.TrainingRange(tier);
            var editY = rect.Min.Y + 74f * scale;
            Typography.Draw(new Vector2(rect.Min.X + 14f * scale, editY - 8f * scale), "Battle Lv",
                RosterUi.CardMuted, TextStyles.Caption2);
            var stepperX = rect.Min.X + 66f * scale;
            var newMin = DrawStepper(drawList, new Vector2(stepperX, editY), curMin, tier.MinLevel, curMax, scale);
            Typography.DrawCentered(new Vector2(stepperX + StepperWidth * scale + 9f * scale, editY), "to",
                RosterUi.CardMuted, TextStyles.Caption1);
            var newMax = DrawStepper(drawList, new Vector2(stepperX + (StepperWidth + 18f) * scale, editY), curMax,
                curMin, tier.MaxLevel, scale);
            if (newMin != curMin || newMax != curMax)
            {
                State.SetTrainingRange(tier, newMin, newMax);
            }
        }
        else
        {
            var need = Training.RequiredBadges(tier);
            RosterUi.ColorButton(buttonRect, "LOCKED", RosterUi.Blue, scale, false);

            // Divider + unlock requirement footer, like the mockup's locked cards.
            var lineY = rect.Max.Y - 26f * scale;
            drawList.AddLine(new Vector2(rect.Min.X + 10f * scale, lineY), new Vector2(rect.Max.X - 10f * scale, lineY),
                ImGui.GetColorU32(RosterUi.CardEdge with { W = 0.35f }), 1f * scale);
            var unlockText = $"Earn {need} badge{(need == 1 ? "" : "s")} to unlock";
            Typography.Draw(new Vector2(rect.Min.X + 14f * scale, rect.Max.Y - 20f * scale), unlockText,
                RosterUi.CardMuted, TextStyles.Caption2);
            Typography.Draw(new Vector2(rect.Min.X + 18f * scale + Typography.Measure(unlockText,
                TextStyles.Caption2).X, rect.Max.Y - 20f * scale), $"({badges}/{need})",
                RosterUi.CountBlue, TextStyles.Caption2);
        }
    }

    // Unscaled width of one [-] value [+] stepper, measured from its left edge.
    private const float StepperWidth = 68f;

    // A compact [-] value [+] stepper with round green buttons; returns the adjusted value.
    // `leftCenter` is the strip's left edge, vertically centred on the row.
    private int DrawStepper(ImDrawListPtr drawList, Vector2 leftCenter, int value, int lo, int hi, float scale)
    {
        var radius = 10f * scale;

        bool RoundButton(Vector2 center, string glyph, bool enabled)
        {
            var hoveredBtn = enabled && LgUi.Interactive &&
                ImGui.IsMouseHoveringRect(center - new Vector2(radius, radius), center + new Vector2(radius, radius));
            if (enabled)
            {
                var fill = hoveredBtn ? GamePalette.Lighten(RosterUi.Green, 0.12f) : RosterUi.Green;
                drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(fill));
                drawList.AddCircle(center, radius, ImGui.GetColorU32(GamePalette.Darken(RosterUi.Green, 0.3f)), 24,
                    1.6f * scale);
            }
            else
            {
                drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(RosterUi.NavyInset));
                drawList.AddCircle(center, radius, ImGui.GetColorU32(RosterUi.CardEdge with { W = 0.4f }), 24,
                    1.2f * scale);
            }

            Typography.DrawCentered(center, glyph, new Vector4(1f, 1f, 1f, enabled ? 1f : 0.4f),
                TextStyles.SubheadlineEmphasized);
            if (hoveredBtn)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }

            return hoveredBtn && ImGui.IsMouseReleased(ImGuiMouseButton.Left);
        }

        if (RoundButton(new Vector2(leftCenter.X + 10f * scale, leftCenter.Y), "-", value > lo))
        {
            value = Math.Max(lo, value - 1);
        }

        Typography.DrawCentered(new Vector2(leftCenter.X + 34f * scale, leftCenter.Y), value.ToString(),
            RosterUi.CardInk, TextStyles.Headline);

        if (RoundButton(new Vector2(leftCenter.X + (StepperWidth - 10f) * scale, leftCenter.Y), "+", value < hi))
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
        RosterUi.DarkCard(drawList, rect, 11f * scale, scale, hovered, accent: typeColor);
        // Subtle type wash so each gym reads as its element at a glance.
        Squircle.Fill(drawList, rect.Min, rect.Max, 11f * scale, ImGui.GetColorU32(typeColor with { W = 0.07f }));

        // Badge emblem: the extracted badge art for this gym. Earned badges show in full colour with
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
                ImGui.GetColorU32(earned ? typeColor with { W = 0.4f } : RosterUi.NavyInset));
            ProgressRing.CenterIcon(drawList, badgeCenter,
                earned ? FontAwesomeIcon.Certificate : FontAwesomeIcon.Trophy,
                earned ? typeColor : RosterUi.CardMuted, 14f * scale);
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
            ProgressRing.CenterIcon(drawList, markCenter, FontAwesomeIcon.Lock,
                RosterUi.CardMuted with { W = 0.85f }, 9f * scale);
        }

        Typography.DrawCentered(new Vector2(badgeCenter.X, rect.Max.Y - 12f * scale),
            $"Gym {gym.Index + 1}", RosterUi.CardMuted, TextStyles.Caption2);

        var textX = rect.Min.X + 62f * scale;
        var textWidth = rect.Max.X - 108f * scale - textX;
        Typography.Draw(new Vector2(textX, rect.Min.Y + 10f * scale),
            FitLabel($"{gym.Leader}", textWidth, TextStyles.Headline), RosterUi.CardInk, TextStyles.Headline);
        Typography.Draw(new Vector2(textX, rect.Min.Y + 31f * scale),
            FitLabel($"{gym.City}", textWidth, TextStyles.Caption1), RosterUi.CardMuted, TextStyles.Caption1);

        // Element chip + level band + team size, like the mockup's tag row. These rows sit below the
        // Travel/Challenge button, so they only have to clear the ACE preview on the right.
        var lowerWidth = rect.Max.X - 72f * scale - textX;
        LgUi.Chip(drawList, new Vector2(textX, rect.Min.Y + 47f * scale), gym.Type, scale);
        var chipW = Typography.Measure(Elements.Name(gym.Type), TextStyles.Caption2).X + 20f * scale;
        Typography.Draw(new Vector2(textX + chipW, rect.Min.Y + 50f * scale),
            FitLabel($"{gym.LevelLabel}  ·  {gym.Team.Length} Pokémon", MathF.Max(0f, lowerWidth - chipW),
                TextStyles.Caption2), RosterUi.CardMuted, TextStyles.Caption2);
        Typography.Draw(new Vector2(textX, rect.Min.Y + 66f * scale),
            FitLabel(earned ? $"✓ {gym.Badge} earned" : gym.Badge, lowerWidth, TextStyles.Caption2),
            earned ? RosterUi.CountGreen : RosterUi.CardMuted with { W = 0.75f }, TextStyles.Caption2);

        // The leader's ace (their strongest team member) as a small preview, tagged below.
        if (Dex.Find(gym.Team[^1].Species) is { } ace)
        {
            var aceCenter = new Vector2(rect.Max.X - 48f * scale, rect.Min.Y + 54f * scale);
            drawList.AddCircleFilled(aceCenter, 15f * scale, ImGui.GetColorU32(RosterUi.NavyInset));
            drawList.AddCircle(aceCenter, 15f * scale, ImGui.GetColorU32(typeColor with { W = 0.55f }), 24,
                1f * scale);
            MonsterArt.Draw(drawList, aceCenter, 24f * scale, ace, -1f, MonsterPose.Idle(time));
            Typography.DrawCentered(new Vector2(aceCenter.X, rect.Max.Y - 8f * scale), "ACE",
                GamePalette.Lighten(typeColor, 0.2f), TextStyles.Caption2);
        }

        var buttonRect = CenteredAt(new Vector2(rect.Max.X - 48f * scale, rect.Min.Y + 20f * scale),
            new Vector2(86f * scale, 28f * scale));
        if (here)
        {
            if (RosterUi.ColorButton(buttonRect, earned ? "Rematch" : "Challenge", typeColor, scale, canBattle))
            {
                StartGym(gym);
            }
        }
        else
        {
            RosterUi.ColorButton(buttonRect, "Travel", RosterUi.Blue, scale, false);
            if (hovered)
            {
                ShowTooltip($"Challenge {gym.Leader} in {gym.City}. Travel there to fight this gym.");
            }
        }
    }
}
