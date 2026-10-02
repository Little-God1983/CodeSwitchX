using System.Runtime.CompilerServices;
using CodeSwitchX.Voice.Speech;

namespace CodeSwitchX.UI.Tests.Voice;

/// <summary>One engine as <see cref="SpeechEngines"/> drives it: says what the test reports, and counts what it is asked.</summary>
internal sealed class FakeEngineVoice(SpeechEngine engine) : ISpeechEngineVoice
{
    public SpeechEngine Engine { get; } = engine;

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

    public async IAsyncEnumerable<SpeechChunk> SpeakAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield break;
    }
}
