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
    // ---- Move Relearner ----------------------------------------------------------------
    // The creature and its four move slots up top, a Level-Up / TM learnset list below. Drag a move
    // from the learnset onto a slot to teach it (any level-up move at or below the creature's level,
    // or any move you own the TM for); drag the move cards to reorder them.

    private void DrawMoveRelearn(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        BiomeBackdrop.Draw(drawList, content, State.CurrentBiome, time, false);

        if (learnsetMonster is not { } mon)
        {
            view = View.Detail;
            return;
        }

        var species = mon.Species;
        LgUi.Header(content, theme, Accent, "Move Relearner", $"{mon.Name}   ·   Lv {mon.Level}", scale);

        var mouse = ImGui.GetMousePos();
        var top = content.Min.Y + 58f * scale;

        // ---- Creature strip: portrait + type chips ----
        var portrait = new Vector2(content.Min.X + 32f * scale, top + 18f * scale);
        ProgressRing.Glow(portrait, 22f * scale, Elements.Color(species.Element), 0.35f);
        MonsterArt.Draw(drawList, portrait, 22f * scale, species, 1f, MonsterPose.Idle(time));
        LgUi.TypeChips(drawList, new Vector2(content.Min.X + 60f * scale, top + 10f * scale), species.Element,
            species.SecondaryElement, scale);

        // ---- Moves grid (2x2) ----
        var movesY = top + 44f * scale;
        Typography.Draw(new Vector2(content.Min.X + 14f * scale, movesY), "Moves", theme.TextStrong,
            TextStyles.SubheadlineEmphasized);
        Typography.Draw(new Vector2(content.Min.X + 66f * scale, movesY + 2f * scale),
            "— drag a learnset move onto a slot", theme.TextStrong with { W = 0.55f }, TextStyles.Caption2);

        var gridTop = movesY + 20f * scale;
        var gap = 6f * scale;
        var cardW = (content.Width - 24f * scale - gap) / 2f;
        var cardH = 40f * scale;
        Rect Slot(int i)
        {
            var min = new Vector2(content.Min.X + 12f * scale + i % 2 * (cardW + gap),
                gridTop + i / 2 * (cardH + gap));
            return new Rect(min, min + new Vector2(cardW, cardH));
        }

        for (var i = 0; i < 4; i++)
        {
            var r = Slot(i);
            if (draggingMoveIndex == i)
            {
                DrawMoveSlotPlaceholder(drawList, r, scale);
                continue;
            }

            var over = (draggingLearnMove is not null || draggingMoveIndex >= 0) && r.Contains(mouse);
            if (i < mon.Moves.Count)
            {
                DrawMoveCard(drawList, r, mon, i, theme, scale, over);
                if (draggingMoveIndex < 0 && draggingLearnMove is null && r.Contains(mouse))
                {
                    // Show the move's full description on hover, just like the learnset rows.
                    ShowTooltip(BuildProfileMoveTooltip(mon.Moves[i], mon.Pp[i]));
                    if (mon.Moves.Count > 1)
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        {
                            draggingMoveIndex = i;
                        }
                    }
                }
            }
            else
            {
                DrawMoveSlotPlaceholder(drawList, r, scale);
                if (over)
                {
                    Squircle.Stroke(drawList, r.Min, r.Max, 9f * scale, ImGui.GetColorU32(Accent with { W = 0.9f }),
                        1.6f * scale);
                }

                Typography.DrawCentered(r.Center, "empty", theme.TextStrong with { W = 0.4f }, TextStyles.Caption2);
            }
        }

        // ---- Learnset panel ----
        var lsY = gridTop + 2f * (cardH + gap) + 8f * scale;
        Typography.Draw(new Vector2(content.Min.X + 14f * scale, lsY), "Learnset", theme.TextStrong,
            TextStyles.SubheadlineEmphasized);
        var tabBounds = new Rect(new Vector2(content.Min.X + 12f * scale, lsY + 18f * scale),
            new Vector2(content.Max.X - 12f * scale, lsY + 44f * scale));
        var picked = LgUi.Segmented(tabBounds, new[] { "Level-Up", "TM's" }, relearnTab, Accent, theme, scale,
            ref relearnTabIndicator);
        if (picked >= 0)
        {
            relearnTab = picked;
            relearnScroll = 0f;
        }

        var rows = BuildLearnRows(species)
            .Where(row => relearnTab == 0 ? row.TmId is null : row.TmId is not null).ToArray();
        var listArea = new Rect(new Vector2(content.Min.X + 8f * scale, lsY + 50f * scale),
            new Vector2(content.Max.X - 9f * scale, content.Max.Y - 40f * scale));
        DrawScrollList(listArea, 42f * scale, 4f * scale, rows.Length, ref relearnScroll, scale,
            (i, row) => DrawRelearnRow(rows[i], mon, row, theme, scale));

        if (rows.Length == 0)
        {
            Typography.DrawCentered(listArea.Center,
                relearnTab == 0 ? "No level-up moves." : "No TM moves for this species.",
                theme.TextStrong with { W = 0.55f }, TextStyles.Caption1);
        }

        // Bottom bar: a hint on the left and a Back button on the right (as in the reference).
        var backRect = CenteredAt(new Vector2(content.Max.X - 46f * scale, content.Max.Y - 22f * scale),
            new Vector2(74f * scale, 30f * scale));
        if (LgUi.Button(backRect, "Back", GamePalette.Cell, theme, true))
        {
            draggingMoveIndex = -1;
            draggingLearnMove = null;
            view = View.Detail;
            return;
        }

        var hint = draggingLearnMove is not null || draggingMoveIndex >= 0
            ? "Drop onto a move slot to set it."
            : "Drag a learnset move onto a slot to teach it.";
        Typography.DrawCentered(new Vector2((content.Min.X + backRect.Min.X) * 0.5f, content.Max.Y - 22f * scale),
            FitLabel(hint, backRect.Min.X - content.Min.X - 20f * scale, TextStyles.Caption1),
            theme.TextStrong with { W = 0.7f }, TextStyles.Caption1);

        // ---- Reorder drag (move cards) ----
        if (draggingMoveIndex >= 0 && draggingMoveIndex < mon.Moves.Count)
        {
            var src = Slot(draggingMoveIndex);
            var half = (src.Max - src.Min) * 0.5f;
            DrawMoveCard(drawList, new Rect(mouse - half, mouse + half), mon, draggingMoveIndex, theme, scale, false);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                for (var i = 0; i < mon.Moves.Count; i++)
                {
                    if (i != draggingMoveIndex && Slot(i).Contains(mouse))
                    {
                        mon.SwapMoves(draggingMoveIndex, i);
                        State.Save();
                        break;
                    }
                }

                draggingMoveIndex = -1;
            }
        }

        // ---- Learnset drag (teach onto a slot) ----
        if (draggingLearnMove is { } lm)
        {
            if (!learnDragMoved && Vector2.Distance(mouse, learnDragOrigin) > 6f * scale)
            {
                learnDragMoved = true;
            }

            DrawFloatingMove(drawList, CenteredAt(mouse, new Vector2(cardW, cardH)), lm, theme, scale);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (learnDragMoved)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        if (Slot(i).Contains(mouse))
                        {
                            TeachToSlot(mon, lm, i);
                            break;
                        }
                    }
                }

                draggingLearnMove = null;
            }
        }
    }

    // A learnset row: source tag (Lv / TM number), name, type chip, PP, and a state pill. Eligible,
    // not-yet-known moves become a drag source.
    private void DrawRelearnRow(LearnRow entry, MonsterInstance mon, Rect row, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var isTm = entry.TmId is not null;
        var known = mon.Knows(entry.Move);
        var canTeach = isTm ? State.OwnedTms.Contains(entry.TmId!) : entry.Level <= mon.Level;
        var draggable = canTeach && !known && draggingMoveIndex < 0 && draggingLearnMove is null;
        var rowHovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(row.Min, row.Max);
        LgUi.Card(drawList, row.Min, row.Max, 8f * scale, scale, rowHovered && draggable,
            sunken: !canTeach && !known);
        if (known)
        {
            drawList.AddRectFilled(row.Min, new Vector2(row.Min.X + 4f * scale, row.Max.Y),
                ImGui.GetColorU32(Accent with { W = 0.85f }), 3f * scale);
        }

        var tag = isTm ? Tms.Label(Tms.NumberOf(entry.TmId!)) : $"Lv {entry.Level}";
        Typography.DrawCentered(new Vector2(row.Min.X + 30f * scale, row.Center.Y), tag,
            isTm ? Accent with { W = 0.9f } : theme.TextStrong, TextStyles.Caption2);

        var moveX = row.Min.X + 60f * scale;
        Typography.Draw(new Vector2(moveX, row.Min.Y + 6f * scale),
            FitLabel(entry.Move.Name, row.Max.X - moveX - 118f * scale, TextStyles.SubheadlineEmphasized),
            theme.TextStrong, TextStyles.SubheadlineEmphasized);
        LgUi.Chip(drawList, new Vector2(moveX, row.Min.Y + 25f * scale), entry.Move.Element, scale,
            Elements.Name(entry.Move.Element));

        Typography.DrawCentered(new Vector2(row.Max.X - 66f * scale, row.Center.Y),
            $"{entry.Move.Pp}/{entry.Move.Pp}", theme.TextStrong with { W = 0.85f }, TextStyles.Caption1);

        var pill = CenteredAt(new Vector2(row.Max.X - 26f * scale, row.Center.Y), new Vector2(46f * scale,
            row.Height - 10f * scale));
        var (pillText, pillColor) = known ? ("Known", Accent)
            : !canTeach ? (isTm ? "TM?" : "Locked", theme.TextMuted)
            : ("Drag", theme.TextStrong with { W = 0.7f });
        Typography.DrawCentered(pill.Center, pillText, pillColor, TextStyles.Caption2);

        // Every row shows the move's description on hover; only teachable ones start a drag.
        if (rowHovered)
        {
            ShowTooltip(BuildProfileMoveTooltip(entry.Move, entry.Move.Pp));
            if (draggable)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    draggingLearnMove = entry.Move;
                    learnDragOrigin = ImGui.GetMousePos();
                    learnDragMoved = false;
                }
            }
        }
    }

    private void TeachToSlot(MonsterInstance mon, MoveDef move, int slot)
    {
        if (mon.Knows(move))
        {
            return;
        }

        if (slot < mon.Moves.Count)
        {
            mon.ReplaceMove(slot, move);
        }
        else if (mon.Moves.Count < 4)
        {
            mon.AddMove(move);
        }
        else
        {
            return;
        }

        State.Save();
    }

    // A lifted move card (a bare MoveDef, not one of the creature's slots) that follows the cursor.
    private void DrawFloatingMove(ImDrawListPtr drawList, Rect rect, MoveDef move, PhoneTheme theme, float scale)
    {
        var color = Elements.Color(move.Element);
        LgUi.Card(drawList, rect.Min, rect.Max, 9f * scale, scale, true);
        Squircle.Stroke(drawList, rect.Min, rect.Max, 9f * scale, ImGui.GetColorU32(color with { W = 0.9f }),
            1.6f * scale);
        drawList.AddRectFilled(rect.Min, new Vector2(rect.Min.X + 5f * scale, rect.Max.Y), ImGui.GetColorU32(color),
            4f * scale);
        Typography.Draw(new Vector2(rect.Min.X + 14f * scale, rect.Min.Y + 7f * scale), move.Name, theme.TextStrong,
            TextStyles.SubheadlineEmphasized);
        Typography.Draw(new Vector2(rect.Min.X + 14f * scale, rect.Max.Y - 16f * scale),
            $"{Elements.Name(move.Element)} {move.CategoryLabel}", theme.TextMuted, TextStyles.Caption2);
    }
}
