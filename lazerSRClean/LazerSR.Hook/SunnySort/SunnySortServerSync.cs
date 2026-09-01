using System;
using System.Threading;
using System.Threading.Tasks;
using LazerSR.Hook.Ipc;

namespace LazerSR.Hook.SunnySort;

/// <summary>
/// 트리거 ③ — lazerSR 실행 시 서버에 있고 로컬엔 없는 sunny 캐시값을 한 번 당겨온다.
/// Hook은 네트워크 금지라 런처에 파이프로 요청하고(<c>sunnysyncreq</c>), 런처가 서버 덤프 JSON을
/// 되돌려주면 <see cref="SunnySortCache.MergeServerJson"/>으로 병합한다.
/// <para>
/// 성공할 때까지만 재시도한다 — 런처가 아직 파이프에 안 붙었으면(osu! 먼저 뜬 경우) 실패하는데,
/// 그때 <see cref="OnLauncherConnected"/>가 <see cref="PipeServer"/>에서 불려 다시 시도한다.
/// </para>
/// </summary>
public static class SunnySortServerSync
{
    private static int inFlightOrDone;

    /// <summary>선곡 화면 로드 등에서 호출. 한 번 성공하면 이후 호출은 무시된다.</summary>
    public static void RequestOnce()
    {
        if (Interlocked.CompareExchange(ref inFlightOrDone, 1, 0) != 0)
            return;

        _ = run();
    }

    /// <summary>런처가 파이프에 (재)연결되면 <see cref="PipeServer"/>가 호출. 아직 성공 못 했으면 재시도.</summary>
    public static void OnLauncherConnected()
    {
        if (Interlocked.CompareExchange(ref inFlightOrDone, 1, 0) != 0)
            return;

        _ = run();
    }

    private static async Task run()
    {
        try
        {
            string json = await PipeServer.RequestAsync("sunnysyncreq", SunnySortState.CalcVersion.ToString(), 30_000)
                                          .ConfigureAwait(false);
            int merged = SunnySortCache.MergeServerJson(json);
            // 성공 - inFlightOrDone은 1로 유지되어 다시 안 돈다.
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref inFlightOrDone, 0); // 실패 - 다음 기회에 재시도
        }
    }
}
