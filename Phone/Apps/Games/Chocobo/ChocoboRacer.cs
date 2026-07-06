using System.Numerics;

namespace VideoSyncPrototype.Phone.Apps.Games.Chocobo;

// One racer on the card. Colours + name are cosmetic; Strength is the hidden form that
// drives both the displayed odds and the race, Pos/Speed/Stride are live race state.
internal sealed class ChocoboRacer
{
    public ChocoboRacer(string name, Vector4 body, Vector4 jockey)
    {
        Name = name;
        Body = body;
        Jockey = jockey;
    }

    public string Name { get; }
    public Vector4 Body { get; }
    public Vector4 Jockey { get; }

    public float Odds { get; set; }
    public float Strength { get; set; }
    public float Pos { get; set; }
    public float Speed { get; set; }
    public float Stride { get; set; }
    public int Rank { get; set; }
    public bool Finished { get; set; }
}
