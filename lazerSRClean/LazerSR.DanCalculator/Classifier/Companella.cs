// Port of mania-hub live-backend/src/dan/companella.ts
//
// Companella is LeoBlack's ONNX dan model: a 10-feature MLP over the eight
// MinaCalc skillsets plus Interlude SR and Sunny SR. Mixed reaches for it on
// low-band 4K RC charts and the RC half of LN hybrids under 9 stars
// (see mixedEstimator.js). Inference is async in JS purely for the dynamic
// import + the session.run promise, so this lives beside the sync classifyChart
// and callers opt in.
//
// PORT NOTE (async -> sync bodies): the ported ONNX/MSD calls are synchronous;
// the public methods stay `async Task<>` to keep the two-pass contract and the
// call-site ergonomics identical to the TS.

using LazerSR.DanCalculator.Beatmap;
using LazerSR.DanCalculator.Estimators;
using LazerSR.DanCalculator.Interlude;
using LazerSR.DanCalculator.Msd;
using LazerSR.DanCalculator.Vibro;

namespace LazerSR.DanCalculator.Classifier;

public sealed class CompanellaFeatureInput
{
    public string OsuText = "";
    public double Rate;
    public int KeyCount;
    public double SunnyStar;
    /// <summary>
    /// Raw (not LN-tail-blended) MinaCalc values, when the caller already has
    /// them. Saves a second MinaCalc pass; chart-analysis stores MSD anyway.
    /// </summary>
    public Dictionary<string, double>? MsdValues;
}

/// <summary>JS `options` bag of classifyChartWithCompanella.</summary>
public sealed class CompanellaClassifyOptions
{
    public Dictionary<string, double>? MsdValues;
    public bool SkipCompanella;
}

public static class Companella
{
    /// <summary>Companella only ever runs on the 4K path Mixed gates it behind.</summary>
    public static bool IsCompanellaSupported(int keyCount) => keyCount == 4;

    /// <summary>
    /// Compute the two inputs Mixed does not already have (MinaCalc MSD and
    /// Interlude SR) and run the model. Returns null when the chart is out of
    /// scope or any stage fails, which leaves callers on their unrefined Azusa or
    /// Sunny estimate.
    /// </summary>
    public static Task<CompanellaEstimate?> ComputeCompanellaEstimateAsync(CompanellaFeatureInput input)
    {
        string osuText = input.OsuText;
        double rate = input.Rate;
        int keyCount = input.KeyCount;
        double sunnyStar = input.SunnyStar;
        if (!IsCompanellaSupported(keyCount) || !double.IsFinite(sunnyStar))
            return Task.FromResult<CompanellaEstimate?>(null);

        try
        {
            // computeMsd defaults to lnTailTaps:false, i.e. the raw MinaCalc values
            // rather than our LN-tail blend. That is deliberate: the model was
            // trained against stock MSD, so the blend would shift every hold-heavy
            // chart off the distribution it learned.
            Dictionary<string, double>? msdValues = input.MsdValues
                ?? Msd.Msd.ComputeMsd(osuText, new MsdOptions { Rate = rate, KeyCount = keyCount })?.Values;
            double interludeStar = InterludeIndex.CalculateInterludeStar(osuText, rate);
            if (msdValues == null) return Task.FromResult<CompanellaEstimate?>(null);

            var estimate = CompanellaEstimator.ClassifyCompanellaDifficulty(msdValues, interludeStar, sunnyStar);
            return Task.FromResult<CompanellaEstimate?>(estimate);
        }
        catch (Exception error)
        {
            Msd.Msd.MsdChartErrorFallback(error); // rethrows MsdThreadUnavailableException
            return Task.FromResult<CompanellaEstimate?>(null);
        }
    }

    /// <summary>
    /// Obtain marathon MSD before classifying, then resolve any Companella plan
    /// using the resulting star value. Other charts obtain MSD only if they need
    /// Companella. skipCompanella keeps the benchmark's marathon correction active
    /// while isolating the optional fusion.
    /// </summary>
    public static async Task<ChartClassification> ClassifyChartWithCompanellaAsync(
        ManiaBeatmap map, string osuText, ClassifyChartInput? input = null, CompanellaClassifyOptions? options = null)
    {
        input ??= new ClassifyChartInput();
        options ??= new CompanellaClassifyOptions();

        double rate = Labels.GetInputRate(input);
        var prepared = input.AdjustVibro ? VibroSections.PrepareVibroChart(osuText, rate, map) : (PrepareVibroChartResult?)null;
        string effectiveText = prepared?.OsuText ?? osuText;
        var effectiveMap = prepared?.Analysis.Status == VibroStatus.Adjusted
            ? ManiaBeatmapParser.Parse(effectiveText)
            : map;
        bool marathon = ChartClassifier.IsMarathonCorrectionCandidate(effectiveMap);
        Dictionary<string, double>? msdValues = options.MsdValues ?? input.MarathonMsdValues;
        if (marathon && msdValues == null)
        {
            try
            {
                msdValues = Msd.Msd.ComputeMsd(effectiveText, new MsdOptions { Rate = rate, KeyCount = map.KeyCount })?.Values;
            }
            catch (Exception error)
            {
                Msd.Msd.MsdChartErrorFallback(error);
                msdValues = null;
            }
        }

        var classifyInput = input.Clone();
        classifyInput.MarathonMsdValues = msdValues;
        var first = ChartClassifier.ClassifyChart(map, osuText, classifyInput);
        if (options.SkipCompanella || !first.CompanellaPending || first.SunnySr == null) return first;
        // A failed marathon MSD pass cannot supply Companella either. Do not retry
        // the same calculator a second time during this request.
        if (marathon && msdValues == null) return first;

        var companella = await ComputeCompanellaEstimateAsync(new CompanellaFeatureInput
        {
            OsuText = effectiveText,
            Rate = rate,
            KeyCount = map.KeyCount,
            SunnyStar = first.SunnySr.Value,
            MsdValues = msdValues,
        }).ConfigureAwait(false);
        if (companella == null) return first;

        var refined = classifyInput.Clone();
        refined.Companella = companella;
        return ChartClassifier.ClassifyChart(map, osuText, refined);
    }
}
