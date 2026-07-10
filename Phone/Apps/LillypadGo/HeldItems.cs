using Dalamud.Interface;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// The stat a pinch berry or a defensive reaction berry raises.
internal enum BoostStat : byte
{
    Atk,
    Def,
    SpAtk,
    SpDef,
    Spd,
    Random,
}

// The passive, always-on numbers a held item feeds into the damage/speed/accuracy formulas.
// Anything that *fires* (berries, orbs, herbs, Rocky Helmet...) is behaviour, not data, and lives
// in Battle.HeldItems.cs instead. Every multiplier defaults to 1x, so an item only states what it
// changes.
internal sealed record HeldSpec
{
    // 1.2x to moves of this type (type boosters and Arceus plates).
    public Element? BoostType { get; init; }

    // Base-power multipliers, applied after STAB/weather.
    public float PhysicalPower { get; init; } = 1f;
    public float SpecialPower { get; init; } = 1f;
    public float AllPower { get; init; } = 1f;

    // Expert Belt: 1.2x, but only into a super-effective hit.
    public bool BoostsSuperEffective { get; init; }

    // Stat multipliers. Attack/Sp. Atk scale the attacking stat (Choice Band, Light Ball);
    // Def/Sp. Def scale the defending stat (Eviolite, Assault Vest).
    public float AtkMultiplier { get; init; } = 1f;
    public float SpAtkMultiplier { get; init; } = 1f;
    public float DefMultiplier { get; init; } = 1f;
    public float SpDefMultiplier { get; init; } = 1f;
    public float SpeedMultiplier { get; init; } = 1f;

    // Crit stages halve the crit denominator each (Scope Lens 1, Leek 2).
    public int CritStages { get; init; }

    // Accuracy of the holder's own moves; FoeAccuracy scales moves aimed *at* the holder.
    public float AccuracyMultiplier { get; init; } = 1f;
    public float FoeAccuracyMultiplier { get; init; } = 1f;

    // Zoom Lens only pays out when its holder moves after the target.
    public bool AccuracyWhenMovingSecond { get; init; }

    // Locks the holder into the first move it picks (Choice trio).
    public bool ChoiceLocked { get; init; }

    // Assault Vest: the holder cannot select a status move.
    public bool BlocksStatusMoves { get; init; }

    // Eviolite: everything above only applies while the species can still evolve.
    public bool OnlyIfUnevolved { get; init; }

    // Light Ball / Thick Club / the Ditto powders: everything above only applies to these species.
    public string[]? SpeciesLock { get; init; }

    // Lagging Tail: the holder moves last within its priority bracket.
    public bool MovesLast { get; init; }

    // Float Stone: scales the weight the weight-based moves read off the holder.
    public float WeightMultiplier { get; init; } = 1f;

    // Heavy-Duty Boots: entry hazards do not touch the holder.
    public bool IgnoresHazards { get; init; }

    // Safety Goggles: powder and spore moves miss the holder entirely.
    public bool BlocksPowder { get; init; }

    // Air Balloon: Ground moves miss until the balloon is popped by any other hit.
    public bool FloatsOverGround { get; init; }

    // Big Root: draining moves and Leech Seed recover 30% more.
    public bool BoostsDrain { get; init; }
}

// Every held item the battle engine understands: the shop/bag presentation (ItemDef) plus the
// passive numbers (HeldSpec) and the family tables the firing behaviour reads.
//
// Ids are Showdown item ids, which means `Assets/pokemon/items/<id>.png` already holds the art
// (see tools/build_items.py) and saves stay compatible with the ids that shipped earlier.
internal static class HeldItems
{
    // ---- Families -------------------------------------------------------------------------------

    // The 18 classic type boosters and Arceus' 17 plates: 1.2x to one type.
    private static readonly (string Id, string Name, Element Type)[] TypeBoosters =
    {
        ("silkscarf", "Silk Scarf", Element.Normal),
        ("charcoal", "Charcoal", Element.Fire),
        ("mysticwater", "Mystic Water", Element.Water),
        ("magnet", "Magnet", Element.Electric),
        ("miracleseed", "Miracle Seed", Element.Grass),
        ("nevermeltice", "Never-Melt Ice", Element.Ice),
        ("blackbelt", "Black Belt", Element.Fighting),
        ("poisonbarb", "Poison Barb", Element.Poison),
        ("softsand", "Soft Sand", Element.Ground),
        ("sharpbeak", "Sharp Beak", Element.Flying),
        ("twistedspoon", "Twisted Spoon", Element.Psychic),
        ("silverpowder", "Silver Powder", Element.Bug),
        ("hardstone", "Hard Stone", Element.Rock),
        ("spelltag", "Spell Tag", Element.Ghost),
        ("dragonfang", "Dragon Fang", Element.Dragon),
        ("blackglasses", "Black Glasses", Element.Dark),
        ("metalcoat", "Metal Coat", Element.Steel),
        ("fairyfeather", "Fairy Feather", Element.Fairy),
    };

    private static readonly (string Id, string Name, Element Type)[] Plates =
    {
        ("flameplate", "Flame Plate", Element.Fire),
        ("splashplate", "Splash Plate", Element.Water),
        ("zapplate", "Zap Plate", Element.Electric),
        ("meadowplate", "Meadow Plate", Element.Grass),
        ("icicleplate", "Icicle Plate", Element.Ice),
        ("fistplate", "Fist Plate", Element.Fighting),
        ("toxicplate", "Toxic Plate", Element.Poison),
        ("earthplate", "Earth Plate", Element.Ground),
        ("skyplate", "Sky Plate", Element.Flying),
        ("mindplate", "Mind Plate", Element.Psychic),
        ("insectplate", "Insect Plate", Element.Bug),
        ("stoneplate", "Stone Plate", Element.Rock),
        ("spookyplate", "Spooky Plate", Element.Ghost),
        ("dracoplate", "Draco Plate", Element.Dragon),
        ("dreadplate", "Dread Plate", Element.Dark),
        ("ironplate", "Iron Plate", Element.Steel),
        ("pixieplate", "Pixie Plate", Element.Fairy),
    };

    // Halve a super-effective hit of one type, then the berry is gone. Chilan is the odd one out:
    // it halves *any* Normal hit, super-effective or not.
    private static readonly (string Id, string Name, Element Type)[] ResistBerries =
    {
        ("occaberry", "Occa Berry", Element.Fire),
        ("passhoberry", "Passho Berry", Element.Water),
        ("wacanberry", "Wacan Berry", Element.Electric),
        ("rindoberry", "Rindo Berry", Element.Grass),
        ("yacheberry", "Yache Berry", Element.Ice),
        ("chopleberry", "Chople Berry", Element.Fighting),
        ("kebiaberry", "Kebia Berry", Element.Poison),
        ("shucaberry", "Shuca Berry", Element.Ground),
        ("cobaberry", "Coba Berry", Element.Flying),
        ("payapaberry", "Payapa Berry", Element.Psychic),
        ("tangaberry", "Tanga Berry", Element.Bug),
        ("chartiberry", "Charti Berry", Element.Rock),
        ("kasibberry", "Kasib Berry", Element.Ghost),
        ("habanberry", "Haban Berry", Element.Dragon),
        ("colburberry", "Colbur Berry", Element.Dark),
        ("babiriberry", "Babiri Berry", Element.Steel),
        ("roseliberry", "Roseli Berry", Element.Fairy),
        ("chilanberry", "Chilan Berry", Element.Normal),
    };

    // Eaten the moment the holder picks up the matching condition.
    private static readonly (string Id, string Name, Status Cures, string Word)[] StatusBerries =
    {
        ("cheriberry", "Cheri Berry", Status.Paralysis, "paralysis"),
        ("chestoberry", "Chesto Berry", Status.Sleep, "sleep"),
        ("pechaberry", "Pecha Berry", Status.Poison, "poison"),
        ("rawstberry", "Rawst Berry", Status.Burn, "a burn"),
        ("aspearberry", "Aspear Berry", Status.Freeze, "a freeze"),
    };

    // Eaten at or below 1/4 HP for a sharp stat spike.
    private static readonly (string Id, string Name, BoostStat Stat, string Word)[] PinchBerries =
    {
        ("liechiberry", "Liechi Berry", BoostStat.Atk, "Attack"),
        ("ganlonberry", "Ganlon Berry", BoostStat.Def, "Defense"),
        ("salacberry", "Salac Berry", BoostStat.Spd, "Speed"),
        ("petayaberry", "Petaya Berry", BoostStat.SpAtk, "Sp. Atk"),
        ("apicotberry", "Apicot Berry", BoostStat.SpDef, "Sp. Def"),
    };

    // Eaten at or below 1/4 HP to restore a third of max HP. (In the mainline games these also
    // confuse a holder whose nature dislikes the flavour; this game has no natures, so they don't.)
    private static readonly (string Id, string Name)[] PinchHealBerries =
    {
        ("figyberry", "Figy Berry"), ("wikiberry", "Wiki Berry"), ("magoberry", "Mago Berry"),
        ("aguavberry", "Aguav Berry"), ("iapapaberry", "Iapapa Berry"),
    };

    // Raise one stat when the holder is struck by a move of the matching type.
    private static readonly (string Id, string Name, Element Type, BoostStat Stat, string Word)[] AbsorbItems =
    {
        ("absorbbulb", "Absorb Bulb", Element.Water, BoostStat.SpAtk, "Sp. Atk"),
        ("cellbattery", "Cell Battery", Element.Electric, BoostStat.Atk, "Attack"),
        ("snowball", "Snowball", Element.Ice, BoostStat.Atk, "Attack"),
        ("luminousmoss", "Luminous Moss", Element.Water, BoostStat.SpDef, "Sp. Def"),
    };

    // ---- Lookup tables the battle engine reads ---------------------------------------------------

    public static readonly IReadOnlyDictionary<string, Element> ResistBerryType =
        ResistBerries.ToDictionary(b => b.Id, b => b.Type, StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, Status> StatusBerryCures =
        StatusBerries.ToDictionary(b => b.Id, b => b.Cures, StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, BoostStat> PinchBerryStat =
        PinchBerries.ToDictionary(b => b.Id, b => b.Stat, StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, (Element Type, BoostStat Stat, string Word)> AbsorbItem =
        AbsorbItems.ToDictionary(a => a.Id, a => (a.Type, a.Stat, a.Word), StringComparer.Ordinal);

    private static readonly HashSet<string> PinchHealIds =
        PinchHealBerries.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);

    public static bool IsPinchHealBerry(string id) => PinchHealIds.Contains(id);

    // ---- Per-item passive numbers -----------------------------------------------------------------

    private static readonly Dictionary<string, HeldSpec> Specs = BuildSpecs();

    // The passive numbers for a held item, or null when it has none (a purely reactive item).
    public static HeldSpec? SpecFor(string itemId) =>
        Specs.TryGetValue(itemId, out var spec) ? spec : null;

    private static Dictionary<string, HeldSpec> BuildSpecs()
    {
        var specs = new Dictionary<string, HeldSpec>(StringComparer.Ordinal)
        {
            ["choiceband"] = new() { AtkMultiplier = 1.5f, ChoiceLocked = true },
            ["choicespecs"] = new() { SpAtkMultiplier = 1.5f, ChoiceLocked = true },
            ["choicescarf"] = new() { SpeedMultiplier = 1.5f, ChoiceLocked = true },
            ["lifeorb"] = new() { AllPower = 1.3f },
            ["expertbelt"] = new() { BoostsSuperEffective = true },
            ["muscleband"] = new() { PhysicalPower = 1.1f },
            ["wiseglasses"] = new() { SpecialPower = 1.1f },
            ["scopelens"] = new() { CritStages = 1 },
            ["razorclaw"] = new() { CritStages = 1 },
            ["leek"] = new() { CritStages = 2, SpeciesLock = new[] { "farfetchd" } },
            ["luckypunch"] = new() { CritStages = 2, SpeciesLock = new[] { "chansey" } },
            ["widelens"] = new() { AccuracyMultiplier = 1.1f },
            ["zoomlens"] = new() { AccuracyMultiplier = 1.2f, AccuracyWhenMovingSecond = true },
            ["brightpowder"] = new() { FoeAccuracyMultiplier = 0.9f },
            ["laxincense"] = new() { FoeAccuracyMultiplier = 0.9f },
            ["eviolite"] = new() { DefMultiplier = 1.5f, SpDefMultiplier = 1.5f, OnlyIfUnevolved = true },
            ["assaultvest"] = new() { SpDefMultiplier = 1.5f, BlocksStatusMoves = true },
            ["ironball"] = new() { SpeedMultiplier = 0.5f },
            ["machobrace"] = new() { SpeedMultiplier = 0.5f },
            ["laggingtail"] = new() { MovesLast = true },
            ["floatstone"] = new() { WeightMultiplier = 0.5f },
            ["heavydutyboots"] = new() { IgnoresHazards = true },
            ["safetygoggles"] = new() { BlocksPowder = true },
            ["airballoon"] = new() { FloatsOverGround = true },
            ["bigroot"] = new() { BoostsDrain = true },
            // Species-locked classics: Pikachu doubles both attacking stats, Cubone's line doubles
            // Attack, and Ditto's powders double Defense / Speed.
            ["lightball"] = new() { AtkMultiplier = 2f, SpAtkMultiplier = 2f, SpeciesLock = new[] { "pikachu" } },
            ["thickclub"] = new() { AtkMultiplier = 2f, SpeciesLock = new[] { "cubone", "marowak" } },
            ["metalpowder"] = new() { DefMultiplier = 2f, SpeciesLock = new[] { "ditto" } },
            ["quickpowder"] = new() { SpeedMultiplier = 2f, SpeciesLock = new[] { "ditto" } },
        };

        foreach (var (id, _, type) in TypeBoosters)
        {
            specs[id] = new HeldSpec { BoostType = type };
        }

        foreach (var (id, _, type) in Plates)
        {
            specs[id] = new HeldSpec { BoostType = type };
        }

        return specs;
    }

    // ---- Catalogue --------------------------------------------------------------------------------

    private static ItemDef Held(string id, string name, string blurb, string description, int price,
        FontAwesomeIcon icon = FontAwesomeIcon.Gem) =>
        new(id, name, blurb, description, price, ItemCategory.HeldItem, icon);

    private static ItemDef Berry(string id, string name, string blurb, string description, int price) =>
        new(id, name, blurb, description, price, ItemCategory.HeldItem, FontAwesomeIcon.Leaf);

    public static readonly IReadOnlyList<ItemDef> All = BuildCatalogue();

    private static List<ItemDef> BuildCatalogue()
    {
        var items = new List<ItemDef>
        {
            // ---- Recovery -------------------------------------------------------------------------
            Berry("oranberry", "Oran Berry", "Restores 10 HP at half health.",
                "A held Berry. Eaten automatically when its holder drops to half HP or less, restoring 10 HP.", 200),
            Berry("sitrusberry", "Sitrus Berry", "Restores 1/4 max HP at half health.",
                "A held Berry. Eaten automatically when its holder drops to half HP or less, restoring a quarter " +
                "of its maximum HP.", 600),
            Berry("berryjuice", "Berry Juice", "Restores 20 HP at half health.",
                "A held drink. Used automatically when its holder drops to half HP or less, restoring 20 HP.", 400),
            Held("leftovers", "Leftovers", "Restores 1/16 max HP each turn.",
                "A held item that restores 1/16 of its holder's maximum HP at the end of every turn.", 1800,
                FontAwesomeIcon.Heart),
            Held("blacksludge", "Black Sludge", "Heals Poison-types, hurts the rest.",
                "A held item that restores 1/16 of a Poison-type holder's maximum HP each turn. Any other holder " +
                "loses 1/8 of its maximum HP instead.", 1400, FontAwesomeIcon.Biohazard),
            Held("shellbell", "Shell Bell", "Heals 1/8 of the damage it deals.",
                "A held item. Whenever its holder lands a damaging move, it recovers 1/8 of the damage dealt.", 2000,
                FontAwesomeIcon.Bell),
            Held("bigroot", "Big Root", "Draining moves recover 30% more.",
                "A held item that raises the HP recovered by draining moves and Leech Seed by 30%.", 1200,
                FontAwesomeIcon.Seedling),

            // ---- Survival -------------------------------------------------------------------------
            Held("focussash", "Focus Sash", "Survives one full-HP knockout.",
                "A held item. If its holder is at full HP, it survives an otherwise fatal hit with 1 HP. Used once.",
                2400, FontAwesomeIcon.Ribbon),
            Held("focusband", "Focus Band", "Sometimes survives a knockout.",
                "A held item that gives its holder a 10% chance to survive an otherwise fatal hit with 1 HP.", 1600,
                FontAwesomeIcon.Ribbon),
            Held("eviolite", "Eviolite", "1.5x defenses if it can still evolve.",
                "A held item. If its holder's species has not finished evolving, its Defense and Sp. Def are 1.5x.",
                3500, FontAwesomeIcon.Shield),
            Held("assaultvest", "Assault Vest", "1.5x Sp. Def, but no status moves.",
                "A held item that raises its holder's Sp. Def by 50%, at the cost of being unable to select any " +
                "status move.", 2800, FontAwesomeIcon.Vest),
            Held("airballoon", "Air Balloon", "Floats over Ground moves until hit.",
                "A held balloon. Its holder is immune to Ground moves while it floats, but the balloon pops the " +
                "moment any other move connects.", 1600, FontAwesomeIcon.Circle),
            Held("heavydutyboots", "Heavy-Duty Boots", "Ignores entry hazards.",
                "A held item. Stealth Rock, Spikes and Toxic Spikes have no effect on its holder when it enters " +
                "battle.", 2000, FontAwesomeIcon.ShoePrints),
            Held("safetygoggles", "Safety Goggles", "Immune to powder and spore moves.",
                "Held eyewear. Powder and spore moves — Sleep Powder, Stun Spore, Spore and the rest — cannot " +
                "touch its holder.", 1800, FontAwesomeIcon.Glasses),

            // ---- Offense --------------------------------------------------------------------------
            Held("choiceband", "Choice Band", "1.5x Attack, but locks in one move.",
                "A held band that raises its holder's Attack by 50%, but only the first move it picks can be used.",
                3000, FontAwesomeIcon.Ring),
            Held("choicespecs", "Choice Specs", "1.5x Sp. Atk, but locks in one move.",
                "Held glasses that raise their holder's Sp. Atk by 50%, but only the first move it picks can be " +
                "used.", 3000, FontAwesomeIcon.Glasses),
            Held("choicescarf", "Choice Scarf", "1.5x Speed, but locks in one move.",
                "A held scarf that raises its holder's Speed by 50%, but only the first move it picks can be used.",
                3000, FontAwesomeIcon.Wind),
            Held("lifeorb", "Life Orb", "1.3x damage, 1/10 recoil.",
                "A held orb. Its holder's attacks deal 30% more damage, but it loses 1/10 of its maximum HP each " +
                "time one lands.", 3000, FontAwesomeIcon.Circle),
            Held("expertbelt", "Expert Belt", "1.2x on super-effective hits.",
                "A held belt that raises the damage of its holder's super-effective moves by 20%.", 2400,
                FontAwesomeIcon.Ring),
            Held("muscleband", "Muscle Band", "1.1x physical damage.",
                "A held band that raises the power of its holder's physical moves by 10%.", 2200,
                FontAwesomeIcon.Dumbbell),
            Held("wiseglasses", "Wise Glasses", "1.1x special damage.",
                "Held glasses that raise the power of their holder's special moves by 10%.", 2200,
                FontAwesomeIcon.Glasses),
            Held("scopelens", "Scope Lens", "Raises the critical-hit rate.",
                "A held lens that makes its holder's moves land critical hits far more often.", 2000,
                FontAwesomeIcon.Crosshairs),
            Held("razorclaw", "Razor Claw", "Raises the critical-hit rate.",
                "A held claw, sharp as a blade, that makes its holder's moves land critical hits far more often.",
                2000, FontAwesomeIcon.HandScissors),
            Held("kingsrock", "King's Rock", "Hits may make the target flinch.",
                "A held stone. Each damaging move its holder lands has a 10% chance to make the target flinch.",
                2000, FontAwesomeIcon.ChessKing),
            Held("razorfang", "Razor Fang", "Hits may make the target flinch.",
                "A held fang. Each damaging move its holder lands has a 10% chance to make the target flinch.",
                2000, FontAwesomeIcon.Tooth),
            Held("widelens", "Wide Lens", "1.1x accuracy.",
                "A held lens that raises the accuracy of its holder's moves by 10%.", 1200, FontAwesomeIcon.Search),
            Held("zoomlens", "Zoom Lens", "1.2x accuracy when moving second.",
                "A held lens. When its holder moves after the target, its accuracy is raised by 20%.", 1200,
                FontAwesomeIcon.SearchPlus),
            Held("brightpowder", "Bright Powder", "Foes are 10% less accurate.",
                "A held powder whose glare lowers the accuracy of moves aimed at its holder by 10%.", 1400,
                FontAwesomeIcon.Sun),
            Held("laxincense", "Lax Incense", "Foes are 10% less accurate.",
                "A held incense whose haze lowers the accuracy of moves aimed at its holder by 10%.", 1400,
                FontAwesomeIcon.Smog),

            // ---- Punishing the attacker -----------------------------------------------------------
            Held("rockyhelmet", "Rocky Helmet", "Hurts attackers that make contact.",
                "A held helmet. Anything that hits its holder with a contact move loses 1/6 of its maximum HP.",
                2200, FontAwesomeIcon.HardHat),
            Held("weaknesspolicy", "Weakness Policy", "Sharply boosts attack when hit hard.",
                "A held item. When its holder is struck by a super-effective move, its Attack and Sp. Atk rise " +
                "sharply. Used once.", 2600, FontAwesomeIcon.FileContract),

            // ---- Self-inflicted -------------------------------------------------------------------
            Held("toxicorb", "Toxic Orb", "Badly poisons its holder.",
                "A held orb that badly poisons its holder at the end of the turn. Pairs with abilities that feed " +
                "on a status condition.", 1800, FontAwesomeIcon.Skull),
            Held("flameorb", "Flame Orb", "Burns its holder.",
                "A held orb that burns its holder at the end of the turn. Pairs with abilities that feed on a " +
                "status condition.", 1800, FontAwesomeIcon.Fire),
            Held("stickybarb", "Sticky Barb", "Hurts its holder each turn.",
                "A held barb that costs its holder 1/8 of its maximum HP every turn.", 800, FontAwesomeIcon.Bacterium),
            Held("ironball", "Iron Ball", "Halves Speed.",
                "A held ball, crushingly heavy: its holder's Speed is halved.", 1000, FontAwesomeIcon.Circle),
            Held("machobrace", "Macho Brace", "Halves Speed while training.",
                "A held brace. Its weight halves the holder's Speed.", 1000, FontAwesomeIcon.Dumbbell),
            Held("laggingtail", "Lagging Tail", "Its holder always moves last.",
                "A held tail so heavy that its holder always moves last among moves of equal priority.", 1000,
                FontAwesomeIcon.ArrowDown),
            Held("floatstone", "Float Stone", "Halves its holder's weight.",
                "A held stone, so light it barely exists. Its holder counts as half its weight against moves that " +
                "care — Low Kick, Grass Knot, Heavy Slam and Heat Crash.", 1000, FontAwesomeIcon.Feather),

            // ---- Herbs and utility ----------------------------------------------------------------
            Held("whiteherb", "White Herb", "Restores lowered stats once.",
                "A held herb. The first time any of its holder's stats have been lowered, it restores them all to " +
                "normal. Used once.", 1400, FontAwesomeIcon.Leaf),
            Held("mentalherb", "Mental Herb", "Frees its holder from a move lock.",
                "A held herb that snaps its holder out of Taunt, Encore or Disable. Used once.", 1400,
                FontAwesomeIcon.Brain),
            Held("quickclaw", "Quick Claw", "Sometimes strikes first.",
                "A held claw that gives its holder a 20% chance to move before the target, whatever their Speed.",
                1800, FontAwesomeIcon.HandScissors),
            Held("bindingband", "Binding Band", "Binding moves hurt more.",
                "A held band that raises the damage its holder's binding moves (Wrap, Bind, Fire Spin...) deal " +
                "each turn.", 1200, FontAwesomeIcon.Link),
            Held("gripclaw", "Grip Claw", "Binding moves last longer.",
                "A held claw that makes its holder's binding moves hold the target for the maximum number of turns.",
                1200, FontAwesomeIcon.HandRock),
            Held("lightclay", "Light Clay", "Screens last longer.",
                "A held clay that keeps its holder's Reflect and Light Screen up for eight turns instead of five.",
                1600, FontAwesomeIcon.Cube),
            Held("heatrock", "Heat Rock", "Sun set by its holder lasts longer.",
                "A held rock. Harsh sunlight its holder summons lasts eight turns instead of five.", 1200,
                FontAwesomeIcon.Sun),
            Held("damprock", "Damp Rock", "Rain set by its holder lasts longer.",
                "A held rock. Rain its holder summons lasts eight turns instead of five.", 1200,
                FontAwesomeIcon.CloudRain),
            Held("smoothrock", "Smooth Rock", "Sandstorms last longer.",
                "A held rock. A sandstorm its holder summons lasts eight turns instead of five.", 1200,
                FontAwesomeIcon.Mountain),
            Held("icyrock", "Icy Rock", "Snow set by its holder lasts longer.",
                "A held rock. Snow its holder summons lasts eight turns instead of five.", 1200,
                FontAwesomeIcon.Snowflake),

            // ---- Reaction berries -----------------------------------------------------------------
            Berry("lumberry", "Lum Berry", "Cures any status condition.",
                "A held Berry. Eaten automatically to cure any status condition the moment its holder picks one up.",
                800),
            Berry("persimberry", "Persim Berry", "Cures confusion.",
                "A held Berry. Eaten automatically to snap its holder out of confusion.", 300),
            Berry("leppaberry", "Leppa Berry", "Restores 10 PP to a spent move.",
                "A held Berry. Eaten automatically when one of its holder's moves runs out of PP, restoring 10 PP " +
                "to it.", 500),
            Berry("enigmaberry", "Enigma Berry", "Heals 1/4 when hit hard.",
                "A held Berry. Eaten when its holder is struck by a super-effective move, restoring a quarter of " +
                "its maximum HP.", 900),
            Berry("jabocaberry", "Jaboca Berry", "Punishes physical attackers.",
                "A held Berry. When its holder is hit by a physical move, the attacker loses 1/8 of its maximum HP.",
                900),
            Berry("rowapberry", "Rowap Berry", "Punishes special attackers.",
                "A held Berry. When its holder is hit by a special move, the attacker loses 1/8 of its maximum HP.",
                900),
            Berry("keeberry", "Kee Berry", "Raises Defense when hit physically.",
                "A held Berry. Eaten when its holder is hit by a physical move, raising its Defense.", 900),
            Berry("marangaberry", "Maranga Berry", "Raises Sp. Def when hit specially.",
                "A held Berry. Eaten when its holder is hit by a special move, raising its Sp. Def.", 900),
            Berry("starfberry", "Starf Berry", "Sharply boosts a random stat in a pinch.",
                "A held Berry. Eaten at a quarter HP or less, sharply raising one of its holder's stats at random.",
                1200),
        };

        foreach (var (id, name, type) in TypeBoosters)
        {
            var element = Elements.Name(type);
            items.Add(Held(id, name, $"1.2x to {element} moves.",
                $"A held item that raises the power of its holder's {element}-type moves by 20%.", 1000));
        }

        foreach (var (id, name, type) in Plates)
        {
            var element = Elements.Name(type);
            items.Add(Held(id, name, $"1.2x to {element} moves.",
                $"An ancient stone tablet. It raises the power of its holder's {element}-type moves by 20%.", 2000));
        }

        foreach (var (id, name, type) in ResistBerries)
        {
            var element = Elements.Name(type);
            items.Add(Berry(id, name, $"Softens a {element} hit.", type == Element.Normal
                ? $"A held Berry. Eaten to halve the damage of any {element}-type move that strikes its holder. " +
                  "Used once."
                : $"A held Berry. Eaten to halve the damage of a super-effective {element}-type move. Used once.",
                400));
        }

        foreach (var (id, name, _, word) in StatusBerries)
        {
            items.Add(Berry(id, name, $"Cures {word}.",
                $"A held Berry. Eaten automatically to cure {word} the moment its holder is afflicted.", 300));
        }

        foreach (var (id, name, _, word) in PinchBerries)
        {
            items.Add(Berry(id, name, $"Raises {word} in a pinch.",
                $"A held Berry. Eaten at a quarter HP or less, sharply raising its holder's {word}.", 1000));
        }

        foreach (var (id, name) in PinchHealBerries)
        {
            items.Add(Berry(id, name, "Restores 1/3 max HP in a pinch.",
                "A held Berry. Eaten at a quarter HP or less, restoring a third of its holder's maximum HP.", 500));
        }

        foreach (var (id, name, type, _, word) in AbsorbItems)
        {
            var element = Elements.Name(type);
            items.Add(Held(id, name, $"Raises {word} when hit by {element}.",
                $"A held item. When its holder is struck by a {element}-type move, its {word} rises. Used once.",
                1200));
        }

        return items;
    }

    // ---- Trainer loadouts ---------------------------------------------------------------------------

    // Items an AI trainer should never be handed. The orbs and the barb are pure self-harm without the
    // ability that pays for them (Guts, Poison Heal); the weights are drawbacks the AI cannot exploit;
    // Berry Juice and Leppa only matter over a long fight the AI will not survive.
    private static readonly HashSet<string> NeverGiveToTrainers = new(StringComparer.Ordinal)
    {
        "toxicorb", "flameorb", "stickybarb", "ironball", "machobrace", "laggingtail", "leppaberry",
    };

    // Would this item do anything at all for this creature? Mirrors the checks ActiveSpec makes at
    // battle time, so a trainer is never handed a Light Ball for a Geodude or an Eviolite for a
    // fully-evolved mon — items that would sit inert all fight.
    private static bool SuitsHolder(MonsterInstance holder, string itemId)
    {
        if (SpecFor(itemId) is not { } spec)
        {
            return true; // purely reactive items (berries, Rocky Helmet) suit anything
        }

        if (spec.SpeciesLock is { } species && Array.IndexOf(species, holder.Species.Id) < 0)
        {
            return false;
        }

        if (spec.OnlyIfUnevolved && Dex.EvolutionOf(holder.Species) is null)
        {
            return false;
        }

        // An Assault Vest on a creature with nothing but status moves leaves it Struggling.
        return !spec.BlocksStatusMoves || holder.Moves.Any(move => !move.IsStatus);
    }

    // Pick a held item an AI trainer's creature could plausibly be carrying, or null if none fits.
    public static string? RollTrainerItem(MonsterInstance holder, Random rng)
    {
        var pool = All.Where(item => !NeverGiveToTrainers.Contains(item.Id) && SuitsHolder(holder, item.Id)).ToList();
        return pool.Count == 0 ? null : pool[rng.Next(pool.Count)].Id;
    }

    // Roll each member of an AI team for a held item. `chance` is the per-creature probability.
    public static void GiveTrainerItems(IEnumerable<MonsterInstance> team, Random rng, float chance)
    {
        foreach (var monster in team)
        {
            if (rng.NextDouble() < chance && RollTrainerItem(monster, rng) is { } item)
            {
                monster.HeldItem = item;
            }
        }
    }

    private static readonly HashSet<string> BerryIds = All
        .Where(item => item.Id.EndsWith("berry", StringComparison.Ordinal))
        .Select(item => item.Id)
        .ToHashSet(StringComparer.Ordinal);

    // Berry Juice is drunk, not eaten: it isn't a Berry for Bug Bite / Pluck / the Berries pocket.
    public static bool IsBerry(string itemId) => BerryIds.Contains(itemId);
}
