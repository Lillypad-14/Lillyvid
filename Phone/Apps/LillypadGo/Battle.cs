namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal enum BattleCue : byte
{
    Info,
    PlayerAttack,
    WildAttack,
    PlayerHurt,
    WildHurt,
    PlayerFaint,
    PlayerSwitch,
    WildFaint,
    EnemySwitch,
    Buff,
    Heal,
    CaptureShake,
    Captured,
    CaptureFail,
    Fled,
    FleeFail,
    XpGain,
    LevelUp,
    Evolve,
}

internal enum BattleOutcome : byte
{
    Ongoing,
    Won,
    Captured,
    Fled,
    Whiteout,
}

internal enum BattleWeather : byte
{
    None,
    Sun,
    Rain,
    Sandstorm,
    Snow,
}

internal enum BattleTerrain : byte
{
    None,
    Electric,
    Grassy,
    Misty,
    Psychic,
}

internal readonly struct BattleMessage
{
    public BattleMessage(string text, BattleCue cue, MonsterInstance? subject = null, int? hpAfter = null,
        bool critical = false, float effectiveness = 1f, MoveDef? move = null, BattleSnapshot? stateAfter = null)
    {
        Text = text;
        Cue = cue;
        Subject = subject;
        HpAfter = hpAfter;
        Critical = critical;
        Effectiveness = effectiveness;
        Move = move;
        StateAfter = stateAfter;
    }

    public string Text { get; }
    public BattleCue Cue { get; }
    public MonsterInstance? Subject { get; }
    public int? HpAfter { get; }
    public bool Critical { get; }
    public float Effectiveness { get; }
    public MoveDef? Move { get; }
    public BattleSnapshot? StateAfter { get; }
}

internal readonly struct BattleSnapshot
{
    private BattleSnapshot(MonsterInstance monster)
    {
        Hp = monster.CurrentHp;
        Status = monster.Status;
        AtkStage = monster.AtkStage;
        DefStage = monster.DefStage;
        SpAtkStage = monster.SpAtkStage;
        SpDefStage = monster.SpDefStage;
        SpdStage = monster.SpdStage;
        Level = monster.Level;
        XpFraction = monster.XpFraction;
    }

    public int Hp { get; }
    public Status Status { get; }
    public int AtkStage { get; }
    public int DefStage { get; }
    public int SpAtkStage { get; }
    public int SpDefStage { get; }
    public int SpdStage { get; }
    public int Level { get; }
    public float XpFraction { get; }

    public static BattleSnapshot Capture(MonsterInstance monster) => new(monster);
}

internal readonly record struct MoveLearnChoice(MonsterInstance Monster, MoveDef Move);

// Turn-based battle engine using modern mainline-style type, stat, damage and status rules.
internal sealed class Battle
{
    private readonly List<MonsterInstance> party;
    private readonly Bag bag;
    private readonly Random rng;
    private readonly HashSet<MonsterInstance> participants = new();
    private readonly Queue<MoveLearnChoice> pendingMoveChoices = new();
    private readonly IReadOnlyList<MonsterInstance>? enemyTeam;
    private readonly int trainerPrize;
    private int enemyIndex;
    private int activeIndex;
    private bool statsFinalized;
    private int weatherTurns;
    private int terrainTurns;
    private int playerReflectTurns;
    private int wildReflectTurns;
    private int playerLightScreenTurns;
    private int wildLightScreenTurns;

    // Wild encounter: a lone opponent that can be caught.
    public Battle(List<MonsterInstance> party, MonsterInstance wild, Bag bag, Random rng)
        : this(party, bag, rng)
    {
        Wild = wild;
        Wild.ResetBattleState();
        Log.Enqueue(new BattleMessage($"A wild {Wild.Name} appeared!", BattleCue.Info));
        ApplyEntryAbility(Active, Wild);
        ApplyEntryAbility(Wild, Active);
    }

    // Trainer or gym battle: the opponent fields a team of 1-6 and their Pokémon cannot be caught.
    public Battle(List<MonsterInstance> party, IReadOnlyList<MonsterInstance> enemyTeam, string trainerName,
        int prize, Bag bag, Random rng)
        : this(party, bag, rng)
    {
        this.enemyTeam = enemyTeam;
        this.trainerPrize = prize;
        TrainerName = trainerName;
        Wild = enemyTeam[0];
        Wild.ResetBattleState();
        Log.Enqueue(new BattleMessage($"{trainerName} wants to battle!", BattleCue.Info));
        Log.Enqueue(new BattleMessage($"{trainerName} sent out {Wild.Name}!", BattleCue.EnemySwitch, Wild,
            stateAfter: BattleSnapshot.Capture(Wild)));
        ApplyEntryAbility(Active, Wild);
        ApplyEntryAbility(Wild, Active);
    }

    private Battle(List<MonsterInstance> party, Bag bag, Random rng)
    {
        this.party = party;
        this.bag = bag;
        this.rng = rng;
        activeIndex = party.FindIndex(m => !m.Fainted);
        if (activeIndex < 0)
        {
            activeIndex = 0;
        }

        Active.ResetBattleState();
        participants.Add(Active);
    }

    public MonsterInstance Wild { get; private set; } = null!;
    public MonsterInstance Active => party[activeIndex];
    public IReadOnlyList<MonsterInstance> Party => party;
    public int ActiveIndex => activeIndex;
    public Queue<BattleMessage> Log { get; } = new();
    public BattleOutcome Outcome { get; private set; } = BattleOutcome.Ongoing;
    public BattleWeather Weather { get; private set; }
    public BattleTerrain Terrain { get; private set; }
    public MonsterInstance? Captured { get; private set; }
    public int PrizeMoney { get; private set; }
    public string? TrainerName { get; }
    public bool IsTrainerBattle => enemyTeam is not null;
    public bool CanCatch => enemyTeam is null;
    public MoveLearnChoice? PendingMoveChoice => pendingMoveChoices.Count > 0 ? pendingMoveChoices.Peek() : null;

    public bool RequiresSwitch { get; private set; }
    public bool RequiresEnemySend { get; private set; }
    public bool CanAct => Outcome == BattleOutcome.Ongoing && !RequiresSwitch && !RequiresEnemySend;

    // Sends out the trainer's next Pokémon. Called by the UI once the previous faint has finished
    // animating, so the display swaps cleanly instead of mid-message.
    public void SendNextEnemy()
    {
        if (!RequiresEnemySend || enemyTeam is null)
        {
            return;
        }

        RequiresEnemySend = false;
        enemyIndex++;
        Wild = enemyTeam[enemyIndex];
        Wild.ResetBattleState();
        Log.Enqueue(new BattleMessage($"{TrainerName} sent out {Wild.Name}!", BattleCue.EnemySwitch, Wild,
            stateAfter: BattleSnapshot.Capture(Wild)));
        ApplyEntryAbility(Wild, Active);
    }

    public float EscapeChance
    {
        get
        {
            var activeSpeed = EffectiveSpeed(Active);
            var wildSpeed = EffectiveSpeed(Wild);
            var chance = activeSpeed >= wildSpeed
                ? 0.9f
                : 0.35f + activeSpeed / (float)(wildSpeed + 1) * 0.4f;
            return Math.Clamp(chance, 0f, 1f);
        }
    }

    // The odds a given Ball captures the wild creature right now (accounts for its catch bonus).
    public float CaptureChanceWith(ItemDef ball)
    {
        var rate = CaptureCheckChance(ball.CatchBonus);
        return Math.Clamp(rate + rate * rate * rate - rate * rate * rate * rate, 0f, 1f);
    }

    // ---- Player actions -------------------------------------------------------------

    public void UseMove(int moveIndex)
    {
        var struggling = moveIndex < 0 && Active.Pp.All(value => value <= 0);
        if (!CanAct || (!struggling && (moveIndex < 0 || moveIndex >= Active.Moves.Count)))
        {
            return;
        }

        Active.Flinched = false;
        Wild.Flinched = false;
        Active.Protecting = false;
        Wild.Protecting = false;
        Active.Enduring = false;
        Wild.Enduring = false;
        var playerMove = struggling ? Moves.Struggle : Active.Moves[moveIndex];
        var wildMove = ChooseWildMove();
        var playerFirst = playerMove.Priority > wildMove.Priority ||
            playerMove.Priority == wildMove.Priority && (EffectiveSpeed(Active) > EffectiveSpeed(Wild) ||
                EffectiveSpeed(Active) == EffectiveSpeed(Wild) && rng.Next(2) == 0);
        if (playerFirst)
        {
            PlayerMove(moveIndex);
            if (WildDown())
            {
                return;
            }

            if (PlayerDown())
            {
                return;
            }

            WildTurn(wildMove);
            if (WildDown())
            {
                return;
            }

            if (PlayerDown())
            {
                return;
            }
        }
        else
        {
            WildTurn(wildMove);
            if (WildDown())
            {
                return;
            }

            if (PlayerDown())
            {
                return;
            }

            PlayerMove(moveIndex);
            WildDown();
        }

        EndOfTurn();
    }

    public void ResolveMoveChoice(int? replaceIndex)
    {
        if (pendingMoveChoices.Count == 0)
        {
            return;
        }

        var choice = pendingMoveChoices.Dequeue();
        if (replaceIndex is { } index && index >= 0 && index < choice.Monster.Moves.Count)
        {
            var forgotten = choice.Monster.Moves[index];
            choice.Monster.ReplaceMove(index, choice.Move);
            Log.Enqueue(new BattleMessage($"{choice.Monster.Name} forgot {forgotten.Name} and learned {choice.Move.Name}!",
                BattleCue.LevelUp, choice.Monster));
        }
        else
        {
            Log.Enqueue(new BattleMessage($"{choice.Monster.Name} kept its current moves.", BattleCue.Info,
                choice.Monster));
        }
    }

    public void Switch(int partyIndex)
    {
        if (Outcome != BattleOutcome.Ongoing || partyIndex < 0 || partyIndex >= party.Count || partyIndex == activeIndex ||
            party[partyIndex].Fainted)
        {
            return;
        }

        var forced = RequiresSwitch;
        RequiresSwitch = false;
        OnSwitchOut(Active); // Natural Cure / Regenerator when leaving the field
        Active.ResetBattleState();
        activeIndex = partyIndex;
        participants.Add(Active);
        Log.Enqueue(new BattleMessage($"Go, {Active.Name}!", BattleCue.PlayerSwitch, Active, Active.CurrentHp,
            stateAfter: BattleSnapshot.Capture(Active)));
        ApplyEntryAbility(Active, Wild);
        if (forced)
        {
            return;
        }

        WildTurn();
        if (PlayerDown())
        {
            return;
        }

        EndOfTurn();
    }

    // Whether an item can currently be used in battle (drives button enabling + tooltips).
    public bool CanUseItem(ItemDef item)
    {
        if (!CanAct || !bag.Has(item.Id))
        {
            return false;
        }

        return item.Category switch
        {
            ItemCategory.Ball => !IsTrainerBattle,
            ItemCategory.Potion => !Active.Fainted && Active.CurrentHp < Active.MaxHp,
            ItemCategory.Revive => party.Any(monster => monster.Fainted),
            ItemCategory.StatusHeal => item.CuresAllStatus
                ? Active.Status != Status.None
                : Active.Status == item.CuresStatus,
            _ => false,
        };
    }

    public void UseItem(ItemDef item)
    {
        if (!CanUseItem(item))
        {
            return;
        }

        if (item.Category == ItemCategory.Ball)
        {
            UseBall(item);
            return;
        }

        bag.Consume(item.Id);
        switch (item.Category)
        {
            case ItemCategory.Potion:
                var before = Active.CurrentHp;
                Active.Heal(item.RestoresFullHp ? Active.MaxHp : item.HealAmount);
                Log.Enqueue(new BattleMessage($"{Active.Name} was healed for {Active.CurrentHp - before} HP.",
                    BattleCue.Heal, Active, Active.CurrentHp, stateAfter: BattleSnapshot.Capture(Active)));
                break;
            case ItemCategory.StatusHeal:
                Active.CureStatus();
                Log.Enqueue(new BattleMessage($"{Active.Name}'s status returned to normal.", BattleCue.Heal,
                    Active, Active.CurrentHp, stateAfter: BattleSnapshot.Capture(Active)));
                break;
            case ItemCategory.Revive:
                var target = party.FirstOrDefault(monster => monster.Fainted);
                if (target is null)
                {
                    return;
                }

                target.Revive(item.RevivesToFull);
                Log.Enqueue(new BattleMessage($"{target.Name} was revived and is ready to battle!", BattleCue.Heal,
                    target, target.CurrentHp, stateAfter: BattleSnapshot.Capture(target)));
                break;
        }

        WildTurn();
        if (PlayerDown())
        {
            return;
        }

        EndOfTurn();
    }

    private void UseBall(ItemDef ball)
    {
        if (!CanAct || IsTrainerBattle || !bag.Has(ball.Id))
        {
            return;
        }

        bag.Consume(ball.Id);
        Log.Enqueue(new BattleMessage($"You used one {ball.Name}!", BattleCue.Info));

        var rate = CaptureCheckChance(ball.CatchBonus);
        var shakes = 0;
        for (var i = 0; i < 3; i++)
        {
            if (rng.NextDouble() < rate)
            {
                shakes++;
            }
            else
            {
                break;
            }
        }

        for (var i = 0; i < shakes; i++)
        {
            Log.Enqueue(new BattleMessage("...", BattleCue.CaptureShake));
        }

        if (rng.NextDouble() < rate || shakes >= 3)
        {
            Wild.ResetBattleState();
            Captured = Wild;
            Outcome = BattleOutcome.Captured;
            Log.Enqueue(new BattleMessage($"Gotcha! {Wild.Name} was caught!", BattleCue.Captured));
            return;
        }

        Log.Enqueue(new BattleMessage($"Argh! {Wild.Name} broke free!", BattleCue.CaptureFail));
        WildTurn();
        if (PlayerDown())
        {
            return;
        }

        EndOfTurn();
    }

    public void Run()
    {
        if (!CanAct)
        {
            return;
        }

        // You can always flee a trainer/gym fight, but forfeiting counts as a loss: no badge,
        // money or end-of-battle spoils (any XP already earned from KOs this fight is kept).
        if (IsTrainerBattle)
        {
            Outcome = BattleOutcome.Fled;
            Log.Enqueue(new BattleMessage($"You forfeited the battle against {TrainerName}.", BattleCue.Fled));
            return;
        }

        if (rng.NextDouble() < EscapeChance)
        {
            Outcome = BattleOutcome.Fled;
            Log.Enqueue(new BattleMessage("Got away safely!", BattleCue.Fled));
            return;
        }

        Log.Enqueue(new BattleMessage("Couldn't escape!", BattleCue.FleeFail));
        WildTurn();
        if (PlayerDown())
        {
            return;
        }

        EndOfTurn();
    }

    // ---- Resolution -----------------------------------------------------------------

    private void PlayerMove(int moveIndex)
    {
        if (!TryAct(Active))
        {
            return;
        }

        var struggling = moveIndex < 0;
        var move = struggling ? Moves.Struggle : Active.Moves[moveIndex];
        if (!struggling && Active.Pp[moveIndex] <= 0)
        {
            Log.Enqueue(new BattleMessage($"{Active.Name} has no power left for {move.Name}!", BattleCue.Info));
            return;
        }

        if (!struggling)
        {
            Active.Pp[moveIndex]--;
        }
        Execute(Active, Wild, move, BattleCue.PlayerAttack, BattleCue.WildHurt);
    }

    private void WildTurn(MoveDef? selectedMove = null)
    {
        if (!CanAct || Wild.Fainted)
        {
            return;
        }

        if (!TryAct(Wild))
        {
            return;
        }

        var move = selectedMove ?? ChooseWildMove();
        var moveIndex = Wild.Moves.IndexOf(move);
        if (moveIndex >= 0 && moveIndex < Wild.Pp.Count && Wild.Pp[moveIndex] > 0)
        {
            Wild.Pp[moveIndex]--;
        }
        Execute(Wild, Active, move, BattleCue.WildAttack, BattleCue.PlayerHurt);
    }

    private bool TryAct(MonsterInstance monster)
    {
        if (monster.Flinched)
        {
            monster.Flinched = false;
            Log.Enqueue(new BattleMessage($"{monster.Name} flinched and couldn't move!", BattleCue.Info, monster));
            return false;
        }

        if (monster.Status == Status.Freeze)
        {
            if (rng.NextDouble() < 0.2)
            {
                monster.Status = Status.None;
                Log.Enqueue(new BattleMessage($"{monster.Name} thawed out!", BattleCue.Buff, monster,
                    stateAfter: BattleSnapshot.Capture(monster)));
            }
            else
            {
                Log.Enqueue(new BattleMessage($"{monster.Name} is frozen solid!", BattleCue.Info, monster));
                return false;
            }
        }

        if (monster.Status == Status.Sleep)
        {
            if (monster.SleepTurns <= 0)
            {
                monster.Status = Status.None;
                Log.Enqueue(new BattleMessage($"{monster.Name} woke up!", BattleCue.Buff, monster,
                    stateAfter: BattleSnapshot.Capture(monster)));
            }
            else
            {
                monster.SleepTurns--;
                Log.Enqueue(new BattleMessage($"{monster.Name} is fast asleep.", BattleCue.Info, monster));
                if (monster.SleepTurns <= 0)
                {
                    Log.Enqueue(new BattleMessage($"{monster.Name} woke up!", BattleCue.Buff, monster,
                        stateAfter: BattleSnapshot.Capture(monster)));
                    monster.Status = Status.None;
                }

                return false;
            }
        }

        if (monster.Status == Status.Paralysis && rng.NextDouble() < 0.25)
        {
            Log.Enqueue(new BattleMessage($"{monster.Name} is paralyzed and cannot move!", BattleCue.Info,
                monster));
            return false;
        }

        if (monster.ConfusionTurns > 0)
        {
            Log.Enqueue(new BattleMessage($"{monster.Name} is confused!", BattleCue.Info, monster));
            monster.ConfusionTurns--;
            if (rng.Next(3) == 0)
            {
                // Magic Guard prevents the confusion self-hit damage, but the turn is still lost.
                if (monster.Ability is not "Magic Guard")
                {
                    var raw = ((2f * monster.Level / 5f + 2f) * 40f * monster.EffectiveAtk /
                        Math.Max(1, monster.EffectiveDef) / 50f) + 2f;
                    var damage = Math.Min(monster.CurrentHp, Math.Max(1, (int)(raw *
                        (0.85f + (float)rng.NextDouble() * 0.15f))));
                    monster.CurrentHp -= damage;
                    var cue = ReferenceEquals(monster, Wild) ? BattleCue.WildHurt : BattleCue.PlayerHurt;
                    Log.Enqueue(new BattleMessage($"It hurt itself in its confusion! ({damage})", cue, monster,
                        monster.CurrentHp, stateAfter: BattleSnapshot.Capture(monster)));
                }
                else
                {
                    Log.Enqueue(new BattleMessage($"{monster.Name} is too confused to move!", BattleCue.Info,
                        monster));
                }

                return false;
            }

            if (monster.ConfusionTurns == 0)
            {
                Log.Enqueue(new BattleMessage($"{monster.Name} snapped out of its confusion!", BattleCue.Info,
                    monster));
            }
        }

        return true;
    }

    private float CaptureCheckChance(float ballBonus)
    {
        var hpTerm = (3f * Wild.MaxHp - 2f * Wild.CurrentHp) / (3f * Wild.MaxHp);
        var statusBonus = Wild.Status != Status.None ? 1.5f : 1f;
        return Math.Clamp(hpTerm * (Wild.Species.CatchRate / 255f) * ballBonus * statusBonus, 0.03f, 1f);
    }

    // Overgrow/Blaze/Torrent/Swarm: +50% to the matching type when the user is at/below 1/3 HP.
    private static float PinchAbilityBoost(MonsterInstance attacker, Element moveType)
    {
        if (attacker.CurrentHp * 3 > attacker.MaxHp)
        {
            return 1f;
        }

        return (attacker.Ability, moveType) switch
        {
            ("Overgrow", Element.Grass) => 1.5f,
            ("Blaze", Element.Fire) => 1.5f,
            ("Torrent", Element.Water) => 1.5f,
            ("Swarm", Element.Bug) => 1.5f,
            _ => 1f,
        };
    }

    private bool WeatherSuppressed => Active.Ability is "Cloud Nine" or "Air Lock" ||
        Wild.Ability is "Cloud Nine" or "Air Lock";

    private int EffectiveSpeed(MonsterInstance monster)
    {
        var speed = monster.EffectiveSpd;
        if (WeatherSuppressed)
        {
            return speed;
        }

        if ((Weather == BattleWeather.Sun && monster.Ability is "Chlorophyll") ||
            (Weather == BattleWeather.Rain && monster.Ability is "Swift Swim") ||
            (Weather == BattleWeather.Sandstorm && monster.Ability is "Sand Rush"))
        {
            speed *= 2;
        }

        return speed;
    }

    private float WeatherPowerMultiplier(Element moveType)
    {
        if (WeatherSuppressed)
        {
            return 1f;
        }

        return (Weather, moveType) switch
        {
            (BattleWeather.Sun, Element.Fire) => 1.5f,
            (BattleWeather.Sun, Element.Water) => 0.5f,
            (BattleWeather.Rain, Element.Water) => 1.5f,
            (BattleWeather.Rain, Element.Fire) => 0.5f,
            (BattleWeather.Sandstorm, Element.Rock or Element.Ground or Element.Steel) => 1.12f,
            (BattleWeather.Snow, Element.Ice) => 1.12f,
            _ => 1f,
        };
    }

    private float TerrainPowerMultiplier(Element moveType)
    {
        return (Terrain, moveType) switch
        {
            (BattleTerrain.Electric, Element.Electric) => 1.3f,
            (BattleTerrain.Grassy, Element.Grass) => 1.3f,
            (BattleTerrain.Misty, Element.Dragon) => 0.5f,
            (BattleTerrain.Psychic, Element.Psychic) => 1.3f,
            _ => 1f,
        };
    }

    private float ScreenDamageMultiplier(MonsterInstance defender, MoveCategory category, bool critical)
    {
        if (critical || category == MoveCategory.Status)
        {
            return 1f;
        }

        var playerSide = ReferenceEquals(defender, Active);
        if (category == MoveCategory.Physical && (playerSide ? playerReflectTurns : wildReflectTurns) > 0)
        {
            return 0.5f;
        }

        if (category == MoveCategory.Special && (playerSide ? playerLightScreenTurns : wildLightScreenTurns) > 0)
        {
            return 0.5f;
        }

        return 1f;
    }

    private void SetWeather(BattleWeather weather, string message)
    {
        Weather = weather;
        weatherTurns = weather == BattleWeather.None ? 0 : 5;
        Log.Enqueue(new BattleMessage(message, BattleCue.Buff));
    }

    private void SetTerrain(BattleTerrain terrain, string message)
    {
        Terrain = terrain;
        terrainTurns = terrain == BattleTerrain.None ? 0 : 5;
        Log.Enqueue(new BattleMessage(message, BattleCue.Buff));
    }

    // ---- Ability effects ------------------------------------------------------------

    private enum StatKind { Atk, Def, SpA, SpD, Spe, Acc, Eva }

    private static bool IsContact(MoveDef move) => !move.IsStatus && move.Category == MoveCategory.Physical;

    private static bool HasSecondary(MoveDef move) => !move.IsStatus && move.Effect != MoveEffect.None;

    private static bool IsSelfEffect(MoveEffect e) => e is MoveEffect.RaiseAtk or MoveEffect.RaiseDef
        or MoveEffect.RaiseSpAtk or MoveEffect.RaiseSpDef or MoveEffect.RaiseSpd or MoveEffect.RaiseAccuracy
        or MoveEffect.RaiseEvasion or MoveEffect.HealUser or MoveEffect.RecoilQuarterMax or MoveEffect.ProtectUser
        or MoveEffect.EndureUser or MoveEffect.SetSun or MoveEffect.SetRain or MoveEffect.SetSandstorm
        or MoveEffect.SetSnow or MoveEffect.SetElectricTerrain or MoveEffect.SetGrassyTerrain
        or MoveEffect.SetMistyTerrain or MoveEffect.SetPsychicTerrain or MoveEffect.ReflectSide
        or MoveEffect.LightScreenSide or MoveEffect.AquaRing or MoveEffect.Ingrain or MoveEffect.BellyDrum
        or MoveEffect.Haze or MoveEffect.CureUserStatus or MoveEffect.Acupressure or MoveEffect.NoOp;

    // Effects that act on the target (blocked by Shield Dust when they're a damaging move's secondary).
    private static bool IsDefenderEffect(MoveEffect e) => e is MoveEffect.LowerTargetAtk or MoveEffect.LowerTargetDef
        or MoveEffect.LowerTargetSpAtk or MoveEffect.LowerTargetSpDef or MoveEffect.LowerTargetSpd
        or MoveEffect.LowerTargetAccuracy or MoveEffect.Burn or MoveEffect.Freeze or MoveEffect.Paralyze
        or MoveEffect.Sleep or MoveEffect.Poison or MoveEffect.Flinch or MoveEffect.Confuse
        or MoveEffect.LowerTargetEvasion or MoveEffect.LeechSeed or MoveEffect.Yawn or MoveEffect.ForceSwitch;

    // Effectiveness, with Scrappy letting Normal/Fighting hit Ghost types.
    private static float EffectivenessWith(MonsterInstance attacker, MonsterInstance defender, MoveDef move)
    {
        var eff = Elements.Effectiveness(move.Element, defender.Element, defender.SecondaryElement);
        if (eff <= 0f && attacker.Ability is "Scrappy" && move.Element is Element.Normal or Element.Fighting &&
            defender.HasType(Element.Ghost))
        {
            var e = defender.Element == Element.Ghost ? 1f : Elements.Effectiveness(move.Element, defender.Element);
            if (defender.SecondaryElement is { } s)
            {
                e *= s == Element.Ghost ? 1f : Elements.Effectiveness(move.Element, s);
            }

            return e;
        }

        return eff;
    }

    // On-switch abilities: Intimidate, Download, and weather setters.
    private void ApplyEntryAbility(MonsterInstance entrant, MonsterInstance opponent)
    {
        switch (entrant.Ability)
        {
            case "Intimidate" when !opponent.Fainted:
                LowerStatByFoe(opponent, StatKind.Atk, 1, "Attack", $"{entrant.Name}'s Intimidate");
                break;
            case "Download" when !opponent.Fainted:
                if (opponent.Def <= opponent.SpDef)
                {
                    RaiseStat(entrant, StatKind.Atk, 1);
                    Log.Enqueue(new BattleMessage($"{entrant.Name}'s Download boosted its Attack!", BattleCue.Buff,
                        entrant, stateAfter: BattleSnapshot.Capture(entrant)));
                }
                else
                {
                    RaiseStat(entrant, StatKind.SpA, 1);
                    Log.Enqueue(new BattleMessage($"{entrant.Name}'s Download boosted its Sp. Atk!", BattleCue.Buff,
                        entrant, stateAfter: BattleSnapshot.Capture(entrant)));
                }

                break;
            case "Drought":
                SetWeather(BattleWeather.Sun, $"{entrant.Name}'s Drought intensified the sunlight!");
                break;
            case "Drizzle":
                SetWeather(BattleWeather.Rain, $"{entrant.Name}'s Drizzle made it rain!");
                break;
            case "Sand Stream":
                SetWeather(BattleWeather.Sandstorm, $"{entrant.Name}'s Sand Stream whipped up a sandstorm!");
                break;
            case "Snow Warning":
                SetWeather(BattleWeather.Snow, $"{entrant.Name}'s Snow Warning started snow!");
                break;
        }
    }

    // Type-immunity / absorbing abilities. Returns true when the move is nullified.
    private bool AbsorbByAbility(MonsterInstance attacker, MonsterInstance defender, MoveDef move)
    {
        var t = move.Element;
        string? msg = null;
        var heal = false;
        StatKind? boost = null;
        switch (defender.Ability)
        {
            case "Levitate" when t == Element.Ground:
                msg = $"{defender.Name} is unaffected thanks to Levitate!";
                break;
            case "Flash Fire" when t == Element.Fire:
                defender.FlashFireActive = true;
                msg = $"Flash Fire powered up {defender.Name}'s Fire moves!";
                break;
            case "Volt Absorb" when t == Element.Electric:
            case "Water Absorb" when t == Element.Water:
            case "Dry Skin" when t == Element.Water:
                heal = true;
                msg = $"{defender.Name} restored HP with {defender.Ability}!";
                break;
            case "Lightning Rod" when t == Element.Electric:
            case "Storm Drain" when t == Element.Water:
                boost = StatKind.SpA;
                msg = $"{defender.Name}'s {defender.Ability} drew in the attack!";
                break;
            case "Motor Drive" when t == Element.Electric:
                boost = StatKind.Spe;
                msg = $"{defender.Name}'s Motor Drive boosted its Speed!";
                break;
            case "Sap Sipper" when t == Element.Grass:
                boost = StatKind.Atk;
                msg = $"{defender.Name}'s Sap Sipper boosted its Attack!";
                break;
        }

        if (msg is null)
        {
            return false;
        }

        if (heal && defender.CurrentHp < defender.MaxHp)
        {
            defender.Heal(Math.Max(1, defender.MaxHp / 4));
            Log.Enqueue(new BattleMessage(msg, BattleCue.Heal, defender, defender.CurrentHp,
                stateAfter: BattleSnapshot.Capture(defender)));
        }
        else if (boost is { } b)
        {
            RaiseStat(defender, b, 1);
            Log.Enqueue(new BattleMessage(msg, BattleCue.Buff, defender, stateAfter: BattleSnapshot.Capture(defender)));
        }
        else
        {
            Log.Enqueue(new BattleMessage(msg, BattleCue.Info, defender, effectiveness: 0f));
        }

        return true;
    }

    private void HandleOnHit(MonsterInstance attacker, MonsterInstance defender, MoveDef move, bool crit,
        bool moldBreaker)
    {
        var contact = IsContact(move);
        if (defender.Fainted)
        {
            if (attacker.Ability is "Moxie" && !attacker.Fainted)
            {
                RaiseStat(attacker, StatKind.Atk, 1);
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s Moxie boosted its Attack!", BattleCue.Buff,
                    attacker, stateAfter: BattleSnapshot.Capture(attacker)));
            }

            if (!moldBreaker && contact && defender.Ability is "Aftermath")
            {
                IndirectDamage(attacker, attacker.MaxHp / 4, "Aftermath");
            }

            return;
        }

        if (crit && !moldBreaker && defender.Ability is "Anger Point")
        {
            defender.AtkStage = 6;
            Log.Enqueue(new BattleMessage($"{defender.Name}'s Anger Point maxed its Attack!", BattleCue.Buff,
                defender, stateAfter: BattleSnapshot.Capture(defender)));
        }

        if (defender.Ability is "Justified" && move.Element == Element.Dark)
        {
            RaiseStat(defender, StatKind.Atk, 1);
            Log.Enqueue(new BattleMessage($"{defender.Name}'s Justified boosted its Attack!", BattleCue.Buff,
                defender, stateAfter: BattleSnapshot.Capture(defender)));
        }

        if (defender.Ability is "Rattled" && move.Element is Element.Bug or Element.Dark or Element.Ghost)
        {
            RaiseStat(defender, StatKind.Spe, 1);
            Log.Enqueue(new BattleMessage($"{defender.Name}'s Rattled boosted its Speed!", BattleCue.Buff,
                defender, stateAfter: BattleSnapshot.Capture(defender)));
        }

        if (contact && defender.Ability is "Weak Armor")
        {
            ApplyStageDown(defender, StatKind.Def, 1);
            RaiseStat(defender, StatKind.Spe, 2);
            Log.Enqueue(new BattleMessage($"{defender.Name}'s Weak Armor shifted its stats!", BattleCue.Buff,
                defender, stateAfter: BattleSnapshot.Capture(defender)));
        }

        // Contact-triggered status from the defender's body onto the attacker (30%).
        if (contact && !moldBreaker && !attacker.Fainted && rng.Next(10) < 3)
        {
            switch (defender.Ability)
            {
                case "Static": TryInflict(attacker, Status.Paralysis, defender); break;
                case "Flame Body": TryInflict(attacker, Status.Burn, defender); break;
                case "Poison Point": TryInflict(attacker, Status.Poison, defender); break;
                case "Effect Spore":
                    TryInflict(attacker, rng.Next(2) == 0 ? Status.Poison : Status.Paralysis, defender);
                    break;
            }
        }

        // The attacker's own contact/hit abilities onto the defender.
        if (!attacker.Fainted)
        {
            if (contact && attacker.Ability is "Poison Touch" && rng.Next(10) < 3)
            {
                TryInflict(defender, Status.Poison, attacker);
            }

            if (attacker.Ability is "Stench" && !defender.Fainted && rng.Next(10) < 1)
            {
                defender.Flinched = true;
            }
        }
    }

    // Applies a status with type/ability immunity checks and Synchronize reflection. Returns success.
    private bool TryInflict(MonsterInstance target, Status status, MonsterInstance? source)
    {
        if (target.Fainted || target.Status != Status.None || status == Status.None ||
            TypeImmuneToStatus(target, status))
        {
            return false;
        }

        if (Terrain == BattleTerrain.Misty || (Terrain == BattleTerrain.Electric && status == Status.Sleep))
        {
            Log.Enqueue(new BattleMessage($"{target.Name} is protected by the terrain!", BattleCue.Info, target));
            return false;
        }

        var abilityImmune = target.Ability switch
        {
            "Immunity" => status == Status.Poison,
            "Limber" => status == Status.Paralysis,
            "Insomnia" or "Vital Spirit" => status == Status.Sleep,
            "Water Veil" => status == Status.Burn,
            _ => false,
        };
        if (abilityImmune)
        {
            return false;
        }

        target.Status = status;
        target.SleepTurns = status == Status.Sleep ? rng.Next(2, 5) : 0;
        Log.Enqueue(new BattleMessage(StatusInflictText(target, status), BattleCue.Buff, target,
            stateAfter: BattleSnapshot.Capture(target)));

        if (target.Ability is "Synchronize" && source is not null && !ReferenceEquals(source, target) &&
            source.Status == Status.None && status is Status.Burn or Status.Poison or Status.Paralysis &&
            !TypeImmuneToStatus(source, status))
        {
            source.Status = status;
            Log.Enqueue(new BattleMessage($"{target.Name}'s Synchronize passed the condition to {source.Name}!",
                BattleCue.Buff, source, stateAfter: BattleSnapshot.Capture(source)));
        }

        return true;
    }

    private static bool TypeImmuneToStatus(MonsterInstance m, Status status) => status switch
    {
        Status.Burn => m.HasType(Element.Fire),
        Status.Freeze => m.HasType(Element.Ice),
        Status.Sleep => false,
        Status.Paralysis => m.HasType(Element.Electric),
        Status.Poison => m.HasType(Element.Poison) || m.HasType(Element.Steel),
        _ => false,
    };

    private static string StatusInflictText(MonsterInstance m, Status status) => status switch
    {
        Status.Burn => $"{m.Name} was scorched!",
        Status.Freeze => $"{m.Name} was frozen solid!",
        Status.Sleep => $"{m.Name} fell asleep!",
        Status.Paralysis => $"{m.Name} was paralyzed!",
        Status.Poison => $"{m.Name} was poisoned!",
        _ => $"{m.Name} was afflicted!",
    };

    private void RaiseStat(MonsterInstance m, StatKind k, int amount)
    {
        switch (k)
        {
            case StatKind.Atk: m.AtkStage = Math.Min(6, m.AtkStage + amount); break;
            case StatKind.Def: m.DefStage = Math.Min(6, m.DefStage + amount); break;
            case StatKind.SpA: m.SpAtkStage = Math.Min(6, m.SpAtkStage + amount); break;
            case StatKind.SpD: m.SpDefStage = Math.Min(6, m.SpDefStage + amount); break;
            case StatKind.Spe: m.SpdStage = Math.Min(6, m.SpdStage + amount); break;
            case StatKind.Acc: m.AccuracyStage = Math.Min(6, m.AccuracyStage + amount); break;
            case StatKind.Eva: m.EvasionStage = Math.Min(6, m.EvasionStage + amount); break;
        }
    }

    private static void ApplyStageDown(MonsterInstance m, StatKind k, int amount)
    {
        switch (k)
        {
            case StatKind.Atk: m.AtkStage = Math.Max(-6, m.AtkStage - amount); break;
            case StatKind.Def: m.DefStage = Math.Max(-6, m.DefStage - amount); break;
            case StatKind.SpA: m.SpAtkStage = Math.Max(-6, m.SpAtkStage - amount); break;
            case StatKind.SpD: m.SpDefStage = Math.Max(-6, m.SpDefStage - amount); break;
            case StatKind.Spe: m.SpdStage = Math.Max(-6, m.SpdStage - amount); break;
            case StatKind.Acc: m.AccuracyStage = Math.Max(-6, m.AccuracyStage - amount); break;
            case StatKind.Eva: m.EvasionStage = Math.Max(-6, m.EvasionStage - amount); break;
        }
    }

    private static bool StatDropBlocked(MonsterInstance t, StatKind k) => t.Ability switch
    {
        "Clear Body" or "White Smoke" or "Full Metal Body" => true,
        "Hyper Cutter" => k == StatKind.Atk,
        "Big Pecks" => k == StatKind.Def,
        "Keen Eye" or "Illuminate" => k == StatKind.Acc,
        _ => false,
    };

    // Lowers a stat inflicted by the foe (Growl, Intimidate, secondary drops). Honours the
    // stat-drop-prevention abilities and triggers Defiant/Competitive. Returns true if it landed.
    private bool LowerStatByFoe(MonsterInstance target, StatKind k, int amount, string statName, string? prefix = null)
    {
        if (target.Fainted)
        {
            return false;
        }

        if (StatDropBlocked(target, k))
        {
            Log.Enqueue(new BattleMessage($"{target.Name}'s {statName} wasn't lowered ({target.Ability})!",
                BattleCue.Info, target));
            return false;
        }

        ApplyStageDown(target, k, amount);
        var lead = prefix is null ? $"{target.Name}'s {statName}" : $"{prefix} cut {target.Name}'s {statName}";
        Log.Enqueue(new BattleMessage($"{lead} fell!", BattleCue.Buff, target,
            stateAfter: BattleSnapshot.Capture(target)));

        if (target.Ability is "Defiant")
        {
            RaiseStat(target, StatKind.Atk, 2);
            Log.Enqueue(new BattleMessage($"{target.Name}'s Defiant sharply raised its Attack!", BattleCue.Buff,
                target, stateAfter: BattleSnapshot.Capture(target)));
        }
        else if (target.Ability is "Competitive")
        {
            RaiseStat(target, StatKind.SpA, 2);
            Log.Enqueue(new BattleMessage($"{target.Name}'s Competitive sharply raised its Sp. Atk!", BattleCue.Buff,
                target, stateAfter: BattleSnapshot.Capture(target)));
        }

        return true;
    }

    private void IndirectDamage(MonsterInstance m, int amount, string sourceLabel)
    {
        if (m.Ability is "Magic Guard" || m.Fainted)
        {
            return;
        }

        var dmg = Math.Min(m.CurrentHp, Math.Max(1, amount));
        m.CurrentHp -= dmg;
        var cue = ReferenceEquals(m, Wild) ? BattleCue.WildHurt : BattleCue.PlayerHurt;
        Log.Enqueue(new BattleMessage($"{m.Name} was hurt by {sourceLabel}! ({dmg})", cue, m, m.CurrentHp,
            stateAfter: BattleSnapshot.Capture(m)));
    }

    // Speed Boost / Shed Skin at the end of each turn.
    private void EndOfTurnAbilities(MonsterInstance m)
    {
        if (m.Fainted)
        {
            return;
        }

        if (m.Ability is "Speed Boost" && m.SpdStage < 6)
        {
            m.SpdStage++;
            Log.Enqueue(new BattleMessage($"{m.Name}'s Speed Boost raised its Speed!", BattleCue.Buff, m,
                stateAfter: BattleSnapshot.Capture(m)));
        }

        if (m.Ability is "Shed Skin" && m.Status != Status.None && rng.Next(10) < 3)
        {
            m.Status = Status.None;
            Log.Enqueue(new BattleMessage($"{m.Name}'s Shed Skin cured its status!", BattleCue.Buff, m,
                stateAfter: BattleSnapshot.Capture(m)));
        }
    }

    // Natural Cure / Regenerator when leaving the field.
    private void OnSwitchOut(MonsterInstance m)
    {
        if (m.Fainted)
        {
            return;
        }

        if (m.Ability is "Natural Cure")
        {
            m.Status = Status.None;
        }

        if (m.Ability is "Regenerator" && m.CurrentHp < m.MaxHp)
        {
            m.Heal(m.MaxHp / 3);
        }
    }

    private MoveDef ChooseWildMove()
    {
        MoveDef best = Wild.Moves.Count > 0 ? Wild.Moves[0] : Moves.Tackle;
        var bestScore = -1f;
        for (var i = 0; i < Wild.Moves.Count; i++)
        {
            if (i < Wild.Pp.Count && Wild.Pp[i] <= 0)
            {
                continue;
            }

            var move = Wild.Moves[i];
            var score = move.Effect switch
            {
                MoveEffect.HealUser when Wild.CurrentHp >= Wild.MaxHp => 0f,
                MoveEffect.HealUser => Wild.HpFraction < 0.4f ? 120f : Wild.HpFraction < 0.7f ? 52f : 10f,
                MoveEffect.RaiseAtk => Wild.AtkStage >= 2 ? 2f : 28f - Wild.AtkStage * 7f,
                MoveEffect.RaiseDef => Wild.DefStage >= 2 ? 2f : 28f - Wild.DefStage * 7f,
                MoveEffect.RaiseSpAtk => Wild.SpAtkStage >= 2 ? 2f : 28f - Wild.SpAtkStage * 7f,
                MoveEffect.RaiseSpDef => Wild.SpDefStage >= 2 ? 2f : 28f - Wild.SpDefStage * 7f,
                MoveEffect.RaiseSpd => Wild.SpdStage >= 2 ? 2f : 28f - Wild.SpdStage * 7f,
                _ when move.IsStatus => 12f,
                _ => move.Power * Elements.Effectiveness(move.Element, Active.Element, Active.SecondaryElement) *
                     (Wild.HasType(move.Element) ? 1.5f : 1f),
            };
            score *= 0.7f + (float)rng.NextDouble() * 0.6f;
            if (score > bestScore)
            {
                bestScore = score;
                best = move;
            }
        }

        return bestScore < 0f ? Moves.Struggle : best;
    }

    private void Execute(MonsterInstance attacker, MonsterInstance defender, MoveDef move, BattleCue attackCue,
        BattleCue hurtCue)
    {
        Log.Enqueue(new BattleMessage($"{attacker.Name} used {move.Name}!", attackCue, attacker, move: move));

        if (!IsSelfEffect(move.Effect) && defender.Protecting)
        {
            Log.Enqueue(new BattleMessage($"{defender.Name} protected itself!", BattleCue.Info, defender));
            return;
        }

        // No Guard makes moves involving it always connect.
        var noGuard = attacker.Ability is "No Guard" || defender.Ability is "No Guard";
        if (move.Accuracy > 0 && !noGuard)
        {
            // Accuracy is scaled by the attacker's accuracy stage vs the defender's evasion stage
            // (Sand Attack, Double Team, etc.), using the standard 3/(3±n) stage ratio, then by
            // Compound Eyes / Hustle / Wonder Skin.
            var stage = Math.Clamp(attacker.AccuracyStage - defender.EvasionStage, -6, 6);
            var stageMul = stage >= 0 ? (3f + stage) / 3f : 3f / (3f - stage);
            var acc = move.Accuracy * stageMul;
            if (attacker.Ability is "Compound Eyes") acc *= 1.3f;
            if (attacker.Ability is "Hustle" && move.Category == MoveCategory.Physical) acc *= 0.8f;
            if (move.IsStatus && defender.Ability is "Wonder Skin") acc *= 0.5f;
            if (rng.NextDouble() * 100f >= acc)
            {
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s attack missed!", BattleCue.Info));
                return;
            }
        }

        if (!move.IsStatus)
        {
            var isStruggle = ReferenceEquals(move, Moves.Struggle);
            // Mold Breaker ignores the target's defensive/immunity abilities.
            var moldBreaker = attacker.Ability is "Mold Breaker";
            var defAb = moldBreaker ? string.Empty : defender.Ability;

            // Type-immunity / absorption abilities (Levitate, Volt Absorb, Flash Fire, ...).
            if (!moldBreaker && AbsorbByAbility(attacker, defender, move))
            {
                return;
            }

            var effectiveness = isStruggle ? 1f : EffectivenessWith(attacker, defender, move);
            if (effectiveness <= 0f)
            {
                Log.Enqueue(new BattleMessage($"It doesn't affect {defender.Name}...", BattleCue.Info, defender,
                    effectiveness: 0f));
                return;
            }

            // Crit: blocked by Battle/Shell Armor, boosted by Super Luck (rate) and Sniper (damage).
            var noCrit = defAb is "Battle Armor" or "Shell Armor";
            var critRate = attacker.Ability is "Super Luck" ? 8 : 24;
            var isCritical = !noCrit && rng.Next(critRate) == 0;
            var crit = isCritical ? (attacker.Ability is "Sniper" ? 2.25f : 1.5f) : 1f;

            var stab = !isStruggle && attacker.HasType(move.Element)
                ? (attacker.Ability is "Adaptability" ? 2f : 1.5f) : 1f;
            var variance = 0.85f + (float)rng.NextDouble() * 0.15f;
            var attack = attacker.OffensiveStat(move.Category, isCritical);
            var defense = defender.DefensiveStat(move.Category, isCritical);
            var attackerStatused = attacker.Status != Status.None;

            // Attack / Defense stat multipliers from abilities.
            var atkMul = 1f;
            if (move.Category == MoveCategory.Physical)
            {
                if (attacker.Ability is "Guts" && attackerStatused) atkMul *= 1.5f;
                if (attacker.Ability is "Hustle") atkMul *= 1.5f;
            }

            var defMul = 1f;
            if (defAb is "Marvel Scale" && defender.Status != Status.None) defMul *= 1.5f;

            // Move-power multipliers.
            var powerMul = PinchAbilityBoost(attacker, move.Element);
            if (attacker.Ability is "Technician" && move.Power <= 60) powerMul *= 1.5f;
            if (attacker.Ability is "Reckless" && move.Effect == MoveEffect.RecoilQuarterMax) powerMul *= 1.2f;
            var sheerForce = attacker.Ability is "Sheer Force" && HasSecondary(move);
            if (sheerForce) powerMul *= 1.3f;
            if (attacker.Ability is "Rivalry" && attacker.Gender != Gender.Genderless &&
                defender.Gender != Gender.Genderless)
            {
                powerMul *= attacker.Gender == defender.Gender ? 1.25f : 0.75f;
            }

            if (attacker.FlashFireActive && move.Element == Element.Fire) powerMul *= 1.5f;

            // Effectiveness-based modifiers.
            var effMul = 1f;
            if (attacker.Ability is "Tinted Lens" && effectiveness < 1f) effMul *= 2f;
            if (effectiveness > 1f && defAb is "Filter" or "Solid Rock") effMul *= 0.75f;
            if (defAb is "Thick Fat" && move.Element is Element.Fire or Element.Ice) effMul *= 0.5f;
            if (defAb is "Heatproof" && move.Element == Element.Fire) effMul *= 0.5f;
            if (defAb is "Dry Skin" && move.Element == Element.Fire) effMul *= 1.25f;
            if (defAb is "Multiscale" && defender.CurrentHp == defender.MaxHp) effMul *= 0.5f;
            effMul *= WeatherPowerMultiplier(move.Element);
            effMul *= TerrainPowerMultiplier(move.Element);
            effMul *= ScreenDamageMultiplier(defender, move.Category, isCritical);

            var burn = attacker.Status == Status.Burn && move.Category == MoveCategory.Physical &&
                attacker.Ability is not "Guts" ? 0.5f : 1f;

            var effectiveDefense = Math.Max(1, (int)(defense * defMul));
            var raw = ((2f * attacker.Level / 5f + 2f) * move.Power * attack * atkMul / effectiveDefense / 50f) + 2f;
            var damage = Math.Max(1, (int)(raw * effectiveness * stab * crit * variance * burn * powerMul * effMul));
            var appliedDamage = Math.Min(defender.CurrentHp, damage);

            // Sturdy: survive a would-be OHKO from full HP with 1 HP left.
            var sturdy = defAb is "Sturdy" && defender.CurrentHp == defender.MaxHp &&
                appliedDamage >= defender.CurrentHp;
            if (sturdy)
            {
                appliedDamage = defender.CurrentHp - 1;
            }

            var endured = defender.Enduring && appliedDamage >= defender.CurrentHp && defender.CurrentHp > 1;
            if (endured)
            {
                appliedDamage = defender.CurrentHp - 1;
            }

            defender.CurrentHp -= appliedDamage;
            if (!ReferenceEquals(attacker, Wild))
            {
                attacker.RecordDamage(appliedDamage);
            }

            Log.Enqueue(new BattleMessage(DamageText(defender, appliedDamage, effectiveness, isCritical), hurtCue,
                defender, defender.CurrentHp, isCritical, effectiveness, move,
                stateAfter: BattleSnapshot.Capture(defender)));
            if (sturdy)
            {
                Log.Enqueue(new BattleMessage($"{defender.Name} endured the hit with Sturdy!", BattleCue.Buff,
                    defender, defender.CurrentHp, stateAfter: BattleSnapshot.Capture(defender)));
            }

            if (endured)
            {
                Log.Enqueue(new BattleMessage($"{defender.Name} endured the hit!", BattleCue.Buff, defender,
                    defender.CurrentHp, stateAfter: BattleSnapshot.Capture(defender)));
            }

            if (!defender.Fainted && defender.Status == Status.Freeze && move.Element == Element.Fire)
            {
                defender.Status = Status.None;
                Log.Enqueue(new BattleMessage($"{defender.Name} thawed out!", BattleCue.Buff, defender,
                    stateAfter: BattleSnapshot.Capture(defender)));
            }

            HandleOnHit(attacker, defender, move, isCritical, moldBreaker);

            // Sheer Force trades the move's secondary effect for the power boost above.
            if (sheerForce)
            {
                return;
            }
        }

        ApplyEffect(attacker, defender, move);
    }

    private void ClearStatStages(MonsterInstance monster)
    {
        monster.AtkStage = 0;
        monster.DefStage = 0;
        monster.SpAtkStage = 0;
        monster.SpDefStage = 0;
        monster.SpdStage = 0;
        monster.AccuracyStage = 0;
        monster.EvasionStage = 0;
    }

    private void ForceOut(MonsterInstance attacker, MonsterInstance defender)
    {
        if (ReferenceEquals(defender, Wild) && CanCatch)
        {
            Outcome = BattleOutcome.Fled;
            Log.Enqueue(new BattleMessage($"{defender.Name} was blown away!", BattleCue.Fled, defender));
            return;
        }

        if (ReferenceEquals(defender, Active))
        {
            var next = party.FindIndex(m => !m.Fainted && !ReferenceEquals(m, Active));
            if (next >= 0)
            {
                RequiresSwitch = true;
                Log.Enqueue(new BattleMessage($"{defender.Name} was forced back!", BattleCue.PlayerSwitch,
                    defender));
            }
            else
            {
                Log.Enqueue(new BattleMessage("But it failed!", BattleCue.Info, attacker));
            }

            return;
        }

        if (ReferenceEquals(defender, Wild) && enemyTeam is not null)
        {
            var next = -1;
            for (var i = 0; i < enemyTeam.Count; i++)
            {
                if (i != enemyIndex && !enemyTeam[i].Fainted)
                {
                    next = i;
                    break;
                }
            }

            if (next >= 0)
            {
                OnSwitchOut(Wild);
                Wild.ResetBattleState();
                enemyIndex = next;
                Wild = enemyTeam[enemyIndex];
                Wild.ResetBattleState();
                Log.Enqueue(new BattleMessage($"{TrainerName} was forced to send out {Wild.Name}!",
                    BattleCue.EnemySwitch, Wild, stateAfter: BattleSnapshot.Capture(Wild)));
                ApplyEntryAbility(Wild, Active);
            }
            else
            {
                Log.Enqueue(new BattleMessage("But it failed!", BattleCue.Info, attacker));
            }
        }
    }

    private void ApplyEffect(MonsterInstance attacker, MonsterInstance defender, MoveDef move)
    {
        if (move.Effect == MoveEffect.None)
        {
            return;
        }

        if (!move.IsStatus && move.EffectChance > 0)
        {
            // Serene Grace doubles secondary-effect chances.
            var chance = move.EffectChance * (attacker.Ability is "Serene Grace" ? 2 : 1);
            if (rng.NextDouble() * 100f > chance)
            {
                return;
            }
        }

        // Shield Dust blocks the added effects of damaging moves that target its holder.
        if (!move.IsStatus && defender.Ability is "Shield Dust" && IsDefenderEffect(move.Effect))
        {
            return;
        }

        switch (move.Effect)
        {
            case MoveEffect.RaiseAtk:
                RaiseStat(attacker, StatKind.Atk, move.StageChange);
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s attack rose!", BattleCue.Buff, attacker,
                    stateAfter: BattleSnapshot.Capture(attacker)));
                break;
            case MoveEffect.RaiseDef:
                RaiseStat(attacker, StatKind.Def, move.StageChange);
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s defense rose!", BattleCue.Buff, attacker,
                    stateAfter: BattleSnapshot.Capture(attacker)));
                break;
            case MoveEffect.RaiseSpAtk:
                RaiseStat(attacker, StatKind.SpA, move.StageChange);
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s Sp. Atk rose!", BattleCue.Buff, attacker,
                    stateAfter: BattleSnapshot.Capture(attacker)));
                break;
            case MoveEffect.RaiseSpDef:
                RaiseStat(attacker, StatKind.SpD, move.StageChange);
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s Sp. Def rose!", BattleCue.Buff, attacker,
                    stateAfter: BattleSnapshot.Capture(attacker)));
                break;
            case MoveEffect.RaiseSpd:
                RaiseStat(attacker, StatKind.Spe, move.StageChange);
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s speed rose!", BattleCue.Buff, attacker,
                    stateAfter: BattleSnapshot.Capture(attacker)));
                break;
            case MoveEffect.LowerTargetAtk:
                LowerStatByFoe(defender, StatKind.Atk, move.StageChange, "Attack");
                break;
            case MoveEffect.LowerTargetSpd:
                LowerStatByFoe(defender, StatKind.Spe, move.StageChange, "Speed");
                break;
            case MoveEffect.LowerTargetDef:
                LowerStatByFoe(defender, StatKind.Def, move.StageChange, "Defense");
                break;
            case MoveEffect.LowerTargetSpAtk:
                LowerStatByFoe(defender, StatKind.SpA, move.StageChange, "Sp. Atk");
                break;
            case MoveEffect.LowerTargetSpDef:
                LowerStatByFoe(defender, StatKind.SpD, move.StageChange, "Sp. Def");
                break;
            case MoveEffect.LowerTargetAccuracy:
                LowerStatByFoe(defender, StatKind.Acc, move.StageChange, "accuracy");
                break;
            case MoveEffect.LowerTargetEvasion:
                LowerStatByFoe(defender, StatKind.Eva, move.StageChange, "evasiveness");
                break;
            case MoveEffect.RaiseEvasion:
                RaiseStat(attacker, StatKind.Eva, move.StageChange);
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s evasiveness rose!", BattleCue.Buff, attacker,
                    stateAfter: BattleSnapshot.Capture(attacker)));
                break;
            case MoveEffect.RaiseAccuracy:
                RaiseStat(attacker, StatKind.Acc, move.StageChange);
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s accuracy rose!", BattleCue.Buff, attacker,
                    stateAfter: BattleSnapshot.Capture(attacker)));
                break;
            case MoveEffect.HealUser:
                attacker.Heal(attacker.MaxHp / 2);
                Log.Enqueue(new BattleMessage($"{attacker.Name} mended its wounds.", BattleCue.Heal, attacker,
                    attacker.CurrentHp, stateAfter: BattleSnapshot.Capture(attacker)));
                break;
            case MoveEffect.Burn:
                TryInflict(defender, Status.Burn, attacker);
                break;
            case MoveEffect.Freeze:
                TryInflict(defender, Status.Freeze, attacker);
                break;
            case MoveEffect.Sleep:
                TryInflict(defender, Status.Sleep, attacker);
                break;
            case MoveEffect.Paralyze:
                TryInflict(defender, Status.Paralysis, attacker);
                break;
            case MoveEffect.Poison:
                TryInflict(defender, Status.Poison, attacker);
                break;
            case MoveEffect.Flinch:
                if (!defender.Fainted && defender.Ability is not "Inner Focus")
                {
                    defender.Flinched = true;
                    if (defender.Ability is "Steadfast")
                    {
                        RaiseStat(defender, StatKind.Spe, 1);
                        Log.Enqueue(new BattleMessage($"{defender.Name}'s Steadfast raised its Speed!",
                            BattleCue.Buff, defender, stateAfter: BattleSnapshot.Capture(defender)));
                    }
                }

                break;
            case MoveEffect.RecoilQuarterMax:
                if (!attacker.Fainted && attacker.Ability is not "Rock Head")
                {
                    IndirectDamage(attacker, attacker.MaxHp / 4, "recoil");
                }

                break;
            case MoveEffect.Confuse:
                if (!defender.Fainted && defender.ConfusionTurns <= 0 && defender.Ability is not "Own Tempo")
                {
                    defender.ConfusionTurns = rng.Next(2, 6);
                    Log.Enqueue(new BattleMessage($"{defender.Name} became confused!", BattleCue.Buff, defender));
                }

                break;
            case MoveEffect.Transform:
                if (!attacker.Fainted && !defender.Fainted && !attacker.IsTransformed && !defender.IsTransformed)
                {
                    var who = attacker.Name;
                    var into = defender.Species.Name;
                    attacker.Transform(defender);
                    Log.Enqueue(new BattleMessage($"{who} transformed into {into}!", BattleCue.Buff, attacker,
                        attacker.CurrentHp, stateAfter: BattleSnapshot.Capture(attacker)));
                }

                break;
            case MoveEffect.ProtectUser:
                attacker.Protecting = true;
                Log.Enqueue(new BattleMessage($"{attacker.Name} protected itself!", BattleCue.Buff, attacker));
                break;
            case MoveEffect.EndureUser:
                attacker.Enduring = true;
                Log.Enqueue(new BattleMessage($"{attacker.Name} braced itself to endure!", BattleCue.Buff,
                    attacker));
                break;
            case MoveEffect.ForceSwitch:
                ForceOut(attacker, defender);
                break;
            case MoveEffect.SetSun:
                SetWeather(BattleWeather.Sun, "The sunlight turned harsh!");
                break;
            case MoveEffect.SetRain:
                SetWeather(BattleWeather.Rain, "Rain began to fall!");
                break;
            case MoveEffect.SetSandstorm:
                SetWeather(BattleWeather.Sandstorm, "A sandstorm kicked up!");
                break;
            case MoveEffect.SetSnow:
                SetWeather(BattleWeather.Snow, "Snow began to fall!");
                break;
            case MoveEffect.SetElectricTerrain:
                SetTerrain(BattleTerrain.Electric, "Electricity surged across the battlefield!");
                break;
            case MoveEffect.SetGrassyTerrain:
                SetTerrain(BattleTerrain.Grassy, "Grass covered the battlefield!");
                break;
            case MoveEffect.SetMistyTerrain:
                SetTerrain(BattleTerrain.Misty, "Mist swirled over the battlefield!");
                break;
            case MoveEffect.SetPsychicTerrain:
                SetTerrain(BattleTerrain.Psychic, "The battlefield got weird!");
                break;
            case MoveEffect.ReflectSide:
                if (ReferenceEquals(attacker, Active)) playerReflectTurns = 5; else wildReflectTurns = 5;
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s side gained Reflect!", BattleCue.Buff,
                    attacker));
                break;
            case MoveEffect.LightScreenSide:
                if (ReferenceEquals(attacker, Active)) playerLightScreenTurns = 5; else wildLightScreenTurns = 5;
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s side gained Light Screen!", BattleCue.Buff,
                    attacker));
                break;
            case MoveEffect.AquaRing:
                attacker.AquaRingActive = true;
                Log.Enqueue(new BattleMessage($"{attacker.Name} surrounded itself with a veil of water!",
                    BattleCue.Buff, attacker));
                break;
            case MoveEffect.Ingrain:
                attacker.IngrainActive = true;
                Log.Enqueue(new BattleMessage($"{attacker.Name} planted its roots!", BattleCue.Buff, attacker));
                break;
            case MoveEffect.LeechSeed:
                if (!defender.Fainted && !defender.HasType(Element.Grass) && !defender.LeechSeeded)
                {
                    defender.LeechSeeded = true;
                    Log.Enqueue(new BattleMessage($"{defender.Name} was seeded!", BattleCue.Buff, defender));
                }
                else
                {
                    Log.Enqueue(new BattleMessage("But it failed!", BattleCue.Info, attacker));
                }

                break;
            case MoveEffect.Yawn:
                if (!defender.Fainted && defender.Status == Status.None && defender.YawnTurns <= 0)
                {
                    defender.YawnTurns = 2;
                    Log.Enqueue(new BattleMessage($"{defender.Name} became drowsy!", BattleCue.Buff, defender));
                }
                else
                {
                    Log.Enqueue(new BattleMessage("But it failed!", BattleCue.Info, attacker));
                }

                break;
            case MoveEffect.BellyDrum:
                if (attacker.CurrentHp > attacker.MaxHp / 2)
                {
                    attacker.CurrentHp -= Math.Max(1, attacker.MaxHp / 2);
                    attacker.AtkStage = 6;
                    Log.Enqueue(new BattleMessage($"{attacker.Name} cut its HP and maximized its Attack!",
                        BattleCue.Buff, attacker, attacker.CurrentHp, stateAfter: BattleSnapshot.Capture(attacker)));
                }
                else
                {
                    Log.Enqueue(new BattleMessage("But it failed!", BattleCue.Info, attacker));
                }

                break;
            case MoveEffect.Haze:
                ClearStatStages(Active);
                ClearStatStages(Wild);
                Log.Enqueue(new BattleMessage("All stat changes were eliminated!", BattleCue.Buff));
                break;
            case MoveEffect.CureUserStatus:
                if (attacker.Status != Status.None)
                {
                    attacker.CureStatus();
                    Log.Enqueue(new BattleMessage($"{attacker.Name}'s status was cured!", BattleCue.Heal,
                        attacker, attacker.CurrentHp, stateAfter: BattleSnapshot.Capture(attacker)));
                }
                else
                {
                    Log.Enqueue(new BattleMessage("But it failed!", BattleCue.Info, attacker));
                }

                break;
            case MoveEffect.Acupressure:
                var stat = rng.Next(7);
                var statName = stat switch
                {
                    0 => "Attack",
                    1 => "Defense",
                    2 => "Sp. Atk",
                    3 => "Sp. Def",
                    4 => "Speed",
                    5 => "accuracy",
                    _ => "evasiveness",
                };
                RaiseStat(attacker, (StatKind)stat, 2);
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s {statName} sharply rose!", BattleCue.Buff,
                    attacker, stateAfter: BattleSnapshot.Capture(attacker)));
                break;
            case MoveEffect.NoOp:
                Log.Enqueue(new BattleMessage("But nothing happened!", BattleCue.Info, attacker));
                break;
        }
    }

    private static string DamageText(MonsterInstance defender, int damage, float effectiveness, bool crit)
    {
        var note = effectiveness > 1f ? " It's super effective!" :
            effectiveness < 1f ? " It's not very effective." : string.Empty;
        var critNote = crit ? " Critical hit!" : string.Empty;
        return $"{defender.Name} took {damage} damage.{critNote}{note}";
    }

    private bool WildDown()
    {
        if (!Wild.Fainted)
        {
            return false;
        }

        Log.Enqueue(new BattleMessage($"{Wild.Name} fainted!", BattleCue.WildFaint));
        AwardXp();

        // Trainer with reserves: pause so the UI can send out their next Pokémon.
        if (enemyTeam is not null && enemyIndex + 1 < enemyTeam.Count)
        {
            RequiresEnemySend = true;
            return true;
        }

        PrizeMoney = enemyTeam is not null ? trainerPrize : 40 + Wild.Level * 20;
        Outcome = BattleOutcome.Won;
        return true;
    }

    private bool PlayerDown()
    {
        if (!Active.Fainted)
        {
            return false;
        }

        Log.Enqueue(new BattleMessage($"{Active.Name} fainted!", BattleCue.PlayerFaint));
        var next = party.FindIndex(m => !m.Fainted);
        if (next < 0)
        {
            Outcome = BattleOutcome.Whiteout;
            Log.Enqueue(new BattleMessage("You have no monsters left...", BattleCue.Info));
            return true;
        }

        RequiresSwitch = true;
        return true;
    }

    private void AwardXp()
    {
        // XP Share: the active battler earns full XP, and every other healthy party member earns a
        // share. Only the active creature's XP line is shown (with its bar); the rest gain quietly
        // but still announce level-ups, new moves and evolutions.
        foreach (var m in party)
        {
            if (m.Fainted)
            {
                continue;
            }

            var active = ReferenceEquals(m, Active);
            var gain = XpAward(Wild.Level, m.Level, active);
            if (gain <= 0)
            {
                continue;
            }

            m.GainEvs(Wild.Species); // EV yield from the defeated species (its top base stats)
            var learned = m.GainXp(gain, out var pendingMoves, out var evolutions);
            if (active)
            {
                Log.Enqueue(new BattleMessage($"{m.Name} gained {gain} XP.", BattleCue.XpGain, m,
                    stateAfter: BattleSnapshot.Capture(m)));
            }

            foreach (var announcement in evolutions)
            {
                Log.Enqueue(new BattleMessage(announcement, BattleCue.Evolve, m, m.CurrentHp,
                    stateAfter: BattleSnapshot.Capture(m)));
            }

            foreach (var move in learned)
            {
                Log.Enqueue(new BattleMessage($"{m.Name} learned {move.Name}!", BattleCue.LevelUp, m));
            }

            foreach (var move in pendingMoves)
            {
                pendingMoveChoices.Enqueue(new MoveLearnChoice(m, move));
                Log.Enqueue(new BattleMessage($"{m.Name} wants to learn {move.Name}.", BattleCue.LevelUp, m,
                    move: move));
            }
        }
    }

    // Onboarding-friendly XP: a solid base scaled by the opponent's level, multiplied hard at low
    // levels so early creatures shoot up toward the first training tiers.
    private static int XpAward(int wildLevel, int monLevel, bool active)
    {
        var baseGain = 16 + wildLevel * 8;
        var levelFactor = monLevel <= 8 ? 3.0f : monLevel <= 15 ? 1.7f : 1.0f;
        var shareFactor = active ? 1.0f : 0.5f;
        return Math.Max(1, (int)(baseGain * levelFactor * shareFactor));
    }

    private void EndOfTurn()
    {
        if (Outcome != BattleOutcome.Ongoing)
        {
            return;
        }

        BurnTick(Active, BattleCue.PlayerHurt);
        if (PlayerDown())
        {
            return;
        }

        BurnTick(Wild, BattleCue.WildHurt);
        if (WildDown())
        {
            return;
        }

        PoisonTick(Active, BattleCue.PlayerHurt);
        if (PlayerDown())
        {
            return;
        }

        PoisonTick(Wild, BattleCue.WildHurt);
        if (WildDown())
        {
            return;
        }

        FieldHealingTick(Active);
        FieldHealingTick(Wild);
        if (Outcome != BattleOutcome.Ongoing)
        {
            return;
        }

        LeechSeedTick(Active, Wild);
        if (PlayerDown())
        {
            return;
        }

        LeechSeedTick(Wild, Active);
        if (WildDown())
        {
            return;
        }

        WeatherTick(Active, BattleCue.PlayerHurt);
        if (PlayerDown())
        {
            return;
        }

        WeatherTick(Wild, BattleCue.WildHurt);
        if (WildDown())
        {
            return;
        }

        YawnTick(Active);
        YawnTick(Wild);
        EndOfTurnAbilities(Active);
        EndOfTurnAbilities(Wild);
        AdvanceFieldTimers();
    }

    private void FieldHealingTick(MonsterInstance monster)
    {
        if (monster.Fainted)
        {
            return;
        }

        var amount = 0;
        var labels = new List<string>();
        if (monster.AquaRingActive)
        {
            amount += Math.Max(1, monster.MaxHp / 16);
            labels.Add("Aqua Ring");
        }

        if (monster.IngrainActive)
        {
            amount += Math.Max(1, monster.MaxHp / 16);
            labels.Add("Ingrain");
        }

        if (Terrain == BattleTerrain.Grassy)
        {
            amount += Math.Max(1, monster.MaxHp / 16);
            labels.Add("Grassy Terrain");
        }

        if (amount <= 0 || monster.CurrentHp >= monster.MaxHp)
        {
            return;
        }

        monster.Heal(amount);
        Log.Enqueue(new BattleMessage($"{monster.Name} restored HP with {string.Join(", ", labels)}!",
            BattleCue.Heal, monster, monster.CurrentHp, stateAfter: BattleSnapshot.Capture(monster)));
    }

    private void LeechSeedTick(MonsterInstance seeded, MonsterInstance healer)
    {
        if (!seeded.LeechSeeded || seeded.Fainted)
        {
            return;
        }

        var damage = Math.Min(seeded.CurrentHp, Math.Max(1, seeded.MaxHp / 8));
        seeded.CurrentHp -= damage;
        var cue = ReferenceEquals(seeded, Wild) ? BattleCue.WildHurt : BattleCue.PlayerHurt;
        Log.Enqueue(new BattleMessage($"{seeded.Name}'s health was sapped by Leech Seed! ({damage})", cue,
            seeded, seeded.CurrentHp, stateAfter: BattleSnapshot.Capture(seeded)));
        if (!healer.Fainted && healer.CurrentHp < healer.MaxHp)
        {
            healer.Heal(damage);
            Log.Enqueue(new BattleMessage($"{healer.Name} absorbed nutrients!", BattleCue.Heal, healer,
                healer.CurrentHp, stateAfter: BattleSnapshot.Capture(healer)));
        }
    }

    private void WeatherTick(MonsterInstance monster, BattleCue hurtCue)
    {
        if (WeatherSuppressed || monster.Fainted)
        {
            return;
        }

        if (Weather == BattleWeather.Sandstorm &&
            !monster.HasType(Element.Rock) && !monster.HasType(Element.Ground) && !monster.HasType(Element.Steel) &&
            monster.Ability is not ("Sand Veil" or "Sand Rush" or "Sand Force" or "Overcoat" or "Magic Guard"))
        {
            var damage = Math.Min(monster.CurrentHp, Math.Max(1, monster.MaxHp / 16));
            monster.CurrentHp -= damage;
            Log.Enqueue(new BattleMessage($"{monster.Name} was buffeted by the sandstorm! ({damage})",
                hurtCue, monster, monster.CurrentHp, stateAfter: BattleSnapshot.Capture(monster)));
        }

        if (Weather == BattleWeather.Rain && monster.Ability is "Rain Dish" && monster.CurrentHp < monster.MaxHp)
        {
            monster.Heal(Math.Max(1, monster.MaxHp / 16));
            Log.Enqueue(new BattleMessage($"{monster.Name} restored HP with Rain Dish!", BattleCue.Heal,
                monster, monster.CurrentHp, stateAfter: BattleSnapshot.Capture(monster)));
        }

        if (Weather == BattleWeather.Rain && monster.Ability is "Hydration" && monster.Status != Status.None)
        {
            monster.CureStatus();
            Log.Enqueue(new BattleMessage($"{monster.Name}'s Hydration cured its status!", BattleCue.Buff,
                monster, monster.CurrentHp, stateAfter: BattleSnapshot.Capture(monster)));
        }

        if (Weather == BattleWeather.Sun && monster.Ability is "Solar Power")
        {
            IndirectDamage(monster, monster.MaxHp / 8, "Solar Power");
        }
    }

    private void YawnTick(MonsterInstance monster)
    {
        if (monster.Fainted || monster.YawnTurns <= 0)
        {
            return;
        }

        monster.YawnTurns--;
        if (monster.YawnTurns == 0)
        {
            TryInflict(monster, Status.Sleep, null);
        }
    }

    private void AdvanceFieldTimers()
    {
        if (weatherTurns > 0)
        {
            weatherTurns--;
            if (weatherTurns == 0)
            {
                Log.Enqueue(new BattleMessage(Weather switch
                {
                    BattleWeather.Sun => "The sunlight faded.",
                    BattleWeather.Rain => "The rain stopped.",
                    BattleWeather.Sandstorm => "The sandstorm subsided.",
                    BattleWeather.Snow => "The snow stopped.",
                    _ => string.Empty,
                }, BattleCue.Info));
                Weather = BattleWeather.None;
            }
        }

        if (terrainTurns > 0)
        {
            terrainTurns--;
            if (terrainTurns == 0)
            {
                Log.Enqueue(new BattleMessage("The terrain returned to normal.", BattleCue.Info));
                Terrain = BattleTerrain.None;
            }
        }

        TickScreen(ref playerReflectTurns, "Your Reflect wore off.");
        TickScreen(ref wildReflectTurns, "The foe's Reflect wore off.");
        TickScreen(ref playerLightScreenTurns, "Your Light Screen wore off.");
        TickScreen(ref wildLightScreenTurns, "The foe's Light Screen wore off.");
    }

    private void TickScreen(ref int turns, string message)
    {
        if (turns <= 0)
        {
            return;
        }

        turns--;
        if (turns == 0)
        {
            Log.Enqueue(new BattleMessage(message, BattleCue.Info));
        }
    }

    private void BurnTick(MonsterInstance monster, BattleCue hurtCue)
    {
        if (monster.Status != Status.Burn || monster.Fainted || monster.Ability is "Magic Guard")
        {
            return;
        }

        var damage = Math.Min(monster.CurrentHp, Math.Max(1, monster.MaxHp / 16));
        monster.CurrentHp -= damage;
        Log.Enqueue(new BattleMessage($"{monster.Name} was hurt by its burn. ({damage})", hurtCue, monster,
            monster.CurrentHp, stateAfter: BattleSnapshot.Capture(monster)));
    }

    private void PoisonTick(MonsterInstance monster, BattleCue hurtCue)
    {
        if (monster.Status != Status.Poison || monster.Fainted)
        {
            return;
        }

        // Poison Heal restores HP instead of taking poison damage.
        if (monster.Ability is "Poison Heal")
        {
            if (monster.CurrentHp < monster.MaxHp)
            {
                monster.Heal(Math.Max(1, monster.MaxHp / 8));
                Log.Enqueue(new BattleMessage($"{monster.Name} restored HP with Poison Heal!", BattleCue.Heal,
                    monster, monster.CurrentHp, stateAfter: BattleSnapshot.Capture(monster)));
            }

            return;
        }

        if (monster.Ability is "Magic Guard")
        {
            return;
        }

        var damage = Math.Min(monster.CurrentHp, Math.Max(1, monster.MaxHp / 8));
        monster.CurrentHp -= damage;
        Log.Enqueue(new BattleMessage($"{monster.Name} was hurt by poison. ({damage})", hurtCue, monster,
            monster.CurrentHp, stateAfter: BattleSnapshot.Capture(monster)));
    }

    public void FinalizeStats()
    {
        if (statsFinalized)
        {
            return;
        }

        statsFinalized = true;
        var victory = Outcome is BattleOutcome.Won or BattleOutcome.Captured;
        foreach (var monster in participants)
        {
            monster.RecordBattle(victory);
        }
    }
}
