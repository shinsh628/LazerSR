using System;
using System.Collections;
using System.Collections.Specialized;
using System.Reflection;
using HarmonyLib;
using LazerSR.Hook.SunnySort;
using osu.Game.Beatmaps;

namespace LazerSR.Hook.Patches;

/// <summary>
/// 트리거 ④ — 게임에 새 맵이 추가되면(임포트 등) 자동으로 sunny 캐시 큐에 넣는다.
/// osu가 이미 갖고 있는 캐러셀 갱신 알림(<c>BeatmapCarousel.beatmapSetsChanged</c>, 업데이트 스레드에서
/// 스케줄됨)을 관찰만 한다 — 우리가 realm 알림을 새로 구독하지 않는다.
/// </summary>
[HarmonyPatch]
public static class SunnySortNewMapPatch
{
    public static MethodBase? TargetMethod()
    {
        var t = AccessTools.TypeByName("osu.Game.Screens.Select.BeatmapCarousel");
        return t == null ? null : AccessTools.Method(t, "beatmapSetsChanged");
    }

    public static bool Prepare() => TargetMethod() != null;

    public static void Postfix(NotifyCollectionChangedEventArgs changed)
    {
        try
        {
            if (changed.Action != NotifyCollectionChangedAction.Add || changed.NewItems == null)
                return;

            foreach (var obj in (IEnumerable)changed.NewItems)
            {
                if (obj is not BeatmapSetInfo set)
                    continue;

                foreach (var b in set.Beatmaps)
                {
                    if (b.Ruleset.OnlineID != 3) // 모든 mania (키 수 무관)
                        continue;

                    SunnySortWorker.Enqueue(b.Hash);
                }
            }
        }
        catch (Exception)
        {
        }
    }
}
