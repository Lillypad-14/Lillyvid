using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using VideoSyncPrototype.Phone.Apps.Games.Framework;
using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Core.Theme;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal sealed partial class LillypadGoApp
{
    // ---- Options ---------------------------------------------------------------------
    // Navy/cream chrome to match the rest of the app: a gear ScreenHeader over a CreamPanel
    // holding a scrollable column of DarkCard sections (Overworld / Battle effects / Battle
    // playback / Save data), each with an accent bar, an icon tile and RosterUi controls.

    private const float MinBattleEffectScale = 0.75f;
    private const float DefaultBattleEffectScale = 1.25f;
    private const float MaxBattleEffectScale = 2f;

    private bool confirmingDeleteSave;
    private bool showDebugPanel;
    private string debugStatus = string.Empty;
    private float optionsScroll;
    private bool battleSpeedSliderActive;

    // The debug menu is gated so casual testers can't reach it. Once the password is entered the
    // gate stays open for the rest of the session (until the app/plugin reloads).
    private const string DebugPassword = "Lilly123";
    private bool debugUnlocked;
    private bool showDebugPrompt;
    private string debugPasswordDraft = string.Empty;
    private bool debugPasswordError;

    private void DrawOptions(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        if (showDebugPrompt)
        {
            DrawDebugPrompt(content, theme, scale);
            return;
        }

        if (showDebugPanel)
        {
            DrawDebugPanel(content, theme, scale);
            return;
        }

        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(RosterUi.NavyBottom));
        var headerBottom = RosterUi.ScreenHeader(content, "OPTIONS", "nav_settings", null, scale);

        // Debug launcher — a subdued capsule on the header's right edge. Prompts for the password
        // the first time it's opened each session, then goes straight in afterwards.
        var debugRect = CenteredAt(new Vector2(content.Max.X - 32f * scale, content.Min.Y + 23f * scale),
            new Vector2(44f * scale, 22f * scale));
        if (RosterUi.ColorButton(debugRect, "DEV", RosterUi.NavyInset, scale, true))
        {
            debugStatus = string.Empty;
            if (debugUnlocked)
            {
                showDebugPanel = true;
            }
            else
            {
                showDebugPrompt = true;
                debugPasswordDraft = string.Empty;
                debugPasswordError = false;
            }
        }

        var navTop = content.Max.Y - NavBarHeight * scale;
        var panel = new Rect(new Vector2(content.Min.X + 7f * scale, headerBottom + 6f * scale),
            new Vector2(content.Max.X - 7f * scale, navTop - 7f * scale));
        RosterUi.CreamPanel(drawList, panel, scale);

        var list = new Rect(new Vector2(panel.Min.X + 9f * scale, panel.Min.Y + 9f * scale),
            new Vector2(panel.Max.X - 9f * scale, panel.Max.Y - 9f * scale));
        var sections = new (float Height, Action<Rect> Draw)[]
        {
            (184f * scale, card => DrawOverworldSection(card, scale)),
            (146f * scale, card => DrawEffectsSection(card, scale)),
            (142f * scale, card => DrawPlaybackSection(card, scale)),
            (104f * scale, card => DrawSaveDataSection(card, scale)),
        };

        DrawSectionColumn(list, sections, ref optionsScroll, scale);

        // The slider writes every frame it's dragged; persist once, when the drag ends.
        if (effectScaleSliderActive && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            effectScaleSliderActive = false;
            State.Save();
        }

        DrawNavigation(content, theme, scale);
    }

    // A clipped, wheel-scrollable column of variable-height section cards. Mirrors DrawScrollList
    // (which only takes uniform rows) — cards are culled off screen and their controls are frozen
    // while the cursor is outside the list so nothing reacts through the clip edges.
    private void DrawSectionColumn(Rect area, (float Height, Action<Rect> Draw)[] sections, ref float scroll,
        float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var gap = 10f * scale;
        var contentHeight = -gap;
        foreach (var (height, _) in sections)
        {
            contentHeight += height + gap;
        }

        var maxScroll = MathF.Max(0f, contentHeight - area.Height);
        var mouseInArea = LgUi.Interactive && ImGui.IsMouseHoveringRect(area.Min, area.Max);
        if (mouseInArea)
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0f)
            {
                scroll -= wheel * 40f * scale;
            }
        }

        scroll = Math.Clamp(scroll, 0f, maxScroll);

        var right = maxScroll > 0f ? area.Max.X - 8f * scale : area.Max.X;
        var previousInteractive = LgUi.Interactive;
        drawList.PushClipRect(area.Min, area.Max, true);
        var y = area.Min.Y - scroll;
        foreach (var (height, draw) in sections)
        {
            if (y + height >= area.Min.Y - 2f && y <= area.Max.Y + 2f)
            {
                LgUi.Interactive = previousInteractive && mouseInArea;
                draw(new Rect(new Vector2(area.Min.X, y), new Vector2(right, y + height)));
            }

            y += height + gap;
        }

        LgUi.Interactive = previousInteractive;
        drawList.PopClipRect();

        if (maxScroll > 0f)
        {
            var track = new Rect(new Vector2(area.Max.X - 4f * scale, area.Min.Y),
                new Vector2(area.Max.X - 1f * scale, area.Max.Y));
            LgUi.Scrollbar(track, scroll, maxScroll, area.Height / MathF.Max(contentHeight, 1f), Accent, scale);
        }
    }

    // The shared shell of an options section: navy card, accent bar, icon tile, title + blurb.
    // Returns the inner content rect below the title block.
    private static Rect DrawSectionCard(Rect card, string title, string blurb, Vector4 accent, FontAwesomeIcon icon,
        float scale, float trailingWidth = 0f)
    {
        var drawList = ImGui.GetWindowDrawList();
        RosterUi.DarkCard(drawList, card, 10f * scale, scale, accent: accent);

        var iconCenter = new Vector2(card.Min.X + 28f * scale, card.Min.Y + 25f * scale);
        RosterUi.IconTile(drawList, iconCenter, 32f * scale, scale, accent with { W = 0.6f });
        ProgressRing.CenterIcon(drawList, iconCenter, icon, accent, 13f * scale);

        var textLeft = card.Min.X + 50f * scale;
        var textWidth = card.Max.X - textLeft - 12f * scale - trailingWidth;
        Typography.Draw(new Vector2(textLeft, card.Min.Y + 8f * scale),
            RosterUi.Ellipsize(title, textWidth, TextStyles.Headline), RosterUi.CardInk, TextStyles.Headline);
        Typography.Draw(new Vector2(textLeft, card.Min.Y + 29f * scale),
            FitLabel(blurb, textWidth, TextStyles.Caption2), RosterUi.CardMuted, TextStyles.Caption2);

        return new Rect(new Vector2(card.Min.X + 14f * scale, card.Min.Y + 52f * scale),
            new Vector2(card.Max.X - 14f * scale, card.Max.Y - 8f * scale));
    }

    private void DrawOverworldSection(Rect card, float scale)
    {
        var inner = DrawSectionCard(card, "Overworld", "Find wild encounters while the phone or app is closed.",
            RosterUi.GreenBright, FontAwesomeIcon.Compass, scale);

        var rowHeight = 30f * scale;
        var tracking = State.BackgroundTrackingEnabled;
        if (DrawToggleRow(RowAt(inner, 0, rowHeight), scale, ref tracking, "Run Lillypad Go in the background"))
        {
            State.BackgroundTrackingEnabled = tracking;
            State.Save();
        }

        var follower = State.FollowerEnabled;
        if (DrawToggleRow(RowAt(inner, 1, rowHeight), scale, ref follower, "Lead Pokémon follows you in the world"))
        {
            State.FollowerEnabled = follower;
            State.Save();
        }

        var worldBattles = State.WorldBattlesEnabled;
        if (DrawToggleRow(RowAt(inner, 2, rowHeight), scale, ref worldBattles, "Battles also play out in the world"))
        {
            State.WorldBattlesEnabled = worldBattles;
            State.Save();
        }

        // Opt-in immersive mode. The hover warning spells out the key capture so nobody is
        // surprised that 1–8 stop reaching their game hotbar mid-battle.
        var immersive = State.ImmersiveModeEnabled;
        var immersiveRow = RowAt(inner, 3, rowHeight);
        if (DrawToggleRow(immersiveRow, scale, ref immersive, "Immerse mode — hunt & battle in the world"))
        {
            State.ImmersiveModeEnabled = immersive;
            State.Save();
        }

        if (LgUi.Interactive && ImGui.IsMouseHoveringRect(immersiveRow.Min, immersiveRow.Max))
        {
            ShowTooltip("Immerse mode (opt-in)\n\n" +
                "Wild Pokémon spawn around you in the game world — click one to engage. Battles run " +
                "from an on-screen battle hotbar: slots 1–4 are your lead's moves (hover for full move " +
                "info), 5 Bag, 6 Swap, 7 Run, 8 opens the phone.\n\n" +
                "⚠ WARNING: while a battle is active, keyboard keys 1–8 are captured for these battle " +
                "actions and will NOT trigger your normal game hotbar. Your real hotbars are never " +
                "modified or overwritten — the capture ends the moment the battle does.\n\n" +
                "The phone keeps working normally alongside this mode.");
        }
    }

    private void DrawEffectsSection(Rect card, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var valueText = $"{MathF.Round(State.BattleEffectScale * 100f)}%";
        var inner = DrawSectionCard(card, "Battle effects", "Size of attack particles and impact visuals.",
            RosterUi.CountBlue, FontAwesomeIcon.Magic, scale, 54f * scale);
        RosterUi.Pill(drawList, new Vector2(card.Max.X - 34f * scale, card.Min.Y + 20f * scale),
            new[] { (valueText, RosterUi.CountBlue) }, TextStyles.FootnoteEmphasized, scale);

        var value = Math.Clamp(State.BattleEffectScale, MinBattleEffectScale, MaxBattleEffectScale);
        var sliderRect = new Rect(inner.Min, new Vector2(inner.Max.X, inner.Min.Y + 28f * scale));
        if (DrawEffectScaleSlider(sliderRect, scale, ref value))
        {
            State.BattleEffectScale = value;
        }

        var resetRect = new Rect(new Vector2(inner.Min.X, inner.Max.Y - 28f * scale),
            new Vector2(inner.Min.X + 96f * scale, inner.Max.Y));
        if (RosterUi.ColorButton(resetRect, "RESET", RosterUi.Blue, scale,
                MathF.Abs(State.BattleEffectScale - DefaultBattleEffectScale) > 0.001f))
        {
            State.BattleEffectScale = DefaultBattleEffectScale;
            State.Save();
        }

        Typography.Draw(new Vector2(resetRect.Max.X + 10f * scale, resetRect.Center.Y - 7f * scale),
            FitLabel($"Default {MathF.Round(DefaultBattleEffectScale * 100f)}%", inner.Max.X - resetRect.Max.X - 12f * scale,
                TextStyles.Caption2), RosterUi.CardMuted, TextStyles.Caption2);
    }

    private void DrawPlaybackSection(Rect card, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var speedText = $"{State.BattleSpeed:0.0}x";
        var inner = DrawSectionCard(card, "Battle playback", "Message pacing, and an optional history of the fight.",
            RosterUi.Purple, FontAwesomeIcon.Play, scale, 54f * scale);
        RosterUi.Pill(drawList, new Vector2(card.Max.X - 34f * scale, card.Min.Y + 20f * scale),
            new[] { (speedText, RosterUi.CardInk) }, TextStyles.FootnoteEmphasized, scale);

        var speed = Math.Clamp(State.BattleSpeed, 0.5f, 2.5f);
        var speedRect = new Rect(inner.Min, new Vector2(inner.Max.X, inner.Min.Y + 28f * scale));
        if (DrawBattleSpeedSlider(speedRect, scale, ref speed))
        {
            State.BattleSpeed = speed;
            State.Save();
        }

        var history = State.BattleLogEnabled;
        var historyRect = new Rect(new Vector2(inner.Min.X, inner.Max.Y - 30f * scale), inner.Max);
        if (DrawToggleRow(historyRect, scale, ref history, "Keep battle log/history"))
        {
            State.BattleLogEnabled = history;
            State.Save();
        }
    }

    // Save data: wipe progress and return to starter selection. Two taps to confirm the wipe.
    private void DrawSaveDataSection(Rect card, float scale)
    {
        var inner = DrawSectionCard(card, "Save data",
            confirmingDeleteSave
                ? "Erase your team, items and progress? This cannot be undone."
                : "Erase all progress and start over from the starter choice.",
            RosterUi.Red, FontAwesomeIcon.TrashAlt, scale);

        var deleteRect = new Rect(new Vector2(inner.Min.X, inner.Max.Y - 30f * scale),
            new Vector2(inner.Min.X + 132f * scale, inner.Max.Y));
        if (RosterUi.ColorButton(deleteRect, confirmingDeleteSave ? "CONFIRM DELETE" : "DELETE SAVE", RosterUi.Red,
                scale, true))
        {
            if (confirmingDeleteSave)
            {
                DeleteSaveAndReturnToStarter();
            }
            else
            {
                confirmingDeleteSave = true;
            }
        }

        if (!confirmingDeleteSave)
        {
            return;
        }

        var cancelRect = new Rect(new Vector2(deleteRect.Max.X + 10f * scale, deleteRect.Min.Y),
            new Vector2(deleteRect.Max.X + 100f * scale, deleteRect.Max.Y));
        if (RosterUi.ColorButton(cancelRect, "CANCEL", RosterUi.Blue, scale, true))
        {
            confirmingDeleteSave = false;
        }
    }

    private static Rect RowAt(Rect inner, int index, float rowHeight) =>
        new(new Vector2(inner.Min.X, inner.Min.Y + index * rowHeight),
            new Vector2(inner.Max.X, inner.Min.Y + (index + 1) * rowHeight));

    // The password gate in front of the debug menu. Modal sub-view of Options, sharing the same
    // navy/cream chrome; a correct entry unlocks the menu for the rest of the session.
    private void DrawDebugPrompt(Rect content, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(RosterUi.NavyBottom));
        var headerBottom = RosterUi.ScreenHeader(content, "DEBUG ACCESS", "nav_settings", null, scale);

        var navTop = content.Max.Y - NavBarHeight * scale;
        var panel = new Rect(new Vector2(content.Min.X + 7f * scale, headerBottom + 6f * scale),
            new Vector2(content.Max.X - 7f * scale, navTop - 7f * scale));
        RosterUi.CreamPanel(drawList, panel, scale);

        var card = new Rect(new Vector2(panel.Min.X + 9f * scale, panel.Min.Y + 9f * scale),
            new Vector2(panel.Max.X - 9f * scale, panel.Min.Y + 145f * scale));
        var inner = DrawSectionCard(card, "Password", "Enter the password to open the testing menu.",
            RosterUi.Gold, FontAwesomeIcon.Lock, scale);

        var inputRect = new Rect(inner.Min, new Vector2(inner.Max.X, inner.Min.Y + 30f * scale));
        var submitted = RosterUi.TextField(inputRect, "##debug-password", "Password", ref debugPasswordDraft, 32, scale);

        if (debugPasswordError)
        {
            Typography.Draw(new Vector2(inner.Min.X, inputRect.Max.Y + 4f * scale), "Incorrect password.",
                RosterUi.Red, TextStyles.Caption2);
        }

        var unlockRect = new Rect(new Vector2(inner.Min.X, inner.Max.Y - 30f * scale),
            new Vector2(inner.Min.X + 96f * scale, inner.Max.Y));
        var unlock = RosterUi.ColorButton(unlockRect, "UNLOCK", RosterUi.Green, scale, true);

        var cancelRect = new Rect(new Vector2(unlockRect.Max.X + 10f * scale, unlockRect.Min.Y),
            new Vector2(unlockRect.Max.X + 106f * scale, unlockRect.Max.Y));
        if (RosterUi.ColorButton(cancelRect, "CANCEL", RosterUi.Blue, scale, true))
        {
            showDebugPrompt = false;
            debugPasswordDraft = string.Empty;
            debugPasswordError = false;
        }

        if (unlock || submitted)
        {
            if (debugPasswordDraft == DebugPassword)
            {
                debugUnlocked = true;
                showDebugPrompt = false;
                showDebugPanel = true;
                debugPasswordDraft = string.Empty;
                debugPasswordError = false;
            }
            else
            {
                debugPasswordError = true;
            }
        }

        DrawNavigation(content, theme, scale);
    }

    // A quick cheat panel for testing: full-heal, max gil, all items/TMs/badges, and resetting the
    // Alpha respawn timers. Drawn as a modal sub-view of Options so the normal controls never run
    // underneath it (no click-through), while the bottom nav stays available to leave the tab.
    private void DrawDebugPanel(Rect content, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(RosterUi.NavyBottom));
        var headerBottom = RosterUi.ScreenHeader(content, "DEBUG MENU", "nav_settings",
            new[] { ("Testing cheats — applied instantly", RosterUi.CardMuted) }, scale);

        var actions = new (string Label, Vector4 Color, Action Do)[]
        {
            ("FULL HEAL", RosterUi.Green, () =>
            {
                State.HealAllMonsters();
                debugStatus = "Party and box fully healed.";
            }),
            ("MAX GIL", RosterUi.Gold, () =>
            {
                State.Money = 999_999;
                State.Save();
                debugStatus = "Gil set to 999,999.";
            }),
            ("ALL ITEMS", RosterUi.Blue, () =>
            {
                foreach (var item in Items.All)
                {
                    State.Bag.Add(item.Id, 99);
                }

                State.Save();
                debugStatus = $"Added x99 of every item ({Items.All.Count} kinds).";
            }),
            ("ALL TMS", RosterUi.Purple, () =>
            {
                foreach (var tm in Tms.All)
                {
                    State.OwnedTms.Add(tm.MoveId);
                }

                State.Save();
                debugStatus = $"Unlocked all {Tms.All.Count} TMs.";
            }),
            ("ALL BADGES", new Vector4(0.95f, 0.62f, 0.24f, 1f), () =>
            {
                foreach (var gym in Gyms.All)
                {
                    State.EarnBadge(gym.Index);
                }

                debugStatus = $"Earned all {Gyms.All.Count} gym badges.";
            }),
            ("RESPAWN ALPHAS", new Vector4(0.62f, 0.5f, 0.95f, 1f), () =>
            {
                State.DebugRespawnAlphas();
                debugStatus = "All region Alphas are alive again.";
            }),
        };

        var navTop = content.Max.Y - NavBarHeight * scale;
        var panel = new Rect(new Vector2(content.Min.X + 7f * scale, headerBottom + 6f * scale),
            new Vector2(content.Max.X - 7f * scale, navTop - 7f * scale));
        RosterUi.CreamPanel(drawList, panel, scale);

        const int cols = 2;
        var pad = 10f * scale;
        var gap = 8f * scale;
        var gridMin = panel.Min + new Vector2(pad, pad);
        var gridMax = new Vector2(panel.Max.X - pad, panel.Max.Y - 76f * scale);
        var rows = (actions.Length + cols - 1) / cols;
        var cellW = (gridMax.X - gridMin.X - gap * (cols - 1)) / cols;
        // Clamp the row height so the grid still fits (and the Close button stays reachable) on
        // short content boxes / high UI scales.
        var cellH = MathF.Min(44f * scale, (gridMax.Y - gridMin.Y - gap * (rows - 1)) / rows);
        for (var i = 0; i < actions.Length; i++)
        {
            var min = new Vector2(gridMin.X + i % cols * (cellW + gap), gridMin.Y + i / cols * (cellH + gap));
            var rect = new Rect(min, min + new Vector2(cellW, cellH));
            if (rect.Max.Y > gridMax.Y + 1f)
            {
                break;
            }

            if (RosterUi.ColorButton(rect, actions[i].Label, actions[i].Color, scale, true))
            {
                actions[i].Do();
            }
        }

        if (!string.IsNullOrEmpty(debugStatus))
        {
            Typography.DrawCentered(new Vector2(panel.Center.X, panel.Max.Y - 56f * scale),
                FitLabel(debugStatus, panel.Width - 24f * scale, TextStyles.Caption1), RosterUi.InkNavy,
                TextStyles.Caption1);
        }

        var closeRect = CenteredAt(new Vector2(panel.Center.X, panel.Max.Y - 26f * scale),
            new Vector2(140f * scale, 30f * scale));
        if (RosterUi.ColorButton(closeRect, "CLOSE", RosterUi.Blue, scale, true))
        {
            showDebugPanel = false;
            debugStatus = string.Empty;
        }

        DrawNavigation(content, theme, scale);
    }

    // Wipes the save and resets every screen's transient state so the app cleanly re-enters starter
    // selection as if freshly installed.
    private void DeleteSaveAndReturnToStarter()
    {
        showDebugPanel = false;
        showDebugPrompt = false;
        debugPasswordDraft = string.Empty;
        debugPasswordError = false;
        debugStatus = string.Empty;
        State.DeleteSaveAndReset();
        battle = null;
        displayedPlayer = null;
        awaitingResult = false;
        resultShownAt = -1f;
        captureFx = null;
        menu = Menu.Root;
        confirmingRun = false;
        pendingGymIndex = -1;
        starterCandidate = null;
        bagUseItem = null;
        bagStatus = string.Empty;
        confirmingDeleteSave = false;
        optionsScroll = 0f;
        view = View.Starter;
    }

    // A chunky switch in the kit's language: label on the left, a navy→green capsule with a cream
    // knob on the right. Returns true on the frame the value flips.
    private bool DrawToggleRow(Rect rect, float scale, ref bool value, string label)
    {
        var drawList = ImGui.GetWindowDrawList();
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);

        var trackW = 42f * scale;
        var trackH = 22f * scale;
        var trackMin = new Vector2(rect.Max.X - trackW, rect.Center.Y - trackH * 0.5f);
        var trackMax = new Vector2(rect.Max.X, rect.Center.Y + trackH * 0.5f);
        var radius = trackH * 0.5f;

        var fill = value ? RosterUi.Green : RosterUi.NavyInset;
        Squircle.FillVerticalGradient(drawList, trackMin, trackMax, radius,
            ImGui.GetColorU32(GamePalette.Lighten(fill, hovered ? 0.16f : 0.08f)),
            ImGui.GetColorU32(GamePalette.Darken(fill, hovered ? 0.04f : 0.10f)));
        Squircle.Stroke(drawList, trackMin, trackMax, radius,
            ImGui.GetColorU32(value ? RosterUi.GreenBright with { W = 0.9f } : RosterUi.CardEdge with { W = 0.8f }),
            1.6f * scale);

        var knobR = radius - 3f * scale;
        var knobX = value ? trackMax.X - radius : trackMin.X + radius;
        var knobCenter = new Vector2(knobX, rect.Center.Y);
        drawList.AddCircleFilled(knobCenter + new Vector2(0f, 1f * scale), knobR,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.25f)), 20);
        drawList.AddCircleFilled(knobCenter, knobR, ImGui.GetColorU32(value ? RosterUi.Cream : RosterUi.CreamShade), 20);
        drawList.AddCircle(knobCenter, knobR, ImGui.GetColorU32(RosterUi.NavyEdge with { W = 0.6f }), 20, 1f * scale);

        Typography.Draw(new Vector2(rect.Min.X, rect.Center.Y - 8f * scale),
            FitLabel(label, trackMin.X - rect.Min.X - 10f * scale, TextStyles.Subheadline),
            hovered ? RosterUi.CardInk : RosterUi.CardMuted, TextStyles.Subheadline);

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        if (!hovered || !ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            return false;
        }

        value = !value;
        return true;
    }

    // Shared slider chrome for the two options sliders: an inset navy track with a coloured fill,
    // a cream knob, and the range end-labels underneath.
    private static bool Slider(Rect rect, float scale, Vector4 accent, string minLabel, string maxLabel, float t,
        out float newT)
    {
        var drawList = ImGui.GetWindowDrawList();
        var trackMin = new Vector2(rect.Min.X, rect.Min.Y + 5f * scale);
        var trackMax = new Vector2(rect.Max.X, rect.Min.Y + 13f * scale);
        var radius = 4f * scale;
        var knob = new Vector2(trackMin.X + (trackMax.X - trackMin.X) * t, (trackMin.Y + trackMax.Y) * 0.5f);

        Squircle.Fill(drawList, trackMin, trackMax, radius, ImGui.GetColorU32(RosterUi.NavyInset));
        Squircle.Fill(drawList, trackMin, new Vector2(knob.X, trackMax.Y), radius,
            ImGui.GetColorU32(GamePalette.Lighten(accent, 0.05f)));
        Squircle.Stroke(drawList, trackMin, trackMax, radius,
            ImGui.GetColorU32(RosterUi.CardEdge with { W = 0.8f }), 1.4f * scale);

        var knobR = 9f * scale;
        drawList.AddCircleFilled(knob + new Vector2(0f, 1.5f * scale), knobR,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.25f)), 24);
        drawList.AddCircleFilled(knob, knobR, ImGui.GetColorU32(RosterUi.Cream), 24);
        drawList.AddCircle(knob, knobR, ImGui.GetColorU32(RosterUi.NavyEdge with { W = 0.85f }), 24, 1.6f * scale);

        Typography.Draw(new Vector2(rect.Min.X, rect.Max.Y - 12f * scale), minLabel, RosterUi.CardMuted,
            TextStyles.Caption2);
        Typography.Draw(
            new Vector2(rect.Max.X - Typography.Measure(maxLabel, TextStyles.Caption2).X, rect.Max.Y - 12f * scale),
            maxLabel, RosterUi.CardMuted, TextStyles.Caption2);

        newT = Math.Clamp((ImGui.GetMousePos().X - trackMin.X) / MathF.Max(1f, trackMax.X - trackMin.X), 0f, 1f);
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        return hovered;
    }

    private bool DrawEffectScaleSlider(Rect rect, float scale, ref float value)
    {
        var t = Math.Clamp((value - MinBattleEffectScale) / (MaxBattleEffectScale - MinBattleEffectScale), 0f, 1f);
        var hovered = Slider(rect, scale, RosterUi.CountBlue, "75%", "200%", t, out var newT);
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            effectScaleSliderActive = true;
        }

        if (!effectScaleSliderActive || !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            return false;
        }

        var next = MinBattleEffectScale + newT * (MaxBattleEffectScale - MinBattleEffectScale);
        if (MathF.Abs(next - value) <= 0.001f)
        {
            return false;
        }

        value = next;
        return true;
    }

    private bool DrawBattleSpeedSlider(Rect rect, float scale, ref float value)
    {
        var t = Math.Clamp((value - 0.5f) / 2f, 0f, 1f);
        var hovered = Slider(rect, scale, RosterUi.Purple, "0.5x", "2.5x", t, out var newT);
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            battleSpeedSliderActive = true;
        }

        if (battleSpeedSliderActive && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            battleSpeedSliderActive = false;
        }

        if (!battleSpeedSliderActive)
        {
            return false;
        }

        var next = 0.5f + newT * 2f;
        if (MathF.Abs(next - value) <= 0.001f)
        {
            return false;
        }

        value = next;
        return true;
    }
}
