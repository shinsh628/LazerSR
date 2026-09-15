// Port of mania-hub live-backend/vendor/leoblack/estimator/roxyEstimator.js
//
// "Roxy" RC-scope 4K estimator: a calibrated meta model. Builds a per-row
// difficulty curve from decayed skill states, maps it to a structural numeric
// via an isotonic knot table, then feeds a large feature vector (its own
// structural detail + Azusa/Sunny/Daniel reference numerics) into a frozen
// ridge meta model (RoxyMetaModel). A chain of OD / structural-backstop /
// reference-gap / Azusa-fusion / marathon corrections follows. High-difficulty
// focused: below Alpha (11) and at/above Emik Zeta high (17) it emits a scope
// label with numeric null, letting the Mixed estimator route elsewhere.
//
// 1:1 mechanical port. Every constant in ROXY_CONFIG, isotonicKnots, the
// STREAM_* tables and the reference-gap tables is transcribed EXACTLY.
// Calibration rationale comments (originally Chinese) translated verbatim.

using System.Globalization;
using LazerSR.DanCalculator.Parser;

namespace LazerSR.DanCalculator.Estimators;

public static class RoxyEstimator
{
    // ── ROXY_CONFIG ─────────────────────────────────────────────────────────
    private const double CfgRcLnRatioLimit = 0.18;
    private const int CfgMinNotes = 80;
    private const double CfgRowToleranceMs = 2;
    private const double CfgEntropyWindowMs = 750;
    private static readonly double[] CfgNpsWindowsMs = { 250, 500, 1000, 4000 };
    private const double CfgSectionMs = 400;
    private const double CfgSectionDecay = 0.9;
    private const double CfgSectionEmaAlpha = 0.15;
    private const double CfgCorrectionClamp = 1.25;
    private const double CfgRawMapP02 = 3.9947;
    private const double CfgRawMapP98 = 7.5454;

    // streamWeights (insertion order defines STREAM_NAMES)
    private static readonly string[] StreamNames =
        { "speed", "handStream", "jack", "chordjack", "tech", "stamina", "course" };
    private static readonly double[] StreamWeights = { 0.22, 0.18, 0.16, 0.16, 0.12, 0.11, 0.05 };
    private static readonly double[] StreamBurstTau = { 220, 260, 300, 260, 450, 1200, 30000 };
    private static readonly double[] StreamStaminaTau = { 1600, 2200, 1800, 2400, 3200, 10000, 120000 };
    private static readonly double[] StreamBurstMix = { 0.78, 0.80, 0.88, 0.82, 0.70, 0.58, 0.35 };

    // STREAM_INPUT_BY_NAME — index i corresponds to StreamNames[i]:
    // speed->speedIn, handStream->handIn, jack->jackIn, chordjack->chordjackIn,
    // tech->techIn, stamina->staminaIn, course->courseIn

    private static readonly double[][] IsotonicKnots =
    {
        new[] { -2.6250, 2.4444 },
        new[] { -2.5000, 2.9000 },
        new[] { -2.1782, 3.2000 },
        new[] { -1.6429, 3.4667 },
        new[] { -0.8081, 4.9333 },
        new[] { -0.5781, 5.0000 },
        new[] { -0.3751, 5.1250 },
        new[] { 0.0878, 5.7000 },
        new[] { 0.5414, 7.3500 },
        new[] { 0.7248, 9.6000 },
        new[] { 1.2435, 9.7625 },
        new[] { 2.2100, 9.8379 },
        new[] { 3.3439, 10.3810 },
        new[] { 4.1521, 10.8619 },
        new[] { 4.6770, 12.2111 },
        new[] { 7.5944, 12.8954 },
        new[] { 10.3796, 12.9333 },
        new[] { 10.7539, 13.1211 },
        new[] { 11.2944, 13.1733 },
        new[] { 12.4106, 13.4225 },
        new[] { 13.3667, 13.7143 },
        new[] { 14.0177, 14.0761 },
        new[] { 15.2659, 14.1489 },
        new[] { 16.4144, 14.3000 },
        new[] { 16.9566, 14.3174 },
        new[] { 17.5080, 14.6000 },
        new[] { 17.9004, 14.8917 },
        new[] { 18.1870, 15.0000 },
        new[] { 18.5160, 15.0636 },
        new[] { 19.5870, 15.2889 },
        new[] { 20.2551, 15.6111 },
        new[] { 21.0298, 16.0000 },
        new[] { 21.3373, 16.5833 },
    };

    private const double RoxyThetaHighNumeric = 18.4;
    private const string RoxyThetaHighLabel = "> CloverWisp Theta high";
    private const double RoxyNumericOutputMax = 30;
    private const double RoxyOdNeutral = 9;
    private const double RoxyCanonicalFirstObjectMs = 1000;
    // Azusa fusion: finalNumeric and pred_Azusa are weighted 0.4/0.6 (biased
    // toward Roxy, because Azusa has higher variance). Averaging two
    // approximately-unbiased estimators reduces variance (benchmark verified:
    // 10~17 Exact roughly +3.5pp).
    private const double RoxyAzusaFusionWeight = 0.4;
    // Roxy high-difficulty focus: consistent with Daniel, low difficulty
    // (final numeric < Alpha) does not emit a valid numeric — returns
    // "< Alpha Low" (numeric null), routed by Mixed to Azusa. Boundary is 11
    // (Alpha). Upper bound likewise: final numeric >= 17 returns
    // "> Emik Zeta high" (numeric null), marked Invalid.
    private const double RoxyScopeMin = 11;
    private const string RoxyScopeMinLabel = "< Alpha Low";
    private const double RoxyScopeMax = 17;
    private const string RoxyScopeMaxLabel = "> Emik Zeta high";

    private const double RoxyReferenceBucketSize = 1.0;
    // ROXY_DISABLED_META_REFERENCES = new Set(["Sunny"])
    private static readonly double[] RoxyReferenceGapFeatureMean =
    {
        0.07809006,
        0.29256211,
        -0.02192547,
        0.26793478,
        0.32663043,
        0.04266659,
        0.02789153,
        0.00078517,
        0.14369285,
        -0.51494749,
    };
    private static readonly double[] RoxyReferenceGapFeatureScale =
    {
        0.34015787,
        0.32576873,
        2.49325258,
        0.22364344,
        0.29159975,
        0.17146345,
        0.19191325,
        0.00597972,
        0.19899545,
        1.40898167,
    };
    private static readonly double[] RoxyReferenceGapBeta =
    {
        -0.0060869565,
        0.0605011303,
        -0.1187884725,
        -0.0070736868,
        -0.0590087101,
        0.1468674261,
        0.0562217676,
        -0.1003859899,
        0.1116677492,
        -0.0281818287,
        0.0297534048,
    };
    private const double RoxyReferenceGapCorrectionScale = 0.33;

    // ROXY_META_ALGOS
    private static readonly string[] RoxyMetaAlgos = { "Azusa", "Sunny", "Daniel", "Roxy" };

    // ── generic helpers ─────────────────────────────────────────────────────

    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

    private static double SafeDiv(double a, double b, double fallback = 0)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b) || Math.Abs(b) < 1e-9) return fallback;
        return a / b;
    }

    private static double? Fmt4(double value)
        => double.IsFinite(value) ? Math.Round(value, 4, MidpointRounding.AwayFromZero) : null;

    private static double? Fmt4(double? value)
        => value.HasValue && double.IsFinite(value.Value)
            ? Math.Round(value.Value, 4, MidpointRounding.AwayFromZero)
            : null;

    private static bool IsFin(double? x) => x.HasValue && double.IsFinite(x.Value);

    // JS `Number(x)` for the loose objects the option bag / normaliser produce.
    private static double ToDouble(object? x)
    {
        switch (x)
        {
            case null: return double.NaN;
            case double d: return d;
            case int i: return i;
            case string s: return NumberJs(s);
            default: return double.NaN;
        }
    }

    // JS `Number(str)` — full-string numeric coercion ("" -> 0, junk -> NaN).
    private static double NumberJs(string? s)
    {
        if (s == null) return 0;
        string t = s.Trim();
        if (t.Length == 0) return 0;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
        if (t == "Infinity" || t == "+Infinity") return double.PositiveInfinity;
        if (t == "-Infinity") return double.NegativeInfinity;
        return double.NaN;
    }

    // JS `String(number)` — shortest round-trip repr (used only for debug + flag).
    private static string JsString(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    private static RcEstimatorResult BuildErrorResult(string code, string message, double? lnRatio = null, double? columnCount = null)
        => new()
        {
            Star = double.NaN,
            LnRatio = IsFin(lnRatio) ? lnRatio!.Value : 0,
            ColumnCount = IsFin(columnCount) ? columnCount!.Value : 0,
            EstDiff = $"Invalid: {message}",
            NumericDifficulty = null,
            NumericDifficultyHint = code,
            Graph = null,
            RawNumericDifficulty = null,
            Debug = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
        };

    // High-difficulty-focus scope boundary result: estDiff is the dan label (no
    // "Invalid:" prefix), numericDifficulty is null — Mixed recognises this and
    // routes to Azusa (low difficulty) / other handling (high difficulty).
    private static RcEstimatorResult BuildScopeResult(
        string label, string code, double structuralNumeric, double rawNumeric,
        double? lnRatio, double? columnCount, object? notes, object? rows)
        => new()
        {
            Star = Math.Round(3.4 + 0.38 * structuralNumeric, 4, MidpointRounding.AwayFromZero),
            LnRatio = IsFin(lnRatio) ? lnRatio!.Value : 0,
            ColumnCount = IsFin(columnCount) ? columnCount!.Value : 0,
            EstDiff = label,
            NumericDifficulty = null,
            NumericDifficultyHint = code,
            Graph = null,
            RawNumericDifficulty = double.IsFinite(rawNumeric)
                ? Math.Round(rawNumeric, 4, MidpointRounding.AwayFromZero)
                : null,
            Debug = new Dictionary<string, object?>
            {
                ["code"] = code,
                ["message"] = $"Roxy RC scope {label} (structural {structuralNumeric.ToString("F2", CultureInfo.InvariantCulture)})",
                ["structuralNumeric"] = Fmt4(structuralNumeric),
                ["notes"] = notes,
                ["rows"] = rows,
            },
        };

    private static string NumericToRoxyRcLabel(double numeric)
    {
        if (double.IsFinite(numeric) && numeric > RoxyThetaHighNumeric) return RoxyThetaHighLabel;
        return RcDifficultyFormat.NumericToRcLabel(numeric);
    }

    // returns string "HR"/"EZ", or a boxed double, or null
    private static object? NormalizeRoxyOdFlag(EstimatorOptions options)
    {
        object? raw = options.ResolveOdFlagRaw();
        if (raw == null) return null;
        if (raw is string es && es == "") return null;
        if (raw is double rd) return double.IsFinite(rd) ? rd : (object?)null;
        if (raw is int ri) return (double)ri;

        string text = raw.ToString()!.Trim();
        if (text.Length == 0) return null;
        string upper = text.ToUpperInvariant();
        if (upper == "HR" || upper == "EZ") return upper;

        double numeric = JsNum.ParseFloat(text);
        return double.IsFinite(numeric) ? numeric : (object?)null;
    }

    private static double? SunnyJudgementWindowFromOd(double od)
    {
        if (!double.IsFinite(od)) return null;
        double raw = 0.3 * Math.Sqrt(Math.Max(1e-6, (64.5 - Math.Ceiling(od * 3)) / 500));
        return Math.Min(raw, 0.6 * (raw - 0.09) + 0.09);
    }

    private static double ResolveRoxyOd(double baseOd, object? odFlag)
    {
        double b = double.IsFinite(baseOd) ? baseOd : 8;
        if (odFlag == null) return b;
        if (odFlag is string s)
        {
            if (s == "HR") return 6.462 + 0.715 * b;
            if (s == "EZ") return -20.761 + 2.566 * b;
        }
        double numeric = ToDouble(odFlag);
        return double.IsFinite(numeric) ? numeric : b;
    }

    private sealed class OdDetails
    {
        public double Base;
        public double Neutral;
        public double Effective;
        public string? Flag;
        public double? BaseWindow;
        public double? EffectiveWindow;
        public double PressureRatio;
    }

    private static OdDetails ComputeOdDetails(double baseOd, object? odFlag)
    {
        double b = double.IsFinite(baseOd) ? baseOd : 8;
        double effective = ResolveRoxyOd(b, odFlag);
        double? baseWindow = SunnyJudgementWindowFromOd(RoxyOdNeutral);
        double? effectiveWindow = SunnyJudgementWindowFromOd(effective);
        double pressureRatio = baseWindow != null && effectiveWindow != null && effectiveWindow > 1e-9
            ? Clamp(baseWindow.Value / effectiveWindow.Value, 0.55, 1.85)
            : 1;

        return new OdDetails
        {
            Base = b,
            Neutral = RoxyOdNeutral,
            Effective = effective,
            Flag = odFlag == null ? null : (odFlag is string sf ? sf : JsString(ToDouble(odFlag))),
            BaseWindow = baseWindow,
            EffectiveWindow = effectiveWindow,
            PressureRatio = pressureRatio,
        };
    }

    private static double ComputeOdCorrection(OdDetails odDetails, double numeric)
    {
        if (odDetails.Flag == null) return 0;
        double ratio = odDetails.PressureRatio;
        if (!double.IsFinite(ratio) || Math.Abs(ratio - 1) < 1e-6) return 0;

        double difficultyGate = Gate(numeric, 6, 18);
        double highDifficultyGate = Gate(numeric, 14, 18.4);
        double correction = Math.Log(ratio) * (3.20 + (1.90 * difficultyGate) + (0.60 * highDifficultyGate));
        return Clamp(correction, -2.20, 2.20);
    }

    private static double Gate(double value, double min, double max)
        => Clamp(SafeDiv(value - min, max - min, 0), 0, 1);

    private static double InverseGate(double value, double min, double max)
        => Clamp(SafeDiv(max - value, max - min, 0), 0, 1);

    private static double StrainRate(double dt, double @base, double offset, double power)
    {
        double effective = Math.Max(16, dt + offset);
        double value = Math.Pow(@base / effective, power);
        return double.IsFinite(value) ? Math.Min(8, value) : 0;
    }

    private static double DecayState(double state, double input, double dt, double tau)
    {
        double delta = double.IsFinite(dt) && dt > 0 ? dt : 0;
        double decay = Math.Exp(-delta / tau);
        return state * decay + input;
    }

    private static double PiecewiseLinear(double value, double[][] knots)
    {
        if (!double.IsFinite(value) || knots.Length == 0) return value;
        if (value <= knots[0][0]) return knots[0][1];
        int last = knots.Length - 1;
        if (value >= knots[last][0]) return knots[last][1];

        for (int i = 0; i < last; i += 1)
        {
            double x0 = knots[i][0], y0 = knots[i][1];
            double x1 = knots[i + 1][0], y1 = knots[i + 1][1];
            if (value >= x0 && value <= x1)
            {
                return y0 + SafeDiv((value - x0) * (y1 - y0), x1 - x0, 0);
            }
        }
        return value;
    }

    private static double LinearMap(double value, double x0, double x1, double y0, double y1)
        => y0 + SafeDiv((value - x0) * (y1 - y0), x1 - x0, 0);

    private static double QuantileFromSorted(IReadOnlyList<double> sortedValues, double q)
    {
        if (sortedValues.Count == 0) return 0;
        double t = Clamp(q, 0, 1) * (sortedValues.Count - 1);
        int left = (int)Math.Floor(t);
        int right = Math.Min(sortedValues.Count - 1, left + 1);
        double w = t - left;
        return sortedValues[left] * (1 - w) + sortedValues[right] * w;
    }

    private static double Quantile(IEnumerable<double> values, double q)
    {
        var sorted = values.Where(double.IsFinite).ToList();
        sorted.Sort();
        return QuantileFromSorted(sorted, q);
    }

    private static double PowerMean(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        double acc = 0;
        foreach (var value in values)
        {
            acc += Math.Pow(Math.Max(0, value), p);
        }
        return Math.Pow(acc / values.Count, 1 / p);
    }

    private static double TopTailMean(IReadOnlyList<double> sortedValues, double ratio)
    {
        if (sortedValues.Count == 0) return 0;
        int count = Math.Max(1, (int)Math.Ceiling(sortedValues.Count * ratio));
        double sum = 0;
        for (int i = sortedValues.Count - count; i < sortedValues.Count; i += 1)
        {
            sum += sortedValues[i];
        }
        return sum / count;
    }

    private static int BitCount4(int mask)
    {
        int value = mask & 15;
        value = value - ((value >> 1) & 5);
        value = (value & 3) + ((value >> 2) & 3);
        return value;
    }

    private static double EntropyFromCounts(int[] counts, double total, double normalizer)
    {
        if (!double.IsFinite(total) || total <= 0) return 0;
        double entropy = 0;
        for (int i = 0; i < counts.Length; i += 1)
        {
            int count = counts[i];
            if (count <= 0) continue;
            double p = count / total;
            entropy -= p * Math.Log2(p);
        }
        return Clamp(entropy / normalizer, 0, 1);
    }

    private static string? NormalizeCvtFlag(string? cvtFlag)
    {
        string normalized = (cvtFlag ?? "").Trim().ToUpperInvariant();
        if (normalized == "HO" || normalized == "IN") return normalized;
        return null;
    }

    private static void ApplyConversionFlag(OsuFileParser parser, string? cvtFlag)
    {
        string? normalized = NormalizeCvtFlag(cvtFlag);
        if (normalized == "HO") parser.ModHO();
        else if (normalized == "IN") parser.ModIN();
    }

    private static List<string> ParseOsuCsvLine(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuote = false;
        for (int i = 0; i < line.Length; i += 1)
        {
            char ch = line[i];
            if (ch == '"')
            {
                inQuote = !inQuote;
                current.Append(ch);
            }
            else if (ch == ',' && !inQuote)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        parts.Add(current.ToString());
        return parts;
    }

    private static readonly System.Text.RegularExpressions.Regex NewlineRe =
        new(@"\r?\n", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static double? DetectFirstHitObjectTime(string? osuText)
    {
        var lines = NewlineRe.Split(osuText ?? "");
        string section = "";
        double first = double.PositiveInfinity;

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//")) continue;
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                section = trimmed;
                continue;
            }
            if (section != "[HitObjects]") continue;

            var parts = ParseOsuCsvLine(line);
            double time = parts.Count > 2 ? NumberJs(parts[2]) : double.NaN;
            if (double.IsFinite(time))
            {
                first = Math.Min(first, time);
            }
        }

        return double.IsFinite(first) ? first : (double?)null;
    }

    private sealed class TimingCanon
    {
        public string Text = "";
        public double SpeedRate;
        public double? FirstTime;
        public bool Applied;
    }

    private static TimingCanon CanonicalizeOsuTiming(string osuText, double speedRate)
    {
        double rate = speedRate;
        double? firstTime = DetectFirstHitObjectTime(osuText);
        if (!double.IsFinite(rate) || rate <= 0 || firstTime == null)
        {
            return new TimingCanon { Text = osuText, SpeedRate = rate, FirstTime = firstTime, Applied = false };
        }

        double firstScaled = firstTime.Value / rate;

        string ScaleTime(string raw)
        {
            double numeric = NumberJs(raw);
            if (!double.IsFinite(numeric)) return raw;
            double scaled = (numeric / rate) - firstScaled + RoxyCanonicalFirstObjectMs;
            return ((long)Math.Floor(scaled)).ToString(CultureInfo.InvariantCulture);
        }

        string ScaleBeatLength(string raw)
        {
            double numeric = NumberJs(raw);
            if (!double.IsFinite(numeric) || numeric <= 0) return raw;
            // PORT NOTE: JS `String(Number((n / rate).toFixed(12)))`. Round to 12
            // decimals then emit the shortest round-trip string.
            double rounded = Math.Round(numeric / rate, 12, MidpointRounding.AwayFromZero);
            return rounded.ToString("R", CultureInfo.InvariantCulture);
        }

        string section = "";
        var lines = NewlineRe.Split(osuText);
        var outLines = new string[lines.Length];
        for (int li = 0; li < lines.Length; li += 1)
        {
            string line = lines[li];
            string trimmed = line.Trim();
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                section = trimmed;
                outLines[li] = line;
                continue;
            }
            if (trimmed.Length == 0 || trimmed.StartsWith("//"))
            {
                outLines[li] = line;
                continue;
            }

            if (section == "[TimingPoints]")
            {
                var parts = ParseOsuCsvLine(line);
                if (parts.Count > 0)
                {
                    parts[0] = ScaleTime(parts[0]);
                    if (parts.Count > 1) parts[1] = ScaleBeatLength(parts[1]);
                    outLines[li] = string.Join(",", parts);
                    continue;
                }
            }

            if (section == "[Events]")
            {
                var parts = ParseOsuCsvLine(line);
                if ((parts.Count > 0 ? parts[0] : "").Trim() == "2" && parts.Count >= 3)
                {
                    parts[1] = ScaleTime(parts[1]);
                    parts[2] = ScaleTime(parts[2]);
                    outLines[li] = string.Join(",", parts);
                    continue;
                }
            }

            if (section == "[HitObjects]")
            {
                var parts = ParseOsuCsvLine(line);
                if (parts.Count >= 5)
                {
                    parts[2] = ScaleTime(parts[2]);
                    int type = double.IsFinite(NumberJs(parts[3])) ? (int)NumberJs(parts[3]) : 0;
                    if ((type & 128) != 0 && parts.Count > 5 && parts[5].Length > 0)
                    {
                        var objectParams = parts[5].Split(':').ToList();
                        objectParams[0] = ScaleTime(objectParams[0]);
                        parts[5] = string.Join(":", objectParams);
                    }
                    outLines[li] = string.Join(",", parts);
                    continue;
                }
            }

            outLines[li] = line;
        }

        return new TimingCanon
        {
            Text = string.Join("\n", outLines),
            SpeedRate = 1,
            FirstTime = firstTime,
            Applied = true,
        };
    }

    // ── tap rows ────────────────────────────────────────────────────────────

    private struct RoxyTap
    {
        public double T;
        public int C;
    }

    private sealed class RoxyRow
    {
        public double T;
        public int Mask;
        public int RowSize;
        public int LeftCount;
        public int RightCount;
        public int LeftMask;
        public int RightMask;

        // computeNpsRows: nps keyed by window ms — index 0..3 = [250,500,1000,4000]
        public double[] Nps = new double[4];
        public double DtRow;
    }

    private static (List<RoxyTap> Taps, List<RoxyRow> Rows) BuildTapRows(OsuParsedData parsed, double speedRate, double toleranceMs)
    {
        var taps = new List<RoxyTap>();
        var columns = parsed.Columns;
        var starts = parsed.NoteStarts;
        var types = parsed.NoteTypes;

        for (int i = 0; i < columns.Count; i += 1)
        {
            double rawTypeD = i < types.Count ? types[i] : double.NaN;
            int rawType = double.IsFinite(rawTypeD) ? (int)rawTypeD : 0;
            if ((rawType & 128) != 0) continue;

            double column = columns[i];
            double start = i < starts.Count ? starts[i] : double.NaN;
            if (!double.IsFinite(column) || column < 0 || column > 3 || !double.IsFinite(start))
            {
                continue;
            }

            taps.Add(new RoxyTap { T = start / speedRate, C = (int)column });
        }

        taps.Sort((a, b) =>
        {
            if (a.T != b.T) return a.T.CompareTo(b.T);
            return a.C.CompareTo(b.C);
        });

        var rows = new List<RoxyRow>();
        for (int i = 0; i < taps.Count;)
        {
            double startTime = taps[i].T;
            int j = i;
            int mask = 0;
            int rowSize = 0;

            while (j < taps.Count && Math.Abs(taps[j].T - startTime) <= toleranceMs)
            {
                int bit = 1 << taps[j].C;
                if ((mask & bit) == 0) rowSize += 1;
                mask |= bit;
                j += 1;
            }

            int leftMask = mask & 0b0011;
            int rightMask = mask & 0b1100;
            rows.Add(new RoxyRow
            {
                T = startTime,
                Mask = mask,
                RowSize = rowSize,
                LeftCount = BitCount4(leftMask),
                RightCount = BitCount4(rightMask),
                LeftMask = leftMask,
                RightMask = rightMask,
            });
            i = j;
        }

        return (taps, rows);
    }

    private sealed class RoxyActivity
    {
        public double InactiveMs;
        public double BreakCount;
        public double ActiveDurationSec;
        public double BreakDensity;
        public double AvgNps;
    }

    private static RoxyActivity ComputeActivityStats(List<RoxyRow> rows, int tapCount)
    {
        if (rows.Count < 2)
        {
            return new RoxyActivity
            {
                InactiveMs = 0,
                BreakCount = 0,
                ActiveDurationSec = 1,
                BreakDensity = 0,
                AvgNps = tapCount,
            };
        }

        double inactiveMs = 0;
        double breakCount = 0;
        for (int i = 1; i < rows.Count; i += 1)
        {
            double gap = rows[i].T - rows[i - 1].T;
            if (gap > 1000)
            {
                inactiveMs += gap - 1000;
                breakCount += 1;
            }
        }

        double durationMs = Math.Max(1, rows[rows.Count - 1].T - rows[0].T - inactiveMs);
        double activeDurationSec = durationMs / 1000;
        return new RoxyActivity
        {
            InactiveMs = inactiveMs,
            BreakCount = breakCount,
            ActiveDurationSec = activeDurationSec,
            BreakDensity = breakCount / Math.Max(activeDurationSec / 60, 1),
            AvgNps = tapCount / Math.Max(activeDurationSec, 1),
        };
    }

    private static void ComputeNpsRows(List<RoxyRow> rows, IReadOnlyList<double> tapTimes)
    {
        var windows = CfgNpsWindowsMs;
        var starts = new int[windows.Length];
        int end = 0;

        foreach (var row in rows)
        {
            while (end < tapTimes.Count && tapTimes[end] <= row.T + 1e-9)
            {
                end += 1;
            }

            for (int w = 0; w < windows.Length; w += 1)
            {
                double windowMs = windows[w];
                double minTime = row.T - windowMs;
                while (starts[w] < tapTimes.Count && tapTimes[starts[w]] <= minTime)
                {
                    starts[w] += 1;
                }
                row.Nps[w] = (end - starts[w]) / (windowMs / 1000);
            }
        }
    }

    private sealed class RoxySummary
    {
        public double Q50;
        public double Q75;
        public double Q90;
        public double Q97;
        public double TailMean;
        public double PowerMean;
        public double Aggregate;

        public double Get(string key) => key switch
        {
            "q50" => Q50,
            "q75" => Q75,
            "q90" => Q90,
            "q97" => Q97,
            "tailMean" => TailMean,
            "powerMean" => PowerMean,
            "aggregate" => Aggregate,
            _ => 0,
        };
    }

    private static RoxySummary SummarizeStream(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).ToList();
        sorted.Sort();
        if (sorted.Count == 0)
        {
            return new RoxySummary();
        }

        double q50 = QuantileFromSorted(sorted, 0.50);
        double q75 = QuantileFromSorted(sorted, 0.75);
        double q90 = QuantileFromSorted(sorted, 0.90);
        double q97 = QuantileFromSorted(sorted, 0.97);
        double tailMean = TopTailMean(sorted, 0.04);
        double pm = PowerMean(sorted, 2.4);
        double aggregate = (0.30 * q97)
            + (0.22 * q90)
            + (0.18 * tailMean)
            + (0.15 * q75)
            + (0.10 * pm)
            + (0.05 * q50);

        return new RoxySummary
        {
            Q50 = q50,
            Q75 = q75,
            Q90 = q90,
            Q97 = q97,
            TailMean = tailMean,
            PowerMean = pm,
            Aggregate = aggregate,
        };
    }

    // Shared by ComputeSectionAggregate (unchanged scalar collapse) and
    // BuildSectionCurve (new, exports the per-section values before they're
    // collapsed). Pure extraction — no behavior change to the existing scalar.
    private static Dictionary<int, double> BuildSectionMax(List<RoxyRow> rows, IReadOnlyList<double> localRaw)
    {
        var sectionMax = new Dictionary<int, double>();
        if (rows.Count == 0 || localRaw.Count == 0) return sectionMax;

        double firstTime = rows[0].T;
        double smoothedRaw = double.IsFinite(localRaw[0]) ? localRaw[0] : 0;
        for (int i = 0; i < rows.Count; i += 1)
        {
            int section = Math.Max(0, (int)Math.Floor((rows[i].T - firstTime) / CfgSectionMs));
            double raw = i < localRaw.Count && double.IsFinite(localRaw[i]) ? localRaw[i] : 0;
            smoothedRaw += CfgSectionEmaAlpha * (raw - smoothedRaw);
            double prev = sectionMax.TryGetValue(section, out var pv) ? pv : 0;
            sectionMax[section] = Math.Max(prev, smoothedRaw);
        }
        return sectionMax;
    }

    private static double ComputeSectionAggregate(List<RoxyRow> rows, IReadOnlyList<double> localRaw)
    {
        var sectionMax = BuildSectionMax(rows, localRaw);
        var values = sectionMax.Values.Where(v => double.IsFinite(v) && v > 0).ToList();
        values.Sort((a, b) => b.CompareTo(a));
        if (values.Count == 0) return 0;

        double weight = 1;
        double total = 0;
        double weightTotal = 0;
        foreach (var value in values)
        {
            total += value * weight;
            weightTotal += weight;
            weight *= CfgSectionDecay;
        }

        return SafeDiv(total, weightTotal, 0);
    }

    // Exports the 400ms section-peak curve BuildSectionMax computes on the way
    // to the single collapsed sectionAgg scalar — the "difficulty over time"
    // signal used to line up against the LeoBlack pattern-window timestamps
    // (LazerSR.DanCalculator.Patterns.FoundPattern). atMs is on Roxy's
    // canonicalized time axis (first analyzed row = ~canonicalFirstObjectMs);
    // callers reverse the canonicalization using the speedRateMode debug block
    // (originalFirstObjectMs / analysisSpeedRate / canonicalFirstObjectMs) to
    // line up with the original .osu timestamps the pattern pipeline uses.
    private static List<Dictionary<string, object?>> BuildSectionCurve(List<RoxyRow> rows, IReadOnlyList<double> localRaw)
    {
        var sectionMax = BuildSectionMax(rows, localRaw);
        var curve = new List<Dictionary<string, object?>>();
        if (rows.Count == 0) return curve;

        double firstTime = rows[0].T;
        foreach (var section in sectionMax.Keys.OrderBy(s => s))
        {
            curve.Add(new Dictionary<string, object?>
            {
                ["atMs"] = Fmt4(firstTime + section * CfgSectionMs),
                ["value"] = Fmt4(sectionMax[section]),
            });
        }
        return curve;
    }

    private sealed class RoxyStats
    {
        // spread of RoxyActivity
        public double InactiveMs;
        public double BreakCount;
        public double ActiveDurationSec;
        public double BreakDensity;
        public double AvgNps;

        public double ChordRate;
        public double ThreeRate;
        public double OverlapRate;
        public double RotationRate;
        public double SameHandQ10;
        public double FastJackRate;
        public double AnchorRate;
        public double AnchorImbalance;
        public double LeftLoad;
        public double RightLoad;
        public double HandBias;
        public double PeakToSustainGap;
        public double[] ColumnCounts = { 0, 0, 0, 0 };
        public double Rows;
        public double Taps;

        public double Get(string name) => name switch
        {
            "inactiveMs" => InactiveMs,
            "breakCount" => BreakCount,
            "activeDurationSec" => ActiveDurationSec,
            "breakDensity" => BreakDensity,
            "avgNps" => AvgNps,
            "chordRate" => ChordRate,
            "threeRate" => ThreeRate,
            "overlapRate" => OverlapRate,
            "rotationRate" => RotationRate,
            "sameHandQ10" => SameHandQ10,
            "fastJackRate" => FastJackRate,
            "anchorRate" => AnchorRate,
            "anchorImbalance" => AnchorImbalance,
            "leftLoad" => LeftLoad,
            "rightLoad" => RightLoad,
            "handBias" => HandBias,
            "peakToSustainGap" => PeakToSustainGap,
            "rows" => Rows,
            "taps" => Taps,
            _ => double.NaN,
        };

        public Dictionary<string, object?> ToDebugDict() => new()
        {
            ["inactiveMs"] = Fmt4(InactiveMs),
            ["breakCount"] = Fmt4(BreakCount),
            ["activeDurationSec"] = Fmt4(ActiveDurationSec),
            ["breakDensity"] = Fmt4(BreakDensity),
            ["avgNps"] = Fmt4(AvgNps),
            ["chordRate"] = Fmt4(ChordRate),
            ["threeRate"] = Fmt4(ThreeRate),
            ["overlapRate"] = Fmt4(OverlapRate),
            ["rotationRate"] = Fmt4(RotationRate),
            ["sameHandQ10"] = Fmt4(SameHandQ10),
            ["fastJackRate"] = Fmt4(FastJackRate),
            ["anchorRate"] = Fmt4(AnchorRate),
            ["anchorImbalance"] = Fmt4(AnchorImbalance),
            ["leftLoad"] = Fmt4(LeftLoad),
            ["rightLoad"] = Fmt4(RightLoad),
            ["handBias"] = Fmt4(HandBias),
            ["peakToSustainGap"] = Fmt4(PeakToSustainGap),
            ["columnCounts"] = ColumnCounts,
            ["rows"] = Fmt4(Rows),
            ["taps"] = Fmt4(Taps),
        };
    }

    private sealed class RoxyCurve
    {
        public Dictionary<string, List<double>> Streams = new();
        public Dictionary<string, RoxySummary> StreamSummaries = new();
        public double WeightedAgg;
        public double SectionAgg;
        public List<double> LocalRaw = new();
        public RoxyStats Stats = new();
    }

    private static double MaxOf(double[] arr)
    {
        double m = double.NegativeInfinity;
        foreach (var v in arr) m = Math.Max(m, v);
        return m;
    }

    private static double MinOf(double[] arr)
    {
        double m = double.PositiveInfinity;
        foreach (var v in arr) m = Math.Min(m, v);
        return m;
    }

    private static RoxyCurve ComputeRoxyCurve(List<RoxyRow> rows, List<RoxyTap> taps, RoxyActivity activity)
    {
        var streams = new Dictionary<string, List<double>>();
        var stateBurst = new double[StreamNames.Length];
        var stateStamina = new double[StreamNames.Length];
        foreach (var name in StreamNames)
        {
            streams[name] = new List<double>();
        }

        var lastColumnTime = new[] { double.NaN, double.NaN, double.NaN, double.NaN };
        var lastHandTime = new[] { double.NaN, double.NaN };
        var prevHandMask = new[] { 0, 0 };
        var handStamina = new[] { 0.0, 0.0 };
        var columnCounts = new[] { 0.0, 0.0, 0.0, 0.0 };
        var dtSameValues = new List<double>();
        var dtHandValues = new List<double>();
        var localRaw = new List<double>();

        var maskCounts = new int[16];
        var transitionCounts = new int[256];
        var entropyQueue = new List<(double T, int Mask, int TransitionCode)>();
        int entropyBack = 0;
        double maskTotal = 0;
        double transitionTotal = 0;

        double prevRowTime = rows.Count > 0 ? rows[0].T - 1000 : 0;
        double prevDtRow = 1000;
        int prevMask = 0;
        double leftLoad = 0;
        double rightLoad = 0;
        double chordRows = 0;
        double threeRows = 0;
        double overlapSum = 0;
        double rotationSum = 0;
        double eligibleHandEvents = 0;
        double anchorRowStrengthSum = 0;
        double fastJackStrengthSum = 0;

        for (int i = 0; i < rows.Count; i += 1)
        {
            var row = rows[i];
            double dtRow = i > 0 ? Math.Max(1, row.T - prevRowTime) : 1000;
            row.DtRow = dtRow;

            int leftMask = row.LeftMask;
            int rightMask = row.RightMask;
            var handMasks = new[] { leftMask, rightMask };
            var dtHand = new[] { double.NaN, double.NaN };
            var rotation = new[] { 0.0, 0.0 };
            int overlapEvents = 0;

            for (int h = 0; h < 2; h += 1)
            {
                if (handMasks[h] == 0) continue;
                if (double.IsFinite(lastHandTime[h]))
                {
                    dtHand[h] = Math.Max(1, row.T - lastHandTime[h]);
                    dtHandValues.Add(dtHand[h]);
                    eligibleHandEvents += 1;
                    if ((handMasks[h] & prevHandMask[h]) == 0 && prevHandMask[h] != 0)
                    {
                        rotation[h] = 1;
                        rotationSum += 1;
                    }
                    if ((handMasks[h] & prevHandMask[h]) != 0)
                    {
                        overlapEvents += 1;
                    }
                }
            }

            double sameHandOverlap = overlapEvents / 2.0;
            overlapSum += sameHandOverlap;

            var dtSame = new[] { double.NaN, double.NaN, double.NaN, double.NaN };
            double jackMax = 0;
            double anchorRow = 0;
            for (int c = 0; c < 4; c += 1)
            {
                if ((row.Mask & (1 << c)) == 0) continue;
                columnCounts[c] += 1;
                if (double.IsFinite(lastColumnTime[c]))
                {
                    dtSame[c] = Math.Max(1, row.T - lastColumnTime[c]);
                    dtSameValues.Add(dtSame[c]);
                    anchorRow = Math.Max(anchorRow, InverseGate(dtSame[c], 220, 260));
                    fastJackStrengthSum += InverseGate(dtSame[c], 120, 150);
                    jackMax = Math.Max(jackMax, StrainRate(dtSame[c], 185, 35, 1.18));
                }
            }
            anchorRowStrengthSum += anchorRow;

            leftLoad += row.LeftCount;
            rightLoad += row.RightCount;
            if (row.RowSize >= 2) chordRows += 1;
            if (row.RowSize >= 3) threeRows += 1;

            maskCounts[row.Mask] += 1;
            maskTotal += 1;
            int transitionCode = -1;
            if (i > 0)
            {
                transitionCode = (prevMask << 4) | row.Mask;
                transitionCounts[transitionCode] += 1;
                transitionTotal += 1;
            }
            entropyQueue.Add((row.T, row.Mask, transitionCode));
            while (entropyBack < entropyQueue.Count && entropyQueue[entropyBack].T < row.T - CfgEntropyWindowMs)
            {
                var old = entropyQueue[entropyBack];
                maskCounts[old.Mask] -= 1;
                maskTotal -= 1;
                if (old.TransitionCode >= 0)
                {
                    transitionCounts[old.TransitionCode] -= 1;
                    transitionTotal -= 1;
                }
                entropyBack += 1;
            }

            double entropy750 = EntropyFromCounts(maskCounts, maskTotal, 4);
            double transitionEntropy750 = EntropyFromCounts(transitionCounts, transitionTotal, 8);
            double rowChord = (row.RowSize - 1) / 3.0;
            double sameHandChord = (Math.Max(0, row.LeftCount - 1) + Math.Max(0, row.RightCount - 1)) / 2.0;

            var handRates = new List<double>();
            for (int h = 0; h < 2; h += 1)
            {
                if (handMasks[h] == 0) continue;
                double handDt = double.IsFinite(dtHand[h]) ? dtHand[h] : 1000;
                handRates.Add(StrainRate(handDt, 180, 40, 1.08));
                handStamina[h] = DecayState(handStamina[h], StrainRate(handDt, 180, 40, 1.08), handDt, 8000);
            }
            for (int h = 0; h < 2; h += 1)
            {
                if (handMasks[h] != 0) continue;
                handStamina[h] = DecayState(handStamina[h], 0, dtRow, 8000);
            }

            double handMax = handRates.Count > 0 ? handRates.Max() : 0;
            double handMean = handRates.Count > 0 ? handRates.Sum() / handRates.Count : 0;
            double speedIn = (0.55 * StrainRate(dtRow, 155, 30, 1.06))
                + (0.30 * handMax)
                + (0.15 * handMean);
            double jackIn = jackMax * (1 + 0.20 * rowChord + 0.15 * anchorRow);
            double handIn = 0;
            for (int h = 0; h < 2; h += 1)
            {
                if (handMasks[h] == 0) continue;
                double handDt = double.IsFinite(dtHand[h]) ? dtHand[h] : 1000;
                handIn = Math.Max(
                    handIn,
                    (0.70 * StrainRate(handDt, 180, 38, 1.10))
                        + (0.30 * rotation[h] * StrainRate(handDt, 205, 45, 1.05)));
            }

            double body = Math.Max(0, row.RowSize - 2) * StrainRate(dtRow, 150, 80, 0.85);
            // PORT NOTE: `chordIn` is computed by the JS source but never consumed
            // (not part of `inputs`). Kept verbatim; it has no side effects.
            double chordIn = rowChord * (1 + 0.18 * speedIn) + 0.22 * sameHandChord + body;
            _ = chordIn;
            double chordjackIn = rowChord * ((0.55 * jackIn) + (0.30 * sameHandOverlap) + (0.15 * handIn));
            double rhythmChaos = i > 0
                ? Math.Min(2, Math.Abs(Math.Log2((dtRow + 24) / (prevDtRow + 24)))) / 2
                : 0;
            double techIn = (0.32 * rhythmChaos)
                + (0.24 * entropy750)
                + (0.24 * transitionEntropy750)
                + (0.20 * (row.Mask != prevMask ? 1 : 0));
            double maxHandStamina = Math.Max(handStamina[0], handStamina[1]);
            double staminaIn = (0.40 * Math.Log(1 + row.Nps[2]) / Math.Log(24))
                + (0.35 * Math.Log(1 + row.Nps[3]) / Math.Log(24))
                + (0.25 * maxHandStamina);
            double courseIn = staminaIn
                * Gate(activity.ActiveDurationSec, 90, 300)
                * (1 - 0.25 * Gate(activity.BreakDensity, 0.006, 0.018));

            // inputs indexed to match StreamNames order
            var inputs = new[] { speedIn, handIn, jackIn, chordjackIn, techIn, staminaIn, courseIn };

            for (int s = 0; s < StreamNames.Length; s += 1)
            {
                double input = inputs[s];
                stateBurst[s] = DecayState(stateBurst[s], input, dtRow, StreamBurstTau[s]);
                stateStamina[s] = DecayState(stateStamina[s], input, dtRow, StreamStaminaTau[s]);
                double value = StreamBurstMix[s] * stateBurst[s]
                    + (1 - StreamBurstMix[s]) * stateStamina[s];
                streams[StreamNames[s]].Add(value);
            }

            double raw = 0;
            for (int s = 0; s < StreamNames.Length; s += 1)
            {
                var list = streams[StreamNames[s]];
                raw += StreamWeights[s] * list[list.Count - 1];
            }
            localRaw.Add(raw);

            for (int c = 0; c < 4; c += 1)
            {
                if ((row.Mask & (1 << c)) != 0) lastColumnTime[c] = row.T;
            }
            for (int h = 0; h < 2; h += 1)
            {
                if (handMasks[h] != 0)
                {
                    lastHandTime[h] = row.T;
                    prevHandMask[h] = handMasks[h];
                }
            }

            prevRowTime = row.T;
            prevDtRow = dtRow;
            prevMask = row.Mask;
        }

        var streamSummaries = new Dictionary<string, RoxySummary>();
        double weightedAgg = 0;
        for (int s = 0; s < StreamNames.Length; s += 1)
        {
            var summary = SummarizeStream(streams[StreamNames[s]]);
            streamSummaries[StreamNames[s]] = summary;
            weightedAgg += StreamWeights[s] * summary.Aggregate;
        }

        double sectionAgg = ComputeSectionAggregate(rows, localRaw);
        double q97Local = Quantile(localRaw, 0.97);
        double q75Local = Quantile(localRaw, 0.75);
        double peakToSustainGap = Clamp(SafeDiv(q97Local - q75Local, Math.Max(q97Local, 1e-6), 0), 0, 1);
        int finiteSameCount = dtSameValues.Count;
        double maxColumnCount = MaxOf(columnCounts);
        double minColumnCount = MinOf(columnCounts);

        var stats = new RoxyStats
        {
            InactiveMs = activity.InactiveMs,
            BreakCount = activity.BreakCount,
            ActiveDurationSec = activity.ActiveDurationSec,
            BreakDensity = activity.BreakDensity,
            AvgNps = activity.AvgNps,
            ChordRate = chordRows / Math.Max(rows.Count, 1),
            ThreeRate = threeRows / Math.Max(rows.Count, 1),
            OverlapRate = overlapSum / Math.Max(rows.Count, 1),
            RotationRate = rotationSum / Math.Max(eligibleHandEvents, 1),
            SameHandQ10 = Quantile(dtHandValues, 0.10),
            FastJackRate = fastJackStrengthSum / Math.Max(finiteSameCount, 1),
            AnchorRate = anchorRowStrengthSum / Math.Max(rows.Count, 1),
            AnchorImbalance = (maxColumnCount - minColumnCount) / Math.Max(taps.Count, 1),
            LeftLoad = leftLoad,
            RightLoad = rightLoad,
            HandBias = Math.Abs(leftLoad - rightLoad) / Math.Max(Math.Max(leftLoad, rightLoad), 1e-6),
            PeakToSustainGap = peakToSustainGap,
            ColumnCounts = columnCounts,
            Rows = rows.Count,
            Taps = taps.Count,
        };

        return new RoxyCurve
        {
            Streams = streams,
            StreamSummaries = streamSummaries,
            WeightedAgg = weightedAgg,
            SectionAgg = sectionAgg,
            LocalRaw = localRaw,
            Stats = stats,
        };
    }

    private sealed class RoxyCorrections
    {
        public double LowCj;
        public double HighStream;
        public double HighCjDamp;
        public double CourseBreakDamp;
        public double CourseSustainLift;
        public double DenseJsLift;
        public double DenseJsDamp;
        public double AnchorLift;
        public double HandBiasLift;
        public double RawSum;
        public double Total;

        public double Get(string name) => name switch
        {
            "lowCj" => LowCj,
            "highStream" => HighStream,
            "highCjDamp" => HighCjDamp,
            "courseBreakDamp" => CourseBreakDamp,
            "courseSustainLift" => CourseSustainLift,
            "denseJsLift" => DenseJsLift,
            "denseJsDamp" => DenseJsDamp,
            "anchorLift" => AnchorLift,
            "handBiasLift" => HandBiasLift,
            "rawSum" => RawSum,
            "total" => Total,
            _ => double.NaN,
        };

        public Dictionary<string, object?> ToDebugDict() => new()
        {
            ["lowCj"] = Fmt4(LowCj),
            ["highStream"] = Fmt4(HighStream),
            ["highCjDamp"] = Fmt4(HighCjDamp),
            ["courseBreakDamp"] = Fmt4(CourseBreakDamp),
            ["courseSustainLift"] = Fmt4(CourseSustainLift),
            ["denseJsLift"] = Fmt4(DenseJsLift),
            ["denseJsDamp"] = Fmt4(DenseJsDamp),
            ["anchorLift"] = Fmt4(AnchorLift),
            ["handBiasLift"] = Fmt4(HandBiasLift),
            ["rawSum"] = Fmt4(RawSum),
            ["total"] = Fmt4(Total),
        };
    }

    // Empirically measured 2026-09-15 median per-axis weighted contribution
    // (StreamWeights[s] * StreamSummaries[s].Aggregate) across 418 Roxy-routed
    // 4K RC charts (random OsuScoreModel sample, 9000 4K files scanned). Needed
    // because the 7 axes' raw accumulator scales are NOT comparable directly —
    // stamina/course decay on 10-170x longer time constants than the other 5
    // axes (StreamBurstTau/StreamStaminaTau above), so their raw magnitude
    // structurally dominates any unnormalized share regardless of the chart's
    // actual content (measured: naive share averaged 0.51/0.46 stamina/course,
    // the other 5 axes combined under 0.04, and every single sampled chart's
    // top axis was one of those two). A pure tau-ratio correction was tried
    // first and failed harder (stamina alone won 418/418 — a second, nested
    // decay accumulator inside staminaIn's own formula adds scale the outer
    // tau ratio doesn't account for). Dividing each axis's contribution by its
    // own corpus-typical scale before comparing axes is what actually produced
    // a balanced spread (avg share 0.07-0.20 across all 7, top axis varying by
    // chart). Order matches StreamNames: speed, handStream, jack, chordjack,
    // tech, stamina, course.
    private static readonly double[] AxisShareReference =
        { 18.009, 18.614, 12.225, 3.061, 10.590, 1658.498, 3742.702 };

    // Per-axis (StreamNames) share of the primary raw structural signal — see
    // AxisShareReference above for why raw contributions are normalized before
    // comparing — plus an approximate "as-if this axis alone were the whole
    // chart" raw-dan-equivalent (on the real calibrated scale, NOT normalized),
    // computed by running that axis's own raw weighted contribution through the
    // SAME log compression / linear map / isotonic knots the composite signal
    // uses. Corrections and the meta-ridge model are NOT applied here — those
    // are whole-chart context (OD, reference-gap, marathon...) that has no
    // per-axis meaning — so this is a rough display-only ranking signal, not a
    // real per-axis dan verdict. Consumed by ChartClassifier only when this
    // Roxy result actually wins the routing (numericDifficultyHint ==
    // "roxy-meta-ridge-v3"), for the dan-info widget's pattern-axis summary.
    private static Dictionary<string, object?> BuildAxisBreakdown(RoxyCurve curve)
    {
        var contribs = new double[StreamNames.Length];
        var normalized = new double[StreamNames.Length];
        double normalizedTotal = 0;
        for (int s = 0; s < StreamNames.Length; s += 1)
        {
            double aggregate = curve.StreamSummaries.TryGetValue(StreamNames[s], out var summary) ? summary.Aggregate : 0;
            double contrib = Math.Max(0, StreamWeights[s] * aggregate);
            contribs[s] = contrib;
            double n = AxisShareReference[s] > 0 ? contrib / AxisShareReference[s] : 0;
            normalized[s] = n;
            normalizedTotal += n;
        }

        var result = new Dictionary<string, object?>();
        for (int s = 0; s < StreamNames.Length; s += 1)
        {
            double share = normalizedTotal > 0 ? normalized[s] / normalizedTotal : 0;
            double logRaw = Math.Log(1 + contribs[s]);
            double preNumeric = Clamp(LinearMap(logRaw, CfgRawMapP02, CfgRawMapP98, -2, 20), -2.5, 21);
            double localRawDan = Clamp(PiecewiseLinear(preNumeric, IsotonicKnots), -2, 20);
            result[StreamNames[s]] = new Dictionary<string, object?>
            {
                ["share"] = Fmt4(share),
                ["localRawDan"] = Fmt4(localRawDan),
            };
        }
        return result;
    }

    private static RoxyCorrections ComputeCorrections(RoxyStats stats)
    {
        double lowCj = 0.75
            * Gate(stats.ChordRate, 0.48, 0.68)
            * Gate(stats.OverlapRate, 0.75, 1.25)
            * (1 - Gate(stats.AvgNps, 19, 23))
            * (1 - Gate(stats.AnchorImbalance, 0.06, 0.12));
        double highStream = 0.65
            * Gate(stats.RotationRate, 0.68, 0.86)
            * InverseGate(stats.SameHandQ10, 100, 130)
            * (1 - Gate(stats.ChordRate, 0.25, 0.42))
            * (1 - Gate(stats.OverlapRate, 0.65, 0.95));
        double highCjDamp = -0.55
            * Gate(stats.ChordRate, 0.78, 0.90)
            * Gate(stats.ThreeRate, 0.18, 0.38)
            * (1 - Gate(stats.FastJackRate, 0.55, 0.75));
        double courseBreakDamp = -0.70
            * Gate(stats.ActiveDurationSec, 240, 480)
            * Gate(stats.BreakDensity, 0.006, 0.018)
            * Gate(stats.PeakToSustainGap, 0.35, 0.75)
            * InverseGate(stats.AvgNps, 12, 18);
        double courseSustainLift = 0.30
            * Gate(stats.ActiveDurationSec, 240, 600)
            * InverseGate(stats.BreakDensity, 0.004, 0.012)
            * InverseGate(stats.PeakToSustainGap, 0.15, 0.45)
            * Gate(stats.AvgNps, 15, 21);
        double denseJsLift = 0.35
            * Gate(stats.ChordRate, 0.35, 0.52)
            * Gate(stats.RotationRate, 0.62, 0.80)
            * InverseGate(stats.SameHandQ10, 90, 125);
        double denseJsDamp = -0.25
            * Gate(stats.ChordRate, 0.58, 0.75)
            * InverseGate(stats.RotationRate, 0.45, 0.62);
        double anchorLift = 0.30
            * Gate(stats.AnchorRate, 0.18, 0.38)
            * Gate(stats.FastJackRate, 0.25, 0.55)
            * (1 - Gate(stats.ChordRate, 0.65, 0.85));
        double handBiasLift = 0.25
            * Gate(stats.HandBias, 0.25, 0.55)
            * Gate(stats.AvgNps, 12, 20);
        double rawSum = lowCj
            + highStream
            + highCjDamp
            + courseBreakDamp
            + courseSustainLift
            + denseJsLift
            + denseJsDamp
            + anchorLift
            + handBiasLift;
        double total = Clamp(rawSum, -CfgCorrectionClamp, CfgCorrectionClamp);

        return new RoxyCorrections
        {
            LowCj = lowCj,
            HighStream = highStream,
            HighCjDamp = highCjDamp,
            CourseBreakDamp = courseBreakDamp,
            CourseSustainLift = courseSustainLift,
            DenseJsLift = denseJsLift,
            DenseJsDamp = denseJsDamp,
            AnchorLift = anchorLift,
            HandBiasLift = handBiasLift,
            RawSum = rawSum,
            Total = total,
        };
    }

    private sealed class RoxyNumericDetails
    {
        public double RawAgg;
        public double LogRaw;
        public double PreNumeric;
        public RoxyCorrections Corrections = new();
        public double RawNumeric;
        public double Numeric;
    }

    private static RoxyNumericDetails ComputeRoxyNumeric(RoxyCurve curve)
    {
        double rawAgg = (0.80 * curve.WeightedAgg) + (0.20 * curve.SectionAgg);
        double logRaw = Math.Log(1 + Math.Max(0, rawAgg));
        double preNumeric = Clamp(
            LinearMap(logRaw, CfgRawMapP02, CfgRawMapP98, -2, 20),
            -2.5,
            21);
        var corrections = ComputeCorrections(curve.Stats);
        double rawNumeric = preNumeric + corrections.Total;
        double numeric = Clamp(PiecewiseLinear(rawNumeric, IsotonicKnots), -2, 20);

        return new RoxyNumericDetails
        {
            RawAgg = rawAgg,
            LogRaw = logRaw,
            PreNumeric = preNumeric,
            Corrections = corrections,
            RawNumeric = rawNumeric,
            Numeric = numeric,
        };
    }

    // ── meta model ──────────────────────────────────────────────────────────

    private static double ToFeatureNumber(double value) => double.IsFinite(value) ? value : 0;
    private static double ToFeatureNumber(double? value) => value.HasValue && double.IsFinite(value.Value) ? value.Value : 0;

    private static double RoundedFeature(double value)
        => double.IsFinite(value) ? Math.Round(value, 4, MidpointRounding.AwayFromZero) : 0;

    private static double QuantizeFeature(double value, double step)
    {
        if (!double.IsFinite(value) || !double.IsFinite(step) || step <= 0) return 0;
        double rounded = Math.Floor(value / step + 0.5) * step;
        return Math.Round(rounded, 4, MidpointRounding.AwayFromZero);
    }

    private static double ComputeReferenceGapCorrection(
        Dictionary<string, double?> referencePredictions, double structuralNumeric, double baseNumeric, RoxyStats stats)
    {
        double @base = baseNumeric;
        if (!double.IsFinite(@base)) return 0;

        double? azusaRaw = referencePredictions.TryGetValue("Azusa", out var az) ? az : null;
        double? danielRaw = referencePredictions.TryGetValue("Daniel", out var dn) ? dn : null;
        bool hasAzusa = azusaRaw != null && double.IsFinite(ToDouble(azusaRaw.Value));
        bool hasDaniel = danielRaw != null && double.IsFinite(ToDouble(danielRaw.Value));
        if (!hasAzusa && !hasDaniel) return 0;

        double azusa = hasAzusa ? azusaRaw!.Value : @base;
        double daniel = hasDaniel ? danielRaw!.Value : @base;
        double structural = double.IsFinite(structuralNumeric) ? structuralNumeric : @base;
        double azusaGap = azusa - @base;
        double danielGap = daniel - @base;
        double structuralGap = structural - @base;
        double chordRate = ToFeatureNumber(stats.ChordRate);
        double rotationRate = ToFeatureNumber(stats.RotationRate);
        double sameHandQ10 = ToFeatureNumber(stats.SameHandQ10);
        double avgNpsGate = Gate(ToFeatureNumber(stats.AvgNps), 12, 24);
        var features = new[]
        {
            azusaGap,
            danielGap,
            structuralGap,
            Math.Abs(azusaGap),
            Math.Abs(danielGap),
            azusaGap * chordRate,
            azusaGap * rotationRate,
            azusaGap / (sameHandQ10 + 1),
            danielGap * chordRate,
            structuralGap * avgNpsGate,
        };

        double value = RoxyReferenceGapBeta[0];
        for (int i = 0; i < features.Length; i += 1)
        {
            double scale = RoxyReferenceGapFeatureScale[i];
            if (scale == 0) scale = 1; // JS `|| 1`
            value += RoxyReferenceGapBeta[i + 1]
                * ((features[i] - RoxyReferenceGapFeatureMean[i]) / scale);
        }
        return Clamp(value, -0.30, 0.30) * RoxyReferenceGapCorrectionScale;
    }

    private static double ComputeAzusaHighGapLift(Dictionary<string, double?> referencePredictions, double baseNumeric)
    {
        double @base = baseNumeric;
        double azusa = referencePredictions.TryGetValue("Azusa", out var az) ? ToDouble(az) : double.NaN;
        if (!double.IsFinite(@base) || !double.IsFinite(azusa)) return 0;
        return 0.05 * Gate(azusa - @base, 0.35, 0.95);
    }

    // Azusa mean fusion: average Roxy's final numeric with Azusa's independent
    // prediction at a fixed weight. The principle is variance reduction from
    // averaging two approximately-unbiased estimators (NOT extra "trust" in
    // Azusa). Roxy is already high-difficulty focused (scope 11~17); low
    // difficulty never reaches here, so no difficulty gate is needed.
    private static double ComputeAzusaFusion(Dictionary<string, double?> referencePredictions, double finalNumeric)
    {
        double azusa = referencePredictions.TryGetValue("Azusa", out var az) ? ToDouble(az) : double.NaN;
        double @base = finalNumeric;
        if (!double.IsFinite(azusa) || !double.IsFinite(@base)) return finalNumeric;
        double fused = @base + (azusa - @base) * RoxyAzusaFusionWeight;
        return Math.Round(fused, 2, MidpointRounding.AwayFromZero);
    }

    private static double? ResultNumeric(double? numericDifficulty, string? estDiff)
    {
        if (numericDifficulty.HasValue)
        {
            double numeric = numericDifficulty.Value;
            if (double.IsFinite(numeric)) return numeric;
        }
        return RcDifficultyFormat.RcLabelToNumeric(estDiff);
    }

    private static double? ResultNumeric(SunnyResult r) => ResultNumeric(r.NumericDifficulty, r.EstDiff);
    private static double? ResultNumeric(RcEstimatorResult r) => ResultNumeric(r.NumericDifficulty, r.EstDiff);

    // JS safeReference: run() or a fallback object on null/throw. Our C# shims
    // never return null, so only the catch path differs. PORT NOTE.
    private static SunnyResult SafeSunny(Func<SunnyResult> run)
    {
        try { return run() ?? SunnyFallback("Invalid: Empty reference result"); }
        catch { return SunnyFallback("Invalid: Reference estimator failed"); }
    }

    private static RcEstimatorResult SafeAzusa(Func<RcEstimatorResult> run)
    {
        try { return run() ?? AzusaFallback("Invalid: Empty reference result"); }
        catch { return AzusaFallback("Invalid: Reference estimator failed"); }
    }

    private static SunnyResult SunnyFallback(string estDiff)
        => new() { Star = double.NaN, EstDiff = estDiff, NumericDifficulty = null };

    private static RcEstimatorResult AzusaFallback(string estDiff)
        => new() { Star = double.NaN, EstDiff = estDiff, NumericDifficulty = null };

    private static Dictionary<string, double?> StabilizeHighReferencePredictions(
        Dictionary<string, double?> predictions, double structuralNumeric)
    {
        double azusa = predictions.TryGetValue("Azusa", out var azv) ? ToDouble(azv) : double.NaN;
        if (!double.IsFinite(azusa) || azusa < 16.8) return predictions;

        double roxy = predictions.TryGetValue("Roxy", out var rxv) ? ToDouble(rxv) : double.NaN;
        double structural = structuralNumeric;
        var finiteHighReferences = new[] { "Azusa", "Sunny", "Daniel" }
            .Select(algo => predictions.TryGetValue(algo, out var v) ? v : null)
            .Where(v => v != null && double.IsFinite(ToDouble(v.Value)))
            .Select(v => v!.Value)
            .OrderBy(v => v)
            .ToList();
        double referenceMedian = finiteHighReferences.Count > 0
            ? finiteHighReferences[finiteHighReferences.Count / 2]
            : azusa;
        double support = Math.Max(
            double.IsFinite(roxy) ? roxy : double.NegativeInfinity,
            double.IsFinite(structural) ? structural : double.NegativeInfinity);
        double fallback = Math.Max(
            Math.Max(double.IsFinite(support) ? support : azusa - 0.35, azusa - 0.35),
            referenceMedian - 0.10);
        var stabilized = new Dictionary<string, double?>(predictions);

        foreach (var algo in new[] { "Sunny", "Daniel" })
        {
            double? value = stabilized.TryGetValue(algo, out var sv) ? sv : null;
            if (value == null || !double.IsFinite(ToDouble(value.Value)))
            {
                stabilized[algo] = fallback;
            }
        }

        return stabilized;
    }

    private sealed class ReferenceDetails
    {
        public Dictionary<string, double?> Predictions = new();
        public (double[] Times, double[] Values)? Graph;
    }

    private static ReferenceDetails BuildReferencePredictions(string osuText, EstimatorOptions options, double structuralNumeric)
    {
        bool wantsGraph = options.WithGraph;

        var precomputedSunnyResult = options.PrecomputedSunnyResult;
        var precomputedDanielResult = options.PrecomputedDanielResult;

        double analysisSpeedRate = options.SpeedRate ?? 1.0;
        // referenceOptions = { ...options, withGraph: false }
        SunnyResult sunnyResult = precomputedSunnyResult != null && (!wantsGraph || precomputedSunnyResult.Graph != null)
            ? precomputedSunnyResult
            : SafeSunny(() => SunnyShim.Run(
                osuText, analysisSpeedRate, AsOdDouble(options.OdFlag), options.CvtFlag, wantsGraph));
        SunnyResult danielResult = precomputedDanielResult
            ?? SafeSunny(() => DanielEstimator.RunDanielEstimatorFromText(osuText, options.With(withGraph: false), null));
        var localSunny = sunnyResult;
        var localDaniel = danielResult;
        RcEstimatorResult azusaResult = SafeAzusa(() => AzusaEstimator.RunAzusaEstimatorFromText(
            osuText,
            options.With(withGraph: wantsGraph,
                precomputedSunnyResult: localSunny,
                precomputedDanielResult: localDaniel),
            null));

        var predictions = StabilizeHighReferencePredictions(new Dictionary<string, double?>
        {
            ["Azusa"] = ResultNumeric(azusaResult),
            ["Sunny"] = ResultNumeric(sunnyResult),
            ["Daniel"] = ResultNumeric(danielResult),
            ["Roxy"] = double.IsFinite(structuralNumeric) ? structuralNumeric : (double?)null,
        }, structuralNumeric);
        // ROXY_DISABLED_META_REFERENCES = new Set(["Sunny"])
        predictions["Sunny"] = null;

        return new ReferenceDetails
        {
            Predictions = predictions,
            Graph = wantsGraph ? azusaResult.Graph : null,
        };
    }

    private static void AddMeta(Dictionary<string, double> map, string name, double value)
        => map[name] = ToFeatureNumber(value);

    private static double StreamAggregate(Dictionary<string, RoxySummary> streams, string name)
        => streams.TryGetValue(name, out var s) ? ToFeatureNumber(s.Aggregate) : 0;

    private static double[] BuildRoxyMetaFeatures(
        Dictionary<string, double?> referencePredictions, RoxyNumericDetails numericDetails,
        RoxyCurve curve, double structuralNumeric)
    {
        var map = new Dictionary<string, double>();
        var finitePredictions = new List<double>();
        var normalizedPredictions = new Dictionary<string, double>();
        var fallbackCandidates = new List<double>();

        foreach (var algo in RoxyMetaAlgos)
        {
            double? rawValue = referencePredictions.TryGetValue(algo, out var rv) ? rv : null;
            if (rawValue != null && double.IsFinite(rawValue.Value))
            {
                fallbackCandidates.Add(QuantizeFeature(rawValue.Value, RoxyReferenceBucketSize));
            }
        }
        if (fallbackCandidates.Count == 0 && double.IsFinite(structuralNumeric))
        {
            fallbackCandidates.Add(QuantizeFeature(structuralNumeric, RoxyReferenceBucketSize));
        }
        fallbackCandidates.Sort();
        double fallbackPrediction = fallbackCandidates.Count == 0
            ? 0
            : fallbackCandidates[fallbackCandidates.Count / 2];

        foreach (var algo in RoxyMetaAlgos)
        {
            double? value = referencePredictions.TryGetValue(algo, out var v) ? v : null;
            bool hasValue = value.HasValue && double.IsFinite(value.Value);
            double normalizedValue = hasValue
                ? QuantizeFeature(value!.Value, RoxyReferenceBucketSize)
                : fallbackPrediction;
            normalizedPredictions[algo] = normalizedValue;
            AddMeta(map, $"pred_{algo}", normalizedValue);
            AddMeta(map, $"has_{algo}", hasValue ? 1 : 0);
            finitePredictions.Add(normalizedValue);
        }

        if (finitePredictions.Count == 0) finitePredictions.Add(0);
        finitePredictions.Sort();
        double predMin = finitePredictions[0];
        double predMax = finitePredictions[finitePredictions.Count - 1];
        double predMean = finitePredictions.Sum() / finitePredictions.Count;
        double predMedian = finitePredictions[finitePredictions.Count / 2];
        AddMeta(map, "pred_min", predMin);
        AddMeta(map, "pred_max", predMax);
        AddMeta(map, "pred_mean", predMean);
        AddMeta(map, "pred_median", predMedian);
        AddMeta(map, "pred_range", predMax - predMin);

        var pairs = new[]
        {
            ("Azusa", "Daniel"),
            ("Azusa", "Sunny"),
            ("Azusa", "Roxy"),
            ("Daniel", "Sunny"),
            ("Daniel", "Roxy"),
            ("Sunny", "Roxy"),
        };
        foreach (var (left, right) in pairs)
        {
            double diff = ToFeatureNumber(normalizedPredictions[left]) - ToFeatureNumber(normalizedPredictions[right]);
            AddMeta(map, $"diff_{left}_{right}", diff);
            AddMeta(map, $"absdiff_{left}_{right}", Math.Abs(diff));
        }

        AddMeta(map, "roxy_logRaw", RoundedFeature(numericDetails.LogRaw));
        AddMeta(map, "roxy_rawAgg", RoundedFeature(numericDetails.RawAgg));
        AddMeta(map, "roxy_preNumeric", RoundedFeature(numericDetails.PreNumeric));
        AddMeta(map, "roxy_rawNumeric", RoundedFeature(numericDetails.RawNumeric));
        AddMeta(map, "roxy_finalNumeric", RoundedFeature(structuralNumeric));

        foreach (var name in new[]
        {
            "lowCj", "highStream", "highCjDamp", "courseBreakDamp", "courseSustainLift",
            "denseJsLift", "denseJsDamp", "anchorLift", "handBiasLift", "total",
        })
        {
            AddMeta(map, $"corr_{name}", RoundedFeature(numericDetails.Corrections.Get(name)));
        }

        foreach (var stream in new[] { "speed", "handStream", "jack", "chordjack", "tech", "stamina", "course" })
        {
            var summary = curve.StreamSummaries.TryGetValue(stream, out var sm) ? sm : new RoxySummary();
            foreach (var key in new[] { "aggregate", "q97", "q90", "q75", "q50", "tailMean", "powerMean" })
            {
                AddMeta(map, $"{stream}_{key}", RoundedFeature(summary.Get(key)));
            }
        }

        var stats = curve.Stats;
        foreach (var name in new[]
        {
            "activeDurationSec", "breakCount", "breakDensity", "avgNps", "chordRate", "threeRate",
            "overlapRate", "rotationRate", "sameHandQ10", "fastJackRate", "anchorRate",
            "anchorImbalance", "handBias", "peakToSustainGap", "rows", "taps",
        })
        {
            AddMeta(map, $"stat_{name}", RoundedFeature(stats.Get(name)));
        }

        double avgNps = ToFeatureNumber(stats.AvgNps);
        double activeDuration = ToFeatureNumber(stats.ActiveDurationSec);
        double chordRate = ToFeatureNumber(stats.ChordRate);
        double fastJackRate = ToFeatureNumber(stats.FastJackRate);
        double overlapRate = ToFeatureNumber(stats.OverlapRate);
        double rotationRate = ToFeatureNumber(stats.RotationRate);
        double sameHandQ10 = ToFeatureNumber(stats.SameHandQ10);
        double breakDensity = ToFeatureNumber(stats.BreakDensity);
        double peakGap = ToFeatureNumber(stats.PeakToSustainGap);
        AddMeta(map, "logAvgNps", Math.Log(1 + Math.Max(0, avgNps)));
        AddMeta(map, "logDuration", Math.Log(1 + Math.Max(0, activeDuration)));
        AddMeta(map, "chordFast", chordRate * fastJackRate);
        AddMeta(map, "chordOverlap", chordRate * overlapRate);
        AddMeta(map, "rotationInvQ10", rotationRate / (sameHandQ10 + 1));
        AddMeta(map, "breakPeak", breakDensity * peakGap);

        return RoxyMetaModel.ROXY_META_FEATURE_NAMES
            .Select(name => ToFeatureNumber(map.TryGetValue(name, out var x) ? x : 0.0))
            .ToArray();
    }

    private sealed class MetaDetails
    {
        public double MetaNumeric;
        public Dictionary<string, double?> ReferencePredictions = new();
        public (double[] Times, double[] Values)? Graph;
    }

    private static MetaDetails ComputeRoxyMetaNumeric(
        string osuText, EstimatorOptions options, RoxyNumericDetails numericDetails,
        RoxyCurve curve, double structuralNumeric)
    {
        var referenceDetails = BuildReferencePredictions(osuText, options, structuralNumeric);
        var referencePredictions = referenceDetails.Predictions;
        var features = BuildRoxyMetaFeatures(referencePredictions, numericDetails, curve, structuralNumeric);
        double metaNumeric = RoxyMetaModel.EvaluateRoxyMetaModel(features);

        return new MetaDetails
        {
            MetaNumeric = metaNumeric,
            ReferencePredictions = referencePredictions,
            Graph = referenceDetails.Graph,
        };
    }

    // PORT NOTE: JS `buildRoxyGraphFromAzusa` is defined but never referenced by
    // runRoxyEstimatorFromText — the graph comes from computeRoxyMetaNumeric.
    // Ported for completeness; not wired in.
    private static (double[] Times, double[] Values)? BuildRoxyGraphFromAzusa(string osuText, EstimatorOptions options)
    {
        if (!options.WithGraph) return null;

        double analysisSpeedRate = options.SpeedRate ?? 1.0;
        SunnyResult sunnyResult = SafeSunny(() => SunnyShim.Run(
            osuText, analysisSpeedRate, AsOdDouble(options.OdFlag), options.CvtFlag, true));
        SunnyResult danielResult = SafeSunny(() =>
            DanielEstimator.RunDanielEstimatorFromText(osuText, options.With(withGraph: false), null));
        RcEstimatorResult azusaResult = SafeAzusa(() => AzusaEstimator.RunAzusaEstimatorFromText(
            osuText,
            options.With(withGraph: true, precomputedSunnyResult: sunnyResult, precomputedDanielResult: danielResult),
            null));

        return azusaResult.Graph;
    }

    private sealed class HighReferenceFloor
    {
        public double Floor;
        public double Activation;
        public double MissingReferenceBoost;
        public double Pressure;
        public double Confidence;
        public double ReferenceFloor;
        public double StructuralFloor;
        public double ReferenceTarget;

        public Dictionary<string, object?> ToDebugDict() => new()
        {
            ["floor"] = Fmt4(Floor),
            ["activation"] = Fmt4(Activation),
            ["missingReferenceBoost"] = Fmt4(MissingReferenceBoost),
            ["pressure"] = Fmt4(Pressure),
            ["confidence"] = Fmt4(Confidence),
            ["referenceFloor"] = Fmt4(ReferenceFloor),
            ["structuralFloor"] = Fmt4(StructuralFloor),
            ["referenceTarget"] = Fmt4(ReferenceTarget),
        };
    }

    private static HighReferenceFloor? ComputeHighReferenceStructuralFloor(
        Dictionary<string, double?> referencePredictions, RoxyNumericDetails numericDetails,
        RoxyCurve curve, double odCorrection)
    {
        double azusa = referencePredictions.TryGetValue("Azusa", out var az) ? ToDouble(az) : double.NaN;
        if (!double.IsFinite(azusa) || azusa < 17.0) return null;
        bool hasSunny = referencePredictions.TryGetValue("Sunny", out var sv) && sv != null && double.IsFinite(ToDouble(sv.Value));
        bool hasDaniel = referencePredictions.TryGetValue("Daniel", out var dv) && dv != null && double.IsFinite(ToDouble(dv.Value));

        var stats = curve.Stats;
        var streams = curve.StreamSummaries;
        double avgNps = ToFeatureNumber(stats.AvgNps);
        double chordRate = ToFeatureNumber(stats.ChordRate);
        double sameHandQ10 = ToFeatureNumber(stats.SameHandQ10);
        if (avgNps < 25 || chordRate < 0.70 || sameHandQ10 > 95) return null;

        double densityGate = Gate(avgNps, 27, 38);
        double chordGate = Gate(chordRate, 0.78, 0.92);
        double threeGate = Gate(ToFeatureNumber(stats.ThreeRate), 0.45, 0.72);
        double jackGate = Gate(StreamAggregate(streams, "jack"), 17.5, 21.8);
        double chordjackGate = Gate(StreamAggregate(streams, "chordjack"), 12.4, 15.6);
        double fastHandGate = InverseGate(sameHandQ10, 70, 110);
        double durationGate = Gate(ToFeatureNumber(stats.ActiveDurationSec), 50, 100);
        double rawGate = Gate(ToFeatureNumber(numericDetails.RawNumeric), 6, 17);
        double pressure = Clamp(
            (0.20 * densityGate)
            + (0.14 * chordGate)
            + (0.10 * threeGate)
            + (0.20 * jackGate)
            + (0.16 * chordjackGate)
            + (0.14 * fastHandGate)
            + (0.06 * durationGate),
            0,
            1);

        double pressureGate = Gate(pressure, 0.22, 0.46);
        double azusaGate = Gate(azusa, 17.0, 18.0);
        double missingReferenceRatio = ((!hasSunny ? 1 : 0) + (!hasDaniel ? 1 : 0)) / 2.0;
        double missingReferenceBoost = missingReferenceRatio
            * Gate(pressure, 0.25, 0.40)
            * Gate(azusa, 17.5, 18.2);
        double activation = Clamp((pressureGate * azusaGate) + missingReferenceBoost, 0, 1);
        if (activation <= 0) return null;

        double confidence = pressureGate * Gate(azusa, 17.0, 20.0);
        double referenceFloor = azusa - (0.45 - (0.25 * confidence));
        double structuralFloor = 16.65
            + (1.55 * confidence)
            + (0.35 * rawGate)
            + (0.25 * Gate(avgNps, 35, 45));
        double odAdjustment = Math.Min(0, double.IsFinite(odCorrection) ? odCorrection : 0) * 0.25;
        double structuralTarget = structuralFloor + odAdjustment;
        double referenceTarget = Math.Max(referenceFloor, structuralFloor) + odAdjustment;
        double floor = structuralTarget + ((referenceTarget - structuralTarget) * activation);

        return new HighReferenceFloor
        {
            Floor = Clamp(floor, 16.8, Math.Min(18.65, azusa + 0.30)),
            Activation = activation,
            MissingReferenceBoost = missingReferenceBoost,
            Pressure = pressure,
            Confidence = confidence,
            ReferenceFloor = referenceFloor,
            StructuralFloor = structuralFloor,
            ReferenceTarget = referenceTarget,
        };
    }

    // JS threads options.odFlag (number | "HR" | "EZ") into runSunnyEstimatorFromText;
    // our SunnyShim.Run takes double?, so a non-numeric flag is dropped. PORT NOTE.
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

    // ── entry point ─────────────────────────────────────────────────────────

    public static RcEstimatorResult RunRoxyEstimatorFromText(string osuText, EstimatorOptions? options = null, OsuFileParser? parsed = null)
    {
        options ??= new EstimatorOptions();
        try
        {
            if (osuText == null || osuText.Trim().Length == 0)
            {
                return BuildErrorResult("EmptyInput", "Beatmap text is empty");
            }

            double speedRate = options.SpeedRate ?? 1.0;
            if (!double.IsFinite(speedRate) || speedRate <= 0)
            {
                return BuildErrorResult("InvalidSpeedRate", "Invalid speed rate");
            }

            object? odFlag = NormalizeRoxyOdFlag(options);
            var timing = CanonicalizeOsuTiming(osuText, speedRate);
            string analysisText = timing.Text;
            double analysisSpeedRate = timing.SpeedRate;

            // effectiveOptions = { ...options, odFlag, speedRate: analysisSpeedRate }
            var effectiveOptions = new EstimatorOptions
            {
                SpeedRate = analysisSpeedRate,
                OdFlag = odFlag,
                OD = options.OD,
                Od = options.Od,
                OverallDifficulty = options.OverallDifficulty,
                CvtFlag = options.CvtFlag,
                WithGraph = options.WithGraph,
                ExtendedEstimationRange = options.ExtendedEstimationRange,
                EnableAlwaysShowLNDifficulty = options.EnableAlwaysShowLNDifficulty,
                ForceSunnyReferenceHo = options.ForceSunnyReferenceHo,
                PrecomputedDanielResult = options.PrecomputedDanielResult,
                PrecomputedSunnyResult = options.PrecomputedSunnyResult,
                MarathonCorrection = options.MarathonCorrection,
            };

            // Share the caller's parsed instance only when the analysis text is
            // equivalent to the original. canonicalizeOsuTiming rewrites the text
            // at EVERY valid speedRate (scale + shift to
            // ROXY_CANONICAL_FIRST_OBJECT_MS). At speedRate=1 the rewrite is a
            // pure constant time-shift and the structural pipeline is delta-based
            // (shift-invariant) -> sharing yields bit-identical output
            // (QA-verified). At speedRate!=1 times are scaled (deltas change) ->
            // the canonicalized text must be re-parsed. cvtFlag HO/IN additionally
            // mutates the parser in place (modHO/modIN) -> excluded from sharing
            // so the caller's instance stays pristine.
            string? cvt = NormalizeCvtFlag(effectiveOptions.CvtFlag);
            bool canShareParsed = parsed != null && speedRate == 1 && cvt != "HO" && cvt != "IN";
            OsuFileParser parser;
            if (canShareParsed)
            {
                parser = parsed!;
            }
            else
            {
                parser = new OsuFileParser(analysisText);
                parser.Process();
                ApplyConversionFlag(parser, effectiveOptions.CvtFlag);
            }
            var parsedData = parser.GetParsedData();
            var odDetails = ComputeOdDetails(parsedData.Od, odFlag);

            double lnRatio = double.IsFinite(parsedData.LnRatio) && parsedData.LnRatio != 0 ? parsedData.LnRatio : 0;
            double columnCount = double.IsFinite(parsedData.ColumnCount) && parsedData.ColumnCount != 0 ? parsedData.ColumnCount : 0;

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
                return BuildErrorResult("UnsupportedKeys", "Roxy only supports 4K", lnRatio, columnCount);
            }
            if (lnRatio > CfgRcLnRatioLimit)
            {
                return BuildErrorResult(
                    "UnsupportedLN",
                    $"Roxy RC scope rejects LN ratio {(lnRatio * 100).ToString("F1", CultureInfo.InvariantCulture)}%",
                    lnRatio, columnCount);
            }

            var (taps, rows) = BuildTapRows(parsedData, analysisSpeedRate, CfgRowToleranceMs);
            if (taps.Count < CfgMinNotes || rows.Count < 2)
            {
                return BuildErrorResult("TooFewNotes", "Not enough RC tap notes", lnRatio, columnCount);
            }

            var tapTimes = taps.Select(t => t.T).ToList();
            ComputeNpsRows(rows, tapTimes);
            var activity = ComputeActivityStats(rows, taps.Count);
            var curve = ComputeRoxyCurve(rows, taps, activity);
            var axisBreakdown = BuildAxisBreakdown(curve);
            var sectionCurve = BuildSectionCurve(rows, curve.LocalRaw);
            var numericDetails = ComputeRoxyNumeric(curve);
            double structuralNumeric = Math.Round(numericDetails.Numeric, 2, MidpointRounding.AwayFromZero);

            // metaOptions = { ...effectiveOptions, odFlag: ROXY_OD_NEUTRAL, precomputedSunnyResult: null }
            var metaOptions = new EstimatorOptions
            {
                SpeedRate = effectiveOptions.SpeedRate,
                OdFlag = RoxyOdNeutral,
                OD = effectiveOptions.OD,
                Od = effectiveOptions.Od,
                OverallDifficulty = effectiveOptions.OverallDifficulty,
                CvtFlag = effectiveOptions.CvtFlag,
                WithGraph = effectiveOptions.WithGraph,
                ExtendedEstimationRange = effectiveOptions.ExtendedEstimationRange,
                EnableAlwaysShowLNDifficulty = effectiveOptions.EnableAlwaysShowLNDifficulty,
                ForceSunnyReferenceHo = effectiveOptions.ForceSunnyReferenceHo,
                PrecomputedDanielResult = effectiveOptions.PrecomputedDanielResult,
                PrecomputedSunnyResult = null,
                MarathonCorrection = effectiveOptions.MarathonCorrection,
            };
            var metaDetails = ComputeRoxyMetaNumeric(analysisText, metaOptions, numericDetails, curve, structuralNumeric);
            double metaNumeric = metaDetails.MetaNumeric;
            double baseUnguardedNumeric = double.IsFinite(metaNumeric) ? metaNumeric : structuralNumeric;
            double structuralBackstopStrength = double.IsFinite(structuralNumeric)
                ? Gate(structuralNumeric, 12.25, 14.0)
                : 0;
            double? structuralBackstop = structuralBackstopStrength > 0
                ? structuralNumeric - 0.15
                : (double?)null;
            double structuralBackstopGap = structuralBackstop != null
                ? structuralBackstop.Value - baseUnguardedNumeric
                : 0;
            bool structuralBackstopApplied = structuralBackstop != null
                && structuralBackstopGap > 0
                && structuralBackstopGap <= 0.35;
            if (structuralBackstopApplied)
            {
                baseUnguardedNumeric += (structuralBackstop!.Value - baseUnguardedNumeric) * structuralBackstopStrength;
            }
            double odCorrection = ComputeOdCorrection(odDetails, baseUnguardedNumeric);
            double unguardedNumeric = Clamp(baseUnguardedNumeric + odCorrection, -2, RoxyNumericOutputMax);
            var highReferenceFloor = ComputeHighReferenceStructuralFloor(
                metaDetails.ReferencePredictions,
                numericDetails,
                curve,
                odCorrection);
            if (highReferenceFloor != null)
            {
                unguardedNumeric = Math.Max(unguardedNumeric, highReferenceFloor.Floor);
            }
            double referenceGapCorrection = odDetails.Flag == null
                ? ComputeReferenceGapCorrection(
                    metaDetails.ReferencePredictions,
                    structuralNumeric,
                    unguardedNumeric,
                    curve.Stats)
                : 0;
            unguardedNumeric = Clamp(unguardedNumeric + referenceGapCorrection, -2, RoxyNumericOutputMax);
            double azusaHighGapLift = odDetails.Flag == null
                ? ComputeAzusaHighGapLift(metaDetails.ReferencePredictions, unguardedNumeric)
                : 0;
            unguardedNumeric = Clamp(unguardedNumeric + azusaHighGapLift, -2, RoxyNumericOutputMax);
            double finalNumeric = ComputeAzusaFusion(
                metaDetails.ReferencePredictions,
                Math.Round(unguardedNumeric, 2, MidpointRounding.AwayFromZero));

            // Marathon duration correction (applied inside the estimator):
            // options.marathonCorrection injects { durationS, ettValues } (not
            // triggered when absent / no MSD); decreases only, log-saturating +
            // numeric taper; the correction runs before the scope decision (a
            // borderline chart pushed into BelowScope after correction is still
            // routed by Mixed). Parameters and mechanism: see
            // docs/features/marathon-correction.md.
            var mc = options.MarathonCorrection;
            if (mc != null && mc.DurationS is { } durS && double.IsFinite(durS))
            {
                double corr = MarathonCorrection.ComputeMarathonCorrection(
                    mc.DurationS,
                    mc.EttValues,
                    finalNumeric);
                if (corr > 0)
                {
                    finalNumeric -= corr;
                }
            }

            // High-difficulty focus: low difficulty (< Alpha) and very high
            // difficulty (>= Zeta high) do not emit a valid numeric — return the
            // dan label + numeric null, routed by Mixed to Azusa (low difficulty).
            if (finalNumeric < RoxyScopeMin)
            {
                return BuildScopeResult(RoxyScopeMinLabel, "BelowScope", finalNumeric, numericDetails.RawNumeric,
                    lnRatio, columnCount, taps.Count, rows.Count);
            }
            if (finalNumeric >= RoxyScopeMax)
            {
                return BuildScopeResult(RoxyScopeMaxLabel, "AboveScope", finalNumeric, numericDetails.RawNumeric,
                    lnRatio, columnCount, taps.Count, rows.Count);
            }

            string estDiff = NumericToRoxyRcLabel(finalNumeric);

            var referenceDict = new Dictionary<string, object?>();
            foreach (var kv in metaDetails.ReferencePredictions)
            {
                referenceDict[kv.Key] = Fmt4(kv.Value);
            }

            var streamsDebug = new Dictionary<string, object?>();
            foreach (var kv in curve.StreamSummaries)
            {
                streamsDebug[kv.Key] = new Dictionary<string, object?>
                {
                    ["q50"] = Fmt4(kv.Value.Q50),
                    ["q75"] = Fmt4(kv.Value.Q75),
                    ["q90"] = Fmt4(kv.Value.Q90),
                    ["q97"] = Fmt4(kv.Value.Q97),
                    ["tailMean"] = Fmt4(kv.Value.TailMean),
                    ["powerMean"] = Fmt4(kv.Value.PowerMean),
                    ["aggregate"] = Fmt4(kv.Value.Aggregate),
                };
            }

            return new RcEstimatorResult
            {
                Star = Math.Round(3.4 + 0.38 * finalNumeric, 4, MidpointRounding.AwayFromZero),
                LnRatio = lnRatio,
                ColumnCount = columnCount,
                EstDiff = estDiff,
                // PORT NOTE: JS stores finalNumeric directly (NOT re-rounded here,
                // unlike Azusa) — a post-marathon-correction value keeps its extra
                // decimals.
                NumericDifficulty = finalNumeric,
                NumericDifficultyHint = "roxy-meta-ridge-v3",
                Graph = options.WithGraph ? metaDetails.Graph : null,
                RawNumericDifficulty = Math.Round(numericDetails.RawNumeric, 4, MidpointRounding.AwayFromZero),
                Debug = new Dictionary<string, object?>
                {
                    ["notes"] = taps.Count,
                    ["rows"] = rows.Count,
                    ["rawAgg"] = Fmt4(numericDetails.RawAgg),
                    ["logRaw"] = Fmt4(numericDetails.LogRaw),
                    ["preNumeric"] = Fmt4(numericDetails.PreNumeric),
                    ["rawNumeric"] = Fmt4(numericDetails.RawNumeric),
                    ["structuralNumeric"] = Fmt4(structuralNumeric),
                    ["metaNumeric"] = Fmt4(metaNumeric),
                    ["baseUnguardedNumeric"] = Fmt4(baseUnguardedNumeric),
                    ["structuralBackstop"] = new Dictionary<string, object?>
                    {
                        ["applied"] = structuralBackstopApplied,
                        ["floor"] = Fmt4(structuralBackstop),
                        ["strength"] = Fmt4(structuralBackstopStrength),
                    },
                    ["unguardedNumeric"] = Fmt4(unguardedNumeric),
                    ["finalNumeric"] = Fmt4(finalNumeric),
                    ["highReferenceStructuralFloor"] = highReferenceFloor?.ToDebugDict(),
                    ["od"] = new Dictionary<string, object?>
                    {
                        ["flag"] = odDetails.Flag,
                        ["base"] = Fmt4(odDetails.Base),
                        ["neutral"] = Fmt4(odDetails.Neutral),
                        ["effective"] = Fmt4(odDetails.Effective),
                        ["baseWindow"] = Fmt4(odDetails.BaseWindow),
                        ["effectiveWindow"] = Fmt4(odDetails.EffectiveWindow),
                        ["pressureRatio"] = Fmt4(odDetails.PressureRatio),
                        ["correction"] = Fmt4(odCorrection),
                    },
                    ["referenceGapCorrection"] = Fmt4(referenceGapCorrection),
                    ["azusaHighGapLift"] = Fmt4(azusaHighGapLift),
                    ["speedRateMode"] = new Dictionary<string, object?>
                    {
                        ["mode"] = "time-scale-only",
                        ["speedRate"] = Fmt4(speedRate),
                        ["analysisSpeedRate"] = Fmt4(analysisSpeedRate),
                        ["canonicalFirstObjectMs"] = RoxyCanonicalFirstObjectMs,
                        ["originalFirstObjectMs"] = Fmt4(timing.FirstTime),
                        ["canonicalized"] = timing.Applied,
                    },
                    ["meta"] = new Dictionary<string, object?>
                    {
                        ["featureCount"] = RoxyMetaModel.ROXY_META_FEATURE_NAMES.Length,
                        ["references"] = referenceDict,
                    },
                    ["stats"] = curve.Stats.ToDebugDict(),
                    ["corrections"] = numericDetails.Corrections.ToDebugDict(),
                    ["streams"] = streamsDebug,
                    ["axisBreakdown"] = axisBreakdown,
                    ["sectionCurve"] = sectionCurve,
                },
            };
        }
        catch (Exception error)
        {
            return BuildErrorResult("RoxyError", string.IsNullOrEmpty(error.Message) ? "Roxy estimator failed" : error.Message);
        }
    }
}
