# Raven's brain on `ClaudeCliSession` (#314)

**Goal:** Raven's brains run on the shared `LittleGod.Llm.ClaudeCli` 0.4.0 package's `ClaudeCliSession`; the app keeps only
what is Raven's: prompts, roles, wording, the Yard's MCP config per chat, the session store, the tool-call ownership
(`AskedChats`), the unasked turns.

**Spec:** issue #314; `E:\Repos\Nuget\LittleGod.Llm\docs\migrating-an-app.md`, `docs/claude-cli.md` §5, §8, §10, §11.

## Shape

- `nuget.config` with the local feed for `LittleGod.*`; `LittleGod.Llm.ClaudeCli` pinned `[0.4.0]` in `Directory.Packages.props`.
- `IBrainSession` (internal): what the brain uses of `ClaudeCliSession` (`AskAsync`, `WarmUp`, `Rest`, `RestartWhenIdle`,
  `Dispose`). The package's launcher is internal, so the brain's tests run over a fake session made from the options the
  brain hands its factory.
- `ClaudeCliBrain` becomes an adapter: settings → `ClaudeCliSessionOptions` before every question and warm-up, updates →
  `BrainEvent`s, `LlmException` → `BrainFailed`, the session's callbacks → `UnaskedTurns`, `AskedChats`, `IBrainSessionStore`.
- Deleted: `ClaudeCliLocator`, `ClaudeStream` and their tests, the mechanics in `ClaudeCliBrain` and the mechanics tests
  in `ClaudeCliBrainTests` (ported to the package's `ClaudeCliSessionTests` with #5).

## Rulings

- **Compactor (user, 2026-10-26):** the package cannot set `CLAUDE_CODE_ENTRYPOINT=claude-vscode`, which keeps a compacted
  chat in VS Code's list; filed as Little-God1983/LittleGod.Llm#10. `ChatCompactor` keeps `BrainProcess.cs` (the app's
  launcher) until then, and uses the package's locator.
- **API key variables (user, 2026-10-26):** kept, as today (`KeepApiKeyEnvironment = true`).
- **Isolation:** `UserSettingsNoHooks` for every role, as today's flags: user settings kept, every hook off, strict MCP,
  `dontAsk`. Raven: `--tools SendMessage`, the Yard's server allowed; chat 0 also `--disallowedTools` for the card tools;
  teller and summarizers: no tools, no server, `FreshConversationPerTurn`. Hooks run exactly as before, so nothing to ask.
- **Usage:** the issue says to feed `UsageStore` from `UsageContent`. The brain never wrote usage there (`UsageStore` holds
  what the transcript indexer reads), so nothing changes.
- **MCP config:** the session writes the servers into its own scratch folder and deletes it with the process. The scratch
  root is `raven\mcp`, which the app already clears at start (a crash leaves the token there); `Clear` now also removes
  folders.
- **Wording:** the package's texts say "The Claude CLI …"; the brain says "Raven's brain …" for those, and
  "Raven's brain could not answer: …" before any other reason. Notices are worded by their `Kind`, as before.
- **Options set before each use:** model, effort and the MCP servers are set on the options right before the session's
  `AskAsync` is read, and before `WarmUp`, on the calling thread: the session reads them when a process starts. The ask
  itself registers with the session at once, so a `Rest()` queued after it is still dropped.
