// Port of the SSR-at-goal helpers from mania-hub
// live-backend/src/features/player-skills.ts:
//   runMsdAtGoal        (~1193)
//   computePlaySsrValues (~1219)
//
// These sit on top of Msd.ComputeMsd (which now routes MsdOptions.ScoreGoal
// through the DLL's calc_ssr path — see MinaCalcNative). The wife accuracy `goal`
// itself comes from the WifeGoal package (ssrGoalForScore); this file only takes
// it as a parameter.
//
// PORT NOTE (engine): our MinaCalc.dll is v505; mania-hub's player-rating MSD path
// runs LeoBlack's Etterna WASM (v0.72.3 / v0.74.0). Skillset values are close but
// not bit-identical — the extrapolation math below is ported verbatim regardless.

using LazerSR.DanCalculator.Msd;
using MsdFacade = LazerSR.DanCalculator.Msd.Msd;

namespace LazerSR.DanCalculator.PlayerRating;

/// <summary>JS <c>{ values, calcRuns }</c>.</summary>
public sealed record PlaySsrValues(IReadOnlyDictionary<string, double> Values, int CalcRuns);

public static class PlaySsr
{
    /// <summary>
    /// The calc clamps goals above 0.965 internally (Etterna's SSR cap); goals
    /// above it are served by extrapolating from the calc's slope between the MSD
    /// baseline goal and the cap.
    /// </summary>
    public const double SSR_CALC_GOAL_CAP = 0.965;

    public const double SSR_EXTRAPOLATION_BASE_GOAL = 0.93;

    // Ceiling for Wife-estimated goals: an all-MAX play estimates ~0.998, and the
    // log-linear extrapolation should not be trusted much further past the cap
    // than the width of the slope window it was measured on.
    private const double SSR_EXTRAPOLATION_MAX_SLOPE = 1.2;

    /// <summary>
    /// SSRs for a play at <paramref name="goal"/>, running the calc once for goals it
    /// can serve directly and extending past its 0.965 cap by extrapolating each
    /// skillset along the chart's own 0.93 -> 0.965 log-slope. Returns the values
    /// plus how many calc runs it took (for event-loop breathing). Null on failure.
    /// </summary>
    public static PlaySsrValues? RunMsdAtGoal(string osuText, double rate, int keyCount, double goal, bool lnTailTaps = false)
    {
        var capped = SafeComputeMsd(osuText, rate, keyCount, Math.Min(goal, SSR_CALC_GOAL_CAP), lnTailTaps);
        if (capped == null) return null;
        if (goal <= SSR_CALC_GOAL_CAP) return new PlaySsrValues(capped, 1);

        var baseValues = SafeComputeMsd(osuText, rate, keyCount, SSR_EXTRAPOLATION_BASE_GOAL, lnTailTaps);
        if (baseValues == null) return new PlaySsrValues(capped, 1);

        double exponent = (goal - SSR_CALC_GOAL_CAP) / (SSR_CALC_GOAL_CAP - SSR_EXTRAPOLATION_BASE_GOAL);
        var values = new Dictionary<string, double>();
        foreach (var (name, atCap) in capped)
        {
            double atBase = baseValues.TryGetValue(name, out var b) ? b : 0.0;
            if (!(atCap > 0) || !(atBase > 0) || atCap <= atBase)
            {
                values[name] = atCap;
                continue;
            }
            double slope = Math.Min(atCap / atBase, SSR_EXTRAPOLATION_MAX_SLOPE);
            values[name] = atCap * Math.Pow(slope, exponent);
        }
        return new PlaySsrValues(values, 2);
    }

    /// <summary>
    /// SSRs on hold-bearing charts blend toward a tail-aware second calc pass;
    /// weights and rationale live with the calc facade (<see cref="Msd"/>).
    /// </summary>
    public static PlaySsrValues? ComputePlaySsrValues(string osuText, double rate, int keyCount, double goal, double? lnRatio = null)
    {
        var baseValues = RunMsdAtGoal(osuText, rate, keyCount, goal);
        if (baseValues == null) return null;

        double blend = MsdFacade.LN_TAIL_BLEND_BY_KEYMODE.GetValueOrDefault(keyCount, 0);
        double ratio = lnRatio ?? 0.0;
        if (!(blend > 0) || !(ratio > MsdFacade.LN_TAIL_MIN_RATIO)) return baseValues;

        var tails = RunMsdAtGoal(osuText, rate, keyCount, goal, lnTailTaps: true);
        if (tails == null) return baseValues;

        return new PlaySsrValues(
            MsdFacade.BlendLnTailValues(baseValues.Values, tails.Values, keyCount),
            baseValues.CalcRuns + tails.CalcRuns);
    }

    // JS `computeMsd(...).catch(msdChartErrorFallback)` — returns the value dict, or
    // null, and rethrows only MsdThreadUnavailableException (retention contract).
    private static IReadOnlyDictionary<string, double>? SafeComputeMsd(
        string osuText, double rate, int keyCount, double scoreGoal, bool lnTailTaps)
    {
        try
        {
            var raw = MsdFacade.ComputeMsd(osuText, new MsdOptions
            {
                Rate = rate,
                KeyCount = keyCount,
                ScoreGoal = scoreGoal,
                LnTailTaps = lnTailTaps,
                AdjustVibro = true,
            })?.Values;
            return raw == null ? null : ToCanonicalSkillsetKeys(raw);
        }
        catch (Exception e)
        {
            MsdFacade.MsdChartErrorFallback(e); // rethrows MsdThreadUnavailableException
            return null;
        }
    }

    // Msd.ComputeMsd yields lowercase keys (overall/jackspeed/...) — the MSD-widget
    // contract. The player-rating pipeline (DanBuckets, PlayUploadRecord, SsrVector)
    // reads mania-hub's SKILL_RATING_SKILLSETS names (Overall/JackSpeed/...), so a
    // lowercase lookup silently returns 0 and every clear loses its argmax bucket.
    // Normalise here, once, at the boundary.
    private static readonly Dictionary<string, string> CanonicalKey = new()
    {
        ["overall"] = "Overall", ["stream"] = "Stream", ["jumpstream"] = "Jumpstream",
        ["handstream"] = "Handstream", ["stamina"] = "Stamina", ["jackspeed"] = "JackSpeed",
        ["chordjack"] = "Chordjack", ["technical"] = "Technical",
    };

    private static IReadOnlyDictionary<string, double> ToCanonicalSkillsetKeys(IReadOnlyDictionary<string, double> raw)
    {
        var outp = new Dictionary<string, double>(raw.Count);
        foreach (var (k, v) in raw)
            outp[CanonicalKey.TryGetValue(k, out var c) ? c : k] = v;
        return outp;
    }
}
