using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoSyncPrototype.Inventory;

/// <summary>
/// Turns the raw inventory snapshot into the discardable, filtered, category-grouped
/// view the table shows. The safety filters here are "hide from the list" filters;
/// they are distinct from the hard
/// <see cref="InventoryReader.IsSafeToDiscard"/> floor that guards the actual discard.
/// </summary>
public static class ItemFilterService
{
    public static IEnumerable<InventoryItemInfo> ApplyFilters(
        IEnumerable<InventoryItemInfo> items,
        SafetyFilters filters,
        string? searchFilter = null,
        bool equippablesOnly = false)
    {
        var source = items;

        if (!string.IsNullOrWhiteSpace(searchFilter))
        {
            source = source.Where(i => i.Name.Contains(searchFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (equippablesOnly)
        {
            source = source.Where(i => i.IsEquippable);
        }

        // Crystals and shards have dedicated game inventory handling and do not belong
        // in the general-purpose discard browser.
        source = source.Where(i => !ItemSafetyData.CrystalAndShardCategoryIds.Contains(i.ItemUICategory));

        if (filters.FilterGearsetItems)
        {
            source = source.Where(i => !InventoryReader.IsInGearset(i.ItemId));
        }

        if (filters.FilterIndisposableItems)
        {
            source = source.Where(i => !i.IsIndisposable);
        }

        if (filters.FilterHighLevelGear)
        {
            source = source.Where(i => i.EquipSlotCategory == 0 || i.ItemLevel < filters.MaxGearItemLevel);
        }

        if (filters.FilterUniqueUntradeable)
        {
            source = source.Where(i => !i.IsUnique || !i.IsUntradable);
        }

        if (filters.FilterHQItems)
        {
            source = source.Where(i => !i.IsHQ);
        }

        if (filters.FilterCollectables)
        {
            source = source.Where(i => !i.IsCollectable);
        }

        return source;
    }

    /// <summary>Counts how many items each toggleable filter is currently hiding.</summary>
    public static FilterHiddenCounts CountHiddenPerFilter(IEnumerable<InventoryItemInfo> items, SafetyFilters filters)
    {
        var counts = new FilterHiddenCounts();
        foreach (var item in items)
        {
            if (InventoryReader.IsInGearset(item.ItemId))
            {
                counts.InGearset++;
            }

            if (item.IsIndisposable)
            {
                counts.Indisposable++;
            }

            if (item.IsHQ)
            {
                counts.HQ++;
            }

            if (item.IsCollectable)
            {
                counts.Collectables++;
            }

            if (item.IsUnique && item.IsUntradable)
            {
                counts.UniqueUntradeable++;
            }

            if (item.EquipSlotCategory != 0 && item.ItemLevel >= filters.MaxGearItemLevel)
            {
                counts.HighLevelGear++;
            }
        }

        return counts;
    }

    /// <summary>
    /// Collapses stacks of the same item id (across bags) into a single row and groups
    /// those rows by ItemUICategory, both alphabetically ordered.
    /// </summary>
    public static List<CategoryGroup> GroupIntoCategories(IEnumerable<InventoryItemInfo> items)
    {
        return items
            .GroupBy(i => new { i.ItemUICategory, i.CategoryName })
            .Select(categoryGroup =>
            {
                var merged = categoryGroup
                    .GroupBy(i => i.ItemId)
                    .Select(itemGroup =>
                    {
                        var first = itemGroup.First();
                        return new InventoryItemInfo
                        {
                            ItemId = first.ItemId,
                            Name = first.Name,
                            Quantity = itemGroup.Sum(i => i.Quantity),
                            Container = first.Container,
                            Slot = first.Slot,
                            IsHQ = first.IsHQ,
                            IconId = first.IconId,
                            CanBeDiscarded = first.CanBeDiscarded,
                            CanBeTraded = first.CanBeTraded,
                            IsCollectable = first.IsCollectable,
                            Durability = first.Durability,
                            CategoryName = first.CategoryName,
                            ItemUICategory = first.ItemUICategory,
                            MarketPrice = first.MarketPrice,
                            MarketPriceFetchTime = first.MarketPriceFetchTime,
                            IsSelected = first.IsSelected,
                            ItemLevel = first.ItemLevel,
                            EquipLevel = first.EquipLevel,
                            Rarity = first.Rarity,
                            IsUnique = first.IsUnique,
                            IsUntradable = first.IsUntradable,
                            IsIndisposable = first.IsIndisposable,
                            EquipSlotCategory = first.EquipSlotCategory,
                            SafetyAssessment = first.SafetyAssessment,
                        };
                    })
                    .OrderBy(i => i.Name)
                    .ToList();

                return new CategoryGroup
                {
                    CategoryId = categoryGroup.Key.ItemUICategory,
                    Name = categoryGroup.Key.CategoryName,
                    Items = merged,
                };
            })
            .OrderBy(c => c.Name)
            .ToList();
    }
}
