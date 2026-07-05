using System;

namespace VideoSyncPrototype.MapMarkers;

/// <summary>
/// Top-level driver for the player map-marker overlay. Called once per frame (independent of
/// the plugin window being open, like MiniMappingway): it categorizes the zone's players once
/// and hands the list to the minimap and main-map renderers as the user's toggles allow.
/// </summary>
internal sealed class MapMarkerService
{
    private readonly PlayerCategorizer categorizer = new();
    private readonly MinimapRenderer minimapRenderer = new();
    private readonly MainMapRenderer mainMapRenderer = new();

    public void Draw(Configuration config)
    {
        if (!config.MapMarkersEnabled)
        {
            return;
        }

        if (!config.MarkersOnMinimap && !config.MarkersOnMainMap)
        {
            return;
        }

        // Nothing to draw if the user turned every category off.
        if (!config.MarkShowFriends && !config.MarkShowFcMembers && !config.MarkShowEveryone)
        {
            return;
        }

        try
        {
            var players = this.categorizer.Collect(config);
            if (players.Count == 0)
            {
                return;
            }

            if (config.MarkersOnMinimap)
            {
                this.minimapRenderer.Draw(players);
            }

            if (config.MarkersOnMainMap)
            {
                this.mainMapRenderer.Draw(players);
            }
        }
        catch (Exception ex)
        {
            // A bad frame (mid-load, addon tearing down) must never take the game with it.
            Plugin.Log.Debug(ex, "Map marker draw skipped for this frame.");
        }
    }
}
