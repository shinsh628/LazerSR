using System;
using System.Reflection;
using HarmonyLib;
using LazerSR.Hook.SunnySort;
using osu.Game.Screens.Select;

namespace LazerSR.Hook.Patches;

/// <summary>선곡 화면 로드 시 <see cref="BeatmapCarousel"/> / <see cref="FilterControl"/> 인스턴스를 잡아둔다.</summary>
[HarmonyPatch]
public static class SunnySortCarouselRefPatch
{
    public static MethodBase? TargetMethod()
    {
        var t = AccessTools.TypeByName("osu.Game.Screens.Select.BeatmapCarousel");
        return t == null ? null : AccessTools.Method(t, "LoadComplete");
    }

    public static bool Prepare() => TargetMethod() != null;

    public static void Postfix(object __instance)
    {
        try
        {
            if (__instance is BeatmapCarousel carousel)
            {
                SunnySortRefs.Carousel = carousel;
            }
        }
        catch (Exception)
        {
        }
    }
}

[HarmonyPatch]
public static class SunnySortFilterControlRefPatch
{
    public static MethodBase? TargetMethod()
    {
        var t = AccessTools.TypeByName("osu.Game.Screens.Select.FilterControl");
        return t == null ? null : AccessTools.Method(t, "LoadComplete");
    }

    public static bool Prepare() => TargetMethod() != null;

    public static void Postfix(object __instance)
    {
        try
        {
            if (__instance is FilterControl fc)
            {
                SunnySortRefs.FilterControl = fc;
            }
        }
        catch (Exception)
        {
        }
    }
}
