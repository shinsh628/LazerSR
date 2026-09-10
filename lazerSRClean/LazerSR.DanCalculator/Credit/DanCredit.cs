// Port of mania-hub live-backend/src/dan/dan-credit.ts
//
// The accuracy-graded credit curve for dan clears: how far above or below a
// chart's own rawDan a pass credits, as a function of where its accuracy sits
// against the ladder's bar. The bar is the zero point on purpose: a bare pass
// at the bar credits the chart's full rawDan, exactly as it always has, and
// the curve only moves credit away from the bar in both directions. That is
// what separates this from the removed danCreditFor fade (added 970c48b1,
// removed 75373b2b), which discounted the at-bar clear itself on top of the
// quorum and landed below what the community tables ask for.
//
// The shared anchor tables are normalized so one table serves every ladder no
// matter where its bar sits (95%, 96%, 97%):
//   above the bar, on t = (accuracy - bar) / max(1 - bar, window), the share
//   of the remaining headroom but never against a span narrower than the
//   decay window;
//   below the bar, on s = (bar - accuracy) / window, so the credit window
//   spans a fixed number of accuracy points under the bar - five on the rice
//   ladders, three on 6K/7K LN (danCreditBelowBarWindowFor).
//
// 4K LN is the exception on both halves and carries tables of its own
// (danCreditOptionsFor): its bar is written in ScoreV2, where a 100% is not
// reachable on most charts with long notes, so a curve whose top anchor sits
// on 100% prices the accuracies people actually set at nearly nothing. Its
// bonus is keyed in absolute points over the bar and tops out at 99.7%, and
// its window runs 2.5 points under the bar rather than three.

using LazerSR.DanCalculator.Classifier;

namespace LazerSR.DanCalculator.Credit;

/// <summary>JS <c>DanCreditAnchors = ReadonlyArray&lt;readonly [at, offset]&gt;</c>.</summary>
public sealed class DanCreditOptions
{
    public (double At, double Offset)[]? AboveBar;
    public (double At, double Offset)[]? BelowBar;
    public double? BelowBarWindow;

    /// <summary>
    /// The smallest magnitude a sub-bar credit may take. Rice uses zero for a
    /// continuous near-bar penalty; LN and courses retain a minimum deduction.
    /// Applied as a clamp on the interpolated offset.
    /// </summary>
    public double? NearBarCap;

    /// <summary>Off for a ladder that credits from the bar up only (4K LN courses).</summary>
    public bool? AllowBelowBar;

    /// <summary>
    /// How the above-bar anchors are keyed: "headroom" is the normalized t
    /// described above; "delta" reads them as absolute accuracy points over the
    /// bar, which is how the course registry's historical tables are written.
    /// </summary>
    public string? AboveBarScale;
}

public static class DanCredit
{
    /// <summary>
    /// How far under a ladder's bar a pass still credits something, in accuracy
    /// points. Five on the rice ladders since 2026-08-31 (four before it), so a 96%
    /// bar credits down to 91% rather than 92%.
    /// </summary>
    public const double DAN_CREDIT_BELOW_BAR_WINDOW = 0.05;

    /// <summary>
    /// The narrowest span the bonus half ever scores against, which is the decay
    /// window as it stood when the bonus was tuned.
    /// </summary>
    public const double DAN_CREDIT_BONUS_MIN_SPAN = 0.04;

    /// <summary>
    /// The 6K/7K LN ladders' own decay window, still much narrower than rice's five
    /// points.
    /// </summary>
    public const double DAN_CREDIT_LN_BELOW_BAR_WINDOW = 0.03;

    /// <summary>4K LN's own window, wider than the other LN ladders' (2026-08-29).</summary>
    public const double DAN_CREDIT_4K_LN_BELOW_BAR_WINDOW = 0.025;

    /// <summary>The ladder-aware decay window, mirroring danCreditNearBarCapFor's shape.</summary>
    public static double DanCreditBelowBarWindowFor(string side, int keyCount)
    {
        if (side != "ln") return DAN_CREDIT_BELOW_BAR_WINDOW;
        return keyCount == 4 ? DAN_CREDIT_4K_LN_BELOW_BAR_WINDOW : DAN_CREDIT_LN_BELOW_BAR_WINDOW;
    }

    /// <summary>
    /// THE tuning knob for the bonus half. The first quarter of the span is a flat
    /// zone: a pass in the point above the bar is a bare clear, not a bonus. The
    /// real bonus opens at 99% (2026-08-28, second cool-off): under it the curve
    /// only crawls to +0.2, because the 98s were still buying a full level (a
    /// 98.3% on a beta++ chart credited bare gamma; the owner prices that run at
    /// gamma--, and +0.14 there is what prints it). At a 96% bar this reads:
    /// 96-96.99% -> +0, 98% -> +0.12, 98.7% -> +0.2, 99% -> +0.7, 99.5% -> +1.1,
    /// 100% -> +1.5 (the 99%-and-up anchors are unchanged).
    /// </summary>
    public static readonly (double At, double Offset)[] DAN_CREDIT_ABOVE_BAR_ANCHORS =
    {
        (0, 0),
        (0.25, 0),
        (0.675, 0.2),
        (0.75, 0.7),
        (0.875, 1.1),
        (1, 1.5),
    };

    /// <summary>
    /// The rice decay half. At a 96% bar:
    /// 95% -> -0.51, 94% -> -0.76, 92% -> -1.25, 91% -> -1.5, and below 91% no
    /// credit at all. The value at 92% deepened from -1 (2026-08-28).
    /// The knee at four fifths of the window is where the old four-point window
    /// ended (2026-08-31). The final point below the bar is continuous
    /// (2026-09-06): 95% keeps its existing -0.5075.
    /// </summary>
    public static readonly (double At, double Offset)[] DAN_CREDIT_BELOW_BAR_ANCHORS =
    {
        (0, 0),
        (0.2, -0.5075),
        (0.8, -1.25),
        (1, -1.5),
    };

    /// <summary>
    /// The 6K/7K LN decay half, over its own three point window (2026-08-31). The
    /// knee at a third of the window is what makes this an extension rather than a
    /// re-pricing. Against the 95% bar: 94.9% -> -0.36, 94.5% -> -0.76,
    /// 94% -> -1.25, 93% -> -1.5, 92% -> -1.75, and below 92% no credit at all.
    /// </summary>
    public static readonly (double At, double Offset)[] DAN_CREDIT_LN_BELOW_BAR_ANCHORS =
    {
        (0, -0.26),
        (1.0 / 3, -1.25),
        (2.0 / 3, -1.5),
        (1, -1.75),
    };

    /// <summary>
    /// 4K LN's own bonus half, keyed in absolute accuracy points over its 97%
    /// ScoreV2 bar rather than in normalized headroom (2026-08-29). The bonus now
    /// tops out at 99.7% and holds that value to 100%:
    /// 97-98% -> +0, 98.5% -> +0.15, 99% -> +0.3, 99.5% -> +0.5, 99.7%+ -> +0.7.
    /// </summary>
    public static readonly (double At, double Offset)[] DAN_CREDIT_4K_LN_ABOVE_BAR_ANCHORS =
    {
        (0, 0),
        (0.01, 0),
        (0.015, 0.15),
        (0.02, 0.3),
        (0.025, 0.5),
        (0.027, 0.7),
    };

    /// <summary>
    /// 4K LN's decay half, over its own 2.5 point window: 97% -> -0.3,
    /// 96.5% -> -0.9, 96% -> -1.06, 95% -> -1.39, 94.5% -> -1.55, and nothing under
    /// that. The bottom is deeper than the other ladders' -1.25 (2026-08-29).
    /// The knee at 96.5% is what keeps the step at the bar small.
    /// </summary>
    public static readonly (double At, double Offset)[] DAN_CREDIT_4K_LN_BELOW_BAR_ANCHORS =
    {
        (0, -0.3),
        (0.2, -0.9),
        (1, -1.55),
    };

    // Both sides of the comparison are decimals, so a pass sitting exactly ON an
    // edge subtracts to a hair under it: 0.92 - 0.96 is -0.040000000000000036,
    // which would fall off the credit window and credit nothing. The tolerance is
    // float slack, not a grace band, so it is a billionth rather than a hundredth.
    public const double CREDIT_EDGE_TOLERANCE = 1e-9;

    private static double InterpolateAnchors((double At, double Offset)[] anchors, double at)
    {
        var first = anchors[0];
        if (at <= first.At) return first.Offset;
        for (int i = 1; i < anchors.Length; i += 1)
        {
            var (upperAt, upperOffset) = anchors[i];
            if (at > upperAt) continue;
            var (lowerAt, lowerOffset) = anchors[i - 1];
            double span = upperAt - lowerAt;
            double t = span > 0 ? (at - lowerAt) / span : 1;
            return lowerOffset + (upperOffset - lowerOffset) * t;
        }
        return anchors[anchors.Length - 1].Offset;
    }

    /// <summary>
    /// The credited level offset for an accuracy against a bar, or null when the
    /// pass is too far under the bar to credit anything. NaN-safe the same way the
    /// old hard gate was: a NaN accuracy fails the window comparison and credits
    /// nothing.
    /// </summary>
    public static double? DanCreditOffset(double accuracy, double bar, DanCreditOptions? options = null)
    {
        options ??= new DanCreditOptions();
        double window = options.BelowBarWindow ?? DAN_CREDIT_BELOW_BAR_WINDOW;
        double delta = accuracy - bar;
        if (!(delta >= -window - CREDIT_EDGE_TOLERANCE)) return null;
        if (delta < -CREDIT_EDGE_TOLERANCE)
        {
            if (options.AllowBelowBar == false) return null;
            var belowBar = options.BelowBar ?? DAN_CREDIT_BELOW_BAR_ANCHORS;
            double s = Math.Min(1, -delta / window);
            double offset = InterpolateAnchors(belowBar, s);
            return Math.Min(offset, -(options.NearBarCap ?? 0));
        }
        var aboveBar = options.AboveBar ?? DAN_CREDIT_ABOVE_BAR_ANCHORS;
        if ((options.AboveBarScale ?? "headroom") == "delta")
        {
            return InterpolateAnchors(aboveBar, Math.Max(0, delta));
        }
        // The bonus always scores against at least the standard 4-point span, not
        // the caller's decay window: narrowing a ladder's window (4K LN) tightens
        // what a near-miss credits without re-heating the bonus v7 cooled, and
        // widening one (rice, 6K/7K LN) does not cool the bonus either.
        double headroom = Math.Max(1 - bar, DAN_CREDIT_BONUS_MIN_SPAN);
        double t = headroom > 0 ? Math.Min(1, Math.Max(0, delta) / headroom) : 1;
        return InterpolateAnchors(aboveBar, t);
    }

    /// <summary>
    /// The ladder-aware near-bar cap. Rice has no cliff. LN's 0.26 is inside the
    /// "-" tier of parseDan and danTableLabelFor. The 4K LN cap is 0.3, a hair
    /// deeper for the same reason.
    /// </summary>
    public static double DanCreditNearBarCapFor(string side, int keyCount)
    {
        if (side == "rc") return 0;
        return keyCount == 4 ? 0.3 : 0.26;
    }

    /// <summary>
    /// Every ladder-aware knob of the chart-clear curve in one place, so the page
    /// that draws the curve and the estimator that credits against it can never
    /// drift apart.
    /// </summary>
    public static DanCreditOptions DanCreditOptionsFor(string side, int keyCount)
    {
        var options = new DanCreditOptions
        {
            NearBarCap = DanCreditNearBarCapFor(side, keyCount),
            BelowBarWindow = DanCreditBelowBarWindowFor(side, keyCount),
        };
        if (side == "ln" && keyCount == 4)
        {
            options.AboveBar = DAN_CREDIT_4K_LN_ABOVE_BAR_ANCHORS;
            options.AboveBarScale = "delta";
            options.BelowBar = DAN_CREDIT_4K_LN_BELOW_BAR_ANCHORS;
        }
        else if (side == "ln")
        {
            options.BelowBar = DAN_CREDIT_LN_BELOW_BAR_ANCHORS;
        }
        return options;
    }

    /// <summary>
    /// A chart clear's credited dan: the chart's rawDan plus the accuracy offset,
    /// clamped to the ladder's floor and ceiling. Null when the accuracy is below
    /// the credit window.
    /// </summary>
    public static double? CreditedDanFor(double chartDan, double accuracy, double bar, string side, int keyCount)
    {
        double? offset = DanCreditOffset(accuracy, bar, DanCreditOptionsFor(side, keyCount));
        if (offset == null) return null;
        double credited = chartDan + offset.Value;
        double? ceiling = DanTables.DanTableCeilingFor(side, keyCount);
        if (ceiling != null) credited = Math.Min(credited, ceiling.Value);
        return Math.Max(credited, DanTables.DanTableFloorFor(side, keyCount));
    }
}
