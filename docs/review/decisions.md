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
Ctrl+Shift+Alt+1..9 jump hotkeys, `summary` lines as chat titles, and `SessionStart` with
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

## Deferred from PR #1

Reported in the PR #1 rounds and left open. Each is assessed once, in the review of the
layer it belongs to: fixed there, moved to "By design" above, or parked on #3.

| Item | Layer |
|---|---|
| A reused PID held by an ordinary live process is not detected (needs the process start time stored next to `ClaudePid`) | L2 #5: fixed, the start time is compared with the chat's last event instead |
| Telemetry time windows only recompute when new usage arrives (midnight and rolling windows go stale while idle) | L4 #7 |
| `SubagentStop` mapping | L6 #9 |
| Pruning of `settings.json` backups made by the hook installer | L6 #9 |
| SQLite `Cache=Shared` together with WAL | L3 #6: still there, but Low: a read waits for the writer only on a table the writer has open. Every read of a table the writer uses happens once at startup (session restore, the indexer's cursors and seen message ids, the telemetry history), so at worst one startup read waits for one flush (parked on #3) |
| Full rescans on every transcript change | L5 #8 |
| Snap-back oscillation guard | L7 #10 |
| Orphaned hidden VS Code windows are not swept at startup | L7 #10 |
| Single-instance enforcement | L8 #11 |

## Deferred from layer reviews

Confirmed in one layer's review, but the fix belongs to a layer that has not been reviewed yet.
Assessed in that layer's review like the items above.

| Item | Layer | Found in |
|---|---|---|
| A chat not restored at startup (quiet for longer than the 24 h restore window) that becomes active again gets a fresh snapshot, and `SessionStore.UpsertAsync` overwrites the stored row with it: a renamed title, `TitleLocked` and `StartedAt` are lost | L3 #6: fixed, the upsert keeps the earlier start, a stored rename and a stored title the snapshot lacks | L2 #5 |
| Such a chat still shows the fresh snapshot's title (or none) instead of its stored rename until the next restart restores the row; the restore window is decided in `StartupCoordinator` | L8 #11 | L3 #6 |

## Tracked elsewhere

Found in a layer review, confirmed, and moved to its own issue because fixing it is new
behaviour rather than a defect in that layer. Not a finding in later reviews.

| Item | Issue | Found in |
|---|---|---|
| Git worktrees added after a workspace was registered are not detected | #14 | L1 #4 |
