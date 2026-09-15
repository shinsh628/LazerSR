// Port of vendor/leoblack/patterns/service.js

using LazerSR.DanCalculator.Parser;

namespace LazerSR.DanCalculator.Patterns;

/// <summary>analyzePatternFromText return shape: { report, topFiveClusters }.</summary>
public sealed record PatternAnalysisResult(LeoBlackPatternReport Report, List<LeoBlackPatternCluster> TopFiveClusters);

public static class PatternService
{
    // CROSS-AGENT (P1): LazerSR.DanCalculator.Parser.PatternOsuParser.ParseOsuManiaFromText(string) -> Patterns.Chart
    // (faithful 1:1 of vendor/leoblack/parser/patternOsuParser.js parseOsuManiaFromText).
    public static PatternAnalysisResult AnalyzePatternFromText(string osuText, double rate = 1.0)
    {
        _ = rate; // void rate;
        var chart = PatternOsuParser.ParseOsuManiaFromText(osuText);
        var report = Summary.FromChart(chart);

        return new PatternAnalysisResult(report, report.Clusters.Take(5).ToList());
    }

    // Exposes FindPatterns' raw per-window matches (Pattern/SpecificType/Start/End,
    // one entry per matched 8-row scan position) directly — the pre-clustering
    // stage AnalyzePatternFromText immediately collapses into one Category string.
    // Callers that want "where in time is this pattern, and how much of the chart
    // does it cover" (rather than a single dominant label) read this instead.
    // No change to the existing clustering/categorisation pipeline.
    public static List<FoundPattern> FindPatternWindows(string osuText)
    {
        var chart = PatternOsuParser.ParseOsuManiaFromText(osuText);
        return FindPatterns.Find(chart);
    }
}
