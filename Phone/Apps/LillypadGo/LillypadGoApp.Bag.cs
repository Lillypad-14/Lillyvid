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
    // Shared transient status line for the Bag/Market screens (e.g. "Bought 1 Potion.").
    private string bagStatus = string.Empty;
    private float bagScroll;
    private int bagSortMode;  // 0 = Type, 1 = Name, 2 = Count
    private ItemTab bagTab = ItemTab.Items;

    // The item awaiting a target choice: while set, the bag shows a "use on which creature?" picker.
    private ItemDef? bagUseItem;

    // The Bag.png category strip, shared by the Bag and the Marketboard. Held Items replaces the
    // mockup's Key Items tab; tab_held.png is that same painted key, recut and hue-rotated to
    // held-item violet (tools/cut_held_tab.py), so it sits in the strip at the mockup's fidelity.
    private static readonly CategoryTab[] ItemFilterTabs =
    {
        new("Items", "tab_items", FontAwesomeIcon.ShoppingBag, RosterUi.Gold),
        new("Medicine", "tab_medicine", FontAwesomeIcon.PrescriptionBottle, LgUi.ItemTint(ItemCategory.Potion)),
        new("Poké Balls", "tab_ball", FontAwesomeIcon.Bullseye, LgUi.ItemTint(ItemCategory.Ball)),
        new("Berries", "tab_berry", FontAwesomeIcon.Leaf, LgUi.ItemTint(ItemCategory.HeldItem)),
        new("Held Items", "tab_held", FontAwesomeIcon.Gem, LgUi.ItemTint(ItemCategory.HeldItem)),
        new("TMs", "tab_tm", FontAwesomeIcon.CompactDisc, RosterUi.CountBlue),
    };

    // Unscaled height of the sort-and-filter block: the Sort capsule's line, a gap, then the tabs.
    private const float ItemFilterRowHeight = 86f;

    // The mockup's sort-and-filter row, stacked. The mockup fits the Sort capsule beside all six
    // category tabs, but it is 941px wide; the phone canvas goes down to 280px (PhoneSizeCatalog),
    // which leaves each tab ~37px — too narrow for both the capsule's label and the tab labels. So
    // the capsule takes its own line and the tabs get the full width.
    // Returns true when the tab or sort mode changed (callers reset scroll).
    private bool DrawItemFilterRow(Rect row, ref ItemTab tab, ref int sortMode, string[] sortLabels, float scale)
    {
        var changed = false;
        var sortW = MathF.Min(row.Width, RosterUi.SortButtonWidth(sortLabels, scale));
        var sortRect = new Rect(row.Min, new Vector2(row.Min.X + sortW, row.Min.Y + 26f * scale));
        if (RosterUi.SortButton(sortRect, sortLabels[sortMode], scale, true))
        {
            sortMode = (sortMode + 1) % sortLabels.Length;
            changed = true;
        }

        if (LgUi.Interactive && ImGui.IsMouseHoveringRect(sortRect.Min, sortRect.Max))
        {
            ShowTooltip($"Sorted by {sortLabels[sortMode].ToLowerInvariant()}. Tap to cycle.");
        }

        var tabs = new Rect(new Vector2(row.Min.X, sortRect.Max.Y + 6f * scale), row.Max);
        var clicked = RosterUi.CategoryTabs(tabs, ItemFilterTabs, (int)tab, scale);
        if (clicked >= 0 && clicked != (int)tab)
        {
            tab = (ItemTab)clicked;
            changed = true;
        }

        return changed;
    }

    private void DrawBag(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();

        // Freeze the bag's own controls while the target picker is open so a tap only reaches the picker.
        var pickerOpen = bagUseItem is not null;
        var prevInteractive = LgUi.Interactive;
        if (pickerOpen)
        {
            LgUi.Interactive = false;
        }

        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(RosterUi.NavyBottom));
        var headerBottom = RosterUi.ScreenHeader(content, "BAG", "nav_bag", null, scale);
        DrawMoneyPill(content, scale);

        var navTop = content.Max.Y - NavBarHeight * scale;
        var panel = new Rect(new Vector2(content.Min.X + 7f * scale, headerBottom + 6f * scale),
            new Vector2(content.Max.X - 7f * scale, navTop - 7f * scale));
        RosterUi.CreamPanel(drawList, panel, scale);

        var owned = State.Bag.Contents().Where(e => Items.InTab(e.Def, bagTab)).ToList();
        owned = bagSortMode switch
        {
            1 => owned.OrderBy(e => e.Def.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            2 => owned.OrderByDescending(e => e.Count).ThenBy(e => e.Def.Name).ToList(),
            _ => owned, // Type = catalogue order (already category-grouped)
        };

        // The bag's TMs pocket lists the machines you own (they never leave the bag once bought).
        var ownedTms = bagTab == ItemTab.Tms
            ? Tms.All.Where(tm => State.OwnedTms.Contains(tm.MoveId)).ToList()
            : new List<TmDef>();
        if (bagTab == ItemTab.Tms && bagSortMode == 1)
        {
            ownedTms = ownedTms.OrderBy(tm => tm.Move.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        var listTop = panel.Min.Y + 8f * scale;
        var filterRow = new Rect(new Vector2(panel.Min.X + 9f * scale, listTop),
            new Vector2(panel.Max.X - 8f * scale, listTop + ItemFilterRowHeight * scale));
        if (DrawItemFilterRow(filterRow, ref bagTab, ref bagSortMode, new[] { "Type", "Name", "Count" }, scale))
        {
            bagScroll = 0f;
        }

        listTop = filterRow.Max.Y + 8f * scale;

        // The Marketboard button carries the latest item message as its sub-line, if there is one.
        var inTown = State.InTown;
        var marketRect = CenteredAt(new Vector2(panel.Center.X, panel.Max.Y - 29f * scale),
            new Vector2(MathF.Min(238f * scale, panel.Width - 40f * scale), 40f * scale));
        var listArea = new Rect(new Vector2(panel.Min.X + 9f * scale, listTop),
            new Vector2(panel.Max.X - 8f * scale, marketRect.Min.Y - 8f * scale));

        if (bagTab == ItemTab.Tms)
        {
            if (ownedTms.Count == 0)
            {
                LgUi.EmptyState(listArea.Center, FontAwesomeIcon.CompactDisc,
                    "No TMs yet. Buy them at a town Marketboard.", theme, scale);
            }
            else
            {
                DrawScrollList(listArea, 50f * scale, 8f * scale, ownedTms.Count, ref bagScroll, scale,
                    (i, rowRect) => DrawTmRow(ownedTms[i], rowRect, theme, scale));
            }
        }
        else if (owned.Count == 0)
        {
            LgUi.EmptyState(listArea.Center, FontAwesomeIcon.SuitcaseRolling,
                bagTab == ItemTab.Items
                    ? "Bag's empty. Restock at a Marketboard."
                    : "Nothing in this pocket yet.", theme, scale);
        }
        else
        {
            DrawScrollList(listArea, 50f * scale, 8f * scale, owned.Count, ref bagScroll, scale,
                (i, rowRect) => DrawBagItemRow(owned[i].Def, owned[i].Count, rowRect, theme, scale));
        }

        // Only the latest item message rides along as the button's sub-line; with nothing to say the
        // button is a single centred label (ColorButton drops the second line when `sub` is null).
        var sub = bagStatus.Length > 0
            ? FitLabel(bagStatus, marketRect.Max.X - marketRect.Min.X - 24f * scale, TextStyles.Caption2)
            : null;
        if (RosterUi.ColorButton(marketRect, "Open Marketboard", inTown ? RosterUi.Blue : RosterUi.NavyInset, scale,
                inTown, "box_cube", sub))
        {
            bagStatus = string.Empty;
            marketScroll = 0f;
            view = View.Market;
        }

        if (ImGui.IsMouseHoveringRect(marketRect.Min, marketRect.Max))
        {
            ShowTooltip(inTown
                ? "Shop for items and heal your team at the Pokécenter counter."
                : "Travel to a town (any aetheryte city) to reach a Marketboard.");
        }

        DrawNavigation(content, theme, scale);

        LgUi.Interactive = prevInteractive;
        if (pickerOpen)
        {
            DrawItemTargetPicker(content, theme, scale);
        }
    }

    // The Poké Dollar balance, an inset navy pill on the header's right (Bag/Market screens).
    private void DrawMoneyPill(Rect content, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var text = LgUi.Money(State.Money);
        var style = TextStyles.FootnoteEmphasized;
        var textSize = Typography.Measure(text, style);
        var max = new Vector2(content.Max.X - 12f * scale, content.Min.Y + 33f * scale);
        // Keep the balance compact so it does not collide with the screen title.
        var min = new Vector2(max.X - textSize.X - 30f * scale, content.Min.Y + 13f * scale);
        var radius = (max.Y - min.Y) * 0.5f;
        Squircle.Fill(drawList, min, max, radius, ImGui.GetColorU32(RosterUi.NavyInset));
        Squircle.Stroke(drawList, min, max, radius, ImGui.GetColorU32(RosterUi.NavyLine with { W = 0.45f }),
            1f * scale);
        ProgressRing.CenterIcon(drawList, new Vector2(min.X + 14f * scale, (min.Y + max.Y) * 0.5f),
            FontAwesomeIcon.Coins, RosterUi.Gold, 10f * scale);
        Typography.Draw(new Vector2(min.X + 26f * scale, (min.Y + max.Y) * 0.5f - 8f * scale), text,
            new Vector4(1f, 1f, 1f, 0.97f), style);
    }

    private void DrawBagItemRow(ItemDef item, int count, Rect rect, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var target = OverworldTarget(item);
        var usable = target is not null && State.Bag.Has(item.Id);
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        var tint = LgUi.ItemTint(item.Category);
        RosterUi.DarkCard(drawList, rect, 10f * scale, scale, hovered && usable, accent: tint);

        var iconCenter = new Vector2(rect.Min.X + 30f * scale, rect.Center.Y);
        RosterUi.IconTile(drawList, iconCenter, 36f * scale, scale);
        LgUi.ItemIcon(drawList, iconCenter, 28f * scale, item);

        Typography.Draw(new Vector2(rect.Min.X + 56f * scale, rect.Min.Y + 8f * scale), item.Name, RosterUi.CardInk,
            TextStyles.Headline);
        Typography.Draw(new Vector2(rect.Min.X + 56f * scale, rect.Min.Y + 29f * scale),
            FitLabel(item.Blurb, rect.Width - 110f * scale, TextStyles.Caption2),
            RosterUi.CardMuted, TextStyles.Caption2);
        Typography.DrawCentered(new Vector2(rect.Max.X - 24f * scale, rect.Center.Y), "x" + count,
            RosterUi.CountGreen, TextStyles.Title3);

        if (hovered)
        {
            ImGui.SetMouseCursor(usable ? ImGuiMouseCursor.Hand : ImGuiMouseCursor.Arrow);
            ShowTooltip(BuildItemUseTooltip(item, target));
            if (usable && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                bagUseItem = item;
                bagStatus = string.Empty;
            }
        }
    }

    private string BuildItemUseTooltip(ItemDef item, MonsterInstance? target)
    {
        var action = item.Category switch
        {
            ItemCategory.Ball => "Poké Balls can only be thrown during a wild battle.",
            ItemCategory.Potion => target is null
                ? "No creature needs healing right now."
                : "Tap to choose which creature to heal.",
            ItemCategory.Revive => target is null
                ? "No creature has fainted."
                : "Tap to choose which creature to revive.",
            ItemCategory.StatusHeal => target is null
                ? "No creature has a matching condition."
                : "Tap to choose which creature to cure.",
            ItemCategory.HeldItem => Items.IsBerry(item.Id)
                ? "Give this to a creature from its Team profile. It is eaten automatically in battle."
                : "Equip this from a creature's Team profile. It activates automatically in battle.",
            ItemCategory.EvolutionStone => "Use this from a compatible creature's Team profile.",
            ItemCategory.AbilityItem => "Use this from a creature's Team profile to preview and change its ability.",
            _ => string.Empty,
        };
        return $"{item.Name}  ·  {LgUi.Money(item.Price)}\n{ItemStatsLine(item)}\n\n{item.Description}\n\n{action}";
    }

    // A one-line precise effect summary shared by the bag and in-battle item tooltips.
    private static string ItemStatsLine(ItemDef item) => item.Category switch
    {
        ItemCategory.Ball => $"Catch rate multiplier ×{item.CatchBonus:0.#}",
        ItemCategory.Potion => item.RestoresFullHp ? "Restores all HP" : $"Restores {item.HealAmount} HP",
        ItemCategory.Revive => item.RevivesToFull ? "Revives to full HP" : "Revives to half HP",
        ItemCategory.StatusHeal => item.CuresAllStatus
            ? "Cures any status condition"
            : $"Cures {StatusWord(item.CuresStatus)}",
        ItemCategory.HeldItem => item.Blurb,
        ItemCategory.EvolutionStone => "Evolves a compatible creature",
        ItemCategory.AbilityItem => "Changes or unlocks an ability",
        _ => string.Empty,
    };

    // The creature an out-of-battle item would act on, or null if none is a valid target.
    private MonsterInstance? OverworldTarget(ItemDef item) => item.Category switch
    {
        ItemCategory.Potion => State.Party
            .Where(m => !m.Fainted && m.CurrentHp < m.MaxHp)
            .OrderBy(m => m.HpFraction)
            .FirstOrDefault(),
        ItemCategory.Revive => State.Party.Concat(State.Box).FirstOrDefault(m => m.Fainted),
        ItemCategory.StatusHeal => State.Party.FirstOrDefault(m =>
            item.CuresAllStatus ? m.Status != Status.None : m.Status == item.CuresStatus),
        _ => null,
    };

    // Whether an out-of-battle item would do anything useful for a specific creature.
    private static bool CanUseItemOn(ItemDef item, MonsterInstance mon) => item.Category switch
    {
        ItemCategory.Potion => !mon.Fainted && mon.CurrentHp < mon.MaxHp,
        ItemCategory.Revive => mon.Fainted,
        ItemCategory.StatusHeal => item.CuresAllStatus ? mon.Status != Status.None : mon.Status == item.CuresStatus,
        _ => false,
    };

    private void UseItemOn(ItemDef item, MonsterInstance target)
    {
        if (!CanUseItemOn(item, target) || !State.Bag.Consume(item.Id))
        {
            return;
        }

        switch (item.Category)
        {
            case ItemCategory.Potion:
                var before = target.CurrentHp;
                target.Heal(item.RestoresFullHp ? target.MaxHp : item.HealAmount);
                bagStatus = $"{item.Name} restored {target.CurrentHp - before} HP to {target.Name}.";
                break;
            case ItemCategory.Revive:
                target.Revive(item.RevivesToFull);
                bagStatus = $"{target.Name} was revived!";
                break;
            case ItemCategory.StatusHeal:
                target.CureStatus();
                bagStatus = $"{item.Name} cured {target.Name}.";
                break;
        }

        State.Save();

        // Keep the picker open only while the item can still help another creature (and any remain).
        if (!State.Bag.Has(item.Id) || !PickerTargets(item).Any(m => CanUseItemOn(item, m)))
        {
            bagUseItem = null;
        }
    }

    // The creatures offered in the target picker for an item (party, plus fainted box members for
    // Revives so a benched, downed creature can still be brought back).
    private IEnumerable<MonsterInstance> PickerTargets(ItemDef item) => item.Category == ItemCategory.Revive
        ? State.Party.Concat(State.Box.Where(m => m.Fainted))
        : State.Party;

    // Overlay that asks which creature to use the held item on. Eligible creatures are tappable;
    // the rest are dimmed with a short reason.
    private void DrawItemTargetPicker(Rect content, PhoneTheme theme, float scale)
    {
        var item = bagUseItem!;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f)));

        var targets = PickerTargets(item).ToList();
        var rowH = 44f * scale;
        var rowGap = 6f * scale;
        var headerH = 46f * scale;
        var footerH = 44f * scale;
        var cardW = MathF.Min(content.Width - 36f * scale, 320f * scale);
        var wanted = headerH + targets.Count * (rowH + rowGap) + footerH;
        var cardH = MathF.Min(wanted, content.Height - 36f * scale);
        var card = CenteredAt(content.Center, new Vector2(cardW, cardH));

        RosterUi.ChunkyCard(drawList, card.Min, card.Max, 14f * scale, scale, RosterUi.Cream, RosterUi.CreamShade,
            RosterUi.NavyEdge);

        LgUi.ItemIcon(drawList, new Vector2(card.Min.X + 26f * scale, card.Min.Y + 23f * scale), 26f * scale, item);
        Typography.Draw(new Vector2(card.Min.X + 46f * scale, card.Min.Y + 14f * scale),
            FitLabel($"Use {item.Name} on…", cardW - 60f * scale, TextStyles.Headline), RosterUi.InkNavy,
            TextStyles.Headline);

        var y = card.Min.Y + headerH;
        foreach (var mon in targets)
        {
            var r = new Rect(new Vector2(card.Min.X + 10f * scale, y),
                new Vector2(card.Max.X - 10f * scale, y + rowH));
            DrawItemTargetRow(drawList, r, item, mon, theme, scale);
            y += rowH + rowGap;
        }

        var cancel = CenteredAt(new Vector2(card.Center.X, card.Max.Y - 22f * scale),
            new Vector2(cardW * 0.5f, 28f * scale));
        if (RosterUi.BlueButton(cancel, "CANCEL", scale, true))
        {
            bagUseItem = null;
        }

        // A tap anywhere off the card also dismisses the picker.
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !card.Contains(ImGui.GetMousePos()))
        {
            bagUseItem = null;
        }
    }

    private void DrawItemTargetRow(ImDrawListPtr drawList, Rect r, ItemDef item, MonsterInstance mon, PhoneTheme theme,
        float scale)
    {
        var eligible = CanUseItemOn(item, mon);
        var hovered = eligible && ImGui.IsMouseHoveringRect(r.Min, r.Max);
        RosterUi.DarkCard(drawList, r, 9f * scale, scale, hovered, !eligible);

        var portrait = new Vector2(r.Min.X + r.Height * 0.5f, r.Center.Y);
        MonsterArt.Draw(drawList, portrait, r.Height * 0.4f, mon.Species, 1f,
            new MonsterPose(time, 0f, 0f, eligible ? 1f : 0.5f, mon.Fainted));

        var tx = r.Min.X + r.Height + 4f * scale;
        var nameCol = eligible ? RosterUi.CardInk : RosterUi.CardMuted;
        Typography.Draw(new Vector2(tx, r.Min.Y + 6f * scale),
            FitLabel(mon.Name, r.Width - r.Height - 96f * scale, TextStyles.Subheadline), nameCol,
            TextStyles.Subheadline);
        Typography.Draw(new Vector2(tx, r.Min.Y + 24f * scale), $"Lv {mon.Level}",
            RosterUi.CardMuted, TextStyles.Caption2);
        LgUi.HpBar(drawList, new Vector2(tx, r.Max.Y - 9f * scale),
            new Vector2(r.Min.X + r.Width * 0.62f, r.Max.Y - 4f * scale), mon.HpFraction);

        if (eligible)
        {
            Typography.DrawCentered(new Vector2(r.Max.X - 30f * scale, r.Center.Y), $"{mon.CurrentHp}/{mon.MaxHp}",
                RosterUi.CardInk with { W = 0.85f }, TextStyles.Caption1);
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    UseItemOn(item, mon);
                }
            }
        }
        else
        {
            var reason = item.Category switch
            {
                ItemCategory.Potion => mon.Fainted ? "Fainted" : "Full HP",
                ItemCategory.Revive => "Healthy",
                ItemCategory.StatusHeal => "No status",
                _ => string.Empty,
            };
            Typography.DrawCentered(new Vector2(r.Max.X - 34f * scale, r.Center.Y), reason,
                RosterUi.CardMuted with { W = 0.7f }, TextStyles.Caption2);
        }
    }
}
