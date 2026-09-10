// Port of mania-hub live-backend/src/dan/dan-estimator/courses.ts
//
// Part of the deprecated estimateDan benchmark path (see PORTING.md). Not wired
// into the production ClassifyChart.
//
// PORT NOTE: the cross-agent brief said courses.ts imports `danCreditOffset`
// from dan-credit.ts, but the source file at
// live-backend/src/dan/dan-estimator/courses.ts has no such import or usage
// (verified 2026-09-10). Nothing to wire.

using System.Text.RegularExpressions;
using LazerSR.DanCalculator.Beatmap;
using LazerSR.DanCalculator.Types;

namespace LazerSR.DanCalculator.Benchmark;

public static class Courses
{
    private static readonly Regex DanWordPattern = new(@"\bdan\b", RegexOptions.Compiled);

    private static int CountDanSegments(List<(double Time, List<ManiaNote> Notes)> orderedRows)
    {
        if (orderedRows.Count == 0) return 0;

        int segments = 0;
        double segmentStart = orderedRows[0].Time;
        double segmentNotes = 0;

        for (int index = 0; index < orderedRows.Count; index++)
        {
            var (time, rowNotes) = orderedRows[index];
            segmentNotes += rowNotes.Count;
            (double Time, List<ManiaNote> Notes)? next = index + 1 < orderedRows.Count ? orderedRows[index + 1] : null;
            if (next == null || next.Value.Time - time > 2500)
            {
                if (time - segmentStart >= 30000 && segmentNotes >= 400) segments++;
                if (next != null)
                {
                    segmentStart = next.Value.Time;
                    segmentNotes = 0;
                }
            }
        }

        return segments;
    }

    public static bool IsDanCourse(DanEstimateInput input, List<(double Time, List<ManiaNote> Notes)> orderedRows, double durationMs, double noteCount)
    {
        string title = (input.Title ?? "").ToLowerInvariant();
        string version = (input.Version ?? "").ToLowerInvariant();
        string combined = $"{title} {version}";
        if (!DanWordPattern.IsMatch(combined)) return false;

        int segmentCount = CountDanSegments(orderedRows);
        return (durationMs >= 180000 && segmentCount >= 3)
            || (durationMs >= 240000 && noteCount >= 6000 && segmentCount >= 3);
    }

    public static double EstimateDanCourseSr(DanFeatureMetrics metrics, double starRating, double fallbackSr)
    {
        if (starRating <= 0) return Math.Max(1, fallbackSr - 0.8);

        double densityPressure = Math.Max(0, metrics.SustainedNps10s - 24) * 0.035
            + Math.Max(0, metrics.PeakNps5s - 28) * 0.025;
        double endurancePressure = Math.Min(0.12, metrics.NoteCount / 100000);
        double midCourseProgression = starRating >= 4.45
            && starRating <= 5.58
            && metrics.NoteCount >= 5500
            && metrics.SustainedNps10s >= 20
            && metrics.SustainedNps10s <= 24.2
            ? 0.18
            : 0;
        double sixthCourseEnduranceStep = starRating >= 4.6
            && starRating <= 4.8
            && metrics.NoteCount >= 6000
            && metrics.SustainedNps10s >= 20
            && metrics.SustainedNps10s <= 21.2
            ? 0.3
            : 0;
        double lowIntroProgression = starRating >= 2.2
            && starRating <= 3
            && metrics.NoteCount >= 1700
            && metrics.NoteCount <= 3200
            && metrics.PeakNps5s <= 12
            && metrics.SustainedNps10s <= 11
            ? (starRating < 2.6 ? 1.2 : 1.15)
            : 0;
        double highCourseDensityStep = metrics.NoteCount >= 7000
            && metrics.NoteCount <= 7800
            && metrics.ChordRatio >= 0.38
            && metrics.ChordRatio <= 0.42
            && metrics.HoldRatio < 0.02
            && metrics.FastRowRatio >= 0.55
            && metrics.FastRowRatio <= 0.62
            && metrics.PeakNps5s >= 25
            && metrics.PeakNps5s <= 26
            && metrics.SustainedNps10s >= 25
            && metrics.PatternVariety >= 3.85
            ? 0.15
            : 0;
        double seventhCourseStaminaCompression = metrics.NoteCount >= 7800
            && metrics.NoteCount <= 8300
            && metrics.ChordRatio >= 0.4
            && metrics.ChordRatio <= 0.44
            && metrics.HoldRatio < 0.02
            && metrics.FastRowRatio >= 0.55
            && metrics.FastRowRatio <= 0.6
            && metrics.PeakNps5s >= 24.5
            && metrics.PeakNps5s <= 25.2
            && metrics.SustainedNps10s >= 22
            && metrics.SustainedNps10s <= 23
            && metrics.PatternVariety >= 3.75
            ? 0.12
            : 0;
        double extraBetaCourseBridge = metrics.NoteCount >= 8300
            && metrics.NoteCount <= 8900
            && metrics.ChordRatio >= 0.42
            && metrics.ChordRatio <= 0.45
            && metrics.HoldRatio < 0.02
            && metrics.FastRowRatio >= 0.55
            && metrics.FastRowRatio <= 0.62
            && metrics.PeakNps5s >= 28
            && metrics.PeakNps5s <= 29
            && metrics.SustainedNps10s >= 27
            && metrics.SustainedNps10s <= 28
            && metrics.PatternVariety >= 3.65
            ? 0.2
            : 0;

        return starRating
            + Math.Min(0.55, densityPressure + endurancePressure + midCourseProgression + sixthCourseEnduranceStep)
            + lowIntroProgression
            + highCourseDensityStep
            + extraBetaCourseBridge
            - seventhCourseStaminaCompression;
    }
}
