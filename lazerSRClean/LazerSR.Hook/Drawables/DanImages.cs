using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using LazerSR.DanCalculator.Classifier;
using osu.Framework.Graphics;

namespace LazerSR.Hook.Drawables;

/// <summary>
/// Dan badge artwork lookup — port of mania-hub src/lib/dan-images.ts
/// (getDanImageSrc + danBareLabel). Assets ship loose under <c>&lt;hookdir&gt;/dans/</c>
/// mirroring mania-hub's public/images/dans tree.
/// <para>
/// mania-hub's badges are SVG/webp; osu!'s renderer is raster only, so we ship
/// PNGs rasterised from those sources (256px, see <c>Assets/dans/</c>).
/// <see cref="ResolveExisting"/> resolves <c>.png</c> first, then <c>.webp</c>;
/// a bare <c>.svg</c> never resolves. Missing art is not an error.
/// </para>
/// </summary>
public static class DanImages
{
    private static readonly Dictionary<string, string> ReformExtensions = new()
    {
        ["1"] = "svg", ["2"] = "svg", ["3"] = "svg", ["4"] = "svg", ["5"] = "svg",
        ["6"] = "svg", ["7"] = "svg", ["8"] = "svg", ["9"] = "svg", ["10"] = "svg",
        ["alpha"] = "webp", ["beta"] = "webp", ["gamma"] = "webp", ["delta"] = "webp",
        ["epsilon"] = "webp", ["zeta"] = "webp", ["eta"] = "webp", ["theta"] = "webp",
        ["iota"] = "webp", ["kappa"] = "webp",
    };

    private static readonly HashSet<string> SevenKLabels = new()
    {
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10",
        "gamma", "azimuth", "zenith", "stellium",
    };

    private static readonly HashSet<string> SixKLabels = new()
    {
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        "terra", "celestial", "mystery", "nihility", "finish",
    };

    private static string? _dansRoot;

    private static string DansRoot => _dansRoot ??= Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory,
        "dans");

    /// <summary>mania-hub danBareLabel — strip the tier suffix, lowercase.</summary>
    public static string BareLabel(string displayName) => displayName.TrimEnd('+', '-', ' ').Trim().ToLowerInvariant();

    /// <summary>
    /// mania-hub getDanImageSrc — the badge's relative path (e.g. "reform/epsilon.webp"),
    /// or null when no art exists for that verdict. Extension is the mania-hub source
    /// format; use <see cref="ResolveExisting"/> for a file osu! can actually load.
    /// </summary>
    public static string? RelativePath(string label, string family, int keyCount)
    {
        label = label.Trim().ToLowerInvariant();
        bool ln = family == "ln";

        if (keyCount == 7)
            return SevenKLabels.Contains(label) ? $"7k/{(ln ? "ln-" : "")}{label}.svg" : null;
        if (keyCount == 6)
            return SixKLabels.Contains(label) ? $"6k/{(ln ? "ln-" : "")}{label}.svg" : null;
        if (keyCount != 4)
            return null;

        if (ln && IsLnLadderLevel(label))
            return $"ln/{label}.svg";

        return ReformExtensions.TryGetValue(label, out var ext) ? $"reform/{label}.{ext}" : null;
    }

    /// <summary>Convenience overload from a verdict half.</summary>
    public static string? RelativePath(DanVerdictHalf half, int keyCount)
        => RelativePath(half.Label, half.Kind, keyCount);

    /// <summary>
    /// Absolute path to an existing, loadable badge file for this verdict, or null.
    /// Prefers a hand-supplied <c>.png</c>, then the mania-hub webp; skips svg.
    /// </summary>
    public static string? ResolveExisting(string label, string family, int keyCount)
    {
        string? rel = RelativePath(label, family, keyCount);
        if (rel == null) return null;

        string noExt = Path.ChangeExtension(rel, null);
        foreach (var ext in new[] { ".png", ".webp" })
        {
            string full = Path.Combine(DansRoot, (noExt + ext).Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) return full;
        }
        return null;
    }

    public static string? ResolveExisting(DanVerdictHalf half, int keyCount)
        => ResolveExisting(half.Label, half.Kind, keyCount);

    private static readonly string[] ReformGreek =
        { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa" };
    private static readonly string[] SevenKBosses = { "gamma", "azimuth", "zenith", "stellium" };
    private static readonly string[] SixKBands = { "terra", "celestial", "mystery", "nihility", "finish" };

    /// <summary>
    /// mania-hub danScaleLabel(danScaleContextFor(..)) — the badge label for a
    /// continuous rawDan value on the ladder a keymode+side is read on. Used to art
    /// a performance dan (which is a number, not a parsed verdict).
    /// </summary>
    public static string ScaleLabel(double value, int keyCount, string family)
    {
        int level = (int)Math.Floor(value + 0.5);
        bool ln = family == "ln";

        if (keyCount == 7)
            return level <= 10 ? Math.Max(0, level).ToString() : SevenKBosses[Math.Clamp(level, 11, 14) - 11];
        if (keyCount == 6)
            return level <= 9 ? Math.Max(0, level).ToString() : SixKBands[Math.Clamp(level, 10, 14) - 10];
        if (keyCount == 4 && ln)
            return Math.Clamp(level, 1, 17).ToString();
        // 4K RC / reform ladder.
        return level <= 10 ? Math.Max(1, level).ToString() : ReformGreek[Math.Clamp(level, 11, 20) - 11];
    }

    /// <summary>
    /// mania-hub dan-images.ts DAN_TIER_COLORS — the exponent suffix hue. Sign picks
    /// it (below the level's middle reads cool, above reads warm), doubling pushes it
    /// further out. Null for the mid tier (no suffix).
    /// </summary>
    public static Colour4? TierColour(string? suffix) => suffix switch
    {
        "--" => new Colour4(0x4d, 0xb8, 0xff, 0xff),
        "-" => new Colour4(0x7a, 0xc8, 0xea, 0xff),
        "+" => new Colour4(0xff, 0xab, 0x74, 0xff),
        "++" => new Colour4(0xef, 0x6f, 0x7f, 0xff),
        _ => null,
    };

    /// <summary>
    /// The tier suffix for a continuous rawDan value — where it sits inside its
    /// level. Same bands as CompanellaEstimator.ParsePrediction / the table verdicts.
    /// </summary>
    public static string TierSuffix(double value)
    {
        double off = value - Math.Round(value, MidpointRounding.AwayFromZero);
        return off <= -0.3 ? "--" : off <= -0.1 ? "-" : off < 0.1 ? "" : off < 0.3 ? "+" : "++";
    }

    private static bool IsLnLadderLevel(string label)
        => int.TryParse(label, out int n) && n >= 1 && n <= 17;
}
