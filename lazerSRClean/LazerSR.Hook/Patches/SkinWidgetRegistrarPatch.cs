using System;
using System.Linq;
using HarmonyLib;
using LazerSR.Hook.Widgets;
using osu.Game.Rulesets;
using osu.Game.Skinning;

namespace LazerSR.Hook.Patches;

[HarmonyPatch(typeof(SerialisedDrawableInfo), nameof(SerialisedDrawableInfo.GetAllAvailableDrawables))]
public static class SkinWidgetRegistrarPatch
{
    private static readonly Type[] LazerSRWidgets = [typeof(BoxElementPlus), typeof(ClassicAccuracyWidget), typeof(DanInfoWidget), typeof(KeyViewerWidget), typeof(ManiaPositionAdjustWidget), typeof(MsdSkillsetWidget), typeof(PatternCopyStatusWidget), typeof(PersonalSunnyWidget), typeof(RealtimeSunnyWidget), typeof(ReplayCompareWidget), typeof(SectionTimerWidget), typeof(StrainGraphWidget), typeof(SunnyPPWidget), typeof(TrainingAccuracyWidget), typeof(TrainingStatusWidget)];

    public static void Postfix(ref Type[] __result, RulesetInfo? ruleset)
    {
        try
        {
            if (ruleset != null && ruleset.ShortName != "mania")
                return;

            __result = __result.Concat(LazerSRWidgets).OrderBy(t => t.Name).ToArray();
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] SkinWidgetRegistrarPatch.Postfix failed: {e}");
        }
    }
}
