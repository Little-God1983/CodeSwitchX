# Yard tile: stable build row Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Yard tile whose repository has the stable-build scripts shows, under its chats, a separator and a row "Stable 0.1.0.45" with a pill "up to date" (green), "outdated" (amber) or "no stable build" (grey). Issue #452.

**Architecture:** `StableBuildRepo.Find` (already parsing `scripts\_common.ps1`) also learns where the install marker is. The tile's git round reads that marker (version + commit the build was made from) and asks git, through a new `GitInspector.SinceBuildAsync`, how many commits GitHub's default branch has after that commit, skipping the build script's own bump commits. The result rides on `GitLine` as a `StableBuildStatus`; the tile shows one row per installed app.

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm, xUnit v3 + Shouldly + NSubstitute, git CLI.

**Spec:** `docs/superpowers/specs/2026-11-27-yard-stable-build-row-design.md`

## Global Constraints

- Repo rules in `CLAUDE.md` apply: file-scoped namespaces, braces always, `_camelCase` fields, warnings are errors, `using Path = System.IO.Path;` in UI files that need `Path`. Load the `csharp-conventions` skill before writing C#/XAML.
- No new packages. JSON with `System.Text.Json` (in the framework).
- The repo is **public**: no names of other repositories (private ones especially) in code, tests, comments or commits. Use made-up names (`App`, `Shop`) in tests.
- Outdated means: **GitHub's default branch** (`RepoActivity.DefaultBranch` / `DefaultHead`) has a commit after the marker's commit that is not a bump commit. Other branches and pull requests never count.
- A bump commit is one whose subject matches `^chore: bump version to \d` **and** whose only changed file is `Directory.Build.props` (the same test `Get-UnpushedBump` in `scripts/_common.ps1` uses).
- Pill words, exactly: `up to date`, `outdated`, `no stable build`. Row text: `Stable <version>`.
- Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **GitHub merged, clone not fetched:** GitHub's head commit is not in the local clone (the everyday case right after a merge on GitHub). Expected: "outdated", count unknown, never "unknown" or "up to date". → test in Task 2.
2. **Marker locked for a moment** (the build is writing it, a scanner holds it): the row keeps what it showed, it does not flip to "no stable build". → test in Task 1 and Task 3.
3. **Build just finished:** the newest commit on GitHub is the bump commit right after the marker's commit. Expected: "up to date". → test in Task 2 (real git).
4. **Marker with a BOM** (PowerShell 5.1's `Set-Content -Encoding UTF8` writes one) and a `commit` of `null` (git missing at build time). Expected: version read; with no commit the row shows the version and no pill. → tests in Task 1.
5. **Same two commits every 30 s round:** git is not asked again while the marker's commit and GitHub's head stay the same. → test in Task 3.

---

### Task 1: Find the marker and read it

**Files:**
- Modify: `src/CodeSwitchX.UI/Yard/StableBuild.cs` (`StableBuildRepo`, new `StableBuildInstall`)
- Test: `tests/CodeSwitchX.UI.Tests/Yard/StableBuildTests.cs`

**Interfaces:**
- Produces:
  - `StableBuildRepo` gets a new last positional parameter `string? MarkerPath = null`: `<DefaultInstallRoot>\<AppName>\<InstallMarkerName>`.
  - `public sealed record StableBuildInstall(string Version, string? Commit)` in `StableBuild.cs`.
  - `public readonly record struct StableBuildRead(bool Known, StableBuildInstall? Install)` with `static readonly StableBuildRead Unknown = new(false, null)`, `NoBuild = new(true, null)`.
  - `public static StableBuildRead StableBuildInstall.Read(string markerPath)`.

- [ ] **Step 1: Write the failing tests** (in `StableBuildTests`, following its temp-folder setup)
  - `Find_gives_the_marker_path_from_the_three_settings`: `_common.ps1` with `ShortcutName 'App.lnk'`, `DefaultInstallRoot 'E:\Stable'`, `AppName 'App'`, `InstallMarkerName 'app-install.json'` → `MarkerPath.ShouldBe(@"E:\Stable\App\app-install.json")`.
  - `Find_has_no_marker_path_for_a_relative_root_a_name_with_a_folder_or_a_marker_not_json`: three cases (`DefaultInstallRoot 'Stable'`, `AppName 'x\App'`, `InstallMarkerName 'install.txt'`) → `StableBuild` still found (`ShouldNotBeNull`), `MarkerPath.ShouldBeNull()`.
  - `Read_gives_version_and_commit_of_a_complete_marker_with_a_bom`: file written with `new UTF8Encoding(true)`, content `{"app":"App","version":"1.2.3.4","complete":true,"installedAt":"2026-11-26 20:51:16","commit":"715bed9","note":"x"}` → `new StableBuildRead(true, new StableBuildInstall("1.2.3.4", "715bed9"))`.
  - `Read_gives_no_build_for_a_missing_file_an_incomplete_marker_or_no_version`: each → `StableBuildRead.NoBuild`.
  - `Read_keeps_a_null_commit`: `"commit": null` → `Install.Commit.ShouldBeNull()`, version kept.
  - `Read_is_unknown_while_another_program_holds_the_file`: open the file with `FileShare.None` during the call → `StableBuildRead.Unknown`.
  - `Read_gives_no_build_for_a_commit_that_is_not_a_short_hash`: `"commit": "--all"` → `Install.Commit.ShouldBeNull()` (it later goes onto git's command line; only `^[0-9a-fA-F]{4,40}$` passes).

- [ ] **Step 2: Run them, see them fail**
  Run: `dotnet test --project tests/CodeSwitchX.UI.Tests --filter-class "*StableBuildTests"`
  Expected: compile errors (`MarkerPath`, `StableBuildInstall` missing).

- [ ] **Step 3: Implement.** In `Find`, read `DefaultInstallRoot`, `AppName`, `InstallMarkerName` with the existing `Setting(text, name)`; `MarkerPath` only when the root `Path.IsPathFullyQualified`, `AppName == Path.GetFileName(AppName)`, the marker name `== Path.GetFileName(...)` and ends with `.json` (OrdinalIgnoreCase); otherwise null. `Read`: `File.ReadAllText` (strips the BOM) + `JsonDocument.Parse`; a missing file, `complete` not `true`, or no non-empty string `version` → `NoBuild`; sharing/lock violation (reuse the `SharingViolation`/`LockViolation` HResults already in the file) → `Unknown`; malformed JSON → `NoBuild`; any other exception except `OperationCanceledException` → `Unknown`.

- [ ] **Step 4: Run them, see them pass** (same command). Expected: all pass, and the existing `StableBuildTests` still pass.

- [ ] **Step 5: Commit** `feat(yard): find and read the stable build's install marker (#452)`

---

### Task 2: Count the commits since the build

**Files:**
- Modify: `src/CodeSwitchX.Core/Workspaces/GitInspector.cs`
- Test: `tests/CodeSwitchX.Core.Tests/Workspaces/GitInspectorTests.cs` (fake runner) and `tests/CodeSwitchX.Core.Tests/Workspaces/GitInspectorRealGitTests.cs` (real git, `RealGitRepos` base)

**Interfaces:**
- Produces (in `GitInspector.cs`, beside `BehindAnswer`):
  - `public readonly record struct SinceBuildAnswer(bool Known, int? NewerCommits, bool Newer)` with `static readonly Unknown = new(false, null, false)`, `UpToDate = new(true, 0, false)`, `static SinceBuildAnswer Outdated(int? commits) => new(true, commits, true)`.
  - `public Task<SinceBuildAnswer> SinceBuildAsync(string root, string builtFrom, string onGitHub, CancellationToken ct)`; `builtFrom` is the marker's (short) hash, `onGitHub` GitHub's full head hash.

- [ ] **Step 1: Write the failing tests**
  - Fake runner (`GitInspectorTests`): `SinceBuild_is_unknown_for_a_built_from_that_is_no_hash_or_an_on_github_that_is_not_full` (`"--all"`, `"abc"` short onGitHub) → `Unknown`, runner never called. `SinceBuild_is_unknown_when_git_gives_no_answer` (runner returns null) → `Unknown`. `SinceBuild_is_unknown_when_the_build_commit_is_not_in_the_clone` (`rev-list --no-walk --ignore-missing <builtFrom>` returns empty) → `Unknown`.
  - Real git (`GitInspectorRealGitTests`, `Arrange()` gives `Work`/`Other`/`Origin`):
    - `A_build_followed_only_by_its_bump_commit_is_up_to_date`: in `Work`, commit, note `rev-parse --short HEAD` as built-from, then commit `Directory.Build.props` with message `chore: bump version to 1.0.0.2` → `SinceBuildAsync(Work, short, HEAD) == UpToDate`.
    - `Each_other_commit_on_the_branch_counts`: after that, one more commit `a.txt` and a commit whose subject is `chore: bump version to 1.0.0.3` but changes `Directory.Build.props` **and** `b.txt` → `Outdated(2)`.
    - `A_head_on_GitHub_the_clone_never_fetched_is_outdated_by_an_unknown_count`: commit and push from `Other`, do not fetch in `Work`; pass `Other`'s `rev-parse HEAD` → `Outdated(null)`.
    - `The_build_commit_itself_is_up_to_date`: `onGitHub` = full hash of the built-from commit → `UpToDate`.

- [ ] **Step 2: Run them, see them fail**
  Run: `dotnet test --project tests/CodeSwitchX.Core.Tests --filter-method "*SinceBuild*"` and `--filter-method "*build*"` on `GitInspectorRealGitTests`.
  Expected: compile errors.

- [ ] **Step 3: Implement** with `_runGit` (null = no answer), in this order:
  1. `builtFrom` matches `^[0-9a-fA-F]{4,40}$` and `IsFullHash(onGitHub)`, else `Unknown`.
  2. `rev-list --no-walk --ignore-missing {builtFrom}` → null: `Unknown`; empty: `Unknown` (the build was made elsewhere); else the full hash `full`.
  3. `full` equals `onGitHub` (OrdinalIgnoreCase) → `UpToDate`.
  4. `rev-list --no-walk --ignore-missing {onGitHub}` → null: `Unknown`; empty: `Outdated(null)` (bump commits are made in this clone, so a commit it lacks is real news).
  5. `log --format=%x1e%H%x1f%s --name-only {full}..{onGitHub}` → null: `Unknown`; split records on `\x1e`, first line `hash\x1fsubject`, the remaining non-empty lines are files; count records that are not bump commits (Global Constraints). 0 → `UpToDate`, n → `Outdated(n)`.

- [ ] **Step 4: Run them, see them pass** (same commands). Expected: pass; the rest of `GitInspectorTests` still passes.

- [ ] **Step 5: Commit** `feat(core): count the default branch's commits since a stable build (#452)`

---

### Task 3: The git round fills the line's stable build status

Depends on Tasks 1 and 2.

**Files:**
- Modify: `src/CodeSwitchX.UI/Yard/StableBuild.cs` (new `StableBuildStatus`, `StableFreshness`), `src/CodeSwitchX.UI/Yard/GitLine.cs`, `src/CodeSwitchX.UI/Yard/YardViewModel.cs` (`CompareAsync`, `RefreshGitAsync`, `OnGitHubChanged`)
- Test: `tests/CodeSwitchX.UI.Tests/Yard/StableBuildTests.cs` (texts), `tests/CodeSwitchX.UI.Tests/Yard/YardViewModelTests.StableBuild.cs` (round)

**Interfaces:**
- Consumes: `StableBuildRepo.MarkerPath`, `StableBuildInstall.Read`, `StableBuildRead` (Task 1); `GitInspector.SinceBuildAsync`, `SinceBuildAnswer` (Task 2).
- Produces:
  - `public enum StableFreshness { NoBuild, Unknown, UpToDate, Outdated }`
  - `public sealed record StableBuildStatus(StableFreshness Freshness, string? Version = null, int? NewerCommits = null, string? Branch = null, string? BuiltFrom = null, string? ComparedWith = null)` with:
    - `string? Label` → `"up to date"` / `"outdated"` / `"no stable build"` / null for `Unknown`.
    - `string? VersionText` → `$"Stable {Version}"`, null without a version.
    - `string ToolTip` → UpToDate: `$"{Version} was built from {Branch} as it is on GitHub now."`; Outdated with a count: `$"{GitLine.CommitsText(n)} on {Branch} since {Version}."`; without: `$"{Branch} on GitHub has commits newer than {Version}."`; NoBuild: `"No stable build installed yet."`; Unknown: `$"{Version} - not compared with GitHub yet."`
  - `GitLine` gets a new last positional parameter `StableBuildStatus? Stable = null`, and `internal bool StableIsOutOfDate => Stable is { BuiltFrom: not null } s && GitHub?.DefaultHead?.Sha is { } head && head != s.ComparedWith;`

- [ ] **Step 1: Write the failing tests**
  - Texts (`StableBuildTests`): one test per `ToolTip`/`Label` case above with `Version "0.1.0.45"`, `Branch "main"`, `NewerCommits 3` → `"3 commits on main since 0.1.0.45."` etc.
  - Round (`YardViewModelTests.StableBuild.cs`, follow `The_git_round_marks_a_repository_with_the_stable_build_scripts` for the repo/scripts setup and `YardGitHubTests.Behind.cs` for a GitHub activity with `DefaultBranch "main"`, `DefaultHead`): `_common.ps1` also sets the three marker settings pointing into the test's temp folder, and a marker file is written there.
    - `The_round_shows_an_up_to_date_build`: git fake answers the two `rev-list --no-walk` calls with full hashes and `log ...` with only a bump record → line `Stable` = `UpToDate`, `Version "1.0.0.5"`, `Branch "main"`.
    - `The_round_shows_an_outdated_build_with_its_count`: `log` with two non-bump records → `Outdated`, `NewerCommits 2`.
    - `A_repository_with_scripts_and_no_marker_shows_no_stable_build` → `NoBuild`.
    - `Without_GitHub_the_version_shows_and_nothing_is_compared`: no activity for the folder → `Unknown`, `Version` set, `SinceBuildAsync`'s git calls never made.
    - `The_same_two_commits_are_not_compared_again_next_round`: two rounds, the `log` call is made once.
    - `A_locked_marker_keeps_what_the_line_showed`: second round with the marker held (`FileShare.None`) → same `Stable` as round one.
    - `A_new_head_on_GitHub_reads_git_again`: after a round, raise the GitHub-changed event with a new `DefaultHead` → a git refresh is requested (as `OnGitHubChanged` does for `BehindIsOutOfDate`).

- [ ] **Step 2: Run them, see them fail**
  Run: `dotnet test --project tests/CodeSwitchX.UI.Tests --filter-class "*StableBuildTests"` and `--filter-class "*YardViewModelTests" --filter-method "*build*"`.
  Expected: compile errors.

- [ ] **Step 3: Implement** a private `async Task<StableBuildStatus?> StableStatusAsync(GitLine line, RepoActivity? activity, GitLine? before, CancellationToken ct)` in `YardViewModel`, called from `CompareAsync` for every line it does not carry over (also on the `_gitHub is null` path, with `activity` null), setting `line with { Stable = ... }`:
  - No `StableBuild` or no `MarkerPath` → null.
  - `StableBuildInstall.Read` (already off the UI thread here) → `Unknown`: return `before?.Stable`; `NoBuild` → `new(StableFreshness.NoBuild)`.
  - No commit, no `activity?.DefaultHead`, or no `DefaultBranch` → `new(Unknown, Version, BuiltFrom: commit)`.
  - `before?.Stable` with the same `Version`, `BuiltFrom` and `ComparedWith == DefaultHead.Sha` and `Freshness` not `Unknown` → reuse it.
  - Else `SinceBuildAsync(line.StableBuild.RepoPath, commit, head.Sha, ct)`: known → `UpToDate`/`Outdated` with `NewerCommits`, `Branch = DefaultBranch`, `BuiltFrom`, `ComparedWith = head.Sha`; not known → `before?.Stable` if it had the same `BuiltFrom` and `ComparedWith`, else `new(Unknown, Version, BuiltFrom: commit)`.
  - In `OnGitHubChanged` make `moved |= lines.Any(l => l.BehindIsOutOfDate || l.StableIsOutOfDate)`. In `RefreshGitAsync`'s read-again check (the `shown.Select(...)` near the `RefreshGitOf(tile, after: true)` call), also read again where `applied[i] && compared[i] && l.StableIsOutOfDate`.
  - The default branch stays GitHub's: never `$script:ReleaseBranch` and never the checked-out branch.

- [ ] **Step 4: Run them, see them pass** (same commands), then the whole UI test project: `dotnet test --project tests/CodeSwitchX.UI.Tests`. Expected: all pass (three `HotkeyServiceTests` may fail while the user's stable CodeSwitchX runs; that is known and not a regression).

- [ ] **Step 5: Commit** `feat(yard): the git round works out each stable build's status (#452)`

---

### Task 4: The row on the tile

Depends on Task 3.

**Files:**
- Modify: `src/CodeSwitchX.UI/Yard/WorkspaceTileViewModel.cs`, `src/CodeSwitchX.UI/Yard/StableBuild.cs` (new `StableBuildRow`), `src/CodeSwitchX.UI/Yard/YardView.xaml` (`TileTemplate`)
- Test: `tests/CodeSwitchX.UI.Tests/Yard/StableBuildEntryTests.cs` (it already builds tiles with stable-build lines; add the row tests there)

**Interfaces:**
- Consumes: `GitLine.Stable`, `StableBuildStatus` (Task 3).
- Produces: `public sealed record StableBuildRow(string? Title, StableBuildStatus Status)`; `WorkspaceTileViewModel.StableRows` (`IReadOnlyList<StableBuildRow>`, observable, `[]` by default).

- [ ] **Step 1: Write the failing tests**
  - `A_tile_without_scripts_has_no_stable_rows` → `StableRows.ShouldBeEmpty()`.
  - `One_app_gets_one_row_titled_by_its_version`: one line with `Stable = new(UpToDate, "1.0.0.5", ...)` → one row, `Title "Stable 1.0.0.5"`.
  - `Lines_of_one_install_give_one_row`: two lines whose `StableBuild.MarkerPath` is the same (spelled with different case) → one row.
  - `Several_apps_carry_their_names`: two different marker paths, lines with `Folder "App"` / `"Shop"` → titles `"App · Stable 1.0.0.5"` and `"Shop · Stable 2.0.0.1"`; a `NoBuild` one titled `"Shop"`.
  - `A_lone_app_with_no_build_has_no_title` → `Title.ShouldBeNull()`, `Status.Label "no stable build"`.
  - `The_same_lines_again_keep_the_same_rows_list` → `ShowGit` with equal lines leaves `StableRows` the same instance (no rebuild, no closed tooltip).

- [ ] **Step 2: Run them, see them fail**
  Run: `dotnet test --project tests/CodeSwitchX.UI.Tests --filter-class "*StableBuildEntryTests"`. Expected: compile errors.

- [ ] **Step 3: Implement.** In `WorkspaceTileViewModel`, a `partial void OnGitLinesChanged(IReadOnlyList<GitLine> value)` sets `StableRows` from lines with `Stable` not null and a `StableBuild.MarkerPath`, `DistinctBy(PathNormalizer.Normalize(MarkerPath))`, in line order, named only when more than one row (the name as `StableBuildEntry` makes it: `line.Folder` else the repo folder's name); assign only when it differs (`SequenceEqual`). In `YardView.xaml`, after the "No Claude Code chats yet" `TextBlock` inside the tile's `StackPanel`, add a panel collapsed while `StableRows.Count` is 0, holding a 1 px separator (`Border` with `Height="1"`, `Margin="0,8,0,6"`, `Background` the theme's `{DynamicResource DividerStrokeColorDefaultBrush}`) and an `ItemsControl` over `StableRows`: per row a `TextBlock` `Title` (collapsed when null, `FontSize="12"`, `Opacity="0.8"`) and a pill `Border` with `ToolTip="{Binding Status.ToolTip}"`, styled like the git state pill in `App.xaml` (`CornerRadius="8" Padding="7,0,7,1" Margin="8,0,0,0" BorderThickness="1"`, text `FontSize="11" FontWeight="SemiBold"`): `UpToDate` back `#2622C55E`, border `#8022C55E`, text `#22C55E`; `Outdated` `AmberPillBack`/`AmberPillBorder`/`AmberPillText`; `NoBuild` back `#14FFFFFF`, border `#40FFFFFF`, text with `Opacity="0.75"`; collapsed when `Status.Label` is null. The row has no click of its own (the tile's click goes through).

- [ ] **Step 4: Build and test**
  Run: `dotnet build CodeSwitchX.slnx` → 0 warnings, 0 errors (XAML errors only show here). Then `dotnet test --solution CodeSwitchX.slnx` → all pass apart from the known three `HotkeyServiceTests` while the stable build runs.

- [ ] **Step 5: Commit** `feat(yard): the tile shows its stable build's version and state (#452)`

## Out of scope

- Whether the stable build is running; one-off builds; any change to `scripts/*.ps1` or the marker format; a Build button on the row; Raven's chat header (it shows `GitLineTemplate`, which does not change).
- The on-screen check on the BenQ: the session that hands this plan out does it after the review, because it needs the user's go to close their running stable build.
