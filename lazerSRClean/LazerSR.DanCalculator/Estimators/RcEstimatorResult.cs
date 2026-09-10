// Shared result shape for the RC-scope estimators (Azusa / Roxy) and the P6
// Mixed estimator that consumes them.
//
// PORT NOTE: not a 1:1 port of a single JS file. azusaEstimator.js and
// roxyEstimator.js each return an object literal with the SAME keys
// (star / lnRatio / columnCount / estDiff / numericDifficulty /
// numericDifficultyHint / graph / rawNumericDifficulty / debug); this class is
// that literal, with the JS keys PascalCased. `buildErrorResult` /
// `buildScopeResult` in both files produce the same shape.

namespace LazerSR.DanCalculator.Estimators;

public sealed class RcEstimatorResult
{
    /// <summary>JS `star` — `Number((3.4 + 0.38 * finalNumeric).toFixed(4))`, or NaN on error.</summary>
    public double Star;

    public double LnRatio;

    public double ColumnCount;

    /// <summary>JS `estDiff` — a dan label, or `"Invalid: &lt;message&gt;"` on error.</summary>
    public string EstDiff = "";

    /// <summary>JS `numericDifficulty` — <c>null</c> when out of RC scope / on error.</summary>
    public double? NumericDifficulty;

    /// <summary>JS `numericDifficultyHint` — e.g. "azusa-rc-v1", "roxy-meta-ridge-v3", "BelowScope".</summary>
    public string? NumericDifficultyHint;

    /// <summary>JS `graph` — `{ times[], values[] }` or <c>null</c>.</summary>
    public (double[] Times, double[] Values)? Graph;

    /// <summary>JS `rawNumericDifficulty` — the pre-blend structural numeric, or <c>null</c>.</summary>
    public double? RawNumericDifficulty;

    /// <summary>JS `debug` — diagnostic bag, passed through untouched by consumers.</summary>
    public Dictionary<string, object?> Debug = new();
}
