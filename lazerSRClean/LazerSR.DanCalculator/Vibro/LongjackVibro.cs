// Port of mania-hub live-backend/vendor/leoblack/vibro.js (upstream js/app/vibro.js)
namespace LazerSR.DanCalculator.Vibro;

/// <summary>
/// Minimal surface of the LeoBlack pattern report that vibro.js actually reads.
/// PORT NOTE: P4 owns the real LeoBlackPatternReport (js/patterns/); it should
/// implement this interface, or the integration agent adapts. vibro.js only
/// touches <c>patternReport.Clusters[].SpecificTypes</c> (array of [name, ratio]
/// pairs) and <c>patternReport.Clusters[].BPM</c>.
/// </summary>
public interface ILeoBlackPatternReport
{
    /// <summary>JS: <c>patternReport.Clusters</c>. Null / not-an-array is tolerated (returns false).</summary>
    IReadOnlyList<ILeoBlackVibroCluster>? Clusters { get; }
}

public interface ILeoBlackVibroCluster
{
    /// <summary>JS: <c>Number(cluster.BPM)</c>.</summary>
    double Bpm { get; }

    /// <summary>JS: <c>cluster.SpecificTypes</c> - array of <c>[name, ratio]</c> pairs.
    /// Null / not-an-array is tolerated (cluster skipped).</summary>
    IReadOnlyList<(string Name, double Ratio)>? SpecificTypes { get; }
}

public static class LongjackVibro
{
    // PORT NOTE: P4 owns patterns/config; values copied here
    // (vendor/leoblack/patterns/config.js PATTERNS_CONFIG).
    public const double LONGJACK_VIBRO_RATIO_THRESHOLD = 0.6;
    public const double LONGJACK_VIBRO_MIN_BPM = 180;

    private static double? PickNumber(IReadOnlyDictionary<string, double>? obj, params string[] keys)
    {
        if (obj is null) return null;
        foreach (var key in keys)
        {
            if (obj.TryGetValue(key, out var value) && double.IsFinite(value)) return value;
        }
        return null;
    }

    public static bool DetectVibro(IReadOnlyDictionary<string, double>? values, double threshold)
    {
        var overall = PickNumber(values, "Overall", "overall");
        var jackSpeed = PickNumber(values, "JackSpeed", "Jackspeed", "jackSpeed", "jackspeed");

        if (overall is not double o || !double.IsFinite(o) || o <= 0 || jackSpeed is not double js || !double.IsFinite(js))
        {
            return false;
        }

        return (js / o) >= threshold;
    }

    public static bool DetectVibroFromLongjackPattern(ILeoBlackPatternReport? patternReport, double threshold, double minBpm)
    {
        if (patternReport?.Clusters is null) return false;

        double bpmLimit = double.IsFinite(minBpm) && minBpm > 0 ? minBpm : 0;

        foreach (var cluster in patternReport.Clusters)
        {
            if (cluster.SpecificTypes is null) continue;
            double clusterBpm = cluster.Bpm;
            if (!double.IsFinite(clusterBpm) || clusterBpm < bpmLimit) continue;
            foreach (var (name, ratio) in cluster.SpecificTypes)
            {
                if (name == "Longjacks" && double.IsFinite(ratio) && ratio >= threshold) return true;
            }
        }

        return false;
    }
}
