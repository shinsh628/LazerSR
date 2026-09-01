# LazerSR Clean — 배포용 클린 버전

## 세션 시작
새 세션 시작 시 `docs\start.md`를 읽고 거기 나열된 문서들을 순서대로 읽는다.

## 작업 마무리
작업 단위가 끝나면 `docs\end.md`를 따른다 (배포 절차 + progress 로그 작성 + 관련 문서 갱신 확인).

## 위치
- 작업 폴더(배포용, Claude는 여기서만 실행): `C:\dev\lazerSR\lazerSRClean\`
- 전체 경로 색인: `docs\INDEX.md`

## 설계 원칙 (사용자 명시)
1. 쓸데없는 파일/코드 최소화
2. 각 기능 독립적, 상호 연관 최소
3. 공통 코드(sunnySR 계산기 등)는 하나의 함수로 만들어 여러 곳에서 참조
4. 기존 osu 기능과 유사한 것을 추가할 때는 로직을 새로 짜지 말고, **osu 원본 코드를 그대로 우리 어셈블리에 복붙한 뒤 최소 수정 + hook**한다 (코드 레벨 버그 방지 목적). **`osu\` 원본 파일 자체는 절대 수정 금지** — 읽기 전용 참조이고, 복붙 사본은 우리 프로젝트 안에서만 산다
5. `backup\`의 검증된 코드를 토대로 개발

## 하드 제약 (요약, 상세는 `docs\guides\architecture.md`)
- `StartupHook`은 global namespace의 `public class StartupHook`, `public static void Initialize()` 시그니처 고정
- 부트스트랩 순서: `DependencyResolver.Install` → `PipeServer.StartBackground` → `Patcher.Apply`
- Harmony 인스턴스는 `HookBootstrap.HarmonyId` 하나만 재사용
- 환경변수는 **자식 osu! 프로세스에만** 설정 — 전역/유저 환경변수 수정 절대 금지
- `osu\`, `sunnyosu\` 삭제 금지 (빌드 의존, `docs\INDEX.md` 참고). **`osu\` 파일 수정도 금지** — 원본 0줄 수정이 레드라인. UI 개입은 전부 HarmonyX 패치 + 클론으로 해결한다

## 안전 레드라인 (요약, 상세는 `docs\guides\safety.md`)
서버로 제출되는 값(점수/정확도/판정/리플레이)을 실제로 조작하지 않는 한 대부분 허용된다. osu!가 실제 게임플레이에 쓰는 라이브 인스턴스(`Player`의 실제 `ScoreProcessor` 등)에는 절대 쓰지 않는다. 네트워크 호출 금지, osu! 파일 직접 쓰기 금지.

## 워크플로
각 작업 단위 완료 후 중지하고 사용자 명령 대기.
