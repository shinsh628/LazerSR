// Port of vendor/leoblack/patterns/patternsDef.js
// Faithful 1:1. All pattern matcher functions keep their SCREAMING_SNAKE names.

namespace LazerSR.DanCalculator.Patterns;

/// <summary>A pattern-window matcher: returns a nonzero window length on match, 0 otherwise.</summary>
public delegate double PatternMatcher(List<PrimitiveRow> xs);

public static class CorePattern
{
    public const string Stream = "Stream";
    public const string Chordstream = "Chordstream";
    public const string Jacks = "Jacks";
    public const string Coordination = "Coordination";
    public const string Density = "Density";
    public const string Wildcard = "Wildcard";
}

/// <summary>makeSpecificPatterns(...) return shape.</summary>
public sealed class SpecificPatterns
{
    public List<(string Name, PatternMatcher Fn)> Stream = new();
    public List<(string Name, PatternMatcher Fn)> Chordstream = new();
    public List<(string Name, PatternMatcher Fn)> Jack = new();
    public List<(string Name, PatternMatcher Fn)> Coordination = new();
    public List<(string Name, PatternMatcher Fn)> Density = new();
    public List<(string Name, PatternMatcher Fn)> Wildcard = new();
}

public static class PatternsDef
{
    public static readonly string[] CORE_PATTERN_LIST =
        { CorePattern.Stream, CorePattern.Chordstream, CorePattern.Jacks, CorePattern.Coordination, CorePattern.Density, CorePattern.Wildcard };

    // String(arr) semantics: "1,2,3" for [1,2,3], "" for [].
    private static string RawKey(List<int> a) => string.Join(",", a);

    private static double RatingMultiplier(string pattern)
        => CORE_RATING_MULTIPLIER_TryGet(pattern);

    private static double CORE_RATING_MULTIPLIER_TryGet(string pattern)
        => PatternsConfig.CORE_RATING_MULTIPLIER.TryGetValue(pattern, out var v) ? v : 1.0;

    public static double ResolveRatingMultiplier(string pattern, string? specificType, string modeTag = "Mix")
    {
        var lnCorePatterns = new HashSet<string> { CorePattern.Coordination, CorePattern.Density, CorePattern.Wildcard };
        var rcCorePatterns = new HashSet<string> { CorePattern.Stream, CorePattern.Chordstream, CorePattern.Jacks };

        double defaultMultiplier = RatingMultiplier(pattern);

        var subtypeMap = PatternsConfig.SUBTYPE_RATING_MULTIPLIER_BY_MODE.TryGetValue(modeTag, out var m)
            ? m
            : PatternsConfig.SUBTYPE_RATING_MULTIPLIER_BY_MODE["Mix"];

        double value = specificType == null
            ? defaultMultiplier
            : (subtypeMap.TryGetValue(specificType, out var sv) ? sv : defaultMultiplier);

        if (modeTag == "RC" && lnCorePatterns.Contains(pattern))
        {
            var mixMap = PatternsConfig.SUBTYPE_RATING_MULTIPLIER_BY_MODE["Mix"];
            double baseVal = specificType == null
                ? defaultMultiplier
                : (mixMap.TryGetValue(specificType, out var mv) ? mv : defaultMultiplier);
            value = baseVal * PatternsConfig.RC_LN_CORE_SCALE;
        }

        if (modeTag == "LN" && rcCorePatterns.Contains(pattern))
        {
            value *= PatternsConfig.RC_CORE_LN_SCALE;
        }

        return value;
    }

    private static List<(string Name, PatternMatcher Fn)> ReorderSpecific(
        List<(string Name, PatternMatcher Fn)> items, string[] preferredOrder)
    {
        if (items.Count <= 1 || preferredOrder.Length == 0) return items;
        var orderRank = new Dictionary<string, int>();
        for (int idx = 0; idx < preferredOrder.Length; idx += 1) orderRank[preferredOrder[idx]] = idx;
        int size = orderRank.Count;
        return items
            .Select((item, index) => (item, index))
            .OrderBy(p => orderRank.TryGetValue(p.item.Name, out var r) ? r : size)
            .ThenBy(p => p.index)
            .Select(x => x.item)
            .ToList();
    }

    private static PrimitiveRow AsHeadPointRow(PrimitiveRow row, List<int> previousHeadCols)
    {
        var headCols = row.LNHeads;
        int jacks = headCols.Count != 0 ? headCols.Count(c => previousHeadCols.Contains(c)) : 0;

        string direction = Direction.NONE;
        bool roll = false;
        if (previousHeadCols.Count != 0 && headCols.Count != 0)
        {
            (direction, roll) = Primitives.DetectDirection(previousHeadCols, headCols);
        }

        return new PrimitiveRow
        {
            Index = row.Index,
            Time = row.Time,
            MsPerBeat = row.MsPerBeat,
            BeatLength = row.BeatLength,
            Notes = headCols.Count,
            Jacks = jacks,
            Direction = direction,
            Roll = roll,
            Keys = row.Keys,
            LeftHandKeys = row.LeftHandKeys,
            LNHeads = row.LNHeads,
            LNBodies = row.LNBodies,
            LNTails = row.LNTails,
            NormalNotes = new List<int>(),
            RawNotes = headCols,
        };
    }

    private static List<PrimitiveRow> HeadRows(List<PrimitiveRow> xs, int n)
    {
        var rows = new List<PrimitiveRow>();
        var prev = new List<int>();
        foreach (var row in xs.Take(n))
        {
            var hr = AsHeadPointRow(row, prev);
            rows.Add(hr);
            if (hr.RawNotes.Count != 0) prev = hr.RawNotes;
        }
        return rows;
    }

    private static bool IsSameHandAdjacent(int colA, int colB, int split)
    {
        if (Math.Abs(colA - colB) != 1) return false;
        return (colA < split) == (colB < split);
    }

    // Raw jack BPM formula (no f32 wrapper). Shared with interlude/noteDifficulty,
    // which applies its own f32 rounding at the call site.
    public static double JackBpm(double deltaMs)
    {
        if (deltaMs <= 0) return 230;
        return Math.Min(15000 / deltaMs, 230);
    }

    private static bool IsLnHeadContext(List<PrimitiveRow> xs)
    {
        return xs.Count > 0 && xs[0].LNHeads.Count > 0;
    }

    private static bool HasLnContext(List<PrimitiveRow> xs, int window)
    {
        foreach (var row in xs.Take(window))
        {
            if (row.LNHeads.Count != 0 || row.LNBodies.Count != 0 || row.LNTails.Count != 0) return true;
        }
        return false;
    }

    private static bool InverseReady(List<PrimitiveRow> xs)
    {
        if (xs.Count < 5) return false;
        var win = xs.Take(5).ToList();
        if (win.Any(r => r.NormalNotes.Count > 0)) return false;
        int maxBodies = win.Max(r => r.LNBodies.Count);
        if (maxBodies < PatternsConfig.INVERSE_MIN_FILLED_LANES) return false;

        var gaps = new List<double>();
        for (int i = 0; i < win.Count - 1; i += 1)
        {
            if (win[i].LNTails.Count > 0 && win[i + 1].LNHeads.Count > 0)
            {
                gaps.Add(win[i + 1].Time - win[i].Time);
            }
        }
        if (gaps.Count < 2) return false;
        return (gaps.Max() - gaps.Min()) <= PatternsConfig.INVERSE_GAP_TOLERANCE_MS;
    }

    public static double CORE_STREAM(List<PrimitiveRow> xs)
    {
        if (xs.Count < 5) return 0;
        PrimitiveRow a = xs[0], b = xs[1], c = xs[2], d = xs[3], e = xs[4];
        if (
            a.Notes == 1 && a.Jacks == 0 &&
            b.Notes == 1 && b.Jacks == 0 &&
            c.Notes == 1 && c.Jacks == 0 &&
            d.Notes == 1 && d.Jacks == 0 &&
            e.Notes == 1 && e.Jacks == 0
        )
        {
            if (a.RawNotes[0] != e.RawNotes[0]) return 5;
        }
        return 0;
    }

    public static double CORE_JACKS(List<PrimitiveRow> xs)
    {
        if (xs.Count == 0) return 0;
        var x0 = xs[0];
        return x0.Jacks > 1 && x0.MsPerBeat < 2000 ? 1 : 0;
    }

    public static double CORE_CHORDSTREAM(List<PrimitiveRow> xs)
    {
        if (xs.Count < 4) return 0;
        PrimitiveRow a = xs[0], b = xs[1], c = xs[2], d = xs[3];
        if (a.Notes > 1 && a.Jacks == 0 && b.Jacks == 0 && c.Jacks == 0 && d.Jacks == 0)
        {
            if (b.Notes > 1 || c.Notes > 1 || d.Notes > 1) return 4;
        }
        return 0;
    }

    public static double CORE_COORDINATION(List<PrimitiveRow> xs)
    {
        if (xs.Count == 0) return 0;
        var a = xs[0];
        return a.LNHeads.Count != 0 || a.LNBodies.Count != 0 || a.LNTails.Count != 0 ? 1 : 0;
    }

    public static double CORE_DENSITY(List<PrimitiveRow> xs)
    {
        if (xs.Count == 0) return 0;
        return IsLnHeadContext(xs) ? 1 : 0;
    }

    public static double CORE_WILDCARD(List<PrimitiveRow> xs)
    {
        if (xs.Count == 0) return 0;
        return IsLnHeadContext(xs) ? 1 : 0;
    }

    public static double JACKS_CHORDJACKS(List<PrimitiveRow> xs)
    {
        if (xs.Count < 2) return 0;
        PrimitiveRow a = xs[0], b = xs[1];
        if (a.Notes > 2 && b.Notes > 1 && b.Jacks >= 1 && ((b.Notes < a.Notes) || (b.Jacks < b.Notes)))
        {
            return 2;
        }
        return 0;
    }

    public static double JACKS_MINIJACKS(List<PrimitiveRow> xs)
    {
        if (xs.Count < 2) return 0;
        PrimitiveRow a = xs[0], b = xs[1];
        return a.Jacks > 0 && b.Jacks == 0 ? 2 : 0;
    }

    public static double JACKS_LONGJACKS(List<PrimitiveRow> xs)
    {
        if (xs.Count < 5) return 0;
        PrimitiveRow a = xs[0], b = xs[1], c = xs[2], d = xs[3], e = xs[4];
        if (a.Jacks > 0 && b.Jacks > 0 && c.Jacks > 0 && d.Jacks > 0 && e.Jacks > 0)
        {
            foreach (var x in a.RawNotes)
            {
                if (b.RawNotes.Contains(x) && c.RawNotes.Contains(x) && d.RawNotes.Contains(x) && e.RawNotes.Contains(x))
                {
                    return 5;
                }
            }
        }
        return 0;
    }

    public static double JACKS_4K_QUADSTREAM(List<PrimitiveRow> xs)
    {
        if (xs.Count < 4) return 0;
        PrimitiveRow a = xs[0], c = xs[2], d = xs[3];
        return a.Notes == 4 && c.Jacks == 0 && d.Jacks == 0 ? 4 : 0;
    }

    public static double JACKS_4K_GLUTS(List<PrimitiveRow> xs)
    {
        if (xs.Count < 3) return 0;
        PrimitiveRow a = xs[0], b = xs[1], c = xs[2];
        if (b.Jacks == 1 && c.Jacks == 1)
        {
            foreach (var x in a.RawNotes)
            {
                if (b.RawNotes.Contains(x) && c.RawNotes.Contains(x)) return 0;
            }
            return 3;
        }
        return 0;
    }

    public static double CHORDSTREAM_4K_HANDSTREAM(List<PrimitiveRow> xs)
    {
        if (xs.Count < 4) return 0;
        PrimitiveRow a = xs[0], b = xs[1], c = xs[2], d = xs[3];
        return a.Notes == 3 && a.Jacks == 0 && b.Jacks == 0 && c.Jacks == 0 && d.Jacks == 0 ? 4 : 0;
    }

    public static double CHORDSTREAM_4K_JUMPSTREAM(List<PrimitiveRow> xs)
    {
        if (xs.Count < 4) return 0;
        PrimitiveRow a = xs[0], b = xs[1], c = xs[2], d = xs[3];
        if (a.Notes == 2 && a.Jacks == 0 && b.Notes == 1 && b.Jacks == 0 && c.Jacks == 0 && d.Jacks == 0)
        {
            if (c.Notes < 3 && d.Notes < 3) return 4;
        }
        return 0;
    }

    public static double CHORDSTREAM_4K_DOUBLE_JUMPSTREAM(List<PrimitiveRow> xs)
    {
        if (xs.Count < 4) return 0;
        PrimitiveRow a = xs[0], b = xs[1], c = xs[2], d = xs[3];
        if (a.Notes == 1 && a.Jacks == 0 && b.Notes == 2 && b.Jacks == 0 && c.Notes == 2 && c.Jacks == 0 && d.Notes == 1 && d.Jacks == 0)
        {
            return 4;
        }
        return 0;
    }

    public static double CHORDSTREAM_4K_TRIPLE_JUMPSTREAM(List<PrimitiveRow> xs)
    {
        if (xs.Count < 5) return 0;
        PrimitiveRow a = xs[0], b = xs[1], c = xs[2], d = xs[3], e = xs[4];
        if (a.Notes == 1 && a.Jacks == 0 && b.Notes == 2 && b.Jacks == 0 && c.Notes == 2 && c.Jacks == 0 && d.Notes == 2 && d.Jacks == 0 && e.Notes == 1 && e.Jacks == 0)
        {
            return 4;
        }
        return 0;
    }

    public static double CHORDSTREAM_4K_JUMPTRILL(List<PrimitiveRow> xs)
    {
        if (xs.Count < 4) return 0;
        PrimitiveRow a = xs[0], b = xs[1], c = xs[2], d = xs[3];
        return a.Notes == 2 && b.Notes == 2 && c.Notes == 2 && d.Notes == 2 && b.Roll && c.Roll && d.Roll ? 4 : 0;
    }

    public static double CHORDSTREAM_4K_SPLITTRILL(List<PrimitiveRow> xs)
    {
        if (xs.Count < 3) return 0;
        PrimitiveRow a = xs[0], b = xs[1], c = xs[2];
        return a.Notes == 2 && b.Notes == 2 && c.Notes == 2 && b.Jacks == 0 && c.Jacks == 0 && !b.Roll && !c.Roll ? 3 : 0;
    }

    public static double STREAM_4K_ROLL(List<PrimitiveRow> xs)
    {
        if (xs.Count < 3) return 0;
        PrimitiveRow a = xs[0], b = xs[1], c = xs[2];
        if (a.Notes == 1 && b.Notes == 1 && c.Notes == 1)
        {
            bool left = a.Direction == Direction.LEFT && b.Direction == Direction.LEFT && c.Direction == Direction.LEFT;
            bool right = a.Direction == Direction.RIGHT && b.Direction == Direction.RIGHT && c.Direction == Direction.RIGHT;
            if (left || right) return 3;
        }
        return 0;
    }

    public static double STREAM_4K_TRILL(List<PrimitiveRow> xs)
    {
        if (xs.Count < 4) return 0;
        PrimitiveRow a = xs[0], b = xs[1], c = xs[2], d = xs[3];
        if (b.Jacks == 0 && c.Jacks == 0 && d.Jacks == 0)
        {
            if (RawKey(a.RawNotes) == RawKey(c.RawNotes) && RawKey(b.RawNotes) == RawKey(d.RawNotes)) return 4;
        }
        return 0;
    }

    public static double STREAM_4K_MINITRILL(List<PrimitiveRow> xs)
    {
        if (xs.Count < 4) return 0;
        PrimitiveRow a = xs[0], b = xs[1], c = xs[2], d = xs[3];
        if (b.Jacks == 0 && c.Jacks == 0)
        {
            if (RawKey(a.RawNotes) == RawKey(c.RawNotes) && RawKey(b.RawNotes) != RawKey(d.RawNotes)) return 4;
        }
        return 0;
    }

    public static double CHORDSTREAM_7K_DOUBLE_STREAMS(List<PrimitiveRow> xs)
    {
        if (xs.Count < 2) return 0;
        PrimitiveRow a = xs[0], b = xs[1];
        return a.Notes == 2 && b.Notes == 2 && b.Jacks == 0 && !b.Roll ? 2 : 0;
    }

    public static double CHORDSTREAM_7K_DENSE_CHORDSTREAM(List<PrimitiveRow> xs)
    {
        if (xs.Count < 2) return 0;
        PrimitiveRow a = xs[0], b = xs[1];
        return a.Notes > 1 && b.Notes > 1 && b.Jacks == 0 ? 2 : 0;
    }

    public static double CHORDSTREAM_7K_LIGHT_CHORDSTREAM(List<PrimitiveRow> xs)
    {
        if (xs.Count < 2) return 0;
        PrimitiveRow a = xs[0], b = xs[1];
        return a.Notes > 1 && b.Notes == 1 && b.Jacks == 0 ? 2 : 0;
    }

    public static double CHORDSTREAM_7K_CHORD_ROLL(List<PrimitiveRow> xs)
    {
        if (xs.Count < 3) return 0;
        PrimitiveRow a = xs[0], b = xs[1], c = xs[2];
        if (a.Notes > 1 && b.Notes > 1 && c.Notes > 1 && b.Roll && c.Roll)
        {
            if ((b.Direction == Direction.LEFT && c.Direction == Direction.LEFT) || (b.Direction == Direction.RIGHT && c.Direction == Direction.RIGHT))
            {
                return 3;
            }
        }
        return 0;
    }

    public static double CHORDSTREAM_7K_BRACKETS(List<PrimitiveRow> xs)
    {
        if (xs.Count < 3) return 0;
        PrimitiveRow a = xs[0], b = xs[1], c = xs[2];
        if (a.Notes > 2 && b.Notes > 2 && c.Notes > 2 && !b.Roll && !c.Roll && b.Jacks == 0 && c.Jacks == 0)
        {
            if ((a.Notes + b.Notes + c.Notes) > 9) return 3;
        }
        return 0;
    }

    public static double CHORDSTREAM_OTHER_DOUBLE_STREAMS(List<PrimitiveRow> xs) => CHORDSTREAM_7K_DOUBLE_STREAMS(xs);
    public static double CHORDSTREAM_OTHER_DENSE_CHORDSTREAM(List<PrimitiveRow> xs) => CHORDSTREAM_7K_DENSE_CHORDSTREAM(xs);
    public static double CHORDSTREAM_OTHER_LIGHT_CHORDSTREAM(List<PrimitiveRow> xs) => CHORDSTREAM_7K_LIGHT_CHORDSTREAM(xs);
    public static double CHORDSTREAM_OTHER_CHORD_ROLL(List<PrimitiveRow> xs) => CHORDSTREAM_7K_CHORD_ROLL(xs);

    public static double COORDINATION_COLUMN_LOCK(List<PrimitiveRow> xs)
    {
        if (xs.Count < 3) return 0;
        int split = xs[0].LeftHandKeys;
        int? lnCol = xs[0].LNHeads.Count != 0 ? xs[0].LNHeads[0] : (int?)null;
        if (lnCol == null) return 0;

        var adjCols = new[] { lnCol.Value - 1, lnCol.Value + 1 }
            .Where(c => c >= 0 && c < xs[0].Keys && IsSameHandAdjacent(lnCol.Value, c, split))
            .ToList();
        if (adjCols.Count == 0) return 0;

        foreach (var adj in adjCols)
        {
            var hits = new List<double>();
            foreach (var row in xs.Take(8))
            {
                if (row.LNBodies.Contains(lnCol.Value) && row.NormalNotes.Contains(adj))
                {
                    hits.Add(row.Time);
                }
            }
            if (hits.Count < 3) continue;

            var bpms = new List<double>();
            for (int i = 0; i < hits.Count - 1; i += 1)
            {
                bpms.Add(JackBpm(hits[i + 1] - hits[i]));
            }
            if (bpms.Count != 0 && bpms.Max() >= PatternsConfig.JACKY_MIN_BPM) return 3;
        }

        return 0;
    }

    public static double COORDINATION_SHIELD(List<PrimitiveRow> xs)
    {
        if (xs.Count < 2) return 0;
        PrimitiveRow a = xs[0], b = xs[1];
        double dt = b.Time - a.Time;
        double beatLimit = b.BeatLength * PatternsConfig.SHIELD_MAX_BEAT_RATIO;
        if (dt < 0 || dt > beatLimit) return 0;

        foreach (var col in a.NormalNotes)
        {
            if (b.LNHeads.Contains(col)) return 2;
        }
        foreach (var col in a.LNTails)
        {
            if (b.NormalNotes.Contains(col)) return 2;
        }
        return 0;
    }

    public static double COORDINATION_RELEASE(List<PrimitiveRow> xs)
    {
        if (xs.Count < PatternsConfig.RELEASE_MIN_TAIL_ROWS) return 0;
        if (COORDINATION_SHIELD(xs) != 0) return 0;
        if (InverseReady(xs)) return 0;
        if (WILDCARD_JACK(xs) != 0) return 0;

        var pickedRows = xs.Take(PatternsConfig.RELEASE_SCAN_ROWS).Where(r => r.LNTails.Count == 1).ToList();
        if (pickedRows.Count < PatternsConfig.RELEASE_MIN_TAIL_ROWS) return 0;

        int useRows = Math.Min(PatternsConfig.RELEASE_FULL_MATCH_ROWS, pickedRows.Count);
        var tails = pickedRows.Take(useRows).Select(r => r.LNTails[0]).ToList();

        var prev = new List<int> { tails[0] };
        var rows = new List<PrimitiveRow>();
        for (int i = 0; i < useRows; i += 1)
        {
            var row = pickedRows[i];
            var cur = new List<int> { tails[i] };
            var (direction, roll) = Primitives.DetectDirection(prev, cur);
            rows.Add(new PrimitiveRow
            {
                Index = row.Index,
                Time = row.Time,
                MsPerBeat = row.MsPerBeat,
                BeatLength = row.BeatLength,
                Notes = 1,
                Jacks = cur[0] == prev[0] ? 1 : 0,
                Direction = direction,
                Roll = roll,
                Keys = row.Keys,
                LeftHandKeys = row.LeftHandKeys,
                LNHeads = row.LNHeads,
                LNBodies = row.LNBodies,
                LNTails = row.LNTails,
                NormalNotes = new List<int>(),
                RawNotes = cur,
            });
            prev = cur;
        }

        var effectiveRows = rows.Count > 1 ? rows.Skip(1).ToList() : new List<PrimitiveRow>();
        if (effectiveRows.Count < PatternsConfig.RELEASE_ROLL_POINTS) return 0;

        bool matched;
        if (PatternsConfig.RELEASE_ROLL_POINTS >= 3)
        {
            matched = STREAM_4K_ROLL(effectiveRows.Take(PatternsConfig.RELEASE_ROLL_POINTS).ToList()) != 0;
        }
        else
        {
            int aa = effectiveRows[0].RawNotes[0];
            int bb = effectiveRows.Count > 1 ? effectiveRows[1].RawNotes[0] : aa;
            double dt = effectiveRows.Count > 1 ? (effectiveRows[1].Time - effectiveRows[0].Time) : 0;
            matched = aa != bb && dt > 0;
        }

        if (matched)
        {
            return useRows >= PatternsConfig.RELEASE_FULL_MATCH_ROWS ? 5 : 4;
        }
        return 0;
    }

    public static double DENSITY_4K_JUMPSTREAM(List<PrimitiveRow> xs)
    {
        if (xs.Count < 4 || !IsLnHeadContext(xs)) return 0;
        return CHORDSTREAM_4K_JUMPSTREAM(HeadRows(xs, 4)) != 0 ? 4 : 0;
    }

    public static double DENSITY_4K_HANDSTREAM(List<PrimitiveRow> xs)
    {
        if (xs.Count < 4 || !IsLnHeadContext(xs)) return 0;
        return CHORDSTREAM_4K_HANDSTREAM(HeadRows(xs, 4)) != 0 ? 4 : 0;
    }

    public static double DENSITY_4K_INVERSE(List<PrimitiveRow> xs)
    {
        return InverseReady(xs) ? 5 : 0;
    }

    public static double DENSITY_7K_DOUBLE_STREAMS(List<PrimitiveRow> xs)
    {
        if (xs.Count < 2 || !IsLnHeadContext(xs)) return 0;
        return CHORDSTREAM_7K_DOUBLE_STREAMS(HeadRows(xs, 2)) != 0 ? 2 : 0;
    }

    public static double DENSITY_7K_DENSE_CHORDSTREAM(List<PrimitiveRow> xs)
    {
        if (xs.Count < 2 || !IsLnHeadContext(xs)) return 0;
        return CHORDSTREAM_7K_DENSE_CHORDSTREAM(HeadRows(xs, 2)) != 0 ? 2 : 0;
    }

    public static double DENSITY_7K_LIGHT_CHORDSTREAM(List<PrimitiveRow> xs)
    {
        if (xs.Count < 2 || !IsLnHeadContext(xs)) return 0;
        return CHORDSTREAM_7K_LIGHT_CHORDSTREAM(HeadRows(xs, 2)) != 0 ? 2 : 0;
    }

    public static double DENSITY_7K_INVERSE(List<PrimitiveRow> xs) => DENSITY_4K_INVERSE(xs);
    public static double DENSITY_OTHER_DOUBLE_STREAMS(List<PrimitiveRow> xs) => DENSITY_7K_DOUBLE_STREAMS(xs);
    public static double DENSITY_OTHER_DENSE_CHORDSTREAM(List<PrimitiveRow> xs) => DENSITY_7K_DENSE_CHORDSTREAM(xs);
    public static double DENSITY_OTHER_LIGHT_CHORDSTREAM(List<PrimitiveRow> xs) => DENSITY_7K_LIGHT_CHORDSTREAM(xs);
    public static double DENSITY_OTHER_INVERSE(List<PrimitiveRow> xs) => DENSITY_7K_INVERSE(xs);

    public static double WILDCARD_JACK(List<PrimitiveRow> xs)
    {
        if (xs.Count < 2 || !HasLnContext(xs, PatternsConfig.JACKY_CONTEXT_WINDOW)) return 0;

        var rows = xs.Take(Math.Max(4, PatternsConfig.JACKY_CONTEXT_WINDOW)).Where(r => r.Notes > 0).ToList();
        if (rows.Count < 2) return 0;

        if (JACKS_CHORDJACKS(rows) != 0 || JACKS_MINIJACKS(rows) != 0) return 4;

        var checkRows = rows.Take(Math.Min(4, rows.Count)).ToList();
        int jackRows = checkRows.Count(r => r.Jacks > 0);
        if (jackRows >= 2 && checkRows.Any(r => r.Notes >= 2)) return 3;

        double fastestMspb = checkRows.Min(r => r.MsPerBeat);
        if (jackRows >= 2 && fastestMspb <= PatternsConfig.JACKY_FALLBACK_MAX_MSPB) return 3;
        return 0;
    }

    public static double WILDCARD_SPEED(List<PrimitiveRow> xs)
    {
        if (xs.Count < 2 || !HasLnContext(xs, 4)) return 0;

        var rows = HeadRows(xs, Math.Min(4, xs.Count));
        if (xs[0].Keys == 4)
        {
            if (rows.Count >= 3 && STREAM_4K_ROLL(rows.Take(3).ToList()) != 0) return 3;
            if (rows.Count >= 2)
            {
                bool sameDir = (rows[0].Direction == Direction.LEFT || rows[0].Direction == Direction.RIGHT)
                    && rows[0].Direction == rows[1].Direction;
                if (sameDir || rows[0].MsPerBeat <= 180) return 3;
            }
        }
        else
        {
            if (rows.Count >= 3 && CHORDSTREAM_7K_CHORD_ROLL(rows.Take(3).ToList()) != 0) return 3;
            if (rows.Count >= 2)
            {
                bool cond = rows[0].Notes >= 2 && rows[1].Notes >= 2
                    && rows[0].Direction == rows[1].Direction
                    && (rows[0].Direction == Direction.LEFT || rows[0].Direction == Direction.RIGHT);
                if (cond || rows[0].MsPerBeat <= 170) return 3;
            }
        }
        return 0;
    }

    private static SpecificPatterns MakeSpecificPatterns(
        List<(string, PatternMatcher)> stream,
        List<(string, PatternMatcher)> chordstream,
        List<(string, PatternMatcher)> jack,
        List<(string, PatternMatcher)> coordination,
        List<(string, PatternMatcher)> density,
        List<(string, PatternMatcher)> wildcard)
    {
        return new SpecificPatterns
        {
            Stream = stream.Select(t => (t.Item1, t.Item2)).ToList(),
            Chordstream = chordstream.Select(t => (t.Item1, t.Item2)).ToList(),
            Jack = jack.Select(t => (t.Item1, t.Item2)).ToList(),
            Coordination = coordination.Select(t => (t.Item1, t.Item2)).ToList(),
            Density = density.Select(t => (t.Item1, t.Item2)).ToList(),
            Wildcard = wildcard.Select(t => (t.Item1, t.Item2)).ToList(),
        };
    }

    public static SpecificPatterns SPECIFIC_4K()
    {
        var coordination = ReorderSpecific(new List<(string, PatternMatcher)>
        {
            ("Column Lock", COORDINATION_COLUMN_LOCK),
            ("Release", COORDINATION_RELEASE),
            ("Shield", COORDINATION_SHIELD),
        }, PatternsConfig.COORDINATION_SPECIFIC_ORDER);

        var density = ReorderSpecific(new List<(string, PatternMatcher)>
        {
            ("JS Density", DENSITY_4K_JUMPSTREAM),
            ("HS Density", DENSITY_4K_HANDSTREAM),
            ("Inverse", DENSITY_4K_INVERSE),
        }, PatternsConfig.DENSITY_SPECIFIC_ORDER);

        var wildcard = ReorderSpecific(new List<(string, PatternMatcher)>
        {
            ("Jacky WC", WILDCARD_JACK),
            ("Speedy WC", WILDCARD_SPEED),
        }, PatternsConfig.WILDCARD_SPECIFIC_ORDER);

        return MakeSpecificPatterns(
            new List<(string, PatternMatcher)> { ("Rolls", STREAM_4K_ROLL), ("Trills", STREAM_4K_TRILL), ("Minitrills", STREAM_4K_MINITRILL) },
            new List<(string, PatternMatcher)> { ("Handstream", CHORDSTREAM_4K_HANDSTREAM), ("Split Trill", CHORDSTREAM_4K_SPLITTRILL), ("Jumptrill", CHORDSTREAM_4K_JUMPTRILL), ("Jumpstream", CHORDSTREAM_4K_JUMPSTREAM) },
            new List<(string, PatternMatcher)> { ("Longjacks", JACKS_LONGJACKS), ("Quadstream", JACKS_4K_QUADSTREAM), ("Gluts", JACKS_4K_GLUTS), ("Chordjacks", JACKS_CHORDJACKS), ("Minijacks", JACKS_MINIJACKS) },
            coordination.Select(t => (t.Name, t.Fn)).ToList(),
            density.Select(t => (t.Name, t.Fn)).ToList(),
            wildcard.Select(t => (t.Name, t.Fn)).ToList());
    }

    public static SpecificPatterns SPECIFIC_7K()
    {
        var coordination = ReorderSpecific(new List<(string, PatternMatcher)>
        {
            ("Column Lock", COORDINATION_COLUMN_LOCK),
            ("Release", COORDINATION_RELEASE),
            ("Shield", COORDINATION_SHIELD),
        }, PatternsConfig.COORDINATION_SPECIFIC_ORDER);

        var density = ReorderSpecific(new List<(string, PatternMatcher)>
        {
            ("DS Density", DENSITY_7K_DOUBLE_STREAMS),
            ("DCS Density", DENSITY_7K_DENSE_CHORDSTREAM),
            ("LCS Density", DENSITY_7K_LIGHT_CHORDSTREAM),
            ("Inverse", DENSITY_7K_INVERSE),
        }, PatternsConfig.DENSITY_SPECIFIC_ORDER);

        var wildcard = ReorderSpecific(new List<(string, PatternMatcher)>
        {
            ("Jacky WC", WILDCARD_JACK),
            ("Speedy WC", WILDCARD_SPEED),
        }, PatternsConfig.WILDCARD_SPECIFIC_ORDER);

        return MakeSpecificPatterns(
            new List<(string, PatternMatcher)>(),
            new List<(string, PatternMatcher)> { ("Brackets", CHORDSTREAM_7K_BRACKETS), ("Double Stream", CHORDSTREAM_7K_DOUBLE_STREAMS), ("Dense Chordstream", CHORDSTREAM_7K_DENSE_CHORDSTREAM), ("Light Chordstream", CHORDSTREAM_7K_LIGHT_CHORDSTREAM) },
            new List<(string, PatternMatcher)> { ("Longjacks", JACKS_LONGJACKS), ("Chordjacks", JACKS_CHORDJACKS), ("Minijacks", JACKS_MINIJACKS) },
            coordination.Select(t => (t.Name, t.Fn)).ToList(),
            density.Select(t => (t.Name, t.Fn)).ToList(),
            wildcard.Select(t => (t.Name, t.Fn)).ToList());
    }

    public static SpecificPatterns SPECIFIC_OTHER()
    {
        var coordination = ReorderSpecific(new List<(string, PatternMatcher)>
        {
            ("Column Lock", COORDINATION_COLUMN_LOCK),
            ("Release", COORDINATION_RELEASE),
            ("Shield", COORDINATION_SHIELD),
        }, PatternsConfig.COORDINATION_SPECIFIC_ORDER);

        var density = ReorderSpecific(new List<(string, PatternMatcher)>
        {
            ("DS Density", DENSITY_OTHER_DOUBLE_STREAMS),
            ("DCS Density", DENSITY_OTHER_DENSE_CHORDSTREAM),
            ("LCS Density", DENSITY_OTHER_LIGHT_CHORDSTREAM),
            ("Inverse", DENSITY_OTHER_INVERSE),
        }, PatternsConfig.DENSITY_SPECIFIC_ORDER);

        var wildcard = ReorderSpecific(new List<(string, PatternMatcher)>
        {
            ("Jacky WC", WILDCARD_JACK),
            ("Speedy WC", WILDCARD_SPEED),
        }, PatternsConfig.WILDCARD_SPECIFIC_ORDER);

        return MakeSpecificPatterns(
            new List<(string, PatternMatcher)>(),
            new List<(string, PatternMatcher)> { ("Chord Rolls", CHORDSTREAM_OTHER_CHORD_ROLL), ("Double Stream", CHORDSTREAM_OTHER_DOUBLE_STREAMS), ("Dense Chordstream", CHORDSTREAM_OTHER_DENSE_CHORDSTREAM), ("Light Chordstream", CHORDSTREAM_OTHER_LIGHT_CHORDSTREAM) },
            new List<(string, PatternMatcher)> { ("Longjacks", JACKS_LONGJACKS), ("Chordjacks", JACKS_CHORDJACKS), ("Minijacks", JACKS_MINIJACKS) },
            coordination.Select(t => (t.Name, t.Fn)).ToList(),
            density.Select(t => (t.Name, t.Fn)).ToList(),
            wildcard.Select(t => (t.Name, t.Fn)).ToList());
    }
}
