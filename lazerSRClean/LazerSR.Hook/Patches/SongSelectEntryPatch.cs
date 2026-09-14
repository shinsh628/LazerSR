using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LazerSR.Hook.Data;
using osu.Game.Screens.Select;

namespace LazerSR.Hook.Patches;

/// <summary>
/// <see cref="SongSelect.OnEntering"/>/<see cref="SongSelect.OnResuming"/> Postfix —
/// 선곡 화면이 현재 화면이 될 때마다(최초 진입 + 다른 화면에서 돌아올 때 둘 다)
/// <see cref="SongSelectEntryState.Bump"/>를 호출해 위젯들이 폴링 없이 재조회하게 한다.
/// <see cref="SoloSongSelect.OnResuming"/>이 <c>base.OnResuming</c>을 호출하는 걸 osu 소스에서
/// 확인했으므로(ReplayPlayer.ImportScore 같은 override-without-base 함정과 다름) 베이스 타입만
/// 패치해도 실제 솔로 선곡 화면에서 정상 발화한다. 읽기 전용 — Bindable 값 하나만 건드린다.
/// </summary>
[HarmonyPatch]
public static class SongSelectEntryPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SongSelect), nameof(SongSelect.OnEntering));
        yield return AccessTools.Method(typeof(SongSelect), nameof(SongSelect.OnResuming));
    }

    public static void Postfix()
    {
        try
        {
            SongSelectEntryState.Bump();
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] SongSelectEntryPatch.Postfix failed: {e}");
        }
    }
}
