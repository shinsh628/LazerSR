using System;
using System.Reflection;
using HarmonyLib;
using LazerSR.Hook.PersonalSunny;
using osu.Game.Screens.Play;

namespace LazerSR.Hook.Patches;

/// <summary>
/// Tracks whether a <see cref="Player"/> screen is currently active - purely so
/// <see cref="PersonalSunnyService"/>'s background pre-computation worker can throttle down during real
/// gameplay to avoid CPU contention with rendering/input handling. Read-only signal; never touches
/// gameplay state, scoring, or any live instance.
/// <para>
/// Fires for every <see cref="Player"/> subclass (training/section-practice/replay included) - the
/// concern here is CPU contention, which applies regardless of which kind of session is rendering, unlike
/// <c>PlayerGameplayPatch</c>'s InfiniteTrainingPlayer exclusion (a replay-compare-specific concern).
/// </para>
/// </summary>
[HarmonyPatch]
public static class PersonalSunnyGameplayEnterPatch
{
    public static MethodBase? TargetMethod() => AccessTools.Method(typeof(Player), "LoadComplete");

    public static bool Prepare() => TargetMethod() != null;

    public static void Postfix()
    {
        try
        {
            PersonalSunnyService.GameplayActive = true;
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] PersonalSunnyGameplayEnterPatch.Postfix failed: {e}");
        }
    }
}

/// <summary>Companion to <see cref="PersonalSunnyGameplayEnterPatch"/> - clears the flag when another screen (e.g. results) is pushed over the player.</summary>
[HarmonyPatch]
public static class PersonalSunnyGameplaySuspendPatch
{
    public static MethodBase? TargetMethod() => AccessTools.Method(typeof(Player), "OnSuspending");

    public static bool Prepare() => TargetMethod() != null;

    public static void Postfix()
    {
        try
        {
            PersonalSunnyService.GameplayActive = false;
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] PersonalSunnyGameplaySuspendPatch.Postfix failed: {e}");
        }
    }
}

/// <summary>Companion to <see cref="PersonalSunnyGameplayEnterPatch"/> - clears the flag when the player screen exits outright.</summary>
[HarmonyPatch]
public static class PersonalSunnyGameplayExitPatch
{
    public static MethodBase? TargetMethod() => AccessTools.Method(typeof(Player), "OnExiting");

    public static bool Prepare() => TargetMethod() != null;

    public static void Postfix()
    {
        try
        {
            PersonalSunnyService.GameplayActive = false;
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] PersonalSunnyGameplayExitPatch.Postfix failed: {e}");
        }
    }
}
