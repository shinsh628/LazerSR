// Port of mania-hub live-backend/src/dan/dan-estimator/features.ts
// Core mania feature extractor: density, pressure, pattern-shape and LN metrics.

using LazerSR.DanCalculator.Beatmap;
using LazerSR.DanCalculator.Types;

namespace LazerSR.DanCalculator.Features;

public static class DanFeatures
{
    private static List<ManiaNote> GetRatedNotes(ManiaBeatmap map, double rate)
    {
        var notes = new List<ManiaNote>();
        foreach (var note in map.Notes)
        {
            if (note.Column < 0 || note.Column >= map.KeyCount) continue;
            notes.Add(rate == 1 ? note : new ManiaNote
            {
                Column = note.Column,
                Time = note.Time / rate,
                EndTime = note.EndTime / rate,
                IsHold = note.IsHold,
            });
        }
        return notes;
    }

    private static List<(double Time, List<ManiaNote> Notes)> GroupNotesByTime(IEnumerable<ManiaNote> notes)
    {
        var rows = new Dictionary<double, List<ManiaNote>>();
        var order = new List<double>();
        foreach (var note in notes)
        {
            if (rows.TryGetValue(note.Time, out var row)) row.Add(note);
            else { rows[note.Time] = new List<ManiaNote> { note }; order.Add(note.Time); }
        }
        return order.Select(t => (t, rows[t])).OrderBy(pair => pair.t).ToList();
    }

    private static int BitCount(int value)
    {
        int count = 0;
        int remaining = value;
        while (remaining > 0)
        {
            count += remaining & 1;
            remaining >>= 1;
        }
        return count;
    }

    private static double NumericNgramRepeatRatio(IReadOnlyList<double> values, int size, double @base)
    {
        if (values.Count < size + 1) return 0;

        var seen = new HashSet<double>();
        double repeated = 0;
        double total = 0;
        for (int index = 0; index <= values.Count - size; index++)
        {
            double key = 0;
            for (int offset = 0; offset < size; offset++)
            {
                key = key * @base + values[index + offset];
            }
            if (seen.Contains(key)) repeated++;
            else seen.Add(key);
            total++;
        }

        return total != 0 ? repeated / total : 0;
    }

    private static double AdjacentNgramRepeatRatio(IReadOnlyList<double> values, int size)
    {
        if (values.Count < size * 2) return 0;

        double repeated = 0;
        double total = 0;
        for (int index = size; index <= values.Count - size; index++)
        {
            total++;
            bool matches = true;
            for (int offset = 0; offset < size; offset++)
            {
                if (values[index + offset] != values[index - size + offset])
                {
                    matches = false;
                    break;
                }
            }
            if (matches) repeated++;
        }

        return total != 0 ? repeated / total : 0;
    }

    private static List<double> SampledWindowNps(IReadOnlyList<double> noteTimes, double windowMs, double durationMs)
    {
        if (noteTimes.Count == 0 || durationMs <= 0) return new List<double>();

        var samples = new List<double>();
        int left = 0;
        int right = 0;
        for (double start = 0; start <= durationMs; start += 1000)
        {
            while (left < noteTimes.Count && noteTimes[left] < start) left++;
            while (right < noteTimes.Count && noteTimes[right] < start + windowMs) right++;
            samples.Add((right - left) / (windowMs / 1000));
        }
        return samples;
    }

    // Beat fractions a row can sit at before it counts as off the 16th grid.
    private static readonly int[] SNAP_DIVISORS = { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32 };

    /// <summary>Smallest divisor d with the gap at k/d beats (k >= 1) inside 2ms + 1%, or 0 when nothing fits.</summary>
    private static int GapSnapDivisor(double gapMs, double beatLengthMs)
    {
        double tolerance = 2 + gapMs * 0.01;
        foreach (var divisor in SNAP_DIVISORS)
        {
            double unit = beatLengthMs / divisor;
            double steps = JsRound(gapMs / unit);
            if (steps >= 1 && Math.Abs(steps * unit - gapMs) <= tolerance) return divisor;
        }
        return 0;
    }

    /// <summary>
    /// Share of note rows off the 16th grid: the gap back to the previous row is
    /// 1/6, 1/8, 1/12, 1/16, 1/24 or 1/32 of the beat at that point, an off-snap
    /// gap inside half a beat, or 55ms and under whatever the grid says (charts
    /// timed at double tempo put their 1/4 there). This is the shape 7K calls
    /// delay: 1/8 and 1/12 flow at 128-150 BPM in the BMS delay packs, 1/6 at
    /// 158-177 in the 7777's practice packs, all of it single notes and small
    /// chords staggered across the columns. Measured 2026-09-03 over 232
    /// delay-named 7K charts (p10 0.43 / p50 0.74) against jack (p90 0.12),
    /// stream (p90 0.10) and 700 random 7K (p50 0.07 / p75 0.23). Read at the
    /// chart's own tempo, so rate leaves it alone. Rows more than a beat apart
    /// count toward the denominator but never as off-grid.
    /// </summary>
    private static double OffGridRowShare(ManiaBeatmap map)
    {
        var timingPoints = (map.TimingPoints ?? new List<ManiaTimingPoint>())
            .Where(point => double.IsFinite(point.Time) && point.BeatLength > 0)
            .OrderBy(point => point.Time)
            .ToList();
        double fallbackBeatLength = map.Bpm > 0 ? 60000 / map.Bpm : double.PositiveInfinity;
        var rows = GroupNotesByTime(map.Notes.Where(note => note.Column >= 0 && note.Column < map.KeyCount));
        if (rows.Count < 2) return 0;
        double offGrid = 0;
        int timingIndex = 0;
        for (int i = 1; i < rows.Count; i++)
        {
            double time = rows[i].Time;
            while (timingIndex + 1 < timingPoints.Count && timingPoints[timingIndex + 1].Time <= time + 1) timingIndex++;
            double beatLength = timingPoints.Count > 0 && timingPoints[timingIndex].Time <= time + 1
                ? timingPoints[timingIndex].BeatLength
                : (timingPoints.Count > 0 ? timingPoints[0].BeatLength : fallbackBeatLength);
            double gap = time - rows[i - 1].Time;
            if (gap <= 55) { offGrid++; continue; }
            if (gap > beatLength) continue;
            int divisor = GapSnapDivisor(gap, beatLength);
            if (divisor >= 6 || (divisor == 0 && gap <= beatLength / 2)) offGrid++;
        }
        return offGrid / rows.Count;
    }

    public static DanFeatureExtractionResult ExtractDanFeatures(ManiaBeatmap map, DanEstimateInput input, double rate)
    {
        var notes = GetRatedNotes(map, rate);
        var warnings = new List<string>();
        var noteTimes = new List<double>();
        var releaseTimes = new List<double>();
        var holdDurations = new List<double>();
        var holdEvents = new List<(double Time, int Delta)>();
        int holdNoteCount = 0;
        double totalHoldMs = 0;
        bool noteTimesSorted = true;
        bool releaseTimesSorted = true;
        double previousNoteTime = double.NegativeInfinity;
        double previousReleaseTime = double.NegativeInfinity;

        foreach (var note in notes)
        {
            if (note.Time < previousNoteTime) noteTimesSorted = false;
            previousNoteTime = note.Time;
            noteTimes.Add(note.Time);

            if (note.IsHold)
            {
                holdNoteCount++;
                if (note.EndTime > note.Time)
                {
                    double holdDuration = note.EndTime - note.Time;
                    holdDurations.Add(holdDuration);
                    totalHoldMs += holdDuration;
                    if (note.EndTime < previousReleaseTime) releaseTimesSorted = false;
                    previousReleaseTime = note.EndTime;
                    releaseTimes.Add(note.EndTime);
                    holdEvents.Add((note.Time, 1));
                    holdEvents.Add((note.EndTime, -1));
                }
            }
        }

        if (!noteTimesSorted) noteTimes.Sort();
        if (!releaseTimesSorted) releaseTimes.Sort();
        double durationMs = Math.Max(
            Math.Max(input.TotalLength.HasValue && input.TotalLength.Value != 0 ? (input.TotalLength.Value * 1000) / rate : 0, map.TotalLength / rate),
            noteTimes.Count > 0 ? noteTimes[^1] : 0);

        if (notes.Count < 50 || durationMs <= 0)
        {
            warnings.Add("This map has very little note data, so the estimate is low confidence.");
        }

        var orderedRows = GroupNotesByTime(notes);
        double holdRatio = notes.Count != 0 ? (double)holdNoteCount / notes.Count : 0;

        int keyLen = Math.Max(1, map.KeyCount);
        var lastByColumn = new double[keyLen];
        for (int i = 0; i < keyLen; i++) lastByColumn[i] = double.NegativeInfinity;
        var jackValues = new List<double>();
        var streamValues = new List<double>();
        var jumpstreamValues = new List<double>();
        var rowDensities = new List<double>();
        var rowDensityWeights = new List<double>();
        var rowIntervals = new List<double>();
        var rowRates = new List<double>();
        var columnIntervals = new List<double>();
        var columnRates = new List<double>();
        var tailIntervals = new List<double>();
        var tailRates = new List<double>();
        var columnCounts = new int[keyLen];
        int chordRows = 0;
        int twoNoteChordRows = 0;
        int holdRows = 0;
        int lnChordRows = 0;
        double longGapCount = 0;
        double longGapMs = 0;
        int fastRowCount = 0;
        int directionChanges = 0;
        int? previousColumn = null;
        int previousDirection = 0;
        int previousChordSize = 0;
        int chordSizeChanges = 0;
        double? previousRowTime = null;
        int? previousRowMask = null;
        int? rowMaskTwoBack = null;
        int repeatedRowPatterns = 0;
        int alternatingRowPatterns = 0;
        int chordPairCount = 0;
        int chordPairOverlapCount = 0;
        double adjacentColumnRehitNotes = 0;
        double twoBackColumnRehitNotes = 0;
        double rowPatternChangeSum = 0;
        var rowMasks = new List<double>();
        var rowTimes = new List<double>();
        var rowSignatures = new List<double>();
        double rowMaskBase = Math.Max(32, Math.Pow(2, Math.Max(1, map.KeyCount)) + 1);
        double rowSignatureBase = rowMaskBase * 256;

        foreach (var (time, rowNotes) in orderedRows)
        {
            var columns = new List<int>();
            int rowMask = 0;
            bool rowHasHold = false;
            foreach (var note in rowNotes)
            {
                columns.Add(note.Column);
                rowMask |= 1 << note.Column;
                if (note.IsHold) rowHasHold = true;
            }
            columns.Sort();

            if (columns.Count >= 2) chordRows++;
            if (columns.Count == 2) twoNoteChordRows++;
            if (rowHasHold)
            {
                holdRows++;
                if (columns.Count >= 2) lnChordRows++;
            }

            // Chord-jack repetition: of adjacent chord rows (<1s apart, both >= 2
            // notes), how many re-hit a column. True chordjack repeats columns on
            // consecutive chords; dense bracket/jumpstream alternates hands and
            // barely overlaps at the same chord density. Must run before the
            // previous-row state updates below.
            if (
                previousRowMask != null && previousChordSize >= 2 && columns.Count >= 2
                && previousRowTime != null && time - previousRowTime.Value < 1000)
            {
                chordPairCount++;
                if ((rowMask & previousRowMask.Value) != 0) chordPairOverlapCount++;
            }

            rowMasks.Add(rowMask);
            rowTimes.Add(time);
            if (previousRowMask != null)
            {
                if (previousRowTime != null && time - previousRowTime.Value <= 500)
                {
                    adjacentColumnRehitNotes += BitCount(rowMask & previousRowMask.Value);
                }
                if (rowMask == previousRowMask.Value) repeatedRowPatterns++;
                rowPatternChangeSum += (double)BitCount(rowMask ^ previousRowMask.Value) / Math.Max(1, map.KeyCount);
            }
            if (rowMaskTwoBack != null && rowTimes.Count >= 3 && time - rowTimes[rowTimes.Count - 3] <= 500)
            {
                twoBackColumnRehitNotes += BitCount(rowMask & rowMaskTwoBack.Value);
            }
            if (rowMaskTwoBack != null && rowMask == rowMaskTwoBack.Value) alternatingRowPatterns++;
            rowMaskTwoBack = previousRowMask;
            previousRowMask = rowMask;
            foreach (var column in columns)
            {
                columnCounts[column]++;
            }
            if (previousChordSize != 0 && previousChordSize != columns.Count) chordSizeChanges++;
            int intervalBucket = 0;
            if (previousRowTime != null)
            {
                double rowDelta = time - previousRowTime.Value;
                if (rowDelta >= 2500)
                {
                    longGapCount++;
                    longGapMs += rowDelta;
                }
                if (rowDelta > 0 && rowDelta < 1200)
                {
                    rowIntervals.Add(rowDelta);
                    rowRates.Add(1000 / rowDelta);
                    rowDensities.Add((columns.Count * 1000) / rowDelta);
                    rowDensityWeights.Add(Math.Max(1, rowDelta));
                    if (columns.Count == 2) jumpstreamValues.Add(Math.Min(60, (columns.Count * 1000) / rowDelta));
                    if (rowDelta <= 80) fastRowCount++;
                    intervalBucket = (int)JsRound(rowDelta / 5);
                }
                else
                {
                    intervalBucket = -1;
                }
            }
            rowSignatures.Add(rowMask * 256 + intervalBucket + 1);
            previousChordSize = columns.Count;
            previousRowTime = time;

            foreach (var column in columns)
            {
                double sameDelta = time - lastByColumn[column];
                if (sameDelta > 0 && sameDelta < 1000)
                {
                    jackValues.Add(Math.Min(230, 15000 / sameDelta));
                }
                if (sameDelta > 0 && sameDelta < 1600)
                {
                    columnIntervals.Add(sameDelta);
                    columnRates.Add(1000 / sameDelta);
                }

                int leftNeighbor = column - 1;
                if (leftNeighbor >= 0)
                {
                    double delta = time - lastByColumn[leftNeighbor];
                    if (delta > 0 && delta < 260)
                    {
                        streamValues.Add((260 - delta) / 35);
                    }
                }
                int rightNeighbor = column + 1;
                if (rightNeighbor < map.KeyCount)
                {
                    double delta = time - lastByColumn[rightNeighbor];
                    if (delta > 0 && delta < 260)
                    {
                        streamValues.Add((260 - delta) / 35);
                    }
                }

                if (previousColumn != null)
                {
                    int direction = Math.Sign(column - previousColumn.Value);
                    if (direction != 0 && previousDirection != 0 && direction != previousDirection) directionChanges++;
                    if (direction != 0) previousDirection = direction;
                }
                previousColumn = column;
                lastByColumn[column] = time;
            }
        }

        for (int i = 1; i < releaseTimes.Count; i++)
        {
            double tailDelta = releaseTimes[i] - releaseTimes[i - 1];
            if (tailDelta > 0 && tailDelta < 1600)
            {
                tailIntervals.Add(tailDelta);
                tailRates.Add(1000 / tailDelta);
            }
        }

        double chordRatio = orderedRows.Count != 0 ? (double)chordRows / orderedRows.Count : 0;
        double twoNoteChordRatio = orderedRows.Count != 0 ? (double)twoNoteChordRows / orderedRows.Count : 0;
        double peakNps1s = DanMath.CountInWindow(noteTimes, 1000);
        double peakNps5s = DanMath.CountInWindow(noteTimes, 5000) / 5.0;
        var nps5sSamples = SampledWindowNps(noteTimes, 5000, durationMs);
        var nps5sQ = DanMath.Quantiles(nps5sSamples, new double[] { 0.5, 0.9, 0.95 });
        double nps5sP50 = nps5sQ[0], nps5sP90 = nps5sQ[1], nps5sP95 = nps5sQ[2];
        double sustainedNps10s = DanMath.CountInWindow(noteTimes, 10000) / 10.0;
        double sustainedNps30s = DanMath.CountInWindow(noteTimes, 30000) / 30.0;
        double sustainedNps60s = DanMath.CountInWindow(noteTimes, 60000) / 60.0;
        double noteSpanMs = noteTimes.Count >= 2 ? Math.Max(1, noteTimes[noteTimes.Count - 1] - noteTimes[0]) : durationMs;
        double activeNps = notes.Count / Math.Max(1, noteSpanMs / 1000);
        double longGapRatio = durationMs > 0 ? longGapMs / durationMs : 0;
        double jackPressure = DanMath.Quantile(jackValues, 0.92);
        double streamPressure = DanMath.Quantile(streamValues, 0.9);
        double jumpstreamPressure = DanMath.Quantile(jumpstreamValues, 0.9);
        double burstDensity = DanMath.Quantile(rowDensities, 0.9);
        double rowBurstPressure = DanMath.Quantile(rowRates, 0.9);
        double fastRowRatio = rowIntervals.Count != 0
            ? (double)fastRowCount / rowIntervals.Count
            : 0;
        double rowIntervalEntropy = DanMath.BucketEntropy(rowIntervals, 5);
        double offGridShare = OffGridRowShare(map);
        double rowPatternEntropy = DanMath.BucketEntropy(rowMasks, 1);
        double rowPatternVariety = rowMasks.Count != 0
            ? rowMasks.Distinct().Count() / Math.Min(rowMasks.Count, Math.Pow(2, Math.Max(1, map.KeyCount)))
            : 0;
        double rowMotifRepeatRatio = NumericNgramRepeatRatio(rowMasks, 4, rowMaskBase);
        double rhythmMotifRepeatRatio = NumericNgramRepeatRatio(rowSignatures, 4, rowSignatureBase);
        double adjacentMotifRepeatRatio = AdjacentNgramRepeatRatio(rowSignatures, 4);
        double repeatedRowPatternRatio = orderedRows.Count > 1 ? (double)repeatedRowPatterns / (orderedRows.Count - 1) : 0;
        double alternatingRowPatternRatio = orderedRows.Count > 2 ? (double)alternatingRowPatterns / (orderedRows.Count - 2) : 0;
        double rowPatternChangeRate = orderedRows.Count > 1 ? rowPatternChangeSum / (orderedRows.Count - 1) : 0;
        double patternVariety = 0.5 * DanMath.RaoQuadraticEntropyLog(DanMath.BucketValues(rowIntervals, 5), 1)
            + 1.125 * DanMath.RaoQuadraticEntropyLog(DanMath.BucketValues(columnIntervals, 5), 2)
            + 0.11 * DanMath.RaoQuadraticEntropyLog(DanMath.BucketValues(tailIntervals, 5), 1);
        double spikiness = DanMath.StrainSpikiness(rowDensities, rowDensityWeights);
        double sustainedPressureRatio = sustainedNps10s / Math.Max(Math.Max(1, peakNps1s), peakNps5s);
        double averageColumnCount = DanMath.Average(columnCounts.Select(c => (double)c).ToList());
        double columnImbalance = averageColumnCount > 0
            ? columnCounts.Sum(count => Math.Abs(count - averageColumnCount)) / (columnCounts.Length * averageColumnCount)
            : 0;
        double anchorPressure = columnImbalance * (0.5 + Math.Min(1.5, jackPressure / 150)) + Math.Max(0, DanMath.Quantile(columnRates, 0.9) - 7) * 0.04;
        double lnReleasePressure = releaseTimes.Count != 0
            ? DanMath.CountInWindow(releaseTimes, 5000) / 5.0 + DanMath.Quantile(tailRates, 0.9) * 0.15
            : 0;
        double lnDensity = durationMs > 0 ? totalHoldMs / (durationMs * Math.Max(1, map.KeyCount)) : 0;
        double lnChordPressure = holdRows != 0 ? (double)lnChordRows / holdRows : 0;
        double lnHoldDurationAvg = DanMath.Average(holdDurations);
        double lnHoldDurationP90 = DanMath.Quantile(holdDurations, 0.9);
        holdEvents = holdEvents.OrderBy(e => e.Time).ThenByDescending(e => e.Delta).ToList();
        double activeHolds = 0;
        double activeHoldPeak = 0;
        double activeHoldArea = 0;
        double previousHoldEventTime = holdEvents.Count > 0 ? holdEvents[0].Time : 0;
        foreach (var ev in holdEvents)
        {
            activeHoldArea += Math.Max(0, ev.Time - previousHoldEventTime) * activeHolds;
            activeHolds = Math.Max(0, activeHolds + ev.Delta);
            activeHoldPeak = Math.Max(activeHoldPeak, activeHolds);
            previousHoldEventTime = ev.Time;
        }
        double averageActiveHolds = durationMs > 0 ? activeHoldArea / durationMs : 0;
        double lnOverlapPressure = averageActiveHolds + activeHoldPeak * 0.35;
        double chordSizeChangeRate = orderedRows.Count != 0 ? (double)chordSizeChanges / orderedRows.Count : 0;
        double directionChangeRate = notes.Count != 0 ? (double)directionChanges / notes.Count : 0;
        double chordjackPressure = jackPressure * (0.28 + chordRatio * 1.35) + burstDensity * chordRatio * 0.6;
        double chordColumnOverlapRatio = chordPairCount != 0 ? (double)chordPairOverlapCount / chordPairCount : 0;
        double adjacentColumnRehitShare = notes.Count != 0 ? adjacentColumnRehitNotes / notes.Count : 0;
        double twoBackColumnRehitShare = notes.Count != 0 ? twoBackColumnRehitNotes / notes.Count : 0;
        double twoBackColumnRehitExcess = twoBackColumnRehitShare - adjacentColumnRehitShare;
        double techPressure = orderedRows.Count != 0
            ? ((double)directionChanges / orderedRows.Count) * 4.4 + ((double)chordSizeChanges / orderedRows.Count) * 3.5 + chordRatio * 1.6 + DanMath.Average(rowDensities) * 0.018
            : 0;

        return new DanFeatureExtractionResult
        {
            Notes = notes,
            NoteTimes = noteTimes,
            DurationMs = durationMs,
            OrderedRows = orderedRows,
            Warnings = warnings,
            Metrics = new DanFeatureMetrics
            {
                KeyCount = map.KeyCount,
                NoteCount = notes.Count,
                DurationMs = durationMs,
                HoldRatio = holdRatio,
                ChordRatio = chordRatio,
                TwoNoteChordRatio = twoNoteChordRatio,
                PeakNps1s = peakNps1s,
                PeakNps5s = peakNps5s,
                Nps5sP50 = nps5sP50,
                Nps5sP90 = nps5sP90,
                Nps5sP95 = nps5sP95,
                SustainedNps10s = sustainedNps10s,
                SustainedNps30s = sustainedNps30s,
                SustainedNps60s = sustainedNps60s,
                ActiveNps = activeNps,
                LongGapRatio = longGapRatio,
                LongGapCount = longGapCount,
                JackPressure = jackPressure,
                StreamPressure = streamPressure,
                JumpstreamPressure = jumpstreamPressure,
                ChordjackPressure = chordjackPressure,
                ChordColumnOverlapRatio = chordColumnOverlapRatio,
                AdjacentColumnRehitShare = adjacentColumnRehitShare,
                TwoBackColumnRehitShare = twoBackColumnRehitShare,
                TwoBackColumnRehitExcess = twoBackColumnRehitExcess,
                TechPressure = techPressure,
                RowBurstPressure = rowBurstPressure,
                FastRowRatio = fastRowRatio,
                RowIntervalEntropy = rowIntervalEntropy,
                OffGridRowShare = offGridShare,
                PatternVariety = patternVariety,
                RowPatternEntropy = rowPatternEntropy,
                RowPatternVariety = rowPatternVariety,
                RepeatedRowPatternRatio = repeatedRowPatternRatio,
                AlternatingRowPatternRatio = alternatingRowPatternRatio,
                RowPatternChangeRate = rowPatternChangeRate,
                RowMotifRepeatRatio = rowMotifRepeatRatio,
                RhythmMotifRepeatRatio = rhythmMotifRepeatRatio,
                AdjacentMotifRepeatRatio = adjacentMotifRepeatRatio,
                StrainSpikiness = spikiness,
                SustainedPressureRatio = sustainedPressureRatio,
                AnchorPressure = anchorPressure,
                LnReleasePressure = lnReleasePressure,
                LnDensity = lnDensity,
                LnOverlapPressure = lnOverlapPressure,
                LnChordPressure = lnChordPressure,
                LnHoldDurationAvg = lnHoldDurationAvg,
                LnHoldDurationP90 = lnHoldDurationP90,
                ChordSizeChangeRate = chordSizeChangeRate,
                DirectionChangeRate = directionChangeRate,
                StaminaPressure = sustainedNps10s,
            },
        };
    }

    // JS Math.round: round half toward +Infinity.
    private static double JsRound(double v) => Math.Floor(v + 0.5);
}
