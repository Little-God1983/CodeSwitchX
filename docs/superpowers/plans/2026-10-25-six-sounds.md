# Six sounds through one queue (#306) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Six sound slots (starts working, finishes, asks a question, asks permission, fails, Raven hears its name), each picked from a shipped pool of musical sounds on the Sounds page, played for all chats through one queue that waits for the floor and drops what is outdated; the old chime and its three Voice-page settings are removed.

**Architecture:** The pool is 12 WAV files made by a script in the repo and embedded in `CodeSwitchX.Voice`; `ChatSounds` plays one at the sounds' level and returns when it has played out. In the UI, `ChatSoundCues` turns session changes and Raven's cards into `SoundCue`s, and `SoundQueue` plays them one after another while Raven's panel says the floor is free. A sound of the chat the user is in waits for its spoken line and plays right before it as the reply's prelude (`SpokenReply.Prelude`). "Raven hears its name" bypasses the queue.

**Tech Stack:** .NET 10 / WPF, CommunityToolkit.Mvvm, NAudio, xUnit + Shouldly + NSubstitute, `Microsoft.Extensions.Time.Testing`.

**Spec:** `docs/superpowers/specs/2026-10-24-raven-sounds-design.md` (sections "The six sounds", "The queue", "Order", "Sounds shipped and added" (shipped pool only), "Settings layout" item 2, "Old settings removed"; issue #306 and its comment).

## Global Constraints

- Slots and labels, in this order: "Starts working", "Finishes", "Asks a question", "Asks permission", "Fails", "Raven hears its name". Each can be "None".
- `ChatNewsKind.NeedsYou` (a chat waiting without a Raven card) uses "Asks a question".
- Sounds play for **all** chats, the one the user is in too. A muted chat (`RavenChat.IsMuted`) adds nothing; the panel mute (`IsMuted`) silences and clears every queued sound; "Speak chat news" off does **not** silence sounds.
- One queue, no cooldown, never two sounds at once, 300 ms gap between sounds.
- The queue holds while the floor is busy, then waits `TrafficWatcher.NewsGrace` (1.5 s) of free floor. Sounds never wait "Pause between messages"; a sound played restarts that pause for Raven's own speech (`Traffic.Announced()`), not for the next sound.
- Outdated cues are dropped before they play: a newer cue of the same chat replaces an older one; a cue whose `StillTrue()` is false; a cue older than `ChatNews.MaximumAge` (2 min, as stale news is not spoken).
- The chat the user is in: its sound waits for its spoken line and plays straight before it, no pause between; ahead of other chats' sounds. The catch-up and anything else Raven has to tell go before other chats' sounds.
- "Raven hears its name" plays at once, past the queue, the floor, the panel mute and chat mutes; only "None" or the sounds slider at 0 silence it. Its shipped default is short and about 12 dB quieter than the others.
- Removed with keys, view-model properties, registry entries, rows and code paths: "Sound for other chats" (`raven.chatSound`), "Cooldown" (`raven.cooldownSeconds`), "The chat I'm in also waits for the cooldown" (`raven.ownNewsWaits`). Stored values of these keys are ignored, not deleted.
- New keys: `raven.sound.start`, `raven.sound.finish`, `raven.sound.question`, `raven.sound.permission`, `raven.sound.fail`, `raven.sound.name`; value = a sound id string, or `"none"`. An unknown id loads as the slot's default.
- Shipped sounds: musical/natural (marimba, kalimba, soft bells, wooden knocks, plucks), 16-bit PCM mono 44.1 kHz, at most 1.5 s, made by `scripts/sounds/make-sounds.cs`, licence CC0 1.0 recorded in `src/CodeSwitchX.Voice/Audio/Sounds/SOURCES.md`.
- At sounds level 0 nothing opens a device, but a sound still takes its length in the queue.
- Match the surrounding code: comment density, `<summary>` prose style, naming. Run tests through the exe or `--solution` (a project path reports zero tests).

## Review Focus

1. A stored sound id that no longer exists (renamed pool, hand-edited DB): the slot loads its default, never None or a throw. Test in Task 7.
2. The user switches chats while the old chat's sound is held for its line: it is no longer "the chat I'm in", so it plays through the queue as another chat's sound, once. Test in Task 6.
3. Ten chats finish at once: ten sounds one after another with gaps, never overlapping; the panel mute halfway stops the one playing and drops the rest. Test in Task 4.
4. A question answered in VS Code while its sound waits behind others: dropped, never played late. Test in Task 6.
5. The chat the user is in finishes with no voice ready (TTS failed or installing): its sound is not held for a line that never comes, it plays through the queue. Test in Task 6.

---

### Task 1: The shipped pool

**Files:**
- Create: `scripts/sounds/make-sounds.cs` (file-based app, writes the WAVs), `src/CodeSwitchX.Voice/Audio/Sounds/*.wav` (12 files), `src/CodeSwitchX.Voice/Audio/Sounds/SOURCES.md`, `src/CodeSwitchX.Voice/Audio/ShippedSounds.cs`
- Modify: `src/CodeSwitchX.Voice/CodeSwitchX.Voice.csproj` (embed `Audio\Sounds\*.wav` as `CodeSwitchX.Voice.Sounds.%(Filename)%(Extension)`, like the samples)
- Test: `tests/CodeSwitchX.Voice.Tests/Audio/ShippedSoundsTests.cs`

**Interfaces:**
- Produces:
  - `public enum SoundSlot { StartsWorking, Finishes, AsksQuestion, AsksPermission, Fails, HearsName }` (in `ShippedSounds.cs`).
  - `public sealed record ShippedSound(string Id, string Name)`.
  - `public static class ShippedSounds` with `IReadOnlyList<ShippedSound> All`, `string DefaultFor(SoundSlot slot)`, `bool Has(string id)`, `internal static (byte[] Pcm, WaveFormat Format) Load(string id)` (reads the embedded WAV with `WaveFileReader`, cached).
- The 12 ids and names: `marimba-rise` "Marimba, rising", `marimba-fall` "Marimba, falling", `marimba-single` "Marimba note", `kalimba-up` "Kalimba, two notes up", `kalimba-single` "Kalimba note", `bell-soft` "Soft bell", `bell-pair` "Two bells", `wood-knock` "Wood knock", `wood-double` "Two wood knocks", `pluck-high` "High pluck", `pluck-low` "Low pluck", `two-tones` "Two tones (the old chime)".
- Defaults: StartsWorking `wood-knock`, Finishes `marimba-rise`, AsksQuestion `kalimba-up`, AsksPermission `bell-pair`, Fails `marimba-fall`, HearsName `pluck-high`.

- [ ] **Step 1: Write the failing tests** in `ShippedSoundsTests`:
  - `Every_shipped_sound_loads_as_16_bit_mono_PCM_at_44_1_kHz`: for each of `All`, `Load(id).Format` is PCM, 16 bit, 1 channel, 44100 Hz, and the PCM is not empty.
  - `No_shipped_sound_is_longer_than_one_and_a_half_seconds`.
  - `No_shipped_sound_clips`: peak sample ≤ 0.9 × `short.MaxValue`; and ≥ 0.5 × for all but `pluck-high`.
  - `The_name_sound_is_quiet_and_short`: `DefaultFor(HearsName)` has a peak ≤ 0.3 × `short.MaxValue` and lasts ≤ 0.4 s.
  - `Each_slot_has_its_own_default_from_the_pool`: six defaults, all distinct, all `Has(id)`.
  - `The_old_chime_is_in_the_pool`: `Has("two-tones")`.
- [ ] **Step 2: Run them to see them fail**
  Run: `dotnet test --solution CodeSwitchX.slnx --filter "FullyQualifiedName~ShippedSoundsTests"` — expected: build error, `ShippedSounds` not defined.
- [ ] **Step 3: Write `scripts/sounds/make-sounds.cs`** (run with `dotnet run scripts/sounds/make-sounds.cs`), writing all 12 files into `src/CodeSwitchX.Voice/Audio/Sounds/`. Synthesis, each with a 2 ms fade-in and an exponential decay to silence at its end:
  - marimba: partials at 1×, 3.93×, 9.2× the fundamental, amplitudes 1, 0.35, 0.1, the upper ones decaying 3× and 6× faster; notes C5–E5–G5 (rising), G5–E5–C5 (falling, a fail), single A5.
  - kalimba: partials 1×, 5.4×, 13.3×, a short noise "tine" click in the first 4 ms; two notes D6 then A6 (a question), single E6.
  - bells: inharmonic partials 1×, 2.76×, 5.4×, 8.93× with slow decay; soft bell at G5, pair at E5 then B5.
  - wood: a 25 ms burst of noise through a resonant two-pole band-pass at 900 Hz (Q 8), plus a 600 Hz partial decaying in 40 ms; double = two knocks 120 ms apart.
  - pluck: Karplus–Strong, A6 (high, then scaled to peak 0.25 and cut at 0.35 s) and A4 (low).
  - two-tones: today's chime tones (880 Hz 0.11 s, 1320 Hz 0.14 s, 12 ms fades).
  - Normalise each to a peak of 0.85 × full scale (`pluck-high` to 0.25), so the sounds' default level of 60 % is about as loud as the old chime.
  Write `SOURCES.md`: every file made by this script; licence CC0 1.0 (public domain dedication); how to remake them.
- [ ] **Step 4: Implement `ShippedSounds`** and the csproj embedding; run the script once and commit the WAVs.
- [ ] **Step 5: Run the tests** — expected: PASS.
- [ ] **Step 6: Commit** `feat: a pool of twelve shipped sounds made by a script (#306)`.

### Task 2: Playing a sound

**Files:**
- Create: `src/CodeSwitchX.Voice/Audio/ChatSounds.cs` (`IChatSounds`, `ChatSounds`, `SoundPicks`)
- Modify: `src/CodeSwitchX.Voice/Audio/AudioOutput.cs` (overload), `src/CodeSwitchX.Voice/Audio/SpeechPlayer.cs` and `src/CodeSwitchX.Voice/Speech/VoiceSamples.cs` (use the overload), `src/CodeSwitchX.Voice/VoiceServiceCollectionExtensions.cs`
- Delete: `src/CodeSwitchX.Voice/Audio/ChatChime.cs` and its test (the "as loud as before" test moves to `ChatSoundsTests` against `two-tones`)
- Test: `tests/CodeSwitchX.Voice.Tests/Audio/ChatSoundsTests.cs`, `tests/CodeSwitchX.Voice.Tests/Audio/AudioOutputTests.cs`

**Interfaces:**
- Consumes: `ShippedSounds.Load`, `AudioLevels.Sounds`, `Loudness`.
- Produces:
  - On `IAudioOutput`: `IWavePlayer Open(IWaveProvider source, Func<float> gain, int latencyMs) => Open(new Loudness(source, gain), latencyMs);` (default interface method; the #305 review note). Speech and samples use it with `() => levels.Voice`.
  - `public interface IChatSounds { Task PlayAsync(string soundId, CancellationToken ct); TimeSpan LengthOf(string soundId); }` — `PlayAsync` completes when the sound has played out, or at once when cancelled (the player is stopped and disposed); never throws (a device failure is logged and completes it). At `AudioLevels.Sounds == 0` no device is opened and it completes after `LengthOf(id)` (via `TimeProvider`). An unknown id completes at once and logs a warning.
  - `public sealed class ChatSounds(IAudioOutput output, AudioLevels levels, TimeProvider time, ILogger<ChatSounds> logger) : IChatSounds`.
  - `public sealed class SoundPicks` — `string? For(SoundSlot slot)` (null = None) and `void Set(SoundSlot slot, string? id)`; any thread; starts on `ShippedSounds.DefaultFor`. Registered singleton, like `AudioLevels`.
  - DI: `IChatSounds` → `ChatSounds`, `SoundPicks` singleton; `IChatChime` gone.

- [ ] **Step 1: Write the failing tests**:
  - `The_open_with_a_gain_scales_what_plays` (AudioOutputTests): a fake default player captures the provider; reading it at gain 0.5 halves samples.
  - `Playing_opens_the_output_at_the_sounds_level_and_completes_when_it_stopped`: fake `IAudioOutput` returns a fake player; `PlayAsync("marimba-rise")` not complete until the fake raises `PlaybackStopped`.
  - `Cancelling_stops_and_disposes_the_player`.
  - `At_level_zero_no_device_opens_and_it_takes_the_sound_s_length` (FakeTimeProvider: incomplete until advanced by `LengthOf`).
  - `A_failing_device_completes_without_throwing`; `An_unknown_sound_completes_at_once`.
  - `The_old_chime_at_the_default_level_is_as_loud_as_before`: `two-tones` peak × `GainOf(60)` within 5 % of `0.33 × short.MaxValue`.
  - `Picks_start_on_the_slots_defaults_and_none_is_null`.
- [ ] **Step 2: Run them to see them fail** (`--filter "FullyQualifiedName~ChatSoundsTests|FullyQualifiedName~AudioOutputTests"`): build errors.
- [ ] **Step 3: Implement** the overload, `ChatSounds`, `SoundPicks`; switch `SpeechPlayer`, `VoiceSamples` to the overload; delete `ChatChime`; register in DI. `SettingsViewModel` and `RavenPanelViewModel` still take `IChatChime?` — change those parameters to `IChatSounds?` and keep their current calls compiling by playing `SoundPicks`' Finishes sound where `_chime.Play()` was (Tasks 6 and 7 replace these call sites).
- [ ] **Step 4: Run the Voice and UI test suites** — expected: PASS (apart from the 3 `HotkeyServiceTests` that fail while the stable app holds the hotkeys).
- [ ] **Step 5: Commit** `feat: sounds play from the pool at the sounds' level, the chime is gone (#306)`.

### Task 3: Where cues come from

**Files:**
- Modify: `src/CodeSwitchX.Core/Sessions/SessionSnapshot.cs`, `src/CodeSwitchX.Core/Sessions/SessionEngine.cs` (hook event `Commit`, ~line 190), `src/CodeSwitchX.UI/Raven/ChatNews.cs`
- Create: `src/CodeSwitchX.UI/Raven/ChatSoundCues.cs` (`SoundCue`, `SoundLine`, `ChatSoundCues`)
- Test: `tests/CodeSwitchX.Core.Tests/Sessions/SessionEngineTests.cs` (or its existing hook-event test file), `tests/CodeSwitchX.UI.Tests/Raven/ChatSoundCuesTests.cs`

**Interfaces:**
- Produces:
  - `SessionSnapshot.TurnStartedAt` (`DateTimeOffset?`, not persisted): set to the event's time on `UserPromptSubmit`, kept otherwise.
  - `ChatNews.Noted` — `event EventHandler<(ChatNewsKind Kind, string SessionId, Guid? WorkspaceId)>?`, raised in `Offer` exactly where `Arrived` is (after the stopped-on-purpose and asked-here filters), any thread.
  - `public enum SoundLine { None, News, Card }` — whether Raven may speak a line for the cue: none (a start), the chat's news, or a card read out.
  - `public sealed record SoundCue(SoundSlot Slot, string SessionId, Guid? WorkspaceId, DateTimeOffset At, SoundLine Line, Func<bool> StillTrue)`.
  - `public sealed class ChatSoundCues : IDisposable` — `ChatSoundCues(IEventBus bus, ChatNews news, TimeProvider time)`; `event EventHandler<SoundCue>? Cue` (any thread). From the bus: a `SessionChanged` whose `Current.TurnStartedAt` is set and differs from `Previous?.TurnStartedAt`, with `Current.ShowsAsChat`, gives `StartsWorking` / `SoundLine.None`, still true while that session's latest known state is `Working`. From `ChatNews.Noted`: Finished → `Finishes`, Failed → `Fails` (still true while the session is not `Working`), NeedsYou → `AsksQuestion` (still true while it is `Waiting`), all `SoundLine.News`. It keeps each session's latest state from the bus for `StillTrue`. Cards are cued by the panel (Task 6), not here.

- [ ] **Step 1: Write the failing tests**:
  - Core: `A_prompt_stamps_the_turn_start_and_a_tool_use_keeps_it` (UserPromptSubmit at t1 → `TurnStartedAt == t1`; PreToolUse at t2 → still t1; UserPromptSubmit at t3 → t3).
  - `A_prompt_cues_starts_working_once_per_turn` (Idle→Working with a new `TurnStartedAt`: one cue; Working→Waiting→Working with the same stamp: none; a new prompt after an idle-prompt Waiting: one cue).
  - `A_start_is_no_longer_true_once_the_chat_stops_working`.
  - `Finished_failed_and_needs_you_news_cue_their_slots` (via `ChatNews.Offer` with Working→Idle, `TurnFailed` Working→Idle, →Waiting).
  - `A_finish_is_no_longer_true_once_the_chat_works_again` and `Needs_you_is_no_longer_true_once_it_stops_waiting`.
  - `A_turn_raven_stopped_and_a_question_held_here_give_no_news_cue` (the ChatNews filters apply).
- [ ] **Step 2: Run them to see them fail.**
- [ ] **Step 3: Implement** the snapshot field, the engine line (`TurnStartedAt = e.EventName == "UserPromptSubmit" ? e.At : s.TurnStartedAt`), `ChatNews.Noted`, `ChatSoundCues`; register `ChatSoundCues` as a singleton in `App.xaml.cs` next to `ChatNews`.
- [ ] **Step 4: Run Core and UI tests** — expected: PASS.
- [ ] **Step 5: Commit** `feat: chats' starts, ends, failures and waits become sound cues (#306)`.

### Task 4: The queue

**Files:**
- Create: `src/CodeSwitchX.UI/Raven/SoundQueue.cs`
- Test: `tests/CodeSwitchX.UI.Tests/Raven/SoundQueueTests.cs`

**Interfaces:**
- Consumes: `IChatSounds`, `SoundPicks`, `SoundCue`, `TrafficWatcher.NewsGrace`, `ChatNews.MaximumAge`.
- Produces: `public sealed class SoundQueue` — UI thread only (all calls and continuations).
  - `SoundQueue(IChatSounds sounds, SoundPicks picks, TimeProvider time, Func<bool> mayPlay, Func<SoundCue, bool> waitsForLine)`.
  - `static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(300)`.
  - `void Add(SoundCue cue)` — a cue whose slot is picked None is not added; a queued cue of the same `SessionId` is replaced.
  - `void Kick()` — the panel's state changed: re-check `mayPlay`. Playing starts once `mayPlay()` has held through `NewsGrace` (a timer restarted by each `Kick` that finds it false→true; a `Kick` finding it false cancels the timer).
  - `IReadOnlyList<SoundCue> TakeForLine(string sessionId)` — removes and returns that session's queued cues that are still true (for the prelude).
  - `void Clear()` — drops all and cancels the one playing.
  - `bool IsPlaying`; `event EventHandler? Played` — raised as each sound starts (the panel calls `Traffic.Announced()`); `event EventHandler? PlayingChanged`.
  - Before playing the front cue: drop it if `!StillTrue()`, if older than `MaximumAge`, or if its slot is now None; skip (keep) it while `waitsForLine(cue)`. One plays at a time; after it, wait `Gap`, then the next if `mayPlay()` (no new grace between sounds of one run).

- [ ] **Step 1: Write the failing tests** (fake `IChatSounds` with a `TaskCompletionSource` per play, `FakeTimeProvider`):
  - `Nothing_plays_until_the_floor_has_been_free_for_the_grace`.
  - `Sounds_play_one_after_another_with_the_gap`.
  - `A_busy_floor_holds_the_rest_and_the_grace_starts_again`.
  - `A_newer_cue_of_the_same_chat_replaces_the_older`.
  - `A_cue_no_longer_true_is_dropped_when_its_turn_comes`.
  - `A_cue_older_than_two_minutes_is_dropped`.
  - `A_slot_set_to_none_after_it_queued_is_dropped`.
  - `A_cue_waiting_for_its_line_is_passed_over_and_taken_for_it` (others play; `TakeForLine` returns it; it never plays from the queue).
  - `A_cue_that_stops_waiting_for_its_line_plays_from_the_queue`.
  - `Ten_cues_play_one_at_a_time_and_clear_stops_the_rest` (Review Focus 3: max one incomplete play at any time; `Clear` cancels the playing token and none of the rest start).
  - `Played_is_raised_as_each_sound_starts`.
- [ ] **Step 2: Run them to see them fail.**
- [ ] **Step 3: Implement `SoundQueue`.**
- [ ] **Step 4: Run them** — PASS.
- [ ] **Step 5: Commit** `feat: one queue plays the chats' sounds in turn when the floor is free (#306)`.

### Task 5: A sound right before a spoken line

**Files:**
- Modify: `src/CodeSwitchX.Voice/Speech/ReplyVoice.cs` (`SpokenReply`, `SpeakAsync`)
- Test: `tests/CodeSwitchX.Voice.Tests/Speech/ReplyVoiceTests.cs`

**Interfaces:**
- Produces: `SpokenReply.Prelude` — `public Func<CancellationToken, Task>? Prelude { get; set; }`, set before the first `Add`. `SpeakAsync` awaits it once per reply, after the reply's first chunk is synthesised and before it is enqueued, outside `_playerGate`, with the hush token; hushed meanwhile, nothing of the reply is enqueued. A faulted prelude is logged and the line still plays.

- [ ] **Step 1: Write the failing tests**: `The_prelude_plays_out_before_the_first_audio_is_queued` (fake player sees no chunk until the prelude's task completes); `The_prelude_runs_once_per_reply` (two sentences, one prelude call); `A_hush_during_the_prelude_queues_nothing`; `A_failing_prelude_still_speaks_the_line`.
- [ ] **Step 2: Run them to see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the Voice tests** — PASS.
- [ ] **Step 5: Commit** `feat: a reply can open with a sound played right before it (#306)`.

### Task 6: Raven's panel uses the queue; the traffic watcher loses the cooldown

**Files:**
- Modify: `src/CodeSwitchX.UI/Raven/RavenPanelViewModel.cs`, `src/CodeSwitchX.UI/Raven/TrafficWatcher.cs`, `src/CodeSwitchX.UI/App.xaml.cs` (constructor wiring)
- Test: `tests/CodeSwitchX.UI.Tests/Raven/RavenPanelViewModelTests.Traffic.cs` (rewrite the chime/cooldown tests), `tests/CodeSwitchX.UI.Tests/Raven/TrafficWatcherTests.cs`, `tests/CodeSwitchX.UI.Tests/Raven/RavenPanelViewModelTests.CatchUp.cs` (drop cooldown cases)

**Interfaces:**
- Consumes: Tasks 2–5.
- The panel's constructor takes `IChatSounds? sounds`, `SoundPicks? picks`, `ChatSoundCues? cues` in place of `IChatChime? chime`; it builds the `SoundQueue` with:
  - `mayPlay = () => FloorIsFree && CallListening() <= TimeSpan.Zero && !HasSomethingToTell && !_telling`
  - `waitsForLine = cue => cue.Line != SoundLine.None && ChatOf(cue.WorkspaceId) == CurrentChat && SpeakNews && !IsMuted && _tts.Status.State is TextToSpeechState.Ready or TextToSpeechState.Off`
- `TrafficWatcher.Floor` gains `bool Sounding = false` (last, defaulted); `IsFree` requires it false; the panel passes `_soundQueue.IsPlaying` and calls `UpdateState` on `PlayingChanged`.
- Removed from `TrafficWatcher`: `DefaultCooldown`, `CooldownChoices`, `Cooldown`, `CooledDown`, `SoundOn`, `OwnNewsWaits`, `TrySound`, `MaySpeakOwnNews`; class summary rewritten for the queue. Removed from the panel: `SoundForOtherChat` and every call.

- [ ] **Step 1: Write the failing tests** (panel tests with fakes for `IChatSounds`, a real `SoundQueue`):
  - `Another_chat_s_finish_plays_its_sound_once_the_floor_is_free` (and is still only marked, never spoken).
  - `The_chat_i_am_in_plays_its_sound_right_before_its_news_line` (the reply's `Prelude` plays the Finishes sound; the queue never plays it).
  - `A_question_read_out_opens_with_the_question_sound`.
  - `A_muted_chat_and_the_panel_mute_queue_nothing_and_the_mute_clears_the_queue`.
  - `Speak_chat_news_off_still_plays_the_chat_s_sounds` (own chat's sound then plays from the queue).
  - `Switching_away_releases_a_sound_held_for_its_line` (Review Focus 2).
  - `A_question_answered_in_vs_code_drops_its_waiting_sound` (Review Focus 4: card closes before its turn).
  - `With_no_voice_ready_the_own_chat_s_sound_plays_from_the_queue` (Review Focus 5).
  - `The_catch_up_goes_before_other_chats_sounds`.
  - `A_sound_restarts_the_pause_for_raven_s_own_speech_but_not_for_the_next_sound`.
  - `Raven_hearing_its_name_plays_at_once_even_muted_and_with_the_floor_busy` (Open mic name alone, `_calledAt` set: `PlayAsync(picks.For(HearsName))` called without waiting; with None, nothing).
  - TrafficWatcher: the cooldown tests are deleted; `The_floor_is_busy_while_a_sound_plays`.
- [ ] **Step 2: Run them to see them fail.**
- [ ] **Step 3: Implement**: subscribe to `ChatSoundCues.Cue` (marshal to the UI thread like `ChatNews.Arrived`), add the cue unless `IsMuted` or `ChatOf(cue.WorkspaceId).IsMuted`; in `Arrive(card)` add an `AsksQuestion`/`AsksPermission` cue with `SoundLine.Card` and `StillTrue = () => card.IsOpen`; call `_soundQueue.Kick()` wherever `ScheduleNews()` is called from a state change; on `Played` call `Traffic.Announced()`; in `TellNewsAsync` and `TellQuestionsAsync`, right after `_voice.Begin(...)`, set `spoken.Prelude` from the newest cue of `TakeForLine(sessionId)` of the line spoken (news: the own lines' session ids; card: its session) to `ct => _sounds.PlayAsync(picks.For(slot)!, ct)`; in `ForRaven` where the name alone sets `_calledAt`, fire `_sounds.PlayAsync(picks.For(HearsName), CancellationToken.None)` when not None; `OnIsMutedChanged(true)` calls `_soundQueue.Clear()`. Delete `SoundForOtherChat`, the `Traffic.MaySpeakOwnNews` condition and the cooldown members.
- [ ] **Step 4: Run the UI tests** — PASS (bar the 3 hotkey tests).
- [ ] **Step 5: Commit** `feat: Raven's panel plays every chat's sounds through the queue, the cooldown is gone (#306)`.

### Task 7: The Sounds page's six pickers; the old settings go

**Files:**
- Modify: `src/CodeSwitchX.UI/Settings/SettingKeys.cs`, `SettingsViewModel.cs`, `AppSettings.cs`, `SettingsPage.cs` (Sounds keywords), `SettingsView.xaml` (Sounds page section "When a chat…"; Voice page rows removed), `src/CodeSwitchX.UI/Shell/ShellViewModel.cs` (`ShowTraffic`, the property-changed list)
- Test: `tests/CodeSwitchX.UI.Tests/Settings/SettingsViewModelTests.cs`, `tests/CodeSwitchX.UI.Tests/Settings/AppSettingsTests.cs`, `tests/CodeSwitchX.UI.Tests/Shell/ShellViewModelTests.cs`

**Interfaces:**
- Consumes: `ShippedSounds`, `SoundPicks`, `IChatSounds`.
- Produces in `SettingsViewModel`:
  - `public sealed record SoundChoice(string? Id, string Name)`; `IReadOnlyList<SoundChoice> SoundChoices` = `("None")` + the pool in `ShippedSounds.All` order.
  - Six `[ObservableProperty] string? _ravenSoundStart`, `_ravenSoundFinish`, `_ravenSoundQuestion`, `_ravenSoundPermission`, `_ravenSoundFail`, `_ravenSoundName` (null = None), loaded from their keys (`"none"` → null; unknown or missing → default), persisted on change, pushed into `SoundPicks`.
  - `[RelayCommand] void PlaySlotSound(SoundSlot slot)` — plays the slot's pick (nothing for None), cancelling a play button's sound still playing.
  - `PlaySoundLevel` (the slider preview) plays the Finishes pick, or `two-tones` when that is None.
- Registry (`AppSettings`): six entries on `SettingsPage.Sounds`, names "sound when a chat starts working", "… finishes", "… asks a question", "… asks permission", "… fails", "sound when Raven hears its name"; options = pool names + "None"; set by name via `OneOf`. Removed: "sound for other chats", "cooldown", "the chat I'm in also waits for the cooldown". "pause between messages" and "catch-up when I switch chats" descriptions say a chat's sound restarts the pause and drop "a chat's sound inside it is left out".
- `SettingsPage.Sounds` keywords add: start, finish, question, permission, fail, name, marimba, kalimba, bell, knock, pluck.
- XAML: the Sounds page gets a second card "When a chat…" with six rows (label 130 px, `ComboBox` bound to `SoundChoices` with `SelectedValuePath="Id"`, a play `IconButton` with `AutomationProperties.Name="Play <slot label>"`); under "Raven hears its name" a help line "Plays at once, even while you talk." The Voice page's "Sound for other chats", "Cooldown" and "The chat I'm in also waits for the cooldown" rows are deleted.

- [ ] **Step 1: Write the failing tests**:
  - `Each_slot_loads_its_default_when_nothing_is_stored`.
  - `A_stored_sound_that_no_longer_exists_loads_the_slot_s_default` (Review Focus 1) and `A_stored_none_loads_as_none`.
  - `Picking_a_sound_persists_it_and_tells_the_picks`.
  - `The_play_button_plays_the_slot_s_pick_and_none_plays_nothing`.
  - AppSettings: `Raven_sets_a_slot_s_sound_by_its_name`, `The_removed_settings_are_not_known_any_more` ("cooldown", "sound for other chats").
  - Shell: the `ShowTraffic` test keeps only the pause.
- [ ] **Step 2: Run them to see them fail.**
- [ ] **Step 3: Implement**, deleting `RavenCooldownSeconds`, `RavenChatSound`, `RavenOwnNewsWaits`, `CooldownChoices` and their keys, loads, persists and rows.
- [ ] **Step 4: Run all UI tests** — PASS (bar the 3 hotkey tests); build the app.
- [ ] **Step 5: Commit** `feat: six sound pickers on the Sounds page, the old chime settings are gone (#306)`.

### Task 8: On screen, and the user listens

- [ ] **Step 1:** With the user's go, close the stable build, run the branch build on the BenQ with scratch workspaces (see the `running-the-app-from-claude` notes), Raven not muted, Open mic off.
- [ ] **Step 2:** Capture the Sounds page and the Voice page (the three rows gone).
- [ ] **Step 3:** With real or relayed hook events check: another chat's finish makes its sound once the floor is free; the chat the user is in plays its sound right before its spoken news; a question is read out after its sound; the catch-up still comes on switching; nothing plays over speech; ten quick events play one at a time. Prove where sound went with the audio-session probe (`sessions.cs`).
- [ ] **Step 4:** Ask the user to listen to the six defaults with the play buttons and say which to change; change defaults in `ShippedSounds` as they choose.
- [ ] **Step 5:** Restore the stable build; screenshots into `docs/review/screenshots/issue306-*.png`; open the PR.
