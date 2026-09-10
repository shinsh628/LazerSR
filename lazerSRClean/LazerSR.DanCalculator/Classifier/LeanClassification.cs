// Port of mania-hub live-backend/src/features/chart-analysis.ts:
//   interface LeanVerdictHalf / interface LeanChartClassification
//   function leanHalf() / function leanClassification()   (lines ~42–140)
//
// The serialisable per-chart record ("classification_json" in mania-hub's
// beatmap_chart_analysis) that the player-rating server consumes. JSON key names
// match the mania-hub shape verbatim — the server parses this exact JSON
// (loadChartSkillInfo in player-skills.ts reads: patterns[{id,score}],
// clusters[{pattern,importance}], lnRatio, jackDemand.detected, clusterCategory,
// motion, vibro, danEligibility.eligible, rc/ln {rawDan, displayName}).

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using LazerSR.DanCalculator.Beatmap;
using LazerSR.DanCalculator.Features;
using LazerSR.DanCalculator.Patterns;
using LazerSR.DanCalculator.Types;
using LazerSR.DanCalculator.Vibro;

namespace LazerSR.DanCalculator.Classifier;

/// <summary>JS <c>LeanVerdictHalf</c>.</summary>
public sealed class LeanVerdictHalf
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("variant")] public string? Variant { get; set; }
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("rawDan")] public double RawDan { get; set; }
    [JsonPropertyName("estimatedSr")] public double EstimatedSr { get; set; }
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
}

/// <summary>JS <c>leanClassification</c> patterns[] entry.</summary>
public sealed class LeanPatternHit
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
}

/// <summary>JS <c>leanClassification</c> clusters[] entry (LeoBlack cluster, flattened).</summary>
public sealed class LeanCluster
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("pattern")] public string Pattern { get; set; } = "";
    [JsonPropertyName("bpm")] public double Bpm { get; set; }
    [JsonPropertyName("mixed")] public bool Mixed { get; set; }
    [JsonPropertyName("amount")] public double Amount { get; set; }
    [JsonPropertyName("importance")] public double Importance { get; set; }
}

/// <summary>JS <c>FourKeyJackDemandVerdict</c> (enum reasons -> string keys).</summary>
public sealed class LeanJackDemand
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("detected")] public bool Detected { get; set; }
    [JsonPropertyName("reasons")] public List<string> Reasons { get; set; } = new();
}

/// <summary>JS <c>ChartDanEligibility</c>.</summary>
public sealed class LeanDanEligibility
{
    [JsonPropertyName("eligible")] public bool Eligible { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("maxSameColumnHeadStack")] public double MaxSameColumnHeadStack { get; set; }
    [JsonPropertyName("redundantSameColumnHeads")] public double RedundantSameColumnHeads { get; set; }
}

/// <summary>JS <c>VibroSection</c> (original chart timestamps).</summary>
public sealed class LeanVibroSection
{
    [JsonPropertyName("startTime")] public double StartTime { get; set; }
    [JsonPropertyName("endTime")] public double EndTime { get; set; }
    [JsonPropertyName("reasons")] public List<string> Reasons { get; set; } = new();
}

/// <summary>JS <c>VibroAnalysis</c>.</summary>
public sealed class LeanVibroAnalysis
{
    [JsonPropertyName("version")] public double Version { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "clean";
    [JsonPropertyName("sections")] public List<LeanVibroSection> Sections { get; set; } = new();
    [JsonPropertyName("excludedDurationMs")] public double ExcludedDurationMs { get; set; }
    [JsonPropertyName("activeDurationMs")] public double ActiveDurationMs { get; set; }
    [JsonPropertyName("timeShare")] public double TimeShare { get; set; }
    [JsonPropertyName("noteShare")] public double NoteShare { get; set; }
    [JsonPropertyName("judgementShare")] public double JudgementShare { get; set; }
    [JsonPropertyName("remainingNotes")] public double RemainingNotes { get; set; }
}

/// <summary>JS <c>MotionFeatures</c> — 8 shares, 4K only.</summary>
public sealed class LeanMotionFeatures
{
    [JsonPropertyName("sameHand")] public double SameHand { get; set; }
    [JsonPropertyName("miniJack")] public double MiniJack { get; set; }
    [JsonPropertyName("oneHandTrill")] public double OneHandTrill { get; set; }
    [JsonPropertyName("crossHandTrill")] public double CrossHandTrill { get; set; }
    [JsonPropertyName("roll4")] public double Roll4 { get; set; }
    [JsonPropertyName("rhythmBreak")] public double RhythmBreak { get; set; }
    [JsonPropertyName("chordSwing")] public double ChordSwing { get; set; }
    [JsonPropertyName("densitySwing")] public double DensitySwing { get; set; }
}

/// <summary>
/// JS <c>LeanChartClassification</c>. Key order and optionality mirror
/// <c>leanClassification()</c>: <c>motion</c> and <c>vibroAnalysis</c> are omitted
/// when null (JS <c>...(x ? {x} : {})</c>); <c>noteBpm</c> / <c>sunnySr</c> /
/// <c>rc</c> / <c>ln</c> / <c>primary</c> are always emitted, null included.
/// </summary>
public sealed class LeanChartClassification
{
    [JsonPropertyName("noteBpm")] public double? NoteBpm { get; set; }

    [JsonPropertyName("motion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LeanMotionFeatures? Motion { get; set; }

    [JsonPropertyName("keyCount")] public int KeyCount { get; set; }
    [JsonPropertyName("supported")] public bool Supported { get; set; }
    [JsonPropertyName("lnRatio")] public double LnRatio { get; set; }
    [JsonPropertyName("sunnySr")] public double? SunnySr { get; set; }
    [JsonPropertyName("vibro")] public bool Vibro { get; set; }

    [JsonPropertyName("vibroAnalysis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LeanVibroAnalysis? VibroAnalysis { get; set; }

    [JsonPropertyName("danEligibility")] public LeanDanEligibility DanEligibility { get; set; } = new();
    [JsonPropertyName("verdictText")] public string? VerdictText { get; set; }
    [JsonPropertyName("rc")] public LeanVerdictHalf? Rc { get; set; }
    [JsonPropertyName("ln")] public LeanVerdictHalf? Ln { get; set; }
    [JsonPropertyName("primary")] public LeanVerdictHalf? Primary { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }

    /// <summary>Structural 4K Jack demand, used only by player-dan skill buckets.</summary>
    [JsonPropertyName("jackDemand")] public LeanJackDemand JackDemand { get; set; } = new();

    [JsonPropertyName("patterns")] public List<LeanPatternHit> Patterns { get; set; } = new();
    [JsonPropertyName("clusters")] public List<LeanCluster> Clusters { get; set; } = new();
    [JsonPropertyName("clusterCategory")] public string? ClusterCategory { get; set; }
    [JsonPropertyName("modeTag")] public string? ModeTag { get; set; }
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
}

public static class LeanClassification
{
    /// <summary>JS <c>leanHalf</c>.</summary>
    private static LeanVerdictHalf? LeanHalf(DanVerdictHalf? half)
    {
        if (half == null) return null;
        return new LeanVerdictHalf
        {
            Kind = half.Kind,
            Source = half.Source,
            Label = half.Label,
            Variant = half.Variant,
            DisplayName = half.DisplayName,
            RawDan = half.RawDan,
            EstimatedSr = half.EstimatedSr,
            Confidence = half.Confidence,
        };
    }

    private static string VibroStatusKey(VibroStatus status) => status switch
    {
        VibroStatus.Adjusted => "adjusted",
        VibroStatus.Excluded => "excluded",
        _ => "clean",
    };

    private static LeanVibroAnalysis LeanVibro(VibroAnalysis v) => new()
    {
        Version = v.Version,
        Status = VibroStatusKey(v.Status),
        Sections = v.Sections.Select(s => new LeanVibroSection
        {
            StartTime = s.StartTime,
            EndTime = s.EndTime,
            Reasons = s.Reasons.Select(r => r.ToString().ToLowerInvariant()).ToList(),
        }).ToList(),
        ExcludedDurationMs = v.ExcludedDurationMs,
        ActiveDurationMs = v.ActiveDurationMs,
        TimeShare = v.TimeShare,
        NoteShare = v.NoteShare,
        JudgementShare = v.JudgementShare,
        RemainingNotes = v.RemainingNotes,
    };

    private static LeanMotionFeatures LeanMotion(MotionFeatures m) => new()
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

    /// <summary>
    /// JS <c>leanClassification(classification, noteBpm, motion)</c>. The cluster
    /// array is built once and passed to the jack-demand verdict, exactly as
    /// upstream does.
    /// </summary>
    public static LeanChartClassification From(
        ChartClassification classification,
        double? noteBpm = null,
        MotionFeatures? motion = null)
    {
        var clusters = (classification.Clusters?.TopFiveClusters ?? new List<LeoBlackPatternCluster>())
            .Select(cluster => new LeanCluster
            {
                Label = cluster.Format(1),
                Pattern = cluster.Pattern,
                Bpm = cluster.BPM,
                Mixed = cluster.Mixed,
                Amount = cluster.Amount,
                Importance = cluster.Importance,
            })
            .ToList();

        var jackDemand = JackDemand.ClassifyFourKeyJackDemand(new JackDemand.FourKeyJackDemandInput
        {
            KeyCount = classification.KeyCount,
            Metrics = classification.Patterns.Metrics,
            Patterns = classification.Patterns.AllPatterns
                .Select(hit => new JackDemand.FourKeyJackDemandPattern { Id = hit.Id, Score = hit.Score })
                .ToList(),
            Clusters = clusters
                .Select(c => new JackDemand.FourKeyJackDemandCluster
                {
                    Label = c.Label,
                    Pattern = c.Pattern,
                    Bpm = c.Bpm,
                    Importance = c.Importance,
                })
                .ToList(),
        });

        var eligibility = classification.DanEligibility;

        return new LeanChartClassification
        {
            NoteBpm = noteBpm,
            Motion = motion == null ? null : LeanMotion(motion),
            KeyCount = classification.KeyCount,
            Supported = classification.Supported,
            LnRatio = classification.LnRatio,
            SunnySr = classification.SunnySr,
            Vibro = classification.Vibro,
            VibroAnalysis = classification.VibroAnalysis == null ? null : LeanVibro(classification.VibroAnalysis),
            DanEligibility = new LeanDanEligibility
            {
                Eligible = eligibility.Eligible,
                Reason = eligibility.Reason,
                MaxSameColumnHeadStack = eligibility.MaxSameColumnHeadStack,
                RedundantSameColumnHeads = eligibility.RedundantSameColumnHeads,
            },
            VerdictText = classification.VerdictText,
            Rc = LeanHalf(classification.Rc),
            Ln = LeanHalf(classification.Ln),
            Primary = LeanHalf(classification.Primary),
            Category = classification.Patterns.Primary?.Label,
            JackDemand = new LeanJackDemand
            {
                Version = jackDemand.Version,
                Detected = jackDemand.Detected,
                Reasons = jackDemand.Reasons.Select(r => r.ToKey()).ToList(),
            },
            Patterns = classification.Patterns.Patterns.Select(hit => new LeanPatternHit
            {
                Id = hit.Id.ToKey(),
                Label = hit.Label,
                Score = hit.Score,
                Confidence = hit.Confidence,
            }).ToList(),
            Clusters = clusters,
            ClusterCategory = classification.Clusters?.Report.Category,
            ModeTag = classification.Clusters?.Report.ModeTag,
            Warnings = new List<string>(classification.Warnings),
        };
    }
}
