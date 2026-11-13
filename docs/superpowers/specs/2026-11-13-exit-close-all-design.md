# Exit: close all VS Code windows too, or only CodeSwitchX (#420)

## Why

Closing CodeSwitchX leaves every VS Code window it docked open on the desktop (`HostManager.ReleaseAll` shows them
again on purpose). That is right for the window's X, but there is no way to end the whole session, CodeSwitchX and its
VS Code windows, in one go.

## Goals

- One **Exit**, in the Yard's top bar after Settings, and the tray menu's Exit, both asking the same question.
- The question names how many VS Code windows are open and how many of them have a chat that is still working, then
  offers **Close all**, **Close only CodeSwitchX**, **Cancel**.
- No VS Code window open: Exit closes at once, without asking.

## Non-goals

- Changing the window's X: it keeps today's behaviour (CodeSwitchX closes, VS Code stays open, no question).
- Closing VS Code windows that are not on a tile (not hosted by CodeSwitchX).

## Anti-goals

- Losing unsaved files: VS Code is asked to close, never killed, so it still asks to save.
- A second exit button with another meaning (the user ruled it out: one Exit).

## Approaches

- **Close request per window (chosen).** Show the hidden windows again, post WM_CLOSE to each hosted window: the same as
  a click on VS Code's own X, VS Code's save prompts included.
- Killing the VS Code processes: loses unsaved files. Ruled out.
- A quit command through the companion extension: needs a new extension version and does nothing a close request does
  not. Ruled out.

## Design

- **Counts.** Open = the workspaces `HostManager` holds Running. Working = those whose Yard tile has a chat working or
  waiting for the user mid-turn (`IsWorking || NeedsAttention`, as the Yard's pull guard counts).
- **Wording** (built in the ViewModel, tested):
  - "3 VS Code windows are open, and 2 of them have a chat that is still working." /
    "1 VS Code window is open, and a chat in it is still working." / "2 VS Code windows are open."
  - Under it: "Closing them stops those chats mid-turn." when one works, then "Unsaved files are not lost: VS Code asks about them, or keeps them for its next start." (round 3: the last VS Code window keeps them by hot exit instead of asking)
- **Dialog.** `ExitWindow` beside the shell, like `RemoveWorkspaceWindow`: three buttons, Cancel on Esc and on closing
  the dialog; Enter is "Close only CodeSwitchX", the choice that loses nothing.
- **Close all.** `HostManager.CloseAll()`: as `ReleaseAll` (shows every hosted window, nothing is hidden again after it)
  and posts a close request to each. Then the main window closes as after its X.
- **Wiring.** `ShellViewModel.ExitCommand`; the window asks through `AskBeforeExit` (set by `MainWindow`, as
  `AskBeforeRemove`) and closes through `IShellWindow.Close()`. The tray's Exit runs the same command.

## Left to the builder

- Where exactly the new Win32 call sits (`IWindowDocker.Close`, PostMessage WM_CLOSE: asynchronous, never waits for VS Code).
