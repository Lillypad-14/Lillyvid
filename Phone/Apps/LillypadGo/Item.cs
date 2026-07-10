using Dalamud.Interface;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

internal enum ItemCategory : byte
{
    Ball,
    Potion,
    Revive,
    StatusHeal,
    HeldItem,
    EvolutionStone,
}

// The Bag/Marketboard filter pockets (per Ideas/UI Update/Bag.png). Items is the whole non-TM
// catalogue; the others are narrower pockets. Berries and Held Items split the HeldItem
// category (a berry is a held item, but it gets its own tab).
internal enum ItemTab : byte
{
    Items,
    Medicine,
    Balls,
    Berries,
    Held,
    Tms,
}

// A purchasable/usable item. Balls capture wild Pokémon; potions restore HP; revives bring back
// fainted Pokémon; status heals cure a single ailment (or all, for Full Heal). Definitions are
// immutable data — behaviour lives in Battle/Bag, which read these fields.
internal sealed class ItemDef
{
    public ItemDef(string id, string name, string blurb, string description, int price, ItemCategory category,
        FontAwesomeIcon icon, int healAmount = 0, float catchBonus = 0f, Status curesStatus = Status.None,
        bool curesAllStatus = false, bool revivesToFull = false)
    {
        Id = id;
        Name = name;
        Blurb = blurb;
        Description = description;
        Price = price;
        Category = category;
        Icon = icon;
        HealAmount = healAmount;
        CatchBonus = catchBonus;
        CuresStatus = curesStatus;
        CuresAllStatus = curesAllStatus;
        RevivesToFull = revivesToFull;
    }

    // A heal amount at or above this value restores the target to full HP.
    public const int FullRestore = 9999;

    public string Id { get; }
    public string Name { get; }

    // A short one-line label that fits an inventory/shop row; Description is the full tooltip text.
    public string Blurb { get; }
    public string Description { get; }
    public int Price { get; }
    public ItemCategory Category { get; }
    public FontAwesomeIcon Icon { get; }
    public int HealAmount { get; }
    public float CatchBonus { get; }
    public Status CuresStatus { get; }
    public bool CuresAllStatus { get; }
    public bool RevivesToFull { get; }

    public bool RestoresFullHp => HealAmount >= FullRestore;
}

// The full item catalogue plus the shop's stock order. Names, prices and effects mirror the
// mainline Pokémon games so the bag reads like the real thing.
internal static class Items
{
    public static readonly ItemDef PokeBall = new(
        "pokeball", "Poké Ball", "Catches wild Pokémon.",
        "A device for catching wild Pokémon. It is thrown like a ball at a target.",
        200, ItemCategory.Ball, FontAwesomeIcon.Bullseye, catchBonus: 1.0f);

    public static readonly ItemDef GreatBall = new(
        "greatball", "Great Ball", "Better catch rate.",
        "A high-performance Ball with a higher catch rate than a standard Poké Ball.",
        600, ItemCategory.Ball, FontAwesomeIcon.Bullseye, catchBonus: 1.5f);

    public static readonly ItemDef UltraBall = new(
        "ultraball", "Ultra Ball", "Even better catch rate.",
        "An ultra-performance Ball with a higher catch rate than a Great Ball.",
        800, ItemCategory.Ball, FontAwesomeIcon.Bullseye, catchBonus: 2.0f);

    public static readonly ItemDef Potion = new(
        "potion", "Potion", "Restores 20 HP.", "Restores 20 HP to a single Pokémon.",
        200, ItemCategory.Potion, FontAwesomeIcon.Flask, healAmount: 20);

    public static readonly ItemDef SuperPotion = new(
        "superpotion", "Super Potion", "Restores 60 HP.", "Restores 60 HP to a single Pokémon.",
        700, ItemCategory.Potion, FontAwesomeIcon.Flask, healAmount: 60);

    public static readonly ItemDef HyperPotion = new(
        "hyperpotion", "Hyper Potion", "Restores 120 HP.", "Restores 120 HP to a single Pokémon.",
        1200, ItemCategory.Potion, FontAwesomeIcon.Flask, healAmount: 120);

    public static readonly ItemDef Revive = new(
        "revive", "Revive", "Revives to half HP.",
        "Revives a fainted Pokémon and restores half of its maximum HP.",
        1500, ItemCategory.Revive, FontAwesomeIcon.Heart);

    public static readonly ItemDef Antidote = new(
        "antidote", "Antidote", "Cures poison.", "Cures a single poisoned Pokémon.",
        100, ItemCategory.StatusHeal, FontAwesomeIcon.Pills, curesStatus: Status.Poison);

    public static readonly ItemDef ParalyzeHeal = new(
        "parlyzheal", "Paralyze Heal", "Cures paralysis.", "Cures a single paralyzed Pokémon.",
        200, ItemCategory.StatusHeal, FontAwesomeIcon.Pills, curesStatus: Status.Paralysis);

    public static readonly ItemDef BurnHeal = new(
        "burnheal", "Burn Heal", "Cures a burn.", "Cures a single burned Pokémon.",
        250, ItemCategory.StatusHeal, FontAwesomeIcon.Pills, curesStatus: Status.Burn);

    public static readonly ItemDef IceHeal = new(
        "iceheal", "Ice Heal", "Thaws a freeze.", "Thaws out a single frozen Pokémon.",
        250, ItemCategory.StatusHeal, FontAwesomeIcon.Pills, curesStatus: Status.Freeze);

    public static readonly ItemDef FullHeal = new(
        "fullheal", "Full Heal", "Cures any status.", "Cures any status condition on a single Pokémon.",
        600, ItemCategory.StatusHeal, FontAwesomeIcon.Star, curesAllStatus: true);

    public static readonly ItemDef FireStone = Stone("firestone", "Fire Stone", "Fire Stone");
    public static readonly ItemDef WaterStone = Stone("waterstone", "Water Stone", "Water Stone");
    public static readonly ItemDef ThunderStone = Stone("thunderstone", "Thunder Stone", "Thunder Stone");
    public static readonly ItemDef LeafStone = Stone("leafstone", "Leaf Stone", "Leaf Stone");
    public static readonly ItemDef MoonStone = Stone("moonstone", "Moon Stone", "Moon Stone");

    // Everything that exists, in a stable display order (also the shop's stock order). The held-item
    // and Berry pockets come from HeldItems, which owns both their presentation and their battle data.
    public static readonly IReadOnlyList<ItemDef> All = new[]
        {
            PokeBall, GreatBall, UltraBall,
            Potion, SuperPotion, HyperPotion,
            Revive,
            Antidote, ParalyzeHeal, BurnHeal, IceHeal, FullHeal,
        }
        .Concat(HeldItems.All)
        .Append(FireStone).Append(WaterStone).Append(ThunderStone).Append(LeafStone).Append(MoonStone)
        .ToList();

    private static readonly Dictionary<string, ItemDef> ById =
        All.ToDictionary(item => item.Id, StringComparer.Ordinal);

    public static ItemDef? Find(string id) => ById.TryGetValue(id, out var item) ? item : null;

    public static bool IsBerry(string itemId) => HeldItems.IsBerry(itemId);

    // Whether an item belongs in a filter pocket. TMs are not ItemDefs, so the Tms tab matches
    // nothing here — screens special-case it to their TM lists.
    public static bool InTab(ItemDef item, ItemTab tab) => tab switch
    {
        ItemTab.Items => true,
        ItemTab.Medicine => item.Category is ItemCategory.Potion or ItemCategory.Revive or ItemCategory.StatusHeal,
        ItemTab.Balls => item.Category == ItemCategory.Ball,
        ItemTab.Berries => item.Category == ItemCategory.HeldItem && IsBerry(item.Id),
        ItemTab.Held => item.Category == ItemCategory.HeldItem && !IsBerry(item.Id),
        _ => false,
    };

    public static ItemDef? StoneFor(string? evolutionMethod) => evolutionMethod switch
    {
        "Fire Stone" => FireStone,
        "Water Stone" => WaterStone,
        "Thunder Stone" => ThunderStone,
        "Leaf Stone" => LeafStone,
        "Moon Stone" => MoonStone,
        _ => null,
    };

    private static ItemDef Stone(string id, string name, string method) => new(
        id, name, $"Evolves a compatible PokÃ©mon.",
        $"A special stone used to evolve PokÃ©mon that respond to a {method}.",
        2100, ItemCategory.EvolutionStone, FontAwesomeIcon.Star);
}
