<#
.SYNOPSIS
    Closes the stable build of CodeSwitchX.

.DESCRIPTION
    Closes CodeSwitchX the way its own window does, so it hands every hosted VS Code window back to
    the desktop. Only instances started from the install root are closed; a Debug build from the repo
    is reported, never touched.

.PARAMETER InstallDir
    Close only the build running from this folder. By default every version running from the
    install root is closed, whichever folder it was started from.

.PARAMETER InstallRoot
    Where build.ps1 put the versioned folders. Defaults to E:\StableVersion.

.EXAMPLE
    .\scripts\stop.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [string]$InstallRoot
)

. (Join-Path $PSScriptRoot '_common.ps1')

if (-not $InstallRoot) { $InstallRoot = $DefaultInstallRoot }
$InstallRoot = Resolve-FullPath $InstallRoot

Write-Host ""
Write-Host "  CodeSwitchX - stopping" -ForegroundColor White

if ($InstallDir) {
    $InstallDir = Resolve-FullPath $InstallDir
    Write-Note "install  $InstallDir"
    $running = Get-AppProcess $InstallDir
}
else {
    Write-Note "root     $InstallRoot  (every version)"
    $running = Get-AppProcess -AnyVersion -InstallRoot $InstallRoot
}

foreach ($other in (Get-OtherAppProcess -InstallRoot $InstallRoot)) {
    if ($running | Where-Object { $_.ProcessId -eq $other.ProcessId }) { continue }
    Write-Warn "CodeSwitchX from $($other.ExecutablePath) (PID $($other.ProcessId)) - left alone"
}

if ($running.Count -eq 0) {
    if ($InstallDir) { Write-Ok "CodeSwitchX is not running from $InstallDir" }
    else             { Write-Ok "the stable build is not running" }
    Write-Host ""
    exit 0
}

foreach ($proc in $running) {
    Write-Step "Closing PID $($proc.ProcessId) ($($proc.ExecutablePath))"
    if (Stop-AppProcess $proc) { Write-Ok "closed" }
    else {
        Fail "PID $($proc.ProcessId) did not close." @(
            "A dialog may be open in it. Close it there, or end it from Task Manager -",
            "the next start of CodeSwitchX shows any VS Code window a killed one left hidden."
        )
    }
}

Write-Host ""
Write-Host "  Stopped." -ForegroundColor Green
Write-Host ""
