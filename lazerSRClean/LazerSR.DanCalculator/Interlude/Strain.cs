// Port of vendor/leoblack/interlude/strain.js

using static LazerSR.DanCalculator.Interlude.NumberUtils;

namespace LazerSR.DanCalculator.Interlude;

public sealed class FingerStrainRow
{
    public double[] NotesV1 = Array.Empty<double>();
    public double[] StrainV1Notes = Array.Empty<double>();
}

public sealed class HandStrainRow
{
    public double[] Strains = Array.Empty<double>();
    public double[] Left = Array.Empty<double>();
    public double[] Right = Array.Empty<double>();
}

public static class Strain
{
    private static readonly double STRAIN_SCALE = F32(0.01626);
    private static readonly double STRAIN_TIME_CAP = F32(200.0);

    private static Func<double, double, double, double> CreateStrainFunction(double halfLife)
    {
        double decayRate = F32(Math.Log(0.5) / halfLife);

        return (value, input, delta) =>
        {
            double clampedDelta = Math.Min(STRAIN_TIME_CAP, delta);
            double decay = F32(Math.Exp(decayRate * clampedDelta));
            double timeCapDecay = delta > STRAIN_TIME_CAP
                ? F32(Math.Exp(decayRate * (delta - STRAIN_TIME_CAP)))
                : 1.0;
            double a = F32(value * timeCapDecay);
            double b = F32(input * input * STRAIN_SCALE);
            return F32(b - (b - a) * decay);
        };
    }

    private static readonly Func<double, double, double, double> StrainBurst = CreateStrainFunction(1575.0);
    private static readonly Func<double, double, double, double> StrainStamina = CreateStrainFunction(60000.0);

    public static List<FingerStrainRow> CalculateFingerStrains(double rate, List<InterludeRow> noteRows, List<NoteRating[]> noteDifficulty)
    {
        if (noteRows == null || noteRows.Count == 0)
        {
            return new List<FingerStrainRow>();
        }

        double rateValue = double.IsFinite(rate) && rate > 0 ? rate : 1.0;
        int keys = noteRows[0].Data.Length;

        var lastNoteInColumn = new double[keys];
        var strainV1 = new double[keys];

        var output = new List<FingerStrainRow>();

        for (int i = 0; i < noteRows.Count; i += 1)
        {
            var row = noteRows[i];
            double offset = row.Time;

            var notesV1 = new double[keys];
            var rowStrainV1 = new double[keys];

            for (int k = 0; k < keys; k += 1)
            {
                if (!InterludeTypes.IsPlayableNoteType(row.Data[k]))
                {
                    continue;
                }

                notesV1[k] = Nz(noteDifficulty[i][k].Total);
                strainV1[k] = StrainBurst(
                    strainV1[k],
                    notesV1[k],
                    (offset - lastNoteInColumn[k]) / rateValue);
                rowStrainV1[k] = strainV1[k];
                lastNoteInColumn[k] = offset;
            }

            output.Add(new FingerStrainRow
            {
                NotesV1 = notesV1,
                StrainV1Notes = rowStrainV1,
            });
        }

        return output;
    }

    public static List<HandStrainRow> CalculateHandStrains(double rate, List<InterludeRow> noteRows, List<NoteRating[]> noteDifficulty)
    {
        if (noteRows == null || noteRows.Count == 0)
        {
            return new List<HandStrainRow>();
        }

        double rateValue = double.IsFinite(rate) && rate > 0 ? rate : 1.0;
        int keys = noteRows[0].Data.Length;
        int handSplit = Layout.KeysOnLeftHand(keys);

        var lastNoteInColumn = new double[keys][];
        for (int k = 0; k < keys; k += 1) lastNoteInColumn[k] = new[] { 0.0, 0.0, 0.0 };
        var output = new List<HandStrainRow>();

        for (int i = 0; i < noteRows.Count; i += 1)
        {
            var row = noteRows[i];
            double offset = row.Time;

            double leftHandBurst = 0.0;
            double leftHandStamina = 0.0;
            double rightHandBurst = 0.0;
            double rightHandStamina = 0.0;

            var strains = new double[keys];

            for (int k = 0; k < keys; k += 1)
            {
                if (!InterludeTypes.IsPlayableNoteType(row.Data[k]))
                {
                    continue;
                }

                double d = Nz(noteDifficulty[i][k].Total);

                if (k < handSplit)
                {
                    for (int handK = 0; handK < handSplit; handK += 1)
                    {
                        double prevBurst = lastNoteInColumn[handK][0];
                        double prevStamina = lastNoteInColumn[handK][1];
                        double prevTime = lastNoteInColumn[handK][2];
                        leftHandBurst = Math.Max(leftHandBurst, StrainBurst(prevBurst, d, (offset - prevTime) / rateValue));
                        leftHandStamina = Math.Max(leftHandStamina, StrainStamina(prevStamina, d, (offset - prevTime) / rateValue));
                    }
                }
                else
                {
                    for (int handK = handSplit; handK < keys; handK += 1)
                    {
                        double prevBurst = lastNoteInColumn[handK][0];
                        double prevStamina = lastNoteInColumn[handK][1];
                        double prevTime = lastNoteInColumn[handK][2];
                        rightHandBurst = Math.Max(rightHandBurst, StrainBurst(prevBurst, d, (offset - prevTime) / rateValue));
                        rightHandStamina = Math.Max(rightHandStamina, StrainStamina(prevStamina, d, (offset - prevTime) / rateValue));
                    }
                }
            }

            for (int k = 0; k < keys; k += 1)
            {
                if (!InterludeTypes.IsPlayableNoteType(row.Data[k]))
                {
                    continue;
                }

                if (k < handSplit)
                {
                    lastNoteInColumn[k] = new[] { leftHandBurst, leftHandStamina, offset };
                    strains[k] = F32(leftHandBurst * 0.875 + leftHandStamina * 0.125);
                }
                else
                {
                    lastNoteInColumn[k] = new[] { rightHandBurst, rightHandStamina, offset };
                    strains[k] = F32(rightHandBurst * 0.875 + rightHandStamina * 0.125);
                }
            }

            output.Add(new HandStrainRow
            {
                Strains = strains,
                Left = new[] { leftHandBurst, leftHandStamina },
                Right = new[] { rightHandBurst, rightHandStamina },
            });
        }

        return output;
    }
}
