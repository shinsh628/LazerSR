using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using LazerSR.Hook.DanRating;
using LazerSR.Hook.Ipc;
using LazerSR.Hook.ReplayUpload;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;
using osu.Game.Beatmaps;
using osu.Game.Scoring;
using osu.Game.Screens.Play;

namespace LazerSR.Hook.Patches;

/// <summary>
/// <see cref="Player.ImportScore"/> Postfix — same target/logic as
/// <see cref="PersonalSunnyScoreCollectorPatch"/> / <see cref="ReplayAutoUploadPatch"/>:
/// training/section-practice/replay Players are auto-excluded (they override
/// ImportScore without <c>base</c>).
/// <para>
/// <b>Ownership check (2026-09-14 fix)</b>: <c>SpectatorPlayer</c> does NOT override
/// <c>ImportScore</c> (verified against osu source — unlike <c>ReplayPlayer</c>/training/
/// section-practice/pattern-copy), so spectating another lazerSR user's live match calls
/// this Postfix with THEIR <see cref="ScoreInfo"/> (<c>RealmUser</c> = the spectated
/// player, not the local one). Without a check this uploads a dan record under their
/// name from our local calculation of what we watched — 6 foreign usernames showed up in
/// the server's <c>dan_plays</c> this way (progress log 2026-09-14). Same ownership check
/// as <see cref="ReplayCollectService"/>'s trigger #1 (<c>ScoreInfo.RealmUser.OnlineID ==
/// 로그인 유저 id</c>) — no .osr-header cross-check here since that guards a bulk realm
/// scan of arbitrary historical rows, not a single fresh ImportScore event.
/// </para>
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

            int localUserId = HookRuntimeContext.Api?.LocalUser.Value.Id ?? 0;
            if (localUserId <= 1 || scoreInfo.RealmUser.OnlineID != localUserId)
            {
                HookLog.Write($"[LazerSR] DanPlayCollectorPatch: skipping non-local score (owner={scoreInfo.RealmUser.OnlineID}, local={localUserId}).");
                return;
            }

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
