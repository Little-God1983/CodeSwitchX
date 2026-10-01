using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Listening;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>Keeps runs as the real listener does: a start ends the run before it, a stop of an old run does nothing.</summary>
internal sealed class FakeOpenMic : IOpenMic
{
    private readonly object _gate = new();
    private OpenMicRun? _run;
    private OpenMicRun? _opening;

    public bool ModelsPresent { get; set; } = true;

    public Exception? DownloadFails { get; set; }

    public Exception? StartFails { get; set; }

    /// <summary>When set, Start waits for it before it opens: a start still in flight.</summary>
    public TaskCompletionSource? StartGate { get; set; }

    /// <summary>When set, the download waits for it: a download still running.</summary>
    public TaskCompletionSource? DownloadGate { get; set; }

    public int Downloads { get; private set; }

    /// <summary>The device it listens on now; null while stopped.</summary>
    public string? Listening
    {
        get
        {
            lock (_gate)
            {
                return _run?.DeviceId;
            }
        }
    }

    /// <summary>A start is under way: it has begun and not returned.</summary>
    public bool Opening => Volatile.Read(ref _opening) is not null;

    public List<string> Started { get; } = [];

    public bool IgnoreSpeech { get; set; }

    public event EventHandler<OpenMicRun>? SpeechStarted;

    public event EventHandler<OpenMicTurn>? TurnEnded;

    public event EventHandler<CapturedBlock>? Heard;

    public event EventHandler<OpenMicRun>? Failed;

    public async Task DownloadModelsAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Downloads++;
        if (DownloadGate is { } gate)
        {
            await gate.Task;
        }

        if (DownloadFails is { } error)
        {
            throw error;
        }

        ModelsPresent = true;
        progress?.Report(1);
    }

    public OpenMicRun Start(string deviceId)
    {
        if (StartFails is { } error)
        {
            throw error;
        }

        var run = new OpenMicRun(deviceId);
        Volatile.Write(ref _opening, run);
        StartGate?.Task.Wait();
        lock (_gate)
        {
            _run = run.Failure is null ? run : null; // one that died while it opened has stopped itself
            Started.Add(deviceId);
        }

        Volatile.Write(ref _opening, null);
        return run;
    }

    public void Stop(OpenMicRun run)
    {
        lock (_gate)
        {
            if (run == _run)
            {
                _run = null;
            }
        }
    }

    /// <summary>The open run; null while stopped.</summary>
    public OpenMicRun? Run
    {
        get
        {
            lock (_gate)
            {
                return _run;
            }
        }
    }

    /// <summary>Speech in the open run, or in <paramref name="run"/>: an old one, raising late.</summary>
    public void Speak(OpenMicRun? run = null) => SpeechStarted?.Invoke(this, run ?? Run!);

    public void EndTurn(OpenMicRun? run = null, double seconds = 2) =>
        TurnEnded?.Invoke(this, new OpenMicTurn(run ?? Run!, new float[(int)(seconds * 16_000)]));

    /// <summary>A batch of 50 ms at this level, as the listener raises them.</summary>
    public void Hear(float rms) => Heard?.Invoke(this, new CapturedBlock(rms, TimeSpan.FromMilliseconds(50)));

    /// <summary>The detector failed mid-turn: the listener ends the turn with an empty clip.</summary>
    public void LoseTurn() => TurnEnded?.Invoke(this, new OpenMicTurn(Run!, []));

    /// <summary>The microphone of <paramref name="run"/>, else of the run being opened, else of the open one, dies; the
    /// listener stops that run itself.</summary>
    public void Fail(OpenMicRun? run = null)
    {
        run ??= Volatile.Read(ref _opening) ?? Run!;
        run.Fail(new MicrophoneException(MicrophoneFailureKind.Missing, "gone", new Exception("gone")));
        Failed?.Invoke(this, run);
        Stop(run);
    }
}
