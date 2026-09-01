using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using LazerSR.Hook.SunnySort;
using osu.Game.Beatmaps;
using osu.Game.Graphics.Carousel;
using osu.Game.Screens.Select;

namespace LazerSR.Hook.Patches;

/// <summary>
/// 정렬 토글이 켜져 있으면 캐러셀 정렬 결과를 sunny 캐시값 오름차순(계산 안 된 맵은 맨 아래)으로 재배치.
/// osu의 <see cref="BeatmapCarouselFilterSorting"/>는 그대로 돌게 두고 결과 Task만 감싼다.
/// </summary>
[HarmonyPatch(typeof(BeatmapCarouselFilterSorting), nameof(BeatmapCarouselFilterSorting.Run))]
public static class SunnySortSortingPatch
{
    public static void Postfix(ref Task<List<CarouselItem>> __result)
    {
        var inner = __result;
        __result = reorder(inner);
    }

    private static async Task<List<CarouselItem>> reorder(Task<List<CarouselItem>> inner)
    {
        var list = await inner.ConfigureAwait(false);

        try
        {
            if (!SunnySortState.SortActive)
                return list;

            double rate = SunnySortState.ActiveRate;
            var ordered = list.OrderBy(item => sortKey(item, rate)).ToList();
            return ordered;
        }
        catch (Exception)
        {
            return list;
        }
    }

    private static double sortKey(CarouselItem item, double rate)
    {
        if (item.Model is not BeatmapInfo bi)
            return double.MaxValue;

        return SunnySortCache.TryGet(bi.Hash, rate, out double sr) ? sr : double.MaxValue;
    }
}

/// <summary>정렬 토글이 켜져 있으면 세트 그룹핑을 끄고 평면 난이도 목록으로 편다 (osu의 Difficulty 정렬과 동일 경로).</summary>
[HarmonyPatch(typeof(BeatmapCarouselFilterGrouping), nameof(BeatmapCarouselFilterGrouping.ShouldGroupBeatmapsTogether))]
public static class SunnySortGroupingPatch
{
    public static void Postfix(ref bool __result)
    {
        if (SunnySortState.SortActive)
            __result = false;
    }
}

/// <summary>범위 슬라이더가 기본값이 아니면, 현재 활성 rate의 sunny 캐시값이 범위 밖인 맵을 숨긴다. 캐시 안 된 맵은 통과.</summary>
[HarmonyPatch(typeof(BeatmapCarouselFilterMatching), nameof(BeatmapCarouselFilterMatching.CheckCriteriaMatch))]
public static class SunnySortMatchingPatch
{
    public static void Postfix(BeatmapInfo beatmap, ref bool __result)
    {
        if (!__result || !SunnySortState.RangeActive)
            return;

        try
        {
            double rate = SunnySortState.ActiveRate;
            if (!SunnySortCache.TryGet(beatmap.Hash, rate, out double sr))
                return;

            bool aboveMin = sr >= SunnySortState.RangeMin;
            bool belowMax = SunnySortState.RangeMax >= SunnySortState.RangeUpper || sr <= SunnySortState.RangeMax;

            if (!(aboveMin && belowMax))
                __result = false;
        }
        catch (Exception)
        {
        }
    }
}
