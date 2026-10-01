using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Listening;

namespace CodeSwitchX.UI.Tests.Raven;

internal sealed class FakeOpenMic : IOpenMic
{
    public bool ModelsPresent { get; set; } = true;

    public Exception? DownloadFails { get; set; }

    public Exception? StartFails { get; set; }

    /// <summary>When set, Start waits for it before it opens: a start still in flight.</summary>
    public TaskCompletionSource? StartGate { get; set; }

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

        StartGate?.Task.Wait();
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
