using System;
using HarmonyLib;
using LazerSR.Hook.LazerSrLeaderboard;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Containers;
using osu.Game.Scoring;
using osu.Game.Screens.Ranking;

namespace LazerSR.Hook.Patches;

/// <summary>
/// 결과창의 <see cref="ReplayDownloadButton"/>이 lazerSR 리더보드 스코어를 가리킬 때, 다운로드
/// 버튼 동작을 우리 경로(<see cref="LazerSrReplayFetcher"/>)로 바꾼다. 그 외 스코어는 osu 원본 동작.
/// </summary>
[HarmonyPatch(typeof(ReplayDownloadButton), "load")]
public static class LazerSrReplayDownloadPatch
{
    public static bool Prepare() => AccessTools.Method(typeof(ReplayDownloadButton), "load") != null;

    // load(OsuGame? game, ScoreModelDownloader scoreDownloader)
    public static void Postfix(object __instance, object __0)
    {
        try
        {
            var rdb = (CompositeDrawable)__instance;
            object? osuGame = __0; // OsuGame? (nullable)

            var scoreBindable = AccessTools.Field(typeof(ReplayDownloadButton), "Score")?.GetValue(rdb)
                as Bindable<ScoreInfo?>;
            if (scoreBindable == null) return;

            object? button = AccessTools.Field(typeof(ReplayDownloadButton), "button")?.GetValue(rdb);
            if (button == null) return;

            // ClickableContainer.Action은 osu.Framework 버전에 따라 프로퍼티이거나 필드다 — 둘 다 시도.
            var actionProp = AccessTools.Property(button.GetType(), "Action");
            var actionField = actionProp == null ? AccessTools.Field(button.GetType(), "Action") : null;
            if (actionProp == null && actionField == null)
            {
                HookLog.Write("[LazerSR] LazerSrReplayDownloadPatch: Action member not found.");
                return;
            }

            var original = (actionProp?.GetValue(button) ?? actionField?.GetValue(button)) as Action;

            Action wrapped = () =>
            {
                var score = scoreBindable.Value;
                if (score != null && LazerSrScoreFactory.IsOurs(score))
                {
                    LazerSrReplayFetcher.Watch(score, osuGame);
                    return;
                }

                original?.Invoke();
            };

            if (actionProp != null)
                actionProp.SetValue(button, wrapped);
            else
                actionField!.SetValue(button, wrapped);
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] LazerSrReplayDownloadPatch.Postfix failed: {e}");
        }
    }
}
