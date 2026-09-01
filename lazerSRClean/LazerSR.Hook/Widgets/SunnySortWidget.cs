using System;
using System.Linq;
using System.Threading.Tasks;
using LazerSR.Hook.SunnySort;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Threading;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Screens.Play;
using osu.Game.Screens.Select.Filter;
using osu.Game.Skinning;
using osuTK;
using osuTK.Graphics;

namespace LazerSR.Hook.Widgets;

/// <summary>
/// 선곡 화면 sunny 정렬 위젯 (최소 슬라이스, 로그 위주).
/// 1행 = 정렬 버튼 3개([sunny][HT][DT]) · 2행 = sunny SR 범위 슬라이더(0~15, 15=∞) · 3행 = 숫자 텍스트 + 일괄계산.
/// </summary>
public partial class SunnySortWidget : CompositeDrawable, ISerialisableDrawable
{
    private const float row_h = 30;
    private const float spacing = 4;

    public bool UsesFixedAnchor { get; set; }

    [SettingSource("너비")]
    public BindableFloat WidgetWidth { get; } = new BindableFloat(340)
    {
        MinValue = 260,
        MaxValue = 560,
        Precision = 10,
    };

    [Resolved(canBeNull: true)]
    private GameplayState? gameplayState { get; set; }

    [Resolved(canBeNull: true)]
    private OsuConfigManager? config { get; set; }

    [Resolved(canBeNull: true)]
    private RealmAccess? realm { get; set; }

    private FillFlowContainer root = null!;
    private RoundedButton noModButton = null!;
    private RoundedButton htButton = null!;
    private RoundedButton dtButton = null!;
    private RoundedButton calcButton = null!;
    private OsuSpriteText countText = null!;
    private RangeSlider rangeSlider = null!;

    private readonly BindableNumber<double> rangeLower = new(SunnySortState.RangeMin)
    {
        MinValue = SunnySortState.RangeLower,
        MaxValue = SunnySortState.RangeUpper,
        Precision = 0.1,
        Default = SunnySortState.RangeLower,
    };

    private readonly BindableNumber<double> rangeUpper = new(SunnySortState.RangeMax)
    {
        MinValue = SunnySortState.RangeLower,
        MaxValue = SunnySortState.RangeUpper,
        Precision = 0.1,
        Default = SunnySortState.RangeUpper,
    };

    private GroupMode? savedGroupMode;
    private ScheduledDelegate? rangeDebounce;
    private int lastStateVersion = -1;
    private int totalManiaMaps = -1;

    public SunnySortWidget()
    {
        Width = WidgetWidth.Value;
        Height = row_h * 3 + spacing * 2 + 14; // +14: 슬라이더 라벨/넙 여유
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
                buttonRow(),
                sliderRow(),
                countRow(),
            },
        };
    }

    private Drawable buttonRow() => new FillFlowContainer
    {
        RelativeSizeAxes = Axes.X,
        Height = row_h,
        Direction = FillDirection.Horizontal,
        Spacing = new Vector2(spacing, 0),
        Children = new Drawable[]
        {
            noModButton = sortButton("sunny 정렬", SunnySortMode.NoMod),
            htButton = sortButton("HT 정렬", SunnySortMode.HalfTime),
            dtButton = sortButton("DT 정렬", SunnySortMode.DoubleTime),
        },
    };

    private RoundedButton sortButton(string text, SunnySortMode mode) => new RoundedButton
    {
        Width = 108,
        Height = row_h,
        Text = text,
        Action = () => toggleSort(mode),
    };

    private Drawable sliderRow() => new Container
    {
        RelativeSizeAxes = Axes.X,
        Height = row_h + 16,
        Child = rangeSlider = new RangeSlider
        {
            RelativeSizeAxes = Axes.X,
            Height = row_h + 16, // RangeSlider는 자체 높이를 안 잡는다 - 명시 필요
            Label = "sunny SR",
            LowerBound = rangeLower,
            UpperBound = rangeUpper,
            MinRange = 0.5f,
            DefaultStringLowerBound = "0",
            DefaultStringUpperBound = "∞",
            NubWidth = Nub.HEIGHT * 1.2f,
        },
    };

    private Drawable countRow() => new Container
    {
        RelativeSizeAxes = Axes.X,
        Height = row_h,
        Children = new Drawable[]
        {
            countText = new OsuSpriteText
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Font = OsuFont.Torus.With(size: 13, fixedWidth: true),
            },
            calcButton = new RoundedButton
            {
                Anchor = Anchor.CentreRight,
                Origin = Anchor.CentreRight,
                Width = 96,
                Height = row_h - 4,
                Text = "일괄계산",
                Action = onCalcClicked,
            },
        },
    };

    protected override void LoadComplete()
    {
        base.LoadComplete();

        WidgetWidth.BindValueChanged(w => Width = w.NewValue, true);

        if (gameplayState != null)
        {
            // 선곡 화면 전용 — HUD에 잘못 배치되면 아무것도 안 그린다.
            root.Alpha = 0;
            return;
        }

        SunnySortServerSync.RequestOnce();

        rangeLower.BindValueChanged(_ => onRangeChanged());
        rangeUpper.BindValueChanged(_ => onRangeChanged());

        // 세션 간 유지된 상태 반영 - 정렬이 켜진 채로 껐다 켰으면 평면화·드롭다운 비활성·재필터.
        if (SunnySortState.SortActive)
        {
            applyFlatten(true);
            SunnySortRefs.SetFilterControlsDisabled(true);
            SunnySortRefs.Refilter();
        }

        refreshButtons();
        refreshCount();
    }

    protected override void Update()
    {
        base.Update();

        if (gameplayState != null)
            return;

        if (SunnySortState.Version != lastStateVersion || SunnySortWorker.Running)
        {
            lastStateVersion = SunnySortState.Version;
            refreshButtons();
        }

        refreshCount();
    }

    private void toggleSort(SunnySortMode mode)
    {
        var newMode = SunnySortState.ActiveSort == mode ? SunnySortMode.Off : mode;
        bool on = newMode != SunnySortMode.Off;

        applyFlatten(on);
        SunnySortRefs.SetFilterControlsDisabled(on);
        SunnySortState.ActiveSort = newMode;

        refreshButtons();
        SunnySortRefs.Refilter();
    }

    private void applyFlatten(bool on)
    {
        if (config == null)
            return;

        try
        {
            var gm = config.GetBindable<GroupMode>(OsuSetting.SongSelectGroupMode);

            if (on)
            {
                if (savedGroupMode == null && gm.Value != GroupMode.None)
                    savedGroupMode = gm.Value;
                gm.Value = GroupMode.None;
            }
            else if (savedGroupMode != null)
            {
                gm.Value = savedGroupMode.Value;
                savedGroupMode = null;
            }
        }
        catch (Exception)
        {
        }
    }

    private void onRangeChanged()
    {
        SunnySortState.SetRange(rangeLower.Value, rangeUpper.Value);

        rangeDebounce?.Cancel();
        rangeDebounce = Scheduler.AddDelayed(() =>
        {
            SunnySortRefs.Refilter();
        }, 250);
    }

    private void onCalcClicked()
    {
        calcButton.Enabled.Value = false;

        Task.Run(() =>
        {
            try
            {
                SunnySortWorker.EnqueueMissingFromRealm();
            }
            catch (Exception)
            {
            }
            finally
            {
                Schedule(() => calcButton.Enabled.Value = true);
            }
        });
    }

    private void refreshButtons()
    {
        style(noModButton, SunnySortState.ActiveSort == SunnySortMode.NoMod);
        style(htButton, SunnySortState.ActiveSort == SunnySortMode.HalfTime);
        style(dtButton, SunnySortState.ActiveSort == SunnySortMode.DoubleTime);

        static void style(RoundedButton b, bool active)
            => b.BackgroundColour = active ? new Color4(120, 200, 120, 255) : new Color4(60, 60, 70, 255);
    }

    private void refreshCount()
    {
        if (SunnySortWorker.Running)
        {
            countText.Text = $"계산 중  {SunnySortWorker.ScopeDone}/{Math.Max(SunnySortWorker.ScopeTotal, 1)}";
            return;
        }

        if (totalManiaMaps < 0 && realm != null)
        {
            try
            {
                // 모든 mania (키 수 무관). Realm LINQ는 b.Ruleset.OnlineID(링크)를 Where에서 못 받음 - 메모리 필터.
                totalManiaMaps = realm.Run(r => r.All<osu.Game.Beatmaps.BeatmapInfo>()
                                                 .Where(b => !b.Hidden)
                                                 .AsEnumerable()
                                                 .Count(b => b.Ruleset.OnlineID == 3));
            }
            catch (Exception)
            {
                totalManiaMaps = 0;
            }
        }

        countText.Text = $"캐시 {SunnySortCache.DistinctMapCount}/{Math.Max(totalManiaMaps, 0)}";
    }

    protected override void Dispose(bool isDisposing)
    {
        rangeDebounce?.Cancel();

        // 위젯이 사라져도 osu 드롭다운은 다시 살려둔다(계속 비활성이면 사용자가 갇힌다).
        // group 값 복원도 시도 - 실패하면 무시(Dispose가 업데이트 스레드 밖일 수 있음).
        try
        {
            SunnySortRefs.SetFilterControlsDisabled(false);

            if (savedGroupMode != null && config != null)
            {
                config.GetBindable<GroupMode>(OsuSetting.SongSelectGroupMode).Value = savedGroupMode.Value;
                savedGroupMode = null;
            }
        }
        catch
        {
            // 무시.
        }

        base.Dispose(isDisposing);
    }
}
