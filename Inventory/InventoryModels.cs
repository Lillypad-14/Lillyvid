using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace VideoSyncPrototype.Inventory;

/// <summary>
/// A single stack of items read out of a game inventory container, flattened into a
/// plain snapshot so the UI and discard scheduler do not touch live game memory while
/// drawing.
/// </summary>
public sealed class InventoryItemInfo
{
    public uint ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public InventoryType Container { get; set; }
    public short Slot { get; set; }
    public bool IsHQ { get; set; }
    public uint IconId { get; set; }
    public bool CanBeDiscarded { get; set; }
    public bool CanBeTraded { get; set; }
    public bool IsCollectable { get; set; }
    public int Durability { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public uint ItemUICategory { get; set; }
    public uint ItemLevel { get; set; }
    public uint EquipLevel { get; set; }
    public byte Rarity { get; set; }
    public bool IsUnique { get; set; }
    public bool IsUntradable { get; set; }
    public bool IsIndisposable { get; set; }
    public uint EquipSlotCategory { get; set; }

    public SafetyAssessment? SafetyAssessment { get; set; }
    public bool IsSelected { get; set; }
    public long? MarketPrice { get; set; }

    /// <summary>The world the cheapest listing is on (cross-world within the data centre).</summary>
    public string? MarketWorld { get; set; }
    public DateTime? MarketPriceFetchTime { get; set; }

    /// <summary>Weapons, tools and armor live in ItemUICategory 35..44.</summary>
    public bool IsGear => ItemUICategory >= 35 && ItemUICategory <= 44;

    /// <summary>Anything with an equip slot except soul crystals (category 62).</summary>
    public bool IsEquippable => EquipSlotCategory != 0 && ItemUICategory != 62;

    /// <summary>Identifies the exact physical slot this stack was read from.</summary>
    public string GetUniqueKey() => $"{Container}_{Slot}";

    public string GetFormattedPrice()
    {
        if (!MarketPrice.HasValue)
        {
            return "---";
        }

        return MarketPrice.Value == -1 ? "N/A" : $"{MarketPrice.Value:N0} gil";
    }
}

/// <summary>Items sharing an ItemUICategory, collapsed to one row per unique item id.</summary>
public sealed class CategoryGroup
{
    public uint CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<InventoryItemInfo> Items { get; set; } = [];
}

/// <summary>
/// Severity of a safety flag on an item. Higher wins when several flags apply, so the
/// worst reason an item shouldn't be discarded is the one shown.
/// </summary>
public enum SafetyFlagColor
{
    None,
    Info,
    Caution,
    Warning,
    Critical,
}

/// <summary>The result of running an item through the safety rules.</summary>
public sealed class SafetyAssessment
{
    public uint ItemId { get; set; }
    public bool IsSafeToDiscard { get; set; }
    public List<string> SafetyFlags { get; set; } = [];
    public SafetyFlagColor FlagColor { get; set; }
}

/// <summary>How many items each safety filter is currently hiding, for the filter summary.</summary>
public sealed class FilterHiddenCounts
{
    public int InGearset { get; set; }
    public int Indisposable { get; set; }
    public int HighLevelGear { get; set; }
    public int UniqueUntradeable { get; set; }
    public int HQ { get; set; }
    public int Collectables { get; set; }
}
