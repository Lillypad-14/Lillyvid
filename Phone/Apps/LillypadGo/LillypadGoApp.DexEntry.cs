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
    private void DrawDexEntry(Rect content, PhoneTheme theme)
    {
        if (dexEntrySpecies is not { } species || !State.Seen.Contains(species.Id))
        {
            view = dexEntryReturnView;
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        BiomeBackdrop.Draw(ImGui.GetWindowDrawList(), content, State.CurrentBiome, time, false);
        var caught = State.Party.Concat(State.Box).Any(monster => monster.Species.Id == species.Id);
        var number = species.DexNumber > 0 ? $"#{species.DexNumber:000}" : "Field entry";
        LgUi.Header(content, theme, Accent, species.Name,
            $"{number}  |  {(caught ? "Caught" : "Seen")}", scale);

        var back = new Rect(new Vector2(content.Min.X + 12f * scale, content.Min.Y + 66f * scale),
            new Vector2(content.Min.X + 82f * scale, content.Min.Y + 96f * scale));
        if (LgUi.Button(back, "Back", GamePalette.Cell, theme, true))
        {
            view = dexEntryReturnView;
            return;
        }

        var tabs = new Rect(new Vector2(content.Min.X + 90f * scale, content.Min.Y + 66f * scale),
            new Vector2(content.Max.X - 12f * scale, content.Min.Y + 96f * scale));
        var changed = LgUi.Segmented(tabs, new[] { "Overview", "Learnset" }, dexEntryTab, Accent, theme, scale,
            ref dexEntryTabIndicator);
        if (changed >= 0)
        {
            dexEntryTab = changed;
            dexEntryScroll = 0f;
        }

        if (dexEntryTab == 0)
        {
            DrawDexOverview(content, theme, species, caught, scale);
        }
        else
        {
            DrawDexLearnset(content, theme, species, scale);
        }
    }

    private void DrawDexOverview(Rect content, PhoneTheme theme, MonsterSpecies species, bool caught, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var heroMin = new Vector2(content.Min.X + 12f * scale, content.Min.Y + 108f * scale);
        var heroMax = new Vector2(content.Max.X - 12f * scale, content.Min.Y + 224f * scale);
        LgUi.Card(drawList, heroMin, heroMax, 12f * scale, scale);
        var portrait = new Vector2(heroMin.X + 58f * scale, (heroMin.Y + heroMax.Y) * 0.5f);
        MonsterArt.Draw(drawList, portrait, 42f * scale, species, 1f, MonsterPose.Idle(time));

        var textX = heroMin.X + 112f * scale;
        var textWidth = heroMax.X - textX - 10f * scale;
        Typography.Draw(new Vector2(textX, heroMin.Y + 16f * scale),
            FitLabel(species.Name, textWidth, TextStyles.Title3), theme.TextStrong, TextStyles.Title3);
        LgUi.TypeChips(drawList, new Vector2(textX, heroMin.Y + 46f * scale), species.Element,
            species.SecondaryElement, scale);
        Typography.Draw(new Vector2(textX, heroMin.Y + 70f * scale), caught ? "Captured" : "Observed",
            caught ? Accent : theme.TextMuted, TextStyles.Caption1);
        Typography.Draw(new Vector2(textX, heroMin.Y + 88f * scale),
            FitLabel($"Catch {species.CatchRate}/255  ·  {GenderRatioText(species)}", textWidth, TextStyles.Caption2),
            theme.TextStrong with { W = 0.72f }, TextStyles.Caption2);
        Typography.Draw(new Vector2(textX, heroMin.Y + 104f * scale),
            FitLabel($"Ability: {string.Join(" / ", species.Abilities)}", textWidth, TextStyles.Caption2),
            theme.TextStrong with { W = 0.72f }, TextStyles.Caption2);

        var statsMin = new Vector2(content.Min.X + 12f * scale, content.Min.Y + 234f * scale);
        var statsMax = new Vector2(content.Max.X - 12f * scale, content.Min.Y + 344f * scale);
        LgUi.Card(drawList, statsMin, statsMax, 12f * scale, scale);
        var stats = new[]
        {
            ("HP", species.BaseHp), ("ATK", species.BaseAtk), ("DEF", species.BaseDef),
            ("SP. ATK", species.BaseSpAtk), ("SP. DEF", species.BaseSpDef), ("SPEED", species.BaseSpd),
        };
        for (var i = 0; i < stats.Length; i++)
        {
            var column = i % 3;
            var row = i / 3;
            var x = statsMin.X + (column + 0.5f) * (statsMax.X - statsMin.X) / 3f;
            var y = statsMin.Y + (row * 50f + 17f) * scale;
            Typography.DrawCentered(new Vector2(x, y), stats[i].Item2.ToString(), theme.TextStrong,
                TextStyles.Headline);
            Typography.DrawCentered(new Vector2(x, y + 20f * scale), stats[i].Item1, theme.TextMuted,
                TextStyles.Caption2);
        }

        var infoMin = new Vector2(content.Min.X + 12f * scale, content.Min.Y + 354f * scale);
        var infoMax = new Vector2(content.Max.X - 12f * scale, content.Max.Y - 14f * scale);
        if (infoMax.Y <= infoMin.Y)
        {
            return;
        }

        LgUi.Card(drawList, infoMin, infoMax, 12f * scale, scale);
        var infoWidth = infoMax.X - infoMin.X - 28f * scale;

        // Evolution row with a small sprite of the evolved form.
        Typography.Draw(new Vector2(infoMin.X + 14f * scale, infoMin.Y + 14f * scale), "Evolution",
            theme.TextStrong, TextStyles.SubheadlineEmphasized);
        if (Dex.EvolutionOf(species) is { } evo)
        {
            var evoCenter = new Vector2(infoMax.X - 30f * scale, infoMin.Y + 30f * scale);
            ProgressRing.Glow(evoCenter, 20f * scale, Elements.Color(evo.Element), 0.3f);
            MonsterArt.Draw(drawList, evoCenter, 18f * scale, evo, 1f, MonsterPose.Idle(time + 1.3f));
        }

        Typography.Draw(new Vector2(infoMin.X + 14f * scale, infoMin.Y + 36f * scale),
            FitLabel(EvolutionSummary(species), infoWidth - 46f * scale, TextStyles.Caption1),
            theme.TextStrong with { W = 0.8f }, TextStyles.Caption1);

        var habitatsY = infoMin.Y + 62f * scale;
        if (habitatsY + 30f * scale < infoMax.Y - 44f * scale)
        {
            Typography.Draw(new Vector2(infoMin.X + 14f * scale, habitatsY), "Habitats",
                theme.TextStrong, TextStyles.SubheadlineEmphasized);
            var habitats = ArrZones.Habitats(species.Id);
            var lines = WrapText(string.IsNullOrWhiteSpace(habitats) ? "No ARR habitat recorded." : habitats,
                infoWidth, TextStyles.Body);
            for (var i = 0; i < lines.Count && i < 2; i++)
            {
                Typography.Draw(new Vector2(infoMin.X + 14f * scale, habitatsY + (22f + i * 19f) * scale), lines[i],
                    theme.TextStrong with { W = 0.75f }, TextStyles.Body);
            }
        }

        Typography.Draw(new Vector2(infoMin.X + 14f * scale, infoMax.Y - 42f * scale),
            $"{species.Learnset.Length} level-up moves", theme.TextStrong with { W = 0.75f }, TextStyles.Caption1);
        Typography.Draw(new Vector2(infoMin.X + 14f * scale, infoMax.Y - 22f * scale),
            "Open Learnset for level and move details.", Accent, TextStyles.Caption2);
    }

    private void DrawDexLearnset(Rect content, PhoneTheme theme, MonsterSpecies species, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var summaryMin = new Vector2(content.Min.X + 12f * scale, content.Min.Y + 108f * scale);
        var summaryMax = new Vector2(content.Max.X - 12f * scale, content.Min.Y + 174f * scale);
        LgUi.Card(drawList, summaryMin, summaryMax, 10f * scale, scale);
        var portrait = new Vector2(summaryMin.X + 34f * scale, (summaryMin.Y + summaryMax.Y) * 0.5f);
        MonsterArt.Draw(drawList, portrait, 23f * scale, species, 1f, MonsterPose.Idle(time));
        var textX = summaryMin.X + 68f * scale;
        Typography.Draw(new Vector2(textX, summaryMin.Y + 10f * scale),
            FitLabel(species.Name, summaryMax.X - textX - 10f * scale, TextStyles.SubheadlineEmphasized),
            theme.TextStrong, TextStyles.SubheadlineEmphasized);
        LgUi.TypeChips(drawList, new Vector2(textX, summaryMin.Y + 36f * scale), species.Element,
            species.SecondaryElement, scale);
        Typography.Draw(new Vector2(summaryMax.X - 58f * scale, summaryMin.Y + 39f * scale),
            $"{species.Learnset.Length} moves", theme.TextMuted, TextStyles.Caption2);

        var headerY = content.Min.Y + 184f * scale;
        Typography.Draw(new Vector2(content.Min.X + 22f * scale, headerY), "LEVEL", theme.TextMuted,
            TextStyles.Caption2);
        Typography.Draw(new Vector2(content.Min.X + 78f * scale, headerY), "MOVE", theme.TextMuted,
            TextStyles.Caption2);
        Typography.Draw(new Vector2(content.Max.X - 54f * scale, headerY), "POWER", theme.TextMuted,
            TextStyles.Caption2);

        var list = new Rect(new Vector2(content.Min.X + 8f * scale, content.Min.Y + 204f * scale),
            new Vector2(content.Max.X - 9f * scale, content.Max.Y - 12f * scale));
        var moves = species.Learnset.OrderBy(entry => entry.Level).ThenBy(entry => entry.Move.Name).ToArray();

        // When opened for one of the player's creatures (via Team → Moves), this tab becomes a move
        // relearner: teach any learnset move at or below the creature's level, replacing one if full.
        var editMon = learnsetMonster is { } lm && lm.Species.Id == species.Id ? learnsetMonster : null;

        // Freeze the list while the replace overlay is up; the shared scroller gates per-row clicks
        // to the visible area, which stops the Teach buttons from reacting through the clip edges.
        var prevInteractive = LgUi.Interactive;
        if (teachPendingMove is not null)
        {
            LgUi.Interactive = false;
        }

        DrawScrollList(list, 48f * scale, 4f * scale, moves.Length, ref dexEntryScroll, scale,
            (i, row) => DrawLearnsetRow(moves[i], editMon, row, theme, scale));

        LgUi.Interactive = prevInteractive;

        if (editMon is not null && teachPendingMove is not null)
        {
            DrawTeachReplace(content, theme, editMon, scale);
        }
    }

    private void DrawLearnsetRow((int Level, MoveDef Move) entry, MonsterInstance? editMon, Rect row, PhoneTheme theme,
        float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var known = editMon is not null && editMon.Knows(entry.Move);
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(row.Min, row.Max);
        LgUi.Card(drawList, row.Min, row.Max, 8f * scale, scale, hovered);
        if (known)
        {
            drawList.AddRectFilled(row.Min, new Vector2(row.Min.X + 4f * scale, row.Max.Y),
                ImGui.GetColorU32(Accent with { W = 0.85f }), 3f * scale);
        }

        Typography.DrawCentered(new Vector2(row.Min.X + 28f * scale, row.Center.Y), $"Lv {entry.Level}",
            theme.TextStrong, TextStyles.Caption1);
        var moveX = row.Min.X + 58f * scale;
        var rightReserve = editMon is not null ? 74f * scale : 50f * scale;
        Typography.Draw(new Vector2(moveX, row.Min.Y + 7f * scale),
            FitLabel(entry.Move.Name, row.Max.X - moveX - rightReserve, TextStyles.SubheadlineEmphasized),
            theme.TextStrong, TextStyles.SubheadlineEmphasized);
        var detail = $"{Elements.Name(entry.Move.Element)}  |  {entry.Move.CategoryLabel}  |  {entry.Move.Pp} PP";
        Typography.Draw(new Vector2(moveX, row.Min.Y + 27f * scale),
            FitLabel(detail, row.Max.X - moveX - rightReserve, TextStyles.Caption2), theme.TextMuted,
            TextStyles.Caption2);

        if (editMon is not null)
        {
            var pill = CenteredAt(new Vector2(row.Max.X - 38f * scale, row.Center.Y),
                new Vector2(64f * scale, 26f * scale));
            if (known)
            {
                Typography.DrawCentered(pill.Center, "Known", Accent, TextStyles.Caption1);
            }
            else if (entry.Level > editMon.Level)
            {
                Typography.DrawCentered(pill.Center, "Locked", theme.TextMuted, TextStyles.Caption1);
            }
            else if (LgUi.Button(pill, "Teach", theme.Accent, theme, true))
            {
                if (!editMon.AddMove(entry.Move))
                {
                    teachPendingMove = entry.Move; // full moveset: choose which to replace
                }

                State.Save();
            }
        }
        else
        {
            var power = entry.Move.IsStatus ? "--" : entry.Move.Power.ToString();
            Typography.DrawCentered(new Vector2(row.Max.X - 26f * scale, row.Center.Y), power,
                Elements.Color(entry.Move.Element), TextStyles.Headline);
        }

        if (hovered)
        {
            ImGui.SetTooltip(BuildProfileMoveTooltip(entry.Move, entry.Move.Pp));
        }
    }

    // Overlay shown when teaching a move to a full (4-move) creature: pick which move to forget.
    private void DrawTeachReplace(Rect content, PhoneTheme theme, MonsterInstance mon, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.66f)));
        var panelMin = new Vector2(content.Min.X + 20f * scale, content.Center.Y - 120f * scale);
        var panelMax = new Vector2(content.Max.X - 20f * scale, content.Center.Y + 120f * scale);
        LgUi.Card(drawList, panelMin, panelMax, 14f * scale, scale);
        Squircle.Stroke(drawList, panelMin, panelMax, 14f * scale, ImGui.GetColorU32(Accent with { W = 0.5f }),
            1.2f * scale);
        var panel = new Rect(panelMin, panelMax);

        Typography.DrawCentered(new Vector2(panel.Center.X, panelMin.Y + 18f * scale),
            $"Teach {teachPendingMove!.Name}", theme.TextStrong, TextStyles.Headline);
        Typography.DrawCentered(new Vector2(panel.Center.X, panelMin.Y + 40f * scale),
            $"Choose a move for {mon.Name} to forget", theme.TextStrong with { W = 0.78f }, TextStyles.Caption1);

        for (var i = 0; i < mon.Moves.Count; i++)
        {
            var move = mon.Moves[i];
            var column = i % 2;
            var rowIdx = i / 2;
            var size = new Vector2(panel.Width * 0.42f, 40f * scale);
            var center = new Vector2(panel.Center.X + (column == 0 ? -1f : 1f) * panel.Width * 0.235f,
                panelMin.Y + (72f + rowIdx * 50f) * scale);
            var rect = new Rect(center - size * 0.5f, center + size * 0.5f);
            if (LgUi.Button(rect, FitLabel(move.Name, size.X - 12f * scale, TextStyles.Subheadline),
                    Elements.Color(move.Element), theme, true, $"{Elements.Name(move.Element)}  {move.Pp} PP"))
            {
                mon.ReplaceMove(i, teachPendingMove);
                teachPendingMove = null;
                State.Save();
                return;
            }
        }

        var cancel = CenteredAt(new Vector2(panel.Center.X, panelMax.Y - 22f * scale),
            new Vector2(panel.Width * 0.5f, 30f * scale));
        if (LgUi.Button(cancel, "Keep current moves", GamePalette.Cell, theme, true))
        {
            teachPendingMove = null;
        }
    }
}
