@echo off
setlocal
set "PS=pwsh"
where pwsh >nul 2>nul || set "PS=powershell"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0stop.ps1" %*
set "RC=%ERRORLEVEL%"
rem Unlike build.cmd, a double-clicked stop does not wait after a success: all it has to say is that
rem CodeSwitchX closed, and its window going away says the same.
if not "%RC%"=="0" pause
exit /b %RC%
