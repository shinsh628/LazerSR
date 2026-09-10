// Port of mania-hub live-backend/src/dan/vibro-sections.ts
using System.Globalization;
using LazerSR.DanCalculator.Beatmap;

namespace LazerSR.DanCalculator.Vibro;

public enum VibroReason
{
    RepeatedWall,          // "repeated_wall"
    SustainedJack,         // "sustained_jack"
    RepeatedJackStream,    // "repeated_jack_stream"
    RepeatedChord,         // "repeated_chord"
    RapidJackBurst,        // "rapid_jack_burst"
    IsolatedJack,          // "isolated_jack"
    SustainedChords,       // "sustained_chords"
    DenseChordRepetition,  // "dense_chord_repetition"
    FastRoll,              // "fast_roll"
    ExtremeDensity,        // "extreme_density"
}

/// <summary>"clean" | "adjusted" | "excluded"</summary>
public enum VibroStatus
{
    Clean,
    Adjusted,
    Excluded,
}

public sealed class VibroSection
{
    /// <summary>Original chart timestamps, before applying the music rate.</summary>
    public double StartTime;
    public double EndTime;
    public List<VibroReason> Reasons = new();
}

public sealed class VibroAnalysis
{
    public double Version;
    /// <summary>Player-rating eligibility; informational on ordinary full-chart estimates.</summary>
    public VibroStatus Status = VibroStatus.Clean;
    public List<VibroSection> Sections = new();
    public double ExcludedDurationMs;
    public double ActiveDurationMs;
    public double TimeShare;
    public double NoteShare;
    /// <summary>Upper bound on the removed share of judgement weight (hold tails included).
    /// Used to assign all observed accuracy loss to the retained material.</summary>
    public double JudgementShare;
    public double RemainingNotes;
}

public readonly record struct PrepareVibroChartResult(VibroAnalysis Analysis, string OsuText);

public static class VibroSections
{
    public const double VIBRO_SECTION_VERSION = 3;

    // Short repetitions need faster reloads than the 92ms sustained-longjack
    // floor, plus corroborating bursts in the same local phrase.
    private const double RECURRING_REPETITION_GAP_MS = 80;

    public static bool UsesSectionVibro(ManiaBeatmap map)
    {
        return map.KeyCount == 4 && map.Notes.Count > 0
            && (double)map.Notes.Count(note => note.IsHold) / map.Notes.Count <= 0.1;
    }

    private static int BitCount(int mask)
    {
        int count = 0;
        for (; mask != 0; mask &= mask - 1) count++;
        return count;
    }

    /// <summary>Locate vibro at the actual played speed, then measure the retained chart.
    /// Identity and metadata never participate in the decision.</summary>
    public static VibroAnalysis AnalyzeVibroSections(ManiaBeatmap map, double rate = 1)
    {
        var result = new VibroAnalysis
        {
            Version = VIBRO_SECTION_VERSION,
            Status = VibroStatus.Clean,
            Sections = new(),
            ExcludedDurationMs = 0,
            ActiveDurationMs = 0,
            TimeShare = 0,
            NoteShare = 0,
            JudgementShare = 0,
            RemainingNotes = map.Notes.Count,
        };
        if (!UsesSectionVibro(map) || !double.IsFinite(rate) || rate <= 0) return result;
        var scan = BuildVibroScan(map, rate);
        result.ActiveDurationMs = MeasureActiveDuration(scan);

        ScanRepeatedRows(scan);
        ScanRepeatedJackStreams(scan);
        ScanRepeatedPairs(scan);
        ScanFixedFingerWindows(scan);
        ScanIsolatedJacks(scan);
        var repeatedFingers = CountFastFingerReturns(scan, 70);
        ScanSustainedChords(scan, repeatedFingers);
        ScanDenseChords(scan);
        ScanFastRolls(scan, repeatedFingers);
        ScanExtremeDensity(scan);
        ScanRecurringRepetitions(scan);

        result.Sections = MergeSections(scan.Intervals);
        if (result.Sections.Count > 0) MeasureSectionCoverage(result, map, scan);
        return result;
    }

    private sealed class VibroScan
    {
        public required double[] Times;
        public required int[] Rows;
        public required double[] PrefixNotes;
        public required double Rate;
        public List<VibroSection> Intervals = new();
        public List<VibroSection> RepetitionBursts = new();
    }

    private static VibroScan BuildVibroScan(ManiaBeatmap map, double rate)
    {
        var masks = new Dictionary<double, int>();
        foreach (var note in map.Notes) masks[note.Time] = masks.GetValueOrDefault(note.Time) | (1 << note.Column);
        var times = masks.Keys.OrderBy(x => x).ToArray();
        var rows = times.Select(time => masks[time]).ToArray();
        var prefixNotes = new List<double> { 0 };
        foreach (var mask in rows) prefixNotes.Add(prefixNotes[^1] + BitCount(mask));
        return new VibroScan { Times = times, Rows = rows, PrefixNotes = prefixNotes.ToArray(), Rate = rate };
    }

    private static void AddSection(VibroScan scan, double start, double end, VibroReason reason)
    {
        if (end > start) scan.Intervals.Add(new VibroSection { StartTime = start, EndTime = end, Reasons = { reason } });
    }

    private static void ScanConsecutiveRows(VibroScan scan, Func<int, bool> matches, int minTransitions, VibroReason reason)
    {
        var times = scan.Times;
        int start = 1;
        for (int i = 1; i <= times.Length; i++)
        {
            if (i < times.Length && matches(i)) continue;
            if (i - start >= minTransitions) AddSection(scan, times[start - 1], times[i - 1], reason);
            start = i + 1;
        }
    }

    private static double RepeatedRowGapMs(int mask) => mask == 15 ? 105 : 92;

    private static void ScanRepeatedRows(VibroScan scan)
    {
        var times = scan.Times;
        var rows = scan.Rows;
        var rate = scan.Rate;
        // Repeated full row shapes distinguish a wall from diverse chordjack.
        ScanConsecutiveRows(scan, i => rows[i] == rows[i - 1] && BitCount(rows[i]) >= 2
            && times[i] - times[i - 1] <= RepeatedRowGapMs(rows[i]) * rate,
        11, VibroReason.RepeatedWall);
        ScanConsecutiveRows(scan, i => rows[i] == rows[i - 1] && times[i] - times[i - 1] <= 92 * rate,
        24, VibroReason.SustainedJack);
    }

    private static void ScanRepeatedJackStreams(VibroScan scan)
    {
        var times = scan.Times;
        var rows = scan.Rows;
        var rate = scan.Rate;
        // A sustained stream of short fixed-shape jacks is repetition too. Quads
        // on the accents and a different repeated jump in the next beat must not
        // reset the evidence for the entire passage. Measure rows participating in
        // 3..11-hit repetitions across 64 uninterrupted fast rows, with substantial
        // repeated-chord work. Single-finger triples with occasional chord accents
        // are ordinary minijack; doubles alone do not qualify either. Longer walls
        // already have their own detector and must not expand into surrounding
        // ordinary chordjack through this window. Short repetitions use the same
        // speed floor as long walls: accumulating slower jump-jack bursts does not
        // make them vibro just because the passage lasts longer. Quad repetitions
        // retain their wider cutoff, still bounded by the 100ms continuous stream.
        var burstRows = new int[times.Length];
        int repeatStart = 0;
        double maxRepeatGap = 0;
        for (int i = 1; i <= times.Length; i++)
        {
            // Keep phrase boundaries at the stream cutoff. Splitting a long jack on
            // rounded 92/93ms gaps would manufacture qualifying short repetitions.
            if (i < times.Length && rows[i] == rows[i - 1] && times[i] - times[i - 1] <= 100 * rate)
            {
                maxRepeatGap = Math.Max(maxRepeatGap, times[i] - times[i - 1]);
                continue;
            }
            int count = i - repeatStart;
            if (count >= 3 && count < 12 && maxRepeatGap <= RepeatedRowGapMs(rows[repeatStart]) * rate)
            {
                for (int f = repeatStart; f < i; f++) burstRows[f] = 1;
            }
            repeatStart = i;
            maxRepeatGap = 0;
        }
        int fastStart = 0;
        double repeatedInWindow = 0;
        double repeatedChordsInWindow = 0;
        const int burstWindowRows = 64;
        for (int i = 0; i < times.Length; i++)
        {
            if (i > 0 && times[i] - times[i - 1] > 100 * rate) fastStart = i;
            repeatedInWindow += burstRows[i];
            repeatedChordsInWindow += burstRows[i] != 0 && BitCount(rows[i]) >= 2 ? 1 : 0;
            if (i >= burstWindowRows) repeatedInWindow -= burstRows[i - burstWindowRows];
            if (i >= burstWindowRows) repeatedChordsInWindow -= burstRows[i - burstWindowRows] != 0 && BitCount(rows[i - burstWindowRows]) >= 2 ? 1 : 0;
            if (i - fastStart + 1 >= burstWindowRows && repeatedInWindow / burstWindowRows >= 0.7
                && repeatedChordsInWindow / burstWindowRows >= 0.35)
            {
                AddSection(scan, times[i - burstWindowRows + 1], times[i], VibroReason.RepeatedJackStream);
            }
        }
    }

    private static void ScanRepeatedPairs(VibroScan scan)
    {
        var times = scan.Times;
        var rows = scan.Rows;
        var rate = scan.Rate;
        // An occasional accent must not disguise an otherwise fixed two-finger
        // repetition. Require the same pair throughout, not a different shared
        // pair on each changing chord.
        foreach (var pair in new[] { 3, 5, 6, 9, 10, 12 })
        {
            int p = pair;
            ScanConsecutiveRows(scan, i => (rows[i] & p) == p && (rows[i - 1] & p) == p
                && times[i] - times[i - 1] <= 70 * rate,
            11, VibroReason.RepeatedChord);
        }
    }

    private sealed record RepetitionBand(double GapMs, double AverageGapMs, int MinHits, double MinShare, VibroReason Reason);

    private static readonly RepetitionBand[] SINGLE_FINGER_BANDS =
    {
        new RepetitionBand(30, 30, 4, 0, VibroReason.RapidJackBurst),
        new RepetitionBand(85, 55, 9, 0, VibroReason.RapidJackBurst),
    };
    private static readonly RepetitionBand[] PAIR_BANDS =
    {
        new RepetitionBand(55, 55, 4, 0, VibroReason.RapidJackBurst),
        new RepetitionBand(60, 60, 6, 0, VibroReason.RapidJackBurst),
        new RepetitionBand(92, 92, 12, 0.7, VibroReason.RepeatedChord),
    };
    private static readonly RepetitionBand[] QUAD_BANDS =
    {
        new RepetitionBand(92, 92, 8, 0, VibroReason.RepeatedWall),
    };

    private static void ScanFixedFingerWindows(VibroScan scan)
    {
        var times = scan.Times;
        var rows = scan.Rows;
        var prefixNotes = scan.PrefixNotes;
        var rate = scan.Rate;
        // Follow fixed fingers through chord accents and intervening rows. Each
        // window must meet its own physical speed and repetition requirements;
        // adding slower context cannot dilute a fast local window. At moderate
        // speeds a fixed pair must dominate the local heads, so a shared pair in
        // otherwise varied chordjack is not enough. Quads need fewer repetitions.
        // Irregular fast single-finger runs allow brief slower gaps only while
        // their average return interval remains extremely short.
        foreach (var mask in new[] { 1, 2, 4, 8, 3, 5, 6, 9, 10, 12, 15 })
        {
            int fingers = BitCount(mask);
            var indices = new List<int>();
            for (int i = 0; i < rows.Length; i++) if ((rows[i] & mask) == mask) indices.Add(i);
            var bands = fingers == 1 ? SINGLE_FINGER_BANDS : fingers == 4 ? QUAD_BANDS : PAIR_BANDS;
            foreach (var band in bands)
            {
                int phraseStart = 0;
                for (int i = 0; i < indices.Count; i++)
                {
                    if (i > 0 && times[indices[i]] - times[indices[i - 1]] > band.GapMs * rate) phraseStart = i;
                    int firstHit = i - band.MinHits + 1;
                    if (firstHit < phraseStart) continue;
                    int first = indices[firstHit];
                    int last = indices[i];
                    if (times[last] - times[first] > (band.MinHits - 1) * band.AverageGapMs * rate) continue;
                    double localNotes = prefixNotes[last + 1] - prefixNotes[first];
                    if (band.MinHits * fingers / localNotes >= band.MinShare) AddSection(scan, times[first], times[last], band.Reason);
                }
            }
            CollectShortRepetitions(scan, indices, fingers);
        }
    }

    /// <summary>Short jacks below the immediate-burst speed need nearby repetition evidence.</summary>
    private static void CollectShortRepetitions(VibroScan scan, List<int> indices, int fingers)
    {
        var times = scan.Times;
        var prefixNotes = scan.PrefixNotes;
        var rate = scan.Rate;
        var repetitionBursts = scan.RepetitionBursts;
        int minHits = fingers == 1 ? 6 : 4;
        int maxHits = fingers == 1 ? 24 : fingers == 2 ? 11 : 7;
        int start = 0;
        for (int end = 1; end <= indices.Count; end++)
        {
            // Keep a whole run together across rounding and minor rhythm changes.
            // Splitting long walls into short candidates would manufacture recurrence.
            if (end < indices.Count && times[indices[end]] - times[indices[end - 1]] <= 100 * rate) continue;
            int hits = end - start;
            if (hits >= minHits && hits <= maxHits)
            {
                for (int i = start + minHits - 1; i < end; i++)
                {
                    int first = indices[i - minHits + 1], last = indices[i];
                    // Short runs need faster finger reloads than a sustained longjack:
                    // ordinary ~90ms speedjack bursts must not become vibro by repetition.
                    if (times[last] - times[first] > (minHits - 1) * RECURRING_REPETITION_GAP_MS * rate) continue;
                    double share = minHits * fingers / (prefixNotes[last + 1] - prefixNotes[first]);
                    // Quad accents at both ends of a four-hit pair leave 8 of 12 heads
                    // on that pair. They must not erase the repetition in its middle.
                    if (fingers == 1 ? share <= 0.5 || (double)minHits / (last - first + 1) < 0.9 : fingers == 2 && share < 2.0 / 3) continue;
                    repetitionBursts.Add(new VibroSection
                    {
                        StartTime = times[first],
                        EndTime = times[last],
                        Reasons = { fingers == 1 ? VibroReason.IsolatedJack : fingers == 2 ? VibroReason.RepeatedChord : VibroReason.RepeatedWall },
                    });
                }
            }
            start = end;
        }
    }

    private static void ScanIsolatedJacks(VibroScan scan)
    {
        var times = scan.Times;
        var rows = scan.Rows;
        var prefixNotes = scan.PrefixNotes;
        var rate = scan.Rate;
        var intervals = scan.Intervals;
        // Sparse accompaniment must not hide a sustained single-finger run. Long
        // runs may use a majority of heads when that finger also occupies nearly
        // every row; short bursts still need the stronger 65% dominance/coverage
        // policy. Dense changing chords around a busy finger do not meet this rule.
        var bursts = new List<VibroSection>();
        double isolatedBurstNotes = 0;
        for (int column = 0; column < 4; column++)
        {
            var indices = new List<int>();
            for (int i = 0; i < rows.Length; i++) if ((rows[i] & (1 << column)) != 0) indices.Add(i);
            int start = 0;
            for (int i = 1; i <= indices.Count; i++)
            {
                if (i < indices.Count && times[indices[i]] - times[indices[i - 1]] <= 100 * rate) continue;
                int count = i - start;
                if (count >= 9)
                {
                    int first = indices[start];
                    int last = indices[i - 1];
                    double localNotes = prefixNotes[last + 1] - prefixNotes[first];
                    bool dominant = count / localNotes >= 0.65;
                    bool longRun = count >= 25 && (times[last] - times[first]) / (count - 1) <= 92 * rate;
                    bool accompaniedLongJack = longRun && count / localNotes > 0.5 && (double)count / (last - first + 1) >= 0.9;
                    if (dominant || accompaniedLongJack)
                    {
                        var section = new VibroSection { StartTime = times[first], EndTime = times[last], Reasons = { VibroReason.IsolatedJack } };
                        if (dominant)
                        {
                            bursts.Add(section);
                            isolatedBurstNotes += count;
                        }
                        if (longRun) intervals.Add(section);
                    }
                }
                start = i;
            }
        }
        // Repeated short isolated bursts are evidence together, not a reason to
        // carve a lone speedjack burst out of an otherwise varied chart.
        if (bursts.Count >= 4 && isolatedBurstNotes / prefixNotes[^1] >= 0.2) intervals.AddRange(bursts);
    }

    private static List<double> CountFastFingerReturns(VibroScan scan, double gapMs)
    {
        var times = scan.Times;
        var rows = scan.Rows;
        var rate = scan.Rate;
        var lastTimes = new double[4];
        Array.Fill(lastTimes, double.NegativeInfinity);
        var repeatedFingers = new List<double>();
        for (int i = 0; i < times.Length; i++)
        {
            double fast = 0;
            for (int column = 0; column < 4; column++)
            {
                if ((rows[i] & (1 << column)) == 0) continue;
                if (times[i] - lastTimes[column] <= gapMs * rate) fast++;
                lastTimes[column] = times[i];
            }
            repeatedFingers.Add(fast);
        }
        return repeatedFingers;
    }

    private static void ScanSustainedChords(VibroScan scan, IReadOnlyList<double> repeatedFingers)
    {
        ScanConsecutiveRows(scan, i => repeatedFingers[i] >= 2, 32, VibroReason.SustainedChords);
    }

    private sealed record DenseChordBand(double GapMs, double ChordShare, double NoteShare);

    private static void ScanDenseChords(VibroScan scan)
    {
        var times = scan.Times;
        var prefixNotes = scan.PrefixNotes;
        var rate = scan.Rate;
        // Dense overlapping chords can keep reloading the same fingers while
        // changing the full row shape or inserting a light row/breather. Requiring
        // 32 consecutive heavy rows misses those passages. Measure both the share
        // of rows reloading multiple fingers and the share of all heads returning
        // quickly, over 64 rows. Faster repeats need less chord-row coverage, but
        // still must dominate the note load. Ordinary fast changing chordjack does
        // not qualify through speed alone.
        const int denseWindowRows = 64;
        foreach (var band in new[]
        {
            new DenseChordBand(75, 0.65, 0.7),
            new DenseChordBand(50, 0.4, 0.65),
        })
        {
            var fastCounts = CountFastFingerReturns(scan, band.GapMs);
            int phraseStart = 0;
            double chordRows = 0;
            double fastHeads = 0;
            for (int i = 0; i < times.Length; i++)
            {
                if (i > 0 && times[i] - times[i - 1] > 1000 * rate) phraseStart = i;
                double fast = fastCounts[i];
                chordRows += fast >= 2 ? 1 : 0;
                fastHeads += fast;
                if (i >= denseWindowRows)
                {
                    chordRows -= fastCounts[i - denseWindowRows] >= 2 ? 1 : 0;
                    fastHeads -= fastCounts[i - denseWindowRows];
                }
                int first = i - denseWindowRows + 1;
                if (first >= phraseStart && times[i] - times[first] <= (denseWindowRows - 1) * 100 * rate
                    && chordRows / denseWindowRows >= band.ChordShare
                    && fastHeads / (prefixNotes[i + 1] - prefixNotes[first]) >= band.NoteShare)
                {
                    AddSection(scan, times[first], times[i], VibroReason.DenseChordRepetition);
                }
            }
        }
    }

    private static void ScanFastRolls(VibroScan scan, IReadOnlyList<double> repeatedFingers)
    {
        var times = scan.Times;
        var rows = scan.Rows;
        var prefixNotes = scan.PrefixNotes;
        var rate = scan.Rate;
        var intervals = scan.Intervals;
        // Disjoint fast rows alone also include flams. Require sustained fast
        // per-finger returns in the same run, not elsewhere in the chart.
        int rollStart = 1;
        var rollBursts = new List<VibroSection>();
        double rollBurstNotes = 0;
        for (int i = 1; i <= times.Length; i++)
        {
            if (i < times.Length && (rows[i] & rows[i - 1]) == 0 && times[i] - times[i - 1] <= 25 * rate) continue;
            if (i - rollStart >= 8)
            {
                double fast = 0;
                for (int r = rollStart; r < i; r++) fast += repeatedFingers[r];
                double notes = prefixNotes[i] - prefixNotes[rollStart];
                // Count returns inside this burst, without borrowing hits from the
                // preceding chord or jack passage.
                var lastTimes = new double[4];
                Array.Fill(lastTimes, double.NegativeInfinity);
                double contextualFast = 0;
                for (int row = rollStart - 1; row < i; row++)
                {
                    for (int column = 0; column < 4; column++)
                    {
                        if ((rows[row] & (1 << column)) == 0) continue;
                        if (times[row] - lastTimes[column] <= RECURRING_REPETITION_GAP_MS * rate) contextualFast++;
                        lastTimes[column] = times[row];
                    }
                }
                if (contextualFast / notes >= 0.25)
                {
                    scan.RepetitionBursts.Add(new VibroSection { StartTime = times[rollStart - 1], EndTime = times[i - 1], Reasons = { VibroReason.FastRoll } });
                }
                if (fast / notes >= 0.25)
                {
                    var section = new VibroSection { StartTime = times[rollStart - 1], EndTime = times[i - 1], Reasons = { VibroReason.FastRoll } };
                    if (i - rollStart >= 24) intervals.Add(section);
                    rollBursts.Add(section);
                    rollBurstNotes += notes;
                }
            }
            rollStart = i + 1;
        }
        if (rollBursts.Count >= 4 && rollBurstNotes / prefixNotes[^1] >= 0.2) intervals.AddRange(rollBursts);
    }

    /// <summary>Alternating fixed jumps reload the same fingers even though adjacent rows differ.</summary>
    private static void CollectAlternatingChords(VibroScan scan)
    {
        var times = scan.Times;
        var rows = scan.Rows;
        var rate = scan.Rate;
        var repetitionBursts = scan.RepetitionBursts;
        int start = 0;
        for (int i = 1; i <= times.Length; i++)
        {
            if (i < times.Length && BitCount(rows[i]) == 2 && BitCount(rows[i - 1]) == 2
                && (rows[i] & rows[i - 1]) == 0 && times[i] - times[i - 1] <= 50 * rate
                && (i == start + 1 || (rows[i] == rows[i - 2] && times[i] - times[i - 2] <= RECURRING_REPETITION_GAP_MS * rate))) continue;
            if (i - start >= 8) repetitionBursts.Add(new VibroSection { StartTime = times[start], EndTime = times[i - 1], Reasons = { VibroReason.RepeatedChord } });
            start = i;
        }
    }

    /// <summary>Three nearby bursts must cover 24 rows and 70% of their local phrase.
    /// Keep their exact intervals, not the pauses between them.</summary>
    private static void ScanRecurringRepetitions(VibroScan scan)
    {
        CollectAlternatingChords(scan);
        // Merge overlapping finger windows first: one repeated jump must not count
        // as three bursts merely because both fingers and their pair were detected.
        // A shared quad accent can end one hand's burst and start the other's.
        // Preserve that boundary; overlapping windows still represent one burst.
        var bursts = MergeSections(scan.RepetitionBursts, false);
        if (bursts.Count < 3) return;
        var indices = new Dictionary<double, int>();
        for (int index = 0; index < scan.Times.Length; index++) indices[scan.Times[index]] = index;
        var prefixDuration = new List<double> { 0 };
        var prefixRows = new List<double> { 0 };
        var prefixJacks = new List<double> { 0 };
        for (int i = 0; i < bursts.Count; i++)
        {
            var burst = bursts[i];
            prefixDuration.Add(prefixDuration[^1] + burst.EndTime - burst.StartTime);
            bool sharedRow = i > 0 && bursts[i - 1].EndTime == burst.StartTime;
            prefixRows.Add(prefixRows[^1] + indices[burst.EndTime] - indices[burst.StartTime] + (sharedRow ? 0 : 1));
            prefixJacks.Add(prefixJacks[^1] + (burst.Reasons.Any(reason => reason != VibroReason.FastRoll) ? 1 : 0));
        }
        // Difference counts avoid revisiting every burst for overlapping windows.
        var included = new int[bursts.Count + 1];
        int phraseStart = 0;
        for (int end = 0; end < bursts.Count; end++)
        {
            if (end > 0 && bursts[end].StartTime - bursts[end - 1].EndTime > 1000 * scan.Rate) phraseStart = end;
            for (int start = end - 2; start >= phraseStart; start--)
            {
                double span = bursts[end].EndTime - bursts[start].StartTime;
                if (span > 6400 * scan.Rate) break;
                double duration = prefixDuration[end + 1] - prefixDuration[start];
                bool sharedFirstRow = start > 0 && bursts[start - 1].EndTime == bursts[start].StartTime;
                double rowsSpan = prefixRows[end + 1] - prefixRows[start] + (sharedFirstRow ? 1 : 0);
                // Rolls can join a repetitive jack passage, but must not establish one
                // on their own: ordinary short roll charts retain their existing rule.
                double jacks = prefixJacks[end + 1] - prefixJacks[start];
                if (jacks >= 2 && rowsSpan >= 24 && duration / span >= 0.7)
                {
                    included[start]++;
                    included[end + 1]--;
                }
            }
        }
        int support = 0;
        for (int i = 0; i < bursts.Count; i++)
        {
            support += included[i];
            if (support > 0) scan.Intervals.Add(bursts[i]);
        }
    }

    private static void ScanExtremeDensity(VibroScan scan)
    {
        var times = scan.Times;
        var rate = scan.Rate;
        int windowStart = 0;
        for (int i = 0; i < times.Length; i++)
        {
            while (times[i] - times[windowStart] > 1000 * rate) windowStart++;
            if (i - windowStart + 1 >= 65) AddSection(scan, times[windowStart], times[i], VibroReason.ExtremeDensity);
        }
    }

    private static List<VibroSection> MergeSections(List<VibroSection> intervals, bool mergeTouching = true)
    {
        var sections = new List<VibroSection>();
        foreach (var section in intervals.OrderBy(a => a.StartTime))
        {
            var previous = sections.Count > 0 ? sections[^1] : null;
            if (previous != null && (section.StartTime < previous.EndTime || (mergeTouching && section.StartTime == previous.EndTime)))
            {
                previous.EndTime = Math.Max(previous.EndTime, section.EndTime);
                var merged = new List<VibroReason>(previous.Reasons);
                foreach (var reason in section.Reasons) if (!merged.Contains(reason)) merged.Add(reason);
                previous.Reasons = merged;
            }
            else
            {
                sections.Add(new VibroSection { StartTime = section.StartTime, EndTime = section.EndTime, Reasons = new List<VibroReason>(section.Reasons) });
            }
        }
        return sections;
    }

    private static double MeasureActiveDuration(VibroScan scan)
    {
        var times = scan.Times;
        var rate = scan.Rate;
        // Empty intros, breaks and padding must not dilute a dominant spam section.
        double duration = 0;
        for (int i = 1; i < times.Length; i++)
        {
            duration += Math.Min(times[i] - times[i - 1], 1000 * rate) / rate;
        }
        return duration;
    }

    private static void MeasureSectionCoverage(VibroAnalysis result, ManiaBeatmap map, VibroScan scan)
    {
        var times = scan.Times;
        var rate = scan.Rate;
        double removedNotes = 0;
        double removedWeight = 0;
        foreach (var note in map.Notes)
        {
            if (!OverlapsVibro(note.Time, note.EndTime, result.Sections)) continue;
            removedNotes++;
            removedWeight += note.IsHold ? 2 : 1;
        }
        result.RemainingNotes -= removedNotes;
        result.NoteShare = removedNotes / map.Notes.Count;
        result.JudgementShare = removedWeight / (removedWeight + result.RemainingNotes);
        int sectionIndex = 0;
        for (int i = 1; i < times.Length; i++)
        {
            while (sectionIndex < result.Sections.Count && result.Sections[sectionIndex].EndTime < times[i - 1]) sectionIndex++;
            for (int j = sectionIndex; j < result.Sections.Count && result.Sections[j].StartTime < times[i]; j++)
            {
                var section = result.Sections[j];
                double overlap = Math.Min(times[i], section.EndTime) - Math.Max(times[i - 1], section.StartTime);
                if (overlap > 0) result.ExcludedDurationMs += Math.Min(overlap, 1000 * rate) / rate;
            }
        }
        result.TimeShare = result.ActiveDurationMs > 0 ? result.ExcludedDurationMs / result.ActiveDurationMs : 1;
        // Both time and note coverage matter. The remaining chart is recomputed,
        // so easy padding cannot retain the original spam-inflated difficulty.
        result.Status = result.TimeShare <= 0.15 && result.NoteShare <= 0.25
            && result.RemainingNotes >= 300 && result.ActiveDurationMs - result.ExcludedDurationMs >= 20_000
            ? VibroStatus.Adjusted : VibroStatus.Excluded;
    }

    private static bool OverlapsVibro(double start, double end, List<VibroSection> sections)
    {
        int low = 0;
        int high = sections.Count;
        while (low < high)
        {
            int mid = (low + high) >> 1;
            if (sections[mid].EndTime < start) low = mid + 1;
            else high = mid;
        }
        return low < sections.Count && sections[low].StartTime <= end;
    }

    // JS Number(): whitespace-trimmed full-string numeric parse; "" -> 0, invalid -> NaN.
    // PORT NOTE: does not replicate JS hex/Infinity literal parsing; .osu hitobject
    // fields are plain integers so this is sufficient.
    private static double JsNumber(string? s)
    {
        if (s is null) return double.NaN;
        s = s.Trim();
        if (s.Length == 0) return 0;
        return double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }

    /// <summary>Preserve all timestamps, metadata and breaks. Only remove hit objects;
    /// stitching the remaining notes together would create artificial stamina.</summary>
    public static string RemoveVibroSections(string osuText, List<VibroSection> sections)
    {
        bool hitObjects = false;
        var kept = new List<string>();
        foreach (var line in osuText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[")) hitObjects = trimmed == "[HitObjects]";
            if (!hitObjects || !trimmed.Contains(","))
            {
                kept.Add(line);
                continue;
            }
            var parts = trimmed.Split(',');
            double start = JsNumber(parts.Length > 2 ? parts[2] : null);
            // JS: Number(parts[3]) & 128 coerces via ToInt32; (int)NaN is 0 in .NET.
            int type = double.IsNaN(JsNumber(parts.Length > 3 ? parts[3] : null)) ? 0 : (int)JsNumber(parts.Length > 3 ? parts[3] : null);
            double end = (type & 128) != 0
                ? JsNumber(parts.Length > 5 ? parts[5].Split(':')[0] : null)
                : start;
            if (!OverlapsVibro(start, end, sections)) kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    public static PrepareVibroChartResult PrepareVibroChart(string osuText, double rate = 1)
        => PrepareVibroChart(osuText, rate, ManiaBeatmapParser.Parse(osuText));

    public static PrepareVibroChartResult PrepareVibroChart(string osuText, double rate, ManiaBeatmap map)
    {
        var analysis = AnalyzeVibroSections(map, rate);
        return new PrepareVibroChartResult(
            analysis,
            analysis.Status == VibroStatus.Adjusted ? RemoveVibroSections(osuText, analysis.Sections) : osuText);
    }

    /// <summary>Lower bound on retained-note accuracy: assume the removed notes were
    /// perfect and all losses occurred in the remainder. Input/output are 0..1.</summary>
    public static double ConservativeVibroAccuracy(double accuracy, double removedShare)
    {
        if (!double.IsFinite(accuracy) || !double.IsFinite(removedShare) || removedShare >= 1) return 0;
        return Math.Max(0, Math.Min(1, 1 - (1 - accuracy) / (1 - Math.Max(0, removedShare))));
    }
}
