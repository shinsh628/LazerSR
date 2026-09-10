// Port of mania-hub live-backend/src/dan/dan-estimator.ts
//
// Part of the DEPRECATED estimateDan benchmark path (see PORTING.md). Not wired
// into the production DanClassifier — ported for parity only.
//
// `estimateDan(map, input)` + `estimateNormalSkillSrAdjustment` (the ~40
// compression/boost rules) + `getRatingFamily`.

using LazerSR.DanCalculator.Beatmap;
using LazerSR.DanCalculator.Classifier;
using LazerSR.DanCalculator.Features;
using LazerSR.DanCalculator.Types;

namespace LazerSR.DanCalculator.Benchmark;

public static class DanEstimator
{
    private sealed class NormalSkillSrAdjustment
    {
        public double Compression;
        public double Boost;
    }

    // DanPrimaryFamily -> DanSkillFamily (jumpstream folds onto handstream for rating).
    private static DanSkillFamily GetRatingFamily(DanSkillFamily family)
        => family == DanSkillFamily.Jumpstream ? DanSkillFamily.Handstream : family;

    private static NormalSkillSrAdjustment EstimateNormalSkillSrAdjustment(
        DanFeatureMetrics metrics,
        double starRating,
        DanSkillFamily family,
        double skillSr,
        double rate)
    {
        double baseRawDan = Labels.SrToRawDan(skillSr, family);
        double compactMidChordTechDrillCompression = family == DanSkillFamily.Tech
            && metrics.HoldRatio < 0.08
            && metrics.ChordRatio >= 0.38
            && metrics.ChordRatio <= 0.65
            && metrics.RowBurstPressure <= 18
            && metrics.PeakNps5s <= 29
            && metrics.JackPressure <= 140
            && starRating >= 5
            && starRating <= 6.35
            ? 1
            : 0;
        double repeatedLowChordStreamCompression = family == DanSkillFamily.Stream
            && metrics.HoldRatio < 0.08
            && metrics.ChordRatio <= 0.36
            && metrics.ActiveNps <= 20
            && metrics.RhythmMotifRepeatRatio >= 0.6
            && metrics.PeakNps5s <= 27.8
            && starRating <= 5.8
            ? 0.55
            : 0;
        double repeatedCompactJumpstreamCompression = family == DanSkillFamily.Jumpstream
            && metrics.HoldRatio < 0.08
            && metrics.ChordRatio >= 0.32
            && metrics.ChordRatio <= 0.56
            && metrics.RhythmMotifRepeatRatio >= 0.6
            && starRating <= 6.35
            ? 0.75
            : 0;
        double repeatedCompactHandstreamCompression = family == DanSkillFamily.Handstream
            && metrics.HoldRatio < 0.08
            && metrics.ChordRatio >= 0.38
            && metrics.ChordRatio <= 0.56
            && metrics.RhythmMotifRepeatRatio >= 0.6
            && starRating <= 6.45
            ? 1.2
            : 0;
        double lowMidChordjackOvercallCompression = family == DanSkillFamily.Chordjack
            && metrics.HoldRatio < 0.08
            && metrics.ChordRatio >= 0.35
            && metrics.ChordRatio <= 0.6
            && metrics.PeakNps5s <= 24.8
            && metrics.SustainedNps10s <= 24.2
            && starRating <= 5.6
            ? 0.45
            : 0;
        double lowSustainTechValleyCompression = family == DanSkillFamily.Tech
            && metrics.Nps5sP50 <= 16
            && starRating <= 6.4
            ? 0.4
            : 0;
        double lowBandStaminaDrillCompression = family == DanSkillFamily.Stamina
            && starRating <= 5.5
            && metrics.RowIntervalEntropy <= 1.6
            ? 0.25
            : 0;
        double repeatedHighChordJackWallCompression = family == DanSkillFamily.Jack
            && metrics.HoldRatio < 0.08
            && metrics.ChordRatio >= 0.72
            && metrics.RhythmMotifRepeatRatio >= 0.27
            && skillSr <= 7.4
            && baseRawDan >= 13
            ? 0.4
            : 0;
        double lowBurstStreamDrillCompression = family == DanSkillFamily.Stream
            && metrics.HoldRatio < 0.08
            && metrics.ChordRatio <= 0.36
            && metrics.RowBurstPressure <= 18
            && skillSr - starRating >= 1.2
            ? 0.7
            : 0;
        double repeatedLowBurstStreamCompression = family == DanSkillFamily.Stream
            && metrics.HoldRatio < 0.08
            && metrics.ActiveNps <= 20
            && metrics.RowBurstPressure <= 18
            && metrics.RhythmMotifRepeatRatio >= 0.5
            ? 0.9
            : 0;
        double baseCompression = Max(
            compactMidChordTechDrillCompression,
            repeatedLowChordStreamCompression,
            repeatedCompactJumpstreamCompression,
            repeatedCompactHandstreamCompression,
            lowMidChordjackOvercallCompression,
            lowSustainTechValleyCompression,
            lowBandStaminaDrillCompression,
            repeatedHighChordJackWallCompression,
            lowBurstStreamDrillCompression,
            repeatedLowBurstStreamCompression);
        double baseCompressedSr = Math.Max(0, skillSr - baseCompression);
        double lowJackHighOverTechCompression = family == DanSkillFamily.Tech
            && metrics.HoldRatio < 0.12
            && baseCompressedSr >= 7.45
            && baseCompressedSr - starRating >= 1.6
            && metrics.JackPressure <= 120
            ? 1.5
            : 0;
        double variedChordSwitchTechCompression = family == DanSkillFamily.Tech
            && metrics.HoldRatio < 0.08
            && metrics.PatternVariety >= 3.04
            && metrics.ChordSizeChangeRate >= 0.56
            ? 0.3
            : 0;
        double lowEntropyMidChordJackCompression = family == DanSkillFamily.Jack
            && metrics.HoldRatio < 0.08
            && metrics.ChordRatio <= 0.84
            && metrics.RowIntervalEntropy <= 0.55
            ? 0.5
            : 0;
        double activeChordjackSwitchCompression = family == DanSkillFamily.Chordjack
            && metrics.HoldRatio < 0.08
            && baseCompressedSr >= 6.55
            && metrics.DirectionChangeRate >= 0.67
            ? 0.2
            : 0;
        double postCompression = Max(
            lowJackHighOverTechCompression,
            variedChordSwitchTechCompression,
            lowEntropyMidChordJackCompression,
            activeChordjackSwitchCompression);
        double postCompressedSr = Math.Max(0, skillSr - baseCompression - postCompression);
        double simpleLowPatternJackFineCompression = family == DanSkillFamily.Jack
            && starRating <= 5.91
            && metrics.PatternVariety <= 1.9
            && metrics.SustainedPressureRatio <= 0.86
            ? 0.4
            : 0;
        double midChordSustainedJackFineCompression = family == DanSkillFamily.Jack
            && metrics.ChordRatio <= 0.63
            && metrics.SustainedPressureRatio >= 0.86
            ? 1.1
            : 0;
        double moderateBurstJackFineCompression = family == DanSkillFamily.Jack
            && metrics.Nps5sP95 >= 28.2
            && metrics.SustainedNps10s <= 28.6
            ? 1.1
            : 0;
        double denseHighChordjackFineCompression = family == DanSkillFamily.Chordjack
            && metrics.ChordRatio >= 0.89
            && metrics.Nps5sP50 <= 21.6
            ? 0.4
            : 0;
        double lowPressureChordjackFineCompression = family == DanSkillFamily.Chordjack
            && starRating >= 5
            && starRating <= 5.5
            && metrics.NoteCount >= 1700
            && metrics.NoteCount <= 2300
            && metrics.ChordRatio >= 0.58
            && metrics.ChordRatio <= 0.66
            && metrics.HoldRatio < 0.03
            && metrics.JackPressure <= 135
            && metrics.Nps5sP50 <= 16.5
            && metrics.PeakNps5s <= 23
            && metrics.SustainedNps10s <= 22
            && metrics.RowBurstPressure <= 18
            && metrics.SustainedPressureRatio <= 0.72
            ? 0.85
            : 0;
        double lowStarOverStreamFineCompression = family == DanSkillFamily.Stream
            && starRating <= 5.35
            && postCompressedSr - starRating >= 1.7
            ? 0.3
            : 0;
        double heldHandstreamFineCompression = family == DanSkillFamily.Handstream
            && starRating >= 5.2
            && metrics.HoldRatio >= 0.04
            ? 0.4
            : 0;
        double repetitiveHighChordTechFineCompression = family == DanSkillFamily.Tech
            && metrics.ChordRatio >= 0.5
            && metrics.RhythmMotifRepeatRatio >= 0.68
            ? 0.7
            : 0;
        double compactHighPeakTechFineCompression = family == DanSkillFamily.Tech
            && postCompressedSr <= 7.75
            && metrics.PeakNps5s >= 29.6
            ? 0.7
            : 0;
        double lowSustainRatioTechFineCompression = family == DanSkillFamily.Tech
            && metrics.FastRowRatio >= 0.4
            && metrics.FastRowRatio <= 0.56
            && metrics.SustainedPressureRatio <= 0.82
            ? 0.5
            : 0;
        double fineCompression = Max(
            simpleLowPatternJackFineCompression,
            midChordSustainedJackFineCompression,
            moderateBurstJackFineCompression,
            denseHighChordjackFineCompression,
            lowPressureChordjackFineCompression,
            lowStarOverStreamFineCompression,
            heldHandstreamFineCompression,
            repetitiveHighChordTechFineCompression,
            compactHighPeakTechFineCompression,
            lowSustainRatioTechFineCompression);
        double fineCompressedSr = Math.Max(0, skillSr - baseCompression - postCompression - fineCompression);
        double rateSensitiveChordjackLateCompression = family == DanSkillFamily.Chordjack
            && metrics.ChordRatio >= 0.75
            && metrics.ChordRatio <= 0.82
            && metrics.RowBurstPressure >= 24
            && metrics.PeakNps5s >= 26.5
            ? 0.7
            : 0;
        double lowActiveAlphaStreamLateCompression = family == DanSkillFamily.Stream
            && starRating >= 5.65
            && starRating <= 5.8
            && metrics.ActiveNps <= 16.2
            && metrics.ChordRatio >= 0.23
            ? 1.1
            : 0;
        double midP50BetaStreamLateCompression = family == DanSkillFamily.Stream
            && starRating >= 5.65
            && starRating <= 5.8
            && metrics.Nps5sP50 >= 21
            && metrics.Nps5sP50 < 22
            && metrics.ChordRatio <= 0.22
            ? 0.7
            : 0;
        double burstyLowMedianStreamLateCompression = family == DanSkillFamily.Stream
            && starRating >= 5.4
            && starRating <= 5.8
            && metrics.NoteCount >= 1700
            && metrics.NoteCount <= 2300
            && metrics.ChordRatio >= 0.18
            && metrics.ChordRatio <= 0.3
            && metrics.Nps5sP50 <= 12
            && metrics.SustainedNps10s >= 24.5
            && metrics.SustainedNps10s <= 26.5
            && metrics.ActiveNps <= 17
            ? 0.45
            : 0;
        double longLowStarStreamLateCompression = family == DanSkillFamily.Stream
            && starRating <= 5.3
            && metrics.NoteCount >= 4000
            ? 0.4
            : 0;
        double lowStarTechAlphaLateCompression = family == DanSkillFamily.Tech
            && starRating <= 5.05
            && fineCompressedSr >= 6.6
            && fineCompressedSr <= 7
            ? 0.4
            : 0;
        double gammaTechLateCompression = family == DanSkillFamily.Tech
            && starRating <= 5.6
            && fineCompressedSr >= 7.45
            && metrics.Nps5sP50 <= 21.4
            && metrics.PatternVariety >= 3
            ? 0.4
            : 0;
        double betaTechLowBurstLateCompression = family == DanSkillFamily.Tech
            && starRating >= 5.7
            && starRating <= 5.9
            && metrics.PatternVariety >= 3.5
            && metrics.Nps5sP50 <= 18.7
            && metrics.RowBurstPressure <= 22
            ? 1.1
            : 0;
        double betaTechHighBurstLateCompression = family == DanSkillFamily.Tech
            && starRating >= 5.7
            && starRating <= 5.9
            && metrics.PatternVariety >= 3.5
            && metrics.Nps5sP50 <= 17
            && metrics.RowBurstPressure >= 30
            ? 1.5
            : 0;
        double staminaAlphaLateCompression = family == DanSkillFamily.Stamina
            && starRating <= 5.8
            && metrics.ChordRatio >= 0.45
            && metrics.ChordRatio <= 0.55
            && metrics.SustainedPressureRatio <= 0.8
            ? 0.4
            : 0;
        double heldVariedHighChordjackLateCompression = family == DanSkillFamily.Chordjack
            && metrics.HoldRatio >= 0.02
            && metrics.ChordRatio >= 0.88
            && metrics.ChordRatio <= 0.91
            && metrics.Nps5sP50 >= 24.5
            && metrics.Nps5sP50 <= 25.5
            && metrics.PatternVariety >= 2.2
            && metrics.SustainedPressureRatio <= 0.8
            ? 1.1
            : 0;
        double lateCompression = Max(
            rateSensitiveChordjackLateCompression,
            lowActiveAlphaStreamLateCompression,
            midP50BetaStreamLateCompression,
            burstyLowMedianStreamLateCompression,
            longLowStarStreamLateCompression,
            lowStarTechAlphaLateCompression,
            gammaTechLateCompression,
            betaTechLowBurstLateCompression,
            betaTechHighBurstLateCompression,
            staminaAlphaLateCompression,
            heldVariedHighChordjackLateCompression);
        double compression = baseCompression + postCompression + fineCompression + lateCompression;
        double compressedSr = Math.Max(0, skillSr - compression);
        double compressedRawDan = Labels.SrToRawDan(compressedSr, family);
        double lowBandTechnicalFloorBoost = family == DanSkillFamily.Tech
            && compressedSr - starRating <= 1.2
            && metrics.PeakNps5s <= 24
            && starRating >= 4.8
            && starRating <= 5.6
            ? 0.4
            : 0;
        double moderateJackFloorBoost = family == DanSkillFamily.Jack
            && compressedRawDan <= 11.5
            && metrics.Nps5sP50 >= 18
            && starRating >= 5.2
            && starRating <= 6.1
            ? 0.4
            : 0;
        double shortLowStreamFineBoost = family == DanSkillFamily.Stream
            && metrics.NoteCount <= 1800
            && starRating <= 5.35
            && compressedSr <= 6.2
            ? 0.2
            : 0;
        double hybridTechFloorFineBoost = family == DanSkillFamily.Tech
            && metrics.HoldRatio >= 0.28
            && starRating <= 5.2
            && metrics.PatternVariety >= 3.3
            ? 1.3
            : 0;
        double gammaTechLowChordLateBoost = family == DanSkillFamily.Tech
            && starRating <= 5.7
            && metrics.ChordRatio <= 0.3
            && metrics.Nps5sP50 >= 22
            ? 0.2
            : 0;
        double lowChordjackLateBoost = family == DanSkillFamily.Chordjack
            && metrics.ChordRatio >= 0.5
            && metrics.ChordRatio <= 0.6
            && metrics.ActiveNps <= 15
            && metrics.SustainedNps10s <= 23
            ? 0.2
            : 0;
        double compactRepeatedGammaStreamLateBoost = family == DanSkillFamily.Stream
            && starRating >= 5.3
            && starRating <= 5.45
            && metrics.NoteCount >= 2000
            && metrics.NoteCount <= 2300
            && metrics.ChordRatio >= 0.14
            && metrics.ChordRatio <= 0.17
            && metrics.Nps5sP50 >= 21
            && metrics.Nps5sP50 <= 22
            && metrics.SustainedNps10s >= 24.5
            && metrics.SustainedNps10s <= 25.5
            && metrics.AdjacentMotifRepeatRatio >= 0.08
            ? 1
            : 0;
        double steadyLowRateJumpstreamFloorBoost = family == DanSkillFamily.Jumpstream
            && starRating <= 4.7
            && metrics.NoteCount >= 3600
            && metrics.NoteCount <= 5000
            && metrics.ChordRatio >= 0.42
            && metrics.ChordRatio <= 0.52
            && metrics.TwoNoteChordRatio >= 0.2
            && metrics.HoldRatio < 0.03
            && metrics.JackPressure < 130
            && metrics.PeakNps5s <= 21
            && metrics.SustainedNps10s <= 20
            && metrics.ActiveNps <= 16.5
            && metrics.RowBurstPressure <= 14
            && metrics.RhythmMotifRepeatRatio >= 0.55
            ? 0.55
            : 0;
        double lowRateHighChordWallFloorBoost = family == DanSkillFamily.Chordjack
            && rate <= 0.85
            && metrics.NoteCount >= 2200
            && metrics.NoteCount <= 2350
            && metrics.ChordRatio >= 0.76
            && metrics.ChordRatio <= 0.82
            && metrics.HoldRatio < 0.03
            && metrics.PeakNps5s >= 22.8
            && metrics.PeakNps5s <= 23.8
            && metrics.SustainedNps10s >= 21.6
            && metrics.SustainedNps10s <= 22.8
            && metrics.JackPressure >= 110
            && metrics.JackPressure <= 122
            && metrics.RowBurstPressure >= 22
            && metrics.RowBurstPressure <= 24
            ? 0.38
            : 0;

        return new NormalSkillSrAdjustment
        {
            Compression = compression,
            Boost = Max(
                lowBandTechnicalFloorBoost,
                moderateJackFloorBoost,
                shortLowStreamFineBoost,
                hybridTechFloorFineBoost,
                gammaTechLowChordLateBoost,
                lowChordjackLateBoost,
                compactRepeatedGammaStreamLateBoost,
                steadyLowRateJumpstreamFloorBoost,
                lowRateHighChordWallFloorBoost),
        };
    }

    // Math.max(...args) — variadic max, JS empty-array would be -Infinity but every
    // call site here passes at least one arg.
    private static double Max(params double[] values)
    {
        double m = double.NegativeInfinity;
        foreach (var v in values) m = Math.Max(m, v);
        return m;
    }

    public static DanEstimate EstimateDan(ManiaBeatmap map, DanEstimateInput? input = null)
    {
        input ??= new DanEstimateInput();
        if (map.KeyCount != 4)
        {
            throw new InvalidOperationException("Dan estimates are currently only supported for 4K beatmaps.");
        }

        double rate = Labels.GetInputRate(input);
        var features = DanFeatures.ExtractDanFeatures(map, input, rate);
        var notes = features.Notes;
        double durationMs = features.DurationMs;
        var orderedRows = features.OrderedRows;
        var metrics = features.Metrics;
        var warnings = new List<string>(features.Warnings);
        if (metrics.HoldRatio > 0.28)
        {
            warnings.Add("This looks LN-heavy; using LN dan calibration when chart pressure is strong.");
        }

        double baseStarRating = input.StarRating.HasValue && double.IsFinite(input.StarRating.Value)
            ? Math.Max(0, input.StarRating ?? 0)
            : 0;
        double starRating = baseStarRating > 0 ? baseStarRating * Math.Pow(rate, 0.7) : 0;
        var scoring = Scoring.EstimateFamilyScores(metrics, starRating, durationMs);
        var skillScores = scoring.SkillScores;
        var lnEstimate = LnDan.EstimateLnDan(map, input, metrics, starRating, durationMs, rate);
        if (lnEstimate != null)
        {
            var lnSkillScores = skillScores.Clone();
            lnSkillScores[DanSkillFamily.Ln] = lnEstimate.EstimatedSr;
            return new DanEstimate
            {
                Label = lnEstimate.Label,
                Variant = lnEstimate.Variant,
                DisplayName = lnEstimate.DisplayName,
                RawDan = lnEstimate.RawDan,
                EstimatedSr = lnEstimate.EstimatedSr,
                Family = DanSkillFamily.Ln,
                Confidence = lnEstimate.Confidence,
                Metrics = metrics,
                SkillScores = lnSkillScores,
                Warnings = warnings,
                Debug = new DanEstimateDebug
                {
                    Scoring = scoring.Debug,
                    FamilyChoice = new DanFamilyChoiceDebug
                    {
                        TopFamily = DanSkillFamily.Ln,
                        TopScore = lnEstimate.EstimatedSr,
                        SelectedFamily = DanSkillFamily.Ln,
                        Reason = lnEstimate.Reason,
                    },
                },
            };
        }

        var familyChoice = FamilyChoice.ChooseSkillFamily(skillScores, metrics);
        var skillFamily = familyChoice.Family;
        var ratingFamily = GetRatingFamily(skillFamily);
        bool isCourse = Courses.IsDanCourse(input, orderedRows, durationMs, notes.Count);
        var family = isCourse ? DanSkillFamily.Dan : skillFamily;
        double unadjustedEstimatedSr = isCourse
            ? Courses.EstimateDanCourseSr(metrics, starRating, skillScores[ratingFamily])
            : skillScores[ratingFamily];
        var normalSkillSrAdjustment = isCourse
            ? new NormalSkillSrAdjustment { Compression = 0, Boost = 0 }
            : EstimateNormalSkillSrAdjustment(metrics, starRating, ratingFamily, unadjustedEstimatedSr, rate);
        double estimatedSr = Math.Max(
            0,
            unadjustedEstimatedSr - normalSkillSrAdjustment.Compression + normalSkillSrAdjustment.Boost);
        double rawDan = Labels.SrToRawDan(estimatedSr, ratingFamily, calibrate: !isCourse);
        var parsed = Labels.ParseDan(rawDan);
        double confidence = Math.Max(
            0.15,
            Math.Min(
                0.92,
                0.55
                  + Math.Min(0.18, notes.Count / 6000.0)
                  + (map.KeyCount == 4 ? 0.12 : -0.1)
                  + (starRating > 0 ? 0.07 : -0.05)
                  - (metrics.HoldRatio > 0.45 ? 0.18 : 0)));

        DanScoringDebug scoringDebug = scoring.Debug;
        if (normalSkillSrAdjustment.Compression != 0 || normalSkillSrAdjustment.Boost != 0)
        {
            // { ...scoring.debug, terms: { ...scoring.debug.terms, normalSkillSr* } }
            var mergedTerms = new Dictionary<string, double>(scoring.Debug.Terms)
            {
                ["normalSkillSrCompression"] = normalSkillSrAdjustment.Compression,
                ["normalSkillSrBoost"] = normalSkillSrAdjustment.Boost,
            };
            scoringDebug = new DanScoringDebug
            {
                DensitySr = scoring.Debug.DensitySr,
                StaminaSr = scoring.Debug.StaminaSr,
                StructuralSr = scoring.Debug.StructuralSr,
                Base = scoring.Debug.Base,
                LnNerf = scoring.Debug.LnNerf,
                Gates = scoring.Debug.Gates,
                Terms = mergedTerms,
                Contributions = scoring.Debug.Contributions,
            };
        }

        return new DanEstimate
        {
            Label = parsed.Label,
            Variant = parsed.Variant,
            DisplayName = parsed.DisplayName,
            RawDan = rawDan,
            EstimatedSr = estimatedSr,
            Family = family,
            Confidence = confidence,
            Metrics = metrics,
            SkillScores = skillScores,
            Warnings = warnings,
            Debug = new DanEstimateDebug
            {
                Scoring = scoringDebug,
                FamilyChoice = familyChoice.Debug,
            },
        };
    }
}
