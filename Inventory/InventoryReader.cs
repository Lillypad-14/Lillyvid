using System;
using System.Collections.Generic;
using Dalamud.Game;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace VideoSyncPrototype.Inventory;

/// <summary>
/// Reads the player's inventories out of live game memory and turns each slot into an
/// <see cref="InventoryItemInfo"/> snapshot, and runs the safety rules that decide
/// whether an item is allowed to be discarded. The implementation uses the live
/// FFXIV inventory and item metadata exposed by FFXIVClientStructs.
/// </summary>
public static class InventoryReader
{
    // The four main character inventory bags.
    private static readonly InventoryType[] MainInventories =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    // The armory chest — one container per equipment slot.
    private static readonly InventoryType[] ArmoryInventories =
    [
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
    ];

    // Regular + premium saddlebag halves.
    private static readonly InventoryType[] SaddlebagInventories =
    [
        InventoryType.SaddleBag1,
        InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1,
        InventoryType.PremiumSaddleBag2,
    ];

    public static bool IsArmoryContainer(InventoryType type) => Array.IndexOf(ArmoryInventories, type) >= 0;

    public static unsafe List<InventoryItemInfo> GetAllItems(bool includeArmory = false, bool includeSaddlebag = false)
    {
        var items = new List<InventoryItemInfo>();
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            Plugin.Log.Error("[Inventory] InventoryManager is null");
            return items;
        }

        foreach (var type in MainInventories)
        {
            AddItemsFromInventory(items, manager, type);
        }

        if (includeArmory)
        {
            foreach (var type in ArmoryInventories)
            {
                AddItemsFromInventory(items, manager, type);
            }
        }

        if (includeSaddlebag)
        {
            foreach (var type in SaddlebagInventories)
            {
                AddItemsFromInventory(items, manager, type);
            }
        }

        return items;
    }

    private static unsafe void AddItemsFromInventory(List<InventoryItemInfo> items, InventoryManager* manager, InventoryType type)
    {
        var container = manager->GetInventoryContainer(type);
        if (container == null || container->Size == 0)
        {
            return;
        }

        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0)
            {
                continue;
            }

            var info = CreateItemInfo(slot, type, (short)i);
            if (info != null)
            {
                items.Add(info);
            }
        }
    }

    private static unsafe InventoryItemInfo? CreateItemInfo(InventoryItem* slot, InventoryType container, short slotIndex)
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (itemSheet == null || !itemSheet.TryGetRow(slot->ItemId, out var row) || row.RowId == 0)
        {
            return null;
        }

        var categoryName = "Miscellaneous";
        if (row.ItemUICategory.RowId != 0 &&
            Plugin.DataManager.GetExcelSheet<ItemUICategory>() is { } categorySheet &&
            categorySheet.TryGetRow(row.ItemUICategory.RowId, out var category))
        {
            categoryName = category.Name.ExtractText();
        }

        return new InventoryItemInfo
        {
            ItemId = slot->ItemId,
            Name = row.Name.ExtractText(),
            Quantity = slot->Quantity,
            Container = container,
            Slot = slotIndex,
            IsHQ = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
            IconId = row.Icon,
            CanBeDiscarded = !row.IsIndisposable,
            CanBeTraded = !row.IsUntradable,
            IsCollectable = row.IsCollectable,
            Durability = slot->Condition,
            CategoryName = categoryName,
            ItemUICategory = row.ItemUICategory.RowId,
            ItemLevel = row.LevelItem.RowId,
            EquipLevel = row.LevelEquip,
            Rarity = row.Rarity,
            IsUnique = row.IsUnique,
            IsUntradable = row.IsUntradable,
            IsIndisposable = row.IsIndisposable,
            EquipSlotCategory = row.EquipSlotCategory.RowId,
        };
    }

    private static uint NormalizeGearsetItemId(uint raw)
    {
        if (raw >= 1000000)
        {
            return raw - 1000000;
        }

        return raw >= 500000 ? raw - 500000 : raw;
    }

    /// <summary>True if the item id is slotted into any saved gearset (HQ/glamour ids normalized).</summary>
    public static unsafe bool IsInGearset(uint itemId)
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null)
        {
            return false;
        }

        for (var i = 0; i < 100; i++)
        {
            var gearset = module->GetGearset(i);
            if (gearset == null || !gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
            {
                continue;
            }

            for (var j = 0; j < gearset->Items.Length; j++)
            {
                if (NormalizeGearsetItemId(gearset->Items[j].ItemId) == itemId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Runs the full safety rule set for a single item, collecting every reason it might
    /// not be safe to discard and the worst severity among them.
    /// </summary>
    public static SafetyAssessment AssessItemSafety(InventoryItemInfo item, InventorySettings settings, HashSet<uint> userBlacklist)
    {
        var assessment = new SafetyAssessment
        {
            ItemId = item.ItemId,
            IsSafeToDiscard = true,
        };

        void Escalate(SafetyFlagColor color)
        {
            if (assessment.FlagColor < color)
            {
                assessment.FlagColor = color;
            }
        }

        if (userBlacklist.Contains(item.ItemId))
        {
            assessment.SafetyFlags.Add("User Blacklisted");
            assessment.IsSafeToDiscard = false;
            assessment.FlagColor = SafetyFlagColor.Critical;
        }

        if (IsInGearset(item.ItemId))
        {
            assessment.SafetyFlags.Add("In Gearset");
            assessment.IsSafeToDiscard = false;
            Escalate(SafetyFlagColor.Warning);
        }

        if (item.IsIndisposable || !item.CanBeDiscarded)
        {
            assessment.SafetyFlags.Add("Cannot Be Discarded");
            assessment.IsSafeToDiscard = false;
            assessment.FlagColor = SafetyFlagColor.Critical;
        }

        if (item.IsGear && item.ItemLevel >= settings.SafetyFilters.MaxGearItemLevel)
        {
            assessment.SafetyFlags.Add($"High Level Gear (i{item.ItemLevel})");
            Escalate(SafetyFlagColor.Warning);
        }

        if (item.IsUnique && item.IsUntradable)
        {
            assessment.SafetyFlags.Add("Unique & Untradeable");
            assessment.IsSafeToDiscard = false;
            Escalate(SafetyFlagColor.Warning);
        }

        if (item.IsHQ)
        {
            assessment.SafetyFlags.Add("High Quality");
            Escalate(SafetyFlagColor.Caution);
        }

        if (item.IsCollectable)
        {
            assessment.SafetyFlags.Add("Collectable");
            Escalate(SafetyFlagColor.Info);
        }

        return assessment;
    }

    /// <summary>The hard floor: the checks that can never be bypassed by a discard action.</summary>
    public static bool IsSafeToDiscard(InventoryItemInfo item, HashSet<uint> userBlacklist)
    {
        if (userBlacklist.Contains(item.ItemId))
        {
            return false;
        }

        if (IsInGearset(item.ItemId))
        {
            return false;
        }

        if (item.IsUnique && item.IsUntradable)
        {
            return false;
        }

        return !item.IsIndisposable && item.CanBeDiscarded;
    }

    /// <summary>Fires the game's discard action for one slot. May raise a yes/no confirmation.</summary>
    public static unsafe void DiscardItem(InventoryItemInfo item)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            throw new InvalidOperationException("InventoryManager is null");
        }

        var container = manager->GetInventoryContainer(item.Container);
        if (container == null)
        {
            throw new InvalidOperationException($"Container {item.Container} not found");
        }

        var slot = container->GetInventorySlot(item.Slot);
        if (slot == null || slot->ItemId != item.ItemId)
        {
            throw new InvalidOperationException($"Item {item.Name} not found in expected slot");
        }

        var agent = AgentInventoryContext.Instance();
        if (agent == null)
        {
            throw new InvalidOperationException("AgentInventoryContext is null");
        }

        agent->DiscardItem(slot, item.Container, item.Slot, 0);
    }
}
