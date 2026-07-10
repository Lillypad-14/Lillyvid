using System.Numerics;
using Lumina.Excel.Sheets;

namespace VideoSyncPrototype.PlayerSearch;

/// <summary>
/// Turns a world-space position (as reported by the object table) into the map
/// coordinates the player reads off the in-game map — e.g. (10.5, 11.2) — and resolves
/// the current zone's display name. Everything keys off the local player's current map,
/// which is correct because same-zone players share it.
/// </summary>
internal static class MapCoordinateConverter
{
    /// <summary>Friendly name of the zone the local player is standing in, or null if unknown.</summary>
    public static string? GetCurrentZoneName()
    {
        if (!TryGetCurrentMap(out var map))
        {
            return null;
        }

        var name = map.PlaceName.ValueNullable?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>The Lumina map row for the local player's current map, if one is loaded.</summary>
    public static bool TryGetCurrentMap(out Map map)
    {
        map = default;

        var mapId = Plugin.ClientState.MapId;
        if (mapId == 0)
        {
            return false;
        }

        var sheet = Plugin.DataManager.GetExcelSheet<Map>();
        if (sheet is null)
        {
            return false;
        }

        var row = sheet.GetRowOrDefault(mapId);
        if (row is null)
        {
            return false;
        }

        map = row.Value;
        return true;
    }

    /// <summary>Converts a world position to in-game map coordinates on the current map.</summary>
    public static bool TryWorldToMapCoordinates(Vector3 world, out Vector2 coordinates)
    {
        coordinates = default;
        if (!TryGetCurrentMap(out var map))
        {
            return false;
        }

        coordinates = WorldToMapCoordinates(world, map);
        return true;
    }

    /// <summary>
    /// The standard FFXIV world-to-map projection. Only X (east/west) and Z (north/south)
    /// matter for the flat map; Y is height and is ignored.
    /// </summary>
    public static Vector2 WorldToMapCoordinates(Vector3 world, Map map)
    {
        var x = ConvertAxis(world.X, map.OffsetX, map.SizeFactor);
        var y = ConvertAxis(world.Z, map.OffsetY, map.SizeFactor);
        return new Vector2(x, y);
    }

    private static float ConvertAxis(float world, short offset, ushort sizeFactor)
    {
        var scale = sizeFactor / 100f;
        var scaled = (world + offset) * scale;
        return ((41f / scale) * ((scaled + 1024f) / 2048f)) + 1f;
    }

    /// <summary>
    /// Inverse of <see cref="WorldToMapCoordinates"/>: turns the coordinates a player reads off
    /// the map — e.g. (10.5, 11.2) — back into a world X/Z position. Height (Y) is not encoded
    /// on the flat map, so it comes back as 0; use the result for flat distances and map pins.
    /// </summary>
    public static Vector3 MapToWorldCoordinates(Vector2 coordinates, Map map)
    {
        var x = InvertAxis(coordinates.X, map.OffsetX, map.SizeFactor);
        var z = InvertAxis(coordinates.Y, map.OffsetY, map.SizeFactor);
        return new Vector3(x, 0f, z);
    }

    private static float InvertAxis(float coordinate, short offset, ushort sizeFactor)
    {
        var scale = sizeFactor / 100f;
        var scaled = (coordinate - 1f) * scale / 41f * 2048f - 1024f;
        return scaled / scale - offset;
    }
}
