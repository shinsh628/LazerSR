// Port of vendor/leoblack/patterns/categorise.js

namespace LazerSR.DanCalculator.Patterns;

public static class Categorise
{
    private static bool IsHybridChart(LeoBlackPatternCluster? primary, LeoBlackPatternCluster? secondary)
    {
        _ = primary;
        _ = secondary;
        return false;
    }

    public static string CategoriseChart(int keys, List<LeoBlackPatternCluster> orderedClusters, double svAmount)
    {
        _ = keys;
        _ = svAmount;

        if (orderedClusters.Count == 0)
        {
            return "Uncategorised";
        }

        double firstImportance = orderedClusters[0].Importance;
        var important = new List<LeoBlackPatternCluster>();
        foreach (var cluster in orderedClusters)
        {
            if ((cluster.Importance / firstImportance) > PatternsConfig.IMPORTANT_CLUSTER_RATIO)
            {
                important.Add(cluster);
            }
            else
            {
                break;
            }
        }

        var cluster1 = important[0];
        var cluster2 = important.Count > 1 ? important[1] : null;

        bool hybrid = IsHybridChart(cluster1, cluster2);
        bool tech = cluster1.Mixed;

        string name;
        if (cluster1.SpecificTypes.Count > 0 && cluster1.SpecificTypes[0].Ratio > 0.05)
        {
            name = cluster1.SpecificTypes[0].Name;
        }
        else if (
            cluster1.SpecificTypes.Count >= 2
            && cluster1.SpecificTypes[0].Name == "Jumpstream"
            && cluster1.SpecificTypes[1].Name == "Handstream")
        {
            double a1 = cluster1.SpecificTypes[0].Ratio;
            double a2 = cluster1.SpecificTypes[1].Ratio;
            name = (a2 / a1) > PatternsConfig.CATEGORY_JS_HS_SECONDARY_RATIO
                ? "Jumpstream/Handstream"
                : cluster1.Pattern;
        }
        else
        {
            name = cluster1.Pattern;
        }

        return $"{name}{(hybrid ? " Hybrid" : "")}{(tech ? " Tech" : "")}";
    }
}
