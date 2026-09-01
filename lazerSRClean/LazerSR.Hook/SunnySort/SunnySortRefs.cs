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

    /// <summary>정렬이 켜져 있는 동안 osu의 정렬·그룹 드롭다운을 비활성화한다(우리가 순서를 지배하므로).</summary>
    public static void SetFilterControlsDisabled(bool disabled)
    {
        var fc = FilterControl;
        if (fc == null)
            return;

        try
        {
            foreach (string fieldName in new[] { "sortDropdown", "groupDropdown" })
            {
                object? dropdown = AccessTools.Field(fc.GetType(), fieldName)?.GetValue(fc);
                if (dropdown == null)
                    continue;

                if (AccessTools.Property(dropdown.GetType(), "Current")?.GetValue(dropdown) is IBindable current)
                    AccessTools.Property(current.GetType(), "Disabled")?.SetValue(current, disabled);
            }
        }
        catch (Exception)
        {
        }
    }
}
