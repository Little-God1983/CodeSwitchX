# Exit: close all or only CodeSwitchX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An Exit (Yard top bar + tray) that asks "Close all / Close only CodeSwitchX / Cancel" with the count of open and working VS Code windows.

**Architecture:** Hosting gets a close request per hosted window (`IWindowDocker.Close`, `HostManager.CloseAll`). The shell
ViewModel counts, words the question and acts on the choice; the window shows the dialog and closes itself.

**Tech Stack:** WPF, CommunityToolkit.Mvvm, CsWin32, xUnit v3 + NSubstitute + Shouldly.

**Spec:** `docs/superpowers/specs/2026-11-13-exit-close-all-design.md`

## Global Constraints

- The window's X keeps today's behaviour: no question, VS Code stays open.
- VS Code is asked to close (WM_CLOSE, posted, never awaited), never killed.
- Only hosted (Running) windows are counted and closed.

## Review Focus

- A hosted window that died between the count and Close all: no close request to a dead hwnd (`IsAlive` check).
- A VS Code still being discovered (Starting) during Close all: it must not be hidden afterwards (`_released` set, as `ReleaseAll`).
- Exit pressed with no window to ask in (`AskBeforeExit` null): nothing closes (tests that do not ask keep the app).

---

### Task 1: HostManager.CloseAll

**Files:** Modify `src/CodeSwitchX.Hosting/Win32/IWindowDocker.cs`, `Win32/SnapWindowDocker.cs`, `NativeMethods.txt`,
`HostManager.cs`. Test `tests/CodeSwitchX.Hosting.Tests/HostManagerTests.cs`.

**Produces:** `void IWindowDocker.Close(nint hwnd)`; `int HostManager.CloseAll()` (releases as `ReleaseAll`, then posts a
close to each Running, alive window; returns how many).

- [ ] Test `CloseAll_shows_and_asks_every_hosted_window_to_close`: two hosted (500, 600), one hidden → `Uncloak` and `Close` each once, returns 2.
- [ ] Test `CloseAll_skips_a_window_that_is_gone`: `IsAlive(600)` false → no `Close(600)`, returns 1.
- [ ] Implement: `PostMessage` + `WM_CLOSE` in NativeMethods.txt; `Close` logs a failed post.
- [ ] Build + `HostManagerTests` green; commit.

### Task 2: Shell ExitCommand + question wording

**Files:** Create `src/CodeSwitchX.UI/Shell/ExitQuestion.cs` (enum `ExitChoice { CloseAll, CloseOnly, Cancel }`,
`static string ExitQuestion.Question(int open, int working)`, `static string ExitQuestion.Detail(int working)`).
Modify `Shell/IShellWindow.cs` (`void Close();`), `Shell/ShellViewModel.cs` (`Func<int, int, ExitChoice>? AskBeforeExit`,
`[RelayCommand] void Exit()`, `(int Open, int Working) ExitCounts()`). Test `tests/CodeSwitchX.UI.Tests/Shell/ExitTests.cs`,
fake in `ShellWindowTests.cs` gets `Close()`.

- [ ] Wording tests: (3,2) "3 VS Code windows are open, and 2 of them have a chat that is still working."; (1,1) "1 VS Code window is open, and a chat in it is still working."; (2,0) "2 VS Code windows are open."; (2,1) "…, and 1 of them has a chat that is still working."; Detail(1) "Closing them stops those chats mid-turn. VS Code still asks about unsaved files."; Detail(0) "VS Code still asks about unsaved files."
- [ ] Behaviour tests: no hosted window → window closed, not asked; CloseAll → `Docker.Close(500)` and window closed; CloseOnly → no `Docker.Close`, window closed; Cancel → nothing; asked with (1, 1) when App's chat works.
- [ ] Implement; build + UI tests for Shell green; commit.

### Task 3: Dialog, Yard button, tray

**Files:** Create `src/CodeSwitchX.UI/Shell/ExitWindow.xaml(.cs)` (like `RemoveWorkspaceWindow`; Enter = Close only,
Esc = Cancel). Modify `MainWindow.xaml.cs` (`AskBeforeExit`), `Yard/YardView.xaml` (Exit after Settings),
`Infrastructure/TrayIconService.cs` (Exit runs `shell.ExitCommand`; fix the stale comment).

- [ ] Build; full UI tests; on-screen check on the BenQ (dialog text, Close only, Close all with a scratch workspace); commit, push, PR.
