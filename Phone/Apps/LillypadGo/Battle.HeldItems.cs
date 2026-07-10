namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// Held-item behaviour. The passive numbers live in HeldItems.SpecFor(id) and are read by the damage,
// speed and accuracy formulas in Battle.cs; everything that *fires* — berries, orbs, herbs, Rocky
// Helmet, Life Orb recoil — is implemented here as a hook the turn loop calls at a defined moment:
//
//   entry            HeldItemOnEntry          Air Balloon announces itself.
//   before the turn  HeldQuickClawWins        Quick Claw rolls for a free first strike.
//   damage formula   HeldPowerMultiplier      type boosters, Life Orb, Expert Belt, bands.
//                    HeldAttackMultiplier     Choice Band/Specs, Light Ball, Thick Club.
//                    HeldDefenseMultiplier    Eviolite, Assault Vest, Metal Powder.
//                    HeldDamageTakenMultiplier resist berries (consumed here).
//   after a hit      HeldItemOnDealtDamage    Life Orb, Shell Bell, King's Rock.
//                    HeldItemOnTookDamage     Rocky Helmet, reaction berries, Weakness Policy.
//   HP/status change HeldItemAfterHpChange    Oran/Sitrus/pinch berries, Focus Band's cousin checks.
//                    HeldItemAfterStatus      Cheri/Chesto/Lum and friends.
//   end of turn      HeldItemEndOfTurn        Leftovers, Black Sludge, orbs, herbs, Leppa.
//
// A held item that is used up clears MonsterInstance.HeldItem, so it is gone for the rest of the
// battle *and* out of the bag — the same as the mainline games.
internal sealed partial class Battle
{
    // The item's passive numbers, or null when it has none, when the holder is the wrong species
    // (Light Ball on anything but Pikachu), or when the holder has outgrown it (Eviolite).
    private static HeldSpec? ActiveSpec(MonsterInstance holder)
    {
        if (!holder.HasHeldItem || HeldItems.SpecFor(holder.HeldItem) is not { } spec)
        {
            return null;
        }

        if (spec.SpeciesLock is { } species &&
            Array.IndexOf(species, holder.Species.Id) < 0)
        {
            return null;
        }

        if (spec.OnlyIfUnevolved && Dex.EvolutionOf(holder.Species) is null)
        {
            return null;
        }

        return spec;
    }

    private void ConsumeHeldItem(MonsterInstance holder)
    {
        if (Items.IsBerry(holder.HeldItem))
        {
            holder.HeldBerryConsumed = true;
        }

        holder.HeldItem = string.Empty;
    }

    private void ItemMessage(MonsterInstance holder, string text, BattleCue cue = BattleCue.Buff) =>
        Log.Enqueue(new BattleMessage(text, cue, holder, holder.CurrentHp,
            stateAfter: BattleSnapshot.Capture(holder)));

    private static string ItemName(string id) => Items.Find(id)?.Name ?? id;

    // ---- Passive numbers, read by the damage formula ----------------------------------------------

    // Base-power multiplier: the 18 type boosters and 17 plates, Life Orb, Expert Belt, the bands.
    private float HeldPowerMultiplier(MonsterInstance attacker, MoveDef move, float effectiveness)
    {
        if (ActiveSpec(attacker) is not { } spec)
        {
            return 1f;
        }

        var multiplier = spec.AllPower;
        if (spec.BoostType == move.Element)
        {
            multiplier *= 1.2f;
        }

        if (spec.BoostsSuperEffective && effectiveness > 1f)
        {
            multiplier *= 1.2f;
        }

        multiplier *= move.Category switch
        {
            MoveCategory.Physical => spec.PhysicalPower,
            MoveCategory.Special => spec.SpecialPower,
            _ => 1f,
        };

        return multiplier;
    }

    // Attacking-stat multiplier: the Choice trio and the species-locked classics.
    private float HeldAttackMultiplier(MonsterInstance attacker, MoveDef move)
    {
        if (ActiveSpec(attacker) is not { } spec)
        {
            return 1f;
        }

        return move.Category == MoveCategory.Special ? spec.SpAtkMultiplier : spec.AtkMultiplier;
    }

    // Defending-stat multiplier: Eviolite, Assault Vest, Metal Powder.
    private float HeldDefenseMultiplier(MonsterInstance defender, MoveDef move)
    {
        if (ActiveSpec(defender) is not { } spec)
        {
            return 1f;
        }

        return move.Category == MoveCategory.Special ? spec.SpDefMultiplier : spec.DefMultiplier;
    }

    // Resist berries. Eaten the instant they soften a hit, so this both reports the multiplier and
    // consumes the berry — call it exactly once per damaging hit.
    private float HeldDamageTakenMultiplier(MonsterInstance defender, MoveDef move, float effectiveness)
    {
        if (!defender.HasHeldItem ||
            !HeldItems.ResistBerryType.TryGetValue(defender.HeldItem, out var resisted) ||
            move.Element != resisted)
        {
            return 1f;
        }

        // Chilan Berry blunts every Normal hit; the other seventeen only answer a super-effective one.
        if (resisted != Element.Normal && effectiveness <= 1f)
        {
            return 1f;
        }

        var berry = ItemName(defender.HeldItem);
        ConsumeHeldItem(defender);
        ItemMessage(defender, $"{defender.Name} ate its {berry} and weakened the hit!", BattleCue.Info);
        return 0.5f;
    }

    private int HeldCritStages(MonsterInstance attacker) => ActiveSpec(attacker)?.CritStages ?? 0;

    private float HeldSpeedMultiplier(MonsterInstance monster) => ActiveSpec(monster)?.SpeedMultiplier ?? 1f;

    // The holder's own accuracy (Wide Lens always, Zoom Lens only when striking second).
    private float HeldAccuracyMultiplier(MonsterInstance attacker)
    {
        if (ActiveSpec(attacker) is not { } spec ||
            (spec.AccuracyWhenMovingSecond && !attacker.MovingSecond))
        {
            return 1f;
        }

        return spec.AccuracyMultiplier;
    }

    // Accuracy of moves aimed *at* this creature (Bright Powder, Lax Incense).
    private float HeldEvasionMultiplier(MonsterInstance defender) => ActiveSpec(defender)?.FoeAccuracyMultiplier ?? 1f;

    private static bool HeldIgnoresHazards(MonsterInstance monster) => ActiveSpec(monster)?.IgnoresHazards ?? false;

    private static bool HeldBlocksStatusMoves(MonsterInstance monster) =>
        ActiveSpec(monster)?.BlocksStatusMoves ?? false;

    private static bool HeldBoostsDrain(MonsterInstance monster) => ActiveSpec(monster)?.BoostsDrain ?? false;

    // Big Root: draining moves and Leech Seed give back 30% more.
    private static int HeldDrainAmount(MonsterInstance drainer, int amount) =>
        HeldBoostsDrain(drainer) ? Math.Max(1, (int)(amount * 1.3f)) : amount;

    private static bool HeldMovesLast(MonsterInstance monster) => ActiveSpec(monster)?.MovesLast ?? false;

    // The weight the weight-scaled moves (Low Kick, Grass Knot, Heavy Slam, Heat Crash) see. Float
    // Stone halves it. Floored well above zero so Heavy Slam's ratio can never divide by nothing.
    private static float EffectiveWeight(MonsterInstance monster) =>
        Math.Max(0.1f, monster.Species.WeightKg * (ActiveSpec(monster)?.WeightMultiplier ?? 1f));

    // The powder and spore moves Safety Goggles filters out.
    private static bool IsPowderMove(MoveDef move) => move.Name is "Sleep Powder" or "Stun Spore" or "Poison Powder"
        or "Spore" or "Cotton Spore" or "Rage Powder" or "Powder" or "Magic Powder";

    private bool HeldBlocksMove(MonsterInstance defender, MoveDef move)
    {
        if (ActiveSpec(defender) is not { } spec)
        {
            return false;
        }

        if (spec.BlocksPowder && IsPowderMove(move))
        {
            Log.Enqueue(new BattleMessage($"{defender.Name}'s {ItemName(defender.HeldItem)} blocked the powder!",
                BattleCue.Info, defender));
            return true;
        }

        if (spec.FloatsOverGround && !move.IsStatus && move.Element == Element.Ground)
        {
            Log.Enqueue(new BattleMessage($"{defender.Name} floats above the ground with its Air Balloon!",
                BattleCue.Info, defender));
            return true;
        }

        return false;
    }

    // How long a field effect this creature just set should last. Light Clay and the weather rocks
    // stretch five turns to eight.
    private static int HeldFieldDuration(MonsterInstance setter, string itemId, int baseTurns) =>
        setter.HeldItem == itemId ? 8 : baseTurns;

    private static int HeldScreenTurns(MonsterInstance setter) => HeldFieldDuration(setter, "lightclay", 5);

    private static int HeldWeatherTurns(MonsterInstance setter, BattleWeather weather) => weather switch
    {
        BattleWeather.Sun => HeldFieldDuration(setter, "heatrock", 5),
        BattleWeather.Rain => HeldFieldDuration(setter, "damprock", 5),
        BattleWeather.Sandstorm => HeldFieldDuration(setter, "smoothrock", 5),
        BattleWeather.Snow => HeldFieldDuration(setter, "icyrock", 5),
        _ => 5,
    };

    // ---- Choice lock ------------------------------------------------------------------------------

    // The Choice trio locks its holder into the first move it executes. Cleared by ResetBattleState
    // when the creature switches out, exactly like Encore.
    private void HeldRememberChoice(MonsterInstance attacker, MoveDef move)
    {
        if (ActiveSpec(attacker) is { ChoiceLocked: true } && attacker.ChoiceLockedMove is null)
        {
            attacker.ChoiceLockedMove = move;
        }
    }

    // The move a Choice item forces this creature to repeat, if any. Null once the item leaves or the
    // creature no longer knows the locked move.
    public MoveDef? ChoiceLockedMove(MonsterInstance monster) =>
        ActiveSpec(monster) is { ChoiceLocked: true } && monster.ChoiceLockedMove is { } locked &&
        monster.Knows(locked)
            ? locked
            : null;

    // Assault Vest holders cannot select a status move.
    public bool IsMoveBlockedByItem(MonsterInstance monster, MoveDef move) =>
        move.IsStatus && HeldBlocksStatusMoves(monster);

    // ---- Turn order -------------------------------------------------------------------------------

    // Quick Claw: a 1-in-5 shot at going first regardless of Speed. Rolled once per turn per holder.
    private bool HeldQuickClawWins(MonsterInstance monster) =>
        monster.HeldItem == "quickclaw" && rng.Next(5) == 0;

    // ---- Entry ------------------------------------------------------------------------------------

    private void HeldItemOnEntry(MonsterInstance entrant)
    {
        if (entrant.HeldItem == "airballoon" && !entrant.Fainted)
        {
            Log.Enqueue(new BattleMessage($"{entrant.Name} floats in the air with its Air Balloon!",
                BattleCue.Info, entrant));
        }
    }

    // ---- After the holder lands a hit --------------------------------------------------------------

    private void HeldItemOnDealtDamage(MonsterInstance attacker, MonsterInstance defender, MoveDef move, int damage)
    {
        if (damage <= 0 || attacker.Fainted)
        {
            return;
        }

        switch (attacker.HeldItem)
        {
            case "lifeorb" when attacker.Ability is not "Magic Guard":
                IndirectDamage(attacker, Math.Max(1, attacker.MaxHp / 10), "its Life Orb");
                break;
            case "shellbell" when attacker.CurrentHp < attacker.MaxHp && attacker.HealBlockTurns <= 0:
                attacker.Heal(Math.Max(1, damage / 8));
                ItemMessage(attacker, $"{attacker.Name} restored HP with its Shell Bell!", BattleCue.Heal);
                break;
            case "kingsrock" or "razorfang" when !defender.Fainted && rng.Next(10) == 0:
                defender.Flinched = true;
                Log.Enqueue(new BattleMessage($"{defender.Name} flinched from the {ItemName(attacker.HeldItem)}!",
                    BattleCue.Info, defender));
                break;
        }
    }

    // ---- After the holder is hit -------------------------------------------------------------------

    // Everything the defender's item does in answer to a landed hit: contact punishment, the
    // reaction berries, Weakness Policy, the absorb items, and Air Balloon popping.
    private void HeldItemOnTookDamage(MonsterInstance defender, MonsterInstance attacker, MoveDef move,
        float effectiveness)
    {
        if (defender.HeldItem == "airballoon")
        {
            defender.HeldItem = string.Empty;
            Log.Enqueue(new BattleMessage($"{defender.Name}'s Air Balloon popped!", BattleCue.Info, defender));
            return;
        }

        if (defender.Fainted || !defender.HasHeldItem)
        {
            return;
        }

        var contact = IsContact(move);
        var item = defender.HeldItem;

        // Contact punishment hurts the attacker and leaves the item intact.
        if (contact && item == "rockyhelmet" && !attacker.Fainted)
        {
            IndirectDamage(attacker, Math.Max(1, attacker.MaxHp / 6), $"{defender.Name}'s Rocky Helmet");
            return;
        }

        // Jaboca / Rowap answer the category they are tuned to, then are eaten.
        var retaliates = (item == "jabocaberry" && move.Category == MoveCategory.Physical) ||
            (item == "rowapberry" && move.Category == MoveCategory.Special);
        if (retaliates && !attacker.Fainted)
        {
            var berry = ItemName(item);
            ConsumeHeldItem(defender);
            IndirectDamage(attacker, Math.Max(1, attacker.MaxHp / 8), $"{defender.Name}'s {berry}");
            return;
        }

        switch (item)
        {
            case "enigmaberry" when effectiveness > 1f && defender.CurrentHp < defender.MaxHp:
                ConsumeHeldItem(defender);
                defender.Heal(Math.Max(1, defender.MaxHp / 4));
                ItemMessage(defender, $"{defender.Name} ate its Enigma Berry and recovered!", BattleCue.Heal);
                return;
            case "keeberry" when move.Category == MoveCategory.Physical:
                ConsumeHeldItem(defender);
                RaiseStat(defender, StatKind.Def, 1);
                ItemMessage(defender, $"{defender.Name} ate its Kee Berry and hardened its Defense!");
                return;
            case "marangaberry" when move.Category == MoveCategory.Special:
                ConsumeHeldItem(defender);
                RaiseStat(defender, StatKind.SpD, 1);
                ItemMessage(defender, $"{defender.Name} ate its Maranga Berry and steeled its Sp. Def!");
                return;
            case "weaknesspolicy" when effectiveness > 1f:
                defender.HeldItem = string.Empty;
                RaiseStat(defender, StatKind.Atk, 2);
                RaiseStat(defender, StatKind.SpA, 2);
                ItemMessage(defender, $"{defender.Name}'s Weakness Policy sharply boosted its attacks!");
                return;
        }

        // Absorb Bulb / Cell Battery / Snowball / Luminous Moss: one stat, one type, one use.
        if (HeldItems.AbsorbItem.TryGetValue(item, out var absorb) && move.Element == absorb.Type)
        {
            var name = ItemName(item);
            defender.HeldItem = string.Empty;
            RaiseStat(defender, ToStatKind(absorb.Stat), 1);
            ItemMessage(defender, $"{defender.Name}'s {name} raised its {absorb.Word}!");
        }
    }

    private static StatKind ToStatKind(BoostStat stat) => stat switch
    {
        BoostStat.Atk => StatKind.Atk,
        BoostStat.Def => StatKind.Def,
        BoostStat.SpAtk => StatKind.SpA,
        BoostStat.SpDef => StatKind.SpD,
        _ => StatKind.Spe,
    };

    // ---- HP thresholds -----------------------------------------------------------------------------

    // Called whenever the holder's HP drops: the healing berries at half, the pinch berries at a
    // quarter. Only one berry can be eaten per check, which is all a creature can hold anyway.
    private void HeldItemAfterHpChange(MonsterInstance monster)
    {
        if (monster.Fainted || !monster.HasHeldItem)
        {
            return;
        }

        var item = monster.HeldItem;
        var name = ItemName(item);

        // Half-HP healing berries.
        if (monster.HpFraction <= 0.5f)
        {
            var healed = item switch
            {
                "oranberry" => 10,
                "berryjuice" => 20,
                "sitrusberry" => Math.Max(1, monster.MaxHp / 4),
                _ => 0,
            };
            if (healed > 0)
            {
                ConsumeHeldItem(monster);
                monster.Heal(healed);
                ItemMessage(monster, $"{monster.Name} ate its {name}!", BattleCue.Heal);
                return;
            }
        }

        if (monster.HpFraction > 0.25f)
        {
            return;
        }

        // Quarter-HP pinch berries: a third of max HP back, or a sharp stat spike.
        if (HeldItems.IsPinchHealBerry(item))
        {
            ConsumeHeldItem(monster);
            monster.Heal(Math.Max(1, monster.MaxHp / 3));
            ItemMessage(monster, $"{monster.Name} ate its {name}!", BattleCue.Heal);
            return;
        }

        if (HeldItems.PinchBerryStat.TryGetValue(item, out var stat))
        {
            ConsumeHeldItem(monster);
            if (stat == BoostStat.Random)
            {
                stat = (BoostStat)rng.Next(5);
            }

            RaiseStat(monster, ToStatKind(stat), 2);
            ItemMessage(monster, $"{monster.Name} ate its {name} and its power surged!");
            return;
        }

        if (item == "starfberry")
        {
            ConsumeHeldItem(monster);
            var rolled = (BoostStat)rng.Next(5);
            RaiseStat(monster, ToStatKind(rolled), 2);
            ItemMessage(monster, $"{monster.Name} ate its Starf Berry and its power surged!");
        }
    }

    // ---- Status ------------------------------------------------------------------------------------

    // Cheri/Chesto/Pecha/Rawst/Aspear each cure their one condition; Lum cures any. Called right
    // after a status lands and again at end of turn, so a berry never sits on an idle condition.
    private void HeldItemAfterStatus(MonsterInstance monster)
    {
        if (monster.Fainted || !monster.HasHeldItem)
        {
            return;
        }

        var item = monster.HeldItem;
        if (monster.Status != Status.None &&
            (item == "lumberry" ||
             (HeldItems.StatusBerryCures.TryGetValue(item, out var cured) && cured == monster.Status)))
        {
            var name = ItemName(item);
            ConsumeHeldItem(monster);
            monster.CureStatus();
            ItemMessage(monster, $"{monster.Name} ate its {name} and shook it off!", BattleCue.Heal);
            return;
        }

        if (monster.ConfusionTurns > 0 && item is "persimberry" or "lumberry")
        {
            var name = ItemName(item);
            ConsumeHeldItem(monster);
            monster.ConfusionTurns = 0;
            ItemMessage(monster, $"{monster.Name} ate its {name} and snapped out of confusion!", BattleCue.Heal);
        }
    }

    // ---- Herbs ---------------------------------------------------------------------------------------

    // Both herbs answer the instant the thing they hate happens, not at end of turn: a White Herb
    // holder that took an Intimidate must have its Attack back before it swings this turn.
    private void HeldItemAfterStatDrop(MonsterInstance monster)
    {
        if (monster.Fainted || monster.HeldItem != "whiteherb" || !HasLoweredStat(monster))
        {
            return;
        }

        monster.HeldItem = string.Empty;
        RestoreLoweredStats(monster);
        ItemMessage(monster, $"{monster.Name}'s White Herb restored its stats!");
    }

    private void HeldItemAfterMoveLock(MonsterInstance monster)
    {
        if (monster.Fainted || monster.HeldItem != "mentalherb" ||
            (monster.TauntTurns <= 0 && monster.EncoreTurns <= 0 && monster.DisableTurns <= 0))
        {
            return;
        }

        monster.HeldItem = string.Empty;
        monster.TauntTurns = 0;
        monster.EncoreTurns = 0;
        monster.EncoreMove = null;
        monster.DisableTurns = 0;
        monster.DisabledMove = null;
        ItemMessage(monster, $"{monster.Name}'s Mental Herb cleared its head!");
    }

    // ---- End of turn --------------------------------------------------------------------------------

    private void HeldItemEndOfTurn(MonsterInstance monster)
    {
        HeldItemAfterStatus(monster);

        // Safety net: a drop or a lock applied outside the two hooks above still gets answered here.
        HeldItemAfterStatDrop(monster);
        HeldItemAfterMoveLock(monster);
        if (monster.Fainted || !monster.HasHeldItem)
        {
            return;
        }

        switch (monster.HeldItem)
        {
            case "leftovers" when monster.CurrentHp < monster.MaxHp && monster.HealBlockTurns <= 0:
                monster.Heal(Math.Max(1, monster.MaxHp / 16));
                ItemMessage(monster, $"{monster.Name} restored HP with Leftovers!", BattleCue.Heal);
                break;

            // Black Sludge nourishes a Poison-type and poisons everything else.
            case "blacksludge" when monster.HasType(Element.Poison):
                if (monster.CurrentHp < monster.MaxHp && monster.HealBlockTurns <= 0)
                {
                    monster.Heal(Math.Max(1, monster.MaxHp / 16));
                    ItemMessage(monster, $"{monster.Name} restored HP with Black Sludge!", BattleCue.Heal);
                }

                break;
            case "blacksludge":
                IndirectDamage(monster, Math.Max(1, monster.MaxHp / 8), "the Black Sludge");
                break;

            case "stickybarb":
                IndirectDamage(monster, Math.Max(1, monster.MaxHp / 8), "the Sticky Barb");
                break;

            case "toxicorb" when monster.Status == Status.None:
                if (TryInflict(monster, Status.Poison, null, badlyPoisoned: true))
                {
                    ItemMessage(monster, $"{monster.Name} was badly poisoned by its Toxic Orb!", BattleCue.Info);
                }

                break;
            case "flameorb" when monster.Status == Status.None:
                if (TryInflict(monster, Status.Burn, null))
                {
                    ItemMessage(monster, $"{monster.Name} was burned by its Flame Orb!", BattleCue.Info);
                }

                break;

            // Leppa Berry tops up the first move that has run dry.
            case "leppaberry":
                var empty = monster.Pp.FindIndex(pp => pp <= 0);
                if (empty >= 0)
                {
                    ConsumeHeldItem(monster);
                    monster.Pp[empty] = Math.Min(monster.Moves[empty].Pp, 10);
                    ItemMessage(monster,
                        $"{monster.Name} ate its Leppa Berry and restored {monster.Moves[empty].Name}!",
                        BattleCue.Heal);
                }

                break;
        }

        HeldItemAfterHpChange(monster);
    }

    private static bool HasLoweredStat(MonsterInstance m) => m.AtkStage < 0 || m.DefStage < 0 || m.SpAtkStage < 0 ||
        m.SpDefStage < 0 || m.SpdStage < 0 || m.AccuracyStage < 0 || m.EvasionStage < 0;

    private static void RestoreLoweredStats(MonsterInstance m)
    {
        m.AtkStage = Math.Max(0, m.AtkStage);
        m.DefStage = Math.Max(0, m.DefStage);
        m.SpAtkStage = Math.Max(0, m.SpAtkStage);
        m.SpDefStage = Math.Max(0, m.SpDefStage);
        m.SpdStage = Math.Max(0, m.SpdStage);
        m.AccuracyStage = Math.Max(0, m.AccuracyStage);
        m.EvasionStage = Math.Max(0, m.EvasionStage);
    }
}
