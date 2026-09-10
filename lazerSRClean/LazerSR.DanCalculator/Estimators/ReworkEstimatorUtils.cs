// Port of mania-hub live-backend/vendor/leoblack/estimator/reworkEstimatorUtils.js
//
// Sunny/Daniel SR -> dan-label helpers + the raw-result normalizer. The numeric
// error codes from the JS estimators (-1 parse fail, -2 not mania) are kept as
// thrown exceptions.
//
// DanIntervalTable == (double Lo, double Hi, string Name)[]  (see Intervals/Tables.cs).

using LazerSR.DanCalculator.Intervals;

namespace LazerSR.DanCalculator.Estimators;

/// <summary>JS `estimateDanielDan` return shape: { label, numeric }.</summary>
public sealed class DanielDanResult
{
    public string Label = "";
    public double? Numeric;
}

/// <summary>JS `normalizeReworkResult` return shape: { star, lnRatio, columnCount, graph }.</summary>
public sealed class NormalizedReworkResult
{
    public double Star;
    public double LnRatio;
    public double ColumnCount;
    public (double[] Times, double[] Values)? Graph;
}

public static class ReworkEstimatorUtils
{
    /// <summary>JS `DAN_MEANS` — [mean, name] rows.</summary>
    public static readonly (double Mean, string Name)[] DanMeans =
    {
        (6.562, "Alpha"),
        (6.957, "Beta"),
        (7.459, "Gamma"),
        (7.939, "Delta"),
        (9.095, "Epsilon"),
        (9.473, "Emik Zeta"),
        (10.162, "Thaumiel Eta"),
        (10.782, "CloverWisp Theta"),
    };

    private const double DAN_ORDER_START = 11;

    private static (double Lower, double Upper)[] PrecomputeDanBoundaries()
    {
        var means = DanMeans.Select(m => m.Mean).ToArray();
        var boundaries = new List<(double, double)>();

        for (int i = 0; i < DanMeans.Length; i += 1)
        {
            double mean = means[i];
            double lower = i > 0
                ? (means[i - 1] + mean) / 2
                : mean - (((means[1] + mean) / 2) - mean);
            double upper = i < means.Length - 1
                ? (mean + means[i + 1]) / 2
                : mean + ((mean - means[i - 1]) / 2);
            boundaries.Add((lower, upper));
        }

        return boundaries.ToArray();
    }

    private static readonly (double Lower, double Upper)[] DanBoundaries = PrecomputeDanBoundaries();

    public static DanielDanResult EstimateDanielDan(double sr)
    {
        if (!double.IsFinite(sr))
        {
            return new DanielDanResult { Label = "Unknown", Numeric = null };
        }

        if (sr < DanBoundaries[0].Lower)
        {
            return new DanielDanResult { Label = $"< {DanMeans[0].Name} Low", Numeric = null };
        }

        if (sr >= DanBoundaries[DanBoundaries.Length - 1].Upper)
        {
            return new DanielDanResult { Label = $"> {DanMeans[DanMeans.Length - 1].Name} High", Numeric = null };
        }

        for (int i = 0; i < DanMeans.Length; i += 1)
        {
            var (lower, upper) = DanBoundaries[i];
            if (sr >= lower && sr < upper)
            {
                double tRaw = (sr - lower) / (upper - lower);
                double t = Math.Max(0, Math.Min(tRaw, 1));
                // PORT NOTE: JS `Number((x).toFixed(2))` — round to 2 decimals, half away from zero.
                double numeric = Math.Round(DAN_ORDER_START + i + t, 2, MidpointRounding.AwayFromZero);

                string label;
                if (t < 1.0 / 3)
                {
                    label = $"{DanMeans[i].Name} Low";
                }
                else if (t < 2.0 / 3)
                {
                    label = $"{DanMeans[i].Name} Mid";
                }
                else
                {
                    label = $"{DanMeans[i].Name} High";
                }

                return new DanielDanResult { Label = label, Numeric = numeric };
            }
        }

        return new DanielDanResult { Label = "Unknown", Numeric = null };
    }

    public static string IntervalLookup(double sr, (double Lo, double Hi, string Name)[] table, string fallbackLabel)
    {
        foreach (var (lower, upper, name) in table)
        {
            if (lower <= sr && sr <= upper) return name;
        }
        // PORT NOTE: source assumes table non-empty; guard array-OOB.
        if (table.Length == 0) return fallbackLabel;
        if (sr < table[0].Lo) return $"< {table[0].Name}";
        if (sr > table[table.Length - 1].Hi) return $"> {table[table.Length - 1].Name}";
        return fallbackLabel;
    }

    // enableAlwaysShowLNDifficulty default false = config.js defaults.enableAlwaysShowLNDifficulty.
    // Callers (analysis.js etc.) pass the resolved state value explicitly.
    public static string EstDiff(
        double sr, double lnRatio, double columnCount, bool useExtended = false, bool enableAlwaysShowLNDifficulty = false)
    {
        var keys = DanIndex.For(columnCount);
        if (keys == null) return "Unknown difficulty";

        var rcTable = keys.Rc.Resolve(useExtended);
        string rcDiff = IntervalLookup(sr, rcTable, "Unknown RC difficulty");
        if (lnRatio < 0.15 && !enableAlwaysShowLNDifficulty) return rcDiff;

        // The LN table may be missing (e.g. 10K only has an RC table): fall back to RC-only.
        var lnTable = keys.Ln?.Resolve(useExtended);
        if (lnTable == null) return rcDiff;
        string lnDiff = IntervalLookup(sr, lnTable, "Unknown LN difficulty");
        return $"{rcDiff} || {lnDiff}";
    }

    public static string EstDiff2(double sr, double srLN, double columnCount, bool useExtended = false)
    {
        var keys = DanIndex.For(columnCount);
        if (keys == null) return "Unknown difficulty";

        var rcTable = keys.Rc.Resolve(useExtended);
        string rcDiff = IntervalLookup(sr, rcTable, "Unknown RC difficulty");
        if (srLN <= 0) return rcDiff;

        // Same as above: RC-only when the LN table is missing.
        var lnTable = keys.Ln?.Resolve(useExtended);
        if (lnTable == null) return rcDiff;
        string lnDiff = IntervalLookup(srLN, lnTable, "Unknown LN difficulty");
        return $"{rcDiff} || {lnDiff}";
    }

    /// <summary>
    /// JS `normalizeReworkResult` — object/array branch. Takes our <see cref="SunnyResult"/>
    /// (the sunny-substitution stand-in for the raw estimator output).
    /// </summary>
    public static NormalizedReworkResult NormalizeReworkResult(SunnyResult result)
    {
        double sr = result.Star;
        double lnRatio = result.LnRatio;
        double columnCount = result.ColumnCount;
        var graph = result.Graph;

        if (!double.IsFinite(sr) || !double.IsFinite(lnRatio) || !double.IsFinite(columnCount))
        {
            throw new InvalidOperationException("Invalid estimator output" + sr + lnRatio + columnCount);
        }

        return new NormalizedReworkResult
        {
            Star = sr,
            LnRatio = lnRatio,
            ColumnCount = columnCount,
            Graph = graph,
        };
    }

    /// <summary>
    /// JS `normalizeReworkResult` — array branch: `[sr, lnRatio, columnCount]`.
    /// </summary>
    public static NormalizedReworkResult NormalizeReworkResult(double sr, double lnRatio, double columnCount)
    {
        if (!double.IsFinite(sr) || !double.IsFinite(lnRatio) || !double.IsFinite(columnCount))
        {
            throw new InvalidOperationException("Invalid estimator output" + sr + lnRatio + columnCount);
        }

        return new NormalizedReworkResult { Star = sr, LnRatio = lnRatio, ColumnCount = columnCount, Graph = null };
    }

    /// <summary>
    /// JS `normalizeReworkResult` — numeric-code branch. Kept as thrown exceptions.
    /// </summary>
    public static NormalizedReworkResult NormalizeReworkResult(double resultCode)
    {
        if (resultCode == -1)
        {
            throw new InvalidOperationException("Beatmap parse failed");
        }
        if (resultCode == -2)
        {
            throw new InvalidOperationException("Beatmap mode is not mania");
        }
        throw new InvalidOperationException($"Unknown result code: {resultCode}");
    }
}
