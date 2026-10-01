# Raven's Open mic (#75)

A second mic mode, **Open mic**: Raven listens all the time, notices when the user has finished speaking (also across a
thinking pause mid-sentence), and stops speaking when the user talks over it. Push to talk stays the default. Design
approved in chat on 2026-10-01. Builds on #73's floor; the wake word and echo cancellation are out of scope.

## What the user decided

- **The mic button and the push-to-talk hotkey pause and resume Open mic** (a call, a colleague at the desk). They do not
  end a turn or record by hand.
- **Turn detection runs in-process** with `Microsoft.ML.OnnxRuntime`: Silero VAD finds speech, Smart Turn v3 decides
  whether a pause ends the turn (from the issue).
- **No echo cancellation.** The user talks through a headset or the RØDE virtual input. A setting turns voice barge-in
  off for when Raven trips on its own voice.
- **Speech shorter than 0.5 s is ignored.**

## Facts it rests on

- **Smart Turn v3** (`pipecat-ai/smart-turn-v3` on Hugging Face, BSD-2-Clause): `smart-turn-v3.2-cpu.onnx`, 8,679,182
  bytes, int8, a Whisper Tiny encoder with a linear head, multilingual (German and English among them), about 12 ms per
  call on a CPU. Input `input_features`, float32 `[1, 80, 800]`: the Whisper log-mel features (n_fft 400, hop 160,
  80 mel bins, `do_normalize=True`) of the last 8 s of 16 kHz audio, **padded with zeros at the front** when shorter.
  Output: the probability that the turn is complete; above 0.5 is complete. Pipecat computes the features in numpy
  (`pipecat/audio/turn/smart_turn/_whisper_features.py`), which is what gets ported.
- **Pipecat's settings** with Smart Turn: the VAD stops after 0.2 s of silence and Smart Turn is asked then; a turn that
  Smart Turn keeps calling incomplete ends after 3 s of silence (`stop_secs`); 0.5 s of audio before the speech start is
  kept (`pre_speech_ms`); the segment is at most 8 s (`max_duration_secs`).
- **Silero VAD** (`snakers4/silero-vad`, MIT, release v6.2.3): `src/silero_vad/data/silero_vad.onnx`, 2,327,524 bytes.
  Inputs `input` float32 `[1, 576]` (the last 64 samples of the previous frame, then a 512-sample frame: 32 ms at
  16 kHz), `state` float32 `[2, 1, 128]`, `sr` int64 (16000). Outputs the speech probability `[1, 1]` and the next
  state. The state and the 64-sample context carry from frame to frame and are reset with the stream.
- **The recorder** (`WasapiMicrophoneRecorder`) hands out each block's RMS only, keeps the whole recording, and stops at
  120 s. Open mic needs the samples as they come and must keep nothing, so it gets its own capture.
- **The panel** already turns a clip into a question: `TranscribeInTurnAsync` (Whisper, the log entry, then `Ask` with
  #73's merge and interrupt rules). A press hushes Raven and stops a digest (`StartRecording`); only a question
  interrupts an answer.

## 1. Parts

All in `CodeSwitchX.Voice/Listening/` unless named otherwise.

### WasapiMicrophoneStream (`IMicrophoneStream`)

- `Start(deviceId)` / `Stop()`, the same device rules as the recorder: a device enumerator per stream made on the
  calling thread, the capture built with no synchronisation context, `Failed` on the capture thread when the device
  dies.
- `FramesCaptured`: the captured block as 16 kHz mono floats with its RMS. The resampler carries its state across blocks
  (the recorder's `AudioMath.Resample` works on a whole clip), so block edges add no clicks or drift.
- Keeps no samples and has no length limit: Open mic runs for hours.

### SileroVad (`IVoiceActivity`)

- `float Step(ReadOnlySpan<float> frame512)`: the speech probability of one 32 ms frame; `Reset()` clears the state and
  the context.
- One ONNX session, one inter-op and one intra-op thread (as Silero's own wrapper).

### SmartTurn (`ITurnEnd`) and WhisperFeatures

- `WhisperFeatures.LogMel(ReadOnlySpan<float> audio16k)`: the last 8 s, front-padded, as `float[80 * 800]`: a port of
  Pipecat's numpy code (Hann window, reflect-padded STFT, Slaney mel filters, log10, clamp to max − 8, (x + 4) / 4).
- `double Complete(ReadOnlySpan<float> turnAudio)`: the probability that the turn is complete. One ONNX session,
  sequential, one inter-op thread.

### TurnDetector

The logic, with no I/O: it takes 16 kHz frames and the two models behind their interfaces, and keeps time by the
samples it has been fed, so tests drive it frame by frame.

- **Pre-roll:** the last 0.5 s before speech is kept in a ring buffer and starts the turn's clip.
- **Speech:** a frame with a probability of 0.5 or more is speech. Speech that adds up to **0.5 s** raises
  `SpeechStarted`; a shorter burst followed by 0.2 s of silence is dropped without a trace (a cough, a key, a knock).
- **A pause:** after **0.2 s** of silence the detector asks Smart Turn about the turn so far (its last 8 s).
  - Complete: `TurnEnded(clip)`, the clip being the pre-roll and everything since, silence trimmed to 0.2 s.
  - Incomplete: it waits. Speech that resumes continues the same turn, and the next pause asks again.
- **Fallbacks:** the turn ends anyway after **3 s** of silence, or when it reaches **120 s** (the recorder's limit, and
  Whisper's sensible length).
- **Raven speaking, barge-in off:** while the listener is told Raven is speaking, frames are not counted as speech, and
  a turn that had not raised `SpeechStarted` yet is dropped.
- `Reset()`: back to waiting, with nothing kept (pause, a mode switch, a new microphone).

### OpenMicListener

- Ties the stream to the detector. The capture thread only queues frames (a bounded channel); one worker thread cuts
  them into 512-sample frames and runs the detector, so ONNX never runs on the capture thread and never on the UI thread.
- `Start(deviceId)` returns the new run (an `OpenMicRun`, ending the run before it); `Stop(run)` closes that run only
  while it is still the current one, so a late stop never closes a newer run. Both are serialised by the listener and
  called off the UI thread. `IgnoreSpeech { set; }`. A pause is a stop: the microphone is closed, so Windows'
  microphone indicator goes off while Open mic is paused; resume starts it again. The panel sets `IgnoreSpeech` while
  Raven speaks with voice barge-in off.
- Events, raised on the worker thread (the panel posts them to the UI thread): `SpeechStarted(run)`,
  `TurnEnded(run, clip)`, `Heard(CapturedBlock)` (the loudest RMS and the duration of about 50 ms of captured audio, for
  the orb's level and the silent-microphone watch). `Failed(run)` comes on the capture thread, with the run's
  `Failure` set first, and the listener then stops that run itself. The panel ignores events of a run that is not its
  current one, and reads `Failure` on the run a start returns, for a microphone that died while it opened.
- If the worker falls behind and the queue is full, blocks are dropped; that is logged once per run.
- A Smart Turn failure is logged once per listener and the turn falls back to the 3 s rule: a turn is never lost to it.

### ListeningModelStore

- The two model files in the models folder next to the Whisper model, each with its pinned URL, length and SHA-256.
- `IsPresent`, `DownloadAsync(progress, ct)`: downloaded to a temporary file, checked, then moved into place; a failed,
  cancelled or mismatching download leaves nothing behind.
- A file that ONNX Runtime refuses (invalid or corrupt) is deleted, so the next switch to Open mic downloads it again.
  Any other load failure (the native runtime missing, out of memory) deletes nothing.

## 2. The panel

`RavenPanelViewModel`, `RavenPanelView.xaml`.

- **Mode picker:** a two-way switch "Push to talk | Open mic" next to the microphone list, saved as `raven.micMode`
  (`SettingKeys.RavenMicMode`); Push to talk is the default and what an unknown value reads as.
- **Switching to Open mic:** downloads the models if they are missing, with the progress in the log as the Whisper
  model's download shows it, then starts the listener on the selected microphone. A failed download warns and switches
  back to Push to talk.
- **Switching to Push to talk:** stops the stream at once (the Windows microphone indicator goes off); a turn being
  spoken is dropped. Push to talk then works as before.
- **The listener runs while the panel is collapsed and while the app is in the background**: the user's hands are in
  VS Code. A new microphone restarts it.
- **The mic button and the hotkey in Open mic** pause and resume. The button's tooltip and accessible name follow
  ("Pause Open mic" / "Resume Open mic").
- **What the orb and the caption show in Open mic:**

  | Situation | Caption | Orb |
  |---|---|---|
  | Waiting for the user | "Open mic" | The light ring (below) |
  | After `SpeechStarted` | "Listening…" | Listening: the wave ring, as in Push to talk |
  | Paused | "Open mic paused" | The light ring, still and dimmed |
  | Then | Transcribing, Thinking, Speaking as today | |

- **The light ring** (the user's pick of four mockups): Open mic's own look while it waits, so a live microphone never
  looks like Push to talk's idle breath. 36 dots on the outer ring (the radius of Idle's thin ring, `Base + 44`), dim
  (alpha 0.16); a soft glint drifts round them (0.9 rad/s, a Gaussian falloff) and lights and enlarges the dots it
  passes; the room's sound lights the dots unevenly, each by its own flicker, so a sound shows as a sparkle round the
  ring. The wave ring rests inside at a lower alpha (0.55). When speech starts the dots fade out as the wave ring takes
  the level, an eased blend rather than a cut. Paused draws the dots still at half their alpha, with no glint.
  `RavenState` gets two states for it, `Attending` (waiting) and `AttendingPaused`. The waiting ring moves at the idle
  frame rate while CodeSwitchX is the active window and, like Idle, stops on a still frame in a background window: Open
  mic waits for hours. With Windows' animations off it draws one still frame; a hidden orb draws nothing. The panel's
  header reads "OPEN MIC" while waiting and "PAUSED" while paused.


- **`SpeechStarted`** does what a press does: Raven stops speaking, a digest stops, the voice expects an answer, the brain
  warms up. Those steps move out of `StartRecording` into one `UserStartsTalking()` both modes call. An answer still
  being written is interrupted only by the question, as #73 decided.
- **`TurnEnded(clip)`** queues the clip into the existing transcription queue (`TranscribeInTurnAsync`) as a finished
  stop; the user's turn ended when the detector saw the pause, which is the time Raven's first-word log line counts from.
  An empty transcript is logged, not noted in the log ("I didn't hear anything" after every noise would be clutter).
- **`RavenSpeaking`** follows `ReplyVoice`: the listener is told when Raven starts and stops being heard.
- **Barge-in setting:** under Raven in Settings, "Stop Raven when I talk over it", on by default, saved as
  `raven.bargeIn` (`SettingKeys.RavenBargeIn`). Off, the listener ignores speech while Raven speaks.

## 3. Failures

- **The models cannot be downloaded:** a warning with the reason, and the mode goes back to Push to talk.
- **A model will not load:** a file ONNX Runtime refuses is deleted, a warning says so, and the mode goes back to Push
  to talk. Any other failure to start the listener warns with its message and goes back to Push to talk, deleting
  nothing.
- **The microphone fails** (unplugged, gone): the same warning as today; the listener stops, the mode stays Open mic and
  shows paused, and the mic button tries again. The silent-microphone watch is push to talk's, per start: "No sound
  from …" (taken back when a late microphone starts sending), and "… stopped sending sound" when a microphone that was
  heard goes digitally silent (a mute key, a headset asleep), with a note when it is back.
- **The speech model fails in an Open mic turn:** the warnings say the next turn tries again ("Speak again to try
  again."), not to press the mic, which would pause Open mic.
- **Smart Turn fails mid-turn:** logged once; the 3 s rule ends the turn.
- **The transcript is empty:** logged only.

## 4. Tests

- **TurnDetector**, with fake models, frame by frame: a 0.4 s burst is ignored; a pause Smart Turn calls incomplete,
  then more speech, makes one turn; a complete pause ends the turn after 0.2 s; the 3 s fallback; the 0.5 s pre-roll is
  in the clip; the 120 s cap; `Reset` drops a half-spoken turn; with barge-in off, speech while Raven speaks is not a
  turn.
- **WhisperFeatures** against Pipecat's numpy code on a fixed clip: the reference features are made once with uv and
  committed as a test fixture, and the C# port must match them closely.
- **The real models** (tests that run only when the model files are present): Silero finds speech in the warm-up sample
  (`WarmUpSpeech.pcm`) and none in silence; a spoken sentence with a one-second gap in the middle comes out of the whole
  listener, fed from a file, as **one** turn, and a finished sentence ends one within a second of its last word.
- **ListeningModelStore:** a mismatching download leaves nothing behind; a present file is not downloaded again.
- **The panel:** switching modes starts and stops the listener and puts the orb in `Attending`; the mic button pauses and resumes in Open mic;
  `SpeechStarted` hushes Raven and stops a digest; `TurnEnded` goes through Whisper to `Ask`; a failed download
  switches back to Push to talk; the barge-in setting reaches the listener.
- **On screen:** a test build fed by audio files instead of the microphone, captured with PrintWindow. The live check
  (talk, pause mid-sentence, talk over Raven) is the user's: nobody else can talk into their microphone.
