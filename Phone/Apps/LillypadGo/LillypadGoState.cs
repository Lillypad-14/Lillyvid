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
    public bool StarterChosen { get; set; }
    public int TotalSteps { get; set; }
    public int BattlesWon { get; set; }
    public int Captures { get; set; }
    public float BattleEffectScale { get; set; } = 1.25f;
    public bool BackgroundTrackingEnabled { get; set; } = true;

    // Transient (not serialized).
    public MonsterInstance? Pending { get; set; }
    public bool InBattle { get; set; }
    public uint Territory { get; set; }
    public Biome CurrentBiome { get; set; } = Biome.Grassland;
    public float StepProgress { get; set; }

    public bool HasAnyMonster => Party.Count > 0 || Box.Count > 0;

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
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Lillypad Go: failed to load save; starting fresh.");
        }

        return state;
    }

    public void Save()
    {
        try
        {
            var dto = new SaveDto
            {
                Snares = Bag.Snares,
                Tonics = Bag.Tonics,
                StarterChosen = StarterChosen,
                TotalSteps = TotalSteps,
                BattlesWon = BattlesWon,
                Captures = Captures,
                BattleEffectScale = BattleEffectScale,
                BackgroundTrackingEnabled = BackgroundTrackingEnabled,
                Seen = Seen.ToArray(),
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
        Bag.Snares = Math.Max(0, dto.Snares);
        Bag.Tonics = Math.Max(0, dto.Tonics);
        StarterChosen = dto.StarterChosen;
        TotalSteps = dto.TotalSteps;
        BattlesWon = dto.BattlesWon;
        Captures = dto.Captures;
        BattleEffectScale = dto.BattleEffectScale <= 0f ? 1.25f : Math.Clamp(dto.BattleEffectScale, 0.75f, 2f);
        BackgroundTrackingEnabled = dto.BackgroundTrackingEnabled ?? true;
        if (dto.Seen is not null)
        {
            foreach (var id in dto.Seen)
            {
                Seen.Add(id);
            }
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
    };

    private static MonsterInstance? FromDto(MonsterDto dto)
    {
        var species = Dex.Find(dto.Species);
        if (species is null)
        {
            return null;
        }

        var instance = new MonsterInstance(species, dto.Level);
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
        return instance;
    }

    private sealed class SaveDto
    {
        public int Snares { get; set; }
        public int Tonics { get; set; }
        public bool StarterChosen { get; set; }
        public int TotalSteps { get; set; }
        public int BattlesWon { get; set; }
        public int Captures { get; set; }
        public float BattleEffectScale { get; set; }
        public bool? BackgroundTrackingEnabled { get; set; }
        public string[]? Seen { get; set; }
        public MonsterDto[]? Party { get; set; }
        public MonsterDto[]? Box { get; set; }
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
    }
}
