// Port of mania-hub live-backend/src/dan/beatmap-parser.ts
// One of TWO parsers in this project. This one (mania-hub's) feeds the classifier,
// feature extraction, pattern analysis, LN kNN and vibro detection.
// LeoBlack's own parser (Parser/OsuFileParser.cs) feeds the LeoBlack estimators/interlude.
// Keep both; they have different output shapes on purpose.

using System.Globalization;
using System.Text.RegularExpressions;

namespace LazerSR.DanCalculator.Beatmap;

public sealed class ManiaNote
{
    public int Column;      // 0-indexed
    public double Time;     // start ms
    public double EndTime;  // end ms (== Time for taps, > Time for holds)
    public bool IsHold;
}

public readonly record struct ManiaScrollVelocity(double Time, double Multiplier);

public readonly record struct ManiaBreakPeriod(double StartTime, double EndTime);

public readonly record struct ManiaTimingPoint(double Time, double BeatLength);

public sealed class ManiaBeatmap
{
    public string Title = "";
    public string Artist = "";
    public string Version = "";
    public string Creator = "";
    public int KeyCount;
    public double Od;
    public double Bpm;
    public List<ManiaNote> Notes = new();
    public double TotalLength;
    public long? BeatmapsetId;
    public string AudioFilename = "";
    public double PreviewTime;
    public string BackgroundFilename = "";
    public List<ManiaBreakPeriod> BreakPeriods = new();
    public List<ManiaScrollVelocity> ScrollVelocities = new();
    /// <summary>Uninherited (red line) timing points in file order.</summary>
    public List<ManiaTimingPoint>? TimingPoints;
}

public static class ManiaBeatmapParser
{
    private const double DEFAULT_BEAT_LENGTH = 1000;
    private const double SCROLL_MULTIPLIER_EPSILON = 1e-4;

    private readonly record struct TimingPoint(double Time, double BeatLength);

    private abstract record ParsedControlPoint(int Order, double Time)
    {
        public sealed record Timing(int Order, double Time, double BeatLength) : ParsedControlPoint(Order, Time);
        public sealed record Effect(int Order, double Time, double ScrollSpeed) : ParsedControlPoint(Order, Time);
    }

    private static int NormalizeKeyCount(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return 4;
        double v = value == Math.Floor(value) ? value : Math.Ceiling(value);
        return (int)Math.Max(1, Math.Min(18, v));
    }

    private static double GetMostCommonBeatLength(List<TimingPoint> timingPoints, double lastObjectTime)
    {
        if (timingPoints.Count == 0) return DEFAULT_BEAT_LENGTH;

        double lastTime = lastObjectTime > 0 ? lastObjectTime : (timingPoints.Count > 0 ? timingPoints[^1].Time : 0);
        var durations = new Dictionary<double, double>();

        for (int i = 0; i < timingPoints.Count; i++)
        {
            var point = timingPoints[i];
            if (point.Time > lastTime)
            {
                durations.TryAdd(point.BeatLength, 0);
                continue;
            }

            double currentTime = i == 0 ? 0 : point.Time;
            double nextTime = i == timingPoints.Count - 1 ? lastTime : timingPoints[i + 1].Time;
            double duration = Math.Max(0, nextTime - currentTime);
            double roundedBeatLength = Math.Round(point.BeatLength * 1000) / 1000;
            durations[roundedBeatLength] = durations.GetValueOrDefault(roundedBeatLength) + duration;
        }

        double mostCommonBeatLength = 0;
        double longestDuration = -1;
        foreach (var (beatLength, duration) in durations)
        {
            if (duration > longestDuration)
            {
                mostCommonBeatLength = beatLength;
                longestDuration = duration;
            }
        }

        if (mostCommonBeatLength <= 0) return DEFAULT_BEAT_LENGTH;

        double minBeatLength = double.PositiveInfinity;
        double maxBeatLength = double.NegativeInfinity;
        foreach (var point in timingPoints)
        {
            if (point.BeatLength < minBeatLength) minBeatLength = point.BeatLength;
            if (point.BeatLength > maxBeatLength) maxBeatLength = point.BeatLength;
        }
        return Math.Max(minBeatLength, Math.Min(maxBeatLength, mostCommonBeatLength));
    }

    private static List<ManiaScrollVelocity> BuildManiaScrollVelocities(
        List<TimingPoint> timingPoints, List<ParsedControlPoint> controlPoints, double lastObjectTime)
    {
        if (controlPoints.Count == 0 || timingPoints.Count == 0) return new();

        double baseBeatLength = GetMostCommonBeatLength(timingPoints, lastObjectTime);
        var collapsed = new List<ManiaScrollVelocity>();
        double currentBeatLength = DEFAULT_BEAT_LENGTH;
        double currentScrollSpeed = 1;

        var sorted = controlPoints
            .OrderBy(p => p.Time).ThenBy(p => p.Order)
            .ToList();

        foreach (var point in sorted)
        {
            if (point.Time > lastObjectTime) break;

            if (point is ParsedControlPoint.Timing t) currentBeatLength = t.BeatLength;
            else if (point is ParsedControlPoint.Effect e) currentScrollSpeed = e.ScrollSpeed;

            double rawMultiplier = Math.Max(0.01, Math.Min(20, currentScrollSpeed * baseBeatLength / currentBeatLength));
            double multiplier = Math.Abs(rawMultiplier - 1) <= SCROLL_MULTIPLIER_EPSILON ? 1 : rawMultiplier;

            if (collapsed.Count > 0 && collapsed[^1].Time == point.Time)
                collapsed[^1] = collapsed[^1] with { Multiplier = multiplier };
            else
                collapsed.Add(new ManiaScrollVelocity(point.Time, multiplier));
        }

        var output = new List<ManiaScrollVelocity>();
        double previousMultiplier = 1;
        foreach (var point in collapsed)
        {
            if (Math.Abs(point.Multiplier - previousMultiplier) <= SCROLL_MULTIPLIER_EPSILON) continue;
            output.Add(point);
            previousMultiplier = point.Multiplier;
        }
        return output;
    }

    private static double ParseFloatJs(string s)
    {
        // JS parseFloat: leading number, ignores trailing junk. NaN -> we return 0 where callers expect it.
        var m = Regex.Match(s.Trim(), @"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?");
        return m.Success ? double.Parse(m.Value, CultureInfo.InvariantCulture) : double.NaN;
    }

    private static int ParseIntJs(string s)
    {
        var m = Regex.Match(s.Trim(), @"^[+-]?\d+");
        return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : 0;
    }

    public static ManiaBeatmap Parse(string content)
    {
        var lines = content.Split('\n').Select(l => l.Trim()).ToArray();

        string title = "", artist = "", version = "", creator = "";
        double circleSize = 4;
        double overallDifficulty = 8;
        long? beatmapsetId = null;
        string audioFilename = "";
        double previewTime = 0;
        string backgroundFilename = "";
        string section = "";
        var notes = new List<ManiaNote>();
        var breakPeriods = new List<ManiaBreakPeriod>();
        var timingPoints = new List<TimingPoint>();
        var controlPoints = new List<ParsedControlPoint>();
        int controlPointOrder = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line[1..^1];
                continue;
            }

            if (section == "General")
            {
                if (line.StartsWith("AudioFilename:")) audioFilename = line[14..].Trim();
                if (line.StartsWith("PreviewTime:"))
                {
                    var parts = line.Split(':');
                    previewTime = parts.Length > 1 ? ParseIntJs(parts[1].Trim()) : 0;
                }
            }

            if (section == "Metadata")
            {
                if (line.StartsWith("Title:")) title = line[6..].Trim();
                if (line.StartsWith("Artist:")) artist = line[7..].Trim();
                if (line.StartsWith("Version:")) version = line[8..].Trim();
                if (line.StartsWith("Creator:")) creator = line[8..].Trim();
                if (line.StartsWith("BeatmapSetID:"))
                {
                    if (long.TryParse(line[13..].Trim(), out var parsed) && parsed > 0) beatmapsetId = parsed;
                    else beatmapsetId = null;
                }
            }

            if (section == "Events")
            {
                if (string.IsNullOrEmpty(backgroundFilename))
                {
                    var match = Regex.Match(line, "^0,0,\"([^\"]+)\"");
                    if (match.Success) backgroundFilename = match.Groups[1].Value;
                }

                var breakMatch = Regex.Match(line, @"^2,(\d+),(\d+)");
                if (breakMatch.Success)
                {
                    double startTime = double.Parse(breakMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    double endTime = double.Parse(breakMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (endTime > startTime) breakPeriods.Add(new ManiaBreakPeriod(startTime, endTime));
                }
            }

            if (section == "Difficulty")
            {
                if (line.StartsWith("CircleSize:")) circleSize = ParseFloatJs(line.Split(':')[1].Trim());
                if (line.StartsWith("OverallDifficulty:")) overallDifficulty = ParseFloatJs(line.Split(':')[1].Trim());
            }

            if (section == "TimingPoints" && line.Contains(','))
            {
                var parts = line.Split(',');
                if (parts.Length >= 2)
                {
                    double time = ParseFloatJs(parts[0]);
                    double beatLength = ParseFloatJs(parts[1]);
                    bool uninherited = parts.Length < 7 || parts[6].Trim() != "0";
                    if (beatLength > 0 && uninherited)
                    {
                        timingPoints.Add(new TimingPoint(time, beatLength));
                        controlPoints.Add(new ParsedControlPoint.Timing(controlPointOrder++, time, beatLength));
                        controlPoints.Add(new ParsedControlPoint.Effect(controlPointOrder++, time, 1));
                    }
                    else if (beatLength < 0 && !uninherited)
                    {
                        controlPoints.Add(new ParsedControlPoint.Effect(
                            controlPointOrder++, time, Math.Max(0.01, Math.Min(20, -100 / beatLength))));
                    }
                }
            }

            if (section == "HitObjects" && line.Contains(','))
            {
                var parts = line.Split(',');
                if (parts.Length >= 5)
                {
                    int x = ParseIntJs(parts[0]);
                    int time = ParseIntJs(parts[2]);
                    int type = ParseIntJs(parts[3]);
                    int keyCount = NormalizeKeyCount(circleSize);

                    int column = (int)Math.Floor((double)x * keyCount / 512);
                    bool isHold = (type & 128) != 0;
                    int endTime = time;

                    if (isHold && parts.Length >= 6)
                    {
                        var extras = parts[5].Split(':');
                        int parsedEnd = ParseIntJs(extras[0]);
                        endTime = parsedEnd != 0 ? parsedEnd : time;
                    }

                    notes.Add(new ManiaNote
                    {
                        Column = Math.Min(column, keyCount - 1),
                        Time = time,
                        EndTime = endTime,
                        IsHold = isHold,
                    });
                }
            }
        }

        double bpm = timingPoints.Count > 0 ? Math.Round(60000 / timingPoints[0].BeatLength) : 0;

        double totalLength = 0;
        foreach (var n in notes)
            if (n.EndTime > totalLength) totalLength = n.EndTime;

        // Stable sort by start time (JS Array.sort is stable).
        var sortedNotes = notes.OrderBy(n => n.Time).ToList();

        return new ManiaBeatmap
        {
            Title = title,
            Artist = artist,
            Version = version,
            Creator = creator,
            KeyCount = NormalizeKeyCount(circleSize),
            Od = overallDifficulty,
            Bpm = bpm,
            Notes = sortedNotes,
            TotalLength = totalLength,
            BeatmapsetId = beatmapsetId,
            AudioFilename = audioFilename,
            PreviewTime = previewTime,
            BackgroundFilename = backgroundFilename,
            BreakPeriods = breakPeriods,
            ScrollVelocities = BuildManiaScrollVelocities(timingPoints, controlPoints, totalLength),
            TimingPoints = timingPoints.Select(t => new ManiaTimingPoint(t.Time, t.BeatLength)).ToList(),
        };
    }
}
