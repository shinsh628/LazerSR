# 경로 색인

`lazerSRClean` 뿐 아니라 `C:\dev\lazerSR` 잔여 경로까지 전부 포함. 상태 태그 의미: **삭제금지**(빌드/참조 의존) · **보존**(참고용, 삭제 안 함) · **관리대상**(개발 중 계속 갱신됨).

---

## `C:\dev\lazerSR\` (레거시 보관소, Claude는 여기서 실행 안 함)

| 경로 | 설명 | 상태 |
|---|---|---|
| `CLAUDE.md` | 축소된 포인터 stub — 실제 규칙은 `lazerSRClean\CLAUDE.md` | 유지 |
| `backup\1.1-strategy-a-original\` | 1.1 pill의 in-place reflection 방식 원본 코드 | 보존 |
| `backup\sunny-v2-removed-2026-05-16\` | lazerSRClean 시작 시 폐기된 구 `LazerSR\` v2 시도(Dan/Graph/MSD/스킨위젯/클론) | 보존 |
| `LazerSR\` | 기능 테스트 참고 환경 — lazerSRClean과 별개로 유지되는 실험용 프로젝트 | 보존 |
| `osu\` | osu!lazer 소스 (ppy-osu-1164870 기준, 2026-07-17 갱신) — 컴파일타임 `ProjectReference` 대상 | **삭제금지** |
| `sunnyosu\` | sunny 리워크 mania 계산기 원본 fork — 이식은 이미 끝나서 **현재 어떤 `.csproj`도 참조 안 함**(2026-08-19 grep으로 확인, `sunnyosu\`가 없어도 빌드 됨). 과거 이식 근거 자료로 보존 | 보존 |
| `enissayosu\` | 용도 미상 — `.csproj`/`.cs`/`.iss` 어디서도 참조 안 됨(2026-08-19 확인). git에는 안 올라감(`.gitignore`) | 확인 필요 |
| `minacalc\` | MinaCalc 원본(Rust) clone — 실제 쓰는 건 `LazerSR.Hook\MinaCalc.dll`(사전 컴파일된 바이너리)뿐, 이 소스는 빌드에 안 쓰임. git에는 안 올라감(`.gitignore`) | 보존 (미사용) |

---

## `C:\dev\lazerSR\lazerSRClean\` (실제 개발 폴더, Claude는 여기서만 실행)

| 경로 | 설명 | 상태 |
|---|---|---|
| `CLAUDE.md` | 세션마다 자동 로드 — 최소 규칙 + 문서 포인터만 | **관리대상** |
| `docs\INDEX.md` | 이 문서 | **관리대상** |
| `docs\start.md` | 세션 시작 체크리스트 | 관리대상 |
| `docs\end.md` | 배포 절차 + 작업 마무리 체크리스트 | 관리대상 |
| `docs\guides\architecture.md` | Hook 부트스트랩/패치/IPC 실제 아키텍처 | **관리대상** |
| `docs\guides\safety.md` | 안전 레드라인 (서버 제출값 기준) | **관리대상** |
| `docs\guides\feature-development.md` | 새 기능(sibling pill / 스킨 위젯) 추가 절차 | **관리대상** |
| `docs\guides\ui-patching.md` | HarmonyX 패치 보일러플레이트 + 함정 목록 | **관리대상** |
| `docs\sunnySR-pill-implementation-guide.md` | 1.1 검증된 pill 삽입 코드 (신규 위치 추가 시 필독) | 관리대상 |
| `docs\sibling-clone-position-map.md` | 위치별(1.1~4.2) sunnySR pill 구현 진행 상황 | **관리대상** |
| `docs\replay-compare-widget-plan.md` | 리플레이 비교 위젯 미니 프로젝트 현황 | 관리대상 |
| `docs\skin-widget-research.md` | osu! 스킨 위젯 시스템 API 레퍼런스 | 보존 (osu! API 자체 서술, 우리 코드 변경과 무관) |
| `docs\SunnyToDan.txt` | `DanCalculator.cs`의 DAN_NAMES/DAN_THRESHOLDS 원본 출처 데이터 | 보존 |
| `docs\widget-mockup.html` | `ReplayCompareWidget` 설정값 시뮬레이션용 HTML 모업 | 보존 |
| `docs\mockup.json` | `ReplayCompareWidget` 기본값 정의 | 보존 |
| `progress\` | 날짜별 작업 일지 (`YYYY-MM-DD.md`) — 실제 작업의 유일한 확정 기록 | **관리대상**, 계속 추가됨 |
| `installer\LazerSRClean.iss` | Inno Setup 스크립트, `AppId` 고정, 버전은 여기서 관리 | 관리대상 |
| `installer\output\` | 컴파일된 `LazerSR-v{version}-Setup.exe` | 재생성 가능 (커밋 대상 아님) |
| `LazerSR.Hook\` | osu! 프로세스에 주입되는 Class Library (net8.0) | **관리대상** |
| `LazerSR.Hook\Patches\` | HarmonyX 패치 클래스들 — 목록은 `architecture.md` §4 | 관리대상 |
| `LazerSR.Hook\Calculators\` | sunnySR/MSD/Dan/replay-timeline/결과창 구간 분석(`ManiaSectionAnalysis`) 등 계산 로직 | 관리대상 |
| `LazerSR.Hook\Screens\` | 무한 트레이닝 화면/시드 비트맵 + 결과창 구간 연습(`SectionPractice*`) + **패턴 복제 화면/시드 비트맵(`PatternCopy*`)** — 전부 `OsuScreen`/`Player`/`WorkingBeatmap` 파생. 로컬 전용 로더 마커 `ILocalOnlyPlayerLoader` 포함 | 관리대상 |
| `LazerSR.Hook\PatternCopy\` | **패턴 복제 모드** — 외부 프로그램(newScreen)이 파이프로 보내온 노트를 실시간 주입. 명령 큐(`PatternCopyBridge`)·주입기·**롱노트 런타임 절단**(`HoldNoteTruncator`)·세션 상태 + **비포커스 프레임 유지**(`InactiveFrameRateOverride`). `architecture.md` §19 | **관리대상** (2026-08-21 신규) |
| `LazerSR.Hook\Input\` | 비포커스 상태에서 하드웨어 키를 받아 프레임워크 입력 큐에 넣는 릴레이(Raw Input `RIDEV_INPUTSINK`). **패턴 복제 모드 전용이며 그 화면의 수명에 묶여 있다** — `safety.md` 참고 | **관리대상** (2026-08-21 신규) |
| `LazerSR.Hook\Training\` | 무한 트레이닝 — 패턴 생성 파이프라인(생성기/마디 큐/주입기), 단기 실력찾기(`ShortTermSearch`), 정확도 집계, 프로필, 배경음(`MusicLibrary`/`TrainingMusicPlayer`/`TrainingMusicStore`/`BeatmapMusicAnalysis`), 무한 세션(`InfiniteSession`). 격자 절대규칙은 `TrainingGrid`, 패턴 정의·상수는 `PatternCatalog` | **관리대상** |
| `LazerSR.Hook\Training\Patterns\` | 세부패턴별 생성 규칙 (`IPatternGenerator` 구현체) + 공용 추첨 도구 `PatternPicker` | 관리대상 |
| `LazerSR.Hook\Widgets\` | `ISerialisableDrawable` 스킨 위젯 구현체. 키뷰어(`KeyViewerWidget`/`KeyViewerKey`)와 `BoxElementPlus`는 osu! 본체 위젯을 상속한 것 — `architecture.md` §10/§11. 실시간 sunny(`RealtimeSunnyWidget`, 앞 400ms 구간 난이도, §20) | 관리대상 |
| `LazerSR.Hook\Drawables\` | 커스텀 drawable — StrainAreaGraph, MsdBarChart, ManiaJudgementLineOverlay/ManiaPressOverlay/ManiaJudgementSimulation(리플레이 판정 표시, `architecture.md` §9), ManiaJudgementScatterGraph(결과창 판정 산점도 + 구간 선택·연습, §12) | 관리대상 |
| `LazerSR.Hook\Data\` | 불변 record 데이터 타입 + 화면 간 전달용 상태 슬롯(`ManiaSimulationState`, `ManiaOverlayVisibility` 등) | 관리대상 |
| `LazerSR.Hook\PersonalSunny\` | 개인화 diff 파이프라인 — 큐/J캐시/적합결과 저장소, 모드 화이트리스트, `PersonalSunnyService`(굽기+적합 오케스트레이터), `Player.ImportScore` 자동 수집 패치. `architecture.md` §17 | **관리대상** (2026-08-19 신규) |
| `LazerSR.Hook\LazerSrStorage.cs` | 개인 저장소(`%LocalAppData%\LazerSR\`) 경로/원자적 쓰기 유틸. 네임스페이스는 루트 고정(`architecture.md` §16) | 관리대상 |
| `LazerSR.Hook\Ipc\PipeServer.cs` | Named Pipe 서버 (`sunny:on/off` + `replaycollect:scan` + ad-hoc 브로드캐스트) | 관리대상 |
| `LazerSR.Hook\ReplayUpload\` | 리플레이 저장 서버 연동 (Hook 측) — realm 스캔·`.osr` 헤더 파서·큐 작성. 업로드는 안 함. `architecture.md` §22 | **관리대상** (2026-08-31 신규) |
| `LazerSR.Hook\Patches\ReplayAutoUploadPatch.cs` | 매 실제 게임 종료 후 리플레이를 큐에 넣는 `Player.ImportScore` Postfix (§22) | 관리대상 (2026-08-31 신규) |
| `LazerSR.Hook\LazerSrLeaderboard\` | 선곡 화면 "lazerSR" 리더보드 탭 — 상태·서버 JSON→ScoreInfo 변환·리플레이 감상. `architecture.md` §23 | **관리대상** (2026-08-31 신규) |
| `LazerSR.Hook\Patches\LazerSrLeaderboard*Patch.cs` | 토글 버튼 삽입 / `FetchWithCriteria` 가로채기 / 다운로드 버튼 래핑 (§23) | 관리대상 (2026-08-31 신규) |
| `LazerSR.Launcher\` | WPF 런처 EXE — osu! 실행 + Pipe 클라이언트 | **관리대상** |
| `LazerSR.Launcher\Configuration\` | 설치 경로/설정 저장 | 관리대상 |
| `LazerSR.Launcher\Update\` | GitHub Releases 기반 자동 업데이트 검사·다운로드 (`architecture.md` §21) | **관리대상** (2026-08-30 신규) |
| `LazerSR.Launcher\Replay\` | 리플레이 저장 서버 클라이언트 — 큐 드레인·multipart 업로드·개수 조회. 에러 그대로 노출 (`architecture.md` §22) | **관리대상** (2026-08-31 신규) |
| `LazerSR.SunnyCalculator\` | 독립 sunnySR 계산 파이프라인 (osu! `DifficultyCalculator` 비상속) | **관리대상** |
| `LazerSR.SunnyCalculator\Difficulty\` | sunnyosu에서 이식된 skill/evaluator/preprocessor | 관리대상 |
| `LazerSR.SunnyCalculator\Tuning\` | sunny 상수 39개 + 만인/개인화 diff + `WithIsolatedDiff` 격리 계층 + 개인화 fit 솔버/굽기. `architecture.md` §17 | **관리대상** (2026-08-19 확장) |

---

## 빌드 의존 관계 요약

```
LazerSR.Launcher.csproj → ProjectReference → LazerSR.Hook.csproj
LazerSR.Hook.csproj      → ProjectReference → LazerSR.SunnyCalculator.csproj
                          → ProjectReference(Private=false) → ..\..\osu\osu.Game\osu.Game.csproj
                                                              → ..\..\osu\osu.Game.Rulesets.Mania\...csproj
LazerSR.SunnyCalculator.csproj → ProjectReference(Private=false) → 위와 동일 osu 경로
```

`osu\`가 없으면 `lazerSRClean` 전체가 빌드되지 않는다 — 절대 삭제 금지. `sunnyosu\`는 위 그래프 어디에도 안 걸린다(2026-08-19 확인) — 없어도 빌드된다, 삭제해도 무방하지만 과거 이식 근거로 보존 중.
