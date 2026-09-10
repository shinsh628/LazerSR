// Port of vendor/leoblack/interlude/difficulty.js

using static LazerSR.DanCalculator.Interlude.NumberUtils;

namespace LazerSR.DanCalculator.Interlude;

public sealed class InterludeDifficultyResult
{
    public List<NoteRating[]> NoteDifficulty = new();
    public List<FingerStrainRow> Strains = new();
    public List<int> Variety = new();
    public List<HandStrainRow> Hands = new();
    public double Overall;
}

public static class Difficulty
{
    private static readonly double CURVE_POWER = F32(0.6);
    private static readonly double CURVE_SCALE = F32(0.4056);
    private static readonly double MOST_IMPORTANT_NOTES = F32(2500.0);

    private static double WeightingCurve(double x)
    {
        return F32(0.002 + Math.Pow(x, 4.0));
    }

    public static double WeightedOverallDifficulty(IEnumerable<double>? data)
    {
        var values = (data ?? Enumerable.Empty<double>()).ToList();
        values.Sort(); // ascending, matches (a,b)=>a-b
        if (values.Count == 0)
        {
            return 0.0;
        }

        double length = F32(values.Count);
        double weight = 0.0;
        double total = 0.0;

        for (int i = 0; i < values.Count; i += 1)
        {
            double x = Math.Max(0.0, (F32(i) + MOST_IMPORTANT_NOTES - length) / MOST_IMPORTANT_NOTES);
            double w = WeightingCurve(x);
            weight += w;
            total += Nz(values[i]) * w;
        }

        if (!double.IsFinite(weight) || weight <= 0)
        {
            return 0.0;
        }

        double transformed = Math.Pow(total / weight, CURVE_POWER) * CURVE_SCALE;
        return double.IsFinite(transformed) ? F32(transformed) : 0.0;
    }

    public static InterludeDifficultyResult CalculateInterludeDifficulty(double rate, List<InterludeRow> noteRows)
    {
        if (noteRows == null || noteRows.Count == 0)
        {
            return new InterludeDifficultyResult();
        }

        var noteDifficulty = NoteDifficulty.CalculateNoteRatings(rate, noteRows);
        var variety = Variety.CalculateVariety(rate, noteRows, noteDifficulty);
        var strains = Strain.CalculateFingerStrains(rate, noteRows, noteDifficulty);
        var hands = Strain.CalculateHandStrains(rate, noteRows, noteDifficulty);

        var strainValues = new List<double>();
        for (int i = 0; i < strains.Count; i += 1)
        {
            var row = strains[i].StrainV1Notes ?? Array.Empty<double>();
            for (int k = 0; k < row.Length; k += 1)
            {
                double v = Nz(row[k]);
                if (v > 0.0)
                {
                    strainValues.Add(v);
                }
            }
        }

        double overall = WeightedOverallDifficulty(strainValues);

        return new InterludeDifficultyResult
        {
            NoteDifficulty = noteDifficulty,
            Strains = strains,
            Variety = variety,
            Hands = hands,
            Overall = double.IsFinite(overall) ? overall : 0.0,
        };
    }
}
