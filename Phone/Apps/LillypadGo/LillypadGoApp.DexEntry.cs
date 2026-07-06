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
            view = View.Dex;
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
            view = View.Dex;
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
        Typography.Draw(new Vector2(textX, heroMin.Y + 72f * scale), caught ? "Captured" : "Observed",
            caught ? Accent : theme.TextMuted, TextStyles.Caption1);
        Typography.Draw(new Vector2(textX, heroMin.Y + 91f * scale), $"Catch rate  {species.CatchRate}/255",
            theme.TextMuted, TextStyles.Caption2);

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
        Typography.Draw(new Vector2(infoMin.X + 14f * scale, infoMin.Y + 14f * scale), "Habitats",
            theme.TextStrong, TextStyles.SubheadlineEmphasized);
        var habitats = ArrZones.Habitats(species.Id);
        var lines = WrapText(string.IsNullOrWhiteSpace(habitats) ? "No ARR habitat recorded." : habitats,
            infoMax.X - infoMin.X - 28f * scale, TextStyles.Body);
        for (var i = 0; i < lines.Count && i < 4; i++)
        {
            Typography.Draw(new Vector2(infoMin.X + 14f * scale, infoMin.Y + (42f + i * 20f) * scale), lines[i],
                theme.TextMuted, TextStyles.Body);
        }

        Typography.Draw(new Vector2(infoMin.X + 14f * scale, infoMax.Y - 42f * scale),
            $"{species.Learnset.Length} level-up moves", theme.TextMuted, TextStyles.Caption1);
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
        var rowHeight = 48f * scale;
        var gap = 4f * scale;
        var contentHeight = moves.Length * (rowHeight + gap);
        var maxScroll = MathF.Max(0f, contentHeight - list.Height);
        dexEntryScroll = Math.Clamp(dexEntryScroll, 0f, maxScroll);
        if (list.Contains(ImGui.GetMousePos()) && LgUi.Interactive)
        {
            dexEntryScroll = Math.Clamp(dexEntryScroll - ImGui.GetIO().MouseWheel * 48f * scale, 0f, maxScroll);
        }

        var y = list.Min.Y - dexEntryScroll;
        drawList.PushClipRect(list.Min, list.Max, true);
        foreach (var entry in moves)
        {
            var row = new Rect(new Vector2(list.Min.X + 4f * scale, y),
                new Vector2(list.Max.X - 4f * scale, y + rowHeight));
            if (RowVisible(row, list))
            {
                var hovered = list.Contains(ImGui.GetMousePos()) && row.Contains(ImGui.GetMousePos());
                LgUi.Card(drawList, row.Min, row.Max, 8f * scale, scale, hovered);
                Typography.DrawCentered(new Vector2(row.Min.X + 28f * scale, row.Center.Y),
                    $"Lv {entry.Level}", theme.TextStrong, TextStyles.Caption1);
                var moveX = row.Min.X + 58f * scale;
                var rightReserve = 50f * scale;
                Typography.Draw(new Vector2(moveX, row.Min.Y + 7f * scale),
                    FitLabel(entry.Move.Name, row.Max.X - moveX - rightReserve, TextStyles.SubheadlineEmphasized),
                    theme.TextStrong, TextStyles.SubheadlineEmphasized);
                var detail = $"{Elements.Name(entry.Move.Element)}  |  {entry.Move.CategoryLabel}  |  {entry.Move.Pp} PP";
                Typography.Draw(new Vector2(moveX, row.Min.Y + 27f * scale),
                    FitLabel(detail, row.Max.X - moveX - rightReserve, TextStyles.Caption2), theme.TextMuted,
                    TextStyles.Caption2);
                var power = entry.Move.IsStatus ? "--" : entry.Move.Power.ToString();
                Typography.DrawCentered(new Vector2(row.Max.X - 26f * scale, row.Center.Y), power,
                    Elements.Color(entry.Move.Element), TextStyles.Headline);
                if (hovered)
                {
                    ImGui.SetTooltip(BuildProfileMoveTooltip(entry.Move, entry.Move.Pp));
                }
            }

            y += rowHeight + gap;
        }
        drawList.PopClipRect();

        LgUi.Scrollbar(new Rect(new Vector2(content.Max.X - 6f * scale, list.Min.Y),
                new Vector2(content.Max.X - 3f * scale, list.Max.Y)), dexEntryScroll, maxScroll,
            list.Height / MathF.Max(list.Height, contentHeight), Accent, scale);
    }
}
