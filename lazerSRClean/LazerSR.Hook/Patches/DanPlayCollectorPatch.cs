using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using LazerSR.Hook.DanRating;
using LazerSR.Hook.Ipc;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;
using osu.Game.Beatmaps;
using osu.Game.Scoring;
using osu.Game.Screens.Play;

namespace LazerSR.Hook.Patches;

/// <summary>
/// <see cref="Player.ImportScore"/> Postfix — same target/logic as
/// <see cref="PersonalSunnyScoreCollectorPatch"/> / <see cref="ReplayAutoUploadPatch"/>:
/// every real solo/multi completion, with training/section-practice/replay Players
/// auto-excluded (they override ImportScore without <c>base</c>).
/// <para>
/// Builds the per-play dan record (<see cref="DanPlayRecordBuilder"/>), writes it to
/// the dan-play queue, and pings the launcher to drain it. Read-only — reads the
/// finished <see cref="Score"/> and a working beatmap, writes only the local queue file.
/// </para>
/// </summary>
[HarmonyPatch]
public static class DanPlayCollectorPatch
{
    private static BeatmapManager? _beatmapManager;

    public static MethodBase? TargetMethod() => AccessTools.Method(typeof(Player), "ImportScore");

    public static bool Prepare() => TargetMethod() != null;

    public static void Postfix(object __instance, Score score, Task __result)
    {
        try
        {
            if (__instance is not CompositeDrawable owner) return;

            var scoreInfo = score?.ScoreInfo;
            if (scoreInfo == null || scoreInfo.Ruleset.OnlineID != 3) return;

            resolveDeps(owner);
            var beatmapManager = _beatmapManager;
            if (beatmapManager == null)
            {
                HookLog.Write("[LazerSR] DanPlayCollectorPatch: no BeatmapManager, skipping.");
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    if (__result != null)
                    {
                        try { await __result.ConfigureAwait(false); }
                        catch { /* import failure is osu!'s to log */ }
                    }

                    var working = beatmapManager.GetWorkingBeatmap(scoreInfo.BeatmapInfo);
                    var record = await DanPlayRecordBuilder.BuildAsync(scoreInfo, working).ConfigureAwait(false);
                    if (record == null)
                    {
                        HookLog.Write($"[LazerSR] DanPlayCollectorPatch: no dan record for {scoreInfo.ID} (not rateable).");
                        return;
                    }

                    if (DanPlayQueueWriter.WriteEntry(scoreInfo, record))
                        await PipeServer.BroadcastAsync("danplayqueued").ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    HookLog.Write($"[LazerSR] DanPlayCollectorPatch worker failed: {e}");
                }
            });
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] DanPlayCollectorPatch.Postfix failed: {e}");
        }
    }

    private static void resolveDeps(CompositeDrawable owner)
    {
        if (_beatmapManager != null) return;
        try
        {
            var deps = AccessTools.Property(typeof(CompositeDrawable), "Dependencies")?.GetValue(owner)
                as IReadOnlyDependencyContainer;
            _beatmapManager = deps?.Get(typeof(BeatmapManager)) as BeatmapManager;
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] DanPlayCollectorPatch.resolveDeps failed: {e}");
        }
    }
}
