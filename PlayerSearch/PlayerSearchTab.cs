using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using VideoSyncPrototype.Windows;

namespace VideoSyncPrototype.PlayerSearch;

/// <summary>
/// The "Player Search" top-level tab: a name box, a Search button, and a list of players
/// in the local player's zone. Clicking a result opens the map and drops a flag on that
/// player. Owns only its own view state and a scanner; all game/map access is delegated to
/// <see cref="PlayerScanner"/>, <see cref="MapCoordinateConverter"/>, and
/// <see cref="MapFlagService"/> so this file stays purely presentational.
/// </summary>
internal sealed class PlayerSearchTab
{
    private readonly PlayerScanner scanner = new();
    private readonly PlayerProfileService profiles = new();
    private string searchText = string.Empty;
    private List<NearbyPlayer> results = [];
    private bool hasSearched;
    private string status = string.Empty;
    private Vector4 statusColor = UiTheme.Muted;

    public void Draw()
    {
        ImGui.Spacing();

        if (UiTheme.BeginCollapsibleSection("Show players on the map", defaultOpen: false, primary: true))
        {
            DrawMarkerSettings();
            ImGui.TreePop();
        }

        ImGui.Spacing();
        UiTheme.SectionTitle("Find players in your zone");
        ImGui.TextDisabled("Search by name, or leave it empty to list everyone nearby.");
        ImGui.Spacing();

        this.DrawSearchBar();

        if (!string.IsNullOrEmpty(this.status))
        {
            ImGui.Spacing();
            ImGui.TextColored(this.statusColor, this.status);
        }

        ImGui.Spacing();
        this.DrawResults();
    }

    // Live overlay controls: dots on the minimap / main map, filtered by relationship.
    // Every toggle writes straight to the shared config so the overlay (driven from
    // Plugin.Draw) picks it up on the next frame.
    private static void DrawMarkerSettings()
    {
        var config = Plugin.Config;

        var enabled = config.MapMarkersEnabled;
        if (ImGui.Checkbox("Show player dots on the map", ref enabled))
        {
            config.MapMarkersEnabled = enabled;
            config.Save();
        }

        if (!enabled)
        {
            ImGui.TextDisabled("Turn this on to mark nearby players on your minimap and map.");
            return;
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Where");
        var onMinimap = config.MarkersOnMinimap;
        if (ImGui.Checkbox("Minimap", ref onMinimap))
        {
            config.MarkersOnMinimap = onMinimap;
            config.Save();
        }

        ImGui.SameLine();
        var onMainMap = config.MarkersOnMainMap;
        if (ImGui.Checkbox("Main map", ref onMainMap))
        {
            config.MarkersOnMainMap = onMainMap;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Who");
        DrawCategoryRow("Friends##markers", () => config.MarkShowFriends, v => config.MarkShowFriends = v, config, nameof(config.MarkFriendColor));
        DrawCategoryRow("FC members##markers", () => config.MarkShowFcMembers, v => config.MarkShowFcMembers = v, config, nameof(config.MarkFcColor));
        DrawCategoryRow("Everyone##markers", () => config.MarkShowEveryone, v => config.MarkShowEveryone = v, config, nameof(config.MarkEveryoneColor));
    }

    private static void DrawCategoryRow(string label, Func<bool> get, Action<bool> set, Configuration config, string colorProperty)
    {
        var value = get();
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            config.Save();
        }

        // A small swatch on the right, click to recolor that category's dots.
        var color = colorProperty switch
        {
            nameof(config.MarkFriendColor) => config.MarkFriendColor,
            nameof(config.MarkFcColor) => config.MarkFcColor,
            _ => config.MarkEveryoneColor,
        };

        // Right-align the swatch. A NoInputs color widget draws a SQUARE button sized to the
        // frame height (not the item width), so we must offset by that height or it spills past
        // the edge. Pull it in an extra few px so it never touches the window border/scrollbar.
        var swatchWidth = ImGui.GetFrameHeight();
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - swatchWidth - 4f);
        if (ImGui.ColorEdit4($"##{colorProperty}", ref color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.AlphaPreview))
        {
            switch (colorProperty)
            {
                case nameof(config.MarkFriendColor):
                    config.MarkFriendColor = color;
                    break;
                case nameof(config.MarkFcColor):
                    config.MarkFcColor = color;
                    break;
                default:
                    config.MarkEveryoneColor = color;
                    break;
            }

            config.Save();
        }
    }

    private void DrawSearchBar()
    {
        const float buttonWidth = 96f;
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
        var submitted = ImGui.InputTextWithHint(
            "##playersearch-name",
            "Player name (optional)",
            ref this.searchText,
            64,
            ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        if (UiTheme.PrimaryButton("Search", new Vector2(buttonWidth, 0f)) || submitted)
        {
            this.RunSearch();
        }
    }

    private void RunSearch()
    {
        this.hasSearched = true;

        if (Plugin.ObjectTable.LocalPlayer is null)
        {
            this.results = [];
            this.SetStatus("Log in to a character to search for players.", UiTheme.Muted);
            return;
        }

        this.results = [.. this.scanner.Scan(this.searchText)];

        if (this.results.Count == 0)
        {
            this.SetStatus(
                string.IsNullOrWhiteSpace(this.searchText)
                    ? "No players found in this zone."
                    : $"No players matching \"{this.searchText.Trim()}\" in this zone.",
                UiTheme.Muted);
        }
        else
        {
            this.ClearStatus();
        }
    }

    private void DrawResults()
    {
        if (!this.hasSearched)
        {
            ImGui.TextDisabled("Press Search to see who's around you.");
            return;
        }

        if (this.results.Count == 0)
        {
            // The status line already explains why; nothing more to draw.
            return;
        }

        var zoneName = MapCoordinateConverter.GetCurrentZoneName();
        for (var i = 0; i < this.results.Count; i++)
        {
            this.DrawPlayerCard(i, this.results[i], zoneName);
        }
    }

    private void DrawPlayerCard(int index, NearbyPlayer player, string? zoneName)
    {
        var cardHeight = (ImGui.GetTextLineHeightWithSpacing() * 2f) + ImGui.GetFrameHeight() +
                         (UiTheme.CardPadding * 2f) + (ImGui.GetStyle().ItemSpacing.Y * 2f);
        if (UiTheme.BeginCard($"##playersearch-card-{index}", cardHeight))
        {
            var title = string.IsNullOrWhiteSpace(player.HomeWorld)
                ? player.Name
                : $"{player.Name}  -  {player.HomeWorld}";
            ImGui.TextColored(UiTheme.AccentHovered, title);

            var haveCoords = MapCoordinateConverter.TryWorldToMapCoordinates(player.WorldPosition, out var coords);
            var detail = zoneName ?? "Current zone";
            if (haveCoords)
            {
                detail += $"  •  ( {coords.X:0.0}, {coords.Y:0.0} )";
            }

            detail += $"  •  {player.Distance:0} yalms away";

            ImGui.TextDisabled(detail);

            // "Find" flags the player on the map. When their coordinates can't be resolved
            // (rare — e.g. a loading screen), the button explains instead of silently failing.
            ImGui.Spacing();
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            var buttonWidth = Math.Max(76f, (ImGui.GetContentRegionAvail().X - (spacing * 3f)) / 4f);
            if (UiTheme.PrimaryButton($"Find##playersearch-find-{index}", new Vector2(buttonWidth, 0f)))
            {
                this.FindPlayer(player);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Open the map and drop a flag on this player.");
            }

            ImGui.SameLine();
            if (!this.profiles.CanExamine)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button($"Examine##playersearch-examine-{index}", new Vector2(buttonWidth, 0f)))
            {
                this.ExaminePlayer(player);
            }

            if (!this.profiles.CanExamine)
            {
                ImGui.EndDisabled();
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(this.profiles.CanExamine
                    ? "Open this player's examine window."
                    : "Examine is unavailable for this game version.");
            }

            ImGui.SameLine();
            if (!this.profiles.CanOpenPlate)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button($"Plate##playersearch-plate-{index}", new Vector2(buttonWidth, 0f)))
            {
                this.OpenPlate(player);
            }

            if (!this.profiles.CanOpenPlate)
            {
                ImGui.EndDisabled();
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(this.profiles.CanOpenPlate
                    ? "Open this player's adventurer plate in game."
                    : "Adventurer plates are unavailable for this game version.");
            }

            ImGui.SameLine();
            if (ImGui.Button($"Lodestone##playersearch-lodestone-{index}", new Vector2(buttonWidth, 0f)))
            {
                this.OpenLodestone(player);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Open an exact name and home-world search on the Lodestone.");
            }
        }

        UiTheme.EndCard();
        ImGui.Spacing();
    }

    private void FindPlayer(NearbyPlayer player)
    {
        if (MapFlagService.TrySetFlag(player.WorldPosition, out var coords))
        {
            this.SetStatus($"Flag set for {player.Name} at ( {coords.X:0.0}, {coords.Y:0.0} ).", UiTheme.Live);
        }
        else
        {
            this.SetStatus($"Could not get coordinates for {player.Name}.", UiTheme.Danger);
        }
    }

    private void OpenPlate(NearbyPlayer player)
    {
        if (this.profiles.TryOpenPlate(player, out var error))
        {
            this.SetStatus($"Opened {player.Name}'s adventurer plate.", UiTheme.Live);
        }
        else
        {
            this.SetStatus(error, UiTheme.Danger);
        }
    }

    private void ExaminePlayer(NearbyPlayer player)
    {
        if (this.profiles.TryExamine(player, out var error))
        {
            this.SetStatus($"Examining {player.Name}.", UiTheme.Live);
        }
        else
        {
            this.SetStatus(error, UiTheme.Danger);
        }
    }

    private void OpenLodestone(NearbyPlayer player)
    {
        if (PlayerProfileService.TryOpenLodestone(player, out var error))
        {
            var world = string.IsNullOrWhiteSpace(player.HomeWorld) ? string.Empty : $" on {player.HomeWorld}";
            this.SetStatus($"Opened Lodestone search for {player.Name}{world}.", UiTheme.Live);
        }
        else
        {
            this.SetStatus(error, UiTheme.Danger);
        }
    }

    private void SetStatus(string message, Vector4 color)
    {
        this.status = message;
        this.statusColor = color;
    }

    private void ClearStatus()
    {
        this.status = string.Empty;
    }
}
