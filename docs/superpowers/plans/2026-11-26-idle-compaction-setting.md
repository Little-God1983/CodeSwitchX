# Idle Compaction Setting Implementation Plan (#450)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A card on Settings > Yard that switches Claude Code's own `idleCompaction` on and off and sets the chat size it starts at, both stored only in Claude Code's user-level `settings.json`.

**Architecture:** The loading and saving of `settings.json` moves out of `ClaudeHookInstaller` into `ClaudeSettingsFile`, unchanged in behaviour. A new `IdleCompactionSetting` reads and writes two values through it. `SettingsViewModel` shows them, reading in `Refresh()` and writing off the UI thread on change. CodeSwitchX stores nothing of its own and compacts nothing itself.

**Tech Stack:** .NET 10, C# 14, WPF, CommunityToolkit.Mvvm, System.Text.Json (`JsonNode`), xUnit v3 + Shouldly + NSubstitute. No new package.

**Spec:** `docs/superpowers/specs/2026-11-26-idle-compaction-setting-design.md`. Read it first.

## Global Constraints

- Load the `csharp-conventions` skill (`.claude/skills/csharp-conventions/SKILL.md`) before writing C# or XAML. Warnings are errors: file-scoped namespaces, braces always.
- Branch `feature/450-idle-compaction-setting` (exists, checked out). Never commit on `main`. Commit and push after each task. Verify the branch with `git branch --show-current` before every commit.
- The repo is public: no private repo names, issue titles or tokens in code, tests or commits.
- Keys and values, exactly: `idleCompaction` (boolean, root of `settings.json`); `CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS` (string, inside the root object `env`); default size `200000`; lowest size `100000`.
- Size choices, exactly: `100000, 150000, 200000, 300000, 400000`.
- Numbers shown to the user use the invariant culture (`200,000 tokens`), whatever the Windows locale is (the user's is German).
- Nothing else in `settings.json` is ever changed. A value that did not change is not rewritten.
- No entry in `AppSettings` (Raven does not change these two), no `SettingKeys` entry, no EF migration.
- Build: `dotnet build CodeSwitchX.slnx`. Tests use Microsoft Testing Platform filters: `dotnet test --project tests/<P>.Tests --filter-class "*NameTests"`. Three `HotkeyServiceTests` fail while another CodeSwitchX runs; that is not a regression.
- If the plan does not fit the code, stop and report. Do not improvise.

## Review Focus

1. **`settings.json` cannot be written** (read-only, locked): the message line says why, and the controls go back to what the file says. Test in Task 3.
2. **German locale:** a size label reads `200,000 tokens`, not `200.000 tokens`. Test in Task 3.
3. **A size in the file that is no listed choice** (`250000`): it is shown as one more choice and selected, and is not changed by flipping the switch. Tests in Tasks 2 and 3.
4. **Odd values in the file:** `idleCompaction` that is no boolean counts as on; a size that is no number counts as 200,000; one below 100,000 counts as 100,000; `env` that is no object refuses a size write and leaves the file as it was. Tests in Task 2.
5. **No `settings.json` yet:** the page shows on and 200,000 and creates no file; switching off creates it. Tests in Tasks 2 and 3.

---

## File Structure

| File | Responsibility |
|---|---|
| Create `src/CodeSwitchX.Ingest/ClaudeSettings/ClaudeSettingsFile.cs` | Load and save Claude Code's user-level `settings.json` (backup, atomic replace, link target, prune) |
| Create `src/CodeSwitchX.Ingest/ClaudeSettings/ClaudeSettingsException.cs` | The file cannot be read, used or written |
| Create `src/CodeSwitchX.Ingest/ClaudeSettings/IdleCompactionSetting.cs` | Read and write the two values; holds the record `IdleCompaction` |
| Modify `src/CodeSwitchX.Ingest/Hooks/ClaudeHookInstaller.cs` | Uses `ClaudeSettingsFile`; public behaviour unchanged |
| Modify `src/CodeSwitchX.Ingest/IngestServiceCollectionExtensions.cs` | Registers `IdleCompactionSetting` |
| Modify `src/CodeSwitchX.UI/Settings/SettingsViewModel.cs` | Properties, read in `Refresh()`, save on change |
| Modify `src/CodeSwitchX.UI/Settings/SettingsView.xaml` | The card |
| Create `tests/CodeSwitchX.Ingest.Tests/ClaudeSettings/IdleCompactionSettingTests.cs` | Task 2 tests |
| Create `tests/CodeSwitchX.UI.Tests/Settings/SettingsViewModelTests.IdleCompaction.cs` | Task 3 tests (the class becomes `partial`) |

Namespace of the new Ingest folder: `CodeSwitchX.Ingest.ClaudeSettings`.

---

### Task 1: Move the file handling into `ClaudeSettingsFile`

A refactoring with no change in behaviour. Its test is the existing `ClaudeHookInstallerTests`, which must pass **unchanged**.

**Files:**
- Create: `src/CodeSwitchX.Ingest/ClaudeSettings/ClaudeSettingsFile.cs`, `src/CodeSwitchX.Ingest/ClaudeSettings/ClaudeSettingsException.cs`
- Modify: `src/CodeSwitchX.Ingest/Hooks/ClaudeHookInstaller.cs` (`Load`, `ReadHookEntries` call, `Save`, `LinkTarget`, `PruneBackups`, `TryDelete`, `WriteOptions`, `BackupsKept`, the two suffix constants)
- Test: `tests/CodeSwitchX.Ingest.Tests/Hooks/ClaudeHookInstallerTests.cs` (not edited)

**Interfaces:**
- Produces:

```csharp
namespace CodeSwitchX.Ingest.ClaudeSettings;

public sealed class ClaudeSettingsException(string message, Exception? inner = null) : Exception(message, inner);

/// <param name="Changed">Whether the file was written.</param>
/// <param name="BackupFile">The copy of the file as it was; null when there was none before, or nothing was written.</param>
internal sealed record ClaudeSettingsSaved(bool Changed, string? BackupFile);

internal sealed class ClaudeSettingsFile(ClaudeCodePaths paths, ILogger logger)
{
    internal const int BackupsKept = 5;
    internal static readonly JsonSerializerOptions WriteOptions; // WriteIndented, UnsafeRelaxedJsonEscaping, as today

    /// The file as text of the form it is written in: what Save compares against.
    internal static string TextOf(JsonObject settings) => settings.ToJsonString(WriteOptions);

    /// A missing file is an empty object. `read` touches the parts the caller is going to read or edit, inside the
    /// same try as today's ReadHookEntries call, so a repeated key or a bad escape there is refused here.
    /// Throws ClaudeSettingsException with today's messages.
    internal JsonObject Load(Action<JsonObject>? read = null);

    /// Today's Save: nothing written when TextOf(settings) == before and the file exists; else backup, atomic
    /// replace of the link target, prune, log. Throws ClaudeSettingsException("Cannot write …").
    internal ClaudeSettingsSaved Save(JsonObject settings, string before);
}
```

- Decided: `ClaudeSettingsFile` is not registered in DI; each user creates its own with its own logger. So `ClaudeHookInstaller`'s constructor keeps its signature (`ClaudeCodePaths paths, ILogger<ClaudeHookInstaller> logger`) and no caller or test changes.
- Decided: `ClaudeHookInstaller` keeps throwing `HookInstallException` with the same messages. Its private `Load()` and `Save()` stay as thin wrappers that call the file and rethrow `ClaudeSettingsException ex` as `new HookInstallException(ex.Message, ex)`. `ClaudeHookInstaller.BackupsKept` stays public and equals `ClaudeSettingsFile.BackupsKept` (tests use it). `Save` maps `ClaudeSettingsSaved` to `HookInstallResult`.

- [ ] **Step 1: Run the installer tests as the baseline**

Run: `dotnet test --project tests/CodeSwitchX.Ingest.Tests --filter-class "*ClaudeHookInstallerTests"`
Expected: all pass. Note the count.

- [ ] **Step 2: Create the two new files and move the code**

Move, do not rewrite: the bodies of `Load` (without `ReadHookEntries`, which stays in the installer and is passed as `read`), `Save`, `LinkTarget`, `PruneBackups`, `TryDelete` go over as they are, comments included. Only the exception type changes.

- [ ] **Step 3: Run the installer tests again**

Run: the command of Step 1.
Expected: the same count, all pass, and `git diff --stat tests/` is empty.

- [ ] **Step 4: Build and commit**

Run: `dotnet build CodeSwitchX.slnx` — expected: 0 warnings, 0 errors.

```bash
git add src/CodeSwitchX.Ingest
git commit -m "refactor(ingest): settings.json handling in a class of its own (#450)"
git push
```

---

### Task 2: `IdleCompactionSetting`

**Files:**
- Create: `src/CodeSwitchX.Ingest/ClaudeSettings/IdleCompactionSetting.cs`
- Modify: `src/CodeSwitchX.Ingest/IngestServiceCollectionExtensions.cs` (in `AddCodeSwitchXEventApi`, after `ClaudeHookInstaller`: `services.AddSingleton<IdleCompactionSetting>();`, and name it in the method's summary)
- Test: `tests/CodeSwitchX.Ingest.Tests/ClaudeSettings/IdleCompactionSettingTests.cs`

**Interfaces:**
- Consumes: `ClaudeSettingsFile.Load(read)`, `TextOf`, `Save` and `ClaudeSettingsException` from Task 1.
- Produces:

```csharp
namespace CodeSwitchX.Ingest.ClaudeSettings;

/// <param name="Enabled">Whether Claude Code compacts an idle chat before its cache runs out.</param>
/// <param name="MinTokens">The context size, in tokens, from which it does.</param>
public sealed record IdleCompaction(bool Enabled, int MinTokens);

public sealed class IdleCompactionSetting
{
    public const int DefaultMinTokens = 200_000;
    public const int LowestMinTokens = 100_000;
    internal const string Key = "idleCompaction";
    internal const string Env = "env";
    internal const string EnvKey = "CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS";

    public IdleCompactionSetting(ClaudeCodePaths paths, ILogger<IdleCompactionSetting> logger);

    /// What Claude Code will do, as the file says. Throws ClaudeSettingsException.
    public IdleCompaction Read();

    /// Writes what differs from Read(); whether the file was written. Throws ClaudeSettingsException;
    /// ArgumentOutOfRangeException for MinTokens below LowestMinTokens.
    public bool Write(IdleCompaction value);
}
```

**Rules (the spec's table, made exact):**

Read:
- `Enabled` is `false` only when the root's `idleCompaction` is the JSON boolean `false`. Anything else, or no key, is `true`.
- `MinTokens`: the `env` entry as a JSON string holding an integer (`long.TryParse`, `NumberStyles.None`, invariant) or as a JSON number that is a whole `long`; clamped to `LowestMinTokens`..`int.MaxValue`. No `env`, `env` that is no object, no entry, or anything else: `DefaultMinTokens`.
- `Load` is called with a `read` that touches the root and, if it is an object, `env` (`_ = settings.Count; _ = env.Count;`).

Write, on a freshly loaded file, comparing `value` with what `Read`'s rules give for that file:
- `Enabled` differs: `true` removes the key; `false` sets `idleCompaction` to `false`.
- `MinTokens` differs: `DefaultMinTokens` removes the entry, and removes `env` when that left it empty; any other value sets the entry to the number as an invariant string, creating `env` when there is none. When `env` exists and is no object: throw `ClaudeSettingsException($"{file}: \"env\" is not an object, so the size cannot be saved there.")` before anything is written.
- When the text after equals the text before, return `false` without calling `Save` (so no empty file is created). Otherwise `Save`, log at Information `"Idle compaction in {File}: {Enabled}, from {MinTokens} tokens"`, return `true`.

- [ ] **Step 1: Write the failing tests**

Class shape as `ClaudeHookInstallerTests` (temp home per instance, `ClaudeCodePaths(_home)`, `WriteSettings(json)`, `Settings()` parsing the file, `Dispose` deleting the folder). The tests, one `[Fact]` or `[Theory]` each:

| Test | Arrange (file) | Act | Assert |
|---|---|---|---|
| `Read_without_a_file_is_on_from_200000_tokens` | none | `Read()` | `new IdleCompaction(true, 200_000)`; the file still does not exist |
| `Read_is_off_only_for_the_boolean_false` (Theory) | `{"idleCompaction":X}` for `false` / `true` / `"false"` / `0` / `null` | `Read().Enabled` | `false` / `true` / `true` / `true` / `true` |
| `Read_takes_the_size_from_env` (Theory) | `{"env":{"CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS":X}}` for `"150000"` / `150000` / `"250000"` | `Read().MinTokens` | `150000` / `150000` / `250000` |
| `Read_treats_an_odd_size_as_Claude_Code_does` (Theory) | same key with `"50000"` / `"0"` / `"abc"` / `""` / `"1.5"` / `true` / `"99999999999"` | `Read().MinTokens` | `100000` / `100000` / `200000` / `200000` / `200000` / `200000` / `int.MaxValue` |
| `Read_with_env_that_is_no_object_is_the_default_size` | `{"env":"x"}` | `Read()` | `(true, 200_000)` |
| `Read_of_a_file_that_is_no_json_throws` | `not json` | `Read()` | `Should.Throw<ClaudeSettingsException>()`, message contains `settings.json` |
| `Read_with_a_repeated_key_in_env_throws` | `{"env":{"A":"1","A":"2"}}` | `Read()` | `Should.Throw<ClaudeSettingsException>()` |
| `Write_off_sets_false_and_leaves_the_rest` | `{"model":"x","hooks":{"Stop":[]}}` | `Write(new(false, 200_000))` | returns `true`; `idleCompaction` is boolean `false`; `model` is `"x"`; `hooks.Stop` is an empty array; no `env`; exactly one `settings.json.csx-backup-*` file exists |
| `Write_on_removes_the_key` | `{"idleCompaction":false,"model":"x"}` | `Write(new(true, 200_000))` | `true`; no `idleCompaction` key; `model` kept |
| `Write_of_a_size_sets_a_string_in_env_and_keeps_its_other_entries` | `{"env":{"FOO":"bar"}}` | `Write(new(true, 100_000))` | entry is the JSON **string** `"100000"`; `FOO` is `"bar"` |
| `Write_of_a_size_creates_env` | `{}` | `Write(new(true, 150_000))` | `env.CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS` is `"150000"` |
| `Write_of_200000_removes_the_entry_and_the_env_it_emptied` | `{"env":{"CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS":"100000"}}` | `Write(new(true, 200_000))` | no `env` key |
| `Write_of_200000_keeps_an_env_with_other_entries` | `{"env":{"FOO":"bar","CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS":"100000"}}` | same | `env` has only `FOO` |
| `Write_leaves_a_value_alone_that_did_not_change` | `{"idleCompaction":true,"env":{"CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS":"50000"}}` | `Write(new(false, 100_000))` | `idleCompaction` is `false`; the entry is still `"50000"` |
| `Write_of_what_the_file_says_already_writes_nothing` | `{"idleCompaction":false}` | `Write(new(false, 200_000))` | `false`; the file's text and last write time are unchanged; no backup file |
| `Write_of_the_defaults_without_a_file_creates_none` | none | `Write(new(true, 200_000))` | `false`; the file does not exist |
| `Write_off_without_a_file_creates_it` | none | `Write(new(false, 200_000))` | `true`; `Read()` gives `(false, 200_000)` |
| `Write_of_a_size_into_env_that_is_no_object_throws_and_leaves_the_file` | `{"env":"x"}` | `Write(new(true, 100_000))` | `Should.Throw<ClaudeSettingsException>()`; file text unchanged |
| `Write_below_the_floor_throws` | none | `Write(new(true, 99_999))` | `Should.Throw<ArgumentOutOfRangeException>()` |
| `Write_then_Read_gives_what_was_written` | `{}` | `Write(new(false, 300_000))` | `Read()` is `(false, 300_000)` |

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project tests/CodeSwitchX.Ingest.Tests --filter-class "*IdleCompactionSettingTests"`
Expected: a build error (the type does not exist).

- [ ] **Step 3: Implement `IdleCompactionSetting` and register it**

- [ ] **Step 4: Run the tests**

Run: the command of Step 2, then `dotnet test --project tests/CodeSwitchX.Ingest.Tests`.
Expected: all pass.

- [ ] **Step 5: Build and commit**

Run: `dotnet build CodeSwitchX.slnx` — expected: 0 warnings, 0 errors.

```bash
git add src/CodeSwitchX.Ingest tests/CodeSwitchX.Ingest.Tests
git commit -m "feat(ingest): read and write Claude Code's idle compaction setting (#450)"
git push
```

---

### Task 3: The view model

**Files:**
- Modify: `src/CodeSwitchX.UI/Settings/SettingsViewModel.cs`
- Modify: `tests/CodeSwitchX.UI.Tests/Settings/SettingsViewModelTests.cs` (only: `public partial class`)
- Test: `tests/CodeSwitchX.UI.Tests/Settings/SettingsViewModelTests.IdleCompaction.cs`

**Interfaces:**
- Consumes: `IdleCompactionSetting`, `IdleCompaction`, `ClaudeSettingsException` (Task 2).
- Produces, on `SettingsViewModel` (follow the Yard properties around `YardMaxChats` for style and doc comments, each naming #450):

```csharp
// Constructor: one more optional parameter at the very end, as GitHubPageViewModel? gitHub is:
//   IdleCompactionSetting? idleCompaction = null
// kept as _idleCompactionSetting = idleCompaction ?? new IdleCompactionSetting(claude, NullLogger<IdleCompactionSetting>.Instance);

public static readonly IReadOnlyList<int> IdleCompactionSizes = [100_000, 150_000, 200_000, 300_000, 400_000];

/// "200,000 tokens": invariant culture, whatever the Windows locale.
public static string TokensText(int tokens);

[ObservableProperty] [NotifyPropertyChangedFor(nameof(IdleCompactionSizeEnabled))] private bool _idleCompaction = true;
[ObservableProperty] private int _idleCompactionMinTokens = IdleCompactionSetting.DefaultMinTokens;
[ObservableProperty] private IReadOnlyList<SettingChoice> _idleCompactionChoices;   // the five sizes; plus the file's own value, in order, when it is none of them
[ObservableProperty] [NotifyPropertyChangedFor(nameof(IdleCompactionReadable))] [NotifyPropertyChangedFor(nameof(IdleCompactionSizeEnabled))] private string? _idleCompactionProblem;

public bool IdleCompactionReadable => IdleCompactionProblem is null;
public bool IdleCompactionSizeEnabled => IdleCompactionReadable && IdleCompaction;

/// Done once every change so far is written, or failed. For tests.
internal Task IdleCompactionSaved { get; }
```

**Behaviour, decided:**
- `Refresh()` calls a new private `ShowIdleCompaction()` after the hook status. It calls `Read()` on the calling thread, as `GetStatus` is called there. Success: sets `IdleCompactionChoices` first, then `IdleCompaction` and `IdleCompactionMinTokens`, and clears `IdleCompactionProblem`. `ClaudeSettingsException`: sets `IdleCompactionProblem` to its message and leaves the other three as they are. Both under a private `bool _showingIdleCompaction` flag.
- `OnIdleCompactionChanged` and `OnIdleCompactionMinTokensChanged` return when `_showingIdleCompaction` or `_loading` is set; otherwise they queue a save of `new IdleCompaction(IdleCompaction, IdleCompactionMinTokens)`, captured at that moment.
- Saves run one after another: each awaits the one before, then `await Task.Run(() => _idleCompactionSetting.Write(value))`. `IdleCompactionSaved` is the last of them and never faults.
- Written: `LastMessage = $"Idle compaction saved to {SettingsFile}."`. Nothing to write: `LastMessage` untouched.
- `ClaudeSettingsException`: `LastMessage = ex.Message`, then `ShowIdleCompaction()` so the controls show the file again. No other exception type is caught there.
- The save's continuation sets view model properties, so no `ConfigureAwait(false)` after the `Task.Run`.
- A `ComboBox` sets `SelectedValue` to `0` or null when its items change under it: `OnIdleCompactionMinTokensChanged` ignores a value below `IdleCompactionSetting.LowestMinTokens`.

- [ ] **Step 1: Write the failing tests**

In the new partial file. `IdleFile(json)` writes `_claude.SettingsFile` (creating `_claude.ClaudeDirectory`); `IdleJson()` parses it.

```csharp
[Fact]
public async Task Idle_compaction_without_a_settings_file_shows_on_from_200000_tokens_and_creates_none()
{
    await _vm.LoadAsync(CancellationToken.None);

    _vm.IdleCompaction.ShouldBeTrue();
    _vm.IdleCompactionMinTokens.ShouldBe(200_000);
    _vm.IdleCompactionChoices.Select(c => c.Value).ShouldBe([100_000, 150_000, 200_000, 300_000, 400_000]);
    _vm.IdleCompactionReadable.ShouldBeTrue();
    File.Exists(_claude.SettingsFile).ShouldBeFalse();
}
```

The rest, each ending in `await _vm.IdleCompactionSaved;` after a change:

| Test | Arrange | Act | Assert |
|---|---|---|---|
| `Idle_compaction_shows_what_the_file_says` | file `{"idleCompaction":false,"env":{"CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS":"100000"}}` | `LoadAsync` | `IdleCompaction` false; `IdleCompactionMinTokens` 100000; `IdleCompactionSizeEnabled` false; the file's text is unchanged (showing writes nothing) |
| `Switching_idle_compaction_off_writes_false_and_says_so` | file `{"model":"x"}` | `LoadAsync`; `IdleCompaction = false` | file: `idleCompaction` false, `model` `"x"`; `LastMessage` contains `Idle compaction saved` |
| `Switching_it_on_again_removes_the_key` | file `{"idleCompaction":false}` | `LoadAsync`; `IdleCompaction = true` | no `idleCompaction` key |
| `Picking_a_size_writes_it_into_env` | no file | `LoadAsync`; `IdleCompactionMinTokens = 100_000` | `env.CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS` is `"100000"` |
| `A_size_in_the_file_that_is_no_choice_is_shown_as_one_more` | file with `"250000"` | `LoadAsync` | choices' values are `[100000,150000,200000,250000,300000,400000]`; the 250000 label is `250,000 tokens`; `IdleCompactionMinTokens` 250000 |
| `Refresh_shows_a_change_made_in_the_file_meanwhile` | no file; `LoadAsync` | write file `{"idleCompaction":false}`; `Refresh()`; `await IdleCompactionSaved` | `IdleCompaction` false; the file's text is still exactly what was written |
| `A_settings_file_that_cannot_be_read_disables_the_card` | file `not json` | `LoadAsync` | `IdleCompactionProblem` not null; `IdleCompactionReadable` false; `IdleCompactionSizeEnabled` false |
| `A_write_that_fails_says_why_and_shows_the_file_again` | file `{"model":"x"}`, `LoadAsync`, then `File.SetAttributes(file, FileAttributes.ReadOnly)` | `IdleCompaction = false` | `LastMessage` contains `Cannot write`; `IdleCompaction` is true again; (reset the attribute to `Normal` in a `finally`, or `Dispose` cannot delete the folder) |
| `Two_quick_changes_are_both_written` | no file; `LoadAsync` | `IdleCompaction = false; IdleCompactionMinTokens = 150_000;` | file: `idleCompaction` false and the entry `"150000"` |
| `A_size_below_the_floor_from_the_view_is_ignored` | no file; `LoadAsync` | `IdleCompactionMinTokens = 0` | no file exists |
| `Tokens_are_written_with_commas_in_any_locale` | `CultureInfo.CurrentCulture = new("de-DE")` (restore in `finally`) | `SettingsViewModel.TokensText(200_000)` | `"200,000 tokens"` |

If the read-only test does not fail the write on this machine (the atomic replace may get through), stop and report rather than weakening the test.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project tests/CodeSwitchX.UI.Tests --filter-class "*SettingsViewModelTests" --filter-method "*dle_compaction*"`
Expected: a build error (the members do not exist).

- [ ] **Step 3: Implement the members in `SettingsViewModel`**

- [ ] **Step 4: Run the tests**

Run: `dotnet test --project tests/CodeSwitchX.UI.Tests --filter-class "*SettingsViewModelTests"` and `--filter-class "*AppHostTests"` (the host must still resolve `SettingsViewModel`).
Expected: all pass.

- [ ] **Step 5: Build and commit**

Run: `dotnet build CodeSwitchX.slnx` — expected: 0 warnings, 0 errors.

```bash
git add src/CodeSwitchX.UI tests/CodeSwitchX.UI.Tests
git commit -m "feat(settings): idle compaction in the Settings view model (#450)"
git push
```

---

### Task 4: The card

**Files:**
- Modify: `src/CodeSwitchX.UI/Settings/SettingsView.xaml` (the `<!-- Yard -->` block: a second `Border Style="{StaticResource Card}"` after the "Chats on a tile" card, inside the same `StackPanel`)

**Interfaces:**
- Consumes: the Task 3 properties.

Built from what the page has already: the row pattern of "Keep a closed chat on its tile" (control docked right, label and `Help` text left), the `Switch`-styled `CheckBox` as in "Speak chat news", the amber problem line as the hotkey rows have (`Foreground="#F59E0B"`, `NullToCollapsed`). No new style, converter or resource.

Exact copy:

| Element | Text |
|---|---|
| `H3` | `Compaction while idle` |
| `Help` under it | `Claude Code's own doing, set here: these two are saved in its settings file, not in CodeSwitchX.` |
| Problem line | `{Binding IdleCompactionProblem}`, collapsed when null |
| Row 1 label | `Compact a chat before its cache runs out` |
| Row 1 help | `After 54 idle minutes Claude Code writes a summary while the chat's cache is still warm. Back after the hour, the chat carries on from the summary. Back sooner, it keeps its whole conversation.` |
| Row 2 label | `For chats from` |
| Row 2 help | `Smaller chats are left alone. Claude Code goes no lower than 100,000. A change applies to chats opened after it.` |

Bindings: the switch `IsChecked="{Binding IdleCompaction}"`, `IsEnabled="{Binding IdleCompactionReadable}"`, `AutomationProperties.Name="Compact a chat before its cache runs out"`. The `ComboBox` (`Width="170"`): `ItemsSource="{Binding IdleCompactionChoices}" DisplayMemberPath="Label" SelectedValuePath="Value" SelectedValue="{Binding IdleCompactionMinTokens}"`, `IsEnabled="{Binding IdleCompactionSizeEnabled}"`, `AutomationProperties.Name="Idle compaction for chats from"`.

- [ ] **Step 1: Add the card**

- [ ] **Step 2: Build (XAML errors show only here)**

Run: `dotnet build CodeSwitchX.slnx`
Expected: 0 warnings, 0 errors. Check every binding path against `SettingsViewModel` by hand: WPF does not.

- [ ] **Step 3: Run everything**

Run: `dotnet test --solution CodeSwitchX.slnx`
Expected: all pass, except at most the three `HotkeyServiceTests` while another CodeSwitchX runs. Report the totals and any failure by name.

- [ ] **Step 4: Commit**

```bash
git add src/CodeSwitchX.UI/Settings/SettingsView.xaml
git commit -m "feat(settings): the idle compaction card on the Yard page (#450)"
git push
```

---

### Task 5: On screen (a separate handoff, after the code review)

Not part of the build handoff. The app is started from this branch with `CLAUDE_CONFIG_DIR` pointing at a scratch folder, so the user's real `settings.json` is never written, on the BenQ monitor only. Checked there: the card shows on Settings > Yard; the switch off writes `"idleCompaction": false` into the scratch file and on removes it; a size writes the `env` entry; the size row greys out with the switch off; a file made unreadable shows the amber line.

## Out of scope

- Compacting anything from CodeSwitchX; changes to "Compact now" or "Keep warm".
- Project-level, local or managed Claude Code settings.
- Raven's settings list (`AppSettings`), `SettingKeys`, migrations.
- A new pull request per task: all of this is one branch and one PR for #450.
