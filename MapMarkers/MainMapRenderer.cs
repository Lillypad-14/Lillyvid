using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;
using VideoSyncPrototype.PlayerSearch;

namespace VideoSyncPrototype.MapMarkers;

/// <summary>
/// Draws player dots onto the large open map (AreaMap). World positions are converted into the
/// map's raw coordinate space, then projected through the live map pan/zoom values. The important
/// bit is anchoring that math to the actual map component rectangle, not the whole window.
/// </summary>
internal sealed class MainMapRenderer
{
    private const float DotRadius = 7f;
    public unsafe void Draw(IReadOnlyList<CategorizedPlayer> players)
    {
        if (players.Count == 0)
        {
            return;
        }

        var addonAddress = Plugin.GameGui.GetAddonByName("AreaMap", 1).Address;
        if (addonAddress == nint.Zero)
        {
            return;
        }

        var addon = (AddonAreaMap*)addonAddress;
        if (!addon->AtkUnitBase.IsVisible || addon->ComponentMap == null)
        {
            return;
        }

        if (!MapCoordinateConverter.TryGetCurrentMap(out var map))
        {
            return;
        }

        var component = addon->ComponentMap;
        var mapScale = component->MapScale;
        if (mapScale <= 0f || !TryGetMapRect(addon, out var mapTopLeft, out var mapSize))
        {
            return;
        }

        var addonScale = addon->AtkUnitBase.Scale;
        var zoneScale = (map.SizeFactor == 0 ? (ushort)100 : map.SizeFactor) / 100f;
        var pan = new Vector2(component->MapOffsetX, component->MapOffsetY);
        var mapCenter = mapTopLeft + (mapSize / 2f);
        var halfExtent = mapSize / 2f;
        var drawList = Dalamud.Bindings.ImGui.ImGui.GetBackgroundDrawList();

        foreach (var player in players)
        {
            if (TryProject(player.WorldPosition, map, zoneScale, pan, mapScale, addonScale, mapCenter, halfExtent, out var screen))
            {
                MinimapRenderer.DrawDot(drawList, screen, player.Color, DotRadius);
                MinimapRenderer.DrawTooltipIfHovered(screen, DotRadius, player.Name);
            }
        }

    }

    private static bool TryProject(
        Vector3 world,
        Lumina.Excel.Sheets.Map map,
        float zoneScale,
        Vector2 pan,
        float mapScale,
        float addonScale,
        Vector2 center,
        Vector2 halfExtent,
        out Vector2 screen)
    {
        var mapUnit = new Vector2(
            (world.X + map.OffsetX) * zoneScale,
            (world.Z + map.OffsetY) * zoneScale);

        screen = center + ((mapUnit - pan) * mapScale * addonScale);

        // Keep dots inside the map widget so an off projection cannot scatter them screen-wide.
        return MathF.Abs(screen.X - center.X) <= halfExtent.X &&
               MathF.Abs(screen.Y - center.Y) <= halfExtent.Y;
    }

    private static unsafe bool TryGetMapRect(AddonAreaMap* addon, out Vector2 topLeft, out Vector2 size)
    {
        topLeft = default;
        size = default;

        var component = addon->ComponentMap;
        if (component == null)
        {
            return false;
        }

        var scale = addon->AtkUnitBase.Scale;
        size = new Vector2(component->MapWidth * scale, component->MapHeight * scale);
        if (size.X <= 0f || size.Y <= 0f)
        {
            return false;
        }

        var owner = component->OwnerNode;
        topLeft = owner != null
            ? new Vector2(owner->ScreenX, owner->ScreenY)
            : new Vector2(addon->AtkUnitBase.X, addon->AtkUnitBase.Y);

        return true;
    }

}
