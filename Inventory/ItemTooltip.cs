using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Lumina.Excel.Sheets;
using VideoSyncPrototype.Windows;

namespace VideoSyncPrototype.Inventory;

/// <summary>
/// A shared hover tooltip for any game item: icon, rarity-coloured name, category, item
/// level / equip level / jobs for gear, and the flavour description — the "what is this"
/// popup used by both the inventory and item-search tabs.
/// </summary>
internal static class ItemTooltip
{
    public static void Draw(uint itemId, bool isEvent, bool isHq, string? footer = null)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);

        if (isEvent)
        {
            DrawEventItem(itemId);
        }
        else
        {
            DrawNormalItem(itemId, isHq);
        }

        if (!string.IsNullOrEmpty(footer))
        {
            ImGui.Spacing();
            ImGui.TextColored(UiTheme.Muted, footer);
        }

        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static void DrawNormalItem(uint itemId, bool isHq)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (sheet == null || !sheet.TryGetRow(itemId, out var item))
        {
            ImGui.Text("Unknown item");
            return;
        }

        var category = string.Empty;
        if (Plugin.DataManager.GetExcelSheet<ItemUICategory>() is { } cats &&
            item.ItemUICategory.RowId != 0 && cats.TryGetRow(item.ItemUICategory.RowId, out var cat))
        {
            category = cat.Name.ExtractText();
        }

        var name = item.Name.ExtractText() + (isHq ? "  (HQ)" : string.Empty);
        DrawHeader(item.Icon, name, RarityColor(item.Rarity), category);

        var equippable = item.EquipSlotCategory.RowId != 0 && item.ItemUICategory.RowId != 62;
        var ilvl = item.LevelItem.RowId;
        if (equippable)
        {
            ImGui.TextColored(new Vector4(0.80f, 0.86f, 1.00f, 1f), $"Item Level {ilvl}    Equip Level {item.LevelEquip}");
            if (item.ClassJobCategory.ValueNullable is { } jobs)
            {
                var jobText = jobs.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(jobText))
                {
                    ImGui.TextColored(UiTheme.Muted, jobText);
                }
            }
        }
        else if (ilvl > 0)
        {
            ImGui.TextColored(new Vector4(0.80f, 0.86f, 1.00f, 1f), $"Item Level {ilvl}");
        }

        var description = item.Description.ExtractText();
        if (!string.IsNullOrWhiteSpace(description))
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.84f, 0.80f, 0.72f, 1f), description);
        }
    }

    private static void DrawEventItem(uint itemId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<EventItem>();
        if (sheet == null || !sheet.TryGetRow(itemId, out var item))
        {
            ImGui.Text("Unknown item");
            return;
        }

        DrawHeader(item.Icon, item.Name.ExtractText(), new Vector4(0.80f, 0.75f, 0.55f, 1f), "Key Item");

        // Key-item flavour text lives in the parallel EventItemHelp sheet.
        if (Plugin.DataManager.GetExcelSheet<EventItemHelp>() is { } help &&
            help.TryGetRow(itemId, out var row))
        {
            var description = row.Description.ExtractText();
            if (!string.IsNullOrWhiteSpace(description))
            {
                ImGui.Separator();
                ImGui.TextColored(new Vector4(0.84f, 0.80f, 0.72f, 1f), description);
            }
        }
    }

    private static void DrawHeader(uint iconId, string name, Vector4 nameColor, string category)
    {
        var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrDefault();
        if (icon != null)
        {
            ImGui.Image(icon.Handle, new Vector2(40f, 40f));
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.TextColored(nameColor, name);
        if (!string.IsNullOrEmpty(category))
        {
            ImGui.TextColored(UiTheme.Muted, category);
        }

        ImGui.EndGroup();
    }

    private static Vector4 RarityColor(byte rarity) => rarity switch
    {
        2 => new Vector4(0.40f, 0.85f, 0.40f, 1f), // green
        3 => new Vector4(0.40f, 0.65f, 1.00f, 1f), // blue
        4 => new Vector4(0.72f, 0.52f, 0.98f, 1f), // purple / relic
        7 => new Vector4(1.00f, 0.55f, 0.79f, 1f), // pink / aetherial
        _ => new Vector4(1.00f, 1.00f, 1.00f, 1f), // white
    };
}
