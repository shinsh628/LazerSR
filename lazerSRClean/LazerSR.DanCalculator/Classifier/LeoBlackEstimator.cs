// Partial port of mania-hub live-backend/src/dan/leoblack-estimator.ts
//
// Only the parsing / labeling helpers the chart classifier needs are ported here:
// RC_TIER_VARIANTS / RC_TIER_OFFSETS, DANIEL_TIER_*, GREEK_LEVELS / GREEK_LABELS,
// the tier regexes, normalLabelForLevel, parseRcBaseLevel, parseLeoBlackRcHalf,
// parseLeoBlackLnHalf, and the ParsedDanPart shape.
//
// runLeoBlackMixed / runLeoBlackSunny / estimateLeoBlackDan are elsewhere:
//   - RunLeoBlackMixed / RunLeoBlackSunny live in Estimators/MixedEstimator.cs
//   - estimateLeoBlackDan (the deprecated DanEstimate adapter) is not ported here
//     (chart classification uses ChartClassifier.ClassifyChart instead).

using System.Text.RegularExpressions;

namespace LazerSR.DanCalculator.Classifier;

/// <summary>JS `ParsedDanPart` — one half of a parsed "RC || LN" verdict.</summary>
public sealed class ParsedDanPart
{
    public string Label = "";
    public string? Variant;
    public double RawDan;
    /// <summary>"below" | "above" | null.</summary>
    public string? Boundary;
}

public static class LeoBlackEstimator
{
    // Upstream tiers split each dan into five bands, same as the app's --/-/none/+/++.
    private static readonly Dictionary<string, string?> RC_TIER_VARIANTS = new()
    {
        ["low"] = "--",
        ["mid/low"] = "-",
        ["mid"] = null,
        ["mid/high"] = "+",
        ["high"] = "++",
    };

    private static readonly Dictionary<string, double> RC_TIER_OFFSETS = new()
    {
        ["low"] = -0.4,
        ["mid/low"] = -0.2,
        ["mid"] = 0,
        ["mid/high"] = 0.2,
        ["high"] = 0.4,
    };

    // Daniel-sourced labels only have three tiers ("Gamma Mid" style).
    private static readonly Dictionary<string, string?> DANIEL_TIER_VARIANTS = new()
    {
        ["Low"] = "-",
        ["Mid"] = null,
        ["High"] = "+",
    };

    private static readonly Dictionary<string, double> DANIEL_TIER_OFFSETS = new()
    {
        ["Low"] = -1.0 / 3.0,
        ["Mid"] = 0,
        ["High"] = 1.0 / 3.0,
    };

    private static readonly Dictionary<string, double> GREEK_LEVELS = new()
    {
        ["alpha"] = 11,
        ["beta"] = 12,
        ["gamma"] = 13,
        ["delta"] = 14,
        ["epsilon"] = 15,
        ["emik zeta"] = 16,
        ["zeta"] = 16,
        ["thaumiel eta"] = 17,
        ["eta"] = 17,
        ["cloverwisp theta"] = 18,
        ["theta"] = 18,
        ["iota"] = 19,
        ["kappa"] = 20,
    };

    private static readonly string[] GREEK_LABELS =
        { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa" };

    private static readonly Regex RC_TIER_PATTERN = new(@"^(.+?) (low|mid/low|mid/high|mid|high)$", RegexOptions.Compiled);
    private static readonly Regex DANIEL_TIER_PATTERN = new(@"^(.+?) (Low|Mid|High)$", RegexOptions.Compiled);
    private static readonly Regex LN_PART_PATTERN = new(@"^(?:\S+ )?LN (\d+)$", RegexOptions.Compiled);
    private static readonly Regex INTRO_PATTERN = new(@"^Intro ([1-3])$", RegexOptions.Compiled);
    private static readonly Regex REFORM_PATTERN = new(@"^Reform (\d+)$", RegexOptions.Compiled);

    private static string NormalLabelForLevel(double level)
    {
        if (level <= 0) return "1";
        if (level <= 10) return ((long)level).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return GREEK_LABELS[(int)Math.Min(level, 20) - 11];
    }

    private static double? ParseRcBaseLevel(string @base)
    {
        var intro = INTRO_PATTERN.Match(@base);
        if (intro.Success) return double.Parse(intro.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 3;
        var reform = REFORM_PATTERN.Match(@base);
        if (reform.Success) return double.Parse(reform.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return GREEK_LEVELS.TryGetValue(@base.ToLowerInvariant(), out var v) ? v : (double?)null;
    }

    public static ParsedDanPart? ParseLeoBlackRcHalf(string text, double? numericDifficulty)
    {
        string? boundary = null;
        string body = text;
        if (body.StartsWith("< "))
        {
            boundary = "below";
            body = body[2..].Trim();
        }
        else if (body.StartsWith("> "))
        {
            boundary = "above";
            body = body[2..].Trim();
        }

        bool numericFinite = numericDifficulty is double nd && double.IsFinite(nd);

        var rcMatch = RC_TIER_PATTERN.Match(body);
        if (rcMatch.Success)
        {
            double? level = ParseRcBaseLevel(rcMatch.Groups[1].Value);
            if (level != null)
            {
                string tier = rcMatch.Groups[2].Value;
                double rawDan = boundary == "below"
                    ? level.Value - 0.5
                    : boundary == "above"
                        ? level.Value + 0.5
                        : numericFinite
                            ? numericDifficulty!.Value
                            : level.Value + RC_TIER_OFFSETS[tier];
                return new ParsedDanPart
                {
                    Label = NormalLabelForLevel(level.Value),
                    Variant = boundary == "below" ? "--" : boundary == "above" ? "++" : RC_TIER_VARIANTS[tier],
                    RawDan = rawDan,
                    Boundary = boundary,
                };
            }
        }

        var danielMatch = DANIEL_TIER_PATTERN.Match(body);
        if (danielMatch.Success)
        {
            if (GREEK_LEVELS.TryGetValue(danielMatch.Groups[1].Value.ToLowerInvariant(), out var level))
            {
                string tier = danielMatch.Groups[2].Value;
                // Daniel numerics are bottom-anchored (11 + index + t with t in [0,1)).
                double rawDan = boundary == "below"
                    ? level - 0.5
                    : boundary == "above"
                        ? level + 0.5
                        : numericFinite
                            ? numericDifficulty!.Value - 0.5
                            : level + DANIEL_TIER_OFFSETS[tier];
                return new ParsedDanPart
                {
                    Label = NormalLabelForLevel(level),
                    Variant = boundary == "below" ? "--" : boundary == "above" ? "++" : DANIEL_TIER_VARIANTS[tier],
                    RawDan = rawDan,
                    Boundary = boundary,
                };
            }
        }

        return null;
    }

    public static ParsedDanPart? ParseLeoBlackLnHalf(string text)
    {
        string? boundary = null;
        string body = text;
        if (body.StartsWith("< "))
        {
            boundary = "below";
            body = body[2..].Trim();
        }
        else if (body.StartsWith("> "))
        {
            boundary = "above";
            body = body[2..].Trim();
        }

        var tierMatch = RC_TIER_PATTERN.Match(body);
        if (!tierMatch.Success) return null;
        var baseMatch = LN_PART_PATTERN.Match(tierMatch.Groups[1].Value);
        if (!baseMatch.Success) return null;

        double level = double.Parse(baseMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        string tier = tierMatch.Groups[2].Value;
        return new ParsedDanPart
        {
            Label = ((long)level).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Variant = boundary == "below" ? "--" : boundary == "above" ? "++" : RC_TIER_VARIANTS[tier],
            RawDan = boundary == "below" ? level - 0.5 : boundary == "above" ? level + 0.5 : level + RC_TIER_OFFSETS[tier],
            Boundary = boundary,
        };
    }
}
