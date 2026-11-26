# Yard Changes Tab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A click on a tile's "N changed" pill opens the repository panel on a new **Changes** tab that lists the changed files, with Copy all, and per file Open folder and Copy folder path (#444).

**Architecture:** `GitInspector` already runs `git status --porcelain=v2` for the pill's count; it now also parses the paths into a value-equal `ChangedFileList` carried by `GitInfo` and `GitLine`. The Yard hands the list to the open panel each git round (as it does the branch). `GitHubPanelViewModel` gets a third tab, `ChangesTabViewModel`, beside its two GitHub lists; the XAML adds the tab, its rows and a clickable state pill.

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm, xUnit v3 + Shouldly (Microsoft Testing Platform).

**Spec:** `docs/superpowers/specs/2026-11-26-yard-changes-tab-design.md` (read it first).

## Global Constraints

- Branch `feature/444-changes-tab` (exists, holds the spec). Commit and push after each task; message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` and names `(#444)`.
- `dotnet build CodeSwitchX.slnx` green after every task: warnings are errors (file-scoped namespaces, braces always, `_camelCase` fields).
- No new NuGet packages. No new folders: Core types in `src/CodeSwitchX.Core/Workspaces/`, UI in `src/CodeSwitchX.UI/Yard/`.
- Follow the repo's `csharp-conventions` skill (`.claude/skills/csharp-conventions/SKILL.md`) and CLAUDE.md. Load it before writing C#/XAML.
- The repo is public: tests use made-up paths and names only.
- Never mix line endings inside one file; keep each file's existing ending.
- Don't run `scripts/build.ps1` and don't touch `<Version>`.
- Three `HotkeyServiceTests` fail while the user's stable CodeSwitchX runs; that is not a regression.

## Review Focus

1. Paths with spaces, umlauts or quotes: git C-quotes them (`"docs/\303\244 b.md"`). Expect the real name, unquoted (Task 1 test).
2. A rename line (`2 R. … new<TAB>old`), each side possibly quoted. Expect the new path, with the old one kept (Task 1 test).
3. A deleted file whose folder is gone too. Expect Explorer to open the nearest folder above that exists (Task 2 test).
4. A git round with the same files. Expect it neither to replace the tile's `GitLines` (`WorkspaceTileViewModel.cs:115` compares with `SequenceEqual`) nor to reset the tab's rows (scroll position). This needs value equality on the list (Task 1 and Task 2 tests).
5. Git gives no answer (status timed out). Expect no list, no made-up "No changed files.", and the tab saying "Git could not tell what changed." (Task 2 test).

---

### Task 1: Changed files from `git status` (Core)

**Files:**
- Create: `src/CodeSwitchX.Core/Workspaces/ChangedFile.cs`
- Modify: `src/CodeSwitchX.Core/Workspaces/GitInspector.cs` (`GitInfo` record line 13; `InspectAsync` lines 111-136; new parser beside `TrackedChangesIn`, line 309)
- Test: `tests/CodeSwitchX.Core.Tests/Workspaces/GitInspectorTests.cs`, `tests/CodeSwitchX.Core.Tests/Workspaces/GitInspectorRealGitTests.cs`

**Interfaces:**
- Produces:
  - `public enum ChangeKind { Edited, New, Deleted, Renamed, Conflict }`
  - `public sealed record ChangedFile(string Path, string FullPath, ChangeKind Kind, string? OldPath = null)`. `Path` is as git gives it, relative to the repository's top, `/`-separated, unquoted; a new folder git lists as one entry keeps its trailing `/`. `FullPath` is `Path.GetFullPath(Path.Combine(top, Path.TrimEnd('/').Replace('/', '\\')))`. `public bool IsFolder => Path.EndsWith('/')`.
  - `public sealed class ChangedFileList : IEquatable<ChangedFileList>` with `public IReadOnlyList<ChangedFile> Files { get; }`, constructor `ChangedFileList(IEnumerable<ChangedFile> files)` that sorts by `Path` (`StringComparer.OrdinalIgnoreCase`), and value equality: `Equals` compares `Files` with `SequenceEqual`, `GetHashCode` combines the count and the files, plus `==`/`!=`.
  - `GitInfo` gains a last parameter `ChangedFileList? ChangedFiles = null`. It is null when the status gave no answer and set (possibly empty) whenever `DirtyCount` is set.
  - `internal static ChangedFileList ChangedFilesIn(IEnumerable<string> statusLines, string top)` in `GitInspector`.

- [ ] **Step 1: Write the failing tests** in `GitInspectorTests.cs`, as a `[Theory]` over single porcelain v2 lines with `top = @"E:\Repo"` (made up):
  - `1 .M N... 100644 100644 100644 aaaa aaaa src/a b.cs` → `("src/a b.cs", @"E:\Repo\src\a b.cs", Edited)`. The space must survive: split lines of type `1` into at most 9 fields.
  - `1 A. N... 000000 100644 100644 0000 aaaa new.cs` → `New`. `1 .D N...` and `1 D. N...` → `Deleted`.
  - `2 R. N... 100644 100644 100644 aaaa aaaa R100 src/new.cs\tsrc/old.cs` → `("src/new.cs", …, Renamed, OldPath: "src/old.cs")`. Split type `2` into at most 10 fields; the last one is `path<TAB>origPath`. `C` (copy) is Renamed too.
  - `u UU N... 100644 100644 100644 100644 aaaa bbbb cccc both.cs` → `Conflict` (at most 11 fields).
  - `? docs/new/` → `New`, `IsFolder` true, `FullPath` `E:\Repo\docs\new`.
  - `? "docs/\303\244 \"q\".md"` → `Path` `docs/ä "q".md`. Unquoting: when a path starts and ends with `"`, drop them and decode the C escapes `\\ \" \t \n \r \a \b \f \v` and `\ooo` (three octal digits = one byte). Gather the bytes and decode them as UTF-8. Apply this to each side of a rename separately.
  - `# branch.oid …` and `# branch.head main` lines yield nothing. A line that fits no format is skipped.
  - Plus a `[Fact]`: two `ChangedFileList`s built from equal files in different orders are equal, have equal hash codes, and list the files sorted by path.
  - Plus a `[Fact]` extending `Inspect_counts_porcelain_status_lines` (line 51): `info.ChangedFiles!.Files.Count` equals `info.DirtyCount`. Update the `info.ShouldBe(new GitInfo(…))` expectation at line 66 to carry the expected list.
  - Plus a `[Fact]`: when the status run returns null, `ChangedFiles` is null.
- [ ] **Step 2: Run them and see them fail.** Run `dotnet test --project tests/CodeSwitchX.Core.Tests --filter-class "*GitInspectorTests"`. Expected: build errors or failures on the new members.
- [ ] **Step 3: Implement.** Write `ChangedFile.cs` and `ChangedFilesIn`. In `InspectAsync`, pass `ChangedFilesIn(lines, top!)` when `lines` is not null (`top` is never null where `gitDir` is not, see `ResolveGitDir` line 461). Document each new member in the repo's comment style.
- [ ] **Step 4: Add a real-git test** to `GitInspectorRealGitTests.cs`, following that file's `RealGitRepos` pattern: a repo with one edited file, one file named `ä b.txt`, and one renamed and staged file (`git mv`). `ChangedFiles` must hold all three, with their real names and kinds.
- [ ] **Step 5: Run the tests and see them pass.** Run `dotnet test --project tests/CodeSwitchX.Core.Tests --filter-class "*GitInspector*"`. Expected: all pass. Then run `dotnet build CodeSwitchX.slnx` and expect 0 warnings.
- [ ] **Step 6: Commit and push.** Message: `feat(core): git status lists the changed files, not only their count (#444)`.

### Task 2: The Changes tab in the panel (ViewModel)

**Files:**
- Create: `src/CodeSwitchX.UI/Yard/ChangesTabViewModel.cs`, `src/CodeSwitchX.UI/Yard/ChangedFileRow.cs`
- Modify: `src/CodeSwitchX.UI/Yard/GitHubPanelViewModel.cs`, `src/CodeSwitchX.UI/Yard/PanelCheckout.cs`
- Test: create `tests/CodeSwitchX.UI.Tests/Yard/GitHubListViewModelTests.Changes.cs` (a partial of `GitHubListViewModelTests`; reuse its fakes `FakeGh`, `FakeClipboard`, `Browsers()` and its panel construction at line 1629)

**Interfaces:**
- Consumes: `ChangedFile`, `ChangedFileList`, `ChangeKind` (Task 1).
- Produces:
  - `PanelCheckout` gains a last parameter `ChangedFileList? Files = null`.
  - `public sealed record ChangedFileRow(ChangedFile File)` with:
    - `StateText`: `edited`, `new`, `deleted`, `renamed`, `conflict`.
    - `DisplayPath`: `Path`, or `$"{Path} ← {OldPath}"` for a rename.
    - `FullPath`.
    - `OpenFolderName`: `$"Open the folder of {Path}"`.
    - `CopyFolderName`: `$"Copy the folder path of {Path}"`.
  - `public sealed partial class ChangesTabViewModel : ObservableObject` with:
    - `[ObservableProperty] IReadOnlyList<ChangedFileRow> _rows` (starts `[]`).
    - `[ObservableProperty] bool _isShown`, `[ObservableProperty] bool _isUnknown`.
    - `CountText`: the row count, `""` when unknown.
    - `TabName`: `"Changes"`.
    - `TabAutomationName`: same pattern as `GitHubListViewModel.TabAutomationName`.
    - `EmptyText`: `"No changed files."` when known and empty, `"Git could not tell what changed."` when unknown, null otherwise.
    - `public void Follow(ChangedFileList? files)`: null sets `IsUnknown`. It replaces `Rows` only when the list differs from the last one it took, by value.
  - `GitHubPanelViewModel`:
    - Constructor gains `bool showChanges = false, Func<ProcessStartInfo, bool>? start = null` after `checkout`. `start` defaults to `ShellStart.TryStart`.
    - `public ChangesTabViewModel? Changes`: present only with a checkout, filled from `checkout.Files`.
    - `[ObservableProperty] bool _isChangesShown`.
    - `public void FollowChanges(ChangedFileList? files)`.
    - Commands `ShowChangesCommand`, `OpenFolderCommand(ChangedFileRow)`, `CopyFolderPathCommand(ChangedFileRow)`, `CopyAllPathsCommand`.

- [ ] **Step 1: Write the failing tests** (names are a guide; the asserts are the requirement):
  - `Opened_on_changes_shows_the_files_and_reads_no_list`: built with `showChanges: true` and a checkout with 2 files. `IsChangesShown` is true, `Changes.IsShown` is true, both GitHub lists have `IsShown` false, and after `LoadAsync` the fake gh got no call.
  - `Show_switches_between_changes_and_a_list`: `ShowCommand(Issue)`, then exactly Issues is shown and `IsChangesShown` is false. `ShowChangesCommand`, then only Changes is shown, and the checkout's `ReadGit` was invoked once.
  - `No_checkout_no_changes_tab`: without a checkout, `Changes` is null.
  - `Copy_all_paths_puts_full_paths_one_to_a_line`: the clipboard holds `FullPath`s joined by `Environment.NewLine` in row order. `CopyMessage` is `"Copied 2 paths."`, or `"Copied 1 path."` for one.
  - `Copy_folder_path_copies_the_folder`: a file row copies `Path.GetDirectoryName(FullPath)`, and a folder row (`docs/new/`) copies its own full path. `CopyMessage` is `$"Copied {folder}."`.
  - `Open_folder_selects_an_existing_file`: in a temp directory, an existing file gives `ProcessStartInfo("explorer.exe", $"/select,\"{full}\"")`.
  - `Open_folder_of_a_deleted_file_opens_the_nearest_folder_there_is`: `sub/gone/x.cs`, where only `sub` exists, gives `("explorer.exe", $"\"{tempDir}\\sub\"")`. A folder row that exists opens itself.
  - `Open_folder_says_so_when_nothing_opens`: a `start` that returns false sets `OpenMessage` to `$"Explorer did not open {folder}."`. When no folder up to the root exists, `OpenMessage` is `$"Can't open a folder for {Path}: it is gone."`.
  - `Follow_with_the_same_files_keeps_the_rows`: `FollowChanges` with an equal new list keeps the same `Rows` instance. A different list replaces it.
  - `Unknown_changes_say_so`: `FollowChanges(null)` gives `IsUnknown` true and `EmptyText` `"Git could not tell what changed."`. An empty list gives `"No changed files."`.
- [ ] **Step 2: Run them and see them fail.** Run `dotnet test --project tests/CodeSwitchX.UI.Tests --filter-class "*GitHubListViewModelTests"`.
- [ ] **Step 3: Implement.**
  - `Shown` stays the last GitHub list in front. `IsChangesShown` hides both lists (`IsShown` false); `Show(kind)` sets `IsChangesShown` false and makes exactly that list shown, even when it was `Shown` already.
  - `LoadAsync` reads nothing when the panel opened on Changes. `Show` already starts a list that has not started.
  - `ShowChanges` invokes `_checkout.ReadGit`, as `OpenMerge` does (line 253), so the list is fresh.
  - Copy messages go through the panel's existing `Copy(text, message)`.
  - `Close` stays as is.
- [ ] **Step 4: Run the tests and see them pass.** Same command; all pass. Then run `dotnet build CodeSwitchX.slnx` and expect 0 warnings.
- [ ] **Step 5: Commit and push.** Message: `feat(yard): the panel's Changes tab, with copy and open folder (#444)`.

### Task 3: The Yard opens it and keeps it current

**Files:**
- Modify:
  - `src/CodeSwitchX.UI/Yard/GitLine.cs`: new last parameter, and `HasChangesToShow`.
  - `src/CodeSwitchX.UI/Yard/YardViewModel.cs`: every place that sets `TrackedChanges` from a `GitInfo` (lines 810-811, 1285-1286, 1291-1300, 1372-1373); `OpenPanel` (1528); `FollowPanelBranch` (1229); new command beside `OpenGitHubLine` (1518).
- Test: create `tests/CodeSwitchX.UI.Tests/Yard/YardGitHubTests.Changes.cs` (a partial of `YardGitHubTests`, following `YardGitHubTests.Pull.cs`); add to `tests/CodeSwitchX.UI.Tests/Yard/GitLineTests.cs`.

**Interfaces:**
- Consumes: Task 1's `GitInfo.ChangedFiles`; Task 2's `PanelCheckout.Files`, the `showChanges` constructor argument, and `FollowChanges`.
- Produces:
  - `GitLine` gains a last parameter `ChangedFileList? ChangedFiles = null`, and `public bool HasChangesToShow => GitHub is not null && DirtyCount > 0`.
  - `YardViewModel.OpenChangesCommand(GitLine?)`: for a line with `GitHub`, it calls `OpenPanel(activity, GitHubPanelViewModel.DefaultKind(activity), line, showChanges: true)`, then `ReadGitOfCheckout(line.Path)`. Otherwise it does nothing.

- [ ] **Step 1: Write the failing tests:**
  - `GitLineTests`: two lines equal but for separate, equal `ChangedFileList` instances are `Equals` (so `SequenceEqual` holds). `HasChangesToShow` is false for `DirtyCount` 0, for null, and without `GitHub`.
  - `YardGitHubTests.Changes`: `OpenChangesCommand` on a dirty line opens a panel with `IsChangesShown` true, and its `Changes.Rows` are the line's files. A git round that returns another file list updates the open panel's rows. A round with the same list keeps the same `Rows` instance. `OpenGitHubLineCommand` still opens on Pull requests or Issues with `IsChangesShown` false.
- [ ] **Step 2: Run them and see them fail.** Run `dotnet test --project tests/CodeSwitchX.UI.Tests --filter-class "*YardGitHubTests" --filter-class "*GitLineTests"`.
- [ ] **Step 3: Implement.** Set `ChangedFiles = info.ChangedFiles` (or `root.ChangedFiles`) wherever `TrackedChanges` is set from a `GitInfo`. `OpenPanel` gets a `bool showChanges = false` parameter and passes `line.ChangedFiles` into `PanelCheckout`. `FollowPanelBranch` also calls `GitHubPanel.FollowChanges(line.ChangedFiles)`.
- [ ] **Step 4: Run the tests and see them pass.** Run the same command, then the whole UI suite with `dotnet test --project tests/CodeSwitchX.UI.Tests`. Expected: all pass except the three known `HotkeyServiceTests` if the stable build runs. Then run `dotnet build CodeSwitchX.slnx` and expect 0 warnings.
- [ ] **Step 5: Commit and push.** Message: `feat(yard): the changed pill opens the Changes tab and the tab follows git (#444)`.

### Task 4: The pill button, the tab and its rows (XAML)

**Files:**
- Modify:
  - `src/CodeSwitchX.UI/App.xaml`: the state pill in `GitLineContent`, lines 143-173.
  - `src/CodeSwitchX.UI/Yard/YardView.xaml`: the panel, lines 395-674.
- Test: `tests/CodeSwitchX.UI.Tests/Yard/GitHubListViewModelTests.Changes.cs` (from Task 2), using the `PanelView` helper of `GitHubListViewModelTests.cs` (line 1112).

**Interfaces:**
- Consumes: everything from Tasks 2 and 3, by binding.

- [ ] **Step 1: Write the failing view test** `Changes_tab_renders_rows_with_both_buttons`. Build a panel opened on Changes with one file, `src/a.cs`. The rendered tree holds buttons named `Open the folder of src/a.cs` and `Copy the folder path of src/a.cs`, and a button named `Copy all paths`. The `Copy all links` button is collapsed.
- [ ] **Step 2: Run it and see it fail.**
- [ ] **Step 3: Implement.**
  - **Pill (`App.xaml`).** Where the line is on a tile (`yard:GitPills.IsShown`, as the issue/PR pills at line 115 use) and `HasChangesToShow` is true, the state pill is a `Button`. Give it the amber look of today's pill and `Cursor="Hand"`. Its command is `DataContext.OpenChangesCommand` of the `yard:YardView` ancestor, with the `GitLine` as parameter. Its `ToolTip` is `"Show the changed files"`, and its `AutomationProperties.Name` is `"Show the changed files"`. Elsewhere (the "clean" pill, a line not on GitHub, Raven's chat header) the existing `Border` stays exactly as it is. The click must be the pill's own, not the line button's, like the issue/PR pills (`GitHubPillButton` style, line 35, shows how).
  - **Tab (`YardView.xaml`).** After the `Tabs` ItemsControl (line 584), in the same row, add one tab bound to `Changes`, collapsed when it is null. It is a `GitHubTabButton` with `TabName` and `CountText` (same hidden "999+" width trick), bound to `ShowChangesCommand`. It has no ↗ arrow. In front, it has `AmberPillBack` behind it and `AmberPillText` as its foreground.
  - **Header row (line 656).** `Copy all links` shows when `IsChangesShown` is false. A `Copy all paths` button bound to `CopyAllPathsCommand` (tooltip: "Puts the full path of every changed file on the clipboard, one to a line") shows when it is true. `CopyMessage` stays.
  - **Lines (662-667).** The `Shown.*` failure and dim lines are collapsed while `IsChangesShown`. Add a dim line bound to `Changes.EmptyText` that shows only while `IsChangesShown`.
  - **Rows.** Add a third `ItemsControl` in the rows `Grid` (line 669): `ItemsSource="{Binding Changes.Rows}" Tag="{Binding Changes}"`, styled `BasedOn="{StaticResource GitHubRows}"` with its own `ItemTemplate`. Its existing `Tag.IsShown` trigger already shows it only in front. Each row is a `DockPanel`:
    - Right: two icon buttons in the copy-link button's style (line 452): open folder (glyph `&#xE838;`, tooltip "Open folder", name `OpenFolderName`) and copy folder path (glyph `&#xE8C8;`, tooltip "Copy folder path", name `CopyFolderName`).
    - Left: `StateText` in amber, small, fixed width.
    - Then `DisplayPath`, trimmed with ellipsis, with `FullPath` as its tooltip.
- [ ] **Step 4: Run it and see it pass.** Run `dotnet build CodeSwitchX.slnx` (XAML errors only show here) and expect 0 warnings. Then run `dotnet test --project tests/CodeSwitchX.UI.Tests --filter-class "*GitHubListViewModelTests"` and expect all to pass.
- [ ] **Step 5: Commit and push.** Message: `feat(yard): changed pill button, Changes tab and its rows on screen (#444)`.

### Task 5: Full suite and on-screen check

- [ ] **Step 1:** Run `dotnet test --solution CodeSwitchX.slnx`. Expected: all pass except the three known `HotkeyServiceTests` while the stable build runs.
- [ ] **Step 2: On-screen check, BenQ monitor only** (hardware ID `BNQ7F05`, the non-primary 16:9 screen left of X=0; never the LG). Read the memory note `running-the-app-from-claude.md` in `C:\Users\Little God\.claude\projects\e--Repos-CodeSwitchX\memory\` first.
  - Start the Debug build of `CodeSwitchX.exe`, with `ELECTRON_RUN_AS_NODE` stripped.
  - **Do not close the user's stable build.** If it blocks the second instance, stop and report.
  - Make a throwaway edit in a scratch repo the Yard shows. Do not use this repo's own working tree.
  - Check:
    - (a) The pill shows the hand cursor and opens the panel on Changes.
    - (b) The rows match the files.
    - (c) Open folder opens Explorer with the file selected.
    - (d) Both copy buttons put the expected text on the clipboard.
    - (e) Clicking the line, not the pill, still opens Pull requests or Issues.
    - (f) A "clean" pill is not a button.
  - Capture a screenshot with PrintWindow. Close only the windows you opened (WM_CLOSE), and remove the scratch edit.
- [ ] **Step 3:** Push and report.

## Out of scope

- Diffs, opening a file in VS Code, stage/commit/discard.
- Repositories not on GitHub.
- Raven's chat header pill.
- Expanding a new folder into its files (`--untracked-files=all`).
- A pull request: the session model opens it after checking your report.
