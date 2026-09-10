# Feature Development Guide

**2026-07-17 재작성.** 구판은 `SunnyFeature` enum + `SunnyState.SetFeatureEnabled` + weak-reference subscriber 패턴을 서술했으나 이는 실제로 구현된 적 없는 설계다 (자세한 사실관계는 `architecture.md` §5). 이 문서는 실제 코드에 존재하는 두 가지 패턴만 다룬다.

---

## 패턴 A — Sibling Pill (기존 SR 표시 위치에 sunnySR 추가)

새 위치(맵 목록, 모드 선택 화면 등)에 sunnySR pill을 추가할 때. **반드시 `sunnySR-pill-implementation-guide.md`를 먼저 읽는다** — 1.1의 검증된 삽입 코드가 그대로 있다.

요약:
1. 타겟 타입의 **top-level `LoadComplete`**를 후크 지점으로 잡는다 (nested type 직접 타겟은 silent failure).
2. `[HarmonyPatch]` + `TargetMethod()`/`Prepare()`/`Postfix(object __instance)` 골격 (`docs/guides/ui-patching.md` §2 템플릿 사용).
3. pill 생성은 항상 동일:
   ```csharp
   var pill = new StarRatingDisplay(default, animated: true) { Anchor = ..., Origin = ... };
   pill.Current = SunnyState.CurrentSr;  // 이 한 줄로 구독 완료
   ```
4. 부모 컨테이너에 reflection으로 삽입 (`GridContainer`면 §3 GridContainer 패턴, `FillFlowContainer`면 `Insert`/`AddInternal`).

어느 위치가 이미 됐는지는 `docs/sibling-clone-position-map.md`에서 확인 — 1.1/2.1/4.1/4.2 완료, 나머지 미완료.

**절대 건드리지 않는 것**: `SunnyRunner.cs`(계산), `SunnyState.cs`(공유 채널), `DifficultyDisplayPatch.Recalculate()`(오케스트레이터).

---

## 패턴 B — 스킨 위젯 (`ISerialisableDrawable`)

Dan/MSD/StrainGraph/ReplayCompare/SectionTimer/SunnyPP/ManiaPositionAdjust가 전부 이 패턴이다. osu! 자체 스킨 에디터에서 사용자가 켜고/끄고/배치한다 — **Launcher 토글 없음.**

### 필수 요소

```csharp
public class MyWidget : CompositeDrawable, ISerialisableDrawable
{
    public bool UsesFixedAnchor { get; set; }

    [SettingSource("표시 이름", "설명")]
    public BindableBool ShowThing { get; } = new(true);

    [Resolved] private IBindable<WorkingBeatmap> beatmap { get; set; } = null!;
    [Resolved] private IBindable<RulesetInfo> ruleset { get; set; } = null!;
    [Resolved] private IBindable<IReadOnlyList<Mod>> mods { get; set; } = null!;

    [BackgroundDependencyLoader]
    private void load() { /* InternalChild 구성 */ }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        beatmap.BindValueChanged(_ => recompute(), true);
        ruleset.BindValueChanged(_ => recompute(), true);
        mods.BindValueChanged(_ => recompute(), true);
    }
}
```

`SettingsColour`가 필요하면 `Bindable<Colour4>`가 아니라 **`BindableColour4`**를 써야 한다 (아니면 enum dropdown으로 깨짐).

### 등록

`Patches/SkinWidgetRegistrarPatch.cs`의 `LazerSRWidgets` 배열에 타입 하나 추가하면 끝. 이 패치가 `SerialisedDrawableInfo.GetAllAvailableDrawables`를 Postfix해서 osu! 스킨 에디터 toolbox에 자동 노출시킨다.

```csharp
private static readonly Type[] LazerSRWidgets = [
    typeof(DanInfoWidget), typeof(ManiaPositionAdjustWidget), typeof(MsdSkillsetWidget),
    typeof(ReplayCompareWidget), typeof(SectionTimerWidget), typeof(StrainGraphWidget), typeof(SunnyPPWidget),
    typeof(MyWidget), // 추가
];
```

### 실시간 판정(judgement) 이벤트 구독 패턴 (2026-07-18 추가)

노트 단위 실시간 판정 오차(ms)가 필요한 위젯(`ClassicAccuracyWidget` 등)은 `GameplayState.ScoreProcessor`의 공개 이벤트를 직접 구독한다 — 리플렉션 불필요:

```csharp
public event Action<JudgementResult>? NewJudgement;      // 판정 발생
public event Action<JudgementResult>? JudgementReverted;  // 되감기/스킵으로 판정 취소
```

`JudgementResult.TimeOffset`은 head/tail 구분 없이 항상 **원본(raw) ms 오차**다 (mania 롱노트 tail의 1.5배 판정 유예는 `HitResult` 등급 계산에만 쓰이고 `TimeOffset` 저장값엔 반영 안 됨).

**단, 원본 = 비트맵 시간 기준이라 배속에 비례해 커진다.** mania 판정창은 같은 배속만큼 함께 늘어나므로, 오차를 **고정 판정창에 대입하려면 반드시 `GameplayRate`로 나눠야 한다** — 안 그러면 DT/HT에서 배율만큼 틀린다 (`ClassicAccuracyWidget`이 실제로 이 버그였음). 상세는 `architecture.md` §14.

**하드 룰**: `NewJudgement`만 구독하고 `JudgementReverted`를 무시하면 리플레이 되감기·1/5/10초/프레임 스킵(전부 내부적으로 `GameplayClockContainer.Seek()`로 귀결, 뒤로 이동 시 `Playfield`가 `judgedEntries` 스택을 LIFO로 되돌리며 `JudgementReverted`만 발화하고 `NewJudgement`는 재발화하지 않음) 상황에서 카운터가 이중 가산되어 드리프트한다. **누적 카운터를 쓰는 위젯은 반드시 `JudgementReverted` 핸들러를 `NewJudgement`와 정확히 대칭으로 구현할 것** — 콤바인(예: 롱노트 head+tail 평균)이 있다면 되돌림 시 원상복구까지 대칭이어야 재정방향 재생 시 어긋나지 않는다. 참고 구현: `LazerSR.Hook\Widgets\ClassicAccuracyWidget.cs`.

osu! 기본 `LegacyAccuracyCounter`는 판정을 직접 누적하지 않고 `ScoreProcessor.Accuracy`(apply/revert 대칭 상태머신) bindable을 구독만 해서 이 문제 자체가 없다 — 커스텀 채점 공식이 필요 없다면 이 방식이 더 단순하다.

### 재계산 패턴 (CTS + Task.Run — 두 패턴 공통)

```csharp
cts?.Cancel();
cts = new CancellationTokenSource();
var ct = cts.Token;
Task.Run(() =>
{
    var playable = beatmap.Value.GetPlayableBeatmap(ruleset.Value, mods.Value, ct); // 반드시 백그라운드
    if (ct.IsCancellationRequested) return;
    var result = SomeCalculator.Calculate(playable, ct);
    Schedule(() => { if (!ct.IsCancellationRequested) updateDisplay(result); });
}, ct);
```

- `GetPlayableBeatmap`는 무거움 — 반드시 `Task.Run` 내부.
- UI 갱신은 반드시 `Schedule(...)` 경유.
- 이전 계산은 새 계산 시작 전에 취소.

---

---

## 패턴 C — 새 화면 추가 (2026-07-28 신규)

osu!에 없는 화면을 만드는 경우. 현재 유일한 사례는 무한 트레이닝(`architecture.md` §7).

### 화면 만들기

`OsuScreen`은 `public abstract`이지만 **abstract 멤버가 하나도 없다** — 본문이 빈 클래스로도 컴파일된다.

```csharp
public partial class MyScreen : OsuScreen
{
    protected override UserActivity? InitialActivity => null;      // 서버에 활동 상태 미전송
    protected override BackgroundScreen CreateBackground() => ...; // null이면 이전 배경 유지
}
```

기본값 중 챙길 것: `AllowUserExit`가 true여야 ESC/뒤로가기로 나갈 수 있다(아니면 갇힌다). `InitialActivity`는 기본이 null이라 그대로 두면 서버에 아무것도 안 간다 — 전송 경로는 `safety.md` 참고.

osu! 화면 구성을 그대로 빌리고 싶으면 원본 클래스를 재사용한다. 예: 멀티 로비 룩은 `OnlinePlayScreenWaveContainer` + `OnlinePlayBackgroundScreen` 파생 + `[Cached] OverlayColourProvider(Plum)` 세 가지면 재현된다. **`LoungeBackgroundScreen`은 직접 쓰지 말 것** — `OnExiting`이 무조건 `true`를 반환해서(`"This screen never exits."`) 화면을 나가도 배경이 스택에 눌러앉는다. 부모인 `OnlinePlayBackgroundScreen`을 상속하면 정상 exit된다.

### 화면 push하기

`ScreenExtensions.Push`/`IsCurrentScreen`은 public이다. 패치에서 push할 때는 `Parent` 체인을 타고 올라가 가장 가까운 `OsuScreen`을 찾은 뒤 push하면, 그 화면의 `OnSuspending`이 전환 연출을 알아서 처리한다.

```csharp
Drawable? current = someChildDrawable;
while (current != null && current is not OsuScreen) current = current.Parent;
if (current is OsuScreen screen && screen.IsCurrentScreen())
    screen.Push(new MyScreen());
```

`IsCurrentScreen()` 가드는 필수 — 아니면 전환 중 push 시 예외가 나고, 연타 방지도 겸한다.

### 게임플레이 화면(`Player` 파생)을 만들 때

`Player`는 `public abstract`이고 abstract 멤버가 `CreateResults` 하나뿐이라 파생 자체는 쉽지만, **`IGameplayLeaderboardProvider` 캐시가 암묵적 필수 계약**이다 (`ui-patching.md` §6 함정 표). 서버 격리가 필요하면 `safety.md`의 차단 표 5개를 그대로 따를 것.

---

## 패턴 D — 무한 트레이닝 세부패턴 추가 (2026-07-28 신규)

새 패턴 생성 규칙을 넣을 때. 전체 구조는 `architecture.md` §7.

1. `Training\Patterns\XxxGenerator.cs`에 `IPatternGenerator` 구현체를 만든다. **생성기는 bpm을 모른다** — 슬롯 배치만 정하고 ms 변환은 주입기가 한다.
2. `Training\PatternCatalog.cs`의 해당 대분류에 `SubPattern` 한 줄을 추가한다. **생성기 팩토리와 탐색 상수(시작 bpm / 스냅 / 휴식)가 여기 함께 들어간다** — 별도 상수 파일은 없다.
3. **정확도 임계는 패턴이 갖지 않는다**(2026-08-18) — 단기/무한 2종뿐이고 대기 화면에서 사용자가 정한다.

격자는 전 패턴 공통 절대규칙이다 — 1마디 = 32슬롯 × 4컬럼, 슬롯 = `7500/bpm` ms.

### 규칙을 받으면 먼저 검산할 것

지금까지 실제로 세 번 걸렸다. **밀도와 회피 규칙이 맞물리면 패턴이 고정되거나 성립하지 않는다.**

- 매 슬롯에 노트가 있고 컬럼이 4개일 때 "같은 컬럼 간격 ≥ 4"를 걸면 **연속 4슬롯이 항상 4컬럼 전부**가 되어 `s[i+4] = s[i]`가 강제된다 — 마디가 순열 하나의 반복(24가지)으로 굳는다.
- 동시치기가 섞이면 3슬롯 창의 노트 수가 늘어 같은 포화가 더 빨리 온다.
- 회피 규칙은 **슬롯 거리가 아니라 "직전 N개 노트"로 서술**하는 편이 안전하다. 밀도를 바꿔도 규칙이 깨지지 않는다.

균등 분배(컬럼별 등장 횟수)를 강제할 때는 **닫힌 형태로 풀리는지 먼저 확인**한다. 24코드잭은 "상보 쌍끼리 개수가 같다"로, 34코드잭은 "제외 컬럼 멀티셋 셔플"로 떨어져 재시도가 필요 없었지만, 덤프 계열과 23코드잭·미니잭 3종은 그렇지 않아 **할당량 가중 그리디 + 마디 재시도**가 필요했다(공용 도구는 `Patterns\PatternPicker.cs`).

### 반복해서 걸린 함정 3가지 (2026-07-29 추가)

**1. 제약을 하나 더 걸면 자유도가 통째로 사라지는 지점이 있다.** 회피 규칙이 촘촘해지면 남은 후보가 1개로 굳어 "추첨"이 사라진다. 그 자체는 문제가 아니지만(덴시핸스·13미니잭은 그래서 오히려 단순해졌다), **점프스트림에서는 그것이 긴 한손트릴의 직접적 원인**이었다 — 강제된 단노트가 트릴을 깨는 유일한 랜덤성을 없앴기 때문이다. 규칙을 추가할 때는 **"이 규칙이 무엇의 자유도를 0으로 만드는가"를 먼저 계산**한다.

**2. 국소 규칙으로 전역 극단을 막으려 하면 새어 나간다.** 점프스트림의 "같은 손 트릴 스텝 2연속 금지"는 트릴 스텝 하나가 앞뒤 단노트에 의해 양옆으로 늘어나는 경우를 못 잡아 5노트 트릴이 마디당 0.38회 남았다. **길이 상한이 목적이면 길이 자체를 상태로 들고 다녀야 한다** — 손별 교대 런 길이를 슬롯마다 갱신하는 방식으로 바꿔 해결했다.

**3. 벽(컬럼 1·4)이 인접 규칙과 만나면 흡수 상태가 생긴다.** 드르륵테크를 롤 계승으로 설계하면 따닥 컬럼이 컬럼 1·4일 때 다음 컬럼이 하나로 강제되어, 전이표에 **빠져나올 수 없는 상태 2개**가 생기고 몇 그룹 안에 단일 패턴 반복으로 굳는다(컬럼 균등 불가). **인접(롤) 조건을 요구하는 패턴은 전이표를 전수로 그려 흡수 상태가 없는지 확인**한다.

### 규칙을 이벤트/마디 단위로 환산해 산술을 먼저 맞춘다

연속핸드스트림과 축연타에서 연달아 걸렸다. "2슬롯마다 이벤트"면 마디당 16이벤트이므로 **16연타는 정확히 1마디**이고, 그러면 "2마디에 네 컬럼 한 번씩"은 성립할 수 없다(4마디가 필요하다). 노트 균등도 마찬가지로 **어떤 창에서 성립하는지 먼저 계산**해야 한다 — 축연타는 축이 16노트를 가져가므로 2마디로는 어떤 배분으로도 균등이 불가능하고, 4마디 창에서만 컬럼당 22노트로 맞는다.

## 패턴 E — osu! 본체 위젯 상속 (2026-07-29 신규)

새 위젯을 맨바닥부터 만들기 전에 **osu!에 비슷한 것이 이미 `ISerialisableDrawable`로 있는지 먼저 본다.** 상속하면 스킨 에디터 연동뿐 아니라 그 위젯이 이미 풀어놓은 문제까지 통째로 따라온다.

| 사례 | 상속한 것 | 공짜로 얻은 것 |
|---|---|---|
| `KeyViewerWidget` | `KeyCounterDisplay` | 키 개수 자동 결정, **리플레이·관전·실플레이 공통 입력 경로**, 되감기 판별(`Activate(forwardPlayback)`), 트리거 목록 변경 대응 |
| `BoxElementPlus` | `BoxElement` | 색상 팔레트 UI, 모서리 둥글기 |

### 주의

- **기존 타입에 설정을 "주입"할 수는 없다.** HarmonyX는 메서드 본체만 바꾼다 — 타입에 프로퍼티를 추가하지 못하므로 `[SettingSource]`를 덧붙이려면 **상속이 유일한 방법**이다. 기존 스킨에 배치된 원본 위젯에는 새 설정이 없다.
- **색은 `BindableColour4` + `[SettingSource]`** 로 노출하면 색상 팔레트 UI가 자동으로 붙는다 (`BoxElement`가 쓰는 방식).
- 등록은 기존과 동일하게 `SkinWidgetRegistrarPatch`의 배열에 타입 추가.
- 부모의 기본 동작이 우리 의도와 다르면 `LoadComplete`에서 되돌린다 (예: `KeyCounterDisplay`는 리플레이일 때만 강제 표시하므로 `AlwaysVisible.UnbindBindings()` 후 true 고정).

## 패턴 F — 결과창 확장 통계 항목 추가 (2026-07-29 신규)

결과창에서 스페이스바로 여는 패널(`StatisticsPanel`)에 새 표시를 넣을 때. 현재 사례는 판정 산점도(`architecture.md` §12).

1. `StatisticsPanel.CreateStatisticItems`를 Postfix하고 `__result`를 `List<StatisticItem>`로 실체화한다 (**패치 클래스 안에서 `yield return` 금지**).
2. 원하는 위치에 `StatisticItem`을 `Insert` — 인덱스 대신 **기존 항목 이름으로 찾아 그 앞뒤**에 넣는 편이 osu! 구성 변경에 강하다.
3. 룰셋 한정이면 `newScore.Ruleset.OnlineID` 가드로 처리한다.

```csharp
public static MethodBase? TargetMethod() => AccessTools.Method(typeof(StatisticsPanel), "CreateStatisticItems");
public static void Postfix(ScoreInfo newScore, ref IEnumerable<StatisticItem> __result) { ... }
```

- **룰셋 클래스(`ManiaRuleset.CreateStatisticsForScore`)는 패치할 수 없다** — `PatchAll` 시점에 룰셋 어셈블리가 미로드라 영구 스킵된다. 이유와 대안은 `architecture.md` §12.
- `requiresHitEvents: true`를 주면 **리플레이 미재생 스코어에서 osu!가 알아서** 항목을 빼고 안내 문구로 대체한다.
- `CreateContent`는 **지연 팩토리**이고 패널이 `LoadComponentAsync`로 로드하므로 무거운 준비는 BDL에 둔다. 생성자는 업데이트 스레드에서 돈다.
- Postfix 인자로 `playableBeatmap`(모드 적용 완료 변환 보면)과 `newScore.HitEvents` 전량이 들어온다 — `GetPlayableBeatmap`을 다시 돌리지 말 것.
- 콘텐츠는 `RelativeSizeAxes = Axes.X` + `AutoSizeAxes = Axes.Y`(또는 고정 `Height`). 단순 "이름 : 값"이면 osu! 본체의 `SimpleStatisticTable` / `SimpleStatisticItem<T>`를 그대로 쓸 수 있다.

## 패턴 G — 기존 화면 위에 게임플레이 화면 띄우기 (2026-08-08 신규)

결과창 같은 **이미 존재하는 osu! 화면**에서 우리 게임플레이 세션을 시작할 때. 현재 사례는 구간 연습(`architecture.md` §15).

패턴 C(새 화면 추가)와 겹치지만, **남의 화면 위에 얹힌다**는 점에서 추가로 챙길 것이 있다.

1. **서버 격리는 `safety.md`의 차단 표를 그대로 따른다.** 진입 화면의 `InitialActivity`가 null이면 우리도 null로 두는 것만으로 **활동 상태가 아예 전송되지 않는다**(값이 안 바뀌므로).
2. **`Beatmap`/`Ruleset`/`Mods`를 바꿨다면 직접 되돌린다.** 부모 화면이 이미 lease 중이면 lease의 자동 원복이 걸리지 않는다 (`ui-patching.md` 함정 표).
3. **배경음악은 직접 끄고 직접 되살린다** — `musicController.Stop()` / `EnsurePlayingSomething()`, 둘 다 `requestedByUser: false`.
4. **직전 `Player`가 아직 살아 있을 수 있다.** 결과창은 `Push`로 열리므로 그 아래 `Player`가 중단 상태로 남아 트랙 조정을 붙잡고 있다 — 배속이 곱해진다. 대응은 `architecture.md` §15.
5. 게임플레이 시작 지점을 옮길 거면 **`StartGameplay()` override**에서 한다(생성자·`CreateGameplayClockContainer` 아님).

화면을 찾아 push하는 방법은 패턴 C와 동일하다 — `Parent` 체인을 타고 가장 가까운 `OsuScreen`을 찾고 `IsCurrentScreen()` 가드 후 `Push`.

## 패턴 H — 세션 간 남는 데이터 저장 (2026-08-18 신규)

값이 osu!를 껐다 켜도 남아야 하면 `LazerSrStorage`를 쓴다. 현재 사례는 배경음 목록(`architecture.md` §16).

1. `LazerSrStorage.GetFolder("<기능이름>")`으로 기능별 폴더를 받는다. **통짜 파일 하나에 여러 기능을 모으지 않는다** — 한 기능의 손상이 전부를 날린다.
2. 읽기는 `ReadText`(실패 시 `null`), 쓰기는 `WriteText`(원자적 교체, 실패 시 `false`). **예외가 밖으로 나가지 않으므로 호출부는 "없으면 빈 상태"로만 처리하면 된다.**
3. 파일 첫머리에 **스키마 버전**을 둔다. 모르는 버전이면 빈 상태로 시작한다 — 재생성 가능한 데이터는 마이그레이션하지 않는다.
4. 직렬화·스키마는 그 기능이 소유한다. 공용 계층은 경로와 입출력까지다.

- **저장 실패가 기능을 막으면 안 된다.** 저장소는 가속·편의 장치이지 정답의 출처가 아니다. 이 성질을 지키면 다중 프로세스 락도 필요 없다(원자적 교체 + 실패 시 무시로 충분).
- **osu! 자산을 참조할 때는 절대 경로를 저장하지 않는다.** 비트맵은 MD5, 파일은 이름·해시로 적어두고 **쓸 때 realm으로 다시 해결**한다. 원본이 사라졌으면 조용히 건너뛰거나 목록에서 걷어낸다.
- 네임스페이스 주의: 저장 관련 타입을 `LazerSR.Hook.Storage`에 두면 osu.Framework의 `Storage` 타입과 충돌한다(`architecture.md` §16).

## 패턴 I — 하나의 위젯이 화면에 따라 다른 역할 (2026-08-18 신규)

`TrainingStatusWidget`이 인게임에서는 상태 표시, 선곡 화면에서는 곡 등록 UI다.

- osu!의 스킨 레이어는 HUD만이 아니다 — **`GlobalSkinnableContainers.SongSelect`**가 있고, `GetAllAvailableDrawables`가 컨테이너별로 거르지 않으므로 **등록된 위젯은 두 레이어 툴박스에 모두 나온다.**
- 어느 화면인지는 **DI로 판별한다**. `GameplayState`는 게임플레이에서만 캐시되므로 `[Resolved(canBeNull: true)]`로 받아 null이면 선곡 화면이다.
- 화면마다 있는 것과 없는 것이 다르다 — `OverlayColourProvider`는 선곡/대기 화면에는 있고 **게임플레이 HUD에는 없다.** 공유 부품은 `[BackgroundDependencyLoader(true)]` + 폴백으로 만든다.

## 계산기(Calculator) 작성 규칙

- **dan 계산은 예외** — `LazerSR.DanCalculator` 독립 프로젝트(mania-hub 포팅)에 있다. 진입점은
  `DanClassifier.ClassifyChart(osuText, input)` (sync). `string osuText` 기반이라 Hook에서 부르려면
  `LegacyBeatmapEncoder`로 재인코딩한다(`DanInfoWidget`이 실제 사례). 상세 `architecture.md` §24
- 위치: `LazerSR.Hook\Calculators\`
- Static 클래스, static `Calculate(...)` 메서드
- `if (beatmap is not ManiaBeatmap) return ...;` 가드
- `CancellationToken` 인자 받고 루프 경계에서 `ThrowIfCancellationRequested()`
- 결과 저장: 다른 패치도 값을 봐야 하면 `SunnyState`에 `volatile`/`Bindable` 필드로 publish, 아니면 `ScheduleOn` 클로저에 직접 전달 (예: MSD는 `SunnyState`를 거치지 않고 클로저로 직접 전달)
- **osu! 라이브 클래스를 재사용할 때는 반드시 별도 인스턴스로** (`safety.md` "라이브 vs 시뮬레이션 인스턴스" 참고) — 예: `ReplayScoreTimeline`이 `new ManiaScoreProcessor()`를 만드는 방식
- **sunny 상수를 프로세스 전역 기본값이 아니라 임시로만 바꿔 계산해야 하면** `SunnyConstants.WithIsolatedDiff(deltas, () => 계산)`를 쓴다(`architecture.md` §17). `AsyncLocal` 기반이라 다른 콜 컨텍스트의 sunny 계산에 전혀 영향을 안 준다 — 개인화diff 굽기/적용이 실제 사례다.

## 데이터 레코드 규칙

- 위치: `LazerSR.Hook\Data\`, 네임스페이스 `LazerSR.Hook.Data`
- C# `record`, 불변, positional constructor, 메서드/검증/기본값 없음
- 빈 배열 `[]`이 "데이터 없음" sentinel — `null` 쓰지 않음

## `SunnyManiaDifficultyCalculator` 인터페이스 (변경 없음)

```csharp
public double Calculate(IBeatmap, IReadOnlyList<Mod>?, CancellationToken, double? weightedNoteCountOverride = null)
public (double Time, double Strain)[] GetStrainTimeline(IBeatmap, IReadOnlyList<Mod>?, CancellationToken)
public static double CalculateWeightedNoteCount(IBeatmap, IReadOnlyList<Mod>?)   // 2026-08-08 추가
```

- `beatmap is not ManiaBeatmap`이면 `ArgumentException`
- osu! `DifficultyCalculator` 상속 금지 — 독립 파이프라인 유지 (`architecture.md` §6 잠재 위험 참고)
- 매 호출마다 `new`로 인스턴스화, 내부 캐싱 없음
- **`weightedNoteCountOverride`는 짧은 맵 보정 `W/(W+60)`의 W만 갈아끼운다** (2026-08-08 추가). 맵의 **일부 구간**을 재면서 길이 때문에 값이 깎이는 것을 막을 때 쓴다 — 전체 맵의 W를 `CalculateWeightedNoteCount`로 구해 넘긴다. **기본값 null이면 기존 동작 그대로**라 다른 호출부에는 영향이 없다.
- `CalculateWeightedNoteCount`는 strain 평가 없이 정렬 + 롱노트 가중 합만 돈다. 누적 규칙은 `Strain.StrainValueAt`을 그대로 미러링하므로 **한쪽을 바꾸면 다른 쪽도 같이 고쳐야 한다.**
