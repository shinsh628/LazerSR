using System;
using System.Collections.Generic;
using System.IO;
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

    [Resolved(canBeNull: true)]
    private GameplayState? gameplayState { get; set; }

    [Resolved(canBeNull: true)]
    private IBindable<WorkingBeatmap>? workingBeatmap { get; set; }

    [Resolved(canBeNull: true)]
    private IBindable<RulesetInfo>? ruleset { get; set; }

    [Resolved(canBeNull: true)]
    private IBindable<IReadOnlyList<Mod>>? mods { get; set; }

    private OsuSpriteText danLine = null!;
    private OsuSpriteText detailLine = null!;
    private CancellationTokenSource? cts;
    private ModSettingChangeTracker? _modTracker;

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
                Y = -6,
                Font = OsuFont.Default.With(size: 15f, weight: FontWeight.SemiBold),
                Text = "Dan Info",
                Alpha = 0.6f,
            },
            detailLine = new OsuSpriteText
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
                    playable = wb.GetPlayableBeatmap(rs ?? wb.BeatmapInfo.Ruleset, Array.Empty<Mod>(), token);
                }

                token.ThrowIfCancellationRequested();

                if (playable is not ManiaBeatmap)
                {
                    publish(token, string.Empty, string.Empty);
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

                var (main, detail) = format(classification);
                publish(token, main, detail);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] DanInfoWidget recalc failed: {e}");
            }
        }, token);
    }

    private void publish(CancellationToken token, string main, string detail)
    {
        Schedule(() =>
        {
            if (token.IsCancellationRequested) return;
            if (string.IsNullOrEmpty(main))
            {
                danLine.Text = "N/A";
                detailLine.Text = string.Empty;
                danLine.Alpha = 0.6f;
                return;
            }

            danLine.Text = main;
            detailLine.Text = detail;
            danLine.Alpha = 1f;
            detailLine.Alpha = string.IsNullOrEmpty(detail) ? 0f : 0.85f;
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

        string main = $"{half(primary)}{(c.Vibro ? "  ⚠VIBRO" : "")}";

        // Second line: the other half, shown only for hybrids where it differs.
        string detail = string.Empty;
        if (c.Rc != null && c.Ln != null && half(c.Rc) != half(c.Ln))
            detail = $"RC {half(c.Rc)}  ·  LN {half(c.Ln)}";

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
