@echo off
setlocal
set "PS=pwsh"
where pwsh >nul 2>nul || set "PS=powershell"
rem Double-clicked, this runs as cmd /c "<this file>" in a window that closes the moment it ends, so
rem it waits for a key whatever the result: the end of a successful build carries warnings, such as
rem another CodeSwitchX left running. Typed into an open console, it only waits on a failure.
rem find.exe by full path: Git's Unix find comes first on some PATHs and takes other arguments.
set "OWNWINDOW="
echo %cmdcmdline% | "%SystemRoot%\System32\find.exe" /i "%~0" >nul && set "OWNWINDOW=1"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" set "OWNWINDOW=1"
if defined OWNWINDOW pause
exit /b %RC%
