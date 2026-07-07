namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal enum MoveEffect : byte
{
    None,
    RaiseAtk,
    RaiseDef,
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
    Paralyze,
    Poison,
    Flinch,
    RecoilQuarterMax,
    Confuse,
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
        MoveEffect.RaiseEvasion => $"Raises the user's evasiveness by {StageText()}.",
        MoveEffect.HealUser => "Restores half of the user's maximum HP.",
        MoveEffect.Burn => EffectText("May burn the target, causing damage after each turn."),
        MoveEffect.Freeze => EffectText("May freeze the target and prevent it from acting."),
        MoveEffect.Paralyze => EffectText("May paralyze the target, lowering Speed and sometimes preventing action."),
        MoveEffect.Poison => EffectText("May poison the target, causing damage after each turn."),
        MoveEffect.Flinch => EffectText("May make the target flinch before it can act."),
        MoveEffect.RecoilQuarterMax => "Damages the user by one quarter of its maximum HP.",
        MoveEffect.Confuse => EffectText("May confuse the target and make it hurt itself."),
        _ => "Deals direct damage with no added effect.",
    };

    private string EffectText(string text) => EffectChance > 0 ? $"{text} ({EffectChance}% chance)" : text;
    private string StageText() => StageChange == 1 ? "one stage" : $"{StageChange} stages";
    private string ChanceText(string guaranteed, string possible) =>
        EffectChance >= 100 ? guaranteed : EffectChance > 0 ? $"{possible} ({EffectChance}% chance)" : guaranteed;
}
