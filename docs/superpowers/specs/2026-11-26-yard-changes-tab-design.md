# Yard: the changed files behind the "N changed" pill — design

Date: 2026-11-26. Size: a small change (one issue, one PR).

## Intent

A tile's state pill says "3 changed" but not which files. The user wants to click it and see them, and from that list:
copy the whole list, open the folder a file is in, and copy that folder's path.

## Approaches

- **A. A "Changes" tab in the repository panel (picked).** The panel a line or an issue/PR pill opens (#335, #406) gets a
  third tab beside Pull requests and Issues. It sits next to Pull and the Merge strips, which care about the same files.
  Only repositories on GitHub have that panel, so only their pills open it.
- **B. A side panel of its own, same look and place.** Works for every repository, but a second panel that looks like
  the first, and no link to Pull/Merge. Picked first, then dropped for A.
- **C. A popup under the pill.** Lightest, but long paths are cut and long lists scroll in a small box; buttons in a
  light-dismiss popup are easy to lose. Ruled out.

The user chose A, for repositories on GitHub only.

## Goals

- The "N changed" pill on a tile line that is on GitHub is a button of its own (like the issue and PR pills: the click is
  the pill's, not the line's) and opens the repository panel on the **Changes** tab.
- The Changes tab lists the files `git status` counts for the pill, one row each: the path relative to the repository
  root and its state in a word (edited, new, deleted, renamed, conflict).
- Per row, two buttons: **open folder** and **copy folder path**.
- At the top of the tab, **Copy all**: the whole list to the clipboard.

## Non-goals

- No diff view, no opening a file in VS Code, no stage/commit/discard.
- Repositories not on GitHub: their pill stays as it is, not clickable.
- Raven's chat header: its pill stays plain text.
- The "clean" pill stays as it is: a click on it is the line's, as today.

## Decisions (calls made in brainstorming; the user may overrule any)

- **No extra git call for the list.** `GitInspector.InspectAsync` already runs `git status --porcelain=v2` for the
  count and throws the paths away. It keeps them instead (`GitInfo` and `GitLine` carry the list), so the tab follows the
  Yard's 30-second git rounds the way the panel already follows the branch and the changed count (`FollowBranch`).
- **Same entries as the pill counts**, so "3 changed" opens a tab with 3 rows: `--untracked-files=normal` stays, and a
  new folder git lists as one entry (`docs/new/`) is one row.
- **Paths:** porcelain output is relative to the repository root (`GitInfo.Top`); a full path is root + path. C-quoted
  paths (`core.quotePath`, names with special characters) are unquoted. A rename shows the new path, the old one after
  an arrow.
- **Copy all** copies full paths, one per line, sorted as the rows are (by path, case-insensitive). The header's copy
  message reports it ("Copied 3 paths"), as Copy all links does.
- **Open folder** opens Explorer on the file's folder with the file selected (`explorer /select,`). A deleted file, or a
  folder that no longer exists, opens the nearest folder above it that does exist. A new-folder entry opens that folder.
- **Copy folder path** copies the full path of the folder the file is in (for a new-folder entry, the folder itself).
- **The tab is there whenever the panel has a checkout** (it was opened from a line). Its label carries the count
  ("Changes 3"); with nothing changed it reads "No changed files." Opening the panel from the line itself still lands on
  Pull requests or Issues as today; only the pill lands on Changes.

## Constraints

- WPF conventions of the repo (CommunityToolkit.Mvvm, `ITextClipboard`, `IUiDispatcher`, styles in `App.xaml` /
  `YardView.xaml`), the existing tab template (#410), Explorer started the way `WorkspaceTileViewModel.RevealInExplorer`
  does.
- The repository is public: tests use made-up paths only.

## Left to the builder

The exact row layout and icons, the state word mapping from porcelain v2 `XY` codes, and how the tab template
accommodates a tab that is not a GitHub list.
