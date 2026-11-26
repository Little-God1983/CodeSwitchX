# Settings: Claude Code's idle compaction (#450)

## Intent

The user wants a chat compacted shortly before its prompt cache runs out, so that coming back after the hour is cheap. Claude
Code already does exactly this on its own (`idleCompaction`), and does it better than CodeSwitchX could. What is missing is a
place to see it, switch it, and set the chat size it starts at. This change adds that place to the Yard page of Settings.

## What Claude Code does (2.1.332, read from its binary and the user's transcripts)

- A chat's prompt cache lasts one hour from its last request. At 90 % of that hour (minute 54) the chat's own process writes a
  summary, reading the warm cache, and holds it.
- If the user comes back after the cache expired, the next turn carries on from the summary; the chat shows "Compacted using a
  summary written while you were away". If the user comes back sooner, the chat keeps its whole conversation.
- It runs only when: `idleCompaction` is not `false` (default true), auto-compact is on, the cache is a 1-hour one, the session
  is attended, the account is not near a usage limit, and the context holds at least 200,000 tokens. That limit is read from
  the environment variable `CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS` and never goes below 100,000.
- 19 such compactions are in the user's transcripts (2026-10-27 to 2026-11-24), all for chats of 208,000 to 808,000 tokens.

## Goals

- A switch for `idleCompaction` and a choice of the size limit, on Settings > Yard.
- Both live in Claude Code's user-level `settings.json` only. CodeSwitchX stores nothing of its own, so the page always shows
  what Claude Code will do, also after the file was edited elsewhere.
- The file is written with the care the hook installer takes: a backup, an atomic replace, a linked file written where it
  points, and nothing else in the file touched.

## Non-goals

- CodeSwitchX compacting chats on a timer of its own (see Approaches).
- Project-level, local or managed Claude Code settings, which may overrule the user-level file.
- Raven changing these two settings by voice (no entry in `AppSettings`).
- Showing on a chat row that a summary is held: only the chat's process knows.

## Anti-goal

The page must never claim more than the file says. A value it cannot read is shown as Claude Code would treat it, and is not
rewritten until the user changes something.

## Approaches

1. **CodeSwitchX compacts at minute 55, with its existing compaction (the first idea).** Ruled out by measurement: the
   compaction runs in a separate headless Claude Code, which does not share the tab's cache. On a hot chat of 107,892 tokens,
   30,985 were read from cache and 74,690 were paid anew. It would pay for every chat that goes quiet, and it shrinks a chat the
   user returns to within the hour.
2. **Send `/compact` into the tab.** Not possible: the Claude extension's open command only puts a prompt into the input box.
3. **Operate Claude Code's own switch (picked by the user).** No compaction code in CodeSwitchX; a settings card that reads and
   writes two values in Claude Code's file.

## Design

### On screen

A second card on Settings > Yard, below "Chats on a tile", built like the rows there (label and help text left, control right;
the boolean is the `Switch`-styled CheckBox the other pages use):

```
Compaction while idle
Claude Code's own doing, set here: these two are saved in its settings file, not in CodeSwitchX.

Compact a chat before its cache runs out                                  [ on ]
  After 54 idle minutes Claude Code writes a summary while the chat's
  cache is still warm. Back after the hour, the chat carries on from the
  summary. Back sooner, it keeps its whole conversation.

For chats from                                              [ 200,000 tokens v ]
  Smaller chats are left alone. Claude Code goes no lower than 100,000.
  A change applies to chats opened after it.
```

- Size choices: 100,000 / 150,000 / 200,000 / 300,000 / 400,000 tokens. A valid other value found in the file is shown as one
  more choice.
- The size row is disabled while the switch is off.
- A failed read or write shows in the Settings view's message line, as a failed hook install does, and the controls go back to
  what the file says.

### In the file

| Control | Key in `settings.json` | Written as |
|---|---|---|
| Switch on | `idleCompaction` | key removed (Claude Code's default is on) |
| Switch off | `idleCompaction` | `false` |
| Size 200,000 | `env.CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS` | entry removed; `env` removed too if that emptied it |
| Any other size | `env.CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS` | the number as a string, `"100000"` |

Reading: `idleCompaction` counts as on unless it is the boolean `false`. The size is the integer in the env entry, raised to
100,000 if lower; missing or not a number means 200,000.

### In the code

- `ClaudeSettingsFile` (new, `CodeSwitchX.Ingest`): loading and saving Claude Code's `settings.json`, moved out of
  `ClaudeHookInstaller` unchanged in behaviour. The installer uses it and keeps throwing `HookInstallException`.
- `IdleCompactionSetting` (new, `CodeSwitchX.Ingest`): reads and writes the two values through `ClaudeSettingsFile`.
- `SettingsViewModel`: two properties, read in `Refresh()` beside the hook status, written off the UI thread on change.
- `SettingsView.xaml`: the card.

## Open point, known and accepted

That Claude Code honours the size limit from the `env` block of `settings.json` is read from its code, not seen in use: proof
needs a chat between the new limit and 200,000 tokens left idle for more than an hour, then the "Compacted using a summary
written while you were away" line on return. The switch itself is a documented setting.

## Left to the builder

Names of members, the exact wording of failure messages, and how the tests are split.
