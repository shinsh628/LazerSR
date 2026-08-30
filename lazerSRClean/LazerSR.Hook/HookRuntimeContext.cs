using osu.Framework.Platform;
using osu.Game.Database;
using osu.Game.Online.API;

namespace LazerSR.Hook;

/// <summary>
/// 여러 패치가 각자 얻은 DI 의존성(RealmAccess/Storage/IAPIProvider) 중 가장 먼저 얻은 걸 공유한다.
/// 파이프로 트리거되는 백그라운드 작업(예: 리플레이 일괄 동기화)은 특정 Drawable 컨텍스트가
/// 없어 직접 DI를 못 타므로, 이미 화면이 로드되며 자연스럽게 확보된 인스턴스를 여기서 재사용한다.
/// <para>
/// <see cref="Patches.DifficultyDisplayPatch"/>가 송 셀렉트 로드 시 가장 먼저(osu! 부팅 직후)
/// 채워준다 — 플레이를 한 번도 안 해도 이 시점엔 이미 채워져 있다.
/// </para>
/// </summary>
internal static class HookRuntimeContext
{
    public static RealmAccess? Realm { get; private set; }
    public static Storage? Storage { get; private set; }
    public static IAPIProvider? Api { get; private set; }

    public static void Populate(RealmAccess? realm, Storage? storage, IAPIProvider? api = null)
    {
        if (realm != null) Realm ??= realm;
        if (storage != null) Storage ??= storage;
        if (api != null) Api ??= api;
    }
}
