using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using VideoSyncPrototype.PlayerSearch;

namespace VideoSyncPrototype.MapMarkers;

/// <summary>
/// Draws player dots onto the round minimap (_NaviMap). The math is ported from
/// MiniMappingway: take each player's world offset from the local player, scale it by the
/// zone / addon / minimap-zoom factors, rotate it to match the spinning compass, then clamp
/// it to the minimap's circle so distant players sit on the rim pointing the right way.
/// </summary>
internal sealed class MinimapRenderer
{
    // The minimap addon is a 218px square at scale 1; the visible circle is ~0.315 of that.
    private const float NaviMapSize = 218f;
    private const float MinimapRadiusRatio = 0.315f;
    private const float DotRadius = 6f;

    public unsafe void Draw(IReadOnlyList<CategorizedPlayer> players)
    {
        if (players.Count == 0)
        {
            return;
        }

        var addonAddress = Plugin.GameGui.GetAddonByName("_NaviMap", 1).Address;
        if (addonAddress == nint.Zero)
        {
            return;
        }

        var addon = (AtkUnitBase*)addonAddress;
        if (!addon->IsVisible || addon->RootNode == null)
        {
            return;
        }

        var local = Plugin.ObjectTable.LocalPlayer;
        if (local is null)
        {
            return;
        }

        var naviScale = addon->Scale;
        var (rotation, zoom) = ReadRotationAndZoom(addon);
        var zoneScale = GetZoneScale();

        var mapSize = NaviMapSize * naviScale;
        var center = new Vector2(addon->X + (mapSize / 2f), addon->Y + (mapSize / 2f));
        center.Y -= 5f;
        var radius = mapSize * MinimapRadiusRatio;

        var localPos = local.Position;
        var drawList = ImGui.GetBackgroundDrawList();

        foreach (var player in players)
        {
            var relative = new Vector2(localPos.X - player.WorldPosition.X, localPos.Z - player.WorldPosition.Z);
            relative *= zoneScale * naviScale * zoom;

            var dot = center - relative;
            dot = Rotate(center, dot, rotation);

            // Distant players get pinned to the rim so they still show a direction.
            var distance = Vector2.Distance(center, dot);
            if (distance > radius)
            {
                dot = center + ((dot - center) * (radius / distance));
            }

            DrawDot(drawList, dot, player.Color, DotRadius);
            DrawTooltipIfHovered(dot, DotRadius, player.Name);
        }
    }

    // Rotation lives on node 8; minimap zoom on a deep image node under node 18. Both are
    // wrapped defensively — if the UI layout ever shifts, we fall back to no-rotation / 1x
    // rather than reading a bad pointer.
    private static unsafe (float Rotation, float Zoom) ReadRotationAndZoom(AtkUnitBase* addon)
    {
        var rotation = 0f;
        var zoom = 1f;

        try
        {
            var rotationNode = addon->GetNodeById(8);
            if (rotationNode != null)
            {
                rotation = rotationNode->Rotation;
            }

            var zoomHost = addon->GetNodeById(18);
            if (zoomHost != null && (int)zoomHost->Type >= 1000)
            {
                var component = ((AtkComponentNode*)zoomHost)->Component;
                if (component != null)
                {
                    var imageNode = component->UldManager.SearchNodeById(6);
                    if (imageNode != null && imageNode->ScaleX > 0f)
                    {
                        zoom = imageNode->ScaleX;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Minimap rotation/zoom read failed; using defaults.");
        }

        return (rotation, zoom);
    }

    private static float GetZoneScale()
    {
        if (!MapCoordinateConverter.TryGetCurrentMap(out var map) || map.SizeFactor == 0)
        {
            return 1f;
        }

        return map.SizeFactor / 100f;
    }

    private static Vector2 Rotate(Vector2 center, Vector2 point, float theta)
    {
        var cos = MathF.Cos(theta);
        var sin = MathF.Sin(theta);
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        return new Vector2(
            (cos * dx) - (sin * dy) + center.X,
            (sin * dx) + (cos * dy) + center.Y);
    }

    internal static void DrawDot(ImDrawListPtr drawList, Vector2 position, Vector4 color, float radius)
    {
        drawList.AddCircleFilled(position, radius, ImGui.ColorConvertFloat4ToU32(color));
        drawList.AddCircle(position, radius, 0xFF000000u, 0, 2f);
    }

    internal static void DrawTooltipIfHovered(Vector2 position, float radius, string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        var mouse = ImGui.GetMousePos();
        var hoverRadius = radius + 5f;
        if (Vector2.DistanceSquared(mouse, position) <= hoverRadius * hoverRadius)
        {
            ImGui.SetTooltip(label);
        }
    }
}
