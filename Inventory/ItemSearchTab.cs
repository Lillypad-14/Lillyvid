using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using VideoSyncPrototype.Windows;

namespace VideoSyncPrototype.Inventory;

/// <summary>
/// The "Item" top-level tab: search every item in the game by name (with category /
/// marketable / equippable / item-level filters), click a result to link it in chat, or
/// hit the wiki button to open it on Gamer Escape. Searches both the normal <c>Item</c>
/// sheet and the <c>EventItem</c> sheet (key/quest items). The result list is virtualised
/// so listing every item at once stays fast.
/// </summary>
internal sealed class ItemSearchTab
{
    private static readonly Vector4 ColHQ = new(0.60f, 0.80f, 1.00f, 1f);

    // Chat channel to send links to. A null command means "print to my own log only".
    private static readonly (string Name, string? Command)[] Channels = BuildChannels();

    private readonly List<GameItem> results = [];
    private List<(uint Id, string Name)>? categoryList;
    private int selectedChannel;

    private string searchText = string.Empty;
    private string lastSearch = "\0"; // force a first evaluation
    private bool listAll;
    private string status = string.Empty;

    // Filters.
    private uint filterCategory;
    private bool marketableOnly;
    private bool equippableOnly;
    private bool includeKeyItems = true;
    private int minIlvl;
    private int maxIlvl;

    private readonly record struct GameItem(uint Id, string Name, uint Icon, bool CanBeHq, bool IsEvent, string Category);

    public void Draw()
    {
        ImGui.Spacing();
        UiTheme.SectionTitle("Find any item");
        ImGui.TextDisabled("Search by name, then click a result to link it in chat (Shift-click = HQ). Press Enter on an empty box to list everything.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(320f);
        ImGui.InputTextWithHint("##item-search", "Search items...", ref this.searchText, 100);

        var enterPressed = ImGui.IsItemFocused() &&
                           (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter));

        ImGui.SameLine();
        ImGui.TextColored(UiTheme.Muted, "Send to");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        if (ImGui.BeginCombo("##item-channel", Channels[this.selectedChannel].Name))
        {
            for (var i = 0; i < Channels.Length; i++)
            {
                if (ImGui.Selectable(Channels[i].Name, i == this.selectedChannel))
                {
                    this.selectedChannel = i;
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Where clicking an item sends its link.\n\"Print to me\" only shows it in your own log.");
        }

        if (!string.IsNullOrEmpty(this.status))
        {
            ImGui.SameLine();
            ImGui.TextColored(UiTheme.Muted, this.status);
        }

        var needsRebuild = false;
        if (this.searchText != this.lastSearch)
        {
            this.lastSearch = this.searchText;
            this.listAll = false;
            needsRebuild = true;
        }
        else if (enterPressed && string.IsNullOrWhiteSpace(this.searchText))
        {
            this.listAll = true;
            needsRebuild = true;
        }

        ImGui.Spacing();
        if (UiTheme.BeginCollapsibleSection("Filters", defaultOpen: true, primary: true))
        {
            needsRebuild |= this.DrawFilters();
            ImGui.TreePop();
        }

        if (needsRebuild)
        {
            this.Rebuild();
        }

        ImGui.Spacing();
        this.DrawResults();
    }

    private bool DrawFilters()
    {
        this.EnsureCategories();
        var changed = false;

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(UiTheme.Muted, "Category");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f);
        var current = this.filterCategory == 0
            ? "All categories"
            : this.categoryList!.Find(c => c.Id == this.filterCategory).Name ?? "All categories";
        if (ImGui.BeginCombo("##item-cat", current))
        {
            if (ImGui.Selectable("All categories", this.filterCategory == 0))
            {
                this.filterCategory = 0;
                changed = true;
            }

            foreach (var (id, name) in this.categoryList!)
            {
                if (ImGui.Selectable($"{name}##cat{id}", this.filterCategory == id))
                {
                    this.filterCategory = id;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        var market = this.marketableOnly;
        if (ImGui.Checkbox("Marketable only", ref market))
        {
            this.marketableOnly = market;
            changed = true;
        }

        ImGui.SameLine();
        var equip = this.equippableOnly;
        if (ImGui.Checkbox("Equippable only", ref equip))
        {
            this.equippableOnly = equip;
            changed = true;
        }

        ImGui.SameLine();
        var keys = this.includeKeyItems;
        if (ImGui.Checkbox("Include key items", ref keys))
        {
            this.includeKeyItems = keys;
            changed = true;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(UiTheme.Muted, "Item level");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f);
        var lo = this.minIlvl;
        if (ImGui.InputInt("min##item-minilvl", ref lo, 0, 0))
        {
            this.minIlvl = Math.Max(0, lo);
            changed = true;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f);
        var hi = this.maxIlvl;
        if (ImGui.InputInt("max##item-maxilvl", ref hi, 0, 0))
        {
            this.maxIlvl = Math.Max(0, hi);
            changed = true;
        }

        if (this.filterCategory != 0 || this.marketableOnly || this.equippableOnly || this.minIlvl > 0 || this.maxIlvl > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear filters"))
            {
                this.filterCategory = 0;
                this.marketableOnly = false;
                this.equippableOnly = false;
                this.minIlvl = 0;
                this.maxIlvl = 0;
                changed = true;
            }
        }

        return changed;
    }

    private void DrawResults()
    {
        if (this.results.Count == 0)
        {
            return;
        }

        ImGui.BeginChild("##item-results", new Vector2(0f, 0f), true);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));

        var shift = ImGui.GetIO().KeyShift;
        var rowH = ImGui.GetTextLineHeight() + 8f;
        var n = this.results.Count;

        // Manual virtualisation: only draw the rows currently scrolled into view.
        var scrollY = ImGui.GetScrollY();
        var viewH = ImGui.GetWindowHeight();
        var first = Math.Clamp((int)(scrollY / rowH) - 1, 0, n);
        var last = Math.Clamp((int)((scrollY + viewH) / rowH) + 1, 0, n);

        if (first > 0)
        {
            ImGui.Dummy(new Vector2(1f, first * rowH));
        }

        for (var i = first; i < last; i++)
        {
            this.DrawRow(this.results[i], rowH, shift);
        }

        if (last < n)
        {
            ImGui.Dummy(new Vector2(1f, (n - last) * rowH));
        }

        ImGui.PopStyleVar();
        ImGui.EndChild();
    }

    private void DrawRow(GameItem item, float rowH, bool shift)
    {
        var start = ImGui.GetCursorPos();

        // The width available from the row's left edge to the right of the visible region
        // (already excludes the scrollbar). Anchoring to this — instead of the finicky
        // GetContentRegionMax — keeps the right-hand button on-screen.
        var rightEdge = start.X + ImGui.GetContentRegionAvail().X;
        var linkHq = shift && item.CanBeHq;
        var prefix = item.IsEvent ? "e" : "i";

        // A full-width selectable is the click target for the whole row. Allow overlap so
        // the wiki button drawn on top of it stays clickable.
        if (ImGui.Selectable($"##row{prefix}{item.Id}", false, ImGuiSelectableFlags.None, new Vector2(0f, rowH)))
        {
            this.SendLink(item, linkHq);
        }

        ImGui.SetItemAllowOverlap();

        if (ImGui.IsItemHovered())
        {
            var dest = Channels[this.selectedChannel].Name;
            var hint = item.CanBeHq ? $"Click → {dest}  ·  Shift-click → HQ" : $"Click → {dest}";
            ItemTooltip.Draw(item.Id, item.IsEvent, shift && item.CanBeHq, hint);
        }

        var afterRow = ImGui.GetCursorPos();
        var centerY = start.Y + ((rowH - ImGui.GetTextLineHeight()) * 0.5f);
        var btnW = rowH - 2f;
        var wikiX = rightEdge - btnW - 6f;

        // Icon + name overlaid on the selectable.
        var iconSize = rowH - 4f;
        ImGui.SetCursorPos(new Vector2(start.X + 2f, start.Y + 2f));
        var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.Icon)).GetWrapOrDefault();
        if (icon != null)
        {
            ImGui.Image(icon.Handle, new Vector2(iconSize, iconSize));
        }
        else
        {
            ImGui.Dummy(new Vector2(iconSize, iconSize));
        }

        ImGui.SameLine(0f, 6f);
        ImGui.SetCursorPosY(centerY);
        ImGui.Text(item.Name);

        if (item.CanBeHq)
        {
            ImGui.SameLine(0f, 4f);
            ImGui.TextColored(ColHQ, "HQ");
        }

        if (item.IsEvent)
        {
            ImGui.SameLine(0f, 4f);
            ImGui.TextColored(UiTheme.Muted, "[Key Item]");
        }

        // Category just left of the wiki button.
        if (!string.IsNullOrEmpty(item.Category))
        {
            var cw = ImGui.CalcTextSize(item.Category).X;
            ImGui.SetCursorPos(new Vector2(wikiX - 10f - cw, centerY));
            ImGui.TextColored(UiTheme.Muted, item.Category);
        }

        // Wiki button at the far right (drawn after the selectable so it wins the click).
        // Tighten the frame padding so the icon fits the small square (the window's default
        // padding is large and would clip it).
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(3f, 2f));
        ImGui.SetCursorPos(new Vector2(wikiX, start.Y + 1f));
        if (UiTheme.IconButton(FontAwesomeIcon.Book, $"wiki{prefix}{item.Id}", new Vector2(btnW, rowH - 2f),
                tooltip: "Open on Gamer Escape wiki"))
        {
            OpenWiki(item.Name);
        }

        ImGui.PopStyleVar();

        ImGui.SetCursorPos(afterRow);
    }

    private void EnsureCategories()
    {
        if (this.categoryList != null)
        {
            return;
        }

        this.categoryList = [];
        var sheet = Plugin.DataManager.GetExcelSheet<ItemUICategory>();
        if (sheet == null)
        {
            return;
        }

        foreach (var row in sheet)
        {
            if (row.RowId == 0)
            {
                continue;
            }

            var name = row.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
            {
                this.categoryList.Add((row.RowId, name));
            }
        }

        this.categoryList.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    private void Rebuild()
    {
        this.results.Clear();

        var term = this.searchText.Trim();
        var useName = term.Length >= 2;
        var anyFilter = this.filterCategory != 0 || this.marketableOnly || this.equippableOnly ||
                        this.minIlvl > 0 || this.maxIlvl > 0;

        if (!this.listAll && !useName && !anyFilter)
        {
            this.status = term.Length == 0
                ? "Press Enter to list everything, or pick a filter."
                : "Type at least 2 characters.";
            return;
        }

        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        var eventSheet = Plugin.DataManager.GetExcelSheet<EventItem>();
        var categorySheet = Plugin.DataManager.GetExcelSheet<ItemUICategory>();
        if (itemSheet == null)
        {
            this.status = "Item data unavailable.";
            return;
        }

        var matches = new List<GameItem>();

        foreach (var row in itemSheet)
        {
            if (row.RowId == 0)
            {
                continue;
            }

            var name = row.Name.ExtractText();
            if (string.IsNullOrEmpty(name) || (useName && !name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (this.filterCategory != 0 && row.ItemUICategory.RowId != this.filterCategory)
            {
                continue;
            }

            if (this.marketableOnly && row.ItemSearchCategory.RowId == 0)
            {
                continue;
            }

            if (this.equippableOnly && row.EquipSlotCategory.RowId == 0)
            {
                continue;
            }

            var ilvl = row.LevelItem.RowId;
            if ((this.minIlvl > 0 && ilvl < this.minIlvl) || (this.maxIlvl > 0 && ilvl > this.maxIlvl))
            {
                continue;
            }

            var category = string.Empty;
            if (categorySheet != null && row.ItemUICategory.RowId != 0 &&
                categorySheet.TryGetRow(row.ItemUICategory.RowId, out var cat))
            {
                category = cat.Name.ExtractText();
            }

            matches.Add(new GameItem(row.RowId, name, row.Icon, row.CanBeHq, false, category));
        }

        // Key/quest items (EventItem sheet) — only when they can't be excluded by an
        // item-only filter that they have no data for.
        var itemOnlyFilter = this.filterCategory != 0 || this.marketableOnly || this.equippableOnly ||
                             this.minIlvl > 0 || this.maxIlvl > 0;
        if (this.includeKeyItems && !itemOnlyFilter && eventSheet != null)
        {
            foreach (var row in eventSheet)
            {
                if (row.RowId == 0)
                {
                    continue;
                }

                var name = row.Name.ExtractText();
                if (string.IsNullOrEmpty(name) || (useName && !name.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                matches.Add(new GameItem(row.RowId, name, row.Icon, false, true, "Key Item"));
            }
        }

        if (this.listAll && !useName)
        {
            matches.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        else if (useName)
        {
            // Best matches first: names that start with the term, then shorter, then alpha.
            matches.Sort((a, b) =>
            {
                var aStarts = a.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase);
                var bStarts = b.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase);
                if (aStarts != bStarts)
                {
                    return aStarts ? -1 : 1;
                }

                if (a.Name.Length != b.Name.Length)
                {
                    return a.Name.Length - b.Name.Length;
                }

                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
        }
        else
        {
            matches.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        this.results.AddRange(matches);
        this.status = $"{matches.Count:N0} item{(matches.Count == 1 ? string.Empty : "s")}.";
    }

    private void SendLink(GameItem item, bool hq)
    {
        try
        {
            // Key/quest items live in the EventItem sheet (raw id >= 2,000,000) and link via
            // the EventItem kind; normal items use the HQ-aware overload.
            var message = item.IsEvent
                ? new SeStringBuilder().AddItemLink(item.Id, ItemKind.EventItem).Build()
                : new SeStringBuilder().AddItemLink(item.Id, hq).Build();

            var command = Channels[this.selectedChannel].Command;
            if (command == null)
            {
                // Local only — just print it in our own chat log.
                Plugin.ChatGui.Print(message);
                return;
            }

            // Prepend the channel command (e.g. "/p ") and send the encoded link so the
            // game routes it to that channel for everyone to see.
            var prefixBytes = Encoding.UTF8.GetBytes(command + " ");
            var payloadBytes = message.Encode();
            var full = new byte[prefixBytes.Length + payloadBytes.Length];
            Buffer.BlockCopy(prefixBytes, 0, full, 0, prefixBytes.Length);
            Buffer.BlockCopy(payloadBytes, 0, full, prefixBytes.Length, payloadBytes.Length);
            if (!GameChat.TrySendEncoded(full, out var error))
            {
                Plugin.ChatGui.PrintError($"[Lillypad] Couldn't send that item link: {error}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"[ItemSearch] Failed to send link for item {item.Id}");
            Plugin.ChatGui.PrintError($"[Lillypad] Couldn't send that item link: {ex.Message}");
        }
    }

    private static (string Name, string? Command)[] BuildChannels()
    {
        var channels = new List<(string, string?)>
        {
            ("Print to me", null),
            ("Say", "/s"),
            ("Yell", "/y"),
            ("Shout", "/sh"),
            ("Party", "/p"),
            ("Alliance", "/a"),
            ("Free Company", "/fc"),
        };

        for (var i = 1; i <= 8; i++)
        {
            channels.Add(($"Linkshell {i}", $"/l{i}"));
        }

        for (var i = 1; i <= 8; i++)
        {
            channels.Add(($"CWLS {i}", $"/cwlinkshell{i}"));
        }

        return [.. channels];
    }

    private static void OpenWiki(string name)
    {
        // Gamer Escape is a MediaWiki: spaces become underscores, the rest is URL-escaped
        // (underscores are unreserved so they survive EscapeDataString).
        var title = Uri.EscapeDataString(name.Trim().Replace(' ', '_'));
        Util.OpenLink($"https://ffxiv.gamerescape.com/wiki/{title}");
    }
}
