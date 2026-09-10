// Port of mania-hub live-backend/src/dan/dan-estimator/scoring.ts
//
// Part of the deprecated estimateDan benchmark path (see PORTING.md). Not wired
// into the production ClassifyChart. Faithful 1:1 mechanical port of hundreds of
// threshold rules and magic constants — every condition, coefficient and comment
// is transcribed verbatim.
//
// JS conventions applied (see PORTING.md):
//   clamp01/gateWhen/minGate      -> DanMath.Clamp01 / GateWhen / MinGate (via `using static`)
//   Math.min/max/abs              -> Math.Min / Max / Abs
//   Record<DanSkillFamily,number> -> SkillScores
//   number                        -> double
//   object literal returned       -> DanFamilyScoreResult (below)

using LazerSR.DanCalculator.Types;
using static LazerSR.DanCalculator.Features.DanMath;

namespace LazerSR.DanCalculator.Benchmark;

/// <summary>
/// estimateFamilyScores return shape. `dan-estimator.ts` destructures
/// `scoring.skillScores` and `scoring.debug`.
/// </summary>
public sealed class DanFamilyScoreResult
{
    public SkillScores SkillScores = new();
    public DanScoringDebug Debug = new();
}

public static class Scoring
{
    private static class BasePressureCalibration
    {
        public const double DensityBase = 2.45;
        public const double Peak5sWeight = 0.095;
        public const double Peak1sWeight = 0.018;
        public const double StaminaBase = 2.65;
        public const double Sustained10sWeight = 0.16;
    }

    public static DanFamilyScoreResult EstimateFamilyScores(DanFeatureMetrics metrics, double starRating, double durationMs)
    {
        double densitySr = BasePressureCalibration.DensityBase
            + metrics.PeakNps5s * BasePressureCalibration.Peak5sWeight
            + metrics.PeakNps1s * BasePressureCalibration.Peak1sWeight;
        double staminaSr = BasePressureCalibration.StaminaBase + metrics.SustainedNps10s * BasePressureCalibration.Sustained10sWeight;
        double structuralSr = Math.Max(densitySr, staminaSr);
        double @base = structuralSr;
        double lnNerf = metrics.HoldRatio > 0.45 ? 0.72 : metrics.HoldRatio > 0.34 ? 0.76 : metrics.HoldRatio > 0.28 ? 0.84 : 1;
        double chordGate = Clamp01((metrics.ChordRatio - 0.18) / 0.34);
        double chordedSpeedGate = Clamp01((metrics.ChordRatio - 0.12) / 0.22);
        double denseChordedSpeedGate = Clamp01((metrics.ChordRatio - 0.32) / 0.28);
        double highChordGate = Clamp01((metrics.ChordRatio - 0.5) / 0.2);
        double denseChordWallGate = Clamp01((metrics.ChordRatio - 0.78) / 0.08);
        double denseJackFileGate = GateWhen(metrics.NoteCount >= 1800
            && metrics.NoteCount <= 3200
            && metrics.ChordRatio >= 0.54
            && metrics.ChordRatio <= 0.72
            && metrics.HoldRatio < 0.06
            && metrics.JackPressure >= 130,
          MinGate(
            (metrics.JackPressure - 125) / 55,
            (metrics.ChordRatio - 0.52) / 0.08,
            (0.74 - metrics.ChordRatio) / 0.08,
            (3200 - metrics.NoteCount) / 700));
        double denseWallJackGate = GateWhen(metrics.NoteCount >= 1800
            && metrics.ChordRatio >= 0.78
            && metrics.HoldRatio < 0.08
            && metrics.JackPressure >= 145
            && metrics.SustainedNps10s >= 23,
          MinGate(
            (metrics.NoteCount - 1700) / 300,
            (metrics.ChordRatio - 0.76) / 0.08,
            (metrics.JackPressure - 142) / 18,
            (metrics.SustainedNps10s - 22) / 2.5));
        double compactJackUnderrateGate = GateWhen(metrics.NoteCount >= 1800
            && metrics.NoteCount <= 2700
            && metrics.ChordRatio >= 0.52
            && metrics.ChordRatio <= 0.7
            && metrics.HoldRatio < 0.08
            && metrics.JackPressure >= 165
            && metrics.SustainedNps10s >= 25
            && starRating >= 5.7
            && starRating <= 6.25,
          Clamp01(0.45 + MinGate(
            (metrics.NoteCount - 1750) / 350,
            (2700 - metrics.NoteCount) / 600,
            (metrics.ChordRatio - 0.52) / 0.08,
            (0.72 - metrics.ChordRatio) / 0.12,
            (metrics.JackPressure - 160) / 16,
            (6.3 - starRating) / 0.25) * 0.55));
        double slowRepetitiveJackstreamGate = GateWhen(metrics.NoteCount >= 1800
            && metrics.NoteCount <= 3200
            && metrics.ChordRatio >= 0.45
            && metrics.ChordRatio <= 0.6
            && metrics.HoldRatio < 0.06
            && metrics.JackPressure >= 115
            && metrics.ChordjackPressure >= 105
            && metrics.SustainedNps10s >= 16
            && metrics.SustainedNps10s <= 22
            && metrics.FastRowRatio < 0.08
            && metrics.RowIntervalEntropy < 1.6
            && metrics.SustainedPressureRatio >= 0.65,
          MinGate(
            (metrics.JackPressure - 110) / 25,
            (metrics.ChordRatio - 0.44) / 0.08,
            (0.62 - metrics.ChordRatio) / 0.08,
            (1.65 - metrics.RowIntervalEntropy) / 0.7,
            (22.5 - metrics.SustainedNps10s) / 4));
        double ratedRepetitiveSpeedjackGate = GateWhen(metrics.NoteCount >= 1800
            && metrics.NoteCount <= 3200
            && metrics.ChordRatio >= 0.45
            && metrics.ChordRatio <= 0.6
            && metrics.HoldRatio < 0.06
            && metrics.JackPressure >= 150
            && metrics.ChordjackPressure >= 150
            && metrics.SustainedNps10s >= 22.5
            && metrics.SustainedNps10s <= 30
            && metrics.FastRowRatio < 0.1
            && metrics.RowIntervalEntropy < 1.7
            && metrics.SustainedPressureRatio >= 0.65,
          MinGate(
            (metrics.JackPressure - 145) / 30,
            (metrics.ChordjackPressure - 145) / 35,
            (metrics.SustainedNps10s - 22) / 3,
            (30.5 - metrics.SustainedNps10s) / 4,
            (1.75 - metrics.RowIntervalEntropy) / 0.7));
        double handstreamChordGate = MinGate(
            (metrics.ChordRatio - 0.28) / 0.14,
            (0.64 - metrics.ChordRatio) / 0.14);
        double jumpstreamChordGate = MinGate(
            (metrics.TwoNoteChordRatio - 0.16) / 0.16,
            (metrics.ChordRatio - 0.24) / 0.12,
            (0.62 - metrics.ChordRatio) / 0.16);
        double pureSpeedGate = Clamp01((0.28 - metrics.ChordRatio) / 0.2);
        double speedGate = 1 - Clamp01((metrics.ChordRatio - 0.08) / 0.22);
        double speedBonus = speedGate * Math.Min(0.38, Math.Max(0, metrics.SustainedNps10s - 22) * 0.045);
        double pureSpeedBonus = pureSpeedGate * Math.Min(
            1.05,
            Math.Max(0, metrics.SustainedNps10s - 31) * 0.16
              + Math.Max(0, metrics.PeakNps5s - 34) * 0.06
              + Math.Max(0, metrics.NoteCount - 3400) * 0.001);
        double lowChordSustainedSpeedBonus = metrics.ChordRatio <= 0.16
            && metrics.SustainedNps10s >= 24.2
            && metrics.PeakNps5s >= 25.2
            && metrics.JackPressure < 175
            && starRating > 0
            && starRating >= 5.4
            && starRating < 6.25
            ? Math.Min(
              0.56,
              Math.Max(0, metrics.SustainedNps10s - 24) * 0.105
                + Math.Max(0, metrics.PeakNps5s - 25) * 0.05
                + Math.Max(0, metrics.NoteCount - 1800) * 0.00008
                + Math.Max(0, metrics.JackPressure - 125) * 0.003
                + Math.Max(0, 5.9 - starRating) * 0.08)
            : 0;
        double longLowChordSpeedBonus = metrics.ChordRatio <= 0.16
            && metrics.SustainedNps10s >= 24.2
            && metrics.PeakNps5s >= 25.2
            && metrics.PeakNps5s <= 26.8
            && metrics.NoteCount >= 2200
            && metrics.JackPressure < 175
            && starRating >= 5.4
            && starRating < 5.9
            ? Math.Min(
              0.34,
              Math.Max(0, metrics.NoteCount - 2100) * 0.00011
                + Math.Max(0, 26.8 - metrics.PeakNps5s) * 0.05
                + Math.Max(0, metrics.SustainedNps10s - 24) * 0.045
                + Math.Max(0, 5.9 - starRating) * 0.13)
            : 0;
        double lightChordGammaSpeedFloorBonus = metrics.ChordRatio >= 0.1
            && metrics.ChordRatio <= 0.16
            && metrics.HoldRatio < 0.02
            && metrics.NoteCount >= 2200
            && metrics.NoteCount <= 2900
            && metrics.SustainedNps10s >= 24.2
            && metrics.SustainedNps10s <= 25.8
            && metrics.PeakNps5s >= 25.2
            && metrics.PeakNps5s <= 26.6
            && metrics.StreamPressure >= 6.15
            && metrics.JackPressure < 150
            && starRating >= 5.35
            && starRating <= 5.7
            ? Math.Min(
              0.52,
              0.39
                + Math.Max(0, metrics.SustainedNps10s - 24.2) * 0.07
                + Math.Max(0, metrics.PeakNps5s - 25.2) * 0.05
                + Math.Max(0, metrics.NoteCount - 2200) * 0.00008
                + Math.Max(0, 5.7 - starRating) * 0.12)
            : 0;
        double lowSrSpeedUnderrateBaseBonus = metrics.ChordRatio >= 0.18
            && metrics.ChordRatio <= 0.3
            && metrics.SustainedNps10s >= 25
            && metrics.PeakNps5s >= 26
            && metrics.JackPressure < 165
            && starRating > 0
            && starRating < 7
            ? Math.Min(
              0.54,
              Math.Max(0, 6.6 - starRating) * 0.54
                + Math.Max(0, metrics.SustainedNps10s - 25) * 0.045
                + Math.Max(0, metrics.PeakNps5s - 26) * 0.035)
            : 0;
        double lowSrSpeedUnderrateTaper = starRating <= 6.4
            ? 1
            : Math.Max(0.2, 1 - (starRating - 6.4) / 0.35);
        double lowSrSpeedUnderrateBonus = lowSrSpeedUnderrateBaseBonus * lowSrSpeedUnderrateTaper;
        double compactDeltaSpeedBridgeGate = metrics.ChordRatio >= 0.18
            && metrics.ChordRatio <= 0.3
            && metrics.HoldRatio < 0.06
            && metrics.NoteCount >= 1800
            && metrics.NoteCount <= 2600
            && metrics.SustainedNps10s >= 27.8
            && metrics.SustainedNps10s <= 29.2
            && metrics.PeakNps5s >= 28.5
            && metrics.PeakNps5s <= 31.5
            && metrics.Nps5sP90 >= metrics.PeakNps5s - 1.4
            && metrics.FastRowRatio >= 0.55
            && metrics.JackPressure < 155
            && starRating >= 5.55
            && starRating <= 6.4
            ? MinGate(
              (metrics.ChordRatio - 0.16) / 0.08,
              (0.32 - metrics.ChordRatio) / 0.08,
              (metrics.NoteCount - 1700) / 400,
              (2700 - metrics.NoteCount) / 400,
              (metrics.SustainedNps10s - 27.5) / 1.2,
              (29.5 - metrics.SustainedNps10s) / 1.2,
              (metrics.PeakNps5s - 28.2) / 1.2,
              (32 - metrics.PeakNps5s) / 1.6,
              (155 - metrics.JackPressure) / 30)
            : 0;
        double compactDeltaSpeedBridgeBonus = compactDeltaSpeedBridgeGate * 0.06;
        double simpleHighDeltaSpeedBridgeBonus = metrics.ChordRatio >= 0.18
            && metrics.ChordRatio <= 0.23
            && metrics.HoldRatio < 0.04
            && metrics.NoteCount >= 1600
            && metrics.NoteCount <= 2500
            && metrics.SustainedNps10s >= 28.7
            && metrics.SustainedNps10s <= 29.4
            && metrics.PeakNps5s >= 29
            && metrics.PeakNps5s <= 30.2
            && metrics.FastRowRatio >= 0.84
            && metrics.JackPressure >= 110
            && metrics.JackPressure <= 130
            && metrics.PatternVariety <= 2.45
            && starRating >= 6.3
            && starRating <= 6.65
            ? Math.Min(
              0.36,
              0.06 + Math.Max(0, metrics.NoteCount - 1800) * 0.00045)
            : 0;
        double sustainedLightJumpstreamGate = metrics.ChordRatio >= 0.18
            && metrics.ChordRatio <= 0.32
            && metrics.HoldRatio < 0.03
            && metrics.NoteCount >= 2800
            && metrics.NoteCount <= 3800
            && metrics.SustainedNps10s >= 27
            && metrics.PeakNps5s >= 28
            && metrics.FastRowRatio >= 0.78
            && metrics.SustainedPressureRatio >= 0.82
            && metrics.StreamPressure >= 6
            && metrics.JackPressure < 160
            && metrics.PatternVariety >= 2.4
            && starRating >= 5.65
            && starRating <= 6.35
            ? MinGate(
              (metrics.NoteCount - 2600) / 600,
              (4000 - metrics.NoteCount) / 800,
              (metrics.ChordRatio - 0.16) / 0.08,
              (0.34 - metrics.ChordRatio) / 0.08,
              (metrics.SustainedNps10s - 26.6) / 2,
              (31.5 - metrics.SustainedNps10s) / 2.5,
              (metrics.FastRowRatio - 0.74) / 0.16,
              (metrics.SustainedPressureRatio - 0.78) / 0.12,
              (160 - metrics.JackPressure) / 40,
              (starRating - 5.6) / 0.25,
              (6.45 - starRating) / 0.35)
            : 0;
        double sustainedLightJumpstreamBonus = sustainedLightJumpstreamGate * 0.12;
        double baseRateSubGammaStreamBonus = metrics.ChordRatio >= 0.18
            && metrics.ChordRatio <= 0.28
            && metrics.HoldRatio < 0.03
            && metrics.NoteCount >= 2800
            && metrics.NoteCount <= 3800
            && metrics.SustainedNps10s >= 25
            && metrics.SustainedNps10s <= 26.2
            && metrics.PeakNps5s >= 25.4
            && metrics.PeakNps5s < 26
            && metrics.StreamPressure >= 6
            && metrics.JackPressure < 160
            && metrics.TechPressure < 6.25
            && metrics.FastRowRatio >= 0.7
            && starRating >= 5.35
            && starRating <= 5.65
            ? Math.Min(
              0.48,
              0.33
                + Math.Max(0, metrics.SustainedNps10s - 25) * 0.07
                + Math.Max(0, metrics.PeakNps5s - 25.4) * 0.08
                + Math.Max(0, metrics.NoteCount - 2800) * 0.00008
                + Math.Max(0, metrics.StreamPressure - 6) * 0.12
                + Math.Max(0, 5.65 - starRating) * 0.14)
            : 0;
        double compactModerateChordSpeedBonus = metrics.ChordRatio >= 0.18
            && metrics.ChordRatio <= 0.28
            && metrics.HoldRatio < 0.04
            && metrics.NoteCount >= 1500
            && metrics.NoteCount <= 2300
            && metrics.SustainedNps10s >= 25
            && metrics.SustainedNps10s <= 26.5
            && metrics.PeakNps5s >= 25.4
            && metrics.PeakNps5s <= 26.6
            && metrics.StreamPressure >= 5.9
            && metrics.JackPressure < 135
            && starRating >= 5.5
            && starRating <= 5.9
            ? Math.Min(
              0.42,
              0.28
                + Math.Max(0, metrics.PeakNps5s - 25.4) * 0.05
                + Math.Max(0, metrics.SustainedNps10s - 25) * 0.06
                + Math.Max(0, metrics.ChordRatio - 0.18) * 0.45
                + Math.Max(0, 5.9 - starRating) * 0.1)
            : 0;
        double speedEnduranceBonus = metrics.ChordRatio <= 0.32
            && metrics.SustainedNps10s >= 29
            && metrics.PeakNps5s >= 30
            && metrics.JackPressure < 165
            && metrics.NoteCount >= 3000
            && starRating > 0
            && starRating < 7
            ? Math.Min(
              0.45,
              Math.Max(0, metrics.NoteCount - 2800) * 0.00035
                + Math.Max(0, metrics.SustainedNps10s - 29) * 0.09
                + Math.Max(0, metrics.PeakNps5s - 30) * 0.04)
            : 0;
        double highSpeedEndgameBonus = metrics.ChordRatio <= 0.35
            && metrics.HoldRatio < 0.08
            && metrics.PeakNps5s >= 31.5
            && metrics.SustainedNps10s >= 30.3
            && metrics.FastRowRatio >= 0.74
            && metrics.JackPressure < 190
            && metrics.NoteCount >= 1800
            ? Math.Min(
              0.92,
              0.34
                + Math.Max(0, metrics.SustainedNps10s - 32) * 0.08
                + Math.Max(0, metrics.PeakNps5s - 33) * 0.035
                + Math.Max(0, 0.35 - metrics.ChordRatio) * 0.45
                + Math.Max(0, metrics.FastRowRatio - 0.74) * 0.3)
            : 0;
        double lowChordSpeedjackAnchorBonus = metrics.ChordRatio >= 0.18
            && metrics.ChordRatio <= 0.24
            && metrics.HoldRatio < 0.08
            && metrics.PeakNps5s >= 31
            && metrics.SustainedNps10s >= 29.8
            && metrics.SustainedNps10s <= 31.7
            && metrics.FastRowRatio >= 0.9
            && metrics.JackPressure >= 190
            && metrics.NoteCount >= 3500
            ? Math.Min(
              0.72,
              0.48
                + Math.Max(0, metrics.JackPressure - 190) * 0.004
                + Math.Max(0, metrics.NoteCount - 3500) * 0.00008
                + Math.Max(0, metrics.FastRowRatio - 0.9) * 0.35)
            : 0;
        double highEntropyLowChordEnduranceBridgeBonus = metrics.ChordRatio >= 0.19
            && metrics.ChordRatio <= 0.23
            && metrics.HoldRatio < 0.02
            && metrics.NoteCount >= 4000
            && metrics.FastRowRatio >= 0.9
            && metrics.PeakNps5s >= 30
            && metrics.PeakNps5s <= 31.2
            && metrics.SustainedNps10s >= 30
            && metrics.SustainedNps10s <= 31.2
            && metrics.JackPressure >= 145
            && metrics.JackPressure <= 165
            && metrics.PatternVariety >= 2.85
            && metrics.RowIntervalEntropy >= 2.2
            ? 1.05
            : 0;
        double variedLowChordSpeedjackBridgeBonus = metrics.ChordRatio >= 0.1
            && metrics.ChordRatio <= 0.35
            && metrics.HoldRatio < 0.08
            && metrics.JackPressure >= 154
            && metrics.JackPressure <= 180
            && metrics.NoteCount >= 2500
            && metrics.NoteCount <= 3550
            && metrics.PatternVariety >= 2.84
            && metrics.PatternVariety <= 3.22
            && metrics.FastRowRatio >= 0.58
            && metrics.SustainedNps10s >= 24.5
            ? Math.Min(
              0.52,
              0.34
                + Math.Max(0, metrics.JackPressure - 154) * 0.003
                + Math.Max(0, metrics.PatternVariety - 2.84) * 0.12
                + Math.Max(0, metrics.FastRowRatio - 0.58) * 0.12)
            : 0;
        double variedLowChordSpeedCompression = metrics.ChordRatio <= 0.22
            && metrics.HoldRatio < 0.08
            && metrics.FastRowRatio >= 0.83
            && metrics.NoteCount >= 4000
            && metrics.PatternVariety >= 2
            && metrics.SustainedNps10s >= 31.5
            ? Math.Min(
              0.95,
              0.35
                + Math.Max(0, metrics.PatternVariety - 2) * 0.22
                + Math.Max(0, metrics.NoteCount - 4000) * 0.00006
                + Math.Max(0, 0.22 - metrics.ChordRatio) * 0.8)
            : 0;
        double thinLowChordSpeedCompression = metrics.ChordRatio >= 0.09
            && metrics.ChordRatio <= 0.14
            && metrics.HoldRatio < 0.08
            && metrics.FastRowRatio >= 0.82
            && metrics.PatternVariety >= 2.7
            && metrics.PatternVariety <= 3.1
            ? metrics.NoteCount >= 4000
              && metrics.PeakNps5s >= 26.8
              && metrics.PeakNps5s <= 27.6
              && metrics.SustainedNps10s >= 26
              && metrics.SustainedNps10s <= 27
              && metrics.JackPressure < 140
              ? 0.4
              : metrics.PeakNps5s >= 33.5
                && metrics.SustainedNps10s >= 33
                && metrics.JackPressure >= 150
                ? metrics.NoteCount >= 4000 ? 0.6 : 0.5
                : 0
            : 0;
        double highVarietyThinStreamEdgeCompression = metrics.ChordRatio >= 0.18
            && metrics.ChordRatio <= 0.24
            && metrics.HoldRatio < 0.05
            && metrics.FastRowRatio >= 0.85
            && metrics.PeakNps5s >= 28.4
            && metrics.PeakNps5s <= 29
            && metrics.SustainedNps10s >= 28
            && metrics.SustainedNps10s <= 28.8
            && metrics.JackPressure >= 120
            && metrics.JackPressure <= 128
            && metrics.PatternVariety >= 2.9
            ? 0.1
            : 0;
        double midVarietyHighSpeedCompression = metrics.PatternVariety >= 2.31
            && metrics.PatternVariety <= 2.49
            && metrics.PeakNps5s >= 32.6
            && metrics.PeakNps5s <= 34.8
            && metrics.SustainedNps10s >= 31.8
            && metrics.SustainedNps10s <= 34.4
            ? Math.Min(
              0.65,
              0.44
                + Math.Max(0, metrics.PeakNps5s - 32.6) * 0.04
                + Math.Max(0, metrics.SustainedNps10s - 31.8) * 0.035)
            : 0;
        double lowMidRateOverpromotionCompression = metrics.FastRowRatio >= 0.04
            && metrics.FastRowRatio <= 0.58
            && metrics.PeakNps5s >= 24.6
            && metrics.PeakNps5s <= 26.2
            && metrics.SustainedNps10s >= 22
            && metrics.SustainedNps10s <= 25
            ? Math.Min(
              0.65,
              0.42
                + Math.Max(0, 26.2 - metrics.PeakNps5s) * 0.05
                + Math.Max(0, 0.58 - metrics.FastRowRatio) * 0.18)
            : 0;
        double extremeChordwallSpeedBonus = metrics.ChordRatio >= 0.78
            && metrics.HoldRatio < 0.08
            && metrics.PeakNps5s >= 36.5
            && metrics.SustainedNps10s >= 35
            && metrics.JackPressure >= 165
            && metrics.NoteCount >= 2200
            ? Math.Min(
              0.9,
              0.405
                + Math.Max(0, metrics.PeakNps5s - 36.5) * 0.072
                + Math.Max(0, metrics.SustainedNps10s - 35) * 0.054
                + Math.Max(0, metrics.JackPressure - 165) * 0.0027)
            : 0;
        double fastSimpleChordWallJackFloorBonus = metrics.NoteCount >= 2200
            && metrics.NoteCount <= 2350
            && metrics.ChordRatio >= 0.8
            && metrics.ChordRatio <= 0.86
            && metrics.HoldRatio < 0.04
            && metrics.FastRowRatio >= 0.88
            && metrics.PatternVariety <= 1.72
            && metrics.RowIntervalEntropy <= 0.65
            && metrics.PeakNps5s >= 34
            && metrics.JackPressure >= 185
            ? Math.Min(
              0.9,
              0.55
                + Math.Max(0, 35 - metrics.PeakNps5s) * 0.24
                + Math.Max(0, metrics.JackPressure - 190) * 0.002)
            : 0;
        double denseSimpleChordWallRateBonus = metrics.NoteCount >= 2800
            && metrics.NoteCount <= 3400
            && metrics.ChordRatio >= 0.92
            && metrics.HoldRatio < 0.04
            && metrics.FastRowRatio <= 0.02
            && metrics.PatternVariety <= 1.75
            && metrics.RowIntervalEntropy <= 0.85
            && metrics.PeakNps5s >= 31
            && metrics.JackPressure >= 143
            ? Math.Min(
              1.1,
              0.7
                + Math.Max(0, metrics.PeakNps5s - 31) * 0.15
                + Math.Max(0, metrics.JackPressure - 145) * 0.015)
            : 0;
        double highEndFastWallJackBonus = metrics.NoteCount >= 3600
            && metrics.ChordRatio >= 0.82
            && metrics.ChordRatio <= 0.9
            && metrics.HoldRatio < 0.04
            && metrics.FastRowRatio >= 0.75
            && metrics.PeakNps5s >= 40
            && metrics.SustainedNps10s >= 39
            && metrics.JackPressure >= 210
            && metrics.PatternVariety <= 2.1
            ? 0.5
            : 0;
        double plainHighChordWallRateCompression = metrics.NoteCount >= 1800
            && metrics.NoteCount <= 2100
            && metrics.ChordRatio >= 0.84
            && metrics.ChordRatio <= 0.9
            && metrics.HoldRatio < 0.04
            && metrics.FastRowRatio < 0.1
            && metrics.PeakNps5s >= 35
            && metrics.PeakNps5s <= 36.2
            && metrics.SustainedNps10s >= 34.8
            && metrics.SustainedNps10s <= 35.8
            && metrics.JackPressure >= 168
            && metrics.PatternVariety <= 2.4
            && metrics.RowIntervalEntropy <= 1.2
            ? 0.4
            : 0;
        double variedMidHighChordWallCompression = metrics.ChordRatio >= 0.77
            && metrics.ChordRatio <= 0.84
            && metrics.HoldRatio < 0.04
            && metrics.PeakNps5s >= 27
            && metrics.PeakNps5s <= 32
            && metrics.JackPressure >= 154
            && metrics.JackPressure <= 165
            && metrics.PatternVariety >= 2.05
            && metrics.PatternVariety <= 2.15
            && metrics.RowIntervalEntropy >= 1.3
            ? 0.15
            : 0;
        double midHighChordjackDeltaBridgeBonus = metrics.NoteCount >= 2100
            && metrics.NoteCount <= 2400
            && metrics.ChordRatio >= 0.75
            && metrics.ChordRatio <= 0.82
            && metrics.HoldRatio < 0.04
            && metrics.FastRowRatio >= 0.1
            && metrics.FastRowRatio <= 0.2
            && metrics.PeakNps5s >= 30
            && metrics.PeakNps5s <= 32
            && metrics.JackPressure >= 155
            && metrics.JackPressure <= 165
            && metrics.PatternVariety >= 2.3
            && metrics.PatternVariety <= 2.6
            && metrics.RowIntervalEntropy >= 1
            && metrics.RowIntervalEntropy <= 1.3
            ? 0.65
            : 0;
        double lowRateChordjackWallFloorBonus = metrics.NoteCount >= 1800
            && metrics.NoteCount <= 2400
            && metrics.ChordRatio >= 0.82
            && metrics.ChordRatio <= 0.89
            && metrics.HoldRatio < 0.04
            && metrics.FastRowRatio <= 0.08
            ? metrics.PeakNps5s >= 25.2
              && metrics.PeakNps5s <= 25.8
              && metrics.JackPressure >= 115
              && metrics.JackPressure <= 120
              && metrics.PatternVariety >= 2.3
              && metrics.RowIntervalEntropy >= 1
              && metrics.RowIntervalEntropy <= 1.15
              ? 0.35
              : metrics.PeakNps5s >= 24.2
                && metrics.PeakNps5s <= 24.6
                && metrics.JackPressure >= 128
                && metrics.JackPressure <= 135
                && metrics.PatternVariety <= 1.8
                && metrics.RowIntervalEntropy <= 0.65
                ? 0.2
                : 0
            : 0;
        double compactHighChordAlphaWallFloorBonus = metrics.NoteCount >= 2100
            && metrics.NoteCount <= 2400
            && metrics.ChordRatio >= 0.76
            && metrics.ChordRatio <= 0.82
            && metrics.HoldRatio < 0.04
            && metrics.PeakNps5s >= 25
            && metrics.PeakNps5s <= 26.2
            && metrics.SustainedNps10s >= 23.7
            && metrics.SustainedNps10s <= 25
            && durationMs >= 115000
            && durationMs <= 135000
            ? 0.7
            : 0;
        double compactHighChordGammaWallFloorBonus = metrics.NoteCount >= 1800
            && metrics.NoteCount <= 2100
            && metrics.ChordRatio >= 0.84
            && metrics.ChordRatio <= 0.9
            && metrics.HoldRatio < 0.04
            && metrics.PeakNps5s >= 28.8
            && metrics.PeakNps5s <= 29.6
            && metrics.SustainedNps10s >= 28.2
            && metrics.SustainedNps10s <= 29.2
            && durationMs >= 86000
            && durationMs <= 94000
            ? 0.22
            : 0;
        double compactHighChordGammaPlusWallBridgeBonus = metrics.NoteCount >= 1800
            && metrics.NoteCount <= 2100
            && metrics.ChordRatio >= 0.84
            && metrics.ChordRatio <= 0.9
            && metrics.HoldRatio < 0.04
            && metrics.PeakNps5s >= 30.2
            && metrics.PeakNps5s <= 31
            && metrics.SustainedNps10s >= 29.5
            && metrics.SustainedNps10s <= 30.3
            && durationMs >= 84000
            && durationMs <= 89000
            ? 0.24
            : 0;
        double compactHighChordDeltaWallBridgeBonus = metrics.NoteCount >= 1800
            && metrics.NoteCount <= 2100
            && metrics.ChordRatio >= 0.84
            && metrics.ChordRatio <= 0.9
            && metrics.HoldRatio < 0.04
            && metrics.PeakNps5s >= 33
            && metrics.PeakNps5s <= 34
            && metrics.SustainedNps10s >= 32.3
            && metrics.SustainedNps10s <= 33.2
            && durationMs >= 76000
            && durationMs <= 82000
            ? 0.38
            : 0;
        double midRatePlainWallJackCompression = metrics.NoteCount >= 2200
            && metrics.NoteCount <= 2300
            && metrics.ChordRatio >= 0.8
            && metrics.ChordRatio <= 0.85
            && metrics.HoldRatio < 0.04
            && metrics.FastRowRatio <= 0.02
            && metrics.PeakNps5s >= 28
            && metrics.PeakNps5s <= 29
            && metrics.SustainedNps10s >= 27
            && metrics.SustainedNps10s <= 28
            && metrics.JackPressure >= 150
            && metrics.JackPressure <= 156
            && metrics.PatternVariety <= 1.8
            && metrics.RowIntervalEntropy <= 0.65
            ? 0.45
            : 0;
        double highRateVariedWallJackBridgeBonus = metrics.NoteCount >= 2200
            && metrics.NoteCount <= 2300
            && metrics.ChordRatio >= 0.8
            && metrics.ChordRatio <= 0.85
            && metrics.HoldRatio < 0.04
            && metrics.FastRowRatio <= 0.02
            && metrics.PeakNps5s >= 33
            && metrics.PeakNps5s <= 34
            && metrics.SustainedNps10s >= 32
            && metrics.SustainedNps10s <= 33
            && metrics.JackPressure >= 180
            && metrics.JackPressure <= 186
            && metrics.PatternVariety >= 2
            && metrics.RowIntervalEntropy >= 1.3
            ? 0.55
            : 0;
        double fastMidChordHandstreamBridgeBonus = metrics.NoteCount >= 2200
            && metrics.NoteCount <= 2600
            && metrics.ChordRatio >= 0.42
            && metrics.ChordRatio <= 0.48
            && metrics.HoldRatio < 0.06
            && metrics.FastRowRatio >= 0.75
            && metrics.PeakNps5s >= 32
            && metrics.SustainedNps10s >= 31
            && metrics.JackPressure >= 130
            && metrics.JackPressure <= 145
            && metrics.PatternVariety >= 2.4
            && metrics.PatternVariety <= 2.6
            && metrics.RowIntervalEntropy >= 1.1
            && metrics.RowIntervalEntropy <= 1.35
            ? 0.6
            : 0;
        double lowRateMidChordJackGate = metrics.NoteCount >= 2300
            && metrics.NoteCount <= 2500
            && metrics.ChordRatio >= 0.58
            && metrics.ChordRatio <= 0.66
            && metrics.HoldRatio < 0.04
            && metrics.FastRowRatio < 0.25
            && metrics.PeakNps5s >= 26.4
            && metrics.PeakNps5s <= 29.2
            && metrics.SustainedNps10s >= 24.8
            && metrics.SustainedNps10s <= 27.6
            && metrics.JackPressure >= 138
            && metrics.JackPressure <= 155
            && metrics.PatternVariety >= 2.2
            && metrics.PatternVariety <= 2.65
            && metrics.RowIntervalEntropy >= 1.25
            && metrics.RowIntervalEntropy <= 1.9
            ? MinGate(
              (metrics.PeakNps5s - 26.2) / 0.6,
              (29.45 - metrics.PeakNps5s) / 0.55,
              (metrics.SustainedNps10s - 24.6) / 0.6,
              (27.9 - metrics.SustainedNps10s) / 0.7,
              (metrics.JackPressure - 136) / 4.7,
              (158 - metrics.JackPressure) / 6,
              (metrics.PatternVariety - 2.2) / 0.1,
              (2.72 - metrics.PatternVariety) / 0.12,
              (1.95 - metrics.RowIntervalEntropy) / 0.162)
            : 0;
        double lowRateMidChordJackCompression = lowRateMidChordJackGate * 0.8;
        double introMidChordJackCompression = metrics.NoteCount >= 2000
            && metrics.NoteCount <= 2200
            && metrics.ChordRatio >= 0.55
            && metrics.ChordRatio <= 0.62
            && metrics.HoldRatio < 0.04
            && metrics.FastRowRatio < 0.04
            && metrics.PeakNps5s >= 20
            && metrics.PeakNps5s <= 22
            && metrics.SustainedNps10s >= 20
            && metrics.SustainedNps10s <= 21
            && metrics.JackPressure >= 145
            && metrics.JackPressure <= 155
            && metrics.PatternVariety >= 2.7
            && metrics.PatternVariety <= 2.9
            && metrics.RowIntervalEntropy >= 1
            && metrics.RowIntervalEntropy <= 1.2
            ? 1
            : 0;
        double staminaEnduranceBonus = metrics.SustainedNps10s >= 28
            && metrics.ChordRatio >= 0.38
            && metrics.ChordRatio <= 0.75
            && metrics.JackPressure < 165
            && metrics.NoteCount >= 4500
            ? Math.Min(
              0.45,
              Math.Max(0, metrics.NoteCount - 4200) * 0.00012
                + Math.Max(0, metrics.SustainedNps10s - 27) * 0.055
                + Math.Max(0, metrics.ChordRatio - 0.38) * 0.35)
            : 0;
        double longSteadyStreamBonus = metrics.SustainedNps10s >= 25
            && metrics.ChordRatio >= 0.26
            && metrics.ChordRatio <= 0.42
            && metrics.JackPressure < 155
            && metrics.NoteCount >= 4200
            ? Math.Min(
              0.28,
              Math.Max(0, metrics.NoteCount - 4000) * 0.00011
                + Math.Max(0, metrics.SustainedNps10s - 25) * 0.055)
            : 0;
        double longSparseStreamCompression = metrics.NoteCount >= 4200
            && metrics.NoteCount <= 5600
            && metrics.ChordRatio >= 0.08
            && metrics.ChordRatio <= 0.17
            && metrics.HoldRatio < 0.03
            && metrics.SustainedNps10s >= 27
            && metrics.SustainedNps10s <= 31
            && metrics.PeakNps5s >= 28
            && metrics.PeakNps5s <= 31
            && metrics.JackPressure >= 120
            && metrics.JackPressure <= 165
            && metrics.StreamPressure >= 6.1
            && metrics.TechPressure < 5.4
            && metrics.ChordSizeChangeRate < 0.22
            && metrics.RowIntervalEntropy < 2.1
            && starRating >= 5.65
            && starRating <= 6.35
            ? MinGate(
              (metrics.NoteCount - 4000) / 700,
              (5800 - metrics.NoteCount) / 900,
              (metrics.ChordRatio - 0.06) / 0.05,
              (0.19 - metrics.ChordRatio) / 0.05,
              (metrics.SustainedNps10s - 26.5) / 2,
              (31.5 - metrics.SustainedNps10s) / 2,
              (metrics.PeakNps5s - 27.5) / 1.8,
              (31.5 - metrics.PeakNps5s) / 1.8,
              (165 - metrics.JackPressure) / 35,
              (2.2 - metrics.RowIntervalEntropy) / 0.8) * 0.55
            : 0;
        double burstTechBonus = metrics.PeakNps1s >= 34
            && metrics.ChordRatio >= 0.18
            && metrics.ChordRatio <= 0.36
            && metrics.TechPressure >= 5.6
            && metrics.JackPressure >= 130
            && metrics.JackPressure <= 190
            && metrics.SustainedNps10s >= 23
            && metrics.NoteCount >= 3000
            ? Math.Min(
              1.08,
              Math.Max(0, metrics.PeakNps1s - 32) * 0.2
                + Math.Max(0, metrics.TechPressure - 5.5) * 0.3
                + Math.Max(0, metrics.JackPressure - 130) * 0.006)
            : 0;
        bool lowSrTechnicalRhythmEligible = metrics.NoteCount >= 2200
            && metrics.NoteCount <= 4200
            && metrics.ChordRatio >= 0.16
            && metrics.ChordRatio <= 0.38
            && metrics.HoldRatio < 0.16
            && metrics.PeakNps5s >= 24.8
            && metrics.SustainedNps10s >= 24
            && metrics.RowBurstPressure >= 20
            && metrics.FastRowRatio >= 0.5
            && metrics.ChordSizeChangeRate >= 0.24
            && metrics.DirectionChangeRate >= 0.62
            && metrics.JackPressure >= 145
            && starRating >= 5.25
            && starRating <= 6.85;
        double lowSrTechnicalRhythmShapeGate = lowSrTechnicalRhythmEligible
            ? Math.Max(
              MinGate(
                (metrics.NoteCount - 2000) / 500,
                (4200 - metrics.NoteCount) / 700,
                (metrics.ChordRatio - 0.14) / 0.08,
                (0.42 - metrics.ChordRatio) / 0.08,
                (metrics.PeakNps5s - 24.5) / 1.4,
                (metrics.SustainedNps10s - 23.8) / 1.2,
                (metrics.FastRowRatio - 0.45) / 0.25,
                (metrics.ChordSizeChangeRate - 0.22) / 0.16,
                (metrics.DirectionChangeRate - 0.6) / 0.08),
              MinGate(
                (metrics.NoteCount - 2000) / 500,
                (4200 - metrics.NoteCount) / 700,
                (metrics.ChordRatio - 0.12) / 0.08,
                (0.42 - metrics.ChordRatio) / 0.08,
                (metrics.RowBurstPressure - 20) / 12,
                (metrics.FastRowRatio - 0.45) / 0.25,
                (metrics.DirectionChangeRate - 0.6) / 0.08))
            : 0;
        double highRatePackTechnicalRhythmInflationGate = lowSrTechnicalRhythmShapeGate > 0
            && metrics.NoteCount >= 2400
            && metrics.NoteCount <= 3100
            && durationMs >= 90000
            && durationMs <= 112000
            && metrics.ChordRatio >= 0.28
            && metrics.ChordRatio <= 0.34
            && metrics.HoldRatio < 0.03
            && metrics.PeakNps5s >= 28.4
            && metrics.SustainedNps10s >= 27.8
            && metrics.FastRowRatio >= 0.94
            && metrics.RowIntervalEntropy >= 2.25
            && metrics.RowIntervalEntropy <= 2.9
            && metrics.ChordSizeChangeRate >= 0.45
            && metrics.ChordSizeChangeRate <= 0.6
            && metrics.DirectionChangeRate >= 0.66
            && metrics.DirectionChangeRate <= 0.74
            && metrics.JackPressure >= 184
            ? MinGate(
              (metrics.NoteCount - 2300) / 400,
              (3200 - metrics.NoteCount) / 500,
              (112000 - durationMs) / 6000,
              (metrics.ChordRatio - 0.26) / 0.06,
              (0.36 - metrics.ChordRatio) / 0.06,
              (metrics.PeakNps5s - 28.4) / 0.5,
              (metrics.SustainedNps10s - 27.8) / 0.4,
              (metrics.FastRowRatio - 0.94) / 0.03,
              (metrics.RowIntervalEntropy - 2.2) / 0.25,
              (2.95 - metrics.RowIntervalEntropy) / 0.25,
              (metrics.ChordSizeChangeRate - 0.44) / 0.08,
              (0.62 - metrics.ChordSizeChangeRate) / 0.08,
              (metrics.JackPressure - 184) / 3)
            : 0;
        double lowSrTechnicalRhythmGate = lowSrTechnicalRhythmShapeGate
            * (starRating > 6 ? 0.72 : 1)
            * (1 - highRatePackTechnicalRhythmInflationGate);
        double lowSrTechnicalRhythmBonus = lowSrTechnicalRhythmGate * Math.Min(
            1.58,
            0.42
              + Math.Max(0, metrics.RowBurstPressure - 20) * 0.04
              + Math.Max(0, metrics.FastRowRatio - 0.5) * 0.58
              + Math.Max(0, metrics.RowIntervalEntropy - 2) * 0.15
              + Math.Max(0, metrics.ChordSizeChangeRate - 0.2) * 0.9
              + Math.Max(0, metrics.JackPressure - 145) * 0.0045
              + Math.Max(0, 6.15 - starRating) * 0.28);
        double ratePackTechShapeGate = metrics.NoteCount >= 2400
            && metrics.NoteCount <= 3100
            && metrics.ChordRatio >= 0.28
            && metrics.ChordRatio <= 0.34
            && metrics.HoldRatio < 0.03
            && metrics.FastRowRatio >= 0.84
            && metrics.RowIntervalEntropy >= 2.25
            && metrics.ChordSizeChangeRate >= 0.45
            && metrics.ChordSizeChangeRate <= 0.6
            && metrics.DirectionChangeRate >= 0.66
            && metrics.DirectionChangeRate <= 0.74
            && metrics.JackPressure >= 150
            && metrics.JackPressure <= 185
            ? MinGate(
              (metrics.NoteCount - 2300) / 400,
              (3200 - metrics.NoteCount) / 500,
              (metrics.ChordRatio - 0.26) / 0.06,
              (0.36 - metrics.ChordRatio) / 0.06,
              (metrics.FastRowRatio - 0.82) / 0.1,
              (metrics.RowIntervalEntropy - 2.2) / 0.4,
              (metrics.ChordSizeChangeRate - 0.44) / 0.08,
              (0.62 - metrics.ChordSizeChangeRate) / 0.08,
              (metrics.JackPressure - 148) / 20,
              (188 - metrics.JackPressure) / 24)
            : 0;
        double lowerRateTechBridgeBonus = ratePackTechShapeGate
            * (starRating >= 5.35 && starRating <= 5.55
              ? MinGate((starRating - 5.3) / 0.12, (5.58 - starRating) / 0.12) * 0.48
              : 0);
        double baseRateTechCompression = ratePackTechShapeGate
            * (starRating >= 5.55 && starRating <= 6.05
              ? Math.Max(0, Math.Min(
                0.9,
                0.22
                  + Math.Max(0, 5.95 - starRating) * 2.4
                  + Math.Max(0, starRating - 5.95) * 0.2))
              : 0);
        double ratePackTechStructuralCompression = ratePackTechShapeGate > 0.3
            ? Math.Min(
              1.28,
              0.86
                + Math.Max(0, metrics.SustainedNps10s - 25.6) * 0.2
                - Math.Max(0, 25.6 - metrics.SustainedNps10s) * 0.02
                - Math.Max(0, metrics.SustainedNps10s - 25) * 0.055
                + Math.Max(0, metrics.FastRowRatio - 0.86) * 0.5
                + Math.Max(0, metrics.ChordSizeChangeRate - 0.5) * 0.3) * Clamp01((ratePackTechShapeGate - 0.18) / 0.22)
            : 0;
        double syncopatedChordTechGate = metrics.NoteCount >= 1600
            && metrics.NoteCount <= 2600
            && metrics.ChordRatio >= 0.28
            && metrics.ChordRatio <= 0.38
            && metrics.HoldRatio < 0.08
            && metrics.FastRowRatio >= 0.42
            && metrics.FastRowRatio <= 0.72
            && metrics.RowIntervalEntropy >= 2
            && metrics.ChordSizeChangeRate >= 0.34
            && metrics.JackPressure >= 155
            && starRating >= 5.4
            && starRating <= 5.9
            ? MinGate(
              (metrics.NoteCount - 2000) / 500,
              (2600 - metrics.NoteCount) / 500,
              (metrics.ChordRatio - 0.26) / 0.08,
              (0.4 - metrics.ChordRatio) / 0.08,
              (metrics.FastRowRatio - 0.4) / 0.16,
              (0.74 - metrics.FastRowRatio) / 0.16,
              (metrics.ChordSizeChangeRate - 0.32) / 0.12)
            : 0;
        double syncopatedChordTechBonus = syncopatedChordTechGate * Math.Min(
            0.78,
            0.32
              + Math.Max(0, metrics.RowIntervalEntropy - 2) * 0.1
              + Math.Max(0, metrics.ChordSizeChangeRate - 0.34) * 0.8
              + Math.Max(0, metrics.JackPressure - 155) * 0.004);
        double compactChordSwitchTechGate = metrics.NoteCount >= 1600
            && metrics.NoteCount <= 2500
            && metrics.ChordRatio >= 0.3
            && metrics.ChordRatio <= 0.48
            && metrics.HoldRatio >= 0.025
            && metrics.HoldRatio <= 0.12
            && metrics.PeakNps5s >= 24.5
            && metrics.SustainedNps10s >= 23.8
            && metrics.FastRowRatio >= 0.72
            && metrics.ChordSizeChangeRate >= 0.48
            && metrics.DirectionChangeRate >= 0.55
            && metrics.JackPressure >= 165
            && metrics.TechPressure >= 6.8
            && starRating >= 5.35
            && starRating <= 6.05
            ? MinGate(
              (metrics.NoteCount - 1500) / 450,
              (2500 - metrics.NoteCount) / 450,
              (metrics.ChordRatio - 0.28) / 0.08,
              (0.5 - metrics.ChordRatio) / 0.08,
              (metrics.HoldRatio - 0.015) / 0.035,
              (0.14 - metrics.HoldRatio) / 0.05,
              (metrics.FastRowRatio - 0.68) / 0.18,
              (metrics.ChordSizeChangeRate - 0.45) / 0.16,
              (metrics.JackPressure - 160) / 30)
            : 0;
        double compactChordSwitchTechBonus = compactChordSwitchTechGate * Math.Min(
            0.88,
            0.39
              + Math.Max(0, metrics.TechPressure - 6.8) * 0.08
              + Math.Max(0, metrics.FastRowRatio - 0.72) * 0.42
              + Math.Max(0, metrics.ChordSizeChangeRate - 0.48) * 0.72
              + Math.Max(0, metrics.JackPressure - 165) * 0.0045
              + Math.Max(0, 5.9 - starRating) * 0.16);
        double technicalAnchorGate = metrics.NoteCount >= 1700
            && metrics.NoteCount <= 3300
            && metrics.ChordRatio >= 0.26
            && metrics.ChordRatio <= 0.38
            && metrics.HoldRatio < 0.08
            && metrics.PeakNps1s >= 32
            && metrics.PeakNps5s >= 27
            && metrics.JackPressure >= 185
            && metrics.DirectionChangeRate >= 0.6
            && starRating >= 5.8
            && starRating <= 6.6
            ? MinGate(
              (metrics.NoteCount - 1600) / 600,
              (3300 - metrics.NoteCount) / 600,
              (metrics.ChordRatio - 0.24) / 0.08,
              (0.4 - metrics.ChordRatio) / 0.08,
              (metrics.PeakNps1s - 31) / 5,
              (metrics.PeakNps5s - 26.5) / 1.8,
              (metrics.JackPressure - 180) / 30)
            : 0;
        double technicalAnchorBonus = technicalAnchorGate * Math.Min(
            0.9,
            0.24
              + Math.Max(0, metrics.JackPressure - 185) * 0.007
              + Math.Max(0, metrics.PeakNps1s - 32) * 0.055
              + Math.Max(0, metrics.PeakNps5s - 27) * 0.06
              + Math.Max(0, metrics.ChordSizeChangeRate - 0.28) * 0.45);
        double highRatePackTechnicalAnchorCompression = technicalAnchorBonus * highRatePackTechnicalRhythmInflationGate;
        double highRateTechnicalAnchorFloorBonus = metrics.NoteCount >= 2600
            && metrics.NoteCount <= 2850
            && metrics.ChordRatio >= 0.28
            && metrics.ChordRatio <= 0.34
            && metrics.HoldRatio < 0.02
            && metrics.PeakNps5s >= 30
            && metrics.SustainedNps10s >= 29
            && metrics.FastRowRatio >= 0.95
            && metrics.JackPressure >= 190
            && metrics.PatternVariety >= 2.55
            && metrics.PatternVariety <= 2.75
            ? Math.Min(
              1.75,
              1.08
                + Math.Max(0, metrics.JackPressure - 190) * 0.007
                + Math.Max(0, metrics.SustainedNps10s - 29) * 0.09
                + Math.Max(0, 31 - metrics.PeakNps5s) * 0.34)
            : 0;
        double compactTechnicalMarathonGate = metrics.NoteCount >= 1200
            && metrics.NoteCount <= 2500
            && durationMs >= 70000
            && durationMs <= 150000
            && metrics.ChordRatio >= 0.18
            && metrics.ChordRatio <= 0.5
            && metrics.HoldRatio < 0.06
            && metrics.SustainedPressureRatio >= 0.62
            && metrics.DirectionChangeRate >= 0.62
            && (metrics.FastRowRatio >= 0.4 || metrics.RowBurstPressure >= 20)
            && metrics.PatternVariety >= 2.45
            && metrics.TechPressure >= 5.45
            && starRating >= 4.5
            && starRating <= 5.95
            ? MinGate(
              (metrics.NoteCount - 1100) / 450,
              (2600 - metrics.NoteCount) / 600,
              (metrics.ChordRatio - 0.16) / 0.08,
              (0.56 - metrics.ChordRatio) / 0.08,
              (durationMs - 65000) / 25000,
              (155000 - durationMs) / 35000,
              (metrics.SustainedPressureRatio - 0.58) / 0.14,
              (metrics.DirectionChangeRate - 0.6) / 0.08,
              (metrics.PatternVariety - 2.35) / 0.3,
              (metrics.TechPressure - 5.35) / 0.45)
            : 0;
        double compactTechnicalMarathonBonus = compactTechnicalMarathonGate * Math.Min(
            0.58,
            0.14
              + Math.Max(0, metrics.TechPressure - 5.4) * 0.18
              + Math.Max(0, metrics.ChordSizeChangeRate - 0.3) * 0.48
              + Math.Max(0, metrics.FastRowRatio - 0.4) * 0.16
              + Math.Max(0, metrics.RowBurstPressure - 18) * 0.012
              + Math.Max(0, 5.8 - starRating) * 0.055);
        double lowDensityChordFlowTechCompression = metrics.NoteCount >= 600
            && metrics.NoteCount <= 1900
            && metrics.ChordRatio >= 0.28
            && metrics.HoldRatio < 0.12
            && metrics.PeakNps5s <= 20.5
            && metrics.SustainedNps10s <= 17
            && metrics.RowBurstPressure <= 16
            && metrics.FastRowRatio <= 0.22
            && metrics.JackPressure <= 115
            && starRating >= 3.5
            && starRating <= 4.6
            ? Math.Min(
              0.42,
              0.18
                + Math.Max(0, 17 - metrics.SustainedNps10s) * 0.018
                + Math.Max(0, 16 - metrics.RowBurstPressure) * 0.014
                + Math.Max(0, 0.22 - metrics.FastRowRatio) * 0.28)
            : 0;
        double introHighChordFlowTechCompression = metrics.NoteCount <= 900
            && metrics.ChordRatio >= 0.45
            && metrics.HoldRatio >= 0.05
            && metrics.SustainedNps10s <= 14
            && metrics.FastRowRatio <= 0.04
            && metrics.ChordSizeChangeRate >= 0.58
            && metrics.TechPressure >= 7.5
            && starRating <= 3.2
            ? Math.Min(
              0.65,
              0.5
                + Math.Max(0, metrics.ChordRatio - 0.45) * 0.8
                + Math.Max(0, metrics.ChordSizeChangeRate - 0.58) * 0.5)
            : 0;
        double earlyVariedPatternTechBonus = metrics.NoteCount >= 700
            && metrics.NoteCount <= 1000
            && metrics.HoldRatio < 0.03
            && metrics.RowIntervalEntropy >= 2.7
            && metrics.PatternVariety >= 3.5
            && metrics.JackPressure >= 95
            && metrics.RowBurstPressure >= 12
            && metrics.FastRowRatio >= 0.1
            && starRating <= 3.25
            ? Math.Min(
              0.52,
              0.34
                + Math.Max(0, metrics.RowIntervalEntropy - 2.7) * 0.04
                + Math.Max(0, metrics.PatternVariety - 3.5) * 0.05
                + Math.Max(0, metrics.JackPressure - 95) * 0.003)
            : 0;
        double earlyLowEntropyTechCompression = metrics.NoteCount >= 850
            && metrics.NoteCount <= 1100
            && metrics.ChordRatio >= 0.28
            && metrics.ChordRatio <= 0.4
            && metrics.HoldRatio < 0.04
            && metrics.PeakNps5s <= 14.5
            && metrics.SustainedNps10s <= 13
            && metrics.FastRowRatio <= 0.08
            && metrics.RowIntervalEntropy <= 1.5
            && metrics.PatternVariety <= 3
            && starRating <= 3.3
            ? Math.Min(
              0.12,
              0.06
                + Math.Max(0, 1.5 - metrics.RowIntervalEntropy) * 0.035
                + Math.Max(0, 0.08 - metrics.FastRowRatio) * 0.25)
            : 0;
        double sparseLowSrTechVocabularyCompression = metrics.NoteCount >= 1000
            && metrics.NoteCount <= 1600
            && metrics.ChordRatio <= 0.16
            && metrics.HoldRatio < 0.1
            && metrics.PeakNps5s <= 7
            && metrics.SustainedNps10s <= 6
            && metrics.JackPressure <= 55
            && metrics.PatternVariety >= 4
            && starRating <= 2
            ? Math.Min(
              0.6,
              0.5
                + Math.Max(0, metrics.PatternVariety - 4) * 0.08
                + Math.Max(0, 7 - metrics.PeakNps5s) * 0.02)
            : 0;
        double lowRateTechnicalVocabularyCompression = metrics.NoteCount >= 2500
            && metrics.NoteCount <= 2900
            && metrics.ChordRatio >= 0.28
            && metrics.ChordRatio <= 0.34
            && metrics.HoldRatio < 0.02
            && metrics.FastRowRatio >= 0.85
            && metrics.PeakNps5s <= 24
            && metrics.SustainedNps10s <= 23.5
            && metrics.JackPressure >= 145
            && metrics.JackPressure <= 160
            && metrics.PatternVariety >= 2.7
            && metrics.RowIntervalEntropy >= 2.4
            ? 0.3
            : 0;
        double lightRowBurstStreamBonus = metrics.NoteCount >= 1000
            && metrics.NoteCount <= 1500
            && metrics.ChordRatio >= 0.16
            && metrics.ChordRatio <= 0.24
            && metrics.HoldRatio < 0.02
            && metrics.PeakNps5s >= 15
            && metrics.SustainedNps10s >= 14
            && metrics.RowBurstPressure >= 22
            && metrics.FastRowRatio >= 0.5
            && metrics.PatternVariety >= 3.2
            && starRating >= 3.5
            && starRating <= 4
            ? Math.Min(
              0.68,
              0.5
                + Math.Max(0, metrics.RowBurstPressure - 22) * 0.018
                + Math.Max(0, metrics.FastRowRatio - 0.5) * 0.18)
            : 0;
        double compactChordFlowTechBonus = metrics.NoteCount >= 1200
            && metrics.NoteCount <= 2300
            && metrics.ChordRatio >= 0.18
            && metrics.ChordRatio <= 0.46
            && metrics.HoldRatio < 0.08
            && metrics.ChordSizeChangeRate >= 0.3
            && metrics.DirectionChangeRate >= 0.62
            && (metrics.FastRowRatio >= 0.4 || metrics.RowBurstPressure >= 20)
            && metrics.TechPressure >= 5.4
            && starRating >= 4.45
            && starRating <= 5.25
            ? Math.Min(
              0.32,
              0.16
                + Math.Max(0, metrics.ChordSizeChangeRate - 0.3) * 0.32
                + Math.Max(0, metrics.FastRowRatio - 0.4) * 0.12
                + Math.Max(0, metrics.RowBurstPressure - 18) * 0.008)
            : 0;
        double fastTechnicalSpeedFloorBonus = metrics.NoteCount >= 1800
            && metrics.NoteCount <= 2200
            && metrics.ChordRatio >= 0.18
            && metrics.ChordRatio <= 0.24
            && metrics.HoldRatio < 0.03
            && metrics.PeakNps5s >= 26
            && metrics.SustainedNps10s >= 24
            && metrics.FastRowRatio >= 0.85
            && metrics.RowBurstPressure >= 26
            && metrics.JackPressure >= 145
            && metrics.TechPressure >= 5.7
            && starRating >= 5.6
            && starRating <= 5.9
            ? Math.Min(
              0.48,
              0.36
                + Math.Max(0, metrics.FastRowRatio - 0.85) * 0.18
                + Math.Max(0, metrics.RowBurstPressure - 26) * 0.012
                + Math.Max(0, metrics.PeakNps5s - 26) * 0.035)
            : 0;
        double variedTechnicalAnchorBridgeBonus = metrics.ChordRatio >= 0.25
            && metrics.ChordRatio <= 0.36
            && metrics.HoldRatio < 0.08
            && metrics.PeakNps5s >= 27
            && metrics.SustainedNps10s >= 26
            && metrics.JackPressure >= 165
            && metrics.PatternVariety >= 3
            && metrics.NoteCount >= 2300
            && metrics.NoteCount <= 3300
            ? Math.Min(
              0.12,
              0.1
                + Math.Max(0, metrics.JackPressure - 165) * 0.0004
                + Math.Max(0, metrics.PatternVariety - 3) * 0.015)
            : 0;
        double lowChordTechnicalSpeedBridgeBonus = metrics.ChordRatio >= 0.14
            && metrics.ChordRatio <= 0.2
            && metrics.HoldRatio < 0.04
            && metrics.NoteCount >= 3000
            && metrics.FastRowRatio >= 0.82
            && metrics.PeakNps5s >= 26.5
            && metrics.PeakNps5s <= 27.5
            && metrics.SustainedNps10s >= 26
            && metrics.SustainedNps10s <= 27
            && metrics.JackPressure >= 110
            && metrics.JackPressure <= 120
            && metrics.PatternVariety >= 2.4
            && metrics.PatternVariety <= 2.6
            && metrics.RowIntervalEntropy <= 0.95
            ? 0.35
            : 0;
        double highAnchorTechDeltaBridgeBonus = metrics.NoteCount >= 2300
            && metrics.NoteCount <= 2500
            && metrics.ChordRatio >= 0.27
            && metrics.ChordRatio <= 0.3
            && metrics.HoldRatio < 0.02
            && metrics.FastRowRatio >= 0.8
            && metrics.FastRowRatio <= 0.85
            && metrics.PeakNps5s >= 27.8
            && metrics.PeakNps5s <= 28.2
            && metrics.JackPressure >= 205
            && metrics.PatternVariety >= 3.6
            && metrics.RowIntervalEntropy >= 2.4
            ? 0.3
            : 0;
        double compactGammaTechCalibrationBridgeBonus = metrics.NoteCount >= 3600
            && metrics.NoteCount <= 3900
            && metrics.ChordRatio >= 0.26
            && metrics.ChordRatio <= 0.29
            && metrics.HoldRatio < 0.02
            && metrics.FastRowRatio >= 0.86
            && metrics.FastRowRatio <= 0.9
            && metrics.PeakNps5s >= 25.5
            && metrics.PeakNps5s <= 26.1
            && metrics.JackPressure >= 145
            && metrics.JackPressure <= 155
            && metrics.PatternVariety >= 2.45
            && metrics.PatternVariety <= 2.65
            && metrics.RowIntervalEntropy >= 1.8
            && metrics.RowIntervalEntropy <= 2.1
            ? 0.1
            : 0;
        double lowEntropyTechDeltaBridgeBonus = metrics.NoteCount >= 3600
            && metrics.NoteCount <= 3800
            && metrics.ChordRatio >= 0.42
            && metrics.ChordRatio <= 0.45
            && metrics.HoldRatio < 0.02
            && metrics.FastRowRatio >= 0.92
            && metrics.PeakNps5s >= 30
            && metrics.SustainedNps10s >= 29
            && metrics.JackPressure >= 130
            && metrics.JackPressure <= 142
            && metrics.PatternVariety <= 2.05
            && metrics.RowIntervalEntropy <= 0.5
            ? 0.5
            : 0;
        double shortLnHybridTechCompression = metrics.NoteCount >= 3300
            && metrics.NoteCount <= 3500
            && metrics.ChordRatio >= 0.35
            && metrics.ChordRatio <= 0.38
            && metrics.HoldRatio >= 0.08
            && metrics.HoldRatio <= 0.09
            && metrics.FastRowRatio >= 0.7
            && metrics.FastRowRatio <= 0.78
            && metrics.PeakNps5s >= 28.5
            && metrics.PeakNps5s <= 29.5
            && metrics.PatternVariety >= 3.3
            && metrics.RowIntervalEntropy >= 2.5
            ? 0.4
            : 0;
        double compactMidChordHandstreamCompression = metrics.NoteCount >= 2600
            && metrics.NoteCount <= 2800
            && metrics.ChordRatio >= 0.38
            && metrics.ChordRatio <= 0.41
            && metrics.HoldRatio < 0.02
            && metrics.FastRowRatio >= 0.74
            && metrics.FastRowRatio <= 0.78
            && metrics.PeakNps5s >= 30
            && metrics.SustainedNps10s >= 29
            && metrics.JackPressure >= 135
            && metrics.JackPressure <= 142
            && metrics.PatternVariety >= 2.3
            && metrics.PatternVariety <= 2.5
            && metrics.RowIntervalEntropy >= 1.2
            && metrics.RowIntervalEntropy <= 1.4
            ? 0.1
            : 0;
        double midChordTechOvercallCompression = metrics.NoteCount >= 3200
            && metrics.NoteCount <= 3400
            && metrics.ChordRatio >= 0.6
            && metrics.ChordRatio <= 0.63
            && metrics.HoldRatio < 0.02
            && metrics.FastRowRatio >= 0.6
            && metrics.FastRowRatio <= 0.7
            && metrics.PeakNps5s >= 30.5
            && metrics.SustainedNps10s >= 30
            && metrics.JackPressure >= 125
            && metrics.JackPressure <= 135
            && metrics.PatternVariety >= 2.9
            && metrics.RowIntervalEntropy >= 1.7
            ? 0.2
            : 0;
        double moderateBurstTechCompression = burstTechBonus > 0
            && metrics.ChordRatio >= 0.3
            && metrics.ChordRatio <= 0.38
            && metrics.JackPressure < 180
            && metrics.NoteCount >= 3000
            && starRating >= 6.1
            ? Math.Min(
              0.78,
              0.18
                + Math.Max(0, metrics.NoteCount - 3000) * 0.00025
                + Math.Max(0, metrics.PeakNps1s - 34) * 0.08
                + Math.Max(0, starRating - 6.1) * 0.25)
            : 0;
        double compactHighChordTechCompression = metrics.NoteCount >= 2000
            && metrics.NoteCount <= 2600
            && metrics.ChordRatio >= 0.5
            && metrics.ChordRatio <= 0.56
            && metrics.HoldRatio < 0.04
            && metrics.JackPressure < 155
            && metrics.TechPressure >= 7.2
            && metrics.SustainedNps10s >= 24
            && starRating >= 5.6
            && starRating <= 6
            ? MinGate(
              (metrics.ChordRatio - 0.48) / 0.04,
              (0.58 - metrics.ChordRatio) / 0.04,
              (metrics.TechPressure - 7) / 0.8,
              (155 - metrics.JackPressure) / 20) * 0.06
            : 0;
        double shortSpikeGate = metrics.NoteCount >= 250
            && metrics.NoteCount <= 2200
            && metrics.StrainSpikiness >= 0.55
            && metrics.SustainedPressureRatio <= 0.58
            && metrics.PeakNps1s >= metrics.SustainedNps10s * 1.9
            ? MinGate(
              (metrics.StrainSpikiness - 0.45) / 0.55,
              (0.62 - metrics.SustainedPressureRatio) / 0.25,
              (metrics.PeakNps1s / Math.Max(1, metrics.SustainedNps10s) - 1.5) / 2.5,
              (2400 - metrics.NoteCount) / 1400)
            : 0;
        double shortSpikeCompression = shortSpikeGate * Math.Min(
            2.45,
            0.85
              + Math.Max(0, metrics.PeakNps1s - metrics.SustainedNps10s) * 0.018
              + Math.Max(0, metrics.StrainSpikiness - 0.55) * 0.7);
        double localizedJumptrillSpikeGate = metrics.NoteCount >= 4000
            && metrics.ChordRatio >= 0.48
            && metrics.ChordRatio <= 0.64
            && metrics.HoldRatio < 0.1
            && metrics.PeakNps5s >= 35
            && metrics.SustainedNps10s >= 34
            && metrics.JackPressure >= 145
            && metrics.StrainSpikiness >= 1.6
            && metrics.Nps5sP90 <= metrics.PeakNps5s - 4
            && metrics.Nps5sP50 <= metrics.PeakNps5s - 10
            ? Clamp01(0.35 + MinGate(
              (metrics.PeakNps5s - metrics.Nps5sP90 - 3.5) / 8,
              (metrics.PeakNps5s - metrics.Nps5sP50 - 8) / 12,
              (metrics.StrainSpikiness - 1.4) / 1,
              (metrics.ChordRatio - 0.45) / 0.08,
              (0.66 - metrics.ChordRatio) / 0.08) * 0.65)
            : 0;
        double localizedJumptrillSpikeCompression = localizedJumptrillSpikeGate * Math.Min(
            2.6,
            1.8
              + Math.Max(0, metrics.PeakNps5s - metrics.Nps5sP90 - 4) * 0.09
              + Math.Max(0, starRating - 7) * 0.25);
        double chordedSpeedBonus = chordedSpeedGate * Math.Min(
            0.95,
            Math.Max(0, metrics.SustainedNps10s - 23) * 0.24 + Math.Max(0, metrics.PeakNps5s - 25) * 0.05);
        double denseChordedSpeedBonus = denseChordedSpeedGate * Math.Min(
            0.95,
            Math.Max(0, metrics.SustainedNps10s - 23) * 0.2 + Math.Max(0, metrics.PeakNps5s - 25) * 0.04);
        double chordjackEnduranceGate = Math.Max(
            0,
            Math.Min(
              1,
              Math.Min(
                (durationMs - 90000) / 90000,
                (metrics.NoteCount - 1600) / 2600)));
        double chordjackEnduranceMultiplier = 0.55 + chordjackEnduranceGate * 0.45;
        double strongJackGate = Math.Max(0, Math.Min(1, (metrics.JackPressure - 110) / 40));
        double etaJackPressureGate = Math.Max(0, Math.Min(1, (metrics.JackPressure - 185) / 30));
        double highChordJackBonus = highChordGate * Math.Min(0.42, Math.Max(0, metrics.JackPressure - 100) / 120);
        double highChordSoftJackPenalty = denseChordWallGate * (1 - etaJackPressureGate) * 0.35;
        double denseJackSrCompressionBase = denseJackFileGate
            * Math.Max(0, Math.Min(1, (starRating - 6.6) / 0.7))
            * 0.28;
        double denseJackTechNerf = denseJackFileGate
            * Math.Min(0.82, 0.68 + Math.Max(0, metrics.TechPressure - 8) * 0.08);
        double lowSrDenseWallJackBonus = denseWallJackGate
            * Math.Max(0, Math.Min(1, (6.45 - starRating) / 0.95))
            * Math.Min(
              0.92,
              Math.Max(0, 6.45 - starRating) * 0.78
                + Math.Max(0, metrics.SustainedNps10s - 24) * 0.035
                + Math.Max(0, metrics.ChordRatio - 0.78) * 0.3);
        double compactJackUnderrateBonus = compactJackUnderrateGate
            * Math.Min(
              0.72,
              Math.Max(0, 6.35 - starRating) * 0.8
                + Math.Max(0, metrics.JackPressure - 160) * 0.015
                + Math.Max(0, metrics.SustainedNps10s - 25) * 0.075);
        double lowRateHighChordJackTaper = metrics.HoldRatio >= 0.08 || starRating <= 6.55
            ? 1
            : Clamp01((6.72 - starRating) / 0.17);
        double lowRateHighChordJackBonus = metrics.NoteCount >= 1800
            && metrics.NoteCount <= 2700
            && metrics.ChordRatio >= 0.8
            && metrics.HoldRatio < 0.16
            && metrics.JackPressure >= 150
            && metrics.JackPressure <= 165
            && metrics.SustainedNps10s >= 27
            && starRating >= 5.9
            && starRating <= 6.8
            ? lowRateHighChordJackTaper * Math.Min(
              0.34,
              0.12
                + Math.Max(0, metrics.SustainedNps10s - 27) * 0.04
                + Math.Max(0, metrics.ChordRatio - 0.8) * 0.15
                + Math.Max(0, 6.8 - starRating) * 0.25)
            : 0;
        double slowRepetitiveJackstreamBonus = slowRepetitiveJackstreamGate * 0.55;
        double ratedRepetitiveSpeedjackBonus = ratedRepetitiveSpeedjackGate * 1.05;
        double repetitiveSpeedjackTechCompression = slowRepetitiveJackstreamGate * 0.34
            + ratedRepetitiveSpeedjackGate * 0.75;
        double compactJackOverboostCompression = compactJackUnderrateBonus
            * Clamp01((starRating - 6.08) / 0.12)
            * Clamp01((metrics.ChordRatio - 0.62) / 0.06)
            * Clamp01((metrics.JackPressure - 172) / 8)
            * 1.2;
        double mediumWallJackSrCompression = metrics.NoteCount >= 3000
            && metrics.ChordRatio >= 0.62
            && metrics.ChordRatio <= 0.73
            && metrics.HoldRatio < 0.08
            && metrics.JackPressure >= 145
            && metrics.JackPressure <= 162
            && metrics.SustainedNps10s >= 30
            && starRating >= 6.8
            ? Math.Min(
              0.68,
              Math.Max(0, starRating - 6.7) * 0.75
                + Math.Max(0, metrics.SustainedNps10s - 30) * 0.11
                + Math.Max(0, 160 - metrics.JackPressure) * 0.045)
            : 0;
        double compactHighChordDeltaJackBonus = metrics.NoteCount >= 2500
            && metrics.NoteCount <= 3300
            && metrics.ChordRatio >= 0.82
            && metrics.ChordRatio <= 0.9
            && metrics.HoldRatio < 0.08
            && metrics.JackPressure >= 148
            && metrics.SustainedNps10s >= 30
            && starRating >= 6.55
            && starRating <= 6.85
            ? Math.Min(
              0.36,
              0.18
                + Math.Max(0, starRating - 6.55) * 0.42
                + Math.Max(0, metrics.SustainedNps10s - 30) * 0.08
                + Math.Max(0, metrics.ChordRatio - 0.82) * 0.5)
            : 0;
        double denseWallJackPenaltyRelief = highChordSoftJackPenalty
            * denseWallJackGate
            * Math.Max(0, Math.Min(1, (6.35 - starRating) / 0.55));
        double wallJackTechNerf = Math.Min(
            0.9,
            denseJackFileGate * 0.45
              + denseWallJackGate * 0.65
              + (metrics.ChordRatio >= 0.62 && metrics.ChordRatio <= 0.74 && metrics.JackPressure >= 145 && metrics.JackPressure < 165 ? 0.45 : 0)
              + (metrics.ChordRatio >= 0.74 && metrics.JackPressure >= 145 ? 0.25 : 0));
        double lowChordBurstStreamNerf = metrics.NoteCount >= 3600
            && metrics.NoteCount <= 5200
            && metrics.ChordRatio >= 0.16
            && metrics.ChordRatio <= 0.28
            && metrics.HoldRatio < 0.08
            && metrics.SustainedNps10s >= 28.5
            && metrics.SustainedNps10s <= 33
            && metrics.PeakNps1s >= 38
            && metrics.JackPressure >= 135
            && metrics.TechPressure <= 6.3
            ? Math.Min(
              0.82,
              Math.Max(0, metrics.PeakNps1s - 36) * 0.045
                + Math.Max(0, metrics.JackPressure - 135) * 0.003
                + Math.Max(0, 0.28 - metrics.ChordRatio) * 0.25
                + Math.Max(0, 0.78 - metrics.SustainedPressureRatio) * 1.8
                + Math.Max(0, metrics.RowBurstPressure - 35) * 0.015
                + Math.Max(0, metrics.FastRowRatio - 0.85) * 0.7)
            : 0;
        double lowChordBurstTechNerf = lowChordBurstStreamNerf * 1.5;
        double farmJumptrillGate = metrics.NoteCount >= 4000
            && metrics.ChordRatio >= 0.42
            && metrics.ChordRatio <= 0.58
            && metrics.HoldRatio >= 0.1
            && metrics.HoldRatio <= 0.24
            && metrics.StreamPressure <= 6.45
            && metrics.TechPressure <= 8.6
            && metrics.ChordjackPressure <= 220
            && durationMs >= 180000
            ? MinGate(
              (metrics.NoteCount - 3800) / 600,
              (metrics.ChordRatio - 0.38) / 0.1,
              (0.62 - metrics.ChordRatio) / 0.12,
              (metrics.HoldRatio - 0.08) / 0.06,
              (0.26 - metrics.HoldRatio) / 0.08,
              (8.6 - metrics.TechPressure) / 0.9)
            : 0;
        double ratedVibroJumptrillGate = farmJumptrillGate * MinGate(
            (metrics.JackPressure - 160) / 18,
            (metrics.PeakNps1s - 44) / 6,
            (metrics.SustainedNps10s - 30) / 4);
        double farmJumptrillJackCompression = farmJumptrillGate * 0.45 + ratedVibroJumptrillGate * 0.95;
        double farmJumptrillStreamCompression = farmJumptrillGate * 0.5 + ratedVibroJumptrillGate * 0.75;
        double farmJumptrillHandstreamCompression = farmJumptrillGate * 0.75 + ratedVibroJumptrillGate * 1.3;
        double farmJumptrillStaminaCompression = farmJumptrillGate * 0.35 + ratedVibroJumptrillGate * 0.9;
        double farmJumptrillChordjackCompression = farmJumptrillGate * 0.75 + ratedVibroJumptrillGate * 1.35;
        double farmJumptrillTechCompression = farmJumptrillGate * 0.9 + ratedVibroJumptrillGate * 1.3;
        double shortDenseChordWallPenalty = denseChordWallGate
            * Math.Max(0, Math.Min(1, (155 - metrics.JackPressure) / 35))
            * Math.Max(0, Math.Min(1, (2400 - metrics.NoteCount) / 900))
            * Math.Max(0, Math.Min(1, (115000 - durationMs) / 45000));
        double highRateShortDenseChordWallPenalty = shortDenseChordWallPenalty
            * Math.Max(0, Math.Min(1, (starRating - 5.75) / 0.45));
        double steadySpeedMapGate = Math.Max(
            0,
            Math.Min(
              1,
              Math.Min(
                (0.42 - metrics.ChordRatio) / 0.18,
                Math.Min(
                  (155 - metrics.JackPressure) / 45,
                  (metrics.SustainedNps10s - 24) / 6))));
        double longEnduranceMapGate = Math.Max(
            0,
            Math.Min(
              1,
              Math.Min(
                (metrics.NoteCount - 4200) / 1800,
                Math.Min(
                  (metrics.SustainedNps10s - 26) / 4,
                  (165 - metrics.JackPressure) / 45))));
        double longSparseJackDropMapGate = durationMs >= 300000
            && metrics.NoteCount >= 4800
            && metrics.NoteCount <= 7200
            && metrics.ChordRatio >= 0.48
            && metrics.ChordRatio <= 0.66
            && metrics.HoldRatio < 0.13
            && metrics.SustainedNps10s >= 18
            && metrics.SustainedNps10s <= 27.5
            && metrics.JackPressure >= 135
            && metrics.JackPressure <= 180
            && metrics.FastRowRatio <= 0.38
            && starRating >= 6
            && starRating <= 7.2
            ? MinGate(
              (durationMs - 280000) / 80000,
              (metrics.NoteCount - 4600) / 1000,
              (metrics.ChordRatio - 0.46) / 0.08,
              (0.68 - metrics.ChordRatio) / 0.08,
              (27.8 - metrics.SustainedNps10s) / 3.5,
              (metrics.JackPressure - 130) / 25,
              (185 - metrics.JackPressure) / 30,
              (0.4 - metrics.FastRowRatio) / 0.18)
            : 0;
        double longMidChordStaminaMapGate = metrics.NoteCount >= 4200
            && metrics.ChordRatio >= 0.42
            && metrics.ChordRatio <= 0.6
            && metrics.JackPressure < 150
            && metrics.HoldRatio < 0.08
            ? Math.Max(
              0,
              Math.Min(
                1,
                Math.Min(
                  (metrics.NoteCount - 4000) / 1600,
                  Math.Min(
                    (metrics.SustainedNps10s - 21) / 10,
                    (150 - metrics.JackPressure) / 55))))
            : 0;
        double fastLongMidChordStaminaGate = longMidChordStaminaMapGate
            * Math.Max(0, Math.Min(1, (metrics.SustainedNps10s - 27.5) / 2));
        double cyberLikeStaminaGate = longMidChordStaminaMapGate
            * Math.Max(0, Math.Min(1, (metrics.JackPressure - 110) / 30));
        double longMidChordSrNerf = cyberLikeStaminaGate
            * Math.Max(0, Math.Min(1, (starRating - 6) / 0.9))
            * Math.Max(0, Math.Min(1, (metrics.ChordRatio - 0.44) / 0.04));
        double moderateMidChordStaminaNerf = longMidChordStaminaMapGate
            * Math.Max(0, Math.Min(1, (metrics.SustainedNps10s - 21) / 4))
            * Math.Max(0, Math.Min(1, (27.5 - metrics.SustainedNps10s) / 2.5))
            * 0.43;
        double midChordRateCompressionNerf = metrics.NoteCount >= 4500
            && metrics.ChordRatio >= 0.42
            && metrics.ChordRatio <= 0.6
            && metrics.JackPressure < 150
            && metrics.HoldRatio < 0.08
            ? Math.Max(0, Math.Min(1, (metrics.SustainedNps10s - 20) / 5))
              * Math.Max(0, Math.Min(1, (28 - metrics.SustainedNps10s) / 5))
              * 0.25
            : 0;
        double highNoteMidRateHandstreamNerf = metrics.NoteCount >= 5500
            && metrics.ChordRatio >= 0.38
            && metrics.ChordRatio <= 0.56
            && metrics.JackPressure < 165
            && metrics.HoldRatio < 0.08
            && metrics.SustainedNps10s >= 27
            && metrics.SustainedNps10s < 33.2
            ? Math.Max(0, Math.Min(1, (metrics.SustainedNps10s - 27) / 1))
              * (metrics.SustainedNps10s <= 31 ? 1 : Math.Max(0, Math.Min(1, (33.2 - metrics.SustainedNps10s) / 2.2)))
              * 0.78
            : 0;
        double highEndMidChordStaminaNerf = metrics.NoteCount >= 5500
            && metrics.ChordRatio >= 0.38
            && metrics.ChordRatio <= 0.56
            && metrics.JackPressure < 165
            && metrics.HoldRatio < 0.08
            && metrics.SustainedNps10s >= 31
            ? Math.Min(
              0.46,
              Math.Max(0, metrics.SustainedNps10s - 31) * 0.07
                + Math.Max(0, metrics.NoteCount - 5400) * 0.00005)
            : 0;
        double longJumpstreamStaminaCompression = metrics.NoteCount >= 7600
            && metrics.ChordRatio >= 0.45
            && metrics.ChordRatio <= 0.56
            && metrics.HoldRatio < 0.03
            && metrics.JackPressure < 135
            && metrics.SustainedNps10s >= 29
            && metrics.SustainedNps10s <= 32
            && metrics.FastRowRatio >= 0.8
            && metrics.SustainedPressureRatio >= 0.9
            && metrics.PatternVariety <= 2.2
            && durationMs >= 340000
            && starRating >= 6.2
            && starRating <= 6.7
            ? MinGate(
              (metrics.NoteCount - 7200) / 1400,
              (metrics.ChordRatio - 0.42) / 0.08,
              (0.58 - metrics.ChordRatio) / 0.08,
              (135 - metrics.JackPressure) / 25,
              (metrics.SustainedNps10s - 28.5) / 2,
              (32.5 - metrics.SustainedNps10s) / 2,
              (metrics.FastRowRatio - 0.76) / 0.14,
              (2.3 - metrics.PatternVariety) / 0.7,
              (durationMs - 320000) / 90000) * 0.38
            : 0;
        double simpleLongJumpstreamPatternCompression = metrics.NoteCount >= 8000
            && metrics.ChordRatio >= 0.46
            && metrics.ChordRatio <= 0.54
            && metrics.HoldRatio < 0.01
            && metrics.JackPressure < 135
            && metrics.SustainedNps10s >= 29
            && metrics.SustainedNps10s <= 31.5
            && metrics.FastRowRatio >= 0.8
            && metrics.SustainedPressureRatio >= 0.9
            && metrics.PatternVariety <= 2.1
            && metrics.RowIntervalEntropy <= 1.6
            && durationMs >= 380000
            && starRating >= 6.2
            && starRating <= 6.6
            ? MinGate(
              (metrics.NoteCount - 7800) / 900,
              (metrics.ChordRatio - 0.44) / 0.08,
              (0.56 - metrics.ChordRatio) / 0.08,
              (135 - metrics.JackPressure) / 25,
              (metrics.SustainedNps10s - 28.5) / 2,
              (31.8 - metrics.SustainedNps10s) / 1.3,
              (metrics.FastRowRatio - 0.76) / 0.14,
              (2.2 - metrics.PatternVariety) / 0.6,
              (1.75 - metrics.RowIntervalEntropy) / 0.5,
              (durationMs - 360000) / 80000) * 0.11
            : 0;
        double deltaHighMidChordTransitionNerf = metrics.NoteCount >= 5500
            && metrics.ChordRatio >= 0.38
            && metrics.ChordRatio <= 0.56
            && metrics.JackPressure < 165
            && metrics.HoldRatio < 0.08
            && metrics.SustainedNps10s >= 31.5
            && metrics.SustainedNps10s < 34.8
            ? Math.Max(0, Math.Min(1, (metrics.SustainedNps10s - 31.5) / 1.5))
              * Math.Max(0, Math.Min(1, (34.8 - metrics.SustainedNps10s) / 1.8))
              * 0.24
            : 0;
        double denseChordStaminaOverrateGate = metrics.NoteCount >= 5200
            && metrics.NoteCount <= 6500
            && metrics.ChordRatio >= 0.56
            && metrics.ChordRatio <= 0.68
            && metrics.HoldRatio < 0.04
            && metrics.JackPressure < 155
            && metrics.SustainedNps10s >= 33
            && metrics.SustainedNps10s <= 35.2
            && durationMs >= 220000
            && durationMs <= 290000
            && starRating >= 7.1
            && starRating <= 7.6
            ? MinGate(
              (metrics.NoteCount - 5000) / 900,
              (6500 - metrics.NoteCount) / 900,
              (metrics.ChordRatio - 0.54) / 0.06,
              (0.7 - metrics.ChordRatio) / 0.08,
              (metrics.SustainedNps10s - 33) / 0.8,
              (35.2 - metrics.SustainedNps10s) / 1.2,
              (155 - metrics.JackPressure) / 25,
              (starRating - 7.1) / 0.3,
              (7.6 - starRating) / 0.4)
            : 0;
        double longSparseJackDropJackCompression = longSparseJackDropMapGate * 1.25;
        double longSparseJackDropStreamCompression = longSparseJackDropMapGate * 0.72;
        double longSparseJackDropHandstreamCompression = longSparseJackDropMapGate * 0.72;
        double longSparseJackDropStaminaCompression = longSparseJackDropMapGate * 0.45;
        double longSparseJackDropChordjackCompression = longSparseJackDropMapGate * 1.35;
        double longSparseJackDropTechCompression = longSparseJackDropMapGate * 1.42;
        double denseChordStaminaCompression = denseChordStaminaOverrateGate * 1.25;
        double shortLnHybridStructuralGate = metrics.NoteCount >= 3600
            && metrics.NoteCount <= 6200
            && durationMs >= 250000
            && metrics.HoldRatio >= 0.18
            && metrics.HoldRatio < 0.28
            && metrics.LnDensity >= 0.12
            && metrics.LnDensity <= 0.3
            && metrics.LnReleasePressure >= 10
            && metrics.LnReleasePressure <= 24
            && metrics.LnHoldDurationP90 <= 800
            && metrics.ChordRatio >= 0.12
            && metrics.ChordRatio <= 0.34
            && metrics.PeakNps5s >= 18
            && metrics.PeakNps5s <= 26
            && metrics.SustainedNps10s >= 18
            && metrics.SustainedNps10s <= 24
            ? MinGate(
              (metrics.NoteCount - 3300) / 800,
              (6600 - metrics.NoteCount) / 900,
              (durationMs - 230000) / 80000,
              (metrics.HoldRatio - 0.16) / 0.06,
              (0.32 - metrics.HoldRatio) / 0.06,
              (metrics.LnDensity - 0.1) / 0.08,
              (0.32 - metrics.LnDensity) / 0.08,
              (metrics.LnReleasePressure - 8) / 5,
              (26 - metrics.LnReleasePressure) / 5,
              (1000 - metrics.LnHoldDurationP90) / 550,
              (metrics.ChordRatio - 0.1) / 0.08,
              (0.36 - metrics.ChordRatio) / 0.08,
              (26.5 - metrics.PeakNps5s) / 2.5,
              (25 - metrics.SustainedNps10s) / 2.5)
            : 0;
        double shortLnHybridStructuralCompression = shortLnHybridStructuralGate * Math.Min(
            0.78,
            0.52
              + Math.Max(0, metrics.LnReleasePressure - 12) * 0.014
              + Math.Max(0, 24 - metrics.PeakNps5s) * 0.02);
        double shortLnHybridRiceRequirementBonus = metrics.NoteCount >= 5500
            && durationMs >= 320000
            && metrics.HoldRatio >= 0.3
            && metrics.HoldRatio <= 0.42
            && metrics.LnDensity >= 0.18
            && metrics.LnDensity <= 0.3
            && metrics.LnReleasePressure >= 24
            && metrics.LnReleasePressure <= 30
            && metrics.LnHoldDurationP90 >= 220
            && metrics.LnHoldDurationP90 <= 300
            && metrics.ChordRatio >= 0.28
            && metrics.ChordRatio <= 0.42
            && metrics.PeakNps5s >= 27
            && metrics.PeakNps5s <= 32
            && metrics.SustainedNps10s >= 26
            && metrics.SustainedNps10s <= 30
            ? 1.55
            : 0;
        double lowChordSteadySpeedStructuralGate = metrics.NoteCount >= 1800
            && metrics.NoteCount <= 5400
            && metrics.ChordRatio >= 0.06
            && metrics.ChordRatio <= 0.17
            && metrics.HoldRatio < 0.08
            && metrics.PeakNps5s >= 24.8
            && metrics.PeakNps5s <= 26.6
            && metrics.SustainedNps10s >= 24
            && metrics.SustainedNps10s <= 26
            && metrics.FastRowRatio >= 0.78
            && metrics.JackPressure < 140
            && metrics.TechPressure < 5.3
            && metrics.ChordSizeChangeRate < 0.24
            ? MinGate(
              (metrics.NoteCount - 1600) / 650,
              (5700 - metrics.NoteCount) / 1100,
              (metrics.ChordRatio - 0.045) / 0.055,
              (0.19 - metrics.ChordRatio) / 0.055,
              (metrics.PeakNps5s - 24.4) / 1.1,
              (26.9 - metrics.PeakNps5s) / 1.1,
              (metrics.SustainedNps10s - 23.6) / 1.1,
              (26.3 - metrics.SustainedNps10s) / 1.1,
              (metrics.FastRowRatio - 0.74) / 0.16,
              (140 - metrics.JackPressure) / 32,
              (5.45 - metrics.TechPressure) / 0.9,
              (0.26 - metrics.ChordSizeChangeRate) / 0.12)
            : 0;
        double lowChordSteadySpeedStructuralCompression = lowChordSteadySpeedStructuralGate * Math.Min(
            1.05,
            0.74
              + Math.Max(0, 3000 - metrics.NoteCount) * 0.00012
              + Math.Max(0, 26 - metrics.SustainedNps10s) * 0.08
              + Math.Max(0, 0.16 - metrics.ChordRatio) * 0.55);
        double moderateChordSteadyStreamStructuralGate = metrics.NoteCount >= 2900
            && metrics.NoteCount <= 3500
            && durationMs >= 165000
            && durationMs <= 215000
            && metrics.ChordRatio >= 0.2
            && metrics.ChordRatio <= 0.27
            && metrics.HoldRatio < 0.03
            && metrics.PeakNps5s >= 25.2
            && metrics.PeakNps5s <= 31.2
            && metrics.SustainedNps10s >= 25
            && metrics.SustainedNps10s <= 31
            && metrics.StreamPressure >= 6
            && metrics.StreamPressure <= 6.55
            && metrics.JackPressure >= 105
            && metrics.JackPressure <= 145
            && metrics.ChordjackPressure <= 95
            && metrics.TechPressure >= 5.4
            && metrics.TechPressure <= 6.25
            && metrics.FastRowRatio >= 0.78
            && metrics.RowBurstPressure >= 22
            && metrics.RowBurstPressure <= 30
            && metrics.PatternVariety >= 2.7
            && metrics.PatternVariety <= 3.25
            && metrics.ChordSizeChangeRate <= 0.35
            && metrics.DirectionChangeRate >= 0.68
            && metrics.SustainedPressureRatio >= 0.84
            ? 1
            : 0;
        double moderateChordSteadyStreamStructuralCompression = moderateChordSteadyStreamStructuralGate * Math.Min(
            1.25,
            0.65 + Math.Max(0, 30.5 - metrics.SustainedNps10s) * 0.115);
        double compactHandstreamStaminaStructuralGate = metrics.NoteCount >= 2200
            && metrics.NoteCount <= 2700
            && durationMs >= 105000
            && durationMs <= 175000
            && metrics.ChordRatio >= 0.4
            && metrics.ChordRatio <= 0.5
            && metrics.HoldRatio >= 0.02
            && metrics.HoldRatio < 0.07
            && metrics.PeakNps5s >= 22
            && metrics.PeakNps5s <= 34
            && metrics.SustainedNps10s >= 21
            && metrics.SustainedNps10s <= 33
            && metrics.StreamPressure >= 5
            && metrics.StreamPressure <= 6.2
            && metrics.JackPressure >= 85
            && metrics.JackPressure <= 145
            && metrics.ChordjackPressure >= 85
            && metrics.ChordjackPressure <= 145
            && metrics.TechPressure >= 7.3
            && metrics.TechPressure <= 8.1
            && metrics.RowIntervalEntropy >= 1.1
            && metrics.RowIntervalEntropy <= 1.5
            && metrics.PatternVariety >= 2.3
            && metrics.PatternVariety <= 2.9
            && metrics.ChordSizeChangeRate >= 0.54
            && metrics.ChordSizeChangeRate <= 0.66
            && metrics.DirectionChangeRate >= 0.6
            && metrics.DirectionChangeRate <= 0.7
            && metrics.SustainedPressureRatio >= 0.82
            && metrics.SustainedPressureRatio <= 0.91
            ? 1
            : 0;
        double compactHandstreamStaminaStructuralCompression = compactHandstreamStaminaStructuralGate * Math.Min(
            1.16,
            0.9
              + Math.Max(0, 24.5 - metrics.SustainedNps10s) * 0.04
              + Math.Max(0, 1 - Math.Abs(metrics.SustainedNps10s - 24) / 1.3) * 0.12
              + Math.Max(0, 1 - Math.Abs(metrics.SustainedNps10s - 28.2) / 3.5) * 0.14);
        double compactHandstreamStaminaTechCompression = compactHandstreamStaminaStructuralGate * 0.28;
        double compactTechnicalFlowStructuralGate = metrics.NoteCount >= 2100
            && metrics.NoteCount <= 3600
            && metrics.ChordRatio >= 0.32
            && metrics.ChordRatio <= 0.56
            && metrics.HoldRatio < 0.03
            && metrics.PeakNps5s >= 24.5
            && metrics.PeakNps5s <= 27.4
            && metrics.SustainedNps10s >= 23.6
            && metrics.SustainedNps10s <= 26.6
            && metrics.JackPressure >= 120
            && metrics.JackPressure <= 165
            && metrics.TechPressure >= 6.7
            && metrics.TechPressure <= 8.4
            && metrics.ChordSizeChangeRate >= 0.48
            && metrics.DirectionChangeRate >= 0.62
            ? MinGate(
              (metrics.NoteCount - 1900) / 600,
              (3800 - metrics.NoteCount) / 700,
              (metrics.ChordRatio - 0.3) / 0.1,
              (0.58 - metrics.ChordRatio) / 0.1,
              (metrics.PeakNps5s - 24.2) / 1.2,
              (27.8 - metrics.PeakNps5s) / 1.2,
              (metrics.SustainedNps10s - 23.3) / 1.2,
              (26.9 - metrics.SustainedNps10s) / 1.2,
              (165 - metrics.JackPressure) / 28,
              (metrics.TechPressure - 6.5) / 1,
              (8.6 - metrics.TechPressure) / 1,
              (metrics.ChordSizeChangeRate - 0.46) / 0.12)
            : 0;
        double compactTechnicalFlowStructuralCompression = compactTechnicalFlowStructuralGate * Math.Min(
            1.05,
            0.72
              + Math.Max(0, metrics.FastRowRatio - 0.5) * 0.22
              + Math.Max(0, metrics.ChordSizeChangeRate - 0.5) * 0.5
              + Math.Max(0, 26.8 - metrics.PeakNps5s) * 0.04);
        double compactChordWallStructuralGate = metrics.NoteCount >= 2000
            && metrics.NoteCount <= 3200
            && metrics.ChordRatio >= 0.68
            && metrics.ChordRatio <= 0.86
            && metrics.HoldRatio < 0.1
            && metrics.JackPressure >= 135
            && metrics.JackPressure <= 158
            && metrics.SustainedNps10s >= 25.8
            && metrics.SustainedNps10s <= 30.4
            && metrics.ChordjackPressure >= 185
            && metrics.PatternVariety <= 3.05
            ? MinGate(
              (metrics.NoteCount - 1800) / 550,
              (3400 - metrics.NoteCount) / 700,
              (metrics.ChordRatio - 0.66) / 0.09,
              (0.88 - metrics.ChordRatio) / 0.09,
              (metrics.JackPressure - 132) / 16,
              (160 - metrics.JackPressure) / 16,
              (metrics.SustainedNps10s - 25.4) / 1.5,
              (30.8 - metrics.SustainedNps10s) / 1.8,
              (metrics.ChordjackPressure - 175) / 38,
              (3.15 - metrics.PatternVariety) / 0.55)
            : 0;
        double compactChordWallStructuralCompression = compactChordWallStructuralGate * Math.Min(
            1.22,
            0.82
              + Math.Max(0, 0.2 - metrics.FastRowRatio) * 1.1
              + Math.Max(0, metrics.ChordRatio - 0.76) * 1.1
              + Math.Max(0, metrics.ChordSizeChangeRate - 0.52) * 0.38);
        double simpleDenseChordWallStructuralGate = metrics.NoteCount >= 2000
            && metrics.NoteCount <= 2600
            && metrics.ChordRatio >= 0.8
            && metrics.ChordRatio <= 0.87
            && metrics.HoldRatio < 0.04
            && metrics.JackPressure >= 140
            && metrics.JackPressure <= 152
            && metrics.SustainedNps10s >= 25.4
            && metrics.SustainedNps10s <= 27.4
            && metrics.FastRowRatio <= 0.05
            && metrics.RowBurstPressure <= 12
            && metrics.RowIntervalEntropy <= 0.75
            && metrics.PatternVariety <= 1.95
            && metrics.SustainedPressureRatio >= 0.82
            ? MinGate(
              (metrics.NoteCount - 1900) / 400,
              (2700 - metrics.NoteCount) / 500,
              (metrics.ChordRatio - 0.78) / 0.08,
              (0.89 - metrics.ChordRatio) / 0.08,
              (metrics.JackPressure - 138) / 10,
              (154 - metrics.JackPressure) / 10,
              (metrics.SustainedNps10s - 25) / 1,
              (27.8 - metrics.SustainedNps10s) / 1.2,
              (0.06 - metrics.FastRowRatio) / 0.06,
              (12.5 - metrics.RowBurstPressure) / 4,
              (0.85 - metrics.RowIntervalEntropy) / 0.45,
              (2.05 - metrics.PatternVariety) / 0.55)
            : 0;
        double simpleDenseChordWallStructuralCompression = simpleDenseChordWallStructuralGate * 0.48;
        double lowRateDenseChordWallGate = metrics.NoteCount >= 1900
            && metrics.NoteCount <= 2400
            && metrics.ChordRatio >= 0.78
            && metrics.ChordRatio <= 0.9
            && metrics.HoldRatio < 0.04
            && metrics.JackPressure >= 100
            && metrics.JackPressure <= 140
            && metrics.SustainedNps10s >= 20.5
            && metrics.SustainedNps10s <= 27.6
            && metrics.FastRowRatio <= 0.16
            && metrics.RowIntervalEntropy <= 1.25
            && metrics.PatternVariety <= 2.65
            ? MinGate(
              (metrics.NoteCount - 1800) / 500,
              (2500 - metrics.NoteCount) / 500,
              (metrics.ChordRatio - 0.76) / 0.08,
              (0.92 - metrics.ChordRatio) / 0.08,
              (metrics.JackPressure - 96) / 20,
              (144 - metrics.JackPressure) / 20,
              (metrics.SustainedNps10s - 20) / 1.6,
              (28 - metrics.SustainedNps10s) / 1.6,
              (0.18 - metrics.FastRowRatio) / 0.12,
              (1.35 - metrics.RowIntervalEntropy) / 0.55)
            : 0;
        double variedLowRateDenseChordWallGate = metrics.NoteCount >= 1900
            && metrics.NoteCount <= 2400
            && metrics.ChordRatio >= 0.78
            && metrics.ChordRatio <= 0.9
            && metrics.HoldRatio < 0.04
            && metrics.JackPressure >= 100
            && metrics.JackPressure <= 140
            && metrics.SustainedNps10s >= 20.5
            && metrics.SustainedNps10s <= 27.6
            && metrics.FastRowRatio <= 0.16
            && metrics.RowIntervalEntropy >= 1
            && metrics.PatternVariety >= 2.3
            && metrics.PatternVariety <= 2.65
            ? MinGate(
              (metrics.NoteCount - 1800) / 500,
              (2500 - metrics.NoteCount) / 500,
              (metrics.ChordRatio - 0.76) / 0.08,
              (0.92 - metrics.ChordRatio) / 0.08,
              (metrics.JackPressure - 96) / 20,
              (144 - metrics.JackPressure) / 20,
              (metrics.SustainedNps10s - 20) / 1.6,
              (28 - metrics.SustainedNps10s) / 1.6,
              (0.18 - metrics.FastRowRatio) / 0.12,
              (metrics.RowIntervalEntropy - 0.95) / 0.3,
              (metrics.PatternVariety - 2.2) / 0.35,
              (2.75 - metrics.PatternVariety) / 0.35)
            : 0;
        double lowRateDenseChordWallCompression = lowRateDenseChordWallGate * Math.Min(
            2.2,
            1.08
              + Math.Max(0, 27.6 - metrics.SustainedNps10s) * 0.2
              + Math.Max(0, 0.86 - metrics.ChordRatio) * 0.4
              + Math.Max(0, 1.1 - metrics.RowIntervalEntropy) * 0.18) + variedLowRateDenseChordWallGate * Math.Min(
            1.35,
            0.92
              + Math.Max(0, metrics.PatternVariety - 2.3) * 0.55
              + Math.Max(0, metrics.RowIntervalEntropy - 1) * 0.4);
        double simpleMidHighChordWallStructuralGate = metrics.NoteCount >= 2000
            && metrics.NoteCount <= 2600
            && metrics.ChordRatio >= 0.68
            && metrics.ChordRatio <= 0.76
            && metrics.HoldRatio < 0.03
            && metrics.JackPressure >= 145
            && metrics.JackPressure <= 156
            && metrics.ChordjackPressure >= 185
            && metrics.ChordjackPressure <= 215
            && metrics.SustainedNps10s >= 24.5
            && metrics.SustainedNps10s <= 26.3
            && metrics.PeakNps5s >= 25.5
            && metrics.PeakNps5s <= 27.2
            && metrics.FastRowRatio <= 0.06
            && metrics.RowBurstPressure <= 12.5
            && metrics.RowIntervalEntropy <= 1.4
            && metrics.SustainedPressureRatio >= 0.78
            ? MinGate(
              (metrics.NoteCount - 1900) / 400,
              (2700 - metrics.NoteCount) / 500,
              (metrics.ChordRatio - 0.66) / 0.08,
              (0.78 - metrics.ChordRatio) / 0.08,
              (metrics.JackPressure - 142) / 10,
              (158 - metrics.JackPressure) / 10,
              (metrics.ChordjackPressure - 180) / 25,
              (220 - metrics.ChordjackPressure) / 25,
              (metrics.SustainedNps10s - 24.2) / 1,
              (26.6 - metrics.SustainedNps10s) / 1.1,
              (metrics.PeakNps5s - 25.2) / 1,
              (27.5 - metrics.PeakNps5s) / 1.1,
              (0.065 - metrics.FastRowRatio) / 0.055,
              (13 - metrics.RowBurstPressure) / 3,
              (1.45 - metrics.RowIntervalEntropy) / 0.45)
            : 0;
        double simpleMidHighChordWallStructuralCompression = simpleMidHighChordWallStructuralGate * 1.85;
        double awkwardMidRateChordjackWallGate = metrics.NoteCount >= 2200
            && metrics.NoteCount <= 2650
            && metrics.ChordRatio >= 0.58
            && metrics.ChordRatio <= 0.68
            && metrics.HoldRatio < 0.03
            && durationMs >= 200000
            && durationMs <= 225000
            && metrics.JackPressure >= 155
            && metrics.JackPressure <= 176
            && metrics.ChordjackPressure >= 190
            && metrics.ChordjackPressure <= 214
            && metrics.PeakNps5s >= 29.4
            && metrics.PeakNps5s <= 32.4
            && metrics.SustainedNps10s >= 28
            && metrics.SustainedNps10s <= 31
            && metrics.FastRowRatio <= 0.25
            && metrics.RowBurstPressure >= 13
            && metrics.RowBurstPressure <= 16
            && metrics.RowIntervalEntropy >= 1.4
            && metrics.RowIntervalEntropy <= 1.85
            && metrics.ChordSizeChangeRate >= 0.5
            && metrics.ChordSizeChangeRate <= 0.6
            && metrics.SustainedPressureRatio < 0.72
            ? MinGate(
              (metrics.NoteCount - 2100) / 400,
              (2750 - metrics.NoteCount) / 500,
              (metrics.ChordRatio - 0.56) / 0.08,
              (0.7 - metrics.ChordRatio) / 0.08,
              (durationMs - 195000) / 20000,
              (230000 - durationMs) / 20000,
              (metrics.JackPressure - 152) / 12,
              (178 - metrics.JackPressure) / 12,
              (metrics.ChordjackPressure - 186) / 18,
              (216 - metrics.ChordjackPressure) / 18,
              (metrics.PeakNps5s - 29) / 1.2,
              (32.8 - metrics.PeakNps5s) / 1.2,
              (metrics.SustainedNps10s - 27.8) / 1,
              (31.2 - metrics.SustainedNps10s) / 1.2,
              (0.28 - metrics.FastRowRatio) / 0.12,
              (metrics.RowBurstPressure - 12.5) / 2,
              (16.5 - metrics.RowBurstPressure) / 2,
              (metrics.RowIntervalEntropy - 1.35) / 0.25,
              (1.9 - metrics.RowIntervalEntropy) / 0.25)
            : 0;
        double awkwardMidRateChordjackWallCompression = awkwardMidRateChordjackWallGate * Math.Min(
            1.15,
            0.9
              + Math.Max(0, 30.5 - metrics.SustainedNps10s) * 0.12
              + Math.Max(0, (durationMs - 210000) / 20000) * 0.22
              + Math.Max(0, 0.7 - metrics.SustainedPressureRatio) * 0.4);
        double denseJackSrCompression = denseJackSrCompressionBase * (1 - Clamp01(awkwardMidRateChordjackWallGate * 3));
        double midHighChordSustainedTechStructuralGate = metrics.NoteCount >= 3400
            && metrics.NoteCount <= 5000
            && metrics.ChordRatio >= 0.55
            && metrics.ChordRatio <= 0.7
            && metrics.HoldRatio < 0.08
            && metrics.PeakNps5s >= 29
            && metrics.PeakNps5s <= 32
            && metrics.SustainedNps10s >= 28.5
            && metrics.SustainedNps10s <= 31
            && metrics.JackPressure >= 110
            && metrics.JackPressure <= 155
            && metrics.TechPressure >= 8.2
            && metrics.RowBurstPressure <= 20
            && metrics.RowIntervalEntropy <= 1.9
            && metrics.ChordSizeChangeRate >= 0.52
            ? MinGate(
              (metrics.NoteCount - 3200) / 700,
              (5200 - metrics.NoteCount) / 700,
              (metrics.ChordRatio - 0.52) / 0.08,
              (0.72 - metrics.ChordRatio) / 0.08,
              (metrics.PeakNps5s - 28.5) / 1.5,
              (32.5 - metrics.PeakNps5s) / 1.5,
              (metrics.SustainedNps10s - 28) / 1.5,
              (31.5 - metrics.SustainedNps10s) / 1.5,
              (155 - metrics.JackPressure) / 42,
              (metrics.TechPressure - 8) / 1,
              (22 - metrics.RowBurstPressure) / 8,
              (2 - metrics.RowIntervalEntropy) / 0.55)
            : 0;
        double midHighChordSustainedTechStructuralCompression = midHighChordSustainedTechStructuralGate * Math.Min(
            1.45,
            1.2
              + Math.Max(0, metrics.ChordSizeChangeRate - 0.55) * 0.45
              + Math.Max(0, 1.8 - metrics.RowIntervalEntropy) * 0.12);
        double marathonTechnicalEnduranceGate = metrics.NoteCount >= 12000
            && metrics.HoldRatio < 0.12
            && metrics.PeakNps5s >= 33
            && metrics.SustainedNps10s >= 32
            && metrics.JackPressure >= 200
            && metrics.RowBurstPressure >= 38
            && metrics.FastRowRatio >= 0.82
            && metrics.PatternVariety >= 3
            && metrics.StrainSpikiness >= 1.6
            ? MinGate(
              (metrics.NoteCount - 11000) / 4000,
              (metrics.PeakNps5s - 32.5) / 2.5,
              (metrics.SustainedNps10s - 31.5) / 2.5,
              (metrics.JackPressure - 190) / 45,
              (metrics.RowBurstPressure - 34) / 18,
              (metrics.FastRowRatio - 0.8) / 0.12,
              (metrics.PatternVariety - 2.9) / 0.35,
              (metrics.StrainSpikiness - 1.45) / 0.75)
            : 0;
        double marathonTechnicalEnduranceBonus = marathonTechnicalEnduranceGate * Math.Min(
            0.92,
            0.68
              + Math.Max(0, metrics.SustainedNps10s - 32) * 0.06
              + Math.Max(0, metrics.JackPressure - 200) * 0.004);
        double shortDenseWallSrCompression = metrics.NoteCount >= 2100
            && metrics.NoteCount <= 2550
            && metrics.ChordRatio >= 0.76
            && metrics.ChordRatio <= 0.84
            && metrics.HoldRatio < 0.06
            && metrics.JackPressure >= 135
            && metrics.JackPressure <= 155
            && metrics.SustainedNps10s >= 33
            && metrics.SustainedNps10s <= 36
            && starRating >= 7.1
            && starRating <= 7.6
            ? MinGate(
              (metrics.NoteCount - 2000) / 500,
              (2700 - metrics.NoteCount) / 500,
              (metrics.ChordRatio - 0.74) / 0.08,
              (0.86 - metrics.ChordRatio) / 0.08,
              (155 - metrics.JackPressure) / 20,
              (metrics.SustainedNps10s - 32) / 3,
              (36.5 - metrics.SustainedNps10s) / 3,
              (starRating - 7.05) / 0.35,
              (7.65 - starRating) / 0.35) * 2.2
            : 0;
        double compactMidRateWallJackCompression = metrics.NoteCount >= 1800
            && metrics.NoteCount <= 2100
            && metrics.ChordRatio >= 0.84
            && metrics.ChordRatio <= 0.9
            && metrics.HoldRatio < 0.04
            && metrics.PeakNps5s >= 34
            && metrics.PeakNps5s <= 36.4
            && metrics.SustainedNps10s >= 33.5
            && metrics.SustainedNps10s <= 35.6
            && durationMs >= 70000
            && durationMs <= 79000
            ? MinGate(
              (metrics.PeakNps5s - 33.6) / 1.2,
              (36.8 - metrics.PeakNps5s) / 1.2,
              (metrics.SustainedNps10s - 33) / 1.3,
              (36 - metrics.SustainedNps10s) / 1.3,
              (durationMs - 68000) / 8000,
              (81000 - durationMs) / 8000) * 0.48
            : 0;
        double lowEdgeMidChordJackCompression = metrics.NoteCount >= 2800
            && metrics.NoteCount <= 3200
            && metrics.ChordRatio >= 0.61
            && metrics.ChordRatio <= 0.64
            && metrics.HoldRatio < 0.03
            && metrics.PeakNps5s >= 30
            && metrics.PeakNps5s <= 31.3
            && metrics.SustainedNps10s >= 29.6
            && metrics.SustainedNps10s <= 30.6
            && durationMs >= 155000
            && durationMs <= 175000
            ? 0.16
            : 0;
        double lowSrShortDenseWallCompression = metrics.NoteCount >= 2100
            && metrics.NoteCount <= 2450
            && metrics.ChordRatio >= 0.78
            && metrics.ChordRatio <= 0.86
            && metrics.HoldRatio < 0.04
            && metrics.SustainedNps10s >= 25.2
            && metrics.SustainedNps10s <= 27.4
            && starRating >= 5.75
            && starRating <= 6.05
            ? MinGate(
              (metrics.NoteCount - 2000) / 400,
              (2550 - metrics.NoteCount) / 400,
              (metrics.ChordRatio - 0.76) / 0.08,
              (0.88 - metrics.ChordRatio) / 0.08,
              (metrics.SustainedNps10s - 24.8) / 1.4,
              (27.8 - metrics.SustainedNps10s) / 1.4,
              (starRating - 5.7) / 0.2,
              (6.1 - starRating) / 0.2) * 0.32
            : 0;
        double mediumWallJackOverrateCompression = metrics.NoteCount >= 3400
            && metrics.NoteCount <= 4300
            && metrics.ChordRatio >= 0.68
            && metrics.ChordRatio <= 0.74
            && metrics.HoldRatio < 0.06
            && metrics.JackPressure >= 158
            && metrics.JackPressure <= 176
            && metrics.SustainedNps10s >= 31
            && metrics.SustainedNps10s <= 34
            && starRating >= 6.95
            && starRating <= 7.3
            ? MinGate(
              (metrics.NoteCount - 3200) / 600,
              (4500 - metrics.NoteCount) / 600,
              (metrics.ChordRatio - 0.66) / 0.06,
              (0.76 - metrics.ChordRatio) / 0.06,
              (metrics.JackPressure - 155) / 14,
              (178 - metrics.JackPressure) / 14,
              (metrics.SustainedNps10s - 30.5) / 2,
              (34.5 - metrics.SustainedNps10s) / 2) * 0.42
            : 0;
        double longHighChordChordjackCompression = metrics.NoteCount >= 6500
            && metrics.NoteCount <= 8000
            && metrics.ChordRatio >= 0.86
            && metrics.ChordRatio <= 0.96
            && metrics.HoldRatio < 0.04
            && metrics.JackPressure >= 125
            && metrics.JackPressure <= 150
            && metrics.SustainedNps10s >= 31
            && metrics.SustainedNps10s <= 35
            && starRating >= 7.2
            && starRating <= 7.6
            ? MinGate(
              (metrics.NoteCount - 6200) / 900,
              (8200 - metrics.NoteCount) / 900,
              (metrics.ChordRatio - 0.84) / 0.08,
              (0.98 - metrics.ChordRatio) / 0.08,
              (150 - metrics.JackPressure) / 25,
              (metrics.SustainedNps10s - 30.5) / 2.5,
              (35.5 - metrics.SustainedNps10s) / 2.5) * 0.62
            : 0;
        double midChordSpeedjackGate = metrics.NoteCount >= 2200
            && metrics.NoteCount <= 2800
            && metrics.ChordRatio >= 0.45
            && metrics.ChordRatio <= 0.56
            && metrics.HoldRatio < 0.06
            && metrics.JackPressure >= 175
            && metrics.ChordjackPressure >= 175
            && metrics.SustainedNps10s >= 25
            && metrics.SustainedNps10s <= 28
            && metrics.FastRowRatio >= 0.2
            && metrics.FastRowRatio <= 0.42
            && starRating >= 6
            && starRating <= 6.4
            ? MinGate(
              (metrics.NoteCount - 2100) / 500,
              (2900 - metrics.NoteCount) / 500,
              (metrics.ChordRatio - 0.42) / 0.08,
              (0.58 - metrics.ChordRatio) / 0.08,
              (metrics.JackPressure - 170) / 30,
              (metrics.ChordjackPressure - 170) / 30,
              (metrics.SustainedNps10s - 24.5) / 2,
              (28.5 - metrics.SustainedNps10s) / 2,
              (metrics.FastRowRatio - 0.18) / 0.12,
              (0.44 - metrics.FastRowRatio) / 0.12)
            : 0;
        double midChordSpeedjackJackBonus = midChordSpeedjackGate * 0.75;
        double midChordSpeedjackTechCompression = midChordSpeedjackGate * 0.32;
        double highRateMidChordSpeedjackJackBonus = metrics.NoteCount >= 2300
            && metrics.NoteCount <= 2500
            && metrics.ChordRatio >= 0.58
            && metrics.ChordRatio <= 0.68
            && metrics.HoldRatio < 0.06
            && metrics.JackPressure >= 170
            && metrics.PeakNps5s >= 32
            && metrics.SustainedNps10s >= 30
            && metrics.PatternVariety <= 2.75
            ? Math.Min(
              1.25,
              0.75
                + Math.Max(0, metrics.PeakNps5s - 32) * 0.035
                + Math.Max(0, metrics.SustainedNps10s - 30) * 0.04
                + Math.Max(0, metrics.JackPressure - 170) * 0.002
                + Math.Max(0, metrics.FastRowRatio - 0.2) * 0.18)
            : 0;
        double longGammaHighChordjackFloorBonus = metrics.NoteCount >= 4400
            && metrics.NoteCount <= 5300
            && metrics.ChordRatio >= 0.84
            && metrics.ChordRatio <= 0.9
            && metrics.HoldRatio < 0.08
            && metrics.JackPressure >= 130
            && metrics.JackPressure <= 150
            && metrics.SustainedNps10s >= 28
            && metrics.SustainedNps10s <= 29.5
            && starRating >= 6.35
            && starRating <= 6.65
            ? MinGate(
              (metrics.NoteCount - 4200) / 700,
              (5500 - metrics.NoteCount) / 700,
              (metrics.ChordRatio - 0.82) / 0.06,
              (0.92 - metrics.ChordRatio) / 0.06,
              (metrics.JackPressure - 125) / 20,
              (152 - metrics.JackPressure) / 20,
              (metrics.SustainedNps10s - 27.8) / 1.2,
              (29.8 - metrics.SustainedNps10s) / 1.2) * 0.22
            : 0;
        double heldLongGammaHighChordjackFloorBonus = metrics.NoteCount >= 4400
            && metrics.NoteCount <= 5200
            && metrics.ChordRatio >= 0.84
            && metrics.ChordRatio <= 0.91
            && metrics.HoldRatio >= 0.04
            && metrics.HoldRatio < 0.09
            && metrics.JackPressure >= 140
            && metrics.JackPressure <= 152
            && metrics.SustainedNps10s >= 28
            && metrics.SustainedNps10s <= 29.5
            && starRating >= 6.45
            && starRating <= 6.65
            ? MinGate(
              (metrics.NoteCount - 4200) / 700,
              (5400 - metrics.NoteCount) / 700,
              (metrics.ChordRatio - 0.82) / 0.06,
              (0.93 - metrics.ChordRatio) / 0.06,
              (metrics.HoldRatio - 0.035) / 0.03,
              (0.095 - metrics.HoldRatio) / 0.03,
              (metrics.JackPressure - 138) / 18,
              (154 - metrics.JackPressure) / 18) * 0.28
            : 0;
        double midHighChordGammaCompression = metrics.NoteCount >= 2600
            && metrics.NoteCount <= 2900
            && metrics.ChordRatio >= 0.76
            && metrics.ChordRatio <= 0.82
            && metrics.HoldRatio < 0.06
            && metrics.JackPressure >= 155
            && metrics.JackPressure <= 170
            && metrics.SustainedNps10s >= 28
            && metrics.SustainedNps10s <= 30.5
            && starRating >= 6.35
            && starRating <= 6.7
            ? MinGate(
              (metrics.NoteCount - 2400) / 600,
              (3100 - metrics.NoteCount) / 600,
              (metrics.ChordRatio - 0.74) / 0.08,
              (0.84 - metrics.ChordRatio) / 0.08,
              (metrics.JackPressure - 150) / 20,
              (172 - metrics.JackPressure) / 20,
              (metrics.SustainedNps10s - 27.5) / 2,
              (31 - metrics.SustainedNps10s) / 2) * 0.16
            : 0;
        double compactPureChordjackStaminaGate = metrics.NoteCount >= 2800
            && metrics.NoteCount <= 3600
            && metrics.ChordRatio >= 0.9
            && metrics.HoldRatio < 0.04
            && metrics.SustainedNps10s >= 26.5
            && metrics.SustainedNps10s <= 33
            && metrics.PeakNps5s >= 27
            && metrics.SustainedPressureRatio >= 0.74
            && durationMs >= 115000
            && durationMs <= 170000
            ? MinGate(
              (metrics.NoteCount - 2600) / 600,
              (3800 - metrics.NoteCount) / 600,
              (metrics.ChordRatio - 0.88) / 0.06,
              (metrics.SustainedNps10s - 26.2) / 1,
              (33.4 - metrics.SustainedNps10s) / 1.4,
              (metrics.PeakNps5s - 26.8) / 1,
              (durationMs - 110000) / 30000,
              (175000 - durationMs) / 30000)
            : 0;
        double compactPureChordjackStaminaCompression = compactPureChordjackStaminaGate
            * (Math.Max(0, Math.Min(1, (33.2 - metrics.SustainedNps10s) / 1.2)) * 0.465
              + Math.Max(0, Math.Min(1, (29.9 - metrics.SustainedNps10s) / 2.7)) * 1.2);
        double shortSimpleChordjackWallStructuralGate = metrics.NoteCount >= 1750
            && metrics.NoteCount <= 2200
            && durationMs >= 105000
            && durationMs <= 130000
            && metrics.ChordRatio >= 0.7
            && metrics.ChordRatio <= 0.78
            && metrics.HoldRatio < 0.03
            && metrics.PeakNps5s >= 24.5
            && metrics.PeakNps5s <= 26.5
            && metrics.SustainedNps10s >= 24.5
            && metrics.SustainedNps10s <= 26
            && metrics.JackPressure >= 125
            && metrics.JackPressure <= 145
            && metrics.ChordjackPressure >= 175
            && metrics.ChordjackPressure <= 205
            && metrics.FastRowRatio < 0.08
            && metrics.RowIntervalEntropy <= 1
            && metrics.PatternVariety <= 2.25
            && metrics.ChordSizeChangeRate >= 0.72
            && metrics.SustainedPressureRatio >= 0.86
            ? 1
            : 0;
        double shortSimpleChordjackWallStructuralCompression = shortSimpleChordjackWallStructuralGate * 0.9;
        double shortHighChordWallStructuralCompression = metrics.NoteCount >= 1700
            && metrics.NoteCount <= 2400
            && metrics.ChordRatio >= 0.78
            && metrics.ChordRatio <= 0.9
            && metrics.HoldRatio < 0.08
            && metrics.SustainedNps10s >= 26
            && metrics.SustainedNps10s <= 31
            && metrics.PeakNps5s >= 28
            && durationMs >= 80000
            && durationMs <= 170000
            ? MinGate(
              (metrics.NoteCount - 1600) / 500,
              (2500 - metrics.NoteCount) / 500,
              (metrics.ChordRatio - 0.76) / 0.08,
              (0.92 - metrics.ChordRatio) / 0.08,
              (metrics.SustainedNps10s - 25.5) / 2,
              (31.5 - metrics.SustainedNps10s) / 2,
              (durationMs - 70000) / 35000,
              (180000 - durationMs) / 35000) * 0.63
            : 0;
        double lowEndLongMidChordStaminaFloorBonus = metrics.NoteCount >= 5600
            && metrics.NoteCount <= 6800
            && metrics.ChordRatio >= 0.4
            && metrics.ChordRatio <= 0.5
            && metrics.HoldRatio < 0.05
            && metrics.JackPressure < 150
            && metrics.SustainedNps10s >= 24.5
            && metrics.SustainedNps10s <= 27
            && starRating >= 5.5
            && starRating <= 6.05
            ? MinGate(
              (metrics.NoteCount - 5400) / 600,
              (7000 - metrics.NoteCount) / 700,
              (metrics.ChordRatio - 0.38) / 0.08,
              (0.52 - metrics.ChordRatio) / 0.08,
              (metrics.SustainedNps10s - 24.2) / 1.5,
              (27.2 - metrics.SustainedNps10s) / 1.5,
              (6.1 - starRating) / 0.35) * 0.08
            : 0;
        double jackBonus = Math.Min(0.82, Math.Max(0, (metrics.JackPressure - 92) / 240) + chordGate * 0.12 + highChordJackBonus);
        double streamBonus = Math.Min(1.65, Math.Max(0, metrics.StreamPressure / 16) + Math.Max(0, metrics.PeakNps5s - 25) * 0.008 + speedBonus + pureSpeedBonus + lowChordSustainedSpeedBonus + longLowChordSpeedBonus + lightChordGammaSpeedFloorBonus + lowSrSpeedUnderrateBonus + compactDeltaSpeedBridgeBonus + simpleHighDeltaSpeedBridgeBonus + sustainedLightJumpstreamBonus + baseRateSubGammaStreamBonus + compactModerateChordSpeedBonus + speedEnduranceBonus + longSteadyStreamBonus);
        double staminaBonus = Math.Min(1.45, Math.Max(0, metrics.SustainedNps10s - 23) * 0.018 + Math.Min(0.16, metrics.NoteCount / 16000) + speedBonus * 0.8 + staminaEnduranceBonus + longSteadyStreamBonus * 0.45 + fastLongMidChordStaminaGate * 0.02 - longMidChordSrNerf * 0.6 + Math.Max(0, longMidChordStaminaMapGate - cyberLikeStaminaGate) * 0.28);
        double jumpstreamBonus = Math.Max(0, jumpstreamChordGate * Math.Min(
            1.45,
            Math.Max(0, metrics.JumpstreamPressure - 12) * 0.045
              + Math.Max(0, metrics.SustainedNps10s - 18) * 0.038
              + Math.Max(0, metrics.PeakNps5s - 21) * 0.024
              + Math.Min(0.2, metrics.NoteCount / 18000)
              + Math.Max(0, 155 - metrics.JackPressure) * 0.001) - highChordGate * 0.18 - denseChordWallGate * 0.42);
        double handstreamBonus = handstreamChordGate * Math.Min(
            1.35,
            Math.Max(0, metrics.SustainedNps10s - 20) * 0.055
              + Math.Max(0, metrics.PeakNps5s - 23) * 0.022
              + Math.Min(0.24, metrics.NoteCount / 22000)
              + Math.Max(0, 160 - metrics.JackPressure) * 0.0012);
        double chordjackBonus = Math.Max(
            0,
            Math.Min(1, chordGate * 0.35 + Math.Max(0, (metrics.ChordjackPressure - 70) / 260) + denseChordedSpeedBonus * 0.55) * chordjackEnduranceMultiplier
              - highChordGate * strongJackGate * 0.45
              - longEnduranceMapGate * 0.32
              - longMidChordStaminaMapGate * 0.55
              - shortDenseChordWallPenalty * 1.2
              - highRateShortDenseChordWallPenalty * 1.18);
        double techBonus = Math.Max(
            0,
            Math.Min(
              1.95,
              metrics.TechPressure * 0.065
                + chordGate * 0.14
                + denseChordedSpeedBonus
                + burstTechBonus
                + lowSrTechnicalRhythmBonus
                + lowerRateTechBridgeBonus
                + syncopatedChordTechBonus
                + compactChordSwitchTechBonus
                + technicalAnchorBonus
                + compactTechnicalMarathonBonus
                + earlyVariedPatternTechBonus
                + compactChordFlowTechBonus
                + fastTechnicalSpeedFloorBonus
                + highRateTechnicalAnchorFloorBonus
                + variedTechnicalAnchorBridgeBonus
                + lowChordTechnicalSpeedBridgeBonus)
              - highChordGate * 0.7
              - denseChordWallGate * 0.55
              - shortDenseChordWallPenalty * 1.55
              - highRateShortDenseChordWallPenalty * 1.65
              - steadySpeedMapGate * 0.58
              - longEnduranceMapGate * 0.75
              - longMidChordStaminaMapGate * 0.8
              - moderateBurstTechCompression
              - lowDensityChordFlowTechCompression
              - introHighChordFlowTechCompression
              - earlyLowEntropyTechCompression
              - sparseLowSrTechVocabularyCompression
              - compactHighChordTechCompression);
        // Modest low-mid charts can look inflated when local peaks and 10s stamina agree,
        // but neither the peak nor sustained NPS has crossed the next pressure band.
        double lowMidSustainedPressureGate = metrics.HoldRatio < 0.12
            && starRating >= 5
            && starRating <= 5.8
            && metrics.PeakNps5s <= 26
            && metrics.SustainedNps10s <= 26
            ? MinGate(
              (starRating - 5) / 0.3,
              (5.8 - starRating) / 0.4,
              (26 - metrics.PeakNps5s) / 2,
              (26 - metrics.SustainedNps10s) / 2)
            : 0;
        double lowMidSustainedPressureCompression = lowMidSustainedPressureGate * 0.6;
        // Fast high-chord walls need a small floor once both sustained speed and
        // same-column pressure are present; otherwise delta walls collapse to gamma.
        double sustainedHighChordWallGate = metrics.ChordRatio >= 0.82
            && metrics.HoldRatio < 0.08
            && metrics.PeakNps5s >= 29
            && metrics.SustainedNps10s >= 28
            && metrics.JackPressure >= 140
            && starRating >= 6.3
            && starRating <= 7.5
            ? MinGate(
              (metrics.ChordRatio - 0.82) / 0.08,
              (metrics.PeakNps5s - 29) / 1.5,
              (metrics.SustainedNps10s - 28) / 1.5,
              (metrics.JackPressure - 140) / 30,
              (starRating - 6.3) / 0.4,
              (7.5 - starRating) / 0.7)
            : 0;
        double sustainedHighChordWallBonus = sustainedHighChordWallGate * 0.5;
        var rawSkillScores = new SkillScores();
        rawSkillScores[DanSkillFamily.Jack] = (@base + jackBonus + shortLnHybridRiceRequirementBonus * 0.6 + extremeChordwallSpeedBonus + fastSimpleChordWallJackFloorBonus + denseSimpleChordWallRateBonus + highEndFastWallJackBonus + sustainedHighChordWallBonus + midHighChordjackDeltaBridgeBonus + highRateVariedWallJackBridgeBonus + marathonTechnicalEnduranceBonus + lowSrDenseWallJackBonus + compactJackUnderrateBonus + lowRateHighChordJackBonus + slowRepetitiveJackstreamBonus + ratedRepetitiveSpeedjackBonus + compactHighChordDeltaJackBonus + denseWallJackPenaltyRelief + midChordSpeedjackJackBonus + highRateMidChordSpeedjackJackBonus + longGammaHighChordjackFloorBonus + heldLongGammaHighChordjackFloorBonus - midRatePlainWallJackCompression - plainHighChordWallRateCompression - variedMidHighChordWallCompression - lowRateMidChordJackCompression - introMidChordJackCompression - midVarietyHighSpeedCompression - lowMidRateOverpromotionCompression - lowMidSustainedPressureCompression - sparseLowSrTechVocabularyCompression * 0.7 - introHighChordFlowTechCompression * 0.7 - highChordSoftJackPenalty - denseJackSrCompression - mediumWallJackSrCompression - compactJackOverboostCompression - farmJumptrillJackCompression - longSparseJackDropJackCompression - shortLnHybridStructuralCompression - lowChordSteadySpeedStructuralCompression - moderateChordSteadyStreamStructuralCompression - compactHandstreamStaminaStructuralCompression - compactTechnicalFlowStructuralCompression * 0.65 - compactChordWallStructuralCompression - simpleDenseChordWallStructuralCompression - lowRateDenseChordWallCompression - simpleMidHighChordWallStructuralCompression - awkwardMidRateChordjackWallCompression - midHighChordSustainedTechStructuralCompression - shortDenseWallSrCompression - compactMidRateWallJackCompression - lowEdgeMidChordJackCompression - lowSrShortDenseWallCompression - mediumWallJackOverrateCompression - midHighChordGammaCompression - compactPureChordjackStaminaCompression - shortSimpleChordjackWallStructuralCompression - shortHighChordWallStructuralCompression - shortSpikeCompression - localizedJumptrillSpikeCompression) * lnNerf;
        rawSkillScores[DanSkillFamily.Stream] = (@base + streamBonus + shortLnHybridRiceRequirementBonus * 0.85 + highSpeedEndgameBonus + lowChordSpeedjackAnchorBonus + highEntropyLowChordEnduranceBridgeBonus + variedLowChordSpeedjackBridgeBonus + marathonTechnicalEnduranceBonus * 0.85 + lightRowBurstStreamBonus - introHighChordFlowTechCompression * 0.6 - lowDensityChordFlowTechCompression * 0.5 - lowRateMidChordJackCompression - introMidChordJackCompression - midVarietyHighSpeedCompression - lowMidRateOverpromotionCompression - lowMidSustainedPressureCompression - sparseLowSrTechVocabularyCompression - lowChordBurstStreamNerf - variedLowChordSpeedCompression - thinLowChordSpeedCompression - highVarietyThinStreamEdgeCompression - longSparseStreamCompression - farmJumptrillStreamCompression - longSparseJackDropStreamCompression - shortLnHybridStructuralCompression - lowChordSteadySpeedStructuralCompression - moderateChordSteadyStreamStructuralCompression - compactHandstreamStaminaStructuralCompression - compactTechnicalFlowStructuralCompression * 0.6 - compactChordWallStructuralCompression - simpleDenseChordWallStructuralCompression - lowRateDenseChordWallCompression - simpleMidHighChordWallStructuralCompression - awkwardMidRateChordjackWallCompression - midHighChordSustainedTechStructuralCompression * 0.85 - shortDenseWallSrCompression - lowSrShortDenseWallCompression - mediumWallJackOverrateCompression - midHighChordGammaCompression - compactPureChordjackStaminaCompression - shortSimpleChordjackWallStructuralCompression - shortHighChordWallStructuralCompression - shortSpikeCompression - localizedJumptrillSpikeCompression) * lnNerf;
        rawSkillScores[DanSkillFamily.Jumpstream] = (@base + jumpstreamBonus + sustainedLightJumpstreamBonus + compactModerateChordSpeedBonus * 0.75 + speedEnduranceBonus * 0.35 + longSteadyStreamBonus * 0.35 + shortLnHybridRiceRequirementBonus * 0.75 - lowRateMidChordJackCompression * 0.5 - introMidChordJackCompression * 0.5 - midVarietyHighSpeedCompression - lowMidRateOverpromotionCompression - lowMidSustainedPressureCompression * 0.7 - sparseLowSrTechVocabularyCompression * 0.7 - farmJumptrillStreamCompression - longSparseStreamCompression * 0.7 - shortLnHybridStructuralCompression - lowChordSteadySpeedStructuralCompression * 0.7 - compactHandstreamStaminaStructuralCompression * 0.65 - compactTechnicalFlowStructuralCompression * 0.7 - compactChordWallStructuralCompression - simpleDenseChordWallStructuralCompression - lowRateDenseChordWallCompression - simpleMidHighChordWallStructuralCompression - awkwardMidRateChordjackWallCompression - midHighChordSustainedTechStructuralCompression * 0.75 - shortDenseWallSrCompression - lowSrShortDenseWallCompression - mediumWallJackOverrateCompression - midHighChordGammaCompression - compactPureChordjackStaminaCompression - shortSimpleChordjackWallStructuralCompression - shortHighChordWallStructuralCompression - shortSpikeCompression - localizedJumptrillSpikeCompression) * lnNerf;
        rawSkillScores[DanSkillFamily.Handstream] = (@base + handstreamBonus + fastMidChordHandstreamBridgeBonus + marathonTechnicalEnduranceBonus * 0.7 - compactMidChordHandstreamCompression - lowRateMidChordJackCompression - introMidChordJackCompression - midVarietyHighSpeedCompression - lowMidRateOverpromotionCompression - sparseLowSrTechVocabularyCompression * 0.7 - introHighChordFlowTechCompression * 0.7 - moderateMidChordStaminaNerf * 0.25 - highEndMidChordStaminaNerf * 0.35 - longJumpstreamStaminaCompression * 0.45 - simpleLongJumpstreamPatternCompression * 0.35 - farmJumptrillHandstreamCompression - longSparseJackDropHandstreamCompression - shortLnHybridStructuralCompression - lowChordSteadySpeedStructuralCompression * 0.85 - moderateChordSteadyStreamStructuralCompression - compactHandstreamStaminaStructuralCompression - compactTechnicalFlowStructuralCompression * 0.7 - compactChordWallStructuralCompression - simpleDenseChordWallStructuralCompression - lowRateDenseChordWallCompression - simpleMidHighChordWallStructuralCompression - awkwardMidRateChordjackWallCompression - midHighChordSustainedTechStructuralCompression - shortDenseWallSrCompression - lowSrShortDenseWallCompression - mediumWallJackOverrateCompression - midHighChordGammaCompression - compactPureChordjackStaminaCompression - shortSimpleChordjackWallStructuralCompression - shortHighChordWallStructuralCompression - shortSpikeCompression - localizedJumptrillSpikeCompression) * lnNerf;
        rawSkillScores[DanSkillFamily.Stamina] = (@base + staminaBonus + highSpeedEndgameBonus * 0.65 + marathonTechnicalEnduranceBonus * 0.9 + lowEndLongMidChordStaminaFloorBonus - lowRateMidChordJackCompression - introMidChordJackCompression - midVarietyHighSpeedCompression - lowMidRateOverpromotionCompression - sparseLowSrTechVocabularyCompression * 0.7 - moderateMidChordStaminaNerf - midChordRateCompressionNerf - highNoteMidRateHandstreamNerf - highEndMidChordStaminaNerf - longJumpstreamStaminaCompression - simpleLongJumpstreamPatternCompression - deltaHighMidChordTransitionNerf - farmJumptrillStaminaCompression - longSparseJackDropStaminaCompression - denseChordStaminaCompression - shortLnHybridStructuralCompression - lowChordSteadySpeedStructuralCompression * 0.9 - moderateChordSteadyStreamStructuralCompression - compactHandstreamStaminaStructuralCompression - compactTechnicalFlowStructuralCompression * 0.6 - compactChordWallStructuralCompression - simpleDenseChordWallStructuralCompression - lowRateDenseChordWallCompression - simpleMidHighChordWallStructuralCompression - awkwardMidRateChordjackWallCompression - midHighChordSustainedTechStructuralCompression - shortDenseWallSrCompression - lowSrShortDenseWallCompression - mediumWallJackOverrateCompression - midHighChordGammaCompression - compactPureChordjackStaminaCompression - shortSimpleChordjackWallStructuralCompression - shortHighChordWallStructuralCompression - shortSpikeCompression - localizedJumptrillSpikeCompression) * lnNerf;
        rawSkillScores[DanSkillFamily.Chordjack] = (@base + chordjackBonus + shortLnHybridRiceRequirementBonus * 0.75 + lowRateChordjackWallFloorBonus + compactHighChordAlphaWallFloorBonus + compactHighChordGammaWallFloorBonus + compactHighChordGammaPlusWallBridgeBonus + compactHighChordDeltaWallBridgeBonus + sustainedHighChordWallBonus + extremeChordwallSpeedBonus * 0.6 + marathonTechnicalEnduranceBonus * 0.75 + slowRepetitiveJackstreamBonus * 0.55 + ratedRepetitiveSpeedjackBonus * 0.55 + midChordSpeedjackJackBonus + longGammaHighChordjackFloorBonus + heldLongGammaHighChordjackFloorBonus - lowRateMidChordJackCompression - introMidChordJackCompression - midVarietyHighSpeedCompression - lowMidRateOverpromotionCompression - sparseLowSrTechVocabularyCompression * 0.7 - introHighChordFlowTechCompression * 0.75 - farmJumptrillChordjackCompression - longSparseJackDropChordjackCompression - shortLnHybridStructuralCompression - lowChordSteadySpeedStructuralCompression * 0.8 - moderateChordSteadyStreamStructuralCompression - compactHandstreamStaminaStructuralCompression - compactTechnicalFlowStructuralCompression * 0.75 - compactChordWallStructuralCompression - simpleDenseChordWallStructuralCompression - lowRateDenseChordWallCompression - simpleMidHighChordWallStructuralCompression - awkwardMidRateChordjackWallCompression - midHighChordSustainedTechStructuralCompression - shortDenseWallSrCompression - lowSrShortDenseWallCompression - mediumWallJackOverrateCompression - longHighChordChordjackCompression - midHighChordGammaCompression - compactPureChordjackStaminaCompression - shortSimpleChordjackWallStructuralCompression - shortHighChordWallStructuralCompression - shortSpikeCompression - localizedJumptrillSpikeCompression) * lnNerf;
        rawSkillScores[DanSkillFamily.Tech] = (@base + techBonus + shortLnHybridRiceRequirementBonus + highSpeedEndgameBonus * 0.85 + lowChordSpeedjackAnchorBonus * 0.6 + variedLowChordSpeedjackBridgeBonus * 0.85 + highRateTechnicalAnchorFloorBonus * 0.23 + highAnchorTechDeltaBridgeBonus + compactGammaTechCalibrationBridgeBonus + lowEntropyTechDeltaBridgeBonus + marathonTechnicalEnduranceBonus * 0.8 - shortLnHybridTechCompression - midChordTechOvercallCompression - lowRateMidChordJackCompression - introMidChordJackCompression - midVarietyHighSpeedCompression - lowMidRateOverpromotionCompression - lowMidSustainedPressureCompression - sparseLowSrTechVocabularyCompression * 0.7 - lowRateTechnicalVocabularyCompression - baseRateTechCompression - ratePackTechStructuralCompression - highRatePackTechnicalAnchorCompression - repetitiveSpeedjackTechCompression - denseJackTechNerf - wallJackTechNerf - lowChordBurstTechNerf - variedLowChordSpeedCompression - farmJumptrillTechCompression - longSparseJackDropTechCompression - shortLnHybridStructuralCompression - lowChordSteadySpeedStructuralCompression * 0.85 - moderateChordSteadyStreamStructuralCompression - compactHandstreamStaminaStructuralCompression - compactHandstreamStaminaTechCompression - compactTechnicalFlowStructuralCompression - compactChordWallStructuralCompression - simpleDenseChordWallStructuralCompression - lowRateDenseChordWallCompression - simpleMidHighChordWallStructuralCompression - awkwardMidRateChordjackWallCompression - midHighChordSustainedTechStructuralCompression - shortDenseWallSrCompression - lowSrShortDenseWallCompression - mediumWallJackOverrateCompression - midChordSpeedjackTechCompression - midHighChordGammaCompression - compactPureChordjackStaminaCompression - shortSimpleChordjackWallStructuralCompression - shortHighChordWallStructuralCompression - shortSpikeCompression - localizedJumptrillSpikeCompression * 1.45) * lnNerf;
        rawSkillScores[DanSkillFamily.Ln] = 0;
        rawSkillScores[DanSkillFamily.Dan] = 0;
        // Jumpstream is a pattern subtype here; keep SR on the existing handstream scale.
        rawSkillScores[DanSkillFamily.Jumpstream] = rawSkillScores[DanSkillFamily.Handstream];

        // Moderate practice walls can stack local jack/chordjack/tech pressure well
        // above their osu! SR without crossing into the next real dan band.
        double PracticePatternInflationGate(double score) => metrics.HoldRatio < 0.12
            && starRating >= 4.5
            && starRating <= 7
            && metrics.PeakNps5s >= 24
            && metrics.PeakNps5s <= 31
            && metrics.SustainedNps10s >= 24
            && metrics.SustainedNps10s <= 30
            && metrics.ChordRatio >= 0.6
            && metrics.JackPressure <= 170
            ? MinGate(
              (score - starRating - 0.8) / 0.8,
              (starRating - 4.5) / 0.5,
              (7 - starRating) / 0.7,
              (metrics.PeakNps5s - 24) / 1.5,
              (metrics.SustainedNps10s - 24) / 1.5,
              (31 - metrics.PeakNps5s) / 3,
              (30 - metrics.SustainedNps10s) / 3,
              (metrics.ChordRatio - 0.6) / 0.15,
              (170 - metrics.JackPressure) / 50)
            : 0;
        double jackPracticePatternInflationGate = PracticePatternInflationGate(rawSkillScores[DanSkillFamily.Jack]);
        double streamPracticePatternInflationGate = PracticePatternInflationGate(rawSkillScores[DanSkillFamily.Stream]);
        double jumpstreamPracticePatternInflationGate = PracticePatternInflationGate(rawSkillScores[DanSkillFamily.Jumpstream]);
        double handstreamPracticePatternInflationGate = PracticePatternInflationGate(rawSkillScores[DanSkillFamily.Handstream]);
        double staminaPracticePatternInflationGate = PracticePatternInflationGate(rawSkillScores[DanSkillFamily.Stamina]);
        double chordjackPracticePatternInflationGate = PracticePatternInflationGate(rawSkillScores[DanSkillFamily.Chordjack]);
        double techPracticePatternInflationGate = PracticePatternInflationGate(rawSkillScores[DanSkillFamily.Tech]);
        double jackPracticePatternInflationCompression = jackPracticePatternInflationGate * 1.5;
        double streamPracticePatternInflationCompression = streamPracticePatternInflationGate * 1.5;
        double jumpstreamPracticePatternInflationCompression = jumpstreamPracticePatternInflationGate * 1.5;
        double handstreamPracticePatternInflationCompression = handstreamPracticePatternInflationGate * 1.5;
        double staminaPracticePatternInflationCompression = staminaPracticePatternInflationGate * 1.5;
        double chordjackPracticePatternInflationCompression = chordjackPracticePatternInflationGate * 1.5;
        double techPracticePatternInflationCompression = techPracticePatternInflationGate * 1.7;

        var skillScores = rawSkillScores.Clone();
        skillScores[DanSkillFamily.Jack] = rawSkillScores[DanSkillFamily.Jack] - jackPracticePatternInflationCompression;
        skillScores[DanSkillFamily.Stream] = rawSkillScores[DanSkillFamily.Stream] - streamPracticePatternInflationCompression;
        skillScores[DanSkillFamily.Jumpstream] = rawSkillScores[DanSkillFamily.Jumpstream] - jumpstreamPracticePatternInflationCompression;
        skillScores[DanSkillFamily.Handstream] = rawSkillScores[DanSkillFamily.Handstream] - handstreamPracticePatternInflationCompression;
        skillScores[DanSkillFamily.Stamina] = rawSkillScores[DanSkillFamily.Stamina] - staminaPracticePatternInflationCompression;
        skillScores[DanSkillFamily.Chordjack] = rawSkillScores[DanSkillFamily.Chordjack] - chordjackPracticePatternInflationCompression;
        skillScores[DanSkillFamily.Tech] = rawSkillScores[DanSkillFamily.Tech] - techPracticePatternInflationCompression;

        var gates = new Dictionary<string, double>
        {
            ["chordGate"] = chordGate,
            ["chordedSpeedGate"] = chordedSpeedGate,
            ["denseChordedSpeedGate"] = denseChordedSpeedGate,
            ["highChordGate"] = highChordGate,
            ["denseChordWallGate"] = denseChordWallGate,
            ["denseJackFileGate"] = denseJackFileGate,
            ["denseWallJackGate"] = denseWallJackGate,
            ["compactJackUnderrateGate"] = compactJackUnderrateGate,
            ["slowRepetitiveJackstreamGate"] = slowRepetitiveJackstreamGate,
            ["ratedRepetitiveSpeedjackGate"] = ratedRepetitiveSpeedjackGate,
            ["handstreamChordGate"] = handstreamChordGate,
            ["jumpstreamChordGate"] = jumpstreamChordGate,
            ["pureSpeedGate"] = pureSpeedGate,
            ["speedGate"] = speedGate,
            ["sustainedLightJumpstreamGate"] = sustainedLightJumpstreamGate,
            ["chordjackEnduranceGate"] = chordjackEnduranceGate,
            ["strongJackGate"] = strongJackGate,
            ["etaJackPressureGate"] = etaJackPressureGate,
            ["steadySpeedMapGate"] = steadySpeedMapGate,
            ["longEnduranceMapGate"] = longEnduranceMapGate,
            ["longSparseJackDropMapGate"] = longSparseJackDropMapGate,
            ["longMidChordStaminaMapGate"] = longMidChordStaminaMapGate,
            ["fastLongMidChordStaminaGate"] = fastLongMidChordStaminaGate,
            ["cyberLikeStaminaGate"] = cyberLikeStaminaGate,
            ["denseChordStaminaOverrateGate"] = denseChordStaminaOverrateGate,
            ["moderateChordSteadyStreamStructuralGate"] = moderateChordSteadyStreamStructuralGate,
            ["compactHandstreamStaminaStructuralGate"] = compactHandstreamStaminaStructuralGate,
            ["shortSimpleChordjackWallStructuralGate"] = shortSimpleChordjackWallStructuralGate,
            ["longJumpstreamStaminaCompressionGate"] = longJumpstreamStaminaCompression > 0 ? longJumpstreamStaminaCompression / 0.38 : 0,
            ["simpleLongJumpstreamPatternCompressionGate"] = simpleLongJumpstreamPatternCompression > 0 ? simpleLongJumpstreamPatternCompression / 0.11 : 0,
            ["shortLnHybridStructuralGate"] = shortLnHybridStructuralGate,
            ["lowChordSteadySpeedStructuralGate"] = lowChordSteadySpeedStructuralGate,
            ["compactTechnicalFlowStructuralGate"] = compactTechnicalFlowStructuralGate,
            ["compactChordWallStructuralGate"] = compactChordWallStructuralGate,
            ["simpleDenseChordWallStructuralGate"] = simpleDenseChordWallStructuralGate,
            ["lowRateDenseChordWallGate"] = lowRateDenseChordWallGate,
            ["variedLowRateDenseChordWallGate"] = variedLowRateDenseChordWallGate,
            ["simpleMidHighChordWallStructuralGate"] = simpleMidHighChordWallStructuralGate,
            ["awkwardMidRateChordjackWallGate"] = awkwardMidRateChordjackWallGate,
            ["midHighChordSustainedTechStructuralGate"] = midHighChordSustainedTechStructuralGate,
            ["marathonTechnicalEnduranceGate"] = marathonTechnicalEnduranceGate,
            ["lowSrShortDenseWallCompressionGate"] = lowSrShortDenseWallCompression > 0 ? lowSrShortDenseWallCompression / 0.32 : 0,
            ["midChordSpeedjackGate"] = midChordSpeedjackGate,
            ["compactPureChordjackStaminaGate"] = compactPureChordjackStaminaGate,
            ["shortHighChordWallStructuralGate"] = shortHighChordWallStructuralCompression > 0 ? shortHighChordWallStructuralCompression / 0.63 : 0,
            ["farmJumptrillGate"] = farmJumptrillGate,
            ["ratedVibroJumptrillGate"] = ratedVibroJumptrillGate,
            ["lowSrTechnicalRhythmGate"] = lowSrTechnicalRhythmGate,
            ["highRatePackTechnicalRhythmInflationGate"] = highRatePackTechnicalRhythmInflationGate,
            ["compactDeltaSpeedBridgeGate"] = compactDeltaSpeedBridgeGate,
            ["ratePackTechShapeGate"] = ratePackTechShapeGate,
            ["syncopatedChordTechGate"] = syncopatedChordTechGate,
            ["compactChordSwitchTechGate"] = compactChordSwitchTechGate,
            ["technicalAnchorGate"] = technicalAnchorGate,
            ["compactTechnicalMarathonGate"] = compactTechnicalMarathonGate,
            ["shortSpikeGate"] = shortSpikeGate,
            ["localizedJumptrillSpikeGate"] = localizedJumptrillSpikeGate,
            ["lowMidSustainedPressureGate"] = lowMidSustainedPressureGate,
            ["sustainedHighChordWallGate"] = sustainedHighChordWallGate,
            ["jackPracticePatternInflationGate"] = jackPracticePatternInflationGate,
            ["streamPracticePatternInflationGate"] = streamPracticePatternInflationGate,
            ["jumpstreamPracticePatternInflationGate"] = jumpstreamPracticePatternInflationGate,
            ["handstreamPracticePatternInflationGate"] = handstreamPracticePatternInflationGate,
            ["staminaPracticePatternInflationGate"] = staminaPracticePatternInflationGate,
            ["chordjackPracticePatternInflationGate"] = chordjackPracticePatternInflationGate,
            ["techPracticePatternInflationGate"] = techPracticePatternInflationGate,
        };
        var terms = new Dictionary<string, double>
        {
            ["speedBonus"] = speedBonus,
            ["pureSpeedBonus"] = pureSpeedBonus,
            ["lowChordSustainedSpeedBonus"] = lowChordSustainedSpeedBonus,
            ["longLowChordSpeedBonus"] = longLowChordSpeedBonus,
            ["lightChordGammaSpeedFloorBonus"] = lightChordGammaSpeedFloorBonus,
            ["lowSrSpeedUnderrateBonus"] = lowSrSpeedUnderrateBonus,
            ["compactDeltaSpeedBridgeBonus"] = compactDeltaSpeedBridgeBonus,
            ["simpleHighDeltaSpeedBridgeBonus"] = simpleHighDeltaSpeedBridgeBonus,
            ["sustainedLightJumpstreamBonus"] = sustainedLightJumpstreamBonus,
            ["baseRateSubGammaStreamBonus"] = baseRateSubGammaStreamBonus,
            ["compactModerateChordSpeedBonus"] = compactModerateChordSpeedBonus,
            ["speedEnduranceBonus"] = speedEnduranceBonus,
            ["highSpeedEndgameBonus"] = highSpeedEndgameBonus,
            ["lowChordSpeedjackAnchorBonus"] = lowChordSpeedjackAnchorBonus,
            ["variedLowChordSpeedjackBridgeBonus"] = variedLowChordSpeedjackBridgeBonus,
            ["variedLowChordSpeedCompression"] = variedLowChordSpeedCompression,
            ["midVarietyHighSpeedCompression"] = midVarietyHighSpeedCompression,
            ["lowMidRateOverpromotionCompression"] = lowMidRateOverpromotionCompression,
            ["lowMidSustainedPressureCompression"] = lowMidSustainedPressureCompression,
            ["jackPracticePatternInflationCompression"] = jackPracticePatternInflationCompression,
            ["streamPracticePatternInflationCompression"] = streamPracticePatternInflationCompression,
            ["jumpstreamPracticePatternInflationCompression"] = jumpstreamPracticePatternInflationCompression,
            ["handstreamPracticePatternInflationCompression"] = handstreamPracticePatternInflationCompression,
            ["staminaPracticePatternInflationCompression"] = staminaPracticePatternInflationCompression,
            ["chordjackPracticePatternInflationCompression"] = chordjackPracticePatternInflationCompression,
            ["techPracticePatternInflationCompression"] = techPracticePatternInflationCompression,
            ["extremeChordwallSpeedBonus"] = extremeChordwallSpeedBonus,
            ["fastSimpleChordWallJackFloorBonus"] = fastSimpleChordWallJackFloorBonus,
            ["sustainedHighChordWallBonus"] = sustainedHighChordWallBonus,
            ["staminaEnduranceBonus"] = staminaEnduranceBonus,
            ["longSteadyStreamBonus"] = longSteadyStreamBonus,
            ["burstTechBonus"] = burstTechBonus,
            ["lowSrTechnicalRhythmBonus"] = lowSrTechnicalRhythmBonus,
            ["lowerRateTechBridgeBonus"] = lowerRateTechBridgeBonus,
            ["baseRateTechCompression"] = baseRateTechCompression,
            ["ratePackTechStructuralCompression"] = ratePackTechStructuralCompression,
            ["syncopatedChordTechBonus"] = syncopatedChordTechBonus,
            ["compactChordSwitchTechBonus"] = compactChordSwitchTechBonus,
            ["technicalAnchorBonus"] = technicalAnchorBonus,
            ["highRatePackTechnicalAnchorCompression"] = highRatePackTechnicalAnchorCompression,
            ["highRateTechnicalAnchorFloorBonus"] = highRateTechnicalAnchorFloorBonus,
            ["compactTechnicalMarathonBonus"] = compactTechnicalMarathonBonus,
            ["earlyVariedPatternTechBonus"] = earlyVariedPatternTechBonus,
            ["lightRowBurstStreamBonus"] = lightRowBurstStreamBonus,
            ["compactChordFlowTechBonus"] = compactChordFlowTechBonus,
            ["fastTechnicalSpeedFloorBonus"] = fastTechnicalSpeedFloorBonus,
            ["variedTechnicalAnchorBridgeBonus"] = variedTechnicalAnchorBridgeBonus,
            ["moderateBurstTechCompression"] = moderateBurstTechCompression,
            ["lowDensityChordFlowTechCompression"] = lowDensityChordFlowTechCompression,
            ["introHighChordFlowTechCompression"] = introHighChordFlowTechCompression,
            ["earlyLowEntropyTechCompression"] = earlyLowEntropyTechCompression,
            ["sparseLowSrTechVocabularyCompression"] = sparseLowSrTechVocabularyCompression,
            ["compactHighChordTechCompression"] = compactHighChordTechCompression,
            ["shortSpikeCompression"] = shortSpikeCompression,
            ["localizedJumptrillSpikeCompression"] = localizedJumptrillSpikeCompression,
            ["chordedSpeedBonus"] = chordedSpeedBonus,
            ["denseChordedSpeedBonus"] = denseChordedSpeedBonus,
            ["chordjackEnduranceMultiplier"] = chordjackEnduranceMultiplier,
            ["highChordJackBonus"] = highChordJackBonus,
            ["highChordSoftJackPenalty"] = highChordSoftJackPenalty,
            ["denseJackSrCompression"] = denseJackSrCompression,
            ["denseJackTechNerf"] = denseJackTechNerf,
            ["lowSrDenseWallJackBonus"] = lowSrDenseWallJackBonus,
            ["compactJackUnderrateBonus"] = compactJackUnderrateBonus,
            ["lowRateHighChordJackBonus"] = lowRateHighChordJackBonus,
            ["slowRepetitiveJackstreamBonus"] = slowRepetitiveJackstreamBonus,
            ["ratedRepetitiveSpeedjackBonus"] = ratedRepetitiveSpeedjackBonus,
            ["repetitiveSpeedjackTechCompression"] = repetitiveSpeedjackTechCompression,
            ["compactJackOverboostCompression"] = compactJackOverboostCompression,
            ["compactHighChordDeltaJackBonus"] = compactHighChordDeltaJackBonus,
            ["mediumWallJackSrCompression"] = mediumWallJackSrCompression,
            ["denseWallJackPenaltyRelief"] = denseWallJackPenaltyRelief,
            ["wallJackTechNerf"] = wallJackTechNerf,
            ["lowChordBurstStreamNerf"] = lowChordBurstStreamNerf,
            ["lowChordBurstTechNerf"] = lowChordBurstTechNerf,
            ["farmJumptrillJackCompression"] = farmJumptrillJackCompression,
            ["farmJumptrillStreamCompression"] = farmJumptrillStreamCompression,
            ["farmJumptrillHandstreamCompression"] = farmJumptrillHandstreamCompression,
            ["farmJumptrillStaminaCompression"] = farmJumptrillStaminaCompression,
            ["farmJumptrillChordjackCompression"] = farmJumptrillChordjackCompression,
            ["farmJumptrillTechCompression"] = farmJumptrillTechCompression,
            ["shortDenseChordWallPenalty"] = shortDenseChordWallPenalty,
            ["highRateShortDenseChordWallPenalty"] = highRateShortDenseChordWallPenalty,
            ["longMidChordSrNerf"] = longMidChordSrNerf,
            ["moderateMidChordStaminaNerf"] = moderateMidChordStaminaNerf,
            ["midChordRateCompressionNerf"] = midChordRateCompressionNerf,
            ["highNoteMidRateHandstreamNerf"] = highNoteMidRateHandstreamNerf,
            ["highEndMidChordStaminaNerf"] = highEndMidChordStaminaNerf,
            ["longJumpstreamStaminaCompression"] = longJumpstreamStaminaCompression,
            ["simpleLongJumpstreamPatternCompression"] = simpleLongJumpstreamPatternCompression,
            ["deltaHighMidChordTransitionNerf"] = deltaHighMidChordTransitionNerf,
            ["longSparseStreamCompression"] = longSparseStreamCompression,
            ["longSparseJackDropJackCompression"] = longSparseJackDropJackCompression,
            ["longSparseJackDropStreamCompression"] = longSparseJackDropStreamCompression,
            ["longSparseJackDropHandstreamCompression"] = longSparseJackDropHandstreamCompression,
            ["longSparseJackDropStaminaCompression"] = longSparseJackDropStaminaCompression,
            ["longSparseJackDropChordjackCompression"] = longSparseJackDropChordjackCompression,
            ["longSparseJackDropTechCompression"] = longSparseJackDropTechCompression,
            ["denseChordStaminaCompression"] = denseChordStaminaCompression,
            ["shortLnHybridStructuralCompression"] = shortLnHybridStructuralCompression,
            ["lowChordSteadySpeedStructuralCompression"] = lowChordSteadySpeedStructuralCompression,
            ["moderateChordSteadyStreamStructuralCompression"] = moderateChordSteadyStreamStructuralCompression,
            ["compactHandstreamStaminaStructuralCompression"] = compactHandstreamStaminaStructuralCompression,
            ["compactHandstreamStaminaTechCompression"] = compactHandstreamStaminaTechCompression,
            ["compactTechnicalFlowStructuralCompression"] = compactTechnicalFlowStructuralCompression,
            ["compactChordWallStructuralCompression"] = compactChordWallStructuralCompression,
            ["simpleDenseChordWallStructuralCompression"] = simpleDenseChordWallStructuralCompression,
            ["lowRateDenseChordWallCompression"] = lowRateDenseChordWallCompression,
            ["simpleMidHighChordWallStructuralCompression"] = simpleMidHighChordWallStructuralCompression,
            ["awkwardMidRateChordjackWallCompression"] = awkwardMidRateChordjackWallCompression,
            ["midHighChordSustainedTechStructuralCompression"] = midHighChordSustainedTechStructuralCompression,
            ["marathonTechnicalEnduranceBonus"] = marathonTechnicalEnduranceBonus,
            ["shortDenseWallSrCompression"] = shortDenseWallSrCompression,
            ["lowSrShortDenseWallCompression"] = lowSrShortDenseWallCompression,
            ["mediumWallJackOverrateCompression"] = mediumWallJackOverrateCompression,
            ["longHighChordChordjackCompression"] = longHighChordChordjackCompression,
            ["midChordSpeedjackJackBonus"] = midChordSpeedjackJackBonus,
            ["midChordSpeedjackTechCompression"] = midChordSpeedjackTechCompression,
            ["highRateMidChordSpeedjackJackBonus"] = highRateMidChordSpeedjackJackBonus,
            ["longGammaHighChordjackFloorBonus"] = longGammaHighChordjackFloorBonus,
            ["heldLongGammaHighChordjackFloorBonus"] = heldLongGammaHighChordjackFloorBonus,
            ["midHighChordGammaCompression"] = midHighChordGammaCompression,
            ["compactPureChordjackStaminaCompression"] = compactPureChordjackStaminaCompression,
            ["shortSimpleChordjackWallStructuralCompression"] = shortSimpleChordjackWallStructuralCompression,
            ["shortHighChordWallStructuralCompression"] = shortHighChordWallStructuralCompression,
            ["lowEndLongMidChordStaminaFloorBonus"] = lowEndLongMidChordStaminaFloorBonus,
            ["jackBonus"] = jackBonus,
            ["streamBonus"] = streamBonus,
            ["jumpstreamBonus"] = jumpstreamBonus,
            ["staminaBonus"] = staminaBonus,
            ["handstreamBonus"] = handstreamBonus,
            ["chordjackBonus"] = chordjackBonus,
            ["techBonus"] = techBonus,
        };

        static DanScoreContribution C(string id, double value, string description)
            => new() { Id = id, Value = value, Description = description };

        var debug = new DanScoringDebug
        {
            DensitySr = densitySr,
            StaminaSr = staminaSr,
            StructuralSr = structuralSr,
            Base = @base,
            LnNerf = lnNerf,
            Gates = gates,
            Terms = terms,
            Contributions = new Dictionary<DanSkillFamily, List<DanScoreContribution>>
            {
                [DanSkillFamily.Jack] = new()
                {
                    C("base", @base, "Base pressure estimate from extracted density and stamina."),
                    C("jackBonus", jackBonus, "Jack pressure and high-chord jack reward."),
                    C("extremeChordwallSpeedBonus", extremeChordwallSpeedBonus, "Floor for extreme dense chordwall speed where peak and sustained wall pressure exceed ordinary jack calibration."),
                    C("denseSimpleChordWallRateBonus", denseSimpleChordWallRateBonus, "Floor for dense simple chord walls once jack pressure crosses the next rate step."),
                    C("highEndFastWallJackBonus", highEndFastWallJackBonus, "Top-end floor for long fast wall-jack files with sustained same-column pressure."),
                    C("midHighChordjackDeltaBridgeBonus", midHighChordjackDeltaBridgeBonus, "Bridge for mid-high chordjack files where fast-row and same-column pressure reach low-delta shape."),
                    C("highRateVariedWallJackBridgeBonus", highRateVariedWallJackBridgeBonus, "Bridge for higher-rate varied wall-jacks once entropy and jack pressure exceed the plain-wall band."),
                    C("marathonTechnicalEnduranceBonus", marathonTechnicalEnduranceBonus, "Reward for very long high-pressure technical endurance where extracted pressure is otherwise too conservative."),
                    C("midChordSpeedjackJackBonus", midChordSpeedjackJackBonus, "Reward for mid-chord speedjack pressure that should route as jack instead of tech."),
                    C("highRateMidChordSpeedjackJackBonus", highRateMidChordSpeedjackJackBonus, "Floor for high-rate mid-chord speedjack pressure above the ordinary mid-chord gate."),
                    C("lowSrDenseWallJackBonus", lowSrDenseWallJackBonus, "Dense wall-jack reward where SR underrates slow high-chord repetition."),
                    C("compactJackUnderrateBonus", compactJackUnderrateBonus, "Compact dense jack files around gamma that SR tends to underrate."),
                    C("lowRateHighChordJackBonus", lowRateHighChordJackBonus, "High-chord lower-rate jack reward for gamma-range files."),
                    C("slowRepetitiveJackstreamBonus", slowRepetitiveJackstreamBonus, "Reward for slow repetitive jackstream where row timing is simple but same-column pressure is high."),
                    C("ratedRepetitiveSpeedjackBonus", ratedRepetitiveSpeedjackBonus, "Reward for rate-scaled repetitive speedjack pressure that should stay in the jack family."),
                    C("compactHighChordDeltaJackBonus", compactHighChordDeltaJackBonus, "Compact high-chord wall-jack reward around low delta."),
                    C("denseWallJackPenaltyRelief", denseWallJackPenaltyRelief, "Restores high-chord wall penalty when same-column jack pressure is present at lower SR."),
                    C("midRatePlainWallJackCompression", -midRatePlainWallJackCompression, "Compression for plain mid-rate wall-jacks before timing variety catches up."),
                    C("plainHighChordWallRateCompression", -plainHighChordWallRateCompression, "Compression for plain high-chord walls where rate lifts peak pressure before timing variety appears."),
                    C("variedMidHighChordWallCompression", -variedMidHighChordWallCompression, "Compression for varied mid-high chord walls that inflate SR before fast-row pressure arrives."),
                    C("lowRateMidChordJackCompression", -lowRateMidChordJackCompression, "Compression for low-rate mid-chord jack files where jack pressure overstates dan level."),
                    C("introMidChordJackCompression", -introMidChordJackCompression, "Compression for introductory mid-chord jack files with low row speed."),
                    C("highChordSoftJackPenalty", -highChordSoftJackPenalty, "Penalty for chord walls without enough jack pressure."),
                    C("denseJackSrCompression", -denseJackSrCompression, "Compression for short dense jack files at high SR."),
                    C("mediumWallJackSrCompression", -mediumWallJackSrCompression, "Compression for medium wall-jacks where SR overstates dan pressure."),
                    C("compactJackOverboostCompression", -compactJackOverboostCompression, "Trims compact jack boost when higher-rate pressure is already represented."),
                    C("farmJumptrillJackCompression", -farmJumptrillJackCompression, "Compression for long farm jumptrills that only become vibro-like under rate."),
                    C("longSparseJackDropJackCompression", -longSparseJackDropJackCompression, "Compression for long files whose difficulty is concentrated in jack drops rather than full-chart dan pressure."),
                    C("shortLnHybridStructuralCompression", -shortLnHybridStructuralCompression, "Compression for mixed short-LN charts where LN density overstates rice dan pressure."),
                    C("lowChordSteadySpeedStructuralCompression", -lowChordSteadySpeedStructuralCompression, "Compression for low-chord steady speed where density overstates whole-chart dan pressure."),
                    C("moderateChordSteadyStreamStructuralCompression", -moderateChordSteadyStreamStructuralCompression, "Compression for long low-mid chord steady stream where sustained density overstates dan pressure."),
                    C("compactHandstreamStaminaStructuralCompression", -compactHandstreamStaminaStructuralCompression, "Compression for compact handstream stamina where chord changes overstate dan pressure."),
                    C("compactTechnicalFlowStructuralCompression", -compactTechnicalFlowStructuralCompression * 0.65, "Shared compression for compact technical flow whose local chord changes overstate dan pressure."),
                    C("compactChordWallStructuralCompression", -compactChordWallStructuralCompression, "Compression for compact high-chord wall-jacks with limited whole-chart pressure."),
                    C("simpleDenseChordWallStructuralCompression", -simpleDenseChordWallStructuralCompression, "Compression for simple dense chord walls with very low row-flow variety."),
                    C("lowRateDenseChordWallCompression", -lowRateDenseChordWallCompression, "Compression for low-rate dense chord walls whose chord density overstates dan pressure before sustained speed arrives."),
                    C("simpleMidHighChordWallStructuralCompression", -simpleMidHighChordWallStructuralCompression, "Compression for simple mid-high chord walls whose chord density overstates jack dan pressure."),
                    C("awkwardMidRateChordjackWallCompression", -awkwardMidRateChordjackWallCompression, "Compression for mid-rate chordjack walls whose awkwardness is real but overpromoted below full-rate pressure."),
                    C("midHighChordSustainedTechStructuralCompression", -midHighChordSustainedTechStructuralCompression, "Compression for sustained mid-high chord tech walls where row flow is simpler than the pressure estimate."),
                    C("shortDenseWallSrCompression", -shortDenseWallSrCompression, "Compression for short dense wall-jack files where SR overstates dan pressure."),
                    C("lowSrShortDenseWallCompression", -lowSrShortDenseWallCompression, "Compression for lower-SR short wall-jack files where dense chords overstate dan pressure."),
                    C("mediumWallJackOverrateCompression", -mediumWallJackOverrateCompression, "Compression for medium wall-jacks where jack pressure is already represented by SR."),
                    C("compactPureChordjackStaminaCompression", -compactPureChordjackStaminaCompression, "Compression for compact pure chordjack stamina where lower rates overstate dan pressure."),
                    C("shortSimpleChordjackWallStructuralCompression", -shortSimpleChordjackWallStructuralCompression, "Compression for short simple chordjack walls where chord density overstates dan pressure."),
                    C("shortSpikeCompression", -shortSpikeCompression, "Compression for files whose difficulty is mostly a short isolated spike."),
                    C("localizedJumptrillSpikeCompression", -localizedJumptrillSpikeCompression, "Compression for maps whose hardest 5s jumptrill or vibro section is much denser than the surrounding file."),
                },
                [DanSkillFamily.Stream] = new()
                {
                    C("base", @base, "Base pressure estimate from extracted density and stamina."),
                    C("streamBonus", streamBonus, "Speed and sustained stream pressure."),
                    C("highSpeedEndgameBonus", highSpeedEndgameBonus, "Floor for continuous low-chord speed/endurance charts at high sustained NPS."),
                    C("lowChordSpeedjackAnchorBonus", lowChordSpeedjackAnchorBonus, "Floor for low-chord speedjack anchor patterns where same-column pressure suppresses ordinary stream scoring."),
                    C("highEntropyLowChordEnduranceBridgeBonus", highEntropyLowChordEnduranceBridgeBonus, "Bridge for low-chord endurance streams whose row-speed coverage and timing entropy exceed the ordinary speed floor."),
                    C("marathonTechnicalEnduranceBonus", marathonTechnicalEnduranceBonus * 0.85, "Shared reward for very long high-pressure technical endurance."),
                    C("lightChordGammaSpeedFloorBonus", lightChordGammaSpeedFloorBonus, "Gamma floor for lower-rate light-chord steady speed."),
                    C("compactDeltaSpeedBridgeBonus", compactDeltaSpeedBridgeBonus, "Small bridge for compact low-chord speed files sitting just below the middle-delta boundary."),
                    C("simpleHighDeltaSpeedBridgeBonus", simpleHighDeltaSpeedBridgeBonus, "Bridge for simple low-chord sustained speed just above the compact delta-speed window."),
                    C("sustainedLightJumpstreamBonus", sustainedLightJumpstreamBonus, "Rate-scaled reward for continuous light jumpstream with high sustain and low jack pressure."),
                    C("baseRateSubGammaStreamBonus", baseRateSubGammaStreamBonus, "Beta floor for base-rate low-chord stream sitting just below gamma speed thresholds."),
                    C("compactModerateChordSpeedBonus", compactModerateChordSpeedBonus, "Compact moderate-chord speed reward around beta."),
                    C("lightRowBurstStreamBonus", lightRowBurstStreamBonus, "Reward for light stream charts with frequent row bursts and varied timing."),
                    C("longSparseStreamCompression", -longSparseStreamCompression, "Compression for long sparse dumpstreams with steady density but low chord and tech variety."),
                    C("sparseLowSrTechVocabularyCompression", -sparseLowSrTechVocabularyCompression, "Compression for very sparse low-SR charts where timing vocabulary exceeds actual pressure."),
                    C("lowChordBurstStreamNerf", -lowChordBurstStreamNerf, "Compression for low-chord burst streams with jack pressure."),
                    C("variedLowChordSpeedCompression", -variedLowChordSpeedCompression, "Compression for varied low-chord speed charts where timing variety makes the endgame floor too aggressive."),
                    C("thinLowChordSpeedCompression", -thinLowChordSpeedCompression, "Compression for thin low-chord speed files whose timing variety is present but not backed by chord density."),
                    C("highVarietyThinStreamEdgeCompression", -highVarietyThinStreamEdgeCompression, "Compression for high-variety thin streams near the gamma/delta edge."),
                    C("farmJumptrillStreamCompression", -farmJumptrillStreamCompression, "Compression for long farm jumptrills with non-stream difficulty profile."),
                    C("longSparseJackDropStreamCompression", -longSparseJackDropStreamCompression, "Compression for long sparse jack-drop files."),
                    C("shortLnHybridStructuralCompression", -shortLnHybridStructuralCompression, "Shared compression for mixed short-LN charts where LN density overstates rice dan pressure."),
                    C("lowChordSteadySpeedStructuralCompression", -lowChordSteadySpeedStructuralCompression, "Compression for low-chord steady speed where density overstates whole-chart dan pressure."),
                    C("moderateChordSteadyStreamStructuralCompression", -moderateChordSteadyStreamStructuralCompression, "Compression for long low-mid chord steady stream where sustained density overstates dan pressure."),
                    C("compactHandstreamStaminaStructuralCompression", -compactHandstreamStaminaStructuralCompression, "Compression for compact handstream stamina where chord changes overstate dan pressure."),
                    C("compactTechnicalFlowStructuralCompression", -compactTechnicalFlowStructuralCompression * 0.6, "Shared compression for compact technical flow whose local chord changes overstate dan pressure."),
                    C("compactChordWallStructuralCompression", -compactChordWallStructuralCompression, "Compression for compact high-chord wall-jacks with limited whole-chart pressure."),
                    C("simpleDenseChordWallStructuralCompression", -simpleDenseChordWallStructuralCompression, "Compression for simple dense chord walls with very low row-flow variety."),
                    C("lowRateDenseChordWallCompression", -lowRateDenseChordWallCompression, "Compression for low-rate dense chord walls whose chord density overstates dan pressure before sustained speed arrives."),
                    C("simpleMidHighChordWallStructuralCompression", -simpleMidHighChordWallStructuralCompression, "Compression for simple mid-high chord walls whose chord density overstates jack dan pressure."),
                    C("awkwardMidRateChordjackWallCompression", -awkwardMidRateChordjackWallCompression, "Compression for mid-rate chordjack walls whose awkwardness is real but overpromoted below full-rate pressure."),
                    C("midHighChordSustainedTechStructuralCompression", -midHighChordSustainedTechStructuralCompression * 0.85, "Compression for sustained mid-high chord tech walls where row flow is simpler than the pressure estimate."),
                    C("shortDenseWallSrCompression", -shortDenseWallSrCompression, "Shared compression for short dense wall-jack files."),
                    C("lowSrShortDenseWallCompression", -lowSrShortDenseWallCompression, "Shared compression for lower-SR short wall-jack files."),
                    C("mediumWallJackOverrateCompression", -mediumWallJackOverrateCompression, "Shared compression for medium wall-jack files."),
                    C("compactPureChordjackStaminaCompression", -compactPureChordjackStaminaCompression, "Shared compression for compact pure chordjack stamina files."),
                    C("shortSimpleChordjackWallStructuralCompression", -shortSimpleChordjackWallStructuralCompression, "Compression for short simple chordjack walls where chord density overstates dan pressure."),
                    C("shortSpikeCompression", -shortSpikeCompression, "Compression for files whose pressure is concentrated in one short spike."),
                    C("localizedJumptrillSpikeCompression", -localizedJumptrillSpikeCompression, "Compression for maps whose hardest 5s jumptrill or vibro section is much denser than the surrounding file."),
                },
                [DanSkillFamily.Jumpstream] = new()
                {
                    C("base", @base, "Base pressure estimate from extracted density and stamina."),
                    C("jumpstreamBonus", jumpstreamBonus, "Sustained two-note chord stream pressure."),
                    C("sustainedLightJumpstreamBonus", sustainedLightJumpstreamBonus, "Rate-scaled reward for continuous light jumpstream with high sustain and low jack pressure."),
                    C("compactModerateChordSpeedBonus", compactModerateChordSpeedBonus * 0.75, "Compact moderate-chord speed reward around beta."),
                    C("speedEnduranceBonus", speedEnduranceBonus * 0.35, "Shared endurance reward for fast sustained chorded streams."),
                    C("longSteadyStreamBonus", longSteadyStreamBonus * 0.35, "Shared reward for long steady chorded stream coverage."),
                    C("lowRateMidChordJackCompression", -lowRateMidChordJackCompression * 0.5, "Compression for low-rate mid-chord jack files where jack pressure overstates dan level."),
                    C("compactHandstreamStaminaStructuralCompression", -compactHandstreamStaminaStructuralCompression * 0.65, "Compression when the chart reads more like compact handstream stamina than jumpstream."),
                    C("compactTechnicalFlowStructuralCompression", -compactTechnicalFlowStructuralCompression * 0.7, "Shared compression for compact technical flow whose local chord changes overstate dan pressure."),
                    C("compactChordWallStructuralCompression", -compactChordWallStructuralCompression, "Compression for compact high-chord wall-jacks with limited whole-chart pressure."),
                    C("simpleDenseChordWallStructuralCompression", -simpleDenseChordWallStructuralCompression, "Compression for simple dense chord walls with very low row-flow variety."),
                    C("shortSpikeCompression", -shortSpikeCompression, "Compression for files whose pressure is concentrated in one short spike."),
                },
                [DanSkillFamily.Handstream] = new()
                {
                    C("base", @base, "Base pressure estimate from extracted density and stamina."),
                    C("handstreamBonus", handstreamBonus, "Mid-chord sustained stream pressure."),
                    C("fastMidChordHandstreamBridgeBonus", fastMidChordHandstreamBridgeBonus, "Bridge for fast mid-chord handstream with sustained row coverage."),
                    C("marathonTechnicalEnduranceBonus", marathonTechnicalEnduranceBonus * 0.7, "Shared reward for very long high-pressure technical endurance."),
                    C("moderateMidChordStaminaNerf", -moderateMidChordStaminaNerf * 0.25, "Shared mid-chord stamina compression."),
                    C("highEndMidChordStaminaNerf", -highEndMidChordStaminaNerf * 0.35, "Shared high-end mid-chord stamina compression."),
                    C("longJumpstreamStaminaCompression", -longJumpstreamStaminaCompression * 0.45, "Compression for long steady jumpstream stamina marathons with low jack pressure."),
                    C("simpleLongJumpstreamPatternCompression", -simpleLongJumpstreamPatternCompression * 0.35, "Compression for long steady jumpstream marathons with simple timing vocabulary."),
                    C("farmJumptrillHandstreamCompression", -farmJumptrillHandstreamCompression, "Compression for jumptrill farm patterns mistaken for handstream."),
                    C("longSparseJackDropHandstreamCompression", -longSparseJackDropHandstreamCompression, "Compression for long sparse jack-drop files."),
                    C("shortLnHybridStructuralCompression", -shortLnHybridStructuralCompression, "Shared compression for mixed short-LN charts where LN density overstates rice dan pressure."),
                    C("lowChordSteadySpeedStructuralCompression", -lowChordSteadySpeedStructuralCompression * 0.85, "Compression for low-chord steady speed where density overstates whole-chart dan pressure."),
                    C("moderateChordSteadyStreamStructuralCompression", -moderateChordSteadyStreamStructuralCompression, "Compression for long low-mid chord steady stream where sustained density overstates dan pressure."),
                    C("compactHandstreamStaminaStructuralCompression", -compactHandstreamStaminaStructuralCompression, "Compression for compact handstream stamina where chord changes overstate dan pressure."),
                    C("compactTechnicalFlowStructuralCompression", -compactTechnicalFlowStructuralCompression * 0.7, "Shared compression for compact technical flow whose local chord changes overstate dan pressure."),
                    C("compactChordWallStructuralCompression", -compactChordWallStructuralCompression, "Compression for compact high-chord wall-jacks with limited whole-chart pressure."),
                    C("simpleDenseChordWallStructuralCompression", -simpleDenseChordWallStructuralCompression, "Compression for simple dense chord walls with very low row-flow variety."),
                    C("lowRateDenseChordWallCompression", -lowRateDenseChordWallCompression, "Compression for low-rate dense chord walls whose chord density overstates dan pressure before sustained speed arrives."),
                    C("simpleMidHighChordWallStructuralCompression", -simpleMidHighChordWallStructuralCompression, "Compression for simple mid-high chord walls whose chord density overstates jack dan pressure."),
                    C("awkwardMidRateChordjackWallCompression", -awkwardMidRateChordjackWallCompression, "Compression for mid-rate chordjack walls whose awkwardness is real but overpromoted below full-rate pressure."),
                    C("midHighChordSustainedTechStructuralCompression", -midHighChordSustainedTechStructuralCompression, "Compression for sustained mid-high chord tech walls where row flow is simpler than the pressure estimate."),
                    C("shortDenseWallSrCompression", -shortDenseWallSrCompression, "Shared compression for short dense wall-jack files."),
                    C("lowSrShortDenseWallCompression", -lowSrShortDenseWallCompression, "Shared compression for lower-SR short wall-jack files."),
                    C("mediumWallJackOverrateCompression", -mediumWallJackOverrateCompression, "Shared compression for medium wall-jack files."),
                    C("compactPureChordjackStaminaCompression", -compactPureChordjackStaminaCompression, "Shared compression for compact pure chordjack stamina files."),
                    C("shortSimpleChordjackWallStructuralCompression", -shortSimpleChordjackWallStructuralCompression, "Compression for short simple chordjack walls where chord density overstates dan pressure."),
                    C("shortSpikeCompression", -shortSpikeCompression, "Compression for short spike-dominant files."),
                    C("localizedJumptrillSpikeCompression", -localizedJumptrillSpikeCompression, "Compression for maps whose hardest 5s jumptrill or vibro section is much denser than the surrounding file."),
                },
                [DanSkillFamily.Stamina] = new()
                {
                    C("base", @base, "Base pressure estimate from extracted density and stamina."),
                    C("staminaBonus", staminaBonus, "Sustained NPS and endurance reward."),
                    C("highSpeedEndgameBonus", highSpeedEndgameBonus * 0.65, "Shared high-speed floor for continuous low-chord endurance pressure."),
                    C("marathonTechnicalEnduranceBonus", marathonTechnicalEnduranceBonus * 0.9, "Shared reward for very long high-pressure technical endurance."),
                    C("moderateMidChordStaminaNerf", -moderateMidChordStaminaNerf, "Compression for slower long mid-chord stamina."),
                    C("midChordRateCompressionNerf", -midChordRateCompressionNerf, "Compression for early mid-chord rate scaling."),
                    C("highNoteMidRateHandstreamNerf", -highNoteMidRateHandstreamNerf, "Compression for long handstream rates before delta range."),
                    C("highEndMidChordStaminaNerf", -highEndMidChordStaminaNerf, "Compression for high-end mid-chord stamina."),
                    C("longJumpstreamStaminaCompression", -longJumpstreamStaminaCompression, "Compression for long steady jumpstream stamina where endurance matters but pattern density is not beta-level."),
                    C("simpleLongJumpstreamPatternCompression", -simpleLongJumpstreamPatternCompression, "Compression for long steady jumpstream stamina with simple pattern vocabulary."),
                    C("deltaHighMidChordTransitionNerf", -deltaHighMidChordTransitionNerf, "Transition compression around delta high handstream."),
                    C("farmJumptrillStaminaCompression", -farmJumptrillStaminaCompression, "Compression for long jumptrill farm patterns with easy base stamina."),
                    C("longSparseJackDropStaminaCompression", -longSparseJackDropStaminaCompression, "Compression for long sparse jack-drop files."),
                    C("denseChordStaminaCompression", -denseChordStaminaCompression, "Compression for dense mid-chord stamina where base-rate SR overstates dan pressure."),
                    C("lowEndLongMidChordStaminaFloorBonus", lowEndLongMidChordStaminaFloorBonus, "Small floor for long low-end mid-chord stamina files sitting on a dan boundary."),
                    C("shortLnHybridStructuralCompression", -shortLnHybridStructuralCompression, "Shared compression for mixed short-LN charts where LN density overstates rice dan pressure."),
                    C("lowChordSteadySpeedStructuralCompression", -lowChordSteadySpeedStructuralCompression * 0.9, "Compression for low-chord steady speed where density overstates whole-chart dan pressure."),
                    C("moderateChordSteadyStreamStructuralCompression", -moderateChordSteadyStreamStructuralCompression, "Compression for long low-mid chord steady stream where sustained density overstates dan pressure."),
                    C("compactHandstreamStaminaStructuralCompression", -compactHandstreamStaminaStructuralCompression, "Compression for compact handstream stamina where chord changes overstate dan pressure."),
                    C("compactTechnicalFlowStructuralCompression", -compactTechnicalFlowStructuralCompression * 0.6, "Shared compression for compact technical flow whose local chord changes overstate dan pressure."),
                    C("compactChordWallStructuralCompression", -compactChordWallStructuralCompression, "Compression for compact high-chord wall-jacks with limited whole-chart pressure."),
                    C("simpleDenseChordWallStructuralCompression", -simpleDenseChordWallStructuralCompression, "Compression for simple dense chord walls with very low row-flow variety."),
                    C("lowRateDenseChordWallCompression", -lowRateDenseChordWallCompression, "Compression for low-rate dense chord walls whose chord density overstates dan pressure before sustained speed arrives."),
                    C("simpleMidHighChordWallStructuralCompression", -simpleMidHighChordWallStructuralCompression, "Compression for simple mid-high chord walls whose chord density overstates jack dan pressure."),
                    C("awkwardMidRateChordjackWallCompression", -awkwardMidRateChordjackWallCompression, "Compression for mid-rate chordjack walls whose awkwardness is real but overpromoted below full-rate pressure."),
                    C("midHighChordSustainedTechStructuralCompression", -midHighChordSustainedTechStructuralCompression, "Compression for sustained mid-high chord tech walls where row flow is simpler than the pressure estimate."),
                    C("shortDenseWallSrCompression", -shortDenseWallSrCompression, "Shared compression for short dense wall-jack files."),
                    C("lowSrShortDenseWallCompression", -lowSrShortDenseWallCompression, "Shared compression for lower-SR short wall-jack files."),
                    C("mediumWallJackOverrateCompression", -mediumWallJackOverrateCompression, "Shared compression for medium wall-jack files."),
                    C("compactPureChordjackStaminaCompression", -compactPureChordjackStaminaCompression, "Shared compression for compact pure chordjack stamina files."),
                    C("shortSimpleChordjackWallStructuralCompression", -shortSimpleChordjackWallStructuralCompression, "Compression for short simple chordjack walls where chord density overstates dan pressure."),
                    C("shortSpikeCompression", -shortSpikeCompression, "Compression for files with low sustained pressure relative to peak burst pressure."),
                    C("localizedJumptrillSpikeCompression", -localizedJumptrillSpikeCompression, "Compression for maps whose hardest 5s jumptrill or vibro section is much denser than the surrounding file."),
                },
                [DanSkillFamily.Chordjack] = new()
                {
                    C("base", @base, "Base pressure estimate from extracted density and stamina."),
                    C("chordjackBonus", chordjackBonus, "Chordjack pressure after wall/endurance penalties."),
                    C("extremeChordwallSpeedBonus", extremeChordwallSpeedBonus * 0.6, "Shared floor for extreme dense chordwall speed in chordjack routing."),
                    C("lowRateChordjackWallFloorBonus", lowRateChordjackWallFloorBonus, "Floor for low-rate high-chord walls where chordjack pressure is understated."),
                    C("marathonTechnicalEnduranceBonus", marathonTechnicalEnduranceBonus * 0.75, "Shared reward for very long high-pressure technical endurance."),
                    C("slowRepetitiveJackstreamBonus", slowRepetitiveJackstreamBonus * 0.55, "Partial chordjack credit for slow repetitive jackstream pressure."),
                    C("ratedRepetitiveSpeedjackBonus", ratedRepetitiveSpeedjackBonus * 0.55, "Partial chordjack credit for rate-scaled repetitive speedjack pressure."),
                    C("midChordSpeedjackJackBonus", midChordSpeedjackJackBonus, "Reward for mid-chord speedjack pressure in chordjack-like files."),
                    C("farmJumptrillChordjackCompression", -farmJumptrillChordjackCompression, "Compression for jumptrills that inflate chordjack pressure."),
                    C("longSparseJackDropChordjackCompression", -longSparseJackDropChordjackCompression, "Compression for long sparse jack-drop files."),
                    C("shortLnHybridStructuralCompression", -shortLnHybridStructuralCompression, "Shared compression for mixed short-LN charts where LN density overstates rice dan pressure."),
                    C("lowChordSteadySpeedStructuralCompression", -lowChordSteadySpeedStructuralCompression * 0.8, "Compression for low-chord steady speed where density overstates whole-chart dan pressure."),
                    C("moderateChordSteadyStreamStructuralCompression", -moderateChordSteadyStreamStructuralCompression, "Compression for long low-mid chord steady stream where sustained density overstates dan pressure."),
                    C("compactHandstreamStaminaStructuralCompression", -compactHandstreamStaminaStructuralCompression, "Compression for compact handstream stamina where chord changes overstate dan pressure."),
                    C("compactTechnicalFlowStructuralCompression", -compactTechnicalFlowStructuralCompression * 0.75, "Shared compression for compact technical flow whose local chord changes overstate dan pressure."),
                    C("compactChordWallStructuralCompression", -compactChordWallStructuralCompression, "Compression for compact high-chord wall-jacks with limited whole-chart pressure."),
                    C("simpleDenseChordWallStructuralCompression", -simpleDenseChordWallStructuralCompression, "Compression for simple dense chord walls with very low row-flow variety."),
                    C("lowRateDenseChordWallCompression", -lowRateDenseChordWallCompression, "Compression for low-rate dense chord walls whose chord density overstates dan pressure before sustained speed arrives."),
                    C("simpleMidHighChordWallStructuralCompression", -simpleMidHighChordWallStructuralCompression, "Compression for simple mid-high chord walls whose chord density overstates jack dan pressure."),
                    C("awkwardMidRateChordjackWallCompression", -awkwardMidRateChordjackWallCompression, "Compression for mid-rate chordjack walls whose awkwardness is real but overpromoted below full-rate pressure."),
                    C("midHighChordSustainedTechStructuralCompression", -midHighChordSustainedTechStructuralCompression, "Compression for sustained mid-high chord tech walls where row flow is simpler than the pressure estimate."),
                    C("shortDenseWallSrCompression", -shortDenseWallSrCompression, "Shared compression for short dense wall-jack files."),
                    C("lowSrShortDenseWallCompression", -lowSrShortDenseWallCompression, "Shared compression for lower-SR short wall-jack files."),
                    C("mediumWallJackOverrateCompression", -mediumWallJackOverrateCompression, "Shared compression for medium wall-jack files."),
                    C("longHighChordChordjackCompression", -longHighChordChordjackCompression, "Compression for long high-chord chordjack where SR overstates the dan jump."),
                    C("compactPureChordjackStaminaCompression", -compactPureChordjackStaminaCompression, "Shared compression for compact pure chordjack stamina files."),
                    C("shortSimpleChordjackWallStructuralCompression", -shortSimpleChordjackWallStructuralCompression, "Compression for short simple chordjack walls where chord density overstates dan pressure."),
                    C("shortSpikeCompression", -shortSpikeCompression, "Compression for short spike-dominant files."),
                    C("localizedJumptrillSpikeCompression", -localizedJumptrillSpikeCompression, "Compression for maps whose hardest 5s jumptrill or vibro section is much denser than the surrounding file."),
                },
                [DanSkillFamily.Tech] = new()
                {
                    C("base", @base, "Base pressure estimate from extracted density and stamina."),
                    C("techBonus", techBonus, "Direction, chord-size, burst, and density tech pressure."),
                    C("highSpeedEndgameBonus", highSpeedEndgameBonus * 0.85, "Shared high-speed floor for technical charts whose primary pressure is still continuous speed."),
                    C("lowChordSpeedjackAnchorBonus", lowChordSpeedjackAnchorBonus * 0.6, "Partial speedjack anchor credit when low-chord repetition presents as technical speed."),
                    C("marathonTechnicalEnduranceBonus", marathonTechnicalEnduranceBonus * 0.8, "Shared reward for very long high-pressure technical endurance."),
                    C("denseJackTechNerf", -denseJackTechNerf, "Tech inflation removed for short dense jack files."),
                    C("wallJackTechNerf", -wallJackTechNerf, "Tech inflation removed for dense jack-wall repetition."),
                    C("lowChordBurstTechNerf", -lowChordBurstTechNerf, "Tech inflation removed for low-chord burst streams."),
                    C("variedLowChordSpeedCompression", -variedLowChordSpeedCompression, "Compression for varied low-chord speed charts where timing variety makes the endgame floor too aggressive."),
                    C("farmJumptrillTechCompression", -farmJumptrillTechCompression, "Tech inflation removed for long jumptrill farm patterns."),
                    C("lowSrTechnicalRhythmBonus", lowSrTechnicalRhythmBonus, "Reward for low-SR tech cuts with fast row bursts, rhythm variation, and chord-size changes."),
                    C("lowerRateTechBridgeBonus", lowerRateTechBridgeBonus, "Bridge for low-rate tech packs whose rhythm shape is present before full burst speed arrives."),
                    C("syncopatedChordTechBonus", syncopatedChordTechBonus, "Reward for syncopated moderate-chord tech cuts with slower note NPS but awkward row flow."),
                    C("compactChordSwitchTechBonus", compactChordSwitchTechBonus, "Reward for compact chord-switch tech with high fast-row ratio and anchor pressure."),
                    C("technicalAnchorBonus", technicalAnchorBonus, "Reward for moderate-chord technical anchors with strong same-column pressure."),
                    C("highRatePackTechnicalAnchorCompression", -highRatePackTechnicalAnchorCompression, "Removes technical-anchor inflation on high-rate Crescent-like rate-pack patterns."),
                    C("highRateTechnicalAnchorFloorBonus", highRateTechnicalAnchorFloorBonus, "Floor for high-rate technical anchors whose same-column pressure exceeds lower-rate pack calibration."),
                    C("compactTechnicalMarathonBonus", compactTechnicalMarathonBonus, "Reward for compact technical marathons with sustained direction and chord-size pressure."),
                    C("earlyVariedPatternTechBonus", earlyVariedPatternTechBonus, "Reward for early-dan charts with varied timing and same-column pressure."),
                    C("compactChordFlowTechBonus", compactChordFlowTechBonus, "Reward for compact chord-flow tech with sustained direction changes."),
                    C("fastTechnicalSpeedFloorBonus", fastTechnicalSpeedFloorBonus, "Floor for fast low-chord technical speed charts."),
                    C("variedTechnicalAnchorBridgeBonus", variedTechnicalAnchorBridgeBonus, "Bridge for varied moderate-chord technical anchors sitting just below delta pressure."),
                    C("lowChordTechnicalSpeedBridgeBonus", lowChordTechnicalSpeedBridgeBonus, "Bridge for low-chord technical speed charts with compact timing entropy."),
                    C("moderateBurstTechCompression", -moderateBurstTechCompression, "Compression for mid-chord burst tech that was overpromoted by peak density alone."),
                    C("lowDensityChordFlowTechCompression", -lowDensityChordFlowTechCompression, "Compression for low-density chord-flow charts where chord changes overstate dan pressure."),
                    C("introHighChordFlowTechCompression", -introHighChordFlowTechCompression, "Compression for introductory high-chord flow with low row speed."),
                    C("earlyLowEntropyTechCompression", -earlyLowEntropyTechCompression, "Compression for early low-density tech with simple row timing."),
                    C("sparseLowSrTechVocabularyCompression", -sparseLowSrTechVocabularyCompression, "Compression for very sparse low-SR charts where timing vocabulary exceeds actual pressure."),
                    C("lowRateTechnicalVocabularyCompression", -lowRateTechnicalVocabularyCompression, "Compression for low-rate technical rhythm where vocabulary exceeds the pressure ceiling."),
                    C("compactHighChordTechCompression", -compactHighChordTechCompression, "Compression for compact high-chord technical marathons at the beta/gamma boundary."),
                    C("baseRateTechCompression", -baseRateTechCompression, "Compression for Crescent-like rate packs where base-rate SR overstates the dan jump."),
                    C("ratePackTechStructuralCompression", -ratePackTechStructuralCompression, "Compression for Crescent-like rate packs whose fast-row shape overstates whole-file tech pressure."),
                    C("repetitiveSpeedjackTechCompression", -repetitiveSpeedjackTechCompression, "Tech inflation removed when the chart is repetitive jackstream or speedjack rather than pattern tech."),
                    C("longSparseJackDropTechCompression", -longSparseJackDropTechCompression, "Tech inflation removed for long sparse jack-drop files."),
                    C("shortLnHybridStructuralCompression", -shortLnHybridStructuralCompression, "Shared compression for mixed short-LN charts where LN density overstates rice dan pressure."),
                    C("lowChordSteadySpeedStructuralCompression", -lowChordSteadySpeedStructuralCompression * 0.85, "Compression for low-chord steady speed where density overstates whole-chart dan pressure."),
                    C("moderateChordSteadyStreamStructuralCompression", -moderateChordSteadyStreamStructuralCompression, "Compression for long low-mid chord steady stream where sustained density overstates dan pressure."),
                    C("compactHandstreamStaminaStructuralCompression", -compactHandstreamStaminaStructuralCompression, "Compression for compact handstream stamina where chord changes overstate dan pressure."),
                    C("compactHandstreamStaminaTechCompression", -compactHandstreamStaminaTechCompression, "Tech inflation removed for compact handstream stamina patterns."),
                    C("compactTechnicalFlowStructuralCompression", -compactTechnicalFlowStructuralCompression, "Compression for compact technical flow whose local chord changes overstate dan pressure."),
                    C("compactChordWallStructuralCompression", -compactChordWallStructuralCompression, "Compression for compact high-chord wall-jacks with limited whole-chart pressure."),
                    C("simpleDenseChordWallStructuralCompression", -simpleDenseChordWallStructuralCompression, "Compression for simple dense chord walls with very low row-flow variety."),
                    C("lowRateDenseChordWallCompression", -lowRateDenseChordWallCompression, "Compression for low-rate dense chord walls whose chord density overstates dan pressure before sustained speed arrives."),
                    C("simpleMidHighChordWallStructuralCompression", -simpleMidHighChordWallStructuralCompression, "Compression for simple mid-high chord walls whose chord density overstates jack dan pressure."),
                    C("awkwardMidRateChordjackWallCompression", -awkwardMidRateChordjackWallCompression, "Compression for mid-rate chordjack walls whose awkwardness is real but overpromoted below full-rate pressure."),
                    C("midHighChordSustainedTechStructuralCompression", -midHighChordSustainedTechStructuralCompression, "Compression for sustained mid-high chord tech walls where row flow is simpler than the pressure estimate."),
                    C("shortDenseWallSrCompression", -shortDenseWallSrCompression, "Shared compression for short dense wall-jack files."),
                    C("lowSrShortDenseWallCompression", -lowSrShortDenseWallCompression, "Shared compression for lower-SR short wall-jack files."),
                    C("mediumWallJackOverrateCompression", -mediumWallJackOverrateCompression, "Shared compression for medium wall-jack files."),
                    C("midChordSpeedjackTechCompression", -midChordSpeedjackTechCompression, "Tech inflation removed from mid-chord speedjack files."),
                    C("compactPureChordjackStaminaCompression", -compactPureChordjackStaminaCompression, "Shared compression for compact pure chordjack stamina files."),
                    C("shortSimpleChordjackWallStructuralCompression", -shortSimpleChordjackWallStructuralCompression, "Compression for short simple chordjack walls where chord density overstates dan pressure."),
                    C("shortSpikeCompression", -shortSpikeCompression, "Compression for tech estimates driven by a short isolated burst."),
                    C("localizedJumptrillSpikeCompression", -localizedJumptrillSpikeCompression * 1.45, "Tech inflation removed when a localized jumptrill or vibro spike is not representative of whole-file tech pressure."),
                },
                [DanSkillFamily.Ln] = new(),
                [DanSkillFamily.Dan] = new(),
            },
        };

        return new DanFamilyScoreResult { SkillScores = skillScores, Debug = debug };
    }
}
