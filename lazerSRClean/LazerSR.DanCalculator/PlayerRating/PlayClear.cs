// Port of the per-play body of mania-hub collectDanClears
// (live-backend/src/features/player-skills.ts ~2032-2147) + DanClearRejectReason (~1953).
//
// One rated play + its chart -> a credited dan clear, or the rule that stopped it.
// The aggregation over a player's clears (weightedDanClearWindow / danFromClears /
// averageSkillsetDans) is server-side and NOT ported here.
//
// mania-hub's `daWidensHitWindows` / `scoreRewritesChart` / null `getPlayRate`
// checks are NOT in collectDanClears — they refuse the play a rating entirely,
// upstream. The client applies them in PlayUploadRecord.Build (ratingExcluded).

using System;
using LazerSR.DanCalculator.Credit;
using LazerSR.DanCalculator.Vibro;

namespace LazerSR.DanCalculator.PlayerRating;

/// <summary>JS <c>DanClearRejectReason</c>.</summary>
public enum DanClearRejectReason
{
    ChartUnanalyzed,
    ChartIneligible,
    ChartVibro,
    RateVibro,
    LowOd,
    EzWindows,
    NoAccuracy,
    NoChartDan,
    BelowBar,
}

/// <summary>The per-play clear outcome. <see cref="Reject"/> null = a credited clear.</summary>
public sealed class PlayClearResult
{
    public DanClearRejectReason? Reject;

    /// <summary>"rc" | "ln" — set whenever a target was resolved (clear or reject).</summary>
    public string? Side;
    public double? ChartRawDan;
    public string? ChartDanLabel;

    // Clear + below_bar only.
    public double? StableAccuracy;
    public double? ScoreV2Accuracy;
    /// <summary>The accuracy actually checked against the bar (currency-matched, vibro-damped).</summary>
    public double? UsedAccuracy;
    public double? Bar;
    public string? Currency;

    // Clear only.
    public double? CreditedDan;
    public string[] Buckets = Array.Empty<string>();

    // Reject detail.
    public double? MinAccuracy; // below_bar
    public double? Od;          // low_od

    public bool IsClear => Reject == null;
}

public sealed class PlayClearInput
{
    public int KeyCount;
    /// <summary>The 1.0x chart info, or null (=> chart_unanalyzed).</summary>
    public PlayChartInfo? Info;
    /// <summary>Resolved by <see cref="DanClearTargetResolver"/>; null =&gt; no_chart_dan.</summary>
    public DanClearTarget? Target;

    /// <summary>Raw OD a Difficulty Adjust play was judged at, or null.</summary>
    public double? OdOverride;
    /// <summary>EZ widened the hit windows 1.4x (both clients).</summary>
    public bool EzWindows;

    /// <summary>ScoreV1 (stable) accuracy from the judgement counts.</summary>
    public double StableAccuracy;
    /// <summary>ScoreV2 accuracy from the judgement counts.</summary>
    public double ScoreV2Accuracy;
    /// <summary>The client's displayed accuracy (lazer = ScoreV2). Null only when there are no counts.</summary>
    public double? DisplayedAccuracy;

    public bool IsVibroAdjusted;
    public double VibroJudgementShare;

    // Bucket-walk inputs.
    /// <summary>The play's SSR vector at its goal+rate (4K); an empty vector for non-4K / no SSR.</summary>
    public ISsrVector PlaySsr = new SsrVector(new System.Collections.Generic.Dictionary<string, double>());
    public double Rate = 1;
    public bool Inverse;
}

public static class PlayClear
{
    /// <summary>JS Math.round.</summary>
    private static double JsRound(double v) => Math.Floor(v + 0.5);

    public static PlayClearResult TryBuild(PlayClearInput input)
    {
        var target = input.Target;
        var result = new PlayClearResult
        {
            Side = target?.Side,
            ChartRawDan = target?.RawDan,
            ChartDanLabel = target?.Label,
        };

        var info = input.Info;
        if (info == null) { result.Reject = DanClearRejectReason.ChartUnanalyzed; return result; }

        if (!info.DanEligible) { result.Reject = DanClearRejectReason.ChartIneligible; return result; }

        // A DA play was judged at the OD it set, not the chart's.
        double? playOd = input.OdOverride ?? info.Od;
        if (playOd != null && playOd.Value < PlayEligibility.DanMinOdFor(input.KeyCount, target?.Side))
        {
            result.Reject = DanClearRejectReason.LowOd;
            result.Od = playOd;
            return result;
        }

        if (input.EzWindows) { result.Reject = DanClearRejectReason.EzWindows; return result; }

        if (input.DisplayedAccuracy == null) { result.Reject = DanClearRejectReason.NoAccuracy; return result; }

        double displayed = input.DisplayedAccuracy.Value;
        double stable = input.StableAccuracy;
        double scoreV2 = input.ScoreV2Accuracy;
        bool isLazerPlay = Math.Abs(displayed - stable) > 1e-9;

        // push()
        if (target == null) { result.Reject = DanClearRejectReason.NoChartDan; return result; }

        string side = target.Side;
        double rawDan = target.RawDan;

        var (barAccuracy, currency) = PerformanceDan.DanClearBarFor(side, input.KeyCount, rawDan);
        double threshold = barAccuracy;
        double accuracy;
        if (currency != "v2")
        {
            accuracy = stable; // JS: stable ?? displayed — stable is always present here
        }
        else if (!double.IsNaN(scoreV2)) // JS `scoreV2 != null`; our recompute always yields a number
        {
            accuracy = scoreV2;
        }
        else if (isLazerPlay)
        {
            accuracy = displayed;
        }
        else
        {
            accuracy = stable;
            threshold += PerformanceDan.StableEquivalentV2BarOffset;
        }

        if (input.IsVibroAdjusted)
            accuracy = VibroSections.ConservativeVibroAccuracy(accuracy, input.VibroJudgementShare);

        result.StableAccuracy = stable;
        result.ScoreV2Accuracy = scoreV2;
        result.UsedAccuracy = accuracy;
        result.Bar = threshold;
        result.Currency = currency;

        double? creditedDan = DanCredit.CreditedDanFor(rawDan, accuracy, threshold, side, input.KeyCount);
        if (creditedDan == null)
        {
            result.Reject = DanClearRejectReason.BelowBar;
            result.MinAccuracy = JsRound((threshold - DanCredit.DanCreditBelowBarWindowFor(side, input.KeyCount)) * 1000) / 1000;
            return result;
        }

        result.CreditedDan = creditedDan.Value;
        result.Buckets = DanBuckets.BucketsForPlay(
            input.KeyCount, side, input.PlaySsr, info.ToDanChartInfo(), input.Rate, input.Inverse);
        return result;
    }
}
