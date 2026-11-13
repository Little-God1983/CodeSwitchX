# Yard: Build new version / Run (design)

## Intent

Several of the user's apps carry the same stable-build scripts (`scripts\build.ps1`, `scripts\_common.ps1`):
bump the version, publish to `E:\StableVersion\<App>-<version>`, move the `<App>` link and the Start Menu
shortcut, commit and push. Today that means opening a terminal or double-clicking `build.cmd`. The Yard tile's
right-click menu should do it, and also start the current stable build of that app.

## Goals

- Two entries on a workspace tile's context menu, only for repos that carry the scripts:
  **Build new version** and **Run**.
- The user sees the build: it runs in its own console window that stays open.

## Non-goals

- No build progress or result inside the Yard (user chose the console window).
- CodeSwitchX does not repeat the script's own checks (level with origin, closing/restarting the app).
- Run never builds; it starts what is already installed.

## Approaches

| Approach | Verdict |
|---|---|
| New terminal (`wt.exe -d <repo> pwsh -NoExit -File scripts\build.ps1`, fallback a plain console running the same) | **Picked.** Window lives apart from CodeSwitchX, so building CodeSwitchX itself can close CodeSwitchX; output stays visible. |
| Start `build.cmd` | Ruled out: it only pauses when Explorer started it, so the window would close the moment the build ends. |
| Run the build inside the Yard with a status on the tile | Ruled out by the user. |

## Design

- **Which repos:** a repo of the tile (its `GitLine`s) qualifies when `scripts\build.ps1` and
  `scripts\_common.ps1` exist and `_common.ps1` has a `$script:ShortcutName = '…'` line.
- **Precheck (user's decision):** *Build new version* is enabled only when the repo is on `main` or `develop`
  and has no uncommitted changes (the git state the tile already shows). Otherwise it is greyed out and its
  tooltip says why ("On feature/x - builds run from main or develop", "3 uncommitted changes", "Git state
  unknown"). The script still makes its own, stricter checks (which of the two branches, level with origin).
- **Build new version:** a yes/no dialog first (builder's call, approved): it names the app and says the build
  bumps the version, commits, pushes, and may close the running app. Yes opens the terminal described above.
- **Run:** opens `%APPDATA%\Microsoft\Windows\Start Menu\Programs\<ShortcutName>` through the shell. The
  shortcut already knows each app's exe or start script. No shortcut → entry greyed out, tooltip
  "No stable build yet".
- **Several qualifying repos on one tile (builder's call, approved):** the entries carry the repo's folder name,
  e.g. "Build CodeSwitchX", "Run CodeSwitchX".

## Constraints

- Never run a real build against `E:\StableVersion` while testing; on-screen checks use a scratch repo with
  fake scripts.
- Check on screen: CodeSwitchX's own build closing CodeSwitchX stays silent (no #420 Exit dialog) and the
  terminal window survives.
- The repo is public: no names of the user's other repos in code, tests or commits.

## Left to the builder

Where the script detection and shortcut-name parsing live (a small service with tests), when detection
refreshes (with the tile's git refresh is fine), exact wording of dialog and tooltips.
