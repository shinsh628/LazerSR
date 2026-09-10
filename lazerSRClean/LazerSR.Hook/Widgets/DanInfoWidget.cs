using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LazerSR.DanCalculator;
using LazerSR.DanCalculator.Classifier;
using LazerSR.Hook.Calculators;
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
using osu.Game.Screens.Play;
using osu.Game.Skinning;

namespace LazerSR.Hook.Widgets;

/// <summary>
/// Difficulty-info skin widget. The dan verdict comes from the ported mania-hub
/// classifier (<see cref="DanClassifier"/>) — 4K RC/LN halves, 6K/7K sunny tables,
/// tier / boundary / confidence / vibro. The sync path is used (no Companella
/// ONNX): for low 4K RC charts that leaves the unrefined sunny fallback, which is
/// fine for a passive readout.
/// </summary>
public class DanInfoWidget : CompositeDrawable, ISerialisableDrawable
{
    public bool UsesFixedAnchor { get; set; }

    [Resolved(canBeNull: true)]
    private GameplayState? gameplayState { get; set; }

    [Resolved(canBeNull: true)]
    private IBindable<WorkingBeatmap>? workingBeatmap { get; set; }

    [Resolved(canBeNull: true)]
    private IBindable<RulesetInfo>? ruleset { get; set; }

    [Resolved(canBeNull: true)]
    private IBindable<IReadOnlyList<Mod>>? mods { get; set; }

    // Local bindable so osu! framework auto-unbinds on dispose (prevents stale static callbacks)
    private readonly Bindable<string> _dominant = new();

    private OsuSpriteText danLine = null!;
    private OsuSpriteText detailLine = null!;
    private CancellationTokenSource? cts;
    private ModSettingChangeTracker? _modTracker;

    // Cached raw results from the last full classification (no rate applied to BPM).
    private int _rawBpm;
    private string _danLine = string.Empty;     // e.g. "Epsilon +  (72%)"
    private string _detailBase = string.Empty;  // e.g. "RC Epsilon + · LN LN 12"  (BPM/dominant appended live)
    private string _dominantAbbr = string.Empty;

    public DanInfoWidget()
    {
        Width = 260;
        Height = 44;
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
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Y = 3,
                Font = OsuFont.Default.With(size: 15f, weight: FontWeight.SemiBold),
                Text = "Dan Info",
                Alpha = 0.6f,
            },
            detailLine = new OsuSpriteText
            {
                Anchor = Anchor.BottomCentre,
                Origin = Anchor.BottomCentre,
                Y = -3,
                Font = OsuFont.Default.With(size: 11f),
                Alpha = 0.6f,
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        if (gameplayState != null)
        {
            triggerRecalculate();
            return;
        }

        _dominant.BindTo(SunnyState.CurrentDominant);
        _dominant.BindValueChanged(_ => triggerRecalculate());

        workingBeatmap?.BindValueChanged(_ => triggerRecalculate());

        mods?.BindValueChanged(e =>
        {
            _modTracker?.Dispose();
            _modTracker = new ModSettingChangeTracker(e.NewValue);
            _modTracker.SettingChanged += _ => triggerRecalculate();
            triggerRecalculate();
        }, true);
    }

    // Runs on the update thread — appends the rate-scaled BPM + dominant to the cached lines.
    private void updateDisplay()
    {
        if (string.IsNullOrEmpty(_danLine))
        {
            danLine.Text = "N/A";
            detailLine.Text = string.Empty;
            danLine.Alpha = 0.6f;
            detailLine.Alpha = 0.6f;
            return;
        }

        double rate = GetRate(mods?.Value);
        int bpm = _rawBpm > 0 ? (int)Math.Round(_rawBpm * rate) : 0;
        string tail = bpm > 0
            ? $" · {bpm}BPM{(string.IsNullOrEmpty(_dominantAbbr) ? "" : " " + _dominantAbbr)}"
            : string.Empty;

        danLine.Text = _danLine;
        detailLine.Text = _detailBase + tail;
        danLine.Alpha = 1f;
        detailLine.Alpha = 0.85f;
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
        string dominantAbbr = SunnyState.CurrentDominant.Value;

        Task.Run(() =>
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
                    playable = wb.GetPlayableBeatmap(rs ?? wb.BeatmapInfo.Ruleset, Array.Empty<Mod>(), token);
                }

                token.ThrowIfCancellationRequested();

                if (playable is not ManiaBeatmap)
                {
                    publish(token, string.Empty, string.Empty, 0, string.Empty);
                    return;
                }

                string osuText = encodeToOsu(playable);
                token.ThrowIfCancellationRequested();

                var classification = DanClassifier.ClassifyChart(osuText, new ClassifyChartInput
                {
                    Rate = rate,
                    StarRating = double.IsFinite(starRating ?? double.NaN) ? starRating : null,
                });

                token.ThrowIfCancellationRequested();

                var (main, detail) = format(classification);
                int rawBpm = string.IsNullOrEmpty(dominantAbbr) || dominantAbbr == "N/A"
                    ? 0
                    : PatternBpmCalculator.GetMaxBpm(playable, dominantAbbr);

                publish(token, main, detail, rawBpm, dominantAbbr);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] DanInfoWidget recalc failed: {e}");
            }
        }, token);
    }

    private void publish(CancellationToken token, string main, string detail, int rawBpm, string abbr)
    {
        Schedule(() =>
        {
            if (token.IsCancellationRequested) return;
            _danLine = main;
            _detailBase = detail;
            _rawBpm = rawBpm;
            _dominantAbbr = abbr;
            updateDisplay();
        });
    }

    private static string encodeToOsu(IBeatmap beatmap)
    {
        using var writer = new StringWriter();
        new LegacyBeatmapEncoder(beatmap, null, null).Encode(writer);
        return writer.ToString();
    }

    private static (string Main, string Detail) format(ChartClassification c)
    {
        var primary = c.Primary;
        if (!c.Supported || primary == null)
            return (string.Empty, string.Empty);

        string main = $"{boundaryMark(primary.Boundary)}{primary.DisplayName}";
        int conf = (int)Math.Round(Math.Clamp(primary.Confidence, 0, 1) * 100);
        main += $"  ({conf}%)";
        if (c.Vibro) main += "  ⚠VIBRO";

        // Detail: show both halves when the chart is a hybrid and they differ.
        string detail;
        if (c.Rc != null && c.Ln != null && half(c.Rc) != half(c.Ln))
            detail = $"RC {half(c.Rc)} · LN {half(c.Ln)}";
        else
            detail = primary.Kind == "ln" ? "LN" : $"{c.KeyCount}K RC";

        return (main, detail);
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
