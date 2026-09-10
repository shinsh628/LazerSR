// See PORTING.md §"Sunny substitution".
//
// Shape of what our SunnyShim.Run(...) returns — a stand-in for LeoBlack JS's
// `runSunnyEstimatorFromText` / `sunnyAlgorithm.calculate` output after it has
// been through `reworkEstimatorUtils.normalizeReworkResult` and sunnyEstimator.js.
// The classifier-integration agent owns SunnyShim's body; estimator agents rely
// only on this contract.

namespace LazerSR.DanCalculator.Estimators;

public sealed class SunnyResult
{
    /// <summary>Vanilla Sunny star rating (JS `result.star`).</summary>
    public double Star;

    /// <summary>hold-note-count / total-note-count of the parsed chart (JS `result.lnRatio`).</summary>
    public double LnRatio;

    /// <summary>Key count as a double (JS `result.columnCount`).</summary>
    public double ColumnCount;

    /// <summary>ReworkEstimatorUtils.EstDiff(...) label (JS `result.estDiff`).</summary>
    public string EstDiff = "";

    /// <summary>Optional strain graph, adapted to { times[], values[] } (JS `result.graph`).</summary>
    public (double[] Times, double[] Values)? Graph;

    /// <summary>Optional continuous numeric difficulty (Daniel/Roxy/Azusa scale).</summary>
    public double? NumericDifficulty;

    /// <summary>Optional hint describing how <see cref="NumericDifficulty"/> was derived.</summary>
    public string? NumericDifficultyHint;
}
