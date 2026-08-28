using System;

namespace LazerSR.Hook.PersonalSunny;

/// <summary>
/// One entry in the top-<see cref="PersonalSunnyTopPoolStore.Capacity"/> "skill ceiling" pool - same
/// shape as <see cref="PersonalSunnyQueueEntry"/> plus the sunny SR that earned it a slot.
/// </summary>
public record PersonalSunnyTopPoolEntry(string BeatmapMd5, double Rate, string? ChartMod, double Sr, double Accuracy, DateTimeOffset EndedAt)
{
    public PersonalSunnyJacKey Key => new(BeatmapMd5, Rate, ChartMod);

    /// <summary>
    /// What <see cref="PersonalSunnyTopPoolStore"/> actually ranks/evicts by, and what
    /// <c>PersonalSunnyService</c>'s recent-pool floor compares against - not raw <see cref="Sr"/>.
    /// </summary>
    public double Performance => ComputePerformance(Sr, Accuracy);

    /// <summary>
    /// A PP-shaped value from our own sunny SR - not real osu! PP (that needs official
    /// <c>DifficultyAttributes</c>, which broad-phase never computes), just the same curve applied to
    /// sunny SR instead: steep power-law difficulty scaling, and a linear 80%-&gt;100% accuracy ramp
    /// that's exactly zero below 80%. Constants reused as-is from
    /// <c>osu.Game.Rulesets.Mania.Difficulty.ManiaPerformanceCalculator.computeDifficultyValue</c> (its
    /// length-bonus term is dropped - irrelevant to what this is used for). See the 2026-08-21 design
    /// discussion: this single formula both ranks Pool A (naturally zeroing out sub-80% flukes instead of
    /// needing a separate accuracy floor) and gates Pool B (relative to Pool A's own ceiling).
    /// </summary>
    public static double ComputePerformance(double sr, double accuracy)
    {
        double difficultyTerm = 8.0 * Math.Pow(Math.Max(sr - 0.15, 0.05), 2.2);
        double accuracyTerm = Math.Max(0.0, 5.0 * accuracy - 4.0);
        return difficultyTerm * accuracyTerm;
    }
}
