using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LazerSR.Hook.PersonalSunny;
using LazerSR.SunnyCalculator;
using LazerSR.SunnyCalculator.Tuning;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens.Play;
using osu.Game.Skinning;
using osuTK;
using osuTK.Graphics;

namespace LazerSR.Hook.Widgets;

/// <summary>
/// Song-select-only widget for the personal sunny+ diff. Two rows.
/// <para>
/// <b>위(pill)</b>: 좌측은 지금 스코프된 맵의 <i>개인화</i> sunnySR(만인 + 개인 diff),
/// 우측은 이 사람이 정확도 95%를 뽑아내는 sunny 값 — 큐에 쌓인 기록으로 적합한 α/β의 역산일 뿐,
/// 맵과 무관한 "실력" 숫자다.
/// </para>
/// <para>
/// <b>아래(status)</b>: J를 굽는 중이면 진행률 막대, 아니면 큐 개수와 리플레이 수집 버튼.
/// 실제 축적/굽기/적합은 전부 <see cref="PersonalSunnyService"/>가 하고, 이 위젯은 그 상태를
/// 매 프레임 폴리(poll)만 한다 — <c>ManiaSimulationProgressDisplay</c>와 같은 방식이다.
/// </para>
/// <para>
/// <b>선곡 화면 전용이다.</b> HUD 툴박스에도 등록되지만(<c>SkinWidgetRegistrarPatch</c>가 컨테이너를
/// 가리지 않으므로) <see cref="GameplayState"/>가 잡히면 아무것도 그리지 않는다.
/// </para>
/// </summary>
public partial class PersonalSunnyWidget : CompositeDrawable, ISerialisableDrawable
{
    private const float pill_row_height = 40;
    private const float status_row_height = 30;
    private const float spacing = 4;

    public bool UsesFixedAnchor { get; set; }

    [SettingSource("너비")]
    public BindableFloat WidgetWidth { get; } = new BindableFloat(320)
    {
        MinValue = 220,
        MaxValue = 520,
        Precision = 10,
    };

    [Resolved(canBeNull: true)]
    private GameplayState? gameplayState { get; set; }

    [Resolved(canBeNull: true)]
    private IBindable<WorkingBeatmap>? beatmap { get; set; }

    [Resolved(canBeNull: true)]
    private IBindable<RulesetInfo>? ruleset { get; set; }

    [Resolved(canBeNull: true)]
    private IBindable<IReadOnlyList<Mod>>? mods { get; set; }

    [Resolved(canBeNull: true)]
    private BeatmapManager? beatmapManager { get; set; }

    [Resolved(canBeNull: true)]
    private RulesetStore? rulesetStore { get; set; }

    [Resolved(canBeNull: true)]
    private RealmAccess? realmAccess { get; set; }

    [Resolved(canBeNull: true)]
    private IAPIProvider? api { get; set; }

    private Drawable root = null!;
    private StarRatingDisplay currentPill = null!;
    private StarRatingDisplay targetPill = null!;
    private Container bakingView = null!;
    private Container idleView = null!;
    private Box bakeFill = null!;
    private OsuSpriteText bakeLabel = null!;
    private OsuSpriteText queueLabel = null!;
    private RoundedButton collectButton = null!;

    private CancellationTokenSource? recalcCts;
    private ModSettingChangeTracker? modTracker;
    private int lastServiceVersion = -1;

    private const double target_accuracy = 0.95;

    public PersonalSunnyWidget()
    {
        Width = WidgetWidth.Value;
        Height = pill_row_height + status_row_height + spacing;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        InternalChild = root = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.Both,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, spacing),
            Children = new Drawable[]
            {
                createPillRow(),
                createStatusRow(),
            },
        };
    }

    private Drawable createPillRow() => new Container
    {
        RelativeSizeAxes = Axes.X,
        Height = pill_row_height,
        Masking = true,
        CornerRadius = 4,
        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black.Opacity(0.6f) },
            new FillFlowContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(8, 0),
                Children = new Drawable[]
                {
                    currentPill = new StarRatingDisplay(default, animated: true)
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                    },
                    new OsuSpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Text = "|",
                        Font = OsuFont.Torus.With(size: 16, weight: FontWeight.Bold),
                        Colour = Color4.White.Opacity(0.4f),
                    },
                    targetPill = new StarRatingDisplay(default, animated: true)
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                    },
                },
            },
        },
    };

    private Drawable createStatusRow() => new Container
    {
        RelativeSizeAxes = Axes.X,
        Height = status_row_height,
        Masking = true,
        CornerRadius = 4,
        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black.Opacity(0.6f) },
            bakingView = createBakingView(),
            idleView = createIdleView(),
        },
    };

    private Container createBakingView() => new Container
    {
        RelativeSizeAxes = Axes.Both,
        Padding = new MarginPadding { Horizontal = 8, Vertical = 5 },
        Children = new Drawable[]
        {
            bakeLabel = new OsuSpriteText
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Font = OsuFont.Torus.With(size: 12),
            },
            new Container
            {
                Anchor = Anchor.BottomLeft,
                Origin = Anchor.BottomLeft,
                RelativeSizeAxes = Axes.X,
                Height = 6,
                Masking = true,
                CornerRadius = 3,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(255, 255, 255, 50) },
                    bakeFill = new Box { RelativeSizeAxes = Axes.Both, Width = 0f, Colour = Color4.White },
                },
            },
        },
    };

    private Container createIdleView() => new Container
    {
        RelativeSizeAxes = Axes.Both,
        Padding = new MarginPadding { Horizontal = 8, Vertical = 3 },
        Children = new Drawable[]
        {
            queueLabel = new OsuSpriteText
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Font = OsuFont.Torus.With(size: 13, fixedWidth: true),
            },
            collectButton = new RoundedButton
            {
                Anchor = Anchor.CentreRight,
                Origin = Anchor.CentreRight,
                Width = 108,
                Height = status_row_height - 8,
                Text = "리플레이 수집",
                Action = onCollectClicked,
            },
        },
    };

    protected override void LoadComplete()
    {
        base.LoadComplete();

        WidgetWidth.BindValueChanged(w => Width = w.NewValue, true);

        // 선곡 화면 전용 - HUD에 잘못 배치돼도(GameplayState가 있으면) 그냥 아무것도 안 그린다.
        if (gameplayState != null)
        {
            root.Alpha = 0;
            return;
        }

        if (beatmapManager != null && rulesetStore != null)
            PersonalSunnyService.EnsureDependencies(beatmapManager, rulesetStore);

        // Proactive one-shot pre-computation - idempotent, so this widget loading again on another
        // screen (or a fresh song-select visit) is a harmless no-op after the first time.
        if (realmAccess != null && api != null)
            PersonalSunnyService.StartBackgroundWarmup(realmAccess, api);

        ruleset?.BindValueChanged(_ => scheduleRecalculate());
        beatmap?.BindValueChanged(_ => scheduleRecalculate());

        mods?.BindValueChanged(e =>
        {
            modTracker?.Dispose();
            modTracker = new ModSettingChangeTracker(e.NewValue);
            modTracker.SettingChanged += _ => scheduleRecalculate();
            scheduleRecalculate();
        }, true);

        refreshTargetPill();
        refreshStatus();
    }

    protected override void Update()
    {
        base.Update();

        if (gameplayState != null)
            return;

        if (PersonalSunnyService.Version == lastServiceVersion)
            return;

        lastServiceVersion = PersonalSunnyService.Version;
        refreshTargetPill();
        refreshStatus();
    }

    // ── 좌측 pill: 지금 스코프된 맵의 개인화 sunnySR ──────────────────────────

    private void scheduleRecalculate()
    {
        recalcCts?.Cancel();
        recalcCts = null;

        var rulesetInfo = ruleset?.Value;
        var workingBeatmap = beatmap?.Value;

        if (rulesetInfo == null || rulesetInfo.OnlineID != 3 || workingBeatmap == null)
        {
            currentPill.Alpha = 0;
            return;
        }

        currentPill.Alpha = 1;

        var cts = new CancellationTokenSource();
        recalcCts = cts;
        var ct = cts.Token;

        // 계산기는 자기 몫의 변환본을 따로 만들지만, mods 자체는 UI가 소유한 인스턴스이므로
        // 사본을 넘긴다 (safety.md, "라이브 vs 시뮬레이션 인스턴스").
        IReadOnlyList<Mod> modCopies = mods?.Value?.Select(m => m.DeepClone()).ToArray() ?? Array.Empty<Mod>();

        Task.Run(() =>
        {
            try
            {
                double[] deltas = PersonalDiff.CombinedWithUniversal();
                double sr = SunnyConstants.WithIsolatedDiff(deltas,
                    () => SunnyRunner.Calculate(workingBeatmap, rulesetInfo, modCopies, ct));

                if (ct.IsCancellationRequested) return;

                Schedule(() =>
                {
                    if (!ct.IsCancellationRequested)
                        currentPill.Current.Value = new StarDifficulty(sr, 0);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] PersonalSunnyWidget recalculation failed: {e.Message}");
            }
        }, ct);
    }

    // ── 우측 pill: 정확도 95%를 뽑아내는 sunny 값 ─────────────────────────────

    private void refreshTargetPill()
    {
        double alpha = PersonalSunnyService.Alpha;
        double beta = PersonalSunnyService.Beta;

        if (!double.IsFinite(beta) || Math.Abs(beta) < 1e-9)
        {
            targetPill.Alpha = 0;
            return;
        }

        // y = -log(1 - accuracy)의 역산: 적합에서 쓴 것과 같은 표적.
        double yTarget = -Math.Log(1.0 - target_accuracy);
        double sr = (yTarget - alpha) / beta;

        targetPill.Alpha = 1;
        targetPill.Current.Value = new StarDifficulty(Math.Max(sr, 0), 0);
    }

    // ── 아래 상태 행 ───────────────────────────────────────────────────────

    private void refreshStatus()
    {
        bool baking = PersonalSunnyService.IsBaking;

        bakingView.Alpha = baking ? 1 : 0;
        idleView.Alpha = baking ? 0 : 1;

        if (baking)
        {
            int total = Math.Max(PersonalSunnyService.BakeTotal, 1);
            float progress = Math.Clamp((float)PersonalSunnyService.BakeDone / total, 0f, 1f);

            bakeFill.Width = progress;
            bakeLabel.Text = $"J 굽는 중  {progress:P0}";
        }
        else
        {
            // 풀별로 "그 풀 자체 상한 대비 분수"를 보여준다(2026-08-22) - Pool A("최고")는 정원
            // PersonalSunnyTopPoolStore.Capacity, Pool B("최근")는 fit에 실제 반영되는 상한
            // RecentPoolEffectiveCount(원본 100개 윈도우가 아니라 그중 축소된 50) 대비. 총합 하나로
            // 합치면 두 풀의 성격이 다른 게 안 드러나고, raw+반영 혼합 표기는 숫자 자체가 의미가
            // 없어져서(예: Pool B 원본이 늘어도 개인화엔 영향 없는데 총합만 커짐) 이 분수 표기로 확정.
            queueLabel.Text = $"최고 {PersonalSunnyService.TopPoolRecordCount}/{PersonalSunnyTopPoolStore.Capacity} · " +
                               $"최근 {PersonalSunnyService.RecentPoolRecordCount}/{PersonalSunnyService.RecentPoolEffectiveCount}";
        }
    }

    private void onCollectClicked()
    {
        if (realmAccess == null || api == null)
            return;

        collectButton.Enabled.Value = false;

        Task.Run(() =>
        {
            try
            {
                PersonalSunnyService.ResetAndCollectFromRealmAsync(realmAccess, api);
            }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] PersonalSunnyWidget collect failed: {e}");
            }
            finally
            {
                Schedule(() => collectButton.Enabled.Value = true);
            }
        });
    }

    protected override void Dispose(bool isDisposing)
    {
        recalcCts?.Cancel();
        modTracker?.Dispose();

        base.Dispose(isDisposing);
    }
}
