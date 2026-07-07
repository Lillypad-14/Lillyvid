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

    // Wild encounter: a lone opponent that can be caught.
    public Battle(List<MonsterInstance> party, MonsterInstance wild, Bag bag, Random rng)
        : this(party, bag, rng)
    {
        Wild = wild;
        Wild.ResetBattleState();
        Log.Enqueue(new BattleMessage($"A wild {Wild.Name} appeared!", BattleCue.Info));
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
    }

    public float EscapeChance
    {
        get
        {
            var chance = Active.EffectiveSpd >= Wild.EffectiveSpd
                ? 0.9f
                : 0.35f + Active.EffectiveSpd / (float)(Wild.EffectiveSpd + 1) * 0.4f;
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
        var playerMove = struggling ? Moves.Struggle : Active.Moves[moveIndex];
        var wildMove = ChooseWildMove();
        var playerFirst = playerMove.Priority > wildMove.Priority ||
            playerMove.Priority == wildMove.Priority && (Active.EffectiveSpd > Wild.EffectiveSpd ||
                Active.EffectiveSpd == Wild.EffectiveSpd && rng.Next(2) == 0);
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
        Active.ResetBattleState();
        activeIndex = partyIndex;
        participants.Add(Active);
        Log.Enqueue(new BattleMessage($"Go, {Active.Name}!", BattleCue.PlayerSwitch, Active, Active.CurrentHp,
            stateAfter: BattleSnapshot.Capture(Active)));
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
                var raw = ((2f * monster.Level / 5f + 2f) * 40f * monster.EffectiveAtk /
                    Math.Max(1, monster.EffectiveDef) / 50f) + 2f;
                var damage = Math.Min(monster.CurrentHp, Math.Max(1, (int)(raw *
                    (0.85f + (float)rng.NextDouble() * 0.15f))));
                monster.CurrentHp -= damage;
                var cue = ReferenceEquals(monster, Wild) ? BattleCue.WildHurt : BattleCue.PlayerHurt;
                Log.Enqueue(new BattleMessage($"It hurt itself in its confusion! ({damage})", cue, monster,
                    monster.CurrentHp, stateAfter: BattleSnapshot.Capture(monster)));
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

        if (move.Accuracy > 0)
        {
            // Accuracy is scaled by the attacker's accuracy stage vs the defender's evasion stage
            // (Sand Attack, Double Team, etc.), using the standard 3/(3±n) stage ratio.
            var stage = Math.Clamp(attacker.AccuracyStage - defender.EvasionStage, -6, 6);
            var stageMul = stage >= 0 ? (3f + stage) / 3f : 3f / (3f - stage);
            if (rng.NextDouble() * 100f >= move.Accuracy * stageMul)
            {
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s attack missed!", BattleCue.Info));
                return;
            }
        }

        if (!move.IsStatus)
        {
            var isStruggle = ReferenceEquals(move, Moves.Struggle);
            var effectiveness = isStruggle ? 1f :
                Elements.Effectiveness(move.Element, defender.Element, defender.SecondaryElement);
            if (effectiveness <= 0f)
            {
                Log.Enqueue(new BattleMessage($"It doesn't affect {defender.Name}...", BattleCue.Info, defender,
                    effectiveness: 0f));
                return;
            }

            var stab = !isStruggle && attacker.HasType(move.Element) ? 1.5f : 1f;
            var crit = rng.Next(24) == 0 ? 1.5f : 1f;
            var variance = 0.85f + (float)rng.NextDouble() * 0.15f;
            var isCritical = crit > 1f;
            var attack = attacker.OffensiveStat(move.Category, isCritical);
            var defense = defender.DefensiveStat(move.Category, isCritical);
            var burn = attacker.Status == Status.Burn && move.Category == MoveCategory.Physical ? 0.5f : 1f;
            var raw = ((2f * attacker.Level / 5f + 2f) * move.Power * attack /
                Math.Max(1, defense) / 50f) + 2f;
            var damage = Math.Max(1, (int)(raw * effectiveness * stab * crit * variance * burn));
            var appliedDamage = Math.Min(defender.CurrentHp, damage);
            defender.CurrentHp -= appliedDamage;
            if (!ReferenceEquals(attacker, Wild))
            {
                attacker.RecordDamage(appliedDamage);
            }
            Log.Enqueue(new BattleMessage(DamageText(defender, appliedDamage, effectiveness, crit > 1f), hurtCue,
                defender, defender.CurrentHp, crit > 1f, effectiveness, move,
                stateAfter: BattleSnapshot.Capture(defender)));
            if (!defender.Fainted && defender.Status == Status.Freeze && move.Element == Element.Fire)
            {
                defender.Status = Status.None;
                Log.Enqueue(new BattleMessage($"{defender.Name} thawed out!", BattleCue.Buff, defender,
                    stateAfter: BattleSnapshot.Capture(defender)));
            }
        }

        ApplyEffect(attacker, defender, move);
    }

    private void ApplyEffect(MonsterInstance attacker, MonsterInstance defender, MoveDef move)
    {
        if (move.Effect == MoveEffect.None)
        {
            return;
        }

        if (!move.IsStatus && move.EffectChance > 0 && rng.NextDouble() * 100f > move.EffectChance)
        {
            return;
        }

        switch (move.Effect)
        {
            case MoveEffect.RaiseAtk:
                attacker.AtkStage = Math.Min(6, attacker.AtkStage + move.StageChange);
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s attack rose!", BattleCue.Buff, attacker,
                    stateAfter: BattleSnapshot.Capture(attacker)));
                break;
            case MoveEffect.RaiseDef:
                attacker.DefStage = Math.Min(6, attacker.DefStage + move.StageChange);
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s defense rose!", BattleCue.Buff, attacker,
                    stateAfter: BattleSnapshot.Capture(attacker)));
                break;
            case MoveEffect.RaiseSpd:
                attacker.SpdStage = Math.Min(6, attacker.SpdStage + move.StageChange);
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s speed rose!", BattleCue.Buff, attacker,
                    stateAfter: BattleSnapshot.Capture(attacker)));
                break;
            case MoveEffect.LowerTargetAtk:
                defender.AtkStage = Math.Max(-6, defender.AtkStage - move.StageChange);
                Log.Enqueue(new BattleMessage($"{defender.Name}'s attack fell!", BattleCue.Buff, defender,
                    stateAfter: BattleSnapshot.Capture(defender)));
                break;
            case MoveEffect.LowerTargetSpd:
                defender.SpdStage = Math.Max(-6, defender.SpdStage - move.StageChange);
                Log.Enqueue(new BattleMessage($"{defender.Name}'s speed fell!", BattleCue.Buff, defender,
                    stateAfter: BattleSnapshot.Capture(defender)));
                break;
            case MoveEffect.LowerTargetDef:
                defender.DefStage = Math.Max(-6, defender.DefStage - move.StageChange);
                Log.Enqueue(new BattleMessage($"{defender.Name}'s defense fell!", BattleCue.Buff, defender,
                    stateAfter: BattleSnapshot.Capture(defender)));
                break;
            case MoveEffect.LowerTargetSpAtk:
                defender.SpAtkStage = Math.Max(-6, defender.SpAtkStage - move.StageChange);
                Log.Enqueue(new BattleMessage($"{defender.Name}'s Sp. Atk fell!", BattleCue.Buff, defender,
                    stateAfter: BattleSnapshot.Capture(defender)));
                break;
            case MoveEffect.LowerTargetSpDef:
                defender.SpDefStage = Math.Max(-6, defender.SpDefStage - move.StageChange);
                Log.Enqueue(new BattleMessage($"{defender.Name}'s Sp. Def fell!", BattleCue.Buff, defender,
                    stateAfter: BattleSnapshot.Capture(defender)));
                break;
            case MoveEffect.LowerTargetAccuracy:
                defender.AccuracyStage = Math.Max(-6, defender.AccuracyStage - move.StageChange);
                Log.Enqueue(new BattleMessage($"{defender.Name}'s accuracy fell!", BattleCue.Buff, defender,
                    stateAfter: BattleSnapshot.Capture(defender)));
                break;
            case MoveEffect.RaiseEvasion:
                attacker.EvasionStage = Math.Min(6, attacker.EvasionStage + move.StageChange);
                Log.Enqueue(new BattleMessage($"{attacker.Name}'s evasiveness rose!", BattleCue.Buff, attacker,
                    stateAfter: BattleSnapshot.Capture(attacker)));
                break;
            case MoveEffect.HealUser:
                attacker.Heal(attacker.MaxHp / 2);
                Log.Enqueue(new BattleMessage($"{attacker.Name} mended its wounds.", BattleCue.Heal, attacker,
                    attacker.CurrentHp, stateAfter: BattleSnapshot.Capture(attacker)));
                break;
            case MoveEffect.Burn:
                if (defender.Status == Status.None && !defender.HasType(Element.Fire) && !defender.Fainted)
                {
                    defender.Status = Status.Burn;
                    Log.Enqueue(new BattleMessage($"{defender.Name} was scorched!", BattleCue.Buff, defender,
                        stateAfter: BattleSnapshot.Capture(defender)));
                }

                break;
            case MoveEffect.Freeze:
                if (defender.Status == Status.None && !defender.HasType(Element.Ice) && !defender.Fainted)
                {
                    defender.Status = Status.Freeze;
                    Log.Enqueue(new BattleMessage($"{defender.Name} was frozen solid!", BattleCue.Buff, defender,
                        stateAfter: BattleSnapshot.Capture(defender)));
                }

                break;
            case MoveEffect.Paralyze:
                if (defender.Status == Status.None && !defender.HasType(Element.Electric) && !defender.Fainted)
                {
                    defender.Status = Status.Paralysis;
                    Log.Enqueue(new BattleMessage($"{defender.Name} was paralyzed!", BattleCue.Buff, defender,
                        stateAfter: BattleSnapshot.Capture(defender)));
                }

                break;
            case MoveEffect.Poison:
                if (defender.Status == Status.None && !defender.HasType(Element.Poison) &&
                    !defender.HasType(Element.Steel) && !defender.Fainted)
                {
                    defender.Status = Status.Poison;
                    Log.Enqueue(new BattleMessage($"{defender.Name} was poisoned!", BattleCue.Buff, defender,
                        stateAfter: BattleSnapshot.Capture(defender)));
                }

                break;
            case MoveEffect.Flinch:
                if (!defender.Fainted)
                {
                    defender.Flinched = true;
                }

                break;
            case MoveEffect.RecoilQuarterMax:
                if (!attacker.Fainted)
                {
                    var recoil = Math.Min(attacker.CurrentHp, Math.Max(1, attacker.MaxHp / 4));
                    attacker.CurrentHp -= recoil;
                    var recoilCue = ReferenceEquals(attacker, Wild) ? BattleCue.WildHurt : BattleCue.PlayerHurt;
                    Log.Enqueue(new BattleMessage($"{attacker.Name} was damaged by recoil. ({recoil})", recoilCue,
                        attacker, attacker.CurrentHp, stateAfter: BattleSnapshot.Capture(attacker)));
                }

                break;
            case MoveEffect.Confuse:
                if (!defender.Fainted && defender.ConfusionTurns <= 0)
                {
                    defender.ConfusionTurns = rng.Next(2, 6);
                    Log.Enqueue(new BattleMessage($"{defender.Name} became confused!", BattleCue.Buff, defender));
                }

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
        WildDown();
    }

    private void BurnTick(MonsterInstance monster, BattleCue hurtCue)
    {
        if (monster.Status != Status.Burn || monster.Fainted)
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
