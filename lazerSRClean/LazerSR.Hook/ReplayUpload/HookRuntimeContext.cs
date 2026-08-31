using osu.Framework.Platform;
using osu.Game.Database;
using osu.Game.Online.API;

namespace LazerSR.Hook.ReplayUpload;

/// <summary>
/// 여러 패치가 각자 DI로 얻은 <see cref="RealmAccess"/>/<see cref="Storage"/>/<see cref="IAPIProvider"/>
/// 중 가장 먼저 얻은 것을 공유한다.
/// <para>
/// 파이프로 트리거되는 백그라운드 작업(리플레이 일괄 수집)은 특정 Drawable 컨텍스트가 없어 직접
/// DI를 못 타므로, 이미 화면이 로드되며 자연스럽게 확보된 인스턴스를 여기서 재사용한다.
/// <see cref="Patches.DifficultyDisplayPatch"/>가 선곡 화면 로드 시(osu! 부팅 직후) 가장 먼저 채운다.
/// </para>
/// </summary>
internal static class HookRuntimeContext
{
    public static RealmAccess? Realm { get; private set; }
    public static Storage? Storage { get; private set; }
    public static IAPIProvider? Api { get; private set; }

    public static void Populate(RealmAccess? realm, Storage? storage, IAPIProvider? api)
    {
        if (realm != null) Realm ??= realm;
        if (storage != null) Storage ??= storage;
        if (api != null) Api ??= api;
    }
}
