// Port of vendor/leoblack/interlude/numberUtils.js

namespace LazerSR.DanCalculator.Interlude;

public static class NumberUtils
{
    /// <summary>Math.fround — round a double to the nearest float and back.</summary>
    public static double F32(double value) => (float)value;

    /// <summary>JS `Number(x) || 0` for a double: NaN and +/-0 collapse to 0, everything else (incl. Infinity) passes.</summary>
    internal static double Nz(double x) => double.IsNaN(x) ? 0.0 : x;

    // Match .NET/F# round behavior (banker's rounding).
    public static double RoundToEven(double value)
    {
        if (!double.IsFinite(value))
        {
            return value;
        }

        double sign = value < 0 ? -1 : 1;
        double absValue = Math.Abs(value);
        double floor = Math.Floor(absValue);
        double frac = absValue - floor;

        if (frac < 0.5)
        {
            return sign * floor;
        }
        if (frac > 0.5)
        {
            return sign * (floor + 1);
        }

        return sign * (floor % 2 == 0 ? floor : floor + 1);
    }
}
