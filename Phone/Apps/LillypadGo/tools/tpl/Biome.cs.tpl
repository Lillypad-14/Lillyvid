namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal enum Biome : byte
{
    Forest,
    Grassland,
    Desert,
    Coast,
    Snow,
    Volcanic,
    Cave,
    Wetland,
}

internal readonly struct SpawnEntry
{
    public SpawnEntry(string speciesId, int weight, int minLevel, int maxLevel)
    {
        SpeciesId = speciesId;
        Weight = weight;
        MinLevel = minLevel;
        MaxLevel = maxLevel;
    }

    public string SpeciesId { get; }
    public int Weight { get; }
    public int MinLevel { get; }
    public int MaxLevel { get; }
}

internal static class Biomes
{
    // A few well-known early Eorzean zones mapped by TerritoryType id. Any zone not listed
    // falls back to a deterministic biome derived from its id, so every zone has content.
    private static readonly Dictionary<uint, Biome> TerritoryMap = new()
    {
        [148] = Biome.Forest, // Central Shroud
        [152] = Biome.Forest, // East Shroud
        [153] = Biome.Forest, // South Shroud
        [154] = Biome.Forest, // North Shroud
        [134] = Biome.Coast, // Middle La Noscea
        [135] = Biome.Coast, // Lower La Noscea
        [137] = Biome.Coast, // Eastern La Noscea
        [138] = Biome.Coast, // Western La Noscea
        [139] = Biome.Wetland, // Upper La Noscea
        [180] = Biome.Volcanic, // Outer La Noscea
        [140] = Biome.Desert, // Western Thanalan
        [141] = Biome.Desert, // Central Thanalan
        [145] = Biome.Desert, // Eastern Thanalan
        [146] = Biome.Desert, // Southern Thanalan
        [147] = Biome.Grassland, // Northern Thanalan
        [155] = Biome.Snow, // Coerthas Central Highlands
        [397] = Biome.Snow, // Coerthas Western Highlands
        [156] = Biome.Wetland, // Mor Dhona
    };

    public static Biome ForTerritory(uint territoryId)
    {
        if (TerritoryMap.TryGetValue(territoryId, out var biome))
        {
            return biome;
        }

        // Deterministic fallback so unseen zones still theme consistently.
        return (Biome)(territoryId % (uint)Enum.GetValues<Biome>().Length);
    }

    public static string Name(Biome biome) => biome switch
    {
        Biome.Forest => "Woodland",
        Biome.Grassland => "Grassland",
        Biome.Desert => "Desert",
        Biome.Coast => "Coast",
        Biome.Snow => "Highlands",
        Biome.Volcanic => "Volcanic",
        Biome.Cave => "Cavern",
        Biome.Wetland => "Wetland",
        _ => "Wilds",
    };

    // Overworld fallback spawns per biome (auto-generated Kanto tables).
    private static readonly Dictionary<Biome, SpawnEntry[]> Tables = new()
    {
/*__TABLES__*/
    };

    public static SpawnEntry[] Table(Biome biome) =>
        Tables.TryGetValue(biome, out var table) ? table : Tables[Biome.Grassland];

    public static string Habitats(string speciesId)
    {
        var names = Tables.Where(pair => pair.Value.Any(entry => entry.SpeciesId == speciesId))
            .Select(pair => Name(pair.Key))
            .Distinct()
            .OrderBy(name => name)
            .ToArray();
        return names.Length == 0 ? "Unknown" : string.Join(", ", names);
    }
}
