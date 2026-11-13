# Yard: Build new version / Run - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A workspace tile's right-click menu offers "Build new version" and "Run" for repos that carry the stable-build scripts (#423).

**Architecture:** The git round (`YardViewModel.GitLinesAsync`, off the UI thread) also detects the scripts and stores the result on each `GitLine` as a `StableBuildRepo`. When the tile's menu opens, the tile turns its lines into `StableBuildEntry` items (precheck, shortcut present?), and the view inserts them as `MenuItem`s in code-behind. Starting a build or a shortcut goes through `IStableBuildLauncher` (Process.Start), asking first through the existing `IYardDialogs`.

**Tech Stack:** WPF, CommunityToolkit.Mvvm, xUnit v3 + Shouldly (as the existing UI tests).

**Spec:** `docs/superpowers/specs/2026-11-13-yard-build-and-run-design.md`

## Global Constraints

- Load the `csharp-conventions` skill before writing C#/XAML. File-scoped namespaces, braces always, `_camelCase`, warnings are errors.
- Never run a real `build.ps1` against `E:\StableVersion`; on-screen checks use a scratch repo with fake scripts.
- The repo is public: no names of the user's other repos in code, tests, commits.
- Allowed branches for Build: exactly `main` and `develop` (ordinal, case-sensitive like git).
- Shortcut folder: `Environment.GetFolderPath(Environment.SpecialFolder.Programs)` (the Start Menu's Programs).
- UI tests on the BenQ monitor only (global CLAUDE.md).

## Review Focus

- `_common.ps1` with the name in double quotes or with extra spaces (`$script:ShortcutName = "X.lnk"`) → still found. Test in Task 1.
- A repo on `main` whose git state is unknown (`DirtyCount` null) → Build greyed, "Git state unknown". Test in Task 1.
- Two lines of one tile in the same checkout (same `GitDir`, #412) → one pair of entries, not two. Test in Task 2.
- `wt.exe` missing → falls back to a plain console; `pwsh` missing → `powershell.exe`. Covered by Task 2's launcher order, checked by reading; no unit test (Process.Start).
- User says No in the dialog → nothing starts. Test in Task 2.

---

### Task 1: Detect the scripts and precheck a line

**Files:**
- Create: `src/CodeSwitchX.UI/Yard/StableBuild.cs`
- Modify: `src/CodeSwitchX.UI/Yard/GitLine.cs` (new optional parameter `StableBuildRepo? StableBuild = null`, last)
- Modify: `src/CodeSwitchX.UI/Yard/YardViewModel.cs` (the two `new GitLine(...)` in `GitLinesAsync`, ~L957 and ~L1035: pass `StableBuild: StableBuildRepo.Find(path)` when the line is a repository)
- Test: `tests/CodeSwitchX.UI.Tests/Yard/StableBuildTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record StableBuildRepo(string RepoPath, string ShortcutName)` with `public static StableBuildRepo? Find(string repoPath)` - `scripts\build.ps1` and `scripts\_common.ps1` exist and `_common.ps1` matches `^\s*\$script:ShortcutName\s*=\s*['"]([^'"]+)['"]` (multiline); otherwise null. IO errors → null. `public string BuildScript => Path.Combine(RepoPath, "scripts", "build.ps1")`.
  - `public static class StableBuildCheck` with `public static string? BuildBlock(GitLine line)` - null when the build may start; otherwise the tooltip text:
    - `Branch` not `main`/`develop`: `"On {Branch} - a stable build runs from main or develop"` (Branch null: `"Git state unknown"`)
    - `DirtyCount` null: `"Git state unknown"`
    - `DirtyCount > 0`: `"{n} uncommitted change(s) - commit or stash first"` (`1 uncommitted change`, `3 uncommitted changes`)

- [ ] **Step 1: Write the failing tests** (temp folders under `Path.GetTempPath()`, deleted in `Dispose`)
  - `Find_returns_shortcut_name_from_common` - `$script:ShortcutName       = 'Some App.lnk'` → `ShortcutName == "Some App.lnk"`.
  - `Find_accepts_double_quotes` - `$script:ShortcutName = "X.lnk"` → `"X.lnk"`.
  - `Find_returns_null_without_build_ps1`, `Find_returns_null_without_shortcut_line`, `Find_returns_null_for_missing_folder`.
  - `BuildBlock_is_null_on_clean_main` and `..._on_clean_develop`.
  - `BuildBlock_names_the_branch` - `feature/x`, 0 dirty → `"On feature/x - a stable build runs from main or develop"`.
  - `BuildBlock_counts_changes` - main, 1 → `"1 uncommitted change - commit or stash first"`; main, 3 → `"3 uncommitted changes - commit or stash first"`.
  - `BuildBlock_unknown_state` - main, `DirtyCount` null → `"Git state unknown"`.
- [ ] **Step 2: Run, expect FAIL (types missing)**
  `dotnet test --project tests/CodeSwitchX.UI.Tests --filter-class "*StableBuildTests"`
- [ ] **Step 3: Implement `StableBuild.cs`, the `GitLine` parameter and the two call sites**
- [ ] **Step 4: Run the class again, expect PASS; then `--filter-class "*Yard*"` stays green**
- [ ] **Step 5: Commit** `feat(yard): find stable-build scripts on each repo line (#423)`

### Task 2: Tile entries, launcher, confirm

**Files:**
- Modify: `src/CodeSwitchX.UI/Yard/StableBuild.cs` (add entry + launcher)
- Modify: `src/CodeSwitchX.UI/Yard/WorkspaceTileViewModel.cs` (method `StableBuildEntries()`)
- Modify: `src/CodeSwitchX.UI/Yard/YardViewModel.cs` (ctor gets optional `IStableBuildLauncher? stableBuilds = null, IYardDialogs? dialogs = null`, last; defaults `new StableBuildLauncher()`, `new YardDialogs()`; exposed `internal` for the tiles)
- Modify: `src/CodeSwitchX.UI/App.xaml.cs` (register `services.AddSingleton<IStableBuildLauncher, StableBuildLauncher>()` next to `IYardDialogs`, ~L332; YardViewModel is made by `ActivatorUtilities.CreateInstance`, ~L335, which fills both optional parameters from DI)
- Test: `tests/CodeSwitchX.UI.Tests/Yard/StableBuildTests.cs` (fake launcher + fake dialogs)

**Interfaces:**
- Consumes: `StableBuildRepo`, `StableBuildCheck.BuildBlock`, `GitLine.StableBuild`.
- Produces:
  - `public interface IStableBuildLauncher { string ShortcutPath(string shortcutName); bool StartBuild(StableBuildRepo repo); bool Run(string shortcutPath); bool ShortcutExists(string shortcutPath); }`
  - `StableBuildLauncher.StartBuild`: try `wt.exe -d "<RepoPath>" <ps> -NoProfile -ExecutionPolicy Bypass -NoExit -File "<BuildScript>"`; on failure start `<ps>` with the same arguments (UseShellExecute → its own console). `<ps>` = `pwsh.exe`, or `powershell.exe` when pwsh cannot be started. `Run`: `Process.Start(new ProcessStartInfo(shortcutPath) { UseShellExecute = true })`. All return false instead of throwing.
  - `public sealed partial class StableBuildEntry` with `string BuildHeader`, `string RunHeader`, `bool CanBuild`, `string? BuildBlock`, `bool CanRun`, `string? RunBlock`, `IRelayCommand BuildCommand`, `IRelayCommand RunCommand`. Headers: one entry on the tile → `"Build new version"` / `"Run"`; several → `"Build {folder}"` / `"Run {folder}"` where folder = `Path.GetFileName(RepoPath)`. `RunBlock` = `"No stable build yet"` when the shortcut is missing.
  - `WorkspaceTileViewModel.StableBuildEntries()` → `IReadOnlyList<StableBuildEntry>`: one per distinct `StableBuild.RepoPath` (OrdinalIgnoreCase) among `GitLines`, in line order.
  - Build: `dialogs.Confirm($"Build a new version of {folder}?\n\nThe build script bumps the version, commits and pushes it, and may close the running {folder} and start the new one. Its window shows every step.")`; Yes → `StartBuild`; false from launcher → `dialogs.Tell($"Could not start the build of {folder}.")`. Run: false → `Tell($"Could not start {folder}.")`.

- [ ] **Step 1: Write the failing tests**
  - `Tile_without_scripts_has_no_entries`.
  - `One_repo_gets_plain_headers` → `"Build new version"`, `"Run"`.
  - `Two_repos_get_folder_headers` → `"Build A"`, `"Run B"` …
  - `Same_checkout_twice_gives_one_entry` (two lines, same `StableBuild.RepoPath`).
  - `Build_disabled_on_feature_branch` → `CanBuild` false, `BuildBlock` from Task 1.
  - `Run_disabled_without_shortcut` → `CanRun` false, `"No stable build yet"`.
  - `Build_asks_and_starts_on_yes`, `Build_starts_nothing_on_no`, `Build_tells_when_launch_fails`, `Run_opens_shortcut_path` (fake launcher records `ShortcutPath("X.lnk")`).
  Build tiles the way `YardViewModelTests` do (reuse its helpers) and set lines through `tile.ShowGit(...)`.
- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Run the class, then the whole UI test project; expect PASS** (`AppHostTests` included, it resolves the Yard)
- [ ] **Step 5: Commit** `feat(yard): Build new version / Run entries with precheck and confirm (#423)`

### Task 3: The menu, and on-screen check

**Files:**
- Modify: `src/CodeSwitchX.UI/Yard/YardView.xaml` (tile `ContextMenu`, L142: add `Opened="OnTileMenuOpened"`; the entries themselves are inserted in code-behind)
- Modify: `src/CodeSwitchX.UI/Yard/YardView.xaml.cs`

**Interfaces:**
- Consumes: `WorkspaceTileViewModel.StableBuildEntries()`, `StableBuildEntry` members.

- [ ] **Step 1: `OnTileMenuOpened(object sender, RoutedEventArgs e)`** - removes the items it added last time (`Tag` = a private static marker object), then, when entries exist, inserts before the `Separator` that precedes "Unregister": a `Separator`, then per entry a Build `MenuItem` and a Run `MenuItem` (Header, Command, `IsEnabled`, ToolTip = block text, `ToolTipService.SetShowOnDisabled(item, true)`). View-only code-behind, allowed by CLAUDE.md.
- [ ] **Step 2: `dotnet build CodeSwitchX.slnx` green; full `dotnet test --solution CodeSwitchX.slnx` green** (three `HotkeyServiceTests` may fail while the stable build runs - not a regression).
- [ ] **Step 3: On-screen check on the BenQ** (memory "running-the-app-from-claude": strip `ELECTRON_RUN_AS_NODE`, Open mic off and restored, ask before closing the stable build)
  - Scratch repo `E:\Repos\Z\Z-Scratch423` (git init, commit, on `main`), `scripts\_common.ps1` with `$script:ShortcutName = 'Z-Scratch423.lnk'`, `scripts\build.ps1` that writes a line and the time. A `Z-Scratch423.lnk` in the Start Menu Programs pointing at `notepad.exe`. Register it as a workspace in the dev build.
  - Check: menu shows both entries; Build asks, Yes opens a terminal running the fake script and the window stays; on a `feature/x` branch / with a dirty file Build is greyed with the tooltip; Run opens notepad; delete the .lnk → Run greyed "No stable build yet".
  - Self-close: a fake `build.ps1` that closes the dev CodeSwitchX the way `Stop-AppProcess` in `scripts/_common.ps1` does (read it; same call, against the dev build's PID). Expect: no Exit dialog, the terminal survives.
  - Cleanup: unregister the workspace, remove the scratch repo and the .lnk.
- [ ] **Step 4: Commit** `feat(yard): tile menu shows the stable-build entries (#423)`, push, open the PR (account `Little-God1983`, body from a file, `Closes #423`).
