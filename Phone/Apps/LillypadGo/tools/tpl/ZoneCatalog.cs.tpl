namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal sealed class ZoneDefinition
{
    public ZoneDefinition(uint territoryId, string region, int regionOrder, string name, Biome biome,
        int progressionOrder, int minLevel, int maxLevel, string exclusiveSpeciesId, params SpawnEntry[] encounters)
    {
        TerritoryId = territoryId;
        Region = region;
        RegionOrder = regionOrder;
        Name = name;
        Biome = biome;
        ProgressionOrder = progressionOrder;
        MinLevel = minLevel;
        MaxLevel = maxLevel;
        ExclusiveSpeciesId = exclusiveSpeciesId;
        Encounters = encounters;
    }

    public uint TerritoryId { get; }
    public string Region { get; }
    public int RegionOrder { get; }
    public string Name { get; }
    public Biome Biome { get; }
    public int ProgressionOrder { get; }
    public int MinLevel { get; }
    public int MaxLevel { get; }
    public string ExclusiveSpeciesId { get; }
    public SpawnEntry[] Encounters { get; }
    public string LevelLabel => $"Lv {MinLevel}-{MaxLevel}";
}

internal static class ArrZones
{
    // Auto-generated Kanto encounter tables mapped onto ARR zone progression.
    public static readonly IReadOnlyList<ZoneDefinition> All = new[]
    {
/*__ZONES__*/
    };

    private static readonly Dictionary<uint, ZoneDefinition> ByTerritory = All.ToDictionary(zone => zone.TerritoryId);

    public static ZoneDefinition? Find(uint territoryId) =>
        ByTerritory.TryGetValue(territoryId, out var zone) ? zone : null;

    public static SpawnEntry[] Encounters(uint territoryId, Biome fallbackBiome) =>
        Find(territoryId)?.Encounters ?? Biomes.Table(fallbackBiome);

    public static string Habitats(string speciesId)
    {
        var names = All.Where(zone => zone.Encounters.Any(entry => entry.SpeciesId == speciesId))
            .Select(zone => zone.Name).Distinct().ToArray();
        return names.Length == 0 ? Biomes.Habitats(speciesId) : string.Join(", ", names);
    }

    public static bool IsExclusiveTo(ZoneDefinition zone, string speciesId) =>
        zone.ExclusiveSpeciesId.Equals(speciesId, StringComparison.OrdinalIgnoreCase);
}
