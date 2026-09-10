// Port of danClearTargetFor + rateVerdictPairFor from mania-hub
// live-backend/src/features/player-skills.ts (~1862 / ~1996).
//
// "Which dan a play is measured against, from the chart and the played rate."
//
// CLIENT ADAPTATION of the rate-verdict lookups: mania-hub reads a stored
// (chart, ratePercent[, modVariant]) verdict from its DB. The client instead
// classifies the chart AT the play's rate (or on the inverted / vibro-adjusted
// chart) right now, and uses that classification's `primary` verdict. So:
//   rate == 1      -> the 1.0x classification's rc/ln half picked by lnRatio
//   rate == 1.5/0.75/other in-band -> `atRateVerdict.primary`
//   inverse/vibro  -> `variantVerdict.primary`
// This drops the (150 -> dtFamily, 75 -> htFamily) dedicated columns but lands
// on the same value: mania-hub's DT/HT sweeps ARE classifyChart at 1.5x / 0.75x.

using LazerSR.DanCalculator.Classifier;

namespace LazerSR.DanCalculator.PlayerRating;

/// <summary>JS <c>DanClearTarget</c> — the dan a play testifies for, before any accuracy gate.</summary>
public sealed record DanClearTarget(double RawDan, string Side, string? Label);

/// <summary>The primary verdict of a classification, reduced to what the target needs.</summary>
public readonly record struct RateVerdict(double RawDan, string Side, string? Label)
{
    public static RateVerdict? FromPrimary(LeanVerdictHalf? primary)
    {
        if (primary == null) return null;
        // player-skills.ts readRawDan semantics for the sweep columns: finite & > 0.
        if (!(double.IsFinite(primary.RawDan) && primary.RawDan > 0)) return null;
        string side = primary.Kind == "ln" ? "ln" : "rc";
        string? label = string.IsNullOrWhiteSpace(primary.DisplayName) ? null : primary.DisplayName.Trim();
        return new RateVerdict(primary.RawDan, side, label);
    }
}

public static class DanClearTargetResolver
{
    /// <summary>
    /// mania-hub <c>danClearTargetFor</c>. <paramref name="atRateVerdict"/> is the
    /// primary verdict from a classification at the play's rate (or on the
    /// inverted / vibro-adjusted chart when <paramref name="isVariant"/>); pass
    /// null when it is not available or the play is a plain 1.0x play.
    /// </summary>
    public static DanClearTarget? Resolve(
        int keyCount,
        double rate,
        bool isVariant,
        PlayChartInfo baseInfo,
        RateVerdict? atRateVerdict)
    {
        // JS `target(rawDan, side, label)` — null rawDan yields null.
        static DanClearTarget? Target(double? rawDan, string side, string? label)
            => rawDan == null ? null : new DanClearTarget(rawDan.Value, side, label);

        // 1. Invert / vibro-adjusted / vibro-clear-evidence: rated against the
        //    variant chart, whose verdict is its own at every rate.
        if (isVariant)
            return atRateVerdict is { } v ? new DanClearTarget(v.RawDan, v.Side, v.Label) : null;

        // 2. Plain 1.0x play: the chart's own rc/ln half, side by lnRatio.
        //    (mania-hub: `play.rate === 1 && info.lnRatio != null`. A null lnRatio
        //    falls through to the "other rate" branch, where clearRatePercent(1)
        //    is null -> no target.)
        if (rate == 1)
        {
            if (baseInfo.LnRatio == null) return null;
            string side = baseInfo.LnRatio.Value >= LnDan.LnPrimaryMinRatioFor(keyCount) ? "ln" : "rc";
            return side == "ln"
                ? Target(baseInfo.LnRawDan, "ln", baseInfo.LnDanLabel)
                : Target(baseInfo.RcRawDan, "rc", baseInfo.RcDanLabel);
        }

        // 3. Any other rate in the estimator's 50-200% band: the at-rate verdict.
        //    Out of band -> no target (clearRatePercent null).
        if (PlayEligibility.ClearRatePercent(rate) == null) return null;
        return atRateVerdict is { } r ? new DanClearTarget(r.RawDan, r.Side, r.Label) : null;
    }
}
