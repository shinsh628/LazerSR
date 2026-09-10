// Port of mania-hub live-backend/vendor/leoblack/rework/reworkMathCore.js
//
// Shared math core for the Sunny and Daniel rework algorithms.
//
// Every function/constant in this file is TEXTUALLY IDENTICAL (whitespace and
// comments ignored) in js/rework/sunnyAlgorithm.js and js/rework/danielAlgorithm.js
// — extracted verbatim, never "improved". Both algorithms depend on the exact
// computation; any numeric/order change here silently changes both estimators.
// See docs/guides/module-conventions.md. Do NOT import this file from
// sunnyWindowAlgorithm.js yet (out of scope for the extraction PR).
//
// EXCEPTION since 042ccee (PR #47 C# osu-author-port sync): stepInterp and
// computeCAndKs are now used ONLY by danielAlgorithm — sunnyAlgorithm.js keeps
// local D1/D2 versions (stepInterp `<=`->`<` exact-match sampling; computeCAndKs
// gains CStepV2/noteHitTimesV2 for effectiveWeights) that differ from daniel's
// old semantics. Do NOT move them back into this shared core: sunny and daniel
// intentionally diverge here.
//
// JS `Float64Array` -> C# `double[]`; `Array.from(x)` -> `(double[])x.Clone()`.

namespace LazerSR.DanCalculator.Estimators;

public static class ReworkMathCore
{
    // Graph-smoothing window constants — used only by SmoothDForGraph below.
    private const double BREAK_ZERO_THRESHOLD_MS = 400;
    private const double GRAPH_RESAMPLE_INTERVAL_MS = 100;
    private const double SMOOTH_SIGMA_MS = 800;

    public static int BisectLeft(IReadOnlyList<double> arr, double target)
    {
        int lo = 0;
        int hi = arr.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (arr[mid] < target) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public static int BisectRight(IReadOnlyList<double> arr, double target)
    {
        int lo = 0;
        int hi = arr.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (arr[mid] <= target) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public static double[] CumulativeSum(IReadOnlyList<double> x, IReadOnlyList<double> f)
    {
        var F = new double[x.Count];
        for (int i = 1; i < x.Count; i += 1)
        {
            F[i] = F[i - 1] + f[i - 1] * (x[i] - x[i - 1]);
        }
        return F;
    }

    public static double QueryCumsum(double q, IReadOnlyList<double> x, IReadOnlyList<double> F, IReadOnlyList<double> f)
    {
        // PORT NOTE: source assumes x non-empty; guard array-OOB.
        if (x.Count == 0) return 0;
        if (q <= x[0]) return 0;
        if (q >= x[x.Count - 1]) return F[F.Count - 1];
        int i = BisectRight(x, q) - 1;
        return F[i] + f[i] * (q - x[i]);
    }

    public static double[] SmoothOnCorners(
        IReadOnlyList<double> x, IReadOnlyList<double> f, double window, double scale = 1.0, string mode = "sum")
    {
        var F = CumulativeSum(x, f);
        var g = new double[f.Count];
        for (int i = 0; i < x.Count; i += 1)
        {
            double s = x[i];
            double a = Math.Max(s - window, x[0]);
            double b = Math.Min(s + window, x[x.Count - 1]);
            double val = QueryCumsum(b, x, F, f) - QueryCumsum(a, x, F, f);
            if (mode == "avg")
            {
                g[i] = b - a > 0 ? val / (b - a) : 0;
            }
            else
            {
                g[i] = scale * val;
            }
        }
        return g;
    }

    public static double[] InterpValues(IReadOnlyList<double> newX, IReadOnlyList<double> oldX, IReadOnlyList<double> oldVals)
    {
        var @out = new double[newX.Count];
        // PORT NOTE: source assumes oldX/oldVals non-empty; guard array-OOB.
        if (oldX.Count == 0 || oldVals.Count == 0) return @out;
        int idx = 0;

        for (int i = 0; i < newX.Count; i += 1)
        {
            double x = newX[i];

            if (x <= oldX[0])
            {
                @out[i] = oldVals[0];
                continue;
            }
            if (x >= oldX[oldX.Count - 1])
            {
                @out[i] = oldVals[oldVals.Count - 1];
                continue;
            }

            while (idx + 1 < oldX.Count && oldX[idx + 1] < x)
            {
                idx += 1;
            }

            double x0 = oldX[idx];
            double x1 = oldX[idx + 1];
            double y0 = oldVals[idx];
            double y1 = oldVals[idx + 1];

            if (x1 == x0)
            {
                @out[i] = y0;
                continue;
            }

            double t = (x - x0) / (x1 - x0);
            @out[i] = y0 + t * (y1 - y0);
        }

        return @out;
    }

    public static double[] StepInterp(IReadOnlyList<double> newX, IReadOnlyList<double> oldX, IReadOnlyList<double> oldVals)
    {
        var @out = new double[newX.Count];
        // PORT NOTE: source assumes oldVals non-empty; guard array-OOB.
        if (oldVals.Count == 0) return @out;
        int idx = 0;
        for (int i = 0; i < newX.Count; i += 1)
        {
            double x = newX[i];
            while (idx + 1 < oldX.Count && oldX[idx + 1] <= x)
            {
                idx += 1;
            }
            int clamped = Math.Max(0, Math.Min(idx, oldVals.Count - 1));
            @out[i] = oldVals[clamped];
        }
        return @out;
    }

    public static double[] GaussianFilter1d(IReadOnlyList<double> data, double sigmaSamples)
    {
        if (!double.IsFinite(sigmaSamples) || sigmaSamples <= 0)
        {
            return data.ToArray();
        }

        int radius = (int)Math.Max(1, Math.Truncate(4 * sigmaSamples + 0.5));
        int kernelSize = radius * 2 + 1;
        var kernel = new double[kernelSize];
        double kernelSum = 0;

        for (int i = -radius; i <= radius; i += 1)
        {
            double v = Math.Exp(-0.5 * Math.Pow(i / sigmaSamples, 2));
            kernel[i + radius] = v;
            kernelSum += v;
        }
        for (int i = 0; i < kernelSize; i += 1)
        {
            kernel[i] /= kernelSum;
        }

        var padded = new double[data.Count + radius * 2];
        for (int i = 0; i < data.Count; i += 1)
        {
            padded[i + radius] = data[i];
        }

        var @out = new double[data.Count];
        for (int i = 0; i < data.Count; i += 1)
        {
            double acc = 0;
            for (int k = 0; k < kernelSize; k += 1)
            {
                acc += padded[i + k] * kernel[k];
            }
            @out[i] = acc;
        }
        return @out;
    }

    public static double RescaleHigh(double sr)
    {
        if (sr <= 9) return sr;
        return 9 + (sr - 9) * (1 / 1.2);
    }

    public static List<double[]> MergeByHead(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
    {
        var result = new List<double[]>();
        int i = 0;
        int j = 0;
        while (i < a.Count && j < b.Count)
        {
            if (a[i][1] <= b[j][1])
            {
                result.Add(a[i]);
                i += 1;
            }
            else
            {
                result.Add(b[j]);
                j += 1;
            }
        }
        while (i < a.Count)
        {
            result.Add(a[i]);
            i += 1;
        }
        while (j < b.Count)
        {
            result.Add(b[j]);
            j += 1;
        }
        return result;
    }

    public readonly record struct CAndKs(double[] CStep, double[] KsStep);

    public static CAndKs ComputeCAndKs(
        int K, IReadOnlyList<double[]> noteSeq, IReadOnlyList<IReadOnlyList<bool>> keyUsage, IReadOnlyList<double> baseCorners)
    {
        var noteHitTimes = noteSeq.Select(n => n[1]).OrderBy(a => a).ToArray();

        var CStep = new double[baseCorners.Count];
        int lo = 0;
        int hi = 0;
        for (int i = 0; i < baseCorners.Count; i += 1)
        {
            double s = baseCorners[i];
            double low = s - 500;
            double high = s + 500;

            while (lo < noteHitTimes.Length && noteHitTimes[lo] < low)
            {
                lo += 1;
            }
            while (hi < noteHitTimes.Length && noteHitTimes[hi] < high)
            {
                hi += 1;
            }

            CStep[i] = hi - lo;
        }

        var KsStep = new double[baseCorners.Count];
        for (int i = 0; i < baseCorners.Count; i += 1)
        {
            int count = 0;
            for (int k = 0; k < K; k += 1)
            {
                if (keyUsage[k][i]) count += 1;
            }
            KsStep[i] = Math.Max(count, 1);
        }

        return new CAndKs(CStep, KsStep);
    }

    public static double[] ApplyProximityEnvelope(
        IReadOnlyList<double> allCorners, IReadOnlyList<double> DAll, IReadOnlyList<double[]> noteSeq)
    {
        if (noteSeq.Count == 0)
        {
            return DAll.ToArray();
        }

        var noteTimes = noteSeq
            .Select(n => n[1])
            .Where(double.IsFinite)
            .OrderBy(a => a)
            .ToArray();

        if (noteTimes.Length == 0)
        {
            return DAll.ToArray();
        }

        const double proximityFadeMs = 500;
        var @out = new double[allCorners.Count];
        for (int i = 0; i < allCorners.Count; i += 1)
        {
            double t = allCorners[i];
            int idx = BisectLeft(noteTimes, t);
            double after = idx < noteTimes.Length ? Math.Abs(noteTimes[idx] - t) : double.PositiveInfinity;
            double before = idx > 0 ? Math.Abs(noteTimes[idx - 1] - t) : double.PositiveInfinity;
            double d = Math.Min(after, before);
            double ratio = Math.Max(0, Math.Min(d / proximityFadeMs, 1));
            double envelope = 0.5 * (1 + Math.Cos(Math.PI * ratio));
            @out[i] = DAll[i] * envelope;
        }
        return @out;
    }

    public static double[] SmoothDForGraph(
        IReadOnlyList<double> allCorners, IReadOnlyList<double> DAll, IReadOnlyList<double[]> noteSeq)
    {
        if (allCorners.Count == 0 || DAll.Count == 0)
        {
            return Array.Empty<double>();
        }

        double tStart = allCorners[0];
        double tEnd = allCorners[allCorners.Count - 1];
        var uniformTimes = new List<double>();
        for (double t = tStart; t <= tEnd + GRAPH_RESAMPLE_INTERVAL_MS; t += GRAPH_RESAMPLE_INTERVAL_MS)
        {
            uniformTimes.Add(t);
        }

        var noteTimes = noteSeq
            .Select(n => n[1])
            .Where(double.IsFinite)
            .OrderBy(a => a)
            .ToArray();

        var uniformD = InterpValues(uniformTimes, allCorners, DAll);

        if (noteTimes.Length > 0)
        {
            for (int i = 0; i < uniformTimes.Count; i += 1)
            {
                double t = uniformTimes[i];
                int idx = BisectLeft(noteTimes, t);
                double after = idx < noteTimes.Length ? Math.Abs(noteTimes[idx] - t) : double.PositiveInfinity;
                double before = idx > 0 ? Math.Abs(noteTimes[idx - 1] - t) : double.PositiveInfinity;
                double dist = Math.Min(after, before);
                if (dist > BREAK_ZERO_THRESHOLD_MS)
                {
                    uniformD[i] = 0;
                }
            }
        }

        double sigmaSamples = SMOOTH_SIGMA_MS / GRAPH_RESAMPLE_INTERVAL_MS;
        var smoothed = GaussianFilter1d(uniformD, sigmaSamples);

        if (noteTimes.Length > 0)
        {
            for (int i = 0; i < uniformTimes.Count; i += 1)
            {
                double t = uniformTimes[i];
                int idx = BisectLeft(noteTimes, t);
                double after = idx < noteTimes.Length ? Math.Abs(noteTimes[idx] - t) : double.PositiveInfinity;
                double before = idx > 0 ? Math.Abs(noteTimes[idx - 1] - t) : double.PositiveInfinity;
                double dist = Math.Min(after, before);
                if (dist > BREAK_ZERO_THRESHOLD_MS)
                {
                    smoothed[i] = 0;
                }
            }
        }

        return InterpValues(allCorners, uniformTimes, smoothed);
    }

    public static double JackNerfer(double delta)
        => 1 - 7e-5 * Math.Pow(0.15 + Math.Abs(delta - 0.08), -4);

    public static readonly double[] TargetPercentiles =
        { 0.945, 0.935, 0.925, 0.915, 0.845, 0.835, 0.825, 0.815 };
}
