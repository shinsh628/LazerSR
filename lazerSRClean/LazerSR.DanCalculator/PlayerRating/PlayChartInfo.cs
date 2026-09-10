// Port of the per-row body of mania-hub's loadChartSkillInfo
// (live-backend/src/features/player-skills.ts ~1650-1722) — how one chart's stored
// classification_json + msd_json + dan sweep columns become a `ChartSkillInfo`.
//
// mania-hub does this server-side over a DB read; on the client we build it from
// the LeanChartClassification the classifier just produced, the chart's baseline
// MSD (4K only), its stored OD and length, and the topology-key family. The dan
// sweep columns (dtRawDan/htRawDan/…) are NOT carried: the client resolves the
// at-rate verdict from a fresh classification at the play's rate (DanClearTarget).

using System;
using System.Collections.Generic;
using System.Linq;
using LazerSR.DanCalculator.Classifier;
using LazerSR.DanCalculator.Features;

namespace LazerSR.DanCalculator.PlayerRating;

/// <summary>
/// The <c>ChartSkillInfo</c> fields the player-rating clear rules and bucket walk
/// read. Server-side type in mania-hub; assembled on the client by
/// <see cref="From"/>.
/// </summary>
public sealed class PlayChartInfo
{
    /// <summary>chart-families family key (v1 == topology key); null when no comparable notes.</summary>
    public string? ChartFamily;

    /// <summary>Analyzer tag ids that passed <c>patternTagMinScore</c> (+ derived "jack" for 6/7/8K).</summary>
    public IReadOnlyList<string> Patterns = Array.Empty<string>();

    public bool JackDemand;
    public double? JackShare;
    public double? StreamShare;
    public bool? TechCategory;
    public bool? ClusterTrill;
    public bool? HandstreamCluster;
    public bool HandstreamEndurance;
    public double TechScore;
    public double ChordjackScore;
    public MotionFeatures? Motion;

    public double? LnRatio;
    public bool Vibro;
    /// <summary>classification_json.danEligibility.eligible !== false (legacy rows default eligible).</summary>
    public bool DanEligible = true;

    public double? RcRawDan;
    public double? LnRawDan;
    public string? RcDanLabel;
    public string? LnDanLabel;

    public double? Od;
    public double? LengthSeconds;
    public int? KeyCount;

    // ── loadChartSkillInfo readers (player-skills.ts) ──────────────────────────

    /// <summary>player-skills.ts readRawDan — a finite, positive rawDan or null.</summary>
    private static double? ReadRawDan(LeanVerdictHalf? half)
    {
        if (half == null) return null;
        double v = half.RawDan;
        return double.IsFinite(v) && v > 0 ? v : (double?)null;
    }

    /// <summary>player-skills.ts readDanLabel — a non-blank displayName or null.</summary>
    private static string? ReadDanLabel(string? value)
        => !string.IsNullOrWhiteSpace(value) ? value!.Trim() : null;

    /// <summary>player-skills.ts readStoredOd — a finite OD in [0,10] or null.</summary>
    public static double? ReadStoredOd(double? value)
    {
        if (value == null) return null;
        double od = value.Value;
        return double.IsFinite(od) && od >= 0 && od <= 10 ? od : (double?)null;
    }

    /// <summary>player-skills.ts readLengthSeconds — Math.round(seconds), positive, or null.</summary>
    public static double? ReadLengthSeconds(double? value)
    {
        if (value == null) return null;
        double seconds = Math.Floor(value.Value + 0.5); // JS Math.round
        return double.IsFinite(seconds) && seconds > 0 ? seconds : (double?)null;
    }

    /// <summary>player-skills.ts LN_PATTERN_IDS.</summary>
    private static readonly HashSet<string> LnPatternIds = new()
    {
        "ln", "lngeneral", "lnrelease", "lninverse", "lntech",
    };

    /// <summary>
    /// mania-hub loadChartSkillInfo — build the info row from a lean classification.
    /// </summary>
    /// <param name="lean">the classifier output at 1.0x (classification_json)</param>
    /// <param name="chartBaselineMsd">
    /// the chart's baseline MSD skillset values (Msd.ComputeMsd at rate 1, goal ~0.93).
    /// 4K only; null elsewhere. Read only by <c>hasHandstreamEndurance</c>.
    /// </param>
    /// <param name="storedOd">chart OD as the beatmap DB stores it (readStoredOd)</param>
    /// <param name="lengthSeconds">chart drain length in seconds (total_length)</param>
    /// <param name="familyKey">topology-key family, or null</param>
    public static PlayChartInfo From(
        LeanChartClassification lean,
        IReadOnlyDictionary<string, double>? chartBaselineMsd,
        double? storedOd,
        double? lengthSeconds,
        string? familyKey)
    {
        int? keyCount = lean.KeyCount > 0 ? lean.KeyCount : (int?)null;

        // patternScores: max score per id (mania-hub folds duplicate hits).
        var patternScores = new Dictionary<string, double>();
        foreach (var hit in lean.Patterns)
        {
            if (string.IsNullOrEmpty(hit.Id)) continue;
            double prev = patternScores.TryGetValue(hit.Id, out var p) ? p : 0.0;
            patternScores[hit.Id] = Math.Max(prev, hit.Score);
        }

        double chordjackScore = patternScores.GetValueOrDefault("chordjack", 0.0);
        double jackScore = patternScores.GetValueOrDefault("jack", 0.0);

        var clusterPairs = lean.Clusters.Select(c => (c.Pattern, c.Importance)).ToList();
        double? jackShare = DanBuckets.JackShare(clusterPairs);
        double? streamShare = DanBuckets.StreamShare(clusterPairs);

        bool isJack = DanBuckets.ChartIsJack(keyCount, chordjackScore, jackScore, jackShare);
        bool vetoesTech = DanBuckets.JackVetoesTech(keyCount, chordjackScore, jackScore, jackShare);

        double rawLnRatio = lean.LnRatio;
        double? lnRatio = double.IsFinite(rawLnRatio) ? Math.Max(0, Math.Min(1, rawLnRatio)) : (double?)null;
        // A chart whose analysis carries no lnRatio cannot be verified as LN.
        bool chartIsLn = lnRatio != null && lnRatio.Value >= LnDan.LnPrimaryMinRatioFor(keyCount);

        var patternIds = patternScores
            .Where(kv => kv.Value >= DanBuckets.PatternTagMinScoreFor(kv.Key)
                         && !(kv.Key == "tech" && vetoesTech)
                         && !(LnPatternIds.Contains(kv.Key) && !chartIsLn))
            .Select(kv => kv.Key)
            .ToList();

        // Derived whole-jack tag for the pattern-axis keymodes (chartIsJack).
        if (keyCount != null && DanBuckets.UsesPatternSkillAxes(keyCount.Value) && isJack && !patternIds.Contains("jack"))
            patternIds.Add("jack");

        string? clusterCategory = lean.ClusterCategory;
        var motion = FromLeanMotion(lean.Motion);

        return new PlayChartInfo
        {
            ChartFamily = familyKey,
            Patterns = patternIds,
            JackDemand = lean.JackDemand.Detected,
            JackShare = jackShare,
            StreamShare = streamShare,
            TechCategory = DanBuckets.TechCategoryFor(clusterCategory),
            ClusterTrill = DanBuckets.ClusterTrillFor(clusterCategory),
            HandstreamCluster = DanBuckets.HandstreamClusterFor(clusterCategory),
            HandstreamEndurance = keyCount == 4 && !chartIsLn
                && DanBuckets.HasHandstreamEndurance(chartBaselineMsd == null ? null : new SsrVector(chartBaselineMsd)),
            TechScore = vetoesTech ? 0 : patternScores.GetValueOrDefault("tech", 0.0),
            ChordjackScore = chordjackScore,
            Motion = motion,
            LnRatio = lnRatio,
            Vibro = lean.Vibro,
            DanEligible = lean.DanEligibility.Eligible != false,
            RcRawDan = ReadRawDan(lean.Rc),
            LnRawDan = ReadRawDan(lean.Ln),
            RcDanLabel = ReadDanLabel(lean.Rc?.DisplayName),
            LnDanLabel = ReadDanLabel(lean.Ln?.DisplayName),
            Od = storedOd,
            LengthSeconds = ReadLengthSeconds(lengthSeconds),
            KeyCount = keyCount,
        };
    }

    private static MotionFeatures? FromLeanMotion(LeanMotionFeatures? m)
    {
        if (m == null) return null;
        return new MotionFeatures
        {
            SameHand = m.SameHand,
            MiniJack = m.MiniJack,
            OneHandTrill = m.OneHandTrill,
            CrossHandTrill = m.CrossHandTrill,
            Roll4 = m.Roll4,
            RhythmBreak = m.RhythmBreak,
            ChordSwing = m.ChordSwing,
            DensitySwing = m.DensitySwing,
        };
    }

    /// <summary>Projection to the bucket walk's input DTO (<see cref="DanChartInfo"/>).</summary>
    public DanChartInfo ToDanChartInfo() => new()
    {
        Patterns = Patterns,
        JackDemand = JackDemand,
        JackShare = JackShare,
        StreamShare = StreamShare,
        TechCategory = TechCategory,
        ClusterTrill = ClusterTrill,
        HandstreamCluster = HandstreamCluster,
        HandstreamEndurance = HandstreamEndurance,
        TechScore = TechScore,
        ChordjackScore = ChordjackScore,
        Motion = Motion,
        LengthSeconds = LengthSeconds,
        KeyCount = KeyCount,
    };
}
