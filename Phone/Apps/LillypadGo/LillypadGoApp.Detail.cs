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
    // The creature profile screen, in the navy/cream "monster battler" chrome (Ideas/UI Update/
    // OnclickPokemon.png): Back/Moves/Release on the navy header, then a cream panel holding the
    // blue hero card + evolution preview, nickname editor, stats, ability, XP and the move grid.

    private void DrawDetail(Rect content, PhoneTheme theme)
    {
        if (detailMonster is not { } monster)
        {
            view = detailReturnView;
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(RosterUi.NavyBottom));
        var headerBottom = RosterUi.ScreenHeader(content, string.Empty, null, null, scale);

        // Header buttons: Back on the left, Moves + Release on the right.
        var btnY = content.Min.Y + 23f * scale;
        var back = CenteredAt(new Vector2(content.Min.X + 44f * scale, btnY), new Vector2(64f * scale, 26f * scale));
        // The header is too narrow for both the arrow sprite and a readable label; the button
        // itself remains the back affordance, so keep the full word visible.
        if (RosterUi.BlueButton(back, "BACK", scale, true))
        {
            view = detailReturnView;
            return;
        }

        var releaseRect = CenteredAt(new Vector2(content.Max.X - 50f * scale, btnY),
            new Vector2(80f * scale, 26f * scale));
        if (RosterUi.ColorButton(releaseRect, "RELEASE", RosterUi.Red, scale, true))
        {
            releaseConfirm = true;
        }

        if (ImGui.IsMouseHoveringRect(releaseRect.Min, releaseRect.Max))
        {
            ShowTooltip($"Release {monster.Name} for {LgUi.Money(ReleaseValue(monster))}.");
        }

        var learnset = CenteredAt(new Vector2(releaseRect.Min.X - 41f * scale, btnY),
            new Vector2(70f * scale, 26f * scale));
        if (RosterUi.ColorButton(learnset, "MOVES", RosterUi.Green, scale, true))
        {
            learnsetMonster = monster;
            teachPendingMove = null;
            relearnTab = 0;
            relearnScroll = 0f;
            draggingMoveIndex = -1;
            draggingLearnMove = null;
            view = View.MoveRelearn;
            return;
        }

        if (ImGui.IsMouseHoveringRect(learnset.Min, learnset.Max))
        {
            ShowTooltip($"View {monster.Species.Name}'s learnset and customise its moves.");
        }

        // ---- Cream panel ----
        var navTop = content.Max.Y - NavBarHeight * scale;
        var panel = new Rect(new Vector2(content.Min.X + 7f * scale, headerBottom + 6f * scale),
            new Vector2(content.Max.X - 7f * scale, navTop - 7f * scale));
        RosterUi.CreamPanel(drawList, panel, scale);
        var left = panel.Min.X + 9f * scale;
        var right = panel.Max.X - 9f * scale;

        // ---- Hero card (blue lead style) + evolution preview ----
        var heroTop = panel.Min.Y + 9f * scale;
        var hero = new Rect(new Vector2(left, heroTop),
            new Vector2(left + panel.Width * 0.56f, heroTop + 116f * scale));
        DrawDetailHeroCard(drawList, hero, monster, scale);
        DrawDetailEvolution(drawList, new Rect(new Vector2(hero.Max.X + 6f * scale, heroTop),
            new Vector2(right, hero.Max.Y)), monster, scale);

        // ---- Nickname editor ----
        var nickTop = hero.Max.Y + 8f * scale;
        var nick = new Rect(new Vector2(left, nickTop), new Vector2(right, nickTop + 46f * scale));
        RosterUi.ChunkyCard(drawList, nick.Min, nick.Max, 9f * scale, scale, RosterUi.TileCream,
            GamePalette.Darken(RosterUi.TileCream, 0.05f), RosterUi.TanEdge);
        Typography.Draw(new Vector2(nick.Min.X + 10f * scale, nick.Min.Y + 4f * scale), "Nickname",
            RosterUi.InkTan, TextStyles.Caption2);
        var nickRect = new Rect(new Vector2(nick.Min.X + 9f * scale, nick.Min.Y + 17f * scale),
            new Vector2(nick.Max.X - 74f * scale, nick.Max.Y - 5f * scale));
        var submittedName = LgUi.Input(nickRect, "##lillypadgo-nickname", ref detailNameDraft, 21, theme, scale);
        var trimmedName = detailNameDraft.Trim();
        var nameChanged = trimmedName != monster.Nickname;
        var saveName = new Rect(new Vector2(nick.Max.X - 68f * scale, nick.Min.Y + 17f * scale),
            new Vector2(nick.Max.X - 8f * scale, nick.Max.Y - 5f * scale));
        var clickedSaveName = RosterUi.BlueButton(saveName, "SAVE", scale, nameChanged);
        if (nameChanged && (clickedSaveName || submittedName))
        {
            monster.Rename(detailNameDraft);
            detailNameDraft = monster.Nickname;
            State.Save();
        }

        // ---- Stats card: battle stat, IV (potential) and EV (trained) per column ----
        var statsTop = nick.Max.Y + 8f * scale;
        var statsMin = new Vector2(left, statsTop);
        var statsMax = new Vector2(right, statsTop + 84f * scale);
        RosterUi.ChunkyCard(drawList, statsMin, statsMax, 9f * scale, scale,
            new Vector4(0.99f, 0.98f, 0.95f, 1f), new Vector4(0.94f, 0.92f, 0.87f, 1f), RosterUi.NavyEdge);
        if (ImGui.IsMouseHoveringRect(statsMin, statsMax))
        {
            ShowTooltip(BuildRecordTooltip(monster));
        }

        var ivBlue = new Vector4(0.20f, 0.46f, 0.78f, 1f);
        var evGreen = new Vector4(0.16f, 0.56f, 0.31f, 1f);
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
            if (i > 0)
            {
                drawList.AddLine(new Vector2(statsMin.X + i * colWidth, statsMin.Y + 10f * scale),
                    new Vector2(statsMin.X + i * colWidth, statsMax.Y - 10f * scale),
                    ImGui.GetColorU32(RosterUi.TanEdge with { W = 0.55f }), 1f * scale);
            }

            // Drop to a smaller style when a value (e.g. a big "234/234" HP) would overrun its column.
            var valueStyle = Typography.Measure(stats[i].Value, TextStyles.Headline).X > colWidth - 3f * scale
                ? TextStyles.Caption1
                : TextStyles.Headline;
            Typography.DrawCentered(new Vector2(x, statsMin.Y + 15f * scale), stats[i].Value, RosterUi.InkNavy,
                valueStyle);
            Typography.DrawCentered(new Vector2(x, statsMin.Y + 32f * scale), stats[i].Label,
                RosterUi.InkNavy with { W = 0.72f }, TextStyles.Caption2);
            Typography.DrawCentered(new Vector2(x, statsMin.Y + 53f * scale), $"IV {monster.Ivs[stats[i].Slot]}",
                ivBlue, TextStyles.Caption2);
            Typography.DrawCentered(new Vector2(x, statsMin.Y + 68f * scale), $"EV {monster.Evs[stats[i].Slot]}",
                evGreen, TextStyles.Caption2);
        }

        // ---- Ability card: name on the header row, description wrapped below ----
        var abilTop = statsMax.Y + 8f * scale;
        var abil = new Rect(new Vector2(left, abilTop), new Vector2(right, abilTop + 56f * scale));
        RosterUi.DarkCard(drawList, abil, 9f * scale, scale);
        Typography.Draw(new Vector2(abil.Min.X + 11f * scale, abil.Min.Y + 8f * scale), "ABILITY",
            RosterUi.CountGreen, TextStyles.Caption2);
        Typography.Draw(new Vector2(abil.Min.X + 64f * scale, abil.Min.Y + 6f * scale),
            FitLabel(monster.Ability, abil.Width - 76f * scale, TextStyles.SubheadlineEmphasized),
            RosterUi.CardInk, TextStyles.SubheadlineEmphasized);
        var abilLines = WrapText(AbilityInfo.Describe(monster.Ability), abil.Width - 24f * scale,
            TextStyles.Caption2);
        for (var i = 0; i < abilLines.Count && i < 2; i++)
        {
            Typography.Draw(new Vector2(abil.Min.X + 12f * scale, abil.Min.Y + (26f + i * 13f) * scale),
                abilLines[i], RosterUi.CardMuted, TextStyles.Caption2);
        }

        // ---- XP strip ----
        var xpTop = abil.Max.Y + 8f * scale;
        var xp = new Rect(new Vector2(left, xpTop), new Vector2(right, xpTop + 28f * scale));
        RosterUi.ChunkyCard(drawList, xp.Min, xp.Max, 9f * scale, scale, RosterUi.TileCream,
            GamePalette.Darken(RosterUi.TileCream, 0.05f), RosterUi.TanEdge);
        var xpLabel = monster.Level >= 100 ? "Maximum level" : $"XP {monster.Xp}/{monster.XpToNext}";
        Typography.Draw(new Vector2(xp.Min.X + 10f * scale, xp.Min.Y + 3f * scale), xpLabel,
            RosterUi.InkTan, TextStyles.Caption2);
        LgUi.Meter(drawList, new Vector2(xp.Min.X + 10f * scale, xp.Max.Y - 9f * scale),
            new Vector2(xp.Max.X - 10f * scale, xp.Max.Y - 4f * scale), monster.XpFraction, RosterUi.Blue);

        // ---- Moves grid ----
        var movesLabelY = xp.Max.Y + 6f * scale;
        Typography.Draw(new Vector2(left + 2f * scale, movesLabelY), "Moves", RosterUi.InkNavy,
            TextStyles.SubheadlineEmphasized);
        Typography.Draw(new Vector2(left + 54f * scale, movesLabelY + 2f * scale),
            "— drag to reorder", RosterUi.InkTan with { W = 0.75f }, TextStyles.Caption2);

        var movesTop = movesLabelY + 18f * scale;
        var movesBottom = panel.Max.Y - 8f * scale;
        if (movesBottom - movesTop < 24f * scale)
        {
            // No room for the moves grid on a very short / high-DPI screen; better to omit it than
            // to draw inverted, overlapping cards.
            draggingMoveIndex = -1;
            if (!releaseConfirm)
            {
                DrawNavigation(content, theme, scale);
            }
            else
            {
                DrawReleaseConfirm(content, theme, monster, scale);
            }

            return;
        }

        var columns = 2;
        var rows = Math.Max(1, (monster.Moves.Count + columns - 1) / columns);
        var gap = 5f * scale;
        var cardWidth = (right - left - gap) / columns;
        var rowH = MathF.Max(22f * scale, MathF.Min(58f * scale, (movesBottom - movesTop - gap * (rows - 1)) / rows));

        // Compute each move-card rect so drag-and-drop can hit-test them.
        var rects = new Rect[monster.Moves.Count];
        for (var i = 0; i < monster.Moves.Count; i++)
        {
            var min = new Vector2(left + (i % columns) * (cardWidth + gap),
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
        else
        {
            DrawNavigation(content, theme, scale);
        }
    }

    // The blue "lead card" hero: name + gender up top, the creature over a watermark, type and
    // level pills, and its HP bar along the bottom.
    private void DrawDetailHeroCard(ImDrawListPtr drawList, Rect r, MonsterInstance monster, float scale)
    {
        var radius = 9f * scale;
        drawList.AddRectFilled(r.Min + new Vector2(0f, 2.5f * scale), r.Max + new Vector2(0f, 2.5f * scale),
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.16f)), radius);
        Squircle.Fill(drawList, r.Min, r.Max, radius, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)));
        Squircle.Stroke(drawList, r.Min, r.Max, radius, ImGui.GetColorU32(RosterUi.Blue), 2.5f * scale);
        var inset = 3f * scale;
        Squircle.FillVerticalGradient(drawList, r.Min + new Vector2(inset, inset), r.Max - new Vector2(inset, inset),
            radius - inset, ImGui.GetColorU32(RosterUi.BlueCardTop), ImGui.GetColorU32(RosterUi.BlueCardBottom));
        RosterUi.Watermark(drawList, new Vector2(r.Max.X - 20f * scale, r.Min.Y + 22f * scale), 30f * scale,
            new Vector4(1f, 1f, 1f, 0.35f));

        var hasGender = monster.Gender != Gender.Genderless;
        var name = FitLabel(monster.Name, r.Width - (hasGender ? 40f : 20f) * scale, TextStyles.Headline);
        var nameW = Typography.Measure(name, TextStyles.Headline).X;
        var nameCenter = new Vector2(r.Center.X - (hasGender ? 7f * scale : 0f), r.Min.Y + 13f * scale);
        Typography.DrawCentered(nameCenter, name, RosterUi.InkNavy, TextStyles.Headline);
        if (hasGender)
        {
            RosterUi.Sprite(drawList, monster.Gender == Gender.Male ? "gender_male" : "gender_female",
                new Vector2(nameCenter.X + nameW * 0.5f + 9f * scale, nameCenter.Y), 12f * scale);
        }

        var artCenter = new Vector2(r.Center.X, r.Min.Y + 55f * scale);
        MonsterArt.Draw(drawList, artCenter, 30f * scale, monster.Species, 1f, MonsterPose.Idle(time));

        // Type + level pills.
        var pillY = r.Max.Y - 24f * scale;
        var typeName = Elements.Name(monster.Element).ToUpperInvariant();
        var typeColor = Elements.Color(monster.Element);
        var typeW = Typography.Measure(typeName, TextStyles.Caption2).X + 18f * scale;
        var lvText = $"Lv. {monster.Level}";
        var lvW = Typography.Measure(lvText, TextStyles.Caption2).X + 18f * scale;
        var pillsW = typeW + 6f * scale + lvW;
        var px = r.Center.X - pillsW * 0.5f;
        var typePill = new Rect(new Vector2(px, pillY - 8f * scale), new Vector2(px + typeW, pillY + 8f * scale));
        Squircle.FillVerticalGradient(drawList, typePill.Min, typePill.Max, 8f * scale,
            ImGui.GetColorU32(GamePalette.Lighten(typeColor, 0.08f)),
            ImGui.GetColorU32(GamePalette.Darken(typeColor, 0.14f)));
        Squircle.Stroke(drawList, typePill.Min, typePill.Max, 8f * scale,
            ImGui.GetColorU32(GamePalette.Darken(typeColor, 0.35f)), 1.4f * scale);
        Typography.DrawCentered(typePill.Center, typeName, new Vector4(1f, 1f, 1f, 1f), TextStyles.Caption2);
        var lvPill = new Rect(new Vector2(typePill.Max.X + 6f * scale, pillY - 8f * scale),
            new Vector2(typePill.Max.X + 6f * scale + lvW, pillY + 8f * scale));
        Squircle.FillVerticalGradient(drawList, lvPill.Min, lvPill.Max, 8f * scale,
            ImGui.GetColorU32(GamePalette.Lighten(RosterUi.Blue, 0.06f)),
            ImGui.GetColorU32(GamePalette.Darken(RosterUi.Blue, 0.14f)));
        Squircle.Stroke(drawList, lvPill.Min, lvPill.Max, 8f * scale, ImGui.GetColorU32(RosterUi.NavyEdge),
            1.4f * scale);
        Typography.DrawCentered(lvPill.Center, lvText, new Vector4(1f, 1f, 1f, 1f), TextStyles.Caption2);

        if (monster.SecondaryElement is { } secondary)
        {
            LgUi.Chip(drawList, new Vector2(r.Min.X + 7f * scale, r.Min.Y + 7f * scale), secondary, scale);
        }

        LgUi.HpBar(drawList, new Vector2(r.Min.X + 9f * scale, r.Max.Y - 12f * scale),
            new Vector2(r.Min.X + r.Width * 0.4f, r.Max.Y - 6f * scale), monster.HpFraction);
    }

    // The evolution preview to the hero card's right: an arrow into a small tan card with the
    // evolved form and its trigger pill, or a "final form" watermark when there is none.
    private void DrawDetailEvolution(ImDrawListPtr drawList, Rect area, MonsterInstance monster, float scale)
    {
        if (Dex.EvolutionOf(monster.Species) is not { } evo)
        {
            RosterUi.Watermark(drawList, new Vector2(area.Center.X + 8f * scale, area.Center.Y - 10f * scale),
                34f * scale, RosterUi.TanEdge with { W = 0.5f });
            Typography.DrawCentered(new Vector2(area.Center.X + 8f * scale, area.Center.Y + 18f * scale),
                "FINAL FORM", RosterUi.InkTan with { W = 0.6f }, TextStyles.Caption2);
            return;
        }

        var arrowX = area.Min.X + 8f * scale;
        var arrowY = area.Center.Y - 6f * scale;
        var arrow = RosterUi.InkTan with { W = 0.55f };
        drawList.AddTriangleFilled(new Vector2(arrowX, arrowY - 7f * scale),
            new Vector2(arrowX + 9f * scale, arrowY), new Vector2(arrowX, arrowY + 7f * scale),
            ImGui.GetColorU32(arrow));

        var card = new Rect(new Vector2(arrowX + 14f * scale, area.Min.Y + 8f * scale),
            new Vector2(area.Max.X, area.Max.Y - 8f * scale));
        RosterUi.ChunkyCard(drawList, card.Min, card.Max, 8f * scale, scale, RosterUi.TanTop, RosterUi.TanBottom,
            RosterUi.TanEdge);
        MonsterArt.Draw(drawList, new Vector2(card.Center.X, card.Min.Y + card.Height * 0.4f),
            MathF.Min(22f * scale, card.Height * 0.3f), evo, 1f, MonsterPose.Idle(time + 1.3f));
        var trigger = monster.Species.EvolveLevel > 0
            ? $"Lv. {monster.Species.EvolveLevel}"
            : monster.Species.EvolveMethod ?? evo.Name;
        RosterUi.Pill(drawList, new Vector2(card.Center.X, card.Max.Y - 14f * scale),
            new[] { (FitLabel(trigger, card.Width - 26f * scale, TextStyles.Caption2), new Vector4(1f, 1f, 1f, 1f)) },
            TextStyles.Caption2, scale);

        if (ImGui.IsMouseHoveringRect(card.Min, card.Max))
        {
            ShowTooltip($"{monster.Species.Name} evolves into {evo.Name}" +
                (monster.Species.EvolveLevel > 0 ? $" at Lv {monster.Species.EvolveLevel}." : "."));
        }
    }

    private void DrawMoveCard(ImDrawListPtr drawList, Rect rect, MonsterInstance monster, int index, PhoneTheme theme,
        float scale, bool highlight)
    {
        var move = monster.Moves[index];
        var color = Elements.Color(move.Element);
        RosterUi.DarkCard(drawList, rect, 9f * scale, scale, highlight, accent: color);
        if (highlight)
        {
            Squircle.Stroke(drawList, rect.Min, rect.Max, 9f * scale, ImGui.GetColorU32(RosterUi.GreenBright),
                2f * scale);
        }

        // The element token is a luxury: on the relearner's narrow 2-up slots it would eat the room
        // the move name needs, so it only appears once the card is wide enough to spare 36px.
        var roomy = rect.Width >= 118f * scale;
        var textX = rect.Min.X + (roomy ? 36f : 12f) * scale;
        if (roomy)
        {
            // Element token: a small coloured coin standing in for the move's type icon.
            var token = new Vector2(rect.Min.X + 21f * scale, rect.Center.Y);
            drawList.AddCircleFilled(token, 9f * scale, ImGui.GetColorU32(GamePalette.Darken(color, 0.32f)));
            drawList.AddCircle(token, 9f * scale, ImGui.GetColorU32(GamePalette.Lighten(color, 0.28f)), 20,
                1.4f * scale);
            drawList.AddCircleFilled(token, 3.5f * scale, ImGui.GetColorU32(GamePalette.Lighten(color, 0.42f)));
        }

        var textWidth = rect.Max.X - 8f * scale - textX;
        var (name, nameStyle) = FitName(move.Name, textWidth, TextStyles.SubheadlineEmphasized, TextStyles.Caption2);
        Typography.Draw(new Vector2(textX, rect.Min.Y + 6f * scale), name, RosterUi.CardInk, nameStyle);

        // Drop the category word, then the type word, before letting the meta line be ellipsized.
        var pp = $"{monster.Pp[index]}/{move.Pp} PP";
        var meta = $"{Elements.Name(move.Element)} {move.CategoryLabel}  {pp}";
        if (Typography.Measure(meta, TextStyles.Caption2).X > textWidth)
        {
            meta = $"{Elements.Name(move.Element)}  {pp}";
            if (Typography.Measure(meta, TextStyles.Caption2).X > textWidth)
            {
                meta = pp;
            }
        }

        Typography.Draw(new Vector2(textX, rect.Max.Y - 16f * scale),
            FitLabel(meta, textWidth, TextStyles.Caption2), RosterUi.CardMuted, TextStyles.Caption2);
    }

    private static void DrawMoveSlotPlaceholder(ImDrawListPtr drawList, Rect rect, float scale)
    {
        RosterUi.DarkCard(drawList, rect, 9f * scale, scale, sunken: true);
        Squircle.Stroke(drawList, rect.Min + new Vector2(3f, 3f) * scale, rect.Max - new Vector2(3f, 3f) * scale,
            7f * scale, ImGui.GetColorU32(RosterUi.CardEdge with { W = 0.45f }), 1f * scale);
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
        RosterUi.ChunkyCard(drawList, min, max, 14f * scale, scale, RosterUi.Cream, RosterUi.CreamShade,
            GamePalette.Darken(RosterUi.Red, 0.15f));
        var panel = new Rect(min, max);

        Typography.DrawCentered(new Vector2(panel.Center.X, min.Y + 26f * scale), $"Release {monster.Name}?",
            RosterUi.InkNavy, TextStyles.Headline);
        var reward = ReleaseValue(monster);
        foreach (var (line, i) in new[]
                 {
                     $"{monster.Name} will leave for good.", $"You'll receive {LgUi.Money(reward)}.",
                 }.Select((t, i) => (t, i)))
        {
            Typography.DrawCentered(new Vector2(panel.Center.X, min.Y + (52f + i * 20f) * scale),
                FitLabel(line, panel.Width - 24f * scale, TextStyles.Caption1),
                RosterUi.InkTan, TextStyles.Caption1);
        }

        var yes = CenteredAt(new Vector2(panel.Center.X - panel.Width * 0.24f, max.Y - 30f * scale),
            new Vector2(panel.Width * 0.4f, 34f * scale));
        var isLast = State.Party.Count + State.Box.Count <= 1;
        if (RosterUi.ColorButton(yes, "RELEASE", RosterUi.Red, scale, !isLast))
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
        if (RosterUi.ColorButton(no, "KEEP", RosterUi.Blue, scale, true))
        {
            releaseConfirm = false;
        }
    }

}
