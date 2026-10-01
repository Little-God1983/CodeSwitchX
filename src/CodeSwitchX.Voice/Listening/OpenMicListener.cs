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

    /// <summary>The microphone died; the listener then stops itself, so a consumer need not call Stop (and must never from inside this handler). On the capture thread.</summary>
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
    /// <summary>The listener whose handler is running on this thread, if any; plain thread state, so it never leaks into tasks a handler starts.</summary>
    [ThreadStatic]
    private static OpenMicListener? _workerOf;

    private readonly object _gate = new();
    private TurnDetector? _detector;
    private IVoiceActivity? _vad;
    private ITurnEnd? _turnEnd;
    private Channel<CapturedFrames>? _queue;
    private CancellationTokenSource? _cts;
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
            StopRun();
            _detector ??= LoadDetector();
            _detector.Reset();
            _detector.IgnoreSpeech = _ignoreSpeech;
            // About ten seconds of 10 ms blocks: a Smart Turn call is milliseconds, so this only fills if the PC stalls.
            var queue = _queue = Channel.CreateBounded<CapturedFrames>(new BoundedChannelOptions(1000)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropWrite,
            });
            var cts = _cts = new CancellationTokenSource();
            var detector = _detector;
            _worker = Task.Run(() => WorkAsync(queue, detector, cts.Token));
            try
            {
                _stream.Start(deviceId);
            }
            catch
            {
                StopRun(); // no idle worker or queue is left behind
                throw;
            }
        }
    }

    public void Stop() => StopIfCurrent(_cts, always: true);

    /// <summary>Stops the run that was current when the caller looked; a run started since is left alone.</summary>
    private void StopIfCurrent(CancellationTokenSource? run, bool always)
    {
        lock (_gate)
        {
            if (run == _cts && (always || run is not null))
            {
                StopRun();
            }
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

    /// <summary>
    /// Ends the current run. Nothing is raised after this: the run's token is cancelled before the queue is completed. A
    /// worker that will not end is abandoned with the detector it is stepping, which is never reused; the next start loads
    /// fresh models and the old ones are disposed when that worker finally ends.
    /// </summary>
    private void StopRun()
    {
        _stream.Stop();
        _cts?.Cancel();
        _queue?.Writer.TryComplete();
        _queue = null;
        var worker = _worker;
        if (_workerOf == this)
        {
            // A handler called Stop from the worker thread: waiting would wait on itself. The worker ends by its token.
            _detector?.Reset();
            return;
        }

        if (worker.Wait(StopTimeout))
        {
            _cts?.Dispose();
            _cts = null;
            _detector?.Reset();
            return;
        }

        _logger.LogWarning("Open mic's worker did not stop within {Seconds} s; its models are replaced", StopTimeout.TotalSeconds);
        var vad = _vad;
        var turnEnd = _turnEnd;
        _detector = null;
        _vad = null;
        _turnEnd = null;
        var cts = _cts;
        _cts = null;
        _ = worker.ContinueWith(_ =>
        {
            cts?.Dispose();
            vad?.Dispose();
            turnEnd?.Dispose();
        }, TaskScheduler.Default);
    }

    private TurnDetector LoadDetector()
    {
        var vad = Load(ListeningModelStore.Silero, _newVad);
        ITurnEnd turnEnd;
        try
        {
            turnEnd = Load(ListeningModelStore.SmartTurn, _newTurnEnd);
        }
        catch
        {
            vad.Dispose();
            throw;
        }

        _vad = vad;
        _turnEnd = turnEnd;
        return new TurnDetector(vad, turnEnd, _logger);
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

    private void OnFailed(object? sender, MicrophoneException error)
    {
        var run = _cts;
        Raise(() => Failed?.Invoke(this, error), "Failed", onWorker: false);

        // The listener closes itself, off the capture thread: stopping from here would wait on the thread that is calling.
        // Only the run that failed: the consumer may have started another by the time this runs.
        _ = Task.Run(() => StopIfCurrent(run, always: false));
    }

    /// <summary>A handler's bug must not end listening. While a worker's handler runs, a Stop from it must not wait on its own thread.</summary>
    private void Raise(Action raise, string name, bool onWorker = true)
    {
        if (onWorker)
        {
            _workerOf = this;
        }

        try
        {
            raise();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A handler of Open mic's {Event} event threw", name);
        }
        finally
        {
            if (onWorker)
            {
                _workerOf = null;
            }
        }
    }

    private async Task WorkAsync(Channel<CapturedFrames> queue, TurnDetector detector, CancellationToken token)
    {
        var frame = new float[SileroVad.FrameSamples];
        var filled = 0;
        var stepLogged = false;
        try
        {
            await foreach (var block in queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                Raise(() => Heard?.Invoke(this, block), "Heard");
                var samples = block.Samples16k.AsSpan();
                while (samples.Length > 0 && !token.IsCancellationRequested)
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
                    TurnEvent? result = null;
                    try
                    {
                        result = detector.Step(frame);
                    }
                    catch (Exception ex)
                    {
                        if (!stepLogged)
                        {
                            stepLogged = true;
                            _logger.LogError(ex, "Open mic's turn detector failed; it starts over");
                        }

                        detector.Reset();
                    }

                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    switch (result)
                    {
                        case TurnEvent.Started:
                            Raise(() => SpeechStarted?.Invoke(this, EventArgs.Empty), "SpeechStarted");
                            break;
                        case TurnEvent.Ended ended:
                            Raise(() => TurnEnded?.Invoke(this, ended.Clip), "TurnEnded");
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open mic's worker failed");
        }
    }
}
