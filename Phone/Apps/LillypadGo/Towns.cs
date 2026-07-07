namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// Aetheryte-city territories where the Marketboard is available. In town you can shop and heal,
// but wild Pokémon never appear — towns are safe hubs, just like the mainline games.
internal static class Towns
{
    private static readonly HashSet<uint> TownTerritories = new()
    {
        128, 129, // Limsa Lominsa (Upper/Lower Decks)
        130, 131, // Ul'dah (Steps of Nald/Thal)
        132, 133, // Gridania (New/Old)
        144,      // The Gold Saucer (gym city; heal before the Volt Badge)
        250,      // The Wolves' Den Pier (gym city; heal before the Fist Badge)
        418, 419, // Ishgard (Foundation/Pillars)
        478,      // Idyllshire
        628,      // Kugane
        635,      // Rhalgr's Reach
        819,      // The Crystarium
        820,      // Eulmore
        962,      // Old Sharlayan
        963,      // Radz-at-Han
    };

    public static bool IsTown(uint territoryId) => TownTerritories.Contains(territoryId);
}
