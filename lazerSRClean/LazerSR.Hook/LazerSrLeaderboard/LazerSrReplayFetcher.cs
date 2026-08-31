using System;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using LazerSR.Hook.Ipc;
using LazerSR.Hook.ReplayUpload;
using osu.Game.Database;
using osu.Game.Overlays.Notifications;
using osu.Game.Scoring;
using osu.Game.Screens;

namespace LazerSR.Hook.LazerSrLeaderboard;

/// <summary>
/// lazerSR 리더보드의 스코어를 "감상"하려 할 때: 런처를 통해 서버에서 <c>.osr</c>을 받아
/// realm에 임포트한 뒤 게임플레이 화면으로 재생한다 (G-1 방식 — <c>ScoreDownloadTracker</c>
/// 상태머신은 안 거친다).
/// </summary>
internal static class LazerSrReplayFetcher
{
    /// <param name="osuGame">패치가 넘겨준 <c>OsuGame</c> 인스턴스(리플렉션으로 <c>PresentScore</c> 호출).</param>
    public static void Watch(ScoreInfo ourScore, object? osuGame)
    {
        string? guid = LazerSrScoreFactory.ExtractGuid(ourScore);
        if (guid == null) return;

        var scoreManager = HookRuntimeContext.ScoreManager;
        if (scoreManager == null)
        {
            HookLog.Write("[LazerSR] LazerSrReplayFetcher: no ScoreManager.");
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                string path = await PipeServer.RequestAsync("lbdl", guid, 30_000).ConfigureAwait(false);

                var imported = await scoreManager
                    .Import(new ProgressNotification(), new[] { new ImportTask(path) })
                    .ConfigureAwait(false);

                var live = imported.FirstOrDefault();
                if (live == null)
                {
                    HookLog.Write($"[LazerSR] LazerSrReplayFetcher: import produced nothing for {guid}.");
                    return;
                }

                present(osuGame, live.Value);
            }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] LazerSrReplayFetcher.Watch({guid}) failed: {e}");
            }
        });
    }

    private static void present(object? osuGame, ScoreInfo score)
    {
        if (osuGame == null) return;

        try
        {
            var scheduleMethod = AccessTools.Method(typeof(osu.Framework.Graphics.Drawable), "Schedule", new[] { typeof(Action) });
            var presentMethod = AccessTools.Method(osuGame.GetType(), "PresentScore",
                new[] { typeof(osu.Game.Scoring.IScoreInfo), typeof(ScorePresentType) });

            if (presentMethod == null)
            {
                HookLog.Write("[LazerSR] LazerSrReplayFetcher: PresentScore method not found.");
                return;
            }

            Action call = () => presentMethod.Invoke(osuGame, new object[] { score, ScorePresentType.Gameplay });

            if (scheduleMethod != null)
                scheduleMethod.Invoke(osuGame, new object[] { call });
            else
                call();
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] LazerSrReplayFetcher.present failed: {e}");
        }
    }
}
