# Yard tile: stable row with the pill on the right, a readable version, Run and Build buttons

Short design, 2026-11-27. Issue #454. Builds on the stable build row (#452) and the menu's Build new version / Run (#423).

## Intent

The stable build row looks out of place on the tile: the version is small and dim, and the pill sits directly after it instead of
lining up with the pills above. The user also wants to run and build an app from the tile without opening the right-click menu.

## What the user decided

- The **pill moves to the tile's right edge**.
- The **version must be easy to read**.
- **Run and Build sit on the tile**, as **two icon buttons right after the version** (chosen over text buttons and over a second
  line of buttons). The right-click entries stay.

```
Stable 0.1.0.46  [play] [wrench]            [up to date]
```

## Approaches

- **Icon buttons in the row (chosen):** fits narrow tiles and rows that carry an app name; the icons carry a tooltip.
- **Text buttons in the row:** clear without hovering, but the version is cut off on a narrow tile.
- **Buttons on a second line:** most room, but every tile with a stable build grows a line.

## Behaviour (Claude's proposals)

- **Version:** 13 px. "Stable" (and an app name before it) dimmed, the number semi-bold at full brightness.
- **Buttons do what the menu entries do**, through the same code: Build checks git at the press, asks before it starts, and runs the
  build script in its own window; Run opens the Start Menu shortcut.
- **Greyed states as in the menu:** Build greyed with the reason as its tooltip (wrong branch, uncommitted changes, behind GitHub);
  Run greyed with "No stable build yet".
- **The greying follows the tile's git round**, not a read of its own: after a commit, Build can stay greyed until the next round
  (up to about 30 s). The menu, which reads git when it opens, stays the way to build at once.
- **Clicks:** a click on a button, greyed or not, never opens the window. A click elsewhere on the row still does.
- **Several apps on one tile:** each row has its own buttons. A row stands for one installed app; where a checkout and its worktree
  install the same app, the row's buttons act on the first of the two lines, and the worktree's build stays in the menu.
- **No row, no buttons:** a repository whose scripts name no install marker has no row today and keeps the menu only.
- **Build icon:** a wrench (Segoe Fluent Icons has no hammer).

## Out of scope

- A git read when the pointer enters the row.
- Showing that a build is running, or that the app is running.
- Changing the menu entries or the build scripts.
