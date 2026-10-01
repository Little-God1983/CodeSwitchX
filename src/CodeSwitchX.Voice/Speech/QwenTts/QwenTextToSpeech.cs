using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Voice.Speech.QwenTts;

/// <summary>
/// Qwen3-TTS behind <see cref="ITextToSpeech"/>: a local sidecar the app starts, installed on first need, stopped with the
/// app or when the model changes. One sidecar at a time; it speaks one request at a time, so a sentence asked while
/// another is generated waits for it there.
/// </summary>
public sealed class QwenTextToSpeech : ITextToSpeech, IDisposable
{
    private const int SampleRate = 24000;

    private readonly IQwenTtsEnvironment _environment;
    private readonly IQwenTtsServerLauncher _launcher;
    private readonly SpeechSettings _settings;
    private readonly HttpClient _http;
    private readonly ILogger<QwenTextToSpeech> _logger;
    private readonly Lock _lock = new();

    private TextToSpeechStatus _status = TextToSpeechStatus.Off;
    private IQwenTtsServer? _server;
    private SpeechModel _serverModel;
    private Task? _preparing;
    private CancellationTokenSource _lifetime = new();

    /// <summary>The model the last attempt failed with: not tried again until the model changes or the app restarts.</summary>
    private SpeechModel? _failedModel;
    private bool _disposed;

    public QwenTextToSpeech(IQwenTtsEnvironment environment, IQwenTtsServerLauncher launcher, SpeechSettings settings, HttpClient http,
        ILogger<QwenTextToSpeech> logger)
    {
        _environment = environment;
        _launcher = launcher;
        _settings = settings;
        _http = http;
        _logger = logger;
        _settings.ModelChanged += (_, _) => Restart();
    }

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

    public void Prepare(bool install)
    {
        lock (_lock)
        {
            var model = _settings.Model;
            if (_disposed || _preparing is not null || _server is not null || _failedModel == model)
            {
                return;
            }

            if (!install && !_environment.IsInstalled)
            {
                return;
            }

            _preparing = PrepareAsync(model, _lifetime.Token);
        }
    }

    private async Task PrepareAsync(SpeechModel model, CancellationToken ct)
    {
        await Task.Yield(); // never on the caller's thread: checking the install reads files
        IQwenTtsServer? server = null;
        try
        {
            if (!_environment.IsInstalled)
            {
                Report(new TextToSpeechStatus(TextToSpeechState.Installing, "starting"));
                await _environment.InstallAsync(new Progress(this, TextToSpeechState.Installing), ct).ConfigureAwait(false);
            }

            Report(new TextToSpeechStatus(TextToSpeechState.Loading, "starting"));
            server = await _launcher.StartAsync(_environment, SpeechSettings.ModelId(model),
                detail => Report(new TextToSpeechStatus(TextToSpeechState.Loading, detail)), ct).ConfigureAwait(false);
            lock (_lock)
            {
                if (ct.IsCancellationRequested)
                {
                    server.Dispose();
                    return;
                }

                _server = server;
                _serverModel = model;
                _preparing = null;
            }

            _ = WatchAsync(server);
            Report(new TextToSpeechStatus(TextToSpeechState.Ready));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            server?.Dispose();
        }
        catch (Exception ex)
        {
            server?.Dispose();
            _logger.LogWarning(ex, "Raven's voice could not get ready");
            lock (_lock)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                _failedModel = model;
                _preparing = null;
            }

            Report(new TextToSpeechStatus(TextToSpeechState.Failed, ex is TextToSpeechException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>A sidecar that ends by itself (a crash, killed by hand) is started again on the next request.</summary>
    private async Task WatchAsync(IQwenTtsServer server)
    {
        await server.Exited.ConfigureAwait(false);
        lock (_lock)
        {
            if (_server != server)
            {
                return; // stopped on purpose
            }

            _server = null;
        }

        _logger.LogWarning("Raven's voice ended by itself; it starts again when it is next needed");
        Report(TextToSpeechStatus.Off);
    }

    /// <summary>The model changed: the sidecar stops, and the next preparation starts the new one.</summary>
    private void Restart()
    {
        IQwenTtsServer? server;
        bool wasOn;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            server = _server;
            wasOn = server is not null || _preparing is not null;
            _server = null;
            _preparing = null;
            _failedModel = null;
            _lifetime.Cancel();
            _lifetime = new CancellationTokenSource();
        }

        server?.Dispose();
        Report(TextToSpeechStatus.Off);
        if (wasOn)
        {
            Prepare(install: false);
        }
    }

    public async IAsyncEnumerable<SpeechChunk> SpeakAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        IQwenTtsServer? server;
        lock (_lock)
        {
            server = _server;
        }

        if (server is null)
        {
            Prepare(install: true);
            throw new TextToSpeechNotReadyException(Status);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.Address, "v1/audio/speech"))
        {
            Content = JsonContent.Create(new { input = text, voice = _settings.Voice, language = "English", response_format = "pcm" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TextToSpeechException($"The voice did not answer: {ex.Message}", ex);
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
                    throw new TextToSpeechException($"The voice stopped mid-sentence: {ex.Message}", ex);
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

    private void Report(TextToSpeechStatus status)
    {
        lock (_lock)
        {
            if (_status == status)
            {
                return;
            }

            _status = status;
        }

        StatusChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        IQwenTtsServer? server;
        lock (_lock)
        {
            _disposed = true;
            server = _server;
            _server = null;
            _lifetime.Cancel();
        }

        server?.Dispose();
    }

    private sealed class Progress(QwenTextToSpeech owner, TextToSpeechState state) : IProgress<string>
    {
        public void Report(string value) => owner.Report(new TextToSpeechStatus(state, value));
    }
}
