using System;
using osu.Framework.Configuration;
using osu.Framework.Platform;

namespace LazerSR.Hook.PatternCopy;

/// <summary>
/// osu!가 <b>포커스를 잃어도 포커스 상태와 같은 프레임</b>을 유지하게 한다. 패턴 복제 모드에서만 켠다.
/// <para>
/// 이 모드에서는 사용자가 대상 게임을 조작하고 osu! 화면은 WGC로 캡처해 오버레이로 비춰 본다.
/// 그런데 프레임워크는 포커스를 잃으면 업데이트·드로우 스레드를 <c>GameThread.DEFAULT_INACTIVE_HZ</c>
/// (60)로 묶으므로, 고주사율 환경에서는 캡처 영상이 눈에 띄게 끊긴다.
/// </para>
/// <para>
/// 스로틀은 <c>GameThread.updateMaximumHz()</c>가 <c>IsActive</c>에 따라 <c>ActiveHz</c>/<c>InactiveHz</c>
/// 중 하나를 클럭에 꽂는 것이 전부다. 따라서 <b>모드가 도는 동안만 <c>InactiveHz</c>를 포커스 시 값으로
/// 올려두고 나갈 때 되돌리면</b> 된다 — 패치도 리플렉션도 필요 없다.
/// osu! 본체의 <c>LatencyCertifierScreen</c>이 <c>ActiveHz</c>에 대해 같은 저장/덮기/복원을 한다.
/// </para>
/// <para>
/// 표시 프레임 설정일 뿐이라 <c>docs\guides\safety.md</c>의 레드라인과 무관하다 — 제출값도,
/// 라이브 인스턴스도, 네트워크도, osu! 파일도 건드리지 않는다.
/// <b>모드 수명에 정확히 묶어둔다</b> — 상시 켜두면 osu!를 안 보고 있을 때도 계속 렌더링한다.
/// </para>
/// </summary>
public static class InactiveFrameRateOverride
{
    /// <summary>갱신률을 못 읽을 때의 폴백. 프레임워크의 <c>updateFrameSyncMode()</c>와 같은 값을 쓴다.</summary>
    private const double fallback_refresh_hz = 60;

    private static GameHost? boundHost;

    private static double savedMaximumInactiveHz;
    private static double savedUpdateInactiveHz;
    private static double savedDrawInactiveHz;

    public static bool Running => boundHost != null;

    /// <summary>실패해도 예외를 던지지 않는다 — 프레임이 안 올라가도 모드 자체는 돌아야 한다.</summary>
    public static void Start(GameHost host, FrameworkConfigManager config)
    {
        if (boundHost != null) return;

        try
        {
            double refreshHz = resolveRefreshHz(host);
            double drawTarget = resolveDrawTarget(host, config, refreshHz);

            // 업데이트 쪽은 그대로 복사해도 안전하다 — maximum_sane_fps로 클램프되므로 항상 유한하다.
            // 판정이 이 스레드에서 도니 포커스 때보다 낮아지면 그것 자체가 손해다.
            double updateTarget = host.MaximumUpdateHz;

            if (!isUsable(drawTarget)) drawTarget = refreshHz;
            if (!isUsable(updateTarget)) updateTarget = refreshHz;

            savedMaximumInactiveHz = host.MaximumInactiveHz;
            savedUpdateInactiveHz = host.UpdateThread.InactiveHz;
            savedDrawInactiveHz = host.DrawThread.InactiveHz;
            boundHost = host;

            // 순서가 중요하다. GameHost.MaximumInactiveHz는 ThreadRunner에도 값을 넘기는데,
            // 단일스레드 ExecutionMode에서는 그 ThreadRunner의 값이 모든 스레드를 지배한다
            // (per-thread InactiveHz만 세팅하면 그 모드에서 아무 효과가 없다).
            // 반대로 이걸 나중에 부르면 아래 per-thread 값을 통째로 덮어쓴다.
            host.MaximumInactiveHz = Math.Max(updateTarget, drawTarget);

            // 두 스레드의 목표가 다르므로(포커스 시에도 다르다) 여기서 각각 정확한 값으로 좁힌다.
            host.UpdateThread.InactiveHz = updateTarget;
            host.DrawThread.InactiveHz = drawTarget;
        }
        catch (Exception ex)
        {
            HookLog.Write($"[LazerSR] InactiveFrameRateOverride.Start failed: {ex}");
            Stop();
        }
    }

    public static void Stop()
    {
        var host = boundHost;

        if (host == null) return;

        boundHost = null;

        try
        {
            host.MaximumInactiveHz = savedMaximumInactiveHz;
            host.UpdateThread.InactiveHz = savedUpdateInactiveHz;
            host.DrawThread.InactiveHz = savedDrawInactiveHz;
        }
        catch (Exception ex)
        {
            HookLog.Write($"[LazerSR] InactiveFrameRateOverride.Stop failed: {ex}");
        }
    }

    /// <summary>
    /// 포커스 상태의 <b>실효</b> 드로우 프레임. <c>MaximumDrawHz</c>를 그대로 쓰면 안 된다.
    /// <para>
    /// <c>updateFrameSyncMode()</c>는 VSync 모드에서 <c>drawLimiter = int.MaxValue</c>로 두고
    /// 실제 제한을 vsync에 위임한다. 그 값이 <c>maximum_sane_fps</c>(= 1000)로 클램프되어
    /// <c>MaximumDrawHz</c>는 1000이 되는데, <b>이건 체감 프레임이 아니다</b> —
    /// 144Hz에서 144가 나오는 건 숫자가 아니라 vsync가 present를 막기 때문이다.
    /// 그런데 <b>가려진 창은 vsync에 물리지 않아</b> 비포커스에는 그 제한자가 통째로 사라진다.
    /// 그래서 1000을 그대로 옮기면 갱신률의 몇 배로 과렌더하다 렉으로 끊긴다(2026-08-21에 실제로 겪음).
    /// </para>
    /// <para>
    /// Unlimited도 같은 이유로 1000이 되는데, 갱신률 위로 더 그려봐야
    /// <b>지금 사용자가 실제로 치고 있는 대상 게임의 GPU를 뺏을 뿐</b>이라 같이 갱신률로 묶는다.
    /// Limit2x/4x/8x는 vsync가 꺼져 있고 값도 유한해 그대로 쓰는 것이 맞다.
    /// </para>
    /// </summary>
    private static double resolveDrawTarget(GameHost host, FrameworkConfigManager config, double refreshHz)
    {
        var mode = config.Get<FrameSync>(FrameworkSetting.FrameSync);

        if (mode == FrameSync.VSync || mode == FrameSync.Unlimited)
            return refreshHz;

        return host.MaximumDrawHz;
    }

    private static double resolveRefreshHz(GameHost host)
    {
        double hz = host.Window?.CurrentDisplayMode.Value.RefreshRate ?? 0;

        return hz > 0 ? hz : fallback_refresh_hz;
    }

    /// <summary>
    /// 무한대/0이 InactiveHz로 들어가면 스로틀이 사라져 프레임이 폭주한다 — 반드시 유한한 양수여야 한다.
    /// </summary>
    private static bool isUsable(double hz) => double.IsFinite(hz) && hz > 0 && hz < int.MaxValue;
}
