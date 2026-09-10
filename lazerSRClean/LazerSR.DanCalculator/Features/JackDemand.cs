// Port of mania-hub live-backend/src/dan/jack-demand.ts
//
// Player-dan tiles answer which specialist a clear demonstrates, not merely
// which MinaCalc label won. MinaCalc deliberately suppresses anchored rows and
// consequently files several community-Jack 4K shapes under Technical or
// Jumpstream. This verdict supplies the missing structural override.
//
// It is intentionally separate from the chart's dan family/rating: none of
// these rules changes what a chart is rated, only which player skill bucket a
// clear supplies evidence for.

using LazerSR.DanCalculator.Types;

namespace LazerSR.DanCalculator.Features;

public static class JackDemand
{
    public const int FOUR_KEY_JACK_DEMAND_VERSION = 1;

    public enum FourKeyJackDemandReason
    {
        DenseAlternatingChords,
        JackClusterDominant,
        JackClusterCorroborated,
        JackMarathon,
    }

    public static string ToKey(this FourKeyJackDemandReason reason) => reason switch
    {
        FourKeyJackDemandReason.DenseAlternatingChords => "dense_alternating_chords",
        FourKeyJackDemandReason.JackClusterDominant => "jack_cluster_dominant",
        FourKeyJackDemandReason.JackClusterCorroborated => "jack_cluster_corroborated",
        FourKeyJackDemandReason.JackMarathon => "jack_marathon",
        _ => "",
    };

    public sealed class FourKeyJackDemandVerdict
    {
        public int Version;
        public bool Detected;
        public List<FourKeyJackDemandReason> Reasons = new();
    }

    public sealed class FourKeyJackDemandCluster
    {
        public string Label = "";
        public string Pattern = "";
        public double Bpm;
        public double Importance;
    }

    public sealed class FourKeyJackDemandPattern
    {
        public ManiaPatternId Id;
        public double Score;
    }

    public sealed class FourKeyJackDemandInput
    {
        public int KeyCount;
        // Only durationMs / chordRatio / chordColumnOverlapRatio /
        // twoBackColumnRehitExcess / jackPressure are read.
        public DanFeatureMetrics Metrics = new();
        // Use allPatterns, not the stored top-five surface. A secondary jack signal
        // must not disappear merely because five other hybrid labels scored higher.
        public List<FourKeyJackDemandPattern> Patterns = new();
        public List<FourKeyJackDemandCluster> Clusters = new();
    }

    private const double DENSE_CHORD_RATIO_MIN = 0.70;
    private const double DENSE_CHORD_OVERLAP_MIN = 0.58;
    private const double TWO_BACK_REHIT_EXCESS_MIN = 0.30;

    // A repeated column is jack demand only while a finger can be asked to re-tap
    // it. One row of 1/4 at this BPM is ~65ms, so a two-row reload lands ~130ms
    // apart; faster than that the same shape has to be spread across two fingers,
    // which is the speed/tech demand rather than the jack one. Measured: the jack
    // marathons carry importance-weighted cluster BPM 159-198, against 277 for a
    // speed-pack file whose every other jack signal reads stronger than all of
    // them (jack 1.00, jack pressure at the cap).
    private const double JACKABLE_MAX_CLUSTER_BPM = 230;
    private const double JACK_CLUSTER_SHARE_MIN = 0.60;
    // Below that share the clusters alone are not enough: half the corpus carries
    // some jack cluster. A quarter of the chart's importance counts when the
    // in-house chordjack detector independently reaches half-confidence and the
    // notes carry real jack pressure, which is two detectors agreeing rather than
    // one threshold lowered. Measured: it moves 4 more charts in 897, none in the
    // tech, speed, stream or handstream packs.
    private const double JACK_CLUSTER_CORROBORATED_SHARE_MIN = 0.25;
    private const double JACK_CLUSTER_CORROBORATED_CHORDJACK_MIN = 0.5;
    private const double JACK_CLUSTER_CORROBORATED_PRESSURE_MIN = 150;
    /// <summary>LeoBlack's family name for minijack, chordjack and longjack clusters.</summary>
    private const string JACK_CLUSTER_PATTERN = "Jacks";
    private const double MARATHON_DURATION_MIN_MS = 240_000;
    private const double MARATHON_JACK_SCORE_MIN = 0.75;
    private const double MARATHON_JACK_PRESSURE_MIN = 175;
    private const double MARATHON_CHORD_RATIO_MIN = 0.45;
    private const double MARATHON_CHORD_OVERLAP_MIN = 0.55;

    private static double PatternScore(List<FourKeyJackDemandPattern> patterns, ManiaPatternId id)
    {
        double score = 0;
        foreach (var pattern in patterns)
        {
            if (pattern.Id != id) continue;
            double value = pattern.Score;
            if (double.IsFinite(value)) score = Math.Max(score, value);
        }
        return score;
    }

    /// <summary>Share of cluster importance carried by jack clusters slow enough to jack.</summary>
    private static double JackClusterImportanceShare(List<FourKeyJackDemandCluster> clusters)
    {
        double total = 0;
        double jack = 0;
        foreach (var cluster in clusters)
        {
            double importance = cluster.Importance;
            if (!double.IsFinite(importance) || importance <= 0) continue;
            total += importance;
            double bpm = cluster.Bpm;
            if (!double.IsFinite(bpm) || bpm <= 0 || bpm > JACKABLE_MAX_CLUSTER_BPM) continue;
            if (cluster.Pattern == JACK_CLUSTER_PATTERN) jack += importance;
        }
        return total > 0 ? jack / total : 0;
    }

    /// <summary>Importance-weighted mean cluster BPM, or null when no cluster carries one.</summary>
    private static double? WeightedMeanClusterBpm(List<FourKeyJackDemandCluster> clusters)
    {
        double weight = 0;
        double weighted = 0;
        foreach (var cluster in clusters)
        {
            double importance = cluster.Importance;
            double bpm = cluster.Bpm;
            if (!double.IsFinite(importance) || importance <= 0) continue;
            if (!double.IsFinite(bpm) || bpm <= 0) continue;
            weight += importance;
            weighted += importance * bpm;
        }
        return weight > 0 ? weighted / weight : (double?)null;
    }

    /// <summary>
    /// Identity-blind 4K Jack demand missed by MinaCalc's JackSpeed/Chordjack
    /// argmax. Three arms cover distinct community-Jack shapes (see source comment).
    /// </summary>
    public static FourKeyJackDemandVerdict ClassifyFourKeyJackDemand(FourKeyJackDemandInput input)
    {
        if (input.KeyCount != 4)
        {
            return new FourKeyJackDemandVerdict { Version = FOUR_KEY_JACK_DEMAND_VERSION, Detected = false, Reasons = new() };
        }

        var metrics = input.Metrics;
        var reasons = new List<FourKeyJackDemandReason>();
        bool denseAlternatingChords = metrics.ChordRatio >= DENSE_CHORD_RATIO_MIN
            && metrics.ChordColumnOverlapRatio >= DENSE_CHORD_OVERLAP_MIN
            && metrics.TwoBackColumnRehitExcess >= TWO_BACK_REHIT_EXCESS_MIN;
        if (denseAlternatingChords) reasons.Add(FourKeyJackDemandReason.DenseAlternatingChords);

        // A chart with no clusters at all keeps the older behaviour rather than
        // losing the arm: the gate is there to reject fast files, not unread ones.
        double? meanClusterBpm = WeightedMeanClusterBpm(input.Clusters);
        bool jackableSpeed = meanClusterBpm == null || meanClusterBpm.Value <= JACKABLE_MAX_CLUSTER_BPM;

        double jackClusterShare = JackClusterImportanceShare(input.Clusters);
        bool jackClusterDominant = jackableSpeed
            && meanClusterBpm != null
            && jackClusterShare >= JACK_CLUSTER_SHARE_MIN;
        if (jackClusterDominant) reasons.Add(FourKeyJackDemandReason.JackClusterDominant);

        bool jackClusterCorroborated = !jackClusterDominant
            && jackableSpeed
            && meanClusterBpm != null
            && jackClusterShare >= JACK_CLUSTER_CORROBORATED_SHARE_MIN
            && PatternScore(input.Patterns, ManiaPatternId.Chordjack) >= JACK_CLUSTER_CORROBORATED_CHORDJACK_MIN
            && metrics.JackPressure >= JACK_CLUSTER_CORROBORATED_PRESSURE_MIN;
        if (jackClusterCorroborated) reasons.Add(FourKeyJackDemandReason.JackClusterCorroborated);
        bool jackMarathon = jackableSpeed
            && metrics.DurationMs >= MARATHON_DURATION_MIN_MS
            && PatternScore(input.Patterns, ManiaPatternId.Jack) >= MARATHON_JACK_SCORE_MIN
            && metrics.JackPressure >= MARATHON_JACK_PRESSURE_MIN
            && metrics.ChordRatio >= MARATHON_CHORD_RATIO_MIN
            && metrics.ChordColumnOverlapRatio >= MARATHON_CHORD_OVERLAP_MIN;
        if (jackMarathon) reasons.Add(FourKeyJackDemandReason.JackMarathon);

        return new FourKeyJackDemandVerdict
        {
            Version = FOUR_KEY_JACK_DEMAND_VERSION,
            Detected = reasons.Count > 0,
            Reasons = reasons,
        };
    }
}
