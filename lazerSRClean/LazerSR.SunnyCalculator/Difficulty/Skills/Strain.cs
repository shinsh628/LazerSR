// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using LazerSR.SunnyCalculator.Tuning;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Rulesets.Mania.Difficulty.Evaluators;
using osu.Game.Rulesets.Mania.Difficulty.Preprocessing;
using osu.Game.Rulesets.Mania.Difficulty.Utils;
using osu.Game.Rulesets.Mods;

namespace osu.Game.Rulesets.Mania.Difficulty.Skills
{
    // Intentionally does NOT inherit osu!'s StrainSkill/Skill. osu! reshaped that class
    // hierarchy's per-object strain storage (StrainSkill.ObjectStrains/GetObjectStrains ->
    // Skill.ObjectDifficulties/GetObjectDifficulties) in 2026.7xx, which broke this class when
    // it inherited it. Section-peak tracking from StrainSkill was never used by sunnySR's own
    // DifficultyValue() below, so it isn't replicated here.
    public class Strain
    {
        // Difficulty calculation weights - see SunnyConstants for the originals and their diffs.
        private static double[] difficultyPercentilesHigh => SunnyConstants.HighPercentiles;
        private static double[] difficultyPercentilesMid => SunnyConstants.MidPercentiles;

        private const double rescale_high_threshold = 9.0;
        private const double rescale_high_factor = 1.2;

        private readonly List<double> objectStrains = new List<double>();

        private double currentStrain;
        private double currentNoteCount;
        private double currentLongNoteWeight;

        /// <summary>
        /// When set, replaces the note count fed to the short map nerf below. Used to rate a slice of a
        /// map as if it carried the whole map's length, so that a selected section isn't nerfed for being
        /// short. Null (the default) keeps the original behaviour for every other caller.
        /// </summary>
        private readonly double? weightedNoteCountOverride;

        public Strain(Mod[] mods, double? weightedNoteCountOverride = null)
        {
            this.weightedNoteCountOverride = weightedNoteCountOverride;
        }

        public void Process(ManiaDifficultyHitObject current)
        {
            objectStrains.Add(StrainValueAt(current));
        }

        public IReadOnlyList<double> GetObjectStrains() => objectStrains;

        public double DifficultyValue()
        {
            double[] sorted = objectStrains.Where(s => s > 0).ToArray();
            if (sorted.Length == 0) return 0.0;

            Array.Sort(sorted);

            double highPercentileMean = DifficultyValueUtils.CalculatePercentileMean(sorted, difficultyPercentilesHigh);
            double midPercentileMean = DifficultyValueUtils.CalculatePercentileMean(sorted, difficultyPercentilesMid);
            double powerMean = DifficultyValueUtils.CalculatePowerMean(sorted, SunnyConstants.PowerMeanLambda);

            double rawDifficulty = SunnyConstants.HighPercentileWeight * highPercentileMean +
                                   SunnyConstants.MidPercentileWeight * midPercentileMean +
                                   SunnyConstants.PowerMeanWeight * powerMean;

            double weightedNoteCount = getWeightedNoteCount();

            // Short map nerf
            double scaled = rawDifficulty * weightedNoteCount / (weightedNoteCount + 60.0);

            // 시프트된 상수 세트(만인 sunny+ 활성 또는 개인화 격리 스코프)에서만 커스텀 억제를 탄다.
            // 체크박스를 꺼서 스톡 sunny로 돌아온 경우엔 바닐라 파이프라인 그대로 — 고SR rescale.
            if (DiffCombiner.UniversalDiffEnabled || SunnyConstants.IsIsolatedScopeActive)
            {
                // Temporary nerf for the shifted constant set. Must run before the high-end rescale.
                scaled = SunnyTempNerf.Apply(scaled);
            }
            else if (scaled > rescale_high_threshold)
            {
                // Adjust high-end star ratings slightly (stock sunny)
                scaled = rescale_high_threshold + (scaled - rescale_high_threshold) / rescale_high_factor;
            }

            return scaled;
        }

        private double StrainValueAt(ManiaDifficultyHitObject current)
        {
            ManiaDifficultyHitObject maniaCurrent = current;
            ManiaDifficultyHitObject? prev = current.Previous(0);

            currentNoteCount++;

            if (maniaCurrent.IsLong)
            {
                double longNoteDuration = Math.Min(maniaCurrent.EndTime - maniaCurrent.StartTime, 1000.0);
                currentLongNoteWeight += 0.5 * longNoteDuration / 200.0;
            }

            if (prev != null && prev.StartTime == maniaCurrent.StartTime)
                return currentStrain;

            currentStrain = StrainEvaluator.EvaluateDifficultyOf(maniaCurrent);
            return currentStrain;
        }

        private double getWeightedNoteCount()
        {
            // SunnyManiaDifficultyCalculator.CalculateWeightedNoteCount mirrors the accumulation below.
            // Keep the two in sync if the long note weighting ever changes.
            return weightedNoteCountOverride ?? currentNoteCount + currentLongNoteWeight;
        }
    }
}
