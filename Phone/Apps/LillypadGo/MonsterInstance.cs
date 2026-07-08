namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal enum Status : byte
{
    None,
    Burn,
    Freeze,
    Paralysis,
    Poison,
}

internal enum Gender : byte
{
    Genderless,
    Male,
    Female,
}

// A concrete creature: a species at a level with its own HP, moves, XP and battle buffs.
internal sealed class MonsterInstance
{
    private const int MaxMoves = 4;
    private static readonly Random Rng = new();

    // Stat indices for the IV/EV arrays.
    private const int Hp = 0, At = 1, Df = 2, Sa = 3, Sd = 4, Sp = 5;

    public MonsterInstance(MonsterSpecies species, int level)
    {
        Species = species;
        Level = Math.Clamp(level, 1, 100);
        Ivs = new int[6];
        Evs = new int[6];
        for (var i = 0; i < 6; i++)
        {
            Ivs[i] = Rng.Next(0, 32);
        }

        Gender = RollGender(species);
        Ability = RollAbility(species);
        RecomputeStats();
        CurrentHp = MaxHp;
        Moves = new List<MoveDef>(MaxMoves);
        Pp = new List<int>(MaxMoves);
        RelearnForLevel();
    }

    private static Gender RollGender(MonsterSpecies species) => species.Genderless
        ? Gender.Genderless
        : Rng.NextDouble() < species.MaleRatio ? Gender.Male : Gender.Female;

    private static string RollAbility(MonsterSpecies species)
    {
        // Pick a regular ability (Showdown lists the hidden ability last in a 3-entry set).
        var regular = species.Abilities.Length == 3 ? 2 : species.Abilities.Length;
        return species.Abilities[Rng.Next(Math.Max(1, regular))];
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

    // SV genetics: per-stat IVs (0-31, fixed) and EVs (0-252, earned), plus ability and gender.
    public int[] Ivs { get; private set; }
    public int[] Evs { get; private set; }
    public string Ability { get; private set; } = "Pressure";
    public Gender Gender { get; private set; }

    public int EvTotal
    {
        get
        {
            var total = 0;
            foreach (var ev in Evs)
            {
                total += ev;
            }

            return total;
        }
    }

    public string GenderSymbol => Gender switch
    {
        Gender.Male => "♂",
        Gender.Female => "♀",
        _ => string.Empty,
    };

    public int AtkStage { get; set; }
    public int DefStage { get; set; }
    public int SpdStage { get; set; }
    public int SpAtkStage { get; set; }
    public int SpDefStage { get; set; }
    public int AccuracyStage { get; set; }
    public int EvasionStage { get; set; }
    public bool Flinched { get; set; }
    public int ConfusionTurns { get; set; }
    public bool FlashFireActive { get; set; } // Flash Fire absorbed a Fire move this battle

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
    public int EffectiveSpd
    {
        get
        {
            var staged = Staged(Spd, SpdStage);
            // Quick Feet: 1.5x Speed while statused, ignoring paralysis' speed cut.
            if (Ability == "Quick Feet" && Status != Status.None)
            {
                return Math.Max(1, (int)(staged * 1.5f));
            }

            return Status == Status.Paralysis ? Math.Max(1, staged / 2) : staged;
        }
    }

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
        FlashFireActive = false;
        RevertTransform(); // a copy from Transform is lost on switch/at battle start
    }

    // ---- Transform (Ditto) ----------------------------------------------------------

    public bool IsTransformed { get; private set; }
    private (int, int, int, int, int, int)? transformedStats;
    private MonsterSpecies? preTransformSpecies;
    private List<MoveDef>? preTransformMoves;
    private List<int>? preTransformPp;
    private string? preTransformAbility;

    // Copies another creature's species, stats, moves and ability for the rest of the battle. HP is
    // kept; a transformed creature reverts on switch-out or when the battle ends.
    public void Transform(MonsterInstance target)
    {
        if (IsTransformed || ReferenceEquals(target, this))
        {
            return;
        }

        preTransformSpecies = Species;
        preTransformMoves = new List<MoveDef>(Moves);
        preTransformPp = new List<int>(Pp);
        preTransformAbility = Ability;

        Species = target.Species;
        Ability = target.Ability;
        // Keep our own HP base; copy the target's offensive/defensive/speed bases.
        transformedStats = (preTransformSpecies.BaseHp, target.Species.BaseAtk, target.Species.BaseDef,
            target.Species.BaseSpAtk, target.Species.BaseSpDef, target.Species.BaseSpd);

        Moves.Clear();
        Pp.Clear();
        foreach (var move in target.Moves)
        {
            Moves.Add(move);
            Pp.Add(Math.Min(5, move.Pp));
        }

        if (Moves.Count == 0)
        {
            RelearnForLevel();
        }

        IsTransformed = true;
        RecomputeStats();
    }

    public void RevertTransform()
    {
        if (!IsTransformed)
        {
            return;
        }

        Species = preTransformSpecies!;
        Ability = preTransformAbility ?? Ability;
        Moves.Clear();
        Moves.AddRange(preTransformMoves!);
        Pp.Clear();
        Pp.AddRange(preTransformPp!);
        transformedStats = null;
        IsTransformed = false;
        RecomputeStats();
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

    // Restores persisted SV genetics; recomputes stats with the loaded IVs/EVs.
    public void RestoreGenetics(int[]? ivs, int[]? evs, string? ability, Gender? gender)
    {
        if (ivs is { Length: 6 })
        {
            for (var i = 0; i < 6; i++)
            {
                Ivs[i] = Math.Clamp(ivs[i], 0, 31);
            }
        }

        if (evs is { Length: 6 })
        {
            for (var i = 0; i < 6; i++)
            {
                Evs[i] = Math.Clamp(evs[i], 0, 252);
            }
        }

        if (!string.IsNullOrEmpty(ability) && Array.IndexOf(Species.Abilities, ability) >= 0)
        {
            Ability = ability;
        }

        if (gender is { } g)
        {
            Gender = Species.Genderless ? Gender.Genderless : g;
        }

        RecomputeStats();
        CurrentHp = MaxHp; // the following Restore() sets the persisted current HP against these stats
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

    // Reorders the moveset (used by the Team screen's drag-and-drop move layout).
    public void SwapMoves(int a, int b)
    {
        if (a < 0 || b < 0 || a >= Moves.Count || b >= Moves.Count || a == b)
        {
            return;
        }

        (Moves[a], Moves[b]) = (Moves[b], Moves[a]);
        (Pp[a], Pp[b]) = (Pp[b], Pp[a]);
    }

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
        // Standard mainline (SV) stat formulas with real base stats, per-stat IVs and EVs, neutral nature.
        var b = transformedStats ?? (Species.BaseHp, Species.BaseAtk, Species.BaseDef, Species.BaseSpAtk,
            Species.BaseSpDef, Species.BaseSpd);
        MaxHp = StatHp(b.Item1, Ivs[Hp], Evs[Hp], Level);
        Atk = StatOther(b.Item2, Ivs[At], Evs[At], Level);
        Def = StatOther(b.Item3, Ivs[Df], Evs[Df], Level);
        SpAtk = StatOther(b.Item4, Ivs[Sa], Evs[Sa], Level);
        SpDef = StatOther(b.Item5, Ivs[Sd], Evs[Sd], Level);
        Spd = StatOther(b.Item6, Ivs[Sp], Evs[Sp], Level);
    }

    private static int StatHp(int baseStat, int iv, int ev, int level) =>
        ((2 * baseStat + iv + ev / 4) * level / 100) + level + 10;

    private static int StatOther(int baseStat, int iv, int ev, int level) =>
        ((2 * baseStat + iv + ev / 4) * level / 100) + 5;

    // Awards EVs from defeating a species (its two highest base stats, mainline-style), capped.
    public void GainEvs(MonsterSpecies defeated)
    {
        if (EvTotal >= 510)
        {
            return;
        }

        var bases = new (int Stat, int Value)[]
        {
            (Hp, defeated.BaseHp), (At, defeated.BaseAtk), (Df, defeated.BaseDef),
            (Sa, defeated.BaseSpAtk), (Sd, defeated.BaseSpDef), (Sp, defeated.BaseSpd),
        };
        Array.Sort(bases, (x, y) => y.Value.CompareTo(x.Value));
        AddEv(bases[0].Stat, 2);
        AddEv(bases[1].Stat, 1);
        RecomputeStats();
    }

    private void AddEv(int stat, int amount)
    {
        if (EvTotal >= 510)
        {
            return;
        }

        Evs[stat] = Math.Min(252, Evs[stat] + Math.Min(amount, 510 - EvTotal));
    }

    private static int Staged(int stat, int stage)
    {
        if (stage >= 0)
        {
            return (int)(stat * ((2f + stage) / 2f));
        }

        return (int)(stat * (2f / (2f - stage)));
    }
}
