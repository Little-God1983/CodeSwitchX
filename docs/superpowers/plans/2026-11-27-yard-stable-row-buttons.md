# Yard tile: stable row buttons Implementation Plan

**Goal:** On a Yard tile, the stable build row (#452) shows its pill at the right edge, a readable version, and two icon buttons
right after the version: Run and Build new version, doing what the right-click entries (#423) do. Issue #454.

**Spec:** `docs/superpowers/specs/2026-11-27-yard-stable-row-buttons-design.md`

**Branch:** `feature/454-stable-row-buttons` (exists, checked out). One PR for the whole issue.

## How it works today (read these first)

- `src/CodeSwitchX.UI/Yard/StableBuild.cs`: `StableBuildStatus`, `StableBuildRow` (a record: `Title`, `Status`), `StableBuildCheck.BuildBlock`,
  `StableBuildEntry` (one repository's `BuildCommand` / `RunCommand`, `BuildBlock`, `RunBlock`, `RunHeader`, `BuildHeader`, `Name`).
- `src/CodeSwitchX.UI/Yard/WorkspaceTileViewModel.cs`: `OnGitLinesChanged` makes `StableRows` (one row per install marker; the same rows
  again keep the list, so an open tooltip stays); `StableBuildEntries()` makes the menu's entries afresh.
- `src/CodeSwitchX.UI/Yard/YardView.xaml`, `TileTemplate`, the block commented "the installed stable builds under a line (#452)":
  the row is a horizontal `StackPanel` with the title and the pill. `ChatRowTemplate` at the top shows how a `Button` inside the
  tile takes the click so the tile's `MouseBinding` (Open) does not fire.
- Tests: `tests/CodeSwitchX.UI.Tests/Yard/StableBuildEntryTests.cs` (its `Launcher` and `Dialogs` fakes, `_tile.ShowGit`, `Line(...)`,
  the row tests around lines 130-175).

## Decisions (all settled, do not reopen)

1. **Rows stay value records.** The buttons' state is plain data on the row, so `StableRows.SequenceEqual(rows)` keeps working and
   an unchanged round does not rebuild the row (which would close tooltips). No command object goes on the row.
2. **The commands live on the tile**, take the row as parameter, and make the entry afresh at the press through
   `StableBuildEntries()`, so a press always uses the lines as they are now. Reason: `StableBuildEntry` is made per use by design (#423).
3. **The greying follows `GitLines`** (the tile's git round). No extra git read for the buttons. Build still reads git at the press
   (`StableBuildEntry.BuildAsync` already does).
4. **A row's buttons act on the first line of its app** (rows are distinct by marker path; a worktree's own build stays in the menu).
5. **Icons:** `Segoe Fluent Icons, Segoe MDL2 Assets`; Run `&#xE768;` (play), Build `&#xE90F;` (wrench).
6. **Layout:** the pill at the right edge; title, version and buttons at the left, buttons directly after the version. When the
   tile is too narrow the lead text trims with an ellipsis; version, buttons and pill are never clipped.
7. **A click on a button, also a greyed one, never opens the window.**

## Global constraints

- Repo `CLAUDE.md` applies. Load the `csharp-conventions` skill before writing C#/XAML. File-scoped namespaces, braces always,
  warnings are errors, field-based `[ObservableProperty]`, no new packages, no new libraries or patterns.
- WPF has no compiled bindings: check every binding path against the type by hand.
- The repo is public: made-up names (`App`, `Shop`) in tests and comments, no other repository's name.
- Git stores LF; do not mix line endings inside a file (keep each file's existing endings).
- Comments say why and carry `(#454)`.
- Commit and push after each task. Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  GitHub account: `gh auth switch --user Little-God1983` before a push. Never delete a branch.
- Do not start the app, and do not run `scripts/build.ps1`. The on-screen check is not part of this plan.
- Three `HotkeyServiceTests` fail while another CodeSwitchX runs (it holds the global hotkeys); that is not a regression.
- If the plan does not fit the code, stop and report; do not improvise.

---

### Task 1: The row carries the buttons' state

**Files:** `src/CodeSwitchX.UI/Yard/StableBuild.cs`, `src/CodeSwitchX.UI/Yard/WorkspaceTileViewModel.cs`,
`tests/CodeSwitchX.UI.Tests/Yard/StableBuildEntryTests.cs`

- Add next to `StableBuildRow` a record
  `StableBuildButtons(string RepoPath, string Name, string BuildName, string? BuildBlock, string? RunName, string? RunBlock)`:
  - `RepoPath`: the entry's repository (`StableBuildRepo.RepoPath`), the key the commands find the entry by.
  - `Name`: `StableBuildEntry.Name`. `BuildName`: `StableBuildEntry.BuildHeader`. `RunName`: `StableBuildEntry.RunHeader` (null = no Run button).
  - Computed: `CanBuild => BuildBlock is null`, `CanRun => RunName is not null && RunBlock is null`,
    `BuildToolTip => BuildBlock ?? BuildName`, `RunToolTip => RunBlock ?? RunName`, `HasRun => RunName is not null`.
  - A static `Of(StableBuildEntry entry)` builds it; give `StableBuildEntry` an `internal string RepoPath` for that.
- `StableBuildRow` becomes `StableBuildRow(string? Title, StableBuildStatus Status, StableBuildButtons? Buttons = null, string? Lead = null)`.
  - `Title` stays as it is (tests and the automation name use it).
  - `Lead` is the dimmed text before the number: `"Stable"` on a lone row with a version, `"App · Stable"` on a named row with a
    version, the app's name alone for no build on a named row, null on a lone row without a version. Add
    `internal static string? LeadOf(StableBuildStatus status, string? name)` beside `TitleOf`.
  - The number itself is `Status.Version` (already there).
- `WorkspaceTileViewModel.OnGitLinesChanged`: for each row's line, find the entry of the same repository in `StableBuildEntries()`
  (compare `PathNormalizer.Normalize` of the repo paths; call `StableBuildEntries()` once per change, not per row) and set
  `Buttons = StableBuildButtons.Of(entry)`, and `Lead`. `StableBuildEntries()` reads `GitLines`, which already holds the new value
  inside `OnGitLinesChanged` (verify; if it does not, pass `value` through an overload).

**Tests** (in `StableBuildEntryTests`, following the row tests there):
- A row of a clean line on main with an installed shortcut: `Buttons.CanBuild` and `CanRun` true, tooltips "Build new version" and "Run".
- A dirty line: `CanBuild` false, `BuildToolTip` is the block text ("2 uncommitted changes - commit or stash first").
- Shortcut missing: `CanRun` false, `RunToolTip` "No stable build yet".
- Two apps on one tile: each row's `Buttons.BuildName` is "Build App" / "Build Shop", `Lead` "App · Stable" / "Shop · Stable".
- The same lines shown twice keep the same `StableRows` instance (the existing equality behaviour still holds with buttons).

**Check:** `dotnet build CodeSwitchX.slnx` green (zero warnings), then
`dotnet test --project tests/CodeSwitchX.UI.Tests --filter-class "*StableBuild*"` all pass.

### Task 2: The tile's two commands (depends on Task 1)

**Files:** `src/CodeSwitchX.UI/Yard/WorkspaceTileViewModel.cs`, `tests/CodeSwitchX.UI.Tests/Yard/StableBuildEntryTests.cs`

- Next to `StableBuildEntries()` add
  `[RelayCommand] private Task BuildStableAsync(StableBuildRow? row)` and `[RelayCommand] private Task RunStableAsync(StableBuildRow? row)`
  (generated `BuildStableCommand`, `RunStableCommand`). Each:
  1. Returns when `row?.Buttons` is null.
  2. Finds the entry in `StableBuildEntries()` by normalized `RepoPath`.
  3. No entry: `_owner.Dialogs.Tell($"Not building {buttons.Name}: its build scripts are not there any more.")` /
     `$"Not starting {buttons.Name}: its build scripts are not there any more."`.
  4. Build: entry with a `BuildBlock`: `Tell($"Not building {entry.Name}: {entry.BuildBlock}.")`; else `await entry.BuildCommand.ExecuteAsync(null)`.
  5. Run: entry that cannot run (`!entry.CanRun`): `Tell($"Not starting {entry.Name}: no stable build yet.")`; else `await entry.RunCommand.ExecuteAsync(null)`.
- Use `ConfigureAwait(true)` as `StableBuildEntry` does (the dialogs are UI).

**Tests:**
- `BuildStableCommand` with the row of a clean line: the dialog asked "Build a new version of app?…" and `Launcher.Built` has the repo.
- The lines turned dirty after the row was made (call `ShowGit` with the dirty line, press with the OLD row): told
  "Not building app: …", nothing built.
- `RunStableCommand`: `Launcher.Ran` has the shortcut path; with the shortcut gone since: told "Not starting app: no stable build yet."
- A row without buttons: nothing asked, told, built or run.

**Check:** build green; `dotnet test --project tests/CodeSwitchX.UI.Tests --filter-class "*StableBuild*"` all pass.

### Task 3: The row's look (depends on Tasks 1 and 2)

**Files:** `src/CodeSwitchX.UI/Yard/YardView.xaml`, `src/CodeSwitchX.UI/Yard/YardView.xaml.cs`

In `TileTemplate`, replace the row's horizontal `StackPanel` (keep the separator, the outer `StackPanel` with its Collapsed trigger, the
pill's styles and colours, and `ToolTip="{Binding Status.ToolTip}"` on the row):

- Root: `DockPanel` (`Margin="0,1"`, `LastChildFill="True"`).
  - First child, `DockPanel.Dock="Right"`: the existing pill `Border`, unchanged but for `Margin="8,0,0,0"`.
  - Second child: an inner `DockPanel` with `HorizontalAlignment="Left"`, so the buttons sit right after the text when there is room
    and the text gives way when there is not. Its children, in this order:
    1. `DockPanel.Dock="Right"`: the buttons' holder, a horizontal `StackPanel` with `Background="Transparent"`, `Margin="6,0,0,0"`,
       `MouseLeftButtonDown="OnStableButtonsMouseDown"`, collapsed when `Buttons` is null.
    2. `DockPanel.Dock="Right"`: the version `TextBlock`: `Text="{Binding Status.Version}"`, `FontSize="13"`, `FontWeight="SemiBold"`,
       `Margin="4,0,0,0"`, collapsed when null. No opacity.
    3. Fill: the lead `TextBlock`: `Text="{Binding Lead}"`, `FontSize="13"`, `Opacity="0.7"`, `TextTrimming="CharacterEllipsis"`,
       collapsed when null (then the version's left margin must not leave a gap: put the 4 px on the lead's right instead).
- Buttons: a view-local style `StableRowButton` in `UserControl.Resources` (pattern: `GitHubPillButton` in `App.xaml`, a template with
  a `Border`): 22 wide, 20 high, `CornerRadius="4"`, transparent background, `#22FFFFFF` on hover, `Cursor="Hand"`, glyph
  `FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"` `FontSize="12"`, `Opacity="0.35"` and `Cursor="Arrow"` when disabled,
  `ToolTipService.ShowOnDisabled="True"`, `Margin="2,0,0,0"`.
  - Run first: content `&#xE768;`, `Command="{Binding DataContext.RunStableCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"`,
    `CommandParameter="{Binding}"`, `IsEnabled="{Binding Buttons.CanRun}"`, `ToolTip="{Binding Buttons.RunToolTip}"`,
    `AutomationProperties.Name="{Binding Buttons.RunName}"`, collapsed when `Buttons.HasRun` is false.
  - Build: content `&#xE90F;`, `BuildStableCommand`, `Buttons.CanBuild`, `Buttons.BuildToolTip`, `AutomationProperties.Name="{Binding Buttons.BuildName}"`.
  - Check that `AncestorType=ItemsControl` resolves to the `StableRows` `ItemsControl`, whose `DataContext` is the tile. A `Command`
    plus an `IsEnabled` binding: the command's CanExecute is always true here, so `IsEnabled` decides; verify the button really greys.
- `YardView.xaml.cs`: `private void OnStableButtonsMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;`
  with a comment why: a press on a greyed button, or between the two, would otherwise reach the tile and open the window (#454).
  A live button handles its own press already.

**Check:** `dotnet build CodeSwitchX.slnx` green (XAML errors only show there), then the whole UI suite:
`dotnet test --project tests/CodeSwitchX.UI.Tests` passes (but for the three `HotkeyServiceTests` if another CodeSwitchX runs).

### Task 4: Pull request (depends on Tasks 1-3)

- `gh pr create` against `main`, title "Yard tile: stable row with pill on the right, readable version, Run and Build buttons (#454)",
  body: what changed, how it was tested, "Closes #454", "On-screen check: pending", ending with the line
  `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Do not merge.

## Out of scope

- A git read on hover; showing a running build or a running app; changes to the menu entries, `StableBuildEntry`'s behaviour or the
  build scripts; the on-screen check and its screenshot (done after this plan by the session that wrote it).
