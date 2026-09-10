// Port of mania-hub live-backend/src/dan/invert-mod.ts
//
// osu!lazer's Invert mod (ManiaModInvert) applied to a mania .osu text, so a
// play set under it can be rated against the chart it actually played.
//
// Lazer rebuilds every column from its "locations": the start time of every
// object, note or hold (a hold's end is ignored, so an existing hold reaches
// the next object like any note would). Consecutive locations become one hold
// from the earlier to just short of the later, shortened by a quarter of the
// beat length in force at the later one but never by more than half the gap,
// so no hold is instantaneous. The last location in every column gets nothing,
// and breaks are cleared. Samples stay on the head; the release is silent.
//
// Everything outside [HitObjects] and the break lines under [Events] is copied
// through byte for byte. Reference: osu.Game.Rulesets.Mania/Mods/ManiaModInvert.cs.

using System.Globalization;
using System.Text.RegularExpressions;

namespace LazerSR.DanCalculator.Classifier;

public static class InvertMod
{
    private sealed class Location
    {
        public double Time;
        public string X = "";
        public string Y = "";
        public string HitSound = "";
        // The osu! hit-sample block ("0:0:0:0:"), already stripped of a hold's
        // end time. Lazer keeps the head's samples and silences the release.
        public string Sample = "";
    }

    private sealed record TimingSection(double Time, double BeatLength);

    private const string DEFAULT_SAMPLE = "0:0:0:0:";

    private static readonly Regex circleSizeRe = new(@"^CircleSize\s*:\s*(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex breakLineRe = new(@"^(2|Break)\s*,", RegexOptions.Compiled);

    private static double ParseFloatJs(string s)
    {
        var m = Regex.Match(s.Trim(), @"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?");
        return m.Success ? double.Parse(m.Value, CultureInfo.InvariantCulture) : double.NaN;
    }

    private static double ParseIntJs(string s)
    {
        var m = Regex.Match(s.Trim(), @"^[+-]?\d+");
        return m.Success ? double.Parse(m.Value, CultureInfo.InvariantCulture) : double.NaN;
    }

    /// <summary>
    /// The inverted chart as .osu text, or null when the file is not a mania
    /// chart this can rebuild.
    /// </summary>
    public static string? InvertManiaOsuText(string osuText)
    {
        var lines = osuText.Split('\n');
        int? keyCount = null;
        var timing = new List<TimingSection>();
        var columns = new Dictionary<int, List<Location>>();
        int hitObjectsStart = -1;
        int hitObjectsEnd = lines.Length;
        string section = "";
        int hitObjects = 0;

        for (int index = 0; index < lines.Length; index += 1)
        {
            string line = lines[index].Trim();
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                if (section == "HitObjects") hitObjectsEnd = index;
                section = line[1..^1];
                if (section == "HitObjects") hitObjectsStart = index;
                continue;
            }
            if (string.IsNullOrEmpty(line) || line.StartsWith("//")) continue;

            if (section == "Difficulty")
            {
                var match = circleSizeRe.Match(line);
                if (match.Success)
                {
                    double parsed = Math.Floor(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) + 0.5);
                    keyCount = parsed == Math.Floor(parsed) && parsed > 0 ? (int)Math.Min(18, parsed) : null;
                }
            }
            else if (section == "TimingPoints")
            {
                var parts = line.Split(',');
                if (parts.Length < 2) continue;
                double time = ParseFloatJs(parts[0]);
                double beatLength = ParseFloatJs(parts[1]);
                // Inherited (green) lines carry a negative multiplier; a short
                // line is a red line by definition.
                bool uninherited = parts.Length < 7 || parts[6].Trim() != "0";
                if (double.IsFinite(time) && double.IsFinite(beatLength) && beatLength > 0 && uninherited)
                    timing.Add(new TimingSection(time, beatLength));
            }
            else if (section == "HitObjects")
            {
                if (keyCount == null) return null;
                var parts = line.Split(',');
                if (parts.Length < 5) continue;
                double x = ParseIntJs(parts[0]);
                double time = ParseIntJs(parts[2]);
                double type = ParseIntJs(parts[3]);
                if (!double.IsFinite(x) || !double.IsFinite(time) || !double.IsFinite(type)) continue;
                hitObjects += 1;
                int column = (int)Math.Max(0, Math.Min(keyCount.Value - 1, Math.Floor(x * keyCount.Value / 512)));
                bool isHold = ((int)type & 128) != 0;
                string rest = string.Join(",", parts.Skip(5));
                string sample = DEFAULT_SAMPLE;
                if (isHold)
                {
                    // "endTime:sample"; the end time itself is not a location.
                    int colon = rest.IndexOf(':');
                    if (colon >= 0 && rest[(colon + 1)..].Trim().Length > 0) sample = rest[(colon + 1)..].Trim();
                }
                else if (rest.Trim().Length > 0)
                {
                    sample = rest.Trim();
                }
                if (!columns.TryGetValue(column, out var list))
                {
                    list = new List<Location>();
                    columns[column] = list;
                }
                list.Add(new Location
                {
                    Time = time,
                    X = parts[0].Trim(),
                    Y = parts[1].Trim(),
                    HitSound = parts[4].Trim(),
                    Sample = sample,
                });
            }
        }

        if (keyCount == null || hitObjectsStart < 0 || hitObjects == 0) return null;
        timing.Sort((a, b) => a.Time.CompareTo(b.Time));

        var inverted = new List<(double Time, int Column, string Line)>();
        // Dictionary iteration order differs from JS Map; the final list is
        // re-sorted by (time, column) so this does not matter.
        foreach (var (column, locations) in columns)
        {
            // Stable sort, like LINQ OrderBy.
            var ordered = locations.OrderBy(l => l.Time).ToList();
            for (int index = 0; index < ordered.Count - 1; index += 1)
            {
                var head = ordered[index];
                double nextTime = ordered[index + 1].Time;
                double beatLength = BeatLengthAt(timing, nextTime);
                double gap = nextTime - head.Time;
                // "Decrease the duration by at most a 1/4 beat to ensure there's
                // no instantaneous notes."
                double duration = Math.Max(gap / 2, gap - beatLength / 4);
                double endTime = Math.Floor(head.Time + duration + 0.5);
                string outLine = endTime > head.Time
                    ? $"{head.X},{head.Y},{head.Time},128,{head.HitSound},{endTime}:{head.Sample}"
                    // Two locations at one instant leave lazer a zero-length hold,
                    // which is a tap in everything but name; write the tap.
                    : $"{head.X},{head.Y},{head.Time},1,{head.HitSound},{head.Sample}";
                inverted.Add((head.Time, column, outLine));
            }
        }
        inverted.Sort((a, b) =>
        {
            int c = a.Time.CompareTo(b.Time);
            return c != 0 ? c : a.Column.CompareTo(b.Column);
        });

        var outLines = new List<string>();
        section = "";
        for (int index = 0; index < hitObjectsStart; index += 1)
        {
            string raw = lines[index];
            string line = raw.Trim();
            if (line.StartsWith("[") && line.EndsWith("]")) section = line[1..^1];
            else if (section == "Events" && breakLineRe.IsMatch(line)) continue;
            outLines.Add(raw);
        }
        outLines.Add(lines[hitObjectsStart]);
        foreach (var entry in inverted) outLines.Add(entry.Line);
        if (hitObjectsEnd < lines.Length)
        {
            outLines.Add("");
            for (int index = hitObjectsEnd; index < lines.Length; index += 1) outLines.Add(lines[index]);
        }
        return string.Join("\n", outLines);
    }

    /// <summary>
    /// ControlPointInfo.TimingPointAt: the red line in force at `time`, falling
    /// back to the first one (and to lazer's default 1000ms beat with none).
    /// </summary>
    private static double BeatLengthAt(List<TimingSection> timing, double time)
    {
        if (timing.Count == 0) return 1000;
        var current = timing[0];
        foreach (var point in timing)
        {
            if (point.Time > time) break;
            current = point;
        }
        return current.BeatLength;
    }
}
