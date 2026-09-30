# Raven Panel with Dictation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a collapsible "Raven" column on the left of the CodeSwitchX main window where I hold a mic button (or Ctrl+Alt+Space), speak German or English, and see my words in a log (issue #69).

**Architecture:** A new class library `CodeSwitchX.Voice` holds everything that is not WPF: audio math, microphone capture on NAudio WASAPI, and Whisper dictation ported from ContentAutomatorX. The UI project gets a `Raven` folder with `RavenPanelViewModel`, `RavenPanelView` and an animated `RavenOrb`. The panel sits in a new left column of `MainWindow`. Its open/collapsed state and chosen microphone are persisted through `SettingsViewModel` in the same way as the Yard tile size (#65). Global hotkeys go through the existing `HotkeyService`.

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm 8.4.2, NAudio (WASAPI), Whisper.net 1.9.1 with the Vulkan runtime and CPU fallback, xunit v3 + Shouldly + NSubstitute.

**Spec:** GitHub issue #69 (`gh issue view 69`). Background: the concept page https://claude.ai/artifact/WA9sYtuQxbWuA6LEcoJ9Gb (private) and the design spec `docs/superpowers/specs/2026-09-23-codeswitchx-design.md`.

## Global Constraints

- The whole repo builds with `TreatWarningsAsErrors=true` and `EnforceCodeStyleInBuild=true`. Every new warning is a build error. That includes IDE0161 (file-scoped namespaces required) and missing braces on `if` statements.
- Files use CRLF line endings, UTF-8 and 4-space indent (2 spaces for xml/csproj/props/json). Edit C# with an editor tool, not shell heredocs. Heredocs corrupt backslashes and `\u` escapes, and Python writes turn CRLF into LF. Check `git diff --stat` after any scripted edit.
- Central package management: add every package version to `Directory.Packages.props` as `<PackageVersion Include="X" Version="..." />`. In csproj files write `<PackageReference Include="X" />` with no version.
- Packages: `Whisper.net` 1.9.1, `Whisper.net.Runtime.Vulkan` 1.9.1, `Whisper.net.Runtime` 1.9.1 (the CPU fallback). Do NOT use `Whisper.net.Runtime.Cuda12`: it has no native code for the user's Blackwell GPU (sm_120). NAudio: use `NAudio.Wasapi` and `NAudio.Core` at the newest stable 2.x version NuGet offers (2.2.1 at the time of writing).
- Whisper runs with language `"auto"`, because the user mixes German and English. ContentAutomatorX runs with `"en"`; do not copy that default.
- Model: `LargeV3Turbo`, file `ggml-large-v3-turbo.bin`, stored in `%LOCALAPPDATA%\CodeSwitchX\models` (`AppPaths.ModelsDirectory`).
- Clips shorter than 500 ms are dropped, because Whisper makes up words on silence. Recording stops by itself at 120 s.
- Hotkeys: Ctrl+Alt+Space is push-to-talk (not Ctrl+Shift+Space, which is VS Code's Trigger Parameter Hints): hold to talk, or a quick tap latches it on. Ctrl+Alt+J collapses/expands the panel. Both are global, registered through `HotkeyService`.
- Panel widths: 320 px open and 58 px collapsed. The collapsed rail keeps a mini orb and the mic button.
- Colors, following the existing views: mic live / recording `#EF4444`, working/transcribing amber `#F59E0B`, voice accent (orb) `#62D0E8`, surfaces `#111827` / `#1F2937`, text `#D1D5DB` / `#9CA3AF`.
- The user-facing name is "Raven". Code and UI copy never say "Jarvis".
- Test style: xunit v3 `[Fact]`/`[Theory]`, Shouldly, NSubstitute, long snake_case test names (e.g. `The_tile_size_is_loaded_and_saved`), `NullLogger<T>.Instance`.
- Running tests: `dotnet test --solution CodeSwitchX.slnx` runs everything. For one project: `dotnet build tests/<Project>` then run `tests/<Project>/bin/Debug/net10.0-windows/<Project>.exe` (add `--filter-class <Full.Name>` to narrow). `dotnet test tests/<Project>` can wrongly report zero tests; don't trust it.
- Never commit to `main`. The work happens on `feature/raven-panel-dictation`. Commit after each task; the controller pushes.
- Do not touch the RAIVEN app or its hooks.

## Review Focus

1. **The selected microphone is unplugged while I hold the button.** The recording stops and the state returns to Idle. The log explains what happened and the picker falls back to the default device. Nothing crashes and nothing hangs in Listening. Covered in Task 4 (`A_microphone_lost_while_recording_returns_to_idle_and_says_so`).
2. **Windows microphone privacy blocks desktop apps.** Pressing the mic shows one warning naming Settings → Privacy & security → Microphone. Covered in Task 3 (HResult classification) and Task 4 (`A_denied_microphone_explains_the_privacy_setting`).
3. **The speech model is not downloaded yet.** The first press still records. Afterwards the log shows download progress, then the transcript. A failed download leaves a warning and the next press tries again. Covered in Task 4 (`A_missing_model_is_downloaded_then_the_clip_is_transcribed` and `A_failed_download_is_reported_and_retried_next_time`).
4. **Ctrl+Alt+Space is already taken by another app.** The app starts normally, the log says the hotkey is unavailable, and the mic button still works. Covered in Task 5 (`HotkeyService` reports failed registrations; the shell writes them to the Raven log).
5. **A tap vs. a hold.** A press shorter than 350 ms latches recording on, and the next press stops it. A longer hold stops on release. Autorepeat from a held key must not restart anything. Covered in Task 4 (`PushToTalkGesture` tests).

---

## File Structure

```
src/CodeSwitchX.Voice/                      (new class library, net10.0-windows, no WPF)
  CodeSwitchX.Voice.csproj
  VoiceServiceCollectionExtensions.cs       AddCodeSwitchXVoice(modelsDirectory)
  Audio/AudioMath.cs                        RMS, level (dB→0..1), mono downmix, resample to 16 kHz
  Audio/SilentMicWatch.cs                   port of dictation.js signalStep (silent / dropped / live)
  Audio/SampleDecoder.cs                    NAudio WaveFormat bytes → mono floats
  Audio/MicrophoneDevice.cs                 record MicrophoneDevice(string Id, string Name)
  Audio/MicrophoneChoice.cs                 stored → exact / relocated by name / default / none
  Audio/MicrophoneFailure.cs                enum MicrophoneFailureKind + MicrophoneException + HResult classifier
  Audio/IMicrophoneCatalog.cs               list, default, DevicesChanged
  Audio/WasapiMicrophoneCatalog.cs          MMDeviceEnumerator + IMMNotificationClient
  Audio/IMicrophoneRecorder.cs              Start / Stop → RecordedClip, LevelChanged, Failed
  Audio/WasapiMicrophoneRecorder.cs         WasapiCapture implementation
  Dictation/…                               ported from ContentAutomatorX src/Dictation.Whisper
tests/CodeSwitchX.Voice.Tests/              (new test project)
src/CodeSwitchX.Core/AppPaths.cs            + ModelsDirectory
src/CodeSwitchX.UI/Raven/
  RavenState.cs, RavenLogEntry.cs, PushToTalkGesture.cs
  RavenPanelViewModel.cs
  WorkspaceVocabularyProvider.cs            workspace + folder names → Whisper prompt
  RavenPanelView.xaml(.cs), RavenOrb.cs
src/CodeSwitchX.UI/MainWindow.xaml          new left column
src/CodeSwitchX.UI/Shell/ShellViewModel.cs  + Raven property, settings sync
src/CodeSwitchX.UI/Settings/…               + RavenPanelOpen, RavenMicrophone persisted
src/CodeSwitchX.UI/Infrastructure/HotkeyService.cs  + push-to-talk (press + release poll) and collapse
src/CodeSwitchX.UI/App.xaml.cs              DI
```

---

### Task 1: Voice project and audio math

**Files:**
- Create: `src/CodeSwitchX.Voice/CodeSwitchX.Voice.csproj`, `src/CodeSwitchX.Voice/Audio/AudioMath.cs`, `src/CodeSwitchX.Voice/Audio/SilentMicWatch.cs`
- Create: `tests/CodeSwitchX.Voice.Tests/CodeSwitchX.Voice.Tests.csproj`, `tests/CodeSwitchX.Voice.Tests/Audio/AudioMathTests.cs`, `tests/CodeSwitchX.Voice.Tests/Audio/SilentMicWatchTests.cs`
- Modify: `CodeSwitchX.slnx` (add both projects, in the same folders as their siblings)

**Interfaces:**
- Produces:
  - `public static class AudioMath { public const int TargetRate = 16000; static float Rms(ReadOnlySpan<float> samples); static double LevelOf(float rms); static float[] ToMono(ReadOnlySpan<float> interleaved, int channels); static float[] Resample(ReadOnlySpan<float> mono, int fromRate, int toRate = TargetRate); }`
  - `public enum SignalEvent { Silent, Dropped, Live }`
  - `public sealed class SilentMicWatch { public SilentMicWatch(SilentMicWatch.Config? config = null); public SignalEvent? Step(float rms, TimeSpan blockDuration); public void Reset(); public sealed record Config(float Floor = 1e-6f, TimeSpan? Grace = null /*2 s*/, TimeSpan? Regrace = null /*10 s*/, TimeSpan? Sustain = null /*0.3 s*/); }`

- [ ] **Step 1: Create the projects.** Base `CodeSwitchX.Voice.csproj` on `src/CodeSwitchX.Core/CodeSwitchX.Core.csproj`. Root props already set the TFM, nullable and warnings-as-errors. Add `<InternalsVisibleTo Include="CodeSwitchX.Voice.Tests" />`. Base the test csproj on `tests/CodeSwitchX.Core.Tests/CodeSwitchX.Core.Tests.csproj` and give it a project reference to the Voice project. Add both projects to `CodeSwitchX.slnx`.

- [ ] **Step 2: Write failing tests for `AudioMath`.**

```csharp
namespace CodeSwitchX.Voice.Tests.Audio;

using CodeSwitchX.Voice.Audio;

public sealed class AudioMathTests
{
    [Fact]
    public void The_rms_of_a_full_scale_square_wave_is_one()
    {
        AudioMath.Rms([1f, -1f, 1f, -1f]).ShouldBe(1f, 1e-6f);
    }

    [Fact]
    public void The_rms_of_nothing_is_zero()
    {
        AudioMath.Rms([]).ShouldBe(0f);
    }

    [Theory]
    [InlineData(1f, 1d)]        // 0 dB
    [InlineData(0.001f, 0d)]    // -60 dB is the floor
    [InlineData(0f, 0d)]
    [InlineData(0.0316228f, 0.5d)] // -30 dB is half way
    public void The_level_maps_minus_60_to_0_db_onto_0_to_1(float rms, double level)
    {
        AudioMath.LevelOf(rms).ShouldBe(level, 0.001);
    }

    [Fact]
    public void Stereo_is_averaged_to_mono()
    {
        AudioMath.ToMono([1f, 0f, 0.5f, 0.5f], channels: 2).ShouldBe([0.5f, 0.5f]);
    }

    [Fact]
    public void Mono_input_is_copied_unchanged()
    {
        AudioMath.ToMono([0.1f, 0.2f], channels: 1).ShouldBe([0.1f, 0.2f]);
    }

    [Fact]
    public void Resampling_48k_to_16k_keeps_one_third_of_the_samples_and_the_dc_level()
    {
        var input = Enumerable.Repeat(0.25f, 48000).ToArray();
        var output = AudioMath.Resample(input, 48000);
        output.Length.ShouldBe(16000);
        output.ShouldAllBe(s => Math.Abs(s - 0.25f) < 1e-4f);
    }

    [Fact]
    public void Resampling_at_the_target_rate_returns_a_copy()
    {
        AudioMath.Resample([0.1f, 0.2f, 0.3f], 16000).ShouldBe([0.1f, 0.2f, 0.3f]);
    }
}
```

- [ ] **Step 3: Run the tests to see them fail.** Run `dotnet build tests/CodeSwitchX.Voice.Tests`. Expected: build errors, because `AudioMath` does not exist yet.

- [ ] **Step 4: Implement `AudioMath`.**

```csharp
namespace CodeSwitchX.Voice.Audio;

/// <summary>Pure sample math for microphone audio (ported from ContentAutomatorX dictation.js).</summary>
public static class AudioMath
{
    public const int TargetRate = 16000;

    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0f;
        }

        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// <summary>Maps an RMS value onto 0..1 for a level meter: -60 dB and below is 0, 0 dB is 1.</summary>
    public static double LevelOf(float rms)
    {
        if (rms <= 0f)
        {
            return 0d;
        }

        var db = 20 * Math.Log10(rms);
        return Math.Clamp((db + 60) / 60, 0d, 1d);
    }

    public static float[] ToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 1)
        {
            return interleaved.ToArray();
        }

        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                sum += interleaved[(f * channels) + c];
            }

            mono[f] = sum / channels;
        }

        return mono;
    }

    /// <summary>Averages windows when down-sampling and holds samples when up-sampling (same as dictation.js resampleTo16k).</summary>
    public static float[] Resample(ReadOnlySpan<float> mono, int fromRate, int toRate = TargetRate)
    {
        if (fromRate == toRate)
        {
            return mono.ToArray();
        }

        var outLength = (int)((long)mono.Length * toRate / fromRate);
        var output = new float[outLength];
        var ratio = (double)fromRate / toRate;
        for (var i = 0; i < outLength; i++)
        {
            var start = (int)(i * ratio);
            var end = Math.Min(mono.Length, Math.Max(start + 1, (int)((i + 1) * ratio)));
            float sum = 0;
            for (var j = start; j < end; j++)
            {
                sum += mono[j];
            }

            output[i] = sum / (end - start);
        }

        return output;
    }
}
```

- [ ] **Step 5: Port `SilentMicWatch`.** Read `E:\Repos\ContentAutomatorX\src\Dictation.Whisper\wwwroot\dictation.js` lines 186-263 (`signalConfig`, `signalStep`) and its tests in `E:\Repos\ContentAutomatorX\tests\dictation-js\signal.test.js`.
  - Translate every `signal.test.js` case into `SilentMicWatchTests` as `[Fact]`s, keeping the same inputs and expected events.
  - Then port `signalStep` into `SilentMicWatch.Step`. Each call is one captured block. The JS state object becomes private fields, and the JS `"silent"`/`"dropped"`/`"live"` strings become `SignalEvent`. `Step` returns `null` when nothing is reported.
  - Keep the constants from the JS: floor `1e-6`, grace 2 s, regrace 10 s, sustain 0.3 s.
  - `Reset()` restores the fresh state, for the start of a new recording.

- [ ] **Step 6: Run the tests.** Build, then run `tests/CodeSwitchX.Voice.Tests/bin/Debug/net10.0-windows/CodeSwitchX.Voice.Tests.exe`. Expected: all pass. Then run `dotnet build CodeSwitchX.slnx`. Expected: 0 warnings.

- [ ] **Step 7: Commit.**

```bash
git add src/CodeSwitchX.Voice tests/CodeSwitchX.Voice.Tests CodeSwitchX.slnx
git commit -m "feat: add the Voice library with audio math and the silent-mic watch (#69)"
```

---

### Task 2: Whisper dictation ported from ContentAutomatorX

**Files:**
- Create in `src/CodeSwitchX.Voice/Dictation/`, ported from `E:\Repos\ContentAutomatorX\src\Dictation.Whisper\`:
  - `IDictationService.cs` (with `DictationResult`, `DictationModelMissingException`, `DictationModelLoadException`)
  - `DictationOptions.cs` (with `WhisperModel`)
  - `DictationVocabulary.cs` (with `Correction`, `IDictationVocabularyProvider`)
  - `IWhisperModelStore.cs`, `WhisperModelStore.cs`, `WhisperDictationService.cs`, `VocabularyPrompt.cs`, `TranscriptCorrector.cs`
- Create: `src/CodeSwitchX.Voice/VoiceServiceCollectionExtensions.cs`
- Modify: `src/CodeSwitchX.Core/AppPaths.cs` (add `ModelsDirectory` and create it in `EnsureCreated`), plus its test in `tests/CodeSwitchX.Core.Tests` (find `AppPathsTests`)
- Modify: `Directory.Packages.props`, `src/CodeSwitchX.Voice/CodeSwitchX.Voice.csproj`
- Create tests in `tests/CodeSwitchX.Voice.Tests/Dictation/`, ported from `E:\Repos\ContentAutomatorX\tests\ContentAutomatorX.UnitTests\Dictation\`: `WhisperDictationServiceTests`, `WhisperModelStoreTests`, `TranscriptCorrectorTests`, `VocabularyPromptTests`. Plus a new `SpokenAudioTranscriptionTests`.

**Interfaces:**
- Consumes: `AudioMath.TargetRate` (Task 1).
- Produces (same shapes as in ContentAutomatorX; namespace `CodeSwitchX.Voice.Dictation`):
  - `public interface IDictationService { Task<DictationResult> TranscribeAsync(ReadOnlyMemory<float> samples, DictationVocabulary vocabulary, bool live, CancellationToken ct); Task WarmUpAsync(CancellationToken ct); }`
  - `public sealed record DictationResult(string Text, TimeSpan AudioLength);`
  - `public interface IWhisperModelStore { WhisperModel Model { get; } string ModelPath { get; } bool IsPresent { get; } long SizeBytes { get; } string? LoadedRuntime { get; } Task DownloadAsync(IProgress<double>? progress, CancellationToken ct); }`
  - `public sealed record DictationVocabulary(IReadOnlyList<string> Words, IReadOnlyList<Correction> Corrections)` (keep whatever shape CAX has; if CAX has an `Empty` member keep it)
  - `public interface IDictationVocabularyProvider { Task<DictationVocabulary> GetAsync(CancellationToken ct); }`
  - `public static IServiceCollection AddCodeSwitchXVoice(this IServiceCollection services, string modelsDirectory)` registers `DictationOptions` (ModelFolder = modelsDirectory, Model = LargeV3Turbo, Language = "auto"), `IWhisperModelStore` → `WhisperModelStore` (singleton), `IDictationService` → `WhisperDictationService` (singleton), `IMicrophoneCatalog` and `IMicrophoneRecorder` (Task 3 adds these two lines). It does NOT register `DictationWarmUpService`; see Step 3.
  - `AppPaths.ModelsDirectory` → `Path.Combine(Root, "models")`.

- [ ] **Step 1: Add the packages.** In `Directory.Packages.props` add `Whisper.net`, `Whisper.net.Runtime.Vulkan` and `Whisper.net.Runtime`, all at 1.9.1, plus `Microsoft.Extensions.Options` at 10.0.12 (match the other Microsoft.Extensions versions there). Reference them from the Voice csproj, together with `Microsoft.Extensions.Logging.Abstractions` and `Microsoft.Extensions.DependencyInjection.Abstractions`.

- [ ] **Step 2: Port the tests first.** Copy the four CAX test files listed above into `tests/CodeSwitchX.Voice.Tests/Dictation/` and convert them:
  - Change the namespace.
  - Replace xunit v2 `Assert.*` with Shouldly.
  - Keep hand-written fakes where CAX has them.
  - Keep every case, including `ShortClip_ReturnsEmptyText_WithoutNeedingTheModel` and `WarmUp_WithBrokenModel_SwallowsTheFailure`. You may rename them to the repo's snake_case style.
  - Add one new test: `The_default_options_use_large_v3_turbo_with_automatic_language`, asserting `new DictationOptions().Model == WhisperModel.LargeV3Turbo` and `.Language == "auto"`.

  Run the build. Expected: compile errors, because the types don't exist yet.

- [ ] **Step 3: Port the sources.** Copy each listed CAX source file, change the namespace to `CodeSwitchX.Voice.Dictation`, and make these changes only:
  - `DictationOptions`: the default `Language` becomes `"auto"`, the default `Model` becomes `WhisperModel.LargeV3Turbo`, and `MinimumClip` stays 500 ms. Drop any ASP.NET-only members.
  - `DictationWarmUpService` is NOT ported. Warm-up is triggered by the panel when a recording starts (Task 4), so the 1.6 GB model is not loaded at every app start.
  - `DictationEndpointHandler`, `DictationRegistration.MapWhisperDictation`, `Pcm16`, `README.md` and the `wwwroot` folder are NOT ported. Capture delivers floats directly.
  - Apply the repo's code style: file-scoped namespaces, braces on every `if`, no `this.`. The build enforces it.

- [ ] **Step 4: `AddCodeSwitchXVoice` and `AppPaths.ModelsDirectory`.** Write the extension method, modelled on `src/CodeSwitchX.Hosting/HostingServiceCollectionExtensions.cs`.
  - Use `services.Configure<DictationOptions>(o => { o.ModelFolder = modelsDirectory; o.Model = WhisperModel.LargeV3Turbo; o.Language = "auto"; })`, then the two singletons.
  - Add `ModelsDirectory` to `AppPaths`, and `Directory.CreateDirectory(ModelsDirectory)` in `EnsureCreated`.
  - Extend `AppPathsTests` with a case asserting `ModelsDirectory == Path.Combine(root, "models")`.

- [ ] **Step 5: The spoken-audio integration test.** It proves the real model and runtime transcribe speech. It is explicit (`[Fact(Explicit = true)]`), so the normal suite doesn't need the 1.6 GB file. The controller runs it once.

```csharp
namespace CodeSwitchX.Voice.Tests.Dictation;

using System.Speech.Synthesis;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public sealed class SpokenAudioTranscriptionTests
{
    // Needs ggml-large-v3-turbo.bin in %LOCALAPPDATA%\CodeSwitchX\models.
    [Fact(Explicit = true)]
    public async Task Synthesized_english_speech_is_transcribed_by_the_real_model()
    {
        var options = Options.Create(new DictationOptions
        {
            ModelFolder = CodeSwitchX.Core.AppPaths.Default().ModelsDirectory,
            Model = WhisperModel.LargeV3Turbo,
            Language = "auto",
        });
        using var service = new WhisperDictationService(new WhisperModelStore(options), options, NullLogger<WhisperDictationService>.Instance);

        var samples = Speak("Open the Diffusion Nexus workspace and start a new chat.");
        var result = await service.TranscribeAsync(samples, new DictationVocabulary(["Diffusion Nexus"], []), live: false, TestContext.Current.CancellationToken);

        result.Text.ShouldContain("Diffusion Nexus", Case.Insensitive);
        result.Text.ShouldContain("chat", Case.Insensitive);
    }

    private static float[] Speak(string text)
    {
        using var stream = new MemoryStream();
        using (var synth = new SpeechSynthesizer())
        {
            synth.SetOutputToAudioStream(stream, new System.Speech.AudioFormat.SpeechAudioFormatInfo(AudioMath.TargetRate, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            synth.Speak(text);
        }

        var bytes = stream.ToArray();
        var floats = new float[bytes.Length / 2];
        for (var i = 0; i < floats.Length; i++)
        {
            floats[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        }

        return floats;
    }
}
```

  Add the `System.Speech` package (newest 10.x version) to `Directory.Packages.props`, referenced only from the Voice.Tests project. The test project needs a reference to `CodeSwitchX.Core` for `AppPaths`. Adjust the `DictationVocabulary` constructor call to the ported shape if it differs.

- [ ] **Step 6: Run the tests.** Build, then run the Voice.Tests exe. Expected: all pass, with the explicit test not run. Run the Core.Tests exe with `--filter-class *AppPathsTests`. Expected: pass. Run `dotnet build CodeSwitchX.slnx`. Expected: 0 warnings.

- [ ] **Step 7: Commit.**

```bash
git add -A src/CodeSwitchX.Voice src/CodeSwitchX.Core tests Directory.Packages.props
git commit -m "feat: port Whisper dictation into the Voice library, large-v3-turbo with automatic language (#69)"
```

---

### Task 3: Microphone catalog and recorder on NAudio WASAPI

**Files:**
- Create in `src/CodeSwitchX.Voice/Audio/`: `MicrophoneDevice.cs`, `MicrophoneChoice.cs`, `MicrophoneFailure.cs`, `SampleDecoder.cs`, `IMicrophoneCatalog.cs`, `WasapiMicrophoneCatalog.cs`, `IMicrophoneRecorder.cs`, `WasapiMicrophoneRecorder.cs`
- Modify: `src/CodeSwitchX.Voice/VoiceServiceCollectionExtensions.cs` (register the catalog and recorder as singletons), `Directory.Packages.props`, `src/CodeSwitchX.Voice/CodeSwitchX.Voice.csproj`
- Test in `tests/CodeSwitchX.Voice.Tests/Audio/`: `MicrophoneChoiceTests.cs`, `MicrophoneFailureTests.cs`, `SampleDecoderTests.cs`

**Interfaces:**
- Consumes: `AudioMath` (Task 1).
- Produces:

```csharp
public sealed record MicrophoneDevice(string Id, string Name);

public enum MicrophoneChoiceOutcome { Stored, Relocated, FellBackToDefault, NoDevices }
public sealed record MicrophoneChoiceResult(MicrophoneDevice? Device, MicrophoneChoiceOutcome Outcome);
public static class MicrophoneChoice
{
    // stored == null means "no choice yet": the default device, reported as Stored.
    public static MicrophoneChoiceResult Resolve(IReadOnlyList<MicrophoneDevice> devices, MicrophoneDevice? stored, MicrophoneDevice? systemDefault);
}

public enum MicrophoneFailureKind { Denied, Missing, Unavailable }
public sealed class MicrophoneException(MicrophoneFailureKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public MicrophoneFailureKind Kind { get; } = kind;
}
public static class MicrophoneFailure
{
    public static MicrophoneFailureKind Classify(Exception error); // HResult based, see Step 3
}

public static class SampleDecoder
{
    // Decodes a WASAPI buffer (IEEE float 32, or PCM 16/24/32) to mono floats.
    public static float[] ToMonoFloats(byte[] buffer, int bytesRecorded, NAudio.Wave.WaveFormat format);
}

public interface IMicrophoneCatalog
{
    IReadOnlyList<MicrophoneDevice> List();          // active capture endpoints
    MicrophoneDevice? Default();                      // Windows default for Role.Communications, then Role.Console
    event EventHandler? DevicesChanged;               // raised on any thread
}

public sealed record RecordedClip(float[] Samples16k, TimeSpan Length);
public interface IMicrophoneRecorder
{
    bool IsRecording { get; }
    void Start(string deviceId);                      // throws MicrophoneException
    RecordedClip Stop();                              // safe when not recording: returns an empty clip
    event EventHandler<float>? BlockCaptured;         // RMS of each captured block; raised on the capture thread
    event EventHandler<MicrophoneException>? Failed;  // capture died mid-recording (device unplugged); raised on the capture thread
}
```

- [ ] **Step 1: Write failing tests for `MicrophoneChoice`.**

```csharp
namespace CodeSwitchX.Voice.Tests.Audio;

using CodeSwitchX.Voice.Audio;

public sealed class MicrophoneChoiceTests
{
    private static readonly MicrophoneDevice Rode = new("{id-rode}", "Microphone (RØDE Connect Virtual)");
    private static readonly MicrophoneDevice Headset = new("{id-headset}", "Headset (Arctis Nova 7)");

    [Fact]
    public void The_stored_device_is_used_when_it_is_still_there()
    {
        MicrophoneChoice.Resolve([Rode, Headset], Headset, Rode)
            .ShouldBe(new MicrophoneChoiceResult(Headset, MicrophoneChoiceOutcome.Stored));
    }

    [Fact]
    public void A_stored_device_with_a_new_id_is_found_again_by_name()
    {
        var moved = Headset with { Id = "{id-headset-2}" };
        MicrophoneChoice.Resolve([Rode, moved], Headset, Rode)
            .ShouldBe(new MicrophoneChoiceResult(moved, MicrophoneChoiceOutcome.Relocated));
    }

    [Fact]
    public void Two_devices_with_the_stored_name_count_as_not_found()
    {
        var a = Headset with { Id = "{a}" };
        var b = Headset with { Id = "{b}" };
        MicrophoneChoice.Resolve([Rode, a, b], Headset, Rode)
            .ShouldBe(new MicrophoneChoiceResult(Rode, MicrophoneChoiceOutcome.FellBackToDefault));
    }

    [Fact]
    public void A_missing_device_falls_back_to_the_default()
    {
        MicrophoneChoice.Resolve([Rode], Headset, Rode)
            .ShouldBe(new MicrophoneChoiceResult(Rode, MicrophoneChoiceOutcome.FellBackToDefault));
    }

    [Fact]
    public void Without_a_stored_choice_the_default_is_used()
    {
        MicrophoneChoice.Resolve([Rode, Headset], null, Rode)
            .ShouldBe(new MicrophoneChoiceResult(Rode, MicrophoneChoiceOutcome.Stored));
    }

    [Fact]
    public void Without_a_default_the_first_device_is_used()
    {
        MicrophoneChoice.Resolve([Headset], null, null)
            .ShouldBe(new MicrophoneChoiceResult(Headset, MicrophoneChoiceOutcome.Stored));
    }

    [Fact]
    public void No_devices_means_no_microphone()
    {
        MicrophoneChoice.Resolve([], Headset, null)
            .ShouldBe(new MicrophoneChoiceResult(null, MicrophoneChoiceOutcome.NoDevices));
    }
}
```

- [ ] **Step 2: Implement `MicrophoneDevice` and `MicrophoneChoice`** so the tests pass. When the stored device is missing and there's no default, fall back to the first device, reported as `FellBackToDefault`.

- [ ] **Step 3: Write failing tests for `MicrophoneFailure.Classify`, then implement it.**
  - Classification uses `Exception.HResult`, also checking `InnerException` and `COMException`:
    - `0x80070005` (E_ACCESSDENIED) → `Denied`.
    - `0x88890004` (AUDCLNT_E_DEVICE_INVALIDATED), `0x80070490` (E_NOTFOUND) and `0x88890005` → `Missing`.
    - Anything else, including `0x8889000A` (AUDCLNT_E_DEVICE_IN_USE) → `Unavailable`.
  - `UnauthorizedAccessException` → `Denied`.
  - Tests use `new COMException("x", unchecked((int)0x80070005))` and similar, one `[Theory]` row per mapping, plus one case for an inner exception.

- [ ] **Step 4: Write failing tests for `SampleDecoder`, then implement it.**
  - Cases:
    - IEEE float stereo 48 kHz (`WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)`): bytes of `[1f, 0f, -1f, -1f]` → `[0.5f, -1f]`.
    - PCM 16-bit mono: bytes `[0x00,0x00, 0xFF,0x7F, 0x00,0x80]` → `[0f, 32767f/32768f, -1f]`.
    - PCM 24-bit mono: one sample of `0x7FFFFF` → about 1.
    - `WaveFormatExtensible` with float subformat, which WASAPI usually reports: treat it as float. Detect with `format.Encoding == WaveFormatEncoding.IeeeFloat || (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT)`.
    - `bytesRecorded` smaller than `buffer.Length` → only the recorded part is decoded.
  - Implementation: decode interleaved samples to float, then `AudioMath.ToMono`.
  - Add `NAudio.Wasapi` and `NAudio.Core` (see Global Constraints) to the props and the Voice csproj.

- [ ] **Step 5: Implement `WasapiMicrophoneCatalog`.** It has no unit test; it is thin over the OS.
  - `List()` → `new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)` mapped to `MicrophoneDevice(d.ID, d.FriendlyName)`, disposing the devices.
  - `Default()` → `GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)`, falling back to `Role.Console`. Catch the `COMException` thrown when there is none and return null.
  - It implements `NAudio.CoreAudioApi.Interfaces.IMMNotificationClient`, registered with `RegisterEndpointNotificationCallback` in the constructor and unregistered in `Dispose`. `OnDeviceStateChanged`, `OnDeviceAdded`, `OnDeviceRemoved` and `OnDefaultDeviceChanged` (capture flow only) raise `DevicesChanged`.
  - Keep one `MMDeviceEnumerator` for the callback's lifetime.

- [ ] **Step 6: Implement `WasapiMicrophoneRecorder`.** No unit test; the controller verifies it on screen.
  - `Start(deviceId)`:
    - Find the device with `enumerator.GetDevice(deviceId)`, wrapping failures in `MicrophoneException(MicrophoneFailure.Classify(e), …)`.
    - Create `new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50)` (shared mode). Subscribe `DataAvailable` and `RecordingStopped`, then `StartRecording()`, also wrapped.
  - `DataAvailable`:
    - `SampleDecoder.ToMonoFloats(e.Buffer, e.BytesRecorded, capture.WaveFormat)` → append to a `List<float>` (lock-free is fine: single capture thread; `Stop` reads after stopping).
    - Raise `BlockCaptured(AudioMath.Rms(block))`.
    - Stop automatically once 120 s of audio has been captured: call `StopRecording()` and remember the auto-stop.
  - `RecordingStopped` with `e.Exception != null` while not stopping on purpose → raise `Failed(new MicrophoneException(MicrophoneFailure.Classify(e.Exception), e.Exception.Message, e.Exception))`.
  - `Stop()`:
    - Call `StopRecording()` and wait for `RecordingStopped` with a `ManualResetEventSlim`, up to 2 s.
    - Dispose the capture and the device.
    - Return `new RecordedClip(AudioMath.Resample(captured, capture.WaveFormat.SampleRate), length)`.
  - Register `IMicrophoneCatalog` → `WasapiMicrophoneCatalog` and `IMicrophoneRecorder` → `WasapiMicrophoneRecorder` as singletons in `AddCodeSwitchXVoice`.

- [ ] **Step 7: Run the tests.** Build and run the Voice.Tests exe. Expected: all pass. Run `dotnet build CodeSwitchX.slnx`. Expected: 0 warnings.

- [ ] **Step 8: Commit.**

```bash
git add -A src/CodeSwitchX.Voice tests/CodeSwitchX.Voice.Tests Directory.Packages.props
git commit -m "feat: microphone catalog and WASAPI recorder with device fallback and failure kinds (#69)"
```

---

### Task 4: The Raven panel view model

**Files:**
- Create in `src/CodeSwitchX.UI/Raven/`: `RavenState.cs`, `RavenLogEntry.cs`, `PushToTalkGesture.cs`, `RavenPanelViewModel.cs`, `WorkspaceVocabularyProvider.cs`
- Modify: `src/CodeSwitchX.UI/CodeSwitchX.UI.csproj` (project reference to CodeSwitchX.Voice)
- Test in `tests/CodeSwitchX.UI.Tests/Raven/`: `PushToTalkGestureTests.cs`, `RavenPanelViewModelTests.cs`, `WorkspaceVocabularyProviderTests.cs`

**Interfaces:**
- Consumes: `IMicrophoneCatalog`, `IMicrophoneRecorder`, `RecordedClip`, `MicrophoneDevice`, `MicrophoneChoice`, `MicrophoneException`/`MicrophoneFailureKind`, `SilentMicWatch`/`SignalEvent`, `AudioMath.LevelOf` (Tasks 1 and 3); `IDictationService`, `IWhisperModelStore`, `IDictationVocabularyProvider`, `DictationVocabulary`, `DictationModelMissingException`, `DictationModelLoadException` (Task 2); `IUiDispatcher` (existing, `Post(Action)`); `TimeProvider`.
- Produces:

```csharp
public enum RavenState { Idle, Listening, Transcribing }
public enum RavenLogKind { You, Note, Warning }
public sealed partial class RavenLogEntry : ObservableObject   // Text is observable so a progress line can update in place
{
    public RavenLogEntry(RavenLogKind kind, string text, DateTimeOffset at);
    public RavenLogKind Kind { get; }
    public DateTimeOffset At { get; }
    [ObservableProperty] private string _text;
}

public enum PushToTalkAction { None, Start, Stop }
public sealed class PushToTalkGesture(TimeProvider time)
{
    public static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(350);
    public PushToTalkAction Press();    // Start when idle; Stop when latched; None when already held (autorepeat)
    public PushToTalkAction Release();  // Stop after a hold >= threshold; None (latch) after a tap; None when not pressed
    public void Reset();                // forget any latch/hold (recording ended for another reason)
}

public sealed partial class RavenPanelViewModel : ObservableObject
{
    public RavenPanelViewModel(IMicrophoneCatalog catalog, IMicrophoneRecorder recorder, IDictationService dictation,
        IWhisperModelStore models, IDictationVocabularyProvider vocabulary, IUiDispatcher dispatcher, TimeProvider time,
        ILogger<RavenPanelViewModel> logger);

    [ObservableProperty] bool IsOpen = true;                         // persisted by the shell (Task 5)
    public ObservableCollection<MicrophoneDevice> Microphones { get; }
    [ObservableProperty] MicrophoneDevice? SelectedMicrophone;       // persisted by the shell (Task 5)
    [ObservableProperty] RavenState State;
    [ObservableProperty] double Level;                               // 0..1, live while Listening
    [ObservableProperty] string Caption;                             // one line under the orb
    [ObservableProperty] string TypedText = "";
    public ObservableCollection<RavenLogEntry> Log { get; }

    public void RefreshMicrophones();                                 // re-list; apply MicrophoneChoice; note fallbacks in the log
    [RelayCommand] void TogglePanel();
    public void PressMic();                                           // button mouse-down / hotkey down
    public Task ReleaseMicAsync();                                    // button mouse-up / hotkey up (awaits transcription when it stops)
    [RelayCommand] void SubmitTyped();                                // Enter in the text box
    public void Note(string text);                                    // a Note entry (the shell uses it for hotkey failures)
}
```

Behavior. Every rule below needs a test:

- **Gesture.** `PressMic()` feeds `PushToTalkGesture.Press()` and `ReleaseMicAsync()` feeds `Release()`. `Start` begins a recording and `Stop` ends it. While `State == Transcribing`, `PressMic()` does nothing.
- **Start.**
  - With no `SelectedMicrophone` (no devices), the log gets the Warning "No microphone found. Plug one in or check Windows sound settings." and the state stays Idle.
  - Otherwise: `recorder.Start(SelectedMicrophone.Id)`, `State = Listening`, `Caption = "Listening…"`, `SilentMicWatch.Reset()`, and `_ = dictation.WarmUpAsync(CancellationToken.None)` fire-and-forget (it never throws).
  - A `MicrophoneException` from `Start` → Warning (text below), `State = Idle`, `gesture.Reset()`.
- **While listening.** `recorder.BlockCaptured` is marshalled through `dispatcher.Post`. It sets `Level = AudioMath.LevelOf(rms)` and feeds `SilentMicWatch.Step(rms, blockDuration)`, taking `blockDuration` as 50 ms. A `SignalEvent.Silent` adds the Warning "No sound from {mic name}. Check that it isn't muted." once per recording.
- **Stop.**
  - `recorder.Stop()`, then `State = Transcribing` and `Caption = "Transcribing…"`.
  - If `clip.Length < 500 ms`, add nothing and go back to Idle. `DictationOptions.MinimumClip` is 500 ms; use the constant 500 ms here as well.
  - Otherwise, if `!models.IsPresent`, first download:
    - Add one Note "Downloading the speech model (1.6 GB)… 0%" and update its `Text` in place from the `IProgress<double>` ("… 42%"), throttled to whole percents.
    - When done, set the text to "Speech model downloaded." and continue.
    - On download failure: Warning "The speech model could not be downloaded: {message}. Press the mic to try again." and back to Idle.
  - Then `dictation.TranscribeAsync(clip.Samples16k, await vocabulary.GetAsync(ct), live: false, ct)`:
    - A non-empty `Text` → add a `You` entry with the trimmed text.
    - Empty text → nothing.
    - `DictationModelLoadException` → Warning "The speech model could not be loaded: {message}".
  - Always end in `State = Idle`, `Level = 0` and `Caption` back to the idle hint "Hold Ctrl+Alt+Space or the mic button to talk."
- **Capture failure.** `recorder.Failed` → through `dispatcher.Post`: the Warning for its kind, `recorder.Stop()` (discard the clip), `State = Idle`, `gesture.Reset()`, then `RefreshMicrophones()`.
- **Warning texts by kind.**
  - Denied: "Windows is blocking microphone access. Turn on Settings → Privacy & security → Microphone → Let desktop apps access your microphone."
  - Missing: "{mic name} is not available any more."
  - Unavailable: "{mic name} could not be opened. Another app may be using it exclusively."
- **Device changes.** `catalog.DevicesChanged` → `dispatcher.Post(RefreshMicrophones)`. `RefreshMicrophones` replaces `Microphones` with `catalog.List()` and applies `MicrophoneChoice.Resolve(list, SelectedMicrophone, catalog.Default())`:
  - `Relocated` → select the relocated device silently.
  - `FellBackToDefault` → select the default and add the Note "{old name} is gone. Using {new name}."
  - `NoDevices` → `SelectedMicrophone = null`.
- **Typed input.** `SubmitTyped` with non-blank `TypedText` → a `You` entry, then `TypedText = ""`.
- **Collapse.** `TogglePanel` flips `IsOpen`. Recording is unaffected by collapsing.

- [ ] **Step 1: Write the failing `PushToTalkGesture` tests.** Use `FakeTimeProvider` from `Microsoft.Extensions.Time.Testing`.

```csharp
namespace CodeSwitchX.UI.Tests.Raven;

using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Time.Testing;

public sealed class PushToTalkGestureTests
{
    private readonly FakeTimeProvider _time = new();
    private PushToTalkGesture NewGesture() => new(_time);

    [Fact]
    public void A_hold_starts_on_press_and_stops_on_release()
    {
        var g = NewGesture();
        g.Press().ShouldBe(PushToTalkAction.Start);
        _time.Advance(TimeSpan.FromMilliseconds(800));
        g.Release().ShouldBe(PushToTalkAction.Stop);
    }

    [Fact]
    public void A_quick_tap_latches_and_the_next_press_stops()
    {
        var g = NewGesture();
        g.Press().ShouldBe(PushToTalkAction.Start);
        _time.Advance(TimeSpan.FromMilliseconds(120));
        g.Release().ShouldBe(PushToTalkAction.None);
        _time.Advance(TimeSpan.FromSeconds(5));
        g.Press().ShouldBe(PushToTalkAction.Stop);
        g.Release().ShouldBe(PushToTalkAction.None);
    }

    [Fact]
    public void Autorepeat_presses_while_held_do_nothing()
    {
        var g = NewGesture();
        g.Press().ShouldBe(PushToTalkAction.Start);
        g.Press().ShouldBe(PushToTalkAction.None);
        g.Press().ShouldBe(PushToTalkAction.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        g.Release().ShouldBe(PushToTalkAction.Stop);
    }

    [Fact]
    public void Exactly_the_threshold_counts_as_a_hold()
    {
        var g = NewGesture();
        g.Press();
        _time.Advance(PushToTalkGesture.HoldThreshold);
        g.Release().ShouldBe(PushToTalkAction.Stop);
    }

    [Fact]
    public void A_release_without_a_press_does_nothing()
    {
        NewGesture().Release().ShouldBe(PushToTalkAction.None);
    }

    [Fact]
    public void Reset_forgets_a_latch()
    {
        var g = NewGesture();
        g.Press();
        g.Release();
        g.Reset();
        g.Press().ShouldBe(PushToTalkAction.Start);
    }
}
```

- [ ] **Step 2: Implement `PushToTalkGesture`** to pass. The UI.Tests csproj already references `Microsoft.Extensions.TimeProvider.Testing`.

- [ ] **Step 3: Write the failing `RavenPanelViewModelTests`.**
  - Fakes: `Substitute.For<IMicrophoneCatalog>()`, `IMicrophoneRecorder`, `IDictationService`, `IWhisperModelStore` and `IDictationVocabularyProvider`. Use the existing `ImmediateDispatcher` from `tests/CodeSwitchX.UI.Tests` (it runs `Post` synchronously) and `FakeTimeProvider`.
  - Raise recorder events with NSubstitute: `_recorder.BlockCaptured += Raise.Event<EventHandler<float>>(_recorder, 0.1f)`.
  - Write one test per behavior rule above. These names are required (Review Focus pins them):
    - `A_hold_records_and_the_transcript_appears_as_my_turn`: `Stop()` returns a 2 s clip; `TranscribeAsync` returns `new DictationResult("Hallo Raven, open Diffusion Nexus", 2 s)`. The log ends with a You entry of that text, and State is Idle.
    - `A_clip_shorter_than_half_a_second_is_dropped` (`TranscribeAsync` is not received).
    - `A_missing_model_is_downloaded_then_the_clip_is_transcribed`: `IsPresent` is false, and `DownloadAsync` reports 0.5 and completes. There is a Note whose final text is "Speech model downloaded.", followed by the You entry.
    - `A_failed_download_is_reported_and_retried_next_time`: the first `DownloadAsync` throws; there is a Warning and the state is Idle. On the second press/release, `DownloadAsync` is received again.
    - `A_denied_microphone_explains_the_privacy_setting`: `Start` throws `MicrophoneException(Denied)`. The Warning contains "Privacy & security".
    - `A_microphone_lost_while_recording_returns_to_idle_and_says_so`: `Failed` is raised while Listening, with kind Missing. The Warning contains the mic name, the state is Idle, and a later press starts fresh (the gesture was reset).
    - `A_silent_microphone_is_warned_about_once`: blocks of `0f` totalling 3 s produce exactly one Warning with "No sound".
    - `The_level_follows_the_microphone_while_listening`.
    - `Pressing_while_transcribing_does_nothing`: `TranscribeAsync` returns a `TaskCompletionSource` task that is still pending.
    - `An_unplugged_selected_microphone_falls_back_to_the_default_with_a_note`.
    - `A_microphone_with_a_new_id_is_selected_again_without_a_note`.
    - `No_microphone_at_all_warns_on_press`.
    - `Typed_text_becomes_my_turn_and_the_box_clears`.
    - `Collapsing_the_panel_does_not_stop_a_recording`.

- [ ] **Step 4: Implement `RavenLogEntry`, `RavenState` and `RavenPanelViewModel`** until the tests pass.
  - Subscribe to the recorder and catalog events in the constructor.
  - `RefreshMicrophones()` is called by the shell after settings are loaded (Task 5), not in the constructor.
  - Add log entries only on the UI thread. Every event handler goes through `_dispatcher.Post`.
  - Log exceptions with `_logger.LogWarning`.
  - Use one `CancellationToken.None` for transcription. Cancelling mid-transcription is not a feature of this issue.

- [ ] **Step 5: Write the failing `WorkspaceVocabularyProviderTests`, then implement it.**
  - Constructor: `WorkspaceVocabularyProvider(IWorkspaceStore store, Func<string, IReadOnlyList<WorkspaceFolder>?> foldersOf)`. Production passes `WorkspaceProbe.FoldersOf`.
  - `GetAsync` returns a `DictationVocabulary` whose words are each workspace `Name` plus, for workspaces with a `WorkspaceFile`, each folder `Label`. The list is distinct and case-insensitive, in store order, with no corrections.
  - Test: two workspaces, one plain (`ContentAutomatorX`) and one multi-folder (`Diffusion-Full` with folders `DiffusionNexus.Installer.SDK` and `DiffusionNexus`) → words `["ContentAutomatorX", "Diffusion-Full", "DiffusionNexus.Installer.SDK", "DiffusionNexus"]`.
  - Test: `foldersOf` returns null (unreadable) → only the workspace name.
  - The file lookup runs off the UI thread: wrap the loop in `Task.Run`.

- [ ] **Step 6: Run the tests.** `dotnet build tests/CodeSwitchX.UI.Tests`, then run the UI.Tests exe with `--filter-class "CodeSwitchX.UI.Tests.Raven.*"`. Expected: all pass. Run `dotnet build CodeSwitchX.slnx`. Expected: 0 warnings.

- [ ] **Step 7: Commit.**

```bash
git add -A src/CodeSwitchX.UI/Raven src/CodeSwitchX.UI/CodeSwitchX.UI.csproj tests/CodeSwitchX.UI.Tests/Raven
git commit -m "feat: Raven panel view model: push-to-talk, dictation into the log, mic fallback and warnings (#69)"
```

---

### Task 5: Panel view, orb, main window column, hotkeys, settings and DI

**Files:**
- Create: `src/CodeSwitchX.UI/Raven/RavenPanelView.xaml`, `RavenPanelView.xaml.cs`, `RavenOrb.cs`
- Modify:
  - `src/CodeSwitchX.UI/MainWindow.xaml`
  - `src/CodeSwitchX.UI/Shell/ShellViewModel.cs`
  - `src/CodeSwitchX.UI/Settings/SettingKeys.cs` and `SettingsViewModel.cs`
  - `src/CodeSwitchX.UI/Infrastructure/HotkeyService.cs`
  - `src/CodeSwitchX.UI/App.xaml.cs`
  - `src/CodeSwitchX.Hosting/NativeMethods.txt` (add `GetAsyncKeyState` if the hotkey code needs CsWin32 for it; a plain `[LibraryImport]` in the UI project is fine too)
- Test:
  - `tests/CodeSwitchX.UI.Tests/Settings/SettingsViewModelTests.cs`
  - `tests/CodeSwitchX.UI.Tests/Shell/ShellViewModelTests.cs` and the shared `ShellTestHarness` (it constructs `ShellViewModel`; add the new constructor argument)
  - `tests/CodeSwitchX.UI.Tests/Infrastructure/HotkeyServiceTests.cs`
  - the `AppHostTests` (container resolves)

**Interfaces:**
- Consumes: `RavenPanelViewModel`, `RavenState`, `RavenLogEntry`, `RavenLogKind` (Task 4); `AddCodeSwitchXVoice` (Task 2); `WorkspaceVocabularyProvider` (Task 4).
- Produces:
  - `SettingKeys.RavenPanelOpen = "raven.panelOpen"` (bool) and `SettingKeys.RavenMicrophone = "raven.microphone"` (`MicrophoneDevice?`, stored as JSON `{id, name}`).
  - `SettingsViewModel.RavenPanelOpen` (bool, default true) and `SettingsViewModel.RavenMicrophone` (`MicrophoneDevice?`), loaded in `LoadAsync` and saved through the existing `Persist` pattern, exactly like `TileScale`.
  - `ShellViewModel.Raven` (`RavenPanelViewModel`), a new constructor parameter.
  - `HotkeyService`: new bindings `PushToTalk` (id 21, Control|Shift|NoRepeat, VK_SPACE 0x20, label "Push to talk") and `ToggleRaven` (id 22, Control|Alt, 'J' 0x4A, label "Collapse or expand Raven"). `Attach` returns or exposes the list of bindings that failed to register: `public IReadOnlyList<HotkeyBinding> FailedBindings { get; }`.

- [ ] **Step 1: Settings, test first.** Mirror `The_tile_size_is_loaded_and_saved` in `SettingsViewModelTests`:
  - `The_raven_panel_state_is_loaded_and_saved`: the store returns `false` for `RavenPanelOpen`; after load it is false; set it true, flush, and `SetAsync(SettingKeys.RavenPanelOpen, true, …)` is received.
  - `The_raven_microphone_is_loaded_and_saved`: same shape with `new MicrophoneDevice("{id}", "Headset")`.
  - `A_missing_raven_panel_setting_means_open`: the store returns null → true.

  Implement the settings in `SettingKeys` and `SettingsViewModel`, following the TileScale code. Reading must tolerate bad JSON, as `LoadTileScaleAsync` does.

- [ ] **Step 2: Shell wiring, test first.** Add `RavenPanelViewModel raven` to the `ShellViewModel` constructor and expose `public RavenPanelViewModel Raven { get; }`. Update `ShellTestHarness` to build a `RavenPanelViewModel` from NSubstitute fakes and `ImmediateDispatcher`. In `InitializeAsync`, after `Settings` load (next to the `Yard.TileScale` sync):
  - `Raven.IsOpen = Settings.RavenPanelOpen;`
  - `Raven.SelectedMicrophone = Settings.RavenMicrophone;`
  - `Raven.RefreshMicrophones();`
  - then copy `Raven.IsOpen`/`Raven.SelectedMicrophone` changes back to `Settings` through `Raven.PropertyChanged`, as the TileScale sync does.

  Tests in `ShellViewModelTests`:
  - `The_raven_panel_starts_in_its_stored_state`
  - `Collapsing_raven_is_saved`
  - `A_stored_microphone_that_is_still_plugged_in_is_selected_at_startup`

- [ ] **Step 3: Hotkeys, test first.**
  - In `HotkeyServiceTests`, assert the two new bindings exist with the exact modifiers and keys above. The existing AltGr rule test must still pass (Ctrl+Alt+J is not Ctrl+Alt+digit).
  - In `HotkeyService.WndProc`:
    - id 21 → `shell.Raven.PressMic()`. Then start a `DispatcherTimer` (30 ms) that polls `GetAsyncKeyState(VK_SPACE)` and `GetAsyncKeyState(VK_CONTROL)`. When either is up, stop the timer and call `_ = shell.Raven.ReleaseMicAsync()`. `NoRepeat` stops autorepeat `WM_HOTKEY`; the gesture ignores repeats anyway.
    - id 22 → `shell.Raven.TogglePanelCommand.Execute(null)`.
    - Neither of these brings the window to the front: talking must work while VS Code has focus.
  - `Attach` collects bindings whose `Register` failed into `FailedBindings`. In `MainWindow.OnSourceInitialized`, after `_hotkeys.Attach`, add `shell.Raven.Note($"{b.Label} ({keys}) is taken by another app. Use the mic button instead.")` for each failed binding in 21/22.

- [ ] **Step 4: `RavenOrb` control.** A `FrameworkElement` with dependency properties `State` (`RavenState`), `Level` (double 0..1) and `IsAnimating` (bool). It draws in `OnRender`:
  - a radial glow in `#62D0E8`;
  - a closed wave ring (radius = base + breathe + noise × Level) from the sum of three sines, as in the concept page's canvas code;
  - rotating arcs while `Transcribing`;
  - a gradient core.

  Animation hooks `CompositionTarget.Rendering` only while `IsAnimating && IsVisible`, and unhooks otherwise. When `SystemParameters.ClientAreaAnimation` is false (reduced motion), it draws one static frame. The mic button turns red `#EF4444` while Listening. The orb has no unit test; the controller checks it on screen.

- [ ] **Step 5: `RavenPanelView`.** A UserControl bound to `RavenPanelViewModel`.
  - Open layout (320 px): the header row reads "Raven" in 16 px semibold, the state text (Idle / Listening / Transcribing, `#62D0E8`, 11 px uppercase) and a chevron button (`&#xE76B;`, Segoe Fluent Icons) bound to `TogglePanelCommand`, tooltip "Collapse (Ctrl+Alt+J)".
  - Below it, the `RavenOrb` at 160×160, then the `Caption`.
  - Then a row with the round 52 px mic button next to a stack: a "Mic" `ComboBox` over `Microphones` (display `Name`) bound to `SelectedMicrophone`, and a 4 px level bar bound to `Level`.
  - The log fills the rest: a `ScrollViewer` + `ItemsControl` over `Log`.
    - A `You` entry is a rounded box (`#1F2937` background) with the time and "You" label.
    - A `Note` is dim `#9CA3AF` text.
    - A `Warning` is amber `#F59E0B` text with a warning glyph.
    - Scroll to the end on `Log.CollectionChanged` (code-behind).
  - At the bottom, a `TextBox` bound to `TypedText` (UpdateSourceTrigger=PropertyChanged) with placeholder "Type instead of talking…", and Enter → `SubmitTypedCommand`.
  - Mic button: `PreviewMouseLeftButtonDown` → `PressMic()` and `PreviewMouseLeftButtonUp` → `ReleaseMicAsync()` in code-behind, with mouse capture so a drag off the button still releases. Space/Enter on the focused button toggles, as a tap would.
  - Collapsed layout (58 px): only a 34 px mini orb (the same `RavenOrb`), the mic button (38 px) and an expand chevron. Show one layout or the other with `IsOpen`.
  - Background `#111827` with a right border `#1F2937`.

- [ ] **Step 6: MainWindow column.** Change the root `Grid` of `MainWindow.xaml` to two columns and two rows:
  - column 0: `Width="Auto"`, holding `RavenPanelView` in Row 0 with `Width` 320 or 58 from `Raven.IsOpen` (a small converter or a style DataTrigger);
  - column 1: `*`, holding the Yard, Cab and Settings views in Row 0;
  - the `PerformanceBarView` in Row 1 with `Grid.ColumnSpan="2"`.

  The Cab's `HostArea.SizeChanged` already republishes the dock rect when the column width changes, so docked VS Code follows. In `CabView.xaml.cs`, also call `Publish()` from `HostArea.LayoutUpdated`. `Publish` ignores unchanged rects, so a move without a size change is caught too.

- [ ] **Step 7: DI.** In `App.ConfigureServices`:
  - `services.AddCodeSwitchXVoice(paths.ModelsDirectory);`
  - `services.AddSingleton<IDictationVocabularyProvider>(sp => new WorkspaceVocabularyProvider(sp.GetRequiredService<IWorkspaceStore>(), WorkspaceProbe.FoldersOf));`
  - `services.AddSingleton<RavenPanelViewModel>();`

  Add a project reference from UI to Voice if Task 4 didn't. Make sure the native Whisper runtimes (`runtimes\vulkan\…`, `runtimes\win-x64\…`) land next to `CodeSwitchX.exe` in the build output. Check `bin/Debug/net10.0-windows/runtimes` after the build. `AppHostTests` (ValidateOnBuild) must pass.

- [ ] **Step 8: Run everything.** `dotnet test --solution CodeSwitchX.slnx`. Expected: all tests pass, 0 warnings.

- [ ] **Step 9: Commit.**

```bash
git add -A src tests
git commit -m "feat: Raven panel in the main window with orb, mic picker, log, hotkeys and saved state (#69)"
```

---

## After the tasks (controller)

- Copy `E:\Repos\ContentAutomatorX\src\ContentAutomatorX.Web\data\models\ggml-large-v3-turbo.bin` into the models folder of a scratch data root, and run the explicit `SpokenAudioTranscriptionTests` once.
- The on-screen check uses the drivers in memory (`running-the-app-from-claude`). The user runs the stable build, so ask before closing it. Check:
  - the panel open, collapsed and expanded again;
  - the Cab with docked VS Code resizing when the panel toggles;
  - the mic list;
  - a typed turn in the log.

  Speaking into the mic is for the user to check.
- Screenshots go in `docs/review/screenshots/issue69-*.png` and are linked from the PR.
