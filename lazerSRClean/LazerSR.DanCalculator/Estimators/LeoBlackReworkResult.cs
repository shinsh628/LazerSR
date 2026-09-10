// Result shape returned by MixedEstimator.RunMixedEstimatorFromText (port of
// mania-hub live-backend/vendor/leoblack/estimator/mixedEstimator.js).
//
// PORT NOTE: not a 1:1 port of a single JS file. The JS `runMixedEstimatorFromText`
// returns `{ ...selectedRework, lnRatio, estDiff, numericDifficulty,
// numericDifficultyHint, mixedCompanellaPlan, actualEstimatorAlgorithm }` where
// `selectedRework` is one of the Sunny / Roxy / Azusa / Daniel result literals.
// This class is the union of those literals' keys (PascalCased) plus the two
// Mixed-only fields. Consumed downstream by chart-classifier.ts / leoblack-estimator.ts
// (ported by the integration agent).

namespace LazerSR.DanCalculator.Estimators;

public sealed class LeoBlackReworkResult
{
    /// <summary>JS `star`.</summary>
    public double Star;

    /// <summary>JS `lnRatio` (forced to 0 when the HO cvt flag is set).</summary>
    public double LnRatio;

    /// <summary>JS `columnCount`.</summary>
    public double ColumnCount;

    /// <summary>JS `estDiff` — the composed dan label.</summary>
    public string EstDiff = "";

    /// <summary>JS `numericDifficulty` — <c>null</c> out of scope / on error.</summary>
    public double? NumericDifficulty;

    /// <summary>JS `numericDifficultyHint`.</summary>
    public string? NumericDifficultyHint;

    /// <summary>JS `rawNumericDifficulty` (present on Roxy/Azusa results, else <c>null</c>).</summary>
    public double? RawNumericDifficulty;

    /// <summary>
    /// JS `mixedCompanellaPlan` — pending low-band Companella fusion plan, or <c>null</c>.
    /// <see cref="MixedEstimator.ApplyCompanellaToMixedResult"/> resolves it once the
    /// Companella result is available.
    /// </summary>
    public MixedCompanellaPlan? MixedCompanellaPlan;

    /// <summary>
    /// JS `actualEstimatorAlgorithm` — which sub-algorithm actually won the routing
    /// chain ("Sunny" / "Roxy" / "Azusa" / "Daniel" / "Companella").
    /// </summary>
    public string ActualEstimatorAlgorithm = "Sunny";

    /// <summary>JS `graph` — `{ times[], values[] }` or <c>null</c>.</summary>
    public (double[] Times, double[] Values)? Graph;

    /// <summary>JS `debug` — diagnostic bag passed through untouched.</summary>
    public Dictionary<string, object?> Debug = new();
}

// Port of the object literal produced by `buildLowBandCompanellaPlan` in
// mixedEstimator.js (and the bare `{ lnRatio, lnDifficulty }` fallback literal —
// there `FuseRc` is left false).
//
// 低难段融合计划：plan 携带 Azusa 的 RC 基准值（fuseRc），
// applyCompanellaToMixedResult 在 Companella 结果到达后做 0.5/0.5 融合。
// onDisagree 指定门控未通过时保留哪一侧（该分支改动前的原赢家），
// 保证融合只在两参考一致且都主张低难时生效，其余行为与改动前一致。
// (Low-band fusion plan: the plan carries Azusa's RC baseline value (fuseRc);
// applyCompanellaToMixedResult does the 0.5/0.5 fusion once the Companella result
// arrives. onDisagree names which side to keep when the gate fails — the original
// winner before this branch — so fusion only takes effect when both references
// agree and both claim low difficulty; all other behaviour is unchanged.)
public sealed class MixedCompanellaPlan
{
    /// <summary>JS `lnRatio`.</summary>
    public double LnRatio;

    /// <summary>JS `lnDifficulty` — the LN half label from the Sunny baseline.</summary>
    public string LnDifficulty = "";

    /// <summary>JS `fuseRc` — <c>true</c> for the low-band Azusa⊕Companella fusion plan;
    /// <c>false</c> for the bare `{ lnRatio, lnDifficulty }` fallback literal.</summary>
    public bool FuseRc;

    /// <summary>JS `onDisagree` — "azusa" or "companella"; which side wins if the gate fails.</summary>
    public string? OnDisagree;

    /// <summary>JS `rcEstDiff` — the Azusa RC label.</summary>
    public string? RcEstDiff;

    /// <summary>JS `rcNumeric` — `resultNumericValue(rcResult)`.</summary>
    public double? RcNumeric;

    /// <summary>JS `rcNumericHint` — `rcResult.numericDifficultyHint ?? null`.</summary>
    public string? RcNumericHint;
}
