#define MyAppName "LazerSR"
#define MyAppVersion "6.10.0"
#define MyAppExeName "LazerSR.Launcher.exe"
#define MyPublishDir "C:\dev\lazerSR\lazerSRClean\LazerSR.Launcher\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
; AppId는 기존 LazerSR과 동일 — 업그레이드로 인식되려면 반드시 같아야 함
AppId={{6F8A2B3C-4D5E-4F60-8A1B-2C3D4E5F6A7B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher=LazerSR
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=C:\dev\lazerSR\lazerSRClean\installer\output
OutputBaseFilename={#MyAppName}-v{#MyAppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; 관리자 권한 불필요 — localappdata에 설치
PrivilegesRequired=lowest
; 설치 전 실행 중인 LazerSR 자동 종료
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 바로 가기 만들기"; GroupDescription: "추가 작업:"

[Files]
; 메인 실행 파일 (SingleFile 번들, .NET 8 런타임 포함)
Source: "{#MyPublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

; Hook DLL — DOTNET_STARTUP_HOOKS 실제 파일 경로 필요 (번들 외부 필수)
Source: "{#MyPublishDir}\LazerSR.Hook.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\LazerSR.SunnyCalculator.dll"; DestDir: "{app}"; Flags: ignoreversion

; HarmonyX 런타임 — DependencyResolver가 exe 폴더에서 로드
Source: "{#MyPublishDir}\0Harmony.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\MonoMod.Backports.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\MonoMod.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\MonoMod.Iced.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\MonoMod.ILHelpers.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\MonoMod.RuntimeDetour.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\MonoMod.Utils.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\Mono.Cecil.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\Mono.Cecil.Mdb.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\Mono.Cecil.Pdb.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\Mono.Cecil.Rocks.dll"; DestDir: "{app}"; Flags: ignoreversion

; MinaCalc 네이티브 DLL — P/Invoke로 로드 (Hook.dll과 같은 폴더 필수)
Source: "{#MyPublishDir}\MinaCalc.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} 제거"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppName} 실행"; Flags: nowait postinstall skipifsilent

[Code]
// 이전 버전(LazerSR v2 포함) 감지 시 자동 제거 후 재설치
function GetUninstallString(): String;
var
  sUnInstPath: String;
  sUnInstallString: String;
begin
  sUnInstPath := ExpandConstant('Software\Microsoft\Windows\CurrentVersion\Uninstall\{#emit SetupSetting("AppId")}_is1');
  sUnInstallString := '';
  if not RegQueryStringValue(HKCU, sUnInstPath, 'UninstallString', sUnInstallString) then
    RegQueryStringValue(HKLM, sUnInstPath, 'UninstallString', sUnInstallString);
  Result := sUnInstallString;
end;

function IsUpgrade(): Boolean;
begin
  Result := (GetUninstallString() <> '');
end;

function UnInstallOldVersion(): Integer;
var
  sUnInstallString: String;
  iResultCode: Integer;
begin
  Result := 0;
  sUnInstallString := GetUninstallString();
  if sUnInstallString <> '' then begin
    sUnInstallString := RemoveQuotes(sUnInstallString);
    if Exec(sUnInstallString, '/SILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_HIDE, ewWaitUntilTerminated, iResultCode) then
      Result := 3
    else
      Result := 2;
  end else
    Result := 1;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssInstall) then begin
    if (IsUpgrade()) then
      UnInstallOldVersion();
  end;
end;
