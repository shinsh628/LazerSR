// Port of vendor/leoblack/patterns/primitives.js

namespace LazerSR.DanCalculator.Patterns;

public static class Direction
{
    public const string NONE = "None";
    public const string LEFT = "Left";
    public const string RIGHT = "Right";
    public const string OUTWARDS = "Outwards";
    public const string INWARDS = "Inwards";
}

/// <summary>A primitive row as calculatePrimitives / asHeadPointRow / COORDINATION_RELEASE build it.</summary>
public sealed class PrimitiveRow
{
    public double Index;
    public double Time;
    public double MsPerBeat;
    public double BeatLength;
    public int Notes;
    public int Jacks;
    public string Direction = Patterns.Direction.NONE;
    public bool Roll;
    public int Keys;
    public int LeftHandKeys;
    public List<int> LNHeads = new();
    public List<int> LNBodies = new();
    public List<int> LNTails = new();
    public List<int> NormalNotes = new();
    public List<int> RawNotes = new();
}

public static class Primitives
{
    private static int KeysOnLeftHand(int keymode)
    {
        if (keymode == 3) return 2;
        if (keymode == 4) return 2;
        if (keymode == 5) return 3;
        if (keymode == 6) return 3;
        if (keymode == 7) return 4;
        if (keymode == 8) return 4;
        if (keymode == 9) return 5;
        if (keymode == 10) return 5;
        return Math.Max(1, (int)Math.Floor(keymode / 2.0));
    }

    private static double BeatLengthAt(Chart chart, double time)
    {
        if (chart.BPM.Count == 0) return 500;
        double current = chart.BPM[0].Data.MsPerBeat;
        foreach (var item in chart.BPM)
        {
            if (item.Time > time) break;
            current = item.Data.MsPerBeat;
        }
        return current;
    }

    public static (string Direction, bool Roll) DetectDirection(List<int> previousRow, List<int> currentRow)
    {
        int pleftmost = previousRow[0];
        int prightmost = previousRow[previousRow.Count - 1];
        int cleftmost = currentRow[0];
        int crightmost = currentRow[currentRow.Count - 1];

        int leftmostChange = cleftmost - pleftmost;
        int rightmostChange = crightmost - prightmost;

        string direction = Patterns.Direction.NONE;
        if (leftmostChange > 0)
        {
            direction = rightmostChange > 0 ? Patterns.Direction.RIGHT : Patterns.Direction.INWARDS;
        }
        else if (leftmostChange < 0)
        {
            direction = rightmostChange < 0 ? Patterns.Direction.LEFT : Patterns.Direction.OUTWARDS;
        }
        else if (rightmostChange < 0)
        {
            direction = Patterns.Direction.INWARDS;
        }
        else if (rightmostChange > 0)
        {
            direction = Patterns.Direction.OUTWARDS;
        }

        bool isRoll = pleftmost > crightmost || prightmost < cleftmost;
        return (direction, isRoll);
    }

    public static List<PrimitiveRow> CalculatePrimitives(Chart chart)
    {
        double firstNote = chart.Notes[0].Time;
        NoteType[] firstRow = chart.Notes[0].Data;

        var previousRow = new List<int>();
        for (int k = 0; k < chart.Keys; k += 1)
        {
            if (firstRow[k] == NoteType.NORMAL || firstRow[k] == NoteType.HOLDHEAD)
            {
                previousRow.Add(k);
            }
        }

        if (previousRow.Count == 0) return new List<PrimitiveRow>();

        double previousTime = firstNote;
        int index = 0;
        int leftHandKeys = KeysOnLeftHand(chart.Keys);
        var outList = new List<PrimitiveRow>();

        for (int rowI = 1; rowI < chart.Notes.Count; rowI += 1)
        {
            var item = chart.Notes[rowI];
            double t = item.Time;
            NoteType[] row = item.Data;
            index += 1;

            var currentRow = new List<int>();
            var normalNotes = new List<int>();
            var lnHeads = new List<int>();
            var lnBodies = new List<int>();
            var lnTails = new List<int>();

            for (int k = 0; k < chart.Keys; k += 1)
            {
                NoteType n = row[k];
                if (n == NoteType.NORMAL || n == NoteType.HOLDHEAD) currentRow.Add(k);
                if (n == NoteType.NORMAL) normalNotes.Add(k);
                if (n == NoteType.HOLDHEAD) lnHeads.Add(k);
                else if (n == NoteType.HOLDBODY) lnBodies.Add(k);
                else if (n == NoteType.HOLDTAIL) lnTails.Add(k);
            }

            if (currentRow.Count == 0 && lnHeads.Count == 0 && lnBodies.Count == 0 && lnTails.Count == 0)
            {
                continue;
            }

            string direction = Patterns.Direction.NONE;
            bool isRoll = false;
            int jacks = 0;

            if (currentRow.Count != 0)
            {
                (direction, isRoll) = DetectDirection(previousRow, currentRow);
                var prevSet = new HashSet<int>(previousRow);
                jacks = currentRow.Count(x => prevSet.Contains(x));
            }

            outList.Add(new PrimitiveRow
            {
                Index = index,
                Time = t - firstNote,
                MsPerBeat = (t - previousTime) * 4.0,
                BeatLength = BeatLengthAt(chart, t),
                Notes = currentRow.Count,
                Jacks = jacks,
                Direction = direction,
                Roll = isRoll,
                Keys = chart.Keys,
                LeftHandKeys = leftHandKeys,
                LNHeads = lnHeads,
                LNBodies = lnBodies,
                LNTails = lnTails,
                NormalNotes = normalNotes,
                RawNotes = currentRow,
            });

            if (currentRow.Count != 0) previousRow = currentRow;
            previousTime = t;
        }

        return outList;
    }

    public static double LnPercent(Chart chart)
    {
        double notes = 0;
        double lnotes = 0;

        foreach (var item in chart.Notes)
        {
            foreach (var n in item.Data)
            {
                if (n == NoteType.NORMAL) notes += 1;
                else if (n == NoteType.HOLDHEAD)
                {
                    notes += 1;
                    lnotes += 1;
                }
            }
        }

        return notes > 0 ? lnotes / notes : 0;
    }

    public static double SvTime(Chart chart)
    {
        if (chart.SV.Count == 0) return 0;

        double total = 0;
        double time = chart.FirstNote;
        double vel = 1;
        int nonOneIntervals = 0;
        bool inNonOne = false;

        foreach (var sv in chart.SV)
        {
            double curVel = sv.Data;
            bool curNonOne = !double.IsFinite(curVel) || Math.Abs(curVel - 1) > PatternsConfig.SV_SPEED_EPS;

            if (!double.IsFinite(vel) || Math.Abs(vel - 1) > PatternsConfig.SV_SPEED_EPS)
            {
                total += sv.Time - time;
            }

            if (curNonOne && !inNonOne)
            {
                nonOneIntervals += 1;
                inNonOne = true;
            }
            else if (!curNonOne)
            {
                inNonOne = false;
            }

            vel = curVel;
            time = sv.Time;
        }

        if (!double.IsFinite(vel) || Math.Abs(vel - 1) > PatternsConfig.SV_SPEED_EPS)
        {
            total += chart.LastNote - time;
        }

        if (nonOneIntervals <= 1)
        {
            return 0;
        }

        bool extreme = false;
        var bpms = chart.BPM;
        if (bpms.Count >= 1)
        {
            double? prevMsPerBeat = null;
            foreach (var item in bpms)
            {
                double msPerBeat = item.Data.MsPerBeat;
                if (!double.IsFinite(msPerBeat) || msPerBeat <= 0)
                {
                    extreme = true;
                    break;
                }

                double bpm = 60000.0 / msPerBeat;
                if (bpm <= PatternsConfig.SV_EXTREME_BPM_MIN || bpm >= PatternsConfig.SV_EXTREME_BPM_MAX)
                {
                    extreme = true;
                    break;
                }

                if (prevMsPerBeat is double pmpb && double.IsFinite(pmpb) && pmpb > 0)
                {
                    double ratio = Math.Max(pmpb / msPerBeat, msPerBeat / pmpb);
                    if (ratio >= PatternsConfig.SV_EXTREME_BPM_RATIO)
                    {
                        extreme = true;
                        break;
                    }
                }

                prevMsPerBeat = msPerBeat;
            }
        }

        if (extreme)
        {
            return Math.Max(total, PatternsConfig.SV_AMOUNT_THRESHOLD + 1.0);
        }

        return total;
    }
}
