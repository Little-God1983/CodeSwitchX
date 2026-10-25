# Your own sounds (#307) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Sounds page's "Your sounds" section: add a WAV or MP3 of your own, which is copied into CodeSwitchX's data folder and joins the pickers of all six slots; each added sound shows its length with play and remove; a file over 3 s is accepted with a notice and cut at 3 s with a short fade-out.

**Architecture:** `SoundImport` (Voice) decodes a WAV or MP3, mixes it to mono, resamples it to 44.1 kHz, cuts it at 3 s with a 250 ms fade-out and writes it as a 16-bit PCM WAV: every sound then plays through the same path as the shipped ones (16-bit, even byte counts, so `Loudness` needs no change). `SoundLibrary` (Voice) is the pool: the shipped sounds plus the user's, kept as `sounds\<id>.wav` with an `index.json` in the data folder. `ChatSounds` and `SoundPicks` read from it; a pick whose file has gone missing plays the slot's default. The Sounds page lists the user's sounds and adds them through an `ISoundFilePicker`.

**Tech Stack:** .NET 10 / WPF, NAudio (`WaveFileReader`, `MediaFoundationReader`, `StereoToMonoSampleProvider`, `WdlResamplingSampleProvider`), xUnit + Shouldly.

**Spec:** `docs/superpowers/specs/2026-10-24-raven-sounds-design.md` ("Sounds shipped and added", "Settings layout" item 3); issue #307 and its two comments.

## Global Constraints

- WAV or MP3 only; the picker filters `*.wav;*.mp3`.
- The file is copied (as a converted WAV) into the data folder: moving or deleting the original breaks nothing.
- Added sounds join the pool for all six slots, after the shipped ones, by the file's name without extension; a name already in the pool gets " (2)", " (3)", ….
- Each added sound shows its length ("1.2 s"), a play and a remove button.
- Over 3 s: accepted with the notice exactly "This sound is 12 s long. Only the first 3 s will be used." (whole seconds, rounded); cut at 3 s with a short fade-out (250 ms).
- A file that cannot be read is refused with a message: "This file can't be used as a sound: <reason>". Nothing is added.
- Removing a sound in use sets the slots that used it back to their default. A sound whose file has gone missing plays the slot's default.
- User sound ids: `user-` + 8 hex characters; they never collide with shipped ids.
- Match the surrounding code: comment density, `<summary>` prose, naming. Run tests through the test exes (`--filter-class "*Name"`).

## Review Focus

1. A stereo, float or 24-bit WAV at 48 kHz: imported as 16-bit mono 44.1 kHz, as loud as it was. Test in Task 1.
2. A file renamed to `.wav` that is not one (or empty, or zero samples): refused, nothing written. Test in Task 1.
3. A user sound whose file was deleted behind the app's back: the slot plays its default, the list leaves it out. Test in Task 2.
4. Two files with the same name: both added, the second as "Name (2)". Test in Task 2.
5. Removing the sound three slots use: those three go back to their defaults, the others keep theirs, and it is stored. Test in Task 3.

---

### Task 1: Importing a file

**Files:**
- Create: `src/CodeSwitchX.Voice/Audio/SoundImport.cs`, `tests/CodeSwitchX.Voice.Tests/Audio/SoundImportTests.cs`, `tests/CodeSwitchX.Voice.Tests/Audio/Fixtures/tone.mp3` (made once by the test project's helper or `scripts/sounds`, CC0)
- Modify: `tests/CodeSwitchX.Voice.Tests/CodeSwitchX.Voice.Tests.csproj` (copy the fixture to the output)

**Interfaces:**
- Produces:
  - `public static class SoundImport` with `public static readonly TimeSpan MaximumLength = TimeSpan.FromSeconds(3)` and `FadeOut = TimeSpan.FromMilliseconds(250)`.
  - `public static TimeSpan Import(string source, string target)`: writes the converted WAV to `target` and returns the source's full length; throws `SoundImportException` (a `public sealed class : Exception`) with a short reason for a file NAudio cannot read, one with no samples, or an extension other than `.wav`/`.mp3`; writes nothing then.
  - `public static string? NoticeFor(TimeSpan length)`: the over-3-s notice (rounded whole seconds), null up to 3 s.

- [ ] **Step 1: Write the failing tests**: `A_stereo_float_wav_at_48_khz_becomes_16_bit_mono_at_44_1_khz` (peak within 2 % of the source's); `A_24_bit_wav_is_read`; `An_mp3_is_read`; `A_file_over_three_seconds_is_cut_with_a_fade` (12 s source → 3 s stored, returns 12 s, last 5 ms near silent, 2.5 s still loud); `A_file_that_is_no_sound_is_refused_and_nothing_is_written` (text renamed .wav; an empty file; a .ogg); `The_notice_says_the_length_in_whole_seconds` (12.4 s → "This sound is 12 s long. Only the first 3 s will be used."; 3 s → null).
- [ ] **Step 2: Run them to see them fail.**
- [ ] **Step 3: Implement** (WAV through `WaveFileReader`, MP3 through `MediaFoundationReader`; `ToSampleProvider`, mono by averaging, `WdlResamplingSampleProvider` to 44.1 kHz, take/fade, write with `WaveFileWriter.CreateWaveFile16` to a temp file then move).
- [ ] **Step 4: Run them** — PASS. Commit `feat: a WAV or MP3 is imported as a sound, cut at 3 s with a fade (#307)`.

### Task 2: The library

**Files:**
- Create: `src/CodeSwitchX.Voice/Audio/SoundLibrary.cs`, `tests/CodeSwitchX.Voice.Tests/Audio/SoundLibraryTests.cs`
- Modify: `src/CodeSwitchX.Voice/Audio/ChatSounds.cs` (`ChatSounds` and `SoundPicks` take the library), `src/CodeSwitchX.Voice/VoiceServiceCollectionExtensions.cs`, `src/CodeSwitchX.Core/AppPaths.cs` (`SoundsDirectory`), the UI composition root, existing tests constructing `ChatSounds`/`SoundPicks`

**Interfaces:**
- Produces:
  - `public sealed record SoundEntry(string Id, string Name, TimeSpan Length, bool Shipped)`.
  - `public sealed class SoundLibrary(string directory)`: `IReadOnlyList<SoundEntry> All` (shipped, then the user's whose files exist), `bool Has(string id)`, `internal (byte[] Pcm, WaveFormat Format) Load(string id)`, `(SoundEntry Sound, string? Notice) Add(string source)` (imports to `<directory>\<id>.wav`, appends to `index.json`), `void Remove(string id)` (deletes the file and the index entry), `event EventHandler? Changed`. Thread-safe (a lock); the index is `[{ "id": …, "name": … }]`.
  - `ChatSounds(IAudioOutput, AudioLevels, TimeProvider, ILogger<ChatSounds>, SoundLibrary)`; `SoundPicks(SoundLibrary)`: `For(slot)` gives the slot's default when the pick is no longer in the library.

- [ ] **Step 1: Write the failing tests**: `Added_sounds_follow_the_shipped_ones_and_survive_a_new_library` (a second `SoundLibrary` on the same folder lists them); `A_second_file_of_the_same_name_is_named_with_2`; `Removing_a_sound_deletes_its_file`; `A_sound_whose_file_went_missing_is_left_out_and_its_slot_plays_the_default` (SoundPicks); `An_unreadable_file_adds_nothing`; `A_broken_index_starts_empty` (no throw); `ChatSounds_plays_a_user_sound` (AudioOutputTests fake devices).
- [ ] **Step 2: Run them to see them fail.**
- [ ] **Step 3: Implement**; register `SoundLibrary` with `AppPaths.SoundsDirectory`.
- [ ] **Step 4: Run Voice and UI tests** — PASS. Commit `feat: your sounds are kept in the data folder and join the pool (#307)`.

### Task 3: The "Your sounds" section

**Files:**
- Modify: `src/CodeSwitchX.UI/Settings/SettingsViewModel.cs`, `SettingsView.xaml` (Sounds page, third card), `AppSettings.cs` (sound names from the library), `SettingsPage.cs` (keywords "add sound", "own sound", "my sounds")
- Create: `src/CodeSwitchX.UI/Settings/SoundFilePicker.cs` (`ISoundFilePicker` + the `OpenFileDialog` one)
- Test: `tests/CodeSwitchX.UI.Tests/Settings/SettingsViewModelTests.cs`, `AppSettingsTests.cs`

**Interfaces:**
- Consumes: `SoundLibrary`.
- Produces in `SettingsViewModel`:
  - `SoundChoices` becomes an `ObservableCollection<SoundChoice>` rebuilt on `SoundLibrary.Changed` (None, then `library.All`).
  - `ObservableCollection<UserSoundRow> UserSounds` (`record UserSoundRow(string Id, string Name, string Length)`, length "1.2 s").
  - `[RelayCommand] void AddSound()`: asks `ISoundFilePicker.Pick()` (null: nothing); adds; `SoundNotice` (string?) shows the notice or the refusal.
  - `[RelayCommand] void PlayUserSound(string id)`, `[RelayCommand] void RemoveSound(string id)`: removing sets every slot that used it back to its default (stored).
  - `LoadSoundAsync` accepts any id the library has.
- XAML: card "Your sounds": help "WAV or MP3. Files over 3 s are cut at 3 s."; the list (name, length, play, remove with `AutomationProperties.Name` "Play <name>", "Remove <name>"); "Add sound…" button; the notice line.

- [ ] **Step 1: Write the failing tests**: `Adding_a_sound_lists_it_and_offers_it_to_every_slot`; `A_long_sound_is_added_with_the_notice`; `An_unreadable_file_shows_why_and_adds_nothing`; `Cancelling_the_picker_adds_nothing`; `Removing_a_sound_in_use_sets_its_slots_back_to_their_defaults` (Review Focus 5); `A_stored_pick_of_a_user_sound_loads`; AppSettings `Raven_sets_a_slot_to_a_sound_of_the_user_s_by_name`.
- [ ] **Step 2: Run them to see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run all UI tests** — PASS. Commit `feat: add, play and remove your own sounds on the Sounds page (#307)`.

### Task 4: On screen

- [ ] With the user's go: branch build on the BenQ; add a short WAV and a long MP3 through the dialog (driven by UIA), see the notice, pick one for a slot, play it (audio probe), remove it and see the slot go back; restore the stable build; screenshots `docs/review/screenshots/issue307-*.png`; PR.
