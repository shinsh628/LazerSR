using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LazerSR.Hook.Drawables;
using LazerSR.Hook.SunnySort;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Screens.Select;
using osuTK;

namespace LazerSR.Hook.Patches;

/// <summary>
/// 평면 캐러셀 패널(<c>PanelBeatmapStandalone</c>)에 sunny pill + 회전 금색 테두리 장식을 붙인다.
/// 풀 슬롯당 1회(<c>LoadComplete</c>). 매 프레임 로직·조건 판정은 <see cref="SunnySortPanelDecoration"/>가 한다.
/// </summary>
[HarmonyPatch]
public static class SunnySortPanelPatch
{
    private static readonly ConditionalWeakTable<object, object> attached = new();

    public static MethodBase? TargetMethod()
    {
        var t = AccessTools.TypeByName("osu.Game.Screens.Select.PanelBeatmapStandalone");
        return t == null ? null : AccessTools.Method(t, "LoadComplete");
    }

    public static bool Prepare() => TargetMethod() != null;

    public static void Postfix(object __instance)
    {
        try
        {
            if (__instance is not Panel panel)
                return;
            if (attached.TryGetValue(__instance, out _))
                return;

            attached.Add(__instance, new object());

            // 기존 SR pill과 같은 flow에 sunny pill을 하나 더 (dots 뒤 - 안전한 위치).
            if (AccessTools.Field(panel.GetType(), "starRatingDisplay")?.GetValue(panel) is not Drawable srPill)
                return;
            if (srPill.Parent is not Container<Drawable> flow)
                return;

            var pill = new StarRatingDisplay(default, StarRatingDisplaySize.Small, animated: true)
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Scale = new Vector2(0.875f),
                Alpha = 0f,
            };
            flow.Add(pill);

            panel.TopLevelContent.Add(new SunnySortPanelDecoration(panel, pill));
        }
        catch (Exception)
        {
        }
    }
}
