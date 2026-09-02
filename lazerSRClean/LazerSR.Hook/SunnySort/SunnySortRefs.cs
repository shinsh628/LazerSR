using System;
using HarmonyLib;
using osu.Framework.Bindables;
using osu.Game.Screens.Select;

namespace LazerSR.Hook.SunnySort;

/// <summary>
/// 위젯이 캐러셀/필터 컨트롤에 닿을 수 있도록 패치가 채워두는 정적 참조.
/// (스킨 위젯은 선곡 화면 트리에서 멀리 떨어져 있어 직접 부모 순회가 번거롭다.)
/// </summary>
public static class SunnySortRefs
{
    public static BeatmapCarousel? Carousel;
    public static FilterControl? FilterControl;

    /// <summary>현재 criteria 그대로 필터 체인을 다시 돌린다 — 정렬/범위 패치가 새 상태를 반영하게.</summary>
    public static void Refilter()
    {
        try
        {
            var carousel = Carousel;
            if (carousel?.Criteria == null)
            {
                return;
            }

            carousel.Filter(carousel.Criteria);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>정렬이 켜져 있는 동안 osu의 정렬 드롭다운만 비활성화한다(우리가 순서를 지배하므로).
    /// 그룹 드롭다운은 건드리지 않는다 — 세트-난이도 평탄화는 <see cref="LazerSR.Hook.Patches.SunnySortGroupingPatch"/>가
    /// criteria.Group과 무관하게 이미 처리하므로, 사용자가 고른 그룹(Artist 등)은 그대로 유지된다.</summary>
    public static void SetFilterControlsDisabled(bool disabled)
    {
        var fc = FilterControl;
        if (fc == null)
            return;

        try
        {
            object? dropdown = AccessTools.Field(fc.GetType(), "sortDropdown")?.GetValue(fc);
            if (dropdown == null)
                return;

            if (AccessTools.Property(dropdown.GetType(), "Current")?.GetValue(dropdown) is IBindable current)
                AccessTools.Property(current.GetType(), "Disabled")?.SetValue(current, disabled);
        }
        catch (Exception)
        {
        }
    }
}
