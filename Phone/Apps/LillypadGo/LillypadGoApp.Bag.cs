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

    private void DrawBag(Rect content, PhoneTheme theme)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        BiomeBackdrop.Draw(drawList, content, State.CurrentBiome, time, false);
        LgUi.Header(content, theme, Accent, "Bag", null, scale);
        DrawMoneyPill(content, theme, scale);

        var owned = State.Bag.Contents().ToList();
        owned = bagSortMode switch
        {
            1 => owned.OrderBy(e => e.Def.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            2 => owned.OrderByDescending(e => e.Count).ThenBy(e => e.Def.Name).ToList(),
            _ => owned, // Type = catalogue order (already category-grouped)
        };

        if (owned.Count > 0)
        {
            var sortRect = CenteredAt(new Vector2(content.Min.X + 70f * scale, content.Min.Y + 44f * scale),
                new Vector2(116f * scale, 22f * scale));
            if (LgUi.Button(sortRect, $"Sort: {new[] { "Type", "Name", "Count" }[bagSortMode]}", GamePalette.Cell,
                    theme, true))
            {
                bagSortMode = (bagSortMode + 1) % 3;
                bagScroll = 0f;
            }
        }

        var listTop = content.Min.Y + 74f * scale;
        var listBottom = content.Max.Y - 104f * scale;
        var listArea = new Rect(new Vector2(content.Min.X + 12f * scale, listTop),
            new Vector2(content.Max.X - 12f * scale, listBottom));

        if (owned.Count == 0)
        {
            LgUi.EmptyState(listArea.Center, FontAwesomeIcon.SuitcaseRolling,
                "Your bag is empty. Buy supplies at a town Marketboard.", theme, scale);
        }
        else
        {
            DrawScrollList(listArea, 50f * scale, 8f * scale, owned.Count, ref bagScroll, scale,
                (i, rowRect) => DrawBagItemRow(owned[i].Def, owned[i].Count, rowRect, theme, scale));
        }

        var inTown = State.InTown;
        var marketRect = CenteredAt(new Vector2(content.Center.X, listBottom + 20f * scale),
            new Vector2(224f * scale, 32f * scale));
        if (LgUi.Button(marketRect, "Open Marketboard", inTown ? theme.Accent : GamePalette.CellSunken, theme, inTown))
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

        var status = bagStatus.Length > 0
            ? bagStatus
            : $"{State.BattlesWon} wins  ·  {State.Captures} captures";
        Typography.DrawCentered(new Vector2(content.Center.X, listBottom + 44f * scale),
            FitLabel(status, content.Width - 24f * scale, TextStyles.Caption1), theme.TextMuted, TextStyles.Caption1);

        DrawNavigation(content, theme, scale);
    }

    // The Poké Dollar balance, drawn as a pill just under the header on the Bag/Market screens.
    private void DrawMoneyPill(Rect content, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var text = LgUi.Money(State.Money);
        var textSize = Typography.Measure(text, TextStyles.Caption1);
        var innerWidth = 18f * scale + textSize.X;
        var padX = 10f * scale;
        var max = new Vector2(content.Max.X - 14f * scale, content.Min.Y + 50f * scale);
        var min = new Vector2(max.X - innerWidth - padX * 2f, max.Y - 21f * scale);
        var radius = (max.Y - min.Y) * 0.5f;
        Squircle.Fill(drawList, min, max, radius, ImGui.GetColorU32(new Vector4(0.02f, 0.025f, 0.035f, 0.55f)));
        Squircle.Stroke(drawList, min, max, radius, ImGui.GetColorU32(Accent with { W = 0.5f }), 1f * scale);
        ProgressRing.CenterIcon(drawList, new Vector2(min.X + padX + 5f * scale, (min.Y + max.Y) * 0.5f),
            FontAwesomeIcon.Coins, Accent, 10f * scale);
        Typography.Draw(new Vector2(min.X + padX + 16f * scale, (min.Y + max.Y) * 0.5f - 8f * scale), text,
            theme.TextStrong, TextStyles.Caption1);
    }

    private void DrawBagItemRow(ItemDef item, int count, Rect rect, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var target = OverworldTarget(item);
        var usable = target is not null && State.Bag.Has(item.Id);
        var hovered = LgUi.Interactive && ImGui.IsMouseHoveringRect(rect.Min, rect.Max);
        var tint = LgUi.ItemTint(item.Category);
        LgUi.Card(drawList, rect.Min, rect.Max, 11f * scale, scale, hovered && usable);
        drawList.AddRectFilled(rect.Min, new Vector2(rect.Min.X + 4f * scale, rect.Max.Y),
            ImGui.GetColorU32(tint with { W = 0.8f }), 3f * scale);

        var iconCenter = new Vector2(rect.Min.X + 30f * scale, rect.Center.Y);
        drawList.AddCircleFilled(iconCenter, 17f * scale, ImGui.GetColorU32(GamePalette.CellSunken));
        drawList.AddCircle(iconCenter, 17f * scale, ImGui.GetColorU32(tint with { W = 0.5f }), 24, 1f * scale);
        LgUi.ItemIcon(drawList, iconCenter, 28f * scale, item);

        Typography.Draw(new Vector2(rect.Min.X + 54f * scale, rect.Min.Y + 8f * scale), item.Name, theme.TextStrong,
            TextStyles.Headline);
        Typography.Draw(new Vector2(rect.Min.X + 54f * scale, rect.Min.Y + 29f * scale),
            FitLabel(item.Blurb, rect.Width - 108f * scale, TextStyles.Caption2),
            theme.TextStrong with { W = 0.78f }, TextStyles.Caption2);
        Typography.DrawCentered(new Vector2(rect.Max.X - 24f * scale, rect.Center.Y), "x" + count, tint,
            TextStyles.Title3);

        if (hovered)
        {
            ImGui.SetMouseCursor(usable ? ImGuiMouseCursor.Hand : ImGuiMouseCursor.Arrow);
            ShowTooltip(BuildItemUseTooltip(item, target));
            if (usable && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                UseItemOverworld(item);
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
                : $"Tap to restore {(item.RestoresFullHp ? target.MaxHp - target.CurrentHp : Math.Min(item.HealAmount, target.MaxHp - target.CurrentHp))} HP to {target.Name}.",
            ItemCategory.Revive => target is null
                ? "No creature has fainted."
                : $"Tap to revive {target.Name} to {(item.RevivesToFull ? target.MaxHp : Math.Max(1, target.MaxHp / 2))} HP.",
            ItemCategory.StatusHeal => target is null
                ? "No creature has a matching condition."
                : $"Tap to cure {target.Name}.",
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

    private void UseItemOverworld(ItemDef item)
    {
        var target = OverworldTarget(item);
        if (target is null || !State.Bag.Consume(item.Id))
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
    }
}
