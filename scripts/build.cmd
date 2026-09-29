@echo off
setlocal
set "PS=pwsh"
where pwsh >nul 2>nul || set "PS=powershell"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "RC=%ERRORLEVEL%"
pause
exit /b %RC%
