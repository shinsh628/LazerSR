// Port of mania-hub live-backend/src/dan/dan-eligibility.ts

using LazerSR.DanCalculator.Beatmap;

namespace LazerSR.DanCalculator.Classifier;

public sealed class ChartDanEligibility
{
    public bool Eligible;
    /// <summary>"stacked_same_column_heads" or null.</summary>
    public string? Reason;
    public double MaxSameColumnHeadStack;
    public double RedundantSameColumnHeads;
}

public static class DanEligibility
{
    // More than one object on the same column at the same instant still asks for
    // one physical key action. A tiny overlap can be an ordinary mapper mistake,
    // but a large pile is a known star-rating exploit: the difficulty calculator
    // counts stored objects that gameplay effectively collapses. Eight keeps the
    // integrity gate far above an accidental double while catching the abusive
    // piles (the motivating charts carry 190, 1,000 and 1,248 heads).
    public const int DAN_INELIGIBLE_STACKED_HEAD_MIN = 8;

    /// <summary>
    /// Structural player-dan eligibility, deliberately blind to every chart
    /// identity and metadata field. The analyzer may still display its ordinary
    /// dan verdict; this only says whether a play on the chart is trustworthy as
    /// evidence about a player's dan.
    /// </summary>
    public static ChartDanEligibility InspectChartDanEligibility(ManiaBeatmap map)
    {
        var heads = new Dictionary<string, int>();
        foreach (var note in map.Notes)
        {
            string key = $"{note.Column}:{note.Time}";
            heads[key] = heads.GetValueOrDefault(key) + 1;
        }

        double maxSameColumnHeadStack = 0;
        double redundantSameColumnHeads = 0;
        foreach (var count in heads.Values)
        {
            maxSameColumnHeadStack = Math.Max(maxSameColumnHeadStack, count);
            redundantSameColumnHeads += Math.Max(0, count - 1);
        }

        bool eligible = maxSameColumnHeadStack < DAN_INELIGIBLE_STACKED_HEAD_MIN;
        return new ChartDanEligibility
        {
            Eligible = eligible,
            Reason = eligible ? null : "stacked_same_column_heads",
            MaxSameColumnHeadStack = maxSameColumnHeadStack,
            RedundantSameColumnHeads = redundantSameColumnHeads,
        };
    }
}
