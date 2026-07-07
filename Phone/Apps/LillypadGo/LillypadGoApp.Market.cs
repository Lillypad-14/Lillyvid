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

    private float marketScroll;

    private void DrawMarket(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        BiomeBackdrop.Draw(drawList, content, State.CurrentBiome, time, false);

        // The Marketboard only exists in town — if the player wanders off, fall back to the Bag.
        if (!State.InTown)
        {
            view = View.Bag;
            return;
        }

        LgUi.Header(content, theme, Accent, "Marketboard", null, scale);
        DrawMoneyPill(content, theme, scale);

        DrawPokecenterCard(content, theme, scale);

        Typography.Draw(new Vector2(content.Min.X + 16f * scale, content.Min.Y + 138f * scale), "FOR SALE",
            Accent, TextStyles.Caption2);
        var listTop = content.Min.Y + 156f * scale;
        var listBottom = content.Max.Y - 62f * scale;
        var listArea = new Rect(new Vector2(content.Min.X + 12f * scale, listTop),
            new Vector2(content.Max.X - 12f * scale, listBottom));
        DrawScrollList(listArea, 50f * scale, 8f * scale, Items.All.Count, ref marketScroll, scale,
            (i, rowRect) => DrawShopRow(Items.All[i], rowRect, theme, scale));

        var status = bagStatus.Length > 0
            ? bagStatus
            : "Buy supplies, then use Poké Balls and potions from the Bag or in battle.";
        Typography.DrawCentered(new Vector2(content.Center.X, listBottom + 16f * scale),
            FitLabel(status, content.Width - 24f * scale, TextStyles.Caption1), theme.TextMuted, TextStyles.Caption1);

        DrawNavigation(content, theme, scale);
    }

    private void DrawPokecenterCard(Rect content, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var green = new Vector4(0.40f, 0.82f, 0.52f, 1f);
        var min = new Vector2(content.Min.X + 12f * scale, content.Min.Y + 60f * scale);
        var max = new Vector2(content.Max.X - 12f * scale, content.Min.Y + 124f * scale);
        LgUi.Card(drawList, min, max, 12f * scale, scale);
        Squircle.Stroke(drawList, min, max, 12f * scale, ImGui.GetColorU32(green with { W = 0.5f }), 1.2f * scale);

        var iconCenter = new Vector2(min.X + 34f * scale, (min.Y + max.Y) * 0.5f);
        drawList.AddCircleFilled(iconCenter, 17f * scale, ImGui.GetColorU32(GamePalette.CellSunken));
        drawList.AddCircle(iconCenter, 17f * scale, ImGui.GetColorU32(green with { W = 0.6f }), 24, 1f * scale);
        ProgressRing.CenterIcon(drawList, iconCenter, FontAwesomeIcon.HandHoldingHeart, green, 16f * scale);

        var wiped = State.AllMonstersFainted;
        Typography.Draw(new Vector2(min.X + 62f * scale, min.Y + 11f * scale), "Pokécenter", theme.TextStrong,
            TextStyles.Headline);
        Typography.Draw(new Vector2(min.X + 62f * scale, min.Y + 32f * scale),
            FitLabel(wiped ? "Revive your fainted team, free of charge." : "Fully restore HP, PP and status — free.",
                max.X - min.X - 62f * scale - 96f * scale, TextStyles.Caption2),
            theme.TextMuted, TextStyles.Caption2);

        var needsCare = State.Party.Concat(State.Box)
            .Any(m => m.Fainted || m.CurrentHp < m.MaxHp || m.Status != Status.None);
        var healRect = CenteredAt(new Vector2(max.X - 54f * scale, (min.Y + max.Y) * 0.5f),
            new Vector2(88f * scale, 32f * scale));
        if (LgUi.Button(healRect, wiped ? "Revive" : "Heal", green, theme, needsCare))
        {
            State.HealAllMonsters();
            bagStatus = "Your team was fully restored. Thank you for waiting!";
        }

        if (ImGui.IsMouseHoveringRect(healRect.Min, healRect.Max))
        {
            ImGui.SetTooltip(needsCare
                ? "Free full heal and revival for your entire roster (party and storage)."
                : "Your team is already in perfect health.");
        }
    }

    private void DrawShopRow(ItemDef item, Rect rect, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var canAfford = State.Money >= item.Price;
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        var tint = LgUi.ItemTint(item.Category);
        LgUi.Card(drawList, rect.Min, rect.Max, 11f * scale, scale, hovered);
        drawList.AddRectFilled(rect.Min, new Vector2(rect.Min.X + 4f * scale, rect.Max.Y),
            ImGui.GetColorU32(tint with { W = 0.8f }), 3f * scale);

        var iconCenter = new Vector2(rect.Min.X + 30f * scale, rect.Center.Y);
        drawList.AddCircleFilled(iconCenter, 17f * scale, ImGui.GetColorU32(GamePalette.CellSunken));
        drawList.AddCircle(iconCenter, 17f * scale, ImGui.GetColorU32(tint with { W = 0.5f }), 24, 1f * scale);
        LgUi.ItemIcon(drawList, iconCenter, 28f * scale, item);

        var owned = State.Bag.Count(item.Id);
        var name = owned > 0 ? $"{item.Name}  (x{owned})" : item.Name;
        Typography.Draw(new Vector2(rect.Min.X + 54f * scale, rect.Min.Y + 8f * scale), name, theme.TextStrong,
            TextStyles.Headline);
        Typography.Draw(new Vector2(rect.Min.X + 54f * scale, rect.Min.Y + 29f * scale),
            FitLabel(item.Blurb, rect.Width - 148f * scale, TextStyles.Caption2),
            theme.TextStrong with { W = 0.78f }, TextStyles.Caption2);

        var buyRect = CenteredAt(new Vector2(rect.Max.X - 46f * scale, rect.Center.Y),
            new Vector2(80f * scale, 30f * scale));
        if (LgUi.Button(buyRect, LgUi.Money(item.Price), canAfford ? theme.Accent : GamePalette.CellSunken, theme,
                canAfford))
        {
            State.Money -= item.Price;
            State.Bag.Add(item.Id, 1);
            State.Save();
            bagStatus = $"Bought 1 {item.Name}.";
        }

        if (!canAfford && LgUi.Interactive && ImGui.IsMouseHoveringRect(buyRect.Min, buyRect.Max))
        {
            ImGui.SetTooltip($"You need {LgUi.Money(item.Price - State.Money)} more.");
        }
    }
}
