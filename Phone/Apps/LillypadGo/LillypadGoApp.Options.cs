using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using VideoSyncPrototype.Phone.Apps.Games.Framework;
using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Core.Theme;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal sealed partial class LillypadGoApp
{
    private const float MinBattleEffectScale = 0.75f;
    private const float DefaultBattleEffectScale = 1.25f;
    private const float MaxBattleEffectScale = 2f;

    private bool confirmingDeleteSave;
    private bool showDebugPanel;
    private string debugStatus = string.Empty;

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

        LgUi.Header(content, theme, Accent, "Options", "Tune Lillypad Go behavior and visuals.", scale);

        // Debug launcher — a small tap target in the header's empty right side. Prompts for the
        // password the first time it's opened each session, then goes straight in afterwards.
        var debugRect = new Rect(new Vector2(content.Max.X - 92f * scale, content.Min.Y + 16f * scale),
            new Vector2(content.Max.X - 14f * scale, content.Min.Y + 42f * scale));
        if (LgUi.Button(debugRect, "Debug", GamePalette.Cell, theme, true))
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

        var trackingMin = new Vector2(content.Min.X + 14f * scale, content.Min.Y + 68f * scale);
        var trackingMax = new Vector2(content.Max.X - 14f * scale, trackingMin.Y + 146f * scale);
        LgUi.Card(drawList, trackingMin, trackingMax, 14f * scale, scale);

        Typography.Draw(new Vector2(trackingMin.X + 14f * scale, trackingMin.Y + 12f * scale), "Overworld",
            theme.TextStrong, TextStyles.SubheadlineEmphasized);
        Typography.Draw(new Vector2(trackingMin.X + 14f * scale, trackingMin.Y + 34f * scale),
            "Find wild encounters while the phone or app is closed.", theme.TextMuted, TextStyles.Caption1);
        var tracking = State.BackgroundTrackingEnabled;
        var checkboxRect = new Rect(new Vector2(trackingMin.X + 14f * scale, trackingMin.Y + 54f * scale),
            new Vector2(trackingMax.X - 14f * scale, trackingMin.Y + 78f * scale));
        if (DrawCheckboxRow(checkboxRect, theme, scale, ref tracking, "Run Lillypad Go in the background"))
        {
            State.BackgroundTrackingEnabled = tracking;
            State.Save();
        }

        var follower = State.FollowerEnabled;
        var followerRect = new Rect(new Vector2(trackingMin.X + 14f * scale, trackingMin.Y + 84f * scale),
            new Vector2(trackingMax.X - 14f * scale, trackingMin.Y + 108f * scale));
        if (DrawCheckboxRow(followerRect, theme, scale, ref follower, "Lead Pokémon follows you in the world"))
        {
            State.FollowerEnabled = follower;
            State.Save();
        }

        var worldBattles = State.WorldBattlesEnabled;
        var worldBattlesRect = new Rect(new Vector2(trackingMin.X + 14f * scale, trackingMax.Y - 34f * scale),
            new Vector2(trackingMax.X - 14f * scale, trackingMax.Y - 10f * scale));
        if (DrawCheckboxRow(worldBattlesRect, theme, scale, ref worldBattles, "Battles also play out in the world"))
        {
            State.WorldBattlesEnabled = worldBattles;
            State.Save();
        }

        var cardMin = new Vector2(content.Min.X + 14f * scale, trackingMax.Y + 12f * scale);
        var cardMax = new Vector2(content.Max.X - 14f * scale, cardMin.Y + 138f * scale);
        LgUi.Card(drawList, cardMin, cardMax, 14f * scale, scale);

        Typography.Draw(new Vector2(cardMin.X + 14f * scale, cardMin.Y + 12f * scale), "Battle effects",
            theme.TextStrong, TextStyles.SubheadlineEmphasized);
        Typography.Draw(new Vector2(cardMin.X + 14f * scale, cardMin.Y + 34f * scale),
            "Controls the size of attack particles and impact visuals.", theme.TextMuted, TextStyles.Caption1);

        var value = Math.Clamp(State.BattleEffectScale, MinBattleEffectScale, MaxBattleEffectScale);
        var sliderRect = new Rect(new Vector2(cardMin.X + 14f * scale, cardMin.Y + 68f * scale),
            new Vector2(cardMax.X - 14f * scale, cardMin.Y + 98f * scale));
        if (DrawEffectScaleSlider(sliderRect, theme, scale, ref value))
        {
            State.BattleEffectScale = value;
        }

        if (effectScaleSliderActive && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            effectScaleSliderActive = false;
            State.Save();
        }

        var valueText = $"{MathF.Round(State.BattleEffectScale * 100f)}%";
        var valueSize = Typography.Measure(valueText, TextStyles.Caption1);
        Typography.Draw(new Vector2(cardMax.X - 14f * scale - valueSize.X, cardMin.Y + 12f * scale), valueText,
            Accent, TextStyles.Caption1);

        var resetRect = new Rect(new Vector2(cardMin.X + 14f * scale, cardMax.Y - 32f * scale),
            new Vector2(cardMin.X + 118f * scale, cardMax.Y - 8f * scale));
        if (LgUi.Button(resetRect, "Reset", GamePalette.Cell, theme,
                MathF.Abs(State.BattleEffectScale - DefaultBattleEffectScale) > 0.001f))
        {
            State.BattleEffectScale = DefaultBattleEffectScale;
            State.Save();
        }

        // Save data: wipe progress and return to starter selection. Two taps to confirm the wipe.
        // Kept as a compact row (not a full card) so it always clears the bottom navigation dock.
        var battleCardMin = new Vector2(content.Min.X + 14f * scale, cardMax.Y + 12f * scale);
        var battleCardMax = new Vector2(content.Max.X - 14f * scale, battleCardMin.Y + 112f * scale);
        LgUi.Card(drawList, battleCardMin, battleCardMax, 14f * scale, scale);
        Typography.Draw(new Vector2(battleCardMin.X + 14f * scale, battleCardMin.Y + 12f * scale), "Battle playback",
            theme.TextStrong, TextStyles.SubheadlineEmphasized);
        Typography.Draw(new Vector2(battleCardMin.X + 14f * scale, battleCardMin.Y + 34f * scale),
            "Control message pacing and keep an optional history of the fight.", theme.TextMuted, TextStyles.Caption1);
        var speed = Math.Clamp(State.BattleSpeed, 0.5f, 2.5f);
        var speedRect = new Rect(new Vector2(battleCardMin.X + 14f * scale, battleCardMin.Y + 58f * scale),
            new Vector2(battleCardMax.X - 14f * scale, battleCardMin.Y + 84f * scale));
        if (DrawBattleSpeedSlider(speedRect, theme, scale, ref speed))
        {
            State.BattleSpeed = speed;
            State.Save();
        }
        var history = State.BattleLogEnabled;
        var historyRect = new Rect(new Vector2(battleCardMin.X + 14f * scale, battleCardMax.Y - 25f * scale),
            new Vector2(battleCardMax.X - 14f * scale, battleCardMax.Y - 5f * scale));
        if (DrawCheckboxRow(historyRect, theme, scale, ref history, "Keep battle log/history"))
        {
            State.BattleLogEnabled = history;
            State.Save();
        }

        if (effectScaleSliderActive && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            State.Save();
        }

        var saveTop = battleCardMax.Y + 14f * scale;
        Typography.Draw(new Vector2(content.Min.X + 14f * scale, saveTop),
            FitLabel(confirmingDeleteSave
                    ? "Erase your team, items and progress? This cannot be undone."
                    : "Save data · erase all progress and restart.",
                content.Width - 28f * scale, TextStyles.Caption1),
            confirmingDeleteSave ? theme.Danger : theme.TextMuted, TextStyles.Caption1);

        var deleteRect = new Rect(new Vector2(content.Min.X + 14f * scale, saveTop + 20f * scale),
            new Vector2(content.Min.X + 168f * scale, saveTop + 46f * scale));
        if (LgUi.Button(deleteRect, confirmingDeleteSave ? "Confirm delete" : "Delete save", theme.Danger, theme, true))
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

        if (confirmingDeleteSave)
        {
            var cancelRect = new Rect(new Vector2(deleteRect.Max.X + 10f * scale, deleteRect.Min.Y),
                new Vector2(deleteRect.Max.X + 94f * scale, deleteRect.Max.Y));
            if (LgUi.Button(cancelRect, "Cancel", GamePalette.Cell, theme, true))
            {
                confirmingDeleteSave = false;
            }
        }

        DrawNavigation(content, theme, scale);
    }

    // The password gate in front of the debug menu. Modal sub-view of Options, matching the debug
    // panel's chrome; a correct entry unlocks the menu for the rest of the session.
    private void DrawDebugPrompt(Rect content, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(RosterUi.NavyBottom));
        LgUi.Header(content, theme, Accent, "Debug Access", "Enter the password to continue.", scale);

        var cardMin = new Vector2(content.Min.X + 14f * scale, content.Min.Y + 84f * scale);
        var cardMax = new Vector2(content.Max.X - 14f * scale, cardMin.Y + 132f * scale);
        LgUi.Card(drawList, cardMin, cardMax, 14f * scale, scale);

        Typography.Draw(new Vector2(cardMin.X + 14f * scale, cardMin.Y + 14f * scale), "Password",
            theme.TextStrong, TextStyles.SubheadlineEmphasized);

        var inputRect = new Rect(new Vector2(cardMin.X + 14f * scale, cardMin.Y + 40f * scale),
            new Vector2(cardMax.X - 14f * scale, cardMin.Y + 72f * scale));
        var submitted = LgUi.Input(inputRect, "##debug-password", ref debugPasswordDraft, 32, theme, scale);

        if (debugPasswordError)
        {
            Typography.Draw(new Vector2(cardMin.X + 14f * scale, cardMin.Y + 78f * scale),
                "Incorrect password.", theme.Danger, TextStyles.Caption1);
        }

        var unlockRect = new Rect(new Vector2(cardMin.X + 14f * scale, cardMax.Y - 34f * scale),
            new Vector2(cardMin.X + 118f * scale, cardMax.Y - 8f * scale));
        var unlock = LgUi.Button(unlockRect, "Unlock", RosterUi.Green, theme, true);

        var cancelRect = new Rect(new Vector2(unlockRect.Max.X + 10f * scale, unlockRect.Min.Y),
            new Vector2(unlockRect.Max.X + 104f * scale, unlockRect.Max.Y));
        if (LgUi.Button(cancelRect, "Cancel", GamePalette.Cell, theme, true))
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
        LgUi.Header(content, theme, Accent, "Debug Menu", "Testing cheats — applied instantly.", scale);

        var actions = new (string Label, Vector4 Color, Action Do)[]
        {
            ("Full Heal Party", RosterUi.Green, () =>
            {
                State.HealAllMonsters();
                debugStatus = "Party and box fully healed.";
            }),
            ("Max Gil", RosterUi.Gold, () =>
            {
                State.Money = 999_999;
                State.Save();
                debugStatus = "Gil set to 999,999.";
            }),
            ("Give All Items", RosterUi.Blue, () =>
            {
                foreach (var item in Items.All)
                {
                    State.Bag.Add(item.Id, 99);
                }

                State.Save();
                debugStatus = $"Added x99 of every item ({Items.All.Count} kinds).";
            }),
            ("Unlock All TMs", RosterUi.Purple, () =>
            {
                foreach (var tm in Tms.All)
                {
                    State.OwnedTms.Add(tm.MoveId);
                }

                State.Save();
                debugStatus = $"Unlocked all {Tms.All.Count} TMs.";
            }),
            ("Earn All Badges", new Vector4(0.95f, 0.62f, 0.24f, 1f), () =>
            {
                foreach (var gym in Gyms.All)
                {
                    State.EarnBadge(gym.Index);
                }

                debugStatus = $"Earned all {Gyms.All.Count} gym badges.";
            }),
            ("Respawn Alphas", new Vector4(0.62f, 0.5f, 0.95f, 1f), () =>
            {
                State.DebugRespawnAlphas();
                debugStatus = "All region Alphas are alive again.";
            }),
        };

        var cardMin = new Vector2(content.Min.X + 14f * scale, content.Min.Y + 72f * scale);
        var cardMax = new Vector2(content.Max.X - 14f * scale, content.Max.Y - (NavBarHeight + 58f) * scale);
        LgUi.Card(drawList, cardMin, cardMax, 14f * scale, scale);

        const int cols = 2;
        var pad = 14f * scale;
        var gap = 10f * scale;
        var cellW = (cardMax.X - cardMin.X - pad * 2f - gap * (cols - 1)) / cols;
        var cellH = 42f * scale;
        for (var i = 0; i < actions.Length; i++)
        {
            var min = new Vector2(cardMin.X + pad + i % cols * (cellW + gap),
                cardMin.Y + pad + i / cols * (cellH + gap));
            var rect = new Rect(min, min + new Vector2(cellW, cellH));
            if (LgUi.Button(rect, actions[i].Label, actions[i].Color, theme, true))
            {
                actions[i].Do();
            }
        }

        if (!string.IsNullOrEmpty(debugStatus))
        {
            Typography.DrawCentered(new Vector2(content.Center.X, cardMax.Y - 20f * scale),
                FitLabel(debugStatus, cardMax.X - cardMin.X - 24f * scale, TextStyles.Subheadline),
                RosterUi.CountGreen, TextStyles.Subheadline);
        }

        var closeRect = new Rect(new Vector2(content.Center.X - 74f * scale, cardMax.Y + 12f * scale),
            new Vector2(content.Center.X + 74f * scale, cardMax.Y + 40f * scale));
        if (LgUi.Button(closeRect, "Close", GamePalette.Cell, theme, true))
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
        view = View.Starter;
    }

    private bool DrawCheckboxRow(Rect rect, PhoneTheme theme, float scale, ref bool value, string label)
    {
        var drawList = ImGui.GetWindowDrawList();
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        var boxSize = 22f * scale;
        var boxMin = new Vector2(rect.Min.X, rect.Center.Y - boxSize * 0.5f);
        var boxMax = boxMin + new Vector2(boxSize, boxSize);
        var radius = 6f * scale;
        var fill = value ? Accent : GamePalette.CellSunken;

        Squircle.FillVerticalGradient(drawList, boxMin, boxMax, radius,
            ImGui.GetColorU32(GamePalette.Lighten(fill, hovered ? 0.16f : 0.08f)),
            ImGui.GetColorU32(GamePalette.Darken(fill, hovered ? 0.08f : 0.14f)));
        Squircle.Stroke(drawList, boxMin, boxMax, radius,
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, hovered ? 0.22f : 0.11f)), 1f * scale);

        if (value)
        {
            var ink = GamePalette.InkOn(Accent);
            drawList.AddLine(new Vector2(boxMin.X + 5f * scale, boxMin.Y + 12f * scale),
                new Vector2(boxMin.X + 9f * scale, boxMin.Y + 16f * scale), ImGui.GetColorU32(ink), 2f * scale);
            drawList.AddLine(new Vector2(boxMin.X + 9f * scale, boxMin.Y + 16f * scale),
                new Vector2(boxMin.X + 17f * scale, boxMin.Y + 7f * scale), ImGui.GetColorU32(ink), 2f * scale);
        }

        Typography.Draw(new Vector2(boxMax.X + 10f * scale, rect.Center.Y - 8f * scale), label,
            hovered ? theme.TextStrong : theme.TextMuted, TextStyles.Subheadline);

        ImGui.SetCursorScreenPos(rect.Min);
        ImGui.InvisibleButton($"##lillypad-go-toggle-{label}", rect.Max - rect.Min);
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

    private bool DrawEffectScaleSlider(Rect rect, PhoneTheme theme, float scale, ref float value)
    {
        var drawList = ImGui.GetWindowDrawList();
        var trackMin = new Vector2(rect.Min.X, rect.Center.Y - 4f * scale);
        var trackMax = new Vector2(rect.Max.X, rect.Center.Y + 4f * scale);
        var radius = 4f * scale;
        var t = Math.Clamp((value - MinBattleEffectScale) / (MaxBattleEffectScale - MinBattleEffectScale), 0f, 1f);
        var knob = new Vector2(trackMin.X + (trackMax.X - trackMin.X) * t, rect.Center.Y);

        Squircle.Fill(drawList, trackMin, trackMax, radius, ImGui.GetColorU32(GamePalette.CellSunken));
        Squircle.Fill(drawList, trackMin, new Vector2(knob.X, trackMax.Y), radius,
            ImGui.GetColorU32(Accent with { W = 0.92f }));
        drawList.AddCircleFilled(knob, 11f * scale, ImGui.GetColorU32(GamePalette.Lighten(Accent, 0.22f)));
        drawList.AddCircle(knob, 11f * scale, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.45f)), 24, 1f * scale);

        Typography.Draw(new Vector2(rect.Min.X, rect.Max.Y - 4f * scale), "75%", theme.TextMuted, TextStyles.Caption2);
        var maxLabel = "200%";
        var maxSize = Typography.Measure(maxLabel, TextStyles.Caption2);
        Typography.Draw(new Vector2(rect.Max.X - maxSize.X, rect.Max.Y - 4f * scale), maxLabel, theme.TextMuted,
            TextStyles.Caption2);

        ImGui.SetCursorScreenPos(rect.Min);
        ImGui.InvisibleButton("##battle-effect-scale", rect.Max - rect.Min);
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            effectScaleSliderActive = true;
        }

        if (!effectScaleSliderActive || !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            return false;
        }

        var mouseX = ImGui.GetMousePos().X;
        var nextT = Math.Clamp((mouseX - trackMin.X) / MathF.Max(1f, trackMax.X - trackMin.X), 0f, 1f);
        var next = MinBattleEffectScale + nextT * (MaxBattleEffectScale - MinBattleEffectScale);
        if (MathF.Abs(next - value) <= 0.001f)
        {
            return false;
        }

        value = next;
        return true;
    }

    private bool DrawBattleSpeedSlider(Rect rect, PhoneTheme theme, float scale, ref float value)
    {
        var drawList = ImGui.GetWindowDrawList();
        var trackMin = new Vector2(rect.Min.X, rect.Center.Y - 4f * scale);
        var trackMax = new Vector2(rect.Max.X, rect.Center.Y + 4f * scale);
        var t = Math.Clamp((value - 0.5f) / 2f, 0f, 1f);
        var knob = new Vector2(trackMin.X + (trackMax.X - trackMin.X) * t, rect.Center.Y);
        Squircle.Fill(drawList, trackMin, trackMax, 4f * scale, ImGui.GetColorU32(GamePalette.CellSunken));
        Squircle.Fill(drawList, trackMin, new Vector2(knob.X, trackMax.Y), 4f * scale,
            ImGui.GetColorU32(Accent with { W = 0.92f }));
        drawList.AddCircleFilled(knob, 10f * scale, ImGui.GetColorU32(GamePalette.Lighten(Accent, 0.22f)));
        Typography.Draw(new Vector2(rect.Min.X, rect.Max.Y - 3f * scale), "0.5x", theme.TextMuted, TextStyles.Caption2);
        var maxLabel = "2.5x";
        Typography.Draw(new Vector2(rect.Max.X - Typography.Measure(maxLabel, TextStyles.Caption2).X,
            rect.Max.Y - 3f * scale), maxLabel, theme.TextMuted, TextStyles.Caption2);
        ImGui.SetCursorScreenPos(rect.Min);
        ImGui.InvisibleButton("##battle-speed", rect.Max - rect.Min);
        if (!ImGui.IsItemActive() && !ImGui.IsItemClicked())
        {
            return false;
        }

        value = 0.5f + Math.Clamp((ImGui.GetMousePos().X - trackMin.X) /
            MathF.Max(1f, trackMax.X - trackMin.X), 0f, 1f) * 2f;
        return true;
    }
}
