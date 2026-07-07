namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// A gym leader: a powerful trainer with a type-themed team, accessible only in their home city,
// who awards a collectable badge. The six starter gyms can be challenged in any order, but are
// listed by difficulty. Beating a gym unlocks the matching Training tier.
internal sealed class GymDef
{
    public GymDef(int index, string leader, string city, string badge, Element type, int minLevel, int maxLevel,
        uint[] territories, (string Species, int Level)[] team)
    {
        Index = index;
        Leader = leader;
        City = city;
        Badge = badge;
        Type = type;
        MinLevel = minLevel;
        MaxLevel = maxLevel;
        Territories = territories;
        Team = team;
    }

    public int Index { get; }
    public string Leader { get; }
    public string City { get; }
    public string Badge { get; }
    public Element Type { get; }
    public int MinLevel { get; }
    public int MaxLevel { get; }
    public uint[] Territories { get; }
    public (string Species, int Level)[] Team { get; }

    public string LevelLabel => $"Lv {MinLevel}-{MaxLevel}";
}

internal static class Gyms
{
    public static readonly IReadOnlyList<GymDef> All = new[]
    {
        new GymDef(0, "Merlwyb", "Limsa Lominsa", "Tide Badge", Element.Water, 10, 13,
            new uint[] { 128, 129 },
            new[] { ("poliwag", 10), ("tentacool", 11), ("staryu", 12), ("starmie", 13) }),

        new GymDef(1, "Kan-E-Senna", "Gridania", "Bloom Badge", Element.Grass, 16, 20,
            new uint[] { 132, 133 },
            new[] { ("oddish", 16), ("bellsprout", 17), ("gloom", 18), ("vileplume", 20) }),

        new GymDef(2, "Nanamo", "Ul'dah", "Quake Badge", Element.Ground, 22, 26,
            new uint[] { 130, 131 },
            new[] { ("sandshrew", 22), ("diglett", 23), ("geodude", 24), ("graveler", 25), ("dugtrio", 26) }),

        new GymDef(3, "Godbert", "The Gold Saucer", "Volt Badge", Element.Electric, 30, 34,
            new uint[] { 144 },
            new[] { ("voltorb", 30), ("magnemite", 31), ("pikachu", 32), ("electrode", 33), ("raichu", 34) }),

        new GymDef(4, "Raubahn", "The Wolves' Den", "Fist Badge", Element.Fighting, 38, 42,
            new uint[] { 250 },
            new[] { ("mankey", 38), ("machop", 39), ("machoke", 40), ("primeape", 41), ("hitmonlee", 41), ("machamp", 42) }),

        new GymDef(5, "Midgardsormr", "Mor Dhona", "Wyrm Badge", Element.Dragon, 46, 52,
            new uint[] { 156 },
            new[] { ("dratini", 46), ("abra", 46), ("dragonair", 48), ("kadabra", 49), ("alakazam", 50), ("dragonite", 52) }),
    };

    public static GymDef? ForTerritory(uint territoryId) =>
        All.FirstOrDefault(gym => Array.IndexOf(gym.Territories, territoryId) >= 0);

    public static Battle Build(GymDef gym, List<MonsterInstance> party, Bag bag, Random rng)
    {
        var team = gym.Team
            .Select(entry => new MonsterInstance(Dex.Find(entry.Species)!, entry.Level))
            .ToList();
        var prize = team.Sum(monster => monster.Level) * 20 + 500;
        return new Battle(party, team, $"Leader {gym.Leader}", prize, bag, rng);
    }
}
