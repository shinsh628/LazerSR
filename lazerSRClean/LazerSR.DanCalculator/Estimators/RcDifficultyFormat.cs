// Port of mania-hub live-backend/vendor/leoblack/estimator/rcDifficultyFormat.js
//
// Bidirectional mapping between RC dan labels ("Reform 7 mid/high", "Gamma low",
// "Intro 2") and the continuous numeric scale (Intro = -2..0, Reform 1..10,
// Greek alpha=11 .. kappa=20, tier offset +-0.4).

using System.Globalization;
using System.Text.RegularExpressions;

namespace LazerSR.DanCalculator.Estimators;

public static class RcDifficultyFormat
{
    public static readonly IReadOnlyList<string> GreekByIndex = new[]
    {
        "Alpha",
        "Beta",
        "Gamma",
        "Delta",
        "Epsilon",
        "Emik Zeta",
        "Thaumiel Eta",
        "CloverWisp Theta",
        "Iota",
        "Kappa",
    };

    public static readonly IReadOnlyList<(string Suffix, double Offset)> RcTierCandidates = new[]
    {
        ("low", -0.4),
        ("mid/low", -0.2),
        ("mid", 0.0),
        ("mid/high", 0.2),
        ("high", 0.4),
    };

    // Insertion order matters (JS Object.entries iteration order).
    private static readonly (string Word, double Value)[] GreekBaseMap =
    {
        ("alpha", 11),
        ("beta", 12),
        ("gamma", 13),
        ("delta", 14),
        ("epsilon", 15),
        ("zeta", 16),
        ("eta", 17),
        ("theta", 18),
        ("iota", 19),
        ("kappa", 20),
    };

    private static double Clamp(double value, double min, double max)
        => Math.Max(min, Math.Min(max, value));

    public static string FormatRcBaseLabel(double @base)
    {
        if (@base <= 0)
        {
            double introLevel = Clamp(@base + 3, 1, 3);
            return $"Intro {introLevel}";
        }

        if (@base <= 10)
        {
            return $"Reform {@base}";
        }

        double greekIndex = Clamp(@base - 11, 0, GreekByIndex.Count - 1);
        return GreekByIndex[(int)greekIndex];
    }

    public static string NumericToRcLabel(double numeric)
    {
        // JS `Number(numeric)` — the value is already numeric here.
        double value = numeric;
        if (!double.IsFinite(value)) return "Invalid";

        double clamped = Clamp(value, -2.4, 20.4);
        (double Base, string Suffix, double Distance)? bestMatch = null;

        for (int @base = -2; @base <= 20; @base += 1)
        {
            foreach (var tier in RcTierCandidates)
            {
                double centerValue = @base + tier.Offset;
                double distance = Math.Abs(clamped - centerValue);
                if (bestMatch == null || distance < bestMatch.Value.Distance)
                {
                    bestMatch = (@base, tier.Suffix, distance);
                }
            }
        }

        if (bestMatch == null) return "Invalid";
        return $"{FormatRcBaseLabel(bestMatch.Value.Base)} {bestMatch.Value.Suffix}";
    }

    private static readonly Regex MidHighRe =
        new(@"\bmid\s*[/-]\s*high\b|\bmidhigh\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MidLowRe =
        new(@"\bmid\s*[/-]\s*low\b|\bmidlow\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LowRe = new(@"\blow\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HighRe = new(@"\bhigh\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MidRe = new(@"\bmid\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static double ParseTierAdjustment(string textLower)
    {
        if (MidHighRe.IsMatch(textLower)) return 0.2;
        if (MidLowRe.IsMatch(textLower)) return -0.2;
        if (LowRe.IsMatch(textLower)) return -0.4;
        if (HighRe.IsMatch(textLower)) return 0.4;
        if (MidRe.IsMatch(textLower)) return 0;
        return 0;
    }

    private static readonly Regex WhitespaceRe = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex AngleBracketRe = new(@"[<>]", RegexOptions.Compiled);
    private static readonly Regex IntroRe =
        new(@"\bintro\s*([123])\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NumberedRe =
        new(@"\b(?:reform|rework|regular)\s*(-?\d+(?:\.\d+)?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FinishRe = new(@"\bfinish\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StelliumRe = new(@"\bstellium\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlainNumberRe =
        new(@"(^|\s)(-?\d+(?:\.\d+)?)(\s|$)", RegexOptions.Compiled);

    private static readonly Dictionary<string, Regex> GreekWordRe =
        GreekBaseMap.ToDictionary(
            e => e.Word,
            e => new Regex($@"\b{e.Word}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase));

    public static double? RcLabelToNumeric(string? label)
    {
        string primary = WhitespaceRe
            .Replace((label ?? "").Split("||")[0], " ")
            .Trim();
        if (primary.Length == 0 || AngleBracketRe.IsMatch(primary)) return null;

        string textLower = primary.ToLowerInvariant();
        double? @base = null;

        var intro = IntroRe.Match(textLower);
        if (intro.Success)
        {
            @base = double.Parse(intro.Groups[1].Value, CultureInfo.InvariantCulture) - 3;
        }

        if (@base == null)
        {
            var numbered = NumberedRe.Match(textLower);
            if (numbered.Success)
            {
                @base = double.Parse(numbered.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        if (@base == null && (FinishRe.IsMatch(textLower) || StelliumRe.IsMatch(textLower)))
        {
            @base = 10;
        }

        if (@base == null)
        {
            foreach (var (word, value) in GreekBaseMap)
            {
                if (GreekWordRe[word].IsMatch(textLower))
                {
                    @base = value;
                    break;
                }
            }
        }

        if (@base == null)
        {
            var plain = PlainNumberRe.Match(textLower);
            if (plain.Success)
            {
                @base = double.Parse(plain.Groups[2].Value, CultureInfo.InvariantCulture);
            }
        }

        if (@base == null || !double.IsFinite(@base.Value)) return null;
        return @base.Value + ParseTierAdjustment(textLower);
    }
}
