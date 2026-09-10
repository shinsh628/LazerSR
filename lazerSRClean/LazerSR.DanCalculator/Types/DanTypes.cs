// Port of mania-hub live-backend/src/dan/dan-estimator/types.ts
// Shared type vocabulary for the whole dan subsystem. Fixed contract: every
// agent implements against these names.

using LazerSR.DanCalculator.Beatmap;

namespace LazerSR.DanCalculator.Types;

public enum DanSkillFamily { Jack, Stream, Jumpstream, Handstream, Stamina, Chordjack, Tech, Ln, Dan }

public static class DanSkillFamilyExtensions
{
    // Serialised/debug names must match the TS string union exactly.
    public static string ToKey(this DanSkillFamily f) => f switch
    {
        DanSkillFamily.Jack => "jack",
        DanSkillFamily.Stream => "stream",
        DanSkillFamily.Jumpstream => "jumpstream",
        DanSkillFamily.Handstream => "handstream",
        DanSkillFamily.Stamina => "stamina",
        DanSkillFamily.Chordjack => "chordjack",
        DanSkillFamily.Tech => "tech",
        DanSkillFamily.Ln => "ln",
        DanSkillFamily.Dan => "dan",
        _ => "dan",
    };

    public static DanSkillFamily FromKey(string s) => s switch
    {
        "jack" => DanSkillFamily.Jack,
        "stream" => DanSkillFamily.Stream,
        "jumpstream" => DanSkillFamily.Jumpstream,
        "handstream" => DanSkillFamily.Handstream,
        "stamina" => DanSkillFamily.Stamina,
        "chordjack" => DanSkillFamily.Chordjack,
        "tech" => DanSkillFamily.Tech,
        "ln" => DanSkillFamily.Ln,
        _ => DanSkillFamily.Dan,
    };
}

public static class DanFamilies
{
    /// <summary>DanPrimaryFamily = everything except ln/dan.</summary>
    public static readonly DanSkillFamily[] Primary =
    {
        DanSkillFamily.Jack, DanSkillFamily.Stream, DanSkillFamily.Jumpstream,
        DanSkillFamily.Handstream, DanSkillFamily.Stamina, DanSkillFamily.Chordjack, DanSkillFamily.Tech,
    };

    public static readonly DanSkillFamily[] All =
    {
        DanSkillFamily.Jack, DanSkillFamily.Stream, DanSkillFamily.Jumpstream, DanSkillFamily.Handstream,
        DanSkillFamily.Stamina, DanSkillFamily.Chordjack, DanSkillFamily.Tech, DanSkillFamily.Ln, DanSkillFamily.Dan,
    };
}

/// <summary>Record&lt;DanSkillFamily, number&gt; -> a fixed 9-slot map with key access.</summary>
public sealed class SkillScores
{
    private readonly Dictionary<DanSkillFamily, double> map = new();

    public double this[DanSkillFamily f]
    {
        get => map.GetValueOrDefault(f);
        set => map[f] = value;
    }

    public SkillScores Clone()
    {
        var c = new SkillScores();
        foreach (var (k, v) in map) c.map[k] = v;
        return c;
    }

    public IReadOnlyDictionary<DanSkillFamily, double> AsDictionary() => map;
}

// PORT NOTE: unsealed so Classifier.ClassifyChartInput can `extends DanEstimateInput`
// (chart-classifier.ts). No behaviour change for existing consumers.
public class DanEstimateInput
{
    public double? StarRating;
    public double? TotalLength;
    public string? Title;
    public string? Version;
    public double? Rate;
}

public sealed class DanFeatureMetrics
{
    public int KeyCount;
    public double NoteCount;
    public double DurationMs;
    public double HoldRatio;
    public double ChordRatio;
    public double TwoNoteChordRatio;
    public double PeakNps1s;
    public double PeakNps5s;
    public double Nps5sP50;
    public double Nps5sP90;
    public double Nps5sP95;
    public double SustainedNps10s;
    public double SustainedNps30s;
    public double SustainedNps60s;
    public double ActiveNps;
    public double LongGapRatio;
    public double LongGapCount;
    public double JackPressure;
    public double StreamPressure;
    public double JumpstreamPressure;
    public double ChordjackPressure;
    public double ChordColumnOverlapRatio;
    public double AdjacentColumnRehitShare;
    public double TwoBackColumnRehitShare;
    public double TwoBackColumnRehitExcess;
    public double TechPressure;
    public double RowBurstPressure;
    public double FastRowRatio;
    public double RowIntervalEntropy;
    public double OffGridRowShare;
    public double PatternVariety;
    public double RowPatternEntropy;
    public double RowPatternVariety;
    public double RepeatedRowPatternRatio;
    public double AlternatingRowPatternRatio;
    public double RowPatternChangeRate;
    public double RowMotifRepeatRatio;
    public double RhythmMotifRepeatRatio;
    public double AdjacentMotifRepeatRatio;
    public double StrainSpikiness;
    public double SustainedPressureRatio;
    public double AnchorPressure;
    public double LnReleasePressure;
    public double LnDensity;
    public double LnOverlapPressure;
    public double LnChordPressure;
    public double LnHoldDurationAvg;
    public double LnHoldDurationP90;
    public double ChordSizeChangeRate;
    public double DirectionChangeRate;
    public double StaminaPressure;
}

public sealed class DanScoreContribution
{
    public string Id = "";
    public double Value;
    public string Description = "";
}

public sealed class DanScoringDebug
{
    public double DensitySr;
    public double StaminaSr;
    public double StructuralSr;
    public double Base;
    public double LnNerf;
    public Dictionary<string, double> Gates = new();
    public Dictionary<string, double> Terms = new();
    public Dictionary<DanSkillFamily, List<DanScoreContribution>> Contributions = new();
}

public sealed class DanFamilyChoiceDebug
{
    public DanSkillFamily TopFamily;
    public double TopScore;
    public DanSkillFamily SelectedFamily;
    public string Reason = "";
}

public sealed class DanEstimateDebug
{
    public DanScoringDebug Scoring = new();
    public DanFamilyChoiceDebug FamilyChoice = new();
}

public sealed class DanEstimate
{
    public string Label = "";
    public string? Variant;
    public string DisplayName = "";
    public double RawDan;
    public double EstimatedSr;
    public DanSkillFamily Family;
    public double Confidence;
    public DanFeatureMetrics Metrics = new();
    public SkillScores SkillScores = new();
    public List<string> Warnings = new();
    public DanEstimateDebug? Debug;
}

public sealed class DanFeatureExtractionResult
{
    public List<ManiaNote> Notes = new();
    public List<double> NoteTimes = new();
    public double DurationMs;
    /// <summary>orderedRows: Array&lt;[number, ManiaNote[]]&gt; (row time, notes on that row).</summary>
    public List<(double Time, List<ManiaNote> Notes)> OrderedRows = new();
    public DanFeatureMetrics Metrics = new();
    public List<string> Warnings = new();
}

public enum ManiaPatternId
{
    Jack, Chordjack, Speedjack, Handjack, Tech, Stream, Dumpstream, Jumpstream, Handstream,
    Quadstream, Delay, Bracket, Chordstream, Ln, Lngeneral, Lnrelease, Lninverse, Lntech,
}

public static class ManiaPatternIdExtensions
{
    public static string ToKey(this ManiaPatternId id) => id.ToString().ToLowerInvariant();
}

public sealed class ManiaPatternHit
{
    public ManiaPatternId Id;
    public string Label = "";
    public double Score;
    public double Confidence;
    public string Evidence = "";
}

public sealed class ManiaPatternAnalysis
{
    public int KeyCount;
    public ManiaPatternHit? Primary;
    public List<ManiaPatternHit> Patterns = new();
    public List<ManiaPatternHit> AllPatterns = new();
    public DanFeatureMetrics Metrics = new();
    public List<string> Warnings = new();
}
