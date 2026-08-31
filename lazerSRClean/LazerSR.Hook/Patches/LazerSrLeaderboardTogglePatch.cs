using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LazerSR.Hook.LazerSrLeaderboard;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Graphics.UserInterface;

namespace LazerSR.Hook.Patches;

/// <summary>
/// 선곡 화면 좌측 리더보드 헤더(<c>BeatmapDetailsArea+Header</c>)에 "lazerSR" 토글 버튼을 넣는다.
/// <list type="bullet">
/// <item>토글 ON → 범위(scope) 드롭다운을 비활성화하고, 리더보드를 우리 서버 내용으로 다시 채운다.</item>
/// <item>상태는 <see cref="LazerSrLeaderboardState"/>가 세션 간 유지한다.</item>
/// </list>
/// 실제 페치 가로채기는 <see cref="LazerSrLeaderboardFetchPatch"/>.
/// </summary>
[HarmonyPatch]
public static class LazerSrLeaderboardTogglePatch
{
    // GetBoundCopy를 헤더 수명에 묶어 둔다(static Bindable에 강한 델리게이트가 남지 않도록).
    private static readonly ConditionalWeakTable<object, object> keep_alive = new();

    public static MethodBase? TargetMethod()
    {
        var t = AccessTools.TypeByName("osu.Game.Screens.Select.BeatmapDetailsArea+Header");
        return t == null ? null : AccessTools.Method(t, "LoadComplete");
    }

    public static bool Prepare() => TargetMethod() != null;

    public static void Postfix(object __instance)
    {
        try
        {
            var header = (CompositeDrawable)__instance;
            var headerType = header.GetType();

            if (AccessTools.Field(headerType, "leaderboardControls")?.GetValue(header) is not FillFlowContainer controls)
            {
                HookLog.Write("[LazerSR] LazerSrLeaderboardTogglePatch: leaderboardControls not found.");
                return;
            }

            object? scopeDropdown = AccessTools.Field(headerType, "scopeDropdown")?.GetValue(header);
            var scopeCurrent = scopeDropdown == null
                ? null
                : AccessTools.Property(scopeDropdown.GetType(), "Current")?.GetValue(scopeDropdown) as IBindable;

            var toggle = new ShearedToggleButton
            {
                Anchor = Anchor.CentreRight,
                Origin = Anchor.CentreRight,
                AutoSizeAxes = Axes.X,
                Height = 30f,
                Text = "lazerSR",
                Margin = new MarginPadding { Left = -9.2f },
            };
            controls.Add(toggle);

            var local = LazerSrLeaderboardState.Enabled.GetBoundCopy();
            keep_alive.Add(header, local);

            toggle.Active.BindTo(local);

            local.BindValueChanged(v =>
            {
                setScopeDisabled(scopeCurrent, v.NewValue);
                triggerLeaderboardRefresh(header);
            }, true);
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] LazerSrLeaderboardTogglePatch.Postfix failed: {e}");
        }
    }

    private static void setScopeDisabled(IBindable? scopeCurrent, bool disabled)
    {
        if (scopeCurrent == null) return;
        try
        {
            AccessTools.Property(scopeCurrent.GetType(), "Disabled")?.SetValue(scopeCurrent, disabled);
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] LazerSrLeaderboardTogglePatch.setScopeDisabled failed: {e}");
        }
    }

    private static void triggerLeaderboardRefresh(Drawable header)
    {
        try
        {
            // header.Parent(ShearAligningWrapper).Parent == BeatmapDetailsArea
            var detailsArea = header.Parent?.Parent;
            if (detailsArea == null) return;

            AccessTools.Method(detailsArea.GetType(), "Refresh")?.Invoke(detailsArea, null);
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] LazerSrLeaderboardTogglePatch.triggerLeaderboardRefresh failed: {e}");
        }
    }
}
