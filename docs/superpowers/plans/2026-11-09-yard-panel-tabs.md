# Yard panel tabs (#406) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A click anywhere on a tile's repository line opens one GitHub panel with a Pull requests tab and an Issues tab, each with a ↗ that opens the repository's `/pulls` or `/issues` page.

**Architecture:** `GitHubListViewModel` shrinks to one kind's list (rows, load state, count). A new `GitHubPanelViewModel` holds both lists, the shown one, and everything the user does in the panel (open, copy, close). `GitLine` carries its repository's `RepoActivity` even when both counts are 0, and the tile's line becomes a button that opens the panel.

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm, xUnit v3 + Shouldly + NSubstitute (Microsoft Testing Platform).

**Spec:** `docs/superpowers/specs/2026-11-09-yard-merge-and-pull-design.md` (sections "Opening the panel", "The panel: layout A1", and the default-tab call). Issue #406. Branch `feature/406-yard-panel-tabs` (the design is already committed there).

## Global Constraints

- Load the `csharp-conventions` skill before writing C#/XAML. File-scoped namespaces, braces always, `_camelCase` fields, field-based `[ObservableProperty]`, warnings are errors.
- Build: `dotnet build CodeSwitchX.slnx` must be green (zero warnings) after every task.
- Tests: `dotnet test --project tests/CodeSwitchX.UI.Tests --filter-class "*<Class>"`; the whole UI suite before the PR. Three `HotkeyServiceTests` fail while the stable build runs; that is not a regression.
- The repository is public: test data uses `acme/app` (as the existing tests do), never a private repository name or title. Screenshots only of public or scratch repositories.
- Copy, exactly: tab names `Pull requests {n}` and `Issues {n}`; empty tabs `No open pull requests.` and `No open issues.`; ↗ tooltips `Open all pull requests on GitHub` / `Open all issues on GitHub`; line automation name `Open issues and pull requests of {repo name}`.
- URLs: `{GitHubRepo.Url}/pulls` and `{GitHubRepo.Url}/issues`, opened through `GitHubBrowsers.Open(account, url)` (the account's browser).
- Default tab: Pull requests when `RepoActivity.OpenPullRequests > 0`, else Issues. A pill click opens its own kind's tab.
- Hover and click on a line never change the tile's size or layout.
- Never `gh auth switch`; no titles or repository names in log messages (existing rule in `GitHubListViewModel.ReadAsync`).

## Review Focus

1. A line whose repository is not on GitHub, or was never read (no `RepoActivity`): it is not a button, and a click on it still opens the workspace like the rest of the tile. Test in Task 3 plus the on-screen check in Task 4.
2. The background tab's read fails while the shown tab's read worked: only that tab shows "Couldn't load the latest list. Showing what was last read." when shown; the other tab is untouched. Test in Task 2.
3. Closing the panel while the second read is still running: that read is cancelled, and neither the tile nor the panel changes afterwards. Test in Task 2.
4. Both reads finish: the tile's issue count and PR count both update (the reads run one after the other, so neither `GitHubActivity.Replace` loses to the other's `since`). Test in Task 2.
5. Keyboard: Tab goes heading → Pull requests → its ↗ → Issues → its ↗ → Copy all links → rows; Escape closes; closing a panel opened from a line by keyboard gives the keyboard back to that line. Tests in Tasks 2 and 3.

---

### Task 1: The line knows its repository even with nothing open

**Files:**
- Modify: `src/CodeSwitchX.UI/Yard/GitLine.cs` (record parameters)
- Modify: `src/CodeSwitchX.UI/Yard/YardViewModel.cs:790-798` (`WithGitHub`)
- Test: `tests/CodeSwitchX.UI.Tests/Yard/YardGitHubTests.cs`

**Interfaces:**
- Produces: `GitLine.GitHub` (`RepoActivity?`, new last positional parameter `RepoActivity? GitHub = null`): the activity last read for the line's repository, set whenever `GitHubActivity.ForFolder(path)` has one, whatever the counts. Pills stay as today (null at count 0).

- [ ] **Step 1: Write the failing tests** in `YardGitHubTests` (reuse the file's existing tile/activity setup):
  - `A_repository_with_nothing_open_keeps_its_activity_on_the_line`: activity read with 0 issues and 0 PRs → `line.GitHub.ShouldNotBeNull()`, `line.GitHub.Repo.Slug.ShouldBe("acme/app")`, `line.Issues.ShouldBeNull()`, `line.PullRequests.ShouldBeNull()`.
  - `A_line_without_a_github_origin_has_no_activity`: folder whose origin is not GitHub → `line.GitHub.ShouldBeNull()`.
- [ ] **Step 2: Run them, expect FAIL** (`GitHub` does not exist): `dotnet test --project tests/CodeSwitchX.UI.Tests --filter-class "*YardGitHubTests"`.
- [ ] **Step 3: Add the parameter and set it in `WithGitHub`** (`GitHub = activity`). Update the `<param>` doc comment.
- [ ] **Step 4: Run `YardGitHubTests` and `GitPillsXamlTests`, expect PASS.**
- [ ] **Step 5: Build the solution and commit:** `feat(yard): a git line keeps its GitHub activity with nothing open (#406)`.

### Task 2: One panel, two tabs

**Files:**
- Create: `src/CodeSwitchX.UI/Yard/GitHubPanelViewModel.cs`
- Modify: `src/CodeSwitchX.UI/Yard/GitHubListViewModel.cs` (shrinks to one kind's list)
- Modify: `src/CodeSwitchX.UI/Yard/YardViewModel.cs:1100-1118, 1390` (`GitHubList` → `GitHubPanel`)
- Modify: `src/CodeSwitchX.UI/Yard/YardView.xaml:9, 390-500` (panel markup, Escape binding)
- Modify: `src/CodeSwitchX.UI/Yard/YardView.xaml.cs:23`, `src/CodeSwitchX.UI/Shell/ShellViewModel.cs:531`
- Test: `tests/CodeSwitchX.UI.Tests/Yard/GitHubListViewModelTests.cs` (keeps its name; it tests the panel and its lists), `tests/CodeSwitchX.UI.Tests/Shell/ShellViewModelTests.cs:50-80`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces:
  - `GitHubListViewModel(GitHubPill pill, GitHubReader reader, GitHubActivity activity, IUiDispatcher ui, TimeProvider time, ILogger logger)`. It keeps `Rows`, `LoadMessage`, `CutShortMessage`, `SkippedMessage`, `IsLoading`, `LoadingMessage`, `LoadAsync(ct)`, `Loaded`, and adds `GitHubItemKind Kind`, observable `int Count` (the pill's count, then `TotalGiven`/stored total after a load), `string TabText` ("Pull requests 2"/"Issues 5", raised with `Count`), `string? EmptyText` (the empty copy when `!IsLoading && Rows.Count == 0 && LoadMessage is null`, else null), `void Stop()` (cancels its read). It loses `Heading`, `AccountLine`, `OpenRepositoryText`, `OpenMessage`, `CopyMessage`, and the Close, OpenRow, OpenRepository, CopyRowLink and CopyAllLinks commands, plus the browsers, clipboard and closed parameters. Those move to the panel.
  - `GitHubPanelViewModel(RepoActivity activity, GitHubItemKind shown, GitHubReader reader, GitHubActivity gitHub, GitHubBrowsers browsers, ITextClipboard clipboard, IUiDispatcher ui, TimeProvider time, ILogger logger, Action<GitHubPanelViewModel> closed)`, with:
    - `string Heading` (= `Repo.Name`), `string AccountLine`, `string OpenRepositoryText`.
    - `GitHubListViewModel PullRequests`, `GitHubListViewModel Issues`, observable `GitHubListViewModel Shown`, `bool IsPullRequestsShown`.
    - Observable `string? OpenMessage` and `string? CopyMessage` (lifetime `CopyMessageLifetime`, moved).
    - Commands `Show(GitHubItemKind)`, `OpenOnGitHub(GitHubItemKind)`, `OpenRepository()`, `OpenRow(GitHubRow)`, `CopyRowLink(GitHubRow)`, `CopyAllLinks()` (acting on `Shown`, CanExecute = `Shown.Rows.Count > 0`, re-raised when `Shown` or its rows change), `Close()`.
    - `Task LoadAsync(CancellationToken ct)`: loads `Shown` first, then the other list, one after the other. `internal Task Loaded`.
    - `static GitHubItemKind DefaultKind(RepoActivity a)`.
  - `YardViewModel.GitHubPanel` (`GitHubPanelViewModel?`) replaces `GitHubList`. `OpenGitHubListCommand(GitHubPill)` stays as the pill's command and opens `new GitHubPanelViewModel(pill.Activity, pill.Kind, ...)` through `private void OpenPanel(RepoActivity activity, GitHubItemKind kind)` (Task 3 reuses it).

- [ ] **Step 1: Adapt the existing tests to the new shape**, with no change in what they assert. Replace the `List(...)` helper with `Panel(activity, kind)`, which builds a `GitHubPanelViewModel`. Then:
  - Row, load and loading assertions read `panel.Shown.*` or `panel.Issues.*`.
  - Open, copy and close calls go to the panel.
  - `yard.GitHubList` becomes `yard.GitHubPanel`.
  - The XAML helper `Panel(GitHubListViewModel?)` becomes `PanelView(GitHubPanelViewModel?)`, with DataContext `new { GitHubPanel = panel }`.
  - `Heading_names_the_repo_and_count` becomes `Heading_names_the_repo`, with `panel.Heading.ShouldBe("app")`.

  `FakeGh` gets `List<string> Asked`, which records `arguments[0]` of each list read.
- [ ] **Step 2: Write the new failing tests** in the same file:
  - `A_pill_opens_its_own_tab`: opening from the PR pill gives `panel.Shown.ShouldBeSameAs(panel.PullRequests)`.
  - `The_default_tab_is_pull_requests_when_any_are_open_else_issues`: `GitHubPanelViewModel.DefaultKind` gives PullRequest for an activity with 1 PR and 12 issues, and Issue for 0 PRs and 12 issues.
  - `Tabs_name_their_kind_and_count`: `PullRequests.TabText` is "Pull requests 1" and `Issues.TabText` is "Issues 12". After an issue list of 19 loads, `Issues.TabText` is "Issues 19".
  - `Show_switches_the_shown_list`: `ShowCommand.Execute(GitHubItemKind.Issue)` makes `Shown` be `Issues` and `IsPullRequestsShown` false.
  - `The_shown_kind_is_read_first_then_the_other`: opened on issues, answer both lists, and `_gh.Asked` is `["issue", "pr"]`.
  - `Both_reads_update_the_tile`: the issue list has 4 and the PR list has 2, so `activity.ForFolder("app")` has `OpenIssues` 4 and `OpenPullRequests` 2.
  - `A_failed_second_read_only_marks_its_own_tab`: issues answer `ListOf(3)` and PRs answer null. Then `Issues.LoadMessage` is null and `PullRequests.LoadMessage` is "Couldn't load the latest list. Showing what was last read."
  - `Closing_during_the_second_read_cancels_it_and_changes_nothing`: the first list answers, close, then the second answers. The second list's rows and the tile's PR count are unchanged.
  - `The_arrow_opens_all_pull_requests_or_issues_on_github`: `OpenOnGitHubCommand` with PullRequest starts a browser with `https://github.com/acme/app/pulls`, and with Issue with `.../issues`. The panel stays open.
  - `An_arrow_the_browser_cannot_open_shows_its_line`: start returns false, so `OpenMessage` is not null.
  - `An_empty_tab_says_so_once_loaded`: the PR list answers `[]`, so `PullRequests.EmptyText` is "No open pull requests.". It is null while loading.
  - `Copy_all_links_copies_the_shown_tab`: the clipboard holds only the shown tab's URLs.
  - XAML (via `PanelView`): `The_tabs_and_arrows_are_tab_stops_in_order`. Tab stop automation names, in order: `Open acme/app on GitHub`, `Show pull requests`, `Open all pull requests on GitHub`, `Show issues`, `Open all issues on GitHub`, `Copy all links`, then rows.
- [ ] **Step 3: Run the suite, expect the new tests to FAIL** (compile errors first): `dotnet test --project tests/CodeSwitchX.UI.Tests --filter-class "*GitHubListViewModelTests"`.
- [ ] **Step 4: Implement** `GitHubPanelViewModel`, the slimmer `GitHubListViewModel`, the Yard, Shell and code-behind renames, and the panel markup:
  - Header: the heading button as today, bound to the panel, then the account line.
  - Tab strip: two pairs of buttons, a tab-name button (template-less, `ShowCommand`, `CommandParameter="{x:Static github:GitHubItemKind.PullRequest}"`) and a ↗ button (`OpenOnGitHubCommand`, the tooltip copy above).
  - Shown tab: PR colours `#2D464B` / `#A6E3E9`, issue colours `#3A4250` / `#B9CDF0` (the pills' colours), chosen by a `DataTrigger` on `IsPullRequestsShown`.
  - Below: Copy all links with `CopyMessage`, the failure lines bound to `Shown.*` and `OpenMessage`, `EmptyText` as a dim line, and the rows `ItemsControl` with `ItemsSource="{Binding Shown.Rows}"`. Its row commands keep `RelativeSource AncestorType=ItemsControl`, which now resolves to the panel.
- [ ] **Step 5: Run `GitHubListViewModelTests`, `GitHubPanelFocusTests`, `ShellViewModelTests`, expect PASS.**
- [ ] **Step 6: Build the solution and commit:** `feat(yard): one GitHub panel with pull request and issue tabs (#406)`.

### Task 3: The repository line opens the panel

**Files:**
- Modify: `src/CodeSwitchX.UI/App.xaml:84-130` (`GitLineTemplate`)
- Modify: `src/CodeSwitchX.UI/Yard/YardViewModel.cs` (new command)
- Modify: `src/CodeSwitchX.UI/Yard/GitHubPanelFocus.cs:40-135` (opener and hand-back)
- Test: `tests/CodeSwitchX.UI.Tests/Yard/YardGitHubTests.cs`, `tests/CodeSwitchX.UI.Tests/Yard/GitPillsXamlTests.cs`, `tests/CodeSwitchX.UI.Tests/Yard/GitHubPanelFocusTests.cs`

**Interfaces:**
- Consumes: `GitLine.GitHub` (Task 1), `YardViewModel.OpenPanel(RepoActivity, GitHubItemKind)` and `GitHubPanelViewModel.DefaultKind` (Task 2).
- Produces: `YardViewModel.OpenGitHubLineCommand` (`[RelayCommand(CanExecute = nameof(CanOpenGitHubLine))] void OpenGitHubLine(GitLine? line)`, `bool CanOpenGitHubLine(GitLine? line) => line?.GitHub is not null` plus the readers being there).

- [ ] **Step 1: Write the failing tests:**
  - `YardGitHubTests.A_line_opens_the_panel_on_pull_requests_when_any_are_open`: 1 PR → `yard.GitHubPanel.Shown.Kind.ShouldBe(GitHubItemKind.PullRequest)`, and `A_line_with_no_pull_requests_opens_on_issues`.
  - `YardGitHubTests.A_line_with_nothing_open_opens_two_empty_tabs`: 0/0 → panel opens, `Shown.Kind` is Issue.
  - `YardGitHubTests.A_line_without_github_cannot_open_the_panel`: `OpenGitHubLineCommand.CanExecute(line)` is false for `GitHub == null`.
  - `GitPillsXamlTests.A_tile_line_on_github_is_one_button_named_for_the_repository`: in the tile (`GitPills.IsShown` true) with `GitHub` set, there is a `Button` whose `DataContext` is the line and whose automation name is "Open issues and pull requests of app"; the pills are still inside it.
  - `GitPillsXamlTests.A_line_without_github_or_in_ravens_header_is_no_button`: for `GitHub == null`, or `IsShown` false, there is no button with the line as `DataContext`.
  - `GitPillsXamlTests.Hovering_the_line_keeps_its_size`: the line's `DesiredSize` with the hover style forced (set the button's hover background directly or `IsMouseOver` via a trigger test hook) equals the plain one.
  - `GitHubPanelFocusTests.Closing_a_panel_a_line_opened_hands_the_keyboard_back_to_that_line`, mirroring the existing pill hand-back test.
- [ ] **Step 2: Run them, expect FAIL.**
- [ ] **Step 3: Implement.**
  - `OpenGitHubLine` calls `OpenPanel(line.GitHub, GitHubPanelViewModel.DefaultKind(line.GitHub))`.
  - In `GitLineTemplate`, when `GitPills.IsShown` is true and `GitHub` is set, the existing line `Grid` sits inside a `Button`:
    - Template: a `Border` with constant `BorderThickness="1"`, `CornerRadius="5"`, transparent brushes, and on `IsMouseOver` the background `#3D4A5C` and border `#6B85A8`.
    - Command: `DataContext.OpenGitHubLineCommand` via `RelativeSource AncestorType=yard:YardView`, with `CommandParameter="{Binding}"` and `Cursor="Hand"`.
    - Otherwise the plain `Grid`, as today. Use two `DataTemplate`s chosen by a `DataTrigger`/`ContentControl` style, or a template selector in `UI/Infrastructure`, whichever needs less markup.
  - The pill buttons keep their own command, so a click on a pill opens its own tab.
  - `GitHubPanelFocus.OnClick` also records a `Button { DataContext: GitLine { GitHub: { } a } }` opener (kind null). `HandBack` then looks for that line's button first and keeps today's pill order after it.
- [ ] **Step 4: Run `YardGitHubTests`, `GitPillsXamlTests`, `GitHubPanelFocusTests`, `GitHubListViewModelTests`, expect PASS.**
- [ ] **Step 5: Build the solution and commit:** `feat(yard): the repository line opens the GitHub panel (#406)`.

### Task 4: Whole suite, on-screen check, PR

- [ ] **Step 1:** `dotnet build CodeSwitchX.slnx`, then `dotnet test --solution CodeSwitchX.slnx`. Expect all green, apart from the three known `HotkeyServiceTests` while stable runs.
- [ ] **Step 2: On-screen check on the BenQ** (global CLAUDE.md, memory `running-the-app-from-claude`), with a public or scratch repository tile. Check:
  - Hovering a line highlights it and the tile doesn't move.
  - A click on a line with open PRs opens the PR tab.
  - A tile with nothing open opens two empty tabs.
  - Both ↗ open the right page in the account's browser.
  - A click on a non-GitHub line, or elsewhere on the tile, still opens the workspace.
  - Tab and Escape work.

  Capture only public tiles.
- [ ] **Step 3:** Push, then open the PR with `GH_TOKEN=$(gh auth token --user Little-God1983)`. Title: `Yard: repository line opens one panel with PR and issue tabs`. The body says `Closes #406` and lists what was checked on screen.
