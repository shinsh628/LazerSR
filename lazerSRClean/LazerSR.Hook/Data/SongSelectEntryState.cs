using osu.Framework.Bindables;

namespace LazerSR.Hook.Data;

/// <summary>
/// 선곡 화면이 "지금 막 현재 화면이 됐다"는 신호 — <see cref="Patches.SongSelectEntryPatch"/>가
/// <c>SongSelect.OnEntering</c>/<c>OnResuming</c>에서 값을 올린다(매번 다른 값이면 되므로 그냥
/// 단조 증가 카운터). 다른 화면(게임플레이/결과창)에서 돌아왔을 때 위젯이 스스로를 다시 로드하는
/// 게 아니라 <c>LoadComplete</c> 이후로는 아무 신호도 못 받는다는 문제(architecture.md 참고)를
/// 폴링 없이 해결하기 위한 채널 — <c>SunnyState</c>와 같은 Bindable 전파 관행을 그대로 따른다.
/// </summary>
public static class SongSelectEntryState
{
    public static readonly Bindable<int> EnteredToken = new();

    public static void Bump() => EnteredToken.Value++;
}
