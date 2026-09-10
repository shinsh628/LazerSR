// Port of vendor/leoblack/interlude/variety.js

using static LazerSR.DanCalculator.Interlude.NumberUtils;

namespace LazerSR.DanCalculator.Interlude;

public static class Variety
{
    private const double VARIETY_WINDOW = 750.0;

    public static List<int> CalculateVariety(double rate, List<InterludeRow> noteRows, List<NoteRating[]> noteDifficulties)
    {
        if (noteRows == null || noteRows.Count == 0)
        {
            return new List<int>();
        }

        double rateValue = double.IsFinite(rate) && rate > 0 ? rate : 1.0;
        int keys = noteRows[0].Data.Length;

        var buckets = new Dictionary<double, int>();
        int front = 0;
        int back = 0;

        var output = new List<int>();

        for (int i = 0; i < noteRows.Count; i += 1)
        {
            double now = noteRows[i].Time;

            while (front < noteRows.Count && noteRows[front].Time < now + VARIETY_WINDOW * rateValue)
            {
                var frontRow = noteRows[front].Data;
                for (int k = 0; k < keys; k += 1)
                {
                    if (!InterludeTypes.IsPlayableNoteType(frontRow[k]))
                    {
                        continue;
                    }

                    double strainBucket = RoundToEven(Nz(noteDifficulties[front][k].Total) / 5.0);
                    buckets[strainBucket] = buckets.GetValueOrDefault(strainBucket) + 1;
                }
                front += 1;
            }

            while (back < i && noteRows[back].Time < now - VARIETY_WINDOW * rateValue)
            {
                var backRow = noteRows[back].Data;
                for (int k = 0; k < keys; k += 1)
                {
                    if (!InterludeTypes.IsPlayableNoteType(backRow[k]))
                    {
                        continue;
                    }

                    double strainBucket = RoundToEven(Nz(noteDifficulties[back][k].Total) / 5.0);
                    int next = buckets.GetValueOrDefault(strainBucket) - 1;
                    if (next <= 0)
                    {
                        buckets.Remove(strainBucket);
                    }
                    else
                    {
                        buckets[strainBucket] = next;
                    }
                }
                back += 1;
            }

            output.Add(buckets.Count);
        }

        return output;
    }
}
