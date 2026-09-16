using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LazerSR.DanCalculator;
using LazerSR.DanCalculator.Classifier;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mania.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Screens.Play;
using osu.Game.Skinning;

namespace LazerSR.Hook.Widgets;

/// <summary>
/// Difficulty-info skin widget. The dan verdict comes from the ported mania-hub
/// classifier (<see cref="DanClassifier"/>) — 4K RC/LN halves, 6K/7K sunny tables,
/// tier / boundary / vibro. Runs the Companella (ONNX) refinement pass on every
/// map/mod change, same cadence as the old sunny→threshold readout.
/// </summary>
public class DanInfoWidget : CompositeDrawable, ISerialisableDrawable
{
    public bool UsesFixedAnchor { get; set; }

    // Shown for every 4/6/7K chart, RC or LN (see ChartClassification.
    // PatternDifficulties). Names are the pattern analyzer's own names (Trills,
    // Chordjacks, Inverse, ...), shown verbatim — no display renaming.
    //
    // Selection (2026-09-16): every pattern scores TimeShare x RelativeIntensity
    // ("how much this pattern actually pushes the map's felt difficulty", not
    // just how much time it covers — matches the local pattern-debug-viewer
    // tool's ranking). The top score always shows; the 2nd/3rd only show if
    // their score is at least this % of the top one, so 1-3 patterns show
    // depending on how spread out the chart's patterns are.
    [SettingSource("패턴 컷오프 비율 (%)")]
    public BindableNumber<float> PatternCutoffPercent { get; } =
        new BindableFloat(50) { MinValue = 0, MaxValue = 100, Precision = 1 };

    [Resolved(canBeNull: true)]
    private GameplayState? gameplayState { get; set; }

    [Resolved(canBeNull: true)]
    private IBindable<WorkingBeatmap>? workingBeatmap { get; set; }

    [Resolved(canBeNull: true)]
    private IBindable<RulesetInfo>? ruleset { get; set; }

    [Resolved(canBeNull: true)]
    private IBindable<IReadOnlyList<Mod>>? mods { get; set; }

    // Row 1 is the dan verdict (both RC and LN halves when the chart has
    // both — no separate "primary only" line repeating one of them); row 2 is
    // the pattern summary.
    private OsuSpriteText danLine = null!;
    private OsuSpriteText row2Line = null!;
    private CancellationTokenSource? cts;
    private ModSettingChangeTracker? _modTracker;
    private ChartClassification? lastClassification;

    // Temporary real-device debug bridge (2026-09-16): dumps the full,
    // unfiltered PatternDifficulties list to a fixed local file every time a
    // map is (re)classified, so the timeline-bar visualization site can be
    // refreshed on request just by asking Claude to re-read this file — no
    // in-game trigger, no server involved.
    private static readonly string PatternDumpPath =
        Path.Combine(Path.GetTempPath(), "lazersr_pattern_debug.json");

    private sealed class PatternDumpEntry
    {
        public string Pattern { get; set; } = "";
        public string SpecificType { get; set; } = "";
        public double TimeShare { get; set; }
        public double RelativeIntensity { get; set; }
        public List<double[]> Intervals { get; set; } = new();
    }

    private sealed class PatternDump
    {
        public string Title { get; set; } = "";
        public string Version { get; set; } = "";
        public int KeyCount { get; set; }
        public double DurationMs { get; set; }
        public List<PatternDumpEntry> Patterns { get; set; } = new();
    }

    public DanInfoWidget()
    {
        Width = 220;
        Height = 40;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black,
                Alpha = 0.55f,
            },
            danLine = new OsuSpriteText
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Y = -9,
                Font = OsuFont.Default.With(size: 15f, weight: FontWeight.SemiBold),
                Text = "Dan Info",
                Alpha = 0.6f,
            },
            row2Line = new OsuSpriteText
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Y = 9,
                Font = OsuFont.Default.With(size: 11f),
                Alpha = 0.6f,
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // Slider tweaks re-render the already-classified map instantly instead
        // of waiting for the next map/mod change to re-run the heavy classify.
        PatternCutoffPercent.BindValueChanged(_ => renderCurrent());

        if (gameplayState != null)
        {
            triggerRecalculate();
            return;
        }

        workingBeatmap?.BindValueChanged(_ => triggerRecalculate());

        mods?.BindValueChanged(e =>
        {
            _modTracker?.Dispose();
            _modTracker = new ModSettingChangeTracker(e.NewValue);
            _modTracker.SettingChanged += _ => triggerRecalculate();
            triggerRecalculate();
        }, true);
    }

    private static double GetRate(IReadOnlyList<Mod>? mods)
    {
        if (mods == null) return 1.0;
        double rate = 1.0;
        foreach (var mod in mods)
            if (mod is IApplicableToRate r)
                rate = r.ApplyToRate(0, rate);
        return rate;
    }

    private void triggerRecalculate()
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        var token = cts.Token;

        var gs = gameplayState;
        var wb = workingBeatmap?.Value;
        var rs = ruleset?.Value;
        double rate = GetRate(mods?.Value);
        double? starRating = wb?.BeatmapInfo.StarRating;

        Task.Run(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();

                IBeatmap playable;
                if (gs != null)
                    playable = gs.Beatmap;
                else
                {
                    if (wb == null) return;
                    playable = wb.GetPlayableBeatmap(rs ?? wb.BeatmapInfo.Ruleset, mods?.Value ?? Array.Empty<Mod>(), token);
                }

                token.ThrowIfCancellationRequested();

                if (playable is not ManiaBeatmap)
                {
                    publish(token, null);
                    return;
                }

                string osuText = encodeToOsu(playable);
                token.ThrowIfCancellationRequested();

                var input = new ClassifyChartInput
                {
                    Rate = rate,
                    StarRating = double.IsFinite(starRating ?? double.NaN) ? starRating : null,
                };
                var classification = await DanClassifier
                    .ClassifyChartWithCompanellaAsync(osuText, input)
                    .ConfigureAwait(false);

                if (token.IsCancellationRequested) return;

                dumpPatternDebug(classification, wb, playable);
                publish(token, classification);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] DanInfoWidget recalc failed: {e}");
            }
        }, token);
    }

    private static void dumpPatternDebug(ChartClassification? classification, WorkingBeatmap? wb, IBeatmap playable)
    {
        // HookLog.Write is a release no-op, so failures here are otherwise
        // invisible — write a diagnostic payload to the same fixed path in
        // every branch (null result, exception) instead of just bailing, so
        // "why is the dump missing" is answerable from the file alone.
        try
        {
            if (classification == null)
            {
                File.WriteAllText(PatternDumpPath, JsonSerializer.Serialize(new { note = "classification was null (unsupported ruleset/beatmap, or classify failed upstream)" }));
                return;
            }

            if (classification.PatternDifficulties == null)
            {
                File.WriteAllText(PatternDumpPath, JsonSerializer.Serialize(new
                {
                    note = "PatternDifficulties was null (BuildPatternDifficulty returned null — see classification.Warnings)",
                    keyCount = classification.KeyCount,
                    supported = classification.Supported,
                    warnings = classification.Warnings,
                }));
                return;
            }

            double duration = playable.HitObjects.Count > 0
                ? playable.HitObjects.Max(h => h.GetEndTime())
                : 0;

            var dump = new PatternDump
            {
                Title = wb?.BeatmapInfo.Metadata.Title ?? "",
                Version = wb?.BeatmapInfo.DifficultyName ?? "",
                KeyCount = classification.KeyCount,
                DurationMs = duration,
                Patterns = classification.PatternDifficulties.Select(p => new PatternDumpEntry
                {
                    Pattern = p.Pattern,
                    SpecificType = p.SpecificType,
                    TimeShare = p.TimeShare,
                    RelativeIntensity = p.RelativeIntensity,
                    Intervals = p.Intervals.Select(iv => new[] { iv.Start, iv.End }).ToList(),
                }).ToList(),
            };

            File.WriteAllText(PatternDumpPath, JsonSerializer.Serialize(dump));
        }
        catch (Exception e)
        {
            try { File.WriteAllText(PatternDumpPath, JsonSerializer.Serialize(new { note = "dump threw", error = e.ToString() })); }
            catch { /* if we can't even write the error, nothing more we can do */ }
        }
    }

    private void publish(CancellationToken token, ChartClassification? classification)
    {
        Schedule(() =>
        {
            if (token.IsCancellationRequested) return;
            lastClassification = classification;
            renderCurrent();
        });
    }

    // Re-renders lastClassification with the widget's current settings —
    // called both after a fresh classify and when the cutoff slider moves, so
    // a slider tweak doesn't need a map re-select to take effect.
    private void renderCurrent()
    {
        var (main, row2) = lastClassification != null
            ? format(lastClassification)
            : (string.Empty, string.Empty);

        if (string.IsNullOrEmpty(main))
        {
            danLine.Text = "N/A";
            row2Line.Text = string.Empty;
            danLine.Alpha = 0.6f;
            row2Line.Alpha = 0f;
            return;
        }

        danLine.Text = main;
        row2Line.Text = row2;
        danLine.Alpha = 1f;
        row2Line.Alpha = string.IsNullOrEmpty(row2) ? 0f : 0.85f;
    }

    private static string encodeToOsu(IBeatmap beatmap)
    {
        using var writer = new StringWriter();
        new LegacyBeatmapEncoder(beatmap, null, null).Encode(writer);
        return writer.ToString();
    }

    private (string Main, string Row2) format(ChartClassification c)
    {
        var primary = c.Primary;
        if (!c.Supported || primary == null)
            return (string.Empty, string.Empty);

        // Row 1: both halves whenever both exist — no separate "primary only"
        // line repeating one of them (that was pure duplication when the two
        // differed, and equally redundant when they happened to match).
        string main = c.Rc != null && c.Ln != null
            ? $"RC {half(c.Rc)}  LN {half(c.Ln)}"
            : $"{(primary.Kind == "ln" ? "LN" : "RC")}  {half(primary)}";

        // Row 2: VIBRO warning has the row to itself when it fires (the
        // pattern summary is always empty on a vibro chart anyway, so there's
        // nothing it would be crowding out). Otherwise the top named patterns
        // joined against the per-map difficulty curve (see ChartClassification.
        // PatternDifficulties) — every 4/6/7K chart, RC or LN.
        string row2 = c.Vibro ? "⚠VIBRO" : (buildPatternSummary(c.PatternDifficulties) ?? string.Empty);

        return (main, row2);
    }

    // Ranks every pattern by TimeShare x RelativeIntensity — "how much this
    // pattern actually pushes the map's felt difficulty", not just how much
    // time it covers (same ranking as the local pattern-debug-viewer tool).
    // The top score always shows; #2/#3 only show if their score is at least
    // PatternCutoffPercent of the top score, so 1-3 patterns show depending on
    // how spread out the chart's patterns are. The % shown is still the plain
    // TimeShare, not the score. Names are shown verbatim (the pattern
    // analyzer's own naming).
    private string? buildPatternSummary(List<ChartPatternDifficulty>? patterns)
    {
        if (patterns == null || patterns.Count == 0) return null;

        var scored = patterns
            .Select(p => (Pattern: p, Score: p.TimeShare * p.RelativeIntensity))
            .OrderByDescending(x => x.Score)
            .ToList();

        double cutoff = scored[0].Score * (PatternCutoffPercent.Value / 100.0);

        var picked = new List<string>();
        for (int i = 0; i < scored.Count && picked.Count < 3; i += 1)
        {
            // Sorted descending, so once one entry falls below the cutoff every
            // remaining one does too.
            if (i > 0 && scored[i].Score < cutoff) break;
            picked.Add($"{scored[i].Pattern.SpecificType} {Math.Round(scored[i].Pattern.TimeShare * 100):0}%");
        }

        return picked.Count > 0 ? string.Join("  ·  ", picked) : null;
    }

    private static string half(DanVerdictHalf h) => $"{boundaryMark(h.Boundary)}{h.DisplayName}";

    private static string boundaryMark(string? boundary) => boundary switch
    {
        "below" => "< ",
        "above" => "> ",
        _ => string.Empty,
    };

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        cts?.Cancel();
        cts?.Dispose();
        _modTracker?.Dispose();
    }
}
