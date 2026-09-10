using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LazerSR.DanCalculator;
using LazerSR.DanCalculator.Beatmap;
using LazerSR.DanCalculator.Classifier;
using LazerSR.DanCalculator.Credit;
using LazerSR.DanCalculator.PlayerRating;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.Online.API;
using osu.Game.Rulesets.Mods;
using ManiaBeatmap = LazerSR.DanCalculator.Beatmap.ManiaBeatmap;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerSR.Hook.DanRating;

/// <summary>
/// Maps an osu! <see cref="ScoreInfo"/> + its beatmap into the ported
/// <see cref="PlayUploadRecordBuilder"/> input and runs the per-play calc. All the
/// dan/pattern/perf-dan work is in <c>LazerSR.DanCalculator</c>; this file is just
/// the osu!-object → DTO glue. Read-only.
/// </summary>
internal static class DanPlayRecordBuilder
{
    /// <summary>
    /// Build the per-play upload record for a finished mania score, or null when the
    /// play is not dan-rateable (non-mania, HO/NR, adaptive speed, DA that widened
    /// windows, or the calc could not run).
    /// </summary>
    public static async Task<PlayUploadRecord?> BuildAsync(ScoreInfo score, IWorkingBeatmap working)
    {
        if (score.Ruleset.OnlineID != 3) return null;

        var mods = score.APIMods.Select(ToRatingMod).ToList();

        // 1.0x .osu of the played chart. Encode the PLAYABLE (mania-converted)
        // beatmap, not working.Beatmap — the raw decoded one has no mania ruleset
        // context so LegacyBeatmapEncoder writes osu!std X coordinates and the
        // column layout is scrambled (DanInfoWidget encodes the playable for the
        // same reason). Rate is applied by the classifier, not here.
        IBeatmap playable;
        try { playable = working.GetPlayableBeatmap(score.Ruleset, Array.Empty<Mod>()); }
        catch { return null; }
        string osuText = Encode(playable);
        if (string.IsNullOrEmpty(osuText)) return null;

        bool inverse = mods.Any(m => m.Acronym == "IN");
        string ratedText = inverse ? (InvertMod.InvertManiaOsuText(osuText) ?? osuText) : osuText;

        ManiaBeatmap parsed;
        try { parsed = ManiaBeatmapParser.Parse(ratedText); }
        catch { return null; }

        int keyCount = PlayEligibility.GetManiaKeyModCount(mods) ?? parsed.KeyCount;

        var stats = score.Statistics;
        var judgements = new DanJudgements(
            stats.GetValueOrDefault(HitResult.Perfect),
            stats.GetValueOrDefault(HitResult.Great),
            stats.GetValueOrDefault(HitResult.Good),
            stats.GetValueOrDefault(HitResult.Ok),
            stats.GetValueOrDefault(HitResult.Meh),
            stats.GetValueOrDefault(HitResult.Miss));

        double? chartOd = PlayEligibility.ParseOsuOd(ratedText)
                          ?? (working.BeatmapInfo.Difficulty.OverallDifficulty is var od && od > 0 ? od : (double?)null);
        double? lengthSeconds = working.BeatmapInfo.Length > 0 ? working.BeatmapInfo.Length / 1000.0 : (double?)null;

        double rate = 1.0;
        foreach (var m in score.Mods)
            if (m is IApplicableToRate r) rate = r.ApplyToRate(0, rate);

        var input = new ClassifyChartInput
        {
            Rate = 1.0,
            StarRating = double.IsFinite(working.BeatmapInfo.StarRating) ? working.BeatmapInfo.StarRating : (double?)null,
            TotalLength = lengthSeconds,
        };
        var baseLean = await DanClassifier.ClassifyChartLeanAsync(ratedText, input).ConfigureAwait(false);

        LeanChartClassification? atRateLean = null;
        if (Math.Abs(rate - 1.0) > 1e-9 || inverse)
        {
            var atRateInput = new ClassifyChartInput
            {
                Rate = rate,
                StarRating = input.StarRating,
                TotalLength = lengthSeconds,
            };
            atRateLean = await DanClassifier.ClassifyChartLeanAsync(ratedText, atRateInput).ConfigureAwait(false);
        }

        var buildInput = new PlayUploadBuildInput
        {
            OsuText = osuText,
            RatedOsuText = ratedText,
            BeatmapMd5 = score.BeatmapHash,
            BeatmapId = working.BeatmapInfo.OnlineID > 0 ? working.BeatmapInfo.OnlineID : (long?)null,
            KeyCount = keyCount,
            PlayedAt = score.Date.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Mods = mods,
            Score = new RatingScore
            {
                Type = score.OnlineID > 0 ? "solo_score" : null,
                LegacyScoreId = score.LegacyOnlineID > 0 ? score.LegacyOnlineID : (long?)null,
                LegacyTotalScore = score.LegacyTotalScore,
                Mods = mods,
                Statistics = new LazerSR.DanCalculator.Vibro.OsuScoreStatistics
                {
                    count_geki = judgements.Max,
                    count_300 = judgements.Great,
                    count_katu = judgements.Good,
                    count_100 = judgements.Ok,
                    count_50 = judgements.Meh,
                    count_miss = judgements.Miss,
                },
            },
            DisplayedAccuracy = score.Accuracy,
            StableAccuracy = PerformanceDan.StableAccuracy(judgements),
            ScoreV2Accuracy = PerformanceDan.ScoreV2Accuracy(judgements),
            ChartOd = chartOd,
            ChartLengthSeconds = lengthSeconds,
            BaseLean = baseLean,
            AtRateLean = atRateLean,
            TopologyKey = ChartFamily.ChartTopologyKey(parsed),
            IncludeLean = true,
        };

        return PlayUploadRecordBuilder.Build(buildInput);
    }

    private static RatingMod ToRatingMod(APIMod m)
    {
        Dictionary<string, double>? settings = null;
        if (m.Settings.Count > 0)
        {
            settings = new Dictionary<string, double>();
            foreach (var (k, v) in m.Settings)
            {
                if (v is double d) settings[k] = d;
                else if (v is float f) settings[k] = f;
                else if (v is int i) settings[k] = i;
                else if (v is long l) settings[k] = l;
                else if (v is bool b) settings[k] = b ? 1 : 0;
                else if (v is string s && double.TryParse(s, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double p)) settings[k] = p;
            }
            if (settings.Count == 0) settings = null;
        }
        return new RatingMod(m.Acronym, settings);
    }

    private static string Encode(IBeatmap beatmap)
    {
        try
        {
            using var writer = new StringWriter();
            new LegacyBeatmapEncoder(beatmap, null, null).Encode(writer);
            return writer.ToString();
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] DanPlayRecordBuilder.Encode failed: {e.Message}");
            return string.Empty;
        }
    }
}
