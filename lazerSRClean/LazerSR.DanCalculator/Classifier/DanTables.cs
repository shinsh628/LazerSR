// Partial port of mania-hub live-backend/src/dan/chart-classifier.ts
//
// PORT NOTE: also lives in chart-classifier.ts; P7 will dedupe.
// Only the pieces dan-credit.ts / creditedDanFor depends on are ported here:
// buildTableLevels + danTableCeilingFor + danTableFloorFor. The full
// formatDanTableLabel / danTableLabelFor family is P7's (chart-classifier)
// scope.

using System.Text.RegularExpressions;
using LazerSR.DanCalculator.Classifier;
using LazerSR.DanCalculator.Intervals;

namespace LazerSR.DanCalculator.Classifier;

internal static class DanTables
{
    internal static readonly Regex TABLE_TIER_PATTERN = new(@"^(.+?) (low|mid/low|mid/high|mid|high)$", RegexOptions.Compiled);
    private static readonly Regex TRAILING_NUMBER = new(@"(\d+)$", RegexOptions.Compiled);
    private static readonly Regex REGULAR_OR_LN_PREFIX = new(@"^(Regular|LN)\s+", RegexOptions.Compiled);
    private static readonly Regex WORD_LN_PREFIX = new(@"^\S+\s+LN\s+", RegexOptions.Compiled);

    // JS Math.round: round half toward +Infinity.
    private static double JsRound(double v) => Math.Floor(v + 0.5);

    internal static string TableLabelForBase(string @base)
        => WORD_LN_PREFIX.Replace(REGULAR_OR_LN_PREFIX.Replace(@base, ""), "").ToLowerInvariant();

    /// <summary>
    /// Label a rawDan that lives on a leoblack interval-table scale (6K/7K rice
    /// and LN verdicts store rawDan as the table's 0-indexed level, e.g. 7K
    /// Gamma = 11). The table's own level names ARE those communities' ladders
    /// ("Regular 7" -> "7", "LN Gamma" -> "gamma"); the 4K greek ladder never
    /// applies outside 4K, so labeling these from parseDan misnames everything
    /// past 10th. Returns null when no table covers the keymode/side.
    /// </summary>
    private static string? FormatDanTableLabel(double rawDan, string side, int keyCount, string scale)
    {
        var table = TableFor(side, keyCount);
        if (table == null) return null;
        var levels = TableLevels(table);
        if (levels.Count == 0) return null;
        double level = Math.Min(levels[^1].Level, Math.Max(levels[0].Level, JsRound(rawDan)));
        int idx = levels.FindIndex(candidate => candidate.Level == level);
        if (idx < 0) return null;
        var entry = levels[idx];
        double offset = Math.Max(-0.5, Math.Min(0.5, rawDan - level));
        // Player credits and averages are continuous, so their suffixes use
        // parseDan's bands and read like the 4K chips. A classifier verdict is one
        // of the table's five named tiers at offsets -.4/-.2/0/.2/.4; use the
        // midpoints between those anchors so "LN Mystery low" round-trips to
        // mystery-- instead of being relabeled mystery- on the evidence surface.
        string? variant = scale == "verdict"
            ? offset < -0.3 ? "--" : offset < -0.1 ? "-" : offset < 0.1 ? null : offset < 0.3 ? "+" : "++"
            : offset <= -0.45 ? "--" : offset <= -0.25 ? "-" : offset < 0.1 ? null : offset < 0.26 ? "+" : "++";
        return $"{TableLabelForBase(entry.Base)}{variant ?? ""}";
    }

    public static string? DanTableLabelFor(double rawDan, string side, int keyCount)
        => FormatDanTableLabel(rawDan, side, keyCount, "credit");

    // Each ladder speaks its own community's language. 4K rice runs 1-10 then the
    // Reform greek levels (parseDan), 4K LN is numeric 1-17 and never goes greek
    // (parseLnDan). 6K/7K rawDans arrive on their leoblack table scale, whose
    // level names are the real Sunny/Jinjin ladders (7K past 10th = Gamma,
    // Azimuth, Zenith, Stellium; 6K LN = Terra..Finish) - the 4K greek ladder
    // ("alpha") does not exist there, so those keymodes label from their table.
    public static string DanLabelFor(double rawDan, string side, int keyCount)
    {
        if (keyCount != 4)
        {
            var tableLabel = DanTableLabelFor(rawDan, side, keyCount);
            if (tableLabel != null) return tableLabel;
        }
        if (side == "ln" && keyCount == 4)
        {
            var (label, variant, _) = LnDan.ParseLnDan(rawDan);
            return $"{label}{variant ?? ""}";
        }
        var parsed = Labels.ParseDan(rawDan);
        return $"{parsed.Label}{parsed.Variant ?? ""}";
    }

    /// <summary>The label of an analyzer verdict, preserving the source table's tier bands.</summary>
    public static string? DanTableVerdictLabelFor(double rawDan, string side, int keyCount)
        => FormatDanTableLabel(rawDan, side, keyCount, "verdict");

    /// <summary>
    /// The inverse of danTableLabelFor's level naming: the table level a bare
    /// ladder label sits on ("gamma" -> 11 on 7K rice, "terra" -> 10 on 6K LN).
    /// Takes a bare label with no +/- variant; returns null when no table covers
    /// the keymode/side or the label is not one of its levels.
    /// </summary>
    public static double? DanTableLevelForLabel(string label, string side, int keyCount)
    {
        var table = TableFor(side, keyCount);
        if (table == null) return null;
        string wanted = label.Trim().ToLowerInvariant();
        int idx = TableLevels(table).FindIndex(candidate => TableLabelForBase(candidate.Base) == wanted);
        return idx < 0 ? (double?)null : TableLevels(table)[idx].Level;
    }

    internal readonly record struct TableLevel(string Base, double Level);

    // Interval tables list five tier rows per dan in ascending order; bases carry
    // their level as a trailing number ("Regular 7", "LN 15") or continue past the
    // last numbered dan by position ("LN Finish" after "LN 10" -> 11).
    private static List<TableLevel> BuildTableLevels((double Lo, double Hi, string Name)[] table)
    {
        var levels = new List<TableLevel>();
        double lastNumeric = 0;
        foreach (var (_, _, name) in table)
        {
            var match = TABLE_TIER_PATTERN.Match(name);
            string @base = match.Success ? match.Groups[1].Value : name;
            if (levels.Any(entry => entry.Base == @base)) continue;
            var numberMatch = TRAILING_NUMBER.Match(@base);
            if (numberMatch.Success)
            {
                lastNumeric = double.Parse(numberMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                levels.Add(new TableLevel(@base, lastNumeric));
            }
            else
            {
                lastNumeric += 1;
                levels.Add(new TableLevel(@base, lastNumeric));
            }
        }
        return levels;
    }

    private static readonly Dictionary<(double Lo, double Hi, string Name)[], List<TableLevel>> Cache = new();

    internal static List<TableLevel> TableLevels((double Lo, double Hi, string Name)[] table)
    {
        if (!Cache.TryGetValue(table, out var levels))
        {
            levels = BuildTableLevels(table);
            Cache[table] = levels;
        }
        return levels;
    }

    private static (double Lo, double Hi, string Name)[]? TableFor(string side, int keyCount)
    {
        var tables = DanIndex.For(keyCount);
        if (tables == null) return null;
        // JS reads `.default` directly (not the extended-aware resolver).
        return side == "ln" ? tables.Ln?.Default : tables.Rc.Default;
    }

    public static double? DanTableCeilingFor(string side, int keyCount)
    {
        // 4K LN speaks its own numeric ladder rather than a leoblack table, but it
        // does end: 17 (Yeehee) is the last course, so the table's "> Lnlism LN 17
        // high" sentinel lands on the same last-level + 0.5 the other keymodes use.
        // 4K RC keeps going into the greek levels, so it still has no ceiling.
        if (keyCount == 4) return side == "ln" ? LnDan.LN_LADDER_TOP + 0.5 : (double?)null;
        var table = TableFor(side, keyCount);
        if (table == null) return null;
        var levels = TableLevels(table);
        if (levels.Count == 0) return null;
        return levels[^1].Level + 0.5;
    }

    /// <summary>
    /// The lowest rawDan a credited clear may clamp to. 0.5 on the 4K ladders,
    /// whose labelers clamp the level to 1, so it prints as the first level's
    /// minus band; the leoblack tables open at level 0 (the Normal Kyu band), so
    /// theirs is 0.
    /// </summary>
    public static double DanTableFloorFor(string side, int keyCount)
    {
        if (keyCount == 4) return 0.5;
        var table = TableFor(side, keyCount);
        if (table == null) return 0.5;
        var levels = TableLevels(table);
        if (levels.Count == 0) return 0.5;
        return Math.Max(0, levels[0].Level - 0.5);
    }
}
