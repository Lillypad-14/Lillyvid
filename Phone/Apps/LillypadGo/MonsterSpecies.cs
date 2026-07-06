using System.Numerics;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal enum Archetype : byte
{
    Quadruped,
    Serpent,
    Avian,
    Blob,
    Wisp,
    Insectoid,
}

// Data-driven look for the procedural renderer. Used as a fallback silhouette while a sprite
// texture is still loading (or missing).
internal readonly struct ArtSpec
{
    public ArtSpec(Archetype shape, Vector4 primary, Vector4 secondary, Vector4 belly, Vector4 eye,
        bool horns = false, bool wings = false, bool tail = false, bool spikes = false, bool fins = false,
        int eyes = 2)
    {
        Shape = shape;
        Primary = primary;
        Secondary = secondary;
        Belly = belly;
        Eye = eye;
        Horns = horns;
        Wings = wings;
        Tail = tail;
        Spikes = spikes;
        Fins = fins;
        Eyes = eyes;
    }

    public Archetype Shape { get; }
    public Vector4 Primary { get; }
    public Vector4 Secondary { get; }
    public Vector4 Belly { get; }
    public Vector4 Eye { get; }
    public bool Horns { get; }
    public bool Wings { get; }
    public bool Tail { get; }
    public bool Spikes { get; }
    public bool Fins { get; }
    public int Eyes { get; }
}

internal sealed class MonsterSpecies
{
    public MonsterSpecies(string id, string name, Element element, ArtSpec art, int baseHp, int baseAtk, int baseDef,
        int baseSpd, int catchRate, (int Level, MoveDef Move)[] learnset, Element? secondaryElement = null,
        int? baseSpAtk = null, int? baseSpDef = null, int dexNumber = 0)
    {
        Id = id;
        Name = name;
        Element = element;
        Art = art;
        BaseHp = baseHp;
        BaseAtk = baseAtk;
        BaseDef = baseDef;
        BaseSpAtk = baseSpAtk ?? baseAtk;
        BaseSpDef = baseSpDef ?? baseDef;
        BaseSpd = baseSpd;
        CatchRate = catchRate;
        Learnset = learnset;
        SecondaryElement = secondaryElement == element ? null : secondaryElement;
        DexNumber = dexNumber;
    }

    public string Id { get; }
    public string Name { get; }
    public int DexNumber { get; }
    public Element Element { get; }
    public Element? SecondaryElement { get; }
    public ArtSpec Art { get; }
    public int BaseHp { get; }
    public int BaseAtk { get; }
    public int BaseDef { get; }
    public int BaseSpAtk { get; }
    public int BaseSpDef { get; }
    public int BaseSpd { get; }
    public int CatchRate { get; }
    public (int Level, MoveDef Move)[] Learnset { get; }

    public bool HasType(Element type) => Element == type || SecondaryElement == type;
}

// The 151 Kanto species are registered from the auto-generated PokedexData.g.cs (Populate).
internal static partial class Dex
{
    private static readonly Dictionary<string, MonsterSpecies> ById = new();

    static Dex() => Populate();

    static partial void Populate();

    private static MonsterSpecies Register(MonsterSpecies species)
    {
        ById[species.Id] = species;
        return species;
    }

    // Resolves (level, move-id) pairs from the generated tables into concrete MoveDefs.
    private static (int Level, MoveDef Move)[] LS((int lvl, string id)[] entries)
    {
        var list = new (int, MoveDef)[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            list[i] = (entries[i].lvl, Moves.M(entries[i].id));
        }

        return list;
    }

    private static void Add(string id, string name, Element type, Element? type2, int hp, int atk, int def,
        int spAtk, int spDef, int spd, int catchRate, int dexNumber, ArtSpec art, (int lvl, string id)[] learnset)
        => Register(new MonsterSpecies(id, name, type, art, hp, atk, def, spd, catchRate, LS(learnset), type2,
            spAtk, spDef, dexNumber));

    public static MonsterSpecies? Find(string id) => ById.TryGetValue(id, out var species) ? species : null;

    public static IReadOnlyCollection<MonsterSpecies> All => ById.Values;
}
