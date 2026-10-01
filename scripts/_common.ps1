# Shared settings and helpers for the CodeSwitchX build/stop scripts.
# Dot-sourced by build.ps1 and stop.ps1 - not meant to be run on its own.

$ErrorActionPreference = 'Stop'

# PowerShell 7.4+ turns a non-zero native exit code into a thrown exception. We check
# $LASTEXITCODE ourselves so the failure prints as a readable message, not a stack trace.
if (Test-Path Variable:\PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$script:RepoRoot  = Split-Path -Parent $PSScriptRoot
$script:UiProject = Join-Path $RepoRoot 'src\CodeSwitchX.UI\CodeSwitchX.UI.csproj'

# Every stable build gets a folder of its own under this root, named after the app and the version it
# was built at: E:\StableVersion\CodeSwitchX-0.1.0.3. Older versions stay beside it. The version is the
# <Version> element in Directory.Build.props - the one place it lives - and build.ps1 moves it on,
# commits that one file and pushes main, so the number in the repo always names a build that exists on
# disk. E:\StableVersion\CodeSwitchX is a junction to the newest one.
#
# The app keeps its data in %LOCALAPPDATA%\CodeSwitchX whichever folder it runs from, so a stable build
# and a Debug build share one database without any link, and an install folder holds program files only.
$script:AppName            = 'CodeSwitchX'
$script:DefaultInstallRoot = 'E:\StableVersion'
$script:ReleaseBranch      = 'main'
$script:PropsPath          = Join-Path $RepoRoot 'Directory.Build.props'
$script:AppExeName         = 'CodeSwitchX.exe'
$script:RelayRelativePath  = 'relay\csx-hook.exe'
$script:ShortcutName       = 'CodeSwitchX.lnk'

# build.ps1 drops this in the install folder. Nothing is ever deleted from a folder that does
# not carry it, so a mistyped -InstallDir cannot take out a neighbouring release.
$script:InstallMarkerName = 'codeswitchx-install.json'

function Write-Step { param([string]$Message) Write-Host ""; Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note { param([string]$Message) Write-Host "    $Message" -ForegroundColor DarkGray }
function Write-Ok   { param([string]$Message) Write-Host "    [ok] $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "    [!]  $Message" -ForegroundColor Yellow }

function Fail {
    param([string]$Message, [string[]]$Hints)
    Write-Host ""
    Write-Host "    [x] $Message" -ForegroundColor Red
    foreach ($hint in $Hints) { Write-Host "        $hint" -ForegroundColor Yellow }
    Write-Host ""
    exit 1
}

# Absolute path, with no requirement that it already exists.
function Resolve-FullPath {
    param([string]$Path)
    return [System.IO.Path]::GetFullPath([System.IO.Path]::Combine((Get-Location).ProviderPath, $Path))
}

function Test-PathUnder {
    param([string]$Path, [string]$Parent, [switch]$OrEqual)
    $full   = (Resolve-FullPath $Path).TrimEnd('\')
    $parent = (Resolve-FullPath $Parent).TrimEnd('\')
    if ($OrEqual -and $full.Equals($parent, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $full.StartsWith($parent + '\', [StringComparison]::OrdinalIgnoreCase)
}

# CodeSwitchX processes started from this install folder - the ones our scripts own. With -AnyVersion,
# every one started from a versioned folder under the root or through the current link, which is what
# build.ps1 and stop.ps1 mean by "the stable build" when no folder is named. A Debug build from the
# repo is never among them.
function Get-AppProcess {
    param([string]$InstallDir, [switch]$AnyVersion, [string]$InstallRoot = $DefaultInstallRoot)
    $all = @(Get-CimInstance Win32_Process -Filter "Name = '$AppExeName'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath })
    if ($AnyVersion) {
        # Exactly our folders, not a neighbour whose name happens to start the same way
        # (CodeSwitchX_old, CodeSwitchXBackup): the current link itself, or a versioned
        # folder, which always has a dash after the app name.
        $current   = Get-CurrentLinkPath $InstallRoot
        $versioned = $current + '-'
        return @($all | Where-Object {
            (Test-PathUnder $_.ExecutablePath $current) -or
            $_.ExecutablePath.StartsWith($versioned, [StringComparison]::OrdinalIgnoreCase)
        })
    }
    $exePath = Join-Path (Resolve-FullPath $InstallDir) $AppExeName
    return @($all | Where-Object { $_.ExecutablePath -ieq $exePath })
}

# Every CodeSwitchX.exe that is not one of ours - a Debug build from the repo, as a rule. Reported, never stopped.
function Get-OtherAppProcess {
    param([string]$InstallRoot = $DefaultInstallRoot)
    $ours = @(Get-AppProcess -AnyVersion -InstallRoot $InstallRoot | ForEach-Object { $_.ProcessId })
    return @(Get-CimInstance Win32_Process -Filter "Name = '$AppExeName'" -ErrorAction SilentlyContinue |
        Where-Object { $ours -notcontains $_.ProcessId })
}

# Closes CodeSwitchX the way its own window does, never by force: on the way out it hands every hosted
# VS Code window back to the desktop, and a killed process would leave those windows cloaked until the
# next start sweeps them up. Accepts a Win32_Process (from Get-CimInstance). Returns 'closed', or why not:
# 'nowindow' when there is no main window to close (still starting, or a second start handing over to
# the first), 'timeout' when the window took the close but the process still runs - a modal dialog open,
# or a hang.
function Stop-AppProcess {
    param($Process, [int]$TimeoutSeconds = 20)
    $proc = Get-Process -Id $Process.ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) { return 'closed' }
    # WM_CLOSE to the main window. CodeSwitchX never hides it, so an instance that has finished starting has one.
    if (-not $proc.CloseMainWindow()) {
        if ($proc.HasExited) { return 'closed' }
        return 'nowindow'
    }
    if ($proc.WaitForExit($TimeoutSeconds * 1000)) { return 'closed' }
    return 'timeout'
}

# What to tell the user when Stop-AppProcess did not close a process.
function Get-StopFailureHints {
    param([string]$Result)
    $killed = "It is not killed on purpose: a killed CodeSwitchX leaves its hosted VS Code windows hidden until its next start."
    if ($Result -eq 'nowindow') {
        return @("It has no window to close - it may still be starting. Wait a moment and run this again, or close it from its tray icon.", $killed)
    }
    return @("It did not close in time - a dialog may be open in it. Close CodeSwitchX by hand, then run this again.", $killed)
}

# git with this script's error handling switched off for the call. Windows PowerShell 5.1, which the .cmd
# wrappers fall back to, turns a native command's stderr into an error record, and under
# $ErrorActionPreference = 'Stop' that ends the script with a stack trace. -Quiet drops stderr; without
# it the user sees git's own message. Check $LASTEXITCODE afterwards.
function Invoke-Git {
    param([string[]]$Arguments, [switch]$Quiet)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Quiet) { & git -C $RepoRoot @Arguments 2>$null }
        else        { & git -C $RepoRoot @Arguments }
    }
    finally { $ErrorActionPreference = $previous }
}

# The short commit of HEAD, or '' when git is missing or this is no checkout.
function Get-HeadCommit {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return '' }
    $commit = "$(Invoke-Git @('rev-parse', '--short', 'HEAD') -Quiet)".Trim()
    if ($LASTEXITCODE -ne 0) { return '' }
    return $commit
}

# A Start Menu entry the user can pin to the taskbar with a right-click. Pinning itself
# cannot be scripted on Windows 11.
function Write-StartMenuShortcut {
    param([string]$InstallDir)
    $programs = [Environment]::GetFolderPath('Programs')
    $linkPath = Join-Path $programs $ShortcutName
    $shell    = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($linkPath)
    $shortcut.TargetPath       = Join-Path $InstallDir $AppExeName
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.IconLocation     = (Join-Path $InstallDir $AppExeName) + ',0'
    $shortcut.Description      = 'CodeSwitchX'
    $shortcut.Save()
    return $linkPath
}

# Deletes the link itself and never what it points at. Remove-Item -Recurse on a junction
# has historically walked into the target and deleted the real files.
function Remove-DirectoryLink {
    param([string]$Path)
    [System.IO.Directory]::Delete($Path, $false)
}

# Where a directory link points, or $null for a real folder or nothing at all.
function Get-DirectoryLinkTarget {
    param([string]$Path)
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($item -and $item.PSObject.Properties['Target'] -and @($item.Target)[0]) { return @($item.Target)[0] }
    return $null
}

# True only for a folder a previous build.ps1 published into. Everything destructive in these
# scripts is gated on this, so a folder we did not create is never cleaned. With -OneOff, only a
# one-off build's folder: what the marker says cannot be aliased the way a path can, so a stable
# release reached through an 8.3 name, a junction or a subst drive is still refused.
function Test-OurInstall {
    param([string]$InstallDir, [switch]$OneOff)
    $marker = Join-Path $InstallDir $InstallMarkerName
    if (-not (Test-Path -LiteralPath $marker)) { return $false }
    try {
        $content = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($content.app -ne $AppName) { return $false }
        return (-not $OneOff) -or ("$($content.version)" -match '-oneoff\.')
    }
    catch { return $false }
}

# True for a folder whose build finished - the only kind the current link ever points at.
function Test-InstallComplete {
    param([string]$InstallDir)
    if (-not (Test-OurInstall $InstallDir)) { return $false }
    try { return [bool]((Get-Content -LiteralPath (Join-Path $InstallDir $InstallMarkerName) -Raw | ConvertFrom-Json).complete) }
    catch { return $false }
}

# A stable build never republishes over a finished version folder. The current link and the Start
# Menu shortcut move to the new folder before the bump is committed and pushed, so a push that fails,
# followed by a reset to origin, hands out the same number again - and its folder is then the one the
# shortcut opens, with CodeSwitchX running from it. Cleaning it would delete every file the running
# app has not locked. A folder that never became current - an interrupted publish, or a running
# CodeSwitchX that would not close - is not marked complete, so build.ps1 still cleans that one.
function Assert-StableTargetFree {
    param([string]$InstallDir, [string]$Version, [string]$Current)
    if (-not (Test-InstallComplete $InstallDir)) { return }
    Fail "$Version is already published in $InstallDir." @(
        "A finished stable folder is never republished over - it may be the version running right now.",
        "Directory.Build.props still says $Current, so the bump of the run that built it never reached origin.",
        "Record it by hand - set <Version> to $Version in Directory.Build.props, commit that and push $ReleaseBranch - then run this again."
    )
}

function Write-InstallMarker {
    param([string]$InstallDir, [string]$Version = '', [switch]$Complete)
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    [ordered]@{
        app         = $AppName
        version     = $Version
        complete    = [bool]$Complete
        installedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        commit      = Get-HeadCommit
        note        = 'Written by scripts/build.ps1. Without this file the scripts refuse to clean this folder.'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallDir $InstallMarkerName) -Encoding UTF8
}

function Test-DirectoryHasContent {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    return (@(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue).Count -gt 0)
}

# --- versioned stable builds ---------------------------------------------------------------

function Get-VersionedInstallDir {
    param([string]$InstallRoot, [string]$Version)
    return Join-Path $InstallRoot "$AppName-$Version"
}

# The fixed path the Start Menu shortcut and any taskbar pin target: E:\StableVersion\CodeSwitchX, a
# junction build.ps1 points at the newest versioned folder, so a pin made months ago keeps opening the
# current build.
function Get-CurrentLinkPath {
    param([string]$InstallRoot = $DefaultInstallRoot)
    return Join-Path (Resolve-FullPath $InstallRoot) $AppName
}

# Points the fixed path at the folder just published: creates the junction, or retargets the one an
# earlier build made. A real folder of that name is left alone and reported - the shortcut then has to
# point at the versioned folder directly. Returns $true when the link now points at the target.
function Set-CurrentLink {
    param([string]$InstallRoot, [string]$Target)
    $linkPath = Get-CurrentLinkPath $InstallRoot
    $target   = (Resolve-FullPath $Target).TrimEnd('\')

    if (Test-Path -LiteralPath $linkPath) {
        $existingTarget = Get-DirectoryLinkTarget $linkPath
        if (-not $existingTarget) {
            Write-Warn "$linkPath is a real folder, not a link to a version - left alone"
            return $false
        }
        if ((Resolve-FullPath $existingTarget).TrimEnd('\') -ieq $target) { return $true }
        Remove-DirectoryLink $linkPath
    }

    New-Item -ItemType Junction -Path $linkPath -Target $target | Out-Null
    return $true
}

# Directory.Build.props is hand-written XML with comments in it that an XML round-trip would
# reformat, so it is read and edited as one string and only the number is ever touched.
function Get-PropsVersion {
    if (-not (Test-Path -LiteralPath $PropsPath)) {
        Fail "Cannot find $PropsPath." @("The version lives in its <Version> element.")
    }
    $found = [regex]::Matches([IO.File]::ReadAllText($PropsPath), '<Version>([^<]+)</Version>')
    if ($found.Count -ne 1) {
        Fail "Expected exactly one <Version> element in Directory.Build.props, found $($found.Count)."
    }
    $current = $found[0].Groups[1].Value.Trim()
    if ($current -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        Fail "Cannot bump '$current' - expected a numeric version like 0.1.0 or 0.1.0.1."
    }
    return $current
}

# A bump zeroes everything to its right - 1.2.3.4 with -Part minor is 1.3.0, not 1.3.3.4 - and only
# 'build' keeps a fourth component, so a release bump drops back to three parts and starts counting
# builds again from there. The everyday case is 'build': 0.1.0 -> 0.1.0.1 -> 0.1.0.2.
function Get-NextVersion {
    param([string]$Current, [string]$Part)
    $n = @($Current -split '\.' | ForEach-Object { [int]$_ })
    while ($n.Count -lt 4) { $n += 0 }
    switch ($Part) {
        'major' { return '{0}.0.0' -f ($n[0] + 1) }
        'minor' { return '{0}.{1}.0' -f $n[0], ($n[1] + 1) }
        'patch' { return '{0}.{1}.{2}' -f $n[0], $n[1], ($n[2] + 1) }
        default { return '{0}.{1}.{2}.{3}' -f $n[0], $n[1], $n[2], ($n[3] + 1) }
    }
}

# The version a one-off -InstallDir build is stamped with, so Explorer's Product version or a log line
# can never pass it off as the stable build of the same number. The 'g' in front of the hash is
# load-bearing: a short hash can be all digits with a leading zero (0123456, about 1 commit in 270),
# which is not a valid SemVer prerelease identifier, and NuGet's restore then fails with nothing but
# MSB4181.
function Get-OneOffVersion {
    param([string]$Current, [string]$Commit)
    if (-not $Commit) { return "$Current-oneoff.nogit" }
    return "$Current-oneoff.g$Commit"
}

# Writes the string back byte for byte apart from the number: same line endings, same BOM or lack of
# one, so the bump commit is a one-line diff.
function Set-PropsVersion {
    param([string]$Version)
    $bytes  = [IO.File]::ReadAllBytes($PropsPath)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text   = [IO.File]::ReadAllText($PropsPath)
    $m      = [regex]::Match($text, '<Version>[^<]+</Version>')
    if (-not $m.Success) {
        # Validated before the publish, but that was minutes ago and the file may have been edited since.
        Fail "Directory.Build.props no longer has a <Version> element - it changed while the build ran." @(
            "The published $Version folder is fine. Set the version to $Version by hand, then commit and push it."
        )
    }
    $text   = $text.Remove($m.Index, $m.Length).Insert($m.Index, "<Version>$Version</Version>")
    [IO.File]::WriteAllText($PropsPath, $text, (New-Object Text.UTF8Encoding $hasBom))
}

# Everything that has to be true before a build may go into the stable root. This refuses rather
# than asks: a yes/no here would put a "stable" build of unmerged code on disk, stamped with a
# version number main is about to hand out again to a different build.
function Assert-ReleaseReady {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Fail "git is not on your PATH." @("The stable build commits and pushes the version bump, so it needs git.")
    }

    $branch = "$(Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD') -Quiet)".Trim()
    if ($LASTEXITCODE -ne 0 -or -not $branch) {
        Fail "$RepoRoot is not a git checkout."
    }
    if ($branch -ne $ReleaseBranch) {
        Fail "You are on branch '$branch', not $ReleaseBranch." @(
            "Stable builds are only made from $ReleaseBranch, so the version bump lands where every later build sees it.",
            "Merge the branch, then:  git checkout $ReleaseBranch; git pull",
            "To try this branch's build, give it a folder of its own outside $DefaultInstallRoot - nothing is bumped or pushed:",
            "  .\scripts\build.ps1 -InstallDir 'E:\Builds\$AppName-test'"
        )
    }

    # Untracked files count too: the SDK compiles every *.cs under src\ whether git knows it or
    # not, so a new file that was never added would go into a build stamped with a commit that
    # does not contain it. bin\, obj\ and the like are ignored, so they never show up here.
    # The exit code decides, not the output: a git that fails here prints nothing to stdout, and
    # nothing reads as "no changes".
    $dirty = @(Invoke-Git @('status', '--porcelain') -Quiet)
    if ($LASTEXITCODE -ne 0) {
        Fail "git status failed (exit code $LASTEXITCODE), so the working tree could not be checked." @(
            "Run git status yourself to see why - a damaged index or a safe.directory refusal are the usual causes."
        )
    }
    if ($dirty.Count -gt 0) {
        Fail "The working tree has uncommitted or untracked files." @(
            @("A stable build has to match a commit on $ReleaseBranch. Commit, stash, ignore or delete these first:") +
            @($dirty | Select-Object -First 8 | ForEach-Object { "  $_" })
        )
    }

    Write-Note "fetching origin/$ReleaseBranch"
    Invoke-Git @('fetch', '--quiet', 'origin', $ReleaseBranch)
    if ($LASTEXITCODE -ne 0) {
        Fail "git fetch failed." @("Check your network and GitHub login, then run this script again.")
    }

    # Both directions. Behind means the build would not be of what is on origin; ahead means the
    # push at the end would carry commits onto $ReleaseBranch that never went through a PR.
    $counts = "$(Invoke-Git @('rev-list', '--left-right', '--count', "HEAD...origin/$ReleaseBranch") -Quiet)".Trim()
    if ($LASTEXITCODE -ne 0 -or -not ($counts -match '^(\d+)\s+(\d+)$')) {
        Fail "Could not compare $ReleaseBranch with origin/$ReleaseBranch." @("Does the remote 'origin' have a '$ReleaseBranch' branch?")
    }
    $ahead  = [int]$Matches[1]
    $behind = [int]$Matches[2]
    if ($behind -gt 0) {
        Fail "$ReleaseBranch is $behind commit(s) behind origin/$ReleaseBranch." @("git pull, then run this script again.")
    }
    if ($ahead -gt 0) {
        Fail "$ReleaseBranch is $ahead commit(s) ahead of origin/$ReleaseBranch." @(
            "A stable build is of code that is already on origin, and this script must not be the thing that pushes other commits.",
            "Push them through a PR, or move them to a branch:  git branch feature/<name>; git reset --hard origin/$ReleaseBranch"
        )
    }
    Write-Ok "on $ReleaseBranch, clean, level with origin"
}

# Commits Directory.Build.props and nothing else, then pushes. Called only after the publish
# succeeded, so a broken build never moves the number.
function Push-VersionBump {
    param([string]$Version)
    Invoke-Git @('commit', '--quiet', '-m', "chore: bump version to $Version", '--', 'Directory.Build.props')
    if ($LASTEXITCODE -ne 0) {
        Fail "The build is done, but committing the version bump failed." @(
            "Directory.Build.props already says $Version. Commit and push it by hand:",
            "  git commit -m `"chore: bump version to $Version`" -- Directory.Build.props; git push origin $ReleaseBranch"
        )
    }
    # The freshness check ran before a publish that takes minutes; the likeliest reason a push
    # fails now is that origin moved meanwhile, and a plain retry would be rejected the same way.
    Invoke-Git @('push', '--quiet', 'origin', $ReleaseBranch)
    if ($LASTEXITCODE -ne 0) {
        Fail "The build is done and the bump to $Version is committed, but the push was rejected." @(
            "If origin/$ReleaseBranch moved while the build ran:  git pull --rebase origin $ReleaseBranch; git push origin $ReleaseBranch",
            "If it was the network or your login, fix that and:  git push origin $ReleaseBranch",
            "If $ReleaseBranch is protected, open a PR for the bump commit instead."
        )
    }
}
