// SunnyShim — the sunny-substitution boundary (PORTING.md §"Sunny substitution").
//
// Wherever LeoBlack JS calls runSunnyEstimatorFromText / rework/sunnyAlgorithm.js
// this project calls SunnyShim.Run instead: our own LazerSR.SunnyCalculator
// (vanilla — no universal/personal diff, no temp-nerf tail) produces `star`, and
// the ported ReworkEstimatorUtils.EstDiff produces the label.
//
// This is the ONE file below the project root allowed to reference osu.Game
// (it needs the .osu decoder + mania beatmap conversion). Everything else in
// LazerSR.DanCalculator stays osu.Game-free.

using System.Globalization;
using System.Text;
using LazerSR.SunnyCalculator;
using LazerSR.SunnyCalculator.Tuning;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Beatmaps;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mania.Objects;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;

namespace LazerSR.DanCalculator.Estimators;

public static class SunnyShim
{
    private static readonly ManiaRuleset mania = new();
    private static readonly object gate = new();
    private static bool decoderRegistered;

    private static void EnsureDecoder()
    {
        if (decoderRegistered) return;
        lock (gate)
        {
            if (decoderRegistered) return;
            LegacyBeatmapDecoder.Register();
            decoderRegistered = true;
        }
    }

    /// <summary>
    /// Stand-in for <c>sunnyEstimator.js</c> / <c>runSunnyEstimatorFromText</c>.
    /// </summary>
    /// <param name="osuText">raw .osu file text</param>
    /// <param name="speedRate">JS <c>options.speedRate</c> (DT/HT rate)</param>
    /// <param name="odFlag">
    /// JS numeric OD override. Non-numeric flags ("HR"/"EZ") are dropped by the
    /// estimator callers before reaching here. Currently unused by our sunny calc
    /// (OD does not feed the sunny strain model). PORT NOTE: LeoBlack sunny reads
    /// it only for its own HitLeniency term, which our port of that lives in
    /// <c>ManiaDifficultyPreprocessor</c> and reads the chart OD directly.
    /// </param>
    /// <param name="cvtFlag">"HO" (hold-off) / "IN" (invert) / null</param>
    /// <param name="withGraph">emit the strain timeline</param>
    public static SunnyResult Run(string osuText, double speedRate = 1.0, double? odFlag = null,
                                  string? cvtFlag = null, bool withGraph = false)
    {
        EnsureDecoder();

        IBeatmap decoded;
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(osuText)))
        using (var reader = new LineBufferedReader(stream))
            decoded = osu.Game.Beatmaps.Formats.Decoder.GetDecoder<osu.Game.Beatmaps.Beatmap>(reader).Decode(reader);

        var working = new FlatWorkingBeatmap(decoded);
        var playable = (ManiaBeatmap)working.GetPlayableBeatmap(mania.RulesetInfo, System.Array.Empty<Mod>());

        int keyCount = playable.TotalColumns;

        int holds = 0, total = 0;
        foreach (var ho in playable.HitObjects)
        {
            total++;
            if (ho is HoldNote) holds++;
        }
        double lnRatio = total > 0 ? (double)holds / total : 0;

        IBeatmap toRate = cvtFlag == "HO" ? StripHolds(playable) : playable;
        Mod[] mods = BuildRateMods(speedRate);

        // `toRate` is an already-converted, already-playable ManiaBeatmap. Use the
        // IBeatmap-direct entry — feeding it back through SunnyRunner /
        // ManiaDifficultyCalculator would re-run GetPlayableBeatmap and re-convert
        // an already-mania chart, which collapses the strain input to a near-constant.
        double star = SunnyConstants.WithIsolatedDiff(
            new double[SunnyConstants.Count],
            forceVanillaTail: true,
            () => new SunnyManiaDifficultyCalculator().Calculate(toRate, mods));

        (double[] Times, double[] Values)? graph = null;
        if (withGraph)
        {
            var timeline = SunnyConstants.WithIsolatedDiff(
                new double[SunnyConstants.Count],
                forceVanillaTail: true,
                () => new SunnyManiaDifficultyCalculator().GetStrainTimeline(toRate, mods));
            var times = new double[timeline.Length];
            var values = new double[timeline.Length];
            for (int i = 0; i < timeline.Length; i++)
            {
                times[i] = timeline[i].Time;
                values[i] = timeline[i].Strain;
            }
            graph = (times, values);
        }

        string estDiff = ReworkEstimatorUtils.EstDiff(star, lnRatio, keyCount, useExtended: false,
            enableAlwaysShowLNDifficulty: false);

        return new SunnyResult
        {
            Star = star,
            LnRatio = lnRatio,
            ColumnCount = keyCount,
            EstDiff = estDiff,
            Graph = graph,
        };
    }

    private static ManiaBeatmap StripHolds(ManiaBeatmap source)
    {
        // Hold-off: every hold becomes a tap at its head time. Shallow-clone the
        // beatmap and swap only the hit-object list (IBeatmap.Clone is a
        // MemberwiseClone, so the HitObjects list must be replaced, not mutated).
        var clone = (ManiaBeatmap)source.Clone();
        var newObjects = new List<ManiaHitObject>(source.HitObjects.Count);
        foreach (var ho in source.HitObjects)
        {
            if (ho is HoldNote hold)
            {
                var note = new Note { StartTime = hold.StartTime, Column = hold.Column, Samples = hold.GetNodeSamples(0) };
                note.ApplyDefaults(source.ControlPointInfo, source.Difficulty);
                newObjects.Add(note);
            }
            else
            {
                newObjects.Add(ho);
            }
        }
        clone.HitObjects = newObjects;
        return clone;
    }

    private static Mod[] BuildRateMods(double speedRate)
    {
        if (Math.Abs(speedRate - 1.0) < 1e-9) return System.Array.Empty<Mod>();

        if (speedRate > 1.0)
        {
            var dt = new ManiaModDoubleTime();
            dt.SpeedChange.Value = Math.Clamp(speedRate, dt.SpeedChange.MinValue, dt.SpeedChange.MaxValue);
            return new Mod[] { dt };
        }

        var ht = new ManiaModHalfTime();
        ht.SpeedChange.Value = Math.Clamp(speedRate, ht.SpeedChange.MinValue, ht.SpeedChange.MaxValue);
        return new Mod[] { ht };
    }

    /// <summary>JS numeric coercion helper kept here so estimator ports can drop object? OD flags.</summary>
    public static double? AsOdDouble(object? odFlag)
    {
        if (odFlag is double d) return d;
        if (odFlag is int i) return i;
        if (odFlag is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
            return p;
        return null;
    }
}
