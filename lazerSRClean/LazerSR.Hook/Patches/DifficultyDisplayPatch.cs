using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using LazerSR.Hook.Calculators;
using LazerSR.Hook.Data;
using LazerSR.Hook.Ipc;
using LazerSR.SunnyCalculator;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Game.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;

namespace LazerSR.Hook.Patches;

/// <summary>
/// Hook on BeatmapTitleWedge.LoadComplete. Does two things:
///  1) Orchestrator — calculates sunnySR on beatmap/ruleset/mods change,
///     publishes to SunnyState.CurrentSr and broadcasts to launcher.
///  2) Location 1.1 — finds the inner DifficultyDisplay and inserts a
///     sunnySR pill bound to SunnyState.CurrentSr next to the original SR pill.
/// </summary>
[HarmonyPatch]
public static class DifficultyDisplayPatch
{
    private const string TARGET_TYPE_NAME = "osu.Game.Screens.Select.BeatmapTitleWedge";
    private const string CHILD_TYPE_NAME  = "osu.Game.Screens.Select.BeatmapTitleWedge+DifficultyDisplay";
    private static CancellationTokenSource? _cts;

    public static MethodBase? TargetMethod()
    {
        Type? type = AccessTools.TypeByName(TARGET_TYPE_NAME);
        return type == null ? null : AccessTools.Method(type, "LoadComplete");
    }

    public static bool Prepare() => TargetMethod() != null;

    public static void Postfix(object __instance)
    {
        try
        {
            Type type = __instance.GetType();

            if (!AccessHelper.TryGet<IBindable<WorkingBeatmap>>(type, "working", __instance, out var workingBindable) || workingBindable == null)
            {
                HookLog.Write("[LazerSR] DifficultyDisplayPatch: 'working' field not found.");
                return;
            }

            if (!AccessHelper.TryGet<IBindable<RulesetInfo>>(type, "ruleset", __instance, out var rulesetBindable) || rulesetBindable == null)
            {
                HookLog.Write("[LazerSR] DifficultyDisplayPatch: 'ruleset' field not found.");
                return;
            }

            if (!AccessHelper.TryGet<IBindable<IReadOnlyList<Mod>>>(type, "mods", __instance, out var modsBindable) || modsBindable == null)
            {
                HookLog.Write("[LazerSR] DifficultyDisplayPatch: 'mods' field not found.");
                return;
            }

            var scheduleOwner  = __instance as Drawable;
            var scheduleMethod = AccessTools.Method(typeof(Drawable), "Schedule", new[] { typeof(Action) });

            // Realm + API provider (best score lookup)
            var depsProperty = AccessTools.Property(typeof(CompositeDrawable), "Dependencies");
            var deps = depsProperty?.GetValue(scheduleOwner) as IReadOnlyDependencyContainer;
            var realmAccess = deps?.Get(typeof(RealmAccess)) as RealmAccess;
            var apiProvider = deps?.Get(typeof(IAPIProvider)) as IAPIProvider;
            var storage = deps?.Get(typeof(osu.Framework.Platform.Storage)) as osu.Framework.Platform.Storage;
            var scoreManager = deps?.Get(typeof(osu.Game.Scoring.ScoreManager)) as osu.Game.Scoring.ScoreManager;
            var beatmapManager = deps?.Get(typeof(BeatmapManager)) as BeatmapManager;
            var rulesetStore = deps?.Get(typeof(RulesetStore)) as RulesetStore;
            ReplayUpload.HookRuntimeContext.Populate(realmAccess, storage, apiProvider, scoreManager, beatmapManager, rulesetStore);

            void Recalculate()
            {
                var cts = new CancellationTokenSource();
                Interlocked.Exchange(ref _cts, cts)?.Cancel();
                // Reset on update thread so widget triggers immediately (shows N/A while computing)
                SunnyState.CurrentDominant.Value = string.Empty;

                WorkingBeatmap working = workingBindable.Value;
                RulesetInfo ruleset = rulesetBindable.Value;
                IReadOnlyList<Mod> mods = modsBindable.Value;
                CancellationToken token = cts.Token;

                if (ruleset.OnlineID != 3)
                {
                    SectionTimerState.PendingSections = null;
                    _ = PipeServer.BroadcastAsync("sunnysr:N/A");
                    _ = PipeServer.BroadcastAsync("dominant:N/A");
                    _ = PipeServer.BroadcastAsync("bestacc:N/A");
                    return;
                }

                Task.Run(async () =>
                {
                    try
                    {
                        double sr = SunnyRunner.Calculate(working, ruleset, mods, token);
                        if (!token.IsCancellationRequested)
                        {
                            scheduleMethod?.Invoke(scheduleOwner, new object[] { (Action)(() => SunnyState.CurrentSr.Value = new StarDifficulty(sr, 0)) });
                            await PipeServer.BroadcastAsync($"sunnysr:{sr:F2}");
                        }

                        // Best score lookup (Realm 쿼리, 빠름 — 취소 불필요)
                        string bestAccText = "N/A";
                        if (realmAccess != null)
                        {
                            try
                            {
                                string beatmapHash = working.BeatmapInfo.Hash;
                                if (!string.IsNullOrEmpty(beatmapHash))
                                {
                                    var currentApiMods = mods.Select(m => new APIMod(m)).ToArray();
                                    string? username = apiProvider?.LocalUser.Value.Username;
                                    if (string.IsNullOrEmpty(username))
                                        username = null;

                                    double? acc = BestScoreFinder.FindBestAccuracy(
                                        realmAccess, beatmapHash, currentApiMods, username);

                                    if (acc.HasValue)
                                        bestAccText = $"{acc.Value * 100:F2}%";
                                }
                            }
                            catch (Exception ex)
                            {
                                HookLog.Write($"[LazerSR] BestScoreFinder failed: {ex.Message}");
                            }
                        }
                        if (!token.IsCancellationRequested)
                            await PipeServer.BroadcastAsync($"bestacc:{bestAccText}");

                        token.ThrowIfCancellationRequested();
                        // 1.0x rate playable: time alignment between sunny and MinaCalc
                        var playable = working.GetPlayableBeatmap(ruleset, Array.Empty<Mod>(), token);
                        token.ThrowIfCancellationRequested();

                        var strainTimeline = new SunnyManiaDifficultyCalculator().GetStrainTimeline(playable, null, token);
                        token.ThrowIfCancellationRequested();

                        // Section timer: pre-compute hard/easy sections for gameplay widget
                        var sections = SectionTimeline.Calculate(strainTimeline, token: token);
                        SectionTimerState.PendingSections = sections.Length > 0 ? sections : null;
                        token.ThrowIfCancellationRequested();

                        string dominant = "N/A";
                        string dominantAbbr = string.Empty;
                        if (strainTimeline.Length > 0)
                        {
                            double maxStrain = 0;
                            foreach (var (_, s) in strainTimeline)
                                if (s > maxStrain) maxStrain = s;

                            double threshold = maxStrain * 0.8;
                            var highStrainTimes = new HashSet<int>();
                            foreach (var (timeMs, strain) in strainTimeline)
                                if (strain >= threshold)
                                    highStrainTimes.Add((int)Math.Round(timeMs));

                            token.ThrowIfCancellationRequested();
                            var sectionMsd = MsdCalculator.CalculateFiltered(playable, highStrainTimes);
                            if (sectionMsd != null)
                            {
                                dominant = GetDominant(sectionMsd);
                                dominantAbbr = dominant.Split(' ')[0];
                            }
                        }
                        if (!token.IsCancellationRequested)
                        {
                            string abbr = dominantAbbr;
                            scheduleMethod?.Invoke(scheduleOwner, new object[] { (Action)(() => SunnyState.CurrentDominant.Value = abbr) });
                        }
                        await PipeServer.BroadcastAsync($"dominant:{dominant}");
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        HookLog.Write($"[LazerSR] sunnySR/dominant calculation failed: {ex.Message}");
                    }
                }, token);
            }

            workingBindable.BindValueChanged(_ => Recalculate());
            rulesetBindable.BindValueChanged(_ => Recalculate());
            ModSettingChangeTracker? modTracker = null;
            modsBindable.BindValueChanged(e =>
            {
                modTracker?.Dispose();
                modTracker = new ModSettingChangeTracker(e.NewValue);
                modTracker.SettingChanged += _ => Recalculate();
                Recalculate();
            });

            // Location 1.1: insert sunnySR pill into the inner DifficultyDisplay's GridContainer
            if (__instance is Drawable owner)
                InsertSunnyPill_1_1(owner);
        }
        catch (Exception ex)
        {
            HookLog.Write($"[LazerSR] DifficultyDisplayPatch.Postfix failed: {ex}");
        }
    }

    private static void InsertSunnyPill_1_1(Drawable owner)
    {
        try
        {
            Type? difficultyDisplayType = AccessTools.TypeByName(CHILD_TYPE_NAME);
            if (difficultyDisplayType == null)
            {
                HookLog.Write("[LazerSR] InsertSunnyPill_1_1: DifficultyDisplay type not found.");
                return;
            }

            var dd = FindFirstChildOfType(owner, difficultyDisplayType);
            if (dd == null)
            {
                HookLog.Write("[LazerSR] InsertSunnyPill_1_1: DifficultyDisplay child not found.");
                return;
            }

            if (!AccessHelper.TryGet<GridContainer>(difficultyDisplayType, "ratingAndNameContainer", dd, out var container) || container == null)
            {
                HookLog.Write("[LazerSR] InsertSunnyPill_1_1: 'ratingAndNameContainer' not found.");
                return;
            }

            var pill = new StarRatingDisplay(default, animated: true)
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
            };
            pill.Current = SunnyState.CurrentSr;

            var dimsField   = AccessTools.Field(typeof(GridContainer), "columnDimensions");
            var dimsProp    = AccessTools.Property(typeof(GridContainer), "ColumnDimensions");
            var contentProp = AccessTools.Property(typeof(GridContainer), "Content");
            if (dimsField == null || dimsProp == null || contentProp == null) return;

            var existingDims = dimsField.GetValue(container) as Dimension[] ?? Array.Empty<Dimension>();

            var gridContent = contentProp.GetValue(container);
            if (gridContent == null) return;
            int rowCount = (int)(gridContent.GetType().GetProperty("Count")?.GetValue(gridContent) ?? 0);
            if (rowCount == 0) return;
            var itemProp = gridContent.GetType().GetProperty("Item");
            var row0 = itemProp?.GetValue(gridContent, new object[] { 0 });
            if (row0 == null) return;

            var existingCells = new List<Drawable>();
            foreach (var item in (IEnumerable)row0)
                if (item is Drawable d) existingCells.Add(d);

            // col 0(SR pill) 바로 뒤에 삽입:
            //   [SR pill | 4px | sunny pill | 기존 나머지...]
            const int inserted = 2;
            var newDims = new Dimension[existingDims.Length + inserted];
            newDims[0] = existingDims[0];
            newDims[1] = new Dimension(GridSizeMode.Absolute, 4);
            newDims[2] = new Dimension(GridSizeMode.AutoSize);
            for (int i = 1; i < existingDims.Length; i++)
                newDims[i + inserted] = existingDims[i];

            var newRow = new Drawable[existingCells.Count + inserted];
            newRow[0] = existingCells[0];
            newRow[1] = new Container();
            newRow[2] = pill;
            for (int i = 1; i < existingCells.Count; i++)
                newRow[i + inserted] = existingCells[i];

            dimsProp.SetValue(container, newDims);

            var opImplicit = typeof(GridContainerContent).GetMethod("op_Implicit", new[] { typeof(Drawable[][]) });
            var newContent = opImplicit?.Invoke(null, new object[] { new Drawable[][] { newRow } });
            contentProp.SetValue(container, newContent);
            HookLog.Write("[LazerSR] InsertSunnyPill_1_1: sunny pill inserted.");
        }
        catch (Exception ex)
        {
            HookLog.Write($"[LazerSR] InsertSunnyPill_1_1 failed: {ex}");
        }
    }

    private static Drawable? FindFirstChildOfType(Drawable root, Type targetType)
    {
        if (targetType.IsInstanceOfType(root)) return root;
        if (root is not CompositeDrawable composite) return null;

        var prop = AccessTools.Property(typeof(CompositeDrawable), "InternalChildren");
        if (prop?.GetValue(composite) is not IEnumerable children) return null;

        foreach (var child in children)
        {
            if (child is Drawable d)
            {
                var found = FindFirstChildOfType(d, targetType);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static string GetDominant(MsdData msd)
    {
        (float val, string name, float weight)[] skills =
        [
            (msd.Stream,     "STR", 1.0f),
            (msd.Jumpstream, "JS",  1.0f),
            (msd.Handstream, "HS",  1.0f),
            (msd.Jackspeed,  "JK",  1.0f),
            (msd.Chordjack,  "CJ",  1.0f),
            (msd.Technical,  "TEC", 0.9f),
        ];

        int best = 0;
        for (int i = 1; i < skills.Length; i++)
            if (skills[i].val * skills[i].weight > skills[best].val * skills[best].weight) best = i;

        return $"{skills[best].name} ({skills[best].val:F2})";
    }
}
