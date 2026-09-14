using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LazerSR.Hook.Drawables;
using LazerSR.Hook.Ipc;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Online.API;
using osu.Game.Screens.Play;
using osu.Game.Skinning;
using osuTK;

namespace LazerSR.Hook.Widgets;

/// <summary>
/// 프로필 위젯 — 서버에 on-write로 집계된 4K dan 등급(architecture.md §25)을 선곡 화면에
/// 한 줄로 보여준다. 왼쪽부터 전체(RC 헤드라인, 굵은 구분선) | 잭 | 테크 | 스피드 | 스태미나
/// (얇은 구분선 x4) | LN(헤드라인). 6K/7K는 아직 안 다룬다.
/// <para>
/// 서버가 업로드 시점에 이미 계산해둔 캐시(dan_verdicts)를 그대로 읽기만 하므로, 위젯은
/// <see cref="LoadComplete"/> 시 한 번만 조회하고 그 뒤로는 갱신하지 않는다 — 결과창엔 스킨
/// 위젯 레이어 자체가 없어 이 위젯이 그려지지 않고, 결과창에서 선곡 화면으로 돌아오면 위젯이
/// 새로 로드되며 그때 다시 조회된다(자연스러운 갱신 타이밍).
/// </para>
/// Hook은 네트워크 금지라 파이프로 런처에 조회를 대신 시킨다(§23 lazerSR 리더보드와 동일 패턴,
/// <c>psreq</c>/<c>psreqok</c>/<c>psreqerr</c>). 읽기 전용 — 서버에 아무것도 쓰지 않는다.
/// </summary>
public class DanProfileWidget : CompositeDrawable, ISerialisableDrawable
{
    public bool UsesFixedAnchor { get; set; }

    [Resolved(canBeNull: true)]
    private GameplayState? gameplayState { get; set; }

    [Resolved(canBeNull: true)]
    private IAPIProvider? api { get; set; }

    [Resolved]
    private IRenderer renderer { get; set; } = null!;

    private const int KEY_COUNT = 4;

    private Container overallCell = null!;
    private Container jackCell = null!;
    private Container techCell = null!;
    private Container speedCell = null!;
    private Container staminaCell = null!;
    private Container lnCell = null!;
    private CancellationTokenSource? cts;

    public DanProfileWidget()
    {
        Width = 420;
        Height = 64;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        InternalChildren = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = Colour4.Black, Alpha = 0.4f },
            new GridContainer
            {
                RelativeSizeAxes = Axes.Both,
                ColumnDimensions = new[]
                {
                    new Dimension(),
                    new Dimension(GridSizeMode.Absolute, 3),   // 굵은 구분선 (전체 | 나머지)
                    new Dimension(),
                    new Dimension(GridSizeMode.Absolute, 1),   // 얇은 구분선 x4
                    new Dimension(),
                    new Dimension(GridSizeMode.Absolute, 1),
                    new Dimension(),
                    new Dimension(GridSizeMode.Absolute, 1),
                    new Dimension(),
                    new Dimension(GridSizeMode.Absolute, 1),
                    new Dimension(),
                },
                Content = new[]
                {
                    new Drawable[]
                    {
                        overallCell = cell(),
                        divider(0.3f),
                        jackCell = cell(),
                        divider(0.15f),
                        techCell = cell(),
                        divider(0.15f),
                        speedCell = cell(),
                        divider(0.15f),
                        staminaCell = cell(),
                        divider(0.15f),
                        lnCell = cell(),
                    },
                },
            },
        };
    }

    private static Container cell() => new Container
    {
        RelativeSizeAxes = Axes.Both,
        Child = new FillFlowContainer
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 2),
        },
    };

    private static Box divider(float alpha) => new Box
    {
        RelativeSizeAxes = Axes.Y,
        Width = 1,
        Colour = Colour4.White,
        Alpha = alpha,
    };

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // 게임플레이 중엔 그리지 않는다 — 선곡 화면 전용(결과창엔 스킨 위젯 레이어 자체가 없음).
        if (gameplayState != null) return;

        string? username = api?.LocalUser.Value.Username;
        if (string.IsNullOrEmpty(username) || username == "Guest") return;

        cts = new CancellationTokenSource();
        var token = cts.Token;

        Task.Run(async () =>
        {
            try
            {
                string json = await PipeServer.RequestAsync("psreq", username, 8000).ConfigureAwait(false);
                var values = parse(json);
                schedule(token, () => apply(values));
            }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] DanProfileWidget fetch failed: {e}");
            }
        });
    }

    private readonly record struct Values(double? Overall, double? Jack, double? Tech, double? Speed, double? Stamina, double? Ln);

    private static Values parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("modes", out var modes)) return default;
        if (!modes.TryGetProperty(KEY_COUNT.ToString(CultureInfo.InvariantCulture), out var mode)) return default;

        mode.TryGetProperty("rc", out var rc);
        mode.TryGetProperty("ln", out var ln);

        return new Values(
            readRawDan(rc),
            readSkillsetDan(rc, "jack"),
            readSkillsetDan(rc, "tech"),
            readSkillsetDan(rc, "speed"),
            readSkillsetDan(rc, "stamina"),
            readRawDan(ln));
    }

    private static double? readRawDan(JsonElement side)
        => side.ValueKind == JsonValueKind.Object && side.TryGetProperty("raw_dan", out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : (double?)null;

    private static double? readSkillsetDan(JsonElement side, string bucketId)
    {
        if (side.ValueKind != JsonValueKind.Object) return null;
        if (!side.TryGetProperty("skillsets", out var skillsets) || skillsets.ValueKind != JsonValueKind.Object) return null;
        return skillsets.TryGetProperty(bucketId, out var bucket) ? readRawDan(bucket) : null;
    }

    private void apply(Values values)
    {
        fill(overallCell, values.Overall, "rc");
        fill(jackCell, values.Jack, "rc");
        fill(techCell, values.Tech, "rc");
        fill(speedCell, values.Speed, "rc");
        fill(staminaCell, values.Stamina, "rc");
        fill(lnCell, values.Ln, "ln");
    }

    private void fill(Container cellContainer, double? rawDan, string family)
    {
        var flow = (FillFlowContainer)cellContainer.Child;
        flow.Clear();

        if (rawDan is not double value)
        {
            flow.Add(new OsuSpriteText
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Font = OsuFont.Default.With(size: 16f, weight: FontWeight.Bold),
                Colour = Colour4.White,
                Alpha = 0.3f,
                Text = "-",
            });
            return;
        }

        string label = DanImages.ScaleLabel(value, KEY_COUNT, family);
        string suffix = DanImages.TierSuffix(value);
        string? imagePath = DanImages.ResolveExisting(label, family, KEY_COUNT);
        var tierColour = DanImages.TierColour(suffix) ?? Colour4.White;

        Texture? texture = imagePath != null ? tryLoad(imagePath) : null;

        if (texture != null)
        {
            var badge = new Container
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Size = new Vector2(36),
                Child = new Sprite { RelativeSizeAxes = Axes.Both, Texture = texture, FillMode = FillMode.Fit },
            };
            if (!string.IsNullOrEmpty(suffix))
                badge.Add(new OsuSpriteText
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.CentreLeft,
                    X = 2,
                    Font = OsuFont.Default.With(size: 12f, weight: FontWeight.Bold),
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
                Font = OsuFont.Default.With(size: 15f, weight: FontWeight.Bold),
                Colour = string.IsNullOrEmpty(suffix) ? Colour4.White : tierColour,
                Text = label + suffix,
            });
        }

        flow.Add(new OsuSpriteText
        {
            Anchor = Anchor.TopCentre,
            Origin = Anchor.TopCentre,
            Font = OsuFont.Default.With(size: 10f),
            Colour = Colour4.White,
            Alpha = 0.7f,
            Text = value.ToString("0.00", CultureInfo.InvariantCulture),
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
            HookLog.Write($"[LazerSR] DanProfileWidget.tryLoad({path}) failed: {e.Message}");
            return null;
        }
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
