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
    // screen. Visuals follow the "Roster & Box" mockup: navy header/nav, cream panel, chunky
    // sprite-based cards (RosterUi + Assets/pokemon/roster).

    private void DrawTeam(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();

        var mouse = ImGui.GetMousePos();
        var dragging = draggingRosterMon is not null;
        if (dragging && !State.Party.Contains(draggingRosterMon!) && !State.Box.Contains(draggingRosterMon!))
        {
            draggingRosterMon = null;
            dragging = false;
        }

        // ---- Chrome: navy backdrop, blue header, cream content panel ----
        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(RosterUi.NavyBottom));
        var headerBottom = DrawRosterHeader(content, scale);
        var navTop = content.Max.Y - NavBarHeight * scale;
        var panel = new Rect(new Vector2(content.Min.X + 7f * scale, headerBottom + 6f * scale),
            new Vector2(content.Max.X - 7f * scale, navTop - 7f * scale));
        Squircle.FillVerticalGradient(drawList, panel.Min, panel.Max, 12f * scale,
            ImGui.GetColorU32(RosterUi.Cream), ImGui.GetColorU32(RosterUi.CreamShade));
        Squircle.Stroke(drawList, panel.Min, panel.Max, 12f * scale,
            ImGui.GetColorU32(RosterUi.NavyEdge with { W = 0.55f }), 1.6f * scale);

        // ---- Geometry ----
        var pad = 8f * scale;
        var partyX = panel.Min.X + pad;
        var partyW = MathF.Max(96f * scale, panel.Width * 0.30f);
        var boxX = partyX + partyW + 8f * scale;
        var boxRight = panel.Max.X - pad;
        var boxW = boxRight - boxX;
        var topRowTop = panel.Min.Y + pad;
        var topRowBottom = topRowTop + 25f * scale;

        var slotTop = topRowBottom + 6f * scale;
        var slotsBottom = panel.Max.Y - pad;
        var slotH = (slotsBottom - slotTop) / LillypadGoState.PartyLimit;

        var ctrlH = 25f * scale;
        var gridTop = slotTop;
        var gridBottom = panel.Max.Y - pad - ctrlH - 6f * scale; // leave the Boxes / Search row
        const int cols = 4;
        var gap = 5f * scale;
        var cellW = (boxW - gap * (cols - 1)) / cols;
        var cellH = MathF.Min(cellW * 1.30f, MathF.Max(cellW * 0.8f, gridBottom - gridTop));
        var rows = Math.Max(1, (int)((gridBottom - gridTop + gap) / (cellH + gap)));
        var perPage = cols * rows;
        var numPages = Math.Max(1, (State.Box.Count + perPage) / perPage);
        boxPage = Math.Clamp(boxPage, 0, numPages - 1);
        var searching = boxSearch.Trim().Length > 0;

        // ---- Party column ----
        RosterUi.GreenTab(drawList, new Rect(new Vector2(partyX, topRowTop), new Vector2(boxX - 8f * scale, topRowBottom)),
            "PARTY", scale);
        for (var i = 0; i < LillypadGoState.PartyLimit; i++)
        {
            var r = new Rect(new Vector2(partyX, slotTop + i * slotH + 2f * scale),
                new Vector2(boxX - 8f * scale, slotTop + (i + 1) * slotH - 2f * scale));
            var mon = i < State.Party.Count ? State.Party[i] : null;
            var isDragged = dragging && ReferenceEquals(mon, draggingRosterMon);
            var over = dragging && r.Contains(mouse);
            var hovered = !dragging && mon is not null && r.Contains(mouse);
            if (mon is not null && !isDragged)
            {
                DrawPartyCard(drawList, r, mon, i, hovered, scale);
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
            else
            {
                DrawEmptyPartySlot(drawList, r, isDragged, scale);
            }

            if (over && !isDragged)
            {
                Squircle.Stroke(drawList, r.Min, r.Max, 9f * scale,
                    ImGui.GetColorU32(RosterUi.GreenBright), 2.5f * scale);
            }
        }

        // ---- Box header: page arrows + BOX plate (left), SORT button (right) ----
        var navRight = boxX + boxW * 0.58f;
        var arrowSize = new Vector2(23f * scale, 23f * scale);
        var arrowY = (topRowTop + topRowBottom) * 0.5f;
        var prev = CenteredAt(new Vector2(boxX + 12f * scale, arrowY), arrowSize);
        var next = CenteredAt(new Vector2(navRight - 12f * scale, arrowY), arrowSize);
        RosterUi.BoxPlate(drawList,
            new Rect(new Vector2(prev.Max.X + 3f * scale, topRowTop + 1f * scale),
                new Vector2(next.Min.X - 3f * scale, topRowBottom - 1f * scale)),
            $"BOX {boxPage + 1}/{numPages}", scale);
        if (RosterUi.IconButton(prev, "arrow_left", "<", scale, boxPage > 0))
        {
            boxPage--;
        }

        if (RosterUi.IconButton(next, "arrow_right", ">", scale, boxPage < numPages - 1))
        {
            boxPage++;
        }

        var sortRect = new Rect(new Vector2(navRight + 6f * scale, topRowTop),
            new Vector2(boxRight, topRowBottom));
        if (RosterUi.BlueButton(sortRect, $"SORT {BoxSortLabel(boxSort).ToUpperInvariant()}", scale,
                State.Box.Count > 1))
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
                var min = new Vector2(boxX + col * (cellW + gap), gridTop + row * (cellH + gap));
                var r = new Rect(min, min + new Vector2(cellW, cellH));
                if (r.Max.Y > gridBottom + 2f * scale)
                {
                    continue;
                }

                var mon = idx < State.Box.Count ? State.Box[idx] : null;
                var match = mon is null || !searching ||
                    mon.Name.Contains(boxSearch.Trim(), StringComparison.OrdinalIgnoreCase);
                var isDragged = dragging && ReferenceEquals(mon, draggingRosterMon);
                var over = dragging && r.Contains(mouse);
                var hovered = !dragging && match && mon is not null && r.Contains(mouse);
                if (mon is not null && !isDragged)
                {
                    DrawBoxCard(drawList, r, mon, idx, match, hovered, scale);
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
                else
                {
                    DrawEmptyBoxTile(drawList, r, scale);
                }

                if (over && !isDragged)
                {
                    Squircle.Stroke(drawList, r.Min, r.Max, 8f * scale,
                        ImGui.GetColorU32(RosterUi.GreenBright), 2.5f * scale);
                }
            }
        }

        // ---- Boxes button + Search row ----
        var ctrlY = panel.Max.Y - pad - ctrlH;
        var boxListRect = new Rect(new Vector2(boxX, ctrlY), new Vector2(boxX + boxW * 0.34f, ctrlY + ctrlH));
        if (RosterUi.BlueButton(boxListRect, "BOXES", scale, numPages > 1, "box_cube"))
        {
            boxPage = 0;
        }

        var searchLeft = boxListRect.Max.X + 6f * scale;
        var searchRight = searching ? boxRight - 24f * scale : boxRight;
        RosterUi.SearchBar(new Rect(new Vector2(searchLeft, ctrlY), new Vector2(searchRight, ctrlY + ctrlH)),
            "##boxsearch", "Search", ref boxSearch, scale);
        if (searching)
        {
            var clearRect = new Rect(new Vector2(searchRight + 2f * scale, ctrlY),
                new Vector2(boxRight, ctrlY + ctrlH));
            if (RosterUi.BlueButton(clearRect, "x", scale, true))
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

                var inParty = mouse.X < boxX - 4f * scale && mouse.Y >= slotTop - slotH * 0.5f &&
                    mouse.Y <= slotsBottom;
                var inBox = mouse.X >= boxX && mouse.X <= boxRight && mouse.Y >= gridTop - gap &&
                    mouse.Y <= gridBottom + gap;
                if (inParty)
                {
                    var slot = Math.Clamp((int)MathF.Floor((mouse.Y - slotTop) / slotH), 0, State.Party.Count);
                    DropOnParty(mon, slot);
                }
                else if (inBox)
                {
                    var col = Math.Clamp((int)((mouse.X - boxX) / (cellW + gap)), 0, cols - 1);
                    var row = Math.Clamp((int)((mouse.Y - gridTop) / (cellH + gap)), 0, rows - 1);
                    DropOnBox(mon, Math.Clamp(boxPage * perPage + row * cols + col, 0, State.Box.Count));
                }
            }
        }

        DrawNavigation(content, theme, scale);
    }

    // The blue Pokémon-style header: navy gradient, faded Poké Ball watermarks, ball logo, outlined
    // "ROSTER & BOX" title and the inset Party/Stored count pill. Returns the header's bottom edge.
    private float DrawRosterHeader(Rect content, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var headerBottom = content.Min.Y + 64f * scale;
        var max = new Vector2(content.Max.X, headerBottom);
        drawList.AddRectFilledMultiColor(content.Min, max,
            ImGui.GetColorU32(RosterUi.NavyTop), ImGui.GetColorU32(RosterUi.NavyTop),
            ImGui.GetColorU32(RosterUi.NavyBottom), ImGui.GetColorU32(RosterUi.NavyBottom));

        drawList.PushClipRect(content.Min, max, true);
        RosterUi.Sprite(drawList, "watermark_dark", new Vector2(content.Min.X + 26f * scale, content.Min.Y + 44f * scale),
            44f * scale, new Vector4(1f, 1f, 1f, 0.55f));
        RosterUi.Sprite(drawList, "watermark_dark", new Vector2(content.Max.X - 34f * scale, content.Min.Y + 16f * scale),
            52f * scale, new Vector4(1f, 1f, 1f, 0.55f));
        RosterUi.Sprite(drawList, "watermark_dark", new Vector2(content.Max.X - 92f * scale, content.Min.Y + 54f * scale),
            36f * scale, new Vector4(1f, 1f, 1f, 0.45f));
        drawList.PopClipRect();

        drawList.AddLine(new Vector2(content.Min.X, content.Min.Y + 1f * scale),
            new Vector2(content.Max.X, content.Min.Y + 1f * scale),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.20f)), 1.2f * scale);
        drawList.AddLine(max with { X = content.Min.X }, max, ImGui.GetColorU32(RosterUi.NavyEdge), 2.5f * scale);

        var titleCenter = new Vector2(content.Center.X + 12f * scale, content.Min.Y + 21f * scale);
        RosterUi.TextOutlined(titleCenter, "ROSTER & BOX", new Vector4(1f, 1f, 1f, 1f), RosterUi.NavyEdge,
            TextStyles.Title2, scale);
        var titleWidth = Typography.Measure("ROSTER & BOX", TextStyles.Title2).X;
        RosterUi.Sprite(drawList, "logo_ball",
            new Vector2(titleCenter.X - titleWidth * 0.5f - 22f * scale, content.Min.Y + 25f * scale), 33f * scale);

        // Party/Stored count pill
        var partyLabel = "Party";
        var partyCount = $"{State.Party.Count}/{LillypadGoState.PartyLimit}";
        var storedLabel = "Stored";
        var storedCount = State.Box.Count.ToString();
        var style = TextStyles.FootnoteEmphasized;
        var gapW = 6f * scale;
        var totalW = Typography.Measure(partyLabel, style).X + Typography.Measure(partyCount, style).X +
            Typography.Measure("|", style).X + Typography.Measure(storedLabel, style).X +
            Typography.Measure(storedCount, style).X + gapW * 4f;
        var pillCenterY = content.Min.Y + 48f * scale;
        var pillMin = new Vector2(content.Center.X - totalW * 0.5f - 12f * scale, pillCenterY - 9f * scale);
        var pillMax = new Vector2(content.Center.X + totalW * 0.5f + 12f * scale, pillCenterY + 9f * scale);
        Squircle.Fill(drawList, pillMin, pillMax, 5f * scale, ImGui.GetColorU32(RosterUi.NavyInset));
        Squircle.Stroke(drawList, pillMin, pillMax, 5f * scale,
            ImGui.GetColorU32(RosterUi.NavyLine with { W = 0.45f }), 1f * scale);

        var x = content.Center.X - totalW * 0.5f;
        foreach (var (text, color) in new[]
                 {
                     (partyLabel, RosterUi.CountGreen), (partyCount, new Vector4(1f, 1f, 1f, 1f)),
                     ("|", RosterUi.NavyLine), (storedLabel, RosterUi.CountBlue),
                     (storedCount, new Vector4(1f, 1f, 1f, 1f)),
                 })
        {
            var w = Typography.Measure(text, style).X;
            Typography.DrawCentered(new Vector2(x + w * 0.5f, pillCenterY), text, color, style);
            x += w + gapW;
        }

        return headerBottom;
    }

    private void BeginRosterDrag(MonsterInstance mon, Vector2 mouse)
    {
        draggingRosterMon = mon;
        rosterDragOrigin = mouse;
        rosterDragMoved = false;
    }

    // An occupied party card: the lead gets the green-highlighted blue card, the rest the tan card.
    // Lv./gender top-left, watermark top-right, creature centre, HP pill + outlined name bottom.
    private void DrawPartyCard(ImDrawListPtr drawList, Rect r, MonsterInstance m, int index, bool hovered,
        float scale)
    {
        var radius = 9f * scale;
        var lead = index == 0;
        if (lead)
        {
            drawList.AddRectFilled(r.Min + new Vector2(0f, 2.5f * scale), r.Max + new Vector2(0f, 2.5f * scale),
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.16f)), radius);
            Squircle.Fill(drawList, r.Min, r.Max, radius, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)));
            Squircle.Stroke(drawList, r.Min, r.Max, radius, ImGui.GetColorU32(RosterUi.GreenBright), 2.5f * scale);
            var inset = 3f * scale;
            Squircle.FillVerticalGradient(drawList, r.Min + new Vector2(inset, inset),
                r.Max - new Vector2(inset, inset), radius - inset,
                ImGui.GetColorU32(hovered ? GamePalette.Lighten(RosterUi.BlueCardTop, 0.04f) : RosterUi.BlueCardTop),
                ImGui.GetColorU32(RosterUi.BlueCardBottom));
        }
        else
        {
            RosterUi.ChunkyCard(drawList, r.Min, r.Max, radius, scale, RosterUi.TanTop, RosterUi.TanBottom,
                RosterUi.TanEdge, hovered);
        }

        RosterUi.Watermark(drawList, new Vector2(r.Max.X - 17f * scale, r.Min.Y + 17f * scale), 26f * scale,
            lead ? new Vector4(1f, 1f, 1f, 0.35f) : RosterUi.TanEdge with { W = 0.45f });

        var ink = lead ? RosterUi.InkNavy : RosterUi.InkTan;
        Typography.Draw(new Vector2(r.Min.X + 7f * scale, r.Min.Y + 5f * scale), $"Lv. {m.Level}", ink,
            TextStyles.FootnoteEmphasized);
        if (m.Gender != Gender.Genderless)
        {
            RosterUi.Sprite(drawList, m.Gender == Gender.Male ? "gender_male" : "gender_female",
                new Vector2(r.Min.X + 12f * scale, r.Min.Y + 25f * scale), 11f * scale);
        }

        var artCenter = new Vector2(r.Center.X + 4f * scale, r.Center.Y - 2f * scale);
        MonsterArt.Draw(drawList, artCenter, r.Height * 0.33f, m.Species, 1f,
            new MonsterPose(time + index, 0f, 0f, 1f, m.Fainted));
        if (m.IsFavorite)
        {
            drawList.AddCircle(artCenter, r.Height * 0.40f, ImGui.GetColorU32(RosterUi.GreenBright with { W = 0.8f }),
                24, 1.4f * scale);
        }

        LgUi.HpBar(drawList, new Vector2(r.Min.X + 7f * scale, r.Max.Y - 13f * scale),
            new Vector2(r.Min.X + 33f * scale, r.Max.Y - 7f * scale), m.HpFraction);
        var nameLeft = r.Min.X + 38f * scale;
        var (name, nameStyle) = FitName(m.Name.ToUpperInvariant(), r.Max.X - 6f * scale - nameLeft,
            TextStyles.FootnoteEmphasized, TextStyles.Caption2,
            TextStyles.Caption2 with { Scale = 0.52f });
        RosterUi.TextOutlined(new Vector2((nameLeft + r.Max.X - 6f * scale) * 0.5f, r.Max.Y - 11f * scale), name,
            new Vector4(1f, 1f, 1f, 1f), lead ? RosterUi.InkNavy : GamePalette.Darken(RosterUi.TanEdge, 0.25f),
            nameStyle, scale);
    }

    // A larger gray card with a centered Poké Ball watermark and "EMPTY".
    private static void DrawEmptyPartySlot(ImDrawListPtr drawList, Rect r, bool highlightSource, float scale)
    {
        RosterUi.ChunkyCard(drawList, r.Min, r.Max, 9f * scale, scale,
            RosterUi.GrayTop, RosterUi.GrayBottom, RosterUi.GrayEdge);
        var tint = new Vector4(0.42f, 0.44f, 0.46f, highlightSource ? 0.35f : 0.6f);
        RosterUi.Watermark(drawList, new Vector2(r.Center.X, r.Center.Y - 6f * scale), r.Height * 0.44f, tint);
        Typography.DrawCentered(new Vector2(r.Center.X, r.Max.Y - 11f * scale), "EMPTY",
            new Vector4(0.45f, 0.47f, 0.49f, 0.9f), TextStyles.FootnoteEmphasized);
    }

    // An occupied box card: tan tile with Lv./gender header, creature centre and a dark name strip.
    private void DrawBoxCard(ImDrawListPtr drawList, Rect r, MonsterInstance m, int idx, bool match, bool hovered,
        float scale)
    {
        var radius = 8f * scale;
        RosterUi.ChunkyCard(drawList, r.Min, r.Max, radius, scale, RosterUi.TanTop, RosterUi.TanBottom,
            RosterUi.TanEdge, hovered);
        Typography.Draw(new Vector2(r.Min.X + 6f * scale, r.Min.Y + 4f * scale), $"Lv. {m.Level}", RosterUi.InkTan,
            TextStyles.Caption2);
        if (m.Gender != Gender.Genderless)
        {
            RosterUi.Sprite(drawList, m.Gender == Gender.Male ? "gender_male" : "gender_female",
                new Vector2(r.Max.X - 11f * scale, r.Min.Y + 10f * scale), 9f * scale);
        }

        MonsterArt.Draw(drawList, new Vector2(r.Center.X, r.Center.Y - 1f * scale), r.Width * 0.36f, m.Species, 1f,
            new MonsterPose(time + idx * 0.3f, 0f, 0f, match ? 1f : 0.4f, m.Fainted));
        if (m.IsFavorite && match)
        {
            drawList.AddCircle(new Vector2(r.Center.X, r.Center.Y - 1f * scale), r.Width * 0.42f,
                ImGui.GetColorU32(RosterUi.GreenBright with { W = 0.8f }), 24, 1.4f * scale);
        }

        var stripTop = r.Max.Y - 19f * scale;
        drawList.AddRectFilled(new Vector2(r.Min.X + 2f * scale, stripTop),
            r.Max - new Vector2(2f * scale, 2f * scale),
            ImGui.GetColorU32(new Vector4(0.22f, 0.20f, 0.16f, 0.72f)), radius - 2f * scale,
            ImDrawFlags.RoundCornersBottom);
        var (name, nameStyle) = FitName(m.Name, r.Width - 8f * scale,
            TextStyles.Caption2, TextStyles.Caption2 with { Scale = 0.46f },
            TextStyles.Caption2 with { Scale = 0.38f });
        Typography.DrawCentered(new Vector2(r.Center.X, stripTop + 9f * scale), name,
            new Vector4(1f, 1f, 1f, 0.96f), nameStyle);

        if (!match)
        {
            Squircle.Fill(drawList, r.Min, r.Max, radius, ImGui.GetColorU32(RosterUi.Cream with { W = 0.55f }));
        }
    }

    // An empty box slot: the pale Poké Ball watermark tile from the asset sheet.
    private static void DrawEmptyBoxTile(ImDrawListPtr drawList, Rect r, float scale)
    {
        if (RosterUi.SpriteRect(drawList, "slot_empty", r.Min, r.Max))
        {
            return;
        }

        Squircle.Fill(drawList, r.Min, r.Max, 8f * scale, ImGui.GetColorU32(RosterUi.TileCream));
        Squircle.Stroke(drawList, r.Min, r.Max, 8f * scale, ImGui.GetColorU32(RosterUi.TileEdge), 1.4f * scale);
        RosterUi.Watermark(drawList, r.Center, r.Width * 0.5f, RosterUi.TileEdge with { W = 0.4f });
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
