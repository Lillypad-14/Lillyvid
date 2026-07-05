using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using VideoSyncPrototype.Windows;

namespace VideoSyncPrototype.Emotes;

internal sealed class EmoteRemapperTab
{
    private readonly Configuration config;
    private readonly EmoteRemapperService service;
    private string openPickerId = string.Empty;
    private string selectedRemapId = string.Empty;
    private string remapFilter = string.Empty;
    private EmoteRemapEntry? pendingPlayEntry;
    private DateTime pendingPlayUtc = DateTime.MinValue;

    public EmoteRemapperTab(Configuration config, EmoteRemapperService service)
    {
        this.config = config;
        this.service = service;
    }

    public void Draw()
    {
        if (this.pendingPlayEntry is not null && DateTime.UtcNow >= this.pendingPlayUtc)
        {
            var pending = this.pendingPlayEntry;
            this.pendingPlayEntry = null;
            this.NormalizeEntry(pending);
            this.config.Save();
            this.service.RefreshCommandHandlers();
            this.service.Execute(pending);
        }

        ImGui.Spacing();

        var enabled = this.config.EmoteRemapperEnabled;
        if (ImGui.Checkbox("Enable emote remapper", ref enabled))
        {
            this.config.EmoteRemapperEnabled = enabled;
            this.config.Save();
            this.service.RefreshCommandHandlers();
        }

        ImGui.SameLine();
        UiTheme.StatusDot(this.service.PenumbraAvailable ? UiTheme.Live : UiTheme.Danger);
        ImGui.SameLine();
        ImGui.TextDisabled(this.service.PenumbraAvailable ? $"Penumbra connected - {this.service.EmoteCommands.Count} emotes loaded" : $"Penumbra IPC unavailable - {this.service.EmoteCommands.Count} emotes loaded");

        ImGui.TextWrapped("Remap a locked or modded emote onto an emote you own. Optional trigger commands must be custom commands like /mydance.");
        ImGui.Spacing();

        if (UiTheme.PrimaryButton("Align to target", new Vector2(150f, 0f)))
        {
            this.service.AlignToTarget();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Target a nearby player, stand within 2 yalms, then align your position and facing for duo or group emotes.");
        }

        ImGui.SameLine();
        ImGui.TextDisabled("For duo and group emotes");
        ImGui.Spacing();

        if (UiTheme.PrimaryButton("Add remap", new Vector2(120f, 0f)))
        {
            var entry = new EmoteRemapEntry();
            this.config.EmoteRemaps.Add(entry);
            this.selectedRemapId = entry.Id;
            this.config.Save();
            this.service.RefreshCommandHandlers();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{this.config.EmoteRemaps.Count} saved");

        ImGui.Spacing();
        if (UiTheme.BeginCollapsibleSection("Saved remaps", defaultOpen: true, primary: true))
        {
            this.DrawSavedRemaps();
            ImGui.TreePop();
        }

        ImGui.Spacing();
        if (UiTheme.BeginCollapsibleSection("Active swap", defaultOpen: true))
        {
            UiTheme.StatusDot(UiTheme.Accent);
            ImGui.SameLine();
            ImGui.TextWrapped(this.service.Status);
            ImGui.Spacing();
            if (ImGui.Button("Clear active swap", new Vector2(150f, 0f)))
            {
                this.service.ClearSwap();
            }

            ImGui.TreePop();
        }
    }

    private void DrawSavedRemaps()
    {
        if (this.config.EmoteRemaps.Count == 0)
        {
            ImGui.TextDisabled("No remaps yet.");
            return;
        }

        var selectedEntry = this.config.EmoteRemaps.Find(entry => entry.Id == this.selectedRemapId);
        if (selectedEntry is null)
        {
            selectedEntry = this.config.EmoteRemaps[0];
            this.selectedRemapId = selectedEntry.Id;
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##emote-remap-filter", "Filter saved remaps...", ref this.remapFilter, 80);
        ImGui.Spacing();

        var visibleCount = 0;
        foreach (var entry in this.config.EmoteRemaps)
        {
            if (MatchesRemapFilter(entry, this.remapFilter))
            {
                visibleCount++;
            }
        }

        var rowHeight = (ImGui.GetTextLineHeightWithSpacing() * 2f) + 8f;
        var listHeight = Math.Clamp(visibleCount, 2, 7) * rowHeight;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, UiTheme.CardBg);
        ImGui.PushStyleColor(ImGuiCol.Border, UiTheme.CardBorder);
        if (ImGui.BeginChild("##emote-remap-list", new Vector2(0f, listHeight), true, ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            var drawList = ImGui.GetWindowDrawList();
            foreach (var entry in this.config.EmoteRemaps)
            {
                if (!MatchesRemapFilter(entry, this.remapFilter))
                {
                    continue;
                }

                ImGui.PushID(entry.Id);
                var selected = entry.Id == this.selectedRemapId;
                if (ImGui.Selectable("##select-remap", selected, ImGuiSelectableFlags.None, new Vector2(0f, rowHeight)))
                {
                    this.selectedRemapId = entry.Id;
                    selectedEntry = entry;
                    this.openPickerId = string.Empty;
                }

                var rowMin = ImGui.GetItemRectMin();
                var dotColor = entry.Enabled ? UiTheme.Live : UiTheme.Idle;
                drawList.AddCircleFilled(
                    rowMin + new Vector2(8f, (ImGui.GetTextLineHeight() * 0.55f) + 4f),
                    3.5f,
                    ImGui.ColorConvertFloat4ToU32(dotColor));

                var name = string.IsNullOrWhiteSpace(entry.Name) ? "Unnamed remap" : entry.Name;
                drawList.AddText(rowMin + new Vector2(18f, 3f), ImGui.GetColorU32(ImGuiCol.Text), name);

                var carrier = entry.AutoCarrier ? "auto carrier" : entry.CarrierEmoteCommand;
                var summary = $"{entry.TargetEmoteCommand}  ->  {carrier}";
                drawList.AddText(
                    rowMin + new Vector2(18f, ImGui.GetTextLineHeightWithSpacing() + 3f),
                    ImGui.ColorConvertFloat4ToU32(UiTheme.Muted),
                    summary);
                ImGui.PopID();
            }

            if (visibleCount == 0)
            {
                ImGui.TextDisabled("No remaps match that filter.");
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleColor(2);

        var selectedIndex = this.config.EmoteRemaps.IndexOf(selectedEntry);
        if (UiTheme.DangerButton("Delete selected", new Vector2(140f, 0f)))
        {
            this.config.EmoteRemaps.RemoveAt(selectedIndex);
            this.selectedRemapId = this.config.EmoteRemaps.Count > 0
                ? this.config.EmoteRemaps[Math.Min(selectedIndex, this.config.EmoteRemaps.Count - 1)].Id
                : string.Empty;
            this.config.Save();
            this.service.RefreshCommandHandlers();
            return;
        }

        ImGui.Spacing();
        UiTheme.SectionTitle("Edit selected remap");
        ImGui.Spacing();

        ImGui.PushID(selectedEntry.Id);
        this.DrawRemapEditor(selectedEntry, selectedIndex);
        ImGui.PopID();
    }

    private static bool MatchesRemapFilter(EmoteRemapEntry entry, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.TargetEmoteCommand.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.CarrierEmoteCommand.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.TriggerCommand.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void DrawRemapEditor(EmoteRemapEntry entry, int index)
    {
        var saveChanged = false;
        var commandHandlersChanged = false;

        var rowEnabled = entry.Enabled;
        if (ImGui.Checkbox("Enabled", ref rowEnabled))
        {
            entry.Enabled = rowEnabled;
            saveChanged = true;
            commandHandlersChanged = true;
        }

        var name = entry.Name;
        if (InputText("Name", $"##emote-name-{index}", ref name, 80, "Gold Dance via Dance"))
        {
            entry.Name = name;
            saveChanged = true;
        }

        var target = entry.TargetEmoteCommand;
        if (this.EmoteCommandPicker("Modded / locked emote", $"emote-target-{index}", ref target, "/golddance"))
        {
            entry.TargetEmoteCommand = target;
            saveChanged = true;
            this.service.NotifyTargetChanged();
        }

        var carrier = entry.CarrierEmoteCommand;
        var autoCarrier = entry.AutoCarrier;
        if (ImGui.Checkbox("I don't have this emote (use an owned carrier)", ref autoCarrier))
        {
            entry.AutoCarrier = autoCarrier;
            saveChanged = true;
        }

        if (entry.AutoCarrier)
        {
            ImGui.BeginDisabled();
        }

        if (this.EmoteCommandPicker("Carrier emote you own", $"emote-carrier-{index}", ref carrier, "/dance"))
        {
            entry.CarrierEmoteCommand = carrier;
            saveChanged = true;
        }

        if (entry.AutoCarrier)
        {
            ImGui.EndDisabled();
        }

        var trigger = entry.TriggerCommand;
        if (InputText("Optional trigger command", $"##emote-trigger-{index}", ref trigger, 64, "/mydance"))
        {
            entry.TriggerCommand = trigger;
            saveChanged = true;
            commandHandlersChanged = true;
        }

        ImGui.Spacing();
        if (UiTheme.PrimaryButton("Play", new Vector2(92f, 0f)))
        {
            // Let the text input finish its deactivation frame before reading the entry.
            this.pendingPlayEntry = entry;
            this.pendingPlayUtc = DateTime.UtcNow.AddMilliseconds(50);
        }

        ImGui.SameLine();
        if (ImGui.Button("Save", new Vector2(92f, 0f)))
        {
            this.NormalizeEntry(entry);
            this.config.Save();
            this.service.RefreshCommandHandlers();
        }

        if (saveChanged)
        {
            this.config.Save();
            if (commandHandlersChanged)
            {
                this.service.RefreshCommandHandlers();
            }
        }
    }

    private static bool InputText(string label, string id, ref string value, int maxLength, string hint)
    {
        ImGui.TextDisabled(label);
        ImGui.SetNextItemWidth(-1f);
        return ImGui.InputTextWithHint(id, hint, ref value, maxLength);
    }

    private bool EmoteCommandPicker(string label, string id, ref string value, string hint)
    {
        var changed = false;
        ImGui.TextDisabled(label);

        const float pickerButtonWidth = 34f;
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - pickerButtonWidth - ImGui.GetStyle().ItemSpacing.X));
        if (ImGui.InputTextWithHint($"##{id}", hint, ref value, 64))
        {
            changed = true;
            this.openPickerId = id;
        }

        if (ImGui.IsItemActivated() || ImGui.IsItemFocused())
        {
            this.openPickerId = id;
        }

        ImGui.SameLine();
        if (ImGui.Button($"v##{id}-picker", new Vector2(pickerButtonWidth, 0f)))
        {
            this.openPickerId = this.openPickerId == id ? string.Empty : id;
        }

        if (this.openPickerId != id)
        {
            return changed;
        }

        var matchCount = 0;
        var needle = value.Trim().TrimStart('/').ToLowerInvariant();
        var childHeight = Math.Min(10, Math.Max(4, this.service.EmoteCommands.Count)) * ImGui.GetFrameHeightWithSpacing();
        ImGui.PushStyleColor(ImGuiCol.ChildBg, UiTheme.CardBg);
        ImGui.PushStyleColor(ImGuiCol.Border, UiTheme.CardBorder);
        if (ImGui.BeginChild($"##{id}-matches", new Vector2(0f, childHeight), true, ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            foreach (var command in this.service.EmoteCommands)
            {
                if (!IsCommandMatch(command, needle))
                {
                    continue;
                }

                if (ImGui.Selectable($"{command}##{id}-{matchCount}", string.Equals(value, command, StringComparison.OrdinalIgnoreCase)))
                {
                    value = command;
                    this.openPickerId = string.Empty;
                    changed = true;
                }

                matchCount++;
                if (matchCount >= 30)
                {
                    break;
                }
            }

            if (matchCount == 0)
            {
                ImGui.TextDisabled("No matching emote commands.");
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        return changed;
    }

    private static bool IsCommandMatch(string command, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
        {
            return true;
        }

        var bareCommand = command.TrimStart('/');
        return bareCommand.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ||
               bareCommand.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private void NormalizeEntry(EmoteRemapEntry entry)
    {
        entry.TargetEmoteCommand = EmoteRemapperService.NormalizeCommand(entry.TargetEmoteCommand);
        entry.CarrierEmoteCommand = EmoteRemapperService.NormalizeCommand(entry.CarrierEmoteCommand);
        entry.TriggerCommand = EmoteRemapperService.NormalizeCommand(entry.TriggerCommand);
        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            entry.Name = $"{entry.TargetEmoteCommand} via {entry.CarrierEmoteCommand}";
        }
    }
}
