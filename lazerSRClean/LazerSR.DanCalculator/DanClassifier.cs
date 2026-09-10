// Public entry point for the ported mania-hub dan classification pipeline.
// See PORTING.md §"Entry point contract".
//
// Mirrors live-backend/src/dan/chart-classifier.ts classifyChart() (sync) and
// live-backend/src/dan/companella.ts classifyChartWithCompanella() (async).

using LazerSR.DanCalculator.Beatmap;
using LazerSR.DanCalculator.Classifier;
using LazerSR.DanCalculator.Features;

namespace LazerSR.DanCalculator;

public static class DanClassifier
{
    /// <summary>
    /// Sync path — no Companella (ONNX). Parses the raw <c>.osu</c> text and
    /// classifies. Matches chart-classifier.ts <c>classifyChart()</c>.
    /// </summary>
    public static ChartClassification ClassifyChart(string osuText, ClassifyChartInput? input = null)
        => ChartClassifier.ClassifyChart(ManiaBeatmapParser.Parse(osuText), osuText, input);

    /// <summary>
    /// Async path — runs Companella when the sync verdict asked for it.
    /// Matches companella.ts <c>classifyChartWithCompanella()</c>.
    /// </summary>
    public static Task<ChartClassification> ClassifyChartWithCompanellaAsync(string osuText, ClassifyChartInput? input = null)
        => Companella.ClassifyChartWithCompanellaAsync(ManiaBeatmapParser.Parse(osuText), osuText, input);

    /// <summary>
    /// The serialisable per-chart record the player-rating server consumes.
    /// Mirrors chart-analysis.ts: <c>leanClassification(classifyChartWithCompanella(...),
    /// computeNoteBpm(osuText), motionFeatures(map.notes, map.keyCount))</c>.
    /// </summary>
    public static async Task<LeanChartClassification> ClassifyChartLeanAsync(string osuText, ClassifyChartInput? input = null)
    {
        var map = ManiaBeatmapParser.Parse(osuText);
        var classification = await Companella.ClassifyChartWithCompanellaAsync(map, osuText, input).ConfigureAwait(false);
        return LeanClassification.From(
            classification,
            NoteBpm.ComputeNoteBpm(osuText),
            MotionFeaturesCalculator.Compute(map.Notes, map.KeyCount));
    }

    /// <summary>Sync variant of <see cref="ClassifyChartLeanAsync"/> (no Companella / ONNX).</summary>
    public static LeanChartClassification ClassifyChartLean(string osuText, ClassifyChartInput? input = null)
    {
        var map = ManiaBeatmapParser.Parse(osuText);
        var classification = ChartClassifier.ClassifyChart(map, osuText, input);
        return LeanClassification.From(
            classification,
            NoteBpm.ComputeNoteBpm(osuText),
            MotionFeaturesCalculator.Compute(map.Notes, map.KeyCount));
    }
}
