// Port of mania-hub player-rating eligibility & rate helpers.
//   Sources:
//     live-backend/src/features/player-skills.ts   (getPlayRate ~851, scoreRewritesChart
//       ~895, scoreInvertsChart ~904, difficultyAdjustOd ~978, daWidensHitWindows ~1012,
//       parseOsuOd ~1020, getRateModAcronym ~1033, modSpeed ~1041, clearRatePercent ~1848,
//       danMinOdFor ~1935, DAN_MIN_OD / DAN_MIN_OD_7K_LN)
//     live-backend/src/features/dan-estimates.ts   (MIN_RATE_PERCENT / MAX_RATE_PERCENT = 50/200,
//       INVERSE_MOD_VARIANT = "IN")
//     live-backend/src/shared/score.ts             (getModAcronyms ~16, getManiaKeyModCount ~41,
//       isLazerScore ~54, isLegacySubmittedScore ~49, getMissCount ~70)
//
// 1:1 mechanical port. JS `Math.round` = round-half-toward-+Inf = Math.Floor(x + 0.5).
// JS `Number(x)`: null/undefined -> handled by the guards; a non-finite result fails
// the Number.isFinite checks exactly as it does upstream.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using LazerSR.DanCalculator.Vibro;

namespace LazerSR.DanCalculator.PlayerRating;

/// <summary>
/// mania-hub `OsuMod`: <c>{ acronym, settings? } | string</c>. Here the acronym is
/// always present; <see cref="Settings"/> is null for a bare string mod or a
/// settings-less object mod. Settings values are pre-coerced to <see cref="double"/>
/// by the caller (the Hook maps osu! <c>Mod</c> objects); a key absent from the
/// dictionary means "the mod did not carry that setting" (JS <c>settings?.x == null</c>).
/// </summary>
public readonly record struct RatingMod(string Acronym, IReadOnlyDictionary<string, double>? Settings)
{
    public RatingMod(string acronym) : this(acronym, null) { }
}

/// <summary>
/// The score-shape fields mania-hub's <c>ScoreLike</c> helpers read. All optional —
/// mirrors <c>score.type</c> / <c>score.legacy_score_id</c> / <c>score.legacy_total_score</c>
/// / <c>score.statistics</c>.
/// </summary>
public sealed class RatingScore
{
    public string? Type;
    public long? LegacyScoreId;
    public double? LegacyTotalScore;
    public IReadOnlyList<RatingMod>? Mods;
    public OsuScoreStatistics? Statistics;
}

public static class PlayEligibility
{
    // dan-estimates.ts:19-20
    public const int MIN_RATE_PERCENT = 50;
    public const int MAX_RATE_PERCENT = 200;

    // dan-estimates.ts:67 — INVERSE_MOD_VARIANT = "IN"
    public const string INVERSE_MOD_VARIANT = "IN";

    // player-skills.ts — the lowest OD a play may have been judged at to credit dan.
    // Below it the windows are loose enough that the accuracy no longer says much
    // about the verdict's level; the verdict itself stays visible on /maps, exactly
    // like the danEligible structural gate. Normally this is the chart's stored OD,
    // but a Difficulty Adjust play is held to the OD it set (odOverride), so raising
    // a low-OD chart to the floor counts. A chart with no stored OD yet passes, on
    // the same terms as the wife goal's OD8 assumption.
    public const double DAN_MIN_OD = 5.5;

    // 7K LN is the one ladder with a lower floor: JinJin's official 7K LN dan
    // courses are OD 5, so a 5.5 floor would turn away the very charts the ladder
    // is measured against.
    public const double DAN_MIN_OD_7K_LN = 5;

    // player-skills.ts CHART_REWRITING_MODS — Hold Off plays every hold as a bare
    // tap (LN chart -> rice); No Release frees every hold tail (where LN accuracy
    // is earned). Such a play is skipped rather than mis-rated.
    private static readonly HashSet<string> CHART_REWRITING_MODS = new() { "HO", "NR" };

    // The keymodes an Invert play is rated on. 7K is the one whose scene names
    // inverse as an LN discipline and whose ladder has a tile for it; the 4K LN
    // verdicts come from LeoBlack, which has never been checked against inverted
    // charts, so those plays stay turned away until that is measured.
    public static readonly IReadOnlySet<int> INVERSE_MOD_KEY_COUNTS = new HashSet<int> { 7 };

    // The pattern tags an Invert play carries instead of its chart's. The mod makes
    // the chart inverse LN by construction, so the clear files by that fact rather
    // than by the analyzer's tags for the rice it never played; the bare "ln" tag
    // puts it in General too, which holds the side's whole body of LN work.
    public static readonly IReadOnlyList<string> INVERSE_MOD_PATTERNS = new[] { "ln", "lninverse" };

    // ── JS helpers ──────────────────────────────────────────────────────────────

    /// <summary>JS Math.round — round half toward +Infinity.</summary>
    private static double JsRound(double v) => System.Math.Floor(v + 0.5);

    private static bool IsFinite(double v) => double.IsFinite(v);

    private static string Acronym(RatingMod mod) => mod.Acronym ?? string.Empty;

    private static bool TrySetting(RatingMod mod, string key, out double value)
    {
        value = double.NaN;
        return mod.Settings != null && mod.Settings.TryGetValue(key, out value);
    }

    // ── shared/score.ts ─────────────────────────────────────────────────────────

    // osu! only offers the xK key mods on converts, so a play carrying one was played
    // at that key count whatever the beatmap's own cs says.
    private static readonly IReadOnlyDictionary<string, int> MANIA_KEY_MOD_COUNTS = new Dictionary<string, int>
    {
        ["1K"] = 1, ["2K"] = 2, ["3K"] = 3, ["4K"] = 4, ["5K"] = 5,
        ["6K"] = 6, ["7K"] = 7, ["8K"] = 8, ["9K"] = 9, ["10K"] = 10,
    };

    /// <summary>shared/score.ts getModAcronyms — non-empty acronyms, CL dropped unless kept.</summary>
    public static List<string> GetModAcronyms(IReadOnlyList<RatingMod>? mods, bool excludeCl = true)
    {
        var result = new List<string>();
        foreach (var mod in mods ?? (IReadOnlyList<RatingMod>)System.Array.Empty<RatingMod>())
        {
            string acronym = Acronym(mod);
            if (acronym.Length == 0) continue;
            if (excludeCl && acronym == "CL") continue;
            result.Add(acronym);
        }
        return result;
    }

    /// <summary>shared/score.ts getManiaKeyModCount — the key count a score's mods force, or null.</summary>
    public static int? GetManiaKeyModCount(IReadOnlyList<RatingMod>? mods)
    {
        foreach (var acronym in GetModAcronyms(mods, excludeCl: false))
        {
            if (MANIA_KEY_MOD_COUNTS.TryGetValue(acronym.ToUpperInvariant(), out int keyCount))
                return keyCount;
        }
        return null;
    }

    private static bool IsLegacySubmittedScore(RatingScore score)
    {
        if (score.Type != null && score.Type != "solo_score") return true;
        return score.LegacyScoreId != null || (score.LegacyTotalScore is > 0);
    }

    /// <summary>shared/score.ts isLazerScore.</summary>
    public static bool IsLazerScore(RatingScore score) => !IsLegacySubmittedScore(score);

    /// <summary>shared/score.ts getMissCount — lazer (count_miss) or stable (miss).</summary>
    public static double GetMissCount(RatingScore score)
    {
        var stats = score.Statistics;
        return stats?.count_miss ?? stats?.miss ?? 0;
    }

    // ── player-skills.ts ────────────────────────────────────────────────────────

    // player-skills.ts modSpeed — the rate a DT/HT-family mod runs at.
    // Lazer's own slider bounds; a value outside them is a corrupt payload, not a
    // rate anyone played at.
    private static double? ModSpeed(RatingMod mod, double defaultSpeed)
    {
        // A bare string mod (Settings == null) always uses the default.
        if (mod.Settings == null) return defaultSpeed;
        if (!TrySetting(mod, "speed_change", out double speed)) return defaultSpeed;
        return IsFinite(speed) && speed >= 0.5 && speed <= 2 ? speed : (double?)null;
    }

    /// <summary>
    /// player-skills.ts getPlayRate — the constant music rate a play was set at: the
    /// rate mod's default 1.5x or 0.75x, or the custom speed_change it carries. Null
    /// when no single rate exists (wind up/down, adaptive speed) or the speed value
    /// is corrupt, in which case the play is skipped rather than mis-rated.
    /// </summary>
    public static double? GetPlayRate(IReadOnlyList<RatingMod>? mods)
    {
        double rate = 1;
        foreach (var mod in mods ?? (IReadOnlyList<RatingMod>)System.Array.Empty<RatingMod>())
        {
            string acronym = Acronym(mod);
            if (acronym == "DT" || acronym == "NC")
            {
                double? speed = ModSpeed(mod, 1.5);
                if (speed == null) return null;
                rate *= speed.Value;
            }
            else if (acronym == "HT" || acronym == "DC")
            {
                double? speed = ModSpeed(mod, 0.75);
                if (speed == null) return null;
                rate *= speed.Value;
            }
            else if (acronym == "WU" || acronym == "WD" || acronym == "AS")
            {
                return null;
            }
        }
        return JsRound(rate * 100) / 100;
    }

    /// <summary>
    /// player-skills.ts scoreRewritesChart — HO / NR turn the stored .osu into a
    /// chart the play never was; such a play is skipped rather than mis-rated.
    /// </summary>
    public static bool ScoreRewritesChart(IReadOnlyList<RatingMod>? mods)
    {
        foreach (var mod in mods ?? (IReadOnlyList<RatingMod>)System.Array.Empty<RatingMod>())
            if (CHART_REWRITING_MODS.Contains(Acronym(mod)))
                return true;
        return false;
    }

    /// <summary>player-skills.ts scoreInvertsChart — whether the play was set under lazer's Invert mod.</summary>
    public static bool ScoreInvertsChart(IReadOnlyList<RatingMod>? mods)
    {
        foreach (var mod in mods ?? (IReadOnlyList<RatingMod>)System.Array.Empty<RatingMod>())
            if (Acronym(mod) == INVERSE_MOD_VARIANT)
                return true;
        return false;
    }

    /// <summary>
    /// player-skills.ts difficultyAdjustOd — the raw OD slider a Difficulty Adjust
    /// play was judged at (Extended Limits: -15..15), or null when the play carries
    /// no DA (or a settings-less DA that left the slider alone).
    /// </summary>
    public static double? DifficultyAdjustOd(IReadOnlyList<RatingMod>? mods)
    {
        foreach (var mod in mods ?? (IReadOnlyList<RatingMod>)System.Array.Empty<RatingMod>())
        {
            if (Acronym(mod) != "DA") continue;
            // JS: `typeof mod === "string" -> continue`. A bare-string DA carries no settings.
            if (mod.Settings == null) continue;
            if (!TrySetting(mod, "overall_difficulty", out double od) || !IsFinite(od)) return null;
            return System.Math.Max(-15, System.Math.Min(15, od));
        }
        return null;
    }

    /// <summary>
    /// player-skills.ts daWidensHitWindows — whether Difficulty Adjust made the play
    /// easier to hit than the chart the SSR is computed from. Refused a rating outright.
    /// A DA that put the slider under the dan OD floor is the abuse shape regardless,
    /// so with no known chart OD that alone disqualifies.
    /// </summary>
    public static bool DaWidensHitWindows(double? odOverride, double? chartOd)
    {
        if (odOverride == null) return false;
        if (chartOd == null) return odOverride.Value < DAN_MIN_OD;
        return odOverride.Value < chartOd.Value;
    }

    /// <summary>
    /// player-skills.ts parseOsuOd — the chart's own OD as the .osu states it, or
    /// null when the file has no OverallDifficulty line.
    /// </summary>
    public static double? ParseOsuOd(string osuText)
    {
        var match = OsuOdRegex.Match(osuText);
        if (!match.Success) return null;
        // JS Number("...") of the captured group.
        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double od))
            return null;
        return IsFinite(od) && od >= 0 && od <= 10 ? od : (double?)null;
    }

    private static readonly Regex OsuOdRegex =
        new(@"^OverallDifficulty\s*:\s*(-?\d+(?:\.\d+)?)", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// player-skills.ts getRateModAcronym — the rate mod a play carries, by acronym
    /// (NC/DC vs DT/HT cannot be told from the numeric rate alone).
    /// </summary>
    public static string? GetRateModAcronym(IReadOnlyList<RatingMod>? mods)
    {
        foreach (var mod in mods ?? (IReadOnlyList<RatingMod>)System.Array.Empty<RatingMod>())
        {
            string acronym = Acronym(mod);
            if (acronym == "DT" || acronym == "NC" || acronym == "HT" || acronym == "DC") return acronym;
        }
        return null;
    }

    /// <summary>
    /// player-skills.ts clearRatePercent — the rate a clear at this play would be
    /// credited at, or null when the play is 1.0x (the chart's own verdict covers it)
    /// or outside the estimator's 50-200% band. Wind up/down and adaptive speed never
    /// get here (getPlayRate already returned null).
    /// </summary>
    public static int? ClearRatePercent(double rate)
    {
        if (!IsFinite(rate) || rate <= 0 || rate == 1) return null;
        int percent = (int)JsRound(rate * 100);
        if (percent == 100 || percent < MIN_RATE_PERCENT || percent > MAX_RATE_PERCENT) return null;
        return percent;
    }

    /// <summary>player-skills.ts danMinOdFor — the OD floor the play's own ladder holds it to.</summary>
    public static double DanMinOdFor(int keyCount, string? side)
        => keyCount == 7 && side == "ln" ? DAN_MIN_OD_7K_LN : DAN_MIN_OD;
}
