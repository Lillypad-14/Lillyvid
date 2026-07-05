using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace VideoSyncPrototype.PlayerSearch;

/// <summary>
/// Scans Dalamud's object table for other players near the local player and, optionally,
/// filters them by name. Only players the client can actually see are ever returned, so
/// the results always match what the map/flag conversion can resolve.
/// </summary>
internal sealed class PlayerScanner
{
    /// <summary>
    /// Returns visible players sorted by distance from the local player. An empty or
    /// whitespace <paramref name="nameFilter"/> lists everyone; otherwise the name is
    /// matched case-insensitively as a substring. The local player is never included.
    /// </summary>
    public IReadOnlyList<NearbyPlayer> Scan(string? nameFilter)
    {
        var results = new List<NearbyPlayer>();

        var local = Plugin.ObjectTable.LocalPlayer;
        if (local is null)
        {
            return results;
        }

        var filter = nameFilter?.Trim();
        var hasFilter = !string.IsNullOrEmpty(filter);
        var localPosition = local.Position;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter player)
            {
                continue;
            }

            // The local player shows up in the object table too — skip it so users aren't
            // told to "find" themselves.
            if (player.Address == local.Address)
            {
                continue;
            }

            var name = player.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (hasFilter && !name.Contains(filter!, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var distance = Vector3.Distance(localPosition, player.Position);
            var homeWorld = player.HomeWorld.IsValid
                ? player.HomeWorld.Value.Name.ToString()
                : string.Empty;
            results.Add(new NearbyPlayer(name, homeWorld, player.Address, player.Position, distance));
        }

        results.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        return results;
    }
}
