// Port of mania-hub live-backend/src/dan/motion-features.ts
//
// Note-data motion features for the 4K tech-vs-speed axis.
//
// The distinction 4K players draw between the two is biomechanical rather than
// rhythmic: a tech chart asks the wrist to oscillate (two-column trills,
// minijacks, patterns that keep returning to a column), a speed chart asks the
// fingers to roll across the hands in one direction. MinaCalc's Technical and
// Stream ratings both rise with density and often land within a rating point
// of each other on the same chart, which is why the MSD lead alone could not
// separate the two: measured over 738 pack-labelled charts sitting in that
// near-tie, the lead scores AUC 0.73 while these features score 0.84 on their
// own and 0.86 alongside it (out-of-fold, packs held out whole).
//
// Every share is weighted by local speed (1/gap, saturated), so a dense burst
// counts for more than a sparse intro and no burst-gap threshold has to be
// invented. The shares are ratios of like-weighted windows, so speeding a
// chart up does not move them: these are a property of the chart, measured
// once at 1.0x, and a rate edit of the same chart reads the same. What the
// rate changes is the MSD vector, which the reader supplies separately.
//
// 4K only. The hand split (columns 0-1 against 2-3) is what makes "one hand
// oscillating" meaningful, and the pack corpus that validated the features is
// 4K; other keymodes bucket by analyzer tags and never ask for this.

using LazerSR.DanCalculator.Beatmap;

namespace LazerSR.DanCalculator.Features;

/// <summary>The stored block. Every field is a share in [0, 1] unless noted.</summary>
public sealed class MotionFeatures
{
    /// <summary>Adjacent single-note pairs landing on the same hand, different column.</summary>
    public double SameHand;
    /// <summary>Adjacent single-note pairs repeating one column (minijack).</summary>
    public double MiniJack;
    /// <summary>Three-note windows reading c,d,c with c and d on ONE hand.</summary>
    public double OneHandTrill;
    /// <summary>Three-note windows reading c,d,c with c and d on opposite hands. The
    ///  strongest single tech marker in the near-tie band: a stream varies its
    ///  columns, a trill keeps coming back to the same two.</summary>
    public double CrossHandTrill;
    /// <summary>Four-note windows stepping one column at a time across all four columns,
    ///  the full cross-hand roll.</summary>
    public double Roll4;
    /// <summary>Adjacent gap ratios that are not a musical 1:1, 2:1 or 1:2.</summary>
    public double RhythmBreak;
    /// <summary>Adjacent row pairs whose chord size changes.</summary>
    public double ChordSwing;
    /// <summary>Coefficient of variation of per-second note density. Not a share.</summary>
    public double DensitySwing;
}

public static class MotionFeaturesCalculator
{
    private const double ROW_EPSILON_MS = 10;
    // Gaps below this are vibro/burst noise; weighting by 1/gap alone would let a
    // few rows speak for the whole chart, so the weight saturates here.
    private const double MIN_GAP_MS = 25;
    // Past this the notes are not one motion any more, so the window is dropped
    // rather than merely down-weighted.
    private const double MAX_GAP_MS = 400;
    // Under this many rows the shares are noise, and no chart a player clears for
    // dan credit is this short.
    private const int MIN_ROWS = 24;

    private sealed class Row
    {
        public double Time;
        public List<int> Columns = new();
    }

    private static int Hand(int column) => column < 2 ? 0 : 1;

    private static List<Row> BuildRows(List<ManiaNote> notes)
    {
        var sorted = notes.OrderBy(a => a.Time).ToList();
        var rows = new List<Row>();
        foreach (var note in sorted)
        {
            var last = rows.Count > 0 ? rows[rows.Count - 1] : null;
            if (last != null && note.Time - last.Time <= ROW_EPSILON_MS) last.Columns.Add(note.Column);
            else rows.Add(new Row { Time = note.Time, Columns = new List<int> { note.Column } });
        }
        foreach (var row in rows) row.Columns.Sort();
        return rows;
    }

    private static double WeightFor(double gap) => 1 / Math.Max(gap, MIN_GAP_MS);

    private static bool RatioIsMusical(double ratio)
    {
        foreach (var target in new[] { 1, 2, 0.5, 1.5, 2.0 / 3, 3, 1.0 / 3, 4, 0.25 })
        {
            if (Math.Abs(ratio - target) <= target * 0.08) return true;
        }
        return false;
    }

    private static double Round4(double value) => Math.Floor(value * 10000 + 0.5) / 10000;

    /// <summary>Null when the chart is not 4K or is too short to measure. (source: motionFeatures)</summary>
    public static MotionFeatures? Compute(List<ManiaNote> notes, int keyCount)
    {
        if (keyCount != 4 || notes.Count < 32) return null;
        var rows = BuildRows(notes);
        if (rows.Count < MIN_ROWS) return null;

        double pairW = 0, sameHandW = 0, miniJackW = 0, chordSwingW = 0;
        double tripW = 0, trillW = 0, crossTrillW = 0;
        double quadW = 0, roll4W = 0;
        double rhythmW = 0, rhythmBreakW = 0;

        for (int i = 0; i + 1 < rows.Count; i++)
        {
            var a = rows[i];
            var b = rows[i + 1];
            double gap = b.Time - a.Time;
            if (gap <= 0 || gap > MAX_GAP_MS) continue;
            double w = WeightFor(gap);
            pairW += w;
            if (a.Columns.Count != b.Columns.Count) chordSwingW += w;
            if (a.Columns.Count == 1 && b.Columns.Count == 1)
            {
                int ca = a.Columns[0], cb = b.Columns[0];
                if (ca == cb) miniJackW += w;
                else if (Hand(ca) == Hand(cb)) sameHandW += w;
            }
        }

        for (int i = 0; i + 2 < rows.Count; i++)
        {
            var a = rows[i];
            var b = rows[i + 1];
            var c = rows[i + 2];
            double g0 = b.Time - a.Time, g1 = c.Time - b.Time;
            if (g0 <= 0 || g1 <= 0 || g0 > MAX_GAP_MS || g1 > MAX_GAP_MS) continue;
            double w = WeightFor(Math.Max(g0, g1));
            rhythmW += w;
            if (!RatioIsMusical(g1 / g0)) rhythmBreakW += w;
            if (a.Columns.Count != 1 || b.Columns.Count != 1 || c.Columns.Count != 1) continue;
            tripW += w;
            int ca = a.Columns[0], cb = b.Columns[0], cc = c.Columns[0];
            if (ca == cc && ca != cb)
            {
                if (Hand(ca) == Hand(cb)) trillW += w;
                else crossTrillW += w;
            }
        }

        for (int i = 0; i + 3 < rows.Count; i++)
        {
            var window = new[] { rows[i], rows[i + 1], rows[i + 2], rows[i + 3] };
            var gaps = new[] { window[1].Time - window[0].Time, window[2].Time - window[1].Time, window[3].Time - window[2].Time };
            if (gaps.Any(gap => gap <= 0 || gap > MAX_GAP_MS)) continue;
            if (window.Any(row => row.Columns.Count != 1)) continue;
            double w = WeightFor(Math.Max(gaps[0], Math.Max(gaps[1], gaps[2])));
            quadW += w;
            var columns = window.Select(row => row.Columns[0]).ToArray();
            var steps = new[] { columns[1] - columns[0], columns[2] - columns[1], columns[3] - columns[2] };
            if (steps.All(step => step == 1) || steps.All(step => step == -1)) roll4W += w;
        }

        double first = rows[0].Time, last = rows[rows.Count - 1].Time;
        int seconds = (int)Math.Max(1, Math.Ceiling((last - first) / 1000));
        var perSecond = new double[seconds];
        foreach (var row in rows)
        {
            int index = (int)Math.Min(seconds - 1, Math.Floor((row.Time - first) / 1000));
            perSecond[index] += row.Columns.Count;
        }
        var active = perSecond.Where(count => count > 0).ToList();
        double mean = active.Count != 0 ? active.Sum() / active.Count : 0;
        double variance = active.Count != 0
            ? active.Sum(n => (n - mean) * (n - mean)) / active.Count
            : 0;

        double Share(double numerator, double denominator) => denominator > 0 ? numerator / denominator : 0;
        return new MotionFeatures
        {
            SameHand = Round4(Share(sameHandW, pairW)),
            MiniJack = Round4(Share(miniJackW, pairW)),
            OneHandTrill = Round4(Share(trillW, tripW)),
            CrossHandTrill = Round4(Share(crossTrillW, tripW)),
            Roll4 = Round4(Share(roll4W, quadW)),
            RhythmBreak = Round4(Share(rhythmBreakW, rhythmW)),
            ChordSwing = Round4(Share(chordSwingW, pairW)),
            DensitySwing = Round4(mean > 0 ? Math.Sqrt(variance) / mean : 0),
        };
    }
}
