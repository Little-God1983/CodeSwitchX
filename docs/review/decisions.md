# Review decisions

Read by every layer review (see `review-prompt.md`). "By design" items are settled and
are not findings. When a review rules something as by design, it is added here in that
layer's PR, with the issue number.

## By design

### Deviations from the original design document

The deviations table at the top of `docs/superpowers/specs/2026-09-23-codeswitchx-design.md`
is settled: WPF instead of WinUI 3, project and file names, `csx-hook.exe`, the
`%LOCALAPPDATA%\CodeSwitchX` data folder, H.NotifyIcon.Wpf, hook entries recognised by the
`csx-hook` marker in the command, named pipe plus 127.0.0.1 transport behind one token,
Ctrl+Shift+Alt+1..9 jump hotkeys, `summary` lines as chat titles (current Claude Code writes
its generated title as `ai-title` lines instead, which count the same, L5 #8), and `SessionStart` with
`source: compact` leaving the state alone.

### Rulings from the PR #1 review rounds

| Area | Decision | Reason |
|---|---|---|
| Hosting | VS Code windows are hidden with `ShowWindowAsync`, not `DWMWA_CLOAK` | Cloaking a window owned by another process returns `E_ACCESSDENIED` |
| Hook relay | Reads up to 16 MiB of stdin; strings are cut at 2000 characters and nested objects or arrays over 8 KiB (`tool_input`, `tool_response`) are dropped | Keeps every envelope under the Event API body limit; the app only needs metadata |
| Hook relay | The relay is framework-dependent and copied to the UI output until the Native AOT build tools (C++ linker) are installed | Build environment, not code |
| Sessions | `SystemProcessProbe` treats a process it may not query (`Win32Exception`) as alive | The PID exists; being unable to read it is not proof of death |
| Sessions | `Restore` demotes only quiet Working sessions; Waiting is kept, and Waiting never decays on the idle timer | A chat blocked on a permission prompt is quiet but still needs the user |
| Sessions | A restored session whose saved `claude` PID is gone drops to Idle and forgets the PID | A dead PID must never be judged again after it is reused |
| Sessions | `Restore` changes the snapshots in memory only; the stored row keeps its old state and PID until the chat's next change (L2 #5) | The only reader is the next restore, which judges the same PID against the same last event and reaches the same result |
| Sessions | A PID counts as the chat's process only if that process started no later than the chat's last event (L2 #5) | Hooks are timestamped on receipt while claude waits for them, so the chat's own process always started earlier; no start time needs storing. `LastEventAt` never goes down, so a backwards clock step only matters between claude's launch and its first hook; that chat shows Errored until the clock catches up (accepted, Low on #3) |
| Sessions | `SessionEngine` publishes snapshots under its lock with a monotonic `Version`; the bus is synchronous and consumers drop older versions | Keeps delivery in engine order across threads |
| Persistence | The transcript indexer never writes cursors; `PersistenceWriter` commits usage, cursors and seen message ids in one transaction | Usage and the offset that covers it must not diverge after a crash |
| Persistence | A failed batch is retried on the next flush; once more than 5000 items are retained they are dropped with an error log | Bounded memory when the database stays unavailable |
| Persistence | The newest 20,000 seen message ids are kept, pruned on the hourly cycle | `--resume` dedup across restarts at bounded size |
| Persistence | Hook payload JSON is not stored unless `StorePayloads` is on | Transcripts and payloads contain source code and prompts (spec, Security) |
| Persistence | Migrations run only when one is pending; an up-to-date database gets EF Core's pending-model-changes check on its own. Before an upgrade, an EF Core migration lock row that is still there after 10 s is deleted as left over from a start that did not finish (L3 #6) | EF Core waits for that row without a timeout, so a leftover row hung every later start. A second instance that is upgrading holds the row only while its migration runs, well inside the 10 s |
| Persistence | `SessionStore.UpsertAsync` keeps a stored rename when the incoming record is not locked (L3 #6) | Nothing unlocks a title (`SessionEngine.Rename` only locks), so an unlocked record for a renamed chat is a fresh snapshot. A feature that resets a title to automatic has to change this merge as well |
| Ingest | A transcript pass moves the chat's inferred state only when it has lines with a timestamp and reaches the end of the file; an interrupt in a pass cut short is reported with the pass that reaches the end. A sub-agent's recent writes count as its parent chat's activity (Working); a quiet sub-agent moves nothing (L5 #8) | Claude Code writes its metadata lines (`ai-title`, `last-prompt`, ...) without a timestamp, some of them mid-turn. A pass cut short by the 8 MiB cap stops mid-file. The parent waits on the Task call while its sub-agent works |
| Ingest | A transcript whose stored offset no longer follows a line end, or that shrank below it, was rewritten: it is read again from the start without sending its title again, and usage stamped at or before the newest usage already counted from it is not counted again. After a restart, until the file counts new usage, the bound is the last write indexed before it (L5 #8) | Claude Code cuts a retracted message out of the file and writes back what followed. The message id memory keeps only the newest 20,000 ids, so after a long history it no longer knows which of a chat's earlier messages were counted. Assistant lines are stamped when their message starts, so they can be older than the line above them but are in order among themselves; the file's write time would cover lines not read yet |
| Ingest | The transcript watcher is replaced by a fresh one every minute, followed by a full scan; after a watcher error the indexer polls every 2 s until then (L5 #8) | A watcher can stop without an error: renaming its folder away takes the watcher with it, and a new folder created in its place is never seen |
| Hook installer | Entries use Claude Code's exec form: `command` is the relay path and `args` holds the event name. This needs Claude Code 2.1.139 or later (`args` arrived in 2.1.139, `StopFailure` in 2.1.78); the installer does not check the version. An install from before this shows Partial (`StopFailure` missing), and Install converts its entries (L6 #9) | Without Git Bash, Claude Code runs a shell-form command through PowerShell, which cannot run a quoted path followed by an argument. The exec form is spawned directly, without a shell. Claude Code updates itself by default |
| Hook installer | A repeated key in settings.json is refused only where the installer reads: the root and the `hooks` tree. Anywhere else it is written back as it was. A settings.json that cannot be used shows its reason in Settings (L6 #9) | Claude Code keeps the last value of a repeated key, so such a file works. Rewriting a part that repeats a key would silently keep only one of them |
| Hook installer | `StopFailure` is installed next to the spec's eight events and ends a turn like `Stop` (L6 #9) | Claude Code sends it instead of `Stop` when a turn ends on an API error (usage limit, overload, prompt too long); without it the chat stays Working |
| Hook relay | `SubagentStop` has no signal; like any hook it still counts as the chat's activity (L6 #9) | It carries the parent chat's `session_id` and fires while the parent's turn goes on, which the parent's own `PostToolUse` or `Stop` ends. As `Stop` it would idle a working chat |
| Telemetry | The shipped prices stay in code (`DefaultPricing`); the `PricingRules` table holds only the user's own rules, each overriding the default for its model. The `RemoveSeededPricing` migration deletes the copies earlier versions stored (L4 #7) | A stored copy of a default would override its later correction, so a fixed price would never reach an existing install. Every stored row was such a copy, because nothing writes a rule of the user's own yet |
| Hosting | Windows are matched by their title. A window is taken as the workspace's without asking only when it is the one window that names it where VS Code writes the folder name (right before the app name, or before the workspace's own profile, after at most the active editor) and no docked tile has the same folder name. Otherwise (two windows do, a docked namesake's floating editor window might, or a title names it only elsewhere: a longer folder name, a profile, an editor tab) CodeSwitchX launches VS Code for the folder and adopts the new window or the one VS Code brings forward. The window in front counts as VS Code's answer only once the launched Code.exe has exited and no new VS Code window is still hidden or untitled, and only when it is in front on two polls in a row. A single open window named like two registered folders is adopted by whichever tile opens first. So is a window that names a folder where VS Code writes it by coincidence: a folder named like a profile that another window uses with no editor open, or like a file open in a window without a folder (L7 #10) | Nothing a VS Code window shows without an extension tells two folders of the same name apart, or a folder name from a profile or file name in the same place; the spec's Link extension handshake is what will confirm the folder. Asking VS Code for every window would launch Code.exe and raise the window on every first open. VS Code can bring a window forward only when Windows lets it take the foreground, so with CodeSwitchX in the background (AutoStart) an open that needs VS Code's answer can end Stopped, and a click on the tile opens it. A folder VS Code opens with a profile it remembers, which the tile does not name, needs that answer too |
| Hosting | At startup every hidden VS Code window that shows a folder is shown again, whether a registered workspace owns it or not, unless another CodeSwitchX is running. A failure there is logged and does not stop the start (L7 #10) | Only CodeSwitchX hides such a window, so one that is hidden at startup was left by a run that ended without releasing it (a crash, End task). Hosting does not know the registered workspaces. A second instance would show the windows the first one hides; since L8 #11 a second start ends before the sweep, and the check stays as a guard |
| Hosting | Snap-back leaves a window alone after 5 snap-backs from one and the same place, each within 2 s of the last, until its next dock or show. Snap-backs while the primary mouse button is held are not counted (L7 #10) | A tiling window manager, or a second CodeSwitchX, puts the window back in its own place after every snap, and the two would move it to and fro for ever. A drag reaches a new place each time, and while the mouse is held still the move loop puts the window back under the cursor after every snap: the user's drag is always followed and its last snap-back wins |
| Shell | One CodeSwitchX runs per user, across sessions. A later start brings the running window forward and ends before it opens the log, the database or the pipe. When the window does not come forward within 5 s (the running instance is still starting, closing or hung), or the running instance is in another session of the same user, the start says so and ends. A claim name this user's CodeSwitchX did not create (another account's, or another kind of object) is logged, and the start goes on without the check (L8 #11) | The Event API's pipe and the database are per user, not per session, and the running instance holds them until its host has stopped. Another account must not keep CodeSwitchX from starting by creating the name first; a start without the check still fails with a message on the pipe if an instance does run |

## Deferred from PR #1

Reported in the PR #1 rounds and left open. Each is assessed once, in the review of the
layer it belongs to: fixed there, moved to "By design" above, or parked on #3.

| Item | Layer |
|---|---|
| A reused PID held by an ordinary live process is not detected (needs the process start time stored next to `ClaudePid`) | L2 #5: fixed, the start time is compared with the chat's last event instead |
| Telemetry time windows only recompute when new usage arrives (midnight and rolling windows go stale while idle) | L4 #7: already fixed in PR #1, a one-minute refresh timer (`TelemetryService.RefreshInterval`) rolls the windows forward; covered by `Time_windows_roll_forward_on_the_minute_timer_while_nothing_is_indexed` |
| `SubagentStop` mapping | L6 #9: acceptable as is, moved to "By design" (no signal) |
| Pruning of `settings.json` backups made by the hook installer | L6 #9: still there, but Low: one small file for each install or removal that changes `settings.json`, and none when nothing changes. Claude Code ignores them (parked on #3) |
| SQLite `Cache=Shared` together with WAL | L3 #6: still there, but Low: a read waits for the writer only on a table the writer has open. Every read of a table the writer uses happens once at startup (session restore, the indexer's cursors and seen message ids, the telemetry history), so at worst one startup read waits for one flush (parked on #3) |
| Full rescans on every transcript change | L5 #8: still there, but Low: the results are correct, and the cost is mostly a second read of each file's size and time (about 145 ms per scan for 2565 transcripts, while a chat writes) (parked on #3) |
| Snap-back oscillation guard | L7 #10: fixed, snap-back gives up on a window that keeps being moved to the same place (see "By design") |
| Orphaned hidden VS Code windows are not swept at startup | L7 #10: fixed, `HiddenWindowSweep` shows them before anything is opened (see "By design") |
| Single-instance enforcement | L8 #11: fixed, a second start brings the running instance forward and ends (`SingleInstance`, see "By design") |

## Deferred from layer reviews

Confirmed in one layer's review, but the fix belongs to a layer that has not been reviewed yet.
Assessed in that layer's review like the items above.

| Item | Layer | Found in |
|---|---|---|
| A chat not restored at startup (quiet for longer than the 24 h restore window) that becomes active again gets a fresh snapshot, and `SessionStore.UpsertAsync` overwrites the stored row with it: a renamed title, `TitleLocked` and `StartedAt` are lost | L3 #6: fixed, the upsert keeps the earlier start, a stored rename and a stored title the snapshot lacks | L2 #5 |
| Such a chat still shows the fresh snapshot's title (or none) instead of its stored rename until the next restart restores the row; the restore window is decided in `StartupCoordinator` | L8 #11: acceptable as is, it cannot happen yet: nothing renames a chat (`SessionEngine.Rename` has no caller). The feature that adds renaming has to bring a stored rename back for a chat that was not restored | L3 #6 |
| The app's own host (`Host.CreateApplicationBuilder()` in `App.OnStartup`) uses the current directory as its content root and reads the `appsettings.json` there and the environment. A malformed `appsettings.json` in the folder CodeSwitchX is started from fails the start. The Event API no longer reads any configuration (L6 #9); the app host needs its own fix, e.g. `ContentRootPath = AppContext.BaseDirectory` and no default sources | L8 #11: fixed, the app host reads no configuration (`App.CreateHostBuilder`) | L6 #9 |
| Nothing clears .NET's cached local time zone, so when Windows switches time zone while the app runs, "Today" keeps resetting at the old zone's midnight until the next restart. `TelemetryService` already reads the zone on every minute tick; clearing the cache belongs in `App` | L8 #11: fixed, the shell's window clears the cache when Windows sends WM_TIMECHANGE (`TimeZoneRefresh`) | L4 #7 |

## Tracked elsewhere

Found in a layer review, confirmed, and moved to its own issue because fixing it is new
behaviour rather than a defect in that layer. Not a finding in later reviews.

| Item | Issue | Found in |
|---|---|---|
| Git worktrees added after a workspace was registered are not detected | #14 | L1 #4 |
| Cache writes to the 1-hour cache are priced at the 5-minute rate (1.25x input instead of 2x); whether Claude Code writes to that cache is checked there | #18 | L4 #7 |
| A chat name set with `/rename` in Claude Code (`custom-title` lines) is not shown | #20 | L5 #8 |
