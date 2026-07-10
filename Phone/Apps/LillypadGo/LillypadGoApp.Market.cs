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
    // ---- Marketboard (town-only shop + Pokécenter) ----------------------------------
    // Navy/cream chrome per Ideas/UI Update/Marketboard.png: the Pokécenter heal card up top, then
    // the Bag.png sort-and-filter strip over a navy shop list with purple price buttons.

    private float marketScroll;
    private ItemTab marketTab = ItemTab.Items;
    private int marketSortMode; // 0 = Type, 1 = Name, 2 = Price

    private void DrawMarket(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();

        // The Marketboard only exists in town — if the player wanders off, fall back to the Bag.
        if (!State.InTown)
        {
            view = View.Bag;
            return;
        }

        drawList.AddRectFilled(content.Min, content.Max, ImGui.GetColorU32(RosterUi.NavyBottom));
        var headerBottom = RosterUi.ScreenHeader(content, "MARKETBOARD", "box_cube", null, scale);
        DrawMoneyPill(content, scale);

        var navTop = content.Max.Y - NavBarHeight * scale;
        var panel = new Rect(new Vector2(content.Min.X + 7f * scale, headerBottom + 6f * scale),
            new Vector2(content.Max.X - 7f * scale, navTop - 7f * scale));
        RosterUi.CreamPanel(drawList, panel, scale);
        var left = panel.Min.X + 9f * scale;
        var right = panel.Max.X - 9f * scale;

        var pokecenter = new Rect(new Vector2(left, panel.Min.Y + 8f * scale),
            new Vector2(right, panel.Min.Y + 66f * scale));
        DrawPokecenterCard(pokecenter, scale);

        var filterRow = new Rect(new Vector2(left, pokecenter.Max.Y + 8f * scale),
            new Vector2(right, pokecenter.Max.Y + (8f + ItemFilterRowHeight) * scale));
        if (DrawItemFilterRow(filterRow, ref marketTab, ref marketSortMode, new[] { "Type", "Name", "Price" }, scale))
        {
            marketScroll = 0f;
        }

        var listArea = new Rect(new Vector2(left, filterRow.Max.Y + 8f * scale),
            new Vector2(right + 1f * scale, panel.Max.Y - 26f * scale));

        if (marketTab == ItemTab.Tms)
        {
            var stock = marketSortMode switch
            {
                1 => Tms.All.OrderBy(tm => tm.Move.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                2 => Tms.All.OrderBy(tm => tm.Price).ThenBy(tm => tm.Number).ToList(),
                _ => Tms.All.ToList(), // Type = TM number order
            };
            DrawScrollList(listArea, 50f * scale, 8f * scale, stock.Count, ref marketScroll, scale,
                (i, rowRect) => DrawTmRow(stock[i], rowRect, theme, scale));
        }
        else
        {
            var stock = Items.All.Where(item => Items.InTab(item, marketTab)).ToList();
            stock = marketSortMode switch
            {
                1 => stock.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                2 => stock.OrderBy(item => item.Price).ThenBy(item => item.Name).ToList(),
                _ => stock, // Type = catalogue order (already category-grouped)
            };
            DrawScrollList(listArea, 50f * scale, 8f * scale, stock.Count, ref marketScroll, scale,
                (i, rowRect) => DrawShopRow(stock[i], rowRect, theme, scale));
        }

        var status = bagStatus.Length > 0
            ? bagStatus
            : marketTab == ItemTab.Tms
                ? "Buy a TM here, then teach it from a creature's Moves screen if it can learn it."
                : "Buy supplies";
        Typography.DrawCentered(new Vector2(panel.Center.X, panel.Max.Y - 14f * scale),
            FitLabel(status, panel.Width - 24f * scale, TextStyles.Caption1), RosterUi.InkTan, TextStyles.Caption1);

        DrawNavigation(content, theme, scale);
    }

    private void DrawPokecenterCard(Rect card, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var green = RosterUi.Green;
        RosterUi.DarkCard(drawList, card, 10f * scale, scale, accent: RosterUi.GreenBright);

        var iconCenter = new Vector2(card.Min.X + 32f * scale, card.Center.Y);
        RosterUi.IconTile(drawList, iconCenter, 38f * scale, scale, RosterUi.GreenBright with { W = 0.6f });
        ProgressRing.CenterIcon(drawList, iconCenter, FontAwesomeIcon.HandHoldingHeart,
            RosterUi.GreenBright, 16f * scale);

        var wiped = State.AllMonstersFainted;
        Typography.Draw(new Vector2(card.Min.X + 60f * scale, card.Min.Y + 10f * scale), "Pokécenter",
            RosterUi.CardInk, TextStyles.Headline);
        Typography.Draw(new Vector2(card.Min.X + 60f * scale, card.Min.Y + 31f * scale),
            FitLabel(wiped ? "Revive your team for free." : "Restores your team to full health.",
                card.Width - 60f * scale - 100f * scale, TextStyles.Caption2),
            RosterUi.CardMuted, TextStyles.Caption2);

        var needsCare = State.Party.Concat(State.Box)
            .Any(m => m.Fainted || m.CurrentHp < m.MaxHp || m.Status != Status.None);
        var healRect = CenteredAt(new Vector2(card.Max.X - 52f * scale, card.Center.Y),
            new Vector2(84f * scale, 30f * scale));
        if (RosterUi.ColorButton(healRect, wiped ? "Revive" : "Heal", green, scale, needsCare))
        {
            State.HealAllMonsters();
            bagStatus = "Your team was fully restored!";
        }

        if (ImGui.IsMouseHoveringRect(healRect.Min, healRect.Max))
        {
            ShowTooltip(needsCare
                ? "Free full heal and revival for your entire roster (party and storage)."
                : "Your team is already in perfect health.");
        }
    }

    private void DrawShopRow(ItemDef item, Rect rect, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var alphaExclusive = Alphas.IsExclusiveDrop(item.Id);
        var canAfford = State.Money >= item.Price;
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        var tint = LgUi.ItemTint(item.Category);
        RosterUi.DarkCard(drawList, rect, 10f * scale, scale, hovered, accent: tint);

        var iconCenter = new Vector2(rect.Min.X + 30f * scale, rect.Center.Y);
        RosterUi.IconTile(drawList, iconCenter, 36f * scale, scale);
        LgUi.ItemIcon(drawList, iconCenter, 28f * scale, item);

        var owned = State.Bag.Count(item.Id);
        var name = owned > 0 ? $"{item.Name}  (x{owned})" : item.Name;
        Typography.Draw(new Vector2(rect.Min.X + 56f * scale, rect.Min.Y + 8f * scale),
            FitLabel(name, rect.Width - 150f * scale, TextStyles.Headline), RosterUi.CardInk, TextStyles.Headline);
        Typography.Draw(new Vector2(rect.Min.X + 56f * scale, rect.Min.Y + 29f * scale),
            FitLabel(item.Blurb, rect.Width - 150f * scale, TextStyles.Caption2),
            RosterUi.CardMuted, TextStyles.Caption2);

        var buyRect = CenteredAt(new Vector2(rect.Max.X - 46f * scale, rect.Center.Y),
            new Vector2(80f * scale, 30f * scale));
        if (alphaExclusive)
        {
            RosterUi.ColorButton(buyRect, "ALPHA", RosterUi.Gold, scale, false);
        }
        else if (RosterUi.ColorButton(buyRect, LgUi.Money(item.Price), RosterUi.Purple, scale, canAfford))
        {
            State.Money -= item.Price;
            State.Bag.Add(item.Id, 1);
            State.Save();
            bagStatus = $"Bought 1 {item.Name}.";
        }

        if (alphaExclusive && hovered)
        {
            ShowTooltip(BuildItemTooltip(item, owned));
        }
        else if (!canAfford && LgUi.Interactive && ImGui.IsMouseHoveringRect(buyRect.Min, buyRect.Max))
        {
            ShowTooltip($"You need {LgUi.Money(item.Price - State.Money)} more.");
        }
        else if (hovered)
        {
            ShowTooltip(BuildItemTooltip(item, owned));
        }
    }

    private void DrawTmRow(TmDef tm, Rect rect, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var owned = State.OwnedTms.Contains(tm.MoveId);
        var alphaExclusive = Alphas.IsExclusiveTm(tm.MoveId);
        var canAfford = State.Money >= tm.Price;
        var tint = Elements.Color(tm.Move.Element);
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        RosterUi.DarkCard(drawList, rect, 10f * scale, scale, hovered, accent: tint);

        // Disc badge with the TM number.
        var iconCenter = new Vector2(rect.Min.X + 30f * scale, rect.Center.Y);
        RosterUi.IconTile(drawList, iconCenter, 36f * scale, scale, GamePalette.Lighten(tint, 0.1f) with { W = 0.8f });
        Typography.DrawCentered(iconCenter, tm.Number.ToString(), GamePalette.Lighten(tint, 0.25f),
            TextStyles.Caption1);

        Typography.Draw(new Vector2(rect.Min.X + 56f * scale, rect.Min.Y + 7f * scale),
            FitLabel($"{Tms.Label(tm.Number)}  {tm.Move.Name}", rect.Width - 152f * scale, TextStyles.Headline),
            RosterUi.CardInk, TextStyles.Headline);
        var power = tm.Move.IsStatus ? "Status" : $"Pow {tm.Move.Power}";
        Typography.Draw(new Vector2(rect.Min.X + 56f * scale, rect.Min.Y + 29f * scale),
            FitLabel($"{Elements.Name(tm.Move.Element)}  ·  {power}  ·  {tm.Move.Pp} PP", rect.Width - 152f * scale,
                TextStyles.Caption2), RosterUi.CardMuted, TextStyles.Caption2);

        var buyRect = CenteredAt(new Vector2(rect.Max.X - 46f * scale, rect.Center.Y),
            new Vector2(80f * scale, 30f * scale));
        if (owned)
        {
            Typography.DrawCentered(buyRect.Center, "Owned", RosterUi.CountGreen, TextStyles.Caption1);
        }
        else if (alphaExclusive)
        {
            RosterUi.ColorButton(buyRect, "ALPHA", RosterUi.Gold, scale, false);
        }
        else if (RosterUi.ColorButton(buyRect, LgUi.Money(tm.Price), RosterUi.Purple, scale, canAfford))
        {
            State.Money -= tm.Price;
            State.OwnedTms.Add(tm.MoveId);
            State.Save();
            bagStatus = $"Bought {Tms.Label(tm.Number)} {tm.Move.Name}.";
        }

        if (hovered)
        {
            var alphaSource = alphaExclusive ? "\n\n" + Alphas.TmSourceText(tm.MoveId) : string.Empty;
            ShowTooltip(BuildProfileMoveTooltip(tm.Move, tm.Move.Pp) + alphaSource);
        }

        if (!owned && !alphaExclusive && !canAfford && LgUi.Interactive && ImGui.IsMouseHoveringRect(buyRect.Min, buyRect.Max))
        {
            ShowTooltip($"You need {LgUi.Money(tm.Price - State.Money)} more.");
        }
    }
}
