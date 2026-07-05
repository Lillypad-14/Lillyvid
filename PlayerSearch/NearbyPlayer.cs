using System.Numerics;

namespace VideoSyncPrototype.PlayerSearch;

/// <summary>
/// A single player found in the local player's zone. World position is kept raw so the
/// map-coordinate conversion and flag placement stay the single source of truth — the UI
/// never has to redo the math.
/// </summary>
internal sealed record NearbyPlayer(
    string Name,
    string HomeWorld,
    nint Address,
    Vector3 WorldPosition,
    float Distance);
