// Port of mania-hub live-backend/src/dan/msd.ts (+ msd-calc.ts, msd-thread.ts,
// vendor/leoblack/ett/index.js + calc.js) as a thin adapter over our in-project
// MinaCalc.dll P/Invoke (Msd/MinaCalcNative.cs). LeoBlack's WASM harness
// (vendor/leoblack/ett) is NOT ported.
//
// PORT NOTE (deviations, see also MinaCalcNative.cs):
//   * Single MinaCalc.dll build vs LeoBlack's 6 pinned Etterna versions.
//     `EtternaVersion` is the fixed string "minacalc-lazersr".
//   * 4K only. Non-4K keycounts return null (the native calc rejects them),
//     even though isMsdSupportedKeyCount() still reports 4..18 for parity with
//     the JS keycount gate.
//   * Rate is applied by rescaling row times (1/rate) and reading the R10 field —
//     see MinaCalcNative rate PORT NOTE. `ScoreGoal` is accepted but ignored
//     (the DLL export has no score-goal parameter).
//   * The dedicated worker-thread + broken-cooldown machinery of msd-thread.ts is
//     replaced by MinaCalcNative's single serialized worker thread. A transient
//     native failure surfaces as a null result; MsdThreadUnavailableException is
//     kept only so msdChartErrorFallback() keeps its rethrow contract.
//   * `values` keys are lowercase (overall/stream/jumpstream/handstream/stamina/
//     jackspeed/chordjack/technical) per PORTING.md §"MSD substitution", NOT the
//     PascalCase DISPLAY_SKILLSET_ORDER of the JS. Consumers in this project
//     (Companella, mixed estimator) read the lowercase keys.

using LazerSR.DanCalculator.Beatmap;
using LazerSR.DanCalculator.Vibro;

namespace LazerSR.DanCalculator.Msd;

public sealed class MsdOptions
{
    /// <summary>Explicitly opt player SSRs into localized vibro removal.</summary>
    public bool AdjustVibro;
    public double? Rate;
    public int? KeyCount;
    /// <summary>Target wife percent. Accepted for parity; ignored by the native calc.</summary>
    public double? ScoreGoal;
    public bool LnTailTaps;
}

public sealed class MsdResult
{
    public string EtternaVersion = "";
    public Dictionary<string, double> Values = new();
    public VibroAnalysis? VibroAnalysis;
    public double? VibroVersion;
    /// <summary>False on full-chart estimates; true when applying player-rating policy.</summary>
    public bool VibroAdjusted;
}

/// <summary>A broken worker is transient infrastructure failure: let the job retry
/// instead of saving empty/lower ratings. Kept for msdChartErrorFallback parity.</summary>
public sealed class MsdThreadUnavailableException : Exception
{
    public MsdThreadUnavailableException(string message) : base(message) { }
}

public static class Msd
{
    // We ship one build; document that LeoBlack pins specific engine versions.
    public const string ETTERNA_VERSION = "minacalc-lazersr";

    public const double DEFAULT_SCORE_GOAL = 0.93;

    // MinaCalc rates 4..18K in LeoBlack's harness; our DLL is 4K only (compute
    // returns null for the rest). Keep the wide gate for parity with the JS.
    private static readonly HashSet<int> MSD_SUPPORTED_KEYS =
        new(Enumerable.Range(4, 15)); // 4..18

    // MinaCalc rates the rice skeleton: LN tails never reach it, so hold-heavy
    // charts underrate. The tail-aware pass (lnTailTaps) is a strict upper bound
    // on the release work - a release is easier than a tap - so consumers blend
    // toward it by a keymode-calibrated weight. Weights were fit on player
    // cohorts (2026-07-19, ~140 players, LN-share cohorts vs pp-anchored
    // residuals): 4K flattens the hybrid cohort at 0.1 (osu pp overpays 4K LN,
    // so zeroing the ln-main residual against pp would overcorrect); the 0.74
    // multi-key calc underrates 7K LN far harder and wants 0.3. Charts without
    // holds produce identical rows either way, so rice values are untouched.
    public static readonly Dictionary<int, double> LN_TAIL_BLEND_BY_KEYMODE =
        new() { { 4, 0.1 }, { 6, 0.3 }, { 7, 0.3 } };
    public const double LN_TAIL_MIN_RATIO = 0.02;

    private static readonly string[] SkillsetKeys =
    {
        "overall", "stream", "jumpstream", "handstream",
        "stamina", "jackspeed", "chordjack", "technical",
    };

    public static bool IsMsdSupportedKeyCount(int keyCount) => MSD_SUPPORTED_KEYS.Contains(keyCount);

    // A chart the calculator rejects can keep the existing no-MSD fallback.
    // A broken worker is transient infrastructure failure: rethrow so the job retries.
    public static object? MsdChartErrorFallback(Exception error)
    {
        if (error is MsdThreadUnavailableException) throw error;
        return null;
    }

    /// <summary>Blend base MSD values toward the tail-aware pass by the keymode weight.</summary>
    public static Dictionary<string, double> BlendLnTailValues(
        IReadOnlyDictionary<string, double> baseValues,
        IReadOnlyDictionary<string, double> tails,
        int keyCount)
    {
        double blend = LN_TAIL_BLEND_BY_KEYMODE.GetValueOrDefault(keyCount, 0);
        var values = new Dictionary<string, double>();
        foreach (var (name, atBase) in baseValues)
        {
            double atTails = tails.TryGetValue(name, out var t) ? t : atBase;
            values[name] = blend > 0 && atBase > 0 && atTails > atBase
                ? atBase + blend * (atTails - atBase)
                : atBase;
        }
        return values;
    }

    /// <summary>Display-ready LN-adjusted MSD: the blended values, or null when blending
    /// changes nothing (rice charts, unsupported keymodes).</summary>
    public static Dictionary<string, double>? LnAdjustedMsd(
        IReadOnlyDictionary<string, double>? baseValues,
        IReadOnlyDictionary<string, double>? tails,
        int keyCount)
    {
        if (baseValues == null || tails == null) return null;
        var blended = BlendLnTailValues(baseValues, tails, keyCount);
        double blendedOverall = blended.GetValueOrDefault("overall", 0);
        double baseOverall = baseValues.TryGetValue("overall", out var b) ? b : 0;
        return blendedOverall - baseOverall >= 0.005 ? blended : null;
    }

    /// <summary>
    /// Compute the Etterna MSD skillset values for a chart at the given rate.
    /// When <c>ScoreGoal</c> is set the DLL's <c>calc_ssr</c> (rate + goal) path is
    /// used (SSR mode — the player-rating pipeline); otherwise the all-rates MSD
    /// baseline. Returns null for keymodes MinaCalc does not support (anything outside
    /// 4-18K), and — in this port — for any non-4K chart.
    /// </summary>
    public static MsdResult? ComputeMsd(string osuText, MsdOptions? options = null)
    {
        options ??= new MsdOptions();

        int? keyCount = options.KeyCount;
        if (keyCount != null && !IsMsdSupportedKeyCount(keyCount.Value)) return null;

        double rate = options.Rate ?? 1;

        var map = ManiaBeatmapParser.Parse(osuText);
        PrepareVibroChartResult? prepared = options.AdjustVibro
            ? VibroSections.PrepareVibroChart(osuText, rate, map)
            : null;
        VibroAnalysis? analysis = VibroSections.UsesSectionVibro(map)
            ? (prepared?.Analysis ?? VibroSections.AnalyzeVibroSections(map, rate))
            : null;

        string osuForCalc = prepared?.OsuText ?? osuText;
        var values = CalculateMsdValues(osuForCalc, options, rate);
        if (values == null) return null;

        return new MsdResult
        {
            EtternaVersion = ETTERNA_VERSION,
            Values = values,
            VibroVersion = VibroSections.VIBRO_SECTION_VERSION,
            VibroAdjusted = options.AdjustVibro,
            VibroAnalysis = analysis,
        };
    }

    // Mirrors vendor/leoblack/ett/calc.js analyzeEtternaFromText: parse, resolve
    // keycount, build rows, run the calc. Rows <= 1 -> zero values.
    private static Dictionary<string, double>? CalculateMsdValues(string osuText, MsdOptions options, double rate)
    {
        var map = ManiaBeatmapParser.Parse(osuText);

        int keycount = ResolveKeycount(map.KeyCount, options.KeyCount);
        if (keycount != 4) return null; // PORT NOTE: native is 4K only.

        var rows = MinaCalcNative.BuildRows(map, options.LnTailTaps);
        if (rows == null) return null;
        if (rows.Count <= 1) return MakeZeroValues();

        var ssr = options.ScoreGoal is double goal
            ? MinaCalcNative.MsdAtGoalNative(rows, rate, goal, keycount)
            : MinaCalcNative.MsdForAllRatesNative(rows, rate);
        if (ssr == null) return null;

        return new Dictionary<string, double>
        {
            ["overall"] = ssr.Overall,
            ["stream"] = ssr.Stream,
            ["jumpstream"] = ssr.Jumpstream,
            ["handstream"] = ssr.Handstream,
            ["stamina"] = ssr.Stamina,
            ["jackspeed"] = ssr.Jackspeed,
            ["chordjack"] = ssr.Chordjack,
            ["technical"] = ssr.Technical,
        };
    }

    private static int ResolveKeycount(int parsedCount, int? over)
    {
        if (over != null && IsMsdSupportedKeyCount(over.Value)) return over.Value;
        if (IsMsdSupportedKeyCount(parsedCount)) return parsedCount;
        throw new InvalidOperationException($"Unsupported keycount: {parsedCount}");
    }

    private static Dictionary<string, double> MakeZeroValues()
    {
        var d = new Dictionary<string, double>();
        foreach (var k in SkillsetKeys) d[k] = 0;
        return d;
    }
}
