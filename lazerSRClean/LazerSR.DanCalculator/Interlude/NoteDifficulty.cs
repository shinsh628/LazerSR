// Port of vendor/leoblack/interlude/noteDifficulty.js

using LazerSR.DanCalculator.Patterns;
using static LazerSR.DanCalculator.Interlude.NumberUtils;

namespace LazerSR.DanCalculator.Interlude;

/// <summary>Per-note rating: { J, SL, SR, Total }.</summary>
public sealed class NoteRating
{
    public double J;
    public double SL;
    public double SR;
    public double Total;
}

public static class NoteDifficulty
{
    private static readonly double JACK_CURVE_CUTOFF = F32(230.0);
    private static readonly double STREAM_CURVE_CUTOFF = F32(10.0);
    private static readonly double STREAM_CURVE_CUTOFF_2 = F32(10.0);
    private static readonly double OHTNERF = F32(3.0);
    private static readonly double STREAM_SCALE = F32(6.0);
    private static readonly double STREAM_POW = F32(0.5);

    private static double MsToJackBpm(double delta)
    {
        double value = F32(PatternsDef.JackBpm(delta));
        return value < JACK_CURVE_CUTOFF ? value : JACK_CURVE_CUTOFF;
    }

    private static double MsToStreamBpm(double delta)
    {
        double x = F32(0.02 * delta);
        if (!double.IsFinite(x) || x <= 0)
        {
            return 0.0;
        }

        double value = F32(
            (300.0 / x)
            - (300.0 / Math.Pow(x, STREAM_CURVE_CUTOFF) / STREAM_CURVE_CUTOFF_2));
        return value > 0 ? value : 0.0;
    }

    private static double JackCompensation(double jackDelta, double streamDelta)
    {
        double ratio = jackDelta / streamDelta;
        if (!double.IsFinite(ratio) || ratio <= 0)
        {
            return 0.0;
        }

        double compensated = Math.Sqrt(Math.Max(0.0, Math.Log2(ratio)));
        return Math.Min(1.0, compensated);
    }

    public static double NoteDifficultyTotal(NoteRating note)
    {
        return F32(
            Math.Pow(
                Math.Pow(STREAM_SCALE * Math.Pow(note.SL, STREAM_POW), OHTNERF)
                + Math.Pow(STREAM_SCALE * Math.Pow(note.SR, STREAM_POW), OHTNERF)
                + Math.Pow(note.J, OHTNERF),
                1.0 / OHTNERF));
    }

    public static List<NoteRating[]> CalculateNoteRatings(double rate, List<InterludeRow> noteRows)
    {
        if (noteRows == null || noteRows.Count == 0)
        {
            return new List<NoteRating[]>();
        }

        double rateValue = double.IsFinite(rate) && rate > 0 ? rate : 1.0;
        int keys = noteRows[0].Data.Length;
        int handSplit = Layout.KeysOnLeftHand(keys);

        var data = new List<NoteRating[]>(noteRows.Count);
        for (int i = 0; i < noteRows.Count; i += 1)
        {
            var rowData = new NoteRating[keys];
            for (int k = 0; k < keys; k += 1) rowData[k] = new NoteRating();
            data.Add(rowData);
        }

        double t0 = noteRows[0].Time;
        double firstTime = (t0 != 0 && !double.IsNaN(t0)) ? t0 : 0; // Number(noteRows[0].time) || 0
        var lastNoteInColumn = new double[keys];
        for (int k = 0; k < keys; k += 1) lastNoteInColumn[k] = firstTime - 1000000.0;

        for (int i = 0; i < noteRows.Count; i += 1)
        {
            var row = noteRows[i];
            double time = row.Time;

            for (int k = 0; k < keys; k += 1)
            {
                NoteType noteType = row.Data[k];
                if (!InterludeTypes.IsPlayableNoteType(noteType))
                {
                    continue;
                }

                double jackDelta = (time - lastNoteInColumn[k]) / rateValue;
                var item = data[i][k];
                item.J = MsToJackBpm(jackDelta);

                int handLo = k < handSplit ? 0 : handSplit;
                int handHi = k < handSplit ? (handSplit - 1) : (keys - 1);

                double sl = 0.0;
                double sr = 0.0;

                for (int handK = handLo; handK <= handHi; handK += 1)
                {
                    if (handK == k)
                    {
                        continue;
                    }

                    double trillDelta = (time - lastNoteInColumn[handK]) / rateValue;
                    double trillValue = MsToStreamBpm(trillDelta) * JackCompensation(jackDelta, trillDelta);
                    if (handK < k)
                    {
                        sl = Math.Max(sl, trillValue);
                    }
                    else
                    {
                        sr = Math.Max(sr, trillValue);
                    }
                }

                item.SL = F32(sl);
                item.SR = F32(sr);
                item.Total = NoteDifficultyTotal(item);
            }

            for (int k = 0; k < keys; k += 1)
            {
                NoteType noteType = row.Data[k];
                if (InterludeTypes.IsPlayableNoteType(noteType))
                {
                    lastNoteInColumn[k] = time;
                }
            }
        }

        return data;
    }
}
