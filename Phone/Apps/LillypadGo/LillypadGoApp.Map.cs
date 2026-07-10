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
        var subtitle = zone is null
            ? new[] { (Biomes.Name(State.CurrentBiome), new Vector4(1f, 1f, 1f, 1f)) }
            : new[]
            {
                (FitLabel(zone.Name, content.Width * 0.55f, TextStyles.FootnoteEmphasized),
                    new Vector4(1f, 1f, 1f, 1f)),
                ("|", RosterUi.NavyLine),
                (zone.LevelLabel, RosterUi.CountGreen),
            };
        RosterUi.ScreenHeader(content, "LILLYPAD GO", "logo_ball", subtitle, scale);

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
            // A pending wild owns the card slot; the resident Alpha collapses into a slim strip
            // beneath it so the two never fight over the same pixels.
            DrawEncounterCard(content, theme, wild, scale);
            if (Alphas.ForTerritory(State.Territory) is { Species: not null } alphaHere)
            {
                DrawAlphaStrip(content, theme, alphaHere, scale);
            }
        }
        else if (Alphas.ForTerritory(State.Territory) is { Species: not null } alphaDef)
        {
            DrawAlphaLairCard(content, theme, alphaDef, scale);
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

    // The region Alpha's lair, drawn in the encounter-card slot whenever the player stands in its
    // territory (and no ordinary wild is pending). The boss dens at a fixed spot: Flag pins the
    // lair on the in-game map (the same marker Player Search drops), and Challenge only unlocks
    // once the player has physically walked to within Alphas.ChallengeRadius of it. Defeated:
    // a quiet marker counting down to its return.
    private void DrawAlphaLairCard(Rect content, PhoneTheme theme, AlphaDef alphaDef, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var species = alphaDef.Species!;
        var traitColor = Alphas.TraitColor(alphaDef.Trait);
        var alive = State.IsAlphaAlive(alphaDef.Id);
        var cleared = State.AlphaFirstCleared(alphaDef.Id);
        var distance = Alphas.DistanceToLair(alphaDef, State.PlayerPosition);
        var inRange = alive && distance is { } near && near <= Alphas.ChallengeRadius;
        var min = new Vector2(content.Min.X + 16f * scale, content.Min.Y + 190f * scale);
        var max = new Vector2(content.Max.X - 16f * scale, content.Min.Y + 300f * scale);
        LgUi.Card(drawList, min, max, 16f * scale, scale, sunken: !alive);
        Squircle.Stroke(drawList, min, max, 16f * scale,
            ImGui.GetColorU32(traitColor with { W = alive ? inRange ? 0.95f : 0.6f : 0.35f }), 1.6f * scale);

        var portrait = new Vector2(min.X + 48f * scale, min.Y + 44f * scale);
        if (alive)
        {
            DrawAlphaAura(drawList, portrait, 34f * scale, traitColor, time, inRange ? 1f : 0.55f);
        }

        MonsterArt.Draw(drawList, portrait, 34f * scale, species, -1f,
            new MonsterPose(time, 0f, 0f, alive ? 1f : 0.35f, !alive));

        var textX = min.X + 96f * scale;
        var title = FitLabel(alphaDef.DisplayName, max.X - 82f * scale - textX, TextStyles.Headline);
        Typography.Draw(new Vector2(textX, min.Y + 8f * scale), title, theme.TextStrong, TextStyles.Headline);
        if (cleared)
        {
            var titleWidth = Typography.Measure(title, TextStyles.Headline).X;
            ProgressRing.CenterIcon(drawList, new Vector2(textX + titleWidth + 14f * scale, min.Y + 18f * scale),
                FontAwesomeIcon.Trophy, RosterUi.Gold, 12f * scale);
        }

        var kills = State.AlphaKills(alphaDef.Id);
        var tally = kills > 0 ? $"x{kills}" : "Undefeated";
        var tallySize = Typography.Measure(tally, TextStyles.Caption2);
        Typography.Draw(new Vector2(max.X - 12f * scale - tallySize.X, min.Y + 11f * scale), tally,
            kills > 0 ? RosterUi.CountGreen : theme.TextMuted, TextStyles.Caption2);

        Typography.Draw(new Vector2(textX, min.Y + 28f * scale),
            $"Lv {alphaDef.Level}  ·  {Alphas.TraitName(alphaDef.Trait)}", traitColor, TextStyles.Caption1);
        Typography.Draw(new Vector2(textX, min.Y + 44f * scale),
            FitLabel($"{alphaDef.Lair} — {Alphas.TraitBlurb(alphaDef.Trait)}", max.X - 12f * scale - textX,
                TextStyles.Caption2), theme.TextMuted, TextStyles.Caption2);

        // Where the den is and how far away the player stands. Green once inside challenge range.
        var (locationText, locationColor) = !alive
            ? ($"Lair at {alphaDef.CoordsLabel}", theme.TextMuted)
            : inRange
                ? ("You stand in its domain — challenge it!", RosterUi.CountGreen)
                : distance is { } far
                    ? ($"Lair at {alphaDef.CoordsLabel}  ·  {far:0} yalms away", theme.TextMuted)
                    : ($"Lair at {alphaDef.CoordsLabel}", theme.TextMuted);
        Typography.Draw(new Vector2(textX, min.Y + 60f * scale),
            FitLabel(locationText, max.X - 12f * scale - textX, TextStyles.Caption2), locationColor,
            TextStyles.Caption2);

        // Flag drops the map pin; it works alive or not so the spot can be marked for later.
        var flag = CenteredAt(new Vector2(max.X - 156f * scale, max.Y - 21f * scale),
            new Vector2(58f * scale, 28f * scale));
        if (LgUi.Button(flag, "Flag", GamePalette.Cell, theme, true))
        {
            Alphas.TryFlagLair(alphaDef);
        }

        if (ImGui.IsMouseHoveringRect(flag.Min, flag.Max))
        {
            ShowTooltip("Opens your map with a flag pinned on the lair.");
        }

        if (alive)
        {
            var challenge = CenteredAt(new Vector2(max.X - 66f * scale, max.Y - 21f * scale),
                new Vector2(100f * scale, 28f * scale));
            if (LgUi.Button(challenge, "Challenge", traitColor, theme, inRange && !State.AllMonstersFainted))
            {
                EngageAlpha(alphaDef);
            }

            if (ImGui.IsMouseHoveringRect(challenge.Min, challenge.Max) && !inRange)
            {
                ShowTooltip(distance is { } d
                    ? $"Too far from the lair — get within {Alphas.ChallengeRadius:0} yalms ({d:0} away)."
                    : $"Travel to the lair at {alphaDef.CoordsLabel} to challenge it.");
            }
        }
        else
        {
            Typography.Draw(new Vector2(min.X + 14f * scale, max.Y - 30f * scale),
                $"Returns in {FormatRespawn(State.AlphaRespawnIn(alphaDef.Id))}",
                new Vector4(0.93f, 0.76f, 0.36f, 1f), TextStyles.Caption1);
        }

        if (ImGui.IsMouseHoveringRect(min, new Vector2(max.X, max.Y - 40f * scale)))
        {
            ShowTooltip(BuildAlphaTooltip(alphaDef, alive));
        }
    }

    // Slim companion banner for the resident Alpha while a wild encounter occupies the main card
    // slot: portrait, live status line, and one context action — Challenge when standing at the
    // lair, otherwise Flag to pin it on the map. On short windows it drops rather than colliding
    // with the party strip below.
    private void DrawAlphaStrip(Rect content, PhoneTheme theme, AlphaDef alphaDef, float scale)
    {
        var min = new Vector2(content.Min.X + 16f * scale, content.Min.Y + 308f * scale);
        var max = new Vector2(content.Max.X - 16f * scale, content.Min.Y + 352f * scale);
        if (max.Y > content.Max.Y - 112f * scale)
        {
            return; // not enough headroom above the party strip
        }

        var drawList = ImGui.GetWindowDrawList();
        var species = alphaDef.Species!;
        var traitColor = Alphas.TraitColor(alphaDef.Trait);
        var alive = State.IsAlphaAlive(alphaDef.Id);
        var distance = Alphas.DistanceToLair(alphaDef, State.PlayerPosition);
        var inRange = alive && distance is { } near && near <= Alphas.ChallengeRadius;
        LgUi.Card(drawList, min, max, 12f * scale, scale, sunken: !alive);
        Squircle.Stroke(drawList, min, max, 12f * scale,
            ImGui.GetColorU32(traitColor with { W = alive ? inRange ? 0.85f : 0.5f : 0.25f }), 1.3f * scale);

        var portrait = new Vector2(min.X + 24f * scale, (min.Y + max.Y) * 0.5f);
        if (alive)
        {
            DrawAlphaAura(drawList, portrait, 15f * scale, traitColor, time, inRange ? 0.9f : 0.45f);
        }

        MonsterArt.Draw(drawList, portrait, 14f * scale, species, -1f,
            new MonsterPose(time, 0f, 0f, alive ? 1f : 0.35f, !alive));

        var textX = min.X + 46f * scale;
        var buttonWidth = 78f * scale;
        var textLimit = max.X - buttonWidth - 22f * scale - textX;
        Typography.Draw(new Vector2(textX, min.Y + 6f * scale),
            FitLabel($"{alphaDef.DisplayName}  ·  Lv {alphaDef.Level}", textLimit,
                TextStyles.FootnoteEmphasized), theme.TextStrong, TextStyles.FootnoteEmphasized);
        var (status, statusColor) = !alive
            ? ($"Returns in {FormatRespawn(State.AlphaRespawnIn(alphaDef.Id))}",
                new Vector4(0.93f, 0.76f, 0.36f, 1f))
            : inRange
                ? ("In its domain — ready to challenge!", RosterUi.CountGreen)
                : distance is { } far
                    ? ($"Lair at {alphaDef.CoordsLabel}  ·  {far:0} yalms away", theme.TextMuted)
                    : ($"Lair at {alphaDef.CoordsLabel}", theme.TextMuted);
        Typography.Draw(new Vector2(textX, min.Y + 23f * scale),
            FitLabel(status, textLimit, TextStyles.Caption2), statusColor, TextStyles.Caption2);

        var button = CenteredAt(new Vector2(max.X - buttonWidth * 0.5f - 10f * scale, (min.Y + max.Y) * 0.5f),
            new Vector2(buttonWidth, 26f * scale));
        if (inRange)
        {
            if (LgUi.Button(button, "Challenge", traitColor, theme, !State.AllMonstersFainted))
            {
                EngageAlpha(alphaDef);
            }
        }
        else if (LgUi.Button(button, "Flag", GamePalette.Cell, theme, true))
        {
            Alphas.TryFlagLair(alphaDef);
        }

        if (ImGui.IsMouseHoveringRect(button.Min, button.Max))
        {
            ShowTooltip(inRange
                ? "Begin the Alpha challenge."
                : "Opens your map with a flag pinned on the lair.");
        }
        else if (ImGui.IsMouseHoveringRect(min, max))
        {
            ShowTooltip(BuildAlphaTooltip(alphaDef, alive));
        }
    }

    // The shared hover blurb for the map lair card and the slim strip.
    private string BuildAlphaTooltip(AlphaDef alphaDef, bool alive) =>
        $"{alphaDef.DisplayName}  ·  Lv {alphaDef.Level}\n{alphaDef.Lore}\n\n" +
        $"{Alphas.TraitName(alphaDef.Trait)}: {Alphas.TraitBlurb(alphaDef.Trait)}\n" +
        (Alphas.WeatherLabel(alphaDef.Weather) is { Length: > 0 } weather
            ? $"Its lair rages with {weather.ToLowerInvariant()}.\n"
            : string.Empty) +
        $"Possible spoils: {Alphas.DropSummary(alphaDef)}\n\n" +
        (alive
            ? $"It dens at {alphaDef.CoordsLabel} — walk within {Alphas.ChallengeRadius:0} yalms to " +
              "challenge it.\nAn Alpha cannot be caught, and retreating from it is always safe."
            : $"It was defeated recently and returns in {FormatRespawn(State.AlphaRespawnIn(alphaDef.Id))}.");

    private static string FormatRespawn(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return "moments";
        }

        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes:D2}m"
            : remaining.TotalMinutes >= 1 ? $"{remaining.Minutes}m" : "under a minute";
    }

    // Starts the Alpha challenge: builds the boss, rolls this attempt's spoils (shown on the result
    // screen, granted in FinishBattle), and opens the battle. Only reachable while physically
    // standing at the lair — the card disables the button, and this re-checks in case of drift.
    private void EngageAlpha(AlphaDef alphaDef)
    {
        if (State.AllMonstersFainted || !State.IsAlphaAlive(alphaDef.Id) ||
            !Alphas.IsWithinLair(alphaDef, State.PlayerPosition) ||
            Alphas.BuildInstance(alphaDef) is not { } alpha)
        {
            return;
        }

        PrepPartyForBattle();
        pendingAlpha = alphaDef;
        pendingAlphaFirstClear = !State.AlphaFirstCleared(alphaDef.Id);
        pendingAlphaDrops.Clear();
        pendingAlphaDrops.AddRange(Alphas.RollDrops(alphaDef, rng, pendingAlphaFirstClear, State.OwnedTms));
        State.Seen.Add(alpha.Species.Id);
        // The Alpha's themed weather rules its lair; only fall back to the zone's live weather when
        // it has none of its own.
        var weather = alphaDef.Weather != BattleWeather.None ? alphaDef.Weather : State.ZoneWeather;
        battle = new Battle(State.Party, alpha, alphaDef, State.Bag, rng, weather);
        pendingGymIndex = -1;
        EnterBattle();
    }

    private void Engage(MonsterInstance wild)
    {
        if (State.AllMonstersFainted)
        {
            return;
        }

        PrepPartyForBattle();
        var highestOwnedLevel = State.Party.Concat(State.Box).Select(monster => monster.Level).DefaultIfEmpty().Max();
        battle = new Battle(State.Party, wild, State.Bag, rng, State.ZoneWeather, highestOwnedLevel);
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
        battle = Training.Build(State.Party, tier, min, max, State.Bag, rng, State.ZoneWeather);
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
        // Cue the gym-leader intro: it plays over the battle scene for a few seconds, then the
        // leader throws out their first Pokémon and the fight begins.
        gymIntroGym = gym;
        gymIntroTimer = 2.8f;
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
        battleHistory.Clear();
        showBattleHistory = false;
        awaitingResult = false;
        resultShownAt = -1f;
        battlePopups.Clear();
        captureFx = null;
        sendOutFx = null;
        enemyAwaitingSendOut = battle.IsTrainerBattle;
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
        // Leave a little more room above the bottom navigation so the party HP readouts do not
        // get clipped by the nav bar on shorter phone layouts.
        var y = content.Max.Y - 84f * scale;
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
                    ShowTooltip(BuildMonsterTooltip(m, "Click to open this creature's profile."));
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        OpenDetail(m, View.Map);
                    }
                }
            }
        }
    }

}
