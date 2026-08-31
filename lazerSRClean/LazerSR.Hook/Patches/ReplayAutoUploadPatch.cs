using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using LazerSR.Hook.ReplayUpload;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Game.Scoring;
using osu.Game.Screens.Play;

namespace LazerSR.Hook.Patches;

/// <summary>
/// 트리거 #2 — <see cref="Player.ImportScore"/> Postfix. 실제 솔로/멀티 플레이가 끝나 스코어가
/// realm에 기록되는 그 지점에서, 방금 친 리플레이를 큐에 넣는다(전송은 런처).
/// <para>
/// <c>ReplayPlayer</c>와 LazerSR의 훈련/구간연습/패턴복제 <c>Player</c>는 이 메서드를 <c>base</c>
/// 호출 없이 override하므로(<c>safety.md</c> 서버 격리) 이 패치는 그쪽엔 안 걸린다 — 관전/리플레이
/// 감상은 자동 제외. 여기 <see cref="Score"/>는 <b>로그인 유저가 방금 친 판이 확정</b>이라
/// 소유권 검사가 필요 없다.
/// </para>
/// 읽기 전용: 이미 완성된 <see cref="Score"/>와 그 <c>.osr</c> 파일만 읽는다.
/// </summary>
[HarmonyPatch]
public static class ReplayAutoUploadPatch
{
    private static Storage? _storage;

    public static MethodBase? TargetMethod() => AccessTools.Method(typeof(Player), "ImportScore");

    public static bool Prepare() => TargetMethod() != null;

    public static void Postfix(object __instance, Score score, Task __result)
    {
        try
        {
            if (__instance is not CompositeDrawable owner) return;

            var scoreInfo = score?.ScoreInfo;
            if (scoreInfo == null) return;
            if (scoreInfo.Ruleset.OnlineID != 3) return; // mania만

            resolveStorage(owner);
            var storage = _storage ?? HookRuntimeContext.Storage;
            if (storage == null)
            {
                HookLog.Write("[LazerSR] ReplayAutoUploadPatch: no Storage available, skipping.");
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    // ImportScore 본문은 이미 별도 스레드에서 돌았다. 완료를 기다린 뒤 .osr이
                    // score.ScoreInfo.Files에 붙고 디스크에 떨어질 때까지 짧게 재시도한다.
                    if (__result != null)
                    {
                        try { await __result.ConfigureAwait(false); }
                        catch { /* import 실패는 osu!가 로깅함 — 우리가 할 일 없음 */ }
                    }

                    string? path = null;
                    for (int i = 0; i < 20 && path == null; i++)
                    {
                        path = ReplayQueueWriter.ResolveReplayPath(scoreInfo, storage);
                        if (path == null) await Task.Delay(150).ConfigureAwait(false);
                    }

                    if (path == null)
                    {
                        HookLog.Write($"[LazerSR] ReplayAutoUploadPatch: no .osr for score {scoreInfo.ID}");
                        return;
                    }

                    ReplayQueueWriter.WriteEntry(scoreInfo, path);
                }
                catch (Exception e)
                {
                    HookLog.Write($"[LazerSR] ReplayAutoUploadPatch worker failed: {e}");
                }
            });
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] ReplayAutoUploadPatch.Postfix failed: {e}");
        }
    }

    private static void resolveStorage(CompositeDrawable owner)
    {
        if (_storage != null) return;

        try
        {
            var deps = AccessTools.Property(typeof(CompositeDrawable), "Dependencies")?.GetValue(owner)
                as IReadOnlyDependencyContainer;
            _storage = deps?.Get(typeof(Storage)) as Storage;
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] ReplayAutoUploadPatch.resolveStorage failed: {e}");
        }
    }
}
