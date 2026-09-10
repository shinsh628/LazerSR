// Port of mania-hub live-backend/vendor/leoblack/parser/patternOsuParser.js
//
// LeoBlack's snap-grid .osu parser used by the pattern analysis + interlude
// star rating. Produces a Chart of NoteType rows plus BPM/SV timelines.
//
// CROSS-AGENT CONTRACT (owned by the Patterns agent — port of
// vendor/leoblack/patterns/chart.js). This file assumes namespace
// `LazerSR.DanCalculator.Patterns` provides:
//
//   enum NoteType { NOTHING = 0, NORMAL = 1, HOLDHEAD = 2, HOLDBODY = 3, HOLDTAIL = 4 }
//
//   sealed class TimeItem<T> {            // createTimeItem(time, data)
//       double Time; T Data;
//       TimeItem(double time, T data);
//   }
//
//   sealed record Bpm(double Meter, double MsPerBeat);   // createBPM(meter, msPerBeat)
//
//   sealed class Chart {                  // createChart(keys, notes, bpm, sv)
//       Chart(int keys,
//             List<TimeItem<NoteType[]>> notes,
//             List<TimeItem<Bpm>> bpm,
//             List<TimeItem<double>> sv);
//       int Keys;
//       List<TimeItem<NoteType[]>> Notes;
//       List<TimeItem<Bpm>> BPM;
//       List<TimeItem<double>> SV;
//       double FirstNote => Notes[0].Time;
//       double LastNote  => Notes[^1].Time;
//   }
//
// If the Patterns port lands with different names, only the references in this
// file need updating — the logic below is a faithful 1:1 port.

using LazerSR.DanCalculator.Patterns;

namespace LazerSR.DanCalculator.Parser;

public static class PatternOsuParser
{
    private enum HitKind { HitCircle, Hold }

    private sealed class HitObject
    {
        public HitKind Kind;
        public double X;
        public double Time;
        public double EndTime;
    }

    private enum TpKind { Uninherited, Inherited }

    private sealed class TimingPoint
    {
        public TpKind Kind;
        public double Time;
        public double MsPerBeat;   // Uninherited
        public double Meter;       // Uninherited
        public double Multiplier;  // Inherited
    }

    private static Dictionary<string, List<string>> ParseSections(string[] lines)
    {
        string? sec = null;
        var @out = new Dictionary<string, List<string>>();

        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                sec = line[1..^1];
                if (!@out.ContainsKey(sec)) @out[sec] = new List<string>();
            }
            else if (sec != null)
            {
                @out[sec].Add(line);
            }
        }
        return @out;
    }

    private static Dictionary<string, string> ParseKV(List<string> sectionLines)
    {
        var @out = new Dictionary<string, string>();
        foreach (var line in sectionLines)
        {
            int idx = line.IndexOf(":");
            if (idx < 0) continue;
            @out[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return @out;
    }

    private static double FindEarliestUpcomingRelease(double?[] holdingUntil)
    {
        double earliest = double.PositiveInfinity;
        foreach (var h in holdingUntil)
        {
            if (h != null && h.Value < earliest) earliest = h.Value;
        }
        return earliest;
    }

    private static List<TimeItem<NoteType[]>> ConvertHitObjects(List<HitObject> objects, int keys)
    {
        var output = new List<TimeItem<NoteType[]>>();
        var holdingUntil = new double?[keys];
        var lastRow = new TimeItem<NoteType[]>(double.NegativeInfinity, Array.Empty<NoteType>());

        NoteType[] EmptyRow() => Enumerable.Repeat(NoteType.NOTHING, keys).ToArray();

        void FinishHolds(double time)
        {
            double earliest = FindEarliestUpcomingRelease(holdingUntil);

            while (earliest < time)
            {
                for (int k = 0; k < keys; k += 1)
                {
                    if (holdingUntil[k] != null && holdingUntil[k]!.Value == earliest)
                    {
                        if (earliest > lastRow.Time)
                        {
                            lastRow = new TimeItem<NoteType[]>(earliest, EmptyRow());
                            output.Add(lastRow);
                            for (int kk = 0; kk < keys; kk += 1)
                            {
                                if (holdingUntil[kk] != null)
                                {
                                    lastRow.Data[kk] = NoteType.HOLDBODY;
                                }
                            }
                        }

                        var cur = lastRow.Data[k];
                        if (cur == NoteType.NOTHING || cur == NoteType.HOLDBODY)
                        {
                            lastRow.Data[k] = NoteType.HOLDTAIL;
                            holdingUntil[k] = null;
                        }
                        else
                        {
                            throw new InvalidOperationException("impossible (HOLDTAIL overwrite conflict)");
                        }
                    }
                }
                earliest = FindEarliestUpcomingRelease(holdingUntil);
            }
        }

        void AddNote(int column, double time)
        {
            FinishHolds(time);

            if (time > lastRow.Time)
            {
                lastRow = new TimeItem<NoteType[]>(time, EmptyRow());
                output.Add(lastRow);
                for (int k = 0; k < keys; k += 1)
                {
                    if (holdingUntil[k] != null) lastRow.Data[k] = NoteType.HOLDBODY;
                }
            }

            var cur = lastRow.Data[column];
            if (cur == NoteType.NOTHING)
            {
                lastRow.Data[column] = NoteType.NORMAL;
            }
            else if (cur == NoteType.NORMAL || cur == NoteType.HOLDHEAD)
            {
                // keep stacked note behavior
            }
            else
            {
                throw new InvalidOperationException(
                    $"Stacked note at {time}, column {column + 1}, coincides with {(int)cur}");
            }
        }

        void StartHold(int column, double time, double endTime)
        {
            FinishHolds(time);

            if (time > lastRow.Time)
            {
                lastRow = new TimeItem<NoteType[]>(time, EmptyRow());
                output.Add(lastRow);
                for (int k = 0; k < keys; k += 1)
                {
                    if (holdingUntil[k] != null) lastRow.Data[k] = NoteType.HOLDBODY;
                }
            }

            var cur = lastRow.Data[column];
            if (cur == NoteType.NOTHING || cur == NoteType.NORMAL)
            {
                lastRow.Data[column] = NoteType.HOLDHEAD;
                holdingUntil[column] = endTime;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Stacked LN at {time}, column {column + 1}, head coincides with {(int)cur}");
            }
        }

        foreach (var obj in objects)
        {
            if (obj.Kind == HitKind.HitCircle)
            {
                AddNote(NoteColumn.XToColumn(obj.X, keys), obj.Time);
            }
            else if (obj.Kind == HitKind.Hold)
            {
                if (obj.EndTime > obj.Time)
                {
                    StartHold(NoteColumn.XToColumn(obj.X, keys), obj.Time, obj.EndTime);
                }
                else
                {
                    AddNote(NoteColumn.XToColumn(obj.X, keys), obj.Time);
                }
            }
        }

        FinishHolds(double.PositiveInfinity);
        return output;
    }

    private static Dictionary<double, double> FindBpmDurations(List<TimingPoint> points, double endTime)
    {
        var uninherited = points.Where(p => p.Kind == TpKind.Uninherited).ToList();
        if (uninherited.Count == 0)
        {
            throw new InvalidOperationException("Beatmap has no BPM points set");
        }

        var data = new Dictionary<double, double>();
        double current = uninherited[0].MsPerBeat;
        double time = uninherited[0].Time;

        foreach (var b in uninherited.Skip(1))
        {
            if (!data.ContainsKey(current)) data[current] = 0;
            data[current] += b.Time - time;
            time = b.Time;
            current = b.MsPerBeat;
        }

        if (!data.ContainsKey(current)) data[current] = 0;
        data[current] += Math.Max(endTime - time, 0);

        return data;
    }

    private static (List<TimeItem<Bpm>> Bpm, List<TimeItem<double>> Sv) ConvertTimingPoints(
        List<TimingPoint> points, double endTime)
    {
        var durations = FindBpmDurations(points, endTime);
        // JS: [...durations.entries()].sort((a, b) => b[1] - a[1])[0][0] — stable, ties keep insertion order.
        double mostCommonMspb = durations.OrderByDescending(kv => kv.Value).First().Key;

        var sv = new List<TimeItem<double>>();
        var bpm = new List<TimeItem<Bpm>>();
        double currentBpmMult = 1;

        foreach (var p in points)
        {
            if (p.Kind == TpKind.Uninherited)
            {
                double mspb = p.MsPerBeat;
                bpm.Add(new TimeItem<Bpm>(p.Time, new Bpm(p.Meter, mspb)));
                currentBpmMult = mspb != 0 ? mostCommonMspb / mspb : 1;
                sv.Add(new TimeItem<double>(p.Time, currentBpmMult));
            }
            else
            {
                sv.Add(new TimeItem<double>(p.Time, currentBpmMult * p.Multiplier));
            }
        }

        return (bpm, sv);
    }

    private static List<TimeItem<double>> CleanedSv(List<TimeItem<double>> sv)
    {
        if (sv.Count == 0) return new();

        var rev = Enumerable.Reverse(sv).ToList();
        var seen = new HashSet<double>();
        var dedupRev = new List<TimeItem<double>>();
        foreach (var item in rev)
        {
            if (seen.Contains(item.Time)) continue;
            seen.Add(item.Time);
            dedupRev.Add(item);
        }
        var dedup = Enumerable.Reverse(dedupRev).ToList();

        var @out = new List<TimeItem<double>>();
        double previousValue = 1;
        foreach (var s in dedup)
        {
            if (Math.Abs(s.Data - previousValue) > 0.005)
            {
                @out.Add(s);
                previousValue = s.Data;
            }
        }
        return @out;
    }

    private static List<TimingPoint> ParseTimingPoints(List<string> lines)
    {
        var @out = new List<TimingPoint>();
        foreach (var line in lines)
        {
            var parts = line.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 2) continue;

            double t = JsNum.ParseFloat(parts[0]);
            double beatLen = JsNum.ParseFloat(parts[1]);
            double meter = parts.Length > 2 && parts[2].Length > 0 ? JsNum.ParseInt(parts[2]) : 4;
            double uninherited = parts.Length > 6 && parts[6].Length > 0 ? JsNum.ParseInt(parts[6]) : 1;

            if (uninherited == 1)
            {
                @out.Add(new TimingPoint
                {
                    Kind = TpKind.Uninherited,
                    Time = t,
                    MsPerBeat = Math.Max(0, beatLen),
                    Meter = meter,
                });
            }
            else if (beatLen != 0)
            {
                @out.Add(new TimingPoint
                {
                    Kind = TpKind.Inherited,
                    Time = t,
                    Multiplier = -100.0 / beatLen,
                });
            }
        }
        return @out;
    }

    private static List<HitObject> ParseHitObjects(List<string> lines)
    {
        var @out = new List<HitObject>();
        foreach (var line in lines)
        {
            var parts = line.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 5) continue;

            double x = JsNum.ParseFloat(parts[0]);
            double time = Math.Truncate(JsNum.ParseFloat(parts[2]));
            double typ = JsNum.ParseInt(parts[3]);

            bool isHold = (ToInt32(typ) & 128) != 0;
            if (isHold)
            {
                double endTime = time;
                if (parts.Length >= 6 && parts[5].Length > 0)
                {
                    string endPart = parts[5].Split(':')[0];
                    double parsed = JsNum.ParseFloat(endPart);
                    if (!double.IsNaN(parsed)) endTime = Math.Truncate(parsed);
                }
                @out.Add(new HitObject { Kind = HitKind.Hold, X = x, Time = time, EndTime = endTime });
            }
            else
            {
                @out.Add(new HitObject { Kind = HitKind.HitCircle, X = x, Time = time });
            }
        }
        return @out;
    }

    public static Chart ParseOsuManiaFromText(string osuText)
    {
        var lines = System.Text.RegularExpressions.Regex.Split(osuText, "\r?\n");
        var sections = ParseSections(lines);

        var diff = ParseKV(sections.TryGetValue("Difficulty", out var d) ? d : new List<string>());
        int keys;
        // PORT NOTE: JS `Math.trunc(Number.parseFloat(diff.CircleSize || "4"))` inside a
        // try/catch that can never actually throw for these inputs; (int) of NaN is 0.
        {
            double parsed = JsNum.ParseFloat(diff.TryGetValue("CircleSize", out var cs) ? cs : "4");
            keys = double.IsNaN(parsed) ? 0 : (int)Math.Truncate(parsed);
        }

        var timingPoints = ParseTimingPoints(sections.TryGetValue("TimingPoints", out var tp) ? tp : new List<string>());
        var hitObjects = ParseHitObjects(sections.TryGetValue("HitObjects", out var ho) ? ho : new List<string>());

        var snaps = ConvertHitObjects(hitObjects, keys);
        if (snaps.Count == 0)
        {
            throw new InvalidOperationException("Beatmap has no hitobjects after conversion");
        }

        double endTime = snaps[^1].Time;
        List<TimeItem<Bpm>> bpm;
        List<TimeItem<double>> sv;

        if (timingPoints.Count > 0)
        {
            (bpm, sv) = ConvertTimingPoints(timingPoints, endTime);
            sv = CleanedSv(sv);
        }
        else
        {
            bpm = new List<TimeItem<Bpm>> { new(0, new Bpm(4, 500.0)) };
            sv = new List<TimeItem<double>> { new(0, 1.0) };
        }

        return new Chart(keys, snaps, bpm, sv);
    }

    // JS bitwise coerces its operand with ToInt32; NaN -> 0.
    private static int ToInt32(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : unchecked((int)(long)v);
}
