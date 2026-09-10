// Port of mania-hub live-backend/src/dan/vibro-clear-evidence.ts
namespace LazerSR.DanCalculator.Vibro;

// PORT NOTE: shared/score.ts and shared/types.ts are not ported in this project.
// The pieces vibro-clear-evidence.ts consumes (OsuScoreStatistics, getHitCounts,
// getScoreHitCounts, calculateStableAccuracy) are ported minimally here. The
// classifier-integration agent may hoist OsuScoreStatistics to Types/ if other
// consumers need it.
public sealed class OsuScoreStatistics
{
    public double? count_geki;
    public double? count_300;
    public double? count_katu;
    public double? count_100;
    public double? count_50;
    public double? count_miss;
    public double? perfect;
    public double? great;
    public double? good;
    public double? ok;
    public double? meh;
    public double? miss;
}

public sealed class VibroClearEvidence
{
    public double Version;
    public double StableAccuracy;
    /// <summary>Null denotes a positive MAX count with no possible 300s.</summary>
    public double? Max300Ratio;
    public bool RatioIsLowerBound;
    public double Od;
    /// <summary>Keep exact evidence after the source score ages out.</summary>
    public OsuScoreStatistics? Statistics;
}

public sealed class VibroClearEvidenceSummary
{
    public double Version;
    public double StableAccuracy;
    public double? Max300Ratio;
    public bool RatioIsLowerBound;
    public double Od;
}

public sealed class VibroClearInput
{
    public OsuScoreStatistics? Statistics;
    public double? StableAccuracy;
    public double? CustomAccuracy;
    public double? MissShare;
    public bool WidenedWindows;
}

public static class VibroClearEvidenceModule
{
    private static readonly HashSet<VibroReason> CLEAR_EVIDENCE_PATTERNS = new()
    {
        VibroReason.DenseChordRepetition,
        VibroReason.SustainedChords,
    };

    /// <summary>Pattern gate only; the caller still requires a PP-backed uprate, a clean
    /// base chart, valid structure and qualifying judgement/window evidence.</summary>
    public static bool HasOnlyClearEvidencePatterns(IReadOnlyList<VibroSection> sections)
    {
        return sections.Count > 0 && sections.All(section => section.Reasons.Count > 0
            && section.Reasons.All(reason => CLEAR_EVIDENCE_PATTERNS.Contains(reason)));
    }

    private readonly record struct HitCounts(double CountMax, double Count300, double Count200, double Count100, double Count50, double CountMiss);

    private static HitCounts GetHitCounts(OsuScoreStatistics? stats)
    {
        var safe = stats ?? new OsuScoreStatistics();
        return new HitCounts(
            safe.count_geki ?? safe.perfect ?? 0,
            safe.count_300 ?? safe.great ?? 0,
            safe.count_katu ?? safe.good ?? 0,
            safe.count_100 ?? safe.ok ?? 0,
            safe.count_50 ?? safe.meh ?? 0,
            safe.count_miss ?? safe.miss ?? 0);
    }

    private readonly record struct ScoreHitCounts(double Max, double Great, double Good, double Ok, double Meh, double Miss);

    private static ScoreHitCounts GetScoreHitCounts(OsuScoreStatistics? statistics)
    {
        var counts = GetHitCounts(statistics);
        return new ScoreHitCounts(counts.CountMax, counts.Count300, counts.Count200, counts.Count100, counts.Count50, counts.CountMiss);
    }

    private static double CalculateStableAccuracy(OsuScoreStatistics? stats)
    {
        var counts = GetHitCounts(stats);
        double total = counts.CountMax + counts.Count300 + counts.Count200 + counts.Count100 + counts.Count50 + counts.CountMiss;
        if (total == 0) return 0;
        return (counts.CountMax * 300 + counts.Count300 * 300 + counts.Count200 * 200 + counts.Count100 * 100 + counts.Count50 * 50) / (total * 300);
    }

    private static bool IsInteger(double v) => double.IsFinite(v) && Math.Truncate(v) == v;

    /// <summary>A score-quality exception, not a claim that judgements prove hand technique.
    /// This checks the score evidence only. The caller must also require a
    /// PP-backed uprate of a clean base chart with dense-chord-only detections.
    /// Applies at the player layer only; chart classification never reads this.</summary>
    public static VibroClearEvidence? AssessVibroClear(VibroClearInput input, double od)
    {
        if (!double.IsFinite(od) || od < 9 || input.WidenedWindows) return null;
        var counts = GetScoreHitCounts(input.Statistics ?? new OsuScoreStatistics());
        var values = new[] { counts.Max, counts.Great, counts.Good, counts.Ok, counts.Meh, counts.Miss };
        if (values.Any(value => !IsInteger(value) || value < 0)) return null;
        double total = values.Sum();
        if (total > 0)
        {
            double stableAccuracy = CalculateStableAccuracy(input.Statistics!);
            if (stableAccuracy < 0.95 || counts.Max <= 0 || counts.Max < 2 * counts.Great) return null;
            return new VibroClearEvidence
            {
                Version = 1,
                StableAccuracy = stableAccuracy,
                Od = od,
                Max300Ratio = counts.Great > 0 ? counts.Max / counts.Great : null,
                RatioIsLowerBound = false,
                Statistics = new OsuScoreStatistics
                {
                    perfect = counts.Max,
                    great = counts.Great,
                    good = counts.Good,
                    ok = counts.Ok,
                    meh = counts.Meh,
                    miss = counts.Miss,
                },
            };
        }

        // Older durable plays kept three judgement-derived summaries, not counts.
        // 320*custom - 300*stable = 20*MAX share exactly. Known misses account
        // for their own loss; every other non-MAX/non-300 costs at most 5/6.
        // This gives an UPPER bound on 300 share, hence a LOWER bound on MAX:300.
        // Never substitute displayed accuracy or infer an exact judgement split.
        double? stable = input.StableAccuracy;
        double? custom = input.CustomAccuracy;
        double? misses = input.MissShare;
        if (stable is not double s || custom is not double c || misses is not double m
            || !new[] { s, c, m }.All(value => double.IsFinite(value) && value >= 0 && value <= 1)
            || s < 0.95) return null;
        double maxShare = (320 * c - 300 * s) / 20;
        double loss = 1 - s;
        const double epsilon = 1e-9;
        if (maxShare <= 0 || maxShare > s + epsilon || maxShare + m > 1 + epsilon
            || m > loss + epsilon) return null;
        double otherShareMin = Math.Max(0, (loss - m) / (5.0 / 6));
        double greatShareMax = 1 - maxShare - m - otherShareMin;
        if (greatShareMax < -epsilon || maxShare + epsilon < 2 * Math.Max(0, greatShareMax)) return null;
        return new VibroClearEvidence
        {
            Version = 1,
            StableAccuracy = s,
            Od = od,
            RatioIsLowerBound = true,
            Max300Ratio = greatShareMax > epsilon ? maxShare / greatShareMax : null,
        };
    }

    public static VibroClearEvidenceSummary SummarizeVibroClear(VibroClearEvidence evidence)
    {
        return new VibroClearEvidenceSummary
        {
            Version = evidence.Version,
            StableAccuracy = evidence.StableAccuracy,
            Max300Ratio = evidence.Max300Ratio,
            RatioIsLowerBound = evidence.RatioIsLowerBound,
            Od = evidence.Od,
        };
    }
}
