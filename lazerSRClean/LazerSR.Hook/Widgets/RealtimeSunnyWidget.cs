using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LazerSR.SunnyCalculator;
using LazerSR.SunnyCalculator.Tuning;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Configuration;
using osu.Game.Rulesets.Mania.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Screens.Play;
using osu.Game.Skinning;

namespace LazerSR.Hook.Widgets;

/// <summary>
/// A sunny pill that shows the difficulty of the next <see cref="window_ms"/> ms instead of the whole map.
/// Every 100 ms it takes the arithmetic mean of every per-note strain inside the window and runs it
/// through the tail of sunny's aggregation pipeline (short-map nerf skipped, temp nerf + 0.975 kept).
/// The per-note strain timeline is baked once on the loading screen.
///
/// sunny vs sunny+ is not decided here — the launcher's checkbox drives the process-wide default, and
/// <see cref="SunnyManiaDifficultyCalculator.GetStrainTimeline"/> reads it like every other consumer.
/// The personal diff is opt-in via <see cref="UsePersonalSunny"/>; when on, the bake runs inside
/// <see cref="SunnyConstants.WithIsolatedDiff{T}"/> exactly like <c>StrainGraphWidget</c>'s overlay.
/// </summary>
public class RealtimeSunnyWidget : CompositeDrawable, ISerialisableDrawable
{
    public bool UsesFixedAnchor { get; set; }

    [SettingSource("개인화 sunny 적용", "이 사람의 실력에 맞춘 개인화 diff를 반영합니다")]
    public BindableBool UsePersonalSunny { get; } = new BindableBool(false);

    private const double window_ms = 400.0;
    private const double difficulty_multiplier = 0.975;

    [Resolved(canBeNull: true)]
    private GameplayState? gameplayState { get; set; }

    [Resolved(canBeNull: true)]
    private IGameplayClock? gameplayClock { get; set; }

    private StarRatingDisplay pill = null!;

    private ManiaBeatmap? maniaBeatmap;
    private Mod[] mods = Array.Empty<Mod>();

    // baked strain timeline, sorted ascending by time. replaced wholesale by bakeTimeline().
    private double[] noteTimes = Array.Empty<double>();
    private double[] noteStrains = Array.Empty<double>();
    private double mapEndTime;

    private CancellationTokenSource? bakeCts;

    public RealtimeSunnyWidget()
    {
        AutoSizeAxes = Axes.Both;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        InternalChild = pill = new StarRatingDisplay(default, animated: true)
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
        };

        if (gameplayState?.Beatmap is not ManiaBeatmap mania || mania.HitObjects.Count == 0)
            return;

        maniaBeatmap = mania;
        mods = gameplayState.Mods.ToArray();
        mapEndTime = mania.HitObjects.Max(h => h.GetEndTime());

        bakeTimeline();
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        if (gameplayState == null)
            return;

        UsePersonalSunny.BindValueChanged(_ => bakeTimeline());
        Scheduler.AddDelayed(updateValue, 100, true);
    }

    private void bakeTimeline()
    {
        if (maniaBeatmap is not { } mania)
            return;

        bakeCts?.Cancel();
        bakeCts?.Dispose();
        bakeCts = new CancellationTokenSource();
        var token = bakeCts.Token;

        bool personal = UsePersonalSunny.Value;
        var localMods = mods;

        Task.Run(() =>
        {
            try
            {
                (double Time, double Strain)[] raw = personal
                    ? SunnyConstants.WithIsolatedDiff(PersonalDiff.CombinedWithUniversal(),
                        () => new SunnyManiaDifficultyCalculator().GetStrainTimeline(mania, localMods, token))
                    : new SunnyManiaDifficultyCalculator().GetStrainTimeline(mania, localMods, token);

                if (token.IsCancellationRequested || raw.Length == 0)
                    return;

                double[] times = raw.Select(p => p.Time).ToArray();
                double[] strains = raw.Select(p => p.Strain).ToArray();

                Schedule(() =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    noteTimes = times;
                    noteStrains = strains;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] RealtimeSunnyWidget bake failed: {e}");
            }
        }, token);
    }

    private void updateValue()
    {
        if (noteTimes.Length == 0)
            return;

        double t = gameplayClock?.CurrentTime ?? Clock.CurrentTime;
        double windowEnd = Math.Min(t + window_ms, mapEndTime);

        double raw = windowStrainMean(t, windowEnd);
        double sr = raw <= 0 ? 0.0 : SunnyTempNerf.Apply(raw) * difficulty_multiplier;

        pill.Current.Value = new StarDifficulty(sr, 0);
    }

    private double windowStrainMean(double start, double end)
    {
        if (end <= start)
            return 0.0;

        int lo = lowerBound(noteTimes, start);

        double sum = 0.0;
        int count = 0;
        for (int i = lo; i < noteTimes.Length && noteTimes[i] < end; i++)
        {
            sum += noteStrains[i];
            count++;
        }

        return count == 0 ? 0.0 : sum / count;
    }

    private static int lowerBound(double[] array, double value)
    {
        int lo = 0, hi = array.Length;

        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (array[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        bakeCts?.Cancel();
        bakeCts?.Dispose();
        Scheduler.CancelDelayedTasks();
    }
}
