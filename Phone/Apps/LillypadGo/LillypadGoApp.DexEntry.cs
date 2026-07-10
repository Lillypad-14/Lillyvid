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
    // A species' full dex entry in the navy/cream chrome: outlined name header with the dex-number
    // pill, Overview/Learnset folder tabs, and navy cards on a cream panel.

    private void DrawDexEntry(Rect content, PhoneTheme theme)
    {
        if (dexEntrySpecies is not { } species || !State.Seen.Contains(species.Id))
        {
            view = dexEntryReturnView;
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(RosterUi.NavyBottom));
        var caught = State.Party.Concat(State.Box).Any(monster => monster.Species.Id == species.Id);
        var number = species.DexNumber > 0 ? $"#{species.DexNumber:000}" : "Field entry";
        var headerBottom = RosterUi.ScreenHeader(content,
            FitLabel(species.Name.ToUpperInvariant(), content.Width - 190f * scale, TextStyles.Title2), null, new[]
            {
                (number, new Vector4(1f, 1f, 1f, 1f)),
                ("|", RosterUi.NavyLine),
                (caught ? "Caught" : "Seen", caught ? RosterUi.CountGreen : RosterUi.CountBlue),
            }, scale);

        var back = CenteredAt(new Vector2(content.Min.X + 44f * scale, content.Min.Y + 23f * scale),
            new Vector2(64f * scale, 26f * scale));
        if (RosterUi.BlueButton(back, "BACK", scale, true, "arrow_left"))
        {
            view = dexEntryReturnView;
            return;
        }

        // Cream panel with the folder tabs on its top edge.
        var navTop = content.Max.Y - NavBarHeight * scale;
        var panel = new Rect(new Vector2(content.Min.X + 7f * scale, headerBottom + 32f * scale),
            new Vector2(content.Max.X - 7f * scale, navTop - 7f * scale));
        RosterUi.CreamPanel(drawList, panel, scale);

        var tabs = new Rect(new Vector2(content.Min.X + 12f * scale, headerBottom + 8f * scale),
            new Vector2(content.Max.X - 12f * scale, headerBottom + 36f * scale));
        var changed = RosterUi.FolderTabs(tabs, new[] { "OVERVIEW", "LEARNSET" }, dexEntryTab, scale);
        if (changed >= 0)
        {
            dexEntryTab = changed;
            dexEntryScroll = 0f;
        }

        if (dexEntryTab == 0)
        {
            DrawDexOverview(panel, species, caught, scale);
        }
        else
        {
            DrawDexLearnset(content, panel, theme, species, scale);
        }

        DrawNavigation(content, theme, scale);
    }

    private void DrawDexOverview(Rect panel, MonsterSpecies species, bool caught, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var left = panel.Min.X + 9f * scale;
        var right = panel.Max.X - 9f * scale;

        // Hero card: portrait, name, types and field notes.
        var hero = new Rect(new Vector2(left, panel.Min.Y + 8f * scale),
            new Vector2(right, panel.Min.Y + 124f * scale));
        RosterUi.DarkCard(drawList, hero, 10f * scale, scale, accent: Elements.Color(species.Element));
        var portrait = new Vector2(hero.Min.X + 58f * scale, hero.Center.Y);
        ProgressRing.Glow(portrait, 40f * scale, Elements.Color(species.Element), 0.35f);
        MonsterArt.Draw(drawList, portrait, 42f * scale, species, 1f, MonsterPose.Idle(time));

        var textX = hero.Min.X + 112f * scale;
        var textWidth = hero.Max.X - textX - 10f * scale;
        Typography.Draw(new Vector2(textX, hero.Min.Y + 14f * scale),
            FitLabel(species.Name, textWidth, TextStyles.Title3), RosterUi.CardInk, TextStyles.Title3);
        LgUi.TypeChips(drawList, new Vector2(textX, hero.Min.Y + 44f * scale), species.Element,
            species.SecondaryElement, scale);
        Typography.Draw(new Vector2(textX, hero.Min.Y + 66f * scale), caught ? "Captured" : "Observed",
            caught ? RosterUi.CountGreen : RosterUi.CardMuted, TextStyles.Caption1);
        Typography.Draw(new Vector2(textX, hero.Min.Y + 84f * scale),
            FitLabel($"Catch {species.CatchRate}/255  ·  {GenderRatioText(species)}", textWidth, TextStyles.Caption2),
            RosterUi.CardMuted, TextStyles.Caption2);
        Typography.Draw(new Vector2(textX, hero.Min.Y + 100f * scale),
            FitLabel($"Ability: {string.Join(" / ", species.Abilities)}", textWidth, TextStyles.Caption2),
            RosterUi.CardMuted, TextStyles.Caption2);

        // Base stats on a white plate, matching the profile screen's stat card.
        var stats = new Rect(new Vector2(left, hero.Max.Y + 8f * scale),
            new Vector2(right, hero.Max.Y + 112f * scale));
        RosterUi.ChunkyCard(drawList, stats.Min, stats.Max, 9f * scale, scale,
            new Vector4(0.99f, 0.98f, 0.95f, 1f), new Vector4(0.94f, 0.92f, 0.87f, 1f), RosterUi.NavyEdge);
        var baseStats = new[]
        {
            ("HP", species.BaseHp), ("ATK", species.BaseAtk), ("DEF", species.BaseDef),
            ("SP. ATK", species.BaseSpAtk), ("SP. DEF", species.BaseSpDef), ("SPEED", species.BaseSpd),
        };
        for (var i = 0; i < baseStats.Length; i++)
        {
            var column = i % 3;
            var row = i / 3;
            var x = stats.Min.X + (column + 0.5f) * stats.Width / 3f;
            var y = stats.Min.Y + (row * 48f + 16f) * scale;
            Typography.DrawCentered(new Vector2(x, y), baseStats[i].Item2.ToString(), RosterUi.InkNavy,
                TextStyles.Headline);
            Typography.DrawCentered(new Vector2(x, y + 20f * scale), baseStats[i].Item1,
                RosterUi.InkNavy with { W = 0.72f }, TextStyles.Caption2);
        }

        var info = new Rect(new Vector2(left, stats.Max.Y + 8f * scale), new Vector2(right, panel.Max.Y - 8f * scale));
        if (info.Height < 60f * scale)
        {
            return;
        }

        RosterUi.DarkCard(drawList, info, 10f * scale, scale);
        var infoWidth = info.Width - 28f * scale;

        // Evolution row with a small sprite of the evolved form.
        Typography.Draw(new Vector2(info.Min.X + 14f * scale, info.Min.Y + 12f * scale), "Evolution",
            RosterUi.CardInk, TextStyles.SubheadlineEmphasized);
        if (Dex.EvolutionOf(species) is { } evo)
        {
            var evoCenter = new Vector2(info.Max.X - 30f * scale, info.Min.Y + 28f * scale);
            ProgressRing.Glow(evoCenter, 20f * scale, Elements.Color(evo.Element), 0.3f);
            MonsterArt.Draw(drawList, evoCenter, 18f * scale, evo, 1f, MonsterPose.Idle(time + 1.3f));
        }

        Typography.Draw(new Vector2(info.Min.X + 14f * scale, info.Min.Y + 34f * scale),
            FitLabel(EvolutionSummary(species), infoWidth - 46f * scale, TextStyles.Caption1),
            RosterUi.CardMuted, TextStyles.Caption1);

        var habitatsY = info.Min.Y + 60f * scale;
        if (habitatsY + 30f * scale < info.Max.Y - 44f * scale)
        {
            Typography.Draw(new Vector2(info.Min.X + 14f * scale, habitatsY), "Habitats",
                RosterUi.CardInk, TextStyles.SubheadlineEmphasized);
            var habitats = ArrZones.Habitats(species.Id);
            var lines = WrapText(string.IsNullOrWhiteSpace(habitats) ? "No ARR habitat recorded." : habitats,
                infoWidth, TextStyles.Callout);
            for (var i = 0; i < lines.Count && i < 2; i++)
            {
                Typography.Draw(new Vector2(info.Min.X + 14f * scale, habitatsY + (20f + i * 17f) * scale), lines[i],
                    RosterUi.CardMuted, TextStyles.Callout);
            }
        }

        Typography.Draw(new Vector2(info.Min.X + 14f * scale, info.Max.Y - 40f * scale),
            $"{species.Learnset.Length} level-up moves", RosterUi.CardMuted, TextStyles.Caption1);
        Typography.Draw(new Vector2(info.Min.X + 14f * scale, info.Max.Y - 21f * scale),
            "Open Learnset for level and move details.", RosterUi.CountGreen, TextStyles.Caption2);
    }

    private void DrawDexLearnset(Rect content, Rect panel, PhoneTheme theme, MonsterSpecies species, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var left = panel.Min.X + 9f * scale;
        var right = panel.Max.X - 9f * scale;

        var summary = new Rect(new Vector2(left, panel.Min.Y + 8f * scale),
            new Vector2(right, panel.Min.Y + 66f * scale));
        RosterUi.DarkCard(drawList, summary, 10f * scale, scale, accent: Elements.Color(species.Element));
        var portrait = new Vector2(summary.Min.X + 32f * scale, summary.Center.Y);
        MonsterArt.Draw(drawList, portrait, 21f * scale, species, 1f, MonsterPose.Idle(time));
        var textX = summary.Min.X + 62f * scale;
        Typography.Draw(new Vector2(textX, summary.Min.Y + 9f * scale),
            FitLabel(species.Name, summary.Max.X - textX - 70f * scale, TextStyles.SubheadlineEmphasized),
            RosterUi.CardInk, TextStyles.SubheadlineEmphasized);
        LgUi.TypeChips(drawList, new Vector2(textX, summary.Min.Y + 31f * scale), species.Element,
            species.SecondaryElement, scale);
        Typography.Draw(new Vector2(summary.Max.X - 62f * scale, summary.Min.Y + 34f * scale),
            $"{species.Learnset.Length} moves", RosterUi.CardMuted, TextStyles.Caption2);

        // Filter tab: all moves, level-up only, or TM-learnable only.
        var filterBounds = new Rect(new Vector2(left, summary.Max.Y + 8f * scale),
            new Vector2(right, summary.Max.Y + 32f * scale));
        var filterPick = RosterUi.FolderTabs(filterBounds, new[] { "All", "Level-Up", "TM" }, dexLearnFilter, scale);
        if (filterPick >= 0 && filterPick != dexLearnFilter)
        {
            dexLearnFilter = filterPick;
            dexEntryScroll = 0f;
        }

        var headerY = filterBounds.Max.Y + 8f * scale;
        Typography.Draw(new Vector2(left + 10f * scale, headerY), "LEARN", RosterUi.InkTan, TextStyles.Caption2);
        Typography.Draw(new Vector2(left + 66f * scale, headerY), "MOVE", RosterUi.InkTan, TextStyles.Caption2);
        Typography.Draw(new Vector2(right - 46f * scale, headerY), "POWER", RosterUi.InkTan, TextStyles.Caption2);

        var list = new Rect(new Vector2(left, headerY + 16f * scale),
            new Vector2(right + 1f * scale, panel.Max.Y - 8f * scale));
        // Level-up moves first, then the species' Gen-IX TM-legal moves; filtered by the tab above.
        var rows = BuildLearnRows(species);
        rows = dexLearnFilter switch
        {
            1 => rows.Where(row => row.TmId is null).ToArray(),
            2 => rows.Where(row => row.TmId is not null).ToArray(),
            _ => rows,
        };

        // When opened for one of the player's creatures (via Team → Moves), this tab becomes a move
        // relearner: teach any learnset move at or below the creature's level, or any owned TM the
        // species can learn, replacing one if the moveset is full.
        var editMon = learnsetMonster is { } lm && lm.Species.Id == species.Id ? learnsetMonster : null;

        // Freeze the list while the replace overlay is up; the shared scroller gates per-row clicks
        // to the visible area, which stops the Teach buttons from reacting through the clip edges.
        var prevInteractive = LgUi.Interactive;
        if (teachPendingMove is not null)
        {
            LgUi.Interactive = false;
        }

        DrawScrollList(list, 48f * scale, 4f * scale, rows.Length, ref dexEntryScroll, scale,
            (i, row) => DrawLearnsetRow(rows[i], editMon, row, theme, scale));

        LgUi.Interactive = prevInteractive;

        if (editMon is not null && teachPendingMove is not null)
        {
            DrawTeachReplace(content, theme, editMon, scale);
        }
    }

    // A teachable/learnable move row: a level-up move (Level >= 0) or a TM move (Level = -1, TmId set).
    private readonly record struct LearnRow(int Level, MoveDef Move, string? TmId);

    private static LearnRow[] BuildLearnRows(MonsterSpecies species)
    {
        var levelUp = species.Learnset.OrderBy(entry => entry.Level).ThenBy(entry => entry.Move.Name)
            .Select(entry => new LearnRow(entry.Level, entry.Move, null));
        var tms = species.TmMoveIds
            .Select(id => (id, move: Moves.Find(id)))
            .Where(entry => entry.move is not null)
            .OrderBy(entry => Tms.NumberOf(entry.id))
            .Select(entry => new LearnRow(-1, entry.move!, entry.id));
        return levelUp.Concat(tms).ToArray();
    }

    private void DrawLearnsetRow(LearnRow entry, MonsterInstance? editMon, Rect row, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var isTm = entry.TmId is not null;
        var known = editMon is not null && editMon.Knows(entry.Move);
        var ownsTm = isTm && State.OwnedTms.Contains(entry.TmId!);
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(row.Min, row.Max);
        var elementColor = Elements.Color(entry.Move.Element);
        RosterUi.DarkCard(drawList, row, 8f * scale, scale, hovered, accent: known ? elementColor : null);

        var source = isTm ? Tms.Label(Tms.NumberOf(entry.TmId!)) : $"Lv {entry.Level}";
        Typography.DrawCentered(new Vector2(row.Min.X + 28f * scale, row.Center.Y), source,
            isTm ? RosterUi.CountGreen : RosterUi.CardInk, TextStyles.Caption2);
        var moveX = row.Min.X + 58f * scale;
        var rightReserve = editMon is not null ? 74f * scale : 50f * scale;
        Typography.Draw(new Vector2(moveX, row.Min.Y + 7f * scale),
            FitLabel(entry.Move.Name, row.Max.X - moveX - rightReserve, TextStyles.SubheadlineEmphasized),
            RosterUi.CardInk, TextStyles.SubheadlineEmphasized);
        var detail = $"{Elements.Name(entry.Move.Element)}  |  {entry.Move.CategoryLabel}  |  {entry.Move.Pp} PP";
        Typography.Draw(new Vector2(moveX, row.Min.Y + 27f * scale),
            FitLabel(detail, row.Max.X - moveX - rightReserve, TextStyles.Caption2),
            RosterUi.CardMuted, TextStyles.Caption2);

        if (editMon is not null)
        {
            var pill = CenteredAt(new Vector2(row.Max.X - 38f * scale, row.Center.Y),
                new Vector2(64f * scale, 26f * scale));
            if (known)
            {
                Typography.DrawCentered(pill.Center, "Known", RosterUi.CountGreen, TextStyles.Caption1);
            }
            else if (isTm && !ownsTm)
            {
                Typography.DrawCentered(pill.Center, "Need TM", RosterUi.CardMuted, TextStyles.Caption2);
            }
            else if (!isTm && entry.Level > editMon.Level)
            {
                Typography.DrawCentered(pill.Center, "Locked", RosterUi.CardMuted, TextStyles.Caption1);
            }
            else if (RosterUi.ColorButton(pill, "Teach", RosterUi.Green, scale, true))
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
                GamePalette.Lighten(elementColor, 0.22f), TextStyles.Headline);
        }

        if (hovered)
        {
            ShowTooltip(BuildProfileMoveTooltip(entry.Move, entry.Move.Pp));
        }
    }

    // Overlay shown when teaching a move to a full (4-move) creature: pick which move to forget.
    private void DrawTeachReplace(Rect content, PhoneTheme theme, MonsterInstance mon, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.66f)));
        var panelMin = new Vector2(content.Min.X + 20f * scale, content.Center.Y - 120f * scale);
        var panelMax = new Vector2(content.Max.X - 20f * scale, content.Center.Y + 120f * scale);
        RosterUi.ChunkyCard(drawList, panelMin, panelMax, 14f * scale, scale, RosterUi.Cream, RosterUi.CreamShade,
            RosterUi.NavyEdge);
        var panel = new Rect(panelMin, panelMax);

        Typography.DrawCentered(new Vector2(panel.Center.X, panelMin.Y + 18f * scale),
            $"Teach {teachPendingMove!.Name}", RosterUi.InkNavy, TextStyles.Headline);
        Typography.DrawCentered(new Vector2(panel.Center.X, panelMin.Y + 40f * scale),
            $"Choose a move for {mon.Name} to forget", RosterUi.InkTan, TextStyles.Caption1);

        for (var i = 0; i < mon.Moves.Count; i++)
        {
            var move = mon.Moves[i];
            var column = i % 2;
            var rowIdx = i / 2;
            var size = new Vector2(panel.Width * 0.42f, 40f * scale);
            var center = new Vector2(panel.Center.X + (column == 0 ? -1f : 1f) * panel.Width * 0.235f,
                panelMin.Y + (72f + rowIdx * 50f) * scale);
            var rect = new Rect(center - size * 0.5f, center + size * 0.5f);
            if (RosterUi.ColorButton(rect, FitLabel(move.Name, size.X - 12f * scale, TextStyles.Subheadline),
                    Elements.Color(move.Element), scale, true,
                    sub: $"{Elements.Name(move.Element)}  {mon.Pp[i]}/{move.Pp} PP"))
            {
                mon.ReplaceMove(i, teachPendingMove);
                teachPendingMove = null;
                State.Save();
                return;
            }
        }

        var cancel = CenteredAt(new Vector2(panel.Center.X, panelMax.Y - 22f * scale),
            new Vector2(panel.Width * 0.5f, 30f * scale));
        if (RosterUi.BlueButton(cancel, "KEEP CURRENT MOVES", scale, true))
        {
            teachPendingMove = null;
        }
    }
}
