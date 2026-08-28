# UI Patching Guide

**2026-07-17 재작성.** 구판의 "정확히 2개 패치, 전부 `Select.*`의 `LoadComplete`" 서술은 실제 코드와 맞지 않았다(`architecture.md` §4 참고). 이 문서는 HarmonyX 패치를 작성할 때 실제로 검증된 보일러플레이트와 함정만 다룬다.

---

## 1. 자동 등록

`Patcher.Apply()`가 `harmony.PatchAll(typeof(Patcher).Assembly)`로 어셈블리 전체를 스캔한다. `LazerSR.Hook\Patches\`에 `[HarmonyPatch]` 클래스 파일만 추가하면 자동 등록됨 — `Patcher.cs` 수정 불필요.

---

## 2. Postfix 템플릿 (private/nested 타겟)

```csharp
[HarmonyPatch]
public static class YourNewPatch
{
    private const string TARGET_TYPE_NAME = "osu.Game.Screens.Select.OuterClass+InnerClass";

    public static MethodBase? TargetMethod()
    {
        Type? type = AccessTools.TypeByName(TARGET_TYPE_NAME);
        return type == null ? null : AccessTools.Method(type, "LoadComplete");
    }

    public static bool Prepare() => TargetMethod() != null;

    public static void Postfix(object __instance)
    {
        try
        {
            if (__instance is not Drawable owner) return;
            // ... 패치 로직 ...
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] YourNewPatch.Postfix failed: {e}");
        }
    }
}
```

- `[HarmonyPatch]`(인자 없음) → `TargetMethod()`/`Prepare()` 네이밍 컨벤션으로 동적 바인딩.
- `Prepare()`가 `false`면 HarmonyX가 조용히 스킵 — osu! 버전 업데이트로 타입/메서드가 사라져도 죽지 않음.
- **top-level try/catch 필수** — Postfix 밖으로 예외가 새면 안 됨.
- nested type은 `+`로 구분 (`.` 아님).
- 타겟이 `LoadComplete`가 아니어도 된다 — `SkinWidgetRegistrarPatch`는 `SerialisedDrawableInfo.GetAllAvailableDrawables`(static 유틸리티)를 탐. `AccessTools.Method(typeof(SerialisedDrawableInfo), nameof(...))`로 직접 지정하면 `TargetMethod()`/`Prepare()` 없이 `[HarmonyPatch(type, name)]` 어트리뷰트 인자로도 가능 (실제 코드 예시 참고).

Postfix 우선 원칙(리턴값 조작 원천 차단)은 `safety.md`에서 다룸 — 이 문서는 순수 기술 패턴만.

### BDL(`[BackgroundDependencyLoader]`) 메서드 패치 (2026-07-28 검증)

`load` 같은 BDL 메서드도 정상적으로 패치된다 — osu.Framework가 리플렉션으로 호출하지만 HarmonyX는 메서드 본체를 바꾸므로 무관하다. 실증: `InfiniteTrainingMenuButtonPatch`가 `ButtonSystem.load`를 Postfix해서 메인 메뉴 버튼을 추가한다.

```csharp
public static MethodBase? TargetMethod() => AccessTools.Method(typeof(ButtonSystem), "load");
public static void Postfix(ButtonSystem __instance) { ... }
```

BDL Postfix 시점에는 원본 `load()`가 만든 자식들이 이미 다 붙어 있으므로, 컨테이너에 요소를 추가하면 맨 뒤에 들어간다. 순서를 지정해야 하면 `FlowContainer.SetLayoutPosition(Drawable, float)`(public)을 쓴다 — 단 기본 layout position이 전부 0이라 한 항목만 값을 주면 0인 나머지 전체보다 뒤로 밀린다.

### `Prefix`가 필요한 경우

원본 실행 자체를 막아야 하고 타겟이 `private`이라 override도 불가능하면 `Prefix`가 유일한 수단이다. 이 프로젝트의 유일한 사례는 `LocalOnlyLeaderboardSkipPatch` — 반드시 `safety.md`의 정당화 서술을 함께 남길 것.

```csharp
public static bool Prefix(object __instance) => __instance is not ILocalOnlyPlayerLoader; // false면 원본 스킵
```

마커로 판별하면 일반 플레이 경로에 영향을 주지 않음을 쉽게 보장할 수 있다. **소비처가 둘 이상이 되면 타입 하드코딩보다 마커 인터페이스가 낫다** — 무한 트레이닝 전용이던 이 패치가 구간 연습까지 덮게 되면서 실제로 그렇게 바꿨다(2026-08-08).

---

## 3. `AccessHelper` — private 멤버 접근

`AccessHelper.TryGet<T>(type, memberName, instance, out value)`가 순서대로 시도:
1. `AccessTools.Property(type, name)`
2. `AccessTools.Field(type, name)`
3. `AccessTools.Field(type, $"<{name}>k__BackingField")` (auto-property backing field)

```csharp
if (!AccessHelper.TryGet<GridContainer>(type, "ratingAndNameContainer", __instance, out var container) || container == null)
    return; // 못 찾으면 조용히 리턴, 절대 throw 안 함
```

`__instance.GetType()`을 넘길 것 (알려진 베이스 타입이 아닌 이상).

### `internal` 필드 mutate + `event` 강제 발화 (`ManiaSkinPositionAccess` 패턴)

위젯에서 osu! 내부 상태(예: `LegacySkin.ManiaConfigurations`, `internal readonly Dictionary<...>`)를 직접 고쳐야 할 때:
1. `AccessTools.Field(type, "필드명")`으로 `internal`/`readonly` 필드도 `GetValue()`는 문제없이 가능 — `readonly`는 리플렉션 `GetValue`를 막지 않는다. 반환된 게 Dictionary 등 참조 타입 컬렉션이면 `SetValue` 없이 내용만 mutate하면 원본에 그대로 반영됨.
2. `public event Action Foo;` 같은 field-like event는 클래스 밖에서 `+=`/`-=`만 가능하고 직접 `Invoke()`는 못 한다. 컴파일러가 생성하는 백킹 필드 이름은 이벤트명과 동일(`Foo`)하므로 `AccessTools.Field(type, "Foo").GetValue(instance) as Action`으로 델리게이트를 꺼내 직접 `Invoke()`하면 우회 가능.

전체 예시는 `LazerSR.Hook\ManiaSkinPositionAccess.cs` (`SkinManager.SourceChanged` 강제 발화로 `ManiaPositionAdjustWidget`의 HitPosition/ScorePosition 변경을 즉시 화면에 반영).

---

## 4. `ScheduleOn` — Drawable.Schedule 우회

`Drawable.Schedule(Action)`은 protected-internal이라 직접 호출 불가. 리플렉션으로 한 번만 resolve:

```csharp
private static readonly MethodInfo? _scheduleMethod =
    AccessTools.Method(typeof(Drawable), "Schedule", new[] { typeof(Action) });

internal static void ScheduleOn(Drawable owner, Action action) =>
    _scheduleMethod?.Invoke(owner, new object[] { action });
```

백그라운드 스레드에서의 모든 UI 변경은 이걸 거친다.

---

## 5. GridContainer 삽입 (검증된 패턴)

`GridContainer.ColumnDimensions`는 write-only property, `Content`는 `GridContainerContent`를 반환한다 — 직접 다루려면:

```csharp
var dimsField   = AccessTools.Field(typeof(GridContainer), "columnDimensions");
var dimsProp    = AccessTools.Property(typeof(GridContainer), "ColumnDimensions");
var contentProp = AccessTools.Property(typeof(GridContainer), "Content");
var existingDims = dimsField.GetValue(container) as Dimension[] ?? Array.Empty<Dimension>();
var gridContent = contentProp.GetValue(container);
// Item[0] indexer로 row 0 순회, 새 Dimension[]/Drawable[] 조립 후
dimsProp.SetValue(container, newDims);
var opImplicit = typeof(GridContainerContent).GetMethod("op_Implicit", new[] { typeof(Drawable[][]) });
contentProp.SetValue(container, opImplicit.Invoke(null, new object[] { new Drawable[][] { newRow } }));
```

전체 코드는 `sunnySR-pill-implementation-guide.md` §3에 그대로 있음 — 복붙 대상.

---

## 6. 알려진 함정

| 함정 | 증상 | 해결 |
|---|---|---|
| nested type(`+`)을 `TargetMethod`로 직접 지정 | `Prepare=true`인데 Postfix 미발화 | top-level 타입의 `LoadComplete` 사용 |
| 같은 메서드에 `[HarmonyPatch]` 클래스 2개 | 두 번째부터 silent 누락 | 하나로 통합 |
| **패치 클래스 내 `yield return` / 제네릭 메서드** | 컴파일러 생성 `[IteratorStateMachine]`을 HarmonyX가 reflection으로 읽다 `TypeLoadException` → **어셈블리 전체 패치 등록이 죽음** | 비제네릭 평범한 메서드, `List<T>` 반환만 사용 |
| `SunnyState.CurrentSr.Value`를 백그라운드에서 직접 세팅 | "Cannot mutate Transforms... not on update thread" | `ScheduleOn` 경유 |
| `GridContainer.ColumnDimensions.GetValue()` 호출 | "Property Get method was not found" | `AccessTools.Field("columnDimensions")`로 읽기 |
| `System.Diagnostics.Trace.WriteLine` 사용 | osu.Framework가 초기화 중 `Trace.Listeners.Clear()` 호출 → 로그 소리소문 사라짐 | `HookLog.Write` 사용 (단, 기본 no-op — `architecture.md` §5) |
| Postfix 최상단에 `static bool` 가드(`if (_done) return; _done = true;`) | 최초 1회만 동작, 이후 영구 무효화 | 인스턴스별 상태 필요 시 `ConditionalWeakTable` 사용 — **`DifficultyIconTooltipPatch`가 이 함정에 실제로 걸려있음, 미수정** |
| `Player`를 직접 상속하면서 `IGameplayLeaderboardProvider`를 캐시하지 않음 | 매 세션 `DependencyNotRegisteredException`이 로그에 쌓임 (게임플레이 자체는 동작 — 예외가 HUD 컴포넌트 비동기 로드 경계에서 잡힘) | osu! `Player` 하위 클래스 5개가 전부 이걸 캐시하지만 **추상 멤버가 아니라 컴파일러가 안 잡아준다.** 리더보드가 없으면 `[Cached(typeof(IGameplayLeaderboardProvider))] EmptyGameplayLeaderboardProvider` (= `EditorPlayer` 방식) |
| `Bindable`을 지역 변수로 만들어 `BindTo`/`BindTarget`으로 UI에 연결 | 한동안 잘 되다가 **GC가 도는 순간 조용히 연결이 끊김**. 예외도 로그도 없음 | osu.Framework의 `Bindable.BindTo`는 서로를 **약한 참조**로 묶는다. `BindValueChanged` 델리게이트는 bindable이 들고 있는 것이라 수명을 지켜주지 않는다 → **반드시 필드로 보관.** `PatternListPanel`의 체크박스가 실제로 걸렸고, 같은 프레임에 도는 무거운 계산(합성 보면 1000노트)이 GC를 유발해 재현 조건이 됐다 |
| **`Alpha = 0`으로 무언가를 숨기기** | 숨긴 대상에 의존하던 오버레이가 통째로 얼어붙거나, 스킨 에디터에서 선택이 안 됨 | `Alpha = 0`은 `IsPresent`를 false로 만들어 **업데이트 경로에서 빠진다.** 그리기만 없애려면 `Colour`를 투명하게 하거나 `AlwaysPresent = true`를 같이 준다 (`ManiaNoteHider`, `BoxElementPlus`가 이 방식) |
| **서스펜드된 스크린에 붙인 드로어블이 계속 돌 거라 가정** | 화면이 전환되는 순간 그 자리에서 멈춤. 예외도 로그도 없음 | 로딩 화면(`PlayerLoader`)에 붙인 작업은 **그 화면이 현재 화면인 동안 끝내야 한다.** 오래 걸리면 `PlayerLoader.ReadyForGameplay`(osu!의 기존 push 게이트)에 조건을 더해 붙잡는다 — 단 **총 소요 시간이 아니라 "진척 여부"로 해제**할 것 (2026-07-29에 시계 기반 한도가 정상 동작을 95.4%에서 잘랐다) |
| **osu! 객체를 트리에 넣기 전에 설정 메서드 호출** | 조용한 `NullReferenceException` → 드로어블 로드 실패 → 상태가 영원히 "준비 안 됨" | BDL에서 만들어지는 내부 필드를 건드리는 메서드가 많다(예: `DrawableRuleset.SetReplayScore`가 `frameStabilityContainer`를 쓴다). `LoadComponent(x)` → 설정 → `AddInternal(x)` 순서로 간다. osu! 본체가 `PrepareReplay()`를 `load()`가 아니라 `LoadComplete()`에서 부르는 이유 |
| **비동기/장시간 작업의 완료 플래그를 실패 경로에서 안 세움** | 소비 측이 영구 대기. 화면에는 "아무 일도 안 일어남"으로만 보여 원인 추적이 매우 어려움 | 셋업 전체를 try/catch로 감싸고 **어떤 경로로 끝나든 완료 표시를 남긴다.** 진척이 멈춘 것을 감지하는 가드도 함께 둔다 |
| **`double.NaN`을 "아직 없음" 초기값으로 쓰고 비교** | `Math.Abs(x - NaN) > eps`가 **항상 false** → 정상 진행을 전부 "멈춤"으로 오판. 짧은 입력에서는 한도에 안 닿아 증상이 안 보임 | `double.IsFinite()` 검사를 먼저 하거나 `NegativeInfinity`를 쓴다 |
| **HUD 위젯에서 룰셋 쪽 `[Cached]` 객체를 `[Resolved]`** | 항상 null (`canBeNull: true`면 조용히) | HUD는 `ManiaInputManager` 등 룰셋 입력 매니저의 **자식이 아니다.** `DrawableRuleset`(Player가 캐시)에서 리플렉션으로 내려가거나, `GameplayState` 경유로 잡는다 |
| **HUD의 `Time.Current`와 `JudgementResult.TimeAbsolute`를 같은 시간축으로 취급** | 시각 대조가 항상 실패 | HUD는 게임플레이 시계, 판정은 **프레임 안정 시계** 기준이다 (`Player.cs:357`이 `IGameplayClock`으로 `FrameStableClock`을 따로 캐시할 만큼 별개). 시각 대조 대신 **카운터 증가** 같은 시계 비의존 방식을 쓴다 |
| `Dispose(bool)`에서 `Bindable.Value`를 쓰기 | 구독 중인 위젯이 **업데이트 스레드 밖에서** 드로어블을 수정하다 죽음 | `Dispose`는 비동기 폐기 스레드에서 돌 수 있다. 화면/세션 상태 정리는 `OnExiting` 같은 업데이트 스레드 경로로 옮기고, `Dispose`에는 이벤트 해제·취소만 남긴다 |
| **`ITooltip.SetContent`가 한 번만 불릴 거라 가정** | 계산/애니메이션이 매 프레임 재시작되어 **영원히 끝나지 않음**. 마우스를 떼야 값이 보이는 식으로 나타나 원인 추적이 어려움 | osu.Framework의 `TooltipContainer`는 툴팁이 떠 있는 동안 **매 프레임** 내용을 다시 밀어 넣는다. 게다가 `DifficultyIcon.TooltipContent` 같은 제공자는 접근할 때마다 **새 객체**를 반환하고 그 타입이 `Equals`를 재정의하지 않아 항상 "바뀜"으로 판정된다 → **대상 식별 키를 직접 만들어** 실제로 바뀔 때만 작업을 재시작할 것 (`DifficultyIconTooltipPatch`가 실제로 걸렸음, `architecture.md` §13) |
| **`renderer.CreateQuadBatch(size, ...)`의 `size`를 정점 수로 착각** | 점/도형이 많아지면 **osu! 즉사 또는 극심한 렉** | `size`는 **쿼드 수**이고 `IRenderer.MAX_QUADS`(10922)가 하드 상한, 넘으면 생성자가 `OverflowException`. **배치 크기는 데이터 개수와 무관한 고정값**으로 잡을 것 — `VertexBatch.Add`가 버퍼가 차면 스스로 플러시하고 이어서 담으므로 데이터가 몇 개든 전부 그려진다. (`* 4`를 곱하는 관용구가 `StrainAreaGraph`와 osu! 본체 `BeatmapMetadataWedge.FailRetryDisplay`에 있는데 **둘 다 틀렸고** 데이터가 작아 안 걸렸을 뿐이다) |
| **`DrawNode.Draw`에서 예외가 나갈 수 있는 구조** | 드로우 스레드 예외 = **게임 즉사**. 실패 상태가 복구 안 되면 매 프레임 재시도로 **예외 폭풍** → 렉으로 먼저 드러남 | `Draw` 전체를 try/catch로 감싸고 **한 번 실패하면 재시도하지 않는 플래그**를 둔다. `shader.Unbind()`는 `finally`로 옮겨 렌더 상태 오염을 막는다 |
| **AutoSize/패딩으로 줄어든 플롯 위에 라벨을 전체 높이 기준으로 배치** | 눈금과 실제 데이터 선이 몇 px 어긋남. 예약 공간을 늘리면 오차도 같이 커짐 | 라벨 레이어를 **플롯과 정확히 같은 범위를 갖는 컨테이너**에 넣는다. `RelativePositionAxes`는 부모 기준이라 부모가 다르면 무조건 어긋난다 |
| **게임플레이 클럭을 `CreateGameplayClockContainer` 안에서 `Reset()`** | 시작 지점이 안 잡히고, 첫 프레임 시각이 **직전 화면에서 재생 중이던 곡 위치**로 읽힘. 종료 조건이 시각 기반이면 "시작하자마자 종료" | 그 시점 컨테이너는 아직 트리에 붙기 전이라 seek이 반영되지 않는다. 클럭이 준비된 뒤인 **`StartGameplay()` override**에서 한다. osu! `EditorPlayer`가 같은 자리에서 같은 호출을 하고도 멀쩡한 건 에디터에서는 트랙이 정지 상태이기 때문 (`architecture.md` §15) |
| **결과창 위에 `Player`를 띄우면 배속이 곱해짐** | DT 리플레이의 구간 연습이 2.25배로 돎. 원배속은 정상이라 눈치채기 어려움 | `progressToResults`가 `Push`를 쓰므로 **직전 `Player`는 중단일 뿐 종료가 아니고**, 트랙 조정을 떼는 `StopUsingBeatmapClock()`은 `OnExiting`에만 있다. `MasterGameplayClockContainer`를 상속해 `StartGameplayClock()`에서 tempo/frequency를 초기화한 뒤 `base` 호출. `MusicController.ResetTrackAdjustments()`로는 못 뗀다(자기 래퍼만 건드림) |
| **다른 화면 위에 화면을 push할 때 lease가 원복해줄 거라 가정** | `Beatmap`/`Ruleset`/`Mods`가 돌아오지 않음 | `OsuScreenStack`이 새 화면의 의존성을 **직전 화면의 의존성을 부모로** 만든다(`CreateLeasedDependencies`). 부모가 이미 lease 중이면 **새 lease를 뜨지 않고 사본만 받으므로** `revertValueOnReturn`이 걸리지 않는다. `OnEntering`에서 저장하고 `OnExiting`에서 직접 되돌릴 것 |
| **네이티브 콜백(WNDPROC 등) 델리게이트를 인스턴스 필드에 보관** | 기능을 한 번 쓰고 끈 뒤 **두 번째로 켤 때 osu! 즉사**. 예외 로그 전무(네이티브 액세스 위반이라 `try/catch` 불가) | 윈도우 클래스는 함수 포인터를 **값으로 복사해 프로세스 수명 내내** 들고 있는데 `GetFunctionPointerForDelegate` 스텁은 GC가 추적하지 않는다 → 세션이 끝나며 수거되면 클래스에 죽은 포인터만 남고, 재진입 시 `CreateWindowEx`가 그걸 쓴다. **델리게이트를 `static readonly`로 클래스와 같은 수명에 두고, 수신 대상만 갈아끼울 것** (`RawKeyboardListener`가 실제로 걸렸음, `architecture.md` §19) |
| **백그라운드 스레드 진입점의 `finally`가 예외를 낼 수 있는 구조** | 프로세스 즉시 종료. 로그 없음 | 스레드 밖으로 나간 예외는 .NET에서 곧 종료다. `finally` 본문까지 try/catch로 감싼다. 실제 사례: `Dispose`가 `Join` 타임아웃 뒤 `ManualResetEventSlim`을 버려서 그 스레드의 `Set()`이 던짐 |
| `FillFlowContainer`(`AutoSizeAxes.Both`)의 직계 자식에 `Anchor.Centre`(또는 그 외 non-TopLeft) + **한쪽 축만 고정 크기**(예: `Width=44`, `Height`는 auto)를 동시에 주고, 이게 3~4단 이상 중첩된 `AutoSizeAxes.Both` FillFlowContainer 체인 안에 있는 경우 | **osu! 프로세스 즉사** — 예외 로그 전무, C# try/catch로 못 잡음(스택오버플로우로 추정) | 그 자식을 양쪽 축 다 고정된 `Container`(`Size = new Vector2(w, h)`)로 감싸고, `Anchor.Centre`는 그 Container 안의 자식에 적용. `ManiaPositionAdjustWidget` 개발 중 실제로 걸렸음 — 이진 탐색으로 서브클래싱/인스턴스 개수/커스텀 폰트/Box 혼합은 전부 무관하고 정확히 이 조합(편측 고정폭 + Anchor.Centre + 깊은 중첩)만 트리거임을 확인 (`progress\2026-07-18.md` 참고) |

---

## 7. Per-Instance State (권장 패턴, 현재 코드 대부분 미준수)

```csharp
private static readonly ConditionalWeakTable<object, PatchState> _states = new();
```

- 드로어블 생명주기에 상태를 묶어 GC와 동기화.
- 클로저에는 `WeakReference<PatchState>`만 — 강한 참조로 static에 저장 금지.

**현재 상태**: 이 패턴을 실제로 쓰는 패치는 거의 없다(`architecture.md` §6). `DifficultyIconTooltipPatch`의 static bool 버그가 이 원칙 미준수의 직접적 결과. 새 패치를 쓸 때는 이 패턴을 따르는 걸 권장하되, 기존 패치를 굳이 지금 다 뜯어고치진 않는다 (외과적 변경 원칙).

---

## 8. 새 패치 체크리스트

1. 타겟 타입의 정확한 CLR 이름 확인 (`+`는 nested type)
2. `LazerSR.Hook\Patches\YourNewPatch.cs` 생성
3. `[HarmonyPatch]` + `TargetMethod()`/`Prepare()`/`Postfix(object __instance)`
4. top-level try/catch, `HookLog.Write`로 실패 로깅
5. `__instance`를 먼저 캐스팅, 실패 시 조기 리턴
6. private 멤버는 `AccessHelper.TryGet<T>()`
7. UI 요소는 `Alpha = 0f`로 시작, 계산 완료 후에만 `1f`
8. 가능하면 `ConditionalWeakTable`로 상태 관리 (§7)
9. 모든 UI 변경은 `ScheduleOn(owner, action)` 경유
10. `Patcher.cs` 수정 불필요 — `PatchAll`이 자동 탐지
