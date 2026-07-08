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
        int effectChance = 0, MoveCategory category = MoveCategory.Physical, int priority = 0, int stageChange = 1)
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

    public bool IsStatus => Power <= 0;

    public string CategoryLabel => Category.ToString();

    public string Description => Effect switch
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
        _ => IsStatus
            ? "No direct battle effect is modelled for this move yet."
            : "Deals direct damage with no added effect.",
    };

    private string EffectText(string text) => EffectChance > 0 ? $"{text} ({EffectChance}% chance)" : text;
    private string StageText() => StageChange == 1 ? "one stage" : $"{StageChange} stages";
    private string ChanceText(string guaranteed, string possible) =>
        EffectChance >= 100 ? guaranteed : EffectChance > 0 ? $"{possible} ({EffectChance}% chance)" : guaranteed;
}
