# Open issues and PRs on the Yard tiles: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Each repository line on a Yard tile shows open issue and PR pills with a hover preview and a full list, and each item opens in the browser logged in to the account that can see it (#335).

**Architecture:** `Core/GitHub` reads GitHub through `gh`, one child process per request with that account's token in its environment, and keeps the last results per folder in memory. `Hosting/Browsers` finds browsers and profiles and builds their arguments. `UI` adds the pills to `GitLine`, the list panel to the Yard, and a GitHub page to Settings.

**Tech Stack:** C# / .NET 10, WPF, CommunityToolkit.Mvvm, xUnit + Shouldly, `gh` 2.89.

**Spec:** `docs/superpowers/specs/2026-11-03-yard-github-pills-design.md`

## Global Constraints

- Never run `gh auth switch` or change the active `gh` account.
- Tokens go only into a child process's `GH_TOKEN` environment variable: never in arguments, logs, the database or a field.
- Titles, labels and private repository names never reach the logs or the database. Failures log the folder path only.
- CodeSwitchX is a public repository: commits, tests, docs, PR text and committed screenshots use made-up names, or public repositories only.
- Read-only. GitHub only (`github.com`). Tiles only: Raven's chat header looks as it does today.
- A count of zero shows no pill. Tile failures are silent: no text, no spinner.
- Only `https://github.com/` addresses are ever opened.
- Refresh interval: 10 minutes. Preview: 5 newest items, most recently created first.
- Issue pill: text `#B9CDF0`, background `#3A4250`, border `#566378`. PR pill: text `#A6E3E9`, background `#2D464B`, border `#4B6F76`.
- Every on-screen run is on the BenQ, except the one check the spec's Testing section allows.
- Tests run through their exe, not a project path (a project path reports zero tests): `dotnet build tests/<Project> -v q` then `tests/<Project>/bin/Debug/net10.0-windows/<Project>.exe -class "<full class name>"`. `HotkeyServiceTests` fail while the stable build runs; that is not a regression.
- Before every commit, check `git branch --show-current` is `feature/yard-github-pills`. Commit messages end with the attribution line in force for the session.

## Review Focus

1. **A public repository both accounts can see:** the owner's account reads it, whatever order `gh` lists accounts in. Pinned in Task 1.
2. **A look-alike remote** (`github.com.evil.example`, `notgithub.com`): no pills, and nothing ever opens. Pinned in Task 1.
3. **A refresh failing after an earlier success:** the last counts stay; a real zero afterwards removes the pill. Pinned in Task 2.
4. **`gh` hanging:** the child is killed at the timeout, and tile git lines keep updating meanwhile. Pinned in Tasks 1 and 2.
5. **A saved browser that has been uninstalled:** the click says so in the panel, and nothing opens in the Windows default. Pinned in Task 3.

---

### Task 1: Reading GitHub through `gh`

**Files:**
- Create: `src/CodeSwitchX.Core/GitHub/GitHubRepo.cs`, `GitHubItem.cs`, `IGhCli.cs`, `GhCli.cs`, `GitHubReader.cs`
- Test: `tests/CodeSwitchX.Core.Tests/GitHub/GitHubRepoTests.cs`, `GhCliTests.cs`, `GitHubReaderTests.cs`

**Interfaces (namespace `CodeSwitchX.Core.GitHub`):**
- Produces:
  - `record GitHubRepo(string Owner, string Name)` with `string Slug`, `static GitHubRepo? FromRemote(string? url)`, and case-insensitive equality on owner and name.
  - `enum GitHubItemKind { Issue, PullRequest }`, `record GitHubLabel(string Name, string Color)`.
  - `record GitHubItem(int Number, string Title, string Url, DateTimeOffset UpdatedAt, IReadOnlyList<GitHubLabel> Labels, string? Branch = null, string? Author = null)`.
  - `record RepoActivity(GitHubRepo Repo, string Account, int OpenIssues, int OpenPullRequests, IReadOnlyList<GitHubItem> NewestIssues, IReadOnlyList<GitHubItem> NewestPullRequests)` with `bool SameAs(RepoActivity other)`, which compares the lists by content.
  - `interface IGhCli { Task<IReadOnlyList<string>> AccountsAsync(CancellationToken ct); Task<string?> RunAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct); }`.
  - `class GitHubReader(IGhCli gh)` with `Task<RepoActivity?> ReadAsync(GitHubRepo repo, CancellationToken ct)` and `Task<IReadOnlyList<GitHubItem>?> ListAsync(GitHubRepo repo, string account, GitHubItemKind kind, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

`GitHubRepoTests.FromRemote` as a theory:

| Remote | Result |
|---|---|
| `https://github.com/acme/tool.git` | `acme/tool` |
| `https://github.com/acme/tool` and `https://github.com/acme/tool/` | `acme/tool` |
| `https://user@github.com/acme/tool.git` | `acme/tool` |
| `git@github.com:acme/tool.git` | `acme/tool` |
| `ssh://git@github.com/acme/tool.git` | `acme/tool` |
| `HTTPS://GitHub.com/acme/tool-` | `acme/tool-` |
| `https://github.com/acme/my.tool.x` | `acme/my.tool.x` |
| `https://gitlab.com/acme/tool`, `https://notgithub.com/acme/tool`, `https://github.com.evil.example/acme/tool`, `https://github.com/acme`, empty, `null` | `null` |

`GhCliTests` against internal `static ProcessStartInfo GhCli.StartInfo(IReadOnlyList<string> arguments, string? token)`:
- `Token_goes_in_the_environment_only`: `Environment["GH_TOKEN"]` is the token, and `ArgumentList` holds no token.
- `An_inherited_token_never_reaches_a_lookup`: with a null token, neither `GH_TOKEN` nor `GITHUB_TOKEN` is in `Environment`, because an inherited one would make `gh auth token --user` return it.
- `Arguments_are_passed_as_a_list`: an argument with spaces and quotes stays one `ArgumentList` entry.

`GitHubReaderTests` with a fake `IGhCli` that answers per login with canned JSON, or null for "cannot see it":
- `Owner_account_reads_its_own_repo_whatever_the_list_order`: accounts `["Zed","acme"]` then `["acme","Zed"]`, repo `acme/tool`, both can see it, and `Zed` has higher permission. `Account` is `acme` both times, and `Zed` is never asked.
- `Otherwise_the_highest_permission_reads_it`: owner `other`, `a` has `READ`, `b` has `ADMIN`: `b`.
- `A_tie_goes_to_the_first_login_alphabetically`: `Zed` and `amy` both `READ`, in either list order: `amy`.
- `The_order_is_ADMIN_MAINTAIN_WRITE_TRIAGE_READ`: a theory over adjacent pairs.
- `Falls_through_when_the_owner_account_cannot_see_it`: the owner's account returns null, another `READ`: the other.
- `No_account_sees_it_gives_null`, and `No_accounts_gives_null`.
- `Counts_are_the_totals_not_the_rows`: `totalCount` 19 with 5 nodes gives `OpenIssues == 19` and `NewestIssues.Count == 5`.
- `Pr_rows_carry_branch_and_author_and_issue_rows_labels`, including a title with quotes and an emoji, and a label's `Color`.
- `A_deleted_author_gives_a_null_Author`: `author: null` in the JSON does not throw.
- `Malformed_json_gives_null`.
- `ListAsync_asks_that_account_with_the_right_command`: runs as the given account with `gh issue list -R acme/tool --state open --limit 1000 --json number,title,url,updatedAt,labels`, or for PRs `gh pr list -R acme/tool --state open --limit 1000 --json number,title,url,updatedAt,labels,headRefName,author`, and returns all rows. A null result gives null.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build tests/CodeSwitchX.Core.Tests -v q`
Expected: build errors, as the types don't exist.

- [ ] **Step 3: Implement `GitHubRepo` and the records**

Parse with `Uri` for URL forms and a regex for the `git@host:` form. Compare the host exactly with `github.com`, ignoring case. Strip one `.git` and trailing slashes. Require exactly two path parts.

- [ ] **Step 4: Implement `GhCli : IGhCli`**

`AccountsAsync` runs `gh auth status --json hosts` and returns `hosts["github.com"]` logins whose `state` is `success`. It returns an empty list when `gh` is missing or fails, so catch the `Win32Exception` that `Process.Start` throws when `gh` is not on the path. `RunAsAsync` first runs `gh auth token --user <login>`, then the request with that token through `GitInspector.RunProcessAsync` (same assembly), with `public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20)`. Never store the token in a field. Remove inherited `GH_TOKEN` and `GITHUB_TOKEN` from every child's environment before setting the token.

- [ ] **Step 5: Implement `GitHubReader`**

`ReadAsync` runs `gh api graphql -f query=<Query> -f owner=<owner> -f name=<name>`, which gives one request per repository and account. Use `-f` for all three: `-F` would type a numeric-looking repository name as a number and fail the `String!` variable. `Query` is exactly:

```graphql
query($owner:String!,$name:String!){repository(owner:$owner,name:$name){viewerPermission
 issues(states:OPEN,first:5,orderBy:{field:CREATED_AT,direction:DESC}){totalCount nodes{number title url updatedAt labels(first:10){nodes{name color}}}}
 pullRequests(states:OPEN,first:5,orderBy:{field:CREATED_AT,direction:DESC}){totalCount nodes{number title url updatedAt headRefName author{login} labels(first:10){nodes{name color}}}}}}
```

A repository that cannot be seen exits 1, so `RunAsAsync` returns null.

Order accounts: if a login equals the owner (ignoring case), try it alone first and accept any answer. Otherwise ask every account and choose by permission, then alphabetically. Sort logins yourself with `StringComparer.OrdinalIgnoreCase`; never rely on `gh`'s order.

**Deviation from the spec, to mention in the PR:** the spec says the result is remembered and worked out again when it fails. Here it is worked out every round instead, with no state. An owner's repository still costs one request, and only repositories under another owner cost one per account. A brief failure of the better account can't then stick to the wrong one.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `tests/CodeSwitchX.Core.Tests/bin/Debug/net10.0-windows/CodeSwitchX.Core.Tests.exe -class "CodeSwitchX.Core.Tests.GitHub.GitHubReaderTests"`, and the same for the other two classes.
Expected: all pass.

- [ ] **Step 7: Check against the real `gh`**

Add a test of `GhCli.RunAsAsync` that runs a small script as `gh`'s stand-in which sleeps past the timeout, through an internal constructor taking the executable and timeout: `A_hanging_gh_is_killed_at_the_timeout`. Then read this public repository once through a scratch console call, with real `gh`.
Expected: the hanging call returns null within the timeout, and the real read shows `Little-God1983` as the account.

- [ ] **Step 8: Commit**

`feat: read open issues and PRs through gh, for any account, without switching (#335)`

### Task 2: Counts on the tile's lines, refreshed every 10 minutes

**Files:**
- Create: `src/CodeSwitchX.Core/GitHub/GitHubActivity.cs`, `src/CodeSwitchX.UI/Yard/GitHubPill.cs`
- Modify: `src/CodeSwitchX.UI/Yard/GitLine.cs`, `src/CodeSwitchX.UI/Yard/YardViewModel.cs:40,274,373,676-732,1033`, `src/CodeSwitchX.UI/App.xaml.cs:178`
- Test: `tests/CodeSwitchX.Core.Tests/GitHub/GitHubActivityTests.cs`, `tests/CodeSwitchX.UI.Tests/Yard/YardGitHubTests.cs`

**Interfaces:**
- Consumes: `GitHubReader.ReadAsync`, `GitHubRepo.FromRemote`, `RepoActivity.SameAs` from Task 1.
- Produces:
  - `class GitHubActivity(GitHubReader reader, Func<string, CancellationToken, Task<string?>> originOf, ILogger<GitHubActivity> logger)` with `event EventHandler? Changed`, `RepoActivity? ForFolder(string path)`, `bool Knows(string path)`, `Task RefreshAsync(IReadOnlyCollection<string> folders, CancellationToken ct)`, and `void Replace(GitHubRepo repo, GitHubItemKind kind, IReadOnlyList<GitHubItem> all)`.
  - `record GitHubPill(RepoActivity Activity, GitHubItemKind Kind)` with `int Count`, `string Heading` ("SomeRepo 19 open"), `string? MoreText`, `string AutomationName`, `IReadOnlyList<GitHubRow> Rows`.
  - `record GitHubRow(GitHubItem Item, string NumberText, string? ByText, string AgeText)` with `static GitHubRow From(GitHubItem item, string account, DateTimeOffset now)`.
  - `GitLine` gains `GitHubPill? Issues = null, GitHubPill? PullRequests = null`.
  - `YardViewModel` gains the optional constructor parameter `GitHubActivity? gitHub = null` and `public static readonly TimeSpan GitHubRefreshInterval = TimeSpan.FromMinutes(10)`.

- [ ] **Step 1: Write the failing tests**

`GitHubActivityTests` with a fake reader behind a fake `IGhCli`:
- `Two_folders_in_one_repo_make_one_read`, including `Acme/Tool` against `acme/tool` remotes.
- `A_failed_read_keeps_the_last_result`, with `ForFolder` still returning it.
- `A_real_zero_replaces_the_last_result`.
- `Nothing_new_keeps_the_same_instance_and_stays_quiet`: `ForFolder` returns the same reference and `Changed` doesn't fire.
- `A_change_fires_Changed_once_for_the_round`.
- `A_folder_without_a_github_origin_is_known_and_has_nothing`.
- `No_more_than_four_reads_run_at_once`.
- `Replace_sets_the_count_and_the_five_newest`: replacing issues with 7 rows gives `OpenIssues == 7` and 5 newest, with PR values unchanged.

`YardGitHubTests` with the existing Yard fakes and `FakeTimeProvider`:
- `A_line_gets_pills_only_for_counts_above_zero`: issues 2 and PRs 0 give `Issues.Count == 2` and `PullRequests == null`.
- `Each_repository_line_gets_its_own_pills`: on a multi-folder workspace.
- `The_first_git_round_starts_the_first_github_round`.
- `A_folder_seen_later_is_read_without_waiting_for_the_timer`.
- `Every_ten_minutes_all_folders_are_read_again`.
- `A_github_round_that_never_ends_does_not_hold_up_git`: the fake reader never completes, and `RefreshGitAsync` still finishes and shows lines.
- `A_round_with_nothing_new_keeps_the_lines`: `tile.GitLines` keeps the same reference, so an open popup stays open.
- `No_activity_service_means_no_pills`: the constructor default.

`GitHubPill` tests:
- `Heading` is `"SomeRepo 19 open"`.
- `MoreText` is `"+14 more. Click the pill for all of them"` at count 19 and null at count 5.
- `ByText` is `"by someone"` when the author differs from the account and null when it is the account, ignoring case, or null.
- `AgeText`: 3 hours gives `"updated today"`, 4 days gives `"updated 4d ago"`, 157 days gives `"updated 5 months ago"`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build tests/CodeSwitchX.Core.Tests -v q`, then `dotnet build tests/CodeSwitchX.UI.Tests -v q`.
Expected: build errors.

- [ ] **Step 3: Implement `GitHubActivity`**

Hold results in memory only, as folder to repo and repo to result. Run rounds one at a time, with at most four reads at once through a `SemaphoreSlim`. On failure log the folder path at Warning level. Fire `Changed` at the end of a round, only if a result differs by `SameAs`.

- [ ] **Step 4: Implement `GitHubPill`, `GitHubRow` and the `GitLine` parameters**

`GitHubPill` equality rests on `Activity` keeping its reference, which Step 3 guarantees. `Rows` is computed on read with `DateTimeOffset.Now` through `GitHubRow.From`, and is excluded from equality.

- [ ] **Step 5: Wire `YardViewModel`**

- Add a private `GitLine WithGitHub(GitLine line)` that sets both pills from `_gitHub?.ForFolder(line.Path)`. Apply it to each line in `RefreshGitAsync(tile, ct)` before `ShowGit`.
- At the end of `RefreshGitLoopAsync`, collect line paths where `Knows` is false and start `_ = RefreshGitHubAsync(paths)` without awaiting, catching and logging as the git round does.
- In `InitializeAsync` beside `_gitTimer`, create `_gitHubTimer` at `GitHubRefreshInterval`, reading every tile's line paths on the UI thread. Dispose it where `_gitTimer` is disposed.
- On `Changed`, post to the UI thread, and re-apply `WithGitHub` through `tile.ShowGit` for every tile.

In `App.xaml.cs`, register `IGhCli` as `GhCli`, `GitHubReader`, and `GitHubActivity` with `originOf` as `(path, ct) => git.RunAsync(path, "config --get remote.origin.url", ct)`, and pass it to `YardViewModel`.

- [ ] **Step 6: Run the tests to verify they pass**

Run the `GitHubActivityTests`, `YardGitHubTests` and existing `YardViewModelTests` classes through their exes.
Expected: all pass.

- [ ] **Step 7: Commit**

`feat: the Yard keeps each repository's open issue and PR counts (#335)`

### Task 3: Browsers, profiles and opening an item

**Files:**
- Create: `src/CodeSwitchX.Hosting/Browsers/BrowserEntry.cs`, `BrowserProfiles.cs`, `BrowserCatalog.cs`, `src/CodeSwitchX.UI/GitHub/GitHubBrowsers.cs`
- Modify: `src/CodeSwitchX.UI/Settings/SettingKeys.cs`, `src/CodeSwitchX.UI/App.xaml.cs`
- Test: `tests/CodeSwitchX.Hosting.Tests/Browsers/BrowserProfilesTests.cs`, `BrowserCatalogTests.cs`, `tests/CodeSwitchX.UI.Tests/GitHub/GitHubBrowsersTests.cs`

**Interfaces:**
- Produces:
  - `record BrowserEntry(string Id, string Label, string? Executable, IReadOnlyList<string> ProfileArguments)` with `const string DefaultId = "default"` and `IReadOnlyList<string> Arguments(string url)`.
  - `static class BrowserProfiles` with `IReadOnlyList<string> Firefox(string profilesIni)` and `IReadOnlyList<(string Directory, string Name)> Chromium(string localStateJson)`.
  - `class BrowserCatalog(IBrowserSource source)` with `IReadOnlyList<BrowserEntry> List()`.
  - `interface IBrowserSource { IReadOnlyList<(string Key, string? Executable)> Installed(); string? FirefoxProfilesIni(); string? EdgeLocalState(); string? ChromeLocalState(); }`.
  - `class GitHubBrowsers(BrowserCatalog catalog, ISettingsStore settings, Func<ProcessStartInfo, bool> start, ILogger<GitHubBrowsers> logger)` with `Task LoadAsync(CancellationToken ct)`, `string ChoiceFor(string login)`, `void SetChoice(string login, string id)`, `string NameFor(string login)`, and `string? Open(string login, string url)`, which returns null when opened and otherwise the line to show.
  - `SettingKeys.GitHubBrowsers = "github.browsers"`, stored as `Dictionary<string, string>` of login to entry id.

- [ ] **Step 1: Check Firefox's second profile**

This is the spec's early check, and it uses the one-time exception. With Firefox running under its default profile, run `firefox.exe -P <another profile> -new-tab https://github.com/settings/profile`.
Expected: either a tab or window in that profile, which needs nothing more, or a refusal, in which case add `--new-instance` to Firefox's `ProfileArguments`. The spec accepts a separate Firefox window. Record the result in the PR.

- [ ] **Step 2: Write the failing tests**

- `BrowserProfiles.Firefox` reads `Name=` from `[Profile*]` sections only, and ignores `[Install*]`' `Default=`.
- `BrowserProfiles.Chromium` reads `profile.info_cache`. A missing or broken file gives an empty list.
- `BrowserCatalog`:
  - The first entry is always the Windows default, labelled `Windows default browser`.
  - `IEXPLORE.EXE` is skipped.
  - One Firefox key seen in both HKLM and HKCU gives one browser.
  - One profile gives a plain label (`Microsoft Edge`), and several give `Firefox (name)` per profile.
  - Ids always carry the profile (`firefox|name`, `edge|Default`), so a saved choice survives a second profile being added.
  - A browser with an unknown key gets one entry with no profile arguments.
- `BrowserEntry.Arguments`:
  - Firefox gives `["-P","name with space","-new-tab",url]`.
  - Edge gives `["--profile-directory=Profile 1",url]`.
  - Each value is one list entry.
- `GitHubBrowsers`:
  - `An_account_without_a_choice_uses_the_Windows_default` starts the URL with `UseShellExecute` true.
  - `A_chosen_browser_is_started_with_its_arguments`.
  - `A_gone_browser_says_so_and_opens_nothing`: `start` is never called, and the line is `"Couldn't find <id>. Pick a browser in Settings, GitHub."`.
  - `A_browser_that_fails_to_start_says_so`: `start` throws, and the line is `"Couldn't open <label>. Pick another browser in Settings, GitHub."`.
  - `Only_github_addresses_are_opened`: `http://github.com/x`, `https://github.com.evil.example/x`, `https://example.com` and `file:///c:/x` never call `start`.
  - `A_choice_is_saved_under_the_login`.

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet build tests/CodeSwitchX.Hosting.Tests -v q`, then `dotnet build tests/CodeSwitchX.UI.Tests -v q`.
Expected: build errors.

- [ ] **Step 4: Implement**

The real `IBrowserSource` reads `SOFTWARE\Clients\StartMenuInternet` in HKLM and HKCU, each key's `shell\open\command` default value for the executable, `%APPDATA%\Mozilla\Firefox\profiles.ini`, and `%LOCALAPPDATA%\<Microsoft\Edge|Google\Chrome>\User Data\Local State`. Never read cookies. `GitHubBrowsers.Open` checks the address with `Uri` (scheme `https`, host exactly `github.com`) and uses `ArgumentList`.

Register these in `App.xaml.cs`, with `LoadAsync` called during start as other settings are.

- [ ] **Step 5: Run the tests to verify they pass**

Run the three classes through their exes.
Expected: all pass.

- [ ] **Step 6: Commit**

`feat: open a GitHub address in the browser chosen for its account (#335)`

### Task 4: The GitHub page in Settings

**Files:**
- Create: `src/CodeSwitchX.UI/Settings/GitHubPageViewModel.cs`
- Modify: `src/CodeSwitchX.UI/Settings/SettingsPage.cs`, `SettingsViewModel.cs:259-263,337-341`, `SettingsView.xaml` (after the Yard page, line 1249 on), `AppSettings.cs:575`, `src/CodeSwitchX.UI/App.xaml.cs`
- Test: `tests/CodeSwitchX.UI.Tests/Settings/GitHubPageViewModelTests.cs`

**Interfaces:**
- Consumes: `IGhCli.AccountsAsync`, `GitHubBrowsers`, `BrowserCatalog`.
- Produces:
  - `SettingsPage.GitHub`.
  - `class GitHubPageViewModel` with `ObservableCollection<GitHubAccountRow> Accounts`, `bool HasNoAccounts`, and `Task RefreshAsync(CancellationToken ct)`.
  - `class GitHubAccountRow` with `string Login`, `IReadOnlyList<BrowserEntry> Choices`, `string SelectedId`, `TestCommand`, `string? Message`.
  - `SettingsViewModel.GitHub`, through the optional constructor parameter `GitHubPageViewModel? gitHub = null`.

- [ ] **Step 1: Write the failing tests**

- `Each_gh_account_gets_a_row`: the row's `SelectedId` comes from `GitHubBrowsers.ChoiceFor`.
- `Picking_a_browser_saves_it`.
- `Test_opens_the_profile_page_in_the_chosen_browser`: `https://github.com/settings/profile`, in that account's chosen browser.
- `Test_shows_the_line_when_it_cannot_open`.
- `A_gone_browser_stays_selected_and_shows_as_missing`: `Choices` gains an entry labelled `Missing: <id>`, selected.
- `No_accounts_says_how_to_log_in`: `HasNoAccounts` is true.
- `Opening_the_page_reads_the_accounts_again`, so a new `gh auth login` shows up by itself.
- In `SettingsViewModelTests`: searching `Firefox`, `PR` or `pull requests` finds the page.
- In `AppSettingsTests`: `"github browser"` is listed with `NotByVoice`, and setting it opens the page.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build tests/CodeSwitchX.UI.Tests -v q`
Expected: build errors.

- [ ] **Step 3: Implement the page**

- In `SettingsPageItem.All`, after Yard: group `CodeSwitchX`, title `GitHub`, icon `\uE774`, subtitle `Which browser opens the issues and pull requests of each account.`, keywords `GitHub`, `issues`, `pull requests`, `PR`, `browser`, `account`, `Firefox`, `Edge`, `Chrome`.
- In XAML, follow the Yard page's card and `ComboBox` pattern: one card per account, with the login as `H3`, the label `Opens in`, the combo bound to `Choices` / `SelectedId`, a `Test` button, and `Message`.
- Below the cards, in `Help` style: `Accounts come from the GitHub command line (gh). To add one, run gh auth login; it shows up here by itself.`
- When `HasNoAccounts`: `No GitHub accounts found. Install the GitHub command line (gh) and run gh auth login.`
- In `AppSettings`, add the entry beside `data`, with `NotByVoice: "the user pairs each account with a browser on the page."`.

- [ ] **Step 4: Run the tests to verify they pass**

Run `GitHubPageViewModelTests`, `SettingsViewModelTests` and `AppSettingsTests` through the exe.
Expected: all pass.

- [ ] **Step 5: Commit**

`feat: a GitHub settings page pairs each account with a browser (#335)`

### Task 5: The pills and the hover preview

**Files:**
- Create: `src/CodeSwitchX.UI/Yard/GitPills.cs`, `src/CodeSwitchX.UI/Yard/GitHubPillPreview.cs`
- Modify: `src/CodeSwitchX.UI/App.xaml:22-67`, `src/CodeSwitchX.UI/Yard/YardView.xaml:175`, `src/CodeSwitchX.UI/Yard/YardViewModel.cs`
- Test: `tests/CodeSwitchX.UI.Tests/Yard/GitPillsXamlTests.cs`, or the on-screen check where a unit test can't reach

**Interfaces:**
- Consumes: `GitLine.Issues`, `GitLine.PullRequests`, `GitHubPill`, `GitHubRow`.
- Produces:
  - An attached, inherited `bool GitPills.IsShown`, default false, set true on the tile's `ItemsControl` only.
  - `YardViewModel.OpenGitHubListCommand(GitHubPill pill)`, a stub until Task 6.
  - XAML resource `GitHubRowTemplate`, reused in Task 6.

- [ ] **Step 1: Write the failing test**

`The_chat_header_never_shows_pills`: load `GitLineTemplate` on an STA thread with a line that has pills. In an `ItemsControl` without `GitPills.IsShown`, the pills are collapsed; with it, they are visible. Follow any existing XAML-loading test in `CodeSwitchX.UI.Tests`; if none exists, use `XamlReader` with the app's resources.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet build tests/CodeSwitchX.UI.Tests -v q`
Expected: build error.

- [ ] **Step 3: Add the pills to `GitLineTemplate`**

Insert an `Auto` column before the state pill, holding a horizontal panel with one `Button` per kind. Its `DataContext` is `Issues` / `PullRequests`, and it collapses when null. The panel's visibility binds to `GitPills.IsShown`. Use a shared `GitHubPillButton` style with `CornerRadius="8"`, `FontSize="11"`, the Global Constraints colours, and a small drawn icon. `Command` is `{Binding DataContext.OpenGitHubListCommand, RelativeSource={RelativeSource AncestorType=yard:YardView}}`. A `Button` marks the mouse event handled, so the tile's `MouseBinding` doesn't open VS Code, the same as chat rows. Disable tooltips on the button (`ToolTip` set, `ToolTipService.IsEnabled="False"`), so the line's path tooltip doesn't appear over the preview.

- [ ] **Step 4: Implement `GitHubPillPreview` and `GitHubRowTemplate`**

An attached behaviour on the pill button:
- `MouseEnter` opens a single `Popup` at once, with `Placement="Bottom"` and `StaysOpen="True"`, `Width` 440, and background `#262B33`, showing `Heading`, `Rows.Take(5)` and `MoreText`.
- `MouseLeave` starts a 160 ms `DispatcherTimer`, and entering the popup cancels it.
- `Unloaded` closes it.

The row template shows `NumberText` dimmed and the title most prominently with wrapping. Below that: labels as chips in their GitHub colours (text `#<Color>`, border 40% and background 13% of it), `Item.Branch` in Consolas with an ellipsis, `ByText`, and `AgeText`.

- [ ] **Step 5: Run the test to verify it passes**

Run: `tests/CodeSwitchX.UI.Tests/bin/Debug/net10.0-windows/CodeSwitchX.UI.Tests.exe -class "CodeSwitchX.UI.Tests.Yard.GitPillsXamlTests"`
Expected: pass.

- [ ] **Step 6: Commit**

`feat: issue and PR pills with a hover preview on the Yard tiles (#335)`

### Task 6: The full list panel

**Files:**
- Create: `src/CodeSwitchX.UI/Yard/GitHubListViewModel.cs`
- Modify: `src/CodeSwitchX.UI/Yard/YardViewModel.cs`, `YardView.xaml:262` (root `DockPanel`), `YardView.xaml.cs`
- Test: `tests/CodeSwitchX.UI.Tests/Yard/GitHubListViewModelTests.cs`

**Interfaces:**
- Consumes: `GitHubReader.ListAsync`, `GitHubActivity.Replace`, `GitHubBrowsers.Open` / `NameFor`, `GitHubRowTemplate`.
- Produces:
  - `partial class GitHubListViewModel` with `string Heading`, `string AccountLine`, `IReadOnlyList<GitHubRow> Rows`, `string? Message`, `CloseCommand`, `OpenRowCommand(GitHubRow)`.
  - `YardViewModel.GitHubList` (observable, null when closed).

- [ ] **Step 1: Write the failing tests**

- `Opening_shows_last_known_rows_at_once_then_the_fresh_list`.
- `Heading_names_the_repo_and_count`: `"SomeRepo: 19 open issues"`, `"SomeRepo: 1 open pull request"`.
- `AccountLine_names_account_and_browser`: `"Read with someone. Opens in Firefox (default-release)."`.
- `A_fresh_list_updates_the_tile_count`: via `GitHubActivity.Replace`.
- `A_failed_load_keeps_the_rows_and_says_so`: `Message` is `"Couldn't load the latest list. Showing what was last read."`.
- `Clicking_a_row_opens_it_and_keeps_the_panel_open`.
- `A_browser_that_cannot_open_shows_its_line_in_the_panel`.
- `Closing_while_loading_changes_nothing`: the late result doesn't reopen or change anything, and the tile count may still update.
- `Opening_another_pill_replaces_the_list`: the earlier load's result is ignored.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build tests/CodeSwitchX.UI.Tests -v q`
Expected: build errors.

- [ ] **Step 3: Implement the view model and `OpenGitHubListCommand`**

The list reads with `pill.Activity.Account`, which is the account already worked out for that repository.

- [ ] **Step 4: Add the panel to `YardView.xaml`**

Wrap the root `DockPanel` in a `Grid`. After it, add a `#88000000` shade that closes on click, and a right-aligned `Border` (`Width="560"`, `MaxWidth` of the view, background `#262626`, left border), both collapsed when `GitHubList` is null. Inside the border: a header with `Heading`, `AccountLine` and a ✕ button; `Message`; and a scrolling `ItemsControl` whose rows are `Button`s using `GitHubRowTemplate`. In `YardView.xaml.cs`, focus the panel when it appears, and let Escape close it through a `KeyBinding` on the panel.

- [ ] **Step 5: Run the tests to verify they pass**

Run the class through the exe.
Expected: all pass.

- [ ] **Step 6: Commit**

`feat: a pill opens the full list, a row opens the item in its account's browser (#335)`

### Task 7: On-screen run and the PR

**Files:**
- Modify: as the run requires.
- Create: `docs/review/screenshots/issue335-*.png`, showing public repositories only.

- [ ] **Step 1: Run the whole suite**

Run: `dotnet build CodeSwitchX.slnx -v q`, then each `tests/*/bin/Debug/net10.0-windows/*.Tests.exe`.
Expected: all pass, except `HotkeyServiceTests` if the stable build is running.

- [ ] **Step 2: Start the app on the BenQ**

Follow memory `running-the-app-from-claude`: strip `ELECTRON_RUN_AS_NODE`, switch the user's Open mic off and restore it afterwards, ask before closing their stable build, and place the window with `SetWindowPos` inside the BenQ's rectangle. Rebuild the UI exe on this branch first.

- [ ] **Step 3: Check the tile on the BenQ**

Expected:
- Pills on a single-repository and a multi-repository tile, with the tile no taller than before.
- No pill on a repository with zero items.
- A long branch name trims instead of hiding the pills.
- The Font size slider scales the pills.
- A click on a pill doesn't open VS Code, and a click elsewhere on the tile still does.
- Hover shows the preview, and the pointer can move into it.
- Raven's chat header shows no pills.

- [ ] **Step 4: Check the list and Settings on the BenQ**

Expected: the list opens, scrolls and closes with ✕, Escape and a click outside. The GitHub settings page lists both accounts and each Firefox profile, and Test opens the profile page in the chosen browser.

- [ ] **Step 5: Check real private items in the browsers (the one exception)**

Pair each account with its browser, then click one private item from each account in the list. Tabs land wherever those browsers' windows are, the LG included, as the user allowed for this check only. Don't move the user's windows. Confirm from the window title that each shows the item's page rather than a 404. Take no captures. Keep it to a handful of tabs. Then restore the settings the run changed.

- [ ] **Step 6: Open the PR**

Capture screenshots only of public repositories. Before committing, look at each image and grep the diff for private names. Run `gh auth switch --user Little-God1983`, push, then run `gh pr create` with `Closes #335`, the deviation from Task 1, and the Firefox result from Task 3.
Expected: a PR with one branch for the whole issue.
