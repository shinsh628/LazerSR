using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using LazerSR.Hook.Ipc;
using LazerSR.Hook.LazerSrLeaderboard;
using osu.Framework.Bindables;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Online.Leaderboards;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;

namespace LazerSR.Hook.Patches;

/// <summary>
/// "lazerSR" 리더보드 토글이 켜져 있으면 <see cref="LeaderboardManager.FetchWithCriteria"/>를 통째로
/// 가로채 우리 서버 결과로 대체한다. osu의 로그인/OnlineID/Status/서포터 게이트를 전부 건너뛰므로
/// 무덤·러브드·미제출 맵에서도 동작한다.
/// <para>
/// 네트워크는 Hook이 직접 안 한다(레드라인). <see cref="PipeServer.RequestAsync"/>로 런처에 물어보고,
/// 런처가 서버 HTTP를 대신 친다. Prefix가 <c>false</c>를 반환하는 유일한 근거는 "원본 실행 자체를
/// 막아야 함"이고, 제출되는 점수·판정·리플레이 값은 읽지도 쓰지도 않는다(safety.md).
/// </para>
/// </summary>
[HarmonyPatch]
public static class LazerSrLeaderboardFetchPatch
{
    private static int generation;

    public static MethodBase? TargetMethod() =>
        AccessTools.Method(typeof(LeaderboardManager), nameof(LeaderboardManager.FetchWithCriteria));

    public static bool Prepare() => TargetMethod() != null;

    public static bool Prefix(LeaderboardManager __instance, LeaderboardCriteria newCriteria)
    {
        if (!LazerSrLeaderboardState.Enabled.Value)
            return true; // 평소 osu 경로

        try
        {
            handle(__instance, newCriteria);
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] LazerSrLeaderboardFetchPatch failed: {e}");
        }

        return false;
    }

    private static void handle(LeaderboardManager manager, LeaderboardCriteria criteria)
    {
        AccessTools.PropertySetter(typeof(LeaderboardManager), nameof(LeaderboardManager.CurrentCriteria))
            .Invoke(manager, new object[] { criteria });

        var scores = (Bindable<LeaderboardScores?>)manager.Scores;
        scores.Value = null;

        BeatmapInfo? beatmap = criteria.Beatmap;
        RulesetInfo? ruleset = criteria.Ruleset;

        if (beatmap == null || ruleset == null)
        {
            scores.Value = LeaderboardScores.Failure(LeaderboardFailState.NoneSelected);
            return;
        }

        if (ruleset.OnlineID != 3)
        {
            scores.Value = LeaderboardScores.Failure(LeaderboardFailState.RulesetUnavailable);
            return;
        }

        // 업로드가 저장한 것과 같은 값을 써야 한다: ScoreInfo.BeatmapHash == BeatmapInfo.Hash (SHA-256).
        // 서버 컬럼 이름은 beatmap_md5지만 실제 내용은 이 해시다.
        string beatmapHash = beatmap.Hash;
        string modsToken = toModsToken(criteria.ExactMods);
        int gen = ++generation;

        Task.Run(async () =>
        {
            string json;
            try
            {
                json = await PipeServer.RequestAsync("lbreq", $"{beatmapHash}:{modsToken}", 10_000).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] leaderboard fetch failed: {e}");
                schedule(manager, () =>
                {
                    if (gen == generation)
                        scores.Value = LeaderboardScores.Failure(LeaderboardFailState.NetworkFailure);
                });
                return;
            }

            var infos = LazerSrScoreFactory.Parse(json, beatmap, ruleset);
            schedule(manager, () =>
            {
                if (gen != generation) return;
                scores.Value = LeaderboardScores.Success(infos, scoresRequested: infos.Length, totalScores: infos.Length, userScore: null);
            });
        });
    }

    private static void schedule(LeaderboardManager manager, Action action)
    {
        try
        {
            var scheduler = Traverse.Create(manager).Property("Scheduler").GetValue<Scheduler>();
            if (scheduler != null)
            {
                scheduler.Add(action);
                return;
            }
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] LazerSrLeaderboardFetchPatch.schedule fallback: {e}");
        }

        action(); // 최후의 수단
    }

    private static string toModsToken(Mod[]? exactMods)
    {
        if (exactMods == null) return "*";      // 필터 없음 — 전체
        if (exactMods.Length == 0) return "-";  // 모드 없는 기록만
        return string.Join(",", exactMods.Select(m => m.Acronym));
    }
}
