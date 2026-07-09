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
    // ---- Roster / Storage --------------------------------------------------------------
    // A box-storage screen laid out like the mainline games: the Party column on the left (top slot
    // is the lead) and a paged Box grid on the right. Everything is drag-and-drop — reorder within
    // the party or box, and drag between them to withdraw/deposit. A tap (no drag) opens the detail
    // screen.

    private void DrawTeam(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        BiomeBackdrop.Draw(drawList, content, State.CurrentBiome, time, false);
        LgUi.Header(content, theme, Accent, "Roster & Box",
            $"Party {State.Party.Count}/{LillypadGoState.PartyLimit}   ·   Stored {State.Box.Count}", scale);

        var mouse = ImGui.GetMousePos();
        var dragging = draggingRosterMon is not null;
        if (dragging && !State.Party.Contains(draggingRosterMon!) && !State.Box.Contains(draggingRosterMon!))
        {
            draggingRosterMon = null;
            dragging = false;
        }

        // ---- Geometry ----
        var top = content.Min.Y + 60f * scale;
        var bottom = content.Max.Y - 50f * scale;
        var partyX = content.Min.X + 10f * scale;
        var partyW = MathF.Max(96f * scale, content.Width * 0.30f);
        var boxX = partyX + partyW + 8f * scale;
        var boxRight = content.Max.X - 8f * scale;
        var boxW = boxRight - boxX;

        var slotTop = top + 18f * scale;
        var slotH = (bottom - slotTop) / LillypadGoState.PartyLimit;

        var gridTop = top + 30f * scale;
        var gridBottom = bottom - 30f * scale; // leave a row for the Box List / Search controls
        const int cols = 5;
        var gap = 4f * scale;
        var cellW = (boxW - gap * (cols - 1)) / cols;
        var rows = Math.Max(1, (int)((gridBottom - gridTop + gap) / (cellW + gap)));
        var perPage = cols * rows;
        var numPages = Math.Max(1, (State.Box.Count + perPage) / perPage);
        boxPage = Math.Clamp(boxPage, 0, numPages - 1);
        var searching = boxSearch.Trim().Length > 0;

        // ---- Party column ----
        Typography.Draw(new Vector2(partyX + 4f * scale, top), "PARTY", Accent with { W = 0.95f },
            TextStyles.Caption2);
        for (var i = 0; i < LillypadGoState.PartyLimit; i++)
        {
            var r = new Rect(new Vector2(partyX, slotTop + i * slotH + 2f * scale),
                new Vector2(boxX - 8f * scale, slotTop + (i + 1) * slotH - 2f * scale));
            var mon = i < State.Party.Count ? State.Party[i] : null;
            var isDragged = dragging && ReferenceEquals(mon, draggingRosterMon);
            var over = dragging && r.Contains(mouse);
            var hovered = !dragging && mon is not null && r.Contains(mouse);
            LgUi.Card(drawList, r.Min, r.Max, 10f * scale, scale, hovered, sunken: mon is null);
            if (i == 0)
            {
                drawList.AddRectFilled(r.Min, new Vector2(r.Min.X + 4f * scale, r.Max.Y),
                    ImGui.GetColorU32(Accent with { W = mon is not null ? 0.9f : 0.35f }), 3f * scale);
            }

            if (over && !isDragged)
            {
                Squircle.Stroke(drawList, r.Min, r.Max, 10f * scale, ImGui.GetColorU32(Accent with { W = 0.9f }),
                    2f * scale);
            }

            if (mon is not null && !isDragged)
            {
                DrawPartySlot(drawList, r, mon, i, theme, scale);
                if (hovered)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ShowTooltip(BuildMonsterTooltip(mon,
                        i == 0 ? "Lead creature. Drag to reorder or deposit." : "Drag to reorder, deposit, or tap."));
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        BeginRosterDrag(mon, mouse);
                    }
                }
            }
            else if (mon is null)
            {
                Typography.DrawCentered(r.Center, i == 0 ? "LEAD" : "empty",
                    theme.TextStrong with { W = 0.4f }, TextStyles.Caption2);
            }
        }

        // ---- Box header: page arrows (left) + Sort button (right) ----
        var navRight = boxX + boxW * 0.50f;
        var arrowSize = new Vector2(20f * scale, 22f * scale);
        var prev = CenteredAt(new Vector2(boxX + 13f * scale, top + 11f * scale), arrowSize);
        var next = CenteredAt(new Vector2(navRight - 13f * scale, top + 11f * scale), arrowSize);
        if (LgUi.Button(prev, "<", GamePalette.Cell, theme, boxPage > 0))
        {
            boxPage--;
        }

        if (LgUi.Button(next, ">", GamePalette.Cell, theme, boxPage < numPages - 1))
        {
            boxPage++;
        }

        Typography.DrawCentered(new Vector2((boxX + navRight) * 0.5f, top + 11f * scale),
            $"Box {boxPage + 1}/{numPages}", theme.TextStrong, TextStyles.SubheadlineEmphasized);

        var sortRect = new Rect(new Vector2(navRight + 6f * scale, top),
            new Vector2(boxRight, top + 23f * scale));
        if (LgUi.Button(sortRect, $"Sort {BoxSortLabel(boxSort)}", GamePalette.Cell, theme, State.Box.Count > 1))
        {
            SortBox();
            boxSort = (boxSort + 1) % 4;
        }

        if (!dragging && ImGui.IsMouseHoveringRect(sortRect.Min, sortRect.Max))
        {
            ShowTooltip("Sort the box by Dex number, level, type, then name.");
        }

        // ---- Box grid ----
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var idx = boxPage * perPage + row * cols + col;
                var min = new Vector2(boxX + col * (cellW + gap), gridTop + row * (cellW + gap));
                var r = new Rect(min, min + new Vector2(cellW, cellW));
                if (r.Max.Y > bottom + 2f * scale)
                {
                    continue;
                }

                var mon = idx < State.Box.Count ? State.Box[idx] : null;
                var match = mon is null || !searching ||
                    mon.Name.Contains(boxSearch.Trim(), StringComparison.OrdinalIgnoreCase);
                var isDragged = dragging && ReferenceEquals(mon, draggingRosterMon);
                var over = dragging && r.Contains(mouse);
                var hovered = !dragging && match && mon is not null && r.Contains(mouse);
                LgUi.Card(drawList, r.Min, r.Max, 8f * scale, scale, hovered, sunken: mon is null || !match);
                if (over && !isDragged)
                {
                    Squircle.Stroke(drawList, r.Min, r.Max, 8f * scale, ImGui.GetColorU32(Accent with { W = 0.9f }),
                        2f * scale);
                }

                if (mon is not null && !isDragged)
                {
                    MonsterArt.Draw(drawList, r.Center, cellW * 0.42f, mon.Species, 1f,
                        new MonsterPose(time + idx * 0.3f, 0f, 0f, match ? 1f : 0.28f, mon.Fainted));
                    if (mon.IsFavorite && match)
                    {
                        drawList.AddCircle(r.Center, cellW * 0.46f, ImGui.GetColorU32(Accent with { W = 0.85f }), 24,
                            1.4f * scale);
                    }

                    if (hovered)
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        ShowTooltip(BuildMonsterTooltip(mon, "Stored. Drag to your party or reorder, or tap."));
                        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        {
                            BeginRosterDrag(mon, mouse);
                        }
                    }
                }
            }
        }

        if (State.Box.Count == 0 && !dragging)
        {
            Typography.DrawCentered(new Vector2((boxX + boxRight) * 0.5f, (gridTop + gridBottom) * 0.5f),
                "Box is empty — drag a party member here to store it.", theme.TextStrong with { W = 0.55f },
                TextStyles.Caption2);
        }

        // ---- Box List + Search row ----
        var ctrlY = gridBottom + 5f * scale;
        var boxListRect = new Rect(new Vector2(boxX, ctrlY), new Vector2(boxX + boxW * 0.30f, ctrlY + 24f * scale));
        if (LgUi.Button(boxListRect, "Boxes", GamePalette.Cell, theme, numPages > 1))
        {
            boxPage = 0;
        }

        var searchLeft = boxX + boxW * 0.34f;
        ProgressRing.CenterIcon(drawList, new Vector2(searchLeft + 8f * scale, ctrlY + 12f * scale),
            FontAwesomeIcon.Search, theme.TextMuted, 12f * scale);
        var searchRight = searching ? boxRight - 24f * scale : boxRight;
        LgUi.Input(new Rect(new Vector2(searchLeft + 18f * scale, ctrlY), new Vector2(searchRight, ctrlY + 24f * scale)),
            "##boxsearch", ref boxSearch, 16, theme, scale);
        if (searching)
        {
            var clearRect = new Rect(new Vector2(searchRight + 2f * scale, ctrlY),
                new Vector2(boxRight, ctrlY + 24f * scale));
            if (LgUi.Button(clearRect, "x", GamePalette.Cell, theme, true))
            {
                boxSearch = string.Empty;
            }
        }

        // ---- Active drag: floating sprite + drop resolution ----
        if (dragging)
        {
            if (!rosterDragMoved && Vector2.Distance(mouse, rosterDragOrigin) > 6f * scale)
            {
                rosterDragMoved = true;
            }

            MonsterArt.Draw(drawList, mouse, cellW * 0.5f, draggingRosterMon!.Species, 1f,
                MonsterPose.Idle(time));

            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var mon = draggingRosterMon!;
                draggingRosterMon = null;
                if (!rosterDragMoved)
                {
                    OpenDetail(mon, View.Team);
                    return;
                }

                var inParty = mouse.X < boxX - 4f * scale && mouse.Y >= slotTop - slotH * 0.5f && mouse.Y <= bottom;
                var inBox = mouse.X >= boxX && mouse.X <= boxRight && mouse.Y >= gridTop - gap && mouse.Y <= bottom;
                if (inParty)
                {
                    var slot = Math.Clamp((int)MathF.Floor((mouse.Y - slotTop) / slotH), 0, State.Party.Count);
                    DropOnParty(mon, slot);
                }
                else if (inBox)
                {
                    var col = Math.Clamp((int)((mouse.X - boxX) / (cellW + gap)), 0, cols - 1);
                    var row = Math.Clamp((int)((mouse.Y - gridTop) / (cellW + gap)), 0, rows - 1);
                    DropOnBox(mon, Math.Clamp(boxPage * perPage + row * cols + col, 0, State.Box.Count));
                }
            }
        }

        DrawNavigation(content, theme, scale);
    }

    private void BeginRosterDrag(MonsterInstance mon, Vector2 mouse)
    {
        draggingRosterMon = mon;
        rosterDragOrigin = mouse;
        rosterDragMoved = false;
    }

    private void DrawPartySlot(ImDrawListPtr drawList, Rect r, MonsterInstance m, int index, PhoneTheme theme,
        float scale)
    {
        var portrait = new Vector2(r.Min.X + r.Height * 0.5f, r.Center.Y);
        MonsterArt.Draw(drawList, portrait, r.Height * 0.36f, m.Species, 1f,
            new MonsterPose(time + index, 0f, 0f, 1f, m.Fainted));
        if (m.IsFavorite)
        {
            drawList.AddCircle(portrait, r.Height * 0.42f, ImGui.GetColorU32(Accent with { W = 0.85f }), 24,
                1.4f * scale);
        }

        var tx = r.Min.X + r.Height + 2f * scale;
        var textWidth = r.Max.X - tx - 6f * scale;
        Typography.Draw(new Vector2(tx, r.Min.Y + 7f * scale), FitLabel(m.Name, textWidth, TextStyles.Subheadline),
            theme.TextStrong, TextStyles.Subheadline);
        Typography.Draw(new Vector2(tx, r.Min.Y + 25f * scale), $"Lv {m.Level}",
            theme.TextStrong with { W = 0.82f }, TextStyles.Caption2);
        LgUi.HpBar(drawList, new Vector2(tx, r.Max.Y - 9f * scale),
            new Vector2(r.Max.X - 6f * scale, r.Max.Y - 4f * scale), m.HpFraction);
    }

    // Drops a creature onto a party slot: reorder within the party, or withdraw from the box (swapping
    // with the slot's occupant when the party is already full).
    private void DropOnParty(MonsterInstance mon, int slot)
    {
        if (State.Party.Contains(mon))
        {
            State.Party.Remove(mon);
            State.Party.Insert(Math.Clamp(slot, 0, State.Party.Count), mon);
        }
        else if (State.Box.Contains(mon))
        {
            if (State.Party.Count < LillypadGoState.PartyLimit)
            {
                State.Box.Remove(mon);
                State.Party.Insert(Math.Clamp(slot, 0, State.Party.Count), mon);
            }
            else
            {
                var target = Math.Clamp(slot, 0, State.Party.Count - 1);
                var displaced = State.Party[target];
                State.Box.Remove(mon);
                State.Party[target] = mon;
                State.Box.Add(displaced);
            }
        }

        State.Save();
    }

    // Drops a creature onto a box cell: reorder within the box, or deposit from the party (never the
    // last remaining party member).
    private void DropOnBox(MonsterInstance mon, int idx)
    {
        if (State.Box.Contains(mon))
        {
            State.Box.Remove(mon);
            State.Box.Insert(Math.Clamp(idx, 0, State.Box.Count), mon);
        }
        else if (State.Party.Contains(mon))
        {
            if (State.Party.Count <= 1)
            {
                return; // never empty the party
            }

            State.Party.Remove(mon);
            State.Box.Insert(Math.Clamp(idx, 0, State.Box.Count), mon);
        }

        State.Save();
    }

    private static string BoxSortLabel(int sort) => sort switch
    {
        1 => "Lvl",
        2 => "Type",
        3 => "Name",
        _ => "Dex",
    };

    // Reorders the whole box by the current criterion (Dex number, level, type, or name).
    private void SortBox()
    {
        Comparison<MonsterInstance> cmp = boxSort switch
        {
            1 => (a, b) => b.Level.CompareTo(a.Level),
            2 => (a, b) => ((int)a.Element).CompareTo((int)b.Element),
            3 => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            _ => (a, b) => a.Species.DexNumber.CompareTo(b.Species.DexNumber),
        };
        State.Box.Sort(cmp);
        boxPage = 0;
        State.Save();
    }

    private static Rect CenteredAt(Vector2 center, Vector2 size) => new(center - size * 0.5f, center + size * 0.5f);

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
