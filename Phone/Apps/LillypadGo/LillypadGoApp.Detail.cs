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
    private void DrawDetail(Rect content, PhoneTheme theme)
    {
        if (detailMonster is not { } monster)
        {
            view = detailReturnView;
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        BiomeBackdrop.Draw(drawList, content, State.CurrentBiome, time, false);
        var back = CenteredAt(new Vector2(content.Min.X + 38f * scale, content.Min.Y + 20f * scale),
            new Vector2(62f * scale, 26f * scale));
        if (LgUi.Button(back, "Back", GamePalette.Cell, theme, true))
        {
            view = detailReturnView;
            return;
        }

        var learnset = CenteredAt(new Vector2(content.Max.X - 122f * scale, content.Min.Y + 20f * scale),
            new Vector2(70f * scale, 26f * scale));
        if (LgUi.Button(learnset, "Moves", Accent, theme, true))
        {
            dexEntrySpecies = monster.Species;
            learnsetMonster = monster;
            teachPendingMove = null;
            dexEntryTab = 1;
            dexEntryTabIndicator = -1f;
            dexEntryScroll = 0f;
            dexEntryReturnView = View.Detail;
            view = View.DexEntry;
            return;
        }

        if (ImGui.IsMouseHoveringRect(learnset.Min, learnset.Max))
        {
            ShowTooltip($"View {monster.Species.Name}'s learnset and customise its moves.");
        }

        var releaseRect = CenteredAt(new Vector2(content.Max.X - 45f * scale, content.Min.Y + 20f * scale),
            new Vector2(62f * scale, 26f * scale));
        if (LgUi.Button(releaseRect, "Release", theme.Danger, theme, true))
        {
            releaseConfirm = true;
        }

        if (ImGui.IsMouseHoveringRect(releaseRect.Min, releaseRect.Max))
        {
            ShowTooltip($"Release {monster.Name} for {LgUi.Money(ReleaseValue(monster))}.");
        }

        var portrait = new Vector2(content.Center.X - (Dex.EvolutionOf(monster.Species) is not null ? 30f * scale : 0f),
            content.Min.Y + 88f * scale);
        ProgressRing.Glow(portrait, 54f * scale, Elements.Color(monster.Element), 0.45f);
        MonsterArt.Draw(drawList, portrait, 48f * scale, monster.Species, 1f,
            MonsterPose.Idle(time));

        // Next evolution preview: arrow + a small sprite of the evolved form, with the trigger.
        if (Dex.EvolutionOf(monster.Species) is { } evo)
        {
            var evoCenter = new Vector2(content.Center.X + 74f * scale, content.Min.Y + 84f * scale);
            Typography.DrawCentered(new Vector2(content.Center.X + 30f * scale, portrait.Y), ">",
                theme.TextStrong with { W = 0.7f }, TextStyles.Title2);
            ProgressRing.Glow(evoCenter, 28f * scale, Elements.Color(evo.Element), 0.32f);
            MonsterArt.Draw(drawList, evoCenter, 24f * scale, evo, 1f, MonsterPose.Idle(time + 1.3f));
            var trigger = monster.Species.EvolveLevel > 0
                ? $"Lv {monster.Species.EvolveLevel}"
                : monster.Species.EvolveMethod ?? evo.Name;
            Typography.DrawCentered(new Vector2(evoCenter.X, evoCenter.Y + 34f * scale),
                FitLabel(trigger, 92f * scale, TextStyles.Caption2), theme.TextStrong with { W = 0.82f },
                TextStyles.Caption2);
        }
        var genderTag = monster.GenderSymbol.Length > 0 ? " " + monster.GenderSymbol : string.Empty;
        var nameText = FitLabel(monster.Name + genderTag, content.Width - 24f * scale, TextStyles.Title2);
        Typography.DrawCentered(new Vector2(content.Center.X, content.Min.Y + 148f * scale), nameText,
            theme.TextStrong, TextStyles.Title2);
        var subtitleText = FitLabel($"{Elements.Format(monster.Element, monster.SecondaryElement)}  |  Lv {monster.Level}",
            content.Width - 24f * scale, TextStyles.Caption1);
        Typography.DrawCentered(new Vector2(content.Center.X, content.Min.Y + 170f * scale), subtitleText,
            theme.TextStrong with { W = 0.85f }, TextStyles.Caption1);

        Typography.Draw(new Vector2(content.Min.X + 16f * scale, content.Min.Y + 188f * scale), "Nickname",
            theme.TextMuted, TextStyles.Caption2);
        var nickRect = new Rect(new Vector2(content.Min.X + 16f * scale, content.Min.Y + 200f * scale),
            new Vector2(content.Max.X - 96f * scale, content.Min.Y + 228f * scale));
        var submittedName = LgUi.Input(nickRect, "##lillypadgo-nickname", ref detailNameDraft, 21, theme, scale);
        var trimmedName = detailNameDraft.Trim();
        var nameChanged = trimmedName != monster.Nickname;
        var saveName = CenteredAt(new Vector2(content.Max.X - 44f * scale, content.Min.Y + 214f * scale),
            new Vector2(72f * scale, 28f * scale));
        var clickedSaveName = LgUi.Button(saveName, "Save", nameChanged ? Accent : GamePalette.CellSunken, theme,
            nameChanged);
        if (nameChanged && (clickedSaveName || submittedName))
        {
            monster.Rename(detailNameDraft);
            detailNameDraft = monster.Nickname;
            State.Save();
        }

        // Stats card: each column shows the battle stat, its IV (potential) and EV (trained).
        var statsMin = new Vector2(content.Min.X + 12f * scale, content.Min.Y + 236f * scale);
        var statsMax = new Vector2(content.Max.X - 12f * scale, content.Min.Y + 326f * scale);
        LgUi.Card(drawList, statsMin, statsMax, 12f * scale, scale);
        if (ImGui.IsMouseHoveringRect(statsMin, statsMax))
        {
            ShowTooltip(BuildRecordTooltip(monster));
        }

        var ivBlue = new Vector4(0.44f, 0.72f, 1f, 0.92f);
        var evGreen = new Vector4(0.44f, 0.86f, 0.52f, 0.92f);
        var stats = new (string Label, string Value, int Slot)[]
        {
            ("HP", $"{monster.CurrentHp}/{monster.MaxHp}", 0), ("ATK", monster.Atk.ToString(), 1),
            ("DEF", monster.Def.ToString(), 2), ("SP.A", monster.SpAtk.ToString(), 3),
            ("SP.D", monster.SpDef.ToString(), 4), ("SPD", monster.Spd.ToString(), 5),
        };
        var colWidth = (statsMax.X - statsMin.X) / stats.Length;
        for (var i = 0; i < stats.Length; i++)
        {
            var x = statsMin.X + (i + 0.5f) * colWidth;
            // Drop to a smaller style when a value (e.g. a big "234/234" HP) would overrun its column.
            var valueStyle = Typography.Measure(stats[i].Value, TextStyles.Headline).X > colWidth - 3f * scale
                ? TextStyles.Caption1
                : TextStyles.Headline;
            Typography.DrawCentered(new Vector2(x, statsMin.Y + 16f * scale), stats[i].Value, theme.TextStrong,
                valueStyle);
            Typography.DrawCentered(new Vector2(x, statsMin.Y + 34f * scale), stats[i].Label,
                theme.TextStrong with { W = 0.78f }, TextStyles.Caption2);
            Typography.DrawCentered(new Vector2(x, statsMin.Y + 56f * scale), $"IV {monster.Ivs[stats[i].Slot]}",
                ivBlue, TextStyles.Caption2);
            Typography.DrawCentered(new Vector2(x, statsMin.Y + 71f * scale), $"EV {monster.Evs[stats[i].Slot]}",
                evGreen, TextStyles.Caption2);
        }

        // Ability card: name on the header row, description wrapped to fit the card below it.
        var abilMin = new Vector2(content.Min.X + 12f * scale, content.Min.Y + 332f * scale);
        var abilMax = new Vector2(content.Max.X - 12f * scale, content.Min.Y + 392f * scale);
        LgUi.Card(drawList, abilMin, abilMax, 12f * scale, scale);
        Typography.Draw(new Vector2(abilMin.X + 12f * scale, abilMin.Y + 9f * scale), "ABILITY",
            Accent with { W = 0.9f }, TextStyles.Caption2);
        Typography.Draw(new Vector2(abilMin.X + 64f * scale, abilMin.Y + 8f * scale),
            FitLabel(monster.Ability, abilMax.X - abilMin.X - 76f * scale, TextStyles.SubheadlineEmphasized),
            theme.TextStrong, TextStyles.SubheadlineEmphasized);
        var abilLines = WrapText(AbilityInfo.Describe(monster.Ability), abilMax.X - abilMin.X - 26f * scale,
            TextStyles.Caption2);
        for (var i = 0; i < abilLines.Count && i < 2; i++)
        {
            Typography.Draw(new Vector2(abilMin.X + 13f * scale, abilMin.Y + (29f + i * 14f) * scale), abilLines[i],
                theme.TextStrong with { W = 0.92f }, TextStyles.Caption2);
        }

        var xpLabel = monster.Level >= 100 ? "Maximum level" : $"XP {monster.Xp}/{monster.XpToNext}";
        Typography.Draw(new Vector2(content.Min.X + 16f * scale, content.Min.Y + 400f * scale), xpLabel,
            theme.TextStrong with { W = 0.8f }, TextStyles.Caption2);
        LgUi.Meter(drawList, new Vector2(content.Min.X + 16f * scale, content.Min.Y + 414f * scale),
            new Vector2(content.Max.X - 16f * scale, content.Min.Y + 420f * scale), monster.XpFraction, Accent);

        Typography.Draw(new Vector2(content.Min.X + 14f * scale, content.Min.Y + 430f * scale), "Moves",
            theme.TextStrong, TextStyles.SubheadlineEmphasized);
        Typography.Draw(new Vector2(content.Min.X + 66f * scale, content.Min.Y + 432f * scale),
            "— drag to reorder", theme.TextStrong with { W = 0.55f }, TextStyles.Caption2);

        var movesTop = content.Min.Y + 448f * scale;
        var movesBottom = content.Max.Y - 8f * scale;
        if (movesBottom - movesTop < 24f * scale)
        {
            // No room for the moves grid on a very short / high-DPI screen; better to omit it than
            // to draw inverted, overlapping cards.
            draggingMoveIndex = -1;
            return;
        }

        var columns = 2;
        var rows = Math.Max(1, (monster.Moves.Count + columns - 1) / columns);
        var gap = 5f * scale;
        var cardWidth = (content.Width - 24f * scale - gap) / columns;
        var rowH = MathF.Max(22f * scale, MathF.Min(58f * scale, (movesBottom - movesTop - gap * (rows - 1)) / rows));

        // Compute each move-card rect so drag-and-drop can hit-test them.
        var rects = new Rect[monster.Moves.Count];
        for (var i = 0; i < monster.Moves.Count; i++)
        {
            var min = new Vector2(content.Min.X + 12f * scale + (i % columns) * (cardWidth + gap),
                movesTop + (i / columns) * (rowH + gap));
            rects[i] = new Rect(min, min + new Vector2(cardWidth, rowH));
        }

        var mouse = ImGui.GetMousePos();
        var dragging = draggingMoveIndex >= 0 && draggingMoveIndex < monster.Moves.Count;

        // Begin a drag on press over a card (only meaningful with 2+ moves).
        if (!dragging && LgUi.Interactive && monster.Moves.Count > 1 &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            for (var i = 0; i < rects.Length; i++)
            {
                if (rects[i].Contains(mouse) && rects[i].Max.Y <= movesBottom + 1f * scale)
                {
                    draggingMoveIndex = i;
                    dragging = true;
                    break;
                }
            }
        }

        for (var i = 0; i < monster.Moves.Count; i++)
        {
            if (rects[i].Max.Y > movesBottom + 1f * scale)
            {
                break;
            }

            if (dragging && i == draggingMoveIndex)
            {
                DrawMoveSlotPlaceholder(drawList, rects[i], scale); // the picked-up card's empty slot
                continue;
            }

            var isDropTarget = dragging && rects[i].Contains(mouse);
            DrawMoveCard(drawList, rects[i], monster, i, theme, scale, isDropTarget);
            if (!dragging && ImGui.IsMouseHoveringRect(rects[i].Min, rects[i].Max))
            {
                ShowTooltip(BuildProfileMoveTooltip(monster.Moves[i], monster.Pp[i]));
            }
        }

        // The floating card follows the cursor; on release, drop onto whatever slot it's over.
        if (dragging)
        {
            var src = rects[draggingMoveIndex];
            var half = (src.Max - src.Min) * 0.5f;
            var floatRect = new Rect(mouse - half, mouse + half);
            DrawMoveCard(drawList, floatRect, monster, draggingMoveIndex, theme, scale, false);

            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                for (var i = 0; i < monster.Moves.Count; i++)
                {
                    if (i != draggingMoveIndex && rects[i].Contains(mouse) &&
                        rects[i].Max.Y <= movesBottom + 1f * scale)
                    {
                        monster.SwapMoves(draggingMoveIndex, i);
                        State.Save();
                        break;
                    }
                }

                draggingMoveIndex = -1;
            }
        }

        if (releaseConfirm)
        {
            DrawReleaseConfirm(content, theme, monster, scale);
        }
    }

    private void DrawMoveCard(ImDrawListPtr drawList, Rect rect, MonsterInstance monster, int index, PhoneTheme theme,
        float scale, bool highlight)
    {
        var move = monster.Moves[index];
        var color = Elements.Color(move.Element);
        LgUi.Card(drawList, rect.Min, rect.Max, 9f * scale, scale, highlight);
        if (highlight)
        {
            Squircle.Stroke(drawList, rect.Min, rect.Max, 9f * scale, ImGui.GetColorU32(color with { W = 0.9f }),
                1.6f * scale);
        }

        drawList.AddRectFilled(rect.Min, new Vector2(rect.Min.X + 5f * scale, rect.Max.Y), ImGui.GetColorU32(color),
            4f * scale);
        Typography.Draw(new Vector2(rect.Min.X + 14f * scale, rect.Min.Y + 7f * scale), move.Name, theme.TextStrong,
            TextStyles.SubheadlineEmphasized);
        Typography.Draw(new Vector2(rect.Min.X + 14f * scale, rect.Max.Y - 16f * scale),
            $"{Elements.Name(move.Element)} {move.CategoryLabel}  {monster.Pp[index]}/{move.Pp} PP", theme.TextMuted,
            TextStyles.Caption2);
    }

    private static void DrawMoveSlotPlaceholder(ImDrawListPtr drawList, Rect rect, float scale)
    {
        Squircle.Fill(drawList, rect.Min, rect.Max, 9f * scale, ImGui.GetColorU32(GamePalette.CellSunken));
        Squircle.Stroke(drawList, rect.Min, rect.Max, 9f * scale, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.12f)),
            1f * scale);
    }

    private static string BuildRecordTooltip(MonsterInstance m)
    {
        var labels = new[] { "HP", "Atk", "Def", "SpA", "SpD", "Spe" };
        var evRows = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            if (m.Evs[i] > 0)
            {
                evRows.Add($"{labels[i]}: {m.Evs[i]} EV  →  +{m.Evs[i] / 4} point{(m.Evs[i] / 4 == 1 ? "" : "s")}");
            }
        }

        var evBlock = evRows.Count == 0
            ? "No EVs trained yet — win battles to earn them."
            : string.Join("\n", evRows);

        var winRate = m.Battles == 0 ? "--" : $"{m.Victories * 100 / m.Battles}%";
        return $"{m.Name}  Lv {m.Level}\n\n" +
               $"EV STAT BONUS  (every 4 EVs = +1 stat point at Lv 100)\n{evBlock}\n" +
               $"Total {m.EvTotal}/510 EVs  ·  {510 - m.EvTotal} still available\n\n" +
               "IVs are fixed potential (0-31), rolled when the Pokémon is met.\n\n" +
               $"{m.Battles} battles · {m.Victories} wins ({winRate}) · {m.DamageDealt} dmg\n" +
               $"Habitats: {ArrZones.Habitats(m.Species.Id)}";
    }

    private void DrawReleaseConfirm(Rect content, PhoneTheme theme, MonsterInstance monster, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.68f)));
        var min = new Vector2(content.Min.X + 24f * scale, content.Center.Y - 92f * scale);
        var max = new Vector2(content.Max.X - 24f * scale, content.Center.Y + 92f * scale);
        LgUi.Card(drawList, min, max, 14f * scale, scale);
        Squircle.Stroke(drawList, min, max, 14f * scale, ImGui.GetColorU32(theme.Danger with { W = 0.6f }),
            1.2f * scale);
        var panel = new Rect(min, max);

        Typography.DrawCentered(new Vector2(panel.Center.X, min.Y + 26f * scale), $"Release {monster.Name}?",
            theme.TextStrong, TextStyles.Headline);
        var reward = ReleaseValue(monster);
        foreach (var (line, i) in new[]
                 {
                     $"{monster.Name} will leave for good.", $"You'll receive {LgUi.Money(reward)}.",
                 }.Select((t, i) => (t, i)))
        {
            Typography.DrawCentered(new Vector2(panel.Center.X, min.Y + (52f + i * 20f) * scale),
                FitLabel(line, panel.Width - 24f * scale, TextStyles.Caption1), theme.TextStrong with { W = 0.82f },
                TextStyles.Caption1);
        }

        var yes = CenteredAt(new Vector2(panel.Center.X - panel.Width * 0.24f, max.Y - 30f * scale),
            new Vector2(panel.Width * 0.4f, 34f * scale));
        var isLast = State.Party.Count + State.Box.Count <= 1;
        if (LgUi.Button(yes, "Release", theme.Danger, theme, !isLast))
        {
            ReleaseMonster(monster);
            return;
        }

        if (isLast && ImGui.IsMouseHoveringRect(yes.Min, yes.Max))
        {
            ShowTooltip("You can't release your last Pokémon.");
        }

        var no = CenteredAt(new Vector2(panel.Center.X + panel.Width * 0.24f, max.Y - 30f * scale),
            new Vector2(panel.Width * 0.4f, 34f * scale));
        if (LgUi.Button(no, "Keep", theme.Accent, theme, true))
        {
            releaseConfirm = false;
        }
    }

}
