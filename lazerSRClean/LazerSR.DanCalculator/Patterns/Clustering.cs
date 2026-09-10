// Port of vendor/leoblack/patterns/clustering.js
// mania-hub local patch (PORT_NOTES): mixed-BPM pool no longer averages in the
// MsPerBeat 0 sentinel; only "timed" windows (>= CLUSTER_TIMED_MIN_MSPB) vote.

using System.Globalization;
using LazerSR.DanCalculator.Vibro;

namespace LazerSR.DanCalculator.Patterns;

// Matches vendor/leoblack/patterns/service.d.ts LeoBlackPatternCluster.
// Also satisfies P3's ILeoBlackVibroCluster (Vibro/LongjackVibro.cs).
public sealed class LeoBlackPatternCluster : ILeoBlackVibroCluster
{
    public string Pattern { get; set; } = "";
    public List<(string Name, double Ratio)> SpecificTypes { get; set; } = new();
    public double RatingMultiplier { get; set; }
    public double BPM { get; set; }
    public bool Mixed { get; set; }
    public double Amount { get; set; }

    public double Importance => Amount * RatingMultiplier * BPM;

    double ILeoBlackVibroCluster.Bpm => BPM;
    IReadOnlyList<(string Name, double Ratio)>? ILeoBlackVibroCluster.SpecificTypes => SpecificTypes;

    public string Format(double rate = 1.0)
    {
        string name = (SpecificTypes.Count > 0 && SpecificTypes[0].Ratio >= PatternsConfig.CLUSTER_SPECIFIC_NAME_MIN_RATIO)
            ? SpecificTypes[0].Name
            : Pattern;
        if (Mixed)
        {
            return $"~{Clustering.JsRound(BPM * rate).ToString(CultureInfo.InvariantCulture)}BPM Mixed {name}";
        }
        return $"{Clustering.JsRound(BPM * rate).ToString(CultureInfo.InvariantCulture)}BPM {name}";
    }
}

public static class Clustering
{
    // JS Math.round: round half toward +Infinity.
    internal static double JsRound(double x) => Math.Floor(x + 0.5);

    private static double PatternAmount(List<(double Start, double End)> sortedStartsEnds)
    {
        double totalTime = 0;
        double currentStart = sortedStartsEnds[0].Start;
        double currentEnd = sortedStartsEnds[0].End;

        foreach (var (start, end) in sortedStartsEnds)
        {
            if (currentEnd < end)
            {
                totalTime += currentEnd - currentStart;
                currentStart = start;
                currentEnd = end;
            }
            else
            {
                currentEnd = Math.Max(currentEnd, end);
            }
        }

        totalTime += currentEnd - currentStart;
        return totalTime;
    }

    // Density/Inverse windows carry MsPerBeat 0 as a "no meaningful tempo"
    // sentinel (resolvedMspb in findPatterns.js), and any window under
    // CLUSTER_TIMED_MIN_MSPB is the same physical artifact measured instead of
    // zeroed: LN tails landing milliseconds before the next head, grace notes,
    // stacked rows. Neither says anything about tempo, so neither votes.
    private static bool IsTimedMspb(double value)
    {
        return value >= PatternsConfig.CLUSTER_TIMED_MIN_MSPB;
    }

    private sealed class ClusterBuilder
    {
        public double SumMs;
        public int TimedCount;
        public double OriginalMsPerBeat;
        public int Count;
        public double BPM;

        public ClusterBuilder(double value)
        {
            bool timed = IsTimedMspb(value);
            SumMs = timed ? value : 0;
            TimedCount = timed ? 1 : 0;
            OriginalMsPerBeat = value;
            Count = 1;
            BPM = 0; // JS: null until calculate(); Value is only read post-calculate.
        }

        public void Add(double v)
        {
            Count += 1;
            if (IsTimedMspb(v))
            {
                SumMs += v;
                TimedCount += 1;
            }
        }

        public void Calculate()
        {
            // Only timed windows vote; a pool with none stays BPM 0.
            BPM = TimedCount == 0 ? 0 : JsRound(60000.0 / (SumMs / TimedCount));
        }

        public double Value => BPM;
    }

    private static List<(FoundPattern P, ClusterBuilder C)> AssignClusters(List<FoundPattern> patterns)
    {
        var bpmsNonMixed = new List<ClusterBuilder>();
        var bpmsMixed = new Dictionary<string, ClusterBuilder>();

        ClusterBuilder AddToCluster(double msPerBeat)
        {
            foreach (var c in bpmsNonMixed)
            {
                if (Math.Abs(c.OriginalMsPerBeat - msPerBeat) < PatternsConfig.BPM_CLUSTER_THRESHOLD)
                {
                    c.Add(msPerBeat);
                    return c;
                }
            }
            var nc = new ClusterBuilder(msPerBeat);
            bpmsNonMixed.Add(nc);
            return nc;
        }

        ClusterBuilder AddToMixedCluster(string pattern, double value)
        {
            if (bpmsMixed.TryGetValue(pattern, out var c))
            {
                c.Add(value);
                return c;
            }
            var nc = new ClusterBuilder(value);
            bpmsMixed[pattern] = nc;
            return nc;
        }

        var patternsWithClusters = new List<(FoundPattern, ClusterBuilder)>();
        foreach (var p in patterns)
        {
            var c = p.Mixed ? AddToMixedCluster(p.Pattern, p.MsPerBeat) : AddToCluster(p.MsPerBeat);
            patternsWithClusters.Add((p, c));
        }

        foreach (var c in bpmsNonMixed) c.Calculate();
        foreach (var c in bpmsMixed.Values) c.Calculate();

        return patternsWithClusters;
    }

    private sealed class Group
    {
        public string Pattern = "";
        public bool Mixed;
        public double Bpm;
        public List<(FoundPattern M, ClusterBuilder C)> Data = new();
    }

    private static List<LeoBlackPatternCluster> SpecificClusters(List<(FoundPattern P, ClusterBuilder C)> patternsWithClusters, string modeTag)
    {
        var map = new Dictionary<string, Group>();
        var order = new List<Group>();

        foreach (var (p, c) in patternsWithClusters)
        {
            string key = $"{p.Pattern}@@{(p.Mixed ? 1 : 0)}@@{c.Value.ToString(CultureInfo.InvariantCulture)}";
            if (!map.TryGetValue(key, out var g))
            {
                g = new Group { Pattern = p.Pattern, Mixed = p.Mixed, Bpm = c.Value };
                map[key] = g;
                order.Add(g);
            }
            g.Data.Add((p, c));
        }

        var outList = new List<LeoBlackPatternCluster>();
        foreach (var group in order)
        {
            var startsEnds = group.Data
                .Select(x => (Start: x.M.Start, End: x.M.End))
                .OrderBy(se => se.Start)
                .ToList();

            int dataCount = group.Data.Count;
            var counter = new Dictionary<string, int>();
            var seen = new List<string>();
            foreach (var (m, _) in group.Data)
            {
                if (m.SpecificType != null)
                {
                    if (!counter.ContainsKey(m.SpecificType)) seen.Add(m.SpecificType);
                    counter[m.SpecificType] = counter.GetValueOrDefault(m.SpecificType) + 1;
                }
            }

            var specificTypes = seen
                .Select(name => (Name: name, Ratio: (double)counter[name] / dataCount))
                .OrderByDescending(x => x.Ratio)
                .ToList();

            string? dominantSpecific = specificTypes.Count != 0 ? specificTypes[0].Name : null;
            double amount = startsEnds.Count != 0 ? PatternAmount(startsEnds) : 0;

            outList.Add(new LeoBlackPatternCluster
            {
                Pattern = group.Pattern,
                SpecificTypes = specificTypes,
                RatingMultiplier = PatternsDef.ResolveRatingMultiplier(group.Pattern, dominantSpecific, modeTag),
                BPM = group.Bpm,
                Mixed = group.Mixed,
                Amount = amount,
            });
        }

        bool hasDW = outList.Any(c => c.Pattern == "Density" || c.Pattern == "Wildcard");
        if (hasDW && PatternsConfig.RELEASE_WITH_DW_MULTIPLIER != 1.0)
        {
            foreach (var c in outList)
            {
                if (c.SpecificTypes.Any(t => t.Name == "Release" && t.Ratio > 0))
                {
                    c.RatingMultiplier *= PatternsConfig.RELEASE_WITH_DW_MULTIPLIER;
                }
            }
        }

        return outList;
    }

    public static List<LeoBlackPatternCluster> CalculateClusteredPatterns(List<FoundPattern> patterns, string modeTag = "Mix")
    {
        var pwc = AssignClusters(patterns);
        return SpecificClusters(pwc, modeTag);
    }
}
