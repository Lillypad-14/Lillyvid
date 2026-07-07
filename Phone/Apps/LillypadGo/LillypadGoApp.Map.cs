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
    // ---- Map / home -----------------------------------------------------------------

    private void DrawMap(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        BiomeBackdrop.Draw(drawList, content, State.CurrentBiome, time, false);
        var zone = ArrZones.Find(State.Territory);
        var location = zone is null ? Biomes.Name(State.CurrentBiome) : $"{zone.Name}  |  {zone.LevelLabel}";
        LgUi.Header(content, theme, Accent, "Lillypad Go",
            FitLabel(location, content.Width - 24f * scale, TextStyles.Caption1), scale);

        // Radar: progress toward the next encounter roll. Scanning is paused when the team is
        // wiped or while resting in a town, so the radar goes quiet in those states.
        var wiped = State.AllMonstersFainted;
        var scanning = !wiped && !State.InTown;
        var radar = new Vector2(content.Center.X, content.Min.Y + 128f * scale);
        var radius = 54f * scale;
        var frac = scanning ? Math.Clamp(State.StepProgress / EncounterService.StepDistance, 0f, 1f) : 0f;
        ProgressRing.Track(radar, radius, 6f * scale, new Vector4(1f, 1f, 1f, 0.10f));
        ProgressRing.Fill(radar, radius, 6f * scale, frac, wiped ? theme.Danger : Accent);
        if (scanning)
        {
            for (var i = 0; i < 3; i++)
            {
                var pulse = (time * 0.5f + i / 3f) % 1f;
                drawList.AddCircle(radar, radius * (0.3f + pulse * 0.7f),
                    ImGui.GetColorU32(Accent with { W = 0.25f * (1f - pulse) }), 48, 2f * scale);
            }
        }

        ProgressRing.CenterIcon(radar, wiped ? FontAwesomeIcon.HeartBroken : FontAwesomeIcon.Walking,
            wiped ? theme.Danger : theme.TextStrong with { W = scanning ? 1f : 0.5f }, radius * 0.5f);

        if (State.AllMonstersFainted)
        {
            DrawWipedBanner(content, theme, scale);
        }
        else if (State.Pending is { } wild)
        {
            DrawEncounterCard(content, theme, wild, scale);
        }
        else
        {
            var gymHere = Gyms.ForTerritory(State.Territory);
            var townNote = gymHere is not null
                ? $"{gymHere.Leader}'s gym is in this city — open Arena"
                : State.InTown ? "Safe in town — no wild Pokémon here" : null;
            var remaining = Math.Max(0, (int)MathF.Ceiling(EncounterService.StepDistance - State.StepProgress));
            var scanText = townNote ?? $"Next trail scan in about {remaining} yalms";
            var scanPos = new Vector2(content.Center.X, radar.Y + radius + 20f * scale);
            var textSize = Typography.Measure(scanText, TextStyles.Footnote);
            var pillMin = scanPos - new Vector2(textSize.X * 0.5f + 7f * scale, 3f * scale);
            var pillMax = scanPos + new Vector2(textSize.X * 0.5f + 7f * scale, textSize.Y + 3f * scale);
            Squircle.Fill(drawList, pillMin, pillMax, 8f * scale,
                ImGui.GetColorU32(new Vector4(0.02f, 0.025f, 0.035f, 0.36f)));
            Typography.DrawCentered(scanPos + new Vector2(0f, textSize.Y * 0.5f), scanText,
                theme.TextStrong with { W = 0.92f }, TextStyles.Footnote);
        }

        DrawPartyStrip(content, theme, scale);

        DrawNavigation(content, theme, scale);
    }

    private void DrawEncounterCard(Rect content, PhoneTheme theme, MonsterInstance wild, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var min = new Vector2(content.Min.X + 16f * scale, content.Min.Y + 190f * scale);
        var max = new Vector2(content.Max.X - 16f * scale, content.Min.Y + 300f * scale);
        LgUi.Card(drawList, min, max, 16f * scale, scale);
        Squircle.Stroke(drawList, min, max, 16f * scale, ImGui.GetColorU32(Accent with { W = 0.8f }), 1.6f * scale);
        var portrait = new Vector2(min.X + 48f * scale, min.Y + 40f * scale);
        MonsterArt.Draw(drawList, portrait, 30f * scale, wild.Species, -1f, MonsterPose.Idle(time));

        // Info column (top). Buttons live in their own band at the bottom so nothing overlaps.
        var textX = min.X + 92f * scale;
        var title = FitLabel("Wild " + wild.Name + "!", max.X - 12f * scale - textX, TextStyles.Headline);
        Typography.Draw(new Vector2(textX, min.Y + 12f * scale), title, theme.TextStrong, TextStyles.Headline);
        Typography.Draw(new Vector2(textX, min.Y + 34f * scale), "Lv " + wild.Level, theme.TextMuted,
            TextStyles.Caption1);
        var chipWidth = LgUi.TypeChips(drawList, new Vector2(textX, min.Y + 54f * scale), wild.Element,
            wild.SecondaryElement, scale);
        var alreadyCaught = State.Party.Concat(State.Box).Any(monster => monster.Species.Id == wild.Species.Id);
        Typography.Draw(new Vector2(textX + chipWidth + 8f * scale, min.Y + 57f * scale),
            alreadyCaught ? "Caught before" : "New capture", alreadyCaught ? theme.TextMuted : Accent,
            TextStyles.Caption2);

        var engageButton = CenteredAt(new Vector2(max.X - 56f * scale, max.Y - 22f * scale),
            new Vector2(88f * scale, 30f * scale));
        if (LgUi.Button(engageButton, "Engage", Accent, theme, true))
        {
            Engage(wild);
        }

        var passButton = CenteredAt(new Vector2(max.X - 148f * scale, max.Y - 22f * scale),
            new Vector2(82f * scale, 30f * scale));
        if (LgUi.Button(passButton, "Pass", GamePalette.Cell, theme, true))
        {
            State.Pending = null;
        }
    }

    private void DrawWipedBanner(Rect content, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var danger = theme.Danger;
        var min = new Vector2(content.Min.X + 16f * scale, content.Min.Y + 196f * scale);
        var max = new Vector2(content.Max.X - 16f * scale, content.Min.Y + 292f * scale);
        LgUi.Card(drawList, min, max, 16f * scale, scale);
        Squircle.Stroke(drawList, min, max, 16f * scale, ImGui.GetColorU32(danger with { W = 0.7f }), 1.4f * scale);

        var iconCenter = new Vector2(min.X + 40f * scale, min.Y + 30f * scale);
        ProgressRing.CenterIcon(drawList, iconCenter, FontAwesomeIcon.HeartBroken, danger, 20f * scale);
        Typography.Draw(new Vector2(min.X + 72f * scale, min.Y + 14f * scale), "Your team has fainted",
            theme.TextStrong, TextStyles.Headline);
        Typography.Draw(new Vector2(min.X + 72f * scale, min.Y + 35f * scale), "No scans or battles until revived",
            danger with { W = 0.9f }, TextStyles.Caption1);

        var body = "Head to any town and open the Marketboard to revive your team at the Pokécenter — it's free.";
        var lines = WrapText(body, max.X - min.X - 28f * scale, TextStyles.Caption1);
        for (var i = 0; i < lines.Count; i++)
        {
            Typography.Draw(new Vector2(min.X + 14f * scale, min.Y + 56f * scale + i * 17f * scale), lines[i],
                theme.TextMuted, TextStyles.Caption1);
        }
    }

    private void Engage(MonsterInstance wild)
    {
        if (State.AllMonstersFainted)
        {
            return;
        }

        PrepPartyForBattle();
        battle = new Battle(State.Party, wild, State.Bag, rng);
        State.Pending = null;
        pendingGymIndex = -1;
        EnterBattle();
    }

    // Starts a randomized Training battle at the given tier.
    private void StartTraining(Training.Tier tier)
    {
        if (State.AllMonstersFainted)
        {
            return;
        }

        PrepPartyForBattle();
        var (min, max) = State.TrainingRange(tier);
        battle = Training.Build(State.Party, tier, min, max, State.Bag, rng);
        State.Pending = null;
        pendingGymIndex = -1;
        EnterBattle();
    }

    // Starts a gym-leader battle. Winning awards the gym's badge (handled in FinishBattle).
    private void StartGym(GymDef gym)
    {
        if (State.AllMonstersFainted || Array.IndexOf(gym.Territories, State.Territory) < 0)
        {
            return;
        }

        PrepPartyForBattle();
        battle = Gyms.Build(gym, State.Party, State.Bag, rng);
        State.Pending = null;
        pendingGymIndex = gym.Index;
        EnterBattle();
    }

    private void PrepPartyForBattle()
    {
        foreach (var m in State.Party)
        {
            m.ResetBattleState();
        }
    }

    // Shared setup once `battle` has been created (wild, training or gym).
    private void EnterBattle()
    {
        displayedPlayer = battle!.Active;
        SetDisplayedPlayer(BattleSnapshot.Capture(battle.Active));
        SetDisplayedWild(BattleSnapshot.Capture(battle.Wild));
        animatedPlayerHp = displayedPlayerHp;
        animatedWildHp = displayedWildHp;
        animatedPlayerXp = displayedPlayerXpFraction;
        State.InBattle = true;
        playerAnim.Reset();
        wildAnim.Reset();
        message = null;
        messageTimer = 0f;
        battleText.Clear();
        awaitingResult = false;
        battlePopups.Clear();
        battleItemScroll = 0f;
        confirmingRun = false;
        moveFx = null;
        menu = Menu.Root;
        view = View.Battle;
    }

    private void DrawPartyStrip(Rect content, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var mouse = ImGui.GetMousePos();
        var y = content.Max.Y - 76f * scale;
        var slot = (content.Width - 24f * scale) / 6f;
        for (var i = 0; i < 6; i++)
        {
            var cx = content.Min.X + 12f * scale + slot * (i + 0.5f);
            var center = new Vector2(cx, y);
            var radius = slot * 0.4f;
            var filled = i < State.Party.Count;
            var hovered = filled && Vector2.DistanceSquared(mouse, center) <= radius * radius;
            if (hovered)
            {
                ProgressRing.Glow(center, radius * 1.05f, Accent, 0.4f);
            }

            drawList.AddCircleFilled(center + new Vector2(0f, 1.5f * scale), radius,
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.28f)));
            drawList.AddCircleFilled(center, radius,
                ImGui.GetColorU32(hovered ? GamePalette.CellHover : filled ? GamePalette.Cell : GamePalette.CellSunken));
            drawList.AddCircle(center, radius, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.09f)), 32, 1f * scale);
            if (filled)
            {
                var m = State.Party[i];
                MonsterArt.Draw(drawList, center, slot * 0.26f, m.Species, 1f,
                    new MonsterPose(time + i, 0f, 0f, 1f, m.Fainted));
                if (m.IsFavorite)
                {
                    drawList.AddCircle(center, radius * 0.92f, ImGui.GetColorU32(Accent with { W = 0.9f }), 24,
                        1.8f * scale);
                }
                LgUi.HpBar(drawList, new Vector2(cx - slot * 0.34f, y + slot * 0.42f),
                    new Vector2(cx + slot * 0.34f, y + slot * 0.5f), m.HpFraction);
                if (hovered)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.SetTooltip(BuildMonsterTooltip(m, "Click to open this creature's profile."));
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        OpenDetail(m, View.Map);
                    }
                }
            }
        }
    }

}
