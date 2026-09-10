using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LazerSR.DanCalculator;
using LazerSR.DanCalculator.Classifier;
using LazerSR.DanCalculator.Credit;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Rulesets.Mania.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace LazerSR.Hook.Drawables;

/// <summary>
/// Results-screen expanded-statistics row (inserted between "Performance Breakdown"
/// and "Timing Distribution"). Left = the map's dan, right = the performance dan for
/// this score (chart dan credited by ScoreV1/V2 accuracy through the dan-credit
/// curve). Temporary layout — badge image + label per side, more to come.
/// Read-only: classifies a working beatmap and reads the finished score.
/// </summary>
public partial class DanResultRow : CompositeDrawable
{
    private readonly ScoreInfo score;
    private readonly IBeatmap playableBeatmap;

    [Resolved]
    private IRenderer renderer { get; set; } = null!;

    private Container mapSlot = null!;
    private Container perfSlot = null!;
    private OsuSpriteText statusText = null!;
    private CancellationTokenSource? cts;

    public DanResultRow(ScoreInfo score, IBeatmap playableBeatmap)
    {
        this.score = score;
        this.playableBeatmap = playableBeatmap;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.X;
        Height = 96;

        InternalChildren = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = Colour4.Black, Alpha = 0.2f },
            new GridContainer
            {
                RelativeSizeAxes = Axes.Both,
                ColumnDimensions = new[]
                {
                    new Dimension(),
                    new Dimension(GridSizeMode.Absolute, 1),
                    new Dimension(),
                },
                Content = new[]
                {
                    new Drawable[]
                    {
                        mapSlot = slot("맵 DAN"),
                        new Box { RelativeSizeAxes = Axes.Y, Width = 1, Colour = Colour4.White, Alpha = 0.15f },
                        perfSlot = slot("퍼포먼스 DAN"),
                    },
                },
            },
            statusText = new OsuSpriteText
            {
                Anchor = Anchor.BottomCentre,
                Origin = Anchor.BottomCentre,
                Y = -4,
                Font = OsuFont.Default.With(size: 10f),
                Colour = Colour4.White,
                Alpha = 0.5f,
                Text = "계산 중…",
            },
        };
    }

    private static Container slot(string caption) => new Container
    {
        RelativeSizeAxes = Axes.Both,
        Child = new FillFlowContainer
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Vertical,
            Spacing = new osuTK.Vector2(0, 4),
            Children = new Drawable[]
            {
                new OsuSpriteText
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Font = OsuFont.Default.With(size: 10f, weight: FontWeight.Bold),
                    Colour = Colour4.White,
                    Alpha = 0.4f,
                    Text = caption,
                },
            },
        },
    };

    protected override void LoadComplete()
    {
        base.LoadComplete();
        cts = new CancellationTokenSource();
        var token = cts.Token;

        Task.Run(async () =>
        {
            try
            {
                if (playableBeatmap is not ManiaBeatmap)
                {
                    schedule(token, () => statusText.Text = "mania 아님");
                    return;
                }

                string osuText;
                using (var writer = new StringWriter())
                {
                    new LegacyBeatmapEncoder(playableBeatmap, null, null).Encode(writer);
                    osuText = writer.ToString();
                }

                double rate = 1.0;
                foreach (var mod in score.Mods)
                    if (mod is IApplicableToRate r)
                        rate = r.ApplyToRate(0, rate);

                var classification = await DanClassifier.ClassifyChartWithCompanellaAsync(osuText, new ClassifyChartInput
                {
                    Rate = rate,
                    StarRating = double.IsFinite(score.BeatmapInfo.StarRating) ? score.BeatmapInfo.StarRating : (double?)null,
                }).ConfigureAwait(false);

                var judgements = readJudgements(score);
                var perf = PerformanceDan.Compute(classification, judgements);

                schedule(token, () => apply(classification, perf));
            }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] DanResultRow failed: {e}");
                schedule(token, () => statusText.Text = "오류");
            }
        });
    }

    private void apply(ChartClassification c, PerformanceDanResult? perf)
    {
        var primary = c.Primary;
        if (primary == null || !c.Supported)
        {
            statusText.Text = $"{c.KeyCount}K · 차트 dan 없음";
            fillSlot(mapSlot, null, "—", "", "");
            fillSlot(perfSlot, null, "—", "", "");
            return;
        }

        var ci = CultureInfo.InvariantCulture;
        string side = primary.Kind == "ln" ? "LN" : "RC";

        // Map dan — the badge is the level; the --/-/+/++ tier rides top-right.
        fillSlot(mapSlot, DanImages.ResolveExisting(primary, c.KeyCount),
            primary.Label, primary.Variant ?? "", $"{side} · {primary.RawDan.ToString("0.00", ci)}");

        if (perf?.PerformanceDan is double pd)
        {
            string label = DanImages.ScaleLabel(pd, c.KeyCount, perf.Side);
            string suffix = DanImages.TierSuffix(pd);
            fillSlot(perfSlot, DanImages.ResolveExisting(label, perf.Side, c.KeyCount),
                label, suffix, pd.ToString("0.00", ci));
            statusText.Text =
                $"{c.KeyCount}K {mods()} · 차트 {primary.RawDan.ToString("0.00", ci)} · "
                + $"acc {(perf.UsedAccuracy * 100).ToString("0.00", ci)}% ({perf.BarCurrency}) · bar {(perf.Bar * 100).ToString("0.00", ci)}%";
        }
        else
        {
            fillSlot(perfSlot, null, "—", "", perf != null
                ? $"bar 미달 (<{(perf.MinAccuracy * 100).ToString("0.0", ci)}%)"
                : "");
            statusText.Text = $"{c.KeyCount}K {mods()} · bar 미달";
        }
    }

    private string mods() => score.Mods.Length > 0 ? string.Join("", score.Mods.Select(m => m.Acronym)) : "NM";

    private void fillSlot(Container slot, string? imagePath, string bareLabel, string suffix, string subText)
    {
        var flow = (FillFlowContainer)slot.Child;
        // Keep the caption (index 0), replace everything after it.
        while (flow.Count > 1)
            flow.Remove(flow[flow.Count - 1], true);

        Texture? texture = imagePath != null ? tryLoad(imagePath) : null;
        var tierColour = DanImages.TierColour(suffix) ?? Colour4.White;

        if (texture != null)
        {
            var badge = new Container
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Size = new osuTK.Vector2(52),
                Child = new Sprite
                {
                    RelativeSizeAxes = Axes.Both,
                    Texture = texture,
                    FillMode = FillMode.Fit,
                },
            };
            if (!string.IsNullOrEmpty(suffix))
                badge.Add(new OsuSpriteText
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.CentreLeft,
                    X = 3,
                    Font = OsuFont.Default.With(size: 15f, weight: FontWeight.Bold),
                    Colour = tierColour,
                    Text = suffix,
                });
            flow.Add(badge);
        }
        else
        {
            flow.Add(new OsuSpriteText
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Font = OsuFont.Default.With(size: 18f, weight: FontWeight.Bold),
                Colour = string.IsNullOrEmpty(suffix) ? Colour4.White : tierColour,
                Text = bareLabel + suffix,
            });
        }

        if (!string.IsNullOrEmpty(subText))
            flow.Add(new OsuSpriteText
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Font = OsuFont.Default.With(size: 11f),
                Colour = Colour4.White,
                Alpha = 0.7f,
                Text = subText,
            });
    }

    private Texture? tryLoad(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var upload = new TextureUpload(stream);
            var texture = renderer.CreateTexture(upload.Width, upload.Height);
            texture.SetData(upload);
            return texture;
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] DanResultRow.tryLoad({path}) failed: {e.Message}");
            return null;
        }
    }

    private static DanJudgements readJudgements(ScoreInfo score)
    {
        int Get(HitResult r) => score.Statistics.TryGetValue(r, out int v) ? v : 0;
        return new DanJudgements(
            Get(HitResult.Perfect), Get(HitResult.Great), Get(HitResult.Good),
            Get(HitResult.Ok), Get(HitResult.Meh), Get(HitResult.Miss));
    }

    private void schedule(CancellationToken token, Action action) => Schedule(() =>
    {
        if (!token.IsCancellationRequested) action();
    });

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        cts?.Cancel();
        cts?.Dispose();
    }
}
