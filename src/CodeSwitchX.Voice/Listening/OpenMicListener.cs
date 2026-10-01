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
