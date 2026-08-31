using System;
using System.IO;
using osu.Framework.Bindables;

namespace LazerSR.Hook.LazerSrLeaderboard;

/// <summary>
/// "lazerSR" 리더보드 토글의 세션 간 유지되는 on/off 상태.
/// <para>
/// osu <c>OsuConfigManager</c>에 새 키를 못 넣으므로 우리 저장소(<see cref="LazerSrStorage"/>)의
/// 작은 파일 하나로 유지한다. <see cref="Patches.LazerSrLeaderboardTogglePatch"/>(헤더의 토글 버튼)와
/// <see cref="Patches.LazerSrLeaderboardFetchPatch"/>(페치 가로채기)가 이 값을 공유한다.
/// </para>
/// </summary>
public static class LazerSrLeaderboardState
{
    private const string folder = "leaderboard";
    private const string file_name = "enabled";

    public static readonly BindableBool Enabled = new BindableBool();

    static LazerSrLeaderboardState()
    {
        try
        {
            string path = Path.Combine(LazerSrStorage.GetFolder(folder), file_name);
            Enabled.Value = LazerSrStorage.ReadText(path)?.Trim() == "1";
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] LazerSrLeaderboardState load failed: {e}");
        }

        Enabled.BindValueChanged(v =>
        {
            try
            {
                string path = Path.Combine(LazerSrStorage.GetFolder(folder), file_name);
                LazerSrStorage.WriteText(path, v.NewValue ? "1" : "0");
            }
            catch (Exception e)
            {
                HookLog.Write($"[LazerSR] LazerSrLeaderboardState save failed: {e}");
            }
        });
    }
}
