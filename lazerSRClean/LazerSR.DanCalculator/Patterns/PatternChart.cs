// Port of vendor/leoblack/patterns/chart.js
// Owned by P4. The LeoBlack .osu parsers (LazerSR.DanCalculator.Parser.PatternOsuParser
// / OsuFileParser, ported by agent P1) construct these types — the shape here matches
// the cross-agent contract documented at the top of Parser/PatternOsuParser.cs.

namespace LazerSR.DanCalculator.Patterns;

/// <summary>NoteType enum from patterns/chart.js (Object.freeze).</summary>
public enum NoteType
{
    NOTHING = 0,
    NORMAL = 1,
    HOLDHEAD = 2,
    HOLDBODY = 3,
    HOLDTAIL = 4,
}

/// <summary>createTimeItem(time, data).</summary>
public sealed class TimeItem<T>
{
    public double Time;
    public T Data;

    public TimeItem(double time, T data)
    {
        Time = time;
        Data = data;
    }
}

/// <summary>createBPM(meter, msPerBeat).</summary>
public sealed record Bpm(double Meter, double MsPerBeat);

/// <summary>createChart(keys, notes, bpm, sv).</summary>
public sealed class Chart
{
    public int Keys;
    public List<TimeItem<NoteType[]>> Notes;
    public List<TimeItem<Bpm>> BPM;
    public List<TimeItem<double>> SV;

    public Chart(int keys, List<TimeItem<NoteType[]>> notes, List<TimeItem<Bpm>> bpm, List<TimeItem<double>> sv)
    {
        Keys = keys;
        Notes = notes;
        BPM = bpm;
        SV = sv;
    }

    public double FirstNote => Notes[0].Time;
    public double LastNote => Notes[^1].Time;
}
