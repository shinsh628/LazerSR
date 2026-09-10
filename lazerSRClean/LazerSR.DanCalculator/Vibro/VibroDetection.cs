// Port of mania-hub live-backend/src/dan/vibro-detection.ts
using LazerSR.DanCalculator.Beatmap;

namespace LazerSR.DanCalculator.Vibro;

// Detection entry points shared by chart analysis and player ratings.
// 4K rice delegates to the section detector; the remaining rules preserve
// the legacy policy for hold-heavy charts and other key counts.
public static class VibroDetection
{
    // LN vibro: chart-wide staggered hold spam (the "gabe power" shape - dense LN
    // rolls you play by shaking, not reading). The longjack detector only sees
    // rice jack clusters, so these charts sailed through with inflated LN dans.
    // A p75 row gap this tight sustained over a whole chart is beyond any legit
    // LN chart: the densest ranked LN dumps (Denouement) sit at ~75ms rows, the
    // calibration corpus bottoms out at 54ms p50 / 76ms p75, vibro at 22ms.
    private const double LN_VIBRO_MIN_ROWS = 150;
    private const double LN_VIBRO_MIN_HOLD_RATIO = 0.5;
    private const double LN_VIBRO_MAX_P75_ROW_GAP_MS = 40;

    // Rice vibro measured directly from note timing, because the longjack-cluster
    // detector only fires on clusters labeled "Longjacks": chord-wall vibro reads
    // as chordjack/quadstream and sailed through (Tamania's "impossible vibro pack"
    // indexed as beta++ jack). Thresholds were calibrated against the local corpus:
    // vibro-titled packs vs ranked jack files and celebrated dense charts
    // (Gengaozo Innocence 1.05x, STRONG 280 1.1x, hurricanic 1.2x all stay clean).
    //
    // Tier 1 (any keymode): sustained same-column hammering. A run of 24+ hits with
    // gaps <= 92ms (~11/s) is beyond human jacking when a quarter of the chart's
    // column gaps are that fast; legit speedjack bursts stay under ~16 hits and
    // ranked jack files measure runs <= 6.
    private const double RICE_VIBRO_MIN_NOTES = 300;
    private const double RICE_VIBRO_COLUMN_GAP_MS = 92;
    private const double RICE_VIBRO_COLUMN_MIN_RUN = 24;
    private const double RICE_VIBRO_COLUMN_MIN_RATIO = 0.25;
    // Tier 2 (4K only): slower chord-wall vibro (~97-105ms quads you shake, not
    // jack). Needs both recurring 4-note wall rows and a chart soaked in fast
    // column repeats; dense legit 4K charts top out at ~3.3% wall rows, and the
    // legit charts that do carry wall rows keep their column repeats at 105ms+,
    // so their <=98ms column ratio measures 0.0 - the 0.32 floor has full margin.
    private const double RICE_VIBRO_WALL_GAP_MS = 105;
    private const double RICE_VIBRO_WALL_MIN_ROWS = 12;
    private const double RICE_VIBRO_WALL_MIN_ROW_RATIO = 0.035;
    private const double RICE_VIBRO_WALL_COLUMN_GAP_MS = 98;
    private const double RICE_VIBRO_WALL_COLUMN_MIN_RATIO = 0.32;

    // Tier 3 (any keymode): burst-soak vibro. Packs full of 8-23-note same-column
    // bursts at <=100ms slip tiers 1-2 (runs too short for tier 1, no quad walls
    // for tier 2), but a chart where a fifth of all column gaps sit inside such
    // runs is nothing but bursts. Legit files with occasional speedjack stay far
    // under: the calibration corpus's densest unflagged charts (William Tell EX
    // piano rolls, Gengaozo 7K Z O) measure ~0.13, ranked jack files ~0.
    private const double RICE_VIBRO_BURST_MIN_NOTES = 200;
    private const double RICE_VIBRO_BURST_GAP_MS = 100;
    private const double RICE_VIBRO_BURST_MIN_RUN = 8;
    private const double RICE_VIBRO_BURST_MIN_RUNS = 4;
    private const double RICE_VIBRO_BURST_MIN_FRACTION = 0.2;

    // Tier 4 (any keymode): superhuman row density. Tiers 1-3 all measure repeats
    // within a column, so a chart that sprays its spam across columns as jumps or
    // quads slips every one of them - the "Hello (BPM) 2023" shape, where 4
    // seconds of 15ms jumps closing an otherwise ordinary LN chart carry 19% of
    // the notes and drag MinaCalc's chordjack from 22 to 76 (at the 0.93 goal the
    // SSR chisel cannot write off a section holding that many points, so it rates
    // the file for the spam). Rows rather than notes, because a wide chord is one
    // action: 7K "This Future" peaks at 91 notes/s but only 13 rows/s and is
    // entirely legit. Measured across every analyzed ranked and loved 4/6/7K chart
    // (n=27,892), peak rows/s tops out at 55 (4K), 57 (7K) and 49 (6K), so 65
    // clears the corpus by 14%; it fires on 88 of 128,784 analyzed charts, none of
    // them ranked or loved.
    private const double RICE_VIBRO_ROW_RATE_WINDOW_MS = 1000;
    private const double RICE_VIBRO_MAX_ROWS_PER_SECOND = 65;

    // Tier 5 (any keymode): chord jacks faster than a hand can jack. Tier 2 only
    // counts *quad* pairs, so a 280BPM file alternating triples and quads slips
    // it (the "Buddah Attachments [280BPM CJ]" shape measures 0.027 against that
    // tier's 0.035 floor) while tier 1's run test misses because no single column
    // ever holds 24 consecutive fast gaps - the chart spreads them. Measuring
    // chord rows directly catches the whole jack-pack family: adjacent rows that
    // both carry a near-full chord inside 70ms, which is a 214BPM chord jack, as
    // a share of all row transitions. Chord size scales with the keymode because
    // a 3-note chord is a wall in 4K and everyday density in 7K. Across every
    // analyzed ranked and loved chart this tops out at 0.0040 (4K, n=20,735),
    // 0.0000 (6K) and 0.0006 (7K), so 0.02 clears the corpus five times over; it
    // fires on 0.32% of analyzed 4K charts, none of them ranked or loved.
    private const double RICE_VIBRO_CHORD_WALL_GAP_MS = 70;
    private const double RICE_VIBRO_CHORD_WALL_MIN_RATIO = 0.02;

    // Tier 6 (4K only): roll vibro, the per-finger speed of the chart's rolls at
    // the played rate. A 163BPM 1/16 four-column roll hits each finger every 92ms
    // and breaks every 8-9 notes, which every tier above lets through (runs too
    // short for tiers 1 and 3, no chords for 2 and 5, the breaks hold tier 4 under
    // its row cap) - and at 1.5x it is a 61ms per-finger shake nobody rolls. Two
    // measures, both at the played rate, and both have to hold. The original
    // four-column calibration used these cutoffs (expanded below):
    //  - per-finger: the share of all column gaps at or under 65ms (~15/s per
    //    finger). Ranked and loved 4K at 1.0x (n=23,545) top out at 0.114; the
    //    motivating chart measures 0.47 at 1.5x and 0.00 at 1.0x.
    //  - roll: the share of row transitions at or under 20ms that move to other
    //    columns (same-column flams like skalop's 8ms doubles do not count). This
    //    is what keeps
    //    the per-finger measure honest: 230-240BPM 1/4 jack files and fast
    //    minijack charts also put a quarter of their column gaps under 65ms
    //    (Overdose Party [230JACK], The Finale (Zero), skalop) but their rows sit
    //    64ms+ apart - a jack is one finger, a roll is the whole hand cycling.
    //    Ranked and loved 4K at 1.0x stay under 0.30 (a loved chart tier 1
    //    already flags), p99.9 at 0.11; the motivating chart measures 0.67 at
    //    1.5x (23ms rows) and 0.00 at 1.0x.
    // No ranked or loved 4K chart meets both at 1.0x. 4K only: ranked 7K carries
    // 55ms column repeats routinely (0.48 on VIVID), so the per-finger measure
    // says nothing there.
    // Three-column rolls need a slightly wider timing envelope than the original
    // four-column calibration: 100ms repeats / 33-34ms rows become 66-67ms /
    // 22-23ms at DT. Requiring both chart-wide shares still separates these from
    // fast jacks and isolated flams; neither signal alone proves roll vibro.
    // Across 111,502 cached 4K charts and 23,593 stored uprates, the expansion
    // adds no ranked/loved charts at 1.0x and reaches 16 additional uprate pairs.
    // The reported pattern measures 0.283 column share and 0.506 row share at DT.
    private const double RICE_VIBRO_ROLL_GAP_MS = 70;
    private const double RICE_VIBRO_ROLL_MIN_RATIO = 0.25;
    private const double RICE_VIBRO_ROLL_ROW_GAP_MS = 25;
    private const double RICE_VIBRO_ROLL_MIN_ROW_RATIO = 0.3;

    private readonly record struct ColumnGapsResult(double MaxRun, double Ratio);
    private readonly record struct BurstRunsResult(double Runs, double Fraction);
    private readonly record struct WallRowsResult(double Rows, double Ratio);

    private static ColumnGapsResult ColumnFastGaps(ManiaBeatmap map, double cutoffMs)
    {
        var byColumn = new Dictionary<int, List<double>>();
        foreach (var note in map.Notes)
        {
            if (!byColumn.TryGetValue(note.Column, out var list)) { list = new(); byColumn[note.Column] = list; }
            list.Add(note.Time);
        }
        double maxRun = 0;
        double fast = 0;
        double total = 0;
        foreach (var times in byColumn.Values)
        {
            times.Sort();
            double run = 0;
            for (int index = 1; index < times.Count; index++)
            {
                double gap = times[index] - times[index - 1];
                if (gap <= 0) continue;
                total++;
                if (gap <= cutoffMs)
                {
                    fast++;
                    run++;
                    if (run > maxRun) maxRun = run;
                }
                else
                {
                    run = 0;
                }
            }
        }
        return new ColumnGapsResult(maxRun, total > 0 ? fast / total : 0);
    }

    /// <summary>
    /// Share of row-to-row transitions at or under cutoffMs that move to other
    /// columns. A roll cycles the hand, so consecutive rows share no column; a
    /// same-column pair that close is a flam (skalop's 8ms doubles) or a stack,
    /// which is not the shape this measures.
    /// </summary>
    private static double FastRollRowShare(ManiaBeatmap map, double cutoffMs)
    {
        var rows = new Dictionary<double, HashSet<int>>();
        foreach (var note in map.Notes)
        {
            if (!rows.TryGetValue(note.Time, out var columns)) { columns = new(); rows[note.Time] = columns; }
            columns.Add(note.Column);
        }
        var times = rows.Keys.OrderBy(x => x).ToArray();
        if (times.Length < 2) return 0;
        double fast = 0;
        for (int index = 1; index < times.Length; index++)
        {
            if (times[index] - times[index - 1] > cutoffMs) continue;
            var previous = rows[times[index - 1]];
            bool shared = false;
            foreach (var column in rows[times[index]]) if (previous.Contains(column)) { shared = true; break; }
            if (!shared) fast++;
        }
        return fast / (times.Length - 1);
    }

    // Same per-column scan, but measuring how much of the chart sits inside fast
    // runs of at least minRun hits (the tier-3 burst-soak signal).
    private static BurstRunsResult ColumnBurstRuns(ManiaBeatmap map, double cutoffMs, double minRun)
    {
        var byColumn = new Dictionary<int, List<double>>();
        foreach (var note in map.Notes)
        {
            if (!byColumn.TryGetValue(note.Column, out var list)) { list = new(); byColumn[note.Column] = list; }
            list.Add(note.Time);
        }
        double runs = 0;
        double inRuns = 0;
        double total = 0;
        foreach (var times in byColumn.Values)
        {
            times.Sort();
            double run = 0;
            void Flush()
            {
                if (run >= minRun)
                {
                    runs++;
                    inRuns += run;
                }
                run = 0;
            }
            for (int index = 1; index < times.Count; index++)
            {
                double gap = times[index] - times[index - 1];
                if (gap <= 0) continue;
                total++;
                if (gap <= cutoffMs) run++;
                else Flush();
            }
            Flush();
        }
        return new BurstRunsResult(runs, total > 0 ? inRuns / total : 0);
    }

    private static WallRowsResult QuadWallRows(ManiaBeatmap map, double cutoffMs)
    {
        var rowSizes = new Dictionary<double, double>();
        foreach (var note in map.Notes) rowSizes[note.Time] = rowSizes.GetValueOrDefault(note.Time) + 1;
        var times = rowSizes.Keys.OrderBy(x => x).ToArray();
        double rows = 0;
        for (int index = 1; index < times.Length; index++)
        {
            double gap = times[index] - times[index - 1];
            if (gap <= 0 || gap > cutoffMs) continue;
            if (rowSizes.GetValueOrDefault(times[index]) >= 4 && rowSizes.GetValueOrDefault(times[index - 1]) >= 4) rows++;
        }
        return new WallRowsResult(rows, times.Length > 1 ? rows / (times.Length - 1) : 0);
    }

    // Share of row transitions where both rows carry a near-full chord and sit
    // inside cutoffMs (the tier-5 chord-jack signal).
    private static double ChordWallRatio(ManiaBeatmap map, double cutoffMs)
    {
        var rowSizes = new Dictionary<double, double>();
        foreach (var note in map.Notes) rowSizes[note.Time] = rowSizes.GetValueOrDefault(note.Time) + 1;
        var times = rowSizes.Keys.OrderBy(x => x).ToArray();
        if (times.Length < 2) return 0;
        double minChord = Math.Max(2, map.KeyCount - 1);
        double walls = 0;
        for (int index = 1; index < times.Length; index++)
        {
            double gap = times[index] - times[index - 1];
            if (gap <= 0 || gap > cutoffMs) continue;
            if (rowSizes.GetValueOrDefault(times[index]) >= minChord && rowSizes.GetValueOrDefault(times[index - 1]) >= minChord) walls++;
        }
        return walls / (times.Length - 1);
    }

    // Peak count of distinct hit instants inside any one real-time second. The
    // window is chart time, so a rate widens it: 1500ms of a 1.5x chart is a
    // second of play.
    private static double PeakRowsPerSecond(ManiaBeatmap map, double windowMs)
    {
        var times = map.Notes.Select(note => note.Time).Distinct().OrderBy(x => x).ToArray();
        double peak = 0;
        int start = 0;
        for (int index = 0; index < times.Length; index++)
        {
            while (times[index] - times[start] > windowMs) start++;
            double rows = index - start + 1;
            if (rows > peak) peak = rows;
        }
        return peak;
    }

    /// <summary>Tier 6 on its own; DetectRateVibro combines the play-side-safe tiers.</summary>
    public static bool DetectRollVibro(ManiaBeatmap map, double rate = 1)
    {
        if (map.KeyCount != 4 || map.Notes.Count < RICE_VIBRO_MIN_NOTES) return false;
        return ColumnFastGaps(map, RICE_VIBRO_ROLL_GAP_MS * rate).Ratio >= RICE_VIBRO_ROLL_MIN_RATIO
            && FastRollRowShare(map, RICE_VIBRO_ROLL_ROW_GAP_MS * rate) >= RICE_VIBRO_ROLL_MIN_ROW_RATIO;
    }

    // A rate can also turn ordinary chordjack into vibro without ever looking like
    // a roll: 128BPM chord walls become 218BPM at 1.7x, and in the reported shape
    // 68% of all row transitions are then near-full chords inside tier 5's 70ms
    // window. The ordinary tier-5 floor (2%) is deliberately too broad for
    // play-side rate checks: scaling it catches a small fast chordjack burst in
    // otherwise legit DT files. Requiring half of the whole chart says the
    // superhuman chord wall IS the chart. Measured over 24,407 stored uprate pairs,
    // this reaches five pairs (three from one short vibro-pack chart) rather than
    // the 165 the 2% floor reaches, while retaining 0.18 margin under the reported
    // chart's 0.68.
    private const double RATE_VIBRO_CHORD_WALL_MIN_RATIO = 0.5;

    // Repeated chords can reload two fingers on every row while rotating the
    // third finger out. Individual jack runs stay short and near-full chord pairs
    // need not occupy half the chart. Require speed, prevalence and sustained work
    // together so isolated DT bursts and ordinary fast chordjack stay eligible.
    // Calibrated on 4K rice (<=10% holds): no matches among 8,090 unique ranked/
    // loved charts at 1.0x or 5,599 with a recorded 96%+ DT clear. The same shape
    // occurs in fast chord-vibro packs; titles never participate in the detector.
    // Faster repeats need less chart-wide coverage, but must satisfy all three
    // requirements at that faster cutoff. A 36% share of 55ms chord repeats must
    // not be treated like 36% of 67ms repeats in a legitimate fast jack chart.
    private readonly record struct SustainedChordBand(double GapMs, double ColumnShare);

    private static readonly SustainedChordBand[] SUSTAINED_CHORD_VIBRO_BANDS =
    {
        new SustainedChordBand(70, 0.4),
        new SustainedChordBand(60, 0.35),
    };
    private const double SUSTAINED_CHORD_VIBRO_MIN_ROW_SHARE = 0.2;
    // 32 consecutive chord rows are about two seconds near the 70ms boundary.
    // Count repetitions so a faster rate cannot make an already-vibro section
    // pass merely by shortening its real-time duration below two seconds.
    private const double SUSTAINED_CHORD_VIBRO_MIN_ROWS = 32;
    private const double SUSTAINED_CHORD_VIBRO_MAX_HOLD_RATIO = 0.1;

    /// <summary>A row qualifies when at least two distinct fingers each re-hit within
    /// the band's time window. A continuous section contains only such rows.</summary>
    public static bool DetectSustainedChordVibro(ManiaBeatmap map, double rate = 1)
    {
        if (map.KeyCount != 4 || map.Notes.Count < RICE_VIBRO_MIN_NOTES || !double.IsFinite(rate) || rate <= 0) return false;
        var rows = new Dictionary<double, int>();
        double holds = 0;
        foreach (var note in map.Notes)
        {
            rows[note.Time] = rows.GetValueOrDefault(note.Time) | (1 << note.Column);
            if (note.IsHold) holds++;
        }
        if (holds / map.Notes.Count > SUSTAINED_CHORD_VIBRO_MAX_HOLD_RATIO) return false;

        var times = rows.Keys.OrderBy(x => x).ToArray();
        foreach (var band in SUSTAINED_CHORD_VIBRO_BANDS)
        {
            var lastColumnTimes = new double[4];
            Array.Fill(lastColumnTimes, double.NegativeInfinity);
            double cutoff = band.GapMs * rate;
            double columnGaps = 0;
            double fastColumnGaps = 0;
            double chordRows = 0;
            double consecutiveRows = 0;
            double longestRun = 0;
            foreach (var time in times)
            {
                int mask = rows[time];
                int fastFingers = 0;
                for (int column = 0; column < 4; column++)
                {
                    if ((mask & (1 << column)) == 0) continue;
                    double gap = time - lastColumnTimes[column];
                    if (double.IsFinite(gap) && gap > 0)
                    {
                        columnGaps++;
                        if (gap <= cutoff)
                        {
                            fastColumnGaps++;
                            fastFingers++;
                        }
                    }
                    lastColumnTimes[column] = time;
                }
                if (fastFingers >= 2)
                {
                    chordRows++;
                    consecutiveRows++;
                    longestRun = Math.Max(longestRun, consecutiveRows);
                }
                else
                {
                    consecutiveRows = 0;
                }
            }
            if (columnGaps > 0
                && fastColumnGaps / columnGaps >= band.ColumnShare
                && chordRows / rows.Count >= SUSTAINED_CHORD_VIBRO_MIN_ROW_SHARE
                && longestRun >= SUSTAINED_CHORD_VIBRO_MIN_ROWS) return true;
        }
        return false;
    }

    /// <summary>
    /// Full-map exclusion at the played rate. 4K rice shares the section policy
    /// with normal-speed chart analysis; adjusted charts return false here.
    ///
    /// The legacy path for other charts must not use all of DetectRiceVibro's
    /// tiers at rate. Tiers 1-4 were
    /// calibrated at 1.0x and the widened cutoffs call real 210-256BPM DT jack
    /// clears vibro. The roll and sustained-chord tiers are rate-calibrated
    /// directly; the chord-wall arm adds chart-soaked near-full walls.
    /// </summary>
    public static bool DetectRateVibro(ManiaBeatmap map, double rate = 1)
    {
        if (VibroSections.UsesSectionVibro(map)) return VibroSections.AnalyzeVibroSections(map, rate).Status == VibroStatus.Excluded;
        if (DetectRollVibro(map, rate)) return true;
        if (DetectSustainedChordVibro(map, rate)) return true;
        if (map.Notes.Count < RICE_VIBRO_BURST_MIN_NOTES) return false;
        return ChordWallRatio(map, RICE_VIBRO_CHORD_WALL_GAP_MS * rate) >= RATE_VIBRO_CHORD_WALL_MIN_RATIO;
    }

    public static bool DetectRiceVibro(ManiaBeatmap map, double rate = 1)
    {
        if (VibroSections.UsesSectionVibro(map)) return VibroSections.AnalyzeVibroSections(map, rate).Status == VibroStatus.Excluded;
        if (map.Notes.Count >= RICE_VIBRO_MIN_NOTES)
        {
            var sustained = ColumnFastGaps(map, RICE_VIBRO_COLUMN_GAP_MS * rate);
            if (sustained.MaxRun >= RICE_VIBRO_COLUMN_MIN_RUN && sustained.Ratio >= RICE_VIBRO_COLUMN_MIN_RATIO) return true;
            if (DetectSustainedChordVibro(map, rate)) return true;

            // The wall tier's chord-size floor assumes 4 columns; wider keymodes carry
            // legit 4-note chords constantly, so it stays 4K-scoped.
            if (map.KeyCount == 4)
            {
                var walls = QuadWallRows(map, RICE_VIBRO_WALL_GAP_MS * rate);
                if (walls.Rows >= RICE_VIBRO_WALL_MIN_ROWS && walls.Ratio >= RICE_VIBRO_WALL_MIN_ROW_RATIO)
                {
                    var fast = ColumnFastGaps(map, RICE_VIBRO_WALL_COLUMN_GAP_MS * rate);
                    if (fast.Ratio >= RICE_VIBRO_WALL_COLUMN_MIN_RATIO) return true;
                }
                if (DetectRollVibro(map, rate)) return true;
            }
        }

        // Tiers 3 and 4 share a lower size floor: TV-size burst packs sit under the
        // tier-1 floor but their soak fraction is unambiguous.
        if (map.Notes.Count >= RICE_VIBRO_BURST_MIN_NOTES)
        {
            var bursts = ColumnBurstRuns(map, RICE_VIBRO_BURST_GAP_MS * rate, RICE_VIBRO_BURST_MIN_RUN);
            if (bursts.Runs >= RICE_VIBRO_BURST_MIN_RUNS && bursts.Fraction >= RICE_VIBRO_BURST_MIN_FRACTION) return true;

            // Tier 4 shares that floor: the shape is a burst, so chart length says
            // nothing about it, and the smallest chart the sweep flags carries 217
            // notes.
            if (PeakRowsPerSecond(map, RICE_VIBRO_ROW_RATE_WINDOW_MS * rate) >= RICE_VIBRO_MAX_ROWS_PER_SECOND) return true;

            if (ChordWallRatio(map, RICE_VIBRO_CHORD_WALL_GAP_MS * rate) >= RICE_VIBRO_CHORD_WALL_MIN_RATIO) return true;
        }
        return false;
    }

    public static bool DetectLnVibro(ManiaBeatmap map, double rate = 1)
    {
        double holds = 0;
        var rowTimes = new HashSet<double>();
        foreach (var note in map.Notes)
        {
            if (note.IsHold && note.EndTime > note.Time) holds++;
            rowTimes.Add(note.Time);
        }
        if (map.Notes.Count == 0 || rowTimes.Count < LN_VIBRO_MIN_ROWS) return false;
        if (holds / map.Notes.Count < LN_VIBRO_MIN_HOLD_RATIO) return false;
        var times = rowTimes.OrderBy(x => x).ToArray();
        var gaps = new List<double>();
        for (int index = 1; index < times.Length; index++) gaps.Add(times[index] - times[index - 1]);
        gaps.Sort();
        double p75 = gaps[(int)Math.Min(gaps.Count - 1, Math.Floor(gaps.Count * 0.75))];
        // Gaps are chart-time; a rate rescales what the player experiences.
        return p75 <= LN_VIBRO_MAX_P75_ROW_GAP_MS * rate;
    }
}
