# Sounds page with volume sliders (#305) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new Settings page "Sounds" under Raven whose Volume section has two sliders, "Sounds" and "Raven's voice", that scale today's chat chime and everything spoken in Raven's voice, live, without touching Windows' volume.

**Architecture:** One `AudioLevels` singleton in `CodeSwitchX.Voice` holds the two gains; a `Loudness` wave provider scales 16-bit PCM by a gain it reads on every `Read`, so a change is heard at once, also in what is already playing. The chime, the speech player and the voice samples wrap their sources in it. `SettingsViewModel` stores the two percentages, pushes them into `AudioLevels`, and plays a preview on release.

**Tech Stack:** .NET / WPF, CommunityToolkit.Mvvm, NAudio, xUnit + Shouldly.

**Spec:** `docs/superpowers/specs/2026-10-24-raven-sounds-design.md` (sections "Volume" and "Settings layout"; issue #305).

## Global Constraints

- Sounds: 0–100 %, default 60. Raven's voice: 0–100 %, default 100 (= today's loudness).
- The sliders scale what CodeSwitchX plays; they never change Windows' or the device's volume (no `IWavePlayer.Volume`, no endpoint volume).
- Changes apply at once, also to sound already playing.
- At 0 nothing is heard, everything else behaves as if it played (the chime still counts as a sound for the traffic watcher; speech still takes its time).
- The page is "Sounds", group Raven, right after Voice in the sidebar; this issue builds only its "Volume" section.
- Voice slider covers: answers, news, catch-ups, questions read out, the Voice page's samples. Sounds slider covers the chime.
- Keys: `raven.soundVolume`, `raven.voiceVolume`.
- Match the surrounding code: comment density, `<summary>` style, naming.

## Review Focus

1. A stored volume out of range or unreadable (e.g. 250, -5, a string): loads as the default, never throws. Test in Task 2.
2. A slider moved while Raven is speaking: the reply gets quieter mid-sentence, not from the next reply on. Test in Task 1 (`Loudness` reads the gain each `Read`).
3. A preview while one already plays (dragging back and forth): one at a time, the voice preview stops the earlier one. Test in Task 2.
4. Text-only Raven (no engine picked): releasing the voice slider plays nothing and does not throw. Test in Task 2.
5. The orb still follows speech at a low voice volume: `Meter` measures before `Loudness`. Test in Task 1.

---

### Task 1: Gains in the audio path

**Files:**
- Create: `src/CodeSwitchX.Voice/Audio/AudioLevels.cs` (holds `AudioLevels` and `Loudness`)
- Modify: `src/CodeSwitchX.Voice/Audio/ChatChime.cs`, `src/CodeSwitchX.Voice/Audio/SpeechPlayer.cs` (`OpenFor`, around line 196), `src/CodeSwitchX.Voice/Speech/VoiceSamples.cs`, `src/CodeSwitchX.Voice/VoiceServiceCollectionExtensions.cs`
- Test: `tests/CodeSwitchX.Voice.Tests/Audio/AudioLevelsTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class AudioLevels` with `public float Sounds { get; set; }` (default `GainOf(60)`) and `public float Voice { get; set; }` (default `1f`), any thread (`Volatile`).
  - `public static float AudioLevels.GainOf(int percent)` = `(percent / 100f)²`, `percent` clamped to 0..100 (perceived loudness, so the low end of the slider is not all near-silence).
  - `internal sealed class Loudness(IWaveProvider source, Func<float> gain) : IWaveProvider`, 16-bit PCM only; reads `gain()` once per `Read` and scales every sample, rounding and clamping to `short`.
  - Registered: `services.AddSingleton<AudioLevels>()`.
  - Constructors gain a last parameter: `ChatChime(IAudioOutput, ILogger<ChatChime>, AudioLevels)`, `WaveOutSpeechPlayer(IAudioOutput, ILogger<WaveOutSpeechPlayer>, AudioLevels)` (the internal one too, before `offThread`), `VoiceSamples(IAudioOutput, AudioLevels)`.

- [ ] **Step 1: Write the failing tests** in `AudioLevelsTests`:
  - `Gain_of_a_percentage_follows_perceived_loudness`: `GainOf(100) == 1f`, `GainOf(0) == 0f`, `GainOf(50) == 0.25f`, `GainOf(150) == 1f`, `GainOf(-5) == 0f`.
  - `Defaults_are_sounds_at_60_and_the_voice_at_full`: `new AudioLevels()` gives `Sounds == GainOf(60)`, `Voice == 1f`.
  - `Loudness_scales_each_sample_by_the_gain`: source samples `[1000, -1000, 32767, -32768]`, gain 0.5 → `[500, -500, 16384, -16384]`.
  - `Loudness_at_full_gain_passes_the_samples_unchanged` and `Loudness_at_zero_is_silence_of_the_same_length` (read count unchanged, all zero).
  - `A_gain_changed_between_reads_is_heard_from_the_next_read`: two reads of a long source, gain 1 then 0.5; first chunk unscaled, second halved.
  - `The_chime_at_the_default_level_is_as_loud_as_before`: the chime's peak sample after `Loudness` at `GainOf(60)` is within 5 % of `0.33 * short.MaxValue` (today's peak). Expose the PCM for the test as `internal static ReadOnlyMemory<byte> ChatChime.Pcm`.

- [ ] **Step 2: Run them to see them fail**
  Run: `dotnet test --solution CodeSwitchX.slnx --filter "FullyQualifiedName~AudioLevelsTests"`
  Expected: build fails, `AudioLevels` not defined.

- [ ] **Step 3: Implement `AudioLevels`, `Loudness`, and wire them in**
  - `ChatChime`: plays `new Loudness(raw, () => _levels.Sounds)`; raise the generated amplitude from `0.33` to `0.33 / GainOf(60)` (≈ 0.92) so the default sounds as today; fix its `<summary>` ("at a third of full loudness" → at the sounds level).
  - `WaveOutSpeechPlayer.OpenFor`: `_audioOutput.Open(new Loudness(new Meter(buffer, this), () => _levels.Voice), 120, out playsOn)`: the meter measures before the gain, so the orb is unchanged by the slider.
  - `VoiceSamples.PlayAsync`: `audioOutput.Open(new Loudness(reader, () => levels.Voice), 300)`. The sample WAVs are 16-bit PCM; if one is not, `Loudness` throws on construction with a clear message and `VoiceSamplesTests` (which walks every clip) catches it.
  - Update the callers that construct these in tests (`AudioOutputTests` and any `new WaveOutSpeechPlayer(`/`new ChatChime(`) with `new AudioLevels()`.

- [ ] **Step 4: Run the Voice tests**
  Run: `dotnet test --solution CodeSwitchX.slnx --filter "FullyQualifiedName~CodeSwitchX.Voice.Tests"`
  Expected: all pass.

- [ ] **Step 5: Commit** `feat: the chime and Raven's voice play at a level of their own (#305)`

### Task 2: The two volume settings

**Files:**
- Modify: `src/CodeSwitchX.UI/Settings/SettingKeys.cs`, `src/CodeSwitchX.UI/Settings/SettingsViewModel.cs`, `src/CodeSwitchX.UI/Settings/VoicePageViewModel.cs`, `src/CodeSwitchX.UI/Settings/AppSettings.cs`
- Test: `tests/CodeSwitchX.UI.Tests/Settings/SettingsViewModelTests.cs`, `tests/CodeSwitchX.UI.Tests/Settings/VoicePageViewModelTests.cs`, `tests/CodeSwitchX.UI.Tests/Settings/AppSettingsTests.cs`, `tests/CodeSwitchX.UI.Tests/ShellTestHarness.cs`

**Interfaces:**
- Consumes: `AudioLevels`, `AudioLevels.GainOf` (Task 1); `IChatChime.Play()`.
- Produces:
  - `SettingKeys.RavenSoundVolume = "raven.soundVolume"`, `SettingKeys.RavenVoiceVolume = "raven.voiceVolume"`.
  - `SettingsViewModel`: `[ObservableProperty] int _ravenSoundVolume = 60;`, `[ObservableProperty] int _ravenVoiceVolume = 100;`; `public const int DefaultSoundVolume = 60, DefaultVoiceVolume = 100;`
  - `SettingsViewModel` constructor: two optional last parameters `IChatChime? chime = null, AudioLevels? levels = null` (`?? new AudioLevels()`), after `shortcuts`.
  - `IRelayCommand SettingsViewModel.PlaySoundLevelCommand` (plays the chime once) and `VoicePageViewModel.PlayVoiceLevel()` (plays the picked voice's sample through the existing one-at-a-time sample path; nothing when no engine or no voice is picked). `SettingsViewModel` exposes `IRelayCommand PlayVoiceLevelCommand` that calls it.
  - By voice (`AppSettings`, Sounds page): `"sound volume"` (aliases "sounds volume", "volume of the sounds", "chime volume") and `"voice volume"` (aliases "Raven's voice volume", "how loud Raven speaks"), values `"0 %"…"100 %"` in steps of 10 offered, any whole number 0..100 accepted ("40", "40 %", "40 percent").

- [ ] **Step 1: Write the failing tests**
  - `SettingsViewModelTests.Volumes_default_to_60_and_100_and_reach_the_audio_levels`: a fresh store; after load, `(RavenSoundVolume, RavenVoiceVolume) == (60, 100)`, `levels.Sounds == GainOf(60)`, `levels.Voice == 1f`.
  - `A_volume_set_is_stored_and_heard_at_once`: set `RavenVoiceVolume = 50` → store holds 50 under `raven.voiceVolume`, `levels.Voice == 0.25f`; same for sounds.
  - `[Theory] A_stored_volume_out_of_range_loads_as_the_default` with 250, -5 and a stored string: loads 60 / 100, no throw.
  - `Playing_the_sound_level_plays_the_chime_once`: fake `IChatChime` counts one play.
  - `VoicePageViewModelTests.Playing_the_voice_level_plays_the_picked_voice` (FakeVoiceSamples records `(engine, voice)` of the picked voice) and `Playing_the_voice_level_without_an_engine_plays_nothing`, and `Playing_the_voice_level_twice_stops_the_first` (first token cancelled).
  - `AppSettingsTests.[Theory] The_voice_volume_is_set_by_voice` with "40", "40 %", "40 percent" → `RavenVoiceVolume == 40`; `A_volume_over_100_is_refused_and_nothing_changes`.

- [ ] **Step 2: Run them to see them fail**
  Run: `dotnet test --solution CodeSwitchX.slnx --filter "FullyQualifiedName~CodeSwitchX.UI.Tests.Settings"`
  Expected: build fails on the missing properties.

- [ ] **Step 3: Implement**
  - Load in the existing load block with `LoadOrDefaultAsync<int?>` (as `RavenPauseSeconds`), out of 0..100 → default. `OnRaven…VolumeChanged` sets the `AudioLevels` gain and `Persist`s. Set the levels from the defaults in the constructor too, so the levels are right before load finishes.
  - `AppSettings`: a `Percent(...)` entry helper beside `Seconds(...)`, using the existing `Number(...)` parser after stripping "%"/"percent"; the refusal wording follows `Seconds`' ("Voice volume is a whole number from 0 to 100 %, not '…'. Nothing was changed.").
  - Pass the real `IChatChime`/`AudioLevels` from DI (App registers `SettingsViewModel` as a singleton; both are singletons in Voice). `ShellTestHarness` passes a fake chime and a fresh `AudioLevels`, exposed for tests.

- [ ] **Step 4: Run the UI tests**
  Run: `dotnet test --solution CodeSwitchX.slnx --filter "FullyQualifiedName~CodeSwitchX.UI.Tests"`
  Expected: all pass.

- [ ] **Step 5: Commit** `feat: the sounds' and Raven's voice volume are settings, set by voice too (#305)`

### Task 3: The Sounds page

**Files:**
- Modify: `src/CodeSwitchX.UI/Settings/SettingsPage.cs`, `src/CodeSwitchX.UI/Settings/SettingsView.xaml`, `src/CodeSwitchX.UI/Settings/SettingsView.xaml.cs`
- Test: `tests/CodeSwitchX.UI.Tests/Settings/SettingsViewModelTests.cs`, `tests/CodeSwitchX.UI.Tests/Settings/AppSettingsTests.cs`

**Interfaces:**
- Consumes: `RavenSoundVolume`, `RavenVoiceVolume`, `PlaySoundLevelCommand`, `PlayVoiceLevelCommand` (Task 2).
- Produces: `SettingsPage.Sounds`, placed right after `Voice` in the enum.

- [ ] **Step 1: Write the failing tests**
  - `[Theory] A_page_is_also_found_by_a_setting_on_it` gets `("volume", SettingsPage.Sounds)`, `("how loud", SettingsPage.Sounds)`, `("chime", SettingsPage.Sounds)`.
  - `The_sidebar_lists_Sounds_right_after_Voice_under_Raven`: in `SettingsPageItem.All`, the item after Voice is Sounds, group "Raven".
  - `Open_the_voice_settings_opens_settings_at_that_page` gets `("sounds", SettingsPage.Sounds)`.

- [ ] **Step 2: Run them to see them fail**
  Run: `dotnet test --solution CodeSwitchX.slnx --filter "FullyQualifiedName~CodeSwitchX.UI.Tests.Settings"`
  Expected: FAIL, `SettingsPage.Sounds` not defined.

- [ ] **Step 3: Implement**
  - `SettingsPageItem`: `new(SettingsPage.Sounds, "Raven", "Sounds", "", "The short sounds chats make, and how loud they and Raven's voice are.", ["sound", "sounds", "volume", "loud", "quiet", "how loud", "louder", "softer", "chime", "beep", "voice volume"])`. Move "sound for other chats", "chime" out of Voice's keywords only if a test asks one page per word; otherwise leave them (the old switch still lives on Voice until #306).
  - XAML: a `StackPanel` for `ConverterParameter=Sounds` like the other pages, holding one `Card` with `H3` "Volume", a `Help` line ("How loud the chats' sounds and Raven's voice are, here in CodeSwitchX only: Windows' volume stays as it is."), and two rows: label ("Sounds", "Raven's voice"; `FontSize="14.5" FontWeight="SemiBold"`), a `Slider` 0–100, `IsSnapToTickEnabled`, `TickFrequency="5"`, `SmallChange="5"`, `LargeChange="10"`, `AutomationProperties.Name` as the label, and a percentage `TextBlock` (`{0} %`, `Mono`) docked right. Bind `Value` two-way to the int properties.
  - Preview on release: in `SettingsView.xaml.cs`, each slider's `Thumb.DragCompleted`, `PreviewMouseLeftButtonUp` (a click on the track) and `KeyUp` (arrows, PageUp/Down, Home/End) run its play command once. A drag fires only once, on its completion.

- [ ] **Step 4: Run all tests**
  Run: `dotnet test --solution CodeSwitchX.slnx`
  Expected: all pass.

- [ ] **Step 5: On-screen check (BenQ only)**
  Build the UI exe on this branch, start it on the BenQ (memory: running-the-app-from-claude; strip `ELECTRON_RUN_AS_NODE`; switch the user's Open mic off and restore it after). Open Settings → Sounds: the page sits right after Voice; both sliders show 60 % and 100 %; dragging the sounds slider and letting go plays the chime at that level; the voice slider plays the picked voice's sample; the search "volume" finds the page. Capture a screenshot for the PR.

- [ ] **Step 6: Commit** `feat: Settings has a Sounds page with the volume of the sounds and of Raven's voice (#305)`

### Task 4: PR

- [ ] Push, `gh auth switch --user Little-God1983`, open the PR "Raven sounds: a Sounds page with volume sliders for the sounds and Raven's voice (#305)" with `Closes #305`, the screenshot, and the attribution line. Then review rounds (memory: review-rounds).
