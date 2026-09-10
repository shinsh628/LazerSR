// Port of vendor/leoblack/interlude/index.js
//
// PORT NOTE: JS entry is async (fetch-capable). Here it is synchronous and takes
// either raw .osu text or an already-processed OsuFileParser.

using LazerSR.DanCalculator.Parser;

namespace LazerSR.DanCalculator.Interlude;

public static class InterludeIndex
{
    private static double NormalizeRate(double rate)
    {
        double value = rate;
        if (!double.IsFinite(value) || value <= 0)
        {
            return 1.0;
        }
        return value;
    }

    // Single public entry point for Interlude SR calculation.
    public static double CalculateInterludeStar(string source, double rate = 1.0, string? cvtFlag = null)
    {
        double resolvedRate = NormalizeRate(rate);
        var rowsResult = ChartBuilder.BuildInterludeRows(source, cvtFlag);
        var difficulty = Difficulty.CalculateInterludeDifficulty(resolvedRate, rowsResult.Rows);
        double overall = difficulty.Overall;
        return double.IsFinite(overall) ? overall : 0.0;
    }

    public static double CalculateInterludeStar(OsuFileParser source, double rate = 1.0, string? cvtFlag = null)
    {
        double resolvedRate = NormalizeRate(rate);
        var rowsResult = ChartBuilder.BuildInterludeRows(source, cvtFlag);
        var difficulty = Difficulty.CalculateInterludeDifficulty(resolvedRate, rowsResult.Rows);
        double overall = difficulty.Overall;
        return double.IsFinite(overall) ? overall : 0.0;
    }
}
