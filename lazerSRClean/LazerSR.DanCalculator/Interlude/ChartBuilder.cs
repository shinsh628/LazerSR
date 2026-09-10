// Port of vendor/leoblack/interlude/chartBuilder.js
//
// PORT NOTE: the JS `resolveOsuText` fetch path and the async signatures are dropped
// (no IO in this project). Only the two real inputs remain: raw .osu text, or an
// already-processed OsuFileParser (shared-parse path). Everything else is 1:1.
//
// Consumes LazerSR.DanCalculator.Parser.OsuFileParser / OsuParsedData (agent P1).

using LazerSR.DanCalculator.Parser;
using LazerSR.DanCalculator.Patterns;
using static LazerSR.DanCalculator.Interlude.NumberUtils;

namespace LazerSR.DanCalculator.Interlude;

public sealed class InterludeRowsResult
{
    public double KeyCount;
    public List<InterludeRow> Rows = new();
}

public static class ChartBuilder
{
    private static string? NormalizeCvtFlag(string? cvtFlag)
    {
        string normalized = (cvtFlag ?? "").Trim().ToUpperInvariant();
        if (normalized == "IN" || normalized == "HO")
        {
            return normalized;
        }
        return null;
    }

    private static void SetNoteType(NoteType[] row, int key, NoteType noteType)
    {
        if (row == null || key < 0 || key >= row.Length)
        {
            return;
        }

        if (row[key] == NoteType.NOTHING)
        {
            row[key] = noteType;
        }
    }

    // First index in sortedAsc where value > target; sortedAsc.Count if none.
    private static int LowerBoundGreater(List<double> sortedAsc, double target)
    {
        int lo = 0;
        int hi = sortedAsc.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (sortedAsc[mid] <= target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    private static NoteType[] GetOrCreateRow(Dictionary<double, NoteType[]> rowMap, int keyCount, double time)
    {
        if (!rowMap.TryGetValue(time, out var row))
        {
            row = InterludeTypes.CreateEmptyRow(keyCount);
            rowMap[time] = row;
        }
        return row;
    }

    private static void ApplyConversionFlag(OsuFileParser parser, string? cvtFlag)
    {
        string? normalized = NormalizeCvtFlag(cvtFlag);
        if (normalized == "IN")
        {
            parser.ModIN();
        }
        else if (normalized == "HO")
        {
            parser.ModHO();
        }
    }

    // Field-copy clone of a processed OsuFileParser. modIN/modHO mutate the parser
    // in place, so a shared instance must be isolated on a clone before converting.
    private static OsuFileParser CloneOsuParser(OsuFileParser src)
    {
        var p = new OsuFileParser("");
        p.Od = src.Od;
        p.ColumnCount = src.ColumnCount;
        p.Columns = new List<int>(src.Columns);
        p.NoteStarts = new List<double>(src.NoteStarts);
        p.NoteEnds = new List<double>(src.NoteEnds);
        p.NoteTypes = new List<double>(src.NoteTypes);
        p.GameMode = src.GameMode;
        p.Status = src.Status;
        p.LnRatio = src.LnRatio;
        p.MetaData = new Dictionary<string, string>(src.MetaData);
        p.Breaks = src.Breaks.Select(b => (double[])b.Clone()).ToList();
        p.ObjectIntervals = src.ObjectIntervals.Select(o => (double[])o.Clone()).ToList();
        p.TimingPoints = src.TimingPoints.Select(tp => (double[])tp.Clone()).ToList();
        return p;
    }

    private static InterludeRowsResult BuildRowsFromSharedParser(OsuFileParser parser, string? cvtFlag)
    {
        var parsed = parser.GetParsedData();
        if (parsed.Status == "NotMania")
        {
            throw new Exception("Beatmap mode is not mania");
        }
        if (parsed.Status == "Fail")
        {
            throw new Exception("Beatmap parse failed");
        }
        string? normalized = NormalizeCvtFlag(cvtFlag);
        OsuParsedData rowsSource;
        if (normalized == "IN" || normalized == "HO")
        {
            var clone = CloneOsuParser(parser);
            ApplyConversionFlag(clone, cvtFlag);
            rowsSource = clone.GetParsedData();
        }
        else
        {
            rowsSource = parsed;
        }

        return new InterludeRowsResult
        {
            KeyCount = Nz(rowsSource.ColumnCount),
            Rows = BuildRowsFromParsed(rowsSource),
        };
    }

    // JS bitwise coerces its operand with ToInt32; NaN/Infinity -> 0.
    private static long ToInt32(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : unchecked((int)(long)v);

    private static List<InterludeRow> BuildRowsFromParsed(OsuParsedData parsed)
    {
        int keyCount = (int)Nz(parsed.ColumnCount);
        if (keyCount < 3 || keyCount > 10)
        {
            return new List<InterludeRow>();
        }

        var columns = parsed.Columns;
        var noteStarts = parsed.NoteStarts;
        var noteEnds = parsed.NoteEnds;
        var noteTypes = parsed.NoteTypes;

        var rowMap = new Dictionary<double, NoteType[]>();
        var holdSpans = new List<(int Key, double StartTime, double EndTime)>();

        for (int i = 0; i < columns.Count; i += 1)
        {
            double key = columns[i];
            double startTime = i < noteStarts.Count ? noteStarts[i] : double.NaN;
            double endTime = i < noteEnds.Count ? noteEnds[i] : double.NaN;
            double rawType = i < noteTypes.Count ? Nz(noteTypes[i]) : 0;
            bool isLongNote = (ToInt32(rawType) & 128) != 0;

            if (!double.IsFinite(key) || key < 0 || key >= keyCount || !double.IsFinite(startTime))
            {
                continue;
            }

            int kk = (int)key;
            var startRow = GetOrCreateRow(rowMap, keyCount, startTime);
            if (isLongNote)
            {
                SetNoteType(startRow, kk, NoteType.HOLDHEAD);

                if (double.IsFinite(endTime) && endTime > startTime)
                {
                    var endRow = GetOrCreateRow(rowMap, keyCount, endTime);
                    SetNoteType(endRow, kk, NoteType.HOLDTAIL);
                    holdSpans.Add((kk, startTime, endTime));
                }
            }
            else
            {
                SetNoteType(startRow, kk, NoteType.NORMAL);
            }
        }

        var sortedTimes = rowMap.Keys.OrderBy(a => a).ToList();

        for (int i = 0; i < holdSpans.Count; i += 1)
        {
            var (key, startTime, endTime) = holdSpans[i];
            int t = LowerBoundGreater(sortedTimes, startTime);
            for (; t < sortedTimes.Count && sortedTimes[t] < endTime; t += 1)
            {
                var row = rowMap[sortedTimes[t]];
                if (row[key] == NoteType.NOTHING)
                {
                    row[key] = NoteType.HOLDBODY;
                }
            }
        }

        return sortedTimes
            .Select(time => new InterludeRow { Time = time, Data = rowMap[time] })
            .Where(row => !InterludeTypes.IsRowEmpty(row.Data))
            .ToList();
    }

    public static InterludeRowsResult BuildInterludeRows(OsuFileParser source, string? cvtFlag = null)
    {
        return BuildRowsFromSharedParser(source, cvtFlag);
    }

    public static InterludeRowsResult BuildInterludeRows(string osuText, string? cvtFlag = null)
    {
        var parser = new OsuFileParser(osuText);
        parser.Process();

        ApplyConversionFlag(parser, cvtFlag);

        var parsed = parser.GetParsedData();
        if (parsed.Status == "NotMania")
        {
            throw new Exception("Beatmap mode is not mania");
        }
        if (parsed.Status == "Fail")
        {
            throw new Exception("Beatmap parse failed");
        }

        return new InterludeRowsResult
        {
            KeyCount = Nz(parsed.ColumnCount),
            Rows = BuildRowsFromParsed(parsed),
        };
    }
}
