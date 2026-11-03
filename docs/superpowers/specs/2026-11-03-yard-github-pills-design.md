# Open issues and PRs on the Yard tiles

## Why

The user works across many repositories under two GitHub accounts and at least one other owner. Today the Yard says
nothing about GitHub, so seeing what is open means going to each repository's page, and that page only works in the
browser logged in to the right account: Firefox holds one GitHub login and Edge the other. A private repository opened in
the wrong browser shows GitHub's 404.

The goal is to see at a glance how many issues and PRs are open per repository, read their headlines without leaving the
Yard, and reach any of them in the browser that can actually show it.

## Goals

- Each repository line on a tile shows its open issue count and its open PR count.
- Hovering a count previews the newest items. Clicking it lists all of them. Clicking an item opens it on GitHub in the
  browser logged in to the right account.
- Both accounts work at the same time, with no key to create, paste or store.

## Non-goals

- No creating, closing, commenting or merging from the Yard. Raven already handles issues through a chat, and that stays
  as it is.
- No Yard-wide total. A repository without a tile does not appear. It can be added later.
- No check results or review status on PRs in this version. It can be added later without changing the layout.
- GitHub only. A remote on any other host shows nothing.
- No notifications, sounds or speech about new issues or PRs.

## Anti-goals: a failure even if it technically works

- Running `gh auth switch`, or changing the active `gh` account in any way. It is global, so it would change the account
  under every running chat that uses `gh`.
- Storing, logging or passing a token on a command line. CodeSwitchX must never hold a token longer than one process start.
- Writing issue or PR titles to the database or the logs. Many come from private repositories.
- Making the Yard busier: errors on tiles, spinners, or lines that grow the tile.
- Slowing the Yard's start or its git refresh while GitHub answers slowly.
- **Private content in this repository.** CodeSwitchX is a public repository. Commits, tests, docs, PR text and review
  screenshots must not show titles, labels or names of private repositories. Use a scratch repository or public ones in
  anything committed, and keep other screenshots out of the repository.

## On the tile

Each line of `GitLineTemplate` gains up to two pills between the branch and the existing `clean` / `n changed` pill:

| Pill | Colours | Icon |
|---|---|---|
| Issues | text `#B9CDF0`, background `#3A4250`, border `#566378` | open-issue dot |
| Pull requests | text `#A6E3E9`, background `#2D464B`, border `#4B6F76` | branch / pull request |

- A count of zero shows no pill. A repository with no issues and no PRs looks as it does today.
- Each repository line has its own pills, because a tile's repositories can belong to different accounts. A tile does not
  grow, and a long branch name gives way (it is already trimmed with an ellipsis).
- Pills scale with the Font size slider, as the rest of the tile's text does.
- **Clicking a pill must not open VS Code**, which a click anywhere else on the tile does.
- `GitLineTemplate` is shared with the git lines in Raven's chat header. The pills appear on Yard tiles only, and the
  header stays as it is. (Builder's call how: for example a separate template or a flag.)

## Hover preview

Hovering a pill shows a popup, with a short grace period so the pointer can move into it. It holds:

- A heading: the repository's name and the count, such as "SomeRepo 19 open".
- The five newest items, most recently created first.
- Issue rows: number, headline, labels in their GitHub colours, and "updated 4d ago".
- PR rows: number, headline, branch name, "by <login>" when the author is not the account that read it, and the update age.
- A footer when there are more: "+14 more. Click the pill for all of them".

The headline is the most prominent thing in a row, because many issues have no labels. The popup appears at once from the
data of the last refresh, with no request on hover.

## Full list

Clicking a pill opens a panel on the right of the Yard, over the tiles.

- It is headed by the repository and count, with a line under it such as "Read with <account>. Opens in <browser>."
- It holds every open item of that kind, using the same rows as the preview, and it scrolls.
- It loads fresh on opening and updates the tile's count with the result. If loading fails, it shows the last known rows
  and says so in one line.
- Clicking a row opens that item in the account's browser, and the panel stays open.
- The ✕, Escape or a click outside closes it.

## Accounts and tokens

`gh` already keeps both accounts in Windows Credential Manager (`gh:github.com:<login>`), verified on the user's machine
with `gh` 2.89.0:

- `gh auth status --json hosts` lists the accounts without showing tokens.
- `gh auth token --user <login>` gives a token for any account without switching the active one.
- Setting `GH_TOKEN` on one child process's environment makes that process act as that account. The active account stayed
  the same while both were read side by side, private repositories included.

The owner's name alone cannot decide the account: one repository belongs to a third owner, an organisation one account can
read and the other gets a 404 from. Trying accounts until one answers is not enough either, because both can see every
public repository, and the account chosen decides the browser, the panel's "Read with" line and the "by <login>" on PR rows.
So among the accounts that can see a repository, the order is:

1. The account whose login is the repository's owner.
2. Otherwise the account with the highest `viewerPermission` on it.
3. On a tie, the first login alphabetically.

The order must never depend on which account is active or on the order `gh` lists them, since chats run `gh auth switch`
and the result must not change between restarts. The result is remembered and worked out again if that account later fails.
A new organisation or a new `gh auth login` needs no setting.

A repository is identified by the `origin` remote of the line's folder, in its `https` or `ssh` form, with or without
`.git`. Names can be unusual (one ends in a hyphen), so parse rather than assume. Without an `origin` on `github.com`, the
line has no pills.

## Refresh

- Counts and the preview's five newest items load shortly after the app starts and every 10 minutes after that. A new issue
  can take up to 10 minutes to show on a tile. Opening the list refreshes that one repository at once.
- Everything is held in memory only, so after a restart pills appear with the first refresh.
- The refresh runs apart from the git refresh and never holds it up, nor does one slow repository hold up the rest.
- Two lines in the same repository share one result.
- Stay well inside GitHub's limits. At about 30 repositories, this should be a few hundred requests per hour against 5,000
  per hour per account. GitHub's search API has a far lower limit of 30 per minute, and one search call returned 100 items
  at most, so it does not suit per-repository counts.

## When something fails

Quietly, on the tile: no error text and no spinner.

| Situation | What shows |
|---|---|
| `gh` not installed, or no accounts | No pills. The GitHub settings page says how to log in |
| No account can see the repository | No pills on that line |
| A refresh fails (offline, rate limit, slow) | The last known counts stay |
| The app has just started and nothing has loaded yet | No pills |

Failures go to the log with the repository's path only, without titles or tokens.

## Settings: a new GitHub page

A new page under the CodeSwitchX group in `SettingsPageItem.All`, with the search keywords "GitHub", "issues", "pull
requests", "PR", "browser", "account" and "Firefox". Each account from `gh` gets one row:

```
 Little-God1983
 Opens in   [ Firefox (default-release)   v ]   [ Test ]
```

Under the rows: "Accounts come from the GitHub command line (gh). To add one, run gh auth login; it shows up here by itself."

- **The dropdown** lists "Windows default browser" and each browser installed on this PC. A browser with several profiles
  lists each profile as its own entry, because each profile holds its own logins and a GitHub session may be in just one.
  The user's Firefox has three profiles and their Edge has one. The browser list comes from
  `HKLM\SOFTWARE\Clients\StartMenuInternet` and `HKCU\...`. Firefox's profiles come from `%APPDATA%\Mozilla\Firefox\profiles.ini`,
  and Edge's and Chrome's from `Local State` (`profile.info_cache`). Skip the Internet Explorer entry.
- **Test** opens `https://github.com/settings/profile` in the chosen browser, which shows who that browser is logged in as.
- **The default** is the Windows default browser until a choice is made.
- **The only thing saved** is the choice per login, as a new key in `SettingKeys`, saved as the user makes it, like other
  settings.
- If a saved browser or profile no longer exists, the dropdown shows it as missing. Opening an item then behaves as under
  "Opening an item" when a browser cannot open it.
- The app cannot tell who is logged in to a browser, and must not read browser cookies to find out. This pairing is the
  user's to make.

## Opening an item

Start the chosen browser directly with the item's address, with no use of the Windows default unless that is the choice.
Firefox takes a profile with `-P`, and Edge and Chrome with `--profile-directory`. The URL must be passed safely as an
argument. Only `https://github.com/` addresses are ever opened.

Firefox with several profiles is checked early, with Firefox already running under another profile, since a running
instance can take over or refuse a second profile. If it does, a separate Firefox process and window is acceptable.

If a browser cannot open an item, or the saved one is gone, the panel says so in one line and points to the GitHub settings
page. It never falls back to the Windows default, which would bring back the 404 this feature exists to prevent. The
"quiet failures" rule is about the tile; the user has just clicked something here, so they get an answer.

## Testing

- Tests with fake `gh` and browser runners cover: remote parsing, finding and re-finding the account, failure rows, the
  dropdown's contents from sample `profiles.ini` and `Local State` files, browser arguments, counts on `GitLine`, and that
  tokens never reach a command line or a log.
- On-screen run on the BenQ monitor, as the user's global rule requires: pills on single- and multi-repository tiles, hover,
  list, and Test.
- One exception, allowed by the user for this check only: real clicks open a real private issue in each browser's real
  profile, so those tabs land wherever the browser's window is, the LG monitor included. Don't move the user's windows;
  confirm the page from the window title, keep it to a handful of tabs, and don't commit a capture of them. Everything else
  stays on the BenQ.

## Decided by the user

- Layout A: pills with a hover preview, not titles written on the tile.
- Two pills, one for issues and one for PRs, rather than one combined count.
- PRs are shown as well as issues.
- A settings page with one browser per account, with Firefox listed per profile and a Test button.

## Calls made in the design, open to change

- Clicking a pill opens the list on the right of the Yard. Clicking a row opens the browser.
- A 10-minute refresh.
- Tiles only: no pills in Raven's chat header.
- Quiet failures, with no messages on the Yard.
- Account discovery rather than a setting per repository, preferring the account that owns the repository.
- A browser that cannot open an item gets one line in the panel, never the Windows default as a fallback.
- Profiles are listed for Edge and Chrome as well as Firefox, whenever a browser has more than one.
- PR rows leave out labels unless a PR has some. None of the user's open PRs has labels today.

## Left to the builder

How requests are made (`gh issue list` / `gh pr list` / `gh api graphql`), as long as the total count is right beyond any
list limit; which project holds the code, following the existing split (`GitInspector` shows the process-running pattern);
the popup and panel's WPF details; exact margins and timing.

## Delivery

One issue, one PR, on `feature/yard-github-pills`. The pills and the browser setting ship together: without the setting, a
private item can open in the wrong browser and show a 404.
