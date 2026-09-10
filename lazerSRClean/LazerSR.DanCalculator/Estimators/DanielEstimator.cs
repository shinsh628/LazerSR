// Port of mania-hub live-backend/vendor/leoblack/estimator/danielEstimator.js
//
// Thin wrapper over calculateDaniel: runs the Daniel algorithm, falls back to
// Sunny for non-4K (JS code -3), then maps the star rating to a dan label.
//
// JS returns a plain object shaped like { star, lnRatio, columnCount, graph,
// estDiff, numericDifficulty, numericDifficultyHint } — mapped to SunnyResult.

using System.Globalization;
using LazerSR.DanCalculator.Parser;

namespace LazerSR.DanCalculator.Estimators;

public static class DanielEstimator
{
    // JS threads options.odFlag straight into runSunnyEstimatorFromText, which
    // accepts number | "HR" | "EZ". Our SunnyShim.Run takes double?; a non-numeric
    // flag is dropped. PORT NOTE: shim signature is fixed by the integration agent.
    private static double? AsOdDouble(object? odFlag)
    {
        if (odFlag is double d) return d;
        if (odFlag is int i) return i;
        if (odFlag is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
        {
            return p;
        }
        return null;
    }

    public static SunnyResult RunDanielEstimatorFromText(string osuText, EstimatorOptions? options = null, OsuFileParser? parsed = null)
    {
        options ??= new EstimatorOptions();
        double speedRate = options.SpeedRate ?? 1.0;
        object? odFlag = options.OdFlag;
        string? cvtFlag = options.CvtFlag;
        bool withGraph = options.WithGraph;

        var danielResult = DanielAlgorithm.CalculateDaniel(osuText, speedRate, odFlag, withGraph, parsed);

        // Keep previous behavior: Daniel only supports 4K and falls back to Sunny.
        if (danielResult.Code == -3)
        {
            return SunnyShim.Run(osuText, speedRate, AsOdDouble(odFlag), cvtFlag, withGraph);
        }

        // JS normalizeReworkResult(danielResult): -1 -> "Beatmap parse failed",
        // -2 -> "Beatmap mode is not mania" (thrown in this port).
        if (danielResult.Code != null)
        {
            ReworkEstimatorUtils.NormalizeReworkResult((double)danielResult.Code.Value);
        }

        if (!double.IsFinite(danielResult.Star)
            || !double.IsFinite(danielResult.LnRatio)
            || !double.IsFinite(danielResult.ColumnCount))
        {
            throw new InvalidOperationException(
                "Invalid estimator output" + danielResult.Star + danielResult.LnRatio + danielResult.ColumnCount);
        }

        var parsedResult = new NormalizedReworkResult
        {
            Star = danielResult.Star,
            LnRatio = danielResult.LnRatio,
            ColumnCount = danielResult.ColumnCount,
            Graph = danielResult.Graph,
        };

        bool useDanielDifficulty = parsedResult.ColumnCount == 4;
        DanielDanResult? danielDifficulty = useDanielDifficulty
            ? ReworkEstimatorUtils.EstimateDanielDan(parsedResult.Star)
            : null;
        double? numericDifficulty = useDanielDifficulty ? danielDifficulty!.Numeric : null;

        string estDiff = useDanielDifficulty
            ? danielDifficulty!.Label
            : ReworkEstimatorUtils.EstDiff(
                parsedResult.Star, parsedResult.LnRatio, parsedResult.ColumnCount,
                options.ExtendedEstimationRange == true,
                options.EnableAlwaysShowLNDifficulty == true);

        // JS: `useDanielDifficulty && !Number.isFinite(numericDifficulty) ? "N/A" : null`.
        string? numericDifficultyHint =
            useDanielDifficulty && !(numericDifficulty.HasValue && double.IsFinite(numericDifficulty.Value))
                ? "N/A"
                : null;

        return new SunnyResult
        {
            Star = parsedResult.Star,
            LnRatio = parsedResult.LnRatio,
            ColumnCount = parsedResult.ColumnCount,
            Graph = parsedResult.Graph,
            EstDiff = estDiff,
            NumericDifficulty = numericDifficulty,
            NumericDifficultyHint = numericDifficultyHint,
        };
    }
}
