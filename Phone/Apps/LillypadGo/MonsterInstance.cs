namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal enum Status : byte
{
    None,
    Burn,
    Freeze,
    Paralysis,
    Poison,
}

// A concrete creature: a species at a level with its own HP, moves, XP and battle buffs.
internal sealed class MonsterInstance
{
    private const int MaxMoves = 4;

    public MonsterInstance(MonsterSpecies species, int level)
    {
        Species = species;
        Level = Math.Clamp(level, 1, 100);
        RecomputeStats();
        CurrentHp = MaxHp;
        Moves = new List<MoveDef>(MaxMoves);
        Pp = new List<int>(MaxMoves);
        RelearnForLevel();
    }

    public MonsterSpecies Species { get; private set; }
    public int Level { get; private set; }
    public int Xp { get; private set; }
    public int MaxHp { get; private set; }
    public int Atk { get; private set; }
    public int Def { get; private set; }
    public int SpAtk { get; private set; }
    public int SpDef { get; private set; }
    public int Spd { get; private set; }
    public int CurrentHp { get; set; }
    public Status Status { get; set; }
    public string Nickname { get; private set; } = string.Empty;
    public bool IsFavorite { get; private set; }
    public int Battles { get; private set; }
    public int Victories { get; private set; }
    public int DamageDealt { get; private set; }
    public List<MoveDef> Moves { get; }
    public List<int> Pp { get; }

    public int AtkStage { get; set; }
    public int DefStage { get; set; }
    public int SpdStage { get; set; }
    public int SpAtkStage { get; set; }
    public int SpDefStage { get; set; }
    public int AccuracyStage { get; set; }
    public int EvasionStage { get; set; }
    public bool Flinched { get; set; }
    public int ConfusionTurns { get; set; }

    public bool Fainted => CurrentHp <= 0;
    public string Name => string.IsNullOrWhiteSpace(Nickname) ? Species.Name : Nickname;
    public Element Element => Species.Element;
    public Element? SecondaryElement => Species.SecondaryElement;
    public int XpToNext => Level * 25;
    public float HpFraction => MaxHp <= 0 ? 0f : Math.Clamp(CurrentHp / (float)MaxHp, 0f, 1f);
    public float XpFraction => Level >= 100 || XpToNext <= 0 ? 1f : Math.Clamp(Xp / (float)XpToNext, 0f, 1f);

    public int EffectiveAtk => Staged(Atk, AtkStage);
    public int EffectiveDef => Staged(Def, DefStage);
    public int EffectiveSpAtk => Staged(SpAtk, SpAtkStage);
    public int EffectiveSpDef => Staged(SpDef, SpDefStage);
    public int EffectiveSpd => Status == Status.Paralysis ? Math.Max(1, Staged(Spd, SpdStage) / 2) :
        Staged(Spd, SpdStage);

    public bool HasType(Element type) => Species.HasType(type);

    public int OffensiveStat(MoveCategory category, bool critical)
    {
        var stat = category == MoveCategory.Special ? SpAtk : Atk;
        var stage = category == MoveCategory.Special ? SpAtkStage : AtkStage;
        return Staged(stat, critical && stage < 0 ? 0 : stage);
    }

    public int DefensiveStat(MoveCategory category, bool critical)
    {
        var stat = category == MoveCategory.Special ? SpDef : Def;
        var stage = category == MoveCategory.Special ? SpDefStage : DefStage;
        return Staged(stat, critical && stage > 0 ? 0 : stage);
    }

    public void ResetBattleState()
    {
        AtkStage = 0;
        DefStage = 0;
        SpdStage = 0;
        SpAtkStage = 0;
        SpDefStage = 0;
        AccuracyStage = 0;
        EvasionStage = 0;
        Flinched = false;
        ConfusionTurns = 0;
    }

    // Restores persisted state (moves already resolved by the caller to avoid the static
    // Moves registry being shadowed by this type's Moves property).
    public void Restore(int xp, int currentHp, Status status, IReadOnlyList<MoveDef> moves, IReadOnlyList<int> pp)
    {
        Xp = Math.Max(0, xp);
        Status = status;
        Moves.Clear();
        Pp.Clear();
        for (var i = 0; i < moves.Count && Moves.Count < MaxMoves; i++)
        {
            Moves.Add(moves[i]);
            Pp.Add(i < pp.Count ? Math.Clamp(pp[i], 0, moves[i].Pp) : moves[i].Pp);
        }

        if (Moves.Count == 0)
        {
            RelearnForLevel();
        }

        CurrentHp = Math.Clamp(currentHp, 0, MaxHp);
    }

    public void RestoreProfile(string? nickname, bool favorite, int battles, int victories, int damageDealt)
    {
        Rename(nickname ?? string.Empty);
        IsFavorite = favorite;
        Battles = Math.Max(0, battles);
        Victories = Math.Clamp(victories, 0, Battles);
        DamageDealt = Math.Max(0, damageDealt);
    }

    public void Rename(string nickname)
    {
        var trimmed = nickname.Trim();
        Nickname = trimmed.Length > 20 ? trimmed[..20] : trimmed;
    }

    public void ToggleFavorite() => IsFavorite = !IsFavorite;

    public void RecordBattle(bool victory)
    {
        Battles++;
        if (victory)
        {
            Victories++;
        }
    }

    public void RecordDamage(int amount) => DamageDealt += Math.Max(0, amount);

    public void Heal(int amount) => CurrentHp = Math.Min(MaxHp, CurrentHp + Math.Max(0, amount));

    // Brings a fainted creature back. Revive restores half HP; Max-style items restore full.
    public void Revive(bool full)
    {
        if (!Fainted)
        {
            return;
        }

        Status = Status.None;
        CurrentHp = full ? MaxHp : Math.Max(1, MaxHp / 2);
    }

    public void CureStatus() => Status = Status.None;

    public void FullHeal()
    {
        CurrentHp = MaxHp;
        Status = Status.None;
        for (var i = 0; i < Pp.Count && i < Moves.Count; i++)
        {
            Pp[i] = Moves[i].Pp;
        }
    }

    // Returns the list of newly learned moves (for the battle log). `evolutions` collects any
    // level-up evolution announcements ("Bulbasaur evolved into Ivysaur!").
    public List<MoveDef> GainXp(int amount, out List<MoveDef> pendingMoves, out List<string> evolutions)
    {
        var learned = new List<MoveDef>();
        pendingMoves = new List<MoveDef>();
        evolutions = new List<string>();
        Xp += Math.Max(0, amount);
        while (Xp >= XpToNext && Level < 100)
        {
            Xp -= XpToNext;
            Level++;
            var beforeHp = MaxHp;
            RecomputeStats();
            CurrentHp += MaxHp - beforeHp;
            foreach (var (lvl, move) in Species.Learnset)
            {
                if (lvl == Level)
                {
                    if (Moves.Contains(move))
                    {
                        continue;
                    }

                    if (TryLearn(move))
                    {
                        learned.Add(move);
                    }
                    else
                    {
                        pendingMoves.Add(move);
                    }
                }
            }

            TryEvolve(evolutions);
        }

        return learned;
    }

    // Applies level-up evolutions (possibly several in a chain if many levels were gained at once).
    private void TryEvolve(List<string> evolutions)
    {
        while (Species.EvolveLevel > 0 && Level >= Species.EvolveLevel && Dex.EvolutionOf(Species) is { } next)
        {
            var before = Name;
            var beforeHp = MaxHp;
            Species = next;
            RecomputeStats();
            if (CurrentHp > 0)
            {
                CurrentHp = Math.Min(MaxHp, CurrentHp + (MaxHp - beforeHp));
            }

            evolutions.Add($"{before} evolved into {Species.Name}!");
        }
    }

    public void ReplaceMove(int index, MoveDef move)
    {
        if (index < 0 || index >= Moves.Count || Moves.Contains(move))
        {
            return;
        }

        Moves[index] = move;
        Pp[index] = move.Pp;
    }

    // Adds a move into a free slot (used by the out-of-battle move relearner). Returns false if the
    // move is already known or all four slots are full.
    public bool AddMove(MoveDef move)
    {
        if (Moves.Contains(move) || Moves.Count >= MaxMoves)
        {
            return false;
        }

        Moves.Add(move);
        Pp.Add(move.Pp);
        return true;
    }

    public bool Knows(MoveDef move) => Moves.Contains(move);

    private bool TryLearn(MoveDef move)
    {
        if (Moves.Contains(move))
        {
            return false;
        }

        if (Moves.Count < MaxMoves)
        {
            Moves.Add(move);
            Pp.Add(move.Pp);
            return true;
        }

        return false;
    }

    private void RelearnForLevel()
    {
        var available = Species.Learnset.Where(entry => entry.Level <= Level).Select(entry => entry.Move)
            .Distinct().TakeLast(MaxMoves);
        foreach (var move in available)
        {
            TryLearn(move);
        }

        if (Moves.Count == 0 && Species.Learnset.Length > 0)
        {
            TryLearn(Species.Learnset[0].Move);
        }
    }

    private void RecomputeStats()
    {
        // The original compact base values are scaled into Pokemon-like base stats,
        // then run through the standard no-EV/no-nature stat formulas.
        MaxHp = StatHp(Species.BaseHp * 4, Level);
        Atk = StatOther(Species.BaseAtk * 4, Level);
        Def = StatOther(Species.BaseDef * 4, Level);
        SpAtk = StatOther(Species.BaseSpAtk * 4, Level);
        SpDef = StatOther(Species.BaseSpDef * 4, Level);
        Spd = StatOther(Species.BaseSpd * 4, Level);
    }

    private static int StatHp(int baseStat, int level) => (2 * baseStat * level / 100) + level + 10;

    private static int StatOther(int baseStat, int level) => (2 * baseStat * level / 100) + 5;

    private static int Staged(int stat, int stage)
    {
        if (stage >= 0)
        {
            return (int)(stat * ((2f + stage) / 2f));
        }

        return (int)(stat * (2f / (2f - stage)));
    }
}
