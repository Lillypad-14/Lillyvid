namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// Player inventory. "Aether Snare" is the original capture device; "Tonic" heals.
internal sealed class Bag
{
    public int Snares { get; set; } = 5;
    public int Tonics { get; set; } = 3;

    public const int TonicHeal = 30;
    public const float SnareBonus = 1.4f;
}
