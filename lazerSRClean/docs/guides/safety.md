# Safety Guidelines

**2026-07-17 재정의.** 구판(2026-05-14 작성)은 "게임플레이 경로 절대 패치 금지"를 문자 그대로의 레드라인으로 삼았으나, 이는 osu!의 실제 무결성 모델과 맞지 않는 구시대 기준이었다. 이 문서는 사용자와의 논의 + 실제 코드 감사를 거쳐 재작성됨.

---

## 핵심 원칙

osu!는 커널 레벨 안티치트가 없고, `tosu`/`gosumemory`/`StreamCompanion` 같은 메모리 읽기 오버레이 도구가 커뮤니티에서 광범위하게 허용된다. osu! 클라이언트에는 hook/injection 탐지 로직이 존재하지 않음이 확인됐다 (`docs/skin-widget-research.md` §14 — 소스 전체 grep 결과 무결성 검사 코드 0건).

**실제로 중요한 건 단 하나: 서버로 제출되는 값(점수, 정확도, 판정, 리플레이)이 실제로 조작되는가.**

이것만 지키면 다음은 전부 허용된다:
- 게임플레이 중 코드가 도는 것 자체
- osu! 라이브 클래스(`ManiaScoreProcessor`, `HitWindows` 등)를 **읽거나, 별도 인스턴스로 로컬 시뮬레이션**하는 것
- 로컬에서 계산한 값(점수 추정치, 콤보 등)을 파이프로 Launcher나 오버레이에 전송하는 것
- `.osr` 리플레이 파일을 읽어서 분석하는 것 (쓰기는 여전히 금지, §3 참고)

## 절대 레드라인 (변경 없음)

1. **osu!가 실제 사용하는 라이브 인스턴스(`Player`가 소유한 `ScoreProcessor`/`HealthProcessor`/`JudgementProcessor`, 실제 제출되는 `Score`/`Replay` 객체)에 절대 쓰지 않는다.** 읽기만 허용.
2. **osu! 프로세스에서 외부로 나가는 네트워크 호출 절대 금지** (`HttpClient`, `Socket` 등). Named Pipe는 로컬 IPC이므로 해당 없음. — 이 레드라인은 **osu!에 주입되는 Hook 전용**이다. 별도 프로세스인 **런처**(`LazerSR.Launcher`)는 자동 업데이트 확인차 GitHub API를 호출한다(`architecture.md` §21). 점수·판정·리플레이와 무관하고 osu! 프로세스 밖이라 레드라인에 걸리지 않는다.
3. **osu! 파일(`client.realm`, `osu.cfg`, `.osu`, `.osr`)에 쓰지 않는다.** 읽기는 허용 (`RealmAccess`, `Storage` API 경유).
4. **`BeatmapInfo.StarRating` 등 realm/DB 저장값에 쓰지 않는다.**

### 라이브 인스턴스 vs 시뮬레이션 인스턴스 — 구분이 핵심

`Calculators/ReplayScoreTimeline.cs`가 `new ManiaScoreProcessor()`를 만들어 `.osr`을 재생하며 점수를 재계산하는 것은 **레드라인 위반이 아니다** — 이 인스턴스는 `Player`가 실제 제출에 쓰는 라이브 `ScoreProcessor`와 완전히 분리된, 화면 표시 전용 로컬 사본이다. 반대로 `Player`의 `[Resolved] ScoreProcessor` 필드를 얻어 그 값을 바꾸는 코드는 절대 금지.

## 더 이상 유효하지 않은 구 레드라인

- ~~"게임플레이/스코어링 경로에 패치 절대 금지"~~ → `PlayerGameplayPatch`가 `Player.LoadComplete`를 패치하지만 읽기 전용 + 로컬 브로드캐스트뿐이라 안전. **패치 대상이 아니라 패치가 뭘 쓰는지가 기준.**
- ~~"오직 `Postfix`만, `Prefix`/`Transpiler`/`Finalizer` 금지"~~ → 이 규칙 자체는 여전히 좋은 기본값으로 유지 권장(Postfix는 구조적으로 리턴값을 못 바꿔 의도치 않은 조작을 원천 차단) — 하지만 "패치 위치가 gameplay면 무조건 금지"였던 이전 근거는 폐기.
- ~~"`Select.*`의 `LoadComplete`만 허용"~~ → `SkinWidgetRegistrarPatch`가 `SerialisedDrawableInfo.GetAllAvailableDrawables`(static 유틸리티)를 패치하지만 순수 읽기+concat이라 안전. 범위 제약이 아니라 **부작용 유무**가 기준.

## 안전성 유지를 위한 실무 패턴 (계속 권장)

- 새 패치도 가능하면 `[HarmonyPostfix]` 우선 — 리턴값 변경 원천 차단, 안전성 증명이 쉬움. `Prefix`/`Transpiler`/`Finalizer`가 필요하다면 반드시 "왜 필요한지 + 어떤 값도 실제 제출 경로에 영향 없음"을 코드 주석 또는 리뷰에서 명시.
- 모든 패치 Postfix는 top-level try/catch 필수 — 예외가 Harmony 트램폴린 밖으로 나가면 osu! 자체가 죽을 수 있음.
- osu! 라이브 클래스를 재사용/시뮬레이션할 때는 반드시 **별도 인스턴스**로 — `new ManiaScoreProcessor()`처럼. `[Resolved]`로 라이브 인스턴스를 얻었다면 그건 읽기 전용으로만 쓴다.
- `SunnyManiaDifficultyCalculator` 등 sunny 파이프라인은 osu! `DifficultyCalculator`를 상속하지 않는다 — 독립 유지 (컴파일 안정성 때문이지 안티치트 때문은 아니지만, 원칙은 유지).

## 무한 트레이닝의 서버 격리 (2026-07-28)

무한 트레이닝은 **완전 로컬 세션**을 표방한다 — 서버 입장에서 메인 메뉴에 머무는 것과 구별되지 않아야 한다. osu!의 로딩+게임플레이 경로를 감사해 서버/DB에 닿는 지점을 전수 확인했고, 아래 5개를 차단했다.

| 지점 | 내용 | 차단 방법 |
|---|---|---|
| `SubmittingPlayer` | 제출 토큰 요청, 점수 제출, **`SpectatorClient.BeginPlaying/EndPlaying`**(타인 관전 활성화) | **`Player` 직접 상속** — `SoloPlayer`/`SubmittingPlayer` 계열을 쓰지 않으면 코드 경로 자체가 없다 |
| `Player.InitialActivity` | 기본값 `InSoloGame(비트맵, 룰셋)` → 메타데이터 서버로 "플레이 중" 전송 | `=> null` override |
| `PlayerLoader.refetchLeaderboard` | `LeaderboardManager.FetchWithCriteria(...)`로 비트맵 리더보드 조회. 읽기 요청이지만 "이 유저가 이 맵을 보고 있다"가 서버에 남음 | `LocalOnlyLeaderboardSkipPatch` (**Prefix**, 아래 참고) |
| `Player.ImportScore` → `scoreManager.Import` | **realm(`client.realm`)에 스코어/리플레이 기록** — 레드라인 3 위반 | `Configuration.ShowResults = false`(주 경로 차단) + `ImportScore` override(실패 오버레이의 "리플레이 저장"이 `forceImport: true`로 우회하는 것까지 차단) |
| 결과 화면 | `ResultsScreen`은 통계 조회 경로를 갖는다 | `ShowResults = false`, `CreateResults`는 `NotSupportedException` |

**안전 확인된 것**: `ILocalUserPlayInfo.PlayingState`는 소비처가 전부 로컬(마우스 confine, import 일시정지, 백그라운드 처리)이라 전송되지 않는다. `Player.CreateScore`의 `api.LocalUser.Value`는 로컬 캐시 읽기.

### `Prefix` 패치를 쓴 유일한 사례 — 정당화

`LocalOnlyLeaderboardSkipPatch`(2026-08-08 개명 전 `InfiniteTrainingLeaderboardSkipPatch`)는 이 프로젝트에서 **유일하게 `Postfix`가 아닌 패치**다. 근거:

- 목적이 **원본 실행 자체를 막는 것**이라 Postfix로는 대체 불가능하고, `refetchLeaderboard`가 `private`이라 override도 불가능하다.
- `__instance is not ILocalOnlyPlayerLoader`일 때만 `true`를 반환하므로 **일반 플레이에는 아무 영향이 없다.** 이 마커를 다는 로더는 무한 트레이닝과 구간 연습 둘뿐이다.
- 이 패치는 리더보드 조회 요청을 생략시킬 뿐이며 **점수/정확도/판정/리플레이 등 서버 제출 경로의 어떤 값도 읽거나 쓰지 않는다.**

### 노트 런타임 주입에 대한 안전성 판단

무한 트레이닝은 게임플레이 중 `Playfield.Add(HitObject)`로 노트를 주입한다. 이는 **레드라인 위반이 아니다** — `Playfield.Add`는 에디터 전용이 아닌 일반 public API이고, 주입 결과로 어긋나는 것은 `ScoreProcessor`의 사전계산값(`MaxHits`/`MaximumTotalScore`)뿐인데 이 세션의 스코어는 **제출되지도 realm에 기록되지도 않는다.** 라이브 `ScoreProcessor`에 우리가 직접 쓰는 코드는 없다(판정은 osu!가 주입된 노트를 정상 처리한 결과일 뿐).

**단, 이 격리가 깨지면 실제 조작이 된다.** 위 표의 5개 차단 중 하나라도 제거하면 주입된 노트로 만들어진 점수가 서버나 로컬 DB에 남을 수 있으므로, 무한 트레이닝 관련 코드를 수정할 때는 반드시 이 표를 먼저 확인할 것.

### 사망 차단과 OD 고정 (2026-07-28 추가)

`InfiniteTrainingPlayer`가 `CheckModsAllowFailure() => false`로 **사망 자체를 막는다.** 단기 탐색은 한계 bpm까지 올리는 게 목적이라 미스 대량 발생이 정상 동작이고, 무한 세션도 죽으면 무한이 아니게 되기 때문이다. `DrainRate`로는 막을 수 없다 — `ManiaHealthProcessor`의 미스 감소량이 `-(DrainRate + 1) * 0.0075`라 0에서도 깎인다.

측정 구간의 노트는 **OD 8.5로 고정 주입**한다(`TrainingSequencer`). 판정창은 비트맵이 아니라 `ApplyDefaults`에 넘기는 난이도 객체가 정하므로 사용자 설정 OD와 분리된다. 둘 다 **로컬 판정에만 관여하고 제출 경로에는 닿지 않는다** — 애초에 이 세션의 스코어는 제출되지도 realm에 기록되지도 않는다.

### 배경음 (2026-07-29 추가)

배경음은 **레드라인 어디에도 닿지 않는다** — 네트워크 호출 없음, osu! 파일 쓰기 없음, 라이브 인스턴스 접근 없음.

- 곡 파일과 메타데이터는 **우리 배포에 동봉**한 것을 `Music\` 폴더에서 읽는다. realm·`Storage`·사용자 비트맵을 조회하지 않는다
- 재생은 게임플레이 시계와 분리된 독립 `DrawableTrack`이다. 게임플레이 시계의 소스 트랙을 갈아끼우지 않으므로 **판정 타이밍에 영향을 줄 수 있는 경로 자체가 없다**
- 배속(`Tempo` 조정)은 우리 트랙에만 걸린다. `MasterGameplayClockContainer`의 `AdjustmentsFromMods`나 `UserPlaybackRate`는 건드리지 않는다

**2026-08-18 변경 — 동봉 폐기, 사용자 비트맵 참조.** 곡 파일을 배포에 담지 않고 사용자의 비트맵을 참조한다. 그 결과:

- **저작권 논점이 사라졌다** — 우리가 재배포하는 오디오가 없다.
- 대신 **realm과 오디오 파일을 읽는다.** 레드라인 3은 osu! 파일에 **쓰지** 않는 것이므로 읽기는 허용 범위다. 오디오도 복사하지 않고 경로만 그때그때 해결한다.
- 우리가 쓰는 것은 `%LocalAppData%\LazerSR\music\songs.json` 하나뿐이다(비트맵 MD5·오디오 파일 이름/해시·bpm 값들). **절대 경로는 저장하지 않는다.**
- 등록 판정에 쓰는 값(타이밍 포인트·프리뷰 타임·오디오 길이)은 전부 읽기 전용 조회다.

### 단기 실력찾기가 새로 건드리는 것 (전부 읽기 전용)

측정 알고리즘은 `GameplayState.ScoreProcessor.NewJudgement`를 **구독만** 한다. 라이브 `ScoreProcessor`에 쓰지 않으며, 자체 정확도는 `JudgementResult.Type`을 읽어 따로 집계한 값이다. sunnySR pill용 합성 보면은 메모리에서만 만들어지고 realm·파일에 닿지 않는다. 측정 결과(`TrainingProfile`)도 프로세스 메모리에만 있다 — 저장 기능은 미구현이다.

## 리플레이 판정 표시 / 키뷰어 (2026-07-29)

전부 **읽기 전용**이고 서버로 제출되는 값(점수·정확도·판정·리플레이)에 손대지 않는다. 레드라인에 걸리는 것이 없다.

### 그림자 룰셋 — 새로 생긴 유일한 "무거운" 동작

`ManiaJudgementSimulation`은 **두 번째 `DrawableManiaRuleset` 인스턴스를 실제로 세워서 리플레이를 돌린다.** 이 프로젝트에서 실행 중인 게임플레이 세션에 부하를 주는 첫 사례이므로 아래를 지킨다.

- **본 게임과 인스턴스를 공유하지 않는다.** 비트맵은 `GetPlayableBeatmap`으로 따로 변환하고 모드는 `Mod.DeepClone()` 한다. 히트오브젝트/모드를 공유하면 라이브 상태가 오염될 수 있다 — `safety.md`의 "라이브 인스턴스 vs 시뮬레이션 인스턴스" 원칙이 그대로 적용된다.
- **무음이어야 한다.** `ISamplePlaybackDisabler`를 캐시하지 않으면 리플레이 전체 타격음이 몰아서 재생된다.
- **로딩 화면에서만 돌고 끝나면 스스로 폐기된다.** 완료 시 룰셋을 제거·dispose 하므로 게임플레이 중에는 존재하지 않는다.
- **실패해도 게임을 막지 않는다.** 진척이 10초간 없으면 게이트가 풀리고, 표시는 회색으로 degrade될 뿐이다.

### 실제 플레이 중에도 켜지는 것

- **키뷰어(`KeyViewerWidget`)** — osu! 기본 키 오버레이(`KeyCounterDisplay`)를 상속한 것이고 같은 범주다. 입력을 읽기만 한다.
- **`BoxElementPlus`** — osu! 기본 `BoxElement`에 자동 숨김을 더한 것. 시계와 컨트롤포인트를 읽기만 한다.
- **판정 표시 4종 + 노트 숨기기**는 `ReplayPlayer`/`SpectatorPlayer`에서만 부착된다. 직접 플레이 중에는 아예 만들어지지 않는다.

## 결과창 판정 산점도 (2026-07-29)

`ResultsJudgementScatterPatch`는 **읽기 + 목록 concat**뿐이다 (`SkinWidgetRegistrarPatch`와 같은 성격). 이미 확정된 스코어의 `HitEvents`를 읽어 그리기만 하며 스코어·리플레이·realm에 쓰지 않는다. 게임플레이 중에 도는 코드가 아니다. 레드라인에 걸리는 것이 없다.

구간 선택·정확도·sunnySR도 같은 성격이다 — 확정된 스코어와 변환 완료 보면을 읽기만 하고, 구간 임시 보면은 메모리에만 존재한다.

## sunny+ 개인화 diff (2026-08-19)

`PersonalSunnyScoreCollectorPatch`(`Player.ImportScore` Postfix)는 이미 완성된 `Score`/`ScoreInfo`를 읽어 우리 저장소(`%LocalAppData%\LazerSR\personalsunny\`)에 큐잉만 한다 — 점수·리플레이·realm 어디에도 쓰지 않고, 네트워크 호출도 없다. 레드라인에 걸리는 것이 없다.

굽기(`PersonalJacobianBaker`)가 sunny를 23회 돌리는 것은 **별도 인스턴스**(`SunnyManiaDifficultyCalculator`를 매번 `new`)에 `SunnyConstants.WithIsolatedDiff`로 임시 상수만 흘려보내는 것이라, `safety.md`의 "라이브 vs 시뮬레이션 인스턴스" 원칙과 같은 성격 — osu! 라이브 난이도 계산 경로에는 손대지 않는다.

### v2 — broad/narrow 2-pool 재설계 (2026-08-20)

새로 생긴 표면 3곳 전부 레드라인에 안 걸린다:

- **`BeatmapInfo.StarRating` 읽기**(`runBroadPhase`의 무료필터) — realm에 이미 저장된 osu! 자신의 값을 읽기만 한다. 쓰지 않는다(레드라인 4).
- **백그라운드 선제 워커**(`PersonalSunnyService.StartBackgroundWarmup`) — `CollectFromRealmAsync`와 완전히 같은, 이미 안전성 확인된 파이프라인을 트리거만 다르게(위젯 로드 시 자동) 돈다. 새로 하는 일이 없다.
- **`Patches/PersonalSunnyGameplayActivityPatch.cs`**(`Player.LoadComplete`/`OnSuspending`/`OnExiting` Postfix 3개) — `__instance`도 안 읽고, `OnExiting`의 `bool` 리턴값도 안 건드리고, 오직 `PersonalSunnyService.GameplayActive` static bool 하나만 갱신한다. 라이브 인스턴스 접근도 없고 게임플레이 동작에 어떤 영향도 못 준다 — 백그라운드 워커가 CPU 경합을 피하려고 참고하는 신호일 뿐.

`BeatmapManager.QueryBeatmap`/`GetWorkingBeatmap`을 `Parallel.ForEach`로 병렬 호출하는 것도 안전 레드라인과 무관하다(osu! 소스 확인 결과 두 메서드 다 스레드 안전) — 다만 우리 캐시(`PersonalSunnyChartSrStore`/`PersonalSunnyJacStore`/`PersonalSunnyTopPoolStore`)에 자체 락을 걸어 데이터 레이스를 막았다.

## 결과창 구간 연습의 서버 격리 (2026-08-08)

구간 연습은 **완전 로컬 세션**이다. 무한 트레이닝의 차단 5개(위 표)를 그대로 따르되, 진입 경로가 결과창이라 **활동 상태에서 더 강한 성질**을 갖는다.

| 지점 | 차단 |
|---|---|
| `SubmittingPlayer`(제출 토큰, 점수 제출, 관전 브로드캐스트) | **`Player` 직접 상속** — 코드 경로 자체가 없다 |
| `Player.InitialActivity` | `=> null` override (아래 참고) |
| `PlayerLoader.refetchLeaderboard` | `SectionPracticePlayerLoader`가 `ILocalOnlyPlayerLoader`를 구현 → `LocalOnlyLeaderboardSkipPatch`가 생략 |
| `Player.ImportScore` → realm 기록 | `Configuration.ShowResults = false` + `ImportScore` override |
| 결과 화면 | `ShowResults = false`, `CreateResults`는 `NotSupportedException` |
| 리플레이 기록 | `PrepareReplay()` no-op (저장·제출되지 않으므로 기록할 이유가 없다) |

### 활동 상태가 **아예 갱신되지 않는다**

`ResultsScreen`과 `PlayerLoader`의 `InitialActivity`가 원래 `null`이다. 우리 `Player`도 `null`이면 `OsuGame`이 화면 전환 시 `configUserActivity`에 넣는 값이 **바뀌지 않고**, `OnlineMetadataClient`는 그 bindable이 **변할 때만** `UpdateActivity`를 보낸다. 즉 서버 입장에서는 결과창에 그대로 머문 것과 구별되지 않는다.

기본값이 `InSoloGame(비트맵, 룰셋)`이므로 **override를 빼면 "플레이 중"이 전송된다.**

### 구간 임시 보면

`SectionPracticeBeatmap`은 원본 `WorkingBeatmap`을 감싸 **노트만 걸러낸 사본**을 돌려준다. realm·파일에 쓰지 않으며, 히트오브젝트와 `BeatmapInfo`는 읽기 전용으로 공유한다(`Clone()`이 얕은 복사이므로 `HitObjects` 리스트만 새로 만든다 — 원본 오염 방지).

### 트랙 조정을 걷어내는 것에 대하여

`SectionPracticeClockContainer`가 시작 시 트랙의 tempo/frequency 조정을 초기화한다(`architecture.md` §15). **오디오 재생 파라미터일 뿐 점수·정확도·판정·리플레이 어디에도 닿지 않는다.** 라이브 `ScoreProcessor`에 쓰는 코드는 없다.

## 리플레이 저장 서버 연동 (2026-08-31)

`Patches/ReplayAutoUploadPatch.cs`(`Player.ImportScore` Postfix)와 `ReplayUpload/`는 **이미 완성된
`Score`/`ScoreInfo`와 그 `.osr` 파일을 읽어** 큐 폴더(`%LocalAppData%\LazerSR\replayupload\`)에
메타데이터를 쓰는 것뿐이다 — 점수·리플레이·realm 어디에도 쓰지 않고, **네트워크 호출도 없다.**
`PersonalSunnyScoreCollectorPatch`와 완전히 같은 성격(같은 타겟, 같은 "완성된 값만 읽기").

실제 업로드(HTTP)는 **런처**(`LazerSR.Launcher\Replay\ReplayServerClient.cs`)가 한다 — 레드라인 2는
Hook 전용이고, 런처는 이미 자동 업데이트로 GitHub API를 호출한다(§21과 동일 논리). 점수를 조작하지
않고 있는 그대로 아카이빙하므로 "서버 제출값 무결성" 원칙에도 걸리지 않는다.

### lazerSR 리더보드 탭 (2026-08-31, `architecture.md` §23)

선곡 화면 리더보드를 우리 서버 내용으로 대체하는 기능. **네트워크는 여전히 Hook이 안 한다** —
`PipeServer`의 요청/응답으로 런처가 서버 HTTP를 대신 친다. `LeaderboardManager.FetchWithCriteria`를
**Prefix로 `return false`** 하는데, `LocalOnlyLeaderboardSkipPatch`와 같은 성격이다: 목적이 원본
실행 자체를 막는 것이고, **리더보드 표시 상태만 읽고 쓰며 제출되는 점수·정확도·판정·리플레이
어느 값도 읽거나 쓰지 않는다.** 다운로드한 `.osr`은 `ScoreManager.Import`로 realm에 들어가는데
이건 사용자가 osu 리더보드에서 남의 리플레이를 받는 것과 동일한 경로(레드라인 3은 osu! 파일에
**쓰지** 않는 것 — realm 임포트는 osu 자신의 API).

## 향후 리스크 대응

ppy가 hook 탐지 로직을 추가할 가능성은 낮지만 0은 아니다 (`skin-widget-research.md` §14 — 코드 한 줄로 추가 가능하다고 평가됨). 만약 osu! 업데이트 노트나 커뮤니티에서 그런 조치가 확인되면:
1. 즉시 사용 중단 안내
2. `DOTNET_STARTUP_HOOKS` 자체를 비활성화하는 배포로 긴급 패치

## 알려진 미해결 이슈

(2026-07-12 감사 항목이었던 `ManiaDifficultyHitObject` 상속 위험과 `DifficultyIconTooltipPatch` static bool 버그는 2026-07-17에 수정 완료 — 둘 다 안전 레드라인과는 무관한 안정성/기능 이슈였음, `architecture.md` §6 참고.)

## 패턴 복제 모드 (2026-08-21)

무한 트레이닝과 **동일한 완전 로컬 세션**이다. 위 "무한 트레이닝의 서버 격리" 표의 차단 5개를 그대로 따른다 —
`Player` 직접 상속(제출 토큰·점수 제출·관전 브로드캐스트 경로 자체가 없음), `InitialActivity => null`,
`ShowResults = false`, `ImportScore` override, `CreateResults`는 `NotSupportedException`.
노트 런타임 주입에 대한 판단도 무한 트레이닝과 같다(`Playfield.Add`는 일반 public API이고, 이 세션의
스코어는 제출도 realm 기록도 되지 않는다).

### 새로 생긴 것 1 — 외부 프로그램에서 오는 노트 스트림

newScreen이 Named Pipe로 보내온 노트를 주입한다. 받은 값으로 하는 일은 `Playfield.Add`와
`HoldNoteTruncator.Truncate`(우리가 주입한 노트의 `Duration` 수정)뿐이다.
**라이브 `ScoreProcessor`/`HealthProcessor`에는 쓰지 않으며**, 판정은 osu!가 주입된 노트를 정상 처리한 결과다.
파이프는 로컬 IPC이므로 레드라인 2(네트워크)에 해당하지 않는다.

### 새로 생긴 것 2 — 비포커스 입력 릴레이 (`RawKeyRelay`)

**이 프로젝트에서 처음으로 게임플레이에 입력을 넣는 기능**이므로 경계를 분명히 해둔다.

- 넣는 것은 **사용자가 실제로 누른 하드웨어 키**다. Raw Input(`RIDEV_INPUTSINK`)으로 받은 것을
  **전달만** 하며 `SendInput` 같은 합성 입력은 어디에도 쓰지 않는다. 자동 연주가 아니다.
- **대상 게임의 입력 경로는 전혀 건드리지 않는다.** 그 게임이 포커스를 쥐고 원본 입력을 그대로 받으므로,
  그쪽 핵 방지에 걸릴 여지가 구조적으로 없다.
- **패턴 복제 모드의 수명에 정확히 묶여 있다** — `PatternCopyPlayer.OnEntering`에서 시작하고
  `OnExiting`에서 해제하며, 핸들러도 `Host.AvailableInputHandlers`에서 제거한다.
  상시 켜져 있으면 성격이 달라지는 기능이므로 이 경계를 반드시 유지할 것.
- 이 세션의 스코어는 제출되지도 realm에 기록되지도 않는다.

### `HookLog`는 계속 no-op이어야 한다

패턴 복제 디버깅 중 한시적으로 파일 기록으로 바꿨다가 되돌렸다(2026-08-21). 기본은 no-op이다(§`architecture.md` §5).
