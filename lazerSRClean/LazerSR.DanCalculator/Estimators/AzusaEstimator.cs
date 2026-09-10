// Port of mania-hub live-backend/vendor/leoblack/estimator/azusaEstimator.js
//
// "Azusa" RC-scope 4K estimator: builds a per-note difficulty curve from decayed
// skill states, summarises it into a structural numeric, then blends with Daniel
// + Sunny reference numerics and runs a chain of calibration corrections.
//
// 1:1 mechanical port. Calibration tables + magic numbers preserved verbatim.

using System.Globalization;
using LazerSR.DanCalculator.Parser;

namespace LazerSR.DanCalculator.Estimators;

public static class AzusaEstimator
{
    // ── AZUSA_CONFIG ────────────────────────────────────────────────────────
    private const double CfgRcLnRatioLimit = 0.18;
    private const int CfgMinNotes = 80;
    private const double CfgRowToleranceMs = 2;
    private const double SwSpeed = 0.36;
    private const double SwStamina = 0.24;
    private const double SwChord = 0.12;
    private const double SwTech = 0.16;
    private const double SwJack = 0.12;
    private const double CfgLocalPower = 2.15;
    private static readonly double[] CfgDecayWindowsMs = { 140, 280, 560, 980 };
    private static readonly double[] CfgDecayWeights = { 0.34, 0.30, 0.22, 0.14 };
    private const double CfgLengthRefNotes = 600;
    private const double CfgLengthExponent = 0.22;
    private const double CfgLengthCap = 3.5;

    private static readonly double[][] AzusaCalibrationLowBlocks =
    {
        new[] { 1.9220, 1.9220, 1.0000 },
        new[] { 2.3660, 2.7684, 1.6667 },
        new[] { 2.8394, 2.8394, 2.0000 },
        new[] { 2.8584, 3.7162, 2.3333 },
        new[] { 3.7798, 3.7798, 3.0000 },
        new[] { 3.8667, 3.8667, 3.0000 },
        new[] { 4.2067, 5.2039, 4.3333 },
        new[] { 5.2506, 5.7713, 5.0667 },
        new[] { 5.8603, 6.1512, 5.3333 },
        new[] { 6.3292, 6.8785, 6.0000 },
        new[] { 7.1715, 7.3617, 6.2000 },
        new[] { 7.4079, 7.8734, 7.2000 },
        new[] { 8.0160, 8.4003, 8.2500 },
        new[] { 8.4133, 8.4133, 9.0000 },
        new[] { 8.9031, 9.4775, 9.5667 },
        new[] { 9.6488, 9.6488, 10.0000 },
        new[] { 9.8301, 9.8301, 10.3000 },
    };

    private static readonly double[][] AzusaCalibrationHighBlocks =
    {
        new[] { 11.4336, 11.4336, 10.4000 },
        new[] { 11.4436, 11.4436, 10.5000 },
        new[] { 11.6012, 11.6665, 10.6500 },
        new[] { 11.6696, 12.2317, 11.5000 },
        new[] { 12.3295, 12.3919, 11.7500 },
        new[] { 12.5238, 12.5238, 12.0000 },
        new[] { 12.5318, 12.8329, 12.1400 },
        new[] { 12.8605, 12.9781, 12.2800 },
        new[] { 12.9868, 13.1170, 12.7800 },
        new[] { 13.2003, 13.4418, 12.7857 },
        new[] { 13.4660, 13.5829, 12.9250 },
        new[] { 13.6044, 13.9924, 13.3667 },
        new[] { 14.0583, 14.0583, 13.4000 },
        new[] { 14.0795, 14.2266, 13.4600 },
        new[] { 14.2346, 14.2346, 13.6000 },
        new[] { 14.2414, 14.2414, 13.7000 },
        new[] { 14.2903, 14.2903, 14.0000 },
        new[] { 14.3258, 14.4760, 14.1200 },
        new[] { 14.5365, 14.6006, 14.1333 },
        new[] { 14.7269, 14.8716, 14.1333 },
        new[] { 15.0048, 15.0048, 14.4000 },
        new[] { 15.0521, 15.0521, 14.4000 },
        new[] { 15.0521, 15.0521, 14.4000 },
        new[] { 15.0950, 15.0950, 14.4000 },
        new[] { 15.2335, 15.2335, 14.4000 },
        new[] { 15.2388, 15.5821, 14.7385 },
        new[] { 15.6977, 15.7002, 14.8500 },
        new[] { 15.7535, 16.1593, 15.0667 },
        new[] { 16.2009, 16.2958, 15.1000 },
        new[] { 16.3172, 16.4748, 15.7600 },
        new[] { 16.5620, 16.9083, 15.9833 },
        new[] { 16.9485, 16.9485, 16.0000 },
        new[] { 17.0216, 17.3799, 16.1000 },
        new[] { 17.4616, 17.4616, 16.4000 },
        new[] { 17.5167, 17.5167, 16.4000 },
        new[] { 17.5306, 17.9077, 16.6400 },
        new[] { 18.1973, 18.1973, 17.2000 },
        new[] { 18.2026, 18.2026, 17.2000 },
        new[] { 18.4562, 19.3477, 17.9500 },
        new[] { 19.3477, 20.5000, 18.2000 },
        new[] { 20.5000, 22.0000, 18.6000 },
        new[] { 22.0000, 24.0000, 19.2000 },
        new[] { 24.0000, 27.0000, 20.0000 },
    };

    private static readonly double[][] AzusaIsotonicPoints =
    {
        new[] { 1.3868, 1.0000 }, new[] { 1.4574, 1.0000 }, new[] { 1.5361, 1.0000 }, new[] { 1.6320, 1.5000 },
        new[] { 1.9833, 2.5800 }, new[] { 2.2465, 2.6000 }, new[] { 2.3344, 2.8000 }, new[] { 2.5779, 3.4500 },
        new[] { 3.8277, 3.6000 }, new[] { 4.2824, 4.3429 }, new[] { 4.5665, 4.6250 }, new[] { 4.8016, 4.6750 },
        new[] { 4.9529, 5.1500 }, new[] { 5.1029, 5.4000 }, new[] { 5.2475, 5.4750 }, new[] { 5.5039, 5.9000 },
        new[] { 5.6951, 6.0143 }, new[] { 5.9213, 6.4000 }, new[] { 6.0093, 6.9000 }, new[] { 6.1337, 7.2000 },
        new[] { 6.7092, 7.4400 }, new[] { 7.2846, 7.5000 }, new[] { 7.4233, 7.8000 }, new[] { 7.9790, 8.6000 },
        new[] { 8.2927, 8.6143 }, new[] { 9.0829, 9.5000 }, new[] { 9.4639, 9.6154 }, new[] { 9.8115, 10.0000 },
        new[] { 9.8344, 10.4000 }, new[] { 10.0013, 10.4000 }, new[] { 10.0778, 10.5000 }, new[] { 10.1054, 10.5000 },
        new[] { 10.1435, 10.6000 }, new[] { 10.4782, 10.6462 }, new[] { 10.8866, 10.8000 }, new[] { 11.0934, 11.1727 },
        new[] { 11.3266, 11.2867 }, new[] { 11.4970, 11.4000 }, new[] { 11.6024, 11.4750 }, new[] { 11.6947, 11.6000 },
        new[] { 11.8932, 12.0636 }, new[] { 12.0076, 12.3000 }, new[] { 12.2947, 12.4150 }, new[] { 12.7583, 12.4500 },
        new[] { 12.8756, 12.9000 }, new[] { 12.9268, 12.9000 }, new[] { 13.0042, 13.2000 }, new[] { 13.2387, 13.2694 },
        new[] { 13.4620, 13.4400 }, new[] { 13.5467, 13.5000 }, new[] { 13.6016, 13.7375 }, new[] { 13.9609, 13.9500 },
        new[] { 14.1414, 14.0250 }, new[] { 14.2226, 14.0762 }, new[] { 14.3178, 14.1273 }, new[] { 14.3786, 14.1643 },
        new[] { 14.4421, 14.2182 }, new[] { 14.4825, 14.3000 }, new[] { 14.5063, 14.3750 }, new[] { 14.5452, 14.4778 },
        new[] { 14.6359, 14.5850 }, new[] { 14.7301, 14.6389 }, new[] { 14.8846, 14.7906 }, new[] { 15.0424, 14.9263 },
        new[] { 15.2159, 15.0944 }, new[] { 15.3942, 15.1875 }, new[] { 15.5380, 15.3300 }, new[] { 15.8096, 15.5320 },
        new[] { 16.0262, 16.1000 }, new[] { 16.0702, 16.1000 }, new[] { 16.2738, 16.1267 }, new[] { 16.4723, 16.3579 },
        new[] { 16.7156, 16.8000 }, new[] { 17.1446, 17.0600 }, new[] { 17.5478, 17.2000 }, new[] { 17.6403, 17.2000 },
        new[] { 17.7603, 17.2000 }, new[] { 17.8264, 17.6000 }, new[] { 18.1258, 17.9750 }, new[] { 18.5000, 18.2000 },
        new[] { 19.2000, 18.7000 }, new[] { 20.0000, 19.2000 }, new[] { 21.2000, 19.8000 }, new[] { 22.5000, 20.0000 },
    };

    // ── helpers ─────────────────────────────────────────────────────────────

    private static RcEstimatorResult BuildErrorResult(string code, string message, double lnRatio, double columnCount)
        => new()
        {
            Star = double.NaN,
            LnRatio = double.IsFinite(lnRatio) ? lnRatio : 0,
            ColumnCount = double.IsFinite(columnCount) ? columnCount : 0,
            EstDiff = $"Invalid: {message}",
            NumericDifficulty = null,
            NumericDifficultyHint = code,
            Graph = null,
            RawNumericDifficulty = null,
            Debug = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
        };

    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

    private static double SafeDiv(double a, double b, double fallback = 0)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b) || Math.Abs(b) < 1e-9) return fallback;
        return a / b;
    }

    private static double? Fmt4(double value)
        => double.IsFinite(value) ? Math.Round(value, 4, MidpointRounding.AwayFromZero) : null;

    private static bool IsFin(double? x) => x.HasValue && double.IsFinite(x.Value);

    private static double MaxOf(double[] values)
    {
        double m = double.NegativeInfinity;
        foreach (var v in values) m = Math.Max(m, v);
        return m;
    }

    private static double PiecewiseLinear(double x, double[][] knots, int valueCol = 1)
    {
        double v = x;
        if (!double.IsFinite(v) || knots.Length == 0) return v;
        if (v <= knots[0][0]) return knots[0][valueCol];
        int last = knots.Length - 1;
        if (v >= knots[last][0]) return knots[last][valueCol];
        for (int i = 0; i < last; i += 1)
        {
            double x0 = knots[i][0], y0 = knots[i][valueCol];
            double x1 = knots[i + 1][0], y1 = knots[i + 1][valueCol];
            if (v >= x0 && v <= x1) return y0 + SafeDiv((v - x0) * (y1 - y0), x1 - x0, 0);
        }
        return v;
    }

    private static double PiecewiseBlock(double x, double[][] blocks)
    {
        double v = x;
        if (!double.IsFinite(v) || blocks.Length == 0) return v;
        if (v <= blocks[0][0]) return blocks[0][2];
        int last = blocks.Length - 1;
        for (int i = 0; i < blocks.Length; i += 1)
        {
            double x0 = blocks[i][0], x1 = blocks[i][1], y = blocks[i][2];
            if (v >= x0 && v <= x1) return y;
            if (i < last && v > x1 && v < blocks[i + 1][0])
            {
                double t = SafeDiv(v - x1, blocks[i + 1][0] - x1, 0);
                return y * (1 - t) + blocks[i + 1][2] * t;
            }
        }
        return blocks[last][2];
    }

    private static double? EstimateDanielNumeric(SunnyResult? result)
    {
        double? numericRaw = result?.NumericDifficulty;
        if (numericRaw.HasValue && double.IsFinite(numericRaw.Value))
        {
            return numericRaw.Value;
        }
        // PORT NOTE: JS also parses a string numericDifficulty here; our SunnyResult
        // types it as double?, so the string branch is unreachable.

        double star = result?.Star ?? double.NaN;
        if (!double.IsFinite(star))
        {
            return null;
        }

        // Piecewise map keeps Daniel high-end semantics while extending low-end below Alpha.
        if (star >= 6.56)
        {
            double normalized = Clamp((star - 6.56) / 0.58, 0, 9.99);
            return Math.Round(11 + normalized, 2, MidpointRounding.AwayFromZero);
        }

        double lowPart = -2 + 13 * Math.Pow(Clamp(star / 6.56, 0, 1), 1.72);
        return Math.Round(lowPart, 2, MidpointRounding.AwayFromZero);
    }

    private static bool HasDanielNativeNumeric(SunnyResult? result)
    {
        double? raw = result?.NumericDifficulty;
        return raw.HasValue && double.IsFinite(raw.Value);
    }

    private static double? EstimateSunnyNumeric(SunnyResult? result)
    {
        double star = result?.Star ?? double.NaN;
        if (!double.IsFinite(star))
        {
            return null;
        }
        double numeric = 2.85 + 1.33 * star;
        return Math.Round(Clamp(numeric, -2, 20), 2, MidpointRounding.AwayFromZero);
    }

    private static double QuantileFromSorted(double[] sortedValues, double q)
    {
        if (sortedValues.Length == 0) return 0;
        double t = Clamp(q, 0, 1) * (sortedValues.Length - 1);
        int left = (int)Math.Floor(t);
        int right = Math.Min(sortedValues.Length - 1, left + 1);
        double w = t - left;
        return sortedValues[left] * (1 - w) + sortedValues[right] * w;
    }

    private static double PowerMean(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        double acc = 0;
        foreach (var value in values)
        {
            acc += Math.Pow(Math.Max(value, 0), p);
        }
        return Math.Pow(acc / values.Count, 1 / p);
    }

    // JS Array#slice(start) semantics (handles negative + overflow start).
    private static int JsSliceStart(int len, int start)
        => start < 0 ? Math.Max(len + start, 0) : Math.Min(start, len);

    // ── tap notes ───────────────────────────────────────────────────────────

    private struct AzusaTap
    {
        public double T;
        public int C;
        public int Hand;
        public double RowSize;
    }

    private static List<AzusaTap> BuildTapNotes(OsuParsedData parsed)
    {
        var taps = new List<AzusaTap>();
        var columns = parsed.Columns;
        var starts = parsed.NoteStarts;

        for (int i = 0; i < columns.Count; i += 1)
        {
            double col = columns[i];
            double time = i < starts.Count ? starts[i] : double.NaN;
            if (!double.IsFinite(col) || !double.IsFinite(time))
            {
                continue;
            }

            taps.Add(new AzusaTap
            {
                T = time,
                C = (int)col,
                Hand = col < 2 ? 0 : 1,
                RowSize = 1,
            });
        }

        taps.Sort((a, b) =>
        {
            if (a.T != b.T) return a.T.CompareTo(b.T);
            return a.C.CompareTo(b.C);
        });

        return taps;
    }

    private static void AnnotateRows(AzusaTap[] taps, double toleranceMs)
    {
        if (taps.Length == 0) return;

        int rowStart = 0;
        for (int i = 1; i <= taps.Length; i += 1)
        {
            bool shouldFlush = i == taps.Length || Math.Abs(taps[i].T - taps[rowStart].T) > toleranceMs;
            if (!shouldFlush)
            {
                continue;
            }

            double rowSize = i - rowStart;
            for (int j = rowStart; j < i; j += 1)
            {
                taps[j].RowSize = rowSize;
            }
            rowStart = i;
        }
    }

    private static double ExpDecayFactor(double dtMs, double tauMs)
    {
        if (!double.IsFinite(dtMs) || dtMs <= 0)
        {
            return 1;
        }
        return Math.Exp(-dtMs / tauMs);
    }

    private static double SkillFromStates(double[] states)
    {
        double sum = 0;
        for (int i = 0; i < states.Length; i += 1)
        {
            sum += states[i] * CfgDecayWeights[i];
        }
        return sum;
    }

    private sealed class AzusaCurve
    {
        public List<double> Local = new();
        public List<double> SpeedSeries = new();
        public List<double> StaminaSeries = new();
        public List<double> ChordSeries = new();
        public List<double> TechSeries = new();
        public List<double> JackSeries = new();
        public List<double> Times = new();
        public List<double> Density250 = new();
        public List<double> Density500 = new();
        public List<double> JackRawSeries = new();
        public double[] ColumnCounts = { 0, 0, 0, 0 };
        public double ChordNoteCount;
    }

    private static AzusaCurve BuildDifficultyCurve(AzusaTap[] taps)
    {
        int n = CfgDecayWindowsMs.Length;
        var speed = new double[n];
        var stamina = new double[n];
        var chordS = new double[n];
        var tech = new double[n];
        var jackSt = new double[n];

        var lastByColumn = new[] { -1e9, -1e9, -1e9, -1e9 };
        var lastByHand = new[] { -1e9, -1e9 };

        var curve = new AzusaCurve();
        int chordNoteCount = 0;
        int cursor250 = 0;
        int cursor500 = 0;

        double prevTime = taps.Length > 0 ? taps[0].T : 0;
        double prevAny1 = -1e9;
        double prevAny2 = -1e9;
        int prevCol = 0;

        for (int i = 0; i < taps.Length; i += 1)
        {
            var note = taps[i];
            double t = note.T;
            int c = note.C;
            curve.ColumnCounts[c] += 1;
            if (note.RowSize >= 2)
            {
                chordNoteCount += 1;
            }

            double dtGlobal = i == 0 ? 0 : Math.Max(0, t - prevTime);
            double dtSame = Math.Max(0, t - lastByColumn[c]);
            double dtHand = Math.Max(0, t - lastByHand[note.Hand]);
            double dtAny = Math.Max(0, t - prevAny1);

            while (cursor250 < i && t - taps[cursor250].T > 250) cursor250 += 1;
            while (cursor500 < i && t - taps[cursor500].T > 500) cursor500 += 1;

            double d250 = (i - cursor250 + 1) / 0.25;
            double d500 = (i - cursor500 + 1) / 0.5;
            curve.Density250.Add(d250);
            curve.Density500.Add(d500);

            double jack = Math.Pow(190 / (dtSame + 35), 1.16);
            curve.JackRawSeries.Add(jack);
            double stream = Math.Pow(170 / (dtAny + 30), 1.07);
            double handStream = Math.Pow(185 / (dtHand + 42), 1.08);

            double movement = Math.Abs(c - prevCol) / 3.0;
            double rhythmRatio = SafeDiv(Math.Max(dtAny, 1), Math.Max(t - prevAny2, 1), 1);
            double rhythmChaos = Math.Abs(Math.Log2(Clamp(rhythmRatio, 0.2, 5)));

            double rowChord = Math.Max(0, note.RowSize - 1);
            double chord = Math.Pow(rowChord + 1, 1.22) - 1;

            double speedInput = 0.60 * stream + 0.30 * handStream + 0.10 * jack;
            double jackInput = jack * (1 + 0.15 * chord);
            double staminaInput = 0.48 * (d500 / 11) + 0.27 * (d250 / 15) + 0.25 * stream;
            double chordInput = chord * (1 + 0.10 * Math.Min(1.5, stream));
            double techInput = 0.45 * rhythmChaos + 0.30 * movement + 0.25 * (rowChord > 0 ? 1 + 0.3 * rowChord : 0);

            for (int j = 0; j < CfgDecayWindowsMs.Length; j += 1)
            {
                double tau = CfgDecayWindowsMs[j];
                double decay = ExpDecayFactor(dtGlobal, tau);
                speed[j] = speed[j] * decay + speedInput;
                stamina[j] = stamina[j] * decay + staminaInput;
                chordS[j] = chordS[j] * decay + chordInput;
                tech[j] = tech[j] * decay + techInput;
                jackSt[j] = jackSt[j] * decay + jackInput;
            }

            double speedSkill = SkillFromStates(speed);
            double staminaSkill = SkillFromStates(stamina);
            double chordSkill = SkillFromStates(chordS);
            double techSkill = SkillFromStates(tech);
            double jackSkill = SkillFromStates(jackSt);

            double p = CfgLocalPower;
            double combined = Math.Pow(
                (
                    SwSpeed * Math.Pow(Math.Max(speedSkill, 0), p)
                    + SwStamina * Math.Pow(Math.Max(staminaSkill, 0), p)
                    + SwChord * Math.Pow(Math.Max(chordSkill, 0), p)
                    + SwTech * Math.Pow(Math.Max(techSkill, 0), p)
                    + SwJack * Math.Pow(Math.Max(jackSkill, 0), p)
                )
                / (SwSpeed + SwStamina + SwChord + SwTech + SwJack),
                1 / p);

            curve.Local.Add(combined);
            curve.SpeedSeries.Add(speedSkill);
            curve.StaminaSeries.Add(staminaSkill);
            curve.ChordSeries.Add(chordSkill);
            curve.TechSeries.Add(techSkill);
            curve.JackSeries.Add(jackSkill);
            curve.Times.Add(t);

            prevAny2 = prevAny1;
            prevAny1 = t;
            prevTime = t;
            prevCol = c;
            lastByColumn[c] = t;
            lastByHand[note.Hand] = t;
        }

        curve.ChordNoteCount = chordNoteCount;
        return curve;
    }

    private readonly record struct AzusaSummary(
        double Q97, double Q94, double Q90, double Q75, double Q50, double TailMean, double Pm);

    private static AzusaSummary Summarize(List<double> values)
    {
        var sorted = values.ToArray();
        System.Array.Sort(sorted);
        double q97 = QuantileFromSorted(sorted, 0.97);
        double q94 = QuantileFromSorted(sorted, 0.94);
        double q90 = QuantileFromSorted(sorted, 0.90);
        double q75 = QuantileFromSorted(sorted, 0.75);
        double q50 = QuantileFromSorted(sorted, 0.50);
        int tailCount = Math.Max(8, (int)Math.Floor(sorted.Length * 0.04));
        int sliceStart = JsSliceStart(sorted.Length, sorted.Length - tailCount);
        double tailSum = 0;
        int tailLen = sorted.Length - sliceStart;
        for (int i = sliceStart; i < sorted.Length; i += 1) tailSum += sorted[i];
        double tailMean = tailSum / tailLen;
        double pm = PowerMean(values, 2.6);
        return new AzusaSummary(q97, q94, q90, q75, q50, tailMean, pm);
    }

    private static double ComputeAzusaNumericFromCurve(AzusaCurve curve, int noteCount)
    {
        if (curve.Local.Count == 0)
        {
            return 0;
        }

        var speed = Summarize(curve.SpeedSeries);
        var stamina = Summarize(curve.StaminaSeries);
        var chord = Summarize(curve.ChordSeries);
        var tech = Summarize(curve.TechSeries);
        var jack = Summarize(curve.JackSeries);

        double density250 = PowerMean(curve.Density250, 1.18);
        double density500 = PowerMean(curve.Density500, 1.12);
        double lengthBoost = Math.Min(CfgLengthCap,
            Math.Pow(Math.Max(noteCount, 1) / CfgLengthRefNotes, CfgLengthExponent));

        double peakBlend =
            (0.26 * speed.Q97)
            + (0.22 * stamina.Q97)
            + (0.10 * chord.Q97)
            + (0.10 * tech.Q97)
            + (0.10 * jack.Q97)
            + (0.06 * speed.Q90)
            + (0.04 * stamina.Q90)
            + (0.02 * chord.Q90)
            + (0.02 * tech.Q90)
            + (0.02 * jack.Q90);

        double sustainBlend =
            (0.18 * speed.Q75)
            + (0.16 * stamina.Q75)
            + (0.08 * chord.Q75)
            + (0.06 * tech.Q75)
            + (0.08 * jack.Q75)
            + (0.10 * speed.TailMean)
            + (0.08 * stamina.TailMean)
            + (0.04 * chord.TailMean)
            + (0.04 * tech.TailMean)
            + (0.04 * jack.TailMean);

        double densityBlend = (0.14 * Math.Log(1 + density250)) + (0.22 * Math.Log(1 + density500));
        double midBlend =
            (0.16 * speed.Q50) + (0.13 * stamina.Q50)
            + (0.06 * chord.Q50) + (0.06 * tech.Q50) + (0.06 * jack.Q50);

        double raw =
            (0.52 * peakBlend) + (0.26 * sustainBlend)
            + (0.10 * densityBlend) + (0.08 * midBlend) + (0.04 * lengthBoost);
        double scaled = 0.82 + (0.43 * raw);

        double maxColumn = MaxOf(curve.ColumnCounts);
        double anchorImbalance = SafeDiv((maxColumn / Math.Max(noteCount, 1)) - 0.25, 0.75, 0);
        double chordRate = SafeDiv(curve.ChordNoteCount, Math.Max(noteCount, 1), 0);
        var jackSorted = curve.JackRawSeries.ToArray();
        System.Array.Sort(jackSorted);
        double jackQ95 = QuantileFromSorted(jackSorted, 0.95);

        // Chordjack interaction: chord density × jack density co-occurrence
        double chordjackBoost = Clamp(
            2.5
            * Clamp((chordRate - 0.40) * 3.5, 0, 1)
            * Clamp((jackQ95 - 1.25) * 2.8, 0, 1)
            * Clamp(1 - (anchorImbalance * 8), 0, 1),
            0,
            2.2);

        double totalTimeSec = Math.Max(1, (curve.Times[curve.Times.Count - 1] - curve.Times[0]) / 1000);
        double avgNPS = noteCount / totalTimeSec;
        double midSpeedBonus = Clamp((avgNPS - 9) * 0.04, 0, 0.35) * Clamp((19 - avgNPS) * 0.25, 0, 1);

        double corrected = scaled + chordjackBoost + midSpeedBonus;
        return Clamp(corrected, -2, 20);
    }

    private sealed class AzusaBlendDetails
    {
        public double? Value;
        public double? LowGateSource;
        public double? LowGate;
        public double? HighGate;
        public double? LowBase;
        public double? HighBase;
    }

    private static AzusaBlendDetails ResolveRcBlendComponents(
        double? primaryNumeric, double? danielNumeric, double? sunnyNumeric,
        double? hintAnchorImbalance, double? hintChordRate, double? hintJackQ95)
    {
        double? primary = IsFin(primaryNumeric) ? primaryNumeric : null;
        double? daniel = IsFin(danielNumeric) ? danielNumeric : null;
        double? sunny = IsFin(sunnyNumeric) ? sunnyNumeric : null;

        if (daniel == null && primary == null && sunny == null)
        {
            return new AzusaBlendDetails();
        }

        double lowGateSource = daniel ?? (sunny ?? primary ?? 0);
        double lowGate = Clamp((9.61 - lowGateSource) / 4.94, 0, 1);
        double highGate = 1 - lowGate;

        double? lowBase;
        if (sunny == null)
        {
            lowBase = null;
        }
        else
        {
            double value = (-8.317) + (1.536 * sunny.Value);
            if (primary != null) value += 0.011 * primary.Value;
            if (daniel != null) value += 0.049 * daniel.Value;

            if (lowGate > 0)
            {
                double primaryPart = primary != null ? Math.Max(0, primary.Value - 10.4) : 0;
                double sunnyPart = Math.Max(0, sunny.Value - 9.84);
                double lowSunnyConvex = Math.Pow(Math.Max(0, 7.935 - sunny.Value), 2);
                value += lowGate * ((0.442 * sunnyPart) + (0.016 * primaryPart) + (0.235 * lowSunnyConvex));
            }

            lowBase = value;
        }

        double? highBase;
        {
            double? dUse = daniel ?? (sunny ?? primary);
            if (dUse == null)
            {
                highBase = null;
            }
            else
            {
                double primaryUse = primary ?? dUse.Value;
                double sunnyUse = sunny ?? dUse.Value;

                double value = (0.809 * dUse.Value) + (0.057 * primaryUse) + (0.165 * sunnyUse) + 0.183;

                double highMask = Clamp((lowGateSource - 14.83) / 2.667, 0, 1);
                if (highMask > 0)
                {
                    value += highMask
                        * ((-0.154 * Math.Max(0, primaryUse - dUse.Value)) + (0.081 * Math.Max(0, sunnyUse - dUse.Value)));
                }

                double? anchorImbalance = IsFin(hintAnchorImbalance) ? hintAnchorImbalance : null;
                double? chordRate = IsFin(hintChordRate) ? hintChordRate : null;
                double? jackQ95 = IsFin(hintJackQ95) ? hintJackQ95 : null;
                if (anchorImbalance != null && chordRate != null && jackQ95 != null)
                {
                    double anchorLift = Clamp(
                        0.20
                        * Math.Max(0, jackQ95.Value - 2.08)
                        * Math.Max(0, 0.24 - chordRate.Value)
                        * Math.Max(0, anchorImbalance.Value - 0.10),
                        0,
                        0.25);
                    value += anchorLift;
                }

                highBase = value;
            }
        }

        double lowLift = double.IsFinite(lowGateSource)
            ? Math.Max(0, 9.889 - lowGateSource) * 0.257
            : 0;

        var details = new AzusaBlendDetails
        {
            LowGateSource = lowGateSource,
            LowGate = lowGate,
            HighGate = highGate,
            LowBase = lowBase,
            HighBase = highBase,
        };

        if (lowBase == null && highBase == null)
        {
            details.Value = null;
            return details;
        }
        if (lowBase == null)
        {
            details.Value = highBase;
            return details;
        }
        if (highBase == null)
        {
            details.Value = lowBase.Value + lowLift;
            return details;
        }

        details.Value = (lowBase.Value * lowGate) + ((highBase.Value + lowLift) * highGate);
        return details;
    }

    private static double CalibrateAzusaNumeric(double? value, double? lowGate, double? highGate)
    {
        // JS `Number(value)` — null -> 0.
        double v = value ?? 0;
        if (!double.IsFinite(v)) return v;
        double low = PiecewiseBlock(v, AzusaCalibrationLowBlocks);
        double high = PiecewiseBlock(v, AzusaCalibrationHighBlocks);
        double? lg = IsFin(lowGate) ? Clamp(lowGate!.Value, 0, 1) : null;
        double? hg = IsFin(highGate) ? Clamp(highGate!.Value, 0, 1) : null;
        if (lg == null && hg == null) return v < 11 ? low : high;
        double lw = lg ?? Math.Max(0, 1 - (hg ?? 0));
        double hw = hg ?? Math.Max(0, 1 - lw);
        double ws = lw + hw;
        if (ws <= 1e-6) return v < 11 ? low : high;
        return (lw * low + hw * high) / ws;
    }

    private static double CalibrateAzusaOutputNumeric(double value)
        => PiecewiseLinear(value, AzusaIsotonicPoints, 1);

    private static double ComputeCurveGapResidualCorrection(
        double baseNumeric, AzusaBlendDetails blendDetails,
        double anchorImbalanceStat, double chordRateStat, double jackQ95Stat,
        double? primaryNumeric, double? sunnyNumeric, double? danielNumeric)
    {
        double x = baseNumeric;
        if (!double.IsFinite(x))
        {
            return 0;
        }

        double highGate = IsFin(blendDetails.HighGate) ? Clamp(blendDetails.HighGate!.Value, 0, 1) : 0;
        double primary = IsFin(primaryNumeric) ? primaryNumeric!.Value : x;
        double sunny = IsFin(sunnyNumeric) ? sunnyNumeric!.Value : x;
        double daniel = IsFin(danielNumeric) ? danielNumeric!.Value : x;
        double ds = daniel - sunny;
        double sp = sunny - primary;
        double anchorImbalance = double.IsFinite(anchorImbalanceStat) ? anchorImbalanceStat : 0;
        double chordRate = double.IsFinite(chordRateStat) ? chordRateStat : 0;
        double jackQ95 = double.IsFinite(jackQ95Stat) ? jackQ95Stat : 0;

        double residual = (
            4.335282
            + (-0.170459 * x)
            + (-1.622303 * Math.Max(0, 11 - x))
            + (1.328125 * Math.Max(0, 12.5 - x))
            + (-0.042829 * Math.Max(0, 14 - x))
            + (-0.834997 * highGate)
            + (3.060352 * highGate * Math.Max(0, 11 - x))
            + (-1.744638 * highGate * Math.Max(0, 12.5 - x))
            + (0.409922 * ds)
            + (0.041072 * sp)
            + (-0.388231 * highGate * ds)
            + (-0.170185 * highGate * sp)
            + (3.466868 * anchorImbalance)
            + (-1.743778 * chordRate)
            + (-0.094758 * jackQ95)
            + (2.626366 * anchorImbalance * jackQ95)
            + (1.836357 * chordRate * jackQ95)
            + (-2.612648 * highGate * anchorImbalance)
            + (-2.493596 * highGate * chordRate)
        );

        return Clamp(residual, -1.2, 1.2);
    }

    private static double ComputeReferenceCorrection(double azusaEst, double? danielNumeric, double? sunnyNumeric)
    {
        double x = azusaEst;
        if (!double.IsFinite(x)) return 0;

        if (x < 10.0 || x > 17.5) return 0;

        double? daniel = IsFin(danielNumeric) ? danielNumeric : null;
        double? sunny = IsFin(sunnyNumeric) ? sunnyNumeric : null;

        // Range-dependent gate and coefficients
        double gate, coeffD, coeffS;

        if (x < 11.5)
        {
            gate = Clamp((x - 10.0) / 1.5, 0, 1);
            coeffD = 0.10;
            coeffS = 0.06;
        }
        else if (x < 12.5)
        {
            gate = 1.0;
            coeffD = 0.20;
            coeffS = 0.13;
        }
        else if (x < 16.0)
        {
            gate = 1.0;
            coeffD = 0.40;
            coeffS = 0.25;
        }
        else
        {
            gate = Clamp((17.5 - x) / 1.5, 0, 1);
            coeffD = 0.28;
            coeffS = 0.17;
        }

        double correction = 0;
        if (daniel != null) correction += coeffD * (daniel.Value - x);
        if (sunny != null) correction += coeffS * (sunny.Value - x);

        return Clamp(correction * gate, -1.2, 1.2);
    }

    private static string? Fixed4(double? x)
        => x.HasValue && double.IsFinite(x.Value)
            ? x.Value.ToString("F4", CultureInfo.InvariantCulture)
            : null;

    public static RcEstimatorResult RunAzusaEstimatorFromText(string osuText, EstimatorOptions? options = null, OsuFileParser? parsed = null)
    {
        options ??= new EstimatorOptions();
        double speedRate = options.SpeedRate is { } sr && double.IsFinite(sr) && sr > 0 ? sr : 1.0;
        bool withGraph = options.WithGraph;
        bool forceSunnyReferenceHo = options.ForceSunnyReferenceHo;
        var precomputedDanielResult = options.PrecomputedDanielResult;
        var precomputedSunnyResult = options.PrecomputedSunnyResult;

        var parser = parsed ?? new OsuFileParser(osuText);
        if (parsed == null) parser.Process();
        var parsedData = parser.GetParsedData();

        double lnRatio = double.IsNaN(parsedData.LnRatio) ? 0 : parsedData.LnRatio;
        double columnCount = double.IsNaN(parsedData.ColumnCount) ? 0 : parsedData.ColumnCount;

        if (parsedData.Status == "Fail")
        {
            return BuildErrorResult("ParseFailed", "Beatmap parse failed", lnRatio, columnCount);
        }
        if (parsedData.Status == "NotMania")
        {
            return BuildErrorResult("NotMania", "Beatmap mode is not mania", lnRatio, columnCount);
        }
        if (columnCount != 4)
        {
            return BuildErrorResult("UnsupportedKeys", "Azusa only supports 4K", lnRatio, columnCount);
        }
        if (lnRatio > CfgRcLnRatioLimit)
        {
            return BuildErrorResult(
                "UnsupportedLN",
                $"Azusa RC scope rejects LN ratio {(lnRatio * 100).ToString("F1", CultureInfo.InvariantCulture)}%",
                lnRatio, columnCount);
        }

        var tapList = BuildTapNotes(parsedData);
        if (tapList.Count < CfgMinNotes)
        {
            return BuildErrorResult(
                "TooShort",
                $"Insufficient notes for stable estimate ({tapList.Count})",
                lnRatio, columnCount);
        }

        double timeScale = speedRate != 0 ? (1 / speedRate) : 1;
        var taps = tapList.ToArray();
        AzusaTap[] scaledTaps;
        if (timeScale == 1)
        {
            scaledTaps = taps;
        }
        else
        {
            scaledTaps = new AzusaTap[taps.Length];
            for (int i = 0; i < taps.Length; i += 1)
            {
                var t = taps[i];
                t.T *= timeScale;
                scaledTaps[i] = t;
            }
        }

        AnnotateRows(scaledTaps, CfgRowToleranceMs * timeScale);

        var curve = BuildDifficultyCurve(scaledTaps);
        double primaryNumeric = ComputeAzusaNumericFromCurve(curve, taps.Length);

        double maxColumn = MaxOf(curve.ColumnCounts);
        double anchorImbalance = SafeDiv((maxColumn / Math.Max(taps.Length, 1)) - 0.25, 0.75, 0);
        double chordRate = SafeDiv(curve.ChordNoteCount, Math.Max(taps.Length, 1), 0);
        var jackSorted = curve.JackRawSeries.ToArray();
        System.Array.Sort(jackSorted);
        double jackQ95 = QuantileFromSorted(jackSorted, 0.95);

        double? danielNumeric = null;
        bool danielHasNativeNumeric = false;
        double? sunnyNumeric = null;
        SunnyResult? sunnyResult = precomputedSunnyResult;

        if (precomputedDanielResult != null)
        {
            danielNumeric = EstimateDanielNumeric(precomputedDanielResult);
            danielHasNativeNumeric = HasDanielNativeNumeric(precomputedDanielResult);
        }
        else
        {
            try
            {
                var danielResult = DanielEstimator.RunDanielEstimatorFromText(osuText, options, parsed);
                danielNumeric = EstimateDanielNumeric(danielResult);
                danielHasNativeNumeric = HasDanielNativeNumeric(danielResult);
            }
            catch
            {
                danielNumeric = null;
                danielHasNativeNumeric = false;
            }
        }

        if (sunnyResult != null)
        {
            sunnyNumeric = EstimateSunnyNumeric(sunnyResult);
        }
        else
        {
            try
            {
                string? sunnyCvt = forceSunnyReferenceHo ? "HO" : options.CvtFlag;
                sunnyResult = SunnyShim.Run(
                    osuText, speedRate, AsOdDouble(options.OdFlag), sunnyCvt, withGraph);
                sunnyNumeric = EstimateSunnyNumeric(sunnyResult);
            }
            catch
            {
                sunnyNumeric = null;
                sunnyResult = null;
            }
        }

        double? danielNumericForBlend = danielNumeric;
        if (!danielHasNativeNumeric && IsFin(danielNumeric))
        {
            double highSignal = Math.Max(
                Math.Max(
                    IsFin(primaryNumeric) ? primaryNumeric : double.NegativeInfinity,
                    IsFin(sunnyNumeric) ? sunnyNumeric!.Value : double.NegativeInfinity),
                danielNumeric!.Value);

            if (highSignal < 14)
            {
                double speedDelta = speedRate - 1.0;
                double fallbackScale = speedDelta < 0
                    ? Clamp((-speedDelta) * 0.43, 0, 1)
                    : Clamp(speedDelta * 0.35, 0, 1);

                danielNumericForBlend = danielNumeric.Value * fallbackScale;
            }
        }

        var blendDetails = ResolveRcBlendComponents(
            primaryNumeric, danielNumericForBlend, sunnyNumeric,
            anchorImbalance, chordRate, jackQ95);
        double? numericDifficulty = blendDetails.Value;
        double calibratedNumeric = CalibrateAzusaNumeric(numericDifficulty, blendDetails.LowGate, blendDetails.HighGate);
        double curveGapResidual = ComputeCurveGapResidualCorrection(
            calibratedNumeric,
            blendDetails,
            anchorImbalance, chordRate, jackQ95,
            primaryNumeric,
            sunnyNumeric,
            danielNumericForBlend);
        double preOutputNumeric = Clamp(calibratedNumeric + curveGapResidual, -2, 20);
        double outputNumeric = CalibrateAzusaOutputNumeric(preOutputNumeric);
        double refCorrection = ComputeReferenceCorrection(outputNumeric, danielNumericForBlend, sunnyNumeric);
        double finalNumeric = Clamp(outputNumeric + refCorrection, -2, 20);

        // 马拉松时长修正（估算器内部应用）。参数与机制见 docs/features/marathon-correction.md。
        var mc = options.MarathonCorrection;
        if (mc != null && mc.DurationS is { } durS && double.IsFinite(durS))
        {
            double corr = MarathonCorrection.ComputeMarathonCorrection(
                mc.DurationS, mc.EttValues, finalNumeric);
            if (corr > 0)
            {
                finalNumeric = finalNumeric - corr;
            }
        }

        string estDiff = RcDifficultyFormat.NumericToRcLabel(finalNumeric);

        var result = new RcEstimatorResult
        {
            Star = Math.Round(3.4 + 0.38 * finalNumeric, 4, MidpointRounding.AwayFromZero),
            LnRatio = lnRatio,
            ColumnCount = columnCount,
            EstDiff = estDiff,
            NumericDifficulty = Math.Round(finalNumeric, 2, MidpointRounding.AwayFromZero),
            NumericDifficultyHint = "azusa-rc-v1",
            Graph = withGraph ? sunnyResult?.Graph : null,
            RawNumericDifficulty = Math.Round(primaryNumeric, 4, MidpointRounding.AwayFromZero),
            Debug = new Dictionary<string, object?>
            {
                ["primaryNumeric"] = Fmt4(primaryNumeric),
                ["blendNumeric"] = numericDifficulty.HasValue ? Fmt4(numericDifficulty.Value) : null,
                ["danielNumeric"] = danielNumeric.HasValue ? Fmt4(danielNumeric.Value) : null,
                ["danielNumericForBlend"] = danielNumericForBlend.HasValue ? Fmt4(danielNumericForBlend.Value) : null,
                ["danielHasNativeNumeric"] = danielHasNativeNumeric,
                ["sunnyNumeric"] = sunnyNumeric.HasValue ? Fmt4(sunnyNumeric.Value) : null,
                ["notes"] = taps.Length,
                ["calibratedNumeric"] = Fmt4(calibratedNumeric),
                ["curveStats"] = new Dictionary<string, object?>
                {
                    ["anchorImbalance"] = Fmt4(anchorImbalance),
                    ["chordRate"] = Fmt4(chordRate),
                    ["jackQ95"] = Fmt4(jackQ95),
                },
                ["curveGapResidual"] = Fmt4(curveGapResidual),
                ["outputNumeric"] = Fmt4(outputNumeric),
                ["postCurveGapResidual"] = Fmt4(refCorrection),
                ["finalNumeric"] = Fmt4(finalNumeric),
                ["blend"] = new Dictionary<string, object?>
                {
                    ["lowGateSource"] = Fixed4(blendDetails.LowGateSource),
                    ["lowGate"] = Fixed4(blendDetails.LowGate),
                    ["highGate"] = Fixed4(blendDetails.HighGate),
                    ["lowBase"] = Fixed4(blendDetails.LowBase),
                    ["highBase"] = Fixed4(blendDetails.HighBase),
                },
            },
        };

        return result;
    }

    private static double? AsOdDouble(object? odFlag)
    {
        if (odFlag is double d) return d;
        if (odFlag is int i) return i;
        if (odFlag is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
        {
            return p;
        }
        return null;
    }

    private static bool IsFin(double x) => double.IsFinite(x);
}
