@echo off
rem LazerSR diagnostic run - traces the .NET host (before any managed code runs) in case the launcher dies silently.
rem The variables below apply only to processes started from this window; no system/user env var is changed.
rem ASCII only on purpose: cmd reads .cmd files in the OEM code page.
set "COREHOST_TRACE=1"
set "COREHOST_TRACE_VERBOSITY=4"
set "COREHOST_TRACEFILE=%LOCALAPPDATA%\LazerSR\host-trace.log"
if exist "%COREHOST_TRACEFILE%" del "%COREHOST_TRACEFILE%"
echo Starting LazerSR launcher in diagnostic mode...
"%~dp0LazerSR.Launcher.exe"
echo.
echo Exit code: %ERRORLEVEL%
echo Logs: %LOCALAPPDATA%\LazerSR\launcher.log
echo       %LOCALAPPDATA%\LazerSR\host-trace.log
pause
