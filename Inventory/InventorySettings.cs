using System;
using System.Collections.Generic;

namespace VideoSyncPrototype.Inventory;

/// <summary>
/// Persisted state for the Inventory tab. Lives inside the plugin
/// <see cref="Configuration"/> so filters, the user blacklist and price preferences
/// survive a relog.
/// </summary>
[Serializable]
public sealed class InventorySettings
{
    public SafetyFilters SafetyFilters { get; set; } = new();

    /// <summary>Look up Universalis market prices for tradeable items.</summary>
    public bool ShowMarketPrices { get; set; }

    /// <summary>Only list items that can be equipped.</summary>
    public bool EquippablesOnly { get; set; }

    /// <summary>Also read the armory chest (equipment spares).</summary>
    public bool IncludeArmory { get; set; }

    /// <summary>Also read the (premium) saddlebag.</summary>
    public bool IncludeSaddlebag { get; set; }

    /// <summary>How long a fetched market price stays fresh before it is refetched.</summary>
    public int PriceCacheDurationMinutes { get; set; } = 30;

    /// <summary>Shrink the item list font so far more rows fit without scrolling.</summary>
    public bool CompactView { get; set; } = true;

    /// <summary>Items the user has personally marked as never-discard.</summary>
    public List<uint> UserBlacklist { get; set; } = [];

    /// <summary>Per-category expand/collapse memory in the item table.</summary>
    public Dictionary<uint, bool> ExpandedCategories { get; set; } = [];
}

/// <summary>
/// The toggleable protection filters. Each one hides a class of items from the discard
/// list; the hardcoded <see cref="ItemSafetyData"/> protections apply regardless.
/// </summary>
[Serializable]
public sealed class SafetyFilters
{
    public bool FilterGearsetItems { get; set; } = true;
    public bool FilterIndisposableItems { get; set; } = true;
    public bool FilterHighLevelGear { get; set; } = true;
    public bool FilterUniqueUntradeable { get; set; } = true;
    public bool FilterHQItems { get; set; }
    public bool FilterCollectables { get; set; }
    public uint MaxGearItemLevel { get; set; } = 600u;
}
