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
        new ZoneDefinition(134, "La Noscea", 0, "Middle La Noscea", Biome.Coast, 0, 4, 7, "magikarp",
            new SpawnEntry("rattata", 34, 4, 7), new SpawnEntry("pidgey", 30, 4, 7), new SpawnEntry("spearow", 20, 4, 7), new SpawnEntry("magikarp", 24, 4, 7)),
        new ZoneDefinition(148, "The Black Shroud", 1, "Central Shroud", Biome.Forest, 1, 4, 7, "metapod",
            new SpawnEntry("caterpie", 34, 4, 7), new SpawnEntry("weedle", 30, 4, 7), new SpawnEntry("pidgey", 22, 4, 7), new SpawnEntry("metapod", 18, 4, 7)),
        new ZoneDefinition(141, "Thanalan", 2, "Central Thanalan", Biome.Desert, 2, 4, 7, "sandshrew",
            new SpawnEntry("rattata", 34, 4, 7), new SpawnEntry("spearow", 28, 4, 7), new SpawnEntry("sandshrew", 22, 4, 7), new SpawnEntry("geodude", 16, 4, 7)),
        new ZoneDefinition(135, "La Noscea", 0, "Lower La Noscea", Biome.Coast, 3, 5, 12, "goldeen",
            new SpawnEntry("slowpoke", 34, 5, 12), new SpawnEntry("squirtle", 28, 5, 12), new SpawnEntry("pikachu", 22, 5, 12), new SpawnEntry("psyduck", 16, 5, 12), new SpawnEntry("goldeen", 22, 5, 12)),
        new ZoneDefinition(140, "Thanalan", 2, "Western Thanalan", Biome.Desert, 4, 6, 14, "cubone",
            new SpawnEntry("rhyhorn", 34, 6, 14), new SpawnEntry("growlithe", 28, 6, 14), new SpawnEntry("zubat", 24, 6, 14), new SpawnEntry("omanyte", 20, 6, 14), new SpawnEntry("kabuto", 14, 6, 14), new SpawnEntry("cubone", 22, 6, 14)),
        new ZoneDefinition(152, "The Black Shroud", 1, "East Shroud", Biome.Forest, 5, 8, 16, "exeggcute",
            new SpawnEntry("pidgeotto", 34, 8, 16), new SpawnEntry("farfetchd", 28, 8, 16), new SpawnEntry("lickitung", 22, 8, 16), new SpawnEntry("weepinbell", 16, 8, 16), new SpawnEntry("exeggcute", 22, 8, 16)),
        new ZoneDefinition(138, "La Noscea", 0, "Western La Noscea", Biome.Coast, 6, 10, 18, "omanyte",
            new SpawnEntry("farfetchd", 34, 10, 18), new SpawnEntry("poliwhirl", 28, 10, 18), new SpawnEntry("butterfree", 22, 10, 18), new SpawnEntry("wartortle", 16, 10, 18), new SpawnEntry("omanyte", 22, 10, 18)),
        new ZoneDefinition(145, "Thanalan", 2, "Eastern Thanalan", Biome.Desert, 7, 12, 22, "dugtrio",
            new SpawnEntry("charmeleon", 34, 12, 22), new SpawnEntry("ponyta", 28, 12, 22), new SpawnEntry("graveler", 22, 12, 22), new SpawnEntry("onix", 16, 12, 22), new SpawnEntry("dugtrio", 22, 12, 22)),
        new ZoneDefinition(153, "The Black Shroud", 1, "South Shroud", Biome.Wetland, 8, 18, 28, "arbok",
            new SpawnEntry("seadra", 34, 18, 28), new SpawnEntry("ivysaur", 28, 18, 28), new SpawnEntry("wartortle", 22, 18, 28), new SpawnEntry("haunter", 16, 18, 28), new SpawnEntry("arbok", 22, 18, 28)),
        new ZoneDefinition(139, "La Noscea", 0, "Upper La Noscea", Biome.Wetland, 9, 20, 30, "golbat",
            new SpawnEntry("arbok", 34, 20, 30), new SpawnEntry("venomoth", 28, 20, 30), new SpawnEntry("seaking", 22, 20, 30), new SpawnEntry("seadra", 16, 20, 30), new SpawnEntry("golbat", 22, 20, 30)),
        new ZoneDefinition(154, "The Black Shroud", 1, "North Shroud", Biome.Forest, 10, 24, 34, "vileplume",
            new SpawnEntry("dodrio", 34, 24, 34), new SpawnEntry("pidgeot", 28, 24, 34), new SpawnEntry("venomoth", 22, 24, 34), new SpawnEntry("chansey", 16, 24, 34), new SpawnEntry("vileplume", 22, 24, 34)),
        new ZoneDefinition(146, "Thanalan", 2, "Southern Thanalan", Biome.Desert, 11, 25, 38, "rhydon",
            new SpawnEntry("golem", 34, 25, 38), new SpawnEntry("magmar", 28, 25, 38), new SpawnEntry("omastar", 22, 25, 38), new SpawnEntry("kabutops", 16, 25, 38), new SpawnEntry("rhydon", 22, 25, 38)),
        new ZoneDefinition(137, "La Noscea", 0, "Eastern La Noscea", Biome.Coast, 12, 28, 38, "cloyster",
            new SpawnEntry("tentacruel", 34, 28, 38), new SpawnEntry("aerodactyl", 28, 28, 38), new SpawnEntry("poliwrath", 22, 28, 38), new SpawnEntry("starmie", 16, 28, 38), new SpawnEntry("cloyster", 22, 28, 38)),
        new ZoneDefinition(180, "La Noscea", 0, "Outer La Noscea", Biome.Volcanic, 13, 34, 43, "rapidash",
            new SpawnEntry("charizard", 34, 34, 43), new SpawnEntry("flareon", 28, 34, 43), new SpawnEntry("arcanine", 22, 34, 43), new SpawnEntry("ninetales", 16, 34, 43), new SpawnEntry("rapidash", 22, 34, 43)),
        new ZoneDefinition(147, "Thanalan", 2, "Northern Thanalan", Biome.Volcanic, 14, 38, 46, "dragonite",
            new SpawnEntry("arcanine", 34, 38, 46), new SpawnEntry("charizard", 28, 38, 46), new SpawnEntry("moltres", 22, 38, 46), new SpawnEntry("flareon", 16, 38, 46), new SpawnEntry("dragonite", 22, 38, 46)),
        new ZoneDefinition(155, "Coerthas", 3, "Coerthas Central Highlands", Biome.Snow, 15, 40, 48, "jynx",
            new SpawnEntry("articuno", 34, 40, 48), new SpawnEntry("lapras", 28, 40, 48), new SpawnEntry("cloyster", 22, 40, 48), new SpawnEntry("dewgong", 16, 40, 48), new SpawnEntry("jynx", 22, 40, 48)),
        new ZoneDefinition(156, "Mor Dhona", 4, "Mor Dhona", Biome.Wetland, 16, 44, 50, "cloyster",
            new SpawnEntry("gyarados", 34, 44, 50), new SpawnEntry("lapras", 28, 44, 50), new SpawnEntry("blastoise", 22, 44, 50), new SpawnEntry("venusaur", 16, 44, 50), new SpawnEntry("cloyster", 22, 44, 50)),
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
