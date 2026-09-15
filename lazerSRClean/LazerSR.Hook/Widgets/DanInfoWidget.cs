using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // Roxy's internal axis names -> the display names the user chose. Only
    // shown for 4K RC charts where Roxy itself won the routing (see
    // ChartClassification.RoxyAxes); everything else keeps the plain dan text.
    private static readonly Dictionary<string, string> roxyAxisDisplayNames = new()
    {
        ["speed"] = "스피드",
        ["handStream"] = "핸스",
        ["jack"] = "미니잭",
        ["chordjack"] = "코드잭",
        ["tech"] = "테크",
        ["stamina"] = "밀도",
        ["course"] = "체력",
    };

    [SettingSource("Roxy 패턴축 최소 raw 기준")]
    public BindableNumber<float> RoxyAxisRawFloor { get; } =
        new BindableFloat(2) { MinValue = -2, MaxValue = 15, Precision = 0.5f };

    [SettingSource("Roxy 패턴축 최소 비중 (%)")]
    public BindableNumber<float> RoxyAxisShareFloorPercent { get; } =
        new BindableFloat(5) { MinValue = 0, MaxValue = 50, Precision = 1 };

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
    private ChartClassification? lastClassification;

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

        // Slider tweaks re-render the already-classified map instantly instead
        // of waiting for the next map/mod change to re-run the heavy classify.
        RoxyAxisRawFloor.BindValueChanged(_ => renderCurrent());
        RoxyAxisShareFloorPercent.BindValueChanged(_ => renderCurrent());

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

                publish(token, classification);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] DanInfoWidget recalc failed: {e}");
            }
        }, token);
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
    // called both after a fresh classify and when the axis-floor sliders move,
    // so slider tweaks don't need a map re-select to take effect.
    private void renderCurrent()
    {
        var (main, detail) = lastClassification != null ? format(lastClassification) : (string.Empty, string.Empty);

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
    }

    private static string encodeToOsu(IBeatmap beatmap)
    {
        using var writer = new StringWriter();
        new LegacyBeatmapEncoder(beatmap, null, null).Encode(writer);
        return writer.ToString();
    }

    private (string Main, string Detail) format(ChartClassification c)
    {
        var primary = c.Primary;
        if (!c.Supported || primary == null)
            return (string.Empty, string.Empty);

        // Top line always tags RC (tap) vs LN so a 4K sub-10 verdict is not ambiguous.
        // The vibro warning here is unchanged from before — it always wins over
        // the axis summary below, which is skipped entirely on a vibro chart.
        string sideTag = primary.Kind == "ln" ? "LN" : "RC";
        string main = $"{sideTag}  {half(primary)}{(c.Vibro ? "  ⚠VIBRO" : "")}";

        // Second line: Roxy's top pattern axes when this chart's verdict actually
        // came from Roxy (4K RC only — see RoxyAxes). Falls back to the RC/LN
        // hybrid detail when Roxy didn't run or nothing clears the floors.
        string? axisSummary = c.Vibro ? null : buildAxisSummary(c.RoxyAxes);
        string detail;
        if (axisSummary != null)
            detail = axisSummary;
        else if (c.Rc != null && c.Ln != null && half(c.Rc) != half(c.Ln))
            detail = $"RC {half(c.Rc)}  ·  LN {half(c.Ln)}";
        else
            detail = string.Empty;

        return (main, detail);
    }

    // Top 3 of Roxy's 7 axes by share of the structural signal, excluding any
    // axis whose local raw-dan-equivalent or share doesn't clear the sliders'
    // floors. Both floors are user-tunable in the skin editor.
    private string? buildAxisSummary(List<RoxyAxisContribution>? axes)
    {
        if (axes == null || axes.Count == 0) return null;

        double rawFloor = RoxyAxisRawFloor.Value;
        double shareFloor = RoxyAxisShareFloorPercent.Value / 100.0;

        var picked = axes
            .Where(a => a.LocalRawDan >= rawFloor && a.Share > shareFloor)
            .OrderByDescending(a => a.Share)
            .Take(3)
            .Select(a => roxyAxisDisplayNames.TryGetValue(a.Axis, out var name) ? name : a.Axis)
            .ToList();

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
