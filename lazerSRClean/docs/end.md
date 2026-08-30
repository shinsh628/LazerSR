# 작업 마무리 체크리스트

## 1. 배포

**2026-08-20부터 기본 경로는 GitHub Actions다** (`.github\workflows\build-installer.yml`, PR #2). 로컬 Inno Setup 수동 컴파일은 CI가 없을 때의 폴백으로만 남겨둔다.

### 1a. 기본 경로 — 태그 push (CI가 빌드 + 릴리즈까지 전부 처리)

```powershell
# (권장) .iss MyAppVersion + .csproj <Version>을 새 버전으로 올려 커밋 — 아래 "버전" 참고
git tag -a v{version} -m "v{version}"   # 예: v6.4.1
git push origin v{version}
```

`v*.*.*` 태그 push가 워크플로를 트리거한다. 워크플로가 하는 일:
1. `dotnet publish LazerSR.Launcher\LazerSR.Launcher.csproj -c Release /p:Version=<태그버전>` (버전 주입 — 런처 자동 업데이트가 이 값을 본다)
2. **체크아웃된 워크스페이스 사본**의 `installer\LazerSRClean.iss`에서 `MyAppVersion`/`MyPublishDir`/`OutputDir`를 태그 버전·러너 경로로 치환 — **저장소에 커밋된 `.iss` 원본은 건드리지 않는다.** 즉 태그를 올리기 전에 로컬 `.iss`의 버전을 미리 손으로 바꿔둘 필요가 없다.
3. Inno Setup(ISCC)로 컴파일
4. 인스톨러를 워크플로 아티팩트로 업로드
5. **태그 push로 트리거된 경우에만** GitHub Release를 태그 이름으로 자동 생성하고 exe를 첨부 — 이 Release가 곧 다른 사용자 런처의 자동 업데이트 소스다 (`architecture.md` §21)

진행 상황 확인:
```powershell
gh run list --repo shinsh628/LazerSR --workflow=build-installer.yml --limit 3
```

**버전**: 워크플로의 launcher publish는 `/p:Version=<태그버전>`을 주입하므로, 런처 자동 업데이트가
비교에 쓰는 어셈블리 버전이 곧 태그 버전이 된다 (`architecture.md` §21). CI 산출물만 놓고 보면 태그 push
전에 `.iss`/`.csproj`를 손볼 필요가 없지만, **로컬 폴백 빌드(1b)의 런처가 자기 버전을 정직하게 보고하려면**
`.iss`의 `MyAppVersion`과 `.csproj`의 `<Version>`을 새 버전으로 같이 올려 커밋하는 것이 권장 절차다.
(예: v6.7.0 릴리즈 때 둘 다 6.7.0으로 올려 커밋 → 태그.)

버전 번호만 바꿔 수동으로 다시 돌리고 싶으면(태그 없이) `workflow_dispatch`로도 실행 가능 — GitHub Actions 탭에서 "Run workflow" + 버전 입력, 또는 `gh workflow run build-installer.yml -f version=6.4.1`. 기본값(`create_release` 체크 해제)에서는 release를 만들지 않고 아티팩트만 나온다.

### 1c. 로컬 git이 없을 때 — `workflow_dispatch` + `create_release`

**태그를 직접 push할 수 없는 상황**(다른 PC의 웹 세션, 태그 push 권한이 없는 자동화 등)에서 쓴다.
"Run workflow"에서 버전을 넣고 **`create_release`를 체크**하면 된다.

```powershell
gh workflow run build-installer.yml -f version=6.5.3 -f create_release=true --ref <브랜치>
```

`gh release create`는 **태그가 없으면 `--target` 커밋에 서버에서 직접 만들어 준다.** 워크플로의
`GITHUB_TOKEN`이 `contents: write`를 갖고 있어 가능한 경로다. 태그는 **그 실행이 체크아웃한 커밋**에
붙으므로, `--ref`로 어느 브랜치를 돌렸는지가 곧 릴리즈 대상이다.

> **주의**: GitHub 웹 UI의 "Draft a new release"로 태그를 만들지 말 것. 릴리즈가 먼저 생기면
> 태그 push 이벤트로 돌아온 워크플로의 `gh release create`가 중복으로 실패해 **exe가 첨부되지 않는다.**

**주의**: `installer\LazerSRClean.iss`의 `AppId`는 절대 바꾸지 않는다 (업그레이드 인식용, 최초 LazerSR과 동일 유지). `[Files]` 목록에 새 의존 DLL을 추가했다면(`architecture.md` §8) `.iss`의 `[Files]` 섹션도 같이 갱신하고 커밋할 것 — 이건 워크플로가 대신 해주지 않는다.

### 1b. 폴백 — 로컬 수동 빌드 (CI 없이 확인만 하고 싶을 때)

```powershell
dotnet publish "LazerSR.Launcher\LazerSR.Launcher.csproj" -c Release
```

출력: `LazerSR.Launcher\bin\Release\net8.0-windows\win-x64\publish\`

1. `installer\LazerSRClean.iss` 2번째 줄 `MyAppVersion` 값을 올린다 (예: `4.1.0` → `4.2.0`) — **이 로컬 수정은 커밋해도 되고 안 해도 된다**, 어차피 1a 경로는 이 파일의 커밋된 값을 안 쓴다.
2. Inno Setup Compiler(ISCC)로 컴파일 — 설치 위치: `C:\Users\shins\AppData\Local\Programs\Inno Setup 6\ISCC.exe` (PATH에 없음, 매번 전체 경로로 호출):
   ```powershell
   & "C:\Users\shins\AppData\Local\Programs\Inno Setup 6\ISCC.exe" "C:\dev\lazerSR\lazerSRClean\installer\LazerSRClean.iss"
   ```
3. 결과물: `installer\output\LazerSR-v{version}-Setup.exe`

## 2. progress 로그 작성

`progress\YYYY-MM-DD.md` (오늘 날짜, 없으면 생성)에 간결하게 추가:

```
- ~~무엇~~ 구현/생성/수정/삭제
```

설명은 최소화 — 작업 흐름만 남긴다. (`CLAUDE.md`의 로그 규칙과 동일, 여기 다시 요약된 것뿐)

## 3. 살아있는 문서 갱신 확인

아래 문서들은 **코드/진행상황을 서술**하므로 세션 중 관련 변경이 있었으면 반드시 갱신 여부를 확인한다 — 안 하면 문서가 실제와 어긋나는 문제가 반복된다(2026-07-17 전면 감사의 원인이 바로 이거였음):

| 변경 종류 | 확인할 문서 |
|---|---|
| 패치 추가/제거, 아키텍처 변경 | `docs\guides\architecture.md`, `docs\guides\ui-patching.md` |
| 안전성에 영향 줄 수 있는 변경(네트워크, 파일쓰기, 라이브 인스턴스 접근 등) | `docs\guides\safety.md` |
| 새 기능/계산기/위젯 패턴 추가 | `docs\guides\feature-development.md` |
| 폴더/파일 구조 변경 | `docs\INDEX.md` |
| 새 위치에 sunnySR pill 추가 | `docs\sibling-clone-position-map.md` (상태 표 갱신) |
| replay-compare 관련 작업 | `docs\replay-compare-widget-plan.md` |

## 4. 대기

각 작업 단위 완료 후 사용자 명령 대기 (`CLAUDE.md` 워크플로 규칙).
