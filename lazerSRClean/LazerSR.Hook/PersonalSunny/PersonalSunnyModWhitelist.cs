using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mods;
using osu.Game.Utils;

namespace LazerSR.Hook.PersonalSunny;

/// <summary>
/// The personal-diff mod whitelist: NM, and DT/NC/HT/DC only at their default rate (1.5x/0.75x).
/// Deliberately mirrors osu!'s own <see cref="Mod.Ranked"/> notion for rate mods (<c>SpeedChange.IsDefault</c>
/// on <c>ModDoubleTime</c>/<c>ModHalfTime</c>) - a score isn't "ranked" in osu! itself once a custom rate is
/// dialled in, so this pipeline doesn't treat it as clean personalisation signal either.
///
/// HO/IN (chart-rewriting mods) and any fine-grained custom rate are excluded outright rather than
/// approximated - see the broad-phase design discussion (2026-08-20) for why: their true difficulty can
/// diverge from the map's stored NoMod <c>StarRating</c> in an unpredictable direction, which the
/// broad-phase pre-filter relies on being at least roughly rank-preserving.
///
/// Anything else present (HD, FL, HR, EZ, Mirror, Random, ...) disqualifies the score outright -
/// deliberately strict, since the whole point is a clean accuracy signal against a chart sunny
/// actually saw.
/// </summary>
public static class PersonalSunnyModWhitelist
{
    private static readonly HashSet<string> allowed_acronyms = new() { "DT", "HT", "NC", "DC" };

    /// <summary>True if every mod on the score is in the whitelist and at its default (osu!-ranked) rate.</summary>
    public static bool IsAllowed(IReadOnlyList<Mod> mods) =>
        mods.All(m => allowed_acronyms.Contains(m.Acronym) && m.Ranked);

    /// <summary>
    /// The exact rate (1.0 if no rate mod) and the chart mod acronym, or null. <see cref="ChartMod"/> is
    /// vestigial now that <see cref="IsAllowed"/> rejects HO/IN outright - always null in practice - but
    /// the field stays so a stored queue/cache entry's shape doesn't need to change again if a future
    /// chart-rewriting mod is ever allowed back in.
    /// </summary>
    public static (double Rate, string? ChartMod) Describe(IReadOnlyList<Mod> mods)
    {
        double rate = ModUtils.CalculateRateWithMods(mods);
        string? chartMod = mods.FirstOrDefault(m => m.Acronym is "HO" or "IN")?.Acronym;

        return (rate, chartMod);
    }

    /// <summary>
    /// Rebuilds a mod list from a stored (rate, chart mod) pair, for baking. Which literal rate mod
    /// (DT vs NC, HT vs DC) produced the rate doesn't matter - only <see cref="ModUtils.CalculateRateWithMods"/>'s
    /// result does, so this always picks the DT/HT family by sign. <paramref name="chartMod"/> is always
    /// null coming from a current <see cref="IsAllowed"/>-filtered entry, but old cache/queue entries
    /// persisted before 2026-08-20 may still carry "HO"/"IN" - handled here so those don't hard-fail.
    /// </summary>
    public static IReadOnlyList<Mod> Reconstruct(double rate, string? chartMod)
    {
        var mods = new List<Mod>();

        if (rate > 1.0)
            mods.Add(new ManiaModDoubleTime { SpeedChange = { Value = Math.Clamp(rate, 1.01, 2.0) } });
        else if (rate < 1.0)
            mods.Add(new ManiaModHalfTime { SpeedChange = { Value = Math.Clamp(rate, 0.5, 0.99) } });

        if (chartMod == "HO")
            mods.Add(new ManiaModHoldOff());
        else if (chartMod == "IN")
            mods.Add(new ManiaModInvert());

        return mods;
    }
}
