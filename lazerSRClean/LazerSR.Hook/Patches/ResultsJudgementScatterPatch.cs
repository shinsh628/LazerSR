using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LazerSR.Hook.Drawables;
using osu.Framework.Graphics;
using osu.Game.Beatmaps;
using osu.Game.Scoring;
using osu.Game.Screens.Ranking.Statistics;

namespace LazerSR.Hook.Patches;

/// <summary>
/// 결과창의 확장 통계 패널(스페이스바)에 우리 항목을 삽입한다:
///  - "Performance Breakdown" 뒤 → 맵/퍼포먼스 dan 행(<see cref="DanResultRow"/>)
///  - "Timing Distribution" 뒤 → mania 판정 산점도(<see cref="ManiaJudgementScatterGraph"/>)
/// 읽기 + 목록 concat뿐이며 스코어를 만지지 않는다. (같은 메서드에 HarmonyPatch 클래스는
/// 하나만 허용되므로 두 삽입을 이 한 클래스에서 처리한다 — ui-patching.md 함정 표)
/// </summary>
[HarmonyPatch]
public static class ResultsJudgementScatterPatch
{
    private const string TIMING_DISTRIBUTION_NAME = "Timing Distribution";
    private const string PERFORMANCE_BREAKDOWN_NAME = "Performance Breakdown";

    public static MethodBase? TargetMethod() =>
        AccessTools.Method(typeof(StatisticsPanel), "CreateStatisticItems");

    public static bool Prepare() => TargetMethod() != null;

    // playableBeatmap은 osu!가 이미 만들어 넘겨주는 변환 완료 보면이다 - 구간 sunnySR 계산에 쓰므로
    // GetPlayableBeatmap을 다시 돌리지 않는다.
    public static void Postfix(ScoreInfo newScore, IBeatmap playableBeatmap, ref IEnumerable<StatisticItem> __result)
    {
        try
        {
            if (newScore.Ruleset.OnlineID != 3) return; // mania 전용

            // 원본은 이터레이터라 지연 실행된다. 호출자가 곧바로 ToArray()로 소비하므로
            // 여기서 실체화해도 동작이 같다. (패치 클래스 안에서 yield return을 쓰면
            // 어셈블리 전체 패치 등록이 죽으므로 List로 처리한다 - ui-patching.md 함정 표)
            var items = new List<StatisticItem>(__result);

            // 맵/퍼포먼스 dan 행 — "Performance Breakdown" 뒤(= "Timing Distribution" 앞).
            // 판정 기록이 없어도 판정 카운트만으로 계산되므로 requiresHitEvents는 false.
            var danRow = new StatisticItem("DAN", () => new DanResultRow(newScore, playableBeatmap)
            {
                RelativeSizeAxes = Axes.X,
            });

            int perfIndex = items.FindIndex(i => i.Name.ToString() == PERFORMANCE_BREAKDOWN_NAME);
            if (perfIndex >= 0)
                items.Insert(perfIndex + 1, danRow);
            else
                items.Insert(0, danRow);

            // 리플레이를 재생하지 않아 판정 기록이 없으면 osu!가 이 항목을 빼고
            // "리플레이를 봐야 통계가 나온다" 안내로 대체한다 (requiresHitEvents: true).
            var scatter = new StatisticItem("판정 산점도", () => new ManiaJudgementScatterGraph(newScore, playableBeatmap)
            {
                RelativeSizeAxes = Axes.X,
                Height = 260,
            }, true);

            int index = items.FindIndex(i => i.Name.ToString() == TIMING_DISTRIBUTION_NAME);

            if (index >= 0)
                items.Insert(index + 1, scatter);
            else
                items.Add(scatter);

            __result = items;
        }
        catch (Exception ex)
        {
            HookLog.Write($"[LazerSR] ResultsJudgementScatterPatch.Postfix failed: {ex}");
        }
    }
}
