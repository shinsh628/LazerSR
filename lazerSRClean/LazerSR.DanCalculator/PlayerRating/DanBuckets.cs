// Port of mania-hub live-backend/src/features/player-skills.ts — the dan
// SKILLSET-BUCKET assignment only (which of a keymode/side's tiles a clear
// files under). The window/stray/average aggregation over those buckets is
// server-side and NOT ported here.
//
// This is a parity port: every constant, near-tie band and logistic weight is
// copied verbatim with its calibration comment, because those numbers ARE the
// algorithm. Chinese calibration comments (none in this range) would be kept.
//
// Ported (line refs into player-skills.ts):
//   SKILL_RATING_SKILLSETS (143), PATTERN_AXIS_KEY_COUNTS/usesPatternSkillAxes (168),
//   PATTERN_TAG_MIN_SCORE/CHORDJACK_TAG_MIN_SCORE/DELAY_TAG_MIN_SCORE (178-213),
//   chartBelongsToTagBucket (216), patternTagMinScore (252),
//   chartIsJack (283), JACK_TECH_VETO_MIN_SCORE/jackVetoesTech (305),
//   clusterShare/JACK_CLUSTERS/STREAM_CLUSTERS/CLUSTER_SHARE_MIN (1383-1417),
//   TECH_CLUSTER_CATEGORY (1437), TRILL_CLUSTER_CATEGORY (1473),
//   HANDSTREAM_CLUSTER_CATEGORY (1476), TRILL_JACK_* (1498-1514),
//   TRILL_RUNNER_UP_MIN_LENGTH_SECONDS (1529), trillIsJack (1537),
//   STAMINA_TILE_JACK_VETO_SHARE (1569), readMotionFeatures (1576),
//   inverseModChartInfo/INVERSE_MOD_PATTERNS (927/948),
//   danSkillsetBuckets (4312), danSkillsetBucketIds (4351),
//   bucketsForClear (4365), resolveTilesForClear (4442),
//   danSkillsetBucketsForPlay (4525), SPEED_NEAR_TIE_MSD (4899),
//   TECH_NEAR_TIE_MIN_SCORE (4920), TECH_NEAR_TIE_MSD_LEAD (4952),
//   SPEED_TECH_MODEL/SPEED_TECH_DUAL_* (4987-5019), speedTechProbability (5027),
//   speedTechTiles (5056), STAMINA_TILE_MIN_LENGTH_SECONDS (5086),
//   STAMINA_HOLD_BASE_BAND/STAMINA_HOLD_RIVALS/staminaHoldRival (5120-5126),
//   HANDSTREAM_NEAR_TIE_MSD (5150), jackContaminated (5153),
//   RICE_MSD_SKILLSETS (5159), BASE_MSD_SKILLSETS (5164),
//   hasHandstreamEndurance (5169), jumpstreamRunnerUp (5181),
//   bucketingSkillset (5211), enduranceSeconds (5310), pickSkillsets (5317),
//   danSkillsetBucketsForValues (5333), dominantSkillset (5499).
//
// NOT ported (server / other WP): danFromClears, weightedDanClearWindow,
//   danIgnoredStrayCount, averageSkillsetDans, anchoredSkillsetDans,
//   collectDanClears, groupDanClearsBySkillset, loadChartSkillInfo (WP-E builds
//   DanChartInfo), the patternIds tag-score filter (WP-E), rate-edit base lengths.

using System.Text.RegularExpressions;
using LazerSR.DanCalculator.Classifier;
using LazerSR.DanCalculator.Features;

namespace LazerSR.DanCalculator.PlayerRating;

/// <summary>
/// JS <c>Record&lt;string, number&gt;</c> — an MSD/SSR vector keyed by the eight
/// <see cref="DanBuckets.SkillRatingSkillsets"/> names ("Overall", "Stream", ...,
/// "JackSpeed", "Chordjack", "Technical"). Missing key reads as 0, mirroring
/// <c>Number(values?.[key] ?? 0)</c>.
/// </summary>
public interface ISsrVector
{
    /// <summary>Skillset value by mania-hub name, or 0 when absent (never null).</summary>
    double Get(string skillset);
}

/// <summary>
/// JS <c>ChartSkillInfo</c> — the subset of stored chart analysis the bucket walk
/// reads (player-skills.ts:1294). WP-E / the server populates this from the lean
/// classification + chart MSD; the derivations are documented on each field.
/// </summary>
public sealed class DanChartInfo
{
    /// <summary>Analyzer pattern tag ids that survived <c>patternTagMinScore</c> (+ derived "jack" for 6K/7K).</summary>
    public IReadOnlyList<string> Patterns = System.Array.Empty<string>();

    /// <summary>classification_json.jackDemand.detected === true (jack-demand.ts).</summary>
    public bool JackDemand;

    /// <summary><c>clusterShare(clusters, /jack/i)</c>; null when the chart has no clusters.</summary>
    public double? JackShare;

    /// <summary><c>clusterShare(clusters, /stream/i)</c>; null when the chart has no clusters.</summary>
    public double? StreamShare;

    /// <summary><c>clusterCategory ? /tech/i.test(clusterCategory) : null</c>.</summary>
    public bool? TechCategory;

    /// <summary><c>clusterCategory (non-empty) ? /trill/i.test(clusterCategory) : null</c>.</summary>
    public bool? ClusterTrill;

    /// <summary><c>clusterCategory (non-empty) ? /handstream/i.test(clusterCategory) : null</c>.</summary>
    public bool? HandstreamCluster;

    /// <summary><c>keyCount === 4 &amp;&amp; !chartIsLn &amp;&amp; hasHandstreamEndurance(chartMsd.values)</c>.</summary>
    public bool HandstreamEndurance;

    /// <summary>Analyzer raw tech score at 1.0x, zeroed when <c>jackVetoesTech</c> strips the tech tag.</summary>
    public double TechScore;

    /// <summary>Analyzer raw chordjack score, unzeroed.</summary>
    public double ChordjackScore;

    /// <summary>Wrist-vs-roll note shares at 1.0x (motion-features.ts), 4K only; null = no reading.</summary>
    public MotionFeatures? Motion;

    /// <summary>Chart drain length at 1.0x in seconds; null when unknown.</summary>
    public double? LengthSeconds;

    /// <summary>Analysis row key count (for the Invert-support gate). Optional.</summary>
    public int? KeyCount;
}

/// <summary>JS <c>DanSkillsetBucket</c> (player-skills.ts:4181).</summary>
public sealed class DanSkillsetBucket
{
    public required string Id;

    /// <summary>Analyzer pattern tags that put a clear in this bucket.</summary>
    public string[] Tags = System.Array.Empty<string>();

    /// <summary>MSD skillsets that put a clear here by the play's strongest skillset (4K only).</summary>
    public string[]? Skillsets;

    /// <summary>Take the bucket from a LeoBlack cluster share instead of <c>Tags</c>: "jack" | "stream" | "tech".</summary>
    public string? ClusterFamily;

    /// <summary>The side's anchor tile — the headline follows it (anchoredSkillsetDans, server-side).</summary>
    public bool Anchor;
}

public static class DanBuckets
{
    // ── skillset vocabulary ────────────────────────────────────────────────
    public static readonly string[] SkillRatingSkillsets =
    {
        "Overall", "Stream", "Jumpstream", "Handstream", "Stamina", "JackSpeed", "Chordjack", "Technical",
    };

    // Keymodes whose skill card, percentiles and leaderboards speak the in-house
    // pattern vocabulary (chordstream, bracket, delay, ...) instead of MinaCalc's
    // skillsets. 6K and 7K are the two where the pattern detector was validated
    // against mapper-named packs and the calc's 4K-born names mislead; 8K joined
    // them on 2026-09-02 because its charts are 7K's vocabulary (often mapped as
    // 7K+1). The pattern ratings are still computed and stored for every keymode;
    // this only decides what is published, plus the derived whole-jack tag
    // (chartIsJack) those keymodes need for their Jack axis.
    public static readonly IReadOnlySet<int> PatternAxisKeyCounts = new HashSet<int> { 6, 7, 8 };

    public static bool UsesPatternSkillAxes(int keyCount) => PatternAxisKeyCounts.Contains(keyCount);

    // A pattern rating from one or two plays is an anecdote, not a rating.
    // (Consumed by the server aggregation; kept here beside the tag bars.)
    public const int PatternRatingMinPlays = 3;

    // Chart analysis stores every detected pattern down to trace hits, so common
    // tags (ln, tech) land on nearly every chart and would aggregate to a copy of
    // Overall. A chart only counts toward a pattern it is meaningfully made of.
    public const double PatternTagMinScore = 0.5;

    // Chordjack needs a higher bar than the rest. The detector reads dense 7K
    // chordstream as chordjack often enough to matter: measured against 131 charts
    // from 23 mapper-named 7K jack packs and 100 from 15 named stream/chordstream
    // packs, the 0.5 bar tagged 95% of the real jack but also 13 of the 100 stream
    // charts. At 0.8 that becomes 87% of real jack against only 3 stream charts.
    public const double ChordjackTagMinScore = 0.8;

    // Delay needs a LOWER bar than the rest: it under-fires on the charts it is
    // meant to name. Delay is the only signal behind the 6K/7K speed tile
    // (Jinjin's 3rd dan skill). At 0.25 a named-speed corpus reaches 96% while the
    // stream corpus drops from 58% to 1%. Nothing scores delay between 0 and 0.25.
    public const double DelayTagMinScore = 0.25;

    /// <summary>The score a pattern must reach to tag a chart, per tag.</summary>
    public static double PatternTagMinScoreFor(string patternId)
    {
        if (patternId == "chordjack") return ChordjackTagMinScore;
        if (patternId == "delay") return DelayTagMinScore;
        return PatternTagMinScore;
    }

    // LeoBlack's cluster vocabulary is six names (Chordstream, Jacks, Stream,
    // Wildcard, Density, Coordination); these pick the two the tiles read.
    private static readonly Regex JackClusters = new("jack", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StreamClusters = new("stream", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Tech is a suffix on the headline label ("Light Chordstream Tech"), naming
    // the whole chart rather than a family inside it.
    private static readonly Regex TechClusterCategory = new("tech", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A trill label sends a 4K Jumpstream argmax to tech (arbitration below).
    private static readonly Regex TrillClusterCategory = new("trill", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The same label read for handstream, for the near-tie.
    private static readonly Regex HandstreamClusterCategory = new("handstream", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A chart belongs to a tile when that family carries this much of its
    // difficulty. Set against 131 charts from 23 mapper-named 7K jack packs and
    // 100 from 15 named stream/chordstream packs. Jack: 0.4 keeps 118/131 real
    // jack and admits 0 of 100 stream. Stream: every cutoff 0.3-0.6 catches
    // 100/100, so it shares the jack number.
    public const double ClusterShareMin = 0.4;

    // The single-note score vetoes tech only at its own near-certainty bar, not
    // the 0.5 tag line. Unambiguous CJ diffs (chordjack >= 0.8) carried a false
    // tech tag 75-88% of the time.
    private const double JackTechVetoMinScore = 0.8;

    // The stamina jack-veto share: too jack to be endurance, not enough to be
    // jack outright. Past the 95th percentile of both mapper-named handstream
    // (p95 0.280) and jumpstream (p95 0.284) packs, and under CLUSTER_SHARE_MIN.
    public const double StaminaTileJackVetoShare = 0.30;

    // 4K Jumpstream-argmax arbitration: 0.60 set on the labelled 240BPM+ jumptrill
    // pair NANO DEATH!!!!! (0.71) / QZKago Requiem NYARMAGEDDON (0.65), read as
    // jack, vs the Blastix Riotz family (0.50/0.57/0.23), read as tech.
    public const double TrillJackMinChordjack = 0.60;

    // The corroborated arm — a lighter trill files jack when jack clusters back
    // it. 0.55 is under both labelled jack files; 0.15 share is under both and
    // clear of Blastix Riotz [GRAVITY] (0.114) and Villain Virus (0.08).
    public const double TrillJackCorroboratedChordjack = 0.55;
    public const double TrillJackCorroboratedShare = 0.15;

    // Past the two arms above a trill is not a jack demand and does not keep tech
    // either: on a file long enough for endurance the runner-up decides. Same
    // 4:00 as STAMINA_TILE_MIN_LENGTH_SECONDS, written out to avoid the forward
    // reference.
    public const double TrillRunnerUpMinLengthSeconds = 240;

    // ── cluster-share ──────────────────────────────────────────────────────

    /// <summary>
    /// How much of a chart's difficulty is jack (or stream), from LeoBlack's
    /// pattern clusters. Importance is amount x difficulty, so this asks "is the
    /// jack the real content" rather than "is there jack". Null when no clusters.
    /// (player-skills.ts:1383, <c>clusterShare</c>.)
    /// </summary>
    public static double? ClusterShare(IReadOnlyList<(string Pattern, double Importance)>? clusters, Regex pattern)
    {
        if (clusters == null) return null;
        double total = 0;
        double matched = 0;
        foreach (var cluster in clusters)
        {
            double importance = cluster.Importance;
            if (!double.IsFinite(importance) || importance <= 0) continue;
            total += importance;
            if (pattern.IsMatch(cluster.Pattern ?? "")) matched += importance;
        }
        return total > 0 ? matched / total : (double?)null;
    }

    /// <summary><c>clusterShare(clusters, JACK_CLUSTERS)</c>.</summary>
    public static double? JackShare(IReadOnlyList<(string Pattern, double Importance)>? clusters)
        => ClusterShare(clusters, JackClusters);

    /// <summary><c>clusterShare(clusters, STREAM_CLUSTERS)</c>.</summary>
    public static double? StreamShare(IReadOnlyList<(string Pattern, double Importance)>? clusters)
        => ClusterShare(clusters, StreamClusters);

    /// <summary><c>/tech/i.test(clusterCategory)</c> when the label is present, else null.</summary>
    public static bool? TechCategoryFor(string? clusterCategory)
        => clusterCategory != null ? TechClusterCategory.IsMatch(clusterCategory) : (bool?)null;

    /// <summary><c>/trill/i.test(clusterCategory)</c> when the label is a non-empty string, else null.</summary>
    public static bool? ClusterTrillFor(string? clusterCategory)
        => clusterCategory != null && clusterCategory.Trim().Length > 0
            ? TrillClusterCategory.IsMatch(clusterCategory)
            : (bool?)null;

    /// <summary><c>/handstream/i.test(clusterCategory)</c> when the label is a non-empty string, else null.</summary>
    public static bool? HandstreamClusterFor(string? clusterCategory)
        => clusterCategory != null && clusterCategory.Trim().Length > 0
            ? HandstreamClusterCategory.IsMatch(clusterCategory)
            : (bool?)null;

    // ── jack reads ─────────────────────────────────────────────────────────

    /// <summary>
    /// The jack tile takes chord jack and single-note jack alike. 4K keeps the old
    /// rule (chordjack certainty alone); 6K/7K/8K OR the analyzer's single-note
    /// jack tag with LeoBlack's jack cluster share. (player-skills.ts:283.)
    /// </summary>
    public static bool ChartIsJack(int? keyCount, double chordjackScore, double jackScore, double? jackShare)
    {
        if (keyCount == null || !UsesPatternSkillAxes(keyCount.Value)) return chordjackScore >= ChordjackTagMinScore;
        if (jackScore >= PatternTagMinScore) return true;
        return jackShare != null
            ? jackShare.Value >= ClusterShareMin
            : chordjackScore >= ChordjackTagMinScore;
    }

    /// <summary>
    /// A jack chart never counts as a tech chart, even when its tech score clears
    /// the bar. 4K: chordjack certainty. 6K/7K/8K: single-note jack at its own
    /// near-certainty bar (0.8), or the jack cluster share, or chordjack
    /// certainty when no clusters exist. (player-skills.ts:306.)
    /// </summary>
    public static bool JackVetoesTech(int? keyCount, double chordjackScore, double jackScore, double? jackShare)
    {
        if (keyCount == null || !UsesPatternSkillAxes(keyCount.Value)) return chordjackScore >= ChordjackTagMinScore;
        if (jackScore >= JackTechVetoMinScore) return true;
        return jackShare != null
            ? jackShare.Value >= ClusterShareMin
            : chordjackScore >= ChordjackTagMinScore;
    }

    /// <summary>Whether LeoBlack's jack clusters carry too much of a chart to call it endurance. (player-skills.ts:1153.)</summary>
    public static bool JackContaminated(double? jackShare)
        => jackShare != null && jackShare.Value >= StaminaTileJackVetoShare;

    /// <summary>
    /// Whether a trill-labelled chart's wrist demand reads as jack. Runs ahead of
    /// the MSD argmax. (player-skills.ts:1537, <c>trillIsJack</c>.)
    /// </summary>
    public static bool TrillIsJack(DanChartInfo? chart)
    {
        if (chart?.ClusterTrill != true) return false;
        if (chart.ChordjackScore >= TrillJackMinChordjack) return true;
        return chart.ChordjackScore >= TrillJackCorroboratedChordjack
            && (chart.JackShare ?? 0) >= TrillJackCorroboratedShare;
    }

    // ── tag-bucket membership ──────────────────────────────────────────────

    /// <summary>Whether chart analysis places a chart in a bucket's tag/cluster arm. (player-skills.ts:216.)</summary>
    public static bool ChartBelongsToTagBucket(DanSkillsetBucket bucket, DanChartInfo? chart)
    {
        // Tech is a label rather than a share: LeoBlack names the whole chart.
        if (bucket.ClusterFamily == "tech")
        {
            if (chart?.TechCategory != null) return chart.TechCategory.Value;
        }
        else if (bucket.ClusterFamily != null)
        {
            double? share = bucket.ClusterFamily == "jack" ? chart?.JackShare : chart?.StreamShare;
            if (share != null) return share.Value >= ClusterShareMin;
        }
        var patterns = chart?.Patterns ?? System.Array.Empty<string>();
        foreach (var tag in patterns)
            if (System.Array.IndexOf(bucket.Tags, tag) >= 0) return true;
        return false;
    }

    // ── SSR-vector helpers ─────────────────────────────────────────────────
    // JS `Number(values?.[skillset] ?? 0)` — missing key => 0; a stored NaN stays NaN.

    private static double Num(ISsrVector? values, string skillset) => values?.Get(skillset) ?? 0.0;

    /// <summary>JS <c>dominantSkillset</c> (player-skills.ts:5499) — strongest skillset except Overall.</summary>
    public static string? DominantSkillset(ISsrVector? values)
    {
        string? best = null;
        double bestValue = 0;
        foreach (var skillset in SkillRatingSkillsets)
        {
            if (skillset == "Overall") continue;
            double value = Num(values, skillset);
            if (double.IsFinite(value) && value > bestValue)
            {
                best = skillset;
                bestValue = value;
            }
        }
        return best;
    }

    /// <summary>JS <c>pickSkillsets</c> (player-skills.ts:5317) — a copy of an SSR vector holding only the named skillsets.</summary>
    public static ISsrVector PickSkillsets(ISsrVector? values, IReadOnlyList<string> keep)
    {
        var picked = new Dictionary<string, double>();
        foreach (var skillset in keep)
        {
            double value = Num(values, skillset);
            if (double.IsFinite(value) && value > 0) picked[skillset] = value;
        }
        return new DictSsrVector(picked);
    }

    // Everything that is not one of the two endurance readings (for a chart whose
    // jack share disqualifies both).
    private static readonly string[] RiceMsdSkillsets =
        SkillRatingSkillsets.Where(s => s != "Overall" && s != "Stamina" && s != "Handstream").ToArray();

    // The base skillsets, i.e. everything the stamina rider rides on top of.
    private static readonly string[] BaseMsdSkillsets =
        SkillRatingSkillsets.Where(s => s != "Overall" && s != "Stamina").ToArray();

    /// <summary>Stamina first and Handstream second, excluding Overall. (player-skills.ts:5169.)</summary>
    public static bool HasHandstreamEndurance(ISsrVector? values)
        => DominantSkillset(values) == "Stamina"
            && DominantSkillset(PickSkillsets(values, BaseMsdSkillsets)) == "Handstream";

    // The skillsets that can stand in for Stream in the stamina hold.
    private static readonly string[] StaminaHoldRivals = { "Technical", "Jumpstream", "Handstream" };

    private static double StaminaHoldRival(ISsrVector? values)
        => StaminaHoldRivals.Select(s => Num(values, s)).Max();

    // How far the strongest OTHER base skillset may sit under Stream and still let
    // a length-qualified Stamina argmax hold the tile. Demiourgos [4K]: 6:27 at
    // 274 BPM Stamina 29.18 / Stream 29.05 / Technical 28.71, filed speed because
    // Technical missed Stream by a third of a point. 0.5 of MSD is the line.
    private const double StaminaHoldBaseBand = 0.5;

    // MinaCalc's Stamina is a rider: a Stamina argmax says the file is long, so it
    // keeps the tile only at 4:00+. Kept as its own constant rather than imported.
    private const double StaminaTileMinLengthSeconds = 240;

    // How far under the top skillset Handstream may sit and still call the chart
    // handstream, on a chart LeoBlack's headline label names handstream. Ceiling
    // is Matusa Bomber [4K] 1.25 (Handstream gap 0.99 on a chart players call
    // tech); 0.95 is as much of the Hold Angel correction as fits under it.
    private const double HandstreamNearTieMsd = 0.95;

    /// <summary>
    /// The strongest skillset other than Jumpstream, which picks the tile for a
    /// Jumpstream argmax LeoBlack's label cannot resolve. A jack-contaminated
    /// chart cannot pick an endurance runner-up. Null falls back to Jumpstream
    /// itself (the legacy tech pairing). (player-skills.ts:5181.)
    /// </summary>
    public static string JumpstreamRunnerUp(ISsrVector? values, bool contaminated)
    {
        var pool = SkillRatingSkillsets.Where(skillset => skillset != "Overall"
            && skillset != "Jumpstream"
            && !(contaminated && (skillset == "Stamina" || skillset == "Handstream"))).ToArray();
        return DominantSkillset(PickSkillsets(values, pool)) ?? "Jumpstream";
    }

    /// <summary>
    /// How long a play asks for, in seconds: the LONGER of the chart's 1.0x drain
    /// and the time the play actually lasted at its rate. Null when unknown.
    /// (player-skills.ts:5310, <c>enduranceSeconds</c>.)
    /// </summary>
    public static double? EnduranceSeconds(double? lengthSeconds, double rate)
    {
        if (lengthSeconds == null) return null;
        double played = lengthSeconds.Value / (double.IsFinite(rate) && rate > 0 ? rate : 1);
        return System.Math.Max(lengthSeconds.Value, played);
    }

    // ── 4K speed / tech logistic model (player-skills.ts:4987) ─────────────
    //
    // Standardisation constants are the fitted corpus mean and standard deviation
    // per input; they are part of the model and move only with a refit.
    private const double SpeedTechBias = -0.5869;

    private static readonly (double Mean, double Sd, double Weight)[] SpeedTechTerms =
    {
        (0.0439, 0.0659, 0.7207),   // rhythmBreak
        (0.0551, 0.0456, 0.9009),   // crossHandTrill
        (0.0044, 0.0065, 0.5595),   // miniJack
        (0.2300, 0.0534, 0.2510),   // sameHand
        (-0.3886, 0.7180, 0.1252),  // Technical - Stream
        (0.3927, 0.1981, 1.2100),   // analyzer tech score
    };

    // Where the model stops claiming a single answer and the chart carries both
    // tiles. Not symmetric around 0.5: the tech side of the band is wider.
    private const double SpeedTechDualLow = 0.35;
    private const double SpeedTechDualHigh = 0.75;

    /// <summary>
    /// How strongly the notes and the ratings read a chart as tech rather than
    /// speed, in [0, 1]. Null when the chart has no stored motion block.
    /// (player-skills.ts:5027, <c>speedTechProbability</c>.)
    /// </summary>
    public static double? SpeedTechProbability(ISsrVector? values, MotionFeatures? motion, double techScore)
    {
        if (motion == null) return null;
        double[] inputs =
        {
            motion.RhythmBreak,
            motion.CrossHandTrill,
            motion.MiniJack,
            motion.SameHand,
            Num(values, "Technical") - Num(values, "Stream"),
            techScore,
        };
        double z = SpeedTechBias;
        for (int index = 0; index < inputs.Length; index++)
        {
            var term = SpeedTechTerms[index];
            double input = inputs[index];
            if (!double.IsFinite(input)) return null;
            z += term.Weight * ((input - term.Mean) / term.Sd);
        }
        return 1 / (1 + System.Math.Exp(-z));
    }

    /// <summary>
    /// The tiles a near-tied speed/tech chart belongs to: one when the model is
    /// sure, both when it is not. Null when there is nothing to read.
    /// (player-skills.ts:5056, <c>speedTechTiles</c>.)
    /// </summary>
    public static (string Primary, bool Shared)? SpeedTechTiles(ISsrVector? values, MotionFeatures? motion, double techScore)
    {
        double? probability = SpeedTechProbability(values, motion, techScore);
        if (probability == null) return null;
        return (
            probability.Value >= 0.5 ? "tech" : "speed",
            probability.Value > SpeedTechDualLow && probability.Value < SpeedTechDualHigh);
    }

    // ── near-tie bands (player-skills.ts:4899) ─────────────────────────────

    // Stream wins from within this of the top. 1.25 buys one point of speed
    // recall for five of tech precision; every Blastix diff stays tech.
    private const double SpeedNearTieMsd = 1.25;

    // A would-be speed verdict moves to tech when Technical is also within
    // SPEED_NEAR_TIE_MSD of Stream AND the chart's raw tech score clears this bar.
    private const double TechNearTieMinScore = 0.8;

    // The low-tech-score arm: a would-be speed verdict moves to tech when
    // Technical OUTRANKS Stream by this much and the chart wears the tech tag.
    private const double TechNearTieMsdLead = 0.35;

    /// <summary>
    /// The skillset a play is filed under. (player-skills.ts:5211,
    /// <c>bucketingSkillset</c>.) See the source for the full case rationale.
    /// </summary>
    public static string? BucketingSkillset(
        ISsrVector? values,
        double? lengthSeconds = null,
        double rate = 1,
        double chartTechScore = 0,
        double? chartJackShare = null,
        bool chartHandstreamCluster = false)
    {
        string? top = DominantSkillset(values);
        if (top == null) return top;
        // Stamina first with Handstream second is endurance evidence even on a
        // short practice cut. Preserve it before Stream's near-tie can replace it.
        if (HasHandstreamEndurance(values) && !JackContaminated(chartJackShare)) return "Handstream";
        double stream = Num(values, "Stream");
        double? endurance = EnduranceSeconds(lengthSeconds, rate);
        bool demandsEndurance = endurance != null && endurance.Value >= StaminaTileMinLengthSeconds;
        // A length-qualified Stamina argmax holds the tile before the speed
        // near-tie can reach it, as long as SOME base skillset stays within
        // STAMINA_HOLD_BASE_BAND of Stream.
        if (top == "Stamina" && demandsEndurance
            && StaminaHoldRival(values) >= stream - StaminaHoldBaseBand
            && !JackContaminated(chartJackShare)) return top;
        double best = Num(values, top);
        // Handstream wins a near-tie the same way Stream does, on a chart LeoBlack
        // itself reads as handstream.
        if (top != "Handstream" && chartHandstreamCluster && !JackContaminated(chartJackShare))
        {
            double handstream = Num(values, "Handstream");
            if (handstream > 0 && handstream >= best - HandstreamNearTieMsd) return "Handstream";
        }
        string nearTie = top == "Stream" || (stream > 0 && stream >= best - SpeedNearTieMsd)
            ? "Stream"
            : top;
        if (nearTie == "Stream")
        {
            double technical = Num(values, "Technical");
            bool techBacked = chartTechScore >= TechNearTieMinScore
                && technical > 0
                && technical >= stream - SpeedNearTieMsd;
            // The low-tech-score arm (TECH_NEAR_TIE_MSD_LEAD): Technical ahead of
            // Stream rather than merely beside it, and the chart wears the tech tag.
            bool leadBacked = chartTechScore >= PatternTagMinScore
                && technical > 0
                && technical - stream >= TechNearTieMsdLead;
            return techBacked || leadBacked ? "Technical" : "Stream";
        }
        // A jack-contaminated chart is not the pattern it claims: re-file without
        // either endurance skillset.
        if (nearTie == "Handstream" && JackContaminated(chartJackShare))
        {
            return BucketingSkillset(PickSkillsets(values, RiceMsdSkillsets), null, 1, chartTechScore, chartJackShare, chartHandstreamCluster);
        }
        if (nearTie != "Stamina" || lengthSeconds == null) return nearTie;
        if (demandsEndurance && !JackContaminated(chartJackShare)) return nearTie;
        return BucketingSkillset(PickSkillsets(values, BaseMsdSkillsets), null, 1, chartTechScore, chartJackShare, chartHandstreamCluster);
    }

    // ── bucket definitions (player-skills.ts:4312) ─────────────────────────

    /// <summary>The skillsets a dan estimate is broken down by, per keymode and side.</summary>
    public static DanSkillsetBucket[] DanSkillsetBuckets(int keyCount, string side)
    {
        if (side == "ln")
        {
            if (keyCount == 7)
            {
                return new[]
                {
                    // General wears the bare "ln" tag too, so it holds the side's
                    // whole body of LN work and anchors the headline.
                    new DanSkillsetBucket { Id = "lngeneral", Tags = new[] { "lngeneral", "ln" }, Anchor = true },
                    new DanSkillsetBucket { Id = "lntech", Tags = new[] { "lntech" } },
                    new DanSkillsetBucket { Id = "lninverse", Tags = new[] { "lninverse" } },
                    new DanSkillsetBucket { Id = "lnrelease", Tags = new[] { "lnrelease" } },
                };
            }
            return System.Array.Empty<DanSkillsetBucket>();
        }
        if (keyCount == 4)
        {
            return new[]
            {
                // The jack tile's tags are the analyzer override: a chart wearing
                // either tag files here regardless of the MSD argmax.
                new DanSkillsetBucket { Id = "jack", Tags = new[] { "chordjack", "speedjack" }, Skillsets = new[] { "JackSpeed", "Chordjack" } },
                // Jumpstream is the fallback pairing on tech; bucketsForClear
                // re-files a Jumpstream argmax by LeoBlack's label / runner-up.
                new DanSkillsetBucket { Id = "tech", Tags = System.Array.Empty<string>(), Skillsets = new[] { "Technical", "Jumpstream" } },
                new DanSkillsetBucket { Id = "speed", Tags = System.Array.Empty<string>(), Skillsets = new[] { "Stream" } },
                new DanSkillsetBucket { Id = "stamina", Tags = System.Array.Empty<string>(), Skillsets = new[] { "Handstream", "Stamina" } },
            };
        }
        return new[]
        {
            // Jack and stream read LeoBlack's clusters rather than their tags;
            // `tags` stays as the fallback for the ~1% of charts with no clusters.
            new DanSkillsetBucket { Id = "jack", Tags = new[] { "jack" }, ClusterFamily = "jack" },
            new DanSkillsetBucket { Id = "tech", Tags = new[] { "tech" }, ClusterFamily = "tech" },
            new DanSkillsetBucket { Id = "speed", Tags = new[] { "delay" } },
            new DanSkillsetBucket { Id = "stream", Tags = new[] { "chordstream", "bracket" }, ClusterFamily = "stream" },
        };
    }

    /// <summary>The bucket ids a keymode/side publishes, in declaration order.</summary>
    public static string[] DanSkillsetBucketIds(int keyCount, string side)
        => DanSkillsetBuckets(keyCount, side).Select(b => b.Id).ToArray();

    // ── bucketsForClear / resolveTilesForClear (player-skills.ts:4365) ─────

    private static DanSkillsetBucket? Find(DanSkillsetBucket[] buckets, string id, bool needSkillsets = false)
    {
        foreach (var b in buckets)
            if (b.Id == id && (!needSkillsets || b.Skillsets != null)) return b;
        return null;
    }

    /// <summary>
    /// The buckets one clear belongs to. 4K files by the play's argmax skillset,
    /// except a chart wearing a bucket's analyzer tag files there outright.
    /// Every filing goes through <see cref="ResolveTilesForClear"/>. Tag keymodes
    /// (6K/7K/8K) file by <see cref="ChartBelongsToTagBucket"/> alone, overlapping
    /// by design. (player-skills.ts:4365, <c>bucketsForClear</c>.)
    /// </summary>
    public static IReadOnlyList<DanSkillsetBucket> BucketsForClear(
        DanSkillsetBucket[] buckets,
        string? topSkillset,
        DanChartInfo? chart,
        ISsrVector? values,
        double rate = 1)
    {
        // Chart analysis verifies jack demand from the notes; it outranks every
        // MSD argmax just like the speedjack/chordjack tag override.
        if (chart?.JackDemand == true || TrillIsJack(chart))
        {
            var jack = buckets.FirstOrDefault(b => b.Id == "jack" && b.Skillsets != null);
            if (jack != null) return ResolveTilesForClear(new[] { jack }, buckets, chart, values);
        }
        var over = buckets.FirstOrDefault(b => b.Skillsets != null && b.Tags.Length > 0 && ChartBelongsToTagBucket(b, chart));
        if (over != null) return ResolveTilesForClear(new[] { over }, buckets, chart, values);
        // Preserve a Stamina-led chart's Handstream evidence when LeoBlack also
        // reads a plain, non-trill pattern.
        if (chart?.HandstreamEndurance == true && chart.TechCategory == false
            && chart.ClusterTrill == false && !JackContaminated(chart.JackShare))
        {
            var stamina = buckets.FirstOrDefault(b => b.Id == "stamina" && b.Skillsets != null);
            if (stamina != null) return new[] { stamina };
        }
        // The Jumpstream arbitration (TRILL_CLUSTER_CATEGORY). A trill label keeps
        // the tech pairing; a plain label files stamina; a tech-suffixed label
        // hands the tile to the runner-up skillset; no stored label keeps tech.
        bool contaminated = JackContaminated(chart?.JackShare ?? null);
        string? effectiveTop;
        if (topSkillset != "Jumpstream" || chart?.ClusterTrill == null)
        {
            effectiveTop = topSkillset;
        }
        else if (chart.ClusterTrill == true)
        {
            // The jack reading already ran as an override above. What is left is a
            // trill that is not a jack demand: on a long file the runner-up
            // decides, otherwise it keeps tech.
            effectiveTop = (EnduranceSeconds(chart.LengthSeconds, rate) ?? 0) >= TrillRunnerUpMinLengthSeconds
                ? JumpstreamRunnerUp(values, contaminated)
                : topSkillset;
        }
        else if (chart.TechCategory == true)
        {
            effectiveTop = JumpstreamRunnerUp(values, contaminated);
        }
        else
        {
            effectiveTop = contaminated ? topSkillset : "Stamina";
        }
        var filed = buckets.Where(b => b.Skillsets != null
            ? effectiveTop != null && System.Array.IndexOf(b.Skillsets!, effectiveTop) >= 0
            : ChartBelongsToTagBucket(b, chart)).ToArray();
        return ResolveTilesForClear(filed, buckets, chart, values);
    }

    /// <summary>
    /// Which tiles a clear ends on, once the note data has its say. A
    /// speed-or-tech tile is re-decided by the logistic model, and a chart can
    /// file TWO tiles where the evidence for one is not evidence against the
    /// other. Never more than two; the first-filed tile stays first (primary).
    /// (player-skills.ts:4442, <c>resolveTilesForClear</c>.)
    /// </summary>
    public static IReadOnlyList<DanSkillsetBucket> ResolveTilesForClear(
        IReadOnlyList<DanSkillsetBucket> filed,
        DanSkillsetBucket[] buckets,
        DanChartInfo? chart,
        ISsrVector? values)
    {
        if (filed.Count != 1 || filed[0].Skillsets == null) return filed;
        var primary = filed[0];

        IReadOnlyList<DanSkillsetBucket> Add(string id)
        {
            var sibling = Find(buckets, id, needSkillsets: true);
            return sibling != null ? new[] { primary, sibling } : filed;
        }

        if (primary.Id == "tech" || primary.Id == "speed")
        {
            var modelled = SpeedTechTiles(values, chart?.Motion, chart?.TechScore ?? 0);
            if (modelled == null) return filed;
            var decided = Find(buckets, modelled.Value.Primary, needSkillsets: true);
            if (decided == null) return filed;
            if (!modelled.Value.Shared) return new[] { decided };
            var other = Find(buckets, modelled.Value.Primary == "tech" ? "speed" : "tech", needSkillsets: true);
            return other != null ? new[] { decided, other } : new[] { decided };
        }

        if (primary.Id == "stamina")
        {
            // A Stamina argmax reports length, not identity; when the base is the
            // speed/tech axis and the notes read tech, the chart is a tech
            // marathon and files under both.
            if (DominantSkillset(values) != "Stamina") return filed;
            string? bas = DominantSkillset(PickSkillsets(values, BaseMsdSkillsets));
            if (bas != "Stream" && bas != "Technical") return filed;
            var modelled = SpeedTechTiles(values, chart?.Motion, chart?.TechScore ?? 0);
            if (modelled == null || modelled.Value.Primary != "tech") return filed;
            return Add("tech");
        }

        return filed;
    }

    // ── Invert ────────────────────────────────────────────────────────────

    // The chart facts the bucket walk reads for an Invert play: the stored
    // chart's cluster shares and tags describe notes the player never held.
    private static readonly string[] InverseModPatterns = { "ln", "lninverse" };

    /// <summary>player-skills.ts:948, <c>inverseModChartInfo</c>.</summary>
    public static DanChartInfo? InverseModChartInfo(DanChartInfo? chart)
    {
        if (chart == null) return null;
        return new DanChartInfo
        {
            Patterns = InverseModPatterns,
            JackDemand = false,
            JackShare = null,
            StreamShare = null,
            TechCategory = null,
            ClusterTrill = null,
            HandstreamCluster = null,
            HandstreamEndurance = chart.HandstreamEndurance, // spread keeps chart's own; not read for Invert
            TechScore = 0,
            ChordjackScore = 0,
            Motion = chart.Motion,
            LengthSeconds = chart.LengthSeconds,
            KeyCount = chart.KeyCount,
        };
    }

    // ── public entry points ───────────────────────────────────────────────

    /// <summary>
    /// The skillset tiles a play's SSR vector belongs to, in filing order (first
    /// = primary on 4K's shared tiles). (player-skills.ts:5333,
    /// <c>danSkillsetBucketsForValues</c>.)
    /// </summary>
    public static string[] DanSkillsetBucketsForValues(
        int keyCount,
        string side,
        ISsrVector values,
        double? lengthSeconds = null,
        double rate = 1,
        DanChartInfo? chart = null)
    {
        string? top = BucketingSkillset(values, lengthSeconds, rate, chart?.TechScore ?? 0, chart?.JackShare ?? null, chart?.HandstreamCluster == true);
        return BucketsForClear(DanSkillsetBuckets(keyCount, side), top, chart, values, rate).Select(b => b.Id).ToArray();
    }

    /// <summary>
    /// The skillset tiles one clear files under. Mirrors
    /// <c>danSkillsetBucketsForPlay</c> (player-skills.ts:4525): an Invert play
    /// files by what the mod made of the chart (<see cref="InverseModChartInfo"/>).
    /// </summary>
    public static string[] BucketsForPlay(
        int keyCount,
        string side,
        ISsrVector values,
        DanChartInfo? storedChart,
        double rate = 1,
        bool inverse = false)
    {
        var chart = inverse ? InverseModChartInfo(storedChart) : storedChart;
        string? topSkillset = BucketingSkillset(
            values,
            chart?.LengthSeconds,
            rate,
            chart?.TechScore ?? 0,
            chart?.JackShare ?? null,
            chart?.HandstreamCluster == true);
        return BucketsForClear(DanSkillsetBuckets(keyCount, side), topSkillset, chart, values, rate).Select(b => b.Id).ToArray();
    }

    // ── motion normaliser (player-skills.ts:1576, readMotionFeatures) ──────

    /// <summary>
    /// The stored motion block read strictly: any share short of a finite number
    /// reads as "no reading" (null). Shares clamped to [0,1] except densitySwing
    /// (>= 0 only). WP-E calls this when parsing a stored classification blob.
    /// </summary>
    public static MotionFeatures? NormalizeMotion(MotionFeatures? m)
    {
        if (m == null) return null;
        double[] core = { m.SameHand, m.MiniJack, m.OneHandTrill, m.CrossHandTrill, m.Roll4, m.RhythmBreak, m.ChordSwing };
        foreach (var v in core)
            if (!double.IsFinite(v)) return null;
        if (!double.IsFinite(m.DensitySwing)) return null;
        double Clamp01(double v) => System.Math.Min(1, System.Math.Max(0, v));
        return new MotionFeatures
        {
            SameHand = Clamp01(m.SameHand),
            MiniJack = Clamp01(m.MiniJack),
            OneHandTrill = Clamp01(m.OneHandTrill),
            CrossHandTrill = Clamp01(m.CrossHandTrill),
            Roll4 = Clamp01(m.Roll4),
            RhythmBreak = Clamp01(m.RhythmBreak),
            ChordSwing = Clamp01(m.ChordSwing),
            DensitySwing = System.Math.Max(0, m.DensitySwing),
        };
    }

    private sealed class DictSsrVector : ISsrVector
    {
        private readonly IReadOnlyDictionary<string, double> _values;
        public DictSsrVector(IReadOnlyDictionary<string, double> values) => _values = values;
        public double Get(string skillset) => _values.TryGetValue(skillset, out var v) ? v : 0.0;
    }
}

/// <summary>
/// Convenience <see cref="ISsrVector"/> over a plain dictionary keyed by
/// mania-hub skillset name.
/// </summary>
public sealed class SsrVector : ISsrVector
{
    private readonly IReadOnlyDictionary<string, double> _values;

    public SsrVector(IReadOnlyDictionary<string, double> values) => _values = values;

    /// <summary>From the eight named skillset values (mania-hub order).</summary>
    public SsrVector(double overall, double stream, double jumpstream, double handstream,
                     double stamina, double jackSpeed, double chordjack, double technical)
        : this(new Dictionary<string, double>
        {
            ["Overall"] = overall,
            ["Stream"] = stream,
            ["Jumpstream"] = jumpstream,
            ["Handstream"] = handstream,
            ["Stamina"] = stamina,
            ["JackSpeed"] = jackSpeed,
            ["Chordjack"] = chordjack,
            ["Technical"] = technical,
        })
    {
    }

    public double Get(string skillset) => _values.TryGetValue(skillset, out var v) ? v : 0.0;
}
