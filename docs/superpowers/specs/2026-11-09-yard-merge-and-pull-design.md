# Yard: merge and pull, one panel for issues and PRs

## Why

The user's routine for finishing a feature: open the PR in the browser, check that Claude ran its review rounds, then tell
a Claude chat "merge and pull". The goal is to do the merge and the pull themselves, from the Yard, without asking a chat.

Separately, the GitHub panel (#335, #382) is only reachable from a pill, and a repository with no open issues and no open
PRs has no pills. Such a tile offers no way to GitHub at all; the user has to find the repository in the browser. And the
panel shows issues or PRs, never both.

This reverses one non-goal of the pills design (`2026-11-03-yard-github-pills-design.md`: "no merging from the Yard").
Creating, closing and commenting stay out.

## Goals

- A click anywhere on a tile's repository line opens the GitHub panel, for every GitHub repository, open items or not.
- The panel shows pull requests and issues together, in two tabs, and links to the repository's `/pulls` and `/issues`
  pages on GitHub.
- An open PR can be merged from its row. When the workspace is on that PR's branch, the same step switches it to the
  default branch and pulls.
- A workspace can be switched to its default branch and pulled on its own, for PRs merged elsewhere.

## Non-goals

- Creating PRs (the user said so explicitly), closing, commenting, approving, labelling.
- Squash or rebase merges, deleting the branch after a merge, choosing the merge method.
- Pulling a feature branch, fetching all repositories, any git action other than the switch and the pull below.
- Check or review status on PR rows (the repository has no CI and no branch protection; the user checks the review
  rounds in the browser).
- Merge or pull through Raven.

## Anti-goals: a failure even if it technically works

- A merge from one click. A merge cannot be undone; it always takes the button and then a choice in the strip.
- Switching a branch or pulling over uncommitted changes, stashing them, resetting, or making a merge commit locally.
  Anything other than a clean switch and a fast-forward pull is refused with the reason.
- Switching the branch under a chat that is working. Both the Pull button and the merge strip refuse the switch then.
- `gh auth switch` or any change to the active gh account (pills design anti-goal, still holds).
- Private titles or repository names in commits, tests, docs, PR text or screenshots: the repository is public.
- A tile that grows or moves: hover and click must not change the tile's layout.

## Decided with the user

### Opening the panel: the repository line (user's idea)

- The whole repository line on a tile (git icon, folder, branch, pills) highlights under the pointer and is one click
  target that opens the panel. The pills stay as they are, as display. Each line is its own target, so a tile with
  several repository lines (folders of a workspace file, nested repos) opens each repository's panel from its own line.
  Linked worktrees add no lines today (`YardViewModel.GitLinesAsync`), so merge and pull act on a line's own checkout,
  never on a worktree.
- The rest of the tile keeps opening the workspace.
- A repository with no open items has a line without pills; it still highlights and opens the panel.

### The panel: layout A1 (user's choice)

```
CodeSwitchX                                   (opens the repo's main page, as today)
Read with <account>. Opens in <browser>.
[ Pull requests 2 ↗ ] [ Issues 5 ↗ ]
#405  Yard: merge and pull            [Merge]
      feature/405-merge-pull
#399  ...
```

- Two tabs, "Pull requests N" and "Issues N". A click on the tab's name switches the list; a click on its ↗ opens
  `https://<host>/<owner>/<repo>/pulls` or `/issues` in the account's browser (the same browser routing as the rows).
- An empty tab says "No open pull requests." or "No open issues.", and its ↗ still works.
- Kept from today: the heading opens the repository, a row opens its item, "Copy all links" (copies the rows of the tab
  shown), the account line, the failure and loading lines, the full list loading in the background.

### Pull: switch to the default branch and pull (user's option 1)

"Pull" means: the workspace's checkout switches to the repository's default branch (main, or develop where that is the
default) and pulls. A button above the tabs reads **"Switch to main and pull"**, or **"Pull main"** when the checkout is
already on it (with the real branch name).

While a chat is working in that workspace, the button refuses and says so: "A chat is working here. Pull when it is
done." (user's choice: refuse, not ask, not just do it). Nothing is switched.

### Merge: a button per row and a confirmation strip (user's choice, M1)

- Every PR row has a **Merge** button. It does nothing but open a strip under the row:
  "Merge #405 into main?" plus one line about the checkout.
- When this workspace (the repository line the panel was opened from) is on the PR's branch, the strip offers
  **Merge and pull** (default), **Merge only**, **Cancel**. Otherwise **Merge** and **Cancel**.
- Since #426 the strip offers **Merge and pull** too when the workspace is on the default branch the PR merges into
  ("This workspace is on main. Merge and pull brings it up to date."): the merge alone left that `main` behind GitHub
  with no pull on offer. The tile then also says so: a line on the default branch that lacks GitHub's newest commit
  shows "12 behind" in place of "clean", from the GitHub read the Yard already makes (no fetch).
- Merge and pull = the merge, then the pull above.

## Calls made by Claude (the user read them in the playback and said "looks good")

- The merge is a merge commit (`gh pr merge <n> --merge`), the branch stays: that is how this repository's PRs have
  been merged so far, and GitHub's auto-delete is off.
- The account that reads the repository for its pills is the account that merges, through the existing per-account
  token path (`GhCli ...AsAsync(login, ...)`), never `gh auth switch`.
- The pull is fast-forward only. A local default branch with commits that are not on GitHub stops the pull with the reason.
- Uncommitted changes to tracked files refuse the switch, checked right before it with
  `git status --porcelain --untracked-files=no` (not the line's dirty count, which includes untracked files and would
  refuse in the user's usual state): "Merged #405, but didn't switch: 2 files have uncommitted changes." Untracked files
  are carried across by git; when one would be overwritten, git refuses on its own and that refusal is the message.
- When a chat in that workspace is working, the strip says "A chat is working here, so the switch is left out." and
  offers only **Merge only** and **Cancel**: the same refusal as the Pull button, so the Yard never switches a branch
  under a working chat. (This replaces "Merge only becomes the default", which the user approved before choosing
  "refuse" for the Pull button; changed for consistency.) "In that workspace" means a chat whose working folder is inside the line's
  checkout; where that routing is too costly, the tile's `IsWorking` is acceptable (it errs on the careful side).
- A merge or pull, once started, is never cancelled: killing `gh pr merge` or `git pull` midway leaves the result unknown
  or a `.git` lock behind. It runs in a Yard-level service that outlives the panel. Closing the panel works as today; the
  outcome shows in the panel if it is open (or reopened for that line), and a failure also shows on the Yard's existing
  failure line (#382) when the panel is closed. While one runs on a line, that line's Merge and Pull buttons are disabled.
- The panel opens on the Pull requests tab when the repository has open PRs, on Issues otherwise. A click that lands on
  a pill opens that pill's tab (it is still a click on the line).
- Every outcome is one line in the panel's message area, e.g. "Merged #405. CodeSwitchX is on main, 3 new commits.",
  "Can't merge #399: it has conflicts with main." (with the ↗ to the PR), "Pull stopped: main has local commits that
  aren't on GitHub."
- After a merge, the row leaves the list and the counts and pills update; after a pull, the line's branch updates.

## Delivery: one milestone, three issues, one PR each

Milestone "Yard: merge and pull". In this order, since each builds on the one before:

1. **The panel**: the repository line as click target, the A1 tabs with ↗ links, empty tabs, default tab. Usable on its
   own: every tile reaches GitHub, both lists in one place.
2. **Pull**: the "Switch to main and pull" / "Pull main" button with its refusals and messages. Usable on its own after
   a merge in the browser.
3. **Merge**: the Merge button per row and the strip (Merge and pull / Merge only / Cancel, busy-chat default),
   reusing the pull from issue 2.

## Left to the builder

- Where the default branch name comes from (GitHub's `defaultBranchRef`, read with the pills, or
  `refs/remotes/origin/HEAD`), and what happens when they disagree or neither is known.
- How the line keeps its repository and account when both counts are 0 (today a pill exists only with a count), and
  what a line without a GitHub remote, or whose read failed, does on hover and click (it should not look clickable).
- The git runner for the writing commands (`git switch`, `git pull --ff-only`) next to `GitInspector`'s read-only one,
  its timeouts, and how a pull's failure text is turned into a message without echoing private names to the log.
- How "this workspace is on the PR's branch" is matched (the line's branch against the PR's `headRefName`; a PR from a
  fork never matches).
- A merge refused by GitHub (conflicts, not mergeable yet, no write access): show GitHub's reason in a short form.
- Keyboard and screen reader names for the line, the tabs, the ↗ links, the Merge button and the strip.
