using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoSyncPrototype.Fun;

internal enum UwuIntensity
{
    Soft,
    Normal,
    Chaos,
}

internal static partial class UwuTranslator
{
    private static readonly (string Source, string Replacement)[] SoftReplacements =
    [
        ("please", "pwease"),
        ("sorry", "sowwy"),
        ("hello", "hewwo"),
        ("really", "weawwy"),
        ("pretty", "pwetty"),
        ("cute", "cutesy"),
        ("thanks", "tankies"),
    ];

    private static readonly (string Source, string Replacement)[] NormalReplacements =
    [
        ("everyone", "evewyone"),
        ("friend", "fwiend"),
        ("friends", "fwiends"),
        ("love", "wuv"),
        ("little", "wittwe"),
        ("goodbye", "buh-bye"),
        ("thank you", "tank chu"),
        ("no", "nu"),
        ("yes", "yesh"),
    ];

    private static readonly (string Source, string Replacement)[] ChaosReplacements =
    [
        ("limit break", "wimit bweak"),
        ("tank", "tanky wanky"),
        ("healer", "healy whealy"),
        ("stack", "stacc"),
        ("boss", "bossy wossy"),
    ];

    private static readonly string[] NormalSuffixes = ["uwu", "owo", ">w<", ":3", "nya~"];
    private static readonly string[] ChaosPunctuation = ["!!", "?!", "~~~", "!!~"];

    public static string Translate(string source, UwuIntensity intensity)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        var random = new Random(GetStableSeed(source, intensity));
        var output = new StringBuilder(source.Length + 24);
        var lastIndex = 0;
        var mayStutter = intensity != UwuIntensity.Soft &&
                         random.NextDouble() < (intensity == UwuIntensity.Chaos ? 0.55 : 0.25);

        foreach (Match match in ProtectedTextRegex().Matches(source))
        {
            AppendTranslated(output, source[lastIndex..match.Index], intensity, random, ref mayStutter);
            output.Append(match.Value);
            lastIndex = match.Index + match.Length;
        }

        AppendTranslated(output, source[lastIndex..], intensity, random, ref mayStutter);

        var result = output.ToString().Trim();
        if (result.Length == 0)
        {
            return result;
        }

        var suffixChance = intensity switch
        {
            UwuIntensity.Soft => 0.10,
            UwuIntensity.Normal => 0.45,
            _ => 0.86,
        };

        if (random.NextDouble() < suffixChance)
        {
            result += " " + NormalSuffixes[random.Next(NormalSuffixes.Length)];
        }

        if (intensity == UwuIntensity.Chaos)
        {
            result += ChaosPunctuation[random.Next(ChaosPunctuation.Length)];
            if (random.NextDouble() < 0.38)
            {
                result += " " + NormalSuffixes[random.Next(NormalSuffixes.Length)];
            }
        }

        return result;
    }

    private static void AppendTranslated(
        StringBuilder output,
        string text,
        UwuIntensity intensity,
        Random random,
        ref bool mayStutter)
    {
        if (text.Length == 0)
        {
            return;
        }

        var translated = ReplaceWords(text, SoftReplacements);
        if (intensity != UwuIntensity.Soft)
        {
            translated = ReplaceWords(translated, NormalReplacements);
        }

        if (intensity == UwuIntensity.Chaos)
        {
            translated = ReplaceWords(translated, ChaosReplacements);
        }

        var characters = translated.ToCharArray();
        for (var i = 0; i < characters.Length; i++)
        {
            var ch = characters[i];
            if (ch is not ('r' or 'R' or 'l' or 'L'))
            {
                continue;
            }

            if (intensity == UwuIntensity.Soft && random.NextDouble() >= 0.34)
            {
                continue;
            }

            characters[i] = char.IsUpper(ch) ? 'W' : 'w';
        }

        translated = new string(characters);
        if (mayStutter)
        {
            var firstWord = FirstWordRegex().Match(translated);
            if (firstWord.Success)
            {
                var first = firstWord.Value[0];
                translated = translated.Insert(firstWord.Index, $"{first}-");
                mayStutter = false;
            }
        }

        output.Append(translated);
    }

    private static string ReplaceWords(
        string input,
        IReadOnlyList<(string Source, string Replacement)> replacements)
    {
        var output = input;
        foreach (var (source, replacement) in replacements)
        {
            output = Regex.Replace(
                output,
                $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(source)}(?![\p{{L}}\p{{N}}])",
                match => MatchCase(match.Value, replacement),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return output;
    }

    private static string MatchCase(string source, string replacement)
    {
        if (source.Equals(source.ToUpperInvariant(), StringComparison.Ordinal))
        {
            return replacement.ToUpperInvariant();
        }

        if (char.IsUpper(source[0]))
        {
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        }

        return replacement;
    }

    private static int GetStableSeed(string source, UwuIntensity intensity)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in source)
            {
                hash ^= ch;
                hash *= 16777619;
            }

            hash ^= (uint)intensity + 1;
            hash *= 16777619;
            return (int)hash;
        }
    }

    [GeneratedRegex(@"(?i)(?<![\p{L}\p{N}_])(?:(?:https?://|www\.)\S+|(?:[a-z0-9-]+\.)+[a-z]{2,}(?:/\S*)?)|\u3010[^\u3011]*\u3011", RegexOptions.CultureInvariant)]
    private static partial Regex ProtectedTextRegex();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex FirstWordRegex();
}
