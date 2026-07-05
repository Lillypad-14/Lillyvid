using System.Numerics;

namespace VideoSyncPrototype.MapMarkers;

/// <summary>
/// Which relationship bucket a player falls into. Ordered by display priority — a player
/// who is both a friend and an FC member is shown as a friend.
/// </summary>
internal enum MarkerCategory
{
    Friend,
    FreeCompany,
    Everyone,
}

/// <summary>A player resolved to a display category and the color their dot should use.</summary>
internal sealed record CategorizedPlayer(string Name, Vector3 WorldPosition, MarkerCategory Category, Vector4 Color);
