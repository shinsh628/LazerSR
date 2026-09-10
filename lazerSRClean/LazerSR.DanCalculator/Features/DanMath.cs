// Port of mania-hub live-backend/src/dan/dan-estimator/math.ts
// Shared numeric helpers for feature extraction, pattern analysis and scoring.
// JS `number` == C# `double` throughout. `Math.min(...values)` on an empty array
// is +Infinity in JS; callers guard, but keep that behaviour where reachable.

namespace LazerSR.DanCalculator.Features;

public static class DanMath
{
    public static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

    public static double Clamp01(double value) => Clamp(value, 0, 1);

    public static double GateWhen(bool condition, double value) => condition ? value : 0;

    public static double MinGate(params double[] values)
    {
        double m = double.PositiveInfinity;
        foreach (var v in values) m = Math.Min(m, v);
        return Clamp01(m);
    }

    public static double Quantile(IReadOnlyList<double> values, double q)
    {
        if (values.Count == 0) return 0;
        int index = QuantileIndex(values.Count, q);
        var copy = values.ToArray();
        return Quickselect(copy, index);
    }

    public static double[] Quantiles(IReadOnlyList<double> values, IReadOnlyList<double> qs)
    {
        if (values.Count == 0) return qs.Select(_ => 0.0).ToArray();
        var sorted = values.OrderBy(x => x).ToArray();
        return qs.Select(q => sorted[QuantileIndex(sorted.Length, q)]).ToArray();
    }

    private static int QuantileIndex(int length, double q)
        => (int)Math.Min(length - 1, Math.Max(0, Math.Floor((length - 1) * q)));

    private static double Quickselect(double[] values, int target)
    {
        int left = 0, right = values.Length - 1;
        while (left < right)
        {
            int pivotIndex = Partition(values, left, right, (left + right) / 2);
            if (target == pivotIndex) return values[target];
            if (target < pivotIndex) right = pivotIndex - 1;
            else left = pivotIndex + 1;
        }
        return values[left];
    }

    private static int Partition(double[] values, int left, int right, int pivotIndex)
    {
        double pivotValue = values[pivotIndex];
        (values[pivotIndex], values[right]) = (values[right], values[pivotIndex]);
        int storeIndex = left;
        for (int index = left; index < right; index++)
        {
            if (values[index] < pivotValue)
            {
                (values[storeIndex], values[index]) = (values[index], values[storeIndex]);
                storeIndex++;
            }
        }
        (values[right], values[storeIndex]) = (values[storeIndex], values[right]);
        return storeIndex;
    }

    public static int CountInWindow(IReadOnlyList<double> times, double windowMs)
    {
        int best = 0, start = 0;
        for (int end = 0; end < times.Count; end++)
        {
            while (times[end] - times[start] > windowMs) start++;
            best = Math.Max(best, end - start + 1);
        }
        return best;
    }

    public static double Average(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        double sum = 0;
        foreach (var v in values) sum += v;
        return sum / values.Count;
    }

    public static double BucketEntropy(IReadOnlyList<double> values, double bucketSize)
    {
        if (values.Count == 0 || bucketSize <= 0) return 0;
        var buckets = new Dictionary<double, double>();
        foreach (var value in values)
        {
            double bucket = Math.Round(value / bucketSize) * bucketSize;
            buckets[bucket] = buckets.GetValueOrDefault(bucket) + 1;
        }
        double entropy = 0;
        foreach (var count in buckets.Values)
        {
            double probability = count / values.Count;
            entropy -= probability * Math.Log2(probability);
        }
        return entropy;
    }

    public static double[] BucketValues(IReadOnlyList<double> values, double bucketSize)
    {
        if (bucketSize <= 0) return values.ToArray();
        return values.Select(value => Math.Round(value / bucketSize) * bucketSize).ToArray();
    }

    public static double RaoQuadraticEntropyLog(IReadOnlyList<double> values, int logIterations)
    {
        if (values.Count < 2) return 0;
        var counts = new Dictionary<double, double>();
        foreach (var value in values)
            counts[value] = counts.GetValueOrDefault(value) + 1;

        var entries = counts.ToArray();
        double total = values.Count;
        double entropy = 0;
        foreach (var (left, leftCount) in entries)
        {
            foreach (var (right, rightCount) in entries)
            {
                double distance = Math.Abs(left - right);
                for (int i = 0; i < logIterations; i++)
                    distance = Math.Log(1 + distance); // Math.log1p
                entropy += (leftCount / total) * (rightCount / total) * distance;
            }
        }
        return entropy;
    }

    public static double PowerMean(IReadOnlyList<double> values, IReadOnlyList<double> weights, double exponent)
    {
        if (values.Count == 0) return 0;
        double weightSum = 0;
        foreach (var w in weights) weightSum += w;
        if (weightSum <= 0) return 0;
        double acc = 0;
        for (int i = 0; i < values.Count; i++)
            acc += Math.Pow(values[i], exponent) * weights[i];
        return Math.Pow(acc / weightSum, 1.0 / exponent);
    }

    public static double StrainSpikiness(IReadOnlyList<double> values, IReadOnlyList<double> weights)
    {
        if (values.Count < 3) return 0;
        double mean = PowerMean(values, weights, 5);
        if (mean <= 0) return 0;
        double weightSum = 0;
        foreach (var w in weights) weightSum += w;
        if (weightSum <= 0) return 0;
        double acc = 0;
        for (int i = 0; i < values.Count; i++)
        {
            double diff = Math.Pow(values[i], 8) - Math.Pow(mean, 8);
            acc += diff * diff * weights[i];
        }
        double variance = acc / weightSum;
        return Math.Sqrt(Math.Pow(variance, 1.0 / 8)) / mean;
    }
}
