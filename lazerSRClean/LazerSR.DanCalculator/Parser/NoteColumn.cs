// Port of mania-hub live-backend/vendor/leoblack/parser/noteColumn.js
//
// Shared lane-mapping util for the two .osu parsers. osuFileParser feeds
// integer x (stringToInt) here; for integer x in [0, 512] both the
// `(x * keys) / 512` and `(x / 512.0) * keys` forms evaluate to the exact
// same dyadic rational, so the shared trunc-then-clamp formula is
// bit-identical for both callers.

using System.Globalization;
using System.Text.RegularExpressions;

namespace LazerSR.DanCalculator.Parser;

public static class NoteColumn
{
    public static int XToColumn(double x, int keys)
    {
        // PORT NOTE: JS `Math.trunc((x / 512.0) * keys)` yields a JS number; the
        // callers immediately treat it as an integer lane index. (int) of NaN is 0
        // in .NET (JS would carry NaN through); real .osu input is always finite.
        int col = (int)Math.Truncate((x / 512.0) * keys);
        if (col < 0) col = 0;
        if (col > keys - 1) col = keys - 1;
        return col;
    }
}

/// <summary>
/// JS number-parsing helpers shared by the LeoBlack .osu parsers. Mirrors the
/// `ParseFloatJs`/`ParseIntJs` pattern from <c>Beatmap/ManiaBeatmap.cs</c>, but
/// returns <see cref="double.NaN"/> on failure (the LeoBlack parsers test the
/// result with <c>Number.isNaN</c>, so 0 would be wrong here).
/// </summary>
internal static class JsNum
{
    private static readonly Regex FloatRe =
        new(@"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?", RegexOptions.Compiled);

    private static readonly Regex IntRe =
        new(@"^[+-]?\d+", RegexOptions.Compiled);

    /// <summary>JS <c>Number.parseFloat</c>: leading number, ignores trailing junk, NaN on none.</summary>
    public static double ParseFloat(string? s)
    {
        if (s == null) return double.NaN;
        var m = FloatRe.Match(s.TrimStart());
        return m.Success ? double.Parse(m.Value, CultureInfo.InvariantCulture) : double.NaN;
    }

    /// <summary>JS <c>Number.parseInt(s, 10)</c>: leading integer, NaN on none.</summary>
    public static double ParseInt(string? s)
    {
        if (s == null) return double.NaN;
        var m = IntRe.Match(s.TrimStart());
        return m.Success ? double.Parse(m.Value, CultureInfo.InvariantCulture) : double.NaN;
    }

    /// <summary>JS <c>Math.round</c>: round half toward +Infinity (not banker's rounding).</summary>
    public static double Round(double x) => Math.Floor(x + 0.5);
}
