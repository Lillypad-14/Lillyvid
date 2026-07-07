namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// The Training system: selectable tiers of randomized trainer battles used to grind XP at a
// level band close to the matching gym. Tier N unlocks once you hold N-1 badges (Tier 1 is
// always open), so each gym win opens the next, harder training band.
internal static class Training
{
    internal sealed record Tier(int Index, string Name, int MinLevel, int MaxLevel, int MaxTeam);

    public static readonly IReadOnlyList<Tier> Tiers = new[]
    {
        new Tier(1, "Novice", 8, 14, 2),
        new Tier(2, "Apprentice", 14, 20, 3),
        new Tier(3, "Adept", 20, 28, 4),
        new Tier(4, "Expert", 28, 36, 5),
        new Tier(5, "Elite", 36, 44, 6),
        new Tier(6, "Champion", 44, 52, 6),
    };

    private static readonly string[] Titles =
    {
        "Youngster", "Lass", "Bug Catcher", "Hiker", "Fisher", "Camper", "Picnicker", "Beauty",
        "Rival", "Ace Trainer", "Black Belt", "Veteran", "Cooltrainer",
    };

    // A tier is unlocked once the trainer holds this many badges.
    public static int RequiredBadges(Tier tier) => tier.Index - 1;

    public static bool IsUnlocked(Tier tier, int badgeCount) => badgeCount >= RequiredBadges(tier);

    public static Battle Build(List<MonsterInstance> party, Tier tier, Bag bag, Random rng)
    {
        var species = Dex.All.ToList();
        var size = rng.Next(1, tier.MaxTeam + 1);
        var team = new List<MonsterInstance>(size);
        for (var i = 0; i < size; i++)
        {
            var pick = species[rng.Next(species.Count)];
            var level = rng.Next(tier.MinLevel, tier.MaxLevel + 1);
            team.Add(new MonsterInstance(pick, level));
        }

        var name = Titles[rng.Next(Titles.Length)];
        var prize = team.Sum(monster => monster.Level) * 10 + tier.Index * 30;
        return new Battle(party, team, name, prize, bag, rng);
    }
}
