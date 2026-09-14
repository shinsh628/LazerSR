// Port of mania-hub live-backend/src/features/player-skills.ts
//   shouldCheckRateVibro (~2622) / chartVibroAtRate (~2626)
//
// This is the player-RATING gate (reject the play outright, damp its accuracy, or
// narrowly excuse it via judgement evidence) — a different purpose from
// ChartClassifier's own vibro pass, which only adjusts the CHART's displayed dan.
// The two are independent call sites over the same VibroSections/VibroDetection
// primitives; this file does not read or reuse ChartClassifier's output.

using System;
using LazerSR.DanCalculator.Beatmap;
using LazerSR.DanCalculator.Classifier;
using LazerSR.DanCalculator.PlayerRating;

namespace LazerSR.DanCalculator.Vibro;

public static class RateVibroChecker
{
    /// <summary>
    /// player-skills.ts shouldCheckRateVibro. 4K always checks — the section policy
    /// (UsesSectionVibro/AnalyzeVibroSections) applies even at 1.0x, to know whether
    /// vibro sections should be excluded from the chart regardless of rate. Other
    /// keymodes only check a non-1.0x play on a chart that is not PP-trusted.
    /// </summary>
    public static bool ShouldCheck(int keyCount, double rate, bool hasPpTrust)
        => keyCount == 4 || (Math.Abs(rate - 1) > 1e-9 && !hasPpTrust);

    /// <summary>
    /// player-skills.ts chartVibroAtRate. <paramref name="ratedMap"/> is the chart the
    /// play is rated against (post-Invert if applicable), parsed at its native
    /// timestamps — rate is a playback-speed multiplier, not baked into note times.
    /// Returns null on internal failure (mirrors the JS try/catch -&gt; null), which the
    /// caller treats as "no rate-vibro data" rather than blocking the upload.
    /// </summary>
    public static RateVibroResult? Check(
        ManiaBeatmap ratedMap,
        double rate,
        bool hasPpTrust,
        bool baseVibro,
        VibroClearInput quality,
        double? odOverride)
    {
        try
        {
            bool sectionVibro = VibroSections.UsesSectionVibro(ratedMap);
            VibroAnalysis? analysis = sectionVibro ? VibroSections.AnalyzeVibroSections(ratedMap, rate) : null;

            // Strong judgements on a consistent vibro pattern do not prove it was
            // played without vibro. This exception only handles rate-induced
            // dense-chord detections on a clean, PP-backed base chart. Explicit
            // walls, jack streams, isolated jacks and rolls cannot be overridden by
            // accuracy.
            VibroClearEvidence? clearEvidence = null;
            if (analysis?.Status == VibroStatus.Excluded && rate > 1 && hasPpTrust
                && VibroClearEvidenceModule.HasOnlyClearEvidencePatterns(analysis.Sections))
            {
                var evidence = VibroClearEvidenceModule.AssessVibroClear(quality, odOverride ?? ratedMap.Od);
                if (evidence != null
                    && DanEligibility.InspectChartDanEligibility(ratedMap).Eligible
                    && VibroSections.AnalyzeVibroSections(ratedMap, 1).Status == VibroStatus.Clean)
                {
                    clearEvidence = evidence;
                }
            }

            bool vibro = analysis != null
                ? analysis.Status == VibroStatus.Excluded && clearEvidence == null
                : (!hasPpTrust && baseVibro) || (Math.Abs(rate - 1) > 1e-9 && VibroDetection.DetectRateVibro(ratedMap, rate));

            return new RateVibroResult
            {
                RateVibro = vibro,
                IsAdjusted = analysis?.Status == VibroStatus.Adjusted,
                JudgementShare = analysis?.JudgementShare ?? 0,
                IsClearEvidence = clearEvidence != null,
            };
        }
        catch
        {
            return null;
        }
    }
}
