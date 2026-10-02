using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Voice.Speech.Sidecar;

/// <summary>
/// One speech engine (Qwen3-TTS, Kokoro) behind <see cref="ITextToSpeech"/>: a local sidecar the app starts, installed on
/// first need, stopped with the app, when the model changes or when another engine is picked, and started again when it
/// crashes or hangs. One sidecar at a time; it speaks one request at a time, so a sentence asked while another is
/// generated waits for it there.
/// </summary>
public sealed class SidecarTextToSpeech : ISpeechEngineVoice, IDisposable
{
    private const int SampleRate = 24000;

    /// <summary>
    /// How long the sidecar may take to load before it counts as failed: Qwen3-TTS's first start downloads the model (1.2
    /// or 3.5 GB) on top of the load and the CUDA graph capture, which take under a minute.
    /// </summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(20);

    /// <summary>How long a failure stands before the next answer tries again (a network that was down, a busy GPU).</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private readonly ISidecarEnvironment _environment;
    private readonly ISidecarLauncher _launcher;
    private readonly SpeechSettings _settings;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger<SidecarTextToSpeech> _logger;
    private readonly Lock _lock = new();

    /// <summary>Taken while the status changes and the change is told, so listeners hear the changes in their order.</summary>
    private readonly Lock _telling = new();

    private TextToSpeechStatus _status = TextToSpeechStatus.Off;
    private ISidecarServer? _server;
    private Task? _preparing;

    /// <summary>The model of the settings when last looked at: a change of another engine's model is none of this one's.</summary>
    private string _model;

    /// <summary>The preparation is installing: that does not depend on the model, so a new model does not cancel it.</summary>
    private bool _installing;

    /// <summary>
    /// Some caller asked for an install (an answer), so the preparation in flight installs if it finds none, even if
    /// it was started by a warm-up that would not have.
    /// </summary>
    private bool _mayInstall;
    private CancellationTokenSource _lifetime = new();

    /// <summary>The model the last attempt failed with, and when: not tried again until <see cref="RetryAfter"/> has passed or the model changes.</summary>
    private string? _failedModel;
    private DateTimeOffset _failedAt;
    private bool _disposed;

    public SidecarTextToSpeech(SpeechEngine engine, ISidecarEnvironment environment, ISidecarLauncher launcher, SpeechSettings settings,
        HttpClient http, TimeProvider time, ILogger<SidecarTextToSpeech> logger)
    {
        Engine = engine;
        _environment = environment;
        _launcher = launcher;
        _settings = settings;
        _http = http;
        _time = time;
        _logger = logger;
        _model = settings.ModelOf(engine);
        _settings.ModelChanged += (_, _) => Restart();
    }

    public SpeechEngine Engine { get; }

    public TextToSpeechStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    public event EventHandler<TextToSpeechStatus>? StatusChanged;

    /// <summary>The preparation running now, if any, for the tests.</summary>
    internal Task Preparing
    {
        get
        {
            lock (_lock)
            {
                return _preparing ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>Returns at once on any thread (the UI's, on unmute): whether it is installed is looked at on the thread pool.</summary>
    public void Prepare(bool install)
    {
        lock (_lock)
        {
            if (_disposed || _server is not null || (_failedModel == _model && _time.GetUtcNow() - _failedAt < RetryAfter))
            {
                return;
            }

            _mayInstall |= install; // also for the preparation already in flight
            _preparing ??= PrepareAsync(_lifetime.Token);
        }
    }

    /// <summary>
    /// Says whether it is on disk (<see cref="TextToSpeechState.Off"/>) or not (<see cref="TextToSpeechState.NotInstalled"/>)
    /// while it is neither running nor getting ready; looked at on the thread pool. Returns at once and never throws.
    /// </summary>
    public void CheckInstall() => _ = CheckInstallAsync(_lifetime.Token);

    private async Task CheckInstallAsync(CancellationToken attempt)
    {
        await OffTheCallersThread(); // it reads files
        string model;
        lock (_lock)
        {
            model = _model;
        }

        bool installed;
        try
        {
            installed = _environment.IsInstalled && _environment.HasModel(model);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not look at {Engine}'s install", Engine);
            return;
        }

        lock (_telling)
        {
            lock (_lock)
            {
                if (attempt.IsCancellationRequested || _server is not null || _preparing is not null || model != _model
                    || _status.State is not (TextToSpeechState.Off or TextToSpeechState.NotInstalled))
                {
                    return;
                }
            }

            Report(installed ? TextToSpeechStatus.Off : new TextToSpeechStatus(TextToSpeechState.NotInstalled), attempt);
        }
    }

    /// <summary>Installs if needed and asked for, then starts the model the settings name by then: one picked during the install is loaded.</summary>
    private async Task PrepareAsync(CancellationToken ct)
    {
        await OffTheCallersThread(); // checking the install reads files
        ISidecarServer? server = null;
        string model;
        lock (_lock)
        {
            model = _model;
        }

        try
        {
            if (!_environment.IsInstalled)
            {
                bool install;
                lock (_lock)
                {
                    install = _mayInstall;
                    _mayInstall = false;
                    _installing = install;
                    if (!install)
                    {
                        _preparing = null; // a warm-up of a voice not installed: nothing to do
                    }
                }

                if (!install)
                {
                    Report(new TextToSpeechStatus(TextToSpeechState.NotInstalled), ct);
                    return;
                }

                Report(new TextToSpeechStatus(TextToSpeechState.Installing, "starting"), ct);
                try
                {
                    await _environment.InstallAsync(new Progress(this, ct), ct).ConfigureAwait(false);
                }
                finally
                {
                    lock (_lock)
                    {
                        _installing = false;
                        model = _model;
                    }
                }
            }

            Report(new TextToSpeechStatus(TextToSpeechState.Loading, "starting"), ct);
            using (var loading = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var deadline = _time.CreateTimer(_ => Cancel(loading), null, LoadTimeout, Timeout.InfiniteTimeSpan))
            {
                try
                {
                    server = await _launcher.StartAsync(_environment, model,
                        detail => Report(new TextToSpeechStatus(TextToSpeechState.Loading, detail), ct), loading.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TextToSpeechException($"The voice did not finish loading in {LoadTimeout.TotalMinutes:0} minutes.");
                }
            }

            lock (_lock)
            {
                if (ct.IsCancellationRequested)
                {
                    server.Dispose();
                    return;
                }

                _server = server;
                _preparing = null;
                _mayInstall = false;
                _failedModel = null;
            }

            _ = WatchAsync(server);
            // Not after a restart that stopped this server meanwhile: its Off, and the next model's Loading, stand.
            Report(new TextToSpeechStatus(TextToSpeechState.Ready), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            server?.Dispose();
        }
        catch (Exception ex)
        {
            server?.Dispose();
            _logger.LogWarning(ex, "Raven's voice ({Engine}) could not get ready", Engine);
            lock (_lock)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                _failedModel = model;
                _failedAt = _time.GetUtcNow();
                _preparing = null;
                _mayInstall = false;
            }

            Report(new TextToSpeechStatus(TextToSpeechState.Failed, ex is TextToSpeechException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}"), ct);
        }
    }

    /// <summary>
    /// Goes on on the thread pool, whatever the caller's thread: Task.Yield would come back to the UI thread through WPF's
    /// synchronisation context, and a sleeping disk would hold the window up.
    /// </summary>
    private static ConfiguredTaskAwaitable OffTheCallersThread() => Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

    private static void Cancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Loaded meanwhile.
        }
    }

    /// <summary>A sidecar that ends by itself (a crash, killed by hand) is started again on the next request.</summary>
    private async Task WatchAsync(ISidecarServer server)
    {
        await server.Exited.ConfigureAwait(false);
        if (Drop(server))
        {
            _logger.LogWarning("Raven's voice ({Engine}) ended by itself; it starts again when it is next needed", Engine);
        }
    }

    public void Recover()
    {
        ISidecarServer? server;
        lock (_lock)
        {
            server = _server;
        }

        if (server is null || !Drop(server))
        {
            return;
        }

        _logger.LogWarning("Raven's voice ({Engine}) hung; it is started again", Engine);
        _ = Task.Run(server.Dispose);
        Prepare(install: false);
    }

    /// <summary>
    /// Forgets <paramref name="server"/> if it is still the one in use, and says Off, in one step under the telling lock:
    /// a preparation can start only once it is forgotten, so its Loading always comes after this Off.
    /// </summary>
    private bool Drop(ISidecarServer server)
    {
        lock (_telling)
        {
            lock (_lock)
            {
                if (_server != server)
                {
                    return false; // stopped on purpose, or dropped already
                }

                _server = null;
                _status = TextToSpeechStatus.Off;
            }

            StatusChanged?.Invoke(this, TextToSpeechStatus.Off);
            return true;
        }
    }

    /// <summary>
    /// The model changed: the sidecar stops, and the next preparation starts the new one. An install going on is left
    /// to finish (it does not depend on the model); it loads the new model after. The old sidecar is killed on the
    /// thread pool: Settings changes the model on the UI thread.
    /// </summary>
    private void Restart()
    {
        bool wasOn;
        lock (_lock)
        {
            var model = _settings.ModelOf(Engine);
            if (_disposed || model == _model)
            {
                return;
            }

            _model = model;
            _failedModel = null;
            if (_installing)
            {
                return;
            }

            wasOn = _server is not null || _preparing is not null;
        }

        Halt(report: !wasOn); // a restart that loads the new model at once says Loading, not Off: the install's note goes on
        if (wasOn)
        {
            Prepare(install: false);
        }
        else
        {
            CheckInstall(); // the new model may not be on disk
        }
    }

    /// <summary>
    /// Another engine was picked, or the install was cancelled: the sidecar stops, and an install or load going on is
    /// given up. It starts again when asked to get ready or to speak. Returns at once on any thread.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_disposed || (_server is null && _preparing is null && _status.State != TextToSpeechState.Failed))
            {
                return; // nothing to stop: off already
            }

            _installing = false;
            _mayInstall = false;
            _failedModel = null;
        }

        Halt();
        CheckInstall();
    }

    public bool IsInstalled
    {
        get
        {
            string model;
            lock (_lock)
            {
                model = _model;
            }

            return _environment.IsInstalled && _environment.HasModel(model);
        }
    }

    public void Install()
    {
        lock (_lock)
        {
            _failedModel = null;
        }

        Prepare(install: true);
    }

    /// <summary>Kills the sidecar on the thread pool and cancels the preparation, then says Off unless told not to.</summary>
    private void Halt(bool report = true)
    {
        ISidecarServer? server;
        lock (_lock)
        {
            server = _server;
            _server = null;
            _preparing = null;
            _lifetime.Cancel();
            _lifetime = new CancellationTokenSource();
        }

        if (server is not null)
        {
            _ = Task.Run(server.Dispose);
        }

        if (report)
        {
            Report(TextToSpeechStatus.Off);
        }
    }

    public async IAsyncEnumerable<SpeechChunk> SpeakAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        ISidecarServer? server;
        lock (_lock)
        {
            server = _server;
        }

        if (server is null)
        {
            Prepare(install: true);
            // The preparation may not have said yet what it does: an install or a load. Off the UI thread here (the
            // reply voice's loop), so the install is looked at.
            var status = Status;
            throw new TextToSpeechNotReadyException(status.State is not (TextToSpeechState.Off or TextToSpeechState.NotInstalled) ? status
                : new TextToSpeechStatus(_environment.IsInstalled ? TextToSpeechState.Loading : TextToSpeechState.Installing));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.Address, "v1/audio/speech"))
        {
            // Buffered, so it goes with a Content-Length: the sidecar's plain HTTP server reads no chunked bodies.
            Content = new StringContent(JsonSerializer.Serialize(new { input = text, voice = _settings.VoiceOf(Engine), language = "English", response_format = "pcm" }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw Interrupted(server) ?? new TextToSpeechException($"The voice did not answer: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                throw new TextToSpeechNotReadyException(new TextToSpeechStatus(TextToSpeechState.Loading));
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new TextToSpeechException($"The voice refused the sentence ({(int)response.StatusCode}): {body}");
            }

            var rate = response.Headers.TryGetValues("X-Sample-Rate", out var values) && int.TryParse(values.FirstOrDefault(), out var r)
                ? r
                : SampleRate;
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[16 * 1024];
            var carry = -1; // the odd byte of a sample cut between two reads
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(carry < 0 ? 0 : 1), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException && !ct.IsCancellationRequested)
                {
                    throw Interrupted(server) ?? new TextToSpeechException($"The voice stopped mid-sentence: {ex.Message}", ex);
                }

                if (read == 0)
                {
                    yield break;
                }

                var length = read + (carry < 0 ? 0 : 1);
                var whole = length & ~1;
                if (whole > 0)
                {
                    yield return new SpeechChunk(buffer.AsSpan(0, whole).ToArray(), rate);
                }

                carry = whole < length ? buffer[length - 1] : -1;
                if (carry >= 0)
                {
                    buffer[0] = (byte)carry;
                }
            }
        }
    }

    /// <summary>
    /// A request cut off because its sidecar was stopped on purpose (a new model, another engine) or after a hang is no
    /// failure: the voice is getting ready again. Null when the sidecar is still the one in use.
    /// </summary>
    private TextToSpeechNotReadyException? Interrupted(ISidecarServer server)
    {
        lock (_lock)
        {
            return _server == server ? null : new TextToSpeechNotReadyException(new TextToSpeechStatus(TextToSpeechState.Loading));
        }
    }

    /// <param name="attempt">The preparation it comes from: nothing more is told of one a restart cancelled.</param>
    private void Report(TextToSpeechStatus status, CancellationToken attempt = default)
    {
        lock (_telling)
        {
            lock (_lock)
            {
                if (attempt.IsCancellationRequested || _status == status)
                {
                    return;
                }

                // The sidecar's own words come only while it loads: a late "Downloading" on its stderr does not undo Ready.
                if (status.State == TextToSpeechState.Loading && _server is not null)
                {
                    return;
                }

                _status = status;
            }

            StatusChanged?.Invoke(this, status);
        }
    }

    public void Dispose()
    {
        ISidecarServer? server;
        lock (_lock)
        {
            _disposed = true;
            server = _server;
            _server = null;
            _lifetime.Cancel();
        }

        server?.Dispose();
    }

    private sealed class Progress(SidecarTextToSpeech owner, CancellationToken attempt) : IProgress<InstallStep>
    {
        public void Report(InstallStep value) =>
            owner.Report(new TextToSpeechStatus(TextToSpeechState.Installing, value.Detail, value.Bytes), attempt);
    }
}
