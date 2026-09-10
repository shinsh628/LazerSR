// Port of mania-hub live-backend/src/dan/dan-estimator/patterns.ts
// Mania pattern analyzer: scores the rice + LN pattern families for a chart.

using System.Globalization;
using LazerSR.DanCalculator.Beatmap;
using LazerSR.DanCalculator.Classifier;
using LazerSR.DanCalculator.Types;

namespace LazerSR.DanCalculator.Features;

public static class DanPatterns
{
    public static readonly Dictionary<ManiaPatternId, string> MANIA_PATTERN_ANALYZER_LABELS = new()
    {
        [ManiaPatternId.Jack] = "Jack",
        [ManiaPatternId.Chordjack] = "Chordjack",
        [ManiaPatternId.Speedjack] = "Speedjack",
        [ManiaPatternId.Handjack] = "Handjack",
        [ManiaPatternId.Tech] = "Tech",
        [ManiaPatternId.Stream] = "Stream",
        [ManiaPatternId.Dumpstream] = "Dumpstream",
        [ManiaPatternId.Jumpstream] = "Jumpstream",
        [ManiaPatternId.Handstream] = "Handstream",
        [ManiaPatternId.Quadstream] = "Quadstream",
        [ManiaPatternId.Delay] = "Delay",
        [ManiaPatternId.Bracket] = "Bracket",
        [ManiaPatternId.Chordstream] = "Chordstream",
        [ManiaPatternId.Ln] = "LN",
        [ManiaPatternId.Lngeneral] = "LN General",
        [ManiaPatternId.Lnrelease] = "LN Release",
        [ManiaPatternId.Lninverse] = "LN Inverse",
        [ManiaPatternId.Lntech] = "LN Tech",
    };

    public static readonly ManiaPatternId[] SUPPORTED_MANIA_PATTERN_IDS =
    {
        ManiaPatternId.Jack, ManiaPatternId.Chordjack, ManiaPatternId.Speedjack, ManiaPatternId.Handjack,
        ManiaPatternId.Tech, ManiaPatternId.Stream, ManiaPatternId.Dumpstream, ManiaPatternId.Jumpstream,
        ManiaPatternId.Handstream, ManiaPatternId.Quadstream, ManiaPatternId.Delay, ManiaPatternId.Bracket,
        ManiaPatternId.Chordstream, ManiaPatternId.Ln, ManiaPatternId.Lngeneral, ManiaPatternId.Lnrelease,
        ManiaPatternId.Lninverse, ManiaPatternId.Lntech,
    };

    // The LN axis subfamilies, as opposed to the primary rice families. Fired on 4K
    // and 7K only; see the subtype gate in analyzeManiaPatterns.
    private static readonly HashSet<ManiaPatternId> LN_SUBTYPE_IDS = new()
    {
        ManiaPatternId.Lngeneral, ManiaPatternId.Lnrelease, ManiaPatternId.Lninverse, ManiaPatternId.Lntech,
    };

    private sealed class RowPatternStats
    {
        public int RowCount;
        public int ChordRows;
        public int TwoNoteRows;
        public int ThreeNoteRows;
        public int FourPlusRows;
        public int ThreePlusRows;
        public int SingleRows;
        public int RepeatedChordRows;
        public int BracketWindowRows;
        public double AverageChordSize;
        public int ChordPairs;
        public int MultiOverlapChordPairs;
        public int ChordRuns;
    }

    private sealed class LnPatternStats
    {
        public double InverseReleaseRatio;
        public double SameColumnReleaseGapP50;
        public double ReleaseOnlyRatio;
        public double HeadTailSwitchRatio;
        public double MixedRowRatio;
        public double TapWhileHoldingRatio;
        public double ActiveReleaseRatio;
        public double CoordinatedReleaseRatio;
        public double HeldWhileReleaseRatio;
        public double HoldDurationP50;
        public double InverseWindowCoverage;
    }

    private const double INVERSE_WINDOW_MS = 8000;
    private const int INVERSE_WINDOW_MIN_PAIRS = 20;
    private const int INVERSE_WINDOW_MIN_WINDOWS = 3;
    private const double INVERSE_WINDOW_RATIO = 0.65;

    private static List<int> RowColumns(List<ManiaNote> rowNotes)
        => rowNotes.Select(note => note.Column).Distinct().OrderBy(c => c).ToList();

    private static bool IsRollBetween(List<int> previous, List<int> current)
    {
        if (previous.Count == 0 || current.Count == 0) return false;
        return previous[0] > current[current.Count - 1] || previous[previous.Count - 1] < current[0];
    }

    private static int SharedColumnCount(List<int> previous, List<int> current)
    {
        int shared = 0;
        foreach (var column in current) if (previous.Contains(column)) shared++;
        return shared;
    }

    private static int BitCount(int mask)
    {
        int count = 0;
        while (mask != 0)
        {
            count += 1;
            mask &= mask - 1;
        }
        return count;
    }

    private static RowPatternStats GetRowPatternStats(List<(double Time, List<ManiaNote> Notes)> orderedRows, int keyCount)
    {
        int chordRows = 0;
        int twoNoteRows = 0;
        int threeNoteRows = 0;
        int fourPlusRows = 0;
        int threePlusRows = 0;
        int singleRows = 0;
        int repeatedChordRows = 0;
        int bracketWindowRows = 0;
        double totalChordSize = 0;
        int chordPairs = 0;
        int multiOverlapChordPairs = 0;
        int chordRuns = 0;
        int? previousChordMask = null;
        int previousRowMask = 0;
        int previousRowSize = 0;
        double previousRowTime = double.NegativeInfinity;
        var previousColumns = new List<int>();
        var beforePreviousColumns = new List<int>();

        foreach (var (time, rowNotes) in orderedRows)
        {
            var columns = RowColumns(rowNotes);
            int size = columns.Count;
            int mask = 0;
            foreach (var column in columns) mask |= 1 << column;

            if (size <= 1) singleRows++;
            if (size >= 2)
            {
                chordRows++;
                totalChordSize += size;
                if (previousChordMask != null && mask == previousChordMask.Value) repeatedChordRows++;
                previousChordMask = mask;
                if (previousRowSize < 2) chordRuns++;
                if (previousRowSize >= 2 && time - previousRowTime < 1000)
                {
                    chordPairs++;
                    if (BitCount(mask & previousRowMask) >= 2) multiOverlapChordPairs++;
                }
            }
            else
            {
                previousChordMask = null;
            }
            previousRowMask = mask;
            previousRowSize = size;
            previousRowTime = time;
            if (size == 2) twoNoteRows++;
            if (size == 3) threeNoteRows++;
            if (size >= 4) fourPlusRows++;
            if (size >= 3) threePlusRows++;
            // Three chords in a row that neither jack nor roll. This is the vendored
            // engine's own bracket primitive (CHORDSTREAM_7K_BRACKETS) with both of its
            // size conditions dropped: it demands every row carry 3+ notes and their
            // sum exceed 9, which between them refuse the two shapes brackets are
            // actually charted in - runs of exactly-three-note chords (3+3+3 is not
            // > 9) and two-note brackets. Measured 2026-08-17 over 361 charts whose
            // mapper tags say bracket: a file at 37% two-note rows scored 0.018 under
            // upstream's rule against 0.128 for its sibling by the same mapper with the
            // same tags, and dropping the floors separates tagged charts from random
            // 7K ones at AUC 0.79 (0.89 against chordjack-tagged) versus 0.72 / 0.74.
            // Requiring the two-note rows to be same-hand adjacent pairs - the literal
            // bracket shape - measured no better than upstream (0.73), which is the
            // same result shape carries everywhere else in this detector.
            if (
                keyCount >= 6 && size >= 2
                && beforePreviousColumns.Count >= 2 && previousColumns.Count >= 2
                && !IsRollBetween(beforePreviousColumns, previousColumns)
                && !IsRollBetween(previousColumns, columns)
                && SharedColumnCount(beforePreviousColumns, previousColumns) == 0
                && SharedColumnCount(previousColumns, columns) == 0)
            {
                bracketWindowRows++;
            }
            beforePreviousColumns = previousColumns;
            previousColumns = columns;
        }

        return new RowPatternStats
        {
            RowCount = orderedRows.Count,
            ChordRows = chordRows,
            TwoNoteRows = twoNoteRows,
            ThreeNoteRows = threeNoteRows,
            FourPlusRows = fourPlusRows,
            ThreePlusRows = threePlusRows,
            SingleRows = singleRows,
            RepeatedChordRows = repeatedChordRows,
            BracketWindowRows = bracketWindowRows,
            AverageChordSize = chordRows != 0 ? totalChordSize / chordRows : 0,
            ChordPairs = chordPairs,
            MultiOverlapChordPairs = multiOverlapChordPairs,
            ChordRuns = chordRuns,
        };
    }

    // Single-note jack content for the 6K/7K jack tag, where the chordjack
    // detector is blind: it counts repeated chords, so a chart built on
    // single-note minijacks and trills (Ningen Shikkaku [Zenx's 7K Miscreation],
    // chordjack 0.35) reads as tech/chordstream. Two shapes, both measured as a
    // share of notes:
    //
    // - jack1Share: notes whose column was also hit on the immediately previous
    //   row, within 400ms. Row-relative on purpose: an absolute repeat window
    //   cannot tell jack from dense 7K stream (same-column re-hits under 180ms
    //   sit at p50 0.36 on the jack corpus and 0.39 on the stream corpus), while
    //   "re-hit one row back" is the jack motion itself. The 400ms cap is what
    //   keeps out slow filler jacks between real content, the shape the cluster
    //   share's false positives took (jacks at exactly half the chordstream BPM).
    // - trillRunShare: notes inside strict two-row alternations (the same two
    //   column sets A/B repeating for 6+ rows, each row gap <= 200ms), the
    //   full-trill spam charts are built on. Nearly binary in practice: every
    //   corpus (jack included) sits at p90 <= 0.026 while trill charts carry
    //   0.1+, so the arm fires on almost nothing but its own class.
    private static (double Jack1Share, double TrillRunShare) GetSingleJackStats(List<(double Time, List<ManiaNote> Notes)> orderedRows)
    {
        var masks = new List<int>();
        var times = new List<double>();
        int noteCount = 0;
        int jack1 = 0;
        foreach (var (time, rowNotes) in orderedRows)
        {
            int mask = 0;
            foreach (var note in rowNotes) mask |= 1 << note.Column;
            noteCount += rowNotes.Count;
            int last = masks.Count - 1;
            if (last >= 0 && time - times[last] <= 400)
            {
                int overlap = mask & masks[last];
                while (overlap != 0)
                {
                    jack1 += 1;
                    overlap &= overlap - 1;
                }
            }
            masks.Add(mask);
            times.Add(time);
        }
        int trillNotes = 0;
        int i = 0;
        while (i < masks.Count - 5)
        {
            int a = masks[i];
            int b = masks[i + 1];
            if (a == 0 || b == 0 || a == b || times[i + 1] - times[i] > 200)
            {
                i += 1;
                continue;
            }
            int j = i + 2;
            while (j < masks.Count && masks[j] == ((j - i) % 2 == 0 ? a : b) && times[j] - times[j - 1] <= 200) j += 1;
            if (j - i >= 6)
            {
                for (int m = i; m < j; m += 1) trillNotes += BitCount(masks[m]);
                i = j;
            }
            else
            {
                i += 1;
            }
        }
        return (
            noteCount > 0 ? (double)jack1 / noteCount : 0,
            noteCount > 0 ? (double)trillNotes / noteCount : 0);
    }

    // Inverse charting joins consecutive notes in a column with LNs, leaving only
    // a small release gap charted as a beat fraction (1/8 to 1/4 beat). A fixed
    // millisecond cutoff misreads slow charts: at 79 BPM a 1/6-beat inverse gap is
    // 127ms, which a 120ms cap counts as not-inverse (JJ's 7K dan 6th missed the
    // lninverse tag with every gap at 126-127ms). Scale the cap with tempo, floored
    // at the old 120ms for fast charts and ceilinged so very slow charts don't
    // count half-second release gaps as inverse holds.
    private static double InverseGapCapMs(double beatLengthMs)
    {
        if (!double.IsFinite(beatLengthMs) || beatLengthMs <= 0) return 120;
        return Math.Min(250, Math.Max(120, beatLengthMs * 0.27));
    }

    private sealed class WindowPair { public int Pairs; public int Inverse; }

    private static LnPatternStats GetLnPatternStats(
        List<ManiaNote> notes,
        List<(double Time, List<ManiaNote> Notes)> orderedRows,
        int keyCount,
        double beatLengthMs)
    {
        var releaseRows = new Dictionary<double, List<ManiaNote>>();
        var headTimes = new HashSet<double>();
        var holdEvents = new List<(double Time, int Delta)>();
        var holdSpans = new List<ManiaNote>();
        int notesByColumnLen = Math.Max(1, keyCount);
        var notesByColumn = new List<ManiaNote>[notesByColumnLen];
        for (int k = 0; k < notesByColumnLen; k++) notesByColumn[k] = new List<ManiaNote>();

        foreach (var note in notes)
        {
            if (note.Column >= 0 && note.Column < notesByColumn.Length) notesByColumn[note.Column].Add(note);
            if (!note.IsHold || note.EndTime <= note.Time) continue;
            holdSpans.Add(note);

            if (releaseRows.TryGetValue(note.EndTime, out var releaseRow)) releaseRow.Add(note);
            else releaseRows[note.EndTime] = new List<ManiaNote> { note };
            holdEvents.Add((note.Time, 1));
            holdEvents.Add((note.EndTime, -1));
        }

        holdEvents = holdEvents.OrderBy(e => e.Time).ThenByDescending(e => e.Delta).ToList();

        int mixedRows = 0;
        int tapWhileHoldingRows = 0;
        int headTailSwitchRows = 0;
        double activeHolds = 0;
        int eventIndex = 0;

        foreach (var (time, rowNotes) in orderedRows)
        {
            headTimes.Add(time);

            while (eventIndex < holdEvents.Count && holdEvents[eventIndex].Time < time)
            {
                activeHolds = Math.Max(0, activeHolds + holdEvents[eventIndex].Delta);
                eventIndex++;
            }

            bool hasHold = rowNotes.Any(note => note.IsHold);
            bool hasTap = rowNotes.Any(note => !note.IsHold);
            if (hasHold && hasTap) mixedRows++;
            if (hasTap && activeHolds > 0) tapWhileHoldingRows++;
            if (releaseRows.ContainsKey(time)) headTailSwitchRows++;
        }

        int releaseOnlyRows = 0;
        foreach (var time in releaseRows.Keys)
        {
            if (!headTimes.Contains(time)) releaseOnlyRows++;
        }

        var sameColumnGaps = new List<double>();
        double gapCap = InverseGapCapMs(beatLengthMs);
        int inverseLikeHolds = 0;
        int sameColumnNextHolds = 0;

        // Prefix maximum of hold end times, ordered by hold start, so "is another
        // hold still down at time t" is a binary search instead of a scan.
        var holdsByStart = holdSpans.OrderBy(h => h.Time).ToList();
        var holdStarts = holdsByStart.Select(h => h.Time).ToList();
        var latestEndByStart = new List<double>();
        double latestEnd = double.NegativeInfinity;
        foreach (var hold in holdsByStart)
        {
            latestEnd = Math.Max(latestEnd, hold.EndTime);
            latestEndByStart.Add(latestEnd);
        }
        bool HeldThrough(double time)
        {
            int low = 0;
            int high = holdStarts.Count;
            while (low < high)
            {
                int mid = (low + high) >> 1;
                if (holdStarts[mid] < time) low = mid + 1;
                else high = mid;
            }
            return low > 0 && latestEndByStart[low - 1] > time;
        }

        int activeReleaseHolds = 0;
        int coordinatedReleaseHolds = 0;
        int heldWhileReleaseHolds = 0;
        var holdDurations = new List<double>();
        var windowPairs = new Dictionary<double, WindowPair>();

        foreach (var columnNotes in notesByColumn)
        {
            columnNotes.Sort((left, right) =>
            {
                int c = left.Time.CompareTo(right.Time);
                return c != 0 ? c : left.EndTime.CompareTo(right.EndTime);
            });
            for (int index = 0; index < columnNotes.Count; index++)
            {
                var note = columnNotes[index];
                if (!note.IsHold || note.EndTime <= note.Time) continue;

                double holdDuration = Math.Max(1, note.EndTime - note.Time);
                holdDurations.Add(holdDuration);

                var nextNote = index + 1 < columnNotes.Count ? columnNotes[index + 1] : null;
                double gap = nextNote != null ? nextNote.Time - note.EndTime : double.PositiveInfinity;
                if (nextNote != null && gap >= 0)
                {
                    sameColumnNextHolds++;
                    sameColumnGaps.Add(gap);
                    double windowKey = Math.Floor(note.Time / INVERSE_WINDOW_MS);
                    if (!windowPairs.TryGetValue(windowKey, out var window)) { window = new WindowPair(); windowPairs[windowKey] = window; }
                    window.Pairs++;
                    if (gap <= gapCap && gap <= holdDuration) window.Inverse++;
                }

                // The release doubles as the cue to press the same column again, so the
                // press carries the timing and the release rides along. Everything else
                // is a release the hand has to place on its own.
                if (gap >= 0 && gap <= gapCap && gap / holdDuration <= 0.7)
                {
                    if (nextNote != null) inverseLikeHolds++;
                    continue;
                }
                activeReleaseHolds++;
                if (headTimes.Contains(note.EndTime)) coordinatedReleaseHolds++;
                if (HeldThrough(note.EndTime)) heldWhileReleaseHolds++;
            }
        }

        int rowCount = Math.Max(1, orderedRows.Count);
        int releaseRowCount = releaseRows.Count;
        int holdCount = Math.Max(1, holdDurations.Count);

        int judgedWindows = 0;
        int inverseWindows = 0;
        foreach (var window in windowPairs.Values)
        {
            if (window.Pairs < INVERSE_WINDOW_MIN_PAIRS) continue;
            judgedWindows++;
            if ((double)window.Inverse / window.Pairs >= INVERSE_WINDOW_RATIO) inverseWindows++;
        }

        return new LnPatternStats
        {
            InverseReleaseRatio = sameColumnNextHolds != 0 ? (double)inverseLikeHolds / sameColumnNextHolds : 0,
            SameColumnReleaseGapP50 = DanMath.Quantile(sameColumnGaps, 0.5),
            ReleaseOnlyRatio = releaseRowCount != 0 ? (double)releaseOnlyRows / releaseRowCount : 0,
            HeadTailSwitchRatio = (double)headTailSwitchRows / rowCount,
            MixedRowRatio = (double)mixedRows / rowCount,
            TapWhileHoldingRatio = (double)tapWhileHoldingRows / rowCount,
            ActiveReleaseRatio = (double)activeReleaseHolds / holdCount,
            CoordinatedReleaseRatio = (double)coordinatedReleaseHolds / holdCount,
            HeldWhileReleaseRatio = (double)heldWhileReleaseHolds / holdCount,
            HoldDurationP50 = DanMath.Quantile(holdDurations, 0.5),
            InverseWindowCoverage = judgedWindows >= INVERSE_WINDOW_MIN_WINDOWS ? (double)inverseWindows / judgedWindows : 0,
        };
    }

    private static double Ratio(double count, double total) => total > 0 ? count / total : 0;

    private static double Pressure(double value, double low, double high)
        => DanMath.Clamp01((value - low) / Math.Max(0.001, high - low));

    private static double RoundedScore(double value) => Math.Floor(DanMath.Clamp01(value) * 1000 + 0.5) / 1000;

    private static ManiaPatternHit Hit(ManiaPatternId id, double score, double dataConfidence, string evidence)
    {
        return new ManiaPatternHit
        {
            Id = id,
            Label = MANIA_PATTERN_ANALYZER_LABELS[id],
            Score = RoundedScore(score),
            Confidence = RoundedScore(score * dataConfidence),
            Evidence = evidence,
        };
    }

    private static string CompactPercent(double value) => $"{(int)Math.Floor(value * 100 + 0.5)}%";

    private static string F1(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    private static string R0(double value) => ((long)Math.Floor(value + 0.5)).ToString(CultureInfo.InvariantCulture);

    public static ManiaPatternAnalysis AnalyzeManiaPatterns(
        ManiaBeatmap map,
        DanEstimateInput? input = null,
        DanFeatureExtractionResult? precomputedFeatures = null)
    {
        input ??= new DanEstimateInput();
        double rate = Labels.GetInputRate(input);
        var features = precomputedFeatures ?? DanFeatures.ExtractDanFeatures(map, input, rate);
        var metrics = features.Metrics;
        var orderedRows = features.OrderedRows;
        var stats = GetRowPatternStats(orderedRows, metrics.KeyCount);
        int rowCount = Math.Max(1, stats.RowCount);
        double chordRatio = metrics.ChordRatio;
        double twoNoteRatio = Ratio(stats.TwoNoteRows, rowCount);
        double threeNoteRatio = Ratio(stats.ThreeNoteRows, rowCount);
        double threePlusRatio = Ratio(stats.ThreePlusRows, rowCount);
        double fourPlusRatio = Ratio(stats.FourPlusRows, rowCount);
        double repeatedChordRatio = Ratio(stats.RepeatedChordRows, Math.Max(1, stats.ChordRows - 1));
        double lowChordGate = DanMath.Clamp01((0.34 - chordRatio) / 0.3);
        double streamActivity = Math.Max(
            Pressure(metrics.StreamPressure, 1.5, 5.5),
            Pressure(metrics.SustainedNps10s, metrics.KeyCount >= 6 ? 7 : 12, metrics.KeyCount >= 6 ? 18 : 27));
        double chordstreamGate = DanMath.MinGate(
            Pressure(chordRatio, metrics.KeyCount >= 6 ? 0.14 : 0.24, metrics.KeyCount >= 6 ? 0.5 : 0.58),
            Pressure(metrics.SustainedNps10s, metrics.KeyCount >= 6 ? 6 : 11, metrics.KeyCount >= 6 ? 17 : 25));
        // Chord density alone is not chordjack: dense 7K bracket/jumpstream files
        // carry chordRatio 0.8+ with almost no consecutive-chord column re-hits.
        // The overlap gate demands actual chord-jack repetition (~0.1 on bracket
        // files vs 0.5-0.97 on true CJ; the 0.18-0.4 ramp sits in the empty band
        // between the two populations).
        double chordOverlapGate = Pressure(metrics.ChordColumnOverlapRatio, 0.18, 0.4);
        double chordjackBase = Math.Max(
            DanMath.MinGate(Pressure(chordRatio, 0.28, 0.64), Pressure(metrics.ChordjackPressure, 70, 185), chordOverlapGate),
            DanMath.MinGate(Pressure(chordRatio, 0.36, 0.72), Pressure(metrics.JackPressure, 80, 180), chordOverlapGate));
        double techScore = Math.Max(
            DanMath.MinGate(Pressure(metrics.TechPressure, 3.5, 8.5), Pressure(metrics.RowPatternChangeRate, 0.34, 0.66)),
            DanMath.MinGate(
                Pressure(metrics.ChordSizeChangeRate, 0.25, 0.58),
                Pressure(metrics.DirectionChangeRate, 0.35, 0.72),
                Pressure(metrics.RowIntervalEntropy, 1.1, 2.4)));
        double dataConfidence = DanMath.Clamp01(0.35 + Math.Min(0.4, metrics.NoteCount / 2500.0) + Math.Min(0.25, stats.RowCount / 900.0));
        var candidates = new List<ManiaPatternHit>();
        // Note times are already rate-scaled, so the beat length must be too.
        double beatLengthMs = double.IsFinite(map.Bpm) && map.Bpm > 0 ? 60000 / (map.Bpm * rate) : 0;
        var lnStats = GetLnPatternStats(features.Notes, orderedRows, metrics.KeyCount, beatLengthMs);
        double lnScore = Math.Max(Math.Max(Math.Max(
            Pressure(metrics.HoldRatio, 0.03, 0.32),
            DanMath.MinGate(Pressure(metrics.LnDensity, 0.02, 0.18), Pressure(metrics.LnOverlapPressure, 0.4, 2.4))),
            DanMath.MinGate(Pressure(metrics.LnReleasePressure, 1.2, 5.5), Pressure(metrics.HoldRatio, 0.015, 0.16))),
            DanMath.MinGate(Pressure(metrics.LnChordPressure, 0.15, 0.65), Pressure(metrics.HoldRatio, 0.02, 0.18)));
        // 4K and 7K both get LN subtypes; the shapes are real on 4 columns too. The
        // raw LN row stats are keymode-neutral, but their distributions are not, so
        // the individual subtypes re-ramp themselves below where 4 columns shift the
        // population (measured over the 63k cached 4K charts that carry long notes).
        bool lnSubtypeKeys = metrics.KeyCount == 7 || metrics.KeyCount == 4;
        double lnSubtypeGate = lnSubtypeKeys ? Pressure(lnScore, 0.18, 0.58) : 0;
        // Two ways in. The whole-chart leg is the original: most same-column
        // releases are inverse re-presses and the chart is nearly all LN (a 16%
        // mixed-row ceiling). The windowed leg is for charts that are inverse in
        // sections: enough 8s windows read as inverse, under a looser mixed-row
        // ceiling, since the rice sits in the other sections. At 0.35-0.75
        // coverage and the 0.2-0.45 mixed ramp the inverse-labelled corpus goes
        // 89% -> 96% tagged and unlabelled LN charts 4.8% -> 6.5% (2026-09-03).
        // 7K only: the window cut was measured there, and on 4K the looser gap rule
        // reads dense short-hold chording as inverse (a 100ms hold re-pressed 100ms
        // later in one of four columns is most of what 4K LN chords look like).
        double lnInverseShape = Math.Max(
            Pressure(lnStats.InverseReleaseRatio, 0.24, 0.62) * DanMath.Clamp01((0.16 - lnStats.MixedRowRatio) / 0.16),
            metrics.KeyCount == 7
                ? Pressure(lnStats.InverseWindowCoverage, 0.35, 0.75) * DanMath.Clamp01((0.45 - lnStats.MixedRowRatio) / 0.25)
                : 0);
        double lnInverseScore = lnSubtypeGate * DanMath.MinGate(
            lnInverseShape,
            Pressure(metrics.LnDensity, 0.12, 0.5),
            Math.Max(
                Pressure(metrics.LnOverlapPressure, 1.1, 3.1),
                Pressure(metrics.LnHoldDurationP90, 260, 520)));
        // Release is about where the release lands, not how many of them there are.
        // The old gate asked for isolated release rows (no note head at the same
        // instant), 12+ releases/sec and hold tails under 520ms, and all three run
        // the wrong way: measured 2026-08-25 over 324 7K charts whose mapper tags,
        // diff name or pack name say release/coordination against 1977 random 7K LN
        // charts, isolated release rows score AUC 0.36, releases/sec 0.19 and short
        // tails 0.23 - release charts have FEWER isolated releases (you let go while
        // hitting something else), longer tails (p50 281ms against 130ms) and no
        // more density than any other LN chart. What it built was a difficulty tag:
        // it fired on 5% of the labelled charts, its median hit sat at 8.32*, and
        // only 15 charts in the whole index cleared it under 5*.
        //
        // What separates instead, on the same corpus: releases that are not the
        // off-half of an inverse re-press (AUC 0.76 against random LN, 0.85 against
        // inverse-labelled charts), tails long enough that letting go is its own
        // motor action rather than the tail of a flick (0.79), and the release
        // landing against something - a press in another column (0.74) or other
        // holds still down (0.72). Two shapes clear the last leg, because release
        // charts come in both: the slow one, where long tails release onto other
        // presses, and the dense all-LN one, where tails are short but every release
        // happens under other holds.
        //
        // The dense leg's ramps were lowered 2026-09-03: the 7K LN dan release
        // practice charts release on half-beat tails at 175-190 BPM (p50 95-180ms),
        // which the slow leg cannot see, and sit at 53-62% released under other
        // holds against the leg's old 0.6 start. Against 150 release-named charts
        // and 14.3k unlabelled 7K LN charts, 0.45-0.7 takes recall at 0.5 from 45%
        // to 51% (the pack's 6th-10th from 0.12-0.67 to 0.51-1.0) for +1.9 points
        // of unlabelled hits.
        //
        // A third shape, the release wall: nearly all LN, 32+ releases a second,
        // half of them under other holds, not inverse. The Zenith and Stellium dan
        // release diffs are this and nothing else scores them (active releases sit
        // at 0.62, under the 0.55-0.88 entry gate, because a third of their holds
        // are re-pressed within a quarter beat). The leg is allowed past that gate.
        // It costs 51 unlabelled charts, every one a 7-12 star LN wall.
        //
        // Still 7K-only, but no longer for the old reason (4K release-only rows were
        // manufactured by short-hold vibro): these ramps are measured on 7K, where
        // the scene names the skillset and the corpus exists, and neither the maps
        // picker nor the player skill buckets offer a 4K release axis to fill.
        double lnReleaseWall = DanMath.MinGate(
            Pressure(metrics.HoldRatio, 0.85, 0.97),
            Pressure(lnStats.HeldWhileReleaseRatio, 0.45, 0.65),
            Pressure(metrics.LnReleasePressure, 32, 46),
            DanMath.Clamp01((0.5 - lnStats.InverseReleaseRatio) / 0.15));
        double lnReleaseScore = metrics.KeyCount == 7
            ? lnSubtypeGate * DanMath.MinGate(
                Pressure(metrics.HoldRatio, 0.2, 0.46),
                Math.Max(Pressure(lnStats.ActiveReleaseRatio, 0.55, 0.88), lnReleaseWall),
                // Low floor on purpose: enough releases that the chart puts them in front
                // of you, ramped under the labelled charts' p02 rather than over their
                // p90 the way the old 12/sec term was.
                Pressure(metrics.LnReleasePressure, 2.5, 6),
                Math.Max(Math.Max(
                    DanMath.MinGate(
                        Pressure(lnStats.HoldDurationP50, 150, 330),
                        Math.Max(
                            Pressure(lnStats.CoordinatedReleaseRatio, 0.34, 0.68),
                            Pressure(lnStats.HeldWhileReleaseRatio, 0.32, 0.62))),
                    DanMath.MinGate(
                        Pressure(lnStats.HeldWhileReleaseRatio, 0.45, 0.7),
                        Pressure(metrics.HoldRatio, 0.6, 0.85),
                        Pressure(lnStats.ActiveReleaseRatio, 0.7, 0.9))),
                    lnReleaseWall))
            : 0;
        double lnTechBurst = Math.Max(
            Pressure(metrics.FastRowRatio, 0.18, 0.36),
            Pressure(metrics.RowBurstPressure, 16, 26));
        // 4K charts tap while holding at roughly half the 7K rate (corpus p50 0.117
        // vs 0.281) and the 7K ramp starts below both populations, so on 4K it
        // saturates and stops discriminating, leaving lntech decided by the burst and
        // tech terms alone. Re-ramped onto the 4K distribution, and the chord-size
        // leg dropped: it carries no LN signal, and it was admitting pure tech and
        // dump charts (Figue Folle, Canon Rock) as LN tech.
        double lnTechCoordination = metrics.KeyCount == 4
            ? Math.Max(
                Pressure(lnStats.TapWhileHoldingRatio, 0.1, 0.25),
                Pressure(lnStats.HeadTailSwitchRatio, 0.3, 0.55))
            : Math.Max(Math.Max(
                Pressure(lnStats.TapWhileHoldingRatio, 0.04, 0.11),
                Pressure(lnStats.HeadTailSwitchRatio, 0.52, 0.72)),
                Pressure(metrics.ChordSizeChangeRate, 0.55, 0.78));
        // A 4K hold leaves three free lanes, so tech-flavoured rice with a token LN
        // still clears the coordination term. Demand actual LN content as well.
        double lnTechContentFloor = metrics.KeyCount == 4 ? Pressure(metrics.HoldRatio, 0.2, 0.4) : 1;
        double lnTechScore = lnSubtypeGate * DanMath.MinGate(
            lnTechBurst,
            lnTechCoordination,
            lnTechContentFloor,
            Math.Max(
                Pressure(metrics.TechPressure, 4.2, 8.4),
                Pressure(metrics.RowIntervalEntropy, 2.0, 2.45)),
            DanMath.Clamp01((0.6 - lnStats.InverseReleaseRatio) / 0.34),
            DanMath.Clamp01((0.66 - lnStats.ReleaseOnlyRatio) / 0.22));
        double lnGeneralCoverage = Math.Max(
            DanMath.MinGate(
                Pressure(metrics.HoldRatio, 0.35, 0.82),
                Pressure(metrics.LnChordPressure, 0.32, 0.66),
                Pressure(lnStats.HeadTailSwitchRatio, 0.35, 0.62)),
            DanMath.MinGate(
                Pressure(metrics.LnDensity, 0.12, 0.42),
                Pressure(metrics.LnReleasePressure, 8, 24),
                Pressure(chordRatio, 0.28, 0.62)));
        // General is the LN chart that is not one of the specialties, so it yields
        // to them fully: the old damper floored at 0.35, which left a visible
        // (>= 0.2) LN General tag on every saturated release or inverse chart.
        // With this ramp a specialty at 0.62+ removes it; across 7K, charts with a
        // specialty at 0.5+ go from 44% to 4.5% co-tagged, and no LN chart loses
        // its only subtype (2026-09-03).
        double lnSpecialtyScore = Math.Max(Math.Max(lnInverseScore, lnReleaseScore), lnTechScore);
        double lnGeneralScore = lnSubtypeGate
            * lnGeneralCoverage
            * DanMath.Clamp01((0.7 - lnSpecialtyScore) / 0.4);

        candidates.Add(Hit(
            ManiaPatternId.Ln,
            metrics.KeyCount == 7 ? lnScore * 0.62 : lnScore,
            dataConfidence,
            $"{CompactPercent(metrics.HoldRatio)} holds, release pressure {F1(metrics.LnReleasePressure)}"));
        if (lnSubtypeKeys)
        {
            candidates.Add(Hit(ManiaPatternId.Lngeneral, lnGeneralScore, dataConfidence, $"{CompactPercent(metrics.LnChordPressure)} LN chord rows, {CompactPercent(lnStats.HeadTailSwitchRatio)} head/tail switches"));
            candidates.Add(Hit(ManiaPatternId.Lninverse, lnInverseScore, dataConfidence, $"{CompactPercent(lnStats.InverseReleaseRatio)} short same-column release gaps, p50 gap {R0(lnStats.SameColumnReleaseGapP50)}ms, {CompactPercent(lnStats.InverseWindowCoverage)} of the chart in inverse sections"));
            candidates.Add(Hit(ManiaPatternId.Lntech, lnTechScore, dataConfidence, $"{CompactPercent(lnStats.TapWhileHoldingRatio)} tap-with-hold rows, burst pressure {F1(metrics.RowBurstPressure)}"));
            if (metrics.KeyCount == 7)
            {
                candidates.Add(Hit(ManiaPatternId.Lnrelease, lnReleaseScore, dataConfidence, $"{CompactPercent(lnStats.ActiveReleaseRatio)} releases timed on their own, p50 tail {R0(lnStats.HoldDurationP50)}ms, {CompactPercent(lnStats.HeldWhileReleaseRatio)} released under other holds"));
            }
        }

        if (metrics.KeyCount == 4)
        {
            double jackScore = Math.Max(
                Pressure(metrics.JackPressure, 75, 185) * (0.55 + lowChordGate * 0.35),
                DanMath.MinGate(Pressure(metrics.JackPressure, 110, 200), Pressure(metrics.FastRowRatio, 0.05, 0.28)));
            candidates.Add(Hit(ManiaPatternId.Jack, jackScore, dataConfidence, $"same-lane pressure {R0(metrics.JackPressure)}"));
            candidates.Add(Hit(ManiaPatternId.Chordjack, chordjackBase, dataConfidence, $"{CompactPercent(chordRatio)} chord rows, jack pressure {R0(metrics.JackPressure)}"));
            candidates.Add(Hit(ManiaPatternId.Speedjack, chordjackBase * DanMath.MinGate(
                Pressure(twoNoteRatio, 0.18, 0.42),
                Pressure(metrics.JackPressure, 115, 205),
                DanMath.Clamp01((0.68 - threePlusRatio) / 0.35)), dataConfidence, $"{CompactPercent(twoNoteRatio)} two-note rows, light dense jacks"));
            candidates.Add(Hit(ManiaPatternId.Handjack, chordjackBase * DanMath.MinGate(
                Pressure(threePlusRatio, 0.08, 0.28),
                Pressure(stats.AverageChordSize, 2.15, 3.1),
                Pressure(metrics.JackPressure, 95, 180)), dataConfidence, $"{CompactPercent(threePlusRatio)} 3+ note rows in jack pressure"));
            candidates.Add(Hit(ManiaPatternId.Tech, techScore, dataConfidence, $"pattern change {CompactPercent(metrics.RowPatternChangeRate)}, tech pressure {F1(metrics.TechPressure)}"));
            candidates.Add(Hit(ManiaPatternId.Stream, lowChordGate * streamActivity * DanMath.Clamp01((150 - metrics.JackPressure) / 120), dataConfidence, $"{CompactPercent(chordRatio)} chord rows, sustained flow"));
            candidates.Add(Hit(ManiaPatternId.Dumpstream, lowChordGate * streamActivity * DanMath.MinGate(
                Pressure(metrics.RowPatternEntropy, 1.8, 3.5),
                Pressure(metrics.RowIntervalEntropy, 1.2, 2.7),
                DanMath.Clamp01((0.75 - metrics.RhythmMotifRepeatRatio) / 0.45)), dataConfidence, $"irregular stream entropy {F1(metrics.RowIntervalEntropy)}"));
            candidates.Add(Hit(ManiaPatternId.Jumpstream, DanMath.MinGate(
                Pressure(twoNoteRatio, 0.14, 0.36),
                Pressure(chordRatio, 0.24, 0.56),
                Pressure(metrics.JumpstreamPressure, 8, 22)), dataConfidence, $"{CompactPercent(twoNoteRatio)} two-note chord rows"));
            candidates.Add(Hit(ManiaPatternId.Handstream, DanMath.MinGate(
                Pressure(threeNoteRatio, 0.06, 0.22),
                Pressure(chordRatio, 0.32, 0.62),
                Pressure(metrics.SustainedNps10s, 13, 27),
                DanMath.Clamp01((175 - metrics.JackPressure) / 120)), dataConfidence, $"{CompactPercent(threeNoteRatio)} three-note rows in stream"));
            candidates.Add(Hit(ManiaPatternId.Quadstream, DanMath.MinGate(
                Pressure(fourPlusRatio, 0.015, 0.08),
                Pressure(chordRatio, 0.36, 0.72),
                Pressure(metrics.SustainedNps10s, 12, 25)), dataConfidence, $"{CompactPercent(fourPlusRatio)} quad rows in stream"));
        }
        else if (metrics.KeyCount >= 6 && metrics.KeyCount <= 8)
        {
            // 8K joined this branch on 2026-09-02: its charts are 7K's vocabulary
            // (many are mapped as 7K+1 with a quiet scratch column), and the generic
            // branch below could never say bracket, delay or jack about them. [8K]
            // Abyss 8 (3992501) is the case that named it: a bracket file that stored
            // as chordstream 1.00 because the branch had no bracket detector.
            double nonLnFlowGate = DanMath.Clamp01((0.3 - metrics.HoldRatio) / 0.22);
            double nonLnPatternGate = DanMath.Clamp01((0.68 - metrics.HoldRatio) / 0.56);
            // Brackets are dense chords that move across the columns, so consecutive
            // chords re-hitting their columns is evidence against the tag: a chordjack
            // chart's chords are bracket-shaped row by row, and without this gate the
            // detector saturated on exactly the files it should refuse (a 260BPM 7K CJ
            // chart was the #1 "Bracket" play on profile skill cards). Measured over
            // the stored bracket-tagged 6K/7K corpus (2026-08-16): jack-family cluster
            // verdicts are ~0% below 0.4 overlap, 19% at 0.45-0.5, 56% at 0.55-0.6 and
            // 94%+ from 0.75, while nearly every saturated (>=0.95) bracket score sat
            // on a jack-family chart. The ramp starts above the chordstream population
            // and is closed before the CJ majority band.
            double bracketOverlapGate = DanMath.Clamp01((0.62 - metrics.ChordColumnOverlapRatio) / 0.17);
            // Bracket content: how much of the chart is sustained chording that neither
            // jacks nor rolls (getRowPatternStats' bracketWindowRows). Bracket *shape*
            // is not usable on its own - on 7 columns a chord is adjacent-pair shaped
            // mostly by chance, so mapper-labelled bracket charts carry 0.199
            // bracket-shaped rows against 0.185 for random 7K charts (AUC 0.52), and
            // chordjack files outscore real bracket files on it. This window is the
            // only content term: the old row-shape and chord-size legs are gone, since
            // both measure density and on a file of two-note brackets only ever
            // subtracted true positives. What holds chordjack out is the overlap gate.
            //
            // Ramp measured 2026-08-17 against 361 charts whose mapper tags say bracket,
            // 500 random 7K charts, 502 chordjack-tagged ones and the 92 main diffs of
            // the BEST OF BRACKETS packs: at 0.18-0.38 the tag reaches every diff in
            // those packs and 56.5% of tag-labelled charts, while random charts sit at
            // 10.8% and chordjack-tagged ones at 2.0%.
            double bracketWindowRatio = Ratio(stats.BracketWindowRows, rowCount);
            double wideChordstream = Math.Max(
                chordstreamGate,
                DanMath.MinGate(Pressure(chordRatio, 0.2, 0.62), Pressure(metrics.ChordSizeChangeRate, 0.18, 0.52)));
            var singleJack = GetSingleJackStats(orderedRows);
            // Chord-tech is not chordjack. The overlap gate above asks whether
            // consecutive chords share ANY column, and a 7K tech chart re-hits one
            // finger between chords all the time (a chord of three moving to a chord
            // of three next to it) without ever jacking the chord: [7K] Miserable
            // Bastard from the Terminal 11 Technical Pack (3537470) sits at 0.50
            // overlap, chordjack 0.92, and filed as Chordjack over tech 0.52. What
            // real chordjack has and chord-tech lacks is the chord itself repeating:
            // consecutive chords sharing two or more columns, single notes re-hit one
            // row back (the minijack content), or chords arriving in long unbroken
            // runs rather than two or three at a time between singles. Measured
            // 2026-09-03 over 7K rice charts filed Chordjack: 151 from chordjack-tagged
            // sets and jack packs against the 4 from tech-tagged sets and tech packs
            // (of 101). Multi-column overlap sits at p10 0.215 / p50 0.559 on the jack
            // side against 0.17-0.23 on the tech side; jack1Share p10 0.216 against
            // 0.12-0.18; mean chord run p10 4.8 against 2.4-6.6. Any one arm keeps the
            // tag. At these ramps 3 of the 4 tech-side charts move to Tech, 8 of the
            // 207 jack-family charts leave the family (7 of them files LeoBlack calls
            // chordstream, none it calls chordjack) and 13 of a random 974 change
            // primary (1.3%, 7 of them to Tech, 4 to Delay once the chordjack veto
            // on delay lets go).
            double chordRepeatGate = Math.Max(Math.Max(
                Pressure(Ratio(stats.MultiOverlapChordPairs, Math.Max(1, stats.ChordPairs)), 0.16, 0.26),
                Pressure(singleJack.Jack1Share, 0.16, 0.26)),
                Pressure((double)stats.ChordRows / Math.Max(1, stats.ChordRuns), 4, 8));
            double chordjackScore = nonLnPatternGate * chordRepeatGate * Math.Max(
                chordjackBase,
                DanMath.MinGate(Pressure(chordRatio, 0.34, 0.72), Pressure(repeatedChordRatio, 0.04, 0.22)));
            // Delay is the off-grid flow itself (offGridRowShare in features.ts):
            // 1/8 and 1/12 rows in the BMS delay packs, 1/6 in the 7777's practice
            // packs. The previous reading was density plus entropy plus low
            // repetition, which any hard broken stream saturates: a 192 BPM 1/4
            // minijack-chordstream chart with 0.3% off-grid rows scored 0.60 and was
            // one player's #1 Delay play. Ramp measured 2026-09-03 over 232
            // delay-named 7K charts (share p10 0.43 / p25 0.61) against 143 jack
            // (p90 0.12), 106 stream (p90 0.10), 51 tech (p50 0.18 / p90 0.45) and
            // 700 random 7K (p50 0.07 / p75 0.23 / p90 0.46). The 0.2-0.5 ramp puts
            // the 0.25 line player-skills reads at 27.5% of rows: a first pass at
            // 0.12-0.42 (19.5%) tagged a bracket chordstream chart carrying thirteen
            // seconds of 1/8 fills, which the user read as stream, not delay. At this
            // ramp the tag keeps 96% of the delay corpus (94% before) and reaches 1%
            // of stream (58% before), 0% of jack, 27% of tech and 8% of random (21%
            // before, 1/6 and 1/8 o2jam-style files). The speed packs split 34% /
            // 66%: their 1/4 streams at 200+ BPM are speed, not delay, and leave the
            // tag on purpose.
            //
            // Same veto the tech tag takes in player-skills, for the same reason: a
            // 1/8 chordjack file is jack, not delay. Narrow ramp rather than a cliff,
            // closed at the same 0.8 TECH_TAG_CHORDJACK_VETO uses.
            double delayChordjackVeto = DanMath.Clamp01((0.8 - chordjackScore) / 0.05);
            double delayScore = nonLnFlowGate * delayChordjackVeto * Pressure(metrics.OffGridRowShare, 0.2, 0.5);
            // Ramps measured 2026-08 against mapper-named 7K pack corpora (279 jack /
            // 78 tech / 136 stream / 150 delay charts) plus 700 random 7K charts:
            // jack1Share sits at p25 0.271 / p50 0.404 on the jack corpus against
            // p90 0.163 (tech), 0.162 (stream) and 0.129 (delay), so the 0.08-0.28
            // ramp puts the 0.5 tag line at 0.18, between those populations. The
            // trill arm's 0.03-0.12 ramp tags from 0.075, three times any corpus p90.
            double jackScore = nonLnPatternGate * Math.Max(
                Pressure(singleJack.Jack1Share, 0.08, 0.28),
                Pressure(singleJack.TrillRunShare, 0.03, 0.12));
            candidates.Add(Hit(ManiaPatternId.Delay, delayScore, dataConfidence, $"{CompactPercent(metrics.OffGridRowShare)} rows off the 16th grid (1/6, 1/8, 1/12)"));
            candidates.Add(Hit(ManiaPatternId.Jack, jackScore, dataConfidence, $"{CompactPercent(singleJack.Jack1Share)} consecutive-row column re-hits, {CompactPercent(singleJack.TrillRunShare)} notes in two-row trill runs"));
            candidates.Add(Hit(ManiaPatternId.Chordjack, chordjackScore, dataConfidence, $"{CompactPercent(chordRatio)} chord rows, {CompactPercent(repeatedChordRatio)} repeated chord rows"));
            candidates.Add(Hit(ManiaPatternId.Tech, nonLnPatternGate * Math.Max(
                techScore,
                wideChordstream * DanMath.MinGate(Pressure(metrics.RowPatternChangeRate, 0.38, 0.72), Pressure(metrics.FastRowRatio, 0.08, 0.36))), dataConfidence, $"chord changes {CompactPercent(metrics.ChordSizeChangeRate)}, tech pressure {F1(metrics.TechPressure)}"));
            candidates.Add(Hit(ManiaPatternId.Bracket, nonLnPatternGate * bracketOverlapGate * DanMath.MinGate(
                Pressure(chordRatio, 0.28, 0.62),
                Pressure(bracketWindowRatio, 0.18, 0.38)), dataConfidence, $"{CompactPercent(bracketWindowRatio)} sustained non-jacking chord runs, {CompactPercent(metrics.ChordColumnOverlapRatio)} consecutive-chord column re-hits"));
            candidates.Add(Hit(ManiaPatternId.Chordstream, nonLnPatternGate * wideChordstream * DanMath.Clamp01((165 - metrics.JackPressure) / 130), dataConfidence, $"{CompactPercent(chordRatio)} chord rows mixed into stream"));
        }
        else
        {
            candidates.Add(Hit(ManiaPatternId.Stream, lowChordGate * streamActivity, dataConfidence, $"{metrics.KeyCount}K low-chord stream flow"));
            candidates.Add(Hit(ManiaPatternId.Chordstream, chordstreamGate, dataConfidence, $"{CompactPercent(chordRatio)} chord rows mixed into stream"));
            candidates.Add(Hit(ManiaPatternId.Chordjack, chordjackBase, dataConfidence, $"{CompactPercent(chordRatio)} chord rows, jack pressure {R0(metrics.JackPressure)}"));
            candidates.Add(Hit(ManiaPatternId.Tech, techScore, dataConfidence, $"pattern change {CompactPercent(metrics.RowPatternChangeRate)}"));
        }

        var allPatterns = candidates
            .Select(candidate => new ManiaPatternHit
            {
                Id = candidate.Id,
                Label = candidate.Label,
                Score = RoundedScore(candidate.Score),
                Confidence = RoundedScore(candidate.Confidence),
                Evidence = candidate.Evidence,
            })
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Label, StringComparer.Ordinal)
            .ToList();
        var visiblePatterns = allPatterns.Where(pattern => pattern.Score >= 0.2).Take(5).ToList();
        var lnPattern = allPatterns.FirstOrDefault(pattern => pattern.Id == ManiaPatternId.Ln);
        // Surface the LN axis alongside the visible patterns only when the chart has
        // a real LN signal (a nonzero score or any holds at all). Unconditionally
        // force-appending it stamped a score-0 ln entry onto every rice chart, which
        // leaked into stored classifications and pattern tags downstream.
        bool hasLnSignal = lnPattern != null && (lnPattern.Score > 0 || metrics.HoldRatio > 0);
        var lnAxisPatterns = new List<ManiaPatternHit>(visiblePatterns);
        if (lnPattern != null && hasLnSignal && !visiblePatterns.Any(pattern => pattern.Id == ManiaPatternId.Ln))
        {
            lnAxisPatterns.Add(lnPattern);
        }
        // Same escape hatch for the LN subtypes. A 4K chart fields ten rice
        // candidates against 7K's eight and doesn't damp its ln score, so the top-5
        // slice was dropping a third of the 4K lngeneral tags that cleared the bar
        // (and ~1% of 7K's). A subtype is an attribute of the chart, not a claim on
        // its identity, so it shouldn't have to outrank the rice families to be
        // recorded. Appended after, so the primary is unaffected.
        var subtypeOverflow = allPatterns.Where(pattern =>
            LN_SUBTYPE_IDS.Contains(pattern.Id)
            && pattern.Score >= 0.2
            && !lnAxisPatterns.Any(visible => visible.Id == pattern.Id)).ToList();
        var patterns = subtypeOverflow.Count > 0 ? lnAxisPatterns.Concat(subtypeOverflow).ToList() : lnAxisPatterns;

        return new ManiaPatternAnalysis
        {
            KeyCount = metrics.KeyCount,
            Primary = patterns.Count > 0 ? patterns[0] : (allPatterns.Count > 0 ? allPatterns[0] : null),
            Patterns = patterns,
            AllPatterns = allPatterns,
            Metrics = metrics,
            Warnings = features.Warnings,
        };
    }
}
