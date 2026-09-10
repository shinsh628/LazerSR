// Port of the Wife3 accuracy-goal block from mania-hub
// live-backend/src/features/player-skills.ts (~lines 340-1160).
//
// This produces the `goal` value fed to MinaCalc per play: the Wife3 estimate
// when a play has judgement counts, raw accuracy otherwise. Wife3 prices a MAX
// above a 300 (osu! accuracy scores both as 100%), which is what lets two 99%+
// plays with different MAX:300 ratios rate differently.
//
// 1:1 mechanical port. All numbers are `double` (JS `number`). `Math.round(x)`
// in JS rounds half toward +Infinity -> JsRound. `Number(x ?? 0)` semantics
// mirrored where they matter. Pure math, no external deps.
//
// PORT NOTE: `OsuScoreStatistics` is reused from LazerSR.DanCalculator.Vibro
// (same JS field names). `SsrGoalScore.Mods` is modelled as acronym strings
// (the JS accepts `OsuMod[] | string[]` and only reads `.acronym`); the Hook
// payload builder passes acronyms. An integration agent may consolidate a
// richer OsuMod type later.

using System.Collections.Concurrent;
using System.Globalization;
using LazerSR.DanCalculator.Vibro;

namespace LazerSR.DanCalculator.PlayerRating;

/// <summary>
/// JS `Pick&lt;OscScore, "accuracy" | "statistics" | "type" | "legacy_score_id"
/// | "legacy_total_score"&gt; &amp; { mods?: ... }` — the fields the SSR-goal path reads.
/// </summary>
public sealed class SsrGoalScore
{
    public double Accuracy;
    public OsuScoreStatistics? Statistics;
    public string? Type;
    public long? LegacyScoreId;
    public double? LegacyTotalScore;
    /// <summary>Mod acronyms. Only "EZ"/"HR" affect the goal (window scale).</summary>
    public IReadOnlyList<string>? Mods;
}

public static class WifeGoal
{
    // ── constants (verbatim from player-skills.ts) ──────────────────────────

    /// <summary>Plays whose goal lands on the calc's 0.8 floor are excluded from MSD.</summary>
    private const double SSR_GOAL_MIN = 0.8;

    // The calc clamps goals above 0.965 internally (Etterna's SSR cap); goals
    // above it are served by extrapolating from the calc's slope between the MSD
    // baseline goal and the cap. Exported for the approximate-SSR baseline, which
    // anchors its accuracy derate on the same window.
    public const double SSR_CALC_GOAL_CAP = 0.965;
    public const double SSR_EXTRAPOLATION_BASE_GOAL = 0.93;

    // Ceiling for Wife-estimated goals: an all-MAX play estimates ~0.998, and the
    // log-linear extrapolation should not be trusted much further past the cap
    // than the width of the slope window it was measured on.
    private const double SSR_GOAL_CAP = 0.9975;

    // Safety bound on the per-chart 0.93->0.965 slope used for extrapolation
    // (measured ~1.07-1.11 on real charts).
    public const double SSR_EXTRAPOLATION_MAX_SLOPE = 1.2;

    // Expected normalized Wife3 points per osu!mania judgement: Etterna's wife3
    // curve (J4: full points inside 5ms, erf falloff with dev 22.7 crossing zero
    // at 65ms, linear to the -2.75 miss weight at 180ms, all normalized to
    // marvelous = 1) averaged uniformly over each judgement's band of the chart's
    // stable hit windows (MAX +-16.5ms fixed; 300/200/100/50 at 64/97/127/151
    // minus 3ms per OD point; at OD8 that is the familiar +-40/73/103/127). The
    // MAX vs 300 split is the load-bearing part: osu! accuracy scores both as
    // 100%, Wife3 does not, which is what lets two 99%+ plays with different
    // MAX:300 ratios rate differently. Valuing the bands at the chart's real OD
    // is what keeps low-OD charts honest: the MAX window does not scale with OD,
    // so true precision keeps its full value, while a 300 earned inside OD0's
    // +-64ms band averages ~0.75 instead of OD8's ~0.97. Unknown OD falls back
    // to the OD8 assumption these constants historically hardcoded. Lazer plays
    // share the stable window model (same assumption the fixed table made), and
    // their LN-side leniency is handled by the lnRatio goal fade below.
    private const double WIFE3_FULL_POINTS_MS = 5;
    private const double WIFE3_ZERO_MS = 65;
    private const double WIFE3_ERF_DEV_MS = 22.7;
    private const double WIFE3_MISS_MS = 180;
    private const double WIFE3_MISS_POINTS = -2.75;
    private const double STABLE_MAX_WINDOW_MS = 16.5;
    // STABLE_WINDOW_BASES_MS = { great: 64, good: 97, ok: 127, meh: 151 }
    private static readonly double[] StableWindowBasesMs = { 64, 97, 127, 151 };
    private const double OD_WINDOW_STEP_MS = 3;

    // Stored score payloads and old beatmap rows may carry no OD; assume the OD8
    // the old constants hardcoded so behavior degrades to the historical one.
    private const double ASSUMED_OD = 8;

    // Etterna's rating_scaler from ScoreManager::CalcPlayerRating.
    // PORT NOTE: only aggregateSsrs (server-side) uses this; kept here for reference.
    public const double AGGREGATE_RATING_SCALER = 1.04;

    // Both clients scale every mania hit window by 1.4 under EZ and by 1/1.4
    // under HR. Stable always did; lazer matched it exactly in July 2025
    // (ppy/osu 8e53f47, a per-note window multiplier), and before that its EZ/HR
    // still widened/narrowed the windows by shifting effective OD, just not by
    // this exact factor. 1.4 is exact for stable and current lazer and the close
    // approximation for the older lazer plays.
    private const double EZ_WINDOW_SCALE = 1.4;

    // JS Math.round: round half toward +Infinity.
    private static double JsRound(double v) => Math.Floor(v + 0.5);

    // ── erf ────────────────────────────────────────────────────────────────

    // Abramowitz & Stegun 7.1.26 (|error| < 1.5e-7, plenty for the aggregation)
    public static double Erf(double x)
    {
        double sign = x < 0 ? -1 : 1;
        double ax = Math.Abs(x);
        double t = 1 / (1 + 0.3275911 * ax);
        double poly = ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t;
        return sign * (1 - poly * Math.Exp(-ax * ax));
    }

    // ── Wife3 curve ────────────────────────────────────────────────────────

    private static double Wife3PointsAt(double ms)
    {
        if (ms <= WIFE3_FULL_POINTS_MS) return 1;
        if (ms <= WIFE3_ZERO_MS) return Erf((WIFE3_ZERO_MS - ms) / WIFE3_ERF_DEV_MS);
        if (ms >= WIFE3_MISS_MS) return WIFE3_MISS_POINTS;
        return (WIFE3_MISS_POINTS * (ms - WIFE3_ZERO_MS)) / (WIFE3_MISS_MS - WIFE3_ZERO_MS);
    }

    private static double Wife3BandAverage(double fromMs, double toMs)
    {
        if (!(toMs > fromMs)) return Wife3PointsAt(toMs);
        const int steps = 512;
        double step = (toMs - fromMs) / steps;
        double sum = 0;
        for (int i = 0; i < steps; i += 1) sum += Wife3PointsAt(fromMs + (i + 0.5) * step);
        return sum / steps;
    }

    /// <summary>JS `Record&lt;WifeJudgement, number&gt;` — expected points per judgement.</summary>
    private readonly record struct WifePoints(double Perfect, double Great, double Good, double Ok, double Meh, double Miss);

    private static readonly ConcurrentDictionary<string, WifePoints> ExpectedWife3PointsCache = new();

    private static WifePoints ExpectedWife3Points(double od, double windowScale)
    {
        string key = $"{od.ToString(CultureInfo.InvariantCulture)}|{windowScale.ToString(CultureInfo.InvariantCulture)}";
        if (ExpectedWife3PointsCache.TryGetValue(key, out var cached)) return cached;

        // edges = [ MAX*scale, ...bases.map(base => (base - 3*od) * scale) ]
        double[] edges = new double[5];
        edges[0] = STABLE_MAX_WINDOW_MS * windowScale;
        for (int i = 0; i < StableWindowBasesMs.Length; i += 1)
            edges[i + 1] = (StableWindowBasesMs[i] - OD_WINDOW_STEP_MS * od) * windowScale;

        var points = new WifePoints(
            Perfect: Wife3BandAverage(0, edges[0]),
            Great: Wife3BandAverage(edges[0], edges[1]),
            Good: Wife3BandAverage(edges[1], edges[2]),
            Ok: Wife3BandAverage(edges[2], edges[3]),
            Meh: Wife3BandAverage(edges[3], edges[4]),
            Miss: WIFE3_MISS_POINTS);

        ExpectedWife3PointsCache.TryAdd(key, points);
        return points;
    }

    // ── judgement-count helpers ────────────────────────────────────────────

    // JS: Number(value ?? 0); Number.isFinite && > 0 ? Math.floor : 0
    private static double ReadCount(double? value)
    {
        double count = value ?? 0;
        return double.IsFinite(count) && count > 0 ? Math.Floor(count) : 0;
    }

    // JS `a ?? b` — b only when a is null/undefined.
    private static double? Coalesce(double? a, double? b) => a ?? b;

    /// <summary>
    /// mania-hub player-skills.ts getMissShare — misses as a fraction of judged
    /// notes, or null when the score carries no counts.
    /// </summary>
    public static double? GetMissShare(OsuScoreStatistics? statistics)
    {
        if (statistics == null) return null;
        double[] counts =
        {
            ReadCount(Coalesce(statistics.perfect, statistics.count_geki)),
            ReadCount(Coalesce(statistics.great, statistics.count_300)),
            ReadCount(Coalesce(statistics.good, statistics.count_katu)),
            ReadCount(Coalesce(statistics.ok, statistics.count_100)),
            ReadCount(Coalesce(statistics.meh, statistics.count_50)),
        };
        double miss = ReadCount(Coalesce(statistics.miss, statistics.count_miss));
        double total = miss;
        foreach (double c in counts) total += c;
        return total > 0 ? miss / total : (double?)null;
    }

    // ── Wife accuracy estimate ─────────────────────────────────────────────

    /// <summary>
    /// Estimated Wife3 percent from a play's judgement counts (lazer or stable
    /// naming), or null when the score carries no counts. <paramref name="od"/> is
    /// the chart's overall difficulty (null assumes the historical OD8) and
    /// <paramref name="windowScale"/> widens/tightens every window (stable EZ/HR).
    /// </summary>
    public static double? EstimateWifeAccuracy(OsuScoreStatistics? statistics, double? od = null, double? windowScale = null)
    {
        if (statistics == null) return null;

        // od == null must fall back to the assumption, not read as a real OD 0.
        double rawOd = od == null ? double.NaN : od.Value;
        double odClamped = double.IsFinite(rawOd) ? Math.Max(0, Math.Min(10, rawOd)) : ASSUMED_OD;

        double rawScale = windowScale ?? double.NaN; // JS Number(undefined) -> NaN
        double scale = double.IsFinite(rawScale) && rawScale > 0 ? rawScale : 1;

        var expected = ExpectedWife3Points(odClamped, scale);

        double perfect = ReadCount(Coalesce(statistics.perfect, statistics.count_geki));
        double great = ReadCount(Coalesce(statistics.great, statistics.count_300));
        double good = ReadCount(Coalesce(statistics.good, statistics.count_katu));
        double ok = ReadCount(Coalesce(statistics.ok, statistics.count_100));
        double meh = ReadCount(Coalesce(statistics.meh, statistics.count_50));
        double miss = ReadCount(Coalesce(statistics.miss, statistics.count_miss));

        double total = perfect + great + good + ok + meh + miss;
        double points = perfect * expected.Perfect + great * expected.Great + good * expected.Good
                        + ok * expected.Ok + meh * expected.Meh + miss * expected.Miss;

        return total > 0 ? points / total : (double?)null;
    }

    // ── EZ/HR window scale ─────────────────────────────────────────────────

    private static double EzWindowScale(SsrGoalScore score)
    {
        double scale = 1;
        foreach (string acronym in score.Mods ?? Array.Empty<string>())
        {
            if (acronym == "EZ") scale *= EZ_WINDOW_SCALE;
            else if (acronym == "HR") scale /= EZ_WINDOW_SCALE;
        }
        return scale;
    }

    // ── lazer / legacy discrimination ──────────────────────────────────────

    private static bool IsLegacySubmittedScore(SsrGoalScore score)
    {
        if (score.Type != null && score.Type != "solo_score") return true;
        // JS: legacy_score_id != null || !!(legacy_total_score && legacy_total_score > 0)
        return score.LegacyScoreId != null || (score.LegacyTotalScore ?? 0) > 0;
    }

    public static bool IsLazerScore(SsrGoalScore score) => !IsLegacySubmittedScore(score);

    // ── SSR goal ───────────────────────────────────────────────────────────

    public static double SsrGoalForAccuracy(double accuracy)
    {
        double acc = double.IsFinite(accuracy) ? accuracy : 0.93;
        return JsRound(Math.Max(SSR_GOAL_MIN, Math.Min(SSR_CALC_GOAL_CAP, acc)) * 10_000) / 10_000;
    }

    /// <summary>
    /// The SSR goal for a play: the Wife3 estimate when judgement counts exist,
    /// raw accuracy otherwise. Only the judgement path may exceed the calc's
    /// 0.965 cap. Returns null when the goal lands on the calc's 0.8 floor
    /// (the play must not count toward MSD).
    /// </summary>
    public static double? SsrGoalForScore(SsrGoalScore score, double? lnRatio = null, double? od = null)
    {
        double goal = SsrGoalForScoreUnchecked(score, lnRatio, od);
        return goal > SSR_GOAL_MIN ? goal : (double?)null;
    }

    public static double SsrGoalForScoreUnchecked(SsrGoalScore score, double? lnRatio = null, double? od = null)
    {
        double? wife = EstimateWifeAccuracy(score.Statistics, od, EzWindowScale(score));
        if (wife == null) return SsrGoalForAccuracy(score.Accuracy);

        // Apply the eligibility floor after the blend. Clamping an ineligible
        // Wife estimate up to 80% first lets even one hold manufacture MSD credit.
        double wifeGoal = Math.Min(SSR_GOAL_CAP, wife.Value);
        if (IsLazerScore(score))
        {
            double accGoal = SsrGoalForAccuracy(score.Accuracy);
            double fade = lnRatio == null ? 1 : Math.Max(0, Math.Min(1, lnRatio.Value));
            return JsRound((wifeGoal * (1 - fade) + accGoal * fade) * 10_000) / 10_000;
        }
        return JsRound(wifeGoal * 10_000) / 10_000;
    }
}
