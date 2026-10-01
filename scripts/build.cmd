@echo off
setlocal
set "PS=pwsh"
where pwsh >nul 2>nul || set "PS=powershell"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "RC=%ERRORLEVEL%"
rem A failed build always waits for a key. A good one waits only when Explorer started this file - a
rem double click, whose window closes the moment it ends, and the end of a successful build carries
rem warnings such as another CodeSwitchX left running. Started from PowerShell, Git Bash, a task or
rem a CI step it does not wait: the output stays on screen, or nobody is there to press a key.
rem CODESWITCHX_BUILD_PAUSE=never switches the wait off altogether, =always forces it.
set "WAIT="
if not "%RC%"=="0" set "WAIT=1"
if not defined WAIT "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0_started-by-explorer.ps1" && set "WAIT=1"
if /i "%CODESWITCHX_BUILD_PAUSE%"=="always" set "WAIT=1"
if /i "%CODESWITCHX_BUILD_PAUSE%"=="never" set "WAIT="
rem Its own line, not pause's: pause prints in the Windows display language.
if defined WAIT (
    echo Press any key to continue . . .
    pause >nul
)
exit /b %RC%
