// Performance dan = a chart's dan credited (or decayed) by how far a clear's
// accuracy sits from that ladder's pass bar. Orchestrates the ported pieces:
//   ChartClassification (chart dan)  +  judgement counts (accuracy)
//   -> danClearBarFor (the bar)  ->  DanCredit.CreditedDanFor (the curve).
//
// Bar + currency selection are lifted from mania-hub
// live-backend/src/features/player-skills.ts (danClearBarFor + the push() branch);
// the accuracy formulas from live-backend/src/shared/score.ts.

using LazerSR.DanCalculator.Classifier;

namespace LazerSR.DanCalculator.Credit;

/// <summary>Mania judgement breakdown (MAX/300/200/100/50/miss).</summary>
public readonly record struct DanJudgements(int Max, int Great, int Good, int Ok, int Meh, int Miss)
{
    public int Total => Max + Great + Good + Ok + Meh + Miss;
}

public sealed class PerformanceDanResult
{
    /// <summary>"rc" | "ln" — the half the verdict was measured on.</summary>
    public string Side = "";
    public int KeyCount;
    public double ChartRawDan;
    public string ChartDisplayName = "";
    public double StableAccuracy;
    public double ScoreV2Accuracy;
    /// <summary>The accuracy actually checked against the bar (currency-matched).</summary>
    public double UsedAccuracy;
    /// <summary>The pass bar, in its own currency (may carry the stable→v2 offset).</summary>
    public double Bar;
    /// <summary>"stable" | "v2" — which formula the bar is written in.</summary>
    public string BarCurrency = "";
    /// <summary>Credited dan, or null when the clear is below the credit window.</summary>
    public double? PerformanceDan;
    /// <summary>The accuracy a pass needs to credit anything at all.</summary>
    public double MinAccuracy;
}

public static class PerformanceDan
{
    /// <summary>
    /// mania-hub player-skills.ts STABLE_EQUIVALENT_V2_BAR_OFFSET — what a ScoreV2
    /// bar becomes when checked on the stable formula instead.
    /// </summary>
    public const double StableEquivalentV2BarOffset = 0.005;

    private const double DanKyuBandMaxRawDan = 1;

    /// <summary>mania-hub shared/score.ts calculateStableAccuracy (ScoreV1, 300-weighted).</summary>
    public static double StableAccuracy(DanJudgements j)
    {
        int total = j.Total;
        if (total == 0) return 0;
        return (j.Max * 300.0 + j.Great * 300.0 + j.Good * 200.0 + j.Ok * 100.0 + j.Meh * 50.0)
               / (total * 300.0);
    }

    /// <summary>mania-hub shared/score.ts calculateScoreV2Accuracy (lazer/ScoreV2, 305-weighted).</summary>
    public static double ScoreV2Accuracy(DanJudgements j)
    {
        int total = j.Total;
        if (total == 0) return 0;
        double weighted = j.Max * 305.0 + j.Great * 300.0 + j.Good * 200.0 + j.Ok * 100.0 + j.Meh * 50.0;
        return Math.Clamp(weighted / (total * 305.0), 0, 1);
    }

    /// <summary>
    /// mania-hub player-skills.ts danClearBarFor — the pass bar for one side of one
    /// keymode's ladder, as its course rules state it.
    /// </summary>
    public static (double Accuracy, string Currency) DanClearBarFor(string side, int keyCount, double? chartDan = null)
    {
        if (keyCount == 4)
            return side == "ln" ? (0.97, "v2") : (0.96, "stable");
        if (side == "ln") return (0.95, "stable");
        bool kyu = chartDan is double d && d < DanKyuBandMaxRawDan;
        return (kyu ? 0.95 : 0.96, "stable");
    }

    /// <summary>
    /// Credit the primary verdict of <paramref name="classification"/> against a
    /// clear's judgement counts. Null when the chart has no dan verdict.
    /// </summary>
    public static PerformanceDanResult? Compute(ChartClassification classification, DanJudgements judgements)
    {
        var primary = classification.Primary;
        if (primary == null || !classification.Supported) return null;

        string side = primary.Kind == "ln" ? "ln" : "rc";
        int keyCount = classification.KeyCount;
        double rawDan = primary.RawDan;

        double stable = StableAccuracy(judgements);
        double v2 = ScoreV2Accuracy(judgements);

        var (barAccuracy, currency) = DanClearBarFor(side, keyCount, rawDan);
        double threshold = barAccuracy;
        double accuracy;
        if (currency != "v2")
        {
            accuracy = stable;
        }
        else if (judgements.Total > 0)
        {
            // Counts present → the ScoreV2 formula is recomputable exactly.
            accuracy = v2;
        }
        else
        {
            accuracy = stable;
            threshold += StableEquivalentV2BarOffset;
        }

        double? credited = DanCredit.CreditedDanFor(rawDan, accuracy, threshold, side, keyCount);
        double window = DanCredit.DanCreditBelowBarWindowFor(side, keyCount);

        return new PerformanceDanResult
        {
            Side = side,
            KeyCount = keyCount,
            ChartRawDan = rawDan,
            ChartDisplayName = primary.DisplayName,
            StableAccuracy = stable,
            ScoreV2Accuracy = v2,
            UsedAccuracy = accuracy,
            Bar = threshold,
            BarCurrency = currency,
            PerformanceDan = credited,
            MinAccuracy = Math.Round((threshold - window) * 1000) / 1000,
        };
    }
}
