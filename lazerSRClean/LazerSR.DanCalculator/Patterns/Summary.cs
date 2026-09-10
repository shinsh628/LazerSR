// Port of vendor/leoblack/patterns/summary.js

using LazerSR.DanCalculator.Vibro;

namespace LazerSR.DanCalculator.Patterns;

/// <summary>fromChart(chart) return shape. Matches vendor/leoblack/patterns/service.d.ts LeoBlackPatternReport.
/// Also satisfies P3's ILeoBlackPatternReport (Vibro/LongjackVibro.cs).</summary>
public sealed class LeoBlackPatternReport : ILeoBlackPatternReport
{
    public List<LeoBlackPatternCluster> Clusters { get; set; } = new();
    public string Category { get; set; } = "";
    public double LNPercent { get; set; }
    public double HBRowRatio { get; set; }
    public string ModeTag { get; set; } = "Mix";
    public double SVAmount { get; set; }
    public double Duration { get; set; }

    IReadOnlyList<ILeoBlackVibroCluster>? ILeoBlackPatternReport.Clusters => Clusters;

    public List<LeoBlackPatternCluster> ImportantClusters
    {
        get
        {
            if (Clusters.Count == 0) return new List<LeoBlackPatternCluster>();
            double importance = Clusters[0].Importance;
            var outList = new List<LeoBlackPatternCluster>();
            foreach (var c in Clusters)
            {
                if (c.Importance / importance > PatternsConfig.IMPORTANT_CLUSTER_RATIO) outList.Add(c);
                else break;
            }
            return outList;
        }
    }
}

public static class Summary
{
    private static readonly HashSet<string> LN_CORE_PATTERNS = new() { "Coordination", "Density", "Wildcard" };

    private static double HbRowRatio(Chart chart)
    {
        var rows = chart.Notes;
        if (rows.Count == 0) return 0;

        int hbRows = 0;
        foreach (var row in rows)
        {
            var data = row.Data;
            bool hasHead = data.Any(n => n == NoteType.HOLDHEAD);
            bool hasNormal = data.Any(n => n == NoteType.NORMAL);
            if (hasHead && hasNormal)
            {
                hbRows += 1;
            }
        }

        return (double)hbRows / rows.Count;
    }

    private static string ResolveModeTag(double lnRatio, double hbRatio)
    {
        string tag = PatternsConfig.ModeTagFromLnRatio(lnRatio);
        if (tag != "Mix") return tag;
        if (hbRatio >= PatternsConfig.HB_ROW_RATIO_THRESHOLD) return "HB";
        return "Mix";
    }

    public static LeoBlackPatternReport FromChart(Chart chart)
    {
        double lnRatio = Primitives.LnPercent(chart);
        double hbRatio = HbRowRatio(chart);
        string modeTag = ResolveModeTag(lnRatio, hbRatio);

        var patterns = FindPatterns.Find(chart);
        if (modeTag == "RC")
        {
            patterns = patterns.Where(p => !LN_CORE_PATTERNS.Contains(p.Pattern)).ToList();
        }

        var clusters = Clustering.CalculateClusteredPatterns(patterns, modeTag)
            .Where(c => c.BPM > 25 || c.BPM == 0)
            .OrderByDescending(c => c.Amount)
            .ToList();

        bool CanBePruned(LeoBlackPatternCluster cluster)
        {
            foreach (var other in clusters)
            {
                if (other.Pattern == cluster.Pattern && other.Amount * 0.5 > cluster.Amount && other.BPM > cluster.BPM)
                {
                    return true;
                }
            }
            return false;
        }

        var filtered = clusters.Where(c => !CanBePruned(c)).ToList();

        var prunedClusters = new List<LeoBlackPatternCluster>();
        foreach (var pattern in PatternsDef.CORE_PATTERN_LIST)
        {
            prunedClusters.AddRange(filtered.Where(c => c.Pattern == pattern).Take(3));
        }
        prunedClusters = prunedClusters.OrderByDescending(c => c.Importance).ToList();

        double svAmount = Primitives.SvTime(chart);
        string category = Categorise.CategoriseChart(chart.Keys, prunedClusters, svAmount);

        return new LeoBlackPatternReport
        {
            Clusters = prunedClusters,
            Category = category,
            LNPercent = lnRatio,
            HBRowRatio = hbRatio,
            ModeTag = modeTag,
            SVAmount = svAmount,
            Duration = chart.LastNote - chart.FirstNote,
        };
    }
}
