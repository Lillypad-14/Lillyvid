using System.Numerics;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace VideoSyncPrototype.PlayerSearch;

/// <summary>
/// Opens the in-game map and drops a flag/waypoint at a world position. Uses Dalamud's
/// <see cref="MapLinkPayload"/> so the flag lands exactly where the game's own map-link
/// system would put it, and the "open map + set flag" happens in a single call.
/// </summary>
internal static class MapFlagService
{
    /// <summary>
    /// Opens the map and flags <paramref name="world"/>. Returns false (setting nothing)
    /// when the current map or territory can't be resolved — e.g. mid-loading-screen.
    /// On success <paramref name="coordinates"/> holds the flagged map coordinates.
    /// </summary>
    public static bool TrySetFlag(Vector3 world, out Vector2 coordinates)
    {
        coordinates = default;

        if (!MapCoordinateConverter.TryGetCurrentMap(out var map))
        {
            return false;
        }

        var territoryId = Plugin.ClientState.TerritoryType;
        var mapId = Plugin.ClientState.MapId;
        if (territoryId == 0 || mapId == 0)
        {
            return false;
        }

        coordinates = MapCoordinateConverter.WorldToMapCoordinates(world, map);
        var link = new MapLinkPayload(territoryId, mapId, coordinates.X, coordinates.Y);
        Plugin.GameGui.OpenMapWithMapLink(link);
        return true;
    }
}
