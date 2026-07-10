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
        if (detailEvolutionPulse > 0f)
        {
            detailEvolutionPulse -= ImGui.GetIO().DeltaTime;
            DrawDetailEvolutionPulse(drawList, hero, monster, scale);
        }

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

        // ---- Held item: equipped from the Bag and consumed/triggered by battle rules. ----
        var heldTop = abil.Max.Y + 8f * scale;
        var held = new Rect(new Vector2(left, heldTop), new Vector2(right, heldTop + 36f * scale));
        // Once the picker is on screen, its controls own the pointer. Do not let the underlying
        // card re-open the picker while a choice is being clicked over the modal.
        var heldHovered = LgUi.Interactive && heldItemPicker is null && ImGui.IsMouseHoveringRect(held.Min, held.Max);
        RosterUi.DarkCard(drawList, held, 9f * scale, scale, hovered: heldHovered, accent: RosterUi.Gold);
        Typography.Draw(new Vector2(held.Min.X + 11f * scale, held.Min.Y + 10f * scale), "HELD ITEM",
            RosterUi.Gold, TextStyles.Caption2);
        var heldName = string.IsNullOrEmpty(monster.HeldItem) ? "None" : Items.Find(monster.HeldItem)?.Name ?? "Unknown";
        Typography.Draw(new Vector2(held.Min.X + 76f * scale, held.Min.Y + 7f * scale),
            FitLabel(heldName, held.Width - 168f * scale, TextStyles.SubheadlineEmphasized), RosterUi.CardInk,
            TextStyles.SubheadlineEmphasized);
        var heldAction = CenteredAt(new Vector2(held.Max.X - 35f * scale, held.Center.Y),
            new Vector2(58f * scale, 22f * scale));
        RosterUi.Pill(drawList, heldAction.Center,
            new[] { ("CHANGE", RosterUi.CardInk) }, TextStyles.Caption2, scale);
        if (heldHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            heldItemPicker = monster;
            heldItemPickerAwaitingRelease = true;
            heldItemPickerTab = 0;
            heldItemPickerSort = 0;
            heldItemPickerSearch = string.Empty;
            heldItemPickerScroll = 0f;
        }
        if (heldHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ShowTooltip("Choose an item from your Bag for this creature to hold in battle.");
        }

        // ---- XP strip ----
        var xpTop = held.Max.Y + 8f * scale;
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

            if (heldItemPicker is not null)
            {
                DrawHeldItemPicker(content, theme, heldItemPicker, scale);
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

        if (heldItemPicker is not null)
        {
            DrawHeldItemPicker(content, theme, heldItemPicker, scale);
        }
    }

    // The held-item picker's pocket filter: everything the bag can equip, or berries / battle
    // held items on their own (mirroring the Bag's Berries vs. Held Items tabs).
    private static readonly CategoryTab[] HeldPickerTabs =
    {
        new("All", "tab_items", FontAwesomeIcon.ShoppingBag, RosterUi.Gold),
        new("Berries", "tab_berry", FontAwesomeIcon.Leaf, LgUi.ItemTint(ItemCategory.HeldItem)),
        new("Held Items", "tab_held", FontAwesomeIcon.Gem, LgUi.ItemTint(ItemCategory.HeldItem)),
    };

    private void DrawHeldItemPicker(Rect content, PhoneTheme theme, MonsterInstance monster, float scale)
    {
        // The tap that opens this overlay must fully finish before any choice can receive input.
        var wasInteractive = LgUi.Interactive;
        var suppressOpeningTap = heldItemPickerAwaitingRelease;
        if (heldItemPickerAwaitingRelease && !ImGui.IsMouseDown(ImGuiMouseButton.Left) &&
            !ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            heldItemPickerAwaitingRelease = false;
        }
        if (suppressOpeningTap)
        {
            LgUi.Interactive = false;
        }

        try
        {
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.66f)));

        var query = heldItemPickerSearch.Trim();
        var choices = Items.All.Where(item => item.Category == ItemCategory.HeldItem && State.Bag.Has(item.Id) &&
            (heldItemPickerTab == 0 || (heldItemPickerTab == 1) == Items.IsBerry(item.Id)) &&
            (query.Length == 0 || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        choices = heldItemPickerSort switch
        {
            1 => choices.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            2 => choices.OrderByDescending(item => State.Bag.Count(item.Id)).ThenBy(item => item.Name).ToList(),
            _ => choices, // Type = catalogue order, which is already grouped by pocket
        };

        // The card sizes itself to its contents up to the screen, then the list scrolls inside it —
        // a bag full of berries used to run the rows off the bottom edge.
        var rowH = 46f * scale;
        var rowGap = 6f * scale;
        var tabsH = 46f * scale;
        var ctrlH = 26f * scale;
        var clearH = 30f * scale;
        var chrome = 46f * scale + tabsH + 8f * scale + ctrlH + 8f * scale + clearH + 8f * scale + 10f * scale;
        var listWanted = Math.Max(1, choices.Count) * (rowH + rowGap) - rowGap;
        var h = MathF.Min(content.Height - 36f * scale, chrome + listWanted);
        var card = CenteredAt(content.Center, new Vector2(content.Width - 24f * scale, h));
        RosterUi.DarkCard(dl, card, 12f * scale, scale, accent: RosterUi.Gold);
        var left = card.Min.X + 10f * scale;
        var right = card.Max.X - 10f * scale;

        Typography.DrawCentered(new Vector2(card.Center.X, card.Min.Y + 15f * scale), "Choose held item",
            RosterUi.CardInk, TextStyles.SubheadlineEmphasized);
        Typography.DrawCentered(new Vector2(card.Center.X, card.Min.Y + 31f * scale),
            FitLabel("Equipped items activate automatically in battle.", card.Width - 60f * scale, TextStyles.Caption2),
            RosterUi.CardMuted, TextStyles.Caption2);
        var close = CenteredAt(new Vector2(card.Max.X - 20f * scale, card.Min.Y + 17f * scale),
            new Vector2(28f * scale, 20f * scale));
        if (RosterUi.ColorButton(close, "X", RosterUi.Red, scale, true))
        {
            heldItemPicker = null;
            return;
        }

        // Filter what the bag offers: everything, just berries, or just battle held items.
        var tabBounds = new Rect(new Vector2(left, card.Min.Y + 46f * scale),
            new Vector2(right, card.Min.Y + 46f * scale + tabsH - 6f * scale));
        var tabClicked = RosterUi.CategoryTabs(tabBounds, HeldPickerTabs, heldItemPickerTab, scale);
        if (tabClicked >= 0 && tabClicked != heldItemPickerTab)
        {
            heldItemPickerTab = tabClicked;
            heldItemPickerScroll = 0f;
        }

        // Sort capsule and search field share one line: the capsule takes exactly what its widest
        // label needs, the search bar drinks the rest.
        var ctrlY = tabBounds.Max.Y + 8f * scale;
        var sortLabels = new[] { "Type", "Name", "Count" };
        var sortW = MathF.Min((right - left) * 0.5f, RosterUi.SortButtonWidth(sortLabels, scale));
        var sortRect = new Rect(new Vector2(left, ctrlY), new Vector2(left + sortW, ctrlY + ctrlH));
        if (RosterUi.SortButton(sortRect, sortLabels[heldItemPickerSort], scale, true))
        {
            heldItemPickerSort = (heldItemPickerSort + 1) % sortLabels.Length;
            heldItemPickerScroll = 0f;
        }

        if (LgUi.Interactive && ImGui.IsMouseHoveringRect(sortRect.Min, sortRect.Max))
        {
            ShowTooltip($"Sorted by {sortLabels[heldItemPickerSort].ToLowerInvariant()}. Tap to cycle.");
        }

        var searchRect = new Rect(new Vector2(sortRect.Max.X + 6f * scale, ctrlY), new Vector2(right, ctrlY + ctrlH));
        var before = heldItemPickerSearch;
        RosterUi.SearchBar(searchRect, "##helditemsearch", "Search items", ref heldItemPickerSearch, scale);
        if (!string.Equals(before, heldItemPickerSearch, StringComparison.Ordinal))
        {
            heldItemPickerScroll = 0f;
        }

        // "No held item" clears the slot; it stays pinned above the list so it is always one tap away.
        var clearY = searchRect.Max.Y + 8f * scale;
        DrawHeldItemChoice(new Rect(new Vector2(left, clearY), new Vector2(right, clearY + clearH)), monster, scale);

        var listArea = new Rect(new Vector2(left, clearY + clearH + 8f * scale),
            new Vector2(right, card.Max.Y - 10f * scale));
        if (choices.Count == 0)
        {
            var message = query.Length > 0
                ? $"Nothing matches “{query}”."
                : heldItemPickerTab == 1
                    ? "No berries in your bag."
                    : "No held items in your bag.";
            Typography.DrawCentered(new Vector2(listArea.Center.X, listArea.Min.Y + 18f * scale),
                FitLabel(message, listArea.Width - 16f * scale, TextStyles.Caption1), RosterUi.CardMuted,
                TextStyles.Caption1);
        }
        else
        {
            DrawScrollList(listArea, rowH, rowGap, choices.Count, ref heldItemPickerScroll, scale,
                (i, rect) => DrawHeldItemRow(rect, monster, choices[i], scale));
        }
        }
        finally
        {
            LgUi.Interactive = wasInteractive;
        }
    }

    // The pinned "No held item" row: green while the creature is already empty-handed.
    private void DrawHeldItemChoice(Rect rect, MonsterInstance monster, float scale)
    {
        var selected = monster.HeldItem.Length == 0;
        if (RosterUi.ColorButton(rect, "No held item", selected ? RosterUi.Green : RosterUi.Blue, scale, true))
        {
            EquipHeldItem(monster, null);
        }
    }

    // A picker row, drawn like the Bag and Marketboard rows: icon tile, name, blurb, owned count.
    // The equipped item's card is accented green rather than by its category tint.
    private void DrawHeldItemRow(Rect rect, MonsterInstance monster, ItemDef item, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        var selected = string.Equals(monster.HeldItem, item.Id, StringComparison.Ordinal);
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        var accent = selected ? RosterUi.Green : LgUi.ItemTint(item.Category);
        RosterUi.DarkCard(dl, rect, 10f * scale, scale, hovered, accent: accent);

        var iconCenter = new Vector2(rect.Min.X + 28f * scale, rect.Center.Y);
        RosterUi.IconTile(dl, iconCenter, 34f * scale, scale);
        LgUi.ItemIcon(dl, iconCenter, 26f * scale, item);

        var textLeft = rect.Min.X + 52f * scale;
        var textWidth = rect.Width - 90f * scale;
        Typography.Draw(new Vector2(textLeft, rect.Min.Y + 6f * scale),
            FitLabel(item.Name, textWidth, TextStyles.SubheadlineEmphasized), RosterUi.CardInk,
            TextStyles.SubheadlineEmphasized);
        Typography.Draw(new Vector2(textLeft, rect.Min.Y + 26f * scale),
            FitLabel(item.Blurb, textWidth, TextStyles.Caption2), RosterUi.CardMuted, TextStyles.Caption2);
        Typography.DrawCentered(new Vector2(rect.Max.X - 22f * scale, rect.Center.Y),
            "x" + State.Bag.Count(item.Id), selected ? RosterUi.CountGreen : RosterUi.CountBlue, TextStyles.Headline);

        // The row has no space for the full effect text, so it hovers instead.
        if (!hovered)
        {
            return;
        }

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ShowTooltip(BuildItemTooltip(item, State.Bag.Count(item.Id)));
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            EquipHeldItem(monster, item);
        }
    }

    // Swap what the creature holds, returning anything it was already carrying to the bag.
    private void EquipHeldItem(MonsterInstance monster, ItemDef? item)
    {
        var nextId = item?.Id ?? string.Empty;
        if (!string.Equals(monster.HeldItem, nextId, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(monster.HeldItem))
            {
                State.Bag.Add(monster.HeldItem);
            }

            if (item is not null)
            {
                State.Bag.Consume(item.Id);
            }

            monster.HeldItem = nextId;
            State.Save();
        }

        heldItemPicker = null;
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

        if (Items.StoneFor(monster.Species.EvolveMethod) is { } stone)
        {
            var useStone = CenteredAt(new Vector2(card.Center.X, card.Max.Y - 34f * scale),
                new Vector2(card.Width - 12f * scale, 20f * scale));
            var canUse = State.Bag.Has(stone.Id);
            if (RosterUi.ColorButton(useStone, canUse ? "USE STONE" : stone.Name, RosterUi.Purple, scale, canUse))
            {
                if (State.Bag.Consume(stone.Id) && monster.TryEvolveWithStone(stone.Id))
                {
                    State.Save();
                    detailEvolutionPulse = 1.35f;
                }
            }

            if (ImGui.IsMouseHoveringRect(useStone.Min, useStone.Max))
            {
                ShowTooltip(canUse ? $"Use a {stone.Name} to evolve {monster.Name}." :
                    $"Buy a {stone.Name} at the Marketboard to evolve {monster.Name}.");
            }
        }

        if (ImGui.IsMouseHoveringRect(card.Min, card.Max))
        {
            ShowTooltip($"{monster.Species.Name} evolves into {evo.Name}" +
                (monster.Species.EvolveLevel > 0 ? $" at Lv {monster.Species.EvolveLevel}." : "."));
        }
    }

    private void DrawDetailEvolutionPulse(ImDrawListPtr drawList, Rect hero, MonsterInstance monster, float scale)
    {
        var t = Math.Clamp(1f - detailEvolutionPulse / 1.35f, 0f, 1f);
        var center = new Vector2(hero.Center.X, hero.Min.Y + 55f * scale);
        var fade = MathF.Sin(Math.Clamp(t * 1.25f, 0f, 1f) * MathF.PI);
        var tint = Elements.Color(monster.Element) with { W = fade };
        for (var i = 0; i < 3; i++)
        {
            var radius = (16f + i * 11f + t * 30f) * scale;
            drawList.AddCircle(center, radius, ImGui.GetColorU32(tint with { W = fade * (0.7f - i * 0.16f) }),
                32, 2f * scale);
        }

        for (var i = 0; i < 7; i++)
        {
            var angle = t * 8f + i * MathF.Tau / 7f;
            var point = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (24f + t * 32f) * scale;
            drawList.AddCircleFilled(point, 2.5f * scale, ImGui.GetColorU32(tint));
        }

        Typography.DrawCentered(new Vector2(hero.Center.X, hero.Min.Y + 25f * scale), "EVOLVED!",
            new Vector4(1f, 1f, 1f, fade), TextStyles.SubheadlineEmphasized);
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
