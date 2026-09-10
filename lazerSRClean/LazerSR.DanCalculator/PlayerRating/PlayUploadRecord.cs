// The per-play record the Hook serialises and uploads. The server bins the
// `clear` into its skillset buckets and runs the windowed averaging
// (weightedDanClearWindow / danFromClears / averageSkillsetDans) — none of which
// is on the client.
//
// PlayUploadRecord.Build ties the ported pieces together, mirroring the
// per-play arm of computePlayerSkillsJob (player-skills.ts ~2740-3045):
//   scoreRewritesChart / inverse-keymode / getPlayRate / daWidensHitWindows  (skip -> null)
//   difficultyAdjustOd -> odOverride;  lnRatio (1 under Invert)
//   ssrGoalForScore(score, lnRatio, odOverride ?? chartOd) -> goal
//   [caller: rate-vibro check -> vibroAdjustment]        (VibroSections / VibroClearEvidence, ported)
//   ratingGoal = vibroAdjustment ? conservativeVibroAccuracy(goal, share) : goal
//   computePlaySsrValues(ratedText, rate, keyCount, ratingGoal, lnRatio)
//   danClearTargetFor -> collectDanClears body (PlayClear)
//
// PORT NOTE: the rate-vibro subsystem (shouldCheckRateVibro / chartVibroAtRate /
// vibroClearEvidence) is ported in VibroSections/VibroClearEvidence but NOT wired
// here — the caller runs it and passes the result in `Vibro`. See BuildInput.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using LazerSR.DanCalculator.Classifier;
using LazerSR.DanCalculator.Credit;
using LazerSR.DanCalculator.Msd;
using LazerSR.DanCalculator.Vibro;
using MsdFacade = LazerSR.DanCalculator.Msd.Msd;

namespace LazerSR.DanCalculator.PlayerRating;

// ── the upload DTO ────────────────────────────────────────────────────────────

public sealed class PlayUploadIdentity
{
    [JsonPropertyName("beatmapMd5")] public string BeatmapMd5 { get; set; } = "";
    [JsonPropertyName("beatmapId")] public long? BeatmapId { get; set; }
    [JsonPropertyName("keyCount")] public int KeyCount { get; set; }
    [JsonPropertyName("rate")] public double Rate { get; set; }
    [JsonPropertyName("mods")] public List<PlayUploadMod> Mods { get; set; } = new();
    [JsonPropertyName("inverse")] public bool Inverse { get; set; }
    [JsonPropertyName("playedAt")] public string? PlayedAt { get; set; }
}

public sealed class PlayUploadMod
{
    [JsonPropertyName("acronym")] public string Acronym { get; set; } = "";
    [JsonPropertyName("settings")] public Dictionary<string, double>? Settings { get; set; }
}

/// <summary>The resolved dan clear (or the rule that stopped it) — the server bins this.</summary>
public sealed class PlayUploadClear
{
    [JsonPropertyName("side")] public string? Side { get; set; }
    [JsonPropertyName("chartRawDan")] public double? ChartRawDan { get; set; }
    [JsonPropertyName("chartDanLabel")] public string? ChartDanLabel { get; set; }
    [JsonPropertyName("stableAccuracy")] public double? StableAccuracy { get; set; }
    [JsonPropertyName("scoreV2Accuracy")] public double? ScoreV2Accuracy { get; set; }
    [JsonPropertyName("usedAccuracy")] public double? UsedAccuracy { get; set; }
    [JsonPropertyName("bar")] public double? Bar { get; set; }
    [JsonPropertyName("currency")] public string? Currency { get; set; }
    [JsonPropertyName("creditedDan")] public double? CreditedDan { get; set; }
    [JsonPropertyName("buckets")] public string[] Buckets { get; set; } = Array.Empty<string>();
    /// <summary>null = credited clear; otherwise the reject reason (camelCase key).</summary>
    [JsonPropertyName("reject")] public string? Reject { get; set; }
    [JsonPropertyName("minAccuracy")] public double? MinAccuracy { get; set; }
    [JsonPropertyName("od")] public double? Od { get; set; }
}

/// <summary>
/// The raw bucketing inputs, so the server can re-bucket on a bucketing-logic
/// change without a client round-trip (mania-hub keeps the same on StoredPlaySsr).
/// </summary>
public sealed class PlayUploadBucketInputs
{
    [JsonPropertyName("msdValues")] public Dictionary<string, double>? MsdValues { get; set; }
    [JsonPropertyName("calcRuns")] public int CalcRuns { get; set; }
    [JsonPropertyName("patterns")] public List<string> Patterns { get; set; } = new();
    [JsonPropertyName("jackDemand")] public bool JackDemand { get; set; }
    [JsonPropertyName("jackShare")] public double? JackShare { get; set; }
    [JsonPropertyName("streamShare")] public double? StreamShare { get; set; }
    [JsonPropertyName("techCategory")] public bool? TechCategory { get; set; }
    [JsonPropertyName("clusterTrill")] public bool? ClusterTrill { get; set; }
    [JsonPropertyName("handstreamCluster")] public bool? HandstreamCluster { get; set; }
    [JsonPropertyName("handstreamEndurance")] public bool HandstreamEndurance { get; set; }
    [JsonPropertyName("techScore")] public double TechScore { get; set; }
    [JsonPropertyName("chordjackScore")] public double ChordjackScore { get; set; }
    [JsonPropertyName("motion")] public LeanMotionFeatures? Motion { get; set; }
    [JsonPropertyName("lengthSeconds")] public double? LengthSeconds { get; set; }
}

public sealed class PlayUploadRecord
{
    [JsonPropertyName("identity")] public PlayUploadIdentity Identity { get; set; } = new();
    [JsonPropertyName("topologyKey")] public string? TopologyKey { get; set; }

    /// <summary>goal &lt;= 0.8 — excluded from the SSR skillset / pattern-axis ranking (still a valid dan clear).</summary>
    [JsonPropertyName("ratingExcluded")] public bool RatingExcluded { get; set; }
    /// <summary>The wife accuracy goal (pre vibro-adjust), for the server's SSR ranking.</summary>
    [JsonPropertyName("goal")] public double Goal { get; set; }

    [JsonPropertyName("clear")] public PlayUploadClear Clear { get; set; } = new();
    [JsonPropertyName("bucketInputs")] public PlayUploadBucketInputs BucketInputs { get; set; } = new();

    /// <summary>
    /// The full classification_json for the played chart at 1.0x. The server
    /// caches it per (chart); omit when the server already has it.
    /// </summary>
    [JsonPropertyName("lean")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LeanChartClassification? Lean { get; set; }
}

// ── the orchestrator ──────────────────────────────────────────────────────────

/// <summary>Caller-supplied inputs for <see cref="PlayUploadRecordBuilder.Build"/>.</summary>
public sealed class PlayUploadBuildInput
{
    /// <summary>Raw 1.0x <c>.osu</c> text of the played chart (the stored file).</summary>
    public string OsuText = "";
    /// <summary>The chart the play is rated against: the same text, or the inverted one under Invert.</summary>
    public string RatedOsuText = "";

    public string BeatmapMd5 = "";
    public long? BeatmapId;
    /// <summary>Effective key count (the key mod's, or the chart's).</summary>
    public int KeyCount;
    public string? PlayedAt;

    public IReadOnlyList<RatingMod>? Mods;
    /// <summary>The score fields the wife goal + eligibility read.</summary>
    public RatingScore Score = new();
    /// <summary>The client's displayed accuracy (lazer = ScoreV2). Null only if the score carries no counts.</summary>
    public double? DisplayedAccuracy;
    /// <summary>ScoreV1 / ScoreV2 accuracy recomputed from the judgement counts (PerformanceDan).</summary>
    public double StableAccuracy;
    public double ScoreV2Accuracy;

    /// <summary>The chart's own OD (parseOsuOd / beatmap DB), or null.</summary>
    public double? ChartOd;
    /// <summary>The chart's drain length in seconds, or null.</summary>
    public double? ChartLengthSeconds;

    /// <summary>Classifier output at 1.0x on the rated chart (LeanClassification.From).</summary>
    public LeanChartClassification BaseLean = new();
    /// <summary>
    /// Classifier output at the play's rate on the rated chart — required for any
    /// non-1.0x play and for Invert / vibro-adjusted plays. Null for a plain 1.0x play.
    /// </summary>
    public LeanChartClassification? AtRateLean;

    /// <summary>The topology key of the rated chart's ManiaBeatmap (ChartFamily), or null.</summary>
    public string? TopologyKey;

    /// <summary>Rate-vibro outcome from the caller (VibroSections/VibroClearEvidence). Optional.</summary>
    public RateVibroResult? Vibro;

    /// <summary>Include the full lean classification in the record (chart is new to the server).</summary>
    public bool IncludeLean = true;
}

/// <summary>The caller's rate-vibro check result (mania-hub <c>chartVibroAtRate</c>).</summary>
public sealed class RateVibroResult
{
    /// <summary>check.vibro === true — the play is a rate-vibro shake; no rating, no clear.</summary>
    public bool RateVibro;
    /// <summary>check.adjustment present — localized vibro removed, accuracy/goal damped.</summary>
    public bool IsAdjusted;
    public double JudgementShare;
    /// <summary>check.clearEvidence present — a vibro-clear-evidence play (target reads the variant verdict).</summary>
    public bool IsClearEvidence;
}

public static class PlayUploadRecordBuilder
{
    private static readonly string[] Skillsets =
        { "Overall", "Stream", "Jumpstream", "Handstream", "Stamina", "JackSpeed", "Chordjack", "Technical" };

    /// <summary>
    /// Build the per-play record, or null when the play is not rated at all
    /// (HO/NR, out-of-band Invert, no single rate, DA that widened the windows,
    /// or a rate-vibro shake).
    /// </summary>
    public static PlayUploadRecord? Build(PlayUploadBuildInput input)
    {
        // ── pre-filters (computePlayerSkillsJob ~2745-2797) ───────────────────
        if (PlayEligibility.ScoreRewritesChart(input.Mods)) return null;

        bool inverse = PlayEligibility.ScoreInvertsChart(input.Mods);
        if (inverse && !PlayEligibility.INVERSE_MOD_KEY_COUNTS.Contains(input.KeyCount)) return null;

        double? rate = PlayEligibility.GetPlayRate(input.Mods);
        if (rate == null) return null;

        double? odOverride = PlayEligibility.DifficultyAdjustOd(input.Mods);
        if (input.ChartOd != null && PlayEligibility.DaWidensHitWindows(odOverride, input.ChartOd))
            return null;

        if (input.Vibro?.RateVibro == true) return null;

        // ── chart info + lnRatio + wife goal ─────────────────────────────────
        var baseInfo = PlayChartInfo.From(
            input.BaseLean,
            chartBaselineMsd: BaselineMsdFor(input),
            storedOd: input.ChartOd,
            lengthSeconds: input.ChartLengthSeconds,
            familyKey: input.TopologyKey);

        double? lnRatio = inverse ? 1 : baseInfo.LnRatio;

        var goalScore = new SsrGoalScore
        {
            Accuracy = input.DisplayedAccuracy ?? input.ScoreV2Accuracy,
            Statistics = input.Score.Statistics,
            Type = input.Score.Type,
            LegacyScoreId = input.Score.LegacyScoreId,
            LegacyTotalScore = input.Score.LegacyTotalScore,
            Mods = input.Mods?.Select(m => m.Acronym).ToList(),
        };
        // computePlayerSkillsJob: `ssrGoalForScore(...) ?? SSR_GOAL_MIN` — the MSD
        // floor never erases a passed score from dan evidence, but MinaCalc never
        // runs at the floor.
        double goal = WifeGoal.SsrGoalForScore(goalScore, lnRatio, odOverride ?? input.ChartOd) ?? 0.8;

        bool isVibroAdjusted = input.Vibro?.IsAdjusted == true;
        double vibroShare = input.Vibro?.JudgementShare ?? 0;
        double ratingGoal = isVibroAdjusted
            ? VibroSections.ConservativeVibroAccuracy(goal, vibroShare)
            : goal;

        bool ratingExcluded = ratingGoal <= 0.8; // SSR_GOAL_MIN

        // ── SSR at goal (4K; null elsewhere / on calc failure) ────────────────
        PlaySsrValues? ssr = ratingExcluded
            ? null
            : PlaySsr.ComputePlaySsrValues(input.RatedOsuText, rate.Value, input.KeyCount, ratingGoal, lnRatio);
        var playSsrVector = ssr != null
            ? new SsrVector(ssr.Values)
            : new SsrVector(new Dictionary<string, double>());

        // ── target + clear ───────────────────────────────────────────────────
        // For a variant play the caller supplies AtRateLean as the classification
        // of the INVERTED / vibro-adjusted chart at the play's rate; for a plain
        // non-1.0x play it is the rated chart at the play's rate.
        bool isVariant = inverse || isVibroAdjusted || input.Vibro?.IsClearEvidence == true;
        var atRateVerdict = RateVerdict.FromPrimary(input.AtRateLean?.Primary);
        var target = DanClearTargetResolver.Resolve(input.KeyCount, rate.Value, isVariant, baseInfo, atRateVerdict);

        var clear = PlayClear.TryBuild(new PlayClearInput
        {
            KeyCount = input.KeyCount,
            Info = baseInfo,
            Target = target,
            OdOverride = odOverride,
            EzWindows = EzWindows(input.Mods),
            StableAccuracy = input.StableAccuracy,
            ScoreV2Accuracy = input.ScoreV2Accuracy,
            DisplayedAccuracy = input.DisplayedAccuracy,
            IsVibroAdjusted = isVibroAdjusted,
            VibroJudgementShare = vibroShare,
            PlaySsr = playSsrVector,
            Rate = rate.Value,
            Inverse = inverse,
        });

        // ── assemble ─────────────────────────────────────────────────────────
        var record = new PlayUploadRecord
        {
            Identity = new PlayUploadIdentity
            {
                BeatmapMd5 = input.BeatmapMd5,
                BeatmapId = input.BeatmapId,
                KeyCount = input.KeyCount,
                Rate = rate.Value,
                Mods = (input.Mods ?? Array.Empty<RatingMod>()).Select(m => new PlayUploadMod
                {
                    Acronym = m.Acronym,
                    Settings = m.Settings?.ToDictionary(kv => kv.Key, kv => kv.Value),
                }).ToList(),
                Inverse = inverse,
                PlayedAt = input.PlayedAt,
            },
            TopologyKey = input.TopologyKey,
            RatingExcluded = ratingExcluded,
            Goal = goal,
            Clear = ToUploadClear(clear),
            BucketInputs = new PlayUploadBucketInputs
            {
                MsdValues = ssr != null ? Skillsets.ToDictionary(s => s, s => playSsrVector.Get(s)) : null,
                CalcRuns = ssr?.CalcRuns ?? 0,
                Patterns = baseInfo.Patterns.ToList(),
                JackDemand = baseInfo.JackDemand,
                JackShare = baseInfo.JackShare,
                StreamShare = baseInfo.StreamShare,
                TechCategory = baseInfo.TechCategory,
                ClusterTrill = baseInfo.ClusterTrill,
                HandstreamCluster = baseInfo.HandstreamCluster,
                HandstreamEndurance = baseInfo.HandstreamEndurance,
                TechScore = baseInfo.TechScore,
                ChordjackScore = baseInfo.ChordjackScore,
                Motion = input.BaseLean.Motion,
                LengthSeconds = baseInfo.LengthSeconds,
            },
            Lean = input.IncludeLean ? input.BaseLean : null,
        };
        return record;
    }

    private static IReadOnlyDictionary<string, double>? BaselineMsdFor(PlayUploadBuildInput input)
    {
        // hasHandstreamEndurance reads the chart's baseline (1.0x, ~0.93) MSD, 4K only.
        if (input.KeyCount != 4) return null;
        try
        {
            return MsdFacade.ComputeMsd(input.RatedOsuText, new MsdOptions { Rate = 1, KeyCount = 4 })?.Values;
        }
        catch
        {
            return null;
        }
    }

    private static bool EzWindows(IReadOnlyList<RatingMod>? mods)
    {
        foreach (var mod in mods ?? Array.Empty<RatingMod>())
            if (mod.Acronym == "EZ") return true;
        return false;
    }

    private static string? RejectKey(DanClearRejectReason r) => r switch
    {
        DanClearRejectReason.ChartUnanalyzed => "chart_unanalyzed",
        DanClearRejectReason.ChartIneligible => "chart_ineligible",
        DanClearRejectReason.ChartVibro => "chart_vibro",
        DanClearRejectReason.RateVibro => "rate_vibro",
        DanClearRejectReason.LowOd => "low_od",
        DanClearRejectReason.EzWindows => "ez_windows",
        DanClearRejectReason.NoAccuracy => "no_accuracy",
        DanClearRejectReason.NoChartDan => "no_chart_dan",
        DanClearRejectReason.BelowBar => "below_bar",
        _ => null,
    };

    private static PlayUploadClear ToUploadClear(PlayClearResult c) => new()
    {
        Side = c.Side,
        ChartRawDan = c.ChartRawDan,
        ChartDanLabel = c.ChartDanLabel,
        StableAccuracy = c.StableAccuracy,
        ScoreV2Accuracy = c.ScoreV2Accuracy,
        UsedAccuracy = c.UsedAccuracy,
        Bar = c.Bar,
        Currency = c.Currency,
        CreditedDan = c.CreditedDan,
        Buckets = c.Buckets,
        Reject = c.Reject == null ? null : RejectKey(c.Reject.Value),
        MinAccuracy = c.MinAccuracy,
        Od = c.Od,
    };
}
