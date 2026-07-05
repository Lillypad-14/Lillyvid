using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Client.Game;
using VideoSyncPrototype.Windows;

namespace VideoSyncPrototype.Inventory;

/// <summary>
/// The "Inventory" top-level tab: a toolbar, a DISPLAY/FILTERS sidebar, collapsible
/// category lists with optional Universalis prices, and a guarded bulk-discard flow.
///
/// Owns its own view state and a small set of services; all game access is delegated
/// to <see cref="InventoryReader"/>, <see cref="ItemFilterService"/>,
/// <see cref="MarketPriceService"/> and <see cref="DiscardScheduler"/>.
/// </summary>
internal sealed class InventoryTab : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    // Inventory-specific accents used for prices, metadata, and HQ labels.
    private static readonly Vector4 ColBlue = new(0.30f, 0.70f, 1.00f, 1f);
    private static readonly Vector4 ColPrice = new(1.00f, 0.80f, 0.20f, 1f);
    private static readonly Vector4 ColInfo = new(0.70f, 0.70f, 0.70f, 1f);
    private static readonly Vector4 ColSubdued = new(0.60f, 0.60f, 0.60f, 1f);
    private static readonly Vector4 ColHQ = new(0.60f, 0.80f, 1.00f, 1f);

    private readonly InventorySettings settings;
    private readonly MarketPriceService priceService;
    private readonly DiscardScheduler discardScheduler = new();
    private readonly HashSet<uint> selected = [];

    private HashSet<uint> blacklist;
    private List<InventoryItemInfo> rawItems = [];
    private List<CategoryGroup> categories = [];
    private FilterHiddenCounts hiddenCounts = new();
    private string searchText = string.Empty;
    private DateTime lastRefresh = DateTime.MinValue;
    private bool viewDirty = true;
    private bool confirmOpen;
    private bool pendingConfirm;
    private SortKey sortColumn = SortKey.None;
    private bool sortAscending;

    private enum SortKey
    {
        None,
        Quantity,
        ItemLevel,
        Price,
        Total,
    }

    private readonly record struct InventoryRowLayout(
        float Width,
        float ItemWidth,
        float QtyX,
        float QtyWidth,
        float ItemLevelX,
        float ItemLevelWidth,
        float LocationX,
        float LocationWidth,
        float PriceX,
        float PriceWidth,
        float ServerX,
        float ServerWidth,
        float TotalX,
        float TotalWidth)
    {
        public static InventoryRowLayout Create(float availableWidth, bool showPrices)
        {
            const float qtyWidth = 40f;
            const float itemLevelWidth = 40f;
            const float locationWidth = 104f;
            const float priceWidth = 72f;
            const float serverWidth = 84f;
            const float totalWidth = 72f;

            var fixedWidth = qtyWidth + itemLevelWidth + locationWidth;
            if (showPrices)
            {
                fixedWidth += priceWidth + serverWidth + totalWidth;
            }

            var itemWidth = Math.Max(150f, availableWidth - fixedWidth);
            var width = Math.Max(availableWidth, itemWidth + fixedWidth);
            var qtyX = itemWidth;
            var itemLevelX = qtyX + qtyWidth;
            var locationX = itemLevelX + itemLevelWidth;
            var priceX = locationX + locationWidth;
            var serverX = priceX + priceWidth;
            var totalX = serverX + serverWidth;
            return new InventoryRowLayout(
                width,
                itemWidth,
                qtyX,
                qtyWidth,
                itemLevelX,
                itemLevelWidth,
                locationX,
                locationWidth,
                priceX,
                priceWidth,
                serverX,
                serverWidth,
                totalX,
                totalWidth);
        }
    }

    public InventoryTab(Configuration config)
    {
        this.settings = config.Inventory;
        this.blacklist = [.. this.settings.UserBlacklist];
        // Plugin construction can happen on Dalamud's worker thread. ObjectTable is
        // framework-thread-only, so resolve the active world during Draw/TickRefresh.
        this.priceService = new MarketPriceService(this.settings, string.Empty);
    }

    public void Draw()
    {
        this.TickRefresh();
        this.TickPrices();

        this.DrawToolbar();

        var statusHeight = ImGui.GetFrameHeightWithSpacing();
        ImGui.BeginChild("##inv-body", new Vector2(0f, -statusHeight), false);

        ImGui.BeginChild("##inv-sidebar", new Vector2(198f, 0f), true);
        this.DrawSidebar();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##inv-main", new Vector2(0f, 0f), false);
        if (this.selected.Count > 0 || this.discardScheduler.IsRunning)
        {
            this.DrawSelectionBar();
        }

        ImGui.BeginChild("##inv-cats", new Vector2(0f, 0f), false);
        if (this.settings.CompactView)
        {
            ImGui.SetWindowFontScale(0.85f);
        }

        this.DrawCategories();
        ImGui.SetWindowFontScale(1f);
        ImGui.EndChild();
        ImGui.EndChild();

        ImGui.EndChild();

        this.DrawStatusBar();
        this.DrawConfirmPopup();
    }

    // ---- Per-frame ticks -------------------------------------------------------------

    private void TickRefresh()
    {
        if (!this.viewDirty && DateTime.Now - this.lastRefresh < RefreshInterval)
        {
            return;
        }

        this.lastRefresh = DateTime.Now;
        this.viewDirty = false;
        this.priceService.UpdateScope(GetMarketScope());
        this.RebuildView();
    }

    private void RebuildView()
    {
        this.rawItems = InventoryReader.GetAllItems(this.settings.IncludeArmory, this.settings.IncludeSaddlebag);

        var filtered = ItemFilterService.ApplyFilters(
            this.rawItems,
            this.settings.SafetyFilters,
            this.searchText,
            this.settings.EquippablesOnly);

        this.categories = ItemFilterService.GroupIntoCategories(filtered);
        foreach (var item in this.categories.SelectMany(c => c.Items))
        {
            item.SafetyAssessment = InventoryReader.AssessItemSafety(item, this.settings, this.blacklist);
            this.priceService.UpdateItemPrice(item);
        }

        this.hiddenCounts = ItemFilterService.CountHiddenPerFilter(this.rawItems, this.settings.SafetyFilters);
    }

    private void TickPrices()
    {
        if (!this.settings.ShowMarketPrices)
        {
            return;
        }

        var visible = this.categories.SelectMany(c => c.Items);
        foreach (var item in this.priceService.GetItemsNeedingPriceFetch(visible))
        {
            _ = this.priceService.FetchPrice(item);
        }
    }

    // ---- Toolbar ---------------------------------------------------------------------

    private void DrawToolbar()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.145f, 0.145f, 0.145f, 1f));
        ImGui.BeginChild("##inv-toolbar", new Vector2(0f, ImGui.GetFrameHeight() + 12f), true, ImGuiWindowFlags.NoScrollbar);

        ImGui.AlignTextToFramePadding();
        Icon(FontAwesomeIcon.Search, ColSubdued);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f);
        if (ImGui.InputTextWithHint("##inv-search", "Search items", ref this.searchText, 100))
        {
            this.viewDirty = true;
        }

        if (!string.IsNullOrWhiteSpace(this.searchText))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("×##inv-clear-search"))
            {
                this.searchText = string.Empty;
                this.viewDirty = true;
            }
        }

        ImGui.SameLine();
        if (UiTheme.IconButton(FontAwesomeIcon.SyncAlt, "inv-refresh", tooltip: "Refresh inventory"))
        {
            this.viewDirty = true;
        }

        ImGui.SameLine();
        if (UiTheme.IconButton(FontAwesomeIcon.Suitcase, "inv-equip", primary: this.settings.EquippablesOnly,
                tooltip: this.settings.EquippablesOnly
                    ? "Equippables only: ON — gear, weapons and accessories.\nClick to show all items."
                    : "Equippables only: OFF — all items.\nClick to show only equippable items."))
        {
            this.settings.EquippablesOnly = !this.settings.EquippablesOnly;
            this.viewDirty = true;
            this.Save();
        }

        if (this.settings.ShowMarketPrices)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.3f, 0.3f, 0.3f, 1f), "|");
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ColSubdued, "DC:");
            ImGui.SameLine();
            var scope = GetMarketScope();
            ImGui.TextColored(ColInfo, string.IsNullOrEmpty(scope) ? "—" : scope);
        }

        // Right-aligned total value of everything currently listed.
        if (this.settings.ShowMarketPrices)
        {
            var total = this.categories.SelectMany(c => c.Items)
                .Where(i => i.MarketPrice is > 0)
                .Sum(i => i.MarketPrice!.Value * i.Quantity);
            var text = $"{total:N0} gil";
            var width = ImGui.CalcTextSize(text).X + ImGui.GetFrameHeight() + 8f;
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - width);
            ImGui.AlignTextToFramePadding();
            Icon(FontAwesomeIcon.Coins, ColPrice);
            ImGui.SameLine(0f, 4f);
            ImGui.TextColored(ColPrice, text);
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    // ---- Sidebar ---------------------------------------------------------------------

    private void DrawSidebar()
    {
        ImGui.TextColored(ColBlue, "DISPLAY");
        ImGui.Separator();
        ImGui.Spacing();

        this.SidebarToggle("Include armory", this.settings.IncludeArmory, v => this.settings.IncludeArmory = v, refresh: true);
        this.SidebarToggle("Include saddlebag", this.settings.IncludeSaddlebag, v => this.settings.IncludeSaddlebag = v, refresh: true);
        this.SidebarToggle("Equippables only", this.settings.EquippablesOnly, v => this.settings.EquippablesOnly = v, refresh: true,
            tooltip: "Show only equipment-slot items (weapons, armor, accessories).");
        this.SidebarToggle("Show market prices", this.settings.ShowMarketPrices, v => this.settings.ShowMarketPrices = v);
        this.SidebarToggle("Compact list", this.settings.CompactView, v => this.settings.CompactView = v,
            tooltip: "Shrink the list font so many more rows fit without scrolling.");

        ImGui.Spacing();
        ImGui.Spacing();

        var filters = this.settings.SafetyFilters;
        ImGui.TextColored(ColBlue, "FILTERS");
        ImGui.SameLine();
        ImGui.TextColored(ColSubdued, $"{CountActiveFilters(filters)} / 6 active");
        ImGui.Separator();
        ImGui.Spacing();

        this.FilterCheckbox("In Gearset", filters.FilterGearsetItems, v => filters.FilterGearsetItems = v,
            "Equipment in any saved gearset", this.hiddenCounts.InGearset);
        this.FilterCheckbox("Indisposable", filters.FilterIndisposableItems, v => filters.FilterIndisposableItems = v,
            "Items the game won't let you discard", this.hiddenCounts.Indisposable);
        this.FilterCheckbox("HQ Items", filters.FilterHQItems, v => filters.FilterHQItems = v,
            "High-Quality flagged items", this.hiddenCounts.HQ);
        this.FilterCheckbox("Collectables", filters.FilterCollectables, v => filters.FilterCollectables = v,
            "Collectability turn-in items", this.hiddenCounts.Collectables);
        this.FilterCheckbox("Unique & Untradeable", filters.FilterUniqueUntradeable, v => filters.FilterUniqueUntradeable = v,
            "Cannot be reacquired or sold", this.hiddenCounts.UniqueUntradeable);

        var highLevel = filters.FilterHighLevelGear;
        if (ImGui.Checkbox("##inv-highlvl", ref highLevel))
        {
            filters.FilterHighLevelGear = highLevel;
            this.viewDirty = true;
            this.Save();
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.Text("High-Level Gear");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(52f);
        var maxIlvl = (int)filters.MaxGearItemLevel;
        if (ImGui.InputInt("##inv-maxilvl", ref maxIlvl, 0, 0))
        {
            filters.MaxGearItemLevel = (uint)Math.Clamp(maxIlvl, 1, 999);
            this.viewDirty = true;
            this.Save();
        }

        // Pin Reset / All on to the bottom of the sidebar.
        var bottom = ImGui.GetWindowHeight() - ImGui.GetFrameHeight() - 12f;
        if (ImGui.GetCursorPosY() < bottom)
        {
            ImGui.SetCursorPosY(bottom);
        }

        ImGui.Separator();
        var half = (ImGui.GetContentRegionAvail().X - 6f) / 2f;
        if (ImGui.Button("Reset", new Vector2(half, 0f)))
        {
            this.settings.SafetyFilters = new SafetyFilters
            {
                FilterGearsetItems = true,
                FilterIndisposableItems = true,
                FilterHQItems = true,
                FilterCollectables = true,
                FilterUniqueUntradeable = true,
                FilterHighLevelGear = false,
            };
            this.viewDirty = true;
            this.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("All on", new Vector2(half, 0f)))
        {
            var f = this.settings.SafetyFilters;
            f.FilterGearsetItems = f.FilterIndisposableItems = f.FilterHQItems =
                f.FilterCollectables = f.FilterUniqueUntradeable = f.FilterHighLevelGear = true;
            this.viewDirty = true;
            this.Save();
        }
    }

    private void SidebarToggle(string label, bool value, Action<bool> setter, bool refresh = false, string? tooltip = null)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            setter(value);
            if (refresh)
            {
                this.viewDirty = true;
            }

            this.Save();
        }

        if (tooltip != null && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }
    }

    private void FilterCheckbox(string label, bool value, Action<bool> setter, string tooltip, int count)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            setter(value);
            this.viewDirty = true;
            this.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }

        if (count > 0)
        {
            var text = count.ToString();
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(text).X - 4f);
            ImGui.TextColored(ColSubdued, text);
        }
    }

    // ---- Selection action bar --------------------------------------------------------

    private void DrawSelectionBar()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.165f, 0.165f, 0.165f, 1f));
        ImGui.BeginChild("##inv-selbar", new Vector2(0f, ImGui.GetFrameHeight() + 10f), true, ImGuiWindowFlags.NoScrollbar);

        if (this.discardScheduler.IsRunning)
        {
            this.DrawDiscardProgress();
            ImGui.EndChild();
            ImGui.PopStyleColor();
            return;
        }

        var selectedItems = this.categories.SelectMany(c => c.Items)
            .Where(i => this.selected.Contains(i.ItemId))
            .ToList();
        var value = selectedItems.Where(i => i.MarketPrice is > 0).Sum(i => i.MarketPrice!.Value * i.Quantity);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ColBlue, $"{selectedItems.Count} selected");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.3f, 0.3f, 0.3f, 1f), "|");
        ImGui.SameLine();
        ImGui.TextColored(ColPrice, $"{value:N0} gil");

        const float buttonArea = 90f + 130f + 90f + 16f;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - buttonArea));

        if (UiTheme.QuietButton("Clear", new Vector2(90f, 0f)))
        {
            this.selected.Clear();
        }

        ImGui.SameLine();
        if (UiTheme.QuietButton("Add to blacklist", new Vector2(130f, 0f)))
        {
            foreach (var id in this.selected)
            {
                if (!this.settings.UserBlacklist.Contains(id))
                {
                    this.settings.UserBlacklist.Add(id);
                }
            }

            this.blacklist = [.. this.settings.UserBlacklist];
            this.selected.Clear();
            this.viewDirty = true;
            this.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Add the selected items to your blacklist (they'll never be discarded).");
        }

        ImGui.SameLine();
        if (UiTheme.DangerButton("Discard", new Vector2(90f, 0f)))
        {
            // Defer the actual OpenPopup to DrawConfirmPopup: OpenPopup must share the ID
            // stack with BeginPopupModal, and that runs at the root window scope — calling
            // it here (inside this child window) hashes a different id and never opens.
            this.pendingConfirm = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Discard the selected items now (with confirmation).");
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    // ---- Categories + item tables ----------------------------------------------------

    private void DrawCategories()
    {
        if (this.categories.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(ColSubdued, "No items match the current filters.");
            return;
        }

        var showPrices = this.settings.ShowMarketPrices;
        foreach (var category in this.categories)
        {
            if (category.Items.Count == 0)
            {
                continue;
            }

            var open = this.settings.ExpandedCategories.GetValueOrDefault(category.CategoryId, true);
            var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.AllowItemOverlap;
            if (open)
            {
                flags |= ImGuiTreeNodeFlags.DefaultOpen;
            }

            var nowOpen = ImGui.TreeNodeEx($"{category.Name}###inv-cat{category.CategoryId}", flags);

            ImGui.SameLine();
            var totalQty = category.Items.Sum(i => i.Quantity);
            ImGui.TextColored(ColInfo, $"({category.Items.Count} items, {totalQty} total)");

            if (showPrices)
            {
                var catValue = category.Items.Where(i => i.MarketPrice is > 0).Sum(i => i.MarketPrice!.Value * i.Quantity);
                if (catValue > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ColPrice, $"{catValue:N0} gil");
                }
            }

            // Right-aligned select-all toggle for the category.
            ImGui.SameLine(ImGui.GetContentRegionMax().X - 92f);
            var allSelected = category.Items
                .Where(i => i.SafetyAssessment?.IsSafeToDiscard ?? false)
                .All(i => this.selected.Contains(i.ItemId));
            var anySelectable = category.Items.Any(i => i.SafetyAssessment?.IsSafeToDiscard ?? false);
            if (anySelectable && ImGui.SmallButton((allSelected ? "Deselect All" : "Select All") + $"##sel{category.CategoryId}"))
            {
                foreach (var i in category.Items.Where(i => i.SafetyAssessment?.IsSafeToDiscard ?? false))
                {
                    if (allSelected)
                    {
                        this.selected.Remove(i.ItemId);
                    }
                    else
                    {
                        this.selected.Add(i.ItemId);
                    }
                }
            }

            this.settings.ExpandedCategories[category.CategoryId] = nowOpen;
            if (nowOpen)
            {
                this.DrawCategoryTable(category, showPrices);
                ImGui.Spacing();
                ImGui.TreePop();
            }
        }
    }

    private void DrawCategoryTable(CategoryGroup category, bool showPrices)
    {
        var layout = InventoryRowLayout.Create(ImGui.GetContentRegionAvail().X, showPrices);
        this.DrawPlainHeader(layout, showPrices);

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));
        var rowHeight = ImGui.GetTextLineHeight() + 8f;
        foreach (var item in this.SortItems(category.Items))
        {
            this.DrawPlainItemRow(category.CategoryId, item, layout, rowHeight, showPrices);
        }

        ImGui.PopStyleVar();
    }

    private void DrawPlainHeader(InventoryRowLayout layout, bool showPrices)
    {
        var start = ImGui.GetCursorPos();
        var screen = ImGui.GetCursorScreenPos();
        var height = ImGui.GetTextLineHeight() + 6f;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(screen, screen + new Vector2(layout.Width, height), ImGui.GetColorU32(ImGuiCol.TableHeaderBg));

        var y = screen.Y + ((height - ImGui.GetTextLineHeight()) * 0.5f);
        draw.AddText(new Vector2(screen.X + 8f, y), ImGui.GetColorU32(ImGuiCol.Text), "Item");
        DrawCenteredText(draw, "Qty", screen.X + layout.QtyX, layout.QtyWidth, y, ImGui.GetColorU32(ImGuiCol.Text));
        DrawCenteredText(draw, "iLvl", screen.X + layout.ItemLevelX, layout.ItemLevelWidth, y, ImGui.GetColorU32(ImGuiCol.Text));
        DrawCenteredText(draw, "Location", screen.X + layout.LocationX, layout.LocationWidth, y, ImGui.GetColorU32(ImGuiCol.Text));
        if (showPrices)
        {
            DrawCenteredText(draw, "Price", screen.X + layout.PriceX, layout.PriceWidth, y, ImGui.GetColorU32(ImGuiCol.Text));
            DrawCenteredText(draw, "Server", screen.X + layout.ServerX, layout.ServerWidth, y, ImGui.GetColorU32(ImGuiCol.Text));
            DrawCenteredText(draw, "Total", screen.X + layout.TotalX, layout.TotalWidth, y, ImGui.GetColorU32(ImGuiCol.Text));
        }

        ImGui.Dummy(new Vector2(layout.Width, height));
        this.DrawSortHitTarget(start, layout.QtyX, layout.QtyWidth, height, "qty", SortKey.Quantity);
        this.DrawSortHitTarget(start, layout.ItemLevelX, layout.ItemLevelWidth, height, "ilvl", SortKey.ItemLevel);
        if (showPrices)
        {
            this.DrawSortHitTarget(start, layout.PriceX, layout.PriceWidth, height, "price", SortKey.Price);
            this.DrawSortHitTarget(start, layout.TotalX, layout.TotalWidth, height, "total", SortKey.Total);
        }
    }

    private void DrawSortHitTarget(Vector2 headerStart, float x, float width, float height, string id, SortKey key)
    {
        var afterHeader = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(headerStart.X + x, headerStart.Y));
        if (ImGui.InvisibleButton($"##inv-sort-{id}", new Vector2(width, height)))
        {
            if (this.sortColumn == key)
            {
                this.sortAscending = !this.sortAscending;
            }
            else
            {
                this.sortColumn = key;
                this.sortAscending = false;
            }
        }

        ImGui.SetCursorPos(afterHeader);
    }

    private void DrawPlainItemRow(uint categoryId, InventoryItemInfo item, InventoryRowLayout layout, float rowHeight, bool showPrices)
    {
        var start = ImGui.GetCursorPos();
        var screen = ImGui.GetCursorScreenPos();
        var selected = this.selected.Contains(item.ItemId);
        var canDiscard = item.SafetyAssessment?.IsSafeToDiscard ?? false;
        if (ImGui.Selectable($"##inv-row-{categoryId}-{item.ItemId}", selected, ImGuiSelectableFlags.None, new Vector2(layout.Width, rowHeight)) && canDiscard)
        {
            if (selected)
            {
                this.selected.Remove(item.ItemId);
            }
            else
            {
                this.selected.Add(item.ItemId);
            }
        }

        ImGui.SetItemAllowOverlap();
        if (ImGui.IsItemHovered())
        {
            var footer = item.SafetyAssessment is { SafetyFlags.Count: > 0 } assessment
                ? string.Join("\n", assessment.SafetyFlags)
                : null;
            ItemTooltip.Draw(item.ItemId, false, item.IsHQ, footer);
        }

        var afterRow = ImGui.GetCursorPos();
        var draw = ImGui.GetWindowDrawList();
        var iconSize = rowHeight - 4f;
        var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.IconId)).GetWrapOrDefault();
        ImGui.SetCursorPos(new Vector2(start.X + 2f, start.Y + 2f));
        if (icon != null)
        {
            ImGui.Image(icon.Handle, new Vector2(iconSize, iconSize));
        }
        else
        {
            ImGui.Dummy(new Vector2(iconSize, iconSize));
        }

        var textY = screen.Y + ((rowHeight - ImGui.GetTextLineHeight()) * 0.5f);
        var itemTextX = screen.X + iconSize + 8f;
        draw.PushClipRect(new Vector2(screen.X, screen.Y), new Vector2(screen.X + layout.ItemWidth, screen.Y + rowHeight), true);
        draw.AddText(new Vector2(itemTextX, textY), ImGui.GetColorU32(ImGuiCol.Text), item.Name);
        var tagX = itemTextX + ImGui.CalcTextSize(item.Name).X + 4f;
        if (item.IsHQ)
        {
            draw.AddText(new Vector2(tagX, textY), ImGui.ColorConvertFloat4ToU32(ColHQ), "HQ");
            tagX += ImGui.CalcTextSize("HQ").X + 4f;
        }

        if (!item.CanBeTraded)
        {
            draw.AddText(new Vector2(tagX, textY), ImGui.ColorConvertFloat4ToU32(ColSubdued), "[Not Tradeable]");
        }

        draw.PopClipRect();

        DrawCenteredText(draw, item.Quantity.ToString("N0"), screen.X + layout.QtyX, layout.QtyWidth, textY, ImGui.GetColorU32(ImGuiCol.Text));
        DrawCenteredText(draw, item.ItemLevel > 0 ? item.ItemLevel.ToString() : "-", screen.X + layout.ItemLevelX, layout.ItemLevelWidth, textY, ImGui.GetColorU32(ImGuiCol.Text));
        DrawCenteredText(draw, GetLocationName(item.Container), screen.X + layout.LocationX, layout.LocationWidth, textY, ImGui.ColorConvertFloat4ToU32(ColSubdued));

        if (showPrices)
        {
            var price = !item.CanBeTraded ? "Untradable" : item.MarketPrice switch
            {
                null => this.priceService.IsFetchingPrice(item.ItemId) ? "..." : "Loading",
                <= 0 => "No data",
                _ => $"{item.MarketPrice.Value:N0}g",
            };
            var priceColor = item.MarketPrice is > 0 ? ImGui.ColorConvertFloat4ToU32(ColPrice) : ImGui.ColorConvertFloat4ToU32(ColSubdued);
            DrawCenteredText(draw, price, screen.X + layout.PriceX, layout.PriceWidth, textY, priceColor);

            var server = item.CanBeTraded && !string.IsNullOrEmpty(item.MarketWorld) ? item.MarketWorld : "-";
            DrawCenteredText(draw, server, screen.X + layout.ServerX, layout.ServerWidth, textY, ImGui.ColorConvertFloat4ToU32(ColSubdued));

            var total = item.CanBeTraded && item.MarketPrice is > 0 ? $"{item.MarketPrice.Value * item.Quantity:N0}g" : "-";
            DrawCenteredText(draw, total, screen.X + layout.TotalX, layout.TotalWidth, textY, ImGui.GetColorU32(ImGuiCol.Text));
        }

        ImGui.SetCursorPos(afterRow);
    }

    private static void DrawCenteredText(ImDrawListPtr draw, string text, float x, float width, float y, uint color)
    {
        var textWidth = ImGui.CalcTextSize(text).X;
        draw.AddText(new Vector2(x + Math.Max(3f, (width - textWidth) * 0.5f), y), color, text);
    }

    // Custom header row so Qty/iLvl/Price/Total are click-to-sort with a direction arrow;
    // the sort is shared across every category so all tables reorder together.
    private void DrawHeaderRow(bool showPrices)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        ImGui.TableNextColumn();
        ImGui.TableHeader("Item");

        ImGui.TableNextColumn();
        this.SortHeader("Qty", SortKey.Quantity);

        ImGui.TableNextColumn();
        this.SortHeader("iLvl", SortKey.ItemLevel);

        ImGui.TableNextColumn();
        ImGui.TableHeader("Location");

        if (showPrices)
        {
            ImGui.TableNextColumn();
            this.SortHeader("Price", SortKey.Price);

            ImGui.TableNextColumn();
            ImGui.TableHeader("Server");

            ImGui.TableNextColumn();
            this.SortHeader("Total", SortKey.Total);
        }
    }

    private void SortHeader(string label, SortKey key)
    {
        ImGui.TableHeader(label);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            if (this.sortColumn == key)
            {
                this.sortAscending = !this.sortAscending;
            }
            else
            {
                this.sortColumn = key;
                this.sortAscending = false; // first click = highest first
            }
        }

        if (this.sortColumn != key)
        {
            return;
        }

        // Font-independent sort arrow drawn straight onto the header cell.
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        var cx = max.X - 9f;
        var cy = (min.Y + max.Y) * 0.5f;
        const float r = 3.5f;
        var col = ImGui.GetColorU32(ImGuiCol.Text);
        if (this.sortAscending)
        {
            dl.AddTriangleFilled(new Vector2(cx, cy - r), new Vector2(cx - r, cy + r), new Vector2(cx + r, cy + r), col);
        }
        else
        {
            dl.AddTriangleFilled(new Vector2(cx - r, cy - r), new Vector2(cx + r, cy - r), new Vector2(cx, cy + r), col);
        }
    }

    private List<InventoryItemInfo> SortItems(List<InventoryItemInfo> items)
    {
        IEnumerable<InventoryItemInfo> Ordered<TKey>(Func<InventoryItemInfo, TKey> selector) =>
            this.sortAscending ? items.OrderBy(selector) : items.OrderByDescending(selector);

        return this.sortColumn switch
        {
            SortKey.Quantity => Ordered(i => i.Quantity).ToList(),
            SortKey.ItemLevel => Ordered(i => i.ItemLevel).ToList(),
            SortKey.Price => this.SortByValue(items, i => i.MarketPrice ?? 0),
            SortKey.Total => this.SortByValue(items, i => (i.MarketPrice ?? 0) * i.Quantity),
            _ => items,
        };
    }

    // Price/Total sorts keep priced items together (ordered by value) and push
    // untradeable / no-data items to the bottom regardless of direction.
    private List<InventoryItemInfo> SortByValue(List<InventoryItemInfo> items, Func<InventoryItemInfo, long> selector)
    {
        var priced = items.Where(i => i.MarketPrice is > 0);
        var unpriced = items.Where(i => i.MarketPrice is not > 0).OrderBy(i => i.Name);
        var ordered = this.sortAscending ? priced.OrderBy(selector) : priced.OrderByDescending(selector);
        return ordered.Concat(unpriced).ToList();
    }

    private void DrawPriceCell(InventoryItemInfo item)
    {
        if (!item.CanBeTraded)
        {
            ImGui.TextColored(ColSubdued, "Untradable");
            return;
        }

        if (!item.MarketPrice.HasValue)
        {
            ImGui.TextColored(ColSubdued, this.priceService.IsFetchingPrice(item.ItemId) ? "..." : "Loading");
            return;
        }

        if (item.MarketPrice.Value <= 0)
        {
            ImGui.TextColored(ColSubdued, "No data");
            return;
        }

        ImGui.TextColored(ColPrice, $"{item.MarketPrice.Value:N0}g");
    }

    private static void DrawTotalCell(InventoryItemInfo item)
    {
        if (!item.CanBeTraded)
        {
            ImGui.TextColored(ColSubdued, "—");
            return;
        }

        if (item.MarketPrice is > 0)
        {
            ImGui.Text($"{item.MarketPrice.Value * item.Quantity:N0}g");
        }
        else if (item.MarketPrice.HasValue)
        {
            ImGui.TextColored(ColSubdued, "N/A");
        }
        else
        {
            ImGui.TextColored(ColSubdued, "—");
        }
    }

    private static void DrawItemNameCell(InventoryItemInfo item, float rowHeight)
    {
        var start = ImGui.GetCursorPos();
        var iconSize = rowHeight - 4f;
        var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.IconId)).GetWrapOrDefault();
        ImGui.SetCursorPos(new Vector2(start.X + 2f, start.Y + 2f));
        if (icon != null)
        {
            ImGui.Image(icon.Handle, new Vector2(iconSize, iconSize));
        }
        else
        {
            ImGui.Dummy(new Vector2(iconSize, iconSize));
        }

        ImGui.SameLine(0f, 6f);
        ImGui.SetCursorPosY(start.Y + ((rowHeight - ImGui.GetTextLineHeight()) * 0.5f));
        ImGui.Text(item.Name);

        if (item.IsHQ)
        {
            ImGui.SameLine(0f, 3f);
            ImGui.TextColored(ColHQ, "[HQ]");
        }

        if (!item.CanBeTraded)
        {
            ImGui.SameLine(0f, 3f);
            ImGui.TextColored(ColSubdued, "[Not Tradeable]");
        }

        ImGui.SetCursorPosY(start.Y + rowHeight);
    }

    private static void CenterTableCell(float rowHeight)
    {
        ImGui.SetCursorPosY(
            ImGui.GetCursorPosY() + Math.Max(0f, (rowHeight - ImGui.GetTextLineHeight()) * 0.5f));
    }

    // ---- Status bar ------------------------------------------------------------------

    private void DrawStatusBar()
    {
        ImGui.Separator();
        var itemCount = this.categories.Sum(c => c.Items.Count);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ColSubdued, $"{itemCount} items · {this.categories.Count} categories");

        var right = $"Blacklist {this.settings.UserBlacklist.Count}";
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(right).X);
        ImGui.TextColored(ColSubdued, right);
    }

    // ---- Discard ---------------------------------------------------------------------

    private void DrawDiscardProgress()
    {
        var progress = this.discardScheduler.Total == 0
            ? 0f
            : this.discardScheduler.Progress / (float)this.discardScheduler.Total;

        ImGui.AlignTextToFramePadding();
        ImGui.Text($"Discarding {this.discardScheduler.Progress}/{this.discardScheduler.Total}");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f);
        UiTheme.PushSliderAccent();
        ImGui.ProgressBar(progress, new Vector2(180f, ImGui.GetFrameHeight()), string.Empty);
        UiTheme.PopSliderAccent();

        ImGui.SameLine();
        if (UiTheme.QuietButton("Cancel"))
        {
            this.discardScheduler.Cancel();
        }

        if (this.discardScheduler.Error != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(UiTheme.Danger, this.discardScheduler.Error);
        }
    }

    private void DrawConfirmPopup()
    {
        if (this.pendingConfirm)
        {
            this.pendingConfirm = false;
            this.confirmOpen = true;
            ImGui.OpenPopup("##inv-discard-confirm");
        }

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(380f, 0f), ImGuiCond.Appearing);

        if (!ImGui.BeginPopupModal("##inv-discard-confirm", ref this.confirmOpen, ImGuiWindowFlags.NoResize))
        {
            return;
        }

        var toDiscard = this.ResolveDiscardTargets();

        UiTheme.SectionTitle("Confirm discard");
        ImGui.Spacing();
        ImGui.TextWrapped($"This will permanently discard {toDiscard.Count} item stack(s). This cannot be undone.");
        ImGui.Spacing();

        ImGui.BeginChild("##confirm-list", new Vector2(0f, 160f), true);
        foreach (var group in toDiscard.GroupBy(i => i.Name).OrderBy(g => g.Key))
        {
            ImGui.BulletText($"{group.Key} x{group.Sum(i => i.Quantity)}");
        }

        ImGui.EndChild();
        ImGui.Spacing();

        if (UiTheme.DangerButton("Discard", new Vector2(150f, 0f)))
        {
            this.discardScheduler.Start(toDiscard, this.blacklist);
            this.selected.Clear();
            this.confirmOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (UiTheme.QuietButton("Cancel", new Vector2(150f, 0f)))
        {
            this.confirmOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    /// <summary>Expands the selected merged rows to every real, still-safe slot to discard.</summary>
    private List<InventoryItemInfo> ResolveDiscardTargets()
    {
        return this.rawItems
            .Where(i => this.selected.Contains(i.ItemId) && InventoryReader.IsSafeToDiscard(i, this.blacklist))
            .ToList();
    }

    // ---- Helpers ---------------------------------------------------------------------

    private static void Icon(FontAwesomeIcon icon, Vector4 color)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(color, icon.ToIconString());
        ImGui.PopFont();
    }

    private static int CountActiveFilters(SafetyFilters f)
    {
        var n = 0;
        if (f.FilterGearsetItems) n++;
        if (f.FilterIndisposableItems) n++;
        if (f.FilterHighLevelGear) n++;
        if (f.FilterUniqueUntradeable) n++;
        if (f.FilterHQItems) n++;
        if (f.FilterCollectables) n++;
        return n;
    }

    private static Vector4 FlagColor(SafetyFlagColor color) => color switch
    {
        SafetyFlagColor.Critical => UiTheme.Danger,
        SafetyFlagColor.Warning => new Vector4(0.90f, 0.55f, 0.25f, 1f),
        SafetyFlagColor.Caution => UiTheme.Accent,
        SafetyFlagColor.Info => UiTheme.Live,
        _ => UiTheme.Muted,
    };

    private static string GetLocationName(InventoryType container) => (int)container switch
    {
        0 => "Inventory (1)",
        1 => "Inventory (2)",
        2 => "Inventory (3)",
        3 => "Inventory (4)",
        3200 => "Armory (Off Hand)",
        3201 => "Armory (Head)",
        3202 => "Armory (Body)",
        3203 => "Armory (Hands)",
        3205 => "Armory (Legs)",
        3206 => "Armory (Feet)",
        3207 => "Armory (Earrings)",
        3208 => "Armory (Necklace)",
        3209 => "Armory (Bracelets)",
        3300 => "Armory (Rings)",
        3500 => "Armory (Main Hand)",
        4000 => "Saddlebag (1)",
        4001 => "Saddlebag (2)",
        4100 => "P.Saddlebag (1)",
        4101 => "P.Saddlebag (2)",
        _ => container.ToString(),
    };

    /// <summary>
    /// The Universalis query scope: the player's data-centre, so prices span every world
    /// on the DC and each reports which server the cheapest listing is on.
    /// </summary>
    private static string GetMarketScope()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return string.Empty;
        }

        var world = player.CurrentWorld.ValueNullable;
        if (world == null)
        {
            return string.Empty;
        }

        var dc = world.Value.DataCenter.ValueNullable?.Name.ExtractText();
        return !string.IsNullOrEmpty(dc) ? dc : world.Value.Name.ExtractText();
    }

    private void Save() => Plugin.Config.Save();

    public void Dispose()
    {
        this.discardScheduler.Dispose();
        this.priceService.Dispose();
    }
}
