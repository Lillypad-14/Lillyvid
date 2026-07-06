using System.Numerics;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// Modern mainline type roster and matchup chart (Generation VI onward).
internal enum Element : byte
{
    Normal,
    Fire,
    Water,
    Electric,
    Grass,
    Ice,
    Fighting,
    Poison,
    Ground,
    Flying,
    Psychic,
    Bug,
    Rock,
    Ghost,
    Dragon,
    Dark,
    Steel,
    Fairy,
}

internal static class Elements
{
    public static readonly Element[] All = Enum.GetValues<Element>();

    public static float Effectiveness(Element attack, Element defense) => (attack, defense) switch
    {
        (Element.Normal, Element.Rock or Element.Steel) => 0.5f,
        (Element.Normal, Element.Ghost) => 0f,

        (Element.Fire, Element.Grass or Element.Ice or Element.Bug or Element.Steel) => 2f,
        (Element.Fire, Element.Fire or Element.Water or Element.Rock or Element.Dragon) => 0.5f,

        (Element.Water, Element.Fire or Element.Ground or Element.Rock) => 2f,
        (Element.Water, Element.Water or Element.Grass or Element.Dragon) => 0.5f,

        (Element.Electric, Element.Water or Element.Flying) => 2f,
        (Element.Electric, Element.Electric or Element.Grass or Element.Dragon) => 0.5f,
        (Element.Electric, Element.Ground) => 0f,

        (Element.Grass, Element.Water or Element.Ground or Element.Rock) => 2f,
        (Element.Grass, Element.Fire or Element.Grass or Element.Poison or Element.Flying or Element.Bug or
            Element.Dragon or Element.Steel) => 0.5f,

        (Element.Ice, Element.Grass or Element.Ground or Element.Flying or Element.Dragon) => 2f,
        (Element.Ice, Element.Fire or Element.Water or Element.Ice or Element.Steel) => 0.5f,

        (Element.Fighting, Element.Normal or Element.Ice or Element.Rock or Element.Dark or Element.Steel) => 2f,
        (Element.Fighting, Element.Poison or Element.Flying or Element.Psychic or Element.Bug or Element.Fairy) =>
            0.5f,
        (Element.Fighting, Element.Ghost) => 0f,

        (Element.Poison, Element.Grass or Element.Fairy) => 2f,
        (Element.Poison, Element.Poison or Element.Ground or Element.Rock or Element.Ghost) => 0.5f,
        (Element.Poison, Element.Steel) => 0f,

        (Element.Ground, Element.Fire or Element.Electric or Element.Poison or Element.Rock or Element.Steel) => 2f,
        (Element.Ground, Element.Grass or Element.Bug) => 0.5f,
        (Element.Ground, Element.Flying) => 0f,

        (Element.Flying, Element.Grass or Element.Fighting or Element.Bug) => 2f,
        (Element.Flying, Element.Electric or Element.Rock or Element.Steel) => 0.5f,

        (Element.Psychic, Element.Fighting or Element.Poison) => 2f,
        (Element.Psychic, Element.Psychic or Element.Steel) => 0.5f,
        (Element.Psychic, Element.Dark) => 0f,

        (Element.Bug, Element.Grass or Element.Psychic or Element.Dark) => 2f,
        (Element.Bug, Element.Fire or Element.Fighting or Element.Poison or Element.Flying or Element.Ghost or
            Element.Steel or Element.Fairy) => 0.5f,

        (Element.Rock, Element.Fire or Element.Ice or Element.Flying or Element.Bug) => 2f,
        (Element.Rock, Element.Fighting or Element.Ground or Element.Steel) => 0.5f,

        (Element.Ghost, Element.Psychic or Element.Ghost) => 2f,
        (Element.Ghost, Element.Dark) => 0.5f,
        (Element.Ghost, Element.Normal) => 0f,

        (Element.Dragon, Element.Dragon) => 2f,
        (Element.Dragon, Element.Steel) => 0.5f,
        (Element.Dragon, Element.Fairy) => 0f,

        (Element.Dark, Element.Psychic or Element.Ghost) => 2f,
        (Element.Dark, Element.Fighting or Element.Dark or Element.Fairy) => 0.5f,

        (Element.Steel, Element.Ice or Element.Rock or Element.Fairy) => 2f,
        (Element.Steel, Element.Fire or Element.Water or Element.Electric or Element.Steel) => 0.5f,

        (Element.Fairy, Element.Fighting or Element.Dragon or Element.Dark) => 2f,
        (Element.Fairy, Element.Fire or Element.Poison or Element.Steel) => 0.5f,
        _ => 1f,
    };

    public static float Effectiveness(Element attack, Element primaryDefense, Element? secondaryDefense)
    {
        var multiplier = Effectiveness(attack, primaryDefense);
        return secondaryDefense.HasValue ? multiplier * Effectiveness(attack, secondaryDefense.Value) : multiplier;
    }

    public static string Name(Element element) => element.ToString();

    public static string Format(Element primary, Element? secondary) =>
        secondary.HasValue ? $"{Name(primary)} / {Name(secondary.Value)}" : Name(primary);

    public static Vector4 Color(Element element) => element switch
    {
        Element.Normal => new Vector4(0.66f, 0.66f, 0.56f, 1f),
        Element.Fire => new Vector4(0.94f, 0.32f, 0.18f, 1f),
        Element.Water => new Vector4(0.27f, 0.50f, 0.93f, 1f),
        Element.Electric => new Vector4(0.94f, 0.76f, 0.20f, 1f),
        Element.Grass => new Vector4(0.39f, 0.72f, 0.28f, 1f),
        Element.Ice => new Vector4(0.48f, 0.80f, 0.82f, 1f),
        Element.Fighting => new Vector4(0.72f, 0.20f, 0.16f, 1f),
        Element.Poison => new Vector4(0.62f, 0.25f, 0.66f, 1f),
        Element.Ground => new Vector4(0.82f, 0.66f, 0.34f, 1f),
        Element.Flying => new Vector4(0.58f, 0.51f, 0.88f, 1f),
        Element.Psychic => new Vector4(0.93f, 0.29f, 0.54f, 1f),
        Element.Bug => new Vector4(0.57f, 0.66f, 0.12f, 1f),
        Element.Rock => new Vector4(0.68f, 0.58f, 0.25f, 1f),
        Element.Ghost => new Vector4(0.40f, 0.33f, 0.60f, 1f),
        Element.Dragon => new Vector4(0.38f, 0.16f, 0.90f, 1f),
        Element.Dark => new Vector4(0.38f, 0.30f, 0.27f, 1f),
        Element.Steel => new Vector4(0.58f, 0.58f, 0.68f, 1f),
        Element.Fairy => new Vector4(0.89f, 0.52f, 0.72f, 1f),
        _ => new Vector4(0.7f, 0.7f, 0.7f, 1f),
    };
}
