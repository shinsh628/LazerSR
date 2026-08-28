# Architecture Guide

LazerSR is a non-invasive instrumentation layer for osu!lazer. It injects a managed Hook DLL via .NET's startup-hook facility and renders sunnySR / Dan / MSD / strain-graph / replay-compare information on top of osu!'s own UI. No native injection, no osu! binary modification, no writes to osu! state.

**검증 기준**: 이 문서는 2026-07-17 기준 `lazerSRClean` 실제 코드(`StartupHook.cs`, `Patcher.cs`, `PipeServer.cs`, `SunnyState.cs`, `Patches/*.cs`)를 직접 읽고 작성됨. 과거 v1/v2 계획 문서가 서술했던 아키텍처와 실제 구현이 갈라진 지점은 각주로 표시.

---

## 1. Injection (`DOTNET_STARTUP_HOOKS`)

Launcher가 osu! 자식 프로세스에만 `DOTNET_STARTUP_HOOKS=<절대경로>\LazerSR.Hook.dll`을 설정한다. osu!의 .NET 8 런타임이 시작 시 이 DLL을 로드하고, global namespace의 `StartupHook.Initialize()`를 호출한다.

```csharp
public class StartupHook  // global namespace 필수
{
    public static void Initialize()
    {
        Run(DependencyResolver.Install, ...);
        Run(PipeServer.StartBackground, ...);
        Run(Patcher.Apply, ...);
    }
}
```

`Run()`은 각 단계를 try/catch로 감싸 한 단계 실패가 나머지를 막지 않게 한다. 실패 로그는 `HookLog.Write`로 간다 — **`HookLog`는 기본적으로 no-op**이다 (아래 §5 참고). 디버깅 시에만 임시로 활성화한다.

### 하드 제약
- `StartupHook`은 반드시 global namespace의 `public class StartupHook`, `public static void Initialize()` 시그니처.
- `Initialize()`에 무거운 작업 넣지 말 것 — osu! `Main()` 이전에 동기 실행됨.
- 새 부트스트랩 단계는 `DependencyResolver.Install`과 `Patcher.Apply` 사이에 `Run(...)`으로 추가.

---

## 2. Dependency Resolution

`DependencyResolver.Install()`이 `AppDomain.CurrentDomain.AssemblyResolve` 핸들러를 등록해 `LazerSR.exe`와 같은 폴더의 HarmonyX/MonoMod/Mono.Cecil/`LazerSR.SunnyCalculator`/`MinaCalc`를 resolve한다. 요청된 어셈블리 simple name이 `osu`로 시작하면 `null`을 반환해 osu! 자체 로더에 위임한다 — LazerSR이 osu! 어셈블리 사본을 직접 로드하면 타입 동일성이 깨진다.

`osu.Game`, `osu.Game.Rulesets.Mania` 등은 **컴파일 타임에만** `ProjectReference Private="false"`로 참조한다 (`..\..\osu\osu.Game\osu.Game.csproj`). 배포 출력에 `osu*.dll`은 포함하지 않는다.

---

## 3. Patcher — Deferred Auto-Discovery

`Patcher.Apply()`는 `osu.Game` 어셈블리 로드를 기다렸다가 (`AppDomain.AssemblyLoad` 이벤트, 이미 로드됐으면 즉시) `harmony.PatchAll(typeof(Patcher).Assembly)`로 **어셈블리 전체를 스캔**해 `[HarmonyPatch]` 클래스를 자동 등록한다. 새 패치는 파일만 추가하면 되고 `Patcher.cs` 수정 불필요.

`Harmony` 인스턴스는 하나뿐 — `HookBootstrap.HarmonyId = "dev.lazersr.hook"`. `_patched` 상태머신(0=idle→1=진행중→2=성공, 실패 시 0으로 복귀)이 `Interlocked.CompareExchange`로 중복 패치를 막는다.

---

## 4. 실제 존재하는 패치 (2026-07-17 기준, 코드로 직접 확인)

과거 설계원칙 문서는 "정확히 2개, 전부 `Select.*`의 `LoadComplete`"라고 서술했으나 **틀렸다.** 실제로는 18개(2026-08-21 기준):

| 파일 | 타겟 | 방식 |
|---|---|---|
| `Patches/DifficultyDisplayPatch.cs` | `BeatmapTitleWedge+DifficultyDisplay.LoadComplete` (1.1) | 오케스트레이터 — 계산 + `SunnyState.CurrentSr`/`CurrentDominant` publish + pill 삽입 |
| `Patches/BeatmapAttributesDisplayPatch.cs` | `BeatmapAttributesDisplay.LoadComplete` (2.1) | reflection insert |
| `Patches/BeatmapMetadataDisplayPatch.cs` | `BeatmapMetadataDisplay.LoadComplete` (4.1) | reflection insert + 재계산(2026-07-17 추가, §6 참고) |
| `Patches/ExpandedPanelMiddleContentPatch.cs` | `ExpandedPanelMiddleContent.LoadComplete` (4.2) | reflection insert |
| `Patches/DifficultyIconTooltipPatch.cs` | `DifficultyIconTooltip.SetContent` | reflection insert + **호버 대상별 재계산**(2026-07-29). 인스턴스별 `ConditionalWeakTable`로 pill/bindable/CTS 보관 (2026-07-17 수정, 과거엔 `static bool`로 세션 최초 1회만 동작하던 버그였음) |
| `Patches/PlayerGameplayPatch.cs` | `Player.LoadComplete` | 인게임 — replay-compare 타임라인 계산 + 100ms 폴링 파이프 브로드캐스트. **읽기 전용, 라이브 `ScoreProcessor`에 쓰지 않음** |
| `Patches/SkinWidgetRegistrarPatch.cs` | `SerialisedDrawableInfo.GetAllAvailableDrawables` (static 유틸리티, `LoadComplete` 아님) | 스킨 위젯 타입 배열에 항목 추가만 (읽기+concat) |
| `Patches/InfiniteTrainingMenuButtonPatch.cs` | `ButtonSystem.load` (**BDL 메서드**, 2026-07-28 추가) | 메인 메뉴 플레이 서브메뉴에 '무한 트레이닝' 버튼 추가 |
| `Patches/LocalOnlyLeaderboardSkipPatch.cs` | `PlayerLoader.refetchLeaderboard` (2026-07-28 추가, 2026-08-08 개명) | **유일한 `Prefix` 패치** — 로더가 `ILocalOnlyPlayerLoader`일 때만 리더보드 서버 조회 생략 (무한 트레이닝 + 구간 연습, 근거는 `safety.md`) |
| `Patches/ManiaJudgementLinePatch.cs` | `Player.LoadComplete` (2026-07-29 추가) | 인게임 — 리플레이/관전 + mania일 때만 각 컬럼에 판정 기준선 오버레이 부착. 읽기 전용 (§9) |
| `Patches/ManiaReplaySimulationPatch.cs` | `PlayerLoader.OnPlayerLoaded` (2026-07-29 추가) | 로딩 화면 — 리플레이 판정 시뮬레이션을 미리 실행. 읽기 전용 (§9) |
| `Patches/ManiaSimulationGatePatch.cs` | `PlayerLoader.ReadyForGameplay` getter (2026-07-29 추가) | 시뮬레이션이 끝날 때까지 로딩 화면 유지 (진척 없음 10초 시 해제). 읽기 전용 (§9) |
| `Patches/ResultsJudgementScatterPatch.cs` | `StatisticsPanel.CreateStatisticItems` (2026-07-29 추가) | 결과창 확장 통계 패널에 mania 판정 산점도 항목 삽입. 읽기 + 목록 concat (§12). 2026-08-08부터 `playableBeatmap`도 함께 넘긴다 (구간 sunnySR·구간 연습용) |
| `Patches/PersonalSunnyScoreCollectorPatch.cs` | `Player.ImportScore` (2026-08-19 추가) | 실제 개인 스코어가 생성될 때 개인화diff 큐에 자동 적재. 읽기 전용 — `Score`의 이미 완성된 필드만 읽는다 (§17) |
| `Patches/PersonalSunnyGameplayActivityPatch.cs`(패치 3개: Enter/Suspend/Exit) | `Player.LoadComplete`/`OnSuspending`/`OnExiting` (2026-08-20 추가) | `PersonalSunnyService.GameplayActive` static bool만 갱신 — `__instance`도 안 읽고 `OnExiting`의 `bool` 리턴값도 안 건드림. 개인화diff 백그라운드 워커가 게임플레이 중 동시성을 낮추기 위한 신호일 뿐 (§17) |
| `Patches/PatternCopyMenuButtonPatch.cs` | `ButtonSystem.load` (**BDL 메서드**, 2026-08-21 추가) | 메인 메뉴 **편집** 서브메뉴에 '패턴 복제' 버튼 추가 (§19) |

`Select.*`/`LoadComplete` 제약은 존재하는 패턴의 다수를 설명하지만 전부는 아니다. 새 패치를 만들 때 이 제약에 얽매이지 말고 §안전 가이드(`safety.md`)의 실제 레드라인을 따를 것.

---

## 5. IPC — 실제로는 매우 단순함

과거 문서(`feature-development-guide.md` 구판)는 `SunnyFeature` enum + `feature:<name>:on/off` 프로토콜 + `SunnyState.SetFeatureEnabled` + weak-reference subscriber 리스트를 서술했다. **이 설계는 실제로 구현된 적이 없다.** `SunnyState.cs` 실물:

```csharp
public static class SunnyState
{
    public static bool Enabled { get; private set; }
    public static readonly Bindable<StarDifficulty> CurrentSr = new();
    public static readonly Bindable<string> CurrentDominant = new(string.Empty);
    public static void SetEnabled(bool enabled) { ... }
}
```

- Launcher→Hook 명령은 `sunny:on` / `sunny:off` / `sunnyplus:on` / `sunnyplus:off`. 파이프 서버는 이 문자열들만 처리하고 나머지는 무시한다 (`PipeServer.HandleConnectionAsync`). **2026-08-21부터 `pc:`로 시작하는 줄은 패턴 복제 모드의 실시간 노트 스트림으로 먼저 걸러진다** (§19).
- **다중 클라이언트를 지원하지 않는 설계가 실제로 문제를 일으킨다.** newScreen이 붙으면 `_activeWriter`가 그쪽으로 넘어가 **Launcher는 그동안 브로드캐스트를 못 받는다**(sunnySR 표시 등이 갱신되지 않음). 기능에는 지장이 없어 그대로 뒀다.
- Hook→Launcher는 단순 ad-hoc 접두사 브로드캐스트: `sunnysr:`, `bestacc:`, `replaycompare:{score}:{combo}` 등. `PipeServer.BroadcastAsync`는 **연결된 클라이언트 1개(`_activeWriter`)에게만** 쓴다 — 다중 Launcher 동시 연결은 지원 안 함.
- **스킨 위젯(Dan/MSD/StrainGraph/ReplayCompare/SectionTimer/SunnyPP)의 개별 ON/OFF는 Launcher가 아니라 osu! 자체 스킨 에디터의 `[SettingSource]`로 이뤄진다.** Launcher의 "Features" 토글 섹션은 2026-05-19에 제거됐다 (`MainWindow.xaml`).
- 값 전파는 커스텀 subscriber 리스트가 아니라 **osu.Framework의 `Bindable<T>` 자체 메커니즘**을 그대로 쓴다 — 각 pill이 `pill.Current = SunnyState.CurrentSr`로 바인딩하면 이후 자동 동기화.
- `SunnyState.CurrentSr`에 값을 쓰는 곳은 원래 `DifficultyDisplayPatch`(송 셀렉트, 로컬 캐러셀 미리보기 기준) 하나뿐이었다. **2026-07-17부터 `BeatmapMetadataDisplayPatch`(4.1, 로딩 화면)도 자체적으로 씀** — 멀티플레이에서 로컬 미리보기와 실제 큐 맵이 달라 로딩 화면 pill이 stale 값을 보여주던 버그 수정(§6 참고).
- **난이도 아이콘 툴팁도 `SunnyState`를 쓰지 않는다**(2026-07-29). 같은 이유다 — 멀티 로비에서 이 툴팁이 가리키는 맵은 전역 슬롯과 무관하므로 stale 값이 떴다. 이제 툴팁이 표시 중인 `displayedContent`(비트맵·룰셋·모드)로 그 자리에서 재계산해 **툴팁 인스턴스 전용** 로컬 `Bindable<StarDifficulty>`에 넣는다. 계산 중이거나 맵이 로컬에 없으면 기본값(0). 자세한 제약은 §13.
- **결과창(4.2)은 `SunnyState`를 쓰지 않는다.** 같은 화면에 서로 다른 스코어(다른 mods)를 보여주는 패널이 여러 개 공존/전환될 수 있어서 전역 단일 슬롯이 부적합하기 때문 — 대신 `ExpandedPanelMiddleContentPatch`가 패널의 `score`로 그 자리에서 sunnySR을 재계산해 **패널 전용 로컬** `Bindable<StarDifficulty>`에 바인딩한다(2026-07-18). 같은 이유로 `ClassicAccuracyState`/`SunnyPPState`(둘 다 `LazerSR.Hook` 네임스페이스, `ScoreId`+값 단일 슬롯)는 `SunnyState`와 달리 **"어떤 스코어의 값인가"까지 같이 들고 있다** — `ClassicAccuracyWidget`/`SunnyPPWidget`이 실제 게임플레이 중 (`gameplayState.Score.ScoreInfo.ID` 기준으로) 발행하고, 결과창은 자신이 보여주는 패널의 `score.ID`가 일치할 때만 값을 쓰고 그 외엔 플레이스홀더(`-`)를 쓴다. `ScoreInfo.ID`(Guid)는 `Player.progressToResults`의 `Score.DeepClone()`을 거쳐도 보존됨을 확인하고 채택한 방식(`ScoreInfo.DeepClone()`이 `MemberwiseClone` 기반이라 `ID` 필드가 그대로 유지됨).

---

## 6. 알려진 아키텍처 결함 (미수정, 참고용)

- **`ConditionalWeakTable` 기반 per-instance state 안전망이 대부분의 패치에 없음** — `ui-patching.md`가 권장하는 패턴이지만 실제로는 정적 필드/직접 참조 위주. 지금은 우연히 문제가 안 되고 있음. (`DifficultyIconTooltipPatch`는 2026-07-17에 이 패턴으로 수정 완료 — 나머지 패치는 아직 미적용.)
- **`HookLog.Write`는 기본적으로 no-op** (2026-05-19 변경, "바탕화면 로그 파일 생성 제거"). 디버깅 시 직접 코드 수정으로 재활성화 필요.

**2026-07-17 수정 완료 (참고용 기록)**:
- `ManiaDifficultyHitObject : DifficultyHitObject` osu! 라이브 베이스 클래스 상속 제거 — bug A/B와 동일한 재발 위험이었음 (`LazerSR.SunnyCalculator\Difficulty\Preprocessing\ManiaDifficultyHitObject.cs`, 필요한 멤버 자체 재구현).
- `DifficultyIconTooltipPatch._pillInserted` static bool → `ConditionalWeakTable` 인스턴스별 추적으로 수정.
- 멀티플레이 로딩 화면 sunnySR pill stale 값 버그 — `BeatmapMetadataDisplayPatch`에 자체 재계산 로직 추가로 수정 (§5 참고).

---

## 7. 무한 트레이닝 (2026-07-28 신규)

osu!에 없는 **새 화면 계층**을 처음으로 추가한 기능. 기존 작업(기존 UI에 요소 삽입)과 성격이 다르다.

```
MainMenu ─(플레이 서브메뉴 버튼)→ InfiniteTrainingScreen ─(시작 버튼)→ InfiniteTrainingPlayerLoader → InfiniteTrainingPlayer
```

| 파일 | 역할 |
|---|---|
| `Screens/InfiniteTrainingScreen.cs` | 대기 화면. 배경은 멀티 로비와 동일 구성(`OnlinePlayScreenWaveContainer` + `OnlinePlayBackgroundScreen` 파생 + plum `OverlayColourProvider`). **3분할 레이아웃** — 좌: `PatternListPanel`, 우상: `TrainingSettingsPanel`, 우하: 시작 버튼. 시작 시 `Beatmap.Value`/`Ruleset.Value`/`Mods.Value`를 세팅하고 로더를 push |
| `Screens/PatternListPanel.cs` | 좌측 패턴 목록. 대분류 접기/펼치기, 세부패턴별 체크박스·bpm·sunnySR pill. **접기/펼치기는 행 목록을 통째로 재생성**해서 `AutoSize` 중첩 체인을 만들지 않는다 |
| `Screens/TrainingSettingsPanel.cs` | 우상단 설정. OD 슬라이더(실반영), 장/단기 측정 토글(소비처 없음). HP는 노출하지 않음 |
| `Training/PatternCatalog.cs` | 대분류 7개 정의. 세부패턴은 규칙이 있는 것만 채운다 (현재 24코드잭 하나) |
| `Training/TrainingSessionState.cs` | **세션 상태 단일 슬롯 — 측정 알고리즘이 쓰고 위젯이 읽는다.** 단계/모드/4행 슬롯/세트 인덱스/정확도 + 휴식 결정 이벤트 |
| `Training/ShortTermSearch.cs` | **단기 실력찾기 상태 기계** — 지속상승 → 휴식상승 → 휴식모드 |
| `Training/InfiniteSession.cs` | **무한 세션 상태 기계**(2026-08-18) — 비복원 추첨 → 세트 → 조기 종료 → 휴식 8초 반복 |
| `Training/TrainingMusicStore.cs` | 사용자가 등록한 배경음 목록. `%LocalAppData%\LazerSR\music\songs.json` |
| `Training/BeatmapMusicAnalysis.cs` | 비트맵이 배경음으로 쓸 수 있는지 검사 (변속/박자/프리뷰/여유 45초) |
| `Screens/SongListPanel.cs` | 대기 화면의 등록곡 목록 — 활성 토글 / 미리듣기 / 삭제 |
| `Training/JudgementAggregator.cs` | **정확도를 계산하는 유일한 곳.** 판정을 세트 단위로 집계 |
| `Training/TrainingProfile.cs` | 세부패턴별 단기 bpm(시작값 겸 결과)과 선택 상태. 세션 한정 |
| `Training/SyntheticPatternMap.cs` | sunnySR pill용 합성 보면 생성 + 계산 |
| `Widgets/TrainingStatusWidget.cs` | 상태 위젯. 상태 제목 / 다목적(플레이중·휴식) / 미정 3컨테이너 |
| `Widgets/TrainingAccuracyWidget.cs` | 무한 트레이닝 전용 정확도. 320 기반, **세트 단위로 리셋** |

### 위젯 ↔ 측정 알고리즘 연결부 (`TrainingSessionState`)

측정 알고리즘보다 위젯을 먼저 만들었으므로, 둘 사이의 계약을 이 상태 클래스가 고정한다. 알고리즘이 나중에 채워야 할 항목이 곧 이 클래스의 필드 목록이다.

| 방향 | 항목 |
|---|---|
| 알고리즘 → 위젯 | `Active`, `Phase`, `Mode`, `Previous`/`Current`/`Next`/`AfterNext`, `RestingResult`, `SetIndex` |
| 위젯 → 알고리즘 | `RestDecisionSubmitted` 이벤트 (`SubmitRestDecision(bool)`) |

- **세트 경계 통지는 `BeginNewSet()` 하나뿐이다.** 정확도 위젯이 `SetIndex` 변화만 보고 누적값을 리셋하므로, 알고리즘은 세트가 바뀔 때 이것만 부르면 된다.
- `Active`가 false면 위젯은 **스킨 에디터 배치용 플레이스홀더**를 그린다 (알고리즘이 없는 현재 상태가 그대로 이 경로다).
- 위젯은 static Bindable에 직접 `BindValueChanged`하지 않고 **`GetBoundCopy()`를 거친다** — static 필드의 이벤트에 델리게이트가 강한 참조로 남으면 위젯이 GC되지 않는다.
- 두 위젯은 같은 상태를 공유한다. 위젯을 나눈 건 스킨 에디터 배치 자유도 때문이고 상태 소스는 하나다.
| `Screens/InfiniteTrainingBeatmap.cs` | **코드로 만드는 시드 비트맵.** 4K, 노트 8개(1000ms·5,000,000ms에 전 칼럼 동시치기), `BeatmapInfo.Length = 5,000,000` → `WorkingBeatmap.GetVirtualTrack()`이 약 83분짜리 **무음 가상 트랙**을 자동 생성. osu! 본체의 `TestWorkingBeatmap`(오디오/배경/스킨 전부 null, 각 소비처가 폴백 보유)으로 감쌈 |
| `Screens/InfiniteTrainingPlayer.cs` | `Player` **직접 상속**(`SubmittingPlayer` 계열 아님). `Update()`에서 `TrainingSequencer`를 구동하기만 한다 — 노트 생성/주입 로직은 전부 `Training\`에 있다. 같은 파일에 `InfiniteTrainingPlayerLoader`(리더보드 스킵 식별용 마커) 포함 |

### 패턴 생성 파이프라인 (`Training\`, 2026-07-28 추가)

노트는 **생성기 → 마디 큐 → 주입기** 3층을 거친다. 핵심은 확정 지점이 주입기 하나로 좁혀진다는 것 — 큐에 남아 있는 마디는 아직 화면에 없으므로 자유롭게 교체·폐기할 수 있고, 나중에 실력탐색이 판단 결과를 반영하는 자리가 여기다.

| 파일 | 역할 |
|---|---|
| `Training/TrainingMeasure.cs` | `SlotState` 열거형(LN 확장 대비 `bool` 아님), `TrainingGrid`(격자 절대규칙), `TrainingMeasure`(128슬롯 배치) |
| `Training/IPatternGenerator.cs` | 세부패턴 생성 계약. `Generate(rng, previous)` — 마디 단위, **bpm 인자 없음** |
| `Training/Patterns/ChordJack24Generator.cs` | 24코드잭. 4동치(슬롯 0,4,…,28) + 2동치(슬롯 2,6,…,30), 2동치는 마디 내 컬럼 균형 강제 |
| `Training/Patterns/ChordJack34Generator.cs` | 34코드잭. 24코드잭의 2동치 자리가 3동치. 3동치는 "어느 컬럼을 뺄까"가 유일한 자유 변수라 **컬럼 균등이 제외 컬럼 멀티셋 셔플로 곧장 풀린다** |
| `Training/Patterns/ChordJack23Generator.cs` | 23코드잭. 3동치 + 2동치 교대(마디당 40노트). **4연타 금지**가 `B_{k-1} ∩ B_k ⊆ {a_k}` 하나로 압축됨. 컬럼당 10노트 균등 |
| `Training/Patterns/MiniJack13Generator.cs` | 13미니잭. 3동치 + 따닥 단노트. 4연타 금지가 `s_{k+1} ≠ s_k` 하나로 압축됨. 컬럼당 8노트 균등 |
| `Training/Patterns/MiniJack12Generator.cs` | 12미니잭. 2동치 + 따닥 단노트(마디당 24노트, 최저 밀도). 3연타 금지. 컬럼당 6노트 균등 |
| `Training/Patterns/MiniJack22Generator.cs` | 22미니잭. 2동치를 짝으로 두 번씩. 4연타 허용·6연타 금지 = `P_{k-1} ∩ P_k ∩ P_{k+1} = ∅`. 컬럼당 4짝 균등 |
| `Training/Patterns/PatternPicker.cs` | 위 4개가 공유하는 추첨 도구(비트 유틸 + **할당량 가중 추첨**). 나머지 생성기는 각자 닫힌 형태로 풀려 쓰지 않는다 |
| `Training/Patterns/SingleDumpGenerator.cs` | 싱글덤프. 매 슬롯 1노트, **직전 2개 노트와 다른 컬럼**, 마디당 컬럼별 8회 균등. 그리디 + 마디 재시도 |
| `Training/Patterns/DenseDumpGenerator.cs` | 덴시덤프. 4슬롯마다 2동치 + 나머지 단노트(마디당 40노트). 간격 1 금지, **간격 2는 8슬롯 구간마다 정확히 1회**, 그 외 간격 3 이상. 컬럼별 10회 균등 목표 |
| `Training/Patterns/RollStreamGenerator.cs` | 드르륵. 인접 컬럼 런(시작/방향/길이)을 이어 붙여 매 슬롯 단노트로 채운다. 간격 3 준수, **컬럼 균등 없음**(가운데 쏠림이 의도된 성질). 같은 런 2연속 뒤에는 그 런을 배제 |
| `Training/Patterns/RollTechGenerator.cs` | 드르륵테크. 4슬롯 그룹의 뒤 두 슬롯이 항상 따닥. **이름과 달리 드르륵이 아니라 싱글덤프를 계승**(아래 함정 참고). 따닥 컬럼 균등 + 인접 중복 금지 |
| `Training/Patterns/AxisJackGenerator.cs` | 축연타. 한 컬럼 **16연타 = 정확히 1마디** + 4이벤트마다 동치(2,3,2,3). 축을 손 교대 봉지로 뽑아 4마디마다 네 컬럼 균등(컬럼당 22노트) |
| `Training/Patterns/JumpStreamGenerator.cs` | 점프스트림. 짝수 슬롯 2동치 + 홀수 슬롯 단노트. 상보 배제는 **데드락 방지로 필수**. 한손 트릴 5노트 금지 + 동일 동치 4연속 금지 |
| `Training/Patterns/SingleHandStreamGenerator.cs` | 싱글핸드스트림. 매 슬롯 노트, 4슬롯마다 3동시치기 + 양옆에 그 여집합 컬럼(`[x][동치][x]` 샌드위치), 나머지는 이웃 둘을 뺀 무작위. 연속 동시치기는 서로 다름. 마디당 32이벤트·48노트 |
| `Training/Patterns/DenseHandStreamGenerator.cs` | 덴시핸드스트림. 싱글핸스의 자유 슬롯이 2동치가 된 것. 양쪽 이웃 배제 규칙 때문에 **추첨이 아예 사라지고** 마디가 `x₀…x₈` 수열로 완전히 결정된다 |
| `Training/Patterns/ContinuousHandStreamGenerator.cs` | 연속핸드스트림. 6반복(3동치+1동치 ×6) + 2휴식 = **주기 16슬롯**(마디의 정확히 절반). 마디당 60노트로 최고 밀도. 한 손 12노트 트릴 + 다른 손 점프잭이 정의상 발생 |
| `Training/TrainingSequencer.cs` | 마디 큐 보유 + 생성 요청 + **슬롯→ms 변환** + `Playfield.Add`. 휴식은 별도 타입 없이 다음 마디 시작 시각을 미루는 것으로 표현. 배경음이 곡의 다운비트에 맞춘 시작 시각을 넘길 수 있다(`alignedStartMs`) |

**격자 절대규칙** (모든 세부패턴 공통, `TrainingGrid`):

```
1마디 = 32슬롯(32분음표) × 4컬럼 = 128슬롯
슬롯 = 7500 / bpm ms      (120bpm에서 62.5ms)
마디 = 240000 / bpm ms    (120bpm에서 2000ms)
16분음표 = 2슬롯, 8분음표 = 4슬롯
```

생성기는 bpm을 모르고 슬롯 위에서만 동작한다. ms 변환은 주입기 전담이다.

### 무한 세션 (2026-08-18 신규)

측정이 아니라 **고정 bpm 반복**이다. 선택된 세부패턴을 비복원 추첨으로 돌리며, 끝나는 조건은 사용자가 ESC로 나가는 것뿐이다.

| 항목 | 값 |
|---|---|
| 세트 길이 | **40초를 넘지 않는 최대 마디 수** (마디 단위로 내림, 최소 1마디) |
| bpm | 대기 화면 패턴 목록의 값 그대로. 세션 내내 고정 |
| 조기 종료 | 세트 **누적** 정확도가 목표 미만인 상태가 **5초** 이어지면 |
| 휴식 | **8초**. 세트의 마지막 노트가 **판정된 시각**부터 잰다 |
| 패턴 추첨 | 비복원, 소진하면 봉지 재충전. 위젯의 다음·다다음 때문에 **3개를 미리 뽑아둔다** |

- **조기 종료는 "새로 내보내지 않는 것"이지 화면의 노트를 지우는 게 아니다.** 이미 주입된 노트는 그대로 내려오고 전부 판정되면 세트가 끝난다. 그래서 두 가지가 함께 필요하다 — `TrainingSequencer.StopEmitting()`(미주입 마디 폐기 + 실제로 나간 노트 수/끝 시각 반환 + 다음 세트 시작 기준을 그 끝으로 당김)과 `JudgementAggregator.TruncateSet()`(완료 조건 정정 + 즉시 완료 검사). **후자가 없으면 그 세트는 오지 않을 판정을 기다리며 세션이 그 자리에서 멎는다.**
- 세트 완료 통지는 판정 콜백에서 오므로 그 자리에서는 게임플레이 시각을 알 수 없다. **휴식 기산점은 바로 다음 `Update`의 시각**으로 잡는다.
- sunnySR은 bpm이 고정이라 **패턴당 한 번만 계산**하고 재사용한다 (단기는 자리별로 매번 다시 계산한다).
- 목표 정확도는 **단기와 별개 값**이고 둘 다 대기 화면에서 사용자가 −/+로 정한다. 두 모드는 배타이며 둘 다 끄면 시작할 수 없다.
- **알려진 성질**: 세트 끝이 패턴 고유 주기 중간에서 잘릴 수 있다(축연타는 컬럼 균등이 4마디 주기). 측정이 아니라 무한이라 그대로 뒀다.

**현재 범위**: **단기 실력찾기**와 **무한 세션**이 동작한다. 장기 실력찾기는 **폐기됐다**(2026-08-18) — 모드는 단기/무한 둘뿐이다. 단기는 대기 화면에서 체크한 세부패턴을 완전 셔플해 순회하며 각각의 적합 bpm을 찾고, 휴식 모드에서 저장/폐기 응답을 받아 `TrainingProfile`에 남긴다. 전부 끝나면 노트 생성 없이 대기한다.

**세부패턴은 15개**다 (2026-07-29 기준, 목표 21개). 대분류 7개 중 빈 것은 없다 — 스트림 1 / 점프스트림 1 / 핸드스트림 3 / 덤프 2 / 테크 2 / 미니잭 3 / 코드잭 3. 대분류 `짤연`은 `테크`로 개명됐다(id `shortburst` → `tech`).

### 단기 실력찾기 (`ShortTermSearch`)

| 단계 | 세트 | 규칙 |
|---|---|---|
| 지속상승 | 2마디, 휴식 없음 | 정확도 ≥ 임계면 +1스냅, 미달이면 동결. **연속 2세트 미달**이면 이탈 |
| 휴식상승 | 2마디 + 패턴별 휴식 | 이탈한 세트의 bpm − 1스냅에서 시작. ≥ 임계면 +1스냅, 미달이면 −1스냅. **어떤 bpm이 누적 3회 미달**하면 그 bpm − 1스냅으로 확정 |

**임계는 하나뿐이고 두 단계가 같은 값을 쓴다**(2026-08-18). 패턴별 T1/T2 두 쌍이던 것을 없앴다 — 정확도 임계는 **단기/무한 2종**뿐이고 대기 화면에서 사용자가 직접 정한다. `PatternGroup`은 이제 탐색 상수를 갖지 않는다.
| 휴식 모드 | 노트 없음 | 결과 표시 + 저장/폐기 응답 대기 |

지속상승에서 도달한 bpm은 피로가 섞여 있어 기록하지 않는다 — 측정은 휴식이 들어간 구간에서만 이뤄진다.

**파이프라인 지연 처리가 단계마다 다르다.** 노트는 판정보다 `LOOKAHEAD_MS`(700) 먼저 주입돼야 하는데, 휴식상승은 휴식(2000ms)이 그 시간을 흡수하므로 **세트 하나만 미리 잡아 즉시 반응**한다. 반면 **휴식이 없는 지속상승은 두 세트를 미리 잡아야** 노트가 끊기지 않으므로, 세트 N의 결과가 **세트 N+2**에 반영된다. 상승 구간이라 1세트 지연의 대가가 거의 없다.

**판정 귀속은 시간 역산으로 한다.** 시퀀서가 세트마다 `[시작ms, 끝ms)`와 노트 수를 알려주고, 집계기가 `result.HitObject.StartTime`으로 소속을 찾는다. 별도의 마디 태그 자료구조가 필요 없다. 구간은 **반열림**이어야 한다 — 휴식이 없는 지속상승에서는 앞 세트의 끝과 뒤 세트의 시작이 정확히 같아지기 때문이다.

**정확도 계산 주체는 `JudgementAggregator` 하나뿐**이고 위젯은 `TrainingSessionState.CurrentAccuracy`를 표시만 한다 — 화면의 숫자와 알고리즘의 판단 근거가 갈라질 수 없다.

**측정 구간 OD는 8.5 고정**이다. 판정창은 `ApplyDefaults`에 넘기는 난이도 객체가 정하므로 사용자 설정 OD와 분리된다.

**패턴별 노트 밀도가 다르다.** 24코드잭은 이벤트 간격이 2슬롯(16분음표)이고 싱글덤프는 1슬롯(32분음표)이다. 따라서 **같은 bpm 수치가 두 패턴에서 서로 다른 노트 발생률을 뜻한다** — 임계·스냅이 패턴별로 따로 잡히므로 측정에는 지장이 없지만, 패턴 간 bpm 수치를 직접 비교할 수는 없다.

**알려진 제약**: 세트 결과로 다음 세트 파라미터를 정하려면 **휴식 길이 ≥ `lookahead_ms`**여야 한다 (판단 시점이 다음 세트 첫 마디의 주입 시점보다 앞서야 하므로). 현재 값은 휴식 2000ms < lookahead 3000ms라 조건을 만족하지 않지만, 파라미터가 고정이라 지금은 무해하다. 탐색 구현 시 해결할 항목.

### 배경음 (`MusicLibrary` / `TrainingMusicPlayer`, 2026-07-29 추가)

배경음은 **게임플레이 시계와 완전히 분리된 독립 채널**이다. 판정 타이밍에 영향을 주지 않는다.

| 파일 | 역할 |
|---|---|
| `Training/TrainingMusicStore.cs` | 사용자가 등록한 곡 목록(2026-08-18부터). 동봉 곡 4개와 `Music\` 폴더는 폐기됐다 |
| `Training/MusicLibrary.cs` | 옥타브 정규화, 마디 스냅 계산, realm 해결(`Resolve`/`LoadTrack`/`PruneMissing`) |
| `Training/TrainingMusicPlayer.cs` | `DrawableTrack` 소유, 곡 봉지 선택, 배속 예약 |

**시계 소스 교체는 쓰지 않는다.** `MasterGameplayClockContainer.StopUsingBeatmapClock()`이 public이고 `Player.CreateGameplayClockContainer`가 `protected virtual`이라 기술적으로는 가능하지만, 그 경로에서는 **곡의 재생 위치가 곧 게임플레이 시각**이 되어 83분 세션과 2~5분 곡이 어긋난다. 곡마다 시각을 되감으면 주입된 노트가 전부 강제 미스가 된다. osu! 본체의 `BackgroundMusicManager`(랭크드 플레이)가 쓰는 독립 `DrawableTrack` 방식이 정답이다.

**정밀 시작이 불가능하므로 인과를 뒤집는다.** BASS 시작 지연이 일정하지 않아 "세트 첫 노트 시각에 트랙을 정확히 시작"은 달성할 수 없다. 대신 트랙을 먼저 돌리고 **실제 재생 위치를 읽어 "다음 다운비트가 오는 게임플레이 시각"을 계산**해 그것을 세트 시작 시각으로 쓴다(`TryBeginSong` → `TrainingSequencer.EnqueueSet(alignedStartMs)`). 세트마다 다시 맞추므로 드리프트가 누적될 구간이 없다.

**스냅과 배속의 기준이 서로 다르다.**

```
스냅 그리드 = 원본 bpm의 마디   (offset + k × 4 × 60000/bpm)
배속        = 패턴bpm / 펄스bpm  (baseBpm, 보통 원본의 절반)
```

원본 기준으로 스냅하는 이유는 맵퍼들이 **프리뷰 타임을 원본 마디 시작에 정확히 찍어놓기 때문**이다(실측 4곡 중 3곡). 펄스 bpm으로 스냅하면 마디 길이가 2배가 되어 그 지점이 마디 한가운데가 된다. 훈련 마디가 소비하는 원본 오디오는 `240000/펄스bpm`으로 원본 마디의 정수배라, 세트 내내 다운비트 정렬이 유지된다.

**옥타브 정규화는 한계를 벗어날 때만 적용한다.** 배속이 `[0.5, 2.0]`(osu!의 하프타임 하한과 `UserPlaybackRate` 상한)을 벗어나면 펄스 bpm을 2배/절반으로 옮긴다. 2의 거듭제곱은 박자 그리드를 보존하므로 정렬에 영향이 없다. 항상 1.0 근처로 맞추는 순수 정규화는 곡별 펄스 판단을 덮어쓰고 탐색 도중 체감 펄스가 뒤집혀 채택하지 않았다.

**곡 교체 시점은 단계마다 다르다.** 휴식이 있는 휴식상승은 **세트마다 새 곡**이고, 휴식이 0인 지속상승은 **곡을 이어서 튼다**(세트가 맞붙어 있어 박자가 그대로 이어진다). 지속상승은 세트를 2개 미리 잡으므로 **배속은 즉시가 아니라 그 세트가 시작하는 시각에 예약 적용**해야 한다 — 즉시 바꾸면 아직 흐르고 있는 세트의 박자가 어긋난다.

**음악 문제가 훈련을 멈추면 안 된다.** 정렬 대기 중 `TryBeginSong`이 계속 `false`를 돌려주면 세트 편성이 막혀 **노트가 아예 생성되지 않는다.** 3초 타임아웃과 로드 실패 시 `Usable = false`로 영구 비활성화하고, 그 뒤로는 정렬 없이 평소대로 편성한다.

#### 곡은 사용자가 등록한다 (2026-08-18, 동봉 방식 폐기)

배포에 곡을 동봉하던 방식을 버리고 **사용자 비트맵을 참조**한다. 저작권 논점이 사라지고 배포 용량도 17MB 줄었다.

- 등록은 **선곡 화면에 배치한 `TrainingStatusWidget`**이 한다(§16). 검사 항목은 변속 없음 / 4·4 박자 / **프리뷰 타임이 실제로 찍혀 있을 것** / 시작 지점 이후 **여유 45초** / 오디오 실존.
- **프리뷰 미설정(-1)은 받지 않는다.** osu!는 길이의 40%로 폴백하는데 그 지점은 마디와 아무 관계가 없어 스냅의 전제가 깨진다.
- **펄스 bpm은 자동으로 정할 수 없다** — 등록해뒀던 4곡 중 하나가 절반이 아니었다. 180 이상이면 절반으로 추정만 하고 사용자가 −/+로 고친다.
- **오디오를 복사하지 않는다.** 저장하는 것은 비트맵 MD5와 오디오 파일 이름·해시뿐이고 **절대 경로는 저장하지 않는다**(osu! 데이터 폴더를 옮기면 전부 깨진다). 재생 직전에 realm으로 다시 해결한다.
- 맵이 지워지면 그 곡도 사라진다 — 대기 화면 `OnEntering`에서 `PruneMissing`으로 걷어내고, 재생 중 한 곡이 안 열리면 다른 곡을 집어본다.
- 중복 등록은 **오디오 파일 해시**로 막는다(검사는 난이도 단위지만 오디오는 맵셋 공용이다).

**미구현**: 일시정지 연동 — 게임플레이를 멈춰도 음악은 계속 흐른다.

### 반드시 알아야 하는 제약

- **시작 시점에 노트가 0개면 진입 자체가 불가능하다.** `Player.LoadedBeatmapSuccessfully`가 `DrawableRuleset.Objects.Any()`로 판정하고, false면 `PlayerLoader`가 즉시 exit한다. 시드 비트맵에 노트를 넣어둔 이유가 이것.
- **컬럼 수는 `BeatmapInfo.Difficulty.CircleSize`가 결정한다** (`ManiaBeatmapConverter`의 `IsForCurrentRuleset` 경로). 세션 도중 변경 불가 — `ManiaPlayfield`/`Stage` 생성자에서 고정되므로 7K 확장은 "시작 시 선택"으로만 가능.
- **노트를 현재 시각보다 과거에 삽입하면 즉시 강제 미스** (`OrderedHitPolicy.HandleHit`). 선행 시간(3000ms)이 스크롤 시간보다 커야 한다.
- **주입하면 채점이 어긋난다** — `ScoreProcessor.ApplyBeatmap`이 `MaxHits`/`MaximumTotalScore`를 로드 시점에 사전계산하고 갱신 API가 없다. 결과적으로 `HasCompleted`가 영원히 false(무한 모드에서는 오히려 의도된 동작)이고 점수는 1,000,000을 넘을 수 있다. **점수 HUD는 스킨 영역으로 두기로 결정됨.**
- mania는 시간 경과 HP 드레인이 없다 (`ManiaHealthProcessor.ComputeDrainRate() => 0`). 83분 공백이 체력에 영향을 주지 않으며, 사망은 미스 누적으로만 발생한다.
- **판정창(OD)은 비트맵이 아니라 `ApplyDefaults`에 넘기는 난이도 객체가 정한다** (`HitObject.ApplyDefaultsToSelf` → `HitWindows.SetDifficulty`). 주입은 우리가 하므로 **노트마다 다른 OD를 줄 수 있고 세션 도중 전환도 가능하다** — 측정 단계만 OD 8.5로 돌리는 것이 이 경로로 가능하다.
- **인게임 판정창 밖의 입력은 `JudgementResult`를 만들지 않는다** (`DrawableNote.CheckForResult`가 `HitResult.None`이면 그냥 `return`). 따라서 자체 계산기는 인게임 판정창보다 **좁게는 재분류할 수 있어도 넓게는 못 한다** — "게임은 사용자 OD, 계산은 다른 OD 고정" 방식은 데이터가 유실된다. 노트별 OD 지정이 정답인 이유.
- **HP(`DrainRate`)로는 사망을 막을 수 없다.** `ManiaHealthProcessor.GetHealthIncreaseFor`에서 미스 감소량은 `-(DrainRate + 1) * 0.0075`라 `DrainRate = 0`에서도 미스당 0.0075가 깎인다(약 133미스면 사망). 그래서 `InfiniteTrainingPlayer`가 `CheckModsAllowFailure() => false`로 **사망 자체를 차단한다.** 결과적으로 `DrainRate`는 체력바 표시에만 관여하므로 설정으로 노출하지 않는다.
- **`Player`를 직접 상속하면 `IGameplayLeaderboardProvider`를 반드시 캐시해야 한다.** 스킨 HUD 기본 구성에 포함된 `DrawableGameplayLeaderboard`가 non-nullable `[Resolved]`로 요구하며, 없으면 매 세션 `DependencyNotRegisteredException`이 쌓인다. osu! 하위 클래스 5개 전부 이걸 캐시한다(추상 멤버가 아니라 컴파일러가 잡아주지 않음). 리더보드가 없는 경우 `EmptyGameplayLeaderboardProvider` 사용 — `EditorPlayer`와 동일 방식.

---

## 8. 배포 구조 (변경 없음, 계속 유효)

```
[배포]\
  LazerSR.exe                  ← SingleFile 번들
  LazerSR.Hook.dll              ← 루즈 파일 필수 (DOTNET_STARTUP_HOOKS 요구)
  LazerSR.SunnyCalculator.dll   ← 루즈 파일
  0Harmony.dll, MonoMod.*.dll, Mono.Cecil.*.dll ← 루즈 파일
  MinaCalc.dll                  ← 네이티브 MSD 라이브러리 (P/Invoke)
```

- `LazerSR.Hook.dll`은 SingleFile 번들에서 제외 (실제 파일 경로 필요).
- HarmonyX/MonoMod/Mono.Cecil은 exe와 같은 폴더 (DependencyResolver가 거기서 resolve).
- `osu*.dll/xml/pdb` 배포 금지.

빌드/배포 절차는 `end.md` 참고.

---

## 9. 리플레이 판정 기준선 (mania, 2026-07-29 신규)

리플레이 재생/관전 중에 **판정이 실제로 일어나는 시각**을 흰 가로선으로 표시한다. 노트 그래픽이 보여주는 위치와 진짜 판정 지점의 차이를 눈으로 확인하는 것이 목적.

| 파일 | 역할 |
|---|---|
| `Patches/ManiaJudgementLinePatch.cs` | `Player.LoadComplete` Postfix. `ReplayPlayer`/`SpectatorPlayer` + `ManiaPlayfield`일 때만 동작. 컬럼별 데이터를 모아 오버레이 2종을 부착 |
| `Drawables/ManiaJudgementLineOverlay.cs` | 컬럼 1개분 판정 기준선. 정지선 1개 + 스크롤선 N개 |
| `Drawables/ManiaPressOverlay.cs` | 컬럼 1개분 입력 사각형 |
| `Data/ManiaPressExtractor.cs` | 리플레이 프레임 → 컬럼별 누름 구간 |
| `Drawables/OutlinedNumber.cs` | 검은 8방향 테두리를 두른 숫자 (노트 위에서도 읽히게) |
| `Drawables/ManiaReplayDisplaySettings.cs` | 리플레이 설정 창의 "판정 표시" 그룹 — 표시 4종 개별 토글 |
| `Data/ManiaOverlayVisibility.cs` | 그 토글 상태 (세션 한정, config 미저장) |

**그리는 것**:

| 요소 | 시각 | 모양 |
|---|---|---|
| 정지 기준선 | 항상 Y=0 | 컬럼 폭 전체 가로선 |
| 단노트 / 롱노트 헤드 | `StartTime` | 컬럼 폭 전체 가로선 |
| 롱노트 릴리즈 | `EndTime` | 컬럼 폭 **70%** 가로선 |
| 롱노트 연결선 | `StartTime`~`EndTime` | 컬럼 중앙 세로선 |

릴리즈 판정창은 1.5배 관대하지만(`TailNote.RELEASE_WINDOW_LENIENCE`) **중심은 정확히 `EndTime`**이라 선 위치에는 영향이 없다 — 나중에 판정창을 띠로 그린다면 그때 테일만 폭이 1.5배가 된다.

세로 연결선은 판정선을 지난 부분을 잘라낸다(붙잡고 있는 동안 아래에서부터 소진). 롱노트 바디가 `sizingContainer`로 줄어드는 것과 같은 표현이고, 키 영역 위로 덧그려지는 것도 함께 막는다.

### 설계상 반드시 지킬 것

- **위치 계산을 직접 하지 않는다.** 전부 `ScrollingHitObjectContainer.PositionAtTime(time)`에 위임한다. osu!가 노트를 배치하는 것과 동일한 경로이므로 스크롤 속도·SV·재생 배속이 자동으로 반영된다. 자체 px/ms 환산을 넣는 순간 SV 맵에서 어긋난다 (Mania-Replay-Master가 실제로 이 함정에 있다).
- **`Stage.HIT_TARGET_POSITION`(110)을 하드코딩하지 않는다.** 부착 대상인 `ColumnHitObjectArea`가 이미 히트포지션 패딩이 적용된 좌표계라 **Y=0이 곧 진짜 판정선**이다. 스킨이 `LegacyManiaSkinConfigurationLookups.HitPosition`으로 위치를 바꿔도 자동으로 따라간다.
- **부착 위치는 `column.HitObjectArea`**(= `Content`)이지 `column.UnderlayElements`가 아니다. `UnderlayElements`는 히트타겟 그래픽보다 뒤라 판정선 근처에서 가려진다.

### `DrawableHitObject`를 쓰지 않은 이유

에디터의 `ManiaBeatSnapGrid`처럼 `ScrollingHitObjectContainer` + 더미 `DrawableHitObject`를 넣으면 위치·수명 관리가 공짜지만, **판정되지 않는 DHO는 컬링되지 않는다.** `DrawableHitObject.updateState`가 `LifetimeEnd = double.MaxValue`로 초기화한 뒤 `state != Idle || HitObject.HitWindows == null`일 때만 실제 값으로 덮는데(`DrawableHitObject.cs:487`), 우리 선은 영원히 `Idle`이고 기본 `HitWindows`가 non-null이라 두 조건 모두 거짓이다. 결과적으로 한 번 등장한 선이 전부 살아남는다. `ManiaBeatSnapGrid`가 무사한 건 선을 가시 범위만큼만 만들고 매번 재생성하기 때문.

그래서 **정렬된 시각 배열 + 매 프레임 이분 탐색 + `Box` 풀**로 직접 윈도잉한다. 매 프레임 상태 없이 탐색하므로 되감기/시크에도 자동으로 맞는다.

윈도잉에서 걸리기 쉬운 지점 두 가지:

- **롱노트는 `EndTime` 기준으로 정렬해 이분 탐색한다.** 시작 기준으로 찾으면 이미 붙잡고 있는(시작이 과거인) 롱노트를 놓친다. 같은 컬럼의 롱노트는 겹칠 수 없으므로 시작 순서와 끝 순서가 같다.
- **탐색 중단 조건은 `|y| > scrollLength`가 아니라 부호를 본다.** 화면보다 긴 롱노트는 시작이 화면 아래(과거 쪽), 끝이 화면 위(미래 쪽)라 양쪽 |y|가 모두 크다 — 절댓값으로 끊으면 그 롱노트를 붙잡고 있는 내내 이후 전부가 사라진다.

### 알아둘 것 (버그로 오인하기 쉬움)

다운스크롤에서 `DrawableManiaHitObject.OnDirectionChanged`가 `Origin = BottomCentre`를 주므로 **단노트/LN헤드는 스프라이트의 아랫변**이 판정 지점이다. 반면 `DrawableHoldNoteTail`은 생성자에서 스크롤 방향과 무관하게 `Origin = TopCentre`로 고정이라 **LN 테일은 스프라이트의 윗변**이 판정 지점이다. 즉 선이 노트 중앙이 아니라 모서리에, 그것도 헤드와 테일이 서로 반대 모서리에 붙는 것이 정상 동작이다.

### 미검증

SV가 심한 맵 / 업스크롤 / 커스텀 `HitPosition` 스킨 / HD·FI·CO(`ManiaModWithPlayfieldCover`가 `HitObjectContainer`를 리페어런트하지만 우리 오버레이는 그 커버 밖이라 가려지지 않는다) / 노트 수천 개에서의 성능.

### 입력 사각형 (`ManiaPressOverlay`, 2026-07-29 추가)

리플레이에 기록된 키 입력을 누른 시각~뗀 시각 그대로의 직사각형으로 그린다. 폭은 컬럼의 90%, 노트 판정을 일으킨 입력은 노란색, 아무 노트에도 걸리지 않은 입력은 밝은 회색이다.

- **`column.UnderlayElements`(노트 뒤)에 붙인다.** 판정선은 `HitObjectArea`(노트 앞)다. 사각형이 노트를 가리지 않으면서 흰 선은 항상 읽히는 배치.
- **mania 리플레이 프레임은 이벤트가 아니라 스냅샷**이다 — `ManiaReplayFrame.Actions`는 "그 시점에 눌려 있는 키 집합"이라 연속한 두 프레임을 비교해야 누름/뗌이 복원된다.
- 윈도잉은 판정 기준선과 동일(끝 시각 정렬 + 이분 탐색 + 미래 방향 부호로 중단). 같은 컬럼의 키는 겹쳐 눌릴 수 없어 끝 시각이 항상 오름차순이다.

**노랑/회색 분류는 근사치다.** 정확한 답은 osu!가 재생 중 실제로 발행하는 `JudgementResult`뿐인데, 그건 판정선을 지나야 알 수 있어서 **아직 내려오는 중인 사각형의 색을 미리 정할 수 없다.** 그래서 `ManiaPressExtractor`가 miss 판정창 안에서 시간순 그리디 매칭으로 미리 계산한다. 기준을 meh가 아니라 **miss 판정창**으로 잡은 이유는 `DrawableNote.CheckForResult`가 `ResultFor`가 `None`일 때만 아무 일도 하지 않기 때문 — 미스 판정도 "판정이 발생한" 것이다. 노트락(`OrderedHitPolicy`)과 롱노트 릴리즈의 노트 소비는 재현하지 않는다.

**관전에서는 사각형이 나오지 않는다.** `SpectatorPlayer`는 리플레이 프레임을 실시간 스트리밍으로 받으므로 부착 시점에 프레임이 비어 있다. 판정 기준선은 비트맵만 쓰므로 관전에서도 정상 동작한다.

### 판정 시뮬레이션 (`ManiaJudgementSimulation`, 2026-07-29 추가)

노랑/회색 분류와 오차 숫자(ms)는 **osu!가 실제로 발행한 `JudgementResult`** 그대로다. 자체 판정 알고리즘은 없다.

| 파일 | 역할 |
|---|---|
| `Patches/ManiaReplaySimulationPatch.cs` | `PlayerLoader.OnPlayerLoaded` Postfix. 로딩 화면에 시뮬레이션 드로어블을 붙인다 |
| `Drawables/ManiaJudgementSimulation.cs` | 화면에 안 보이는 두 번째 mania 룰셋. 리플레이를 먹이고 시계를 빨리 감아 판정을 수집 |
| `Data/ManiaSimulationState.cs` | 결과 단일 슬롯 (`Score` 참조로 소유 확인, `Ready` 플래그) |

**왜 이렇게까지 하는가**: 판정값은 재생 헤드가 노트를 지나야 생기는데, 입력 사각형은 그보다 훨씬 전부터 화면에 내려온다. 그래서 미리 알아야 하고, 자체 계산으로는 **짝짓기(어느 입력이 어느 노트의 판정인가, 특히 노트락)** 를 재현할 수 없다. osu! 자신에게 한 번 미리 돌려보게 하는 것이 유일하게 정확한 방법이다.

**선례**: osu! 본체의 `osu.Game\Tests\Visual\ReplayStabilityTestScene.cs`(테스트 프로젝트가 아니라 **`osu.Game` 어셈블리에 포함**된다)가 `ReplayPlayer`를 띄우고 `ScoreProcessor.NewJudgement`를 모은다. mania 노트락 테스트 20여 개가 전부 이 방식이다. 우리는 스크린을 띄울 수 없으므로 `DrawableRuleset`만 따로 세운다.

#### 반드시 지켜야 하는 것

- **로딩 화면(`PlayerLoader`)에 붙이고, 끝날 때까지 로딩을 붙잡는다.** 편의가 아니라 **구조상 필수**다 — 로더가 서스펜드되면(= 게임플레이 화면으로 전환되면) 시뮬레이션 드로어블의 업데이트가 끊겨 **그 자리에서 멈춘다**(2026-07-29 로그로 확인). 따라서 로더가 현재 화면인 동안 완주해야 한다. 게이트는 `ManiaSimulationGatePatch`가 `PlayerLoader.ReadyForGameplay`(osu!가 이미 갖고 있는 push 게이트, `:136`/`:683`)에 조건을 하나 더해서 건다.
  - ⚠️ **초기 설계 문서에 "서스펜드된 스크린도 계속 업데이트되므로 게임플레이 중에도 이어서 돈다"고 적혀 있었는데 틀렸다.** 검증하지 않은 가정이었고, 이것이 시뮬레이션이 전혀 동작하지 않던 두 번째 원인이다.
  - **게이트 해제 조건은 "시간"이 아니라 "진척 여부"다.** 총 소요 시간 한도(초기엔 15초→30초)는 정상 동작을 잘라먹었다 — 13.5분 맵이 95.4%에서 잘렸다(2026-07-29). 정상 소요 시간이 맵 길이와 기기 성능에 따라 크게 달라 시계 기반 한도는 애초에 맞출 수가 없다. 지금은 `ManiaSimulationResult.Alive`가 **10초간 진척 없음**을 감지할 때만 게이트를 푼다. 진행 중이면 무한정 기다리고, 시뮬레이션이 죽은 경우에만 빠져나온다. 그 경우 사각형은 회색·숫자 없음으로 degrade될 뿐 리플레이 감상은 막히지 않는다.
  - 대기 중 진행률은 `ManiaSimulationProgressDisplay`가 로딩 화면 하단에 막대로 보여준다.
  - 실패한 결과를 `ManiaSimulationState.Current`에 올리면 **아무도 채우지 않을 값을 게이트가 기다린다.** 반드시 드로어블 부착에 성공한 뒤에 슬롯에 올린다.
- **`Clock`을 직접 쥔다.** `FrameStabilityContainer`는 DI의 `IGameplayClock`을 기준으로 삼는데, Player 안에 붙이면 본 재생을 그대로 따라가 버린다. Player **바깥**(로딩 화면)이라 DI에 `IGameplayClock`이 없고 우리 `FramedClock(ManualClock)`이 기준이 된다.
- **`ISamplePlaybackDisabler`를 캐시해 무음으로 만든다.** 안 하면 리플레이 전체 타격음이 1~2초 안에 몰아서 재생된다 (`PausableSkinnableSound.Play()`가 이 값이 true면 즉시 리턴).
- **비트맵과 모드를 복제한다.** `GetPlayableBeatmap`으로 우리 몫의 변환본을 따로 만들고 `Mod.DeepClone()`을 쓴다. 히트오브젝트/모드 인스턴스를 본 게임과 공유하면 상태가 섞인다.
- **룰셋은 비트맵이 아니라 `GameplayState.Ruleset`에서 가져온다.** 변환보면(osu! → mania)에서는 `BeatmapInfo.Ruleset`이 원본 룰셋이라 엉뚱한 룰셋이 선다.
- **`SetReplayScore()`는 룰셋을 로드한 뒤에 부른다.** 내부에서 `frameStabilityContainer.ReplayInputHandler`를 세우는데(`DrawableRuleset.cs:315`) 그 컨테이너는 BDL `load()`에서 생성된다(`:177`). 로드 전에 부르면 NRE가 나고 **드로어블이 통째로 로드 실패해 결과가 영원히 준비되지 않는다**(2026-07-29에 실제로 겪음). `LoadComponent(ruleset)` → `SetReplayScore` → `AddInternal` 순서. osu! 본체가 `PrepareReplay()`를 `Player.load()`가 아니라 `LoadComplete()`에서 부르는 이유도 이것.
- **어떤 경로로 실패하든 `Ready`는 반드시 세운다.** 안 세우면 소비 측이 영구히 기다리며 원인 없는 회색 화면이 된다. 셋업 전체를 try/catch로 감싸고, 시각이 안 움직이면 포기하는 stall 가드도 둔다.
- **비프레임안정 고속 시크는 금지.** `Player.SetGameplayStartTime` 주석대로 중간 판정이 올바르게 적용/되돌려지지 않는다. 빨리 감기는 `UpdateSubTree()`를 프레임당 예산(8ms)만큼 반복 호출하는 방식이다 — 매 호출마다 `FrameStabilityContainer`가 정상 60Hz 스텝을 소화한다.

#### 판정 ↔ 입력 짝짓기

`JudgementResult.TimeAbsolute`는 판정이 일어난 프레임의 시각인데, 리플레이 재생에서는 프레임 안정 시계가 리플레이 프레임 시각으로 스텝하므로 **입력이 원인인 판정의 `TimeAbsolute`는 곧 그 입력의 누름(또는 뗌) 시각**이다. 그래서 시각(±1ms)으로 짝을 짓는다.

같은 순간에 판정이 여러 개 몰릴 수 있다 — 노트락(`OrderedHitPolicy.HandleHit`)이 앞 노트들을 강제 미스시키기 때문. 그 입력이 실제로 겨냥한 노트는 **그중 가장 늦은 노트**이므로 `NoteTime`이 최대인 것을 채택한다.

#### 실패 시 동작

시뮬레이션이 실패하거나 아직 안 끝났으면 사각형은 전부 회색이고 숫자가 없다. 게임플레이 자체에는 영향이 없다.

### 표시 토글 (`ManiaReplayDisplaySettings`, 2026-07-29 추가)

리플레이 설정 창(오른쪽 패널)에 **"판정 표시"** 그룹을 추가해 개별로 켜고 끈다 — 노트 판정선 / 판정 기준선 / 입력 구간 / 판정 오차 / **노트 숨기기**.

- `ReplayPlayer.AddSettings(PlayerSettingsGroup)`가 osu! 본체에서 public이다 (`ReplayPlayer.cs:88`). osu!std의 `ReplayAnalysisSettings`가 커서 분석 토글을 넣는 것과 같은 경로.
- 상태는 `ManiaOverlayVisibility`의 static `BindableBool` 5개. **전부 기본값 꺼짐, 세션 한정이고 config에 저장하지 않는다.**
- **오버레이는 static bindable을 직접 구독하지 않는다.** 자체 `BindableBool` 필드에 `BindTo`한다 — static 이벤트에 델리게이트가 직접 남으면 오버레이가 GC되지 않는다(`ui-patching.md` 함정 표와 같은 이유).
- 관전에는 리플레이 설정 창 자체가 없으므로 `ReplayPlayer`일 때만 붙인다.
- **노트 숨기기(`ManiaNoteHider`)는 `Alpha`가 아니라 `Colour`를 투명하게 만든다.** `Alpha = 0`은 `IsPresent`를 false로 만드는데, 우리 오버레이들이 바로 그 `HitObjectContainer`의 `PositionAtTime()`에 위치 계산을 의존하므로 업데이트가 끊기면 표시가 통째로 얼어붙을 수 있다(로더 서스펜드로 시뮬레이션이 멈췄던 것과 같은 메커니즘). `Colour`는 `IsPresent` 판정에 들어가지 않는다. 원래 색은 기억해 뒀다가 되돌린다.

---

## 10. 키뷰어 위젯 (2026-07-29 신규)

하단 키 박스 + 그 위로 흘러가는 흰 막대. 리플레이·관전·실제 플레이 전부에서 동작한다.

| 파일 | 역할 |
|---|---|
| `Widgets/KeyViewerWidget.cs` | `KeyCounterDisplay` 파생. 키 목록 구성 + 설정 4개 + 단축키 이름 조회 |
| `Widgets/KeyViewerKey.cs` | 컬럼 하나 — 키 박스 + 막대 영역 + 막대 수명 관리 |

**osu! 본체 재사용이 거의 전부다.** `KeyCounterDisplay`는 이미 `ISerialisableDrawable`이고, `RulesetInputManager.Attach()`가 `DefaultKeyBindings`의 distinct 액션마다 `KeyCounterActionTrigger`를 만들어 준다. 그래서:

- **키 개수가 자동으로 맞는다** (4K → 4개)
- **리플레이·관전·실플레이를 구분할 필요가 없다** — 리플레이 입력도 `RulesetInputManager.HandleInputStateChange`가 `KeyBindingContainer.TriggerPressed`로 돌려주므로 같은 이벤트가 온다
- 되감기 판별도 `Activate(bool forwardPlayback)`으로 이미 넘어온다

### 반드시 알아야 하는 것

- **키 박스에는 배치 순서(1..n)를 적는다.** `InputTrigger.Name`은 `"B1"` 같은 액션 번호라 쓸 수 없고, 실제 바인딩 키를 얻으려면 `KeyBindingContainer.KeyBindings` + `ReadableKeyCombinationProvider`를 거쳐야 하는데 **HUD는 `ManiaInputManager`의 자식이 아니라 `[Resolved]`가 안 된다**(리플렉션이 필요하다). 번호로 충분하다고 판단해 그 경로를 통째로 걷어냈다. 번호는 `KeyFlow.Count + 1`로 매긴다 — `KeyCounterDisplay`가 `KeyFlow.Add(CreateCounter(trigger))` 형태로 부르므로 호출 시점의 Count가 곧 이미 들어간 개수다.
- **색은 `BindableColour4` + `[SettingSource]` 두 개**로 노출한다 (osu! 본체 `BoxElement`와 같은 방식이라 색상 팔레트 UI가 자동으로 붙는다). 테두리/번호 색과 눌림/막대 색이 분리돼 있다. 두 색을 같게 두면 눌린 동안 번호가 안 보인다.
- **시크·되감기는 흐르던 막대를 전부 무의미하게 만든다.** 프레임 간 경과가 음수이거나 1초를 넘으면 시크로 보고 전부 지우고 그 지점부터 다시 시작한다. **배속은 여기 안 걸린다** — 프레임당 경과가 배속만큼 늘어날 뿐이다.
- 누르는 중인 막대는 **끝 시각을 "지금"으로 잡는다**. 그러면 아래 면이 키 박스에 붙어 있고 위 면만 자라는 동작이 완성된 막대와 같은 수식 하나로 처리된다.
- 박스가 흰색으로 차면 흰 글자가 안 보이므로 눌린 동안 글자를 검게 바꾼다.
- `KeyCounterDisplay`는 리플레이일 때만 강제 표시하고 그 외엔 `OsuSetting.KeyOverlay`를 따른다. 스킨에 직접 배치한 위젯이므로 `AlwaysVisible`을 풀어 **항상 표시**로 바꾼다.
- **죽은 클릭(노트 판정을 일으키지 않은 입력) 판별은 시각이 아니라 컬럼별 판정 카운터로 한다.** 처음엔 판정 시각과 입력 시각을 ±30ms로 대조했는데 **전부 죽은 것으로 나왔다**: 입력 시각은 HUD의 게임플레이 시계로, `JudgementResult.TimeAbsolute`는 룰셋의 **프레임 안정 시계**로 재어진다(`Player.cs:357`가 `IGameplayClock`으로 `FrameStableClock`을 따로 캐시할 만큼 다른 시계다). 지금은 입력 시작 시 그 컬럼의 누적 판정 수를 스냅샷하고 2프레임 뒤 늘었는지만 본다 — 시계에 전혀 의존하지 않는다.
- **`ScoreProcessor`는 `GameplayState`를 통해 잡는다.** 직접 `[Resolved]`하지 않는다 — 이 프로젝트의 다른 위젯들이 검증한 경로가 `GameplayState.ScoreProcessor`다.
- **방향 전환은 앵커만 뒤집는다.** 막대의 거리 계산을 항상 0 이상으로 두고 부호는 앵커가 정하게 하면, 위/아래 두 경우를 같은 수식으로 처리할 수 있다.
- 각 컬럼은 **양축 모두 고정 크기**다. `KeyCounterDisplay`가 `AutoSizeAxes.Both` + `FillFlowContainer` 구조라, 편측 고정폭 자식을 넣으면 `ui-patching.md`의 즉사 패턴에 걸린다.

---

## 11. `BoxElementPlus` (2026-07-29 신규)

osu! 기본 `BoxElement`에 상황별 자동 숨김 두 가지를 더한 위젯.

| 설정 | 동작 |
|---|---|
| 일시정지 대응 | 일시정지 중과 **재개 카운트다운 동안** 완전 투명 |
| SV 대응 | 채보의 **SV(초록선)가 기준값 미만**인 구간에서 완전 투명 |
| SV 기준값 | 위 판정에 쓸 SV 기준. 0.05~2.0, 기본 0.5 |

### 반드시 알아야 하는 것

- **기존 `BoxElement`에 설정을 끼워 넣는 것은 불가능하다.** HarmonyX는 메서드 본체를 바꿀 뿐 **타입에 프로퍼티를 추가하지 못한다.** 그래서 상속으로 `[SettingSource]`를 덧붙인 별도 위젯을 만들었다. 기존 스킨에 배치된 osu! 원본 `BoxElement`에는 이 설정이 없다.
- **숨김은 `Alpha`가 아니라 `Colour`의 투명도로 한다.** `Alpha = 0`은 `IsPresent`를 false로 만들어 **스킨 에디터에서 선택조차 못 하게 된다**.
- **재개 카운트다운은 따로 처리할 필요가 없다.** `Player.Resume()`이 `DelayedResumeOverlay` 완료 뒤에야 시계를 시작하므로, 카운트다운 내내 `IGameplayClock.IsPaused`가 true다. 검사 하나로 둘 다 덮인다.
- **BPM 변화는 일부러 보지 않는다.** `EffectPointAt(t).ScrollSpeed`(초록선 SV)만 본다. mania는 `RelativeScaleBeatLengths => true`라 BPM 변화도 실제 화면 스크롤에는 영향을 주지만, 이 설정이 보려는 것은 **"맵이 지정한 스크롤 속도"**이므로 의도적으로 제외했다 (사용자 지시, 2026-07-29).

---

## 12. 결과창 판정 산점도 (mania, 2026-07-29 신규)

결과창에서 스페이스바로 여는 확장 통계 패널(`StatisticsPanel`)에 항목을 하나 추가한다. 가로축은 시간(첫 노트~마지막 노트), 세로축은 판정 오차(위=얼리, 아래=레이트), 점 하나가 노트 하나다.

| 파일 | 역할 |
|---|---|
| `Patches/ResultsJudgementScatterPatch.cs` | `StatisticsPanel.CreateStatisticItems` Postfix. mania일 때만 항목 삽입 |
| `Drawables/ManiaJudgementScatterGraph.cs` | 산점도 본체 — 축/판정창 경계선/토글 버튼/점 렌더링 |

### 삽입 지점을 `StatisticsPanel`로 잡은 이유 (중요)

`ManiaRuleset.CreateStatisticsForScore`가 mania 전용이고 배열을 반환해 더 깔끔해 보이지만 **패치할 수 없다.** `Patcher`가 `osu.Game` 어셈블리 로드 즉시 `PatchAll`을 돌리는데(§3), 그 시점에 `osu.Game.Rulesets.Mania`는 아직 로드되지 않았다 → `TargetMethod()`가 null → `Prepare()` false → **영구 스킵**(재시도 경로가 없다). 룰셋 어셈블리를 타겟으로 잡으려면 두 번째 지연 패치 패스가 필요하다.

`StatisticsPanel`은 `osu.Game` 소속이고 `ResultsScreen`이 **직접 `new`** 하는 단일 클래스(서브클래스 없음)라, 솔로/리플레이/멀티/관전 결과창 전부를 하나로 덮는다. mania 한정은 `newScore.Ruleset.OnlineID != 3` 가드로 처리한다.

### 알아야 할 것

- **`CreateStatisticItems`는 이터레이터**다. `__result`를 `List`로 실체화한 뒤 넣는다 — 패치 클래스 안에서 `yield return`을 쓰면 어셈블리 전체 패치 등록이 죽는다(`ui-patching.md` 함정 표). 호출자가 곧바로 `.ToArray()`로 소비하므로 실체화해도 동작이 같다.
- 삽입 위치는 인덱스가 아니라 **`"Timing Distribution"` 항목을 이름으로 찾아 그 뒤**다.
- `StatisticItem`을 `requiresHitEvents: true`로 만들면 **리플레이 미재생 스코어에서 osu!가 알아서** 항목을 빼고 "리플레이를 봐야 통계가 나온다" 안내로 대체한다. 우리 쪽 처리가 필요 없다.
- `StatisticItem.CreateContent`는 지연 팩토리이고 패널이 `LoadComponentAsync`로 로드하므로 **무거운 준비 작업은 BDL에 둔다**. 생성자는 업데이트 스레드에서 돈다.
- Postfix 인자로 `playableBeatmap`(모드까지 적용된 변환 완료 보면)과 `newScore.HitEvents` 전량이 그냥 들어온다 — `GetPlayableBeatmap`을 다시 돌릴 필요가 없다.

### 표시 규칙

- **미스는 제외한다.** `JudgementResult.TimeOffset`이 미스 판정창 값으로 클램프되므로 실제 오차가 아니라 상수이고, 그리면 가장자리 직선 하나가 될 뿐이다(osu! 자신의 타이밍 분포 그래프도 같은 이유로 `IsHit()`으로 거른다). `HoldNote` 본체/바디는 `HitWindows`가 Empty라 같은 필터에서 함께 걸러진다.
- **세로축은 OD에서 자동 계산된 미스 판정창 ±로 고정**이다(실측 최대치 아님). 판정창은 실제 판정된 히트오브젝트의 `HitWindows`에서 읽으므로 EZ/HR의 판정창 배율(`DifficultyMultiplier`)까지 자동 반영된다.
- **오차와 판정창을 둘 다 `GameplayRate`로 나눈다** — §14 참고.
- 점 색은 `OsuColour.ForHitResult` 그대로. 바로 위 히스토그램과 같은 색 체계라 범례를 두지 않았다.
- 하단 토글 3종(단노트/롱헤드/롱테일). 분류는 **`TailNote` → `HeadNote` → `Note` 순서로 검사**한다 — 셋 다 `Note`를 상속하므로 순서가 곧 분류 규칙이고, `Note`를 먼저 보면 전부 단노트로 뭉친다.
- 버튼은 `OsuClickableContainer` + `Box` + 라벨로 직접 만들었다. `RoundedButton`은 `TrianglesV2`/`OverlayColourProvider` 의존이 있고 `OsuButton`은 abstract이다.

### 렌더링 — 쿼드 배치 크기는 점 개수와 분리한다

점 하나당 Drawable을 만들지 않고 `StrainAreaGraph`와 같은 커스텀 `DrawNode` + 쿼드 배치로 전부 그린다. **여기서 반드시 지킬 것**:

- `renderer.CreateQuadBatch<T>(size, maxBuffers)`의 `size`는 정점 수가 아니라 **쿼드 수**다. `IRenderer.MAX_QUADS = 10922`가 하드 상한이고 넘으면 생성자가 `OverflowException`을 던진다.
- **배치 크기는 점 개수와 무관한 고정값**(현재 2,048)으로 잡는다. `VertexBatch.Add`는 버퍼가 차면 스스로 플러시하고 이어서 담으므로 점이 몇 개든 전부 그려지고 드로우 콜만 나뉜다. 점 개수에 맞춰 잡으면 상한을 넘는 순간 죽는다.
- **`Draw`에서 예외가 나가면 게임이 죽는다.** try/catch로 감싸고 한 번 실패하면 재시도하지 않는다 — 실패 상태가 복구되지 않으면 매 프레임 재시도가 곧 예외 폭풍이 된다(실제로 겪은 사고, `ui-patching.md` 함정 표).

---

## 13. 난이도 아이콘 툴팁의 sunnySR 재계산 (2026-07-29)

`DifficultyIconTooltip`은 `DifficultyIcon`당 인스턴스 하나를 **재사용**하고, 호버 대상이 바뀌면 `SetContent`가 다시 불린다. pill 삽입은 인스턴스당 1회(`ConditionalWeakTable`)지만 **계산은 대상이 바뀔 때마다** 돌아야 한다.

- **`SetContent`는 매 프레임 호출된다.** osu.Framework의 `TooltipContainer`가 툴팁이 떠 있는 동안 내용을 계속 다시 밀어 넣고, `DifficultyIcon.TooltipContent`는 접근할 때마다 **새 객체**를 반환하는데 그 타입은 `Equals`를 재정의하지 않는다(참조 비교라 항상 "바뀜"으로 판정). 그래서 **대상 식별 키**(`MD5 | OnlineID | 룰셋ID | 모드+설정값`)를 두고 키가 실제로 바뀔 때만 재계산한다. 이 가드가 없으면 프레임마다 취소·리셋이 반복되어 계산이 영원히 끝나지 않는다.
- 모드 설정값을 키에 넣는 이유는 rate-adjust처럼 **아크로님이 같고 배속만 다른** 경우가 있기 때문이다.
- 큐 항목의 비트맵은 온라인 비트맵(`APIBeatmap`)이라 로컬 사본을 조회해야 한다. **MD5 우선 → OnlineID 폴백** — osu!가 플레이리스트 항목의 "다운로드됨" 판정에 쓰는 순서와 같다. 못 찾으면 계산 없이 기본값(0).
- `BeatmapManager`는 툴팁의 `Dependencies`에서 얻되 **최초 성공 참조를 static 캐시**한다. 첫 `SetContent` 시점에는 아직 로드 전일 수 있다.
- mania가 아니면 pill을 숨긴다(`Alpha = 0`). 이 툴팁은 모든 룰셋에서 쓰인다.

---

## 14. `JudgementResult.TimeOffset`과 배속 (2026-07-29)

**`TimeOffset`은 비트맵 시간 기준이라 배속에 비례해 커진다.** 반면 mania의 판정창은 같은 배속만큼 **함께 늘어난다**(`ManiaHitWindows.SpeedMultiplier`, 소스 주석에 "판정창을 트랙 속도와 무관하게 유지"라고 명시).

따라서 **원본 `TimeOffset`을 고정 판정창에 대입하면 배속만큼 값이 어긋난다.** osu! 본체도 UR 계산에서 `e.TimeOffset / e.GameplayRate`로 같은 보정을 한다(`HitEventExtensions.cs`, 주석: "Division by gameplay rate is to account for TimeOffset scaling with gameplay rate").

- `ClassicAccuracyWidget`이 이 버그로 DT/HT에서 정확도가 배율만큼 틀렸고, 2026-07-29에 `TimeOffset / |GameplayRate|`로 수정했다. 되감기 중 `GameplayRate`가 음수일 수 있어 절댓값을 쓴다.
- 판정 산점도(§12)도 오차와 판정창을 **둘 다** 나눠 실시간 ms 기준으로 표시한다.
- `GameplayRate`와 `SpeedMultiplier` 모두 리플레이의 **사용자 재생 배속(`UserPlaybackRate`)은 포함하지 않는다** — 게임 자신의 판정과 같은 기준이다.
- **미해결**: mania EZ/HR은 OD가 아니라 판정창 폭에 직접 1.4배/÷1.4를 건다(`DifficultyMultiplier`). `ClassicAccuracyWidget`은 비트맵 원본 OD만 쓰므로 EZ/HR에서는 여전히 어긋난다. 산점도는 히트오브젝트의 실제 `HitWindows`를 읽으므로 영향이 없다.

---

## 15. 결과창 구간 선택 / 구간 연습 (2026-08-08 신규)

산점도(§12) 안에서 드래그로 구간을 잡고, 그 구간의 정확도·sunnySR을 보고, 그 구간만 다시 플레이한다.

| 파일 | 역할 |
|---|---|
| `Drawables/ManiaJudgementScatterGraph.cs` | 드래그 입력·선택선·상세 표시·컬럼 필터·연습 버튼 (산점도 본체에 통합) |
| `Calculators/ManiaSectionAnalysis.cs` | 구간 정확도 3종 / 구간 판정 추출 / 구간 임시 `ManiaBeatmap` 조립 |
| `Screens/SectionPracticePlayer.cs` | 구간 연습 게임플레이 화면 + `SectionPracticePlayerLoader` |
| `Screens/SectionPracticeBeatmap.cs` | 원본에서 구간 밖 노트만 걷어낸 `WorkingBeatmap` 사본 |
| `Screens/ILocalOnlyPlayerLoader.cs` | 리더보드 스킵 판별용 마커 |

### 표시

- **정확도 3종**은 320 판정의 가중치와 분모만 다르다 — 레거시 300 / lazer 305 / pp 320. 나머지(300·200·100·50·0)와 집계는 동일하다.
- **정확도용 판정 목록은 점 목록과 별개로 보관한다.** 산점도는 미스를 버리지만(§12) 정확도는 미스가 분모에 들어가야 한다.
- **구간 sunnySR은 raw만 구간에서 뽑고 짧은 맵 보정의 노트 수는 전체 맵 값을 대입한다.** `SunnyManiaDifficultyCalculator.Calculate`의 `weightedNoteCountOverride`(기본 null이라 기존 호출부 무영향)와 `CalculateWeightedNoteCount`가 그 통로다.
- **컬럼 필터는 정확도까지만 따라간다.** sunnySR과 구간 연습은 전 컬럼 기준이다 — mania 난이도는 컬럼 간 상호작용이 본질이라 일부 컬럼만 남긴 값은 원본과 아무 관계가 없다.
- 입력 레이어는 `Alpha=1`이어야 한다(0이면 `IsPresent`가 false라 입력이 안 온다). 드래그 없는 클릭은 `OnClick`으로 받아 해제한다 — 드래그가 있었으면 발화하지 않으므로 `MouseUp` 순서에 의존하지 않는다.

### 구간 연습 — 맵을 자르는 방식인 이유

**원본 전체를 플레이하며 시작 지점만 옮기는 방식(osu! `EditorPlayer`와 동일)을 먼저 시도했다가 폐기했다.** 그 경로에서는 구간 이전 노트를 미리 판정으로 채우고(리플레이 판정 매칭) 강제 미스를 막아야 하는데, 후자에 필요한 osu! 멤버 3개가 전부 internal이라 리플렉션이 필요했다. **노트를 실제로 걷어내니 그 보정이 통째로 사라졌다** — osu!가 보기엔 그냥 짧은 맵이라 완료 판정·점수·정확도가 평소 경로로 맞는다.

`WorkingBeatmap` 래퍼가 가능한 이유:

- `Beatmap`(raw 보면) / `GetBackground` / `GetStream` 은 **public**
- **`TryTransferTrack`(public virtual)** 이 `BeatmapInfo`를 공유하면 로드된 트랙 인스턴스를 그대로 넘겨준다 (구현이 `target.track = Track` 한 줄) — 오디오를 다시 로드하지 않는다
- 리플렉션이 필요한 추상 멤버는 `GetBeatmapTrack` 하나뿐
- `IBeatmap.Clone()`은 `MemberwiseClone`이라 **`HitObjects` 리스트가 공유된다. 반드시 새 리스트로 갈아끼워야** 원본이 오염되지 않는다
- 다른 어셈블리에서 `protected internal` 멤버(`GetSkin`)를 재정의할 때는 **`protected`로 선언**해야 한다

### 반드시 알아야 하는 것 (전부 실기에서 걸렸다)

- **`CreateGameplayClockContainer` 안에서 `Reset()`을 부르면 안 된다.** 그 시점 컨테이너는 아직 트리에 붙기 전이라 seek이 반영되지 않고, 첫 프레임의 `CurrentTime`이 **직전 화면에서 재생 중이던 곡 위치**로 읽힌다(구간 연습이 "시작하자마자 종료"되던 원인). 클럭이 완전히 준비된 뒤인 **`StartGameplay()` override**에서 한다. `EditorPlayer`가 같은 자리에서 같은 호출을 하고도 멀쩡한 건 에디터에서는 트랙이 정지 상태이기 때문이다.
- **결과창으로 넘어간 직전 `Player`는 아직 살아 있다.** `progressToResults`가 `Push`를 쓰므로 그 화면은 종료가 아니라 **중단**되고, 트랙 조정을 떼는 `StopUsingBeatmapClock()`은 `Player.OnExiting`에만 있어 실행되지 않는다. DT 플레이 뒤 결과창 배경음악이 빠른 것이 그 상태다. **그 위에 두 번째 게임플레이 클럭을 세우면 같은 트랙에 배속이 곱해진다**(1.5 → 2.25). `MasterGameplayClockContainer`를 상속해 `StartGameplayClock()`에서 tempo/frequency를 초기화한 뒤 `base`를 부르는 것으로 해결했다(`SectionPracticeClockContainer`). 볼륨·밸런스는 건드리지 않는다 — 오디오 더킹이 쓴다.
  - `MusicController.ResetTrackAdjustments()`로는 못 뗀다. 그쪽은 자기 `CurrentTrack` 래퍼의 조정만 건드리고 게임플레이 클럭은 **raw 트랙에 직접** 물린다(해당 메서드 주석이 명시).
- **`OsuScreenStack`은 새 화면의 의존성을 "직전 화면의 의존성"을 부모로 만든다**(`CreateLeasedDependencies`). 결과창은 `DisallowExternalBeatmapRulesetChanges = true`라 Beatmap/Ruleset/Mods를 이미 lease 중이므로, 그 위에 얹히는 로더는 **새 lease를 뜨지 않고 사본만 받는다.** 이중 lease 예외는 안 나지만 **`ReplayPlayerLoader`가 기대는 "lease가 알아서 원복해준다"가 성립하지 않는다** — 셋 다 `OnExiting`에서 직접 되돌려야 한다.
- **배경음악은 진입 시 직접 꺼야 한다.** 로더 `OnEntering`에서 `base.OnEntering(e)` **뒤에** `musicController.Stop()`(그 시점이면 `PlayerLoader.AllowGlobalTrackControl => false`가 적용돼 다시 틀리지 않는다), `OnExiting`에서 `EnsurePlayingSomething()`. **`requestedByUser`는 반드시 false** — true면 `UserPauseRequested`가 서서 그 뒤로 음악이 영영 돌아오지 않는다.
- **효과음은 끌 수 없다.** osu!에 재생 중인 샘플을 일괄로 멈추는 API가 없다. `ISamplePlaybackDisabler`는 게임플레이 트리 안의 `PausableSkinnableSound`에만 닿는다.
- 시작 지점은 `구간시작 − 1500ms × 배속`이다 — 리드인이 **체감 기준**이라 맵 시간으로는 배속을 곱한다.
- 구간 끝은 **마지막 노트의 `GetEndTime()`** 이라 경계에 걸친 롱노트를 끝까지 잡을 수 있다. 종료는 `ScoreProcessor.HasCompleted` → 1초 뒤 `Exit()`.
- 재시도는 로더의 `createPlayer` 팩토리가 같은 구간으로 다시 만들므로 **구간이 유지된다**(단축키 포함).

### 알려진 동작 차이

구간 연습에서 나온 뒤 결과창 배경음악이 **정배속**이 된다(바닐라는 모드 배속을 유지한다). 위 배속 수정이 직전 Player의 조정까지 걷어내기 때문이며, 무해하다고 판단해 그대로 뒀다.

---

## 16. 개인 저장소 (`%LocalAppData%\LazerSR\`, 2026-08-18 신규)

세션 간 유지가 필요한 데이터의 공용 보관소. 첫 소비처는 배경음 목록이다.

| 파일 | 역할 |
|---|---|
| `LazerSrStorage.cs` | 저장 루트 결정 + 폴더 보장 + **원자적 쓰기**(임시 파일 → `File.Move` 교체) + 안전한 읽기 |
| `Training/TrainingMusicStore.cs` | 배경음 목록 (`music\songs.json`, 스키마 버전 2) |

- **위치가 `%LocalAppData%\LazerSR\`인 이유**: 설치 경로는 관리자 권한이 필요하고 재설치 때 덮인다. Roaming이 아니라 Local인 이유는 이 데이터가 그 PC의 비트맵 라이브러리에 종속되기 때문이다. 런처 설정(`%AppData%\LazerSR\settings.json`)은 지금 위치를 유지한다.
- **네임스페이스를 `LazerSR.Hook` 루트에 둔다.** `LazerSR.Hook.Storage`로 두면 osu.Framework의 `Storage` **타입**과 이름이 충돌해, 같은 어셈블리의 다른 파일에서 `Storage`가 네임스페이스로 먼저 해석되며 컴파일이 깨진다(실제로 겪었다).
- **저장 실패는 절대 기능을 막지 않는다.** 읽기는 `null`, 쓰기는 `false`를 돌려줄 뿐 예외를 밖으로 내보내지 않는다. 스키마 버전이 다르면 조용히 빈 상태에서 시작한다 — 재생성 가능한 데이터라 마이그레이션하지 않는다.
- 공용화는 **경로·입출력까지만**이다. 무엇을 어떤 형식으로 담을지는 각 기능이 소유한다(범용 key-value 추상화를 만들지 않는다).

### 위젯이 선곡 화면에서 다른 역할을 한다

`TrainingStatusWidget`은 **인게임에서는 트레이닝 상태 표시, 선곡 화면에서는 곡 등록 UI**로 동작한다.

- osu!에 **`GlobalSkinnableContainers.SongSelect` 레이어가 실재**한다(`SongSelect.cs:306`이 직접 `SkinnableContainer`를 만든다). `SerialisedDrawableInfo.GetAllAvailableDrawables`는 컨테이너별로 후보를 거르지 않으므로 **등록 코드를 바꿀 필요가 없다** — 이미 두 레이어의 툴박스에 다 나온다.
- **모드 구분은 `GameplayState`가 캐시돼 있는지 하나로** 한다. 선곡 화면에는 없고 게임플레이(및 HUD 스킨 에디터)에는 있다.
- 선곡 화면에서는 캐러셀 선택이 곧 `Beatmap.Value`이므로 `IBindable<WorkingBeatmap>`만으로 지금 커서가 놓인 난이도를 안다. 오디오 길이는 미리듣기 트랙이 로드된 뒤에야 읽히므로 **길이가 생기면 그때 다시 검사**한다(`TrackLoaded` 확인 후 `Track.Length`).
- **`OverlayColourProvider`는 선곡 화면과 대기 화면에는 있지만 게임플레이 HUD에는 없다.** 두 곳에서 같이 로드되는 부품(`StepButton`)은 `[BackgroundDependencyLoader(true)]` + 폴백 색으로 없어도 죽지 않게 한다.

---

## 17. sunny+ 개인화 diff (2026-08-19 신규)

sunny 상수 39개 중 11개(`Tuning/PersonalBox.Tuned`)를 한 사람의 실제 정확도에 맞춰 미는 것. 계산식은 안 건드리고 상수만 이동한다 — 전체 설계 근거는 `OsuScoreModel/temp/personal/handover_lazersr.md` 참고.

```
최종 상수 = 스톡 sunny + 만인diff(UniversalDiff, 고정) + 개인화diff(PersonalDiff, 런타임 가변)
```

### 격리 — `SunnyConstants.WithIsolatedDiff`

`SunnyConstants`는 필드 대신 `AsyncLocal<double[]?>` 백업 프로퍼티다. 기본 읽기는 프로세스 전역 기본값(`DiffCombiner.Combine()` = 만인diff만, `Reload()`가 채움) — 지금 존재하는 모든 sunny 소비처(1.1/2.1/4.1/4.2 pill, 툴팁, 결과창 등)가 그대로 읽는 값이고 **개인화diff는 여기 절대 안 섞인다.**

`SunnyConstants.WithIsolatedDiff(deltas, () => 계산)`으로 감싼 콜 컨텍스트 안에서만 `deltas`(스톡 기준 전체 39-벡터)가 대신 읽힌다. `AsyncLocal`은 `Task.Run`/`await`를 타고 흐르지만 형제 Task나 다른 스레드로는 새지 않으므로, 개인화 계산·굽기가 다른 곳의 sunny 계산과 동시에 돌아도 서로 영향이 없다 — 락도 "지금 굽는 중인지 확인"도 필요 없다. 이 메서드를 쓰는 소비처는 지금 `PersonalSunnyWidget`(현재 맵 개인화 pill)과 `StrainGraphWidget`(§18의 개인화 오버레이), 굽기 파이프라인 자체뿐이다.

### 개인화 fit — `Tuning/PersonalJacobianBaker.cs` + `PersonalFitSolver.cs`

맵 하나당 sunny를 23회 스윕(기준점 1 + 11개 상수 각각 ±H 중심차분, H=0.075 유닛공간, 박스는 만인 지점 중심 ±30%)해서 `SR0`(만인 지점 값) + 유닛공간 자코비안 11개를 굽는다. 이후 적합은 sunny를 다시 안 부르고 `SR(c0+d) ≈ SR0 + J·d` 선형화만으로 α/β(sr0 단독 OLS)와 개인 Δ 11개(잔차를 자코비안에 ridge)를 2단계로 분리해서 푼다 — 가우스 소거, ridge=0.01 고정(오프라인 5-fold CV로 정한 값, 클라이언트는 재탐색 안 함).

**2단계 ridge는 raw 자코비안 그대로에 페널티를 건다.** 11개 축이 서로 다른 자코비안 스케일을 갖다 보니 등방(isotropic) 페널티가 상관된 두 축(예: `MixFirst`/`PressingWeight`, 같은 strain-mix 분기를 반대로 누르는 구조라 r=-0.89) 중 스케일이 큰 쪽에 credit을 몰아주는 편향이 있음이 실측으로 확인됐다. 열별 표준편차로 정규화하는 방식을 2026-08-22에 시도해 held-out 예측 오차는 개선됐지만, 정규화된 축(특히 `ChordScale`처럼 원래 분산이 작은 축)에 기존 `ridge=0.01`이 턱없이 약해 언클램프 해가 박스를 최대 6배 벗어났고, 클램프가 그 초과분을 자르며 원래 α가 흡수했어야 할 성분이 새 나가 350개 채보 전부에 균일한 -0.54 SR 하락을 만드는 사고로 이어져 롤백했다. 정규화 아이디어 자체는 유효해 보이나 ridge 재보정 없이는 위험 — 상관축 짝지음 페널티도 시도했으나 held-out CV 기준 오히려 나빠져 보류. `PersonalFitSolver.Solve` XML 문서 참고.

**α·β를 보존한다.** 파이썬 레퍼런스(`personal_fit.py`)는 프로파일 아웃하고 버리지만, 여기서는 위젯의 "정확도 95%에서 몇 성인가"(`PersonalSunnyWidget.target_accuracy`) 표시가 정확히 이 둘의 역산(`SR = (yTarget-α)/β`)이라 `PersonalSunnyFitStore`에 같이 저장한다.

### 파이프라인 v2 — 2-pool broad/narrow (2026-08-20 재설계)

v1(2026-08-19)은 최근 100개 FIFO 하나뿐이었다. 문제: 정확도로 표본을 뽑는 게 아니라 순수 최근성이라 실력 천장 근처 데이터가 잘 안 모이고, SR 스프레드도 좁았다. v2는 풀을 두 개로 나눈다.

```
Pool A — 상위 200 ("실력 천장", Performance 기준, PersonalSunnyTopPoolEntry.Performance)
  ordered map: Dictionary<ChartKey,Entry>(존재조회) + SortedSet<(Performance,ChartKey)>(정렬·min-eviction)
  PersonalSunnyTopPoolStore, capacity 200(2026-08-22, 300에서 축소), 재도전 시 Performance가 기존보다 높을 때만 in-place 갱신(2026-08-22 수정 - 원래 무조건 덮어써서 더 나쁜 재도전이 개인 최고 기록을 지워버리는 버그가 있었음, 실측으로 확인)
  schema_version 2(2026-08-22, 300->200 캐패시티 변경으로 bump - 이 값이 안 맞는 파일은 로드 시 그냥 버려짐. 이 풀의 캐패시티/랭킹공식/eviction 의미가 바뀔 때마다 반드시 같이 bump할 것 - Pool B(QueueStore)와 달리 Offer()는 항상 "더 나을 때만" 갱신이라 옛 파일이 새 로직으로 저절로 안 맞춰짐)

Pool B — 최근 100 중 상위 50 (평소 실력, 정확도 85% 고정 하한)
  PersonalSunnyQueueStore, FIFO 100개, dedup 없음 - 저장 자체는 그대로
  fit 투입 시점(combinedEntries)에서만 채보당 최고 정확도 1개로 dedup 후 Performance 상위 50개로 축소(2026-08-22)
```

Pool A는 원래 순수 SR로 뽑았으나(2026-08-20 설계), 실기 대조 결과 선형모델이 저SR 천장효과를 못 담아내는 게 드러나 2026-08-21에 `Performance`(osu! `ManiaPerformanceCalculator`의 SR^2.2×정확도 배율 곡선을 그대로 차용, 80% 미만 정확도는 자동 0) 기준으로 전면 교체됐다 — 정렬만 바뀌었을 뿐 "정확도로 표본을 거르지 않는다"는 원래 취지(선택편향 회피)는 유지된다. 반면 SR만으로는 천장 근처에 몰려 스프레드가 좁아 ridge fit의 β 추정이 불안정해지므로, Pool B가 낮은/중간 SR의 "평소" 정확도를 보충한다.

**"실력 천장" 풀인데 최고 기록이 안 남는 버그가 두 겹으로 있었다(2026-08-22, 실측으로 확인).** `Offer()` 자체가 이미 풀에 있는 채보를 무조건 최신 기록으로 덮어썼고, 전체 재수집 경로(`runBroadPhase`의 `perChart`)도 채보당 "가장 최근 플레이"를 대표로 뽑아 `Offer()`에 넘겨 애초에 최고 기록이 도달을 못 했다. 두 층 다 고쳐서 해소 — `Offer()`의 실시간 단일 반영 경로(`Player.ImportScore` → `offerToTopPool`)는 이 dedup을 안 거치므로 처음부터 정상이었다.

**Pool B의 진입 문턱(`passesRecentPoolFloor`)은 정확도 85% 고정이지 Performance 상대값이 아니다.** 한때 "Pool A 최댓값 × 0.7"(Performance 상대값)로 시도했으나, 이 방식은 SR이 낮은 채보가 문턱을 넘으려면 정확도를 거의 만점급으로 밀어올려야 해서 Pool B의 저SR 구간이 "저SR+고정확도"로만 쏠리고, 그 결과 ridge fit의 β(SR-정확도 공분산 기울기)가 과도하게 가팔라져 목표 SR이 실측보다 낮게 나오는 문제가 있었다(2026-08-21 재검토). 고정 85% 컷은 SR과 무관하게 "성실한 시도"만 거르므로 이 왜곡이 없다 — 85%는 2026-08-21 이상치 분석(진짜 이상치 0.1347, 다음 정상값 0.4238, 정상 꼬리는 0.60부터)에서 가져온 값.

**Pool B는 Arcaea 포텐셜(b30+r10) 방식을 따라 저장 풀 전체가 아니라 그 중 상위 일부만 fit에 넣는다(2026-08-22).** Arcaea의 Recent10은 "최근 30판 중 Play Rating 상위 10개, 채보당 최고 1개만"으로 계산된다 — `PersonalSunnyQueueStore`(최근 100개 FIFO)를 그대로 두고, `combinedEntries()`가 fit 입력을 만들 때만 채보 키로 그룹핑해 최고 정확도 1개씩 남긴 뒤 `Performance` 상위 `recent_pool_effective_count`(50, 비율은 Arcaea의 1:3 대신 1:2)개만 뽑는다. `PersonalSunnyQueueStore` 자체(저장/FIFO/85% 문턱)는 안 건드린다 — 이 축소는 오직 fit 투입 단계에만 적용. 최종 fit 입력은 `combinedEntries()`(Pool A ∪ Pool B의 이 축소분, dedup 없이 그냥 이어붙임 — Arcaea b30+r10 선례처럼 겹침 허용, 2026-08-20 피드백 반영)다.

#### 계산 깔때기 (broad → narrow)

```
[0] 채보(맵,배속,모드) 단위 dedup — 최근 순이 아니라 정확도 최고치가 대표(2026-08-22 수정, 아래 참고)
[1] 무료 필터 — NM / DT·NC@1.5x / HT·DC@0.75x 3버킷, 각각 BeatmapInfo.StarRating(osu! 자신이 이미
    realm에 계산해둔 NoMod 값, 읽기만 함)으로 상위 2,000 컷. -1(미계산)은 무조건 통과.
    3버킷으로 나누는 이유: 배속 모드의 실제 난이도는 저장값보다 높아서(HT/DC는 낮아서) NM과 한
    리스트로 같이 정렬하면 부당비교가 된다. 여유폭 2,000은 실측 데이터 없이 고정한 값(재검토 이슈).
[2] Broad-phase — 생존자만 PersonalJacobianBaker.CalculateUniversalSr(1회 계산, Bake의 앞부분만
    떼어낸 것)를 병렬로 실행, 결과는 PersonalSunnyChartSrStore(경량 캐시, 채보당 평생 1회)에 저장
[3] 정확한 상위 200 확정 → PersonalSunnyTopPoolStore.Offer
[4] Narrow-phase — Pool A∪B에 대해서만 기존 23-sweep 자코비안 굽기(PersonalSunnyJacStore)
```

- **broad-phase(runBroadPhase)와 narrow-phase(bakeMissing) 둘 다 `Parallel.ForEach`**를 쓴다. `BeatmapManager.QueryBeatmap`/`GetWorkingBeatmap`은 osu! 소스 확인 결과 내부적으로 `Realm.Run`/`WorkingBeatmapCache`의 `lock`으로 이미 스레드 안전하다. 우리 쪽 캐시 3개(`PersonalSunnyChartSrStore`/`PersonalSunnyJacStore`/`PersonalSunnyTopPoolStore`)는 각자 자체 락(`ConcurrentDictionary`+저장전용 락, 또는 `PersonalSunnyTopPoolStore`처럼 두 컬렉션을 함께 지키는 단일 락)을 갖고 있어 병렬 호출에 안전하다.
- **캐시는 2종류, 무효화 기준도 같이 챙긴다.** `PersonalSunnyChartSrStore`(경량, broad-phase 후보 전체를 넓게 들고 있음)와 `PersonalSunnyJacStore`(무거움, Pool A∪B로 뽑힌 것만) 둘 다 `UniversalDiff.Deltas`를 문자열로 스냅샷해 파일에 같이 저장 — 만인diff가 리튜닝되면 자동으로 폐기되고 재계산된다. `refit()`은 `PersonalSunnyJacStore`만 Pool A∪B로 pruning하고 `PersonalSunnyChartSrStore`는 일부러 안 건드린다(성격이 다른 캐시).
- **캐시 3개(`PersonalSunnyChartSrStore`/`PersonalSunnyJacStore`/`PersonalSunnyTopPoolStore`) 모두 `Put`/`Offer`에 `save: bool = true` 매개변수가 있다(2026-08-22 추가).** 기본값 `true`는 `RecordScore`의 단건 실시간 반영 경로용 — 그때그때 바로 파일에 씀. `runBroadPhase`/`bakeMissing`의 `Parallel.ForEach`는 캐시가 비어있는 최초 실행에서 후보 수천 개를 처리하는데, 항목마다 전체 캐시를 재직렬화해 쓰면 O(n) 쓰기를 n번(=O(n²)) 하는 데다 스레드마다 같은 저장 락을 두고 경합해 병렬성도 깎아먹는다 — 그래서 이 두 곳은 `save: false`로 메모리만 갱신하고 루프가 끝난 뒤 `Flush()`를 한 번만 호출한다.
- **채보 키는 이제 (맵 MD5, 정확한 배속) 사실상 둘뿐이다.** `PersonalSunnyModWhitelist`가 2026-08-20부터 osu! 자신의 `Mod.Ranked`(DT/HT/NC/DC는 `SpeedChange.IsDefault`일 때만 true)를 그대로 가져다 써서 **정확히 1.5x/0.75x가 아닌 배속과 HO/IN을 아예 큐 진입 단계에서 제외**한다 — HO/IN처럼 `HitObjects` 자체를 재작성하는 모드는 broad-phase 무료필터(원본 채보의 저장된 SR)가 순위 프록시로 못 쓰이기 때문(방향을 예측할 수 없는 오차). `ChartMod` 필드는 구조상 남아있지만 이제 항상 `null`.
- **자동 수집은 `Player.ImportScore`가 유일한 진입점**이고, 훈련/구간연습/리플레이감상용 `Player` 파생 클래스들은 전부 이 메서드를 `base` 호출 없이 override하므로(safety.md 서버 격리 표) 패치가 그쪽엔 자동으로 안 걸린다. `RecordScore`가 Pool B에 추가하면서 `offerToTopPool`로 Pool A 경쟁에도 반영한다(broad-phase 전체를 다시 돌 필요 없이, 그 채보 하나만 `resolveUniversalSr`).

#### 백그라운드 선제 계산 — `PersonalSunnyService.StartBackgroundWarmup`

osu! 자신의 `BeatmapUpdater`(임포트 시 스레드풀로 별점을 미리 계산해 realm에 저장)와 같은 패턴이다. 위 broad/narrow 파이프라인을 반응형(수집 버튼)이 아니라 **위젯이 처음 로드될 때 자동으로 한 번** 돌린다(`Interlocked.CompareExchange`로 세션당 1회만, `Task.Run`) — 계산 로직·캐시는 완전히 동일한 것을 재사용, 트리거만 다르다.

- **위젯 Drawable 생명주기와 무관하게 산다.** 워커는 `PersonalSunnyService`(static)에 있고 `Task.Run`으로 떠 있어서, 선곡→게임플레이→결과창으로 화면이 바뀌어도(위젯 자체는 게임플레이 중 안 그려짐) 계속 돈다.
- **게임플레이 중엔 동시성을 1로 낮춘다.** `Patches/PersonalSunnyGameplayActivityPatch.cs`가 `Player.LoadComplete`/`OnSuspending`/`OnExiting`을 Postfix해서 `PersonalSunnyService.GameplayActive`만 갱신하고(읽기 전용, 라이브 인스턴스 무관), broad/narrow 두 `Parallel.ForEach`가 이 값을 보는 `currentParallelism()`을 쓴다. 훈련/구간연습/리플레이도 전부 대상 — CPU 경합이 관심사라 `PlayerGameplayPatch`처럼 특정 Player 종류를 가릴 이유가 없다.
- 초기 이력 스캔 자체는 못 없앤다 — 다만 **채보당 평생 1번**(SR 캐시)이라 두 번째 실행부터는 새로 생긴 채보만 계산한다.

### 위젯 — `Widgets/PersonalSunnyWidget.cs`

선곡 화면 전용(`GameplayState`가 잡히면 그리지 않음 — `SkinWidgetRegistrarPatch`가 HUD/선곡 두 툴박스에 다 노출시키므로 §16과 같은 이유로 자체 판별 필요). 위쪽에 pill 2개 — 현재 스코프된 맵의 (만인+개인) sunnySR, 그리고 이 사람이 정확도 95%를 뽑는 sunny 값(맵과 무관, α·β 역산). 아래쪽은 굽는 중이면 진행률 막대, 아니면 **`"최고 {TopPoolRecordCount}/{PersonalSunnyTopPoolStore.Capacity} · 최근 {RecentPoolRecordCount}/{RecentPoolEffectiveCount}"` + 수집 버튼**(2026-08-22) — 각 풀을 "그 풀 자체 상한 대비 분수"로 보여준다. 총합 하나로 합치거나 raw+반영을 섞어 보여주는 방식도 검토했으나, 전자는 두 풀의 성격 차이가 안 드러나고 후자는 Pool B 원본이 늘어도 개인화엔 영향 없는데 숫자만 커지는 문제가 있어 이 분수 표기로 확정.

수집 버튼(`onCollectClicked`)은 `PersonalSunnyService.ResetAndCollectFromRealmAsync`를 부른다(2026-08-22 신규) — `PersonalSunnyTopPoolStore.Clear()` 후 `CollectFromRealmAsync`. Pool B는 `ReplaceQueueAndRun`이 매 수집마다 전체 교체라 이미 매번 리셋되지만, Pool A는 `Offer()`가 항상 "더 나을 때만 갱신"이라 그 자체로는 절대 안 비워진다 — 캐패시티·랭킹 공식이 바뀌어도 이미 저장된 파일이 새 로직에 맞게 저절로 줄어들거나 재정렬되지 않는다는 뜻. 그래서 수동 버튼은 명시적으로 Pool A를 비우고 처음부터 다시 채운다(자동 백그라운드 워밍업/실시간 `RecordScore` 경로는 그대로 증분 유지 — 매번 통째로 다시 도는 건 낭비).

---

## 18. StrainGraphWidget 개인화 오버레이 (2026-08-19 신규)

`StrainGraphWidget`이 만인 strain과 (만인+개인) strain을 각각 계산해(`WithIsolatedDiff`로 후자만 격리) 겹쳐 그린다. 매 시점 t의 두 값을 집합처럼 봐서: 교집합(`min`)은 기존 흰/회색 막대(재생 진행 밝기 표시 포함) 그대로, 만인이 더 큰 만큼(내가 남들보다 잘 치는 부분)은 파랑, 개인이 더 큰 만큼(못 치는 부분)은 빨강으로 그 위에 얹는다. 정규화는 안 한다 — 두 곡선을 같은 화면 높이에 맞추는 분모(둘 중 최댓값)만 공유하고, 모양을 서로 맞추는 리스케일은 하지 않는다.

`Drawables/StrainAreaGraph.cs`에 `StrainCapCurve`를 추가해 이 겹치는 캡을 그린다 — 기존 `StrainCurve`(항상 바닥에서 시작)와 같은 quad-batch 방식이지만 `[Low, High]` 구간만 뜬 막대를 그린다. 재생 진행에 따른 밝기 분리(§ 없음, `playedMask` 트릭)는 기존 흰/회색 막대에만 적용되고 새 캡 2개는 안 받는다 — 이 오버레이는 진행률이 아니라 난이도 성향 표시라 성격이 다르다고 판단.

---

## 19. 패턴 복제 모드 (2026-08-21 신규)

대상 리듬게임 화면을 실시간으로 읽어 그 패턴을 osu!에 **그대로 재현**하는 모드. 노트를 만드는 쪽은
osu! 밖의 별도 프로그램 **newScreen**(`C:\dev\ScreenEditor\newScreen`)이고, 여기는 그것을 받아 놓기만 한다.

```
메인 메뉴 ─(편집 서브메뉴)→ PatternCopyScreen ─(즉시)→ PatternCopyPlayerLoader → PatternCopyPlayer
                                                                                     ↑ pc:* 명령
newScreen (화면 캡처 → 노트 감지) ──Named Pipe──> PatternCopyBridge ──> PatternCopyInjector
```

| 파일 | 역할 |
|---|---|
| `Patches/PatternCopyMenuButtonPatch.cs` | 메인 메뉴 **편집** 서브메뉴에 버튼 추가 (무한 트레이닝은 플레이 서브메뉴) |
| `Screens/PatternCopyScreen.cs` | **화면을 그리지 않는 통과 지점.** 고를 것이 없으므로 곧장 게임플레이로 넘어간다 |
| `Screens/PatternCopyBeatmap.cs` | 6키 시드 비트맵(약 83분). 무한 트레이닝의 것과 성격이 같고 키 수만 다르다 |
| `Screens/PatternCopyPlayer.cs` | `Player` 직접 상속. 매 `Update`에서 명령 큐를 비워 주입한다 |
| `PatternCopy/PatternCopyBridge.cs` | 파이프(백그라운드 스레드) → 업데이트 스레드 사이의 명령 큐 |
| `PatternCopy/PatternCopyInjector.cs` | 시각 원점 관리 + `Playfield.Add` + 열린 롱노트 추적 |
| `PatternCopy/HoldNoteTruncator.cs` | 주입된 롱노트를 게임플레이 도중 잘라낸다 (아래 참고) |
| `PatternCopy/PatternCopySessionState.cs` | 캡처 세션 경계 통지 (`SessionIndex`) |
| `PatternCopy/InactiveFrameRateOverride.cs` | 포커스를 잃어도 포커스 시와 같은 프레임을 유지 (아래 참고) |
| `Input/RawKeyRelay.cs` | 비포커스 상태에서도 키 입력을 받게 한다 (아래 참고) |
| `Widgets/PatternCopyStatusWidget.cs` | 콤보/정확도 표시. 세션이 바뀌면 리셋 |

**화면이 통과 지점인 이유는 DI다.** `Beatmap`/`Ruleset`/`Mods`는 `OsuScreen`의 protected 멤버라,
메뉴 버튼 패치에서 직접 세팅하려면 리플렉션이 필요하다. 화면을 하나 두면 전부 평범한 대입이 된다.

### 프로토콜 (newScreen → Hook, 한 줄씩)

```
pc:sync:<보낸쪽 경과ms>              시간 원점. 매 틱 오지만 원점이 없을 때만 쓴다
pc:note:<id>:<col>:<hitMs>:<durMs>   길이가 확정된 노트 (durMs=0이면 단노트)
pc:hold:<id>:<col>:<hitMs>           끝을 아직 모르는 롱노트
pc:cut:<id>:<durMs>                  그 롱노트의 실제 길이 확정
pc:stop                              캡처 종료 (열린 롱노트 정리)
```

- **`pc:sync`를 매 틱 반복해서 보낸다.** 한 번만 보내면 캡처 시작과 모드 진입의 **순서에 의존**하게 되고,
  모드에 들어오기 전에 온 `pc:sync`는 `Bridge`가 버리므로 원점이 영영 잡히지 않는다(실제로 겪은 버그).
- 컬럼 매핑과 사이드노트 병합은 **newScreen이 전담**한다. Hook은 컬럼 번호를 그대로 받는다.
- **파이프 클라이언트는 `PipeOptions.Asynchronous`로 열어야 한다.** 없으면 동기 핸들이 되고 Windows가
  같은 핸들의 I/O를 직렬화해서, 수신 스레드가 `ReadFile`에서 대기하는 동안 `WriteFile`이 함께 멈춘다.
  또한 **들어오는 줄을 읽어서 버려야 한다** — `PipeServer`는 `_activeWriter`(가장 최근 연결) 하나에게만
  쓰는데, 게임플레이 중에는 `PlayerGameplayPatch`가 100ms마다 브로드캐스트하기 때문이다.
  Launcher의 `PipeClient`가 원래 둘 다 하고 있었다.

### 롱노트 런타임 절단 — `ApplyDefaults()`를 다시 부르면 안 된다

대상 게임의 롱노트는 언제 끝날지 미리 알 수 없어서 **아주 길게(100초) 깔아두고 끝이 확정되면 자른다.**
리드타임보다 긴 롱노트(= 절대다수)는 **이미 헤드가 판정되어 붙잡고 있는 상태에서** 잘라야 한다.

`ApplyDefaults()` 재호출은 `CreateNestedHitObjects()`가 Head/Tail/Body를 **새 객체로 교체**하고
`DrawableHitObject.onDefaultsApplied`가 전체 재적용을 유발해서, 헤드의 판정 결과와 누적된 홀드 기록이
통째로 날아간다 — 절단이 아니라 **노트 재생성**이 되고 붙잡고 있던 홀드가 끊긴다(실기 확인).

대신 세 가지만 외과적으로 고친다:
1. `HoldNote.Duration` 대입 — setter가 **기존** `Tail.StartTime`을 갱신한다(객체 교체 없음).
2. `Body.Duration` 대입 — `Duration` setter가 Body는 건드리지 않아 수동으로 맞춘다.
3. `DrawableHitObject.DefaultsApplied` **강제 발화**(리플렉션) — 화면상 길이를
   `ScrollingHitObjectContainer`가 `layoutComputed`에 캐시하므로 무효화가 필요하다.
   **게임플레이 중 이 이벤트의 구독자는 `invalidateHitObject` 하나뿐**이라(나머지는 에디터 전용)
   부작용 없이 레이아웃 재계산만 얻는다.

### 비포커스 입력 릴레이 (`Input\`)

이 모드에서는 사용자가 **대상 게임을 조작**하므로 osu!는 포커스를 못 받는다. 그러면 SDL 기본 키보드
핸들러가 아무것도 받지 못한다. Raw Input의 `RIDEV_INPUTSINK`로 **포커스와 무관하게** 하드웨어 키를
받아 프레임워크 입력 큐에 넣는다.

- `UserInputManager`가 매 프레임 폴링하는 대상이 `Host.AvailableInputHandlers`이고, 이 프로퍼티는
  `{ get; private set; }`이라 auto-property 백킹 필드를 통해서만 핸들러를 추가할 수 있다(§ui-patching §3 패턴).
- **키보드 입력은 포커스로 걸러지지 않는다.** `UserInputManager`의 포커스/커서 검사는 마우스 전용이고
  `Host.IsActive` 게이트는 없다 — 그래서 이 방식이 성립한다.
- **osu!가 포커스를 가진 동안에는 릴레이를 멈춰야 한다.** 안 그러면 SDL 핸들러와 겹쳐 한 번 누른 키가
  두 번 들어간다. 또 그 전환 시점에 **눌러둔 키를 전부 떼어줘야** 한다(안 그러면 영원히 눌린 키가 남는다).
- 합성 입력(`SendInput` 등)은 쓰지 않는다 — 사용자가 실제로 누른 키를 전달만 한다.

#### ⚠️ 윈도우 클래스와 WNDPROC 델리게이트의 수명은 반드시 같아야 한다 (2026-08-21 재진입 즉사)

**증상**: 패턴 복제 모드를 한 번 하고 나갔다가 **두 번째로 들어가면** 게임플레이로 전환되는 순간
osu!가 로그 한 줄 없이 죽는다. 첫 진입은 멀쩡하다.

**원인**: Win32 윈도우 클래스는 `RegisterClassEx` 시점의 함수 포인터를 **값으로 복사해 보관**하고
`UnregisterClass` 전까지 **프로세스 수명 내내** 남는다. 반면 `Marshal.GetFunctionPointerForDelegate`가
만든 스텁은 **GC가 추적하지 않는다.** 델리게이트를 인스턴스 필드(= 세션 수명)에 두면 세션이 끝나며
수거되고, 클래스에는 **해제된 메모리를 가리키는 포인터만 남는다.** 두 번째 세션이 같은 클래스 이름으로
창을 만들면 `RegisterClassEx`는 `ERROR_CLASS_ALREADY_EXISTS`로 실패하지만 `CreateWindowEx`는
**성공하면서 그 죽은 포인터를 쓴다** — 반환 전에 동기로 보내는 `WM_NCCREATE`부터 액세스 위반이다.
네이티브 크래시라 `try/catch`로 못 잡고 `HookLog`도 no-op이라 아무 흔적이 없다.

**해결**: 델리게이트를 `static readonly`로 프로세스 수명에 맞추고 클래스도 한 번만 등록한다.
프로시저가 하나로 공유되므로 **수신 대상만 세션마다 static 필드로 갈아끼운다**.

- 이 버그가 없었어도 **두 번째 세션의 키 입력은 원래 안 먹었다** — 새 창의 메시지가 이미 `Dispose`된
  첫 세션 리스너로 갔기 때문이다. 즉사와 별개의 결함이 같은 자리에 있었다.
- **네이티브에서 불리는 콜백 밖으로 예외를 내보내지 않는다.** 같은 이유로 **백그라운드 스레드
  진입점의 `finally`도 통째로 try/catch로 감싼다** — 스레드 밖으로 나간 예외는 .NET에서 곧 프로세스
  종료다. `Dispose`가 `Join` 타임아웃 뒤 `ManualResetEventSlim`을 버리면 메시지 스레드의
  `ready.Set()`이 던지는 경로가 실재했다(그래서 지금은 버리지 않는다).

### 비포커스 프레임 유지 (`InactiveFrameRateOverride`, 2026-08-21 재작업)

osu! 화면을 WGC로 캡처해 오버레이로 보는 모드라 **비포커스 프레임이 곧 캡처 품질**이다.
프레임워크는 포커스를 잃으면 업데이트·드로우 스레드를 `GameThread.DEFAULT_INACTIVE_HZ`(60)로 묶는다.

스로틀의 전부는 이것 하나다 —

```csharp
// GameThread.updateMaximumHz()
Scheduler.Add(() => Clock.MaximumUpdateHz = IsActive.Value ? activeHz : inactiveHz);
```

따라서 **모드가 도는 동안만 `InactiveHz`를 포커스 시 값으로 올리고 나갈 때 되돌리면** 된다.
패치도 리플렉션도 필요 없다 — osu! 본체 `LatencyCertifierScreen`이 `ActiveHz`에 대해 같은
저장/덮기/복원을 한다. 수명은 `RawKeyRelay`와 같이 `PatternCopyPlayer.OnEntering`/`OnExiting`이다.

**⚠️ `MaximumDrawHz`를 그대로 복사하면 안 된다 — 이게 2026-08-21 첫 시도가 실패한 이유다.**
`updateFrameSyncMode()`는 VSync에서 `drawLimiter = int.MaxValue`로 두고 실제 제한을 vsync에 위임하며,
그 값이 `maximum_sane_fps`(= `GameThread.DEFAULT_ACTIVE_HZ` = 1000)로 클램프된다. 즉 VSync·Unlimited에서
`MaximumDrawHz`는 **1000이고 이건 체감 프레임이 아니다** — 144Hz에서 144가 나오는 건 숫자가 아니라
vsync가 present를 막기 때문이다. 그런데 **가려진 창은 vsync에 물리지 않아** 비포커스에는 그 제한자가
통째로 사라진다. 그래서 1000을 옮기면 갱신률의 몇 배로 과렌더하다 렉으로 끊긴다.

| FrameSync | 포커스 시 실효 draw | `DrawThread.InactiveHz`에 넣는 값 |
|---|---|---|
| VSync (osu! 기본) | 갱신률 (vsync가 제한) | `Window.CurrentDisplayMode.Value.RefreshRate` |
| Limit2x / 4x / 8x | `MaximumDrawHz` (유한, vsync off) | 그대로 |
| Unlimited | 1000 | 갱신률 — 그 위는 **대상 게임의 GPU를 뺏을 뿐** |

업데이트 스레드는 `MaximumUpdateHz`를 **그대로 복사한다**. 항상 `maximum_sane_fps`로 클램프돼 유한하고,
판정이 이 스레드에서 도니 포커스 때보다 낮추면 그 자체가 손해다.

**세팅 순서가 있다.** `GameHost.MaximumInactiveHz`는 update/draw에 **같은 값**을 꽂으면서 `ThreadRunner`에도
넘기는데, 단일스레드 `ExecutionMode`에서는 그 `ThreadRunner` 값이 모든 스레드를 지배한다(per-thread만
세팅하면 그 모드에서 무효). 그래서 **`MaximumInactiveHz` 먼저 → per-thread `InactiveHz`로 좁히기** 순서다.
반대로 하면 per-thread 값이 덮인다.

> **과거 기록 정정**: 이 기능은 한 번 "실측상 비포커스에서도 프레임이 안 떨어지므로 막을 문제가 없다"고
> 결론 내고 되돌린 적이 있다. **그 판단은 틀렸다** — 당시 개발 PC가 60Hz라 `DEFAULT_INACTIVE_HZ`(60)와
> 차이가 안 났을 뿐이다. 고주사율 환경에서는 그대로 드러난다.

### 여전히 이걸로 못 고치는 것

대상 게임이 **전체화면 독점(exclusive fullscreen)**이면 DWM 합성 경로가 빠져 osu!의 present 자체가
진행되지 않는다. 이건 프레임워크 밖의 문제라 위 조정으로 해결되지 않는다 — 대상 게임을 borderless로
돌리거나, osu! 창을 완전히 가리지 않게 두거나, 보조 모니터로 빼야 한다.
**증상 분리법**: 포커스만 뺏고 osu! 창은 보이게 둔 채 FPS를 본다. 딱 60이면 위 스로틀이 원인이고,
그보다 낮거나 불규칙하면 오클루전이 섞인 것이다.

---

## 20. 실시간 sunny 위젯 (`RealtimeSunnyWidget`, 2026-08-29 신규)

게임플레이 HUD 스킨 위젯. 기본 sunny pill(`StarRatingDisplay`)과 생김새가 같고, 맵 전체가 아니라
**앞으로 400ms 구간의 난이도**를 100ms마다 갱신해 보여준다.

- **사전계산**: 로딩 중 BDL의 `Task.Run`에서 `SunnyManiaDifficultyCalculator.GetStrainTimeline`을 한 번
  돌려 노트별 `(시각, strain)`를 통째로 들고 있는다. 이후 매 틱은 배열 조회만 한다(무거운 계산 없음).
- **매 틱**: `gameplayClock.CurrentTime`부터 `min(+400ms, 맵 끝)` 윈도우 안의 **모든 노트 strain 산술평균**
  = raw. 윈도우 폭은 배속과 무관한 고정 400ms(비트맵 시간). 동시치기는 sunny 원본처럼 값이 개수만큼
  중복 들어간다. 윈도우에 노트가 없으면 0. (상위 N개만 뽑던 초기안은 로컬 피크만 반영돼 맵 전체
  sunny보다 값이 과하게 높아서 산술평균으로 교체했다.)
- **합산**: 실시간이라 상위/중위 퍼센타일·파워민을 쓰지 않는다. `raw`를 §합산 파이프라인의 **짧은 맵
  보정 이후부터** 통과시킨다 — `SunnyTempNerf.Apply(raw) × 0.975`. (고SR 리스케일은 원본에서 이미 비활성.)
- sunny/sunny+ 구분은 여기서 안 한다 — 런처 체크박스가 프로세스 전역 기본값을 정하고 `GetStrainTimeline`이
  다른 소비처처럼 그 값을 읽는다. **개인화 diff는 위젯 설정 토글(`UsePersonalSunny`)로 opt-in** — 켜면
  베이크가 `SunnyConstants.WithIsolatedDiff(PersonalDiff.CombinedWithUniversal(), …)` 안에서 돈다
  (`StrainGraphWidget` 개인화 오버레이와 동일). 토글을 바꾸면 타임라인을 다시 베이크한다.
- **표시**: `pill.Current.Value`에 새 값을 넣기만 한다. `StarRatingDisplay(animated: true)`가
  `100 + 80·|Δ|`ms 트윈으로 직전 표시값→새 값을 추종하고 색도 그 트윈값을 따라가므로 0으로 튀지 않는다.
- 서버 레드라인 무관: 읽기 전용, 라이브 인스턴스 미접근, 네트워크·파일 쓰기 없음.
