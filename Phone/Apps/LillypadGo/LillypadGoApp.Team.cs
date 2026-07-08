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
    // ---- Team / Bag -----------------------------------------------------------------

    private void DrawTeam(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        BiomeBackdrop.Draw(drawList, content, State.CurrentBiome, time, false);
        LgUi.Header(content, theme, Accent,"Roster", null, scale);

        var segBounds = CenteredAt(new Vector2(content.Center.X, content.Min.Y + 52f * scale),
            new Vector2(232f * scale, 30f * scale));
        var tabs = new[] { $"Team {State.Party.Count}/6", $"Storage {State.Box.Count}" };
        var pickedTab = LgUi.Segmented(segBounds, tabs, teamShowingStorage ? 1 : 0, Accent, theme, scale,
            ref teamTabIndicator);
        if (pickedTab >= 0)
        {
            teamShowingStorage = pickedTab == 1;
            teamPage = 0;
        }

        var all = teamShowingStorage ? State.Box : State.Party;

        // Sort control (cycles): Caught order / Level / Type / Dex # / Name.
        var sortRect = CenteredAt(new Vector2(content.Min.X + 92f * scale, content.Min.Y + 84f * scale),
            new Vector2(160f * scale, 24f * scale));
        if (LgUi.Button(sortRect, $"Sort: {TeamSortLabel(teamSort)}", GamePalette.Cell, theme, true))
        {
            teamSort = (TeamSort)(((int)teamSort + 1) % 5);
            teamPage = 0;
        }

        if (ImGui.IsMouseHoveringRect(sortRect.Min, sortRect.Max))
        {
            ImGui.SetTooltip("Tap to change how this list is ordered.");
        }

        var sorted = SortedTeamView(all, teamSort);
        var top = content.Min.Y + 102f * scale;
        var bottom = content.Max.Y - 52f * scale;
        var rowH = 62f * scale;
        var rowsPerPage = Math.Max(1, (int)((bottom - top) / rowH));
        var pageCount = Math.Max(1, (sorted.Count + rowsPerPage - 1) / rowsPerPage);
        teamPage = Math.Clamp(teamPage, 0, pageCount - 1);
        var start = teamPage * rowsPerPage;
        var visible = Math.Min(rowsPerPage, sorted.Count - start);
        for (var row = 0; row < visible; row++)
        {
            var (m, index) = sorted[start + row];
            var min = new Vector2(content.Min.X + 12f * scale, top + row * rowH + 4f * scale);
            var max = new Vector2(content.Max.X - 12f * scale, top + (row + 1) * rowH - 4f * scale);
            var rowHovered = ImGui.IsMouseHoveringRect(min, max);
            LgUi.Card(drawList, min, max, 12f * scale, scale, rowHovered);
            var portrait = new Vector2(min.X + rowH * 0.5f, (min.Y + max.Y) * 0.5f);
            MonsterArt.Draw(drawList, portrait, rowH * 0.28f, m.Species, 1f,
                new MonsterPose(time + index, 0f, 0f, 1f, m.Fainted));
            if (m.IsFavorite)
            {
                drawList.AddCircle(portrait, rowH * 0.34f, ImGui.GetColorU32(Accent with { W = 0.9f }), 28,
                    1.8f * scale);
            }
            var rosterName = FitLabel($"{m.Name}   Lv {m.Level}", max.X - min.X - rowH * 0.95f - 76f * scale,
                TextStyles.Headline);
            Typography.Draw(new Vector2(min.X + rowH * 0.95f, min.Y + 6f * scale), rosterName, theme.TextStrong,
                TextStyles.Headline);
            LgUi.TypeChips(drawList, new Vector2(min.X + rowH * 0.95f, min.Y + 25f * scale), m.Element,
                m.SecondaryElement, scale);
            LgUi.HpBar(drawList, new Vector2(min.X + rowH * 0.95f, max.Y - 9f * scale),
                new Vector2(max.X - 72f * scale, max.Y - 4f * scale), m.HpFraction);

            var rowRect = new Rect(min, max);
            if (rowRect.Contains(ImGui.GetMousePos()))
            {
                ImGui.SetTooltip(BuildMonsterTooltip(m, teamShowingStorage
                    ? "Stored creature. Add it to the team or swap it with the last team slot."
                    : index == 0 ? "Current lead creature." : "Team creature."));
            }

            var detailRect = new Rect(min, new Vector2(max.X - 70f * scale, max.Y));
            if (detailRect.Contains(ImGui.GetMousePos()))
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    OpenDetail(m, View.Team);
                    return;
                }
            }

            if (teamShowingStorage)
            {
                var action = State.Party.Count < LillypadGoState.PartyLimit ? "Add" : "Swap";
                if (LgUi.Button(CenteredAt(new Vector2(max.X - 34f * scale, (min.Y + max.Y) * 0.5f),
                        new Vector2(58f * scale, 26f * scale)), action, Accent, theme, true))
                {
                    MoveStoredToTeam(index);
                    return;
                }
            }
            else
            {
                var leadRect = CenteredAt(new Vector2(max.X - 34f * scale, min.Y + 17f * scale),
                    new Vector2(58f * scale, 20f * scale));
                var canLead = index != 0;
                if (LgUi.Button(leadRect, "Lead", canLead ? Accent : GamePalette.CellSunken, theme, canLead))
                {
                    var lead = State.Party[index];
                    State.Party.RemoveAt(index);
                    State.Party.Insert(0, lead);
                    State.Save();
                    return;
                }

                var storeRect = CenteredAt(new Vector2(max.X - 34f * scale, max.Y - 13f * scale),
                    new Vector2(58f * scale, 20f * scale));
                var canStore = State.Party.Count > 1;
                if (LgUi.Button(storeRect, "Store", canStore ? GamePalette.Cell : GamePalette.CellSunken, theme,
                        canStore))
                {
                    MoveTeamToStorage(index);
                    return;
                }
            }
        }

        if (all.Count == 0)
        {
            LgUi.EmptyState(new Vector2(content.Center.X, content.Center.Y),
                teamShowingStorage ? FontAwesomeIcon.BoxOpen : FontAwesomeIcon.Paw,
                teamShowingStorage ? "Storage is empty." : "Your team is empty.", theme, scale);
        }

        if (pageCount > 1)
        {
            var pagerY = content.Max.Y - 56f * scale;
            if (teamPage > 0 && LgUi.Button(CenteredAt(new Vector2(content.Center.X - 68f * scale, pagerY),
                    new Vector2(70f * scale, 24f * scale)), "Previous", GamePalette.Cell, theme, true))
            {
                teamPage--;
            }

            Typography.DrawCentered(new Vector2(content.Center.X, pagerY), $"{teamPage + 1}/{pageCount}",
                theme.TextMuted, TextStyles.Caption1);
            if (teamPage + 1 < pageCount && LgUi.Button(CenteredAt(new Vector2(content.Center.X + 68f * scale, pagerY),
                    new Vector2(70f * scale, 24f * scale)), "Next", GamePalette.Cell, theme, true))
            {
                teamPage++;
            }
        }

        DrawNavigation(content, theme, scale);
    }

    private static Rect CenteredAt(Vector2 center, Vector2 size) => new(center - size * 0.5f, center + size * 0.5f);

    // A sorted VIEW over the party/box that preserves each creature's real index (so the row
    // actions still target the right slot in the underlying list).
    private static List<(MonsterInstance Mon, int Index)> SortedTeamView(List<MonsterInstance> list, TeamSort sort)
    {
        var indexed = list.Select((m, i) => (Mon: m, Index: i));
        var ordered = sort switch
        {
            TeamSort.Level => indexed.OrderByDescending(e => e.Mon.Level).ThenBy(e => e.Index),
            TeamSort.Type => indexed.OrderBy(e => (int)e.Mon.Element).ThenByDescending(e => e.Mon.Level),
            TeamSort.Dex => indexed.OrderBy(e => e.Mon.Species.DexNumber),
            TeamSort.Name => indexed.OrderBy(e => e.Mon.Name, StringComparer.OrdinalIgnoreCase),
            _ => indexed,
        };
        return ordered.ToList();
    }

    private static string TeamSortLabel(TeamSort sort) => sort switch
    {
        TeamSort.Level => "Level",
        TeamSort.Type => "Type",
        TeamSort.Dex => "Dex #",
        TeamSort.Name => "Name",
        _ => "Caught",
    };

    private void MoveStoredToTeam(int boxIndex)
    {
        if (boxIndex < 0 || boxIndex >= State.Box.Count)
        {
            return;
        }

        var incoming = State.Box[boxIndex];
        State.Box.RemoveAt(boxIndex);
        if (State.Party.Count < LillypadGoState.PartyLimit)
        {
            State.Party.Add(incoming);
        }
        else
        {
            var outgoing = State.Party[^1];
            State.Party[^1] = incoming;
            State.Box.Add(outgoing);
        }

        State.Save();
    }

    private static void MoveTeamToStorage(int partyIndex)
    {
        if (State.Party.Count <= 1 || partyIndex < 0 || partyIndex >= State.Party.Count)
        {
            return;
        }

        var outgoing = State.Party[partyIndex];
        State.Party.RemoveAt(partyIndex);
        State.Box.Add(outgoing);
        State.Save();
    }

    private void OpenDetail(MonsterInstance monster, View returnView)
    {
        detailMonster = monster;
        detailReturnView = returnView;
        detailNameDraft = monster.Nickname;
        releaseConfirm = false;
        draggingMoveIndex = -1;
        view = View.Detail;
    }

    // The Poké Dollars earned for releasing a creature (scales with level and species strength).
    private static int ReleaseValue(MonsterInstance monster) =>
        Math.Max(40, monster.Level * 12 + monster.Species.BaseStatTotal / 3);

    private void ReleaseMonster(MonsterInstance monster)
    {
        var reward = ReleaseValue(monster);
        var removed = State.Party.Remove(monster) || State.Box.Remove(monster);
        if (!removed)
        {
            return;
        }

        State.Money += reward;
        releaseConfirm = false;
        detailMonster = null;
        State.Save();
        view = detailReturnView;
    }

}
