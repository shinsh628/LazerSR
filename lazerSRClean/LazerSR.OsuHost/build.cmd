@echo off
rem Builds LazerSR.OsuHost\bin\osu!.exe with MSVC. Called by LazerSR.Launcher.csproj (BuildOsuHost target).
rem No delayed expansion: the "!" in osu!.exe would be eaten.
rem Requires Visual Studio / Build Tools with the C++ workload (GitHub windows runners have it).
setlocal

set "HERE=%~dp0"
set "OUT=%HERE%bin"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"

if not exist "%VSWHERE%" (
    echo build.cmd: error: vswhere.exe not found - install Visual Studio Build Tools with the C++ workload.
    exit /b 1
)

set "VSDIR="
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if "%VSDIR%"=="" (
    echo build.cmd: error: no Visual Studio installation with the C++ x64 toolset found.
    exit /b 1
)

call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>nul || exit /b 1

if not exist "%OUT%" mkdir "%OUT%"
pushd "%OUT%"

rc /nologo /fo host.res "%HERE%host.rc" || (popd & exit /b 1)

rem /MT: static CRT, no VC++ redistributable needed.
rem /CETCOMPAT:NO: match osu!.exe, which ships with CETCompat=false since 2026.920.0-lazer.
cl /nologo /utf-8 /O2 /W4 /WX /MT /DUNICODE /D_UNICODE "%HERE%host.c" host.res /Fe:"osu!.exe" ^
    /link /SUBSYSTEM:WINDOWS /CETCOMPAT:NO /MANIFEST:EMBED /MANIFESTINPUT:"%HERE%app.manifest" ^
    user32.lib shell32.lib || (popd & exit /b 1)

del /q host.obj host.res 2>nul
popd
exit /b 0
