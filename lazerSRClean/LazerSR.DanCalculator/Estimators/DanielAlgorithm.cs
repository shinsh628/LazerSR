// Port of mania-hub live-backend/vendor/leoblack/rework/danielAlgorithm.js
//
// thebagelofman's "Daniel" 4K star-rating rework. Uses ReworkMathCore for all
// shared corner-math. 1:1 mechanical port — function names, branching and magic
// numbers preserved.
//
// calculateDaniel's JS return is a union:
//   -1 / -2 / -3          -> error codes (parse fail / not mania / unsupported keys)
//   [sr, lnRatio, colCnt] -> normal (no graph)
//   { star, lnRatio, columnCount, graph } -> normal (withGraph)
// modelled here as DanielCalcResult (Code != null means an error code).

using LazerSR.DanCalculator.Parser;
using static LazerSR.DanCalculator.Estimators.ReworkMathCore;

namespace LazerSR.DanCalculator.Estimators;

/// <summary>Union return of <see cref="DanielAlgorithm.CalculateDaniel"/>.</summary>
public sealed class DanielCalcResult
{
    /// <summary>-1 parse fail, -2 not mania, -3 unsupported keys. <c>null</c> = success.</summary>
    public int? Code;
    public double Star;
    public double LnRatio;
    public double ColumnCount;
    public (double[] Times, double[] Values)? Graph;
}

public static class DanielAlgorithm
{
    private sealed class Preprocessed
    {
        public string Status = "";
        public double X;
        public int K;
        public double T;
        public List<double[]> NoteSeq = new();
        public List<double[]>[] NoteSeqByColumn = System.Array.Empty<List<double[]>>();
        public double LnRatio;
        public double ColumnCount;
    }

    private static double NumOrZero(double v) => double.IsNaN(v) ? 0 : v;

    private static Preprocessed PreprocessDaniel(string osuText, double speedRate, OsuFileParser? parsed)
    {
        // parsed: a shared OsuFileParser instance already processed by the caller.
        var parser = parsed ?? new OsuFileParser(osuText);
        if (parsed == null) parser.Process();
        var parsedData = parser.GetParsedData();

        double lnRatio = NumOrZero(parsedData.LnRatio);
        double columnCount = NumOrZero(parsedData.ColumnCount);

        if (parsedData.Status == "Fail")
        {
            return new Preprocessed { Status = "Fail", LnRatio = lnRatio, ColumnCount = columnCount };
        }
        if (parsedData.Status == "NotMania")
        {
            return new Preprocessed { Status = "NotMania", LnRatio = lnRatio, ColumnCount = columnCount };
        }
        if (columnCount != 4)
        {
            return new Preprocessed
            {
                Status = "UnsupportedKeys",
                K = (int)columnCount,
                LnRatio = lnRatio,
                ColumnCount = columnCount,
            };
        }

        // Keep Daniel port consistent with the original osu_file_parser.py used by Daniel-main.
        // That parser effectively keeps OD fixed to 9 for this algorithm branch.
        const double od = 9;

        double timeScale = speedRate != 0 ? 1 / speedRate : 1;

        var noteSeq = new List<double[]>();
        for (int i = 0; i < parsedData.Columns.Count; i += 1)
        {
            double k = parsedData.Columns[i];
            double h = parsedData.NoteStarts[i];
            h = Math.Floor(h * timeScale);
            noteSeq.Add(new[] { k, h });
        }

        noteSeq.Sort((a, b) =>
        {
            if (a[1] != b[1]) return a[1].CompareTo(b[1]);
            return a[0].CompareTo(b[0]);
        });

        int K = (int)columnCount;
        var noteSeqByColumn = new List<double[]>[K];
        for (int k = 0; k < K; k += 1) noteSeqByColumn[k] = new List<double[]>();
        foreach (var n in noteSeq)
        {
            int col = (int)n[0];
            if (col >= 0 && col < K) noteSeqByColumn[col].Add(n);
        }

        double x = 0.3 * Math.Sqrt((64.5 - Math.Ceiling(od * 3)) / 500);
        x = Math.Min(x, 0.6 * (x - 0.09) + 0.09);

        double T = noteSeq.Count > 0 ? noteSeq[noteSeq.Count - 1][1] + 1 : 0;

        return new Preprocessed
        {
            Status = "OK",
            X = x,
            K = K,
            T = T,
            NoteSeq = noteSeq,
            NoteSeqByColumn = noteSeqByColumn,
            LnRatio = lnRatio,
            ColumnCount = columnCount,
        };
    }

    private static (double[] AllCorners, double[] BaseCorners, double[] ACorners) GetCorners(double T, List<double[]> noteSeq)
    {
        var cornersBase = new HashSet<double>();
        foreach (var n in noteSeq)
        {
            double h = n[1];
            cornersBase.Add(h);
            cornersBase.Add(h + 501);
            cornersBase.Add(h - 499);
            cornersBase.Add(h + 1);
        }
        cornersBase.Add(0);
        cornersBase.Add(T);

        var baseCorners = cornersBase.Where(s => s >= 0 && s <= T).OrderBy(s => s).ToArray();

        var cornersA = new HashSet<double>();
        foreach (var n in noteSeq)
        {
            double h = n[1];
            cornersA.Add(h);
            cornersA.Add(h + 1000);
            cornersA.Add(h - 1000);
        }
        cornersA.Add(0);
        cornersA.Add(T);

        var aCorners = cornersA.Where(s => s >= 0 && s <= T).OrderBy(s => s).ToArray();

        var all = new HashSet<double>(baseCorners);
        foreach (var s in aCorners) all.Add(s);
        var allCorners = all.OrderBy(s => s).ToArray();

        return (allCorners, baseCorners, aCorners);
    }

    private static bool[][] GetKeyUsage(int K, double T, List<double[]> noteSeq, double[] baseCorners)
    {
        var keyUsage = new bool[K][];
        for (int k = 0; k < K; k += 1) keyUsage[k] = new bool[baseCorners.Length];

        foreach (var n in noteSeq)
        {
            int k = (int)n[0];
            double h = n[1];
            double startTime = Math.Max(h - 150, 0);
            double endTime = Math.Min(h + 150, T - 1);
            int leftIdx = BisectLeft(baseCorners, startTime);
            int rightIdx = BisectLeft(baseCorners, endTime);
            for (int idx = leftIdx; idx < rightIdx; idx += 1)
            {
                keyUsage[k][idx] = true;
            }
        }

        return keyUsage;
    }

    private static double[][] GetKeyUsage400(int K, List<double[]> noteSeq, double[] baseCorners)
    {
        var keyUsage400 = new double[K][];
        for (int k = 0; k < K; k += 1) keyUsage400[k] = new double[baseCorners.Length];

        // Loop-invariant coefficient — hoisted out of the per-note loops.
        double k400Scale = 3.75 / (400.0 * 400.0);

        foreach (var n in noteSeq)
        {
            int k = (int)n[0];
            double h = n[1];
            int left400Idx = BisectLeft(baseCorners, h - 400);
            int centerIdx = BisectLeft(baseCorners, h);
            int right400Idx = BisectLeft(baseCorners, h + 400);

            if (centerIdx >= 0 && centerIdx < baseCorners.Length)
            {
                keyUsage400[k][centerIdx] += 3.75;
            }

            for (int idx = left400Idx; idx < centerIdx; idx += 1)
            {
                keyUsage400[k][idx] += 3.75 - k400Scale * ((baseCorners[idx] - h) * (baseCorners[idx] - h));
            }

            for (int idx = centerIdx + 1; idx < right400Idx; idx += 1)
            {
                keyUsage400[k][idx] += 3.75 - k400Scale * ((baseCorners[idx] - h) * (baseCorners[idx] - h));
            }
        }

        return keyUsage400;
    }

    private static double[] ComputeAnchor(int K, double[][] keyUsage400, double[] baseCorners)
    {
        var anchor = new double[baseCorners.Length];

        for (int idx = 0; idx < baseCorners.Length; idx += 1)
        {
            var counts = new double[K];
            for (int k = 0; k < K; k += 1) counts[k] = keyUsage400[k][idx];
            System.Array.Sort(counts, (a, b) => b.CompareTo(a));

            var nonZero = counts.Where(v => v > 0).ToArray();
            double raw = 0;
            if (nonZero.Length > 1)
            {
                double walk = 0;
                double maxWalk = 0;
                for (int i = 0; i < nonZero.Length - 1; i += 1)
                {
                    double ratio = nonZero[i + 1] / nonZero[i];
                    double weight = 1 - 4 * ((0.5 - ratio) * (0.5 - ratio));
                    walk += nonZero[i] * weight;
                    maxWalk += nonZero[i];
                }
                raw = maxWalk > 0 ? walk / maxWalk : 0;
            }

            anchor[idx] = 1 + Math.Min(raw - 0.18, 5 * ((raw - 0.22) * (raw - 0.22) * (raw - 0.22)));
        }

        return anchor;
    }

    private static (double[][] DeltaKs, double[] Jbar) ComputeJbar(int K, double x, List<double[]>[] noteSeqByColumn, double[] baseCorners)
    {
        // Loop-invariant term (depends only on x) — hoisted out of the per-note loop.
        double xPowQuarter = 0.11 * Math.Pow(x, 0.25);

        var jks = new double[K][];
        var deltaKs = new double[K][];
        for (int k = 0; k < K; k += 1)
        {
            jks[k] = new double[baseCorners.Length];
            deltaKs[k] = new double[baseCorners.Length];
            for (int i = 0; i < baseCorners.Length; i += 1) deltaKs[k][i] = 1e9;
        }

        for (int k = 0; k < K; k += 1)
        {
            var notes = noteSeqByColumn[k];
            for (int i = 0; i < notes.Count - 1; i += 1)
            {
                double start = notes[i][1];
                double end = notes[i + 1][1];
                if (end <= start) continue;

                int leftIdx = BisectLeft(baseCorners, start);
                int rightIdx = BisectLeft(baseCorners, end);
                if (leftIdx >= rightIdx) continue;

                double delta = 0.001 * (end - start);
                double val = Math.Pow(delta, -1) * Math.Pow(delta + xPowQuarter, -1) * JackNerfer(delta);

                for (int idx = leftIdx; idx < rightIdx; idx += 1)
                {
                    jks[k][idx] = val;
                    deltaKs[k][idx] = delta;
                }
            }
        }

        var jbarKs = new double[K][];
        for (int k = 0; k < K; k += 1)
        {
            jbarKs[k] = SmoothOnCorners(baseCorners, jks[k], 500, 0.001, "sum");
        }

        var jbar = new double[baseCorners.Length];
        for (int i = 0; i < baseCorners.Length; i += 1)
        {
            double num = 0;
            double den = 0;
            for (int k = 0; k < K; k += 1)
            {
                double v = jbarKs[k][i];
                double w = 1 / Math.Max(deltaKs[k][i], 1e-9);
                num += Math.Pow(Math.Max(v, 0), 5) * w;
                den += w;
            }
            jbar[i] = Math.Pow(num / Math.Max(den, 1e-9), 0.2);
        }

        return (deltaKs, jbar);
    }

    private static readonly double[][] CrossMatrix =
    {
        new double[] { -1 },
        new double[] { 0.075, 0.075 },
        new double[] { 0.125, 0.05, 0.125 },
        new double[] { 0.125, 0.125, 0.125, 0.125 },
        new double[] { 0.175, 0.25, 0.05, 0.25, 0.175 },
        new double[] { 0.175, 0.25, 0.175, 0.175, 0.25, 0.175 },
        new double[] { 0.225, 0.35, 0.25, 0.05, 0.25, 0.35, 0.225 },
        new double[] { 0.225, 0.35, 0.25, 0.225, 0.225, 0.25, 0.35, 0.225 },
        new double[] { 0.275, 0.45, 0.35, 0.25, 0.05, 0.25, 0.35, 0.45, 0.275 },
        new double[] { 0.275, 0.45, 0.35, 0.25, 0.275, 0.275, 0.25, 0.35, 0.45, 0.275 },
        new double[] { 0.325, 0.55, 0.45, 0.35, 0.25, 0.05, 0.25, 0.35, 0.45, 0.55, 0.325 },
    };

    private static double[] ComputeXbar(int K, double x, List<double[]>[] noteSeqByColumn, List<int>[] activeColumns, double[] baseCorners)
    {
        double[] crossCoeff;
        if (K >= 0 && K < CrossMatrix.Length)
        {
            crossCoeff = CrossMatrix[K];
        }
        else
        {
            crossCoeff = new double[K + 1];
            for (int i = 0; i < crossCoeff.Length; i += 1) crossCoeff[i] = 1.0 / (K + 1);
        }

        double CC(int k) => k >= 0 && k < crossCoeff.Length ? crossCoeff[k] : 0;

        var xks = new double[K + 1][];
        var fastCross = new double[K + 1][];
        for (int k = 0; k < K + 1; k += 1)
        {
            xks[k] = new double[baseCorners.Length];
            fastCross[k] = new double[baseCorners.Length];
        }

        for (int k = 0; k < K + 1; k += 1)
        {
            List<double[]> notesInPair;
            if (k == 0)
            {
                notesInPair = noteSeqByColumn[0];
            }
            else if (k == K)
            {
                notesInPair = noteSeqByColumn[K - 1];
            }
            else
            {
                notesInPair = MergeByHead(noteSeqByColumn[k - 1], noteSeqByColumn[k]);
            }

            for (int i = 1; i < notesInPair.Count; i += 1)
            {
                double start = notesInPair[i - 1][1];
                double end = notesInPair[i][1];
                if (end <= start) continue;

                int leftIdx = BisectLeft(baseCorners, start);
                int rightIdx = BisectLeft(baseCorners, end);
                if (rightIdx <= leftIdx) continue;

                double delta = 0.001 * (end - start);
                double val = 0.16 * Math.Pow(Math.Max(x, delta), -2);

                var leftCols = leftIdx >= 0 && leftIdx < activeColumns.Length ? activeColumns[leftIdx] : EmptyCols;
                var rightCols = rightIdx >= 0 && rightIdx < activeColumns.Length ? activeColumns[rightIdx] : EmptyCols;

                bool leftInactive = !leftCols.Contains(k - 1) && !rightCols.Contains(k - 1);
                bool rightInactive = !leftCols.Contains(k) && !rightCols.Contains(k);

                if (leftInactive || rightInactive)
                {
                    val *= 1 - CC(k);
                }

                double fastVal = Math.Max(0, 0.4 * Math.Pow(Math.Max(Math.Max(delta, 0.06), 0.75 * x), -2) - 80);

                for (int idx = leftIdx; idx < rightIdx; idx += 1)
                {
                    xks[k][idx] = val;
                    fastCross[k][idx] = fastVal;
                }
            }
        }

        var xBase = new double[baseCorners.Length];
        for (int i = 0; i < baseCorners.Length; i += 1)
        {
            double sum1 = 0;
            double sum2 = 0;
            for (int k = 0; k < K + 1; k += 1)
            {
                sum1 += xks[k][i] * CC(k);
            }
            for (int k = 0; k < K; k += 1)
            {
                double pair = fastCross[k][i] * CC(k) * fastCross[k + 1][i] * CC(k + 1);
                if (pair > 0)
                {
                    sum2 += Math.Sqrt(pair);
                }
            }
            xBase[i] = sum1 + sum2;
        }

        return SmoothOnCorners(baseCorners, xBase, 500, 0.001, "sum");
    }

    private static readonly List<int> EmptyCols = new();

    private static double[] ComputePbar(double x, List<double[]> noteSeq, double[] anchor, double[] baseCorners)
    {
        static double StreamBooster(double delta)
        {
            double bpm = Math.Max(0, Math.Min(7.5 / Math.Max(delta, 1e-9), 420));
            double primary = 0.10 / (1 + Math.Exp(-0.06 * (bpm - 175)));
            double secondary = (bpm >= 200 && bpm <= 350)
                ? 0.30 * (1 - Math.Exp(-0.02 * (bpm - 200)))
                : 0;
            return 1 + primary + secondary;
        }

        var pStep = new double[baseCorners.Length];

        // Loop-invariant terms (depend only on x) — hoisted out of the per-note loop.
        double xInv = Math.Pow(x, -1);
        double xSixth = x / 6;
        double xHalf = x / 2;
        double twoThirdsX = (2 * x) / 3;
        double baseInc = Math.Pow(0.08 * xInv * (1 - 24 * xInv * (xSixth * xSixth)), 0.25);
        double spike = 1000 * Math.Pow(0.02 * (4 / x - 24), 0.25);

        for (int i = 0; i < noteSeq.Count - 1; i += 1)
        {
            double hL = noteSeq[i][1];
            double hR = noteSeq[i + 1][1];
            double deltaTime = hR - hL;

            if (deltaTime < 1e-9)
            {
                int leftIdx0 = BisectLeft(baseCorners, hL);
                int rightIdx0 = BisectRight(baseCorners, hL);
                for (int idx = leftIdx0; idx < rightIdx0; idx += 1)
                {
                    pStep[idx] += spike;
                }
                continue;
            }

            int leftIdx = BisectLeft(baseCorners, hL);
            int rightIdx = BisectLeft(baseCorners, hR);
            if (rightIdx <= leftIdx) continue;

            double delta = 0.001 * deltaTime;
            double bVal = StreamBooster(delta);

            double inc;
            if (delta < twoThirdsX)
            {
                inc = Math.Pow(delta, -1)
                    * Math.Pow(0.08 * xInv * (1 - 24 * xInv * ((delta - xHalf) * (delta - xHalf))), 0.25)
                    * Math.Max(bVal, 1);
            }
            else
            {
                inc = Math.Pow(delta, -1) * baseInc * Math.Max(bVal, 1);
            }

            for (int idx = leftIdx; idx < rightIdx; idx += 1)
            {
                double boosted = inc * anchor[idx];
                pStep[idx] += Math.Min(boosted, Math.Max(inc, inc * 2 - 10));
            }
        }

        return SmoothOnCorners(baseCorners, pStep, 500, 0.001, "sum");
    }

    private static double[] ComputeAbar(int K, List<int>[] activeColumns, double[][] deltaKs, double[] aCorners, double[] baseCorners)
    {
        var dks = new double[K - 1][];
        for (int k = 0; k < K - 1; k += 1) dks[k] = new double[baseCorners.Length];

        for (int i = 0; i < baseCorners.Length; i += 1)
        {
            var cols = activeColumns[i];
            for (int j = 0; j < cols.Count - 1; j += 1)
            {
                int k0 = cols[j];
                int k1 = cols[j + 1];
                dks[k0][i] = Math.Abs(deltaKs[k0][i] - deltaKs[k1][i])
                    + 0.4 * Math.Max(0, Math.Max(deltaKs[k0][i], deltaKs[k1][i]) - 0.11);
            }
        }

        var aStep = new double[aCorners.Length];
        for (int i = 0; i < aStep.Length; i += 1) aStep[i] = 1;

        for (int i = 0; i < aCorners.Length; i += 1)
        {
            int idx = BisectLeft(baseCorners, aCorners[i]);
            idx = Math.Max(0, Math.Min(idx, baseCorners.Length - 1));

            var cols = idx >= 0 && idx < activeColumns.Length ? activeColumns[idx] : EmptyCols;
            for (int j = 0; j < cols.Count - 1; j += 1)
            {
                int k0 = cols[j];
                int k1 = cols[j + 1];
                double dVal = dks[k0][idx];
                double dk0 = deltaKs[k0][idx];
                double dk1 = deltaKs[k1][idx];

                if (dVal < 0.02)
                {
                    aStep[i] *= Math.Min(0.75 + 0.5 * Math.Max(dk0, dk1), 1);
                }
                else if (dVal < 0.07)
                {
                    aStep[i] *= Math.Min(0.65 + 5 * dVal + 0.5 * Math.Max(dk0, dk1), 1);
                }
            }
        }

        return SmoothOnCorners(aCorners, aStep, 250, 1.0, "avg");
    }

    public static DanielCalcResult CalculateDaniel(string osuText, double speedRate = 1.0, object? odFlag = null,
        bool withGraph = false, OsuFileParser? parsed = null)
    {
        var pre = PreprocessDaniel(osuText, speedRate, parsed);

        if (pre.Status == "Fail") return new DanielCalcResult { Code = -1 };
        if (pre.Status == "NotMania") return new DanielCalcResult { Code = -2 };
        if (pre.Status == "UnsupportedKeys") return new DanielCalcResult { Code = -3 };

        int K = pre.K;
        double T = pre.T;
        var noteSeq = pre.NoteSeq;
        var noteSeqByColumn = pre.NoteSeqByColumn;
        double x = pre.X;
        double lnRatio = pre.LnRatio;
        double columnCount = pre.ColumnCount;

        if (noteSeq.Count == 0 || K <= 0 || T <= 0) return new DanielCalcResult { Code = -1 };

        var (allCorners, baseCorners, aCorners) = GetCorners(T, noteSeq);

        var keyUsage = GetKeyUsage(K, T, noteSeq, baseCorners);
        var activeColumns = new List<int>[baseCorners.Length];
        for (int i = 0; i < baseCorners.Length; i += 1)
        {
            var active = new List<int>();
            for (int k = 0; k < K; k += 1)
            {
                if (keyUsage[k][i]) active.Add(k);
            }
            activeColumns[i] = active;
        }

        var keyUsage400 = GetKeyUsage400(K, noteSeq, baseCorners);
        var anchor = ComputeAnchor(K, keyUsage400, baseCorners);

        var (deltaKs, jbarBase) = ComputeJbar(K, x, noteSeqByColumn, baseCorners);
        var jbar = InterpValues(allCorners, baseCorners, jbarBase);

        var xbarBase = ComputeXbar(K, x, noteSeqByColumn, activeColumns, baseCorners);
        var xbar = InterpValues(allCorners, baseCorners, xbarBase);

        var pbarBase = ComputePbar(x, noteSeq, anchor, baseCorners);
        var pbar = InterpValues(allCorners, baseCorners, pbarBase);

        var abarBase = ComputeAbar(K, activeColumns, deltaKs, aCorners, baseCorners);
        var abar = InterpValues(allCorners, aCorners, abarBase);

        var (cStep, ksStep) = ComputeCAndKs(K, noteSeq, keyUsage, baseCorners);
        var cArr = StepInterp(allCorners, baseCorners, cStep);
        var ksArr = StepInterp(allCorners, baseCorners, ksStep);

        var dAll = new double[allCorners.Length];
        for (int i = 0; i < allCorners.Length; i += 1)
        {
            double leftPart = 0.4 * Math.Pow(Math.Pow(abar[i], 3.0 / ksArr[i]) * Math.Min(jbar[i], 8 + 0.85 * jbar[i]), 1.5);
            double rightPart = 0.6 * Math.Pow(Math.Pow(abar[i], 2.0 / 3) * (0.8 * pbar[i]), 1.5);
            double sAll = Math.Pow(leftPart + rightPart, 2.0 / 3);
            double tAll = (Math.Pow(abar[i], 3.0 / ksArr[i]) * xbar[i]) / (xbar[i] + sAll + 1);
            dAll[i] = 2.7 * Math.Pow(sAll, 0.5) * Math.Pow(tAll, 1.5) + sAll * 0.27;
        }

        var gaps = new double[allCorners.Length];
        if (allCorners.Length >= 2)
        {
            gaps[0] = (allCorners[1] - allCorners[0]) / 2;
            gaps[gaps.Length - 1] = (allCorners[allCorners.Length - 1] - allCorners[allCorners.Length - 2]) / 2;
            for (int i = 1; i < allCorners.Length - 1; i += 1)
            {
                gaps[i] = (allCorners[i + 1] - allCorners[i - 1]) / 2;
            }
        }

        var effectiveWeights = new double[cArr.Length];
        for (int i = 0; i < cArr.Length; i += 1) effectiveWeights[i] = cArr[i] * gaps[i];

        // PORT NOTE: JS Array#sort is stable and its `(a,b)=>DAll[a]-DAll[b]`
        // comparator returns NaN for NaN entries (treated as "keep order").
        // OrderBy is stable too, but C#'s double comparer sorts NaN first.
        // Well-formed charts produce no NaN here.
        var sortedIndices = Enumerable.Range(0, dAll.Length).OrderBy(i => dAll[i]).ToArray();
        var dSorted = sortedIndices.Select(i => dAll[i]).ToArray();
        var wSorted = sortedIndices.Select(i => effectiveWeights[i]).ToArray();

        var cumWeights = new double[wSorted.Length];
        double running = 0;
        for (int i = 0; i < wSorted.Length; i += 1)
        {
            running += wSorted[i];
            cumWeights[i] = running;
        }

        double totalWeight = cumWeights.Length > 0 ? cumWeights[cumWeights.Length - 1] : 0;
        if (!double.IsFinite(totalWeight) || totalWeight <= 0)
        {
            if (withGraph)
            {
                return new DanielCalcResult
                {
                    Star = 0,
                    LnRatio = lnRatio,
                    ColumnCount = columnCount,
                    Graph = ((double[])allCorners.Clone(), new double[allCorners.Length]),
                };
            }
            return new DanielCalcResult { Star = 0, LnRatio = lnRatio, ColumnCount = columnCount };
        }

        var normCumWeights = new double[cumWeights.Length];
        for (int i = 0; i < cumWeights.Length; i += 1) normCumWeights[i] = cumWeights[i] / totalWeight;

        var percentileIndices = TargetPercentiles.Select(p => BisectLeft(normCumWeights, p)).ToArray();

        double GroupAvg(int from, int to)
        {
            double sum = 0;
            int count = 0;
            for (int j = from; j < to; j += 1)
            {
                int idx = percentileIndices[j];
                sum += dSorted[Math.Min(idx, dSorted.Length - 1)];
                count += 1;
            }
            return sum / count;
        }

        double percentile93 = GroupAvg(0, 4);
        double percentile83 = GroupAvg(4, 8);

        double num = 0;
        double den = 0;
        for (int i = 0; i < dSorted.Length; i += 1)
        {
            num += Math.Pow(dSorted[i], 5) * wSorted[i];
            den += wSorted[i];
        }
        double weightedMean = Math.Pow(num / Math.Max(den, 1e-9), 0.2);

        double sr = (0.88 * percentile93) * 0.25 + (0.94 * percentile83) * 0.2 + weightedMean * 0.55;
        sr *= (double)noteSeq.Count / (noteSeq.Count + 60);
        sr = RescaleHigh(sr) * 0.975;

        if (withGraph)
        {
            var dPre = ApplyProximityEnvelope(allCorners, dAll, noteSeq);
            var dGraph = SmoothDForGraph(allCorners, dPre, noteSeq);
            return new DanielCalcResult
            {
                Star = sr,
                LnRatio = lnRatio,
                ColumnCount = columnCount,
                Graph = ((double[])allCorners.Clone(), dGraph),
            };
        }

        return new DanielCalcResult { Star = sr, LnRatio = lnRatio, ColumnCount = columnCount };
    }
}
