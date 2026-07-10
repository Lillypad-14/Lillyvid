namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal enum MoveEffect : byte
{
    None,
    RaiseAtk,
    RaiseDef,
    RaiseSpAtk,
    RaiseSpDef,
    RaiseSpd,
    LowerTargetAtk,
    LowerTargetSpd,
    LowerTargetDef,
    LowerTargetSpAtk,
    LowerTargetSpDef,
    LowerTargetAccuracy,
    RaiseEvasion,
    HealUser,
    Burn,
    Freeze,
    Sleep,
    Paralyze,
    Poison,
    Flinch,
    RecoilQuarterMax,
    Confuse,
    Transform,
    RaiseAccuracy,
    LowerTargetEvasion,
    ProtectUser,
    EndureUser,
    ForceSwitch,
    SetSun,
    SetRain,
    SetSandstorm,
    SetSnow,
    SetElectricTerrain,
    SetGrassyTerrain,
    SetMistyTerrain,
    SetPsychicTerrain,
    ReflectSide,
    LightScreenSide,
    AquaRing,
    Ingrain,
    LeechSeed,
    Yawn,
    BellyDrum,
    Haze,
    CureUserStatus,
    Acupressure,
    NoOp,
    UserFaints,   // Self-Destruct / Explosion: the user faints after dealing damage
    TrapTarget,   // Mean Look / Block / Spider Web: the target can no longer flee or switch out
    SmackDown,    // knocks a target out of the air, grounding it for the rest of the battle
    DrainHalf,    // heals the user by half the damage dealt (Giga Drain, Leech Life, ...)
    MultiHit,     // strikes two to five times in one turn
    HighCrit,     // has a heightened critical-hit rate
    Ohko,         // one-hit KO if it lands (low accuracy)
    LevelDamage,  // deals damage equal to the user's level (Seismic Toss, Night Shade)
    FixedDamage40, // always deals 40 HP (Dragon Rage)
    FixedDamage20, // always deals 20 HP (Sonic Boom)
    HalveTargetHp, // deals damage equal to half the target's current HP (Super Fang)
    MustRecharge,  // the user must spend the next turn recharging (Hyper Beam, Giga Impact)
    RaiseAtkDef,      // raises the user's Attack and Defense (Bulk Up)
    RaiseSpAtkSpDef,  // raises the user's Sp. Atk and Sp. Def (Calm Mind)
    RaiseAtkSpd,      // raises the user's Attack and Speed (Dragon Dance)
    MirrorMove,       // uses the target's last move
    Copycat,          // uses the last move used in the battle
    Metronome,        // uses a random move
    SleepTalk,        // while asleep, uses one of the user's own moves at random
    Counter,          // returns double the physical damage taken this turn
    MirrorCoat,       // returns double the special damage taken this turn
    MetalBurst,       // returns 1.5x the damage taken this turn
    Endeavor,         // brings the target down to the user's current HP
    FinalGambit,      // deals damage equal to the user's HP, then the user faints
    PainSplit,        // averages the user's and target's current HP
    Rest,             // the user falls asleep and fully heals
    TrickRoom,        // reverses turn order (slower moves first) for five turns
    LockInMove,       // locks the user into a rampage for 2-3 turns, then confuses it (Outrage)
    SafeguardSide,    // blocks major status conditions on the user's side for five turns
    MistSide,         // blocks stat drops on the user's side for five turns
    TailwindSide,     // doubles the user's side's Speed for four turns
    Spikes,           // lays up to three layers of entry hazards
    ToxicSpikes,      // lays up to two poison entry-hazard layers
    StealthRock,      // lays Rock-type entry hazards
    Substitute,       // trades one quarter max HP for a damage-absorbing substitute
    Taunt,            // prevents the target using status moves for three turns
    Encore,           // locks the target into its last move for three turns
    Disable,          // disables the target's last move for four turns
    PerishSong,       // all active battlers faint after three turns unless switched
    DestinyBond,      // takes down the next foe that knocks the user out
    Stockpile,        // stores energy and raises Defense / Sp. Def, max three stacks
    SpitUp,           // releases Stockpile energy as damage and removes the boosts
    Swallow,          // consumes Stockpile energy to heal and removes the boosts
    TrapDamage,       // binds the target and damages it for four to five turns
    BreakScreens,     // removes Reflect and Light Screen from the target's side
    RapidSpin,        // removes the user's hazards / binding / Leech Seed and raises Speed
    HealBlock,        // prevents the target from healing for two turns
    LowerUserAtkDef,  // lowers the user's Attack and Defense
    LowerUserSpAtk,   // sharply lowers the user's Sp. Atk
    LowerUserSpd,     // lowers the user's Speed
    RaiseAllStats,    // raises Attack, Defense, Sp. Atk, Sp. Def, and Speed
    TriStatus,        // randomly burns, freezes, or paralyzes the target
    ClearTerrain,     // clears the active terrain
    SkillSwap,        // swaps the user's Ability with the target's
    WaterSport,       // weakens Fire moves for five turns
    WorkUp,           // raises Attack and Sp. Atk
    WorrySeed,        // changes the target's Ability to Insomnia
    RequiresBerry,    // requires the user to have consumed a Berry
    RequiresItem,     // requires the target to hold an item
    ConsumeTargetItem,// consumes a target Berry
    StealItem,        // takes the target's held item
    RemoveItem,       // removes the target's held item
    FlingItem,        // uses the user's held item as the attack
    SwitchItems,      // swaps held items
    NaturalGift,      // uses the user's Berry as the attack
    PayDay,           // awards additional battle money
    FocusPunch,       // fails if the user was damaged before acting
    FutureSight,      // schedules a delayed Psychic strike
}

internal enum MoveCategory : byte
{
    Physical,
    Special,
    Status,
}

// Power 0 means a status/support move.
internal sealed class MoveDef
{
    public MoveDef(string name, Element element, int power, int accuracy, int pp, MoveEffect effect = MoveEffect.None,
        int effectChance = 0, MoveCategory category = MoveCategory.Physical, int priority = 0, int stageChange = 1,
        string? shortDesc = null)
    {
        Name = name;
        Element = element;
        Power = power;
        Accuracy = accuracy;
        Pp = pp;
        Effect = effect;
        EffectChance = effectChance;
        Category = power <= 0 ? MoveCategory.Status : category;
        Priority = priority;
        StageChange = Math.Clamp(stageChange, 1, 6);
        ShortDesc = shortDesc ?? string.Empty;
    }

    public string Name { get; }
    public Element Element { get; }
    public int Power { get; }
    public int Accuracy { get; }
    public int Pp { get; }
    public MoveEffect Effect { get; }
    public int EffectChance { get; }
    public MoveCategory Category { get; }
    public int Priority { get; }
    public int StageChange { get; }

    // A one-line mechanic summary from the generated move data (present for every move).
    public string ShortDesc { get; }

    public bool IsStatus => Power <= 0;

    public string CategoryLabel => Category.ToString();

    // Prefer the move's own description so every move explains what it does; fall back to the
    // effect-derived text for anything the generator didn't supply.
    public string Description => !string.IsNullOrEmpty(ShortDesc) ? ShortDesc : EffectDescription;

    private string EffectDescription => Effect switch
    {
        MoveEffect.RaiseAtk => $"Raises the user's Attack by {StageText()}.",
        MoveEffect.RaiseDef => $"Raises the user's Defense by {StageText()}.",
        MoveEffect.RaiseSpAtk => $"Raises the user's Sp. Atk by {StageText()}.",
        MoveEffect.RaiseSpDef => $"Raises the user's Sp. Def by {StageText()}.",
        MoveEffect.RaiseSpd => $"Raises the user's Speed by {StageText()}.",
        MoveEffect.LowerTargetAtk => ChanceText($"Lowers the target's Attack by {StageText()}.",
            $"May lower the target's Attack by {StageText()}."),
        MoveEffect.LowerTargetSpd => ChanceText($"Lowers the target's Speed by {StageText()}.",
            $"May lower the target's Speed by {StageText()}."),
        MoveEffect.LowerTargetDef => ChanceText($"Lowers the target's Defense by {StageText()}.",
            $"May lower the target's Defense by {StageText()}."),
        MoveEffect.LowerTargetSpAtk => ChanceText($"Lowers the target's Sp. Atk by {StageText()}.",
            $"May lower the target's Sp. Atk by {StageText()}."),
        MoveEffect.LowerTargetSpDef => ChanceText($"Lowers the target's Sp. Def by {StageText()}.",
            $"May lower the target's Sp. Def by {StageText()}."),
        MoveEffect.LowerTargetAccuracy => ChanceText($"Lowers the target's accuracy by {StageText()}.",
            $"May lower the target's accuracy by {StageText()}."),
        MoveEffect.LowerTargetEvasion => ChanceText($"Lowers the target's evasiveness by {StageText()}.",
            $"May lower the target's evasiveness by {StageText()}."),
        MoveEffect.RaiseEvasion => $"Raises the user's evasiveness by {StageText()}.",
        MoveEffect.RaiseAccuracy => $"Raises the user's accuracy by {StageText()}.",
        MoveEffect.HealUser => "Restores half of the user's maximum HP.",
        MoveEffect.Burn => EffectText("May burn the target, causing damage after each turn."),
        MoveEffect.Freeze => EffectText("May freeze the target and prevent it from acting."),
        MoveEffect.Sleep => EffectText("May put the target to sleep for a few turns."),
        MoveEffect.Paralyze => EffectText("May paralyze the target, lowering Speed and sometimes preventing action."),
        MoveEffect.Poison => EffectText("May poison the target, causing damage after each turn."),
        MoveEffect.Flinch => EffectText("May make the target flinch before it can act."),
        MoveEffect.RecoilQuarterMax => "Damages the user by one quarter of its maximum HP.",
        MoveEffect.Confuse => EffectText("May confuse the target and make it hurt itself."),
        MoveEffect.Transform => "The user transforms into a copy of the target, with its moves and stats.",
        MoveEffect.ProtectUser => "Protects the user from most attacks this turn.",
        MoveEffect.EndureUser => "Lets the user survive a knockout blow this turn with 1 HP.",
        MoveEffect.ForceSwitch => "Forces the target out. In wild battles, this can end the encounter.",
        MoveEffect.SetSun => "Starts harsh sunlight for five turns. Fire is stronger; Water is weaker.",
        MoveEffect.SetRain => "Starts rain for five turns. Water is stronger; Fire is weaker.",
        MoveEffect.SetSandstorm => "Starts a sandstorm for five turns, damaging most non-Rock/Ground/Steel creatures.",
        MoveEffect.SetSnow => "Starts snow for five turns, strengthening Ice defenses.",
        MoveEffect.SetElectricTerrain => "Sets Electric Terrain for five turns, boosting Electric moves and blocking sleep.",
        MoveEffect.SetGrassyTerrain => "Sets Grassy Terrain for five turns, boosting Grass moves and healing each turn.",
        MoveEffect.SetMistyTerrain => "Sets Misty Terrain for five turns, weakening Dragon moves and blocking status.",
        MoveEffect.SetPsychicTerrain => "Sets Psychic Terrain for five turns, boosting Psychic moves.",
        MoveEffect.ReflectSide => "Raises a reflective barrier that softens physical damage for five turns.",
        MoveEffect.LightScreenSide => "Raises a light screen that softens special damage for five turns.",
        MoveEffect.AquaRing => "Veils the user in water, restoring a little HP at the end of each turn.",
        MoveEffect.Ingrain => "Roots the user in place, restoring a little HP at the end of each turn.",
        MoveEffect.LeechSeed => "Plants a seed that drains the target each turn and heals the user.",
        MoveEffect.Yawn => "Makes the target drowsy. It falls asleep at the end of the next turn.",
        MoveEffect.BellyDrum => "Cuts the user's HP in half to maximize Attack.",
        MoveEffect.Haze => "Eliminates all stat changes on both sides.",
        MoveEffect.CureUserStatus => "Cures the user's major status condition.",
        MoveEffect.Acupressure => "Sharply raises one of the user's battle stats at random.",
        MoveEffect.NoOp => "Has no battle effect. Sometimes doing nothing is the point.",
        MoveEffect.UserFaints => "Deals damage, but the user faints afterward.",
        MoveEffect.TrapTarget => "Prevents the target from fleeing or switching out.",
        MoveEffect.SmackDown => "Knocks a flying target to the ground, so Ground moves can hit it.",
        MoveEffect.DrainHalf => "The user recovers half of the damage this move deals.",
        MoveEffect.MultiHit => "Hits two to five times in a single turn.",
        MoveEffect.HighCrit => "Deals damage with a heightened critical-hit rate.",
        MoveEffect.Ohko => "Knocks out the target in one hit if it connects.",
        MoveEffect.LevelDamage => "Deals damage equal to the user's level.",
        MoveEffect.FixedDamage40 => "Always deals 40 HP of damage.",
        MoveEffect.FixedDamage20 => "Always deals 20 HP of damage.",
        MoveEffect.HalveTargetHp => "Deals damage equal to half the target's current HP.",
        MoveEffect.MustRecharge => "The user must spend the next turn recharging.",
        MoveEffect.RaiseAtkDef => $"Raises the user's Attack and Defense by {StageText()}.",
        MoveEffect.RaiseSpAtkSpDef => $"Raises the user's Sp. Atk and Sp. Def by {StageText()}.",
        MoveEffect.RaiseAtkSpd => $"Raises the user's Attack and Speed by {StageText()}.",
        _ => IsStatus
            ? "No direct battle effect is modelled for this move yet."
            : "Deals direct damage with no added effect.",
    };

    private string EffectText(string text) => EffectChance > 0 ? $"{text} ({EffectChance}% chance)" : text;
    private string StageText() => StageChange == 1 ? "one stage" : $"{StageChange} stages";
    private string ChanceText(string guaranteed, string possible) =>
        EffectChance >= 100 ? guaranteed : EffectChance > 0 ? $"{possible} ({EffectChance}% chance)" : guaranteed;
}
