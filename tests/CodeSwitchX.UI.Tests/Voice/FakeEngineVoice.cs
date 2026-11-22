using System.Runtime.CompilerServices;
using CodeSwitchX.Voice.Speech;
using LittleGod.Speech;
using LittleGod.Speech.Kokoro;
using LittleGod.Speech.QwenTts;
using LittleGod.Speech.Sidecar;
using Microsoft.Extensions.AI;

namespace CodeSwitchX.UI.Tests.Voice;

/// <summary>One engine as <see cref="SpeechEngines"/> drives it: says what the test reports, and counts what it is asked.</summary>
internal sealed class FakeEngineVoice(SpeechEngine engine) : ISpeechEngine
{
    public SidecarEngine Engine { get; } = engine == SpeechEngine.Kokoro ? KokoroEngine.Definition : QwenTtsEngine.Definition;

    public string Model { get; set; } = (engine == SpeechEngine.Kokoro ? KokoroEngine.Definition : QwenTtsEngine.Definition).DefaultModel;

    public string Voice { get; set; } = (engine == SpeechEngine.Kokoro ? KokoroEngine.Definition : QwenTtsEngine.Definition).DefaultVoice;

    public TextToSpeechStatus Status { get; private set; } = TextToSpeechStatus.Off;

    public event EventHandler<TextToSpeechStatus>? StatusChanged;

    public bool IsInstalled { get; set; }

    public int Installs { get; private set; }

    public int Stops { get; private set; }

    public void Report(TextToSpeechStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public void Prepare(bool install)
    {
    }

    public void Install() => Installs++;

    public void Stop() => Stops++;

    public void CheckInstall() => Report(IsInstalled ? TextToSpeechStatus.Off : new TextToSpeechStatus(TextToSpeechState.NotInstalled));

    public void Recover()
    {
    }

    public bool SpeaksSentencesTogether => true;

    public async IAsyncEnumerable<SpeechChunk> SpeakAsync(string text, SpeechRequest? request, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield break;
    }

    public Task<TextToSpeechResponse> GetAudioAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TextToSpeechResponse([]));

    public async IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(string text, TextToSpeechOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
