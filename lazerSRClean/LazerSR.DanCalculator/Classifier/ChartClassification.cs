// Port of the public type surface of mania-hub live-backend/src/dan/chart-classifier.ts
//
// ChartClassification / ClassifyChartInput / DanVerdictHalf / DanVerdictSource.

using LazerSR.DanCalculator.Estimators;
using LazerSR.DanCalculator.Patterns;
using LazerSR.DanCalculator.Types;
using LazerSR.DanCalculator.Vibro;

namespace LazerSR.DanCalculator.Classifier;

/// <summary>
/// JS union: "leoblack-mixed" | "leoblack-companella" | "leoblack-sunny-table" | "inhouse-ln-knn".
/// </summary>
public static class DanVerdictSource
{
    public const string LeoBlackMixed = "leoblack-mixed";
    public const string LeoBlackCompanella = "leoblack-companella";
    public const string LeoBlackSunnyTable = "leoblack-sunny-table";
    public const string InhouseLnKnn = "inhouse-ln-knn";
}

public sealed class DanVerdictHalf
{
    /// <summary>"rc" | "ln".</summary>
    public string Kind = "";
    /// <summary>One of the <see cref="DanVerdictSource"/> constants.</summary>
    public string Source = "";
    public string Label = "";
    public string? Variant;
    public string DisplayName = "";
    public double RawDan;
    public double EstimatedSr;
    public double Confidence;
    /// <summary>"below" | "above" | null.</summary>
    public string? Boundary;
    /// <summary>Verbatim engine output this half was derived from.</summary>
    public string Raw = "";
}

public sealed class ChartClassification
{
    public int KeyCount;
    /// <summary>True when at least one dan verdict exists (4/6/7K charts).</summary>
    public bool Supported;
    public double LnRatio;
    public double? SunnySr;
    /// <summary>Raw LeoBlack Mixed verdict text ("RC || LN" for hybrids), if it ran.</summary>
    public string? VerdictText;
    public DanVerdictHalf? Rc;
    public DanVerdictHalf? Ln;
    public DanVerdictHalf? Primary;
    /// <summary>The primary verdict as a DanEstimate (benchmark / dan_estimates shape).</summary>
    public DanEstimate? Estimate;
    public ManiaPatternAnalysis Patterns = new();
    public PatternAnalysisResult? Clusters;
    public bool Vibro;
    public VibroAnalysis? VibroAnalysis;
    /// <summary>Whether this chart may testify toward a player's dan.</summary>
    public ChartDanEligibility DanEligibility = new();
    /// <summary>
    /// True when Mixed wanted Companella for the RC half but none was supplied,
    /// so the verdict is still the Sunny fallback. Re-running through
    /// classifyChartWithCompanella resolves it.
    /// </summary>
    public bool CompanellaPending;
    public List<string> Warnings = new();
    /// <summary>
    /// Roxy's 7-axis structural breakdown (speed/handStream/jack/chordjack/tech/
    /// stamina/course), each axis's share of the raw structural signal plus an
    /// approximate per-axis raw-dan-equivalent. Non-null ONLY when Roxy actually
    /// won the routing for this chart's RC verdict (4K RC, high enough difficulty
    /// to clear Roxy's scope — lower-difficulty 4K RC routes to Azusa instead and
    /// this stays null). Display-only ranking signal; not a real per-axis dan.
    /// </summary>
    public List<RoxyAxisContribution>? RoxyAxes;
    /// <summary>
    /// LeoBlack pattern-window types (Stream/Chordstream/Jacks core, RC scope
    /// only — Coordination/Density/Wildcard excluded) joined against Roxy's
    /// 400ms section-difficulty curve: each pattern's share of the chart's time
    /// span, plus its typical section value relative to the chart's own peak
    /// section (0..1). Non-null ONLY under the same Roxy-won-routing condition
    /// as <see cref="RoxyAxes"/>. This — not RoxyAxes — is the "which named
    /// pattern, and how much does it matter" signal (2026-09-15).
    /// </summary>
    public List<RoxyPatternDifficulty>? RoxyPatterns;
}

/// <summary>One LeoBlack pattern type's time-share + relative difficulty within
/// one chart. <see cref="Pattern"/> is the core category (Stream/Chordstream/
/// Jacks); <see cref="SpecificType"/> is the display name (e.g. "Trills",
/// "Chordjacks") — falls back to Pattern when no specific type won.</summary>
public sealed class RoxyPatternDifficulty
{
    public string Pattern = "";
    public string SpecificType = "";
    /// <summary>0..1+ share of the chart's time span this pattern covers
    /// (union of its windows' intervals, not a raw duration sum).</summary>
    public double TimeShare;
    /// <summary>0..1: this pattern's typical Roxy section value divided by the
    /// chart's own peak section value — "how close to this chart's hardest
    /// moment" rather than any cross-chart dan-scale comparison.</summary>
    public double RelativeIntensity;
}

/// <summary>One axis of Roxy's structural breakdown. <see cref="Axis"/> is the
/// internal Roxy stream name ("speed"/"handStream"/"jack"/"chordjack"/"tech"/
/// "stamina"/"course") — display naming is a UI concern, not this type's.</summary>
public sealed class RoxyAxisContribution
{
    public string Axis = "";
    /// <summary>0..1 share of Roxy's weighted structural aggregate.</summary>
    public double Share;
    /// <summary>Approximate raw-dan-equivalent if this axis alone drove the chart.</summary>
    public double LocalRawDan;
}

/// <summary>JS `ClassifyChartInput extends DanEstimateInput`.</summary>
public class ClassifyChartInput : DanEstimateInput
{
    /// <summary>Player-rating policy only. Ordinary chart estimates always rate all notes.</summary>
    public bool AdjustVibro;

    /// <summary>
    /// Which half becomes the primary verdict; "auto" picks LN at the keymode's
    /// identity line (lnPrimaryMinRatioFor). "rc" | "ln" | "auto".
    /// </summary>
    public string? PreferFamily;

    /// <summary>
    /// Companella verdict for the RC half, when the caller has already run the
    /// model. Ignored on charts Mixed did not ask for it.
    /// </summary>
    public CompanellaEstimate? Companella;

    /// <summary>Raw MSD at the requested rate, before LN-tail blending.</summary>
    public Dictionary<string, double>? MarathonMsdValues;

    public ClassifyChartInput Clone() => new()
    {
        StarRating = StarRating,
        TotalLength = TotalLength,
        Title = Title,
        Version = Version,
        Rate = Rate,
        AdjustVibro = AdjustVibro,
        PreferFamily = PreferFamily,
        Companella = Companella,
        MarathonMsdValues = MarathonMsdValues,
    };
}
