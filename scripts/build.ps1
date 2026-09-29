<#
.SYNOPSIS
    Builds CodeSwitchX in Release and installs it as the next stable version.

.DESCRIPTION
    Bumps the version, publishes the app (with relay\csx-hook.exe beside it) into a folder named after
    it under E:\StableVersion (CodeSwitchX-0.1.0.3), points E:\StableVersion\CodeSwitchX at it - the
    fixed path the Start Menu entry and any taskbar pin open - and then commits the bump and pushes
    main. Older versions stay beside the new one; delete them by hand when you like.

    A running stable CodeSwitchX is closed first, the way its own window closes, so it hands its
    hosted VS Code windows back to the desktop. CodeSwitchX runs one instance per user, so the old
    one would otherwise keep answering every start. If it was running, the new version is started
    once the build is done.

    That only happens from a clean main that is level with origin. On any other branch the script
    stops before building anything; give it -InstallDir for a build that goes somewhere else.

.PARAMETER Part
    Which part of the version to increase: major, minor, patch or build. Defaults to build, the
    everyday "another build of the same release" case: 0.1.0 -> 0.1.0.1 -> 0.1.0.2. A release
    bump zeroes everything to its right and drops the fourth part: 0.1.0.2 -Part minor -> 0.2.0.

.PARAMETER InstallRoot
    Where the versioned folders and the current link go. Defaults to E:\StableVersion. stop.ps1
    takes the same switch.

.PARAMETER InstallDir
    A folder of your own for the build instead of a versioned one. Nothing is bumped, committed
    or pushed, and any branch is fine - this is the way to try a feature branch's build. The
    build is stamped <current version>-oneoff.<commit> so it cannot be mistaken for a stable one.
    Must be outside the repo and outside the stable root, and must be empty or a folder this
    script published into before - a folder holding anything else is refused untouched.

.PARAMETER Clean
    With -InstallDir: wipe that folder before publishing. Only ever a folder carrying our install
    marker. A versioned folder always starts empty, so the switch has nothing to do there.

.PARAMETER NoShortcut
    Skip the Start Menu shortcut. For a test publish somewhere other than the real install.

.EXAMPLE
    .\scripts\build.ps1
    The next stable version: bump, publish, commit, push.

.EXAMPLE
    .\scripts\build.ps1 -Part minor
    0.1.0.4 -> 0.2.0, for an actual release rather than another build of one.

.EXAMPLE
    .\scripts\build.ps1 -InstallDir E:\Builds\CodeSwitchX-test -NoShortcut
    Try this branch's build without touching the stable versions or the repo.
#>
[CmdletBinding()]
param(
    [ValidateSet('major', 'minor', 'patch', 'build')]
    [string]$Part = 'build',
    [string]$InstallRoot,
    [string]$InstallDir,
    [switch]$Clean,
    [switch]$NoShortcut
)

. (Join-Path $PSScriptRoot '_common.ps1')

if (-not $InstallRoot) { $InstallRoot = $DefaultInstallRoot }
$InstallRoot = Resolve-FullPath $InstallRoot

# Two ways in: no -InstallDir means the next stable version, with everything that entails; an
# -InstallDir is a one-off build that leaves the repo alone.
$stable = -not $InstallDir
if (-not $stable) { $InstallDir = Resolve-FullPath $InstallDir }

Write-Host ""
Write-Host "  CodeSwitchX - Release build" -ForegroundColor White
if ($stable) { Write-Note "root     $InstallRoot  (next stable version)" }
else         { Write-Note "install  $InstallDir  (one-off build, no version bump)" }

# --- preflight ---------------------------------------------------------------------------
Write-Step "Checking prerequisites"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail "The .NET SDK is not on your PATH." @("Install the SDK global.json asks for from https://dotnet.microsoft.com/download")
}
Write-Ok ".NET SDK $(dotnet --version)"

if (-not (Test-Path -LiteralPath $UiProject)) {
    Fail "Cannot find $UiProject." @("Run this script from a clone of the CodeSwitchX repo.")
}

$current = Get-PropsVersion
$version = ''
if ($stable) {
    # Branch, clean tree and origin are checked before a single file is built, so a wrong branch
    # costs seconds, not a publish.
    Assert-ReleaseReady
    $version = Get-NextVersion -Current $current -Part $Part
    $InstallDir = Get-VersionedInstallDir -InstallRoot $InstallRoot -Version $version
    Write-Ok "version $current -> $version"
    Write-Note "install  $InstallDir"
    Write-Note "Directory.Build.props is bumped, committed and pushed only if the publish succeeds"
    if ($Clean) { Write-Warn "-Clean has nothing to do here: a versioned folder always starts empty" }
}
else {
    # Stamped so Explorer's Product version or a log line can never pass this off as the stable build
    # of the same number. FileVersion stays numeric, as Windows requires.
    $commit = "$(git -C $RepoRoot rev-parse --short HEAD 2>$null)".Trim()
    if (-not $commit) { $commit = 'nogit' }
    $version = "$current-oneoff.$commit"
    Write-Ok "version $version"
}

# An install folder inside the repo gets swept up by the next build, and each build then copies the
# previous build's output into itself.
if (Test-PathUnder $InstallDir $RepoRoot) {
    Fail "-InstallDir must be outside the repo ($RepoRoot)." @(
        "Publishing into the repo makes every later build copy the last build into itself.",
        "Leave -InstallDir unset to build the next stable version under $InstallRoot."
    )
}

# The stable root is for versioned builds from main only. A hand-picked folder in there would be
# a "stable version" that skipped every check above - and -InstallRoot must not be a way round
# that, so the default root is checked as well as the one given.
if (-not $stable -and ((Test-PathUnder $InstallDir $InstallRoot -OrEqual) -or (Test-PathUnder $InstallDir $DefaultInstallRoot -OrEqual))) {
    Fail "-InstallDir must be outside the stable root ($InstallRoot)." @(
        "That folder holds the stable versions, which only build.ps1 without -InstallDir makes, from main.",
        "Put a one-off build somewhere else, for example:",
        "  .\scripts\build.ps1 -InstallDir 'E:\Builds\CodeSwitchX-test' -NoShortcut"
    )
}

# The install root is shared with other releases (E:\StableVersion holds RawCutX, ContentAutomatorX
# and friends). A fresh versioned build deletes its folder first, so we only ever work in a folder
# that is empty or already carries our install marker.
if ((Test-DirectoryHasContent $InstallDir) -and -not (Test-OurInstall $InstallDir)) {
    Fail "$InstallDir already holds files that are not a CodeSwitchX install." @(
        "Not one of them was touched.",
        "Move that folder out of the way, or give the build a folder of its own:",
        "  .\scripts\build.ps1 -InstallDir 'E:\Builds\CodeSwitchX-test' -NoShortcut"
    )
}

# --- close the running app ---------------------------------------------------------------
# A stable build closes every version running from the root: CodeSwitchX is one instance per user,
# so a start of the new version would only bring the old one forward. A one-off build only closes
# what runs from its own folder, since that is what locks the files it overwrites.
$running = if ($stable) { Get-AppProcess -AnyVersion -InstallRoot $InstallRoot } else { Get-AppProcess $InstallDir }
$restart = $running.Count -gt 0
if ($running.Count -gt 0) {
    Write-Step "Closing the running CodeSwitchX"
    foreach ($proc in $running) {
        if (Stop-AppProcess $proc) { Write-Ok "closed PID $($proc.ProcessId) ($($proc.ExecutablePath))" }
        else {
            Fail "PID $($proc.ProcessId) did not close." @(
                "A dialog may be open in it. Close CodeSwitchX by hand, then run this script again.",
                "It is not killed on purpose: a killed CodeSwitchX leaves its hosted VS Code windows hidden."
            )
        }
    }
}

if ($stable) {
    foreach ($other in (Get-OtherAppProcess -InstallRoot $InstallRoot)) {
        Write-Warn "another CodeSwitchX runs from $($other.ExecutablePath) (PID $($other.ProcessId)) - left alone"
        Write-Note "while it runs, starting the new version only brings that one forward"
    }
}

# --- clean -------------------------------------------------------------------------------
# A versioned folder always starts empty. It can only exist from an earlier run of this exact
# version that failed after claiming it - the number was not written back, so it comes round again.
if (($stable -or $Clean) -and (Test-DirectoryHasContent $InstallDir)) {
    Write-Step "Cleaning $InstallDir"

    # Belt and braces: the guard above already rejected a folder that is not ours, but this is
    # the only recursive delete in the scripts, so it checks for itself too.
    if (-not (Test-OurInstall $InstallDir)) {
        Fail "Refusing to clean $InstallDir - it carries no CodeSwitchX install marker." @(
            "Only a folder this script published into is ever deleted."
        )
    }

    try { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
    catch {
        Fail "Could not clean $InstallDir - $($_.Exception.Message)" @(
            "Something is probably still holding a file there. Close it and try again."
        )
    }
    Write-Ok "removed"
}

# --- claim the folder ----------------------------------------------------------------------
# Before the publish, not after. A publish interrupted part-way (Ctrl+C, a warning-as-error, a
# virus scanner holding a file) would otherwise leave a folder full of files and no marker, which
# every later run refuses to publish into *and* refuses to clean. The checks above established
# the folder is empty or ours, so claiming it here is safe. The marker says "complete": false until
# the publish has succeeded, and the current link never points at such a folder.
Write-InstallMarker -InstallDir $InstallDir -Version $version

# --- publish -----------------------------------------------------------------------------
# PowerShell unwraps a one-element array from an `if`, and splatting a plain string with @ hands
# MSBuild the characters one by one - so the array is built explicitly rather than cast.
$versionArgs = @("-p:Version=$version")
$buildFailedHints = @("Scroll up for the first error - warnings count as errors here (Directory.Build.props).")
if ($stable) { $buildFailedHints += "Directory.Build.props is untouched, still $current." }

Write-Step "Publishing Release build"
Write-Note "warnings are errors in this repo, so a warning will stop the build"

# Framework-dependent, like the relay it carries: CodeSwitchX.UI.csproj refuses a self-contained publish.
dotnet publish $UiProject --configuration Release --output $InstallDir --nologo @versionArgs
if ($LASTEXITCODE -ne 0) {
    Fail "The build failed (exit code $LASTEXITCODE)." $buildFailedHints
}

foreach ($required in @($AppExeName, $RelayRelativePath)) {
    if (-not (Test-Path -LiteralPath (Join-Path $InstallDir $required))) {
        Fail "The build reported success but $required is missing from $InstallDir."
    }
}
Write-InstallMarker -InstallDir $InstallDir -Version $version -Complete
Write-Ok "published to $InstallDir"

# --- make it the current version ------------------------------------------------------------
# The fixed path E:\StableVersion\CodeSwitchX is what the Start Menu entry and any taskbar pin open.
# Pointing it at the new folder is what makes this build "current" without every pin on the machine
# having to change.
$shortcutDir = $InstallDir
if ($stable) {
    Write-Step "Making $version the current version"
    if (Set-CurrentLink -InstallRoot $InstallRoot -Target $InstallDir) {
        $shortcutDir = Get-CurrentLinkPath $InstallRoot
        Write-Ok "$shortcutDir -> $InstallDir"
    }
    else {
        Write-Warn "no current link; the shortcut will point at $InstallDir directly, and a taskbar pin made earlier still opens the old build"
    }
}

if ($NoShortcut) {
    Write-Ok "no Start Menu shortcut, as asked"
    if ($stable -and $shortcutDir -eq $InstallDir) {
        Write-Warn "the existing Start Menu shortcut still opens the previous build"
    }
}
else {
    $linkPath = Write-StartMenuShortcut $shortcutDir
    Write-Ok "Start Menu shortcut at $linkPath"
    Write-Note "right-click it in the Start Menu and choose 'Pin to taskbar'"
}

# --- record the version -------------------------------------------------------------------
if ($stable) {
    Write-Step "Recording version $version"
    Set-PropsVersion $version
    Write-Ok "Directory.Build.props bumped to $version"
    Push-VersionBump $version
    Write-Ok "committed and pushed to origin/$ReleaseBranch"
}

# --- start it again ---------------------------------------------------------------------------
# Only when this script closed it. A VS Code extension host (a Claude Code session, say) passes
# ELECTRON_RUN_AS_NODE down, and every Code.exe CodeSwitchX starts would inherit it and run as plain
# Node, without a window - so the variable goes from this script's environment, which ends here.
# Started through the shell, which hands the app none of this script's handles: with them it held
# the output pipe of a piped run open, and whoever read it waited until CodeSwitchX exited.
$exePath = Join-Path $shortcutDir $AppExeName
if ($restart) {
    Write-Step "Starting CodeSwitchX $version"
    Remove-Item Env:\ELECTRON_RUN_AS_NODE -ErrorAction SilentlyContinue
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo $exePath
    $startInfo.WorkingDirectory = $shortcutDir
    $startInfo.UseShellExecute  = $true
    $started = [System.Diagnostics.Process]::Start($startInfo)
    Write-Ok "started PID $($started.Id)"
}

# --- done ----------------------------------------------------------------------------------
Write-Host ""
if ($stable) { Write-Host "  Build complete - CodeSwitchX $version" -ForegroundColor Green }
else         { Write-Host "  Build complete." -ForegroundColor Green }
if (-not $restart) {
    if ($NoShortcut) { Write-Host "  Start it: " -NoNewline }
    else             { Write-Host "  Start it from the Start Menu (CodeSwitchX), or: " -NoNewline }
    Write-Host $exePath -ForegroundColor White
}
Write-Note "if Settings then reports the Claude Code hooks as outdated, install them again there"
if ($stable) {
    Write-Note "tag it if this is a release you want on GitHub:  git tag v$version; git push origin v$version"
}
Write-Host ""
