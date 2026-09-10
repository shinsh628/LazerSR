// Port of mania-hub live-backend/vendor/leoblack/parser/osuFileParser.js
//
// This is LeoBlack's OWN .osu parser, separate from the already-ported
// mania-hub parser (Beatmap/ManiaBeatmapParser). It feeds the LeoBlack
// estimators + interlude and keeps its exact output shape / field names.

namespace LazerSR.DanCalculator.Parser;

/// <summary>Shape of <see cref="OsuFileParser.GetParsedData"/> (matches JS getParsedData()).</summary>
public sealed class OsuParsedData
{
    public double ColumnCount;
    public List<int> Columns = new();
    public List<double> NoteStarts = new();
    public List<double> NoteEnds = new();
    public List<double> NoteTypes = new();
    public double Od;
    public string? GameMode;
    public string Status = "";
    public double LnRatio;
    public Dictionary<string, string> MetaData = new();
    public List<double[]> Breaks = new();
    public List<double[]> ObjectIntervals = new();
}

public sealed class OsuFileParser
{
    public string OsuText;
    public double Od = -1;
    public double ColumnCount = -1;
    public List<int> Columns = new();
    public List<double> NoteStarts = new();
    public List<double> NoteEnds = new();
    public List<double> NoteTypes = new();
    public string? GameMode = null;
    public string Status = "init";
    public double LnRatio = 0;
    /// <summary>column -> sorted note start times (JS `noteTimes` object).</summary>
    public Dictionary<int, List<double>> NoteTimes = new();
    public Dictionary<string, string> MetaData = new();
    public List<double[]> Breaks = new();
    public List<double[]> ObjectIntervals = new();
    /// <summary>Array of [time, beatLength] pairs (JS `timingPoints`).</summary>
    public List<double[]> TimingPoints = new();

    public OsuFileParser(string osuText)
    {
        OsuText = osuText;
    }

    private static double StringToInt(string value) => Math.Truncate(JsNum.ParseFloat(value));

    private static int BisectRight(IReadOnlyList<double> arr, double target)
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

    public OsuParsedData GetParsedData() => new()
    {
        ColumnCount = ColumnCount,
        Columns = Columns,
        NoteStarts = NoteStarts,
        NoteEnds = NoteEnds,
        NoteTypes = NoteTypes,
        Od = Od,
        GameMode = GameMode,
        Status = Status,
        LnRatio = LnRatio,
        MetaData = MetaData,
        Breaks = Breaks,
        ObjectIntervals = ObjectIntervals,
    };

    public void Process()
    {
        var lines = System.Text.RegularExpressions.Regex.Split(OsuText, "\r?\n");

        bool inMetadataSection = false;
        bool inEventsSection = false;
        bool inTimingSection = false;

        for (int i = 0; i < lines.Length; i += 1)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;

            if (line == "[Metadata]")
            {
                inMetadataSection = true;
                inEventsSection = false;
                inTimingSection = false;
                continue;
            }
            if (line == "[Events]")
            {
                inMetadataSection = false;
                inEventsSection = true;
                inTimingSection = false;
                continue;
            }
            if (line == "[TimingPoints]")
            {
                inMetadataSection = false;
                inEventsSection = false;
                inTimingSection = true;
                continue;
            }
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                inMetadataSection = false;
                inEventsSection = false;
                inTimingSection = false;
            }

            if (inMetadataSection && line.Contains(":"))
            {
                int splitIdx = line.IndexOf(":");
                string key = line[..splitIdx].Trim();
                string value = line[(splitIdx + 1)..].Trim();
                MetaData[key] = value;
            }

            if (inEventsSection)
            {
                ParseEventLine(line);
            }

            if (inTimingSection)
            {
                ParseTimingPointLine(line);
            }

            if (line.Contains("OverallDifficulty:"))
            {
                string? odPart = SplitFirstColonValue(line);
                if (odPart != null)
                {
                    double parsed = JsNum.ParseFloat(odPart.Trim());
                    if (!double.IsNaN(parsed)) Od = parsed;
                }
            }

            if (line.Contains("CircleSize:"))
            {
                string? csPart = SplitFirstColonValue(line);
                if (csPart != null)
                {
                    string cs = csPart.Trim();
                    ColumnCount = cs == "0" ? 10 : StringToInt(cs);
                }
            }

            if (line.Contains("Mode:"))
            {
                string? modePart = SplitFirstColonValue(line);
                if (modePart != null)
                {
                    string mode = modePart.Trim();
                    GameMode = mode;
                    if (mode != "3")
                    {
                        Status = "NotMania";
                    }
                }
            }

            if (line == "[HitObjects]")
            {
                for (int j = i + 1; j < lines.Length; j += 1)
                {
                    string objLine = lines[j].Trim();
                    if (objLine.Length == 0) continue;
                    ParseHitObject(objLine);
                }
                break;
            }
        }

        LnRatio = GetLNRatio();
        NoteTimes = GetNoteTimes();
        ObjectIntervals = GetObjectIntervals();

        if (TimingPoints.Count == 0)
        {
            TimingPoints = new List<double[]> { new[] { 0.0, 500.0 } };
        }
        // JS: this.timingPoints.sort((a, b) => a[0] - b[0]) — stable.
        TimingPoints = TimingPoints.OrderBy(tp => tp[0]).ToList();

        if (Status != "Fail" && Status != "NotMania")
        {
            Status = "OK";
        }
    }

    // JS `line.split(":")[1]` — element index 1 of the colon split (or null when absent).
    private static string? SplitFirstColonValue(string line)
    {
        var parts = line.Split(':');
        return parts.Length > 1 ? parts[1] : null;
    }

    public void ParseEventLine(string eventLine)
    {
        if (string.IsNullOrEmpty(eventLine) || eventLine.StartsWith("//")) return;

        var @params = eventLine.Split(',').Select(part => part.Trim()).ToArray();
        if (@params.Length < 3) return;

        if (@params[0] != "2" && @params[0] != "Break") return;

        double breakStart = JsNum.ParseInt(@params[1]);
        double breakEnd = JsNum.ParseInt(@params[2]);
        if (double.IsNaN(breakStart) || double.IsNaN(breakEnd)) return;

        if (breakEnd > breakStart)
        {
            Breaks.Add(new[] { breakStart, breakEnd });
        }
    }

    public void ParseHitObject(string objectLine)
    {
        var @params = objectLine.Split(',');
        if (@params.Length < 5) return;

        try
        {
            double x = StringToInt(@params[0]);
            int column = 0;
            if (ColumnCount > 0)
            {
                // Keep lane mapping proportional to 512 width to avoid skew on keymodes
                // where 512 is not divisible by key count.
                column = NoteColumn.XToColumn(x, (int)ColumnCount);
            }
            Columns.Add(column);

            double noteStart = JsNum.ParseInt(@params[2]);
            double noteType = JsNum.ParseInt(@params[3]);
            NoteStarts.Add(noteStart);
            NoteTypes.Add(noteType);

            double noteEnd = noteStart;
            if ((ToInt32(noteType) & 128) != 0 && @params.Length >= 6)
            {
                var lastParamChunk = @params[5].Split(':');
                noteEnd = JsNum.ParseInt(lastParamChunk[0]);
            }
            NoteEnds.Add(noteEnd);
        }
        catch
        {
            Status = "Fail";
        }
    }

    public void ParseTimingPointLine(string timingLine)
    {
        if (string.IsNullOrEmpty(timingLine) || timingLine.StartsWith("//")) return;

        var parts = timingLine.Split(',').Select(item => item.Trim()).ToArray();
        if (parts.Length < 2) return;

        double t = Math.Truncate(JsNum.ParseFloat(parts[0]));
        double beatLength = JsNum.ParseFloat(parts[1]);
        double uninherited = parts.Length > 6 && parts[6].Length > 0 ? JsNum.ParseInt(parts[6]) : 1;

        if (!double.IsNaN(t) && !double.IsNaN(beatLength) && uninherited == 1 && beatLength > 0)
        {
            TimingPoints.Add(new[] { t, beatLength });
        }
    }

    public double GetBeatLengthAt(double timeMs)
    {
        if (TimingPoints.Count == 0) return 500.0;
        var times = TimingPoints.Select(tp => tp[0]).ToArray();
        int idx = BisectRight(times, Math.Truncate(timeMs)) - 1;
        if (idx < 0) return TimingPoints[0][1];
        return TimingPoints[idx][1];
    }

    public double GetLNRatio()
    {
        int totalNotes = NoteTypes.Count;
        if (totalNotes == 0) return 0;
        int lnCount = 0;
        foreach (var t in NoteTypes)
        {
            if ((ToInt32(t) & 128) != 0) lnCount += 1;
        }
        return (double)lnCount / totalNotes;
    }

    public double GetColumnCount() => ColumnCount;

    public Dictionary<int, List<double>> GetNoteTimes()
    {
        var noteTimes = new Dictionary<int, List<double>>();
        for (int i = 0; i < Columns.Count; i += 1)
        {
            int col = Columns[i];
            double time = NoteStarts[i];
            if (!noteTimes.TryGetValue(col, out var list))
            {
                list = new List<double>();
                noteTimes[col] = list;
            }
            list.Add(time);
        }

        foreach (var key in noteTimes.Keys.ToArray())
        {
            noteTimes[key].Sort();
        }

        return noteTimes;
    }

    public List<double[]> GetObjectIntervals()
    {
        if (NoteStarts.Count == 0) return new();

        var sortedStarts = NoteStarts.OrderBy(a => a).ToArray();
        var intervals = new List<double[]>();
        double? prevStart = null;
        foreach (var startTime in sortedStarts)
        {
            double interval = prevStart == null ? 0 : startTime - prevStart.Value;
            intervals.Add(new[] { startTime, interval });
            prevStart = startTime;
        }

        intervals.Sort((a, b) =>
        {
            if (b[1] != a[1]) return b[1].CompareTo(a[1]);
            return a[0].CompareTo(b[0]);
        });

        return intervals;
    }

    public void ModIN()
    {
        var startsByCol = new Dictionary<int, List<double>>();
        for (int i = 0; i < Columns.Count; i += 1)
        {
            int col = Columns[i];
            double start = NoteStarts[i];
            if (!startsByCol.TryGetValue(col, out var list))
            {
                list = new List<double>();
                startsByCol[col] = list;
            }
            list.Add(start);
        }

        var newObjects = new List<double[]>();
        // JS iterates Object.keys() — integer keys in ascending numeric order.
        foreach (var col in startsByCol.Keys.OrderBy(k => k))
        {
            var locations = startsByCol[col];
            locations.Sort();

            for (int i = 0; i < locations.Count - 1; i += 1)
            {
                double startTime = locations[i];
                double nextTime = locations[i + 1];
                double duration = nextTime - startTime;
                double beatLength = GetBeatLengthAt(nextTime);
                duration = Math.Max(duration / 2, duration - beatLength / 4);
                double endTime = startTime + duration;

                newObjects.Add(new[]
                {
                    JsNum.Round(startTime),
                    (double)col,
                    JsNum.Round(endTime),
                });
            }
        }

        newObjects.Sort((a, b) =>
        {
            if (a[0] != b[0]) return a[0].CompareTo(b[0]);
            return a[1].CompareTo(b[1]);
        });

        Columns = newObjects.Select(obj => (int)obj[1]).ToList();
        NoteStarts = newObjects.Select(obj => obj[0]).ToList();
        NoteTypes = newObjects.Select(_ => 128.0).ToList();
        NoteEnds = newObjects.Select(obj => obj[2]).ToList();
        Breaks = new();
        LnRatio = GetLNRatio();
        NoteTimes = GetNoteTimes();
        ObjectIntervals = GetObjectIntervals();
    }

    public void ModHO()
    {
        for (int i = 0; i < NoteTypes.Count; i += 1)
        {
            if ((ToInt32(NoteTypes[i]) & 128) != 0)
            {
                NoteTypes[i] = 1;
                NoteEnds[i] = 0;
            }
        }

        LnRatio = GetLNRatio();
        NoteTimes = GetNoteTimes();
        ObjectIntervals = GetObjectIntervals();
    }

    // JS bitwise coerces its operand with ToInt32; NaN -> 0.
    private static int ToInt32(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : unchecked((int)(long)v);
}
