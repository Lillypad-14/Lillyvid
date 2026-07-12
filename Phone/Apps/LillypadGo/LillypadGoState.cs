using System.Numerics;
using System.Text.Json;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// Persisted trainer state (party, box, bag, dex, steps) plus transient overworld state
// (current zone/biome, the pending wild encounter, and whether a battle is open). Saved to
// its own lillypadgo.json so its internal types stay internal.
internal sealed class LillypadGoState
{
    public const int PartyLimit = 6;

    private readonly string path;

    public LillypadGoState(string path)
    {
        this.path = path;
    }

    public Bag Bag { get; private set; } = new();
    public List<MonsterInstance> Party { get; } = new();
    public List<MonsterInstance> Box { get; } = new();
    public HashSet<string> Seen { get; } = new();
    public HashSet<int> Badges { get; } = new();

    // Move ids for the Gen-IX TMs the trainer owns. TMs are permanent once bought (reusable), so a
    // set of owned move ids is all we persist.
    public HashSet<string> OwnedTms { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool StarterChosen { get; set; }
    public int Money { get; set; }
    public int TotalSteps { get; set; }
    public int BattlesWon { get; set; }
    public int Captures { get; set; }
    public float BattleEffectScale { get; set; } = 1.25f;
    public float BattleSpeed { get; set; } = 1f;
    public bool BattleLogEnabled { get; set; } = true;
    public bool BackgroundTrackingEnabled { get; set; } = true;

    // Shows the lead party Pokémon walking behind the player in the game world.
    public bool FollowerEnabled { get; set; } = true;

    // Mirrors phone battles into the game world (battlers + move effects in front of the player).
    public bool WorldBattlesEnabled { get; set; } = true;

    // Opt-in immersive mode: wild Pokémon spawn in the game world and battles are playable
    // from an on-screen hotbar (keys 1–8) without opening the phone. Off by default.
    public bool ImmersiveModeEnabled { get; set; }

    // Where the user parked the immersive hotbar (screen px; negative = default bottom-center)
    // and whether they collapsed it to just its header strip.
    public float ImmersiveBarX { get; set; } = -1f;
    public float ImmersiveBarY { get; set; } = -1f;
    public float ImmersiveBarWidth { get; set; } = -1f;
    public float ImmersiveBarHeight { get; set; } = -1f;
    public bool ImmersiveBarCollapsed { get; set; }

    // Transient (not serialized).
    public MonsterInstance? Pending { get; set; }
    public bool InBattle { get; set; }
    public uint Territory { get; set; }

    // The player's live world position (sampled every frame by EncounterService), used to gate
    // Alpha challenges on standing near the lair. Null while no local player is loaded.
    public Vector3? PlayerPosition { get; set; }
    public Biome CurrentBiome { get; set; } = Biome.Grassland;
    public float StepProgress { get; set; }

    // Live weather in the player's current zone (sampled by EncounterService), carried into wild
    // and training battles. Gym battles ignore it and start clear.
    public BattleWeather ZoneWeather { get; set; }

    public bool HasAnyMonster => Party.Count > 0 || Box.Count > 0;

    // True once every owned creature has fainted — the trainer is "whited out" and must revive
    // at a town Marketboard before scanning or battling again.
    public bool AllMonstersFainted =>
        HasAnyMonster && Party.All(monster => monster.Fainted) && Box.All(monster => monster.Fainted);

    public bool InTown => Towns.IsTown(Territory);

    public int BadgeCount => Badges.Count;

    public bool HasBadge(int gymIndex) => Badges.Contains(gymIndex);

    // ---- Region Alphas ---------------------------------------------------------------
    // Per-alpha defeat history: how many times it has fallen and when it last fell (drives the
    // few-hour respawn window). First clear = the Region Trophy / Alpha Badge for that region.

    private sealed class AlphaRecord
    {
        public int Kills { get; set; }
        public DateTime? LastDefeatUtc { get; set; }
    }

    private readonly Dictionary<string, AlphaRecord> alphaRecords = new(StringComparer.Ordinal);

    public int AlphaKills(string alphaId) =>
        alphaRecords.TryGetValue(alphaId, out var record) ? record.Kills : 0;

    public bool AlphaFirstCleared(string alphaId) => AlphaKills(alphaId) > 0;

    public int AlphaTrophyCount => alphaRecords.Count(entry => entry.Value.Kills > 0);

    public bool IsAlphaAlive(string alphaId) => AlphaRespawnIn(alphaId) <= TimeSpan.Zero;

    // Time until the alpha reclaims its territory; zero or negative when it is alive right now.
    public TimeSpan AlphaRespawnIn(string alphaId)
    {
        if (!alphaRecords.TryGetValue(alphaId, out var record) || record.LastDefeatUtc is not { } fell)
        {
            return TimeSpan.Zero;
        }

        return fell + Alphas.RespawnTime - DateTime.UtcNow;
    }

    // Debug: clears every Alpha's respawn timer (keeping kill history) so they are all alive again.
    public void DebugRespawnAlphas()
    {
        foreach (var record in alphaRecords.Values)
        {
            record.LastDefeatUtc = null;
        }

        Save();
    }

    public void RecordAlphaDefeat(string alphaId)
    {
        if (!alphaRecords.TryGetValue(alphaId, out var record))
        {
            record = new AlphaRecord();
            alphaRecords[alphaId] = record;
        }

        record.Kills++;
        record.LastDefeatUtc = DateTime.UtcNow;
        Save();
    }

    // Per-tier chosen training level range (a sub-band the player picks inside the tier's bounds).
    private int[]? trainingMin;
    private int[]? trainingMax;

    private void EnsureTrainingRanges()
    {
        if (trainingMin is { } lo && lo.Length == Training.Tiers.Count && trainingMax is { Length: > 0 })
        {
            return;
        }

        trainingMin = new int[Training.Tiers.Count];
        trainingMax = new int[Training.Tiers.Count];
        for (var i = 0; i < Training.Tiers.Count; i++)
        {
            trainingMin[i] = Training.Tiers[i].MinLevel;
            trainingMax[i] = Training.Tiers[i].MaxLevel;
        }
    }

    public (int Min, int Max) TrainingRange(Training.Tier tier)
    {
        EnsureTrainingRanges();
        var i = tier.Index - 1;
        var min = Math.Clamp(trainingMin![i], tier.MinLevel, tier.MaxLevel);
        var max = Math.Clamp(trainingMax![i], tier.MinLevel, tier.MaxLevel);
        return (min, Math.Max(min, max));
    }

    public void SetTrainingRange(Training.Tier tier, int min, int max)
    {
        EnsureTrainingRanges();
        var i = tier.Index - 1;
        min = Math.Clamp(min, tier.MinLevel, tier.MaxLevel);
        max = Math.Clamp(Math.Max(min, max), tier.MinLevel, tier.MaxLevel);
        trainingMin![i] = min;
        trainingMax![i] = max;
        Save();
    }

    public void EarnBadge(int gymIndex)
    {
        if (Badges.Add(gymIndex))
        {
            Save();
        }
    }

    // The Pokécenter service: fully restores HP, PP and status for the whole roster (party + box).
    public void HealAllMonsters()
    {
        foreach (var monster in Party)
        {
            monster.FullHeal();
        }

        foreach (var monster in Box)
        {
            monster.FullHeal();
        }

        Save();
    }

    // Wipes all saved progress (party, box, bag, dex, badges, money, options) and deletes the save
    // file, returning the trainer to a brand-new game. The caller drops the app back to starter
    // selection. The state instance is reset in place so long-lived references (e.g. the encounter
    // service) keep pointing at it.
    public void DeleteSaveAndReset()
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Lillypad Go: failed to delete save file.");
        }

        Party.Clear();
        Box.Clear();
        Seen.Clear();
        Badges.Clear();
        OwnedTms.Clear();
        alphaRecords.Clear();
        Bag.Clear();
        trainingMin = null;
        trainingMax = null;

        Money = 0;
        TotalSteps = 0;
        BattlesWon = 0;
        Captures = 0;
        StarterChosen = false;
        BattleEffectScale = 1.25f;
        BattleSpeed = 1f;
        BattleLogEnabled = true;
        BackgroundTrackingEnabled = true;

        Pending = null;
        InBattle = false;
        StepProgress = 0f;
        ZoneWeather = default;

        SeedNewGame();
    }

    public void AddCaught(MonsterInstance monster)
    {
        Seen.Add(monster.Species.Id);
        if (Party.Count < PartyLimit)
        {
            Party.Add(monster);
        }
        else
        {
            Box.Add(monster);
        }

        Save();
    }

    public static LillypadGoState Load(string path)
    {
        var state = new LillypadGoState(path);
        try
        {
            if (File.Exists(path))
            {
                var dto = JsonSerializer.Deserialize<SaveDto>(File.ReadAllText(path));
                if (dto is not null)
                {
                    state.Apply(dto);
                }
            }
            else
            {
                state.SeedNewGame();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Lillypad Go: failed to load save; starting fresh.");
        }

        return state;
    }

    // Starting purse and supplies for a brand-new trainer (no save file yet).
    private void SeedNewGame()
    {
        Money = 500;
        Bag.Add(Items.PokeBall.Id, 5);
        Bag.Add(Items.Potion.Id, 3);
    }

    public void Save()
    {
        try
        {
            var dto = new SaveDto
            {
                Items = Bag.Counts.ToDictionary(entry => entry.Key, entry => entry.Value),
                Money = Money,
                StarterChosen = StarterChosen,
                TotalSteps = TotalSteps,
                BattlesWon = BattlesWon,
                Captures = Captures,
                BattleEffectScale = BattleEffectScale,
                BattleSpeed = BattleSpeed,
                BattleLogEnabled = BattleLogEnabled,
                BackgroundTrackingEnabled = BackgroundTrackingEnabled,
                FollowerEnabled = FollowerEnabled,
                WorldBattlesEnabled = WorldBattlesEnabled,
                ImmersiveModeEnabled = ImmersiveModeEnabled,
                ImmersiveBarX = ImmersiveBarX,
                ImmersiveBarY = ImmersiveBarY,
                ImmersiveBarWidth = ImmersiveBarWidth,
                ImmersiveBarHeight = ImmersiveBarHeight,
                ImmersiveBarCollapsed = ImmersiveBarCollapsed,
                Seen = Seen.ToArray(),
                Badges = Badges.ToArray(),
                OwnedTms = OwnedTms.ToArray(),
                Alphas = alphaRecords.ToDictionary(entry => entry.Key, entry => new AlphaDto
                {
                    Kills = entry.Value.Kills,
                    LastDefeatUnix = entry.Value.LastDefeatUtc is { } fell
                        ? new DateTimeOffset(fell).ToUnixTimeSeconds()
                        : 0,
                }),
                TrainingMin = trainingMin,
                TrainingMax = trainingMax,
                Party = Party.Select(ToDto).ToArray(),
                Box = Box.Select(ToDto).ToArray(),
            };
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Lillypad Go: failed to save.");
        }
    }

    private void Apply(SaveDto dto)
    {
        Money = Math.Max(0, dto.Money);
        if (dto.Items is { Count: > 0 })
        {
            Bag.Load(dto.Items);
        }
        else
        {
            // Migrate a pre-item-system save: the old "Aether Snare"/"Tonic" ints become
            // Poké Balls and Potions.
            Bag.Clear();
            Bag.Add(Items.PokeBall.Id, Math.Max(0, dto.Snares));
            Bag.Add(Items.Potion.Id, Math.Max(0, dto.Tonics));
        }

        StarterChosen = dto.StarterChosen;
        TotalSteps = dto.TotalSteps;
        BattlesWon = dto.BattlesWon;
        Captures = dto.Captures;
        BattleEffectScale = dto.BattleEffectScale <= 0f ? 1.25f : Math.Clamp(dto.BattleEffectScale, 0.75f, 2f);
        BattleSpeed = dto.BattleSpeed <= 0f ? 1f : Math.Clamp(dto.BattleSpeed, 0.5f, 2.5f);
        BattleLogEnabled = dto.BattleLogEnabled ?? true;
        BackgroundTrackingEnabled = dto.BackgroundTrackingEnabled ?? true;
        FollowerEnabled = dto.FollowerEnabled ?? true;
        WorldBattlesEnabled = dto.WorldBattlesEnabled ?? true;
        ImmersiveModeEnabled = dto.ImmersiveModeEnabled ?? false;
        ImmersiveBarX = dto.ImmersiveBarX ?? -1f;
        ImmersiveBarY = dto.ImmersiveBarY ?? -1f;
        ImmersiveBarWidth = dto.ImmersiveBarWidth ?? -1f;
        ImmersiveBarHeight = dto.ImmersiveBarHeight ?? -1f;
        ImmersiveBarCollapsed = dto.ImmersiveBarCollapsed ?? false;
        if (dto.Seen is not null)
        {
            foreach (var id in dto.Seen)
            {
                Seen.Add(id);
            }
        }

        Badges.Clear();
        if (dto.Badges is not null)
        {
            foreach (var badge in dto.Badges)
            {
                Badges.Add(badge);
            }
        }

        OwnedTms.Clear();
        if (dto.OwnedTms is not null)
        {
            foreach (var tm in dto.OwnedTms)
            {
                OwnedTms.Add(tm);
            }
        }

        alphaRecords.Clear();
        if (dto.Alphas is not null)
        {
            foreach (var (alphaId, record) in dto.Alphas)
            {
                alphaRecords[alphaId] = new AlphaRecord
                {
                    Kills = Math.Max(0, record.Kills),
                    LastDefeatUtc = record.LastDefeatUnix > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(record.LastDefeatUnix).UtcDateTime
                        : null,
                };
            }
        }

        if (dto.TrainingMin is { Length: > 0 } tmin && dto.TrainingMax is { Length: > 0 } tmax &&
            tmin.Length == Training.Tiers.Count && tmax.Length == Training.Tiers.Count)
        {
            trainingMin = tmin;
            trainingMax = tmax;
        }

        Party.Clear();
        Box.Clear();
        foreach (var m in dto.Party ?? Array.Empty<MonsterDto>())
        {
            if (FromDto(m) is { } instance)
            {
                Seen.Add(instance.Species.Id);
                if (Party.Count < PartyLimit)
                {
                    Party.Add(instance);
                }
                else
                {
                    Box.Add(instance);
                }
            }
        }

        foreach (var m in dto.Box ?? Array.Empty<MonsterDto>())
        {
            if (FromDto(m) is { } instance)
            {
                Seen.Add(instance.Species.Id);
                Box.Add(instance);
            }
        }

        StarterChosen |= HasAnyMonster;
    }

    private static MonsterDto ToDto(MonsterInstance m) => new()
    {
        Species = m.Species.Id,
        Level = m.Level,
        Xp = m.Xp,
        Hp = m.CurrentHp,
        Status = (byte)m.Status,
        Nickname = m.Nickname,
        Favorite = m.IsFavorite,
        Battles = m.Battles,
        Victories = m.Victories,
        DamageDealt = m.DamageDealt,
        Moves = m.Moves.Select(move => move.Name).ToArray(),
        Pp = m.Pp.ToArray(),
        Ivs = m.Ivs.ToArray(),
        Evs = m.Evs.ToArray(),
        Ability = m.Ability,
        HeldItem = m.HeldItem,
        Gender = (byte)m.Gender,
    };

    private static MonsterInstance? FromDto(MonsterDto dto)
    {
        var species = Dex.Find(dto.Species);
        if (species is null)
        {
            return null;
        }

        var instance = new MonsterInstance(species, dto.Level);
        // Restore genetics before HP so stats (and MaxHp) are correct when the saved HP is applied.
        instance.RestoreGenetics(dto.Ivs, dto.Evs, dto.Ability,
            dto.Gender is { } g ? (Gender)g : null);

        var moves = new List<MoveDef>();
        foreach (var name in dto.Moves ?? Array.Empty<string>())
        {
            if (Moves.Find(name) is { } move)
            {
                moves.Add(move);
            }
        }

        instance.Restore(dto.Xp, dto.Hp, (Status)dto.Status, moves, dto.Pp ?? Array.Empty<int>());
        instance.RestoreProfile(dto.Nickname, dto.Favorite, dto.Battles, dto.Victories, dto.DamageDealt);
        instance.HeldItem = Items.Find(dto.HeldItem ?? string.Empty) is { Category: ItemCategory.HeldItem }
            ? dto.HeldItem! : string.Empty;
        return instance;
    }

    private sealed class SaveDto
    {
        // Legacy fields, still read to migrate old saves; no longer written.
        public int Snares { get; set; }
        public int Tonics { get; set; }
        public Dictionary<string, int>? Items { get; set; }
        public int Money { get; set; }
        public bool StarterChosen { get; set; }
        public int TotalSteps { get; set; }
        public int BattlesWon { get; set; }
        public int Captures { get; set; }
        public float BattleEffectScale { get; set; }
        public float BattleSpeed { get; set; }
        public bool? BattleLogEnabled { get; set; }
        public bool? BackgroundTrackingEnabled { get; set; }
        public bool? FollowerEnabled { get; set; }
        public bool? WorldBattlesEnabled { get; set; }
        public bool? ImmersiveModeEnabled { get; set; }
        public float? ImmersiveBarX { get; set; }
        public float? ImmersiveBarY { get; set; }
        public float? ImmersiveBarWidth { get; set; }
        public float? ImmersiveBarHeight { get; set; }
        public bool? ImmersiveBarCollapsed { get; set; }
        public string[]? Seen { get; set; }
        public int[]? Badges { get; set; }
        public string[]? OwnedTms { get; set; }
        public Dictionary<string, AlphaDto>? Alphas { get; set; }
        public int[]? TrainingMin { get; set; }
        public int[]? TrainingMax { get; set; }
        public MonsterDto[]? Party { get; set; }
        public MonsterDto[]? Box { get; set; }
    }

    private sealed class AlphaDto
    {
        public int Kills { get; set; }
        public long LastDefeatUnix { get; set; }
    }

    private sealed class MonsterDto
    {
        public string Species { get; set; } = string.Empty;
        public int Level { get; set; }
        public int Xp { get; set; }
        public int Hp { get; set; }
        public byte Status { get; set; }
        public string? Nickname { get; set; }
        public bool Favorite { get; set; }
        public int Battles { get; set; }
        public int Victories { get; set; }
        public int DamageDealt { get; set; }
        public string[]? Moves { get; set; }
        public int[]? Pp { get; set; }
        public int[]? Ivs { get; set; }
        public int[]? Evs { get; set; }
        public string? Ability { get; set; }
        public string? HeldItem { get; set; }
        public byte? Gender { get; set; }
    }
}
