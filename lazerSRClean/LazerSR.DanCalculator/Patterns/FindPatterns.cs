// Port of vendor/leoblack/patterns/findPatterns.js
// mania-hub re-pin PR #48: O(n^2) slice-copy loop -> bounded 8-element head window.

namespace LazerSR.DanCalculator.Patterns;

/// <summary>A found pattern window as appendFoundPattern builds it. MsPerBeat 0 is the
/// Density/Inverse "no meaningful tempo" sentinel (resolvedMspb) excluded from cluster BPM averaging.</summary>
public sealed class FoundPattern
{
    public string Pattern = "";
    public string? SpecificType;
    public bool Mixed;
    public double Start;
    public double End;
    public double MsPerBeat;
}

public static class FindPatterns
{
    private static (double N, string Name)? PickSpecificFirst(List<(string Name, PatternMatcher Fn)> specificList, List<PrimitiveRow> remaining)
    {
        foreach (var (name, p) in specificList)
        {
            double n = p(remaining);
            if (n != 0) return (n, name);
        }
        return null;
    }

    private static List<(double N, string Name)> PickSpecificAll(List<(string Name, PatternMatcher Fn)> specificList, List<PrimitiveRow> remaining)
    {
        var matched = new List<(double N, string Name)>();
        foreach (var (name, p) in specificList)
        {
            double n = p(remaining);
            if (n != 0) matched.Add((n, name));
        }
        return matched;
    }

    private static double ResolvedMspb(string pattern, string? specificType, double meanMspb)
    {
        if (pattern == CorePattern.Density && specificType == "Inverse")
        {
            return 0.0;
        }
        return meanMspb;
    }

    private static void AppendFoundPattern(List<FoundPattern> results, string pattern, string? specificType, double n2, List<PrimitiveRow> remaining, double lastNote)
    {
        int n2i = (int)n2;
        var d = remaining.Take(n2i).ToList();
        double meanMspb = d.Sum(x => x.MsPerBeat) / d.Count;
        bool mixed = !d.All(x => Math.Abs(x.MsPerBeat - meanMspb) < PatternsConfig.PATTERN_STABILITY_THRESHOLD);

        double start = remaining[0].Time;
        double end;

        if (pattern == CorePattern.Jacks)
        {
            double endCandidate = n2i < remaining.Count ? remaining[n2i].Time : lastNote;
            end = Math.Max(remaining[0].Time + remaining[0].MsPerBeat * 0.5, endCandidate);
        }
        else
        {
            end = n2i < remaining.Count ? remaining[n2i].Time : lastNote;
        }

        results.Add(new FoundPattern
        {
            Pattern = pattern,
            SpecificType = specificType,
            Mixed = mixed,
            Start = start,
            End = end,
            MsPerBeat = ResolvedMspb(pattern, specificType, meanMspb),
        });
    }

    private static void AppendCoreMatches(List<FoundPattern> results, string pattern, double coreN, List<(string Name, PatternMatcher Fn)> specificList, List<PrimitiveRow> remaining, double lastNote)
    {
        if (coreN == 0) return;

        if (PatternsConfig.ENABLE_MULTI_LABEL_SAME_WINDOW)
        {
            var matched = PickSpecificAll(specificList, remaining);
            if (matched.Count == 0)
            {
                AppendFoundPattern(results, pattern, null, coreN, remaining, lastNote);
                return;
            }

            foreach (var (m, specificType) in matched)
            {
                AppendFoundPattern(results, pattern, specificType, Math.Max(coreN, m), remaining, lastNote);
            }
            return;
        }

        var picked = PickSpecificFirst(specificList, remaining);
        if (picked == null)
        {
            AppendFoundPattern(results, pattern, null, coreN, remaining, lastNote);
            return;
        }

        var (mm, specificType2) = picked.Value;
        AppendFoundPattern(results, pattern, specificType2, Math.Max(coreN, mm), remaining, lastNote);
    }

    // ponytail: matchers only read a bounded head window (max 8) and use xs.length only as a
    // window guard; appendFoundPattern reads at most index 5. So an 8-element head-window slice
    // per iteration is bit-for-bit equivalent to the old full-tail view.
    private const int MATCHER_WINDOW = 8;

    private static List<FoundPattern> Matches(SpecificPatterns specificPatterns, double lastNote, List<PrimitiveRow> primitives)
    {
        var results = new List<FoundPattern>();
        int start = 0;

        while (start < primitives.Count)
        {
            var remaining = primitives.Skip(start).Take(MATCHER_WINDOW).ToList();
            AppendCoreMatches(results, CorePattern.Stream, PatternsDef.CORE_STREAM(remaining), specificPatterns.Stream, remaining, lastNote);
            AppendCoreMatches(results, CorePattern.Chordstream, PatternsDef.CORE_CHORDSTREAM(remaining), specificPatterns.Chordstream, remaining, lastNote);
            AppendCoreMatches(results, CorePattern.Jacks, PatternsDef.CORE_JACKS(remaining), specificPatterns.Jack, remaining, lastNote);
            AppendCoreMatches(results, CorePattern.Coordination, PatternsDef.CORE_COORDINATION(remaining), specificPatterns.Coordination, remaining, lastNote);
            AppendCoreMatches(results, CorePattern.Density, PatternsDef.CORE_DENSITY(remaining), specificPatterns.Density, remaining, lastNote);
            AppendCoreMatches(results, CorePattern.Wildcard, PatternsDef.CORE_WILDCARD(remaining), specificPatterns.Wildcard, remaining, lastNote);

            start += 1;
        }

        return results;
    }

    public static List<FoundPattern> Find(Chart chart)
    {
        var primitives = Primitives.CalculatePrimitives(chart);
        SpecificPatterns keymodePatterns;

        if (chart.Keys == 4) keymodePatterns = PatternsDef.SPECIFIC_4K();
        else if (chart.Keys == 7) keymodePatterns = PatternsDef.SPECIFIC_7K();
        else keymodePatterns = PatternsDef.SPECIFIC_OTHER();

        return Matches(keymodePatterns, chart.LastNote - chart.FirstNote, primitives);
    }
}
