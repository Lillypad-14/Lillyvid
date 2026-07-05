using System.Collections.Generic;

namespace VideoSyncPrototype.Inventory;

/// <summary>
/// Item categories that are not useful in the discard browser. Destructive-action
/// safety otherwise comes from live FFXIV item metadata, gearsets, and the user's
/// explicit blacklist rather than a brittle list of item IDs.
/// </summary>
public static class ItemSafetyData
{
    /// <summary>Crystals and shards have dedicated inventory handling.</summary>
    public static readonly HashSet<uint> CrystalAndShardCategoryIds = [63u, 64u];
}
