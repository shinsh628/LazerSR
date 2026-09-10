// Port of mania-hub live-backend/src/dan/dan-estimator/labels.ts
// Dan ladder label vocabulary, raw-dan <-> SR calibration, input rate parsing.

using LazerSR.DanCalculator.Types;

namespace LazerSR.DanCalculator.Classifier;

public sealed record ParsedDanLabel(string Label, string? Variant, string DisplayName);

public static class Labels
{
    private static readonly string[] DAN_LABELS =
    {
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "10",
        "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa",
    };

    /* The ladder runs to kappa, and so does everything that reads it: LeoBlack's
       own 4K rice table names theta/iota/kappa (GREEK_LEVELS in
       leoblack-estimator.ts) and the badge art and dan picker carry all ten greek
       levels. This used to stop at eta, which capped the LABELS rather than the
       ratings: a credited 18.4 kept its number, sorted above a theta clear, and
       then printed "eta++" beside it, because parseDan clamped the level to 17 and
       left the 1.4 offset in the "++" band. Every stored 4K rice verdict comes from
       LeoBlack, so the chart side was already naming those levels correctly and
       only the player side had to shorten them. */
    private static readonly int MAX_SUPPORTED_DAN_INDEX = DAN_LABELS.Length - 1;

    // DAN_MEANS: Record<DanPrimaryFamily, number[]>
    private static double[] DanMeans(DanSkillFamily family) => family switch
    {
        DanSkillFamily.Jack => new[] { 3.15, 3.55, 3.95, 4.35, 4.75, 5.15, 5.45, 5.7, 5.92, 6.1, 6.35, 6.75, 7.15, 7.65, 8.25, 8.85, 9.55, 10.25, 11.0, 11.8 },
        DanSkillFamily.Stream => new[] { 3.1, 3.5, 3.9, 4.3, 4.7, 5.05, 5.35, 5.6, 5.78, 5.92, 6.12, 6.5, 6.92, 7.42, 8.08, 8.8, 9.65, 10.42, 11.2, 12.0 },
        DanSkillFamily.Jumpstream => new[] { 3.15, 3.55, 3.95, 4.35, 4.75, 5.1, 5.4, 5.66, 5.86, 6.02, 6.35, 6.72, 7.08, 7.55, 8.15, 8.85, 9.65, 10.38, 11.14, 11.95 },
        DanSkillFamily.Handstream => new[] { 3.2, 3.6, 4.0, 4.4, 4.8, 5.15, 5.45, 5.72, 5.92, 6.08, 6.6, 6.9, 6.96, 7.72, 8.48, 9.18, 9.98, 10.72, 11.46, 12.2 },
        DanSkillFamily.Stamina => new[] { 3.2, 3.6, 4.0, 4.4, 4.8, 5.15, 5.45, 5.72, 5.92, 6.08, 6.3, 6.7, 7.12, 7.62, 8.28, 8.98, 9.78, 10.52, 11.26, 12.0 },
        DanSkillFamily.Chordjack => new[] { 3.2, 3.6, 4.0, 4.42, 4.82, 5.18, 5.48, 5.75, 5.95, 6.12, 6.35, 6.75, 7.15, 7.65, 8.25, 8.85, 9.55, 10.25, 11.0, 11.8 },
        DanSkillFamily.Tech => new[] { 3.25, 3.65, 4.05, 4.48, 4.88, 5.25, 5.55, 5.82, 6.02, 6.18, 6.42, 6.82, 7.22, 7.72, 8.35, 9.02, 9.8, 10.52, 11.26, 12.0 },
        _ => new[] { 3.25, 3.65, 4.05, 4.48, 4.88, 5.25, 5.55, 5.82, 6.02, 6.18, 6.42, 6.82, 7.22, 7.72, 8.35, 9.02, 9.8, 10.52, 11.26, 12.0 },
    };

    private static double RawDanFromMeans(double value, double[] means)
    {
        int maxIndex = MAX_SUPPORTED_DAN_INDEX;
        var cappedMeans = means.Take(maxIndex + 1).ToArray();
        var boundaries = new (double Lower, double Upper, int Level)[cappedMeans.Length];
        for (int index = 0; index < cappedMeans.Length; index++)
        {
            double mean = cappedMeans[index];
            double lower = index == 0 ? mean - (cappedMeans[index + 1] - mean) / 2 : (cappedMeans[index - 1] + mean) / 2;
            double upper = index == cappedMeans.Length - 1 ? mean + (mean - cappedMeans[index - 1]) / 2 : (mean + cappedMeans[index + 1]) / 2;
            boundaries[index] = (lower, upper, index + 1);
        }

        if (value < boundaries[0].Lower) return 1;
        var last = boundaries[boundaries.Length - 1];
        if (value >= last.Upper) return maxIndex + 1;

        foreach (var boundary in boundaries)
        {
            if (value >= boundary.Lower && value < boundary.Upper)
            {
                double t = (value - boundary.Lower) / Math.Max(0.001, boundary.Upper - boundary.Lower);
                return boundary.Level + t - 0.5;
            }
        }

        return 1;
    }

    // SR_CALIBRATION: Record<DanPrimaryFamily, { slope; offset; gateStart; gateWidth }>
    private static (double Slope, double Offset, double GateStart, double GateWidth) SrCalibration(DanSkillFamily family) => family switch
    {
        DanSkillFamily.Jack => (0.74, 1.3, 7.4, 0.3),
        DanSkillFamily.Stream => (0.92, -0.4, 7, 0.9),
        DanSkillFamily.Jumpstream => (1.02, -0.45, 7.25, 0.55),
        DanSkillFamily.Handstream => (1.16, -2, 7.4, 0.2),
        DanSkillFamily.Stamina => (1.12, -1.5, 7.3, 0.55),
        DanSkillFamily.Chordjack => (1, 0, 7.15, 0.85),
        DanSkillFamily.Tech => (0.95, -0.9, 7.6, 0.9),
        _ => (0.95, -0.9, 7.6, 0.9),
    };

    private static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));

    private static double CalibrateSrForFamily(double sr, DanSkillFamily family)
    {
        var calibration = SrCalibration(family);
        double targetSr = sr * calibration.Slope + calibration.Offset;
        double gate = Clamp01((sr - calibration.GateStart) / calibration.GateWidth);
        return sr + (targetSr - sr) * gate;
    }

    private static DanSkillFamily GetCalibrationFamily(DanSkillFamily family)
        => family == DanSkillFamily.Jumpstream ? DanSkillFamily.Handstream : family;

    public static double GetInputRate(DanEstimateInput input)
    {
        double rate = input.Rate ?? double.NaN;
        return double.IsFinite(rate) && rate > 0.4 && rate < 2.5 ? rate : 1;
    }

    public static double SrToRawDan(double sr, DanSkillFamily family, bool calibrate = true)
    {
        var calibrationFamily = GetCalibrationFamily(family);
        double calibratedSr = calibrate == false ? sr : CalibrateSrForFamily(sr, calibrationFamily);
        return RawDanFromMeans(calibratedSr, DanMeans(calibrationFamily));
    }

    /// <summary>
    /// The inverse of parseDan's level naming: the level a bare 4K rice ladder
    /// label sits on ("10" -> 10, "epsilon" -> 15). Exported so the dan course
    /// registry can name a course by its community label and take the number from
    /// the same array parseDan prints it back from. Null for anything off the
    /// ladder; takes a bare label with no +/- variant.
    /// </summary>
    public static int? DanLevelForLabel(string label)
    {
        int index = Array.IndexOf(DAN_LABELS, label.Trim().ToLowerInvariant());
        return index < 0 ? null : index + 1;
    }

    public static ParsedDanLabel ParseDan(double rawDan)
    {
        int maxLevel = MAX_SUPPORTED_DAN_INDEX + 1;
        int level = (int)Math.Min(maxLevel, Math.Max(1, JsRound(rawDan)));
        double offset = rawDan - level;
        string? variant = offset <= -0.45 ? "--" : offset <= -0.25 ? "-" : offset < 0.1 ? null : offset < 0.26 ? "+" : "++";
        string labelName = DAN_LABELS[level - 1];
        return new ParsedDanLabel(labelName, variant, $"{labelName}{variant ?? ""}");
    }

    // JS Math.round: round half toward +Infinity.
    private static double JsRound(double v) => Math.Floor(v + 0.5);
}
