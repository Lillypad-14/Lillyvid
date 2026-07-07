namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// The trainer's inventory: item id -> quantity. Item behaviour and metadata live in the Items
// catalogue; the bag just tracks how many of each you hold.
internal sealed class Bag
{
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, int> Counts => counts;

    public int Count(string itemId) => counts.TryGetValue(itemId, out var quantity) ? quantity : 0;

    public bool Has(string itemId) => Count(itemId) > 0;

    public void Add(string itemId, int amount = 1)
    {
        if (amount <= 0)
        {
            return;
        }

        counts[itemId] = Count(itemId) + amount;
    }

    public bool Consume(string itemId, int amount = 1)
    {
        var held = Count(itemId);
        if (held < amount)
        {
            return false;
        }

        var remaining = held - amount;
        if (remaining <= 0)
        {
            counts.Remove(itemId);
        }
        else
        {
            counts[itemId] = remaining;
        }

        return true;
    }

    public int TotalOf(ItemCategory category) =>
        Items.All.Where(item => item.Category == category).Sum(item => Count(item.Id));

    // The owned items, in the catalogue's display order.
    public IEnumerable<(ItemDef Def, int Count)> Contents() =>
        Items.All.Select(item => (item, Count(item.Id))).Where(entry => entry.Item2 > 0);

    public void Clear() => counts.Clear();

    public void Load(IReadOnlyDictionary<string, int>? saved)
    {
        counts.Clear();
        if (saved is null)
        {
            return;
        }

        foreach (var (id, quantity) in saved)
        {
            if (quantity > 0 && Items.Find(id) is not null)
            {
                counts[id] = quantity;
            }
        }
    }
}
