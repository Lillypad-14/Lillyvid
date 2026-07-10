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
    // Navy/cream chrome per Ideas/UI Update/MoveRelearn.png: the creature portrait and its four
    // move slots up top, a Level-Up / TM learnset below. Drag a move from the learnset onto a slot
    // to teach it (any level-up move at or below the creature's level, or any move you own the TM
    // for); drag the move cards to reorder them. Back sits top-left, matching the detail screen.

    private void DrawMoveRelearn(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();

        if (learnsetMonster is not { } mon)
        {
            view = View.Detail;
            return;
        }

        var species = mon.Species;
        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(RosterUi.NavyBottom));
        var gender = mon.GenderSymbol.Length > 0 ? " " + mon.GenderSymbol : string.Empty;
        var headerBottom = RosterUi.ScreenHeader(content, "MOVE RELEARNER", null, new[]
        {
            (mon.Name + gender, RosterUi.CountBlue),
            ("|", RosterUi.NavyLine),
            ($"Lv. {mon.Level}", new Vector4(1f, 1f, 1f, 1f)),
        }, scale);

        // Back, top-left on the navy header (matches the detail screen).
        var back = CenteredAt(new Vector2(content.Min.X + 44f * scale, content.Min.Y + 23f * scale),
            new Vector2(64f * scale, 26f * scale));
        // Keep the compact header control readable; the button placement already communicates back.
        if (RosterUi.BlueButton(back, "BACK", scale, true))
        {
            draggingMoveIndex = -1;
            draggingLearnMove = null;
            view = View.Detail;
            return;
        }

        // ---- Cream panel ----
        var navTop = content.Max.Y - NavBarHeight * scale;
        var panel = new Rect(new Vector2(content.Min.X + 7f * scale, headerBottom + 6f * scale),
            new Vector2(content.Max.X - 7f * scale, navTop - 7f * scale));
        RosterUi.CreamPanel(drawList, panel, scale);
        var left = panel.Min.X + 9f * scale;
        var right = panel.Max.X - 9f * scale;

        var mouse = ImGui.GetMousePos();
        var top = panel.Min.Y + 9f * scale;

        // ---- Portrait card + move slot grid (2x2) ----
        // The portrait takes a fixed slice of the panel rather than a fixed pixel width, so the two
        // move slots beside it stay wide enough for their names on the narrower phone sizes.
        var portraitW = MathF.Min(88f * scale, (right - left) * 0.26f);
        var portrait = new Rect(new Vector2(left, top), new Vector2(left + portraitW, top + 102f * scale));
        drawList.AddRectFilled(portrait.Min + new Vector2(0f, 2.5f * scale), portrait.Max + new Vector2(0f, 2.5f * scale),
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.16f)), 9f * scale);
        Squircle.Fill(drawList, portrait.Min, portrait.Max, 9f * scale, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)));
        Squircle.Stroke(drawList, portrait.Min, portrait.Max, 9f * scale, ImGui.GetColorU32(RosterUi.Blue), 2.5f * scale);
        var inset = 3f * scale;
        Squircle.FillVerticalGradient(drawList, portrait.Min + new Vector2(inset, inset),
            portrait.Max - new Vector2(inset, inset), 9f * scale - inset,
            ImGui.GetColorU32(RosterUi.BlueCardTop), ImGui.GetColorU32(RosterUi.BlueCardBottom));
        RosterUi.Watermark(drawList, new Vector2(portrait.Max.X - 16f * scale, portrait.Min.Y + 16f * scale),
            24f * scale, new Vector4(1f, 1f, 1f, 0.35f));
        MonsterArt.Draw(drawList, portrait.Center, MathF.Min(26f * scale, portrait.Width * 0.32f), species, 1f,
            MonsterPose.Idle(time));

        var gridLeft = portrait.Max.X + 8f * scale;
        LgUi.TypeChips(drawList, new Vector2(gridLeft + 2f * scale, top + 1f * scale), species.Element,
            species.SecondaryElement, scale);
        Typography.Draw(new Vector2(gridLeft + 2f * scale, top + 18f * scale),
            FitLabel("Moves — drag a learned move onto a slot.", right - gridLeft - 4f * scale, TextStyles.Caption2),
            RosterUi.InkTan, TextStyles.Caption2);

        var gridTop = top + 32f * scale;
        var gap = 6f * scale;
        var cardW = (right - gridLeft - gap) / 2f;
        var cardH = 36f * scale;
        Rect Slot(int i)
        {
            var min = new Vector2(gridLeft + i % 2 * (cardW + gap), gridTop + i / 2 * (cardH + gap));
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
                    Squircle.Stroke(drawList, r.Min, r.Max, 9f * scale, ImGui.GetColorU32(RosterUi.GreenBright),
                        2f * scale);
                }

                Typography.DrawCentered(r.Center, "EMPTY", RosterUi.CardMuted with { W = 0.5f }, TextStyles.Caption2);
            }
        }

        // ---- Learnset panel ----
        var lsY = MathF.Max(portrait.Max.Y, gridTop + 2f * cardH + gap) + 8f * scale;
        Typography.Draw(new Vector2(left + 2f * scale, lsY), "Learnset", RosterUi.InkNavy,
            TextStyles.SubheadlineEmphasized);
        var tabBounds = new Rect(new Vector2(left, lsY + 18f * scale), new Vector2(right, lsY + 44f * scale));
        var picked = RosterUi.FolderTabs(tabBounds, new[] { "LEVEL-UP", "TM'S" }, relearnTab, scale);
        if (picked >= 0)
        {
            relearnTab = picked;
            relearnScroll = 0f;
        }

        var rows = BuildLearnRows(species)
            .Where(row => relearnTab == 0 ? row.TmId is null : row.TmId is not null).ToArray();
        var listArea = new Rect(new Vector2(left, lsY + 50f * scale),
            new Vector2(right + 1f * scale, panel.Max.Y - 38f * scale));
        DrawScrollList(listArea, 42f * scale, 4f * scale, rows.Length, ref relearnScroll, scale,
            (i, row) => DrawRelearnRow(rows[i], mon, row, theme, scale));

        if (rows.Length == 0)
        {
            Typography.DrawCentered(listArea.Center,
                relearnTab == 0 ? "No level-up moves." : "No TM moves for this species.",
                RosterUi.InkTan, TextStyles.Caption1);
        }

        // Bottom bar: a hint on the left and a Back button on the right (as in the reference).
        var barY = panel.Max.Y - 19f * scale;
        var backRect = CenteredAt(new Vector2(right - 37f * scale, barY), new Vector2(74f * scale, 26f * scale));
        if (RosterUi.BlueButton(backRect, "BACK", scale, true))
        {
            draggingMoveIndex = -1;
            draggingLearnMove = null;
            view = View.Detail;
            return;
        }

        var hint = draggingLearnMove is not null || draggingMoveIndex >= 0
            ? "Drop onto a move slot to set it."
            : "Drag a learned move onto a slot to teach it.";
        Typography.DrawCentered(new Vector2((left + backRect.Min.X) * 0.5f, barY),
            FitLabel(hint, backRect.Min.X - left - 16f * scale, TextStyles.Caption1),
            RosterUi.InkTan, TextStyles.Caption1);

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

        DrawNavigation(content, theme, scale);
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
        var elementColor = Elements.Color(entry.Move.Element);
        RosterUi.DarkCard(drawList, row, 8f * scale, scale, rowHovered && draggable,
            sunken: !canTeach && !known, accent: known || canTeach ? elementColor : null);

        var tag = isTm ? Tms.Label(Tms.NumberOf(entry.TmId!)) : $"Lv. {entry.Level}";
        Typography.DrawCentered(new Vector2(row.Min.X + 30f * scale, row.Center.Y), tag,
            isTm ? RosterUi.CountGreen : RosterUi.CardInk, TextStyles.Caption2);

        var moveX = row.Min.X + 60f * scale;
        Typography.Draw(new Vector2(moveX, row.Min.Y + 5f * scale),
            FitLabel(entry.Move.Name, row.Max.X - moveX - 118f * scale, TextStyles.SubheadlineEmphasized),
            canTeach || known ? RosterUi.CardInk : RosterUi.CardMuted, TextStyles.SubheadlineEmphasized);
        LgUi.Chip(drawList, new Vector2(moveX, row.Min.Y + 23f * scale), entry.Move.Element, scale,
            Elements.Name(entry.Move.Element).ToUpperInvariant());

        Typography.DrawCentered(new Vector2(row.Max.X - 66f * scale, row.Center.Y),
            $"{entry.Move.Pp}/{entry.Move.Pp}", RosterUi.CardMuted, TextStyles.Caption1);

        var pill = CenteredAt(new Vector2(row.Max.X - 26f * scale, row.Center.Y), new Vector2(46f * scale,
            row.Height - 10f * scale));
        var (pillText, pillColor) = known ? ("Known", RosterUi.CountGreen)
            : !canTeach ? (isTm ? "TM?" : "Locked", RosterUi.CardMuted with { W = 0.6f })
            : ("Drag", RosterUi.CardInk with { W = 0.8f });
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
        RosterUi.DarkCard(drawList, rect, 9f * scale, scale, true, accent: color);
        Squircle.Stroke(drawList, rect.Min, rect.Max, 9f * scale, ImGui.GetColorU32(RosterUi.GreenBright),
            2f * scale);
        var textX = rect.Min.X + 14f * scale;
        Typography.Draw(new Vector2(textX, rect.Min.Y + 4f * scale),
            FitLabel(move.Name, rect.Max.X - 8f * scale - textX, TextStyles.SubheadlineEmphasized),
            RosterUi.CardInk, TextStyles.SubheadlineEmphasized);
        Typography.Draw(new Vector2(textX, rect.Max.Y - 15f * scale),
            $"{Elements.Name(move.Element)} {move.CategoryLabel}", RosterUi.CardMuted, TextStyles.Caption2);
    }
}
