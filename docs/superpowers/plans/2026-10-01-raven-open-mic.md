# Raven's Open mic Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A second mic mode, Open mic: Raven listens all the time, ends the user's turn when Silero VAD hears a pause and Smart Turn v3 calls it complete, stops speaking when talked over, and shows a light ring on its orb while it waits.

**Architecture:** A new `CodeSwitchX.Voice/Listening` folder holds the pieces: a continuous WASAPI stream at 16 kHz, the two ONNX models behind small interfaces, a pure `TurnDetector` state machine, and `OpenMicListener`, which runs the detector on its own worker thread behind `IOpenMic`. The panel (`RavenPanelViewModel`) gets a `MicMode`; in Open mic the listener's `SpeechStarted` does what a press does and its `TurnEnded` clip goes into the existing transcription queue, so #73's floor rules apply unchanged.

**Tech Stack:** .NET 10 WPF, `Microsoft.ML.OnnxRuntime` 1.30.0 (CPU), NAudio WASAPI, xunit v3 + Shouldly + NSubstitute, `FakeTimeProvider`; uv + numpy for one reference fixture.

**Spec:** `docs/superpowers/specs/2026-10-01-raven-open-mic-design.md`

## Global Constraints

- Branch `feature/raven-open-mic`; one PR for #75. Commit and push after each task. Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; the PR body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- Push to talk stays the default; an unknown stored mode reads as Push to talk.
- Silero VAD v6.2.3 `silero_vad.onnx`: 2,327,524 bytes, SHA-256 `1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3`, from `https://raw.githubusercontent.com/snakers4/silero-vad/v6.2.3/src/silero_vad/data/silero_vad.onnx`. Inputs `input` float[1, 576], `state` float[2, 1, 128], `sr` int64 scalar 16000; outputs `output` [1, 1], `stateN`.
- Smart Turn `smart-turn-v3.2-cpu.onnx`: 8,679,182 bytes, SHA-256 `2bb026316b14a660486a75b1733cd3fbab8c2fd0314dc9af7be49f8cca967e4f`, from `https://huggingface.co/pipecat-ai/smart-turn-v3/resolve/f766f81d3cfdf7737ac64aad813d91bbfd56bf93/smart-turn-v3.2-cpu.onnx`. Input `input_features` float[1, 80, 800]; output `logits` [1, 1], already a probability; above 0.5 is complete. Measured 15 ms per call on this PC's CPU.
- Timings (spec): speech starts at probability ≥ 0.5 and continues at ≥ 0.35; `SpeechStarted` after 0.5 s of speech; a pause is 0.2 s of silence; the turn ends anyway after 3 s of silence or at 120 s; 0.5 s pre-roll; Smart Turn sees the last 8 s, zero-padded at the front.
- Models live in `%LOCALAPPDATA%\CodeSwitchX\models\listening\`.
- Settings keys: `raven.micMode` (`"PushToTalk"` / `"OpenMic"`), `raven.bargeIn` (bool, default true).
- Captions: `"Open mic"` (waiting), `"Open mic paused"`, `"Listening…"` (speech). Mic button in Open mic: tooltip and accessible name `"Pause Open mic"` / `"Resume Open mic"`.
- Light ring: 36 dots on radius `Base + 44`, dim alpha 0.16, glint 0.9 rad/s with falloff `exp(-2.2·d²)`, flicker `0.5 + 0.5·sin(i·2.7 + t·9)`; paused: still, alpha 0.08, no glint. Waiting animates in a background window at the idle frame rate.
- ONNX never runs on the capture thread or the UI thread.
- Tests run through the xunit exe: `dotnet build tests/<Project>` then `tests/<Project>/bin/Debug/net10.0-windows/<Project>.exe --filter-class "<Full.Class.Name>"` (or `--filter-method "*Name*"`). `-class`/`-method` only print help.
- Leave the RAIVEN app untouched. Don't report dollar costs.

## Review Focus

1. **The app starts with Open mic saved** → the listener starts once the microphones are listed (and downloads the models first if they are gone), without a press. Test in Task 8.
2. **The user picks another microphone while Open mic listens** (or the chosen one falls back to the default) → the listener restarts on the new device; nothing is left listening on the old one. Test in Task 8.
3. **Pause, or a switch to Push to talk, in the middle of a spoken turn** → the half turn is dropped, never transcribed later, and the caption leaves "Listening…". Test in Task 8.
4. **The microphone dies while Open mic runs** → one warning, the mode stays Open mic and shows paused; the next press starts it again. Test in Task 8.
5. **A held push-to-talk recording when the user switches to Open mic** → the recording is stopped and transcribed as a normal release would, and its late key release changes nothing. Test in Task 8.

---

## File Structure

Created:
- `src/CodeSwitchX.Voice/Listening/WhisperFeatures.cs` — Whisper log-mel features (port of Pipecat's numpy code).
- `src/CodeSwitchX.Voice/Listening/ListeningModelStore.cs` — the two model files: pinned URL, length, SHA-256, download.
- `src/CodeSwitchX.Voice/Listening/ListeningModels.cs` — `IVoiceActivity`, `ITurnEnd`, `SileroVad`, `SmartTurn`, `ListeningModelException`.
- `src/CodeSwitchX.Voice/Listening/TurnDetector.cs` — the state machine.
- `src/CodeSwitchX.Voice/Listening/StreamResampler.cs` — block-by-block resampling to 16 kHz.
- `src/CodeSwitchX.Voice/Listening/IMicrophoneStream.cs`, `WasapiMicrophoneStream.cs`, `FileMicrophoneStream.cs` — continuous capture, real and from a file.
- `src/CodeSwitchX.Voice/Listening/OpenMicListener.cs` — `IOpenMic` and its implementation.
- `src/CodeSwitchX.UI/Raven/MicMode.cs`.
- Tests: `tests/CodeSwitchX.Voice.Tests/Listening/*` (one file per class above), `tests/CodeSwitchX.Voice.Tests/Listening/Fixtures/warmup-features.bin`, `tests/CodeSwitchX.Voice.Tests/Listening/make_whisper_features.py`, `tests/CodeSwitchX.UI.Tests/Raven/RavenPanelViewModelTests.OpenMic.cs`, `tests/CodeSwitchX.UI.Tests/Raven/FakeOpenMic.cs`.

Modified:
- `Directory.Packages.props`, `src/CodeSwitchX.Voice/CodeSwitchX.Voice.csproj`, `tests/CodeSwitchX.Voice.Tests/CodeSwitchX.Voice.Tests.csproj`
- `src/CodeSwitchX.Voice/VoiceServiceCollectionExtensions.cs`
- `src/CodeSwitchX.UI/Raven/RavenState.cs`, `RavenOrb.cs`, `RavenPanelViewModel.cs`, `RavenPanelView.xaml`
- `src/CodeSwitchX.UI/Settings/SettingKeys.cs`, `SettingsViewModel.cs`, `SettingsView.xaml`, `src/CodeSwitchX.UI/Shell/ShellViewModel.cs`, `src/CodeSwitchX.UI/App.xaml.cs`
- `tests/CodeSwitchX.UI.Tests/Shell/ShellViewModelTests.cs`
- `docs/superpowers/specs/2026-10-01-raven-open-mic-design.md` (the pause detail, Task 6)

---

### Task 1: ONNX Runtime and the Whisper features

**Files:**
- Modify: `Directory.Packages.props`, `src/CodeSwitchX.Voice/CodeSwitchX.Voice.csproj`, `tests/CodeSwitchX.Voice.Tests/CodeSwitchX.Voice.Tests.csproj`
- Create: `src/CodeSwitchX.Voice/Listening/WhisperFeatures.cs`
- Create: `tests/CodeSwitchX.Voice.Tests/Listening/make_whisper_features.py`, `tests/CodeSwitchX.Voice.Tests/Listening/Fixtures/warmup-features.bin`
- Test: `tests/CodeSwitchX.Voice.Tests/Listening/WhisperFeaturesTests.cs`

**Interfaces:**
- Produces: `public static class WhisperFeatures { public const int Mels = 80; public const int Frames = 800; public const int Samples = 128_000; public static float[] LogMel(ReadOnlySpan<float> audio16k); }` — returns `Mels * Frames` floats, row-major `[mel, frame]`, from the last 8 s of the input, zero-padded at the front.

- [ ] **Step 1: Add the package**

In `Directory.Packages.props` after the `Microsoft.Extensions.TimeProvider.Testing` line:

```xml
    <PackageVersion Include="Microsoft.ML.OnnxRuntime" Version="1.30.0" />
```

In `src/CodeSwitchX.Voice/CodeSwitchX.Voice.csproj`, in the package `ItemGroup` after `Microsoft.Extensions.Options`:

```xml
    <!-- Open mic's turn detection: Silero VAD and Smart Turn v3, on the CPU (15 ms a call; see Listening/). -->
    <PackageReference Include="Microsoft.ML.OnnxRuntime" />
```

In `tests/CodeSwitchX.Voice.Tests/CodeSwitchX.Voice.Tests.csproj` add an item group copying fixtures:

```xml
  <ItemGroup>
    <None Include="Listening\Fixtures\**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: Write the fixture generator and make the fixture**

`tests/CodeSwitchX.Voice.Tests/Listening/make_whisper_features.py`:

```python
"""Makes Fixtures/warmup-features.bin: Pipecat's Smart Turn features of the warm-up sample, the reference the C# port
(WhisperFeatures) is tested against. Run from this folder: uv run --with numpy python make_whisper_features.py
Pipecat is pinned to the commit the port was written from."""
import importlib.util, pathlib, urllib.request
import numpy as np

COMMIT = "3b2dccd843c65d1632631ceac248a5128fb32242"
URL = f"https://raw.githubusercontent.com/pipecat-ai/pipecat/{COMMIT}/src/pipecat/audio/turn/smart_turn/_whisper_features.py"
here = pathlib.Path(__file__).parent
source = here / "_whisper_features.py"
urllib.request.urlretrieve(URL, source)
spec = importlib.util.spec_from_file_location("wf", source)
wf = importlib.util.module_from_spec(spec)
spec.loader.exec_module(wf)

pcm = (here / "../../../src/CodeSwitchX.Voice/Dictation/WarmUpSpeech.pcm").read_bytes()
audio = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
audio = np.pad(audio, (128000 - audio.size, 0))  # Smart Turn pads at the front (local_smart_turn_v3.py)
features = wf.compute_whisper_log_mel_features(audio, do_normalize=True)
assert features.shape == (80, 800)
(here / "Fixtures").mkdir(exist_ok=True)
(here / "Fixtures/warmup-features.bin").write_bytes(features.astype("<f4").tobytes())
source.unlink()
print("wrote", features.shape, features.min(), features.max())
```

Run: `cd tests/CodeSwitchX.Voice.Tests/Listening && uv run --with numpy python make_whisper_features.py`
Expected: `wrote (80, 800) …`, and `Fixtures/warmup-features.bin` is 256,000 bytes.

- [ ] **Step 3: Write the failing tests**

`tests/CodeSwitchX.Voice.Tests/Listening/WhisperFeaturesTests.cs`:

```csharp
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Listening;

namespace CodeSwitchX.Voice.Tests.Listening;

public sealed class WhisperFeaturesTests
{
    [Fact]
    public void The_features_of_the_warm_up_sample_match_Pipecats()
    {
        var expected = Fixture("warmup-features.bin");

        var features = WhisperFeatures.LogMel(WarmUpSpeech.Load());

        features.Length.ShouldBe(WhisperFeatures.Mels * WhisperFeatures.Frames);
        var worst = 0f;
        for (var i = 0; i < features.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(features[i] - expected[i]));
        }

        worst.ShouldBeLessThan(2e-3f, "the port must compute what Smart Turn was trained on");
    }

    [Fact]
    public void Only_the_last_eight_seconds_count()
    {
        var sample = WarmUpSpeech.Load();
        var longer = new float[WhisperFeatures.Samples + 16_000];
        sample.CopyTo(longer, longer.Length - sample.Length);

        WhisperFeatures.LogMel(longer).ShouldBe(WhisperFeatures.LogMel(sample));
    }

    [Fact]
    public void Silence_gives_finite_features()
    {
        WhisperFeatures.LogMel(new float[16_000]).ShouldAllBe(f => float.IsFinite(f));
    }

    private static float[] Fixture(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Listening", "Fixtures", name));
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
```

- [ ] **Step 4: Run them to see them fail**

Run: `dotnet build tests/CodeSwitchX.Voice.Tests` → Expected: build error `The name 'WhisperFeatures' does not exist`.

- [ ] **Step 5: Write the port**

`src/CodeSwitchX.Voice/Listening/WhisperFeatures.cs`:

```csharp
namespace CodeSwitchX.Voice.Listening;

/// <summary>
/// The Whisper log-mel features Smart Turn v3 reads: 80 mel bins by 800 frames of the last 8 s of 16 kHz audio, padded
/// with zeros at the front when shorter. A port of Pipecat's numpy code (pipecat/audio/turn/smart_turn/
/// _whisper_features.py at 3b2dccd, itself the math of transformers' WhisperFeatureExtractor): the waveform is
/// normalised to zero mean and unit variance, framed with a periodic Hann window (n_fft 400, hop 160, reflect-padded by
/// 200 at both ends), turned into a power spectrum, projected on Slaney mel filters, log10'd, the last frame dropped,
/// clamped to 8 under the maximum and mapped by (x + 4) / 4. Tested against Pipecat's output (WhisperFeaturesTests).
/// <para>
/// The 400-point transform is a plain DFT on precomputed tables: 800 frames of 201 bins is some 64 million
/// multiply-adds, tens of milliseconds, once per pause in the user's speech.
/// </para>
/// </summary>
public static class WhisperFeatures
{
    public const int Mels = 80;
    public const int Frames = 800;
    public const int Samples = 8 * Rate;

    private const int Rate = 16_000;
    private const int Fft = 400;
    private const int Hop = 160;
    private const int Bins = (Fft / 2) + 1;

    private static readonly double[] Window = HannWindow();
    private static readonly double[] Cos = Table(Math.Cos);
    private static readonly double[] Sin = Table(Math.Sin);
    private static readonly double[,] MelFilters = BuildMelFilters();

    public static float[] LogMel(ReadOnlySpan<float> audio16k)
    {
        // The last 8 s, zeros in front (local_smart_turn_v3.py truncate_audio_to_last_n_seconds).
        var x = new float[Samples];
        var take = Math.Min(audio16k.Length, Samples);
        audio16k[^take..].CopyTo(x.AsSpan(Samples - take));
        Normalize(x);

        var padded = new double[Samples + Fft];
        for (var i = 0; i < padded.Length; i++)
        {
            padded[i] = x[Reflect(i - (Fft / 2), Samples)];
        }

        var log = new double[Mels, Frames];
        var power = new double[Bins];
        var max = double.NegativeInfinity;
        for (var frame = 0; frame < Frames; frame++) // the reference makes 801 and drops the last
        {
            var start = frame * Hop;
            for (var k = 0; k < Bins; k++)
            {
                double re = 0, im = 0;
                for (var n = 0; n < Fft; n++)
                {
                    var v = padded[start + n] * Window[n];
                    var index = (k * n) % Fft;
                    re += v * Cos[index];
                    im -= v * Sin[index];
                }

                power[k] = (re * re) + (im * im);
            }

            for (var m = 0; m < Mels; m++)
            {
                double sum = 0;
                for (var k = 0; k < Bins; k++)
                {
                    sum += MelFilters[k, m] * power[k];
                }

                var value = Math.Log10(Math.Max(1e-10, sum));
                log[m, frame] = value;
                max = Math.Max(max, value);
            }
        }

        var features = new float[Mels * Frames];
        for (var m = 0; m < Mels; m++)
        {
            for (var frame = 0; frame < Frames; frame++)
            {
                features[(m * Frames) + frame] = (float)((Math.Max(log[m, frame], max - 8.0) + 4.0) / 4.0);
            }
        }

        return features;
    }

    /// <summary>Zero mean, unit variance (numpy's x.var() + 1e-7 under the root), as transformers' do_normalize.</summary>
    private static void Normalize(float[] x)
    {
        double mean = 0;
        foreach (var v in x)
        {
            mean += v;
        }

        mean /= x.Length;
        double variance = 0;
        foreach (var v in x)
        {
            variance += (v - mean) * (v - mean);
        }

        variance /= x.Length;
        var scale = 1.0 / Math.Sqrt(variance + 1e-7);
        for (var i = 0; i < x.Length; i++)
        {
            x[i] = (float)((x[i] - mean) * scale);
        }
    }

    /// <summary>numpy's "reflect" padding: the edge sample is not repeated.</summary>
    private static int Reflect(int i, int length) => i < 0 ? -i : i >= length ? (2 * (length - 1)) - i : i;

    private static double[] HannWindow()
    {
        var window = new double[Fft];
        for (var n = 0; n < Fft; n++)
        {
            window[n] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * n / Fft)); // periodic: np.hanning(401)[:-1]
        }

        return window;
    }

    private static double[] Table(Func<double, double> f)
    {
        var table = new double[Fft];
        for (var i = 0; i < Fft; i++)
        {
            table[i] = f(2 * Math.PI * i / Fft);
        }

        return table;
    }

    /// <summary>Slaney-scale triangular filters with Slaney area normalisation, [bin, mel] as the reference builds them.</summary>
    private static double[,] BuildMelFilters()
    {
        var melMin = HertzToMel(0);
        var melMax = HertzToMel(Rate / 2.0);
        var filterFreqs = new double[Mels + 2];
        for (var i = 0; i < filterFreqs.Length; i++)
        {
            filterFreqs[i] = MelToHertz(melMin + ((melMax - melMin) * i / (Mels + 1)));
        }

        var filters = new double[Bins, Mels];
        for (var k = 0; k < Bins; k++)
        {
            var fft = (Rate / 2.0) * k / (Bins - 1);
            for (var m = 0; m < Mels; m++)
            {
                var down = (fft - filterFreqs[m]) / (filterFreqs[m + 1] - filterFreqs[m]);
                var up = (filterFreqs[m + 2] - fft) / (filterFreqs[m + 2] - filterFreqs[m + 1]);
                var enorm = 2.0 / (filterFreqs[m + 2] - filterFreqs[m]);
                filters[k, m] = Math.Max(0, Math.Min(down, up)) * enorm;
            }
        }

        return filters;
    }

    private static double HertzToMel(double hz) =>
        hz >= 1000 ? 15.0 + (Math.Log(hz / 1000.0) * (27.0 / Math.Log(6.4))) : 3.0 * hz / 200.0;

    private static double MelToHertz(double mel) =>
        mel >= 15.0 ? 1000.0 * Math.Exp((Math.Log(6.4) / 27.0) * (mel - 15.0)) : 200.0 * mel / 3.0;
}
```

(`down` is the reference's `-slopes[:, :-2] / filter_diff[:-1]` = `(fft - f[m]) / (f[m+1] - f[m])`; `up` is `slopes[:, 2:] / filter_diff[1:]` = `(f[m+2] - fft) / (f[m+2] - f[m+1])`.)

- [ ] **Step 6: Run the tests**

Run: `dotnet build tests/CodeSwitchX.Voice.Tests && tests/CodeSwitchX.Voice.Tests/bin/Debug/net10.0-windows/CodeSwitchX.Voice.Tests.exe --filter-class "CodeSwitchX.Voice.Tests.Listening.WhisperFeaturesTests"`
Expected: 3 passed. If the first fails by more than 2e-3, compare one frame's power spectrum against numpy before touching the tolerance.

- [ ] **Step 7: Commit**

```bash
git add Directory.Packages.props src/CodeSwitchX.Voice tests/CodeSwitchX.Voice.Tests
git commit -m "feat: Whisper log-mel features for Smart Turn, ported from Pipecat (#75)"
git push
```

---

### Task 2: The model store

**Files:**
- Create: `src/CodeSwitchX.Voice/Listening/ListeningModelStore.cs`
- Test: `tests/CodeSwitchX.Voice.Tests/Listening/ListeningModelStoreTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ListeningModel(string FileName, Uri Source, long Length, string Sha256)`
  - `public sealed class ListeningModelStore(string folder, HttpClient http)` with `public static readonly ListeningModel Silero, SmartTurn;`, `public string PathOf(ListeningModel model)`, `public bool IsPresent { get; }`, `public Task DownloadAsync(IProgress<double>? progress, CancellationToken ct)`, `public void Forget(ListeningModel model)`.

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.Voice.Tests/Listening/ListeningModelStoreTests.cs`:

```csharp
using System.Net;
using System.Security.Cryptography;
using CodeSwitchX.Voice.Listening;

namespace CodeSwitchX.Voice.Tests.Listening;

public sealed class ListeningModelStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "csx-listening-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task Both_models_are_downloaded_checked_and_put_in_place()
    {
        var a = new byte[] { 1, 2, 3 };
        var b = new byte[] { 4, 5, 6, 7 };
        var store = Store(new Files { ["a.onnx"] = a, ["b.onnx"] = b }, Model("a.onnx", a), Model("b.onnx", b));
        var reported = new List<double>();

        await store.DownloadAsync(new Progress(reported), TestContext.Current.CancellationToken);

        store.IsPresent.ShouldBeTrue();
        File.ReadAllBytes(Path.Combine(_folder, "a.onnx")).ShouldBe(a);
        reported[^1].ShouldBe(1.0);
        Directory.GetFiles(_folder, "*.partial").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_download_whose_hash_does_not_match_leaves_nothing_behind()
    {
        var good = new byte[] { 1, 2, 3 };
        var store = Store(new Files { ["a.onnx"] = [9, 9, 9] }, Model("a.onnx", good));

        var error = await Should.ThrowAsync<IOException>(() => store.DownloadAsync(null, TestContext.Current.CancellationToken));

        error.Message.ShouldContain("a.onnx");
        store.IsPresent.ShouldBeFalse();
        Directory.GetFiles(_folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_model_already_in_place_is_not_downloaded_again_and_a_forgotten_one_is()
    {
        var a = new byte[] { 1, 2, 3 };
        var files = new Files { ["a.onnx"] = a };
        var model = Model("a.onnx", a);
        var store = Store(files, model);
        await store.DownloadAsync(null, TestContext.Current.CancellationToken);

        await store.DownloadAsync(null, TestContext.Current.CancellationToken);
        files.Requests.ShouldBe(1);

        store.Forget(model);
        store.IsPresent.ShouldBeFalse();
        await store.DownloadAsync(null, TestContext.Current.CancellationToken);
        files.Requests.ShouldBe(2);
    }

    [Fact]
    public void The_pinned_models_are_the_ones_the_spec_names()
    {
        ListeningModelStore.Silero.Length.ShouldBe(2_327_524);
        ListeningModelStore.Silero.Sha256.ShouldBe("1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3");
        ListeningModelStore.SmartTurn.Length.ShouldBe(8_679_182);
        ListeningModelStore.SmartTurn.Sha256.ShouldBe("2bb026316b14a660486a75b1733cd3fbab8c2fd0314dc9af7be49f8cca967e4f");
    }

    private ListeningModelStore Store(Files files, params ListeningModel[] models) =>
        new(_folder, new HttpClient(files)) { Models = models };

    private static ListeningModel Model(string name, byte[] content) =>
        new(name, new Uri("https://models.test/" + name), content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));

    private sealed class Progress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }

    private sealed class Files : HttpMessageHandler, System.Collections.IEnumerable
    {
        private readonly Dictionary<string, byte[]> _files = [];

        public int Requests { get; private set; }

        public byte[] this[string name] { set => _files[name] = value; }

        public System.Collections.IEnumerator GetEnumerator() => _files.GetEnumerator();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(_files.TryGetValue(request.RequestUri!.Segments[^1], out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/CodeSwitchX.Voice.Tests` → Expected: build error, `ListeningModelStore` missing.

- [ ] **Step 3: Write the store**

`src/CodeSwitchX.Voice/Listening/ListeningModelStore.cs`:

```csharp
using System.Security.Cryptography;

namespace CodeSwitchX.Voice.Listening;

/// <summary>One model file Open mic needs: where it comes from, and what it must be once here.</summary>
public sealed record ListeningModel(string FileName, Uri Source, long Length, string Sha256);

/// <summary>
/// Open mic's two models, in a folder of their own beside the Whisper model. Each is pinned (a release tag, a Hugging
/// Face commit) and checked by length and SHA-256: a download goes to a ".partial" file, and only one that matches is
/// moved into place, so a cut or changed file is never loaded. Some 11 MB together, fetched the first time the user
/// switches to Open mic.
/// </summary>
public sealed class ListeningModelStore(string folder, HttpClient http)
{
    /// <summary>Silero VAD v6.2.3 (MIT): is this 32 ms of audio speech?</summary>
    public static readonly ListeningModel Silero = new("silero_vad.onnx",
        new Uri("https://raw.githubusercontent.com/snakers4/silero-vad/v6.2.3/src/silero_vad/data/silero_vad.onnx"),
        2_327_524, "1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3");

    /// <summary>Pipecat's Smart Turn v3.2, int8 for the CPU (BSD-2-Clause): has the user finished their turn?</summary>
    public static readonly ListeningModel SmartTurn = new("smart-turn-v3.2-cpu.onnx",
        new Uri("https://huggingface.co/pipecat-ai/smart-turn-v3/resolve/f766f81d3cfdf7737ac64aad813d91bbfd56bf93/smart-turn-v3.2-cpu.onnx"),
        8_679_182, "2bb026316b14a660486a75b1733cd3fbab8c2fd0314dc9af7be49f8cca967e4f");

    /// <summary>The models this store keeps; the tests hand in their own.</summary>
    internal IReadOnlyList<ListeningModel> Models { get; init; } = [Silero, SmartTurn];

    public string PathOf(ListeningModel model) => Path.Combine(folder, model.FileName);

    /// <summary>Every model is in place at its pinned length (the hash was checked when it arrived).</summary>
    public bool IsPresent => Models.All(Present);

    /// <summary>Downloads the models not yet in place. Progress is 0..1 over all their bytes. Throws on a failed or
    /// mismatching download, which leaves no file behind.</summary>
    public async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var missing = Models.Where(m => !Present(m)).ToList();
        var total = (double)Math.Max(1, missing.Sum(m => m.Length));
        long done = 0;
        foreach (var model in missing)
        {
            var partial = PathOf(model) + ".partial";
            try
            {
                using var response = await http.GetAsync(model.Source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long read = 0;
                var target = File.Create(partial);
                await using (target.ConfigureAwait(false))
                {
                    var buffer = new byte[81_920];
                    int n;
                    while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, n);
                        read += n;
                        progress?.Report(Math.Min((done + read) / total, 0.99));
                    }
                }

                var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (read != model.Length || sha != model.Sha256)
                {
                    throw new IOException($"{model.FileName} did not arrive whole ({read:N0} of {model.Length:N0} bytes, or another file)");
                }

                File.Move(partial, PathOf(model), overwrite: true);
                done += model.Length;
            }
            finally
            {
                if (File.Exists(partial))
                {
                    File.Delete(partial);
                }
            }
        }

        progress?.Report(1.0);
    }

    /// <summary>Deletes a model that would not load, so the next switch to Open mic downloads it again.</summary>
    public void Forget(ListeningModel model)
    {
        if (File.Exists(PathOf(model)))
        {
            File.Delete(PathOf(model));
        }
    }

    private bool Present(ListeningModel model) => new FileInfo(PathOf(model)) is { Exists: true } file && file.Length == model.Length;
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet build tests/CodeSwitchX.Voice.Tests && tests/CodeSwitchX.Voice.Tests/bin/Debug/net10.0-windows/CodeSwitchX.Voice.Tests.exe --filter-class "CodeSwitchX.Voice.Tests.Listening.ListeningModelStoreTests"`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/CodeSwitchX.Voice/Listening/ListeningModelStore.cs tests/CodeSwitchX.Voice.Tests/Listening/ListeningModelStoreTests.cs
git commit -m "feat: Open mic's models are downloaded pinned and checked by hash (#75)"
git push
```

---

### Task 3: Silero VAD and Smart Turn on ONNX Runtime

**Files:**
- Create: `src/CodeSwitchX.Voice/Listening/ListeningModels.cs`
- Test: `tests/CodeSwitchX.Voice.Tests/Listening/ListeningModelsTests.cs`

**Interfaces:**
- Consumes: `WhisperFeatures.LogMel` (Task 1), `ListeningModelStore` (Task 2).
- Produces:
  - `public interface IVoiceActivity : IDisposable { float Step(ReadOnlySpan<float> frame); void Reset(); }` — `frame` is exactly `SileroVad.FrameSamples` (512) samples.
  - `public interface ITurnEnd : IDisposable { double Complete(ReadOnlySpan<float> turn16k); }`
  - `public sealed class SileroVad(string modelPath) : IVoiceActivity` with `public const int FrameSamples = 512;`
  - `public sealed class SmartTurn(string modelPath) : ITurnEnd`
  - `public sealed class ListeningModelException(ListeningModel model, Exception inner) : Exception` with `public ListeningModel Model { get; }`.

- [ ] **Step 1: Write the failing tests (real models; Explicit like the Whisper ones)**

`tests/CodeSwitchX.Voice.Tests/Listening/ListeningModelsTests.cs`:

```csharp
using System.Diagnostics;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Listening;

namespace CodeSwitchX.Voice.Tests.Listening;

/// <summary>Needs the models in %LOCALAPPDATA%\CodeSwitchX\models\listening (switch to Open mic once, or run
/// ListeningModelStore.DownloadAsync).</summary>
public sealed class ListeningModelsTests
{
    private static string PathOf(ListeningModel model) =>
        Path.Combine(CodeSwitchX.Core.AppPaths.Default().ModelsDirectory, "listening", model.FileName);

    [Fact(Explicit = true)]
    public void Silero_hears_speech_in_the_warm_up_sample_and_none_in_silence()
    {
        using var vad = new SileroVad(PathOf(ListeningModelStore.Silero));
        var speech = WarmUpSpeech.Load();

        var heard = Frames(speech).Max(f => vad.Step(f));
        vad.Reset();
        var quiet = Frames(new float[16_000]).Max(f => vad.Step(f));

        heard.ShouldBeGreaterThan(0.8f);
        quiet.ShouldBeLessThan(0.2f);
    }

    [Fact(Explicit = true)]
    public void Smart_turn_answers_in_well_under_a_frame_budget()
    {
        using var turn = new SmartTurn(PathOf(ListeningModelStore.SmartTurn));
        var speech = WarmUpSpeech.Load();
        turn.Complete(speech); // the first call pays for the session's set-up

        var clock = Stopwatch.StartNew();
        var p = turn.Complete(speech);

        p.ShouldBeInRange(0, 1);
        clock.ElapsedMilliseconds.ShouldBeLessThan(250);
    }

    [Fact]
    public void A_model_file_that_is_not_a_model_fails_with_its_own_exception()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            Should.Throw<Microsoft.ML.OnnxRuntime.OnnxRuntimeException>(() => new SileroVad(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IEnumerable<float[]> Frames(float[] audio)
    {
        for (var i = 0; i + SileroVad.FrameSamples <= audio.Length; i += SileroVad.FrameSamples)
        {
            yield return audio[i..(i + SileroVad.FrameSamples)];
        }
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/CodeSwitchX.Voice.Tests` → Expected: build error, `SileroVad` missing.

- [ ] **Step 3: Write the models**

`src/CodeSwitchX.Voice/Listening/ListeningModels.cs`:

```csharp
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CodeSwitchX.Voice.Listening;

/// <summary>Is this 32 ms frame speech? A probability 0..1. Keeps state from frame to frame: one per stream.</summary>
public interface IVoiceActivity : IDisposable
{
    float Step(ReadOnlySpan<float> frame);

    void Reset();
}

/// <summary>Has the user finished their turn, judged from the turn's audio so far? A probability 0..1.</summary>
public interface ITurnEnd : IDisposable
{
    double Complete(ReadOnlySpan<float> turn16k);
}

/// <summary>A model file is in place but will not load: the file is deleted (<see cref="ListeningModelStore.Forget"/>) and
/// downloaded again the next time.</summary>
public sealed class ListeningModelException(ListeningModel model, Exception inner)
    : Exception($"{model.FileName} could not be loaded: {inner.Message}", inner)
{
    public ListeningModel Model { get; } = model;
}

/// <summary>
/// Silero VAD v6 on 512-sample frames at 16 kHz. As Silero's own wrapper (utils_vad.py OnnxWrapper): the model sees the
/// last 64 samples of the previous frame before each frame (576 in all), and its state [2, 1, 128] carries from frame to
/// frame. One thread each way: a frame is a fraction of a millisecond.
/// </summary>
public sealed class SileroVad : IVoiceActivity
{
    public const int FrameSamples = 512;
    private const int Context = 64;

    private readonly InferenceSession _session;
    private readonly float[] _input = new float[Context + FrameSamples];
    private float[] _state = new float[2 * 128];
    private readonly DenseTensor<long> _rate = new(new long[] { 16_000 }, []);

    public SileroVad(string modelPath)
    {
        using var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1 };
        _session = new InferenceSession(modelPath, options);
    }

    public float Step(ReadOnlySpan<float> frame)
    {
        if (frame.Length != FrameSamples)
        {
            throw new ArgumentException($"A frame is {FrameSamples} samples.", nameof(frame));
        }

        frame.CopyTo(_input.AsSpan(Context));
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(_input, [1, _input.Length])),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, [2, 1, 128])),
            NamedOnnxValue.CreateFromTensor("sr", _rate),
        };
        using var results = _session.Run(inputs);
        var probability = results.First(r => r.Name == "output").AsEnumerable<float>().First();
        _state = results.First(r => r.Name == "stateN").AsEnumerable<float>().ToArray();
        _input.AsSpan(FrameSamples, Context).CopyTo(_input); // the frame's last 64 samples are the next one's context
        return probability;
    }

    public void Reset()
    {
        Array.Clear(_input);
        _state = new float[2 * 128];
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>Pipecat's Smart Turn v3.2 on the CPU: the turn's last 8 s as Whisper features in, the probability that the
/// turn is complete out (the output is named "logits" but is already a sigmoid). About 15 ms.</summary>
public sealed class SmartTurn : ITurnEnd
{
    private readonly InferenceSession _session;

    public SmartTurn(string modelPath)
    {
        using var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(modelPath, options);
    }

    public double Complete(ReadOnlySpan<float> turn16k)
    {
        var features = WhisperFeatures.LogMel(turn16k);
        var input = NamedOnnxValue.CreateFromTensor("input_features",
            new DenseTensor<float>(features, [1, WhisperFeatures.Mels, WhisperFeatures.Frames]));
        using var results = _session.Run([input]);
        return results[0].AsEnumerable<float>().First();
    }

    public void Dispose() => _session.Dispose();
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet build tests/CodeSwitchX.Voice.Tests && tests/CodeSwitchX.Voice.Tests/bin/Debug/net10.0-windows/CodeSwitchX.Voice.Tests.exe --filter-class "CodeSwitchX.Voice.Tests.Listening.ListeningModelsTests"`
Expected: the non-explicit test passes. Then download the models once (a scratch `dotnet run` file or the Task 2 store against `%LOCALAPPDATA%\CodeSwitchX\models\listening`) and run the explicit ones too: add `--explicit on` to the exe command. Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/CodeSwitchX.Voice/Listening/ListeningModels.cs tests/CodeSwitchX.Voice.Tests/Listening/ListeningModelsTests.cs
git commit -m "feat: Silero VAD and Smart Turn v3 run in-process on ONNX Runtime (#75)"
git push
```

---

### Task 4: The turn detector

**Files:**
- Create: `src/CodeSwitchX.Voice/Listening/TurnDetector.cs`
- Test: `tests/CodeSwitchX.Voice.Tests/Listening/TurnDetectorTests.cs`

**Interfaces:**
- Consumes: `IVoiceActivity`, `ITurnEnd`, `SileroVad.FrameSamples` (Task 3).
- Produces:
  - `public abstract record TurnEvent { public sealed record Started : TurnEvent; public sealed record Ended(float[] Clip) : TurnEvent; }`
  - `public sealed class TurnDetector(IVoiceActivity vad, ITurnEnd turnEnd, ILogger logger)` with `public TurnEvent? Step(ReadOnlySpan<float> frame)`, `public void Reset()`, `public bool IgnoreSpeech { get; set; }` and the constants `StartThreshold = 0.5f`, `ContinueThreshold = 0.35f`, `MinimumSpeech = 0.5 s`, `Pause = 0.2 s`, `GiveUp = 3 s`, `PreRoll = 0.5 s`, `MaximumTurn = 120 s`.

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.Voice.Tests/Listening/TurnDetectorTests.cs`:

```csharp
using CodeSwitchX.Voice.Listening;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Voice.Tests.Listening;

public sealed class TurnDetectorTests
{
    private const int Frame = SileroVad.FrameSamples; // 32 ms
    private readonly ScriptedVad _vad = new();
    private readonly ScriptedTurnEnd _turn = new();
    private readonly TurnDetector _detector;

    public TurnDetectorTests() => _detector = new TurnDetector(_vad, _turn, NullLogger.Instance);

    [Fact]
    public void A_burst_shorter_than_half_a_second_is_no_turn()
    {
        var events = Feed(Silence(1), Speech(0.4), Silence(1));

        events.ShouldBeEmpty();
        _turn.Calls.ShouldBe(0);
    }

    [Fact]
    public void Half_a_second_of_speech_starts_the_turn_and_a_complete_pause_ends_it_after_0_2_s()
    {
        _turn.Answers.Enqueue(0.9);

        var events = Feed(Silence(1), Speech(1.0), Silence(1));

        events.Count.ShouldBe(2);
        events[0].ShouldBeOfType<TurnEvent.Started>();
        var clip = events[1].ShouldBeOfType<TurnEvent.Ended>().Clip;
        // 0.5 s pre-roll + 1 s speech + 0.2 s of the pause, give or take a frame each
        Seconds(clip).ShouldBeInRange(1.6, 1.8);
        _turn.Calls.ShouldBe(1);
    }

    [Fact]
    public void A_pause_Smart_Turn_calls_incomplete_does_not_end_the_turn_and_more_speech_continues_it()
    {
        _turn.Answers.Enqueue(0.1); // "I'd like to open the …"
        _turn.Answers.Enqueue(0.9);

        var events = Feed(Speech(1.0), Silence(1.0), Speech(1.0), Silence(0.5));

        events.OfType<TurnEvent.Started>().Count().ShouldBe(1);
        Seconds(events.OfType<TurnEvent.Ended>().ShouldHaveSingleItem().Clip).ShouldBeGreaterThan(3.0);
        _turn.Calls.ShouldBe(2);
    }

    [Fact]
    public void Three_seconds_of_silence_end_the_turn_whatever_Smart_Turn_says()
    {
        _turn.Answers.Enqueue(0.1);

        var events = Feed(Speech(1.0), Silence(2.9));
        events.OfType<TurnEvent.Ended>().ShouldBeEmpty();

        events = Feed(Silence(0.2));
        events.OfType<TurnEvent.Ended>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Smart_Turn_failing_falls_back_to_the_three_seconds()
    {
        _turn.Throws = true;

        var events = Feed(Speech(1.0), Silence(3.2));

        events.OfType<TurnEvent.Ended>().ShouldHaveSingleItem();
    }

    [Fact]
    public void The_pre_roll_carries_the_first_syllable_the_VAD_was_late_for()
    {
        _turn.Answers.Enqueue(0.9);
        var marker = 0.25f;

        var events = Feed(Silence(1), Audio(marker, Frame, speech: false), Speech(1.0), Silence(0.5));

        events.OfType<TurnEvent.Ended>().Single().Clip.ShouldContain(marker);
    }

    [Fact]
    public void A_turn_ends_at_two_minutes()
    {
        var events = Feed(Speech(121));

        Seconds(events.OfType<TurnEvent.Ended>().ShouldHaveSingleItem().Clip).ShouldBeLessThanOrEqualTo(120.6);
    }

    [Fact]
    public void Reset_drops_a_half_spoken_turn()
    {
        Feed(Speech(1.0));

        _detector.Reset();
        var events = Feed(Silence(4));

        events.ShouldBeEmpty();
        _vad.Resets.ShouldBe(1);
    }

    [Fact]
    public void While_speech_is_ignored_it_starts_no_turn()
    {
        _detector.IgnoreSpeech = true;

        Feed(Speech(2.0), Silence(1)).ShouldBeEmpty();

        _detector.IgnoreSpeech = false;
        _turn.Answers.Enqueue(0.9);
        Feed(Speech(1.0), Silence(0.5)).OfType<TurnEvent.Ended>().ShouldHaveSingleItem();
    }

    [Fact]
    public void A_dip_under_the_start_level_but_over_the_continue_level_is_still_speech()
    {
        _turn.Answers.Enqueue(0.9);

        var events = Feed(Speech(0.3), Audio(0.1f, Frame * 5, speech: true, probability: 0.4f), Speech(0.3), Silence(0.5));

        events.OfType<TurnEvent.Started>().ShouldHaveSingleItem();
        _turn.Calls.ShouldBe(1, "the dip was no pause");
    }

    private static double Seconds(float[] clip) => clip.Length / 16_000.0;

    private List<TurnEvent> Feed(params float[][] parts)
    {
        var events = new List<TurnEvent>();
        foreach (var part in parts)
        {
            for (var i = 0; i + Frame <= part.Length; i += Frame)
            {
                if (_detector.Step(part.AsSpan(i, Frame)) is { } e)
                {
                    events.Add(e);
                }
            }
        }

        return events;
    }

    private float[] Speech(double seconds) => Audio(0.5f, Samples(seconds), speech: true);

    private float[] Silence(double seconds) => Audio(0f, Samples(seconds), speech: false);

    private static int Samples(double seconds) => (int)Math.Round(seconds * 16_000 / Frame) * Frame;

    /// <summary>Audio whose frames the scripted VAD reads back: the first sample of each frame says how likely speech is.</summary>
    private float[] Audio(float value, int samples, bool speech, float? probability = null)
    {
        var audio = new float[samples];
        Array.Fill(audio, value);
        for (var i = 0; i < samples; i += Frame)
        {
            _vad.Script.Enqueue(probability ?? (speech ? 0.9f : 0.05f));
        }

        return audio;
    }

    private sealed class ScriptedVad : IVoiceActivity
    {
        public Queue<float> Script { get; } = new();

        public int Resets { get; private set; }

        public float Step(ReadOnlySpan<float> frame) => Script.Dequeue();

        public void Reset() => Resets++;

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedTurnEnd : ITurnEnd
    {
        public Queue<double> Answers { get; } = new();

        public int Calls { get; private set; }

        public bool Throws { get; set; }

        public double Complete(ReadOnlySpan<float> turn16k)
        {
            Calls++;
            turn16k.Length.ShouldBeLessThanOrEqualTo(8 * 16_000 + Frame);
            return Throws ? throw new InvalidOperationException("model died") : Answers.Count > 0 ? Answers.Dequeue() : 0.9;
        }

        public void Dispose()
        {
        }
    }
}
```

Note on `Reset_drops_a_half_spoken_turn`: the frames fed after `Reset()` are scripted as silence, so the scripted VAD's queue stays in step; `Reset` itself must not consume a script entry.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/CodeSwitchX.Voice.Tests` → Expected: build error, `TurnDetector` missing.

- [ ] **Step 3: Write the detector**

`src/CodeSwitchX.Voice/Listening/TurnDetector.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Voice.Listening;

/// <summary>What a frame told the detector: the user started talking (half a second of speech), or their turn is over.</summary>
public abstract record TurnEvent
{
    public sealed record Started : TurnEvent;

    /// <summary>The turn's audio, 16 kHz: the half second before the speech, the speech, and the first 0.2 s of the pause.</summary>
    public sealed record Ended(float[] Clip) : TurnEvent;
}

/// <summary>
/// Open mic's ear, in two stages as Pipecat pairs them: Silero says which 32 ms frames are speech, and when the speech
/// pauses for 0.2 s Smart Turn says whether the turn is over or the user is only thinking. A pause it calls incomplete
/// is waited out: speech that resumes continues the same turn, and the next pause asks again. A turn ends anyway after
/// 3 s of silence, so a model that never says "complete" (or fails) cannot hold the floor, and at two minutes, the
/// recorder's limit.
/// <para>
/// Silero's own hysteresis: a frame must reach 0.5 to start speech, and 0.35 keeps it going. Speech must add up to half
/// a second before the turn counts (<see cref="TurnEvent.Started"/>): a cough, a key or a knock falls short and is
/// dropped without a trace once 0.2 s of silence follows it.
/// </para>
/// <para>
/// Pure: no clock, no I/O. Time is the samples it is fed, so the tests drive it frame by frame. Not thread-safe: one
/// worker feeds it (<see cref="OpenMicListener"/>); <see cref="IgnoreSpeech"/> may be set from any thread.
/// </para>
/// </summary>
public sealed class TurnDetector(IVoiceActivity vad, ITurnEnd turnEnd, ILogger logger)
{
    public const float StartThreshold = 0.5f;
    public const float ContinueThreshold = 0.35f;
    public static readonly TimeSpan MinimumSpeech = TimeSpan.FromSeconds(0.5);
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(0.2);
    public static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PreRoll = TimeSpan.FromSeconds(0.5);
    public static readonly TimeSpan MaximumTurn = TimeSpan.FromSeconds(120);

    private const int Rate = 16_000;
    private const int Frame = SileroVad.FrameSamples;
    private static readonly int SmartTurnWindow = 8 * Rate;

    private readonly Queue<float[]> _preRoll = new();
    private readonly List<float> _turn = [];
    private volatile bool _ignoreSpeech;
    private bool _inTurn;
    private bool _started;
    private bool _asked;
    private int _speechFrames;
    private int _silentFrames;
    private bool _smartTurnFailed;

    /// <summary>Raven is speaking and the user turned voice barge-in off: what is heard is not taken as speech, and a
    /// turn not yet started is dropped.</summary>
    public bool IgnoreSpeech
    {
        get => _ignoreSpeech;
        set => _ignoreSpeech = value;
    }

    /// <summary>Feeds one frame of <see cref="SileroVad.FrameSamples"/> samples.</summary>
    public TurnEvent? Step(ReadOnlySpan<float> frame)
    {
        var probability = vad.Step(frame);
        var copy = frame.ToArray();
        var speech = !_ignoreSpeech && probability >= (_inTurn ? ContinueThreshold : StartThreshold);
        if (!_inTurn)
        {
            if (!speech)
            {
                _preRoll.Enqueue(copy);
                while (_preRoll.Count > FramesIn(PreRoll))
                {
                    _preRoll.Dequeue();
                }

                return null;
            }

            _inTurn = true;
            foreach (var earlier in _preRoll)
            {
                _turn.AddRange(earlier);
            }

            _preRoll.Clear();
        }

        _turn.AddRange(copy);
        if (speech)
        {
            _speechFrames++;
            _silentFrames = 0;
            _asked = false;
            if (!_started && _speechFrames >= FramesIn(MinimumSpeech))
            {
                _started = true;
                return new TurnEvent.Started();
            }
        }
        else
        {
            _silentFrames++;
            if (!_started && _silentFrames >= FramesIn(Pause))
            {
                Reset(keepVad: true); // a cough, a key: no turn
                return null;
            }

            if (_started && _silentFrames >= FramesIn(GiveUp))
            {
                return End();
            }

            if (_started && !_asked && _silentFrames >= FramesIn(Pause))
            {
                _asked = true;
                if (IsComplete())
                {
                    return End();
                }
            }
        }

        return _turn.Count >= MaximumTurn.TotalSeconds * Rate ? End() : null;
    }

    /// <summary>Back to waiting, with nothing kept: a pause, a mode switch, a new microphone.</summary>
    public void Reset() => Reset(keepVad: false);

    private void Reset(bool keepVad)
    {
        _turn.Clear();
        _preRoll.Clear();
        _inTurn = false;
        _started = false;
        _asked = false;
        _speechFrames = 0;
        _silentFrames = 0;
        if (!keepVad)
        {
            vad.Reset();
        }
    }

    private bool IsComplete()
    {
        try
        {
            var samples = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_turn);
            return turnEnd.Complete(samples[Math.Max(0, samples.Length - SmartTurnWindow)..]) > 0.5;
        }
        catch (Exception ex)
        {
            if (!_smartTurnFailed)
            {
                _smartTurnFailed = true; // once per listener: every pause would fail the same way
                logger.LogWarning(ex, "Smart Turn failed; Open mic ends turns after {Seconds} s of silence instead", GiveUp.TotalSeconds);
            }

            return false;
        }
    }

    /// <summary>The turn, its silence cut to the 0.2 s pause.</summary>
    private TurnEvent.Ended End()
    {
        var extraSilence = Math.Max(0, _silentFrames - FramesIn(Pause)) * Frame;
        var clip = _turn.GetRange(0, _turn.Count - extraSilence).ToArray();
        Reset(keepVad: true);
        return new TurnEvent.Ended(clip);
    }

    private static int FramesIn(TimeSpan span) => (int)Math.Round(span.TotalSeconds * Rate / Frame);
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet build tests/CodeSwitchX.Voice.Tests && tests/CodeSwitchX.Voice.Tests/bin/Debug/net10.0-windows/CodeSwitchX.Voice.Tests.exe --filter-class "CodeSwitchX.Voice.Tests.Listening.TurnDetectorTests"`
Expected: 10 passed.

- [ ] **Step 5: Commit**

```bash
git add src/CodeSwitchX.Voice/Listening/TurnDetector.cs tests/CodeSwitchX.Voice.Tests/Listening/TurnDetectorTests.cs
git commit -m "feat: Open mic's turn detector: Silero for speech, Smart Turn for the end of a turn (#75)"
git push
```

---

### Task 5: Continuous capture at 16 kHz

**Files:**
- Create: `src/CodeSwitchX.Voice/Listening/StreamResampler.cs`, `IMicrophoneStream.cs`, `WasapiMicrophoneStream.cs`, `FileMicrophoneStream.cs`
- Test: `tests/CodeSwitchX.Voice.Tests/Listening/StreamResamplerTests.cs`, `FileMicrophoneStreamTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class StreamResampler(int fromRate) { public float[] Push(ReadOnlySpan<float> mono); }` — output equals `AudioMath.Resample` of the concatenated input, less at most one sample at the end.
  - `public sealed record CapturedFrames(float[] Samples16k, float Rms);`
  - `public interface IMicrophoneStream { void Start(string deviceId); void Stop(); event EventHandler<CapturedFrames>? FramesCaptured; event EventHandler<MicrophoneException>? Failed; }`
  - `public sealed class WasapiMicrophoneStream : IMicrophoneStream, IDisposable`
  - `public sealed class FileMicrophoneStream(string pcmPath, bool realTime) : IMicrophoneStream, IDisposable` — 16 kHz mono 16-bit PCM (a `.wav` has its 44-byte header skipped), fed in 10 ms blocks, then silence until stopped.

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.Voice.Tests/Listening/StreamResamplerTests.cs`:

```csharp
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Listening;

namespace CodeSwitchX.Voice.Tests.Listening;

public sealed class StreamResamplerTests
{
    [Theory]
    [InlineData(48_000, 480)] // RØDE Connect: 10 ms blocks
    [InlineData(44_100, 441)]
    [InlineData(48_000, 137)] // odd block sizes
    [InlineData(16_000, 160)]
    public void Block_by_block_gives_what_the_whole_clip_gives(int rate, int block)
    {
        var input = Enumerable.Range(0, rate).Select(i => (float)Math.Sin(i * 0.01)).ToArray();
        var resampler = new StreamResampler(rate);

        var streamed = new List<float>();
        for (var i = 0; i < input.Length; i += block)
        {
            streamed.AddRange(resampler.Push(input.AsSpan(i, Math.Min(block, input.Length - i))));
        }

        var whole = AudioMath.Resample(input, rate);
        streamed.Count.ShouldBeInRange(whole.Length - 1, whole.Length);
        for (var i = 0; i < streamed.Count; i++)
        {
            streamed[i].ShouldBe(whole[i], 1e-6f);
        }
    }
}
```

`tests/CodeSwitchX.Voice.Tests/Listening/FileMicrophoneStreamTests.cs`:

```csharp
using CodeSwitchX.Voice.Listening;

namespace CodeSwitchX.Voice.Tests.Listening;

public sealed class FileMicrophoneStreamTests
{
    [Fact]
    public void A_file_is_fed_in_10_ms_blocks_then_silence_until_stopped()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x10, 16_000 * 2).ToArray()); // 1 s of a constant level
        using var stream = new FileMicrophoneStream(path, realTime: false);
        var frames = new List<CapturedFrames>();
        var enough = new ManualResetEventSlim();
        stream.FramesCaptured += (_, f) =>
        {
            lock (frames)
            {
                frames.Add(f);
                if (frames.Count == 150)
                {
                    enough.Set();
                }
            }
        };

        stream.Start("file");
        enough.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        stream.Stop();

        lock (frames)
        {
            frames.ShouldAllBe(f => f.Samples16k.Length == 160);
            frames[0].Rms.ShouldBeGreaterThan(0f);
            frames[120].Rms.ShouldBe(0f); // after the file: silence
        }

        File.Delete(path);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/CodeSwitchX.Voice.Tests` → Expected: build errors, types missing.

- [ ] **Step 3: Write the resampler, the interface and the file stream**

`src/CodeSwitchX.Voice/Listening/StreamResampler.cs`:

```csharp
using CodeSwitchX.Voice.Audio;

namespace CodeSwitchX.Voice.Listening;

/// <summary>
/// <see cref="AudioMath.Resample"/> one block at a time: output sample i averages input samples [⌊i·r⌋, ⌊(i+1)·r⌋), r
/// being from/to, counted over everything pushed so far, so block edges change nothing. Input not yet covered by a whole
/// output sample waits for the next block.
/// </summary>
public sealed class StreamResampler(int fromRate)
{
    private readonly double _ratio = (double)fromRate / AudioMath.TargetRate;
    private readonly List<float> _pending = [];
    private long _pendingStart; // the absolute index of _pending[0]
    private long _next; // the next output sample's index

    public float[] Push(ReadOnlySpan<float> mono)
    {
        if (fromRate == AudioMath.TargetRate)
        {
            return mono.ToArray();
        }

        _pending.AddRange(mono);
        var available = _pendingStart + _pending.Count;
        var output = new List<float>();
        while (true)
        {
            var start = (long)(_next * _ratio);
            var end = Math.Max(start + 1, (long)((_next + 1) * _ratio));
            if (end > available)
            {
                break;
            }

            float sum = 0;
            for (var j = start; j < end; j++)
            {
                sum += _pending[(int)(j - _pendingStart)];
            }

            output.Add(sum / (end - start));
            _next++;
        }

        var keepFrom = (long)(_next * _ratio);
        var drop = (int)Math.Clamp(keepFrom - _pendingStart, 0, _pending.Count);
        _pending.RemoveRange(0, drop);
        _pendingStart += drop;
        return [.. output];
    }
}
```

`src/CodeSwitchX.Voice/Listening/IMicrophoneStream.cs`:

```csharp
using CodeSwitchX.Voice.Audio;

namespace CodeSwitchX.Voice.Listening;

/// <summary>A captured block, resampled to 16 kHz mono, with its level.</summary>
public sealed record CapturedFrames(float[] Samples16k, float Rms);

/// <summary>
/// A microphone that captures until stopped and keeps nothing: Open mic runs for hours. Its events are raised on the
/// capture thread and must not block it (queue, or post asynchronously).
/// </summary>
public interface IMicrophoneStream
{
    /// <exception cref="MicrophoneException">The device could not be opened.</exception>
    void Start(string deviceId);

    /// <summary>Stops capturing; safe when not started. Waits up to 2 s for the capture thread.</summary>
    void Stop();

    event EventHandler<CapturedFrames>? FramesCaptured;

    /// <summary>The capture died (the device was unplugged); it has stopped.</summary>
    event EventHandler<MicrophoneException>? Failed;
}
```

`src/CodeSwitchX.Voice/Listening/FileMicrophoneStream.cs`:

```csharp
using CodeSwitchX.Voice.Audio;

namespace CodeSwitchX.Voice.Listening;

/// <summary>
/// A microphone that plays a file: 16 kHz mono 16-bit PCM (a .wav's 44-byte header skipped), in 10 ms blocks, then
/// silence until stopped. For the tests that feed the whole listener, and for the on-screen check of Open mic, which
/// nobody but the user can talk into. <paramref name="realTime"/> paces it as a microphone would.
/// </summary>
public sealed class FileMicrophoneStream(string pcmPath, bool realTime) : IMicrophoneStream, IDisposable
{
    private const int Block = 160;
    private CancellationTokenSource? _run;
    private Task _feeding = Task.CompletedTask;

    public event EventHandler<CapturedFrames>? FramesCaptured;

    public event EventHandler<MicrophoneException>? Failed;

    public void Start(string deviceId)
    {
        var bytes = File.ReadAllBytes(pcmPath);
        var offset = pcmPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? 44 : 0;
        var samples = new float[(bytes.Length - offset) / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes, offset + (i * 2)) / 32768f;
        }

        var run = _run = new CancellationTokenSource();
        _feeding = Task.Run(async () =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (long n = 0; !run.IsCancellationRequested; n++)
            {
                var block = new float[Block];
                var at = n * Block;
                if (at < samples.Length)
                {
                    samples.AsSpan((int)at, (int)Math.Min(Block, samples.Length - at)).CopyTo(block);
                }

                FramesCaptured?.Invoke(this, new CapturedFrames(block, AudioMath.Rms(block)));
                if (realTime)
                {
                    var due = TimeSpan.FromMilliseconds((n + 1) * 10) - clock.Elapsed;
                    if (due > TimeSpan.Zero)
                    {
                        await Task.Delay(due).ConfigureAwait(false);
                    }
                }
                else if (n % 100 == 0)
                {
                    await Task.Yield();
                }
            }
        });
    }

    public void Stop()
    {
        _run?.Cancel();
        _feeding.Wait(TimeSpan.FromSeconds(2));
        _run = null;
    }

    public void Dispose() => Stop();
}
```

(`Failed` is never raised by a file; keep the event for the interface: add `#pragma warning disable CS0067` around it if the build warns.)

- [ ] **Step 4: Write the WASAPI stream**

`src/CodeSwitchX.Voice/Listening/WasapiMicrophoneStream.cs` — the recorder's device handling (`WasapiMicrophoneRecorder`), without keeping samples or a limit:

```csharp
using CodeSwitchX.Voice.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Listening;

/// <summary>
/// Open mic's capture: as <see cref="WasapiMicrophoneRecorder"/> opens a device (an enumerator per stream made on the
/// calling thread, the capture built with no synchronisation context), but every block goes out at 16 kHz as it comes
/// and nothing is kept, so it can run for hours.
/// </summary>
public sealed class WasapiMicrophoneStream : IMicrophoneStream, IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private Session? _session;

    public event EventHandler<CapturedFrames>? FramesCaptured;

    public event EventHandler<MicrophoneException>? Failed;

    public void Start(string deviceId)
    {
        lock (_gate)
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("The stream is already running.");
            }

            MMDeviceEnumerator? enumerator = null;
            MMDevice? device = null;
            WasapiCapture? capture = null;
            try
            {
                enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDevice(deviceId);
                var previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(null);
                try
                {
                    capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }

                var session = new Session(this, enumerator, device, capture);
                capture.DataAvailable += session.OnData;
                capture.RecordingStopped += session.OnStopped;
                capture.StartRecording();
                _session = session;
            }
            catch (Exception e)
            {
                capture?.Dispose();
                device?.Dispose();
                enumerator?.Dispose();
                throw new MicrophoneException(MicrophoneFailure.Classify(e), e.Message, e);
            }
        }
    }

    public void Stop()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        session?.Finish();
    }

    public void Dispose() => Stop();

    private sealed class Session(WasapiMicrophoneStream owner, MMDeviceEnumerator enumerator, MMDevice device, WasapiCapture capture)
    {
        private readonly StreamResampler _resampler = new(capture.WaveFormat.SampleRate);
        private readonly ManualResetEventSlim _stopped = new(false);
        private volatile bool _stopping;

        public void OnData(object? sender, WaveInEventArgs e)
        {
            if (_stopping)
            {
                return;
            }

            var block = SampleDecoder.ToMonoFloats(e.Buffer, e.BytesRecorded, capture.WaveFormat);
            owner.FramesCaptured?.Invoke(owner, new CapturedFrames(_resampler.Push(block), AudioMath.Rms(block)));
        }

        public void OnStopped(object? sender, StoppedEventArgs e)
        {
            _stopped.Set();
            if (e.Exception is not null && !_stopping)
            {
                owner.Failed?.Invoke(owner, new MicrophoneException(MicrophoneFailure.Classify(e.Exception), e.Exception.Message, e.Exception));
            }
        }

        public void Finish()
        {
            _stopping = true;
            try
            {
                capture.StopRecording();
            }
            catch (Exception)
            {
                _stopped.Set(); // the device may be gone already
            }

            if (_stopped.Wait(StopTimeout))
            {
                Release();
            }
            else
            {
                _ = Task.Run(Release); // disposing joins the capture thread: never on the caller
            }
        }

        private void Release()
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;
            capture.Dispose();
            device.Dispose();
            enumerator.Dispose();
            _stopped.Dispose();
        }
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet build tests/CodeSwitchX.Voice.Tests && tests/CodeSwitchX.Voice.Tests/bin/Debug/net10.0-windows/CodeSwitchX.Voice.Tests.exe --filter-class "CodeSwitchX.Voice.Tests.Listening.StreamResamplerTests" --filter-class "CodeSwitchX.Voice.Tests.Listening.FileMicrophoneStreamTests"`
Expected: 5 passed.

- [ ] **Step 6: Commit**

```bash
git add src/CodeSwitchX.Voice tests/CodeSwitchX.Voice.Tests/Listening
git commit -m "feat: a microphone stream that captures at 16 kHz until stopped, and one that plays a file (#75)"
git push
```

---

### Task 6: The listener

**Files:**
- Create: `src/CodeSwitchX.Voice/Listening/OpenMicListener.cs`
- Modify: `src/CodeSwitchX.Voice/VoiceServiceCollectionExtensions.cs`, `docs/superpowers/specs/2026-10-01-raven-open-mic-design.md`
- Test: `tests/CodeSwitchX.Voice.Tests/Listening/OpenMicListenerTests.cs`

**Interfaces:**
- Consumes: `IMicrophoneStream`, `CapturedFrames` (Task 5); `TurnDetector`, `TurnEvent` (Task 4); `IVoiceActivity`, `ITurnEnd`, `SileroVad`, `SmartTurn`, `ListeningModelException` (Task 3); `ListeningModelStore` (Task 2).
- Produces:

```csharp
public interface IOpenMic
{
    bool ModelsPresent { get; }
    Task DownloadModelsAsync(IProgress<double>? progress, CancellationToken ct);
    /// <exception cref="ListeningModelException">A model would not load; its file is deleted.</exception>
    /// <exception cref="MicrophoneException">The device could not be opened.</exception>
    void Start(string deviceId);
    void Stop();
    bool IgnoreSpeech { get; set; }
    event EventHandler? SpeechStarted;
    event EventHandler<float[]>? TurnEnded;
    event EventHandler<CapturedFrames>? Heard;
    event EventHandler<MicrophoneException>? Failed;
}
public sealed class OpenMicListener : IOpenMic, IDisposable
```

Events are raised on the listener's worker thread (`Failed` on the capture thread).

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.Voice.Tests/Listening/OpenMicListenerTests.cs`:

```csharp
using System.Speech.Synthesis;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Listening;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Voice.Tests.Listening;

public sealed class OpenMicListenerTests
{
    [Fact]
    public async Task Blocks_of_any_size_reach_the_detector_as_512_sample_frames_and_its_events_come_out()
    {
        var stream = new FakeStream();
        var vad = new LevelVad();
        using var listener = new OpenMicListener(stream, new ListeningModelStore(Path.GetTempPath(), new HttpClient()),
            () => vad, () => new AlwaysComplete(), NullLogger<OpenMicListener>.Instance);
        var started = new TaskCompletionSource();
        var ended = new TaskCompletionSource<float[]>();
        listener.SpeechStarted += (_, _) => started.TrySetResult();
        listener.TurnEnded += (_, clip) => ended.TrySetResult(clip);

        listener.Start("mic");
        stream.Feed(0.5f, seconds: 1.0, block: 441); // speech, in odd blocks
        stream.Feed(0f, seconds: 0.5, block: 441);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var clip = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clip.Length.ShouldBeGreaterThan(16_000);
        vad.FrameSizes.ShouldAllBe(n => n == SileroVad.FrameSamples);
        listener.Stop();
        stream.Running.ShouldBeFalse();
    }

    [Fact]
    public async Task A_stop_drops_a_half_spoken_turn_and_a_new_start_begins_fresh()
    {
        var stream = new FakeStream();
        var vad = new LevelVad();
        using var listener = new OpenMicListener(stream, new ListeningModelStore(Path.GetTempPath(), new HttpClient()),
            () => vad, () => new AlwaysComplete(), NullLogger<OpenMicListener>.Instance);
        var ends = 0;
        listener.TurnEnded += (_, _) => Interlocked.Increment(ref ends);

        listener.Start("mic");
        stream.Feed(0.5f, seconds: 1.0, block: 160);
        listener.Stop();
        listener.Start("mic");
        stream.Feed(0f, seconds: 1.0, block: 160);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        ends.ShouldBe(0);
        vad.Resets.ShouldBeGreaterThanOrEqualTo(1);
        listener.Stop();
    }

    [Fact]
    public void A_model_that_will_not_load_is_forgotten_and_reported()
    {
        var folder = Path.Combine(Path.GetTempPath(), "csx-om-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var store = new ListeningModelStore(folder, new HttpClient());
        File.WriteAllBytes(store.PathOf(ListeningModelStore.Silero), [1, 2, 3]);
        using var listener = new OpenMicListener(new FakeStream(), store, NullLogger<OpenMicListener>.Instance);

        var error = Should.Throw<ListeningModelException>(() => listener.Start("mic"));

        error.Model.ShouldBe(ListeningModelStore.Silero);
        File.Exists(store.PathOf(ListeningModelStore.Silero)).ShouldBeFalse();
        Directory.Delete(folder, recursive: true);
    }

    // Needs the models in %LOCALAPPDATA%\CodeSwitchX\models\listening.
    [Fact(Explicit = true)]
    public async Task A_sentence_with_a_one_second_pause_in_the_middle_is_one_turn()
    {
        var store = new ListeningModelStore(Path.Combine(CodeSwitchX.Core.AppPaths.Default().ModelsDirectory, "listening"), new HttpClient());
        var path = Path.Combine(Path.GetTempPath(), "csx-open-mic-pause.pcm");
        var audio = Speak("I would like to open the") .Concat(new float[16_000]).Concat(Speak("Diffusion Nexus workspace, please.")).Concat(new float[3 * 16_000]).ToArray();
        WritePcm(path, audio);
        using var listener = new OpenMicListener(new FileMicrophoneStream(path, realTime: false), store, NullLogger<OpenMicListener>.Instance);
        var turns = new List<float[]>();
        listener.TurnEnded += (_, clip) => { lock (turns) { turns.Add(clip); } };

        listener.Start("file");
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        listener.Stop();

        lock (turns)
        {
            turns.Count.ShouldBe(1, "the pause mid-sentence must not end the turn");
            (turns[0].Length / 16_000.0).ShouldBeGreaterThan(3.0);
        }
    }

    private static float[] Speak(string text)
    {
        using var stream = new MemoryStream();
        using (var synth = new SpeechSynthesizer())
        {
            synth.SetOutputToAudioStream(stream, new System.Speech.AudioFormat.SpeechAudioFormatInfo(16_000,
                System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            synth.Speak(text);
        }

        var bytes = stream.ToArray();
        var samples = new float[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        }

        return samples;
    }

    private static void WritePcm(string path, float[] audio)
    {
        var bytes = new byte[audio.Length * 2];
        for (var i = 0; i < audio.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (short)Math.Clamp(audio[i] * 32767f, short.MinValue, short.MaxValue));
        }

        File.WriteAllBytes(path, bytes);
    }

    private sealed class FakeStream : IMicrophoneStream
    {
        public bool Running { get; private set; }

        public event EventHandler<CapturedFrames>? FramesCaptured;

        public event EventHandler<MicrophoneException>? Failed;

        public void Start(string deviceId) => Running = true;

        public void Stop() => Running = false;

        public void Feed(float level, double seconds, int block)
        {
            var total = (int)(seconds * 16_000);
            for (var i = 0; i < total; i += block)
            {
                var samples = new float[Math.Min(block, total - i)];
                Array.Fill(samples, level);
                FramesCaptured?.Invoke(this, new CapturedFrames(samples, level));
            }
        }

        public void Fail() => Failed?.Invoke(this, new MicrophoneException(MicrophoneFailureKind.Missing, "gone", new Exception()));
    }

    /// <summary>Speech is any frame whose first sample is loud.</summary>
    private sealed class LevelVad : IVoiceActivity
    {
        public List<int> FrameSizes { get; } = [];

        public int Resets { get; private set; }

        public float Step(ReadOnlySpan<float> frame)
        {
            lock (FrameSizes)
            {
                FrameSizes.Add(frame.Length);
            }

            return frame[0] > 0.1f ? 0.9f : 0.05f;
        }

        public void Reset() => Resets++;

        public void Dispose()
        {
        }
    }

    private sealed class AlwaysComplete : ITurnEnd
    {
        public double Complete(ReadOnlySpan<float> turn16k) => 0.9;

        public void Dispose()
        {
        }
    }
}
```

(If `MicrophoneException`'s constructor or `MicrophoneFailureKind.Missing` differ, read `src/CodeSwitchX.Voice/Audio/MicrophoneFailure.cs` and use its real signature.)

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/CodeSwitchX.Voice.Tests` → Expected: build error, `OpenMicListener` missing.

- [ ] **Step 3: Write the listener**

`src/CodeSwitchX.Voice/Listening/OpenMicListener.cs`:

```csharp
using System.Threading.Channels;
using CodeSwitchX.Voice.Audio;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Voice.Listening;

/// <summary>Open mic: listens until stopped and says when the user starts talking and when their turn is over.</summary>
public interface IOpenMic
{
    bool ModelsPresent { get; }

    Task DownloadModelsAsync(IProgress<double>? progress, CancellationToken ct);

    /// <summary>Loads the models (once) and opens the microphone. Call off the UI thread.</summary>
    /// <exception cref="ListeningModelException">A model would not load; its file is deleted.</exception>
    /// <exception cref="MicrophoneException">The device could not be opened.</exception>
    void Start(string deviceId);

    /// <summary>Closes the microphone (Windows' microphone indicator goes off) and drops a turn being spoken. Call off the UI thread.</summary>
    void Stop();

    /// <summary>What is heard is not taken as speech: Raven speaks and voice barge-in is off.</summary>
    bool IgnoreSpeech { get; set; }

    /// <summary>Half a second of speech: the user is talking. On the worker thread.</summary>
    event EventHandler? SpeechStarted;

    /// <summary>The user's turn is over: its audio, 16 kHz. On the worker thread.</summary>
    event EventHandler<float[]>? TurnEnded;

    /// <summary>Each captured block, for the orb's level and the silent-microphone watch. On the worker thread.</summary>
    event EventHandler<CapturedFrames>? Heard;

    /// <summary>The microphone died; the listener has stopped. On the capture thread.</summary>
    event EventHandler<MicrophoneException>? Failed;
}

/// <summary>
/// Ties a <see cref="IMicrophoneStream"/> to a <see cref="TurnDetector"/>. The capture thread only queues blocks; one
/// worker per run cuts them into 512-sample frames and runs the detector, so the models never run on the capture thread
/// (or the UI thread, which only starts and stops this off itself). The models are loaded on the first start and kept.
/// </summary>
public sealed class OpenMicListener : IOpenMic, IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly IMicrophoneStream _stream;
    private readonly ListeningModelStore _store;
    private readonly Func<IVoiceActivity> _newVad;
    private readonly Func<ITurnEnd> _newTurnEnd;
    private readonly ILogger<OpenMicListener> _logger;
    private readonly object _gate = new();
    private TurnDetector? _detector;
    private IVoiceActivity? _vad;
    private ITurnEnd? _turnEnd;
    private Channel<CapturedFrames>? _queue;
    private Task _worker = Task.CompletedTask;
    private volatile bool _ignoreSpeech;
    private bool _overflowLogged;

    public OpenMicListener(IMicrophoneStream stream, ListeningModelStore store, ILogger<OpenMicListener> logger)
        : this(stream, store, () => new SileroVad(store.PathOf(ListeningModelStore.Silero)),
            () => new SmartTurn(store.PathOf(ListeningModelStore.SmartTurn)), logger)
    {
    }

    /// <summary>With models of the caller's: the tests' fakes.</summary>
    internal OpenMicListener(IMicrophoneStream stream, ListeningModelStore store, Func<IVoiceActivity> vad, Func<ITurnEnd> turnEnd,
        ILogger<OpenMicListener> logger)
    {
        _stream = stream;
        _store = store;
        _newVad = vad;
        _newTurnEnd = turnEnd;
        _logger = logger;
        _stream.FramesCaptured += OnFrames;
        _stream.Failed += OnFailed;
    }

    public event EventHandler? SpeechStarted;

    public event EventHandler<float[]>? TurnEnded;

    public event EventHandler<CapturedFrames>? Heard;

    public event EventHandler<MicrophoneException>? Failed;

    public bool ModelsPresent => _store.IsPresent;

    public bool IgnoreSpeech
    {
        get => _ignoreSpeech;
        set
        {
            _ignoreSpeech = value;
            if (_detector is { } detector)
            {
                detector.IgnoreSpeech = value;
            }
        }
    }

    public Task DownloadModelsAsync(IProgress<double>? progress, CancellationToken ct) => _store.DownloadAsync(progress, ct);

    public void Start(string deviceId)
    {
        lock (_gate)
        {
            Stop();
            _detector ??= LoadDetector();
            _detector.Reset();
            _detector.IgnoreSpeech = _ignoreSpeech;
            // About ten seconds of 10 ms blocks: a Smart Turn call is milliseconds, so this only fills if the PC stalls.
            var queue = _queue = Channel.CreateBounded<CapturedFrames>(new BoundedChannelOptions(1000)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropWrite,
            });
            _worker = Task.Run(() => WorkAsync(queue, _detector));
            _stream.Start(deviceId); // throws MicrophoneException; the worker then ends with Stop
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stream.Stop();
            _queue?.Writer.TryComplete();
            _queue = null;
            if (!_worker.Wait(StopTimeout))
            {
                _logger.LogWarning("Open mic's worker did not stop within {Seconds} s", StopTimeout.TotalSeconds);
            }

            _detector?.Reset();
        }
    }

    public void Dispose()
    {
        Stop();
        _stream.FramesCaptured -= OnFrames;
        _stream.Failed -= OnFailed;
        _vad?.Dispose();
        _turnEnd?.Dispose();
    }

    private TurnDetector LoadDetector()
    {
        _vad = Load(ListeningModelStore.Silero, _newVad);
        _turnEnd = Load(ListeningModelStore.SmartTurn, _newTurnEnd);
        return new TurnDetector(_vad, _turnEnd, _logger);
    }

    private T Load<T>(ListeningModel model, Func<T> load)
    {
        try
        {
            return load();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Model} would not load; it is deleted and downloaded again next time", model.FileName);
            _store.Forget(model);
            throw new ListeningModelException(model, ex);
        }
    }

    private void OnFrames(object? sender, CapturedFrames frames)
    {
        if (_queue is { } queue && !queue.Writer.TryWrite(frames) && !_overflowLogged)
        {
            _overflowLogged = true;
            _logger.LogWarning("Open mic fell behind the microphone; some audio was dropped");
        }
    }

    private void OnFailed(object? sender, MicrophoneException error) => Failed?.Invoke(this, error);

    private async Task WorkAsync(Channel<CapturedFrames> queue, TurnDetector detector)
    {
        var frame = new float[SileroVad.FrameSamples];
        var filled = 0;
        try
        {
            await foreach (var block in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                Heard?.Invoke(this, block);
                var samples = block.Samples16k.AsSpan();
                while (samples.Length > 0)
                {
                    var take = Math.Min(samples.Length, frame.Length - filled);
                    samples[..take].CopyTo(frame.AsSpan(filled));
                    samples = samples[take..];
                    filled += take;
                    if (filled < frame.Length)
                    {
                        break;
                    }

                    filled = 0;
                    switch (detector.Step(frame))
                    {
                        case TurnEvent.Started:
                            SpeechStarted?.Invoke(this, EventArgs.Empty);
                            break;
                        case TurnEvent.Ended ended:
                            TurnEnded?.Invoke(this, ended.Clip);
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open mic's worker failed");
        }
    }
}
```

- [ ] **Step 4: Register it**

In `VoiceServiceCollectionExtensions.AddCodeSwitchXVoice`, after the `IMicrophoneRecorder` line:

```csharp
        // Open mic: listens until stopped; its two models are fetched the first time the user switches to it.
        services.AddSingleton(_ => new ListeningModelStore(Path.Combine(modelsDirectory, "listening"), new HttpClient()));
        services.AddSingleton<IMicrophoneStream, WasapiMicrophoneStream>();
        services.AddSingleton<IOpenMic, OpenMicListener>();
```

and `using CodeSwitchX.Voice.Listening;` at the top.

- [ ] **Step 5: Bring the spec in line**

In the spec's `### OpenMicListener` section, replace the `Start(deviceId)`, `Stop()`, `Pause()`, `Resume()`, `RavenSpeaking { set; }` bullet with:

```markdown
- `Start(deviceId)`, `Stop()`, `IgnoreSpeech { set; }`. A pause is a stop: the microphone is closed, so Windows'
  microphone indicator goes off while Open mic is paused; resume starts it again. The panel sets `IgnoreSpeech` while
  Raven speaks with voice barge-in off.
```

- [ ] **Step 6: Run the tests**

Run: `dotnet build tests/CodeSwitchX.Voice.Tests && tests/CodeSwitchX.Voice.Tests/bin/Debug/net10.0-windows/CodeSwitchX.Voice.Tests.exe --filter-class "CodeSwitchX.Voice.Tests.Listening.OpenMicListenerTests"`
Expected: 3 passed. With the models present, run with `--explicit on`: 4 passed. If the explicit one gives two turns, log Smart Turn's probability at the mid-sentence pause and report it; do not tune thresholds away from the spec without asking.

- [ ] **Step 7: Commit**

```bash
git add src/CodeSwitchX.Voice tests/CodeSwitchX.Voice.Tests/Listening docs/superpowers/specs/2026-10-01-raven-open-mic-design.md
git commit -m "feat: Open mic's listener runs turn detection on its own thread behind IOpenMic (#75)"
git push
```

---

### Task 7: The light ring on Raven's orb

**Files:**
- Modify: `src/CodeSwitchX.UI/Raven/RavenState.cs`, `src/CodeSwitchX.UI/Raven/RavenOrb.cs`, `src/CodeSwitchX.UI/Raven/RavenPanelView.xaml`

**Interfaces:**
- Produces: `RavenState.Attending`, `RavenState.AttendingPaused`. The orb takes `Level` (the room) while `Attending`.

- [ ] **Step 1: Add the states**

In `RavenState.cs`, after `Idle`:

```csharp
    /// <summary>Open mic waits for the user: the microphone is live. The orb shows its light ring.</summary>
    Attending,

    /// <summary>Open mic is paused: the light ring, still and dimmed.</summary>
    AttendingPaused,
```

- [ ] **Step 2: Draw the ring**

In `RavenOrb.cs`:

1. Extend the class summary's first sentence with ", a ring of dots with a glint going round while Open mic waits".
2. `ShouldAnimate`: a paused ring is still, a waiting one moves in a background window too:

```csharp
    private bool ShouldAnimate => IsAnimating && IsVisible && MotionAllowed && _window is not null && _window.WindowState != WindowState.Minimized
        && State != RavenState.AttendingPaused && (_window.IsActive || State != RavenState.Idle);
```

3. Fields after `_shownLevel`:

```csharp
    /// <summary>1 while Open mic waits, easing to 0 as speech takes over: the light ring fades out, the wave ring comes in.</summary>
    private double _attend;
    private readonly Dictionary<byte, Brush> _dots = [];
```

4. In `OnRendering`, the level target and the blend:

```csharp
        var target = State is RavenState.Listening or RavenState.Speaking or RavenState.Attending ? Math.Clamp(Level, 0, 1) : 0;
        _shownLevel += (target - _shownLevel) * (1 - Math.Pow(1 - 0.18, dt * 60));
        var attending = State is RavenState.Attending or RavenState.AttendingPaused ? 1 : 0;
        _attend += (attending - _attend) * (1 - Math.Pow(1 - 0.12, dt * 60));

        // Thinking and the light ring move as calmly as the idle breath, and may last for hours in a background window.
        if (State is RavenState.Idle or RavenState.Thinking or RavenState.Attending && now - _lastDraw < IdleFrame)
```

5. In `DrawFrame`, replace from `var amp = State switch` through the `else { dc.DrawEllipse(... Base + 44 ...) }` branch with:

```csharp
        var attend = _hooked ? _attend : State is RavenState.Attending or RavenState.AttendingPaused ? 1 : 0;
        var room = State == RavenState.Attending ? Math.Min(_hooked ? _shownLevel : Math.Clamp(Level, 0, 1), 0.3) : 0;
        var amp = State switch
        {
            RavenState.Listening or RavenState.Speaking => _hooked ? _shownLevel : Math.Clamp(Level, 0, 1),
            RavenState.Thinking => 0.18 + (Math.Sin(t * 2.4) * 0.12), // a slow ripple: no voice moves it
            _ => 0,
        };
        var breathe = State == RavenState.Idle ? Math.Sin(t * 1.4) * 3 : 0;

        DrawGlow(dc, center, scale, amp + (room * 0.3));
        DrawWaveRing(dc, center, scale, t, amp, breathe, 0.9 - (0.35 * attend));
        if (State == RavenState.Transcribing)
        {
            DrawArcs(dc, center, scale, t);
        }
        else if (State == RavenState.Thinking)
        {
            DrawOrbit(dc, center, scale, t);
        }
        else if (State == RavenState.Speaking)
        {
            DrawRipples(dc, center, scale, t, amp);
        }
        else if (attend < 0.99)
        {
            dc.DrawEllipse(null, VoicePen(0.25 * (1 - attend), Math.Max(0.75, 1.4 * scale)), center, (Base + 44) * scale, (Base + 44) * scale);
        }

        if (attend > 0.01)
        {
            DrawLightRing(dc, center, scale, t, room, attend, State == RavenState.AttendingPaused);
        }
```

6. `DrawWaveRing` gets an alpha parameter: signature `(DrawingContext dc, Point center, double scale, double t, double amp, double breathe, double alpha)` and its last line `dc.DrawGeometry(null, VoicePen(alpha, Math.Max(1, 2.5 * scale)), geometry);`.

7. The ring itself, after `DrawOrbit`:

```csharp
    /// <summary>
    /// Open mic's light ring: 36 dim dots on the outer ring with a soft glint drifting round them, and the room's sound
    /// lighting them unevenly, each by its own flicker, so a sound shows as a sparkle round the ring. Paused: the dots
    /// still, at half their light, with no glint. <paramref name="fade"/> takes it out as speech starts.
    /// </summary>
    private void DrawLightRing(DrawingContext dc, Point center, double scale, double t, double room, double fade, bool paused)
    {
        const int Dots = 36;
        var radius = (Base + 44) * scale;
        var glint = t * 0.9;
        for (var i = 0; i < Dots; i++)
        {
            var a = (i / (double)Dots * Math.PI * 2) - (Math.PI / 2);
            var d = Math.Abs(((((a - glint) % (Math.PI * 2)) + (Math.PI * 3)) % (Math.PI * 2)) - Math.PI);
            var g = paused ? 0 : Math.Exp(-d * d * 2.2);
            var flicker = 0.5 + (0.5 * Math.Sin((i * 2.7) + (t * 9)));
            var lit = paused ? 0.08 : 0.16 + (g * 0.55) + (room * 2.2 * flicker);
            var size = Math.Max(0.6, (1.6 + (g * 1.4) + (room * 3 * flicker)) * scale);
            dc.DrawEllipse(DotBrush(lit * fade), null, new Point(center.X + (Math.Cos(a) * radius), center.Y + (Math.Sin(a) * radius)), size, size);
        }
    }

    private Brush DotBrush(double alpha)
    {
        var key = AlphaByte(alpha);
        if (!_dots.TryGetValue(key, out var brush))
        {
            brush = Frozen(new SolidColorBrush(Color.FromArgb(key, Voice.R, Voice.G, Voice.B)));
            _dots[key] = brush;
        }

        return brush;
    }
```

- [ ] **Step 3: The rail orb animates while Open mic waits**

In `RavenPanelView.xaml`, the rail orb's style triggers (the `DataTrigger` on `Idle` that sets `IsAnimating` to False): add the same trigger for `AttendingPaused`. Leave `Attending` animating.

- [ ] **Step 4: Build**

Run: `dotnet build src/CodeSwitchX.UI` → Expected: 0 warnings, 0 errors. (The look is checked on screen in Task 10.)

- [ ] **Step 5: Commit**

```bash
git add src/CodeSwitchX.UI/Raven
git commit -m "feat: Raven's orb shows a light ring while Open mic waits (#75)"
git push
```

---

### Task 8: Open mic in the panel

**Files:**
- Create: `src/CodeSwitchX.UI/Raven/MicMode.cs`, `tests/CodeSwitchX.UI.Tests/Raven/FakeOpenMic.cs`, `tests/CodeSwitchX.UI.Tests/Raven/RavenPanelViewModelTests.OpenMic.cs`
- Modify: `src/CodeSwitchX.UI/Raven/RavenPanelViewModel.cs`

**Interfaces:**
- Consumes: `IOpenMic`, `ListeningModelException` (Task 6); `RavenState.Attending/AttendingPaused` (Task 7).
- Produces: `public enum MicMode { PushToTalk, OpenMic }`; on `RavenPanelViewModel`: `[ObservableProperty] MicMode MicMode`, `[ObservableProperty] bool BargeIn = true`, `public string MicButtonName { get; }` (raises change with `MicMode`/pause), `internal Task PendingOpenMic`, constructor parameter `IOpenMic? openMic = null` after `teller`.

- [ ] **Step 1: The fake and the enum**

`src/CodeSwitchX.UI/Raven/MicMode.cs`:

```csharp
namespace CodeSwitchX.UI.Raven;

/// <summary>How the user talks to Raven: holding a key or the button, or Open mic, which listens all the time.</summary>
public enum MicMode
{
    PushToTalk,
    OpenMic,
}
```

`tests/CodeSwitchX.UI.Tests/Raven/FakeOpenMic.cs`:

```csharp
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Listening;

namespace CodeSwitchX.UI.Tests.Raven;

internal sealed class FakeOpenMic : IOpenMic
{
    public bool ModelsPresent { get; set; } = true;

    public Exception? DownloadFails { get; set; }

    public Exception? StartFails { get; set; }

    public int Downloads { get; private set; }

    /// <summary>The device it listens on now; null while stopped.</summary>
    public string? Listening { get; private set; }

    public List<string> Started { get; } = [];

    public bool IgnoreSpeech { get; set; }

    public event EventHandler? SpeechStarted;

    public event EventHandler<float[]>? TurnEnded;

    public event EventHandler<CapturedFrames>? Heard;

    public event EventHandler<MicrophoneException>? Failed;

    public Task DownloadModelsAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Downloads++;
        if (DownloadFails is { } error)
        {
            return Task.FromException(error);
        }

        ModelsPresent = true;
        progress?.Report(1);
        return Task.CompletedTask;
    }

    public void Start(string deviceId)
    {
        if (StartFails is { } error)
        {
            throw error;
        }

        Listening = deviceId;
        Started.Add(deviceId);
    }

    public void Stop() => Listening = null;

    public void Speak() => SpeechStarted?.Invoke(this, EventArgs.Empty);

    public void EndTurn(double seconds = 2) => TurnEnded?.Invoke(this, new float[(int)(seconds * 16_000)]);

    /// <summary>A block of 10 ms at this level.</summary>
    public void Hear(float rms) => Heard?.Invoke(this, new CapturedFrames(new float[160], rms));

    public void Fail() => Failed?.Invoke(this, new MicrophoneException(MicrophoneFailureKind.Missing, "gone", new Exception("gone")));
}
```

- [ ] **Step 2: Write the failing tests**

`tests/CodeSwitchX.UI.Tests/Raven/RavenPanelViewModelTests.OpenMic.cs`:

```csharp
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Listening;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed partial class RavenPanelViewModelTests
{
    private readonly FakeOpenMic _openMic = new();

    private async Task<RavenPanelViewModel> NewOpenMicVmAsync()
    {
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech,
            new ImmediateDispatcher(), _time, NullLogger<RavenPanelViewModel>.Instance, openMic: _openMic);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        return vm;
    }

    private async Task<RavenPanelViewModel> InOpenMicAsync()
    {
        var vm = await NewOpenMicVmAsync();
        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);
        return vm;
    }

    [Fact]
    public async Task Switching_to_Open_mic_listens_on_the_selected_microphone_and_the_orb_waits()
    {
        var vm = await InOpenMicAsync();

        _openMic.Listening.ShouldBe(Headset.Id);
        vm.State.ShouldBe(RavenState.Attending);
        vm.Caption.ShouldBe("Open mic");
    }

    [Fact]
    public async Task Switching_back_to_push_to_talk_stops_listening()
    {
        var vm = await InOpenMicAsync();

        vm.MicMode = MicMode.PushToTalk;
        await WithinAsync(vm.PendingOpenMic);

        _openMic.Listening.ShouldBeNull();
        vm.State.ShouldBe(RavenState.Idle);
    }

    [Fact]
    public async Task Missing_models_are_downloaded_first_and_a_failed_download_goes_back_to_push_to_talk()
    {
        _openMic.ModelsPresent = false;
        _openMic.DownloadFails = new HttpRequestException("no network");
        var vm = await NewOpenMicVmAsync();

        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);

        _openMic.Downloads.ShouldBe(1);
        vm.MicMode.ShouldBe(MicMode.PushToTalk);
        _openMic.Listening.ShouldBeNull();
        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.Warning && e.Text.Contains("no network"));
    }

    [Fact]
    public async Task A_model_that_will_not_load_goes_back_to_push_to_talk_with_a_warning()
    {
        _openMic.StartFails = new ListeningModelException(ListeningModelStore.SmartTurn, new Exception("bad file"));
        var vm = await NewOpenMicVmAsync();

        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);

        vm.MicMode.ShouldBe(MicMode.PushToTalk);
        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.Warning && e.Text.Contains("smart-turn"));
    }

    [Fact]
    public async Task The_mic_button_and_the_hotkey_pause_and_resume()
    {
        var vm = await InOpenMicAsync();

        await vm.TapMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingOpenMic);
        _openMic.Listening.ShouldBeNull();
        vm.State.ShouldBe(RavenState.AttendingPaused);
        vm.Caption.ShouldBe("Open mic paused");
        vm.MicButtonName.ShouldBe("Resume Open mic");

        vm.PressMic(TalkInput.Hotkey);
        await vm.ReleaseMicAsync(TalkInput.Hotkey);
        await WithinAsync(vm.PendingOpenMic);
        _openMic.Listening.ShouldBe(Headset.Id);
        vm.State.ShouldBe(RavenState.Attending);
        vm.MicButtonName.ShouldBe("Pause Open mic");
        _recorder.DidNotReceive().Start(Arg.Any<string>());
    }

    [Fact]
    public async Task Speech_hushes_Raven_and_a_finished_turn_is_transcribed_and_asked()
    {
        var vm = await InOpenMicAsync();

        _openMic.Speak();
        vm.State.ShouldBe(RavenState.Listening);
        vm.Caption.ShouldBe("Listening…");
        _openMic.EndTurn();
        await WithinAsync(vm.PendingTranscriptions);
        await WithinAsync(vm.PendingAnswers);

        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.You && e.Text == "Hallo Raven, open Diffusion Nexus");
        _brain.Asked.ShouldContain(q => q.Contains("open Diffusion Nexus"));
        vm.State.ShouldBe(RavenState.Attending);
    }

    [Fact]
    public async Task An_empty_transcript_in_Open_mic_is_not_noted_in_the_log()
    {
        Transcribes(Task.FromResult(new DictationResult("", TimeSpan.FromSeconds(1))));
        var vm = await InOpenMicAsync();
        var before = vm.Log.Count;

        _openMic.Speak();
        _openMic.EndTurn();
        await WithinAsync(vm.PendingTranscriptions);

        vm.Log.Count.ShouldBe(before);
    }

    [Fact]
    public async Task With_barge_in_off_speech_is_ignored_while_Raven_speaks()
    {
        _brain.Answer = _ => [new BrainText("You have one chat waiting.")];
        var vm = await InOpenMicAsync();
        vm.BargeIn = false;

        Type(vm, "What's waiting on me?");
        await Until(() => vm.State == RavenState.Speaking);
        _openMic.IgnoreSpeech.ShouldBeTrue();

        vm.BargeIn = true;
        _openMic.IgnoreSpeech.ShouldBeFalse();
    }

    [Fact]
    public async Task A_microphone_that_sends_nothing_in_Open_mic_is_warned_of_once()
    {
        var vm = await InOpenMicAsync();

        for (var i = 0; i < 500; i++) // 5 s of digital silence
        {
            _openMic.Hear(0f);
        }

        vm.Log.Count(e => e.Kind == RavenLogKind.Warning && e.Text.StartsWith("No sound from")).ShouldBe(1);
    }

    // Review focus 1
    [Fact]
    public async Task Open_mic_saved_from_last_time_starts_once_the_microphones_are_listed()
    {
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech,
            new ImmediateDispatcher(), _time, NullLogger<RavenPanelViewModel>.Instance, openMic: _openMic);

        vm.MicMode = MicMode.OpenMic; // the shell sets it from the settings before the list arrives
        await WithinAsync(vm.RefreshMicrophonesAsync());
        await WithinAsync(vm.PendingOpenMic);

        _openMic.Listening.ShouldBe(Headset.Id);
    }

    // Review focus 2
    [Fact]
    public async Task Picking_another_microphone_moves_Open_mic_to_it()
    {
        var vm = await InOpenMicAsync();

        vm.SelectedMicrophone = Desk;
        await WithinAsync(vm.PendingOpenMic);

        _openMic.Listening.ShouldBe(Desk.Id);
        _openMic.Started.ShouldBe([Headset.Id, Desk.Id]);
    }

    // Review focus 3
    [Fact]
    public async Task Pausing_mid_turn_drops_the_turn()
    {
        var vm = await InOpenMicAsync();
        _openMic.Speak();

        await vm.TapMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingOpenMic);

        vm.State.ShouldBe(RavenState.AttendingPaused);
        vm.Caption.ShouldBe("Open mic paused");
        await _dictation.DidNotReceive().TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>());
    }

    // Review focus 4
    [Fact]
    public async Task A_dead_microphone_warns_once_and_pauses_and_a_press_starts_it_again()
    {
        var vm = await InOpenMicAsync();

        _openMic.Fail();
        await WithinAsync(vm.PendingOpenMic);

        vm.MicMode.ShouldBe(MicMode.OpenMic);
        vm.State.ShouldBe(RavenState.AttendingPaused);
        vm.Log.Count(e => e.Kind == RavenLogKind.Warning).ShouldBe(1);

        await vm.TapMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingOpenMic);
        _openMic.Listening.ShouldBe(Headset.Id);
    }

    // Review focus 5
    [Fact]
    public async Task A_held_push_to_talk_recording_is_finished_when_the_user_switches_to_Open_mic()
    {
        var vm = await NewOpenMicVmAsync();
        vm.PressMic(TalkInput.Hotkey);
        await WithinAsync(vm.PendingStart);
        Speak();
        _time.Advance(Hold);

        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);
        await WithinAsync(vm.PendingTranscriptions);
        await vm.ReleaseMicAsync(TalkInput.Hotkey);

        _recorder.Received(1).Stop();
        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.You);
        _openMic.Listening.ShouldBe(Headset.Id, "the late release must not pause Open mic");
    }
}
```

`Type`, `Until`, `WithinAsync` and `Speak` are the helpers the other partial files of `RavenPanelViewModelTests` already define; `BrainText` comes from `CodeSwitchX.Conductor` (add the `using`). If the barge-in test cannot hold `Speaking` long enough to assert, gate the speech as `RavenPanelViewModelTests.Speech.cs` does with `_speech.Gate`.

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet build tests/CodeSwitchX.UI.Tests` → Expected: build errors (`MicMode`, `PendingOpenMic`, `openMic` parameter).

- [ ] **Step 4: Implement in `RavenPanelViewModel`**

Add `using CodeSwitchX.Voice.Listening;`. Then:

1. **Fields** (after `_voiceNote`):

```csharp
    private readonly IOpenMic? _openMic;

    /// <summary>Open mic's microphone is open (UI thread).</summary>
    private bool _attending;

    /// <summary>Open mic is paused by the user, or by a failure until the next press.</summary>
    private bool _attendPaused;

    /// <summary>The user is talking in Open mic: <see cref="IOpenMic.SpeechStarted"/> came, the turn has not ended.</summary>
    private bool _openSpeech;

    /// <summary>Each start or stop of Open mic: one that finishes after a newer one began is undone (UI thread).</summary>
    private long _openMicRun;

    /// <summary>The microphone Open mic listens on, to restart it on another.</summary>
    private string? _openMicDevice;

    /// <summary>Open mic was switched on before the first microphone listing arrived (the stored mode at startup): it
    /// starts once the listing is applied.</summary>
    private bool _openMicWaitsForList;

    /// <summary>Open mic's own silent-microphone watch: one warning per start, as push to talk gives one per recording.</summary>
    private readonly SilentMicWatch _openSilence = new();
```

2. **Constructor**: add the parameter `IOpenMic? openMic = null` after `teller`, and at the end of the body:

```csharp
        _openMic = openMic;
        if (openMic is not null)
        {
            openMic.SpeechStarted += (_, _) => _dispatcher.Post(OnOpenSpeech);
            openMic.TurnEnded += (_, clip) => _dispatcher.Post(() => OnOpenTurn(clip));
            openMic.Heard += (_, block) => _dispatcher.Post(() => OnOpenHeard(block));
            openMic.Failed += (_, error) => _dispatcher.Post(() => OnOpenMicFailed(error));
        }
```

3. **Properties** (after `_speakNews`):

```csharp
    /// <summary>Push to talk, or Open mic. The panel owns it; the shell stores it in the settings.</summary>
    [ObservableProperty]
    private MicMode _micMode;

    /// <summary>Talking over Raven stops it in Open mic; off, Open mic ignores speech while Raven speaks (Raven heard on
    /// speakers). The shell keeps it in step with Settings.</summary>
    [ObservableProperty]
    private bool _bargeIn = true;

    /// <summary>The mic button's name and tooltip: what a press does in the mode the panel is in.</summary>
    public string MicButtonName => MicMode == MicMode.PushToTalk ? "Push to talk"
        : _attendPaused ? "Resume Open mic" : "Pause Open mic";

    /// <summary>The last start or stop of Open mic; completed when none runs.</summary>
    internal Task PendingOpenMic { get; private set; } = Task.CompletedTask;
```

4. **Mode changes**:

```csharp
    partial void OnMicModeChanged(MicMode value)
    {
        if (value == MicMode.OpenMic)
        {
            if (_openMic is null)
            {
                AddEntry(RavenLogKind.Warning, "Open mic is not available.");
                MicMode = MicMode.PushToTalk;
                return;
            }

            if (_capturing)
            {
                // A held push-to-talk recording ends as a release would; its key's late release is forgotten.
                ForgetHold();
                _ = BeginStop();
            }

            _attendPaused = false;
            PendingOpenMic = StartOpenMicAsync();
        }
        else
        {
            StopOpenMic();
            _attendPaused = false;
        }

        OnPropertyChanged(nameof(MicButtonName));
        UpdateState();
    }

    partial void OnBargeInChanged(bool value) => UpdateIgnoreSpeech();

    private void UpdateIgnoreSpeech()
    {
        if (_openMic is not null)
        {
            _openMic.IgnoreSpeech = _speaking && !BargeIn;
        }
    }
```

5. **Start and stop**:

```csharp
    /// <summary>
    /// Opens Open mic on the selected microphone: downloads its models first if they are missing (the log shows the
    /// progress), waits for a microphone listing still on its way, and starts the listener off the UI thread. A failed
    /// download or a model that will not load goes back to push to talk with a warning; a microphone that will not open
    /// leaves Open mic paused, for a press to try again.
    /// </summary>
    private async Task StartOpenMicAsync()
    {
        var run = ++_openMicRun;
        UpdateState();
        if (!_openMic!.ModelsPresent && !await DownloadOpenMicModelsAsync())
        {
            if (run == _openMicRun)
            {
                MicMode = MicMode.PushToTalk;
            }

            return;
        }

        try
        {
            await PendingRefresh;
        }
        catch (Exception)
        {
            // Logged where it happened; whatever was applied is what there is.
        }

        if (run != _openMicRun)
        {
            return;
        }

        if (SelectedMicrophone is not { } mic)
        {
            if (_appliedListing == 0)
            {
                _openMicWaitsForList = true; // the startup listing has not arrived: ApplyDevices starts it
                return;
            }

            AddEntry(RavenLogKind.Warning, NoMicrophoneWarning);
            PauseOpenMic();
            return;
        }

        try
        {
            await Task.Run(() => _openMic.Start(mic.Id));
        }
        catch (ListeningModelException ex)
        {
            _logger.LogWarning(ex, "Open mic's models would not load");
            AddEntry(RavenLogKind.Warning, $"Open mic could not start: {ex.Message}. It is downloaded again the next time you switch to Open mic.");
            if (run == _openMicRun)
            {
                MicMode = MicMode.PushToTalk;
            }

            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open mic could not open {Microphone}", mic.Name);
            AddEntry(RavenLogKind.Warning, WarningFor(ex is MicrophoneException failure ? failure.Kind : MicrophoneFailureKind.Unavailable, mic));
            if (run == _openMicRun)
            {
                PauseOpenMic();
            }

            return;
        }

        if (run != _openMicRun)
        {
            _ = Task.Run(_openMic.Stop); // switched away while it opened
            return;
        }

        _attending = true;
        _openMicDevice = mic.Id;
        _openSilence.Reset();
        UpdateIgnoreSpeech();
        UpdateState();
    }

    private async Task<bool> DownloadOpenMicModelsAsync()
    {
        const string Prefix = "Downloading Open mic's models (11 MB)… ";
        var entry = AddEntry(RavenLogKind.Note, Prefix + "0%");
        var progress = new PostedPercent(_dispatcher, percent => entry.Text = $"{Prefix}{percent}%");
        try
        {
            await _openMic!.DownloadModelsAsync(progress, CancellationToken.None);
            entry.Text = "Open mic's models downloaded.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open mic's models could not be downloaded");
            ReplaceEntry(entry, RavenLogKind.Warning, $"Open mic's models could not be downloaded: {ex.Message}. Back to push to talk.");
            return false;
        }
    }

    /// <summary>Closes Open mic's microphone (off the UI thread) and drops a turn being spoken.</summary>
    private void StopOpenMic()
    {
        _openMicRun++;
        _openSpeech = false;
        if (_attending && _openMic is not null)
        {
            _attending = false;
            _openMicDevice = null;
            var stop = Task.Run(_openMic.Stop);
            PendingOpenMic = stop;
        }
    }

    private void PauseOpenMic()
    {
        StopOpenMic();
        _attendPaused = true;
        OnPropertyChanged(nameof(MicButtonName));
        UpdateState();
    }

    private void ToggleOpenMicPause()
    {
        if (_attendPaused)
        {
            _attendPaused = false;
            OnPropertyChanged(nameof(MicButtonName));
            PendingOpenMic = StartOpenMicAsync();
        }
        else
        {
            PauseOpenMic();
        }
    }
```

6. **Presses in Open mic** — first lines of `Press` and `ReleaseMicAsync`:

```csharp
        if (MicMode == MicMode.OpenMic)
        {
            ToggleOpenMicPause(); // the button and the hotkey pause and resume Open mic
            return Task.CompletedTask;
        }
```

In `ReleaseMicAsync`, before the existing `if`:

```csharp
        if (MicMode == MicMode.OpenMic && _heldInputs.Count == 0)
        {
            return Task.CompletedTask; // a press already paused or resumed; a release does nothing
        }
```

(A key held from push to talk across the switch is still in `_heldInputs` until `ForgetHold`, which the switch calls, so its release lands here and does nothing.)

7. **Talking** — move the start of `StartRecording` into one method both modes call. In `StartRecording` replace the four lines `_digest?.Cancel(); _voice.Hush(); _voice.Expect(); _brain.WarmUp();` (and their comment) with `UserStartsTalking();`, and add:

```csharp
    /// <summary>
    /// The user talks (a press, or half a second of speech in Open mic): Raven stops speaking at once, and what it was
    /// saying is not said after. A digest stops too, also one that has not begun to speak; an answer is interrupted only by
    /// a question: talk that brings none leaves it to be written. The brain warms up while the user talks.
    /// </summary>
    private void UserStartsTalking()
    {
        _digest?.Cancel();
        _voice.Hush();
        _voice.Expect();
        _brain.WarmUp();
    }

    private void OnOpenSpeech()
    {
        if (!_attending)
        {
            return; // stopped or paused since
        }

        _openSpeech = true;
        UserStartsTalking();
        _vocabularyFetch = Task.Run(FetchVocabularyAsync);
        UpdateState();
    }

    /// <summary>The turn is over: its clip joins the transcription queue as a released recording's does.</summary>
    private void OnOpenTurn(float[] clip)
    {
        if (!_attending || !_openSpeech)
        {
            return;
        }

        _openSpeech = false;
        _pending++;
        var length = TimeSpan.FromSeconds(clip.Length / 16_000.0);
        var heard = new SpeechReading(true, 0, 0, length); // the detector heard the speech
        var number = ++_clipsQueued;
        var turn = TranscribeInTurnAsync(_pipeline, number, Task.FromResult<RecordedClip?>(new RecordedClip(clip, length)), heard,
            SelectedMicrophone?.Name, _vocabularyFetch, _time.GetUtcNow(), quiet: true);
        _pipeline = turn;
        UpdateState();
    }

    /// <summary>The orb's level, and the same watch push to talk keeps: a microphone that sends nothing is warned of once per start.</summary>
    private void OnOpenHeard(CapturedFrames block)
    {
        if (!_attending)
        {
            return;
        }

        if (State is RavenState.Attending or RavenState.Listening)
        {
            Level = AudioMath.LevelOf(block.Rms);
        }

        if (_openSilence.Step(block.Rms, TimeSpan.FromSeconds(block.Samples16k.Length / 16_000.0)) == SignalEvent.Silent)
        {
            AddEntry(RavenLogKind.Warning, $"No sound from {SelectedMicrophone?.Name}. Check that it isn't muted.");
        }
    }

    private void OnOpenMicFailed(MicrophoneException error)
    {
        _logger.LogWarning(error, "Open mic's microphone failed");
        if (!_attending)
        {
            return;
        }

        AddEntry(RavenLogKind.Warning, WarningFor(error.Kind, SelectedMicrophone));
        PauseOpenMic();
        _ = RefreshMicrophonesAsync();
    }
```

8. **`TranscribeInTurnAsync`** gets a last parameter `bool quiet = false`. Where it adds the "too short" note and where the text comes back empty, Open mic only logs:

```csharp
            if (clip.Length < MinimumClip)
            {
                if (quiet)
                {
                    _logger.LogInformation("Open mic's turn was too short to transcribe ({Seconds:0.00} s)", clip.Length.TotalSeconds);
                    return;
                }
                …existing note…
            }
```

and after `var text = result.Text.Trim();`:

```csharp
            if (text.Length == 0 && quiet)
            {
                _logger.LogInformation("Open mic's turn of {Seconds:0.0} s had no words", clip.Length.TotalSeconds);
            }
```

9. **Speaking** — in `OnSpeakingChanged`, after `_speaking = speaking;` add `UpdateIgnoreSpeech();`.

10. **The first listing** — in `ApplyDevices`, right after the `try { … SelectedMicrophone = choice.Device; } finally { _refreshing = false; }` block and before `if (preferred is null || …) return;`:

```csharp
        if (_openMicWaitsForList && MicMode == MicMode.OpenMic)
        {
            _openMicWaitsForList = false;
            PendingOpenMic = StartOpenMicAsync();
        }
```

11. **Microphone changes** — at the end of `OnSelectedMicrophoneChanged`:

```csharp
        if (_attending && value is not null && value.Id != _openMicDevice)
        {
            StopOpenMic();
            PendingOpenMic = StartOpenMicAsync();
        }
```

12. **The floor** — `FloorIsFree` gains `&& !_openSpeech`.

13. **`UpdateState`** — after the `_capturing` block:

```csharp
        if (_openSpeech)
        {
            State = RavenState.Listening;
            Caption = "Listening…";
            return;
        }
```

and the idle branch becomes:

```csharp
        if (_pending == 0)
        {
            if (MicMode == MicMode.OpenMic)
            {
                State = _attendPaused ? RavenState.AttendingPaused : RavenState.Attending;
                Caption = _attendPaused ? "Open mic paused" : "Open mic";
            }
            else
            {
                State = RavenState.Idle;
                Caption = IdleCaption;
            }

            ScheduleNews();
            return;
        }
```

(`Level = 0;` above it stays: the room level comes back with the next block.)

- [ ] **Step 5: Run the panel tests**

Run: `dotnet build tests/CodeSwitchX.UI.Tests && tests/CodeSwitchX.UI.Tests/bin/Debug/net10.0-windows/CodeSwitchX.UI.Tests.exe --filter-class "CodeSwitchX.UI.Tests.Raven.RavenPanelViewModelTests"`
Expected: all pass, the 14 new ones included.

- [ ] **Step 6: Commit**

```bash
git add src/CodeSwitchX.UI/Raven tests/CodeSwitchX.UI.Tests/Raven
git commit -m "feat: Raven's panel listens in Open mic: speech hushes Raven, a finished turn is asked, the mic button pauses (#75)"
git push
```

---

### Task 9: The picker, the settings and the shell

**Files:**
- Modify: `src/CodeSwitchX.UI/Settings/SettingKeys.cs`, `SettingsViewModel.cs`, `SettingsView.xaml`, `src/CodeSwitchX.UI/Shell/ShellViewModel.cs`, `src/CodeSwitchX.UI/Raven/RavenPanelView.xaml`
- Test: `tests/CodeSwitchX.UI.Tests/Shell/ShellViewModelTests.cs`

**Interfaces:**
- Consumes: `RavenPanelViewModel.MicMode`, `BargeIn`, `MicButtonName` (Task 8).
- Produces: `SettingKeys.RavenMicMode = "raven.micMode"`, `SettingKeys.RavenBargeIn = "raven.bargeIn"`; `SettingsViewModel.RavenMicMode` (string), `SettingsViewModel.RavenBargeIn` (bool, default true).

- [ ] **Step 1: Write the failing tests**

Append to `ShellViewModelTests`:

```csharp
    [Fact]
    public async Task The_mic_mode_is_restored_and_stored()
    {
        _h.Settings.GetAsync<string?>(SettingKeys.RavenMicMode, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("OpenMic"));

        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.Raven.MicMode.ShouldBe(MicMode.OpenMic);

        _h.Shell.Raven.MicMode = MicMode.PushToTalk;

        await _h.Shell.Settings.FlushSavesAsync(CancellationToken.None);
        await _h.Settings.Received(1).SetAsync(SettingKeys.RavenMicMode, "PushToTalk", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_stored_mic_mode_reads_as_push_to_talk()
    {
        _h.Settings.GetAsync<string?>(SettingKeys.RavenMicMode, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("Shout"));

        await _h.Shell.InitializeAsync(CancellationToken.None);

        _h.Shell.Raven.MicMode.ShouldBe(MicMode.PushToTalk);
    }

    [Fact]
    public async Task Barge_in_follows_the_setting()
    {
        _h.Settings.GetAsync<bool?>(SettingKeys.RavenBargeIn, Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(false));

        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.Raven.BargeIn.ShouldBeFalse();

        _h.Shell.Settings.RavenBargeIn = true;

        _h.Shell.Raven.BargeIn.ShouldBeTrue();
        await _h.Shell.Settings.FlushSavesAsync(CancellationToken.None);
        await _h.Settings.Received(1).SetAsync(SettingKeys.RavenBargeIn, true, Arg.Any<CancellationToken>());
    }
```

(Add `using CodeSwitchX.UI.Raven;` if the file lacks it. The shell harness builds the panel without an `IOpenMic`: switching to Open mic there warns "Open mic is not available." and falls back. If the harness can supply one, register a `FakeOpenMic`; otherwise assert the restored mode only before the panel processes it — read the harness (`_h`) first and pick the one that fits; the panel's own tests cover the start.)

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/CodeSwitchX.UI.Tests` → Expected: build errors (`RavenMicMode`, `RavenBargeIn`).

- [ ] **Step 3: Settings**

`SettingKeys.cs`, after `RavenSpeakNews`:

```csharp
    public const string RavenMicMode = "raven.micMode";
    public const string RavenBargeIn = "raven.bargeIn";
```

`SettingsViewModel.cs`: beside `_ravenSpeakNews`:

```csharp
    /// <summary>The mic mode as stored ("PushToTalk", "OpenMic"); the panel owns it (see <see cref="Raven.RavenPanelViewModel.MicMode"/>).</summary>
    [ObservableProperty] private string _ravenMicMode = nameof(Raven.MicMode.PushToTalk);

    [ObservableProperty] private bool _ravenBargeIn = true;
```

in the load beside `RavenSpeakNews`:

```csharp
            RavenMicMode = await LoadOrDefaultAsync<string?>(SettingKeys.RavenMicMode, "Raven's mic mode", ct) ?? nameof(Raven.MicMode.PushToTalk);
            RavenBargeIn = await LoadOrDefaultAsync<bool?>(SettingKeys.RavenBargeIn, "whether talking over Raven stops it", ct) ?? true;
```

and beside `OnRavenSpeakNewsChanged`:

```csharp
    partial void OnRavenMicModeChanged(string value) => Persist(SettingKeys.RavenMicMode, value);

    partial void OnRavenBargeInChanged(bool value) => Persist(SettingKeys.RavenBargeIn, value);
```

`SettingsView.xaml`, after the "Speak chat news" CheckBox:

```xml
      <CheckBox Margin="140,6,0,0" IsChecked="{Binding RavenBargeIn}" AutomationProperties.Name="Stop Raven when I talk over it"
                Content="Stop Raven when I talk over it"
                ToolTip="In Open mic, talking while Raven speaks stops it. Turn this off if Raven hears its own voice through your speakers: Open mic then ignores what it hears while Raven speaks." />
```

- [ ] **Step 4: The shell**

In `ShellViewModel`, after `Raven.SpeakNews = Settings.RavenSpeakNews;`:

```csharp
        Raven.BargeIn = Settings.RavenBargeIn;
        Raven.MicMode = Enum.TryParse<MicMode>(Settings.RavenMicMode, out var mode) && Enum.IsDefined(mode) ? mode : MicMode.PushToTalk;
```

In the `Raven.PropertyChanged` handler add:

```csharp
            else if (e.PropertyName == nameof(RavenPanelViewModel.MicMode))
            {
                Settings.RavenMicMode = Raven.MicMode.ToString();
            }
```

In the `Settings.PropertyChanged` handler add:

```csharp
            else if (e.PropertyName == nameof(SettingsViewModel.RavenBargeIn))
            {
                Raven.BargeIn = Settings.RavenBargeIn;
            }
```

(turn the existing single `if` into `if … else if`).

- [ ] **Step 5: The picker and the mic button**

In `RavenPanelView.xaml`, inside the right-hand `StackPanel` (the one with the "Mic" label), above the label:

```xml
                <!-- Push to talk or Open mic: two radio buttons styled as one switch. -->
                <StackPanel Orientation="Horizontal" Margin="0,0,0,8">
                  <RadioButton Style="{StaticResource ModeSwitch}" Content="Push to talk" AutomationProperties.Name="Push to talk mode"
                               IsChecked="{Binding MicMode, Converter={StaticResource EnumIs}, ConverterParameter=PushToTalk}" />
                  <RadioButton Style="{StaticResource ModeSwitch}" Content="Open mic" AutomationProperties.Name="Open mic mode"
                               IsChecked="{Binding MicMode, Converter={StaticResource EnumIs}, ConverterParameter=OpenMic}" />
                </StackPanel>
```

Add to the view's resources a `ModeSwitch` style (a `RadioButton` template: a `Border` with `CornerRadius="10"`, `Padding="10,3"`, `FontSize="12"`; unchecked `Background="#1F2937"` `Foreground="#9CA3AF"`, checked `Background="#2662D0E8"` `Foreground="#62D0E8"` `BorderBrush="#62D0E8"`), and an `EnumIs` converter: if the project has no enum-to-bool converter (search `IValueConverter` under `src/CodeSwitchX.UI/Infrastructure`), create `src/CodeSwitchX.UI/Infrastructure/EnumIsConverter.cs`:

```csharp
using System.Globalization;
using System.Windows.Data;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>True when the bound enum has the value named by the parameter; checking the radio button sets that value.</summary>
public sealed class EnumIsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter as string;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Enum.Parse(targetType, (string)parameter) : Binding.DoNothing;
}
```

and declare it in the resources as `<infra:EnumIsConverter x:Key="EnumIs" />` with the matching `xmlns:infra` if not declared.

On both mic buttons (panel and rail) bind the name and tooltip: in the `MicButton` style replace the two setters with

```xml
      <Setter Property="ToolTip" Value="{Binding MicButtonToolTip}" />
      <Setter Property="AutomationProperties.Name" Value="{Binding MicButtonName}" />
```

and add to the view model, beside `MicButtonName`:

```csharp
    public string MicButtonToolTip => MicMode == MicMode.PushToTalk ? MicToolTip : $"{MicButtonName} ({HotkeyService.PushToTalk.Keys})";
```

raising `OnPropertyChanged(nameof(MicButtonToolTip))` wherever `MicButtonName` is raised. In the `MicButton` template add a trigger for the paused look:

```xml
              <DataTrigger Binding="{Binding State}" Value="AttendingPaused">
                <Setter Property="Content" Value="&#xE769;" />
                <Setter Property="Opacity" Value="0.7" />
              </DataTrigger>
```

(`E769` is Segoe MDL2's Pause glyph; the button then shows that a press resumes.)

- [ ] **Step 6: Run the tests and build**

Run: `dotnet build tests/CodeSwitchX.UI.Tests && tests/CodeSwitchX.UI.Tests/bin/Debug/net10.0-windows/CodeSwitchX.UI.Tests.exe`
Expected: all UI tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/CodeSwitchX.UI tests/CodeSwitchX.UI.Tests
git commit -m "feat: a Push to talk | Open mic switch in Raven's panel and a barge-in setting, both kept in the settings (#75)"
git push
```

---

### Task 10: On-screen check, full suite, PR

**Files:**
- Modify: `src/CodeSwitchX.UI/App.xaml.cs`
- Create: `docs/review/screenshots/issue75-*.png`

- [ ] **Step 1: A file-fed microphone for debug builds**

In `App.xaml.cs`, after `services.AddCodeSwitchXVoice(…)`:

```csharp
#if DEBUG
        // The on-screen check of Open mic: nobody but the user can talk into their microphone, so a debug build can play
        // a 16 kHz mono 16-bit PCM file into Open mic instead.
        if (Environment.GetEnvironmentVariable("CODESWITCHX_OPEN_MIC_FILE") is { Length: > 0 } openMicFile)
        {
            services.AddSingleton<IMicrophoneStream>(_ => new FileMicrophoneStream(openMicFile, realTime: true));
        }
#endif
```

(`using CodeSwitchX.Voice.Listening;`.)

- [ ] **Step 2: Full suite**

Run: `dotnet build CodeSwitchX.slnx` then every test exe under `tests/*/bin/Debug/net10.0-windows/*.Tests.exe`.
Expected: all pass, 0 warnings. Record the totals for the PR.

- [ ] **Step 3: On screen**

Following the memory "Running the app from Claude": ask before closing the user's stable build; strip `ELECTRON_RUN_AS_NODE`; capture with PrintWindow only; never inject keys unless the app is in front.

1. Make the PCM: the Task 6 explicit test's sentence with a 1 s pause, then 4 s of silence (write it to the scratchpad).
2. Start the branch build with `CODESWITCHX_OPEN_MIC_FILE` set; switch to Open mic with UI Automation (the "Open mic mode" radio button).
3. Capture: the light ring waiting (`issue75-waiting.png`), Listening while the file speaks, the one question in the log after it (`issue75-turn.png`), paused after invoking the mic button (`issue75-paused.png`).
4. Check the app log for one turn, not two, and the time from the end of the turn to the transcript.

- [ ] **Step 4: Commit, push, PR**

```bash
git add src/CodeSwitchX.UI/App.xaml.cs docs/review/screenshots
git commit -m "test: a debug build can feed Open mic from a file, and screenshots of Open mic for #75"
git push
gh pr create --title "Raven: Open mic mode (#75)" --body-file <scratchpad>/pr75.md
```

The PR body: what the user can do now, the done-when checks with how each was verified (the live microphone check is the user's), the models and their licences, the test totals, the screenshots, "Closes #75", and the `🤖 Generated with [Claude Code](https://claude.com/claude-code)` line.
