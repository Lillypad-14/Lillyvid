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

    // The item awaiting a target choice: while set, the bag shows a "use on which creature?" picker.
    private ItemDef? bagUseItem;

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

        LgUi.Interactive = prevInteractive;
        if (pickerOpen)
        {
            DrawItemTargetPicker(content, theme, scale);
        }
    }

    // The Poké Dollar balance, drawn as a pill just under the header on the Bag/Market screens.
    private void DrawMoneyPill(Rect content, PhoneTheme theme, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var text = LgUi.Money(State.Money);
        var textSize = Typography.Measure(text, TextStyles.Caption1);
        var innerWidth = 18f * scale + textSize.X;
        var padX = 10f * scale;
        var max = new Vector2(content.Max.X - 14f * scale, content.Min.Y + 62f * scale);
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
        var tint = LgUi.ItemTint(item.Category);
        var rowH = 44f * scale;
        var rowGap = 6f * scale;
        var headerH = 46f * scale;
        var footerH = 44f * scale;
        var cardW = MathF.Min(content.Width - 36f * scale, 320f * scale);
        var wanted = headerH + targets.Count * (rowH + rowGap) + footerH;
        var cardH = MathF.Min(wanted, content.Height - 36f * scale);
        var card = CenteredAt(content.Center, new Vector2(cardW, cardH));

        Elevation.Draw(drawList, card.Min, card.Max, 16f * scale, scale, 16f, 5f, 0.4f);
        Squircle.FillVerticalGradient(drawList, card.Min, card.Max, 16f * scale,
            ImGui.GetColorU32(GamePalette.Lighten(GamePalette.Board, 0.05f) with { W = 0.99f }),
            ImGui.GetColorU32(GamePalette.Darken(GamePalette.Board, 0.16f) with { W = 0.99f }));
        Squircle.Stroke(drawList, card.Min, card.Max, 16f * scale, ImGui.GetColorU32(tint with { W = 0.6f }),
            1.4f * scale);

        LgUi.ItemIcon(drawList, new Vector2(card.Min.X + 26f * scale, card.Min.Y + 23f * scale), 26f * scale, item);
        Typography.Draw(new Vector2(card.Min.X + 46f * scale, card.Min.Y + 14f * scale),
            FitLabel($"Use {item.Name} on…", cardW - 60f * scale, TextStyles.Headline), theme.TextStrong,
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
        if (LgUi.Button(cancel, "Cancel", GamePalette.Cell, theme, true))
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
        LgUi.Card(drawList, r.Min, r.Max, 9f * scale, scale, hovered, !eligible);

        var portrait = new Vector2(r.Min.X + r.Height * 0.5f, r.Center.Y);
        MonsterArt.Draw(drawList, portrait, r.Height * 0.4f, mon.Species, 1f,
            new MonsterPose(time, 0f, 0f, eligible ? 1f : 0.5f, mon.Fainted));

        var tx = r.Min.X + r.Height + 4f * scale;
        var nameCol = eligible ? theme.TextStrong : theme.TextMuted;
        Typography.Draw(new Vector2(tx, r.Min.Y + 6f * scale),
            FitLabel(mon.Name, r.Width - r.Height - 96f * scale, TextStyles.Subheadline), nameCol,
            TextStyles.Subheadline);
        Typography.Draw(new Vector2(tx, r.Min.Y + 24f * scale), $"Lv {mon.Level}",
            theme.TextMuted, TextStyles.Caption2);
        LgUi.HpBar(drawList, new Vector2(tx, r.Max.Y - 9f * scale),
            new Vector2(r.Min.X + r.Width * 0.62f, r.Max.Y - 4f * scale), mon.HpFraction);

        if (eligible)
        {
            Typography.DrawCentered(new Vector2(r.Max.X - 30f * scale, r.Center.Y), $"{mon.CurrentHp}/{mon.MaxHp}",
                theme.TextStrong with { W = 0.8f }, TextStyles.Caption1);
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
                theme.TextMuted with { W = 0.7f }, TextStyles.Caption2);
        }
    }
}
