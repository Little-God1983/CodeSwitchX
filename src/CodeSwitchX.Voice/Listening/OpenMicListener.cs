using System.Threading.Channels;
using CodeSwitchX.Voice.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace CodeSwitchX.Voice.Listening;

/// <summary>
/// Open mic: listens until stopped and says when the user starts talking and when their turn is over.
/// <para>
/// Runs: each <see cref="Start"/> begins a new run (ending the one before) and returns it; <see cref="Stop"/> ends a run
/// only while it is still the current one, so a late stop of an old run never closes a newer one. Start and Stop are
/// serialised by the listener and may come from any thread but the UI thread (they wait for the microphone). The events
/// that belong to a run carry it, so a consumer can drop what an old run raised after it moved on.
/// </para>
/// </summary>
public interface IOpenMic
{
    bool ModelsPresent { get; }

    Task DownloadModelsAsync(IProgress<double>? progress, CancellationToken ct);

    /// <summary>Loads the models (once), ends the current run and opens the microphone in a new one. Call off the UI thread.</summary>
    /// <returns>The new run. It may have failed already (<see cref="OpenMicRun.Failure"/>): its microphone can die while it opens.</returns>
    /// <exception cref="ListeningModelException">A model file would not load; it is deleted.</exception>
    /// <exception cref="MicrophoneException">The device could not be opened.</exception>
    OpenMicRun Start(string deviceId);

    /// <summary>Closes the microphone (Windows' microphone indicator goes off) and drops a turn being spoken, if
    /// <paramref name="run"/> is still the current run; an old run is left alone. Call off the UI thread.</summary>
    void Stop(OpenMicRun run);

    /// <summary>What is heard is not taken as speech: Raven speaks and voice barge-in is off.</summary>
    bool IgnoreSpeech { get; set; }

    /// <summary>Half a second of speech: the user is talking. Carries the run. On the worker thread.</summary>
    event EventHandler<OpenMicRun>? SpeechStarted;

    /// <summary>The user's turn is over: its run and its audio, 16 kHz. The clip is empty when the detector failed
    /// mid-turn: the turn is dropped, and the consumer leaves "listening". On the worker thread.</summary>
    event EventHandler<OpenMicTurn>? TurnEnded;

    /// <summary>
    /// What was captured, in batches of about <see cref="OpenMicListener.HeardBatch"/> rather than per 10 ms block, for
    /// the orb's level and the silent-microphone watch (see <see cref="HeardAudio"/>). Batched, an idle Open mic posts
    /// some 20 updates a second to the UI, not 100. On the worker thread.
    /// </summary>
    event EventHandler<HeardAudio>? Heard;

    /// <summary>A run's microphone died (<see cref="OpenMicRun.Failure"/> says how); the listener then stops that run itself,
    /// so a consumer need not call Stop (and must never from inside this handler). On the capture thread.</summary>
    event EventHandler<OpenMicRun>? Failed;
}

/// <summary>One run of Open mic, from the <see cref="IOpenMic.Start"/> that returned it until it is stopped or its microphone fails.</summary>
public sealed class OpenMicRun(string deviceId)
{
    private MicrophoneException? _failure;

    /// <summary>The microphone it listens on.</summary>
    public string DeviceId { get; } = deviceId;

    /// <summary>Why its microphone died, once it did. Set before <see cref="IOpenMic.Failed"/> is raised, so a consumer
    /// that gets the run back from Start after the failure still sees it.</summary>
    public MicrophoneException? Failure => Volatile.Read(ref _failure);

    /// <summary>For an <see cref="IOpenMic"/>: the run's microphone died. Call before raising <see cref="IOpenMic.Failed"/>.</summary>
    public void Fail(MicrophoneException error) => Volatile.Write(ref _failure, error);
}

/// <summary>
/// One <see cref="IOpenMic.Heard"/> batch. <paramref name="Loudest"/> block's RMS is the orb's level. <paramref name="Quietest"/>
/// is the silent-microphone watch's: it counts a batch as sound for its whole <paramref name="Duration"/>, so one click in
/// a batch of digital zeros must not make the batch loud, or a dead device clicking every 60 ms would pass for live. A
/// struct: one is raised 20 times a second for as long as Open mic is on.
/// </summary>
public readonly record struct HeardAudio(float Loudest, float Quietest, TimeSpan Duration);

/// <summary>A finished turn of <paramref name="Run"/>: its audio, 16 kHz; empty when the detector lost it.</summary>
public sealed record OpenMicTurn(OpenMicRun Run, float[] Clip);

/// <summary>
/// Ties a <see cref="IMicrophoneStream"/> to a <see cref="TurnDetector"/>. The capture thread only queues blocks; one
/// worker per run cuts them into 512-sample frames and runs the detector, so the models never run on the capture thread
/// (or the UI thread, which only starts and stops this off itself). The models are loaded on the first start and kept.
/// </summary>
public sealed class OpenMicListener : IOpenMic, IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How much audio one <see cref="Heard"/> carries: enough for a smooth orb, few enough posts to the UI.</summary>
    public static readonly TimeSpan HeardBatch = TimeSpan.FromMilliseconds(50);

    private static readonly int HeardBatchSamples = (int)(HeardBatch.TotalSeconds * AudioMath.TargetRate);

    /// <summary>About ten seconds of 10 ms blocks: a Smart Turn call is milliseconds, so the queue only fills if the PC stalls.</summary>
    internal const int QueueBlocks = 1000;

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

    /// <summary>The current run, null while stopped; read on the capture thread, so its failure is tied to the right run.</summary>
    private volatile OpenMicRun? _run;
    private Channel<CapturedFrames>? _queue;
    private CancellationTokenSource? _cts;
    private Task _worker = Task.CompletedTask;
    private volatile bool _ignoreSpeech;

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

    public event EventHandler<OpenMicRun>? SpeechStarted;

    public event EventHandler<OpenMicTurn>? TurnEnded;

    public event EventHandler<HeardAudio>? Heard;

    public event EventHandler<OpenMicRun>? Failed;

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

    public OpenMicRun Start(string deviceId)
    {
        lock (_gate)
        {
            StopRun();
            _detector ??= LoadDetector();
            _detector.Reset();
            _detector.IgnoreSpeech = _ignoreSpeech;
            var run = new OpenMicRun(deviceId);
            // DropWrite drops a block when the queue is full and still reports the write as done: only this callback
            // says audio was lost. Once per run, so a stalled PC does not flood the log.
            var overflowLogged = false;
            var queue = _queue = Channel.CreateBounded<CapturedFrames>(
                new BoundedChannelOptions(QueueBlocks) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropWrite },
                _ =>
                {
                    if (!overflowLogged)
                    {
                        overflowLogged = true;
                        _logger.LogWarning("Open mic fell behind the microphone; some audio was dropped");
                    }
                });
            var cts = _cts = new CancellationTokenSource();
            var detector = _detector;
            _worker = Task.Run(() => WorkAsync(run, queue, detector, cts.Token));
            _run = run;
            try
            {
                _stream.Start(deviceId);
            }
            catch
            {
                StopRun(); // no idle worker or queue is left behind
                throw;
            }

            return run;
        }
    }

    public void Stop(OpenMicRun run)
    {
        lock (_gate)
        {
            if (run == _run)
            {
                StopRun();
            }
        }
    }

    /// <summary>Ends whatever run is current: disposal and the tests.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopRun();
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
        _run = null;
        _stream.Stop();
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        _queue?.Writer.TryComplete();
        _queue = null;
        var worker = _worker;
        if (_workerOf == this)
        {
            // A handler called Stop from the worker thread: waiting would wait on itself. The worker ends by its token,
            // and its token's source is disposed once it has.
            _detector?.Reset();
            _ = worker.ContinueWith(_ => cts?.Dispose(), TaskScheduler.Default);
            return;
        }

        if (worker.Wait(StopTimeout))
        {
            cts?.Dispose();
            _detector?.Reset();
            return;
        }

        _logger.LogWarning("Open mic's worker did not stop within {Seconds} s; its models are replaced", StopTimeout.TotalSeconds);
        var vad = _vad;
        var turnEnd = _turnEnd;
        _detector = null;
        _vad = null;
        _turnEnd = null;
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

    /// <summary>
    /// Only ONNX Runtime's own error says the file is bad (invalid or corrupt): that file is deleted, to be downloaded
    /// again. Anything else (the native runtime missing, out of memory) has nothing to do with the file, and deleting a
    /// good model would only download it again to fail the same way: it goes to the caller as it is.
    /// </summary>
    private T Load<T>(ListeningModel model, Func<T> load)
    {
        try
        {
            return load();
        }
        catch (OnnxRuntimeException ex)
        {
            _logger.LogWarning(ex, "{Model} would not load; it is deleted and downloaded again next time", model.FileName);
            _store.Forget(model);
            throw new ListeningModelException(model, ex);
        }
    }

    /// <summary>The queue is null once the run is stopped; a write after its completion is simply refused.</summary>
    private void OnFrames(object? sender, CapturedFrames frames) => _queue?.Writer.TryWrite(frames);

    private void OnFailed(object? sender, MicrophoneException error)
    {
        if (_run is not { } run)
        {
            _logger.LogWarning(error, "Open mic's microphone failed while no run was open");
            return;
        }

        run.Fail(error);
        Raise(() => Failed?.Invoke(this, run), "Failed", onWorker: false);

        // The listener closes itself, off the capture thread: stopping from here would wait on the thread that is calling.
        // Only the run that failed: the consumer may have started another by the time this runs.
        _ = Task.Run(() => Stop(run));
    }

    /// <summary>A handler's bug must not end listening. While a worker's handler runs, a Stop from it must not wait on its own thread.</summary>
    private void Raise(Action raise, string name, bool onWorker = true) => Raise(raise, static r => r(), name, onWorker);

    /// <summary>Raises an event through <paramref name="raise"/>, given <paramref name="state"/>: with a static lambda, a
    /// frequent event allocates no closure.</summary>
    private void Raise<T>(T state, Action<T> raise, string name, bool onWorker = true)
    {
        if (onWorker)
        {
            _workerOf = this;
        }

        try
        {
            raise(state);
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

    private async Task WorkAsync(OpenMicRun run, Channel<CapturedFrames> queue, TurnDetector detector, CancellationToken token)
    {
        var frame = new float[SileroVad.FrameSamples];
        var filled = 0;
        var stepLogged = false;
        var heard = 0; // samples in the Heard batch so far
        var loudest = 0f;
        var quietest = float.MaxValue;
        var inTurn = false; // Started came, Ended has not
        try
        {
            await foreach (var block in queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                heard += block.Samples16k.Length;
                loudest = Math.Max(loudest, block.Rms);
                quietest = Math.Min(quietest, block.Rms);
                if (heard >= HeardBatchSamples)
                {
                    var batch = new HeardAudio(loudest, quietest, TimeSpan.FromSeconds((double)heard / AudioMath.TargetRate));
                    heard = 0;
                    loudest = 0f;
                    quietest = float.MaxValue;
                    Raise((Listener: this, Batch: batch), static s => s.Listener.Heard?.Invoke(s.Listener, s.Batch), "Heard"); // no closure, 20 times a second
                }

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
                        if (inTurn)
                        {
                            // The turn is lost with the detector's state; the consumer must not stay "listening" for it.
                            result = new TurnEvent.Ended([]);
                        }
                    }

                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    switch (result)
                    {
                        case TurnEvent.Started:
                            inTurn = true;
                            Raise(() => SpeechStarted?.Invoke(this, run), "SpeechStarted");
                            break;
                        case TurnEvent.Ended ended:
                            inTurn = false;
                            var turn = new OpenMicTurn(run, ended.Clip);
                            Raise(() => TurnEnded?.Invoke(this, turn), "TurnEnded");
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
