using System.Numerics;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using VideoSyncPrototype.PlayerSearch;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// The one battle modifier every Alpha carries. Each trait is implemented in Battle
// (Swift/Regenerator/IronHide) or MonsterInstance.RecomputeStats (Frenzied/Ancient).
internal enum AlphaTrait : byte
{
    Frenzied,    // +30% Attack and Sp. Atk
    Ancient,     // huge HP pool
    Swift,       // always moves first
    Regenerator, // heals every turn
    IronHide,    // starts battle with a damage shield
}

// A weighted entry in an Alpha's spoils table.
internal readonly record struct AlphaDrop(string ItemId, int MinCount, int MaxCount, int Weight);

// A rolled, resolved spoil ready to display on the result screen and grant in FinishBattle.
internal sealed record AlphaLoot(string Label, ItemDef? Item, TmDef? Tm, int Count);

// One region's Alpha: a fixed, oversized boss that always haunts the same territory. Alphas are
// never catchable and respawn a few hours after being defeated, so they read as landmarks
// ("if I need Alpha Sandshrew, I know exactly where to go") rather than random encounters.
internal sealed class AlphaDef
{
    public AlphaDef(string id, string speciesId, int level, uint territoryId, Vector2 mapCoords, string lair,
        string lore, AlphaTrait trait, BattleWeather weather, AlphaDrop[] drops, string[] tmMoveIds)
    {
        Id = id;
        SpeciesId = speciesId;
        Level = level;
        TerritoryId = territoryId;
        MapCoords = mapCoords;
        Lair = lair;
        Lore = lore;
        Trait = trait;
        Weather = weather;
        Drops = drops;
        TmMoveIds = tmMoveIds;
    }

    public string Id { get; }
    public string SpeciesId { get; }
    public int Level { get; }
    public uint TerritoryId { get; }

    // Where the boss physically dens, as in-game map coordinates — the number pair players read
    // off the map, e.g. (18.9, 20.5). The lair card flags this spot and gates the challenge on it.
    public Vector2 MapCoords { get; }
    public string CoordsLabel => $"({MapCoords.X:0.0}, {MapCoords.Y:0.0})";

    // Short landmark name ("The Collapsed Mine") and a one-line description of the lair.
    public string Lair { get; }
    public string Lore { get; }
    public AlphaTrait Trait { get; }

    // The weather that rules the lair, carried into the fight to match the boss (a sandstorm for
    // the burrower, rain for the sea serpent, ...). BattleWeather.None means no themed weather, so
    // the zone's live weather is used instead.
    public BattleWeather Weather { get; }
    public AlphaDrop[] Drops { get; }

    // Rare TM spoils (Showdown move ids); invalid/unowned ids are filtered at roll time.
    public string[] TmMoveIds { get; }

    public MonsterSpecies? Species => Dex.Find(SpeciesId);
    public string DisplayName => $"Alpha {Species?.Name ?? SpeciesId}";
    public string Region => ArrZones.Find(TerritoryId)?.Region ?? "Unknown";
    public string ZoneName => ArrZones.Find(TerritoryId)?.Name ?? "Unknown";
}

// The Alpha roster (exactly one per region) plus their trait presentation and loot rolls.
internal static class Alphas
{
    // How long a defeated Alpha stays gone before it reclaims its territory.
    public static readonly TimeSpan RespawnTime = TimeSpan.FromHours(3);

    // Alphas draw visibly larger than a normal battler (sprite, shadow, map/dex portraits).
    public const float BattleScale = 1.6f;

    public static readonly IReadOnlyList<AlphaDef> All = new[]
    {
        new AlphaDef("alpha-sandshrew", "sandshrew", 22, 141, new Vector2(18.9f, 20.5f),
            "The Collapsed Mine", "A giant Sandshrew dens in a caved-in mineshaft, armoured in scarred plates.",
            AlphaTrait.IronHide, BattleWeather.Sandstorm,
            new[]
            {
                new AlphaDrop("greatball", 2, 4, 30),
                new AlphaDrop("superpotion", 2, 3, 30),
                new AlphaDrop("softsand", 1, 1, 20),
                new AlphaDrop("moonstone", 1, 1, 20),
            },
            new[] { "dig", "bulldoze", "swordsdance" }),
        new AlphaDef("alpha-onix", "onix", 28, 152, new Vector2(17.6f, 26.8f),
            "The Sunken Tunnel", "A massive Onix lies coiled through a collapsed tunnel deep beneath the Shroud.",
            AlphaTrait.Ancient, BattleWeather.Sandstorm,
            new[]
            {
                new AlphaDrop("ultraball", 2, 3, 30),
                new AlphaDrop("hyperpotion", 1, 2, 30),
                new AlphaDrop("hardstone", 1, 1, 20),
                new AlphaDrop("stoneplate", 1, 1, 15),
                new AlphaDrop("leafstone", 1, 1, 15),
            },
            new[] { "rockslide", "stoneedge", "earthquake" }),
        new AlphaDef("alpha-gyarados", "gyarados", 33, 138, new Vector2(17.2f, 33.4f),
            "The Wrecking Shallows", "An enraged Gyarados churns the shipwreck shallows, attacking anything that nears.",
            AlphaTrait.Frenzied, BattleWeather.Rain,
            new[]
            {
                new AlphaDrop("ultraball", 2, 4, 30),
                new AlphaDrop("mysticwater", 1, 1, 20),
                new AlphaDrop("splashplate", 1, 1, 15),
                new AlphaDrop("waterstone", 1, 1, 20),
                new AlphaDrop("hyperpotion", 2, 3, 25),
            },
            new[] { "waterfall", "surf", "icebeam" }),
        new AlphaDef("alpha-dragonite", "dragonite", 62, 155, new Vector2(11.8f, 16.5f),
            "The Storm-Wracked Cliff", "A Dragonite circles an isolated highland cliff, wreathed in blizzard and lightning.",
            AlphaTrait.Swift, BattleWeather.Snow,
            new[]
            {
                new AlphaDrop("dragonfang", 1, 1, 25),
                new AlphaDrop("dracoplate", 1, 1, 15),
                new AlphaDrop("thunderstone", 1, 1, 20),
                new AlphaDrop("fullheal", 2, 3, 25),
                new AlphaDrop("ultraball", 3, 5, 25),
            },
            new[] { "dragonclaw", "outrage", "thunderbolt" }),
        new AlphaDef("alpha-mewtwo", "mewtwo", 68, 156, new Vector2(27.0f, 13.5f),
            "The Crystal Scar", "A presence beyond nature broods among the crystals, mending its wounds as fast as they are dealt.",
            AlphaTrait.Regenerator, BattleWeather.None,
            new[]
            {
                new AlphaDrop("twistedspoon", 1, 1, 20),
                new AlphaDrop("mindplate", 1, 1, 15),
                new AlphaDrop("leftovers", 1, 1, 15),
                new AlphaDrop("lifeorb", 1, 1, 15),
                new AlphaDrop("abilitypatch", 1, 1, 15),
                new AlphaDrop("revive", 2, 3, 35),
            },
            new[] { "psychic", "shadowball", "calmmind" }),
    };

    private static readonly Dictionary<string, AlphaDef> ById = All.ToDictionary(alpha => alpha.Id, StringComparer.Ordinal);
    private static readonly Dictionary<uint, AlphaDef> ByTerritory = All.ToDictionary(alpha => alpha.TerritoryId);

    public static AlphaDef? Find(string id) => ById.TryGetValue(id, out var alpha) ? alpha : null;

    public static AlphaDef? ForTerritory(uint territoryId) =>
        ByTerritory.TryGetValue(territoryId, out var alpha) ? alpha : null;

    // Held gear and evolution stones in an Alpha loot table are restricted stock. Consumables
    // remain ordinary Marketboard supplies even when an Alpha may also award them.
    public static bool IsExclusiveDrop(string itemId) =>
        Items.Find(itemId) is { Category: ItemCategory.HeldItem or ItemCategory.EvolutionStone or ItemCategory.AbilityItem } &&
        All.Any(alpha => alpha.Drops.Any(drop => string.Equals(drop.ItemId, itemId, StringComparison.Ordinal)));

    public static string DropSourceText(string itemId)
    {
        var sources = All.Where(alpha => alpha.Drops.Any(drop =>
                string.Equals(drop.ItemId, itemId, StringComparison.Ordinal)))
            .Select(alpha => $"Alpha {alpha.Species?.Name ?? alpha.SpeciesId} - {alpha.Lair} ({alpha.ZoneName})")
            .ToList();
        return sources.Count == 0
            ? string.Empty
            : "Alpha source (random possible spoil):\n" + string.Join("\n", sources);
    }

    public static bool IsExclusiveTm(string moveId) =>
        All.Any(alpha => alpha.TmMoveIds.Contains(moveId, StringComparer.Ordinal));

    public static string TmSourceText(string moveId)
    {
        var sources = All.Where(alpha => alpha.TmMoveIds.Contains(moveId, StringComparer.Ordinal))
            .Select(alpha => $"Alpha {alpha.Species?.Name ?? alpha.SpeciesId} - {alpha.Lair} ({alpha.ZoneName})")
            .ToList();
        return sources.Count == 0
            ? string.Empty
            : "Alpha source (random possible TM spoil):\n" + string.Join("\n", sources);
    }

    public static string TraitName(AlphaTrait trait) => trait switch
    {
        AlphaTrait.Frenzied => "Frenzied",
        AlphaTrait.Ancient => "Ancient",
        AlphaTrait.Swift => "Swift",
        AlphaTrait.Regenerator => "Regenerator",
        _ => "Iron Hide",
    };

    public static string TraitBlurb(AlphaTrait trait) => trait switch
    {
        AlphaTrait.Frenzied => "Its attacks hit 30% harder.",
        AlphaTrait.Ancient => "An enormous pool of HP.",
        AlphaTrait.Swift => "It always moves first.",
        AlphaTrait.Regenerator => "It heals at the end of every turn.",
        _ => "It starts battle guarded by a shield.",
    };

    public static string WeatherLabel(BattleWeather weather) => weather switch
    {
        BattleWeather.Sun => "Harsh sunlight",
        BattleWeather.Rain => "Rain",
        BattleWeather.Sandstorm => "Sandstorm",
        BattleWeather.Snow => "Snow",
        _ => string.Empty,
    };

    public static Vector4 TraitColor(AlphaTrait trait) => trait switch
    {
        AlphaTrait.Frenzied => new Vector4(0.95f, 0.36f, 0.30f, 1f),
        AlphaTrait.Ancient => new Vector4(0.93f, 0.76f, 0.36f, 1f),
        AlphaTrait.Swift => new Vector4(0.42f, 0.83f, 0.95f, 1f),
        AlphaTrait.Regenerator => new Vector4(0.45f, 0.86f, 0.52f, 1f),
        _ => new Vector4(0.68f, 0.72f, 0.80f, 1f),
    };

    // Builds the live boss for a challenge: max IVs, trait stat bonuses, and the "Alpha X" name
    // the battle log and HUD pick up automatically.
    public static MonsterInstance? BuildInstance(AlphaDef def)
    {
        if (def.Species is not { } species)
        {
            return null;
        }

        var alpha = new MonsterInstance(species, def.Level);
        alpha.MakeAlpha(def.Trait);
        alpha.Rename(def.DisplayName);
        return alpha;
    }

    // Rolls this defeat's spoils: two weighted picks from the drop table, plus a TM the trainer
    // doesn't own yet (guaranteed on the first clear, a 30% bonus afterwards).
    public static List<AlphaLoot> RollDrops(AlphaDef def, Random rng, bool firstClear,
        IReadOnlySet<string> ownedTms)
    {
        var loot = new List<AlphaLoot>();
        var pool = def.Drops.ToList();
        for (var pick = 0; pick < 2 && pool.Count > 0; pick++)
        {
            var total = pool.Sum(drop => drop.Weight);
            var roll = rng.Next(Math.Max(1, total));
            var chosen = pool[^1];
            foreach (var drop in pool)
            {
                roll -= drop.Weight;
                if (roll < 0)
                {
                    chosen = drop;
                    break;
                }
            }

            pool.Remove(chosen);
            if (Items.Find(chosen.ItemId) is not { } item)
            {
                continue;
            }

            var count = rng.Next(chosen.MinCount, chosen.MaxCount + 1);
            loot.Add(new AlphaLoot(count > 1 ? $"{item.Name} x{count}" : item.Name, item, null, count));
        }

        if (firstClear || rng.NextDouble() < 0.3)
        {
            var tms = def.TmMoveIds.Select(Tms.Find).OfType<TmDef>()
                .Where(tm => !ownedTms.Contains(tm.MoveId)).ToList();
            if (tms.Count > 0)
            {
                var tm = tms[rng.Next(tms.Count)];
                loot.Add(new AlphaLoot($"{Tms.Label(tm.Number)} {tm.Move.Name}", null, tm, 1));
            }
        }

        return loot;
    }

    // ---- Lair position ---------------------------------------------------------------
    // The boss dens at a fixed spot in its zone. The player has to physically travel there:
    // the map card can pin the lair on the in-game map (the same flag marker Player Search
    // drops), and the challenge only unlocks within this radius of the den.

    public const float ChallengeRadius = 60f; // yalms

    // World position of the lair on the current map. Only resolvable while standing in the
    // alpha's territory — which is the only time callers need it.
    public static bool TryGetLairWorld(AlphaDef def, out Vector3 world)
    {
        world = default;
        if (Plugin.ClientState.TerritoryType != def.TerritoryId ||
            !MapCoordinateConverter.TryGetCurrentMap(out var map))
        {
            return false;
        }

        world = MapCoordinateConverter.MapToWorldCoordinates(def.MapCoords, map);
        return true;
    }

    // Flat distance in yalms from `position` to the lair, or null when it can't be resolved
    // (wrong zone, no player, map not loaded yet).
    public static float? DistanceToLair(AlphaDef def, Vector3? position)
    {
        if (position is not { } pos || !TryGetLairWorld(def, out var lair))
        {
            return null;
        }

        var dx = pos.X - lair.X;
        var dz = pos.Z - lair.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public static bool IsWithinLair(AlphaDef def, Vector3? position) =>
        DistanceToLair(def, position) is { } distance && distance <= ChallengeRadius;

    // Opens the in-game map with a flag pinned on the lair — the same marker Player Search sets.
    public static bool TryFlagLair(AlphaDef def)
    {
        var mapId = Plugin.ClientState.MapId;
        if (Plugin.ClientState.TerritoryType != def.TerritoryId || mapId == 0)
        {
            return false;
        }

        var link = new MapLinkPayload(def.TerritoryId, mapId, def.MapCoords.X, def.MapCoords.Y);
        Plugin.GameGui.OpenMapWithMapLink(link);
        return true;
    }

    // A one-line "Possible spoils" summary for tooltips and the Alphas dex tab.
    public static string DropSummary(AlphaDef def)
    {
        var names = def.Drops.Select(drop => Items.Find(drop.ItemId)?.Name).OfType<string>().Distinct();
        var tms = def.TmMoveIds.Select(Tms.Find).OfType<TmDef>().Select(tm => $"TM {tm.Move.Name}");
        return string.Join(", ", names.Concat(tms));
    }
}
