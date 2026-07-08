namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// A purchasable Generation-IX TM: a catalogue number, the move it teaches, and its shop price.
internal sealed record TmDef(int Number, string MoveId, MoveDef Move, int Price);

// The TM catalogue, built over the generated TmCatalog.MoveIds (every Gen-IX TM move at least one of
// the 151 Kanto species can learn). TMs are numbered by catalogue position and priced from the move's
// power; status TMs use a flat price. Which species may actually learn a TM is enforced at teach time
// via MonsterSpecies.CanLearnTm, straight from Showdown's learnset data.
internal static class Tms
{
    public static readonly IReadOnlyList<TmDef> All = Build();

    private static readonly Dictionary<string, TmDef> ById =
        All.ToDictionary(tm => tm.MoveId, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<TmDef> Build()
    {
        var list = new List<TmDef>();
        var number = 1;
        foreach (var id in TmCatalog.MoveIds)
        {
            if (Moves.Find(id) is not { } move)
            {
                continue;
            }

            list.Add(new TmDef(number++, id, move, Price(move)));
        }

        return list;
    }

    private static int Price(MoveDef move) =>
        move.IsStatus ? 3000 : Math.Clamp(1000 + move.Power * 15, 1200, 6000);

    public static TmDef? Find(string moveId) => ById.TryGetValue(moveId, out var tm) ? tm : null;

    public static int NumberOf(string moveId) => Find(moveId)?.Number ?? 0;

    public static string Label(int number) => $"TM{number:D3}";
}
