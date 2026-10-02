using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Voice.Speech.Sidecar;

/// <summary>A running sidecar (<c>tts_sidecar.py</c> under an engine's script), ready to speak.</summary>
public interface ISidecarServer : IDisposable
{
    /// <summary>Where it listens: http://127.0.0.1:port/.</summary>
    Uri Address { get; }

    /// <summary>The bearer token every request carries.</summary>
    string Token { get; }

    /// <summary>Completes once the process has ended (a crash, or <see cref="IDisposable.Dispose"/>).</summary>
    Task Exited { get; }
}

/// <summary>Starts sidecars; the tests start fakes.</summary>
public interface ISidecarLauncher
{
    /// <summary>Starts one for <paramref name="model"/> and waits until it has loaded the model and is ready.</summary>
    /// <param name="onStatus">What it is doing meanwhile, in a few words.</param>
    /// <exception cref="TextToSpeechException">It failed to start, or ended before it was ready.</exception>
    Task<ISidecarServer> StartAsync(ISidecarEnvironment environment, string model, Action<string> onStatus, CancellationToken ct);
}

/// <summary>
/// Starts the sidecar with the environment's Python: no window, listening on a free port of 127.0.0.1 with a fresh token.
/// It ends with CodeSwitchX however that ends (it waits on this process), and is killed when disposed. What it writes to
/// stderr reaches the app's log: while it loads at debug level (libraries chatter), and the tail as a warning when the
/// load fails; once it is ready, every line as a warning, since then it writes only what went wrong (a traceback).
/// </summary>
public sealed class SidecarLauncher : ISidecarLauncher
{
    private readonly ILogger<SidecarLauncher> _logger;

    public SidecarLauncher(ILogger<SidecarLauncher> logger)
    {
        _logger = logger;
    }

    public async Task<ISidecarServer> StartAsync(ISidecarEnvironment environment, string model, Action<string> onStatus,
        CancellationToken ct)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var start = new ProcessStartInfo(environment.Python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-u", environment.WriteScript(), "--model", environment.ModelArgument(model), "--parent-pid", Environment.ProcessId.ToString() })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["CSX_TTS_TOKEN"] = token;
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var (name, value) in environment.Variables)
        {
            start.Environment[name] = value;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new TextToSpeechException("The voice did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new TextToSpeechException($"The voice could not be started: {ex.Message}", ex);
        }

        var server = new Server(process, token, _logger);
        try
        {
            await server.WaitUntilReadyAsync(onStatus, ct).ConfigureAwait(false);
            return server;
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    private sealed class Server : ISidecarServer
    {
        private const int ErrorLinesKept = 15;
        private readonly Process _process;
        private readonly Queue<string> _errors = new();
        private readonly TaskCompletionSource<Uri> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ILogger _logger;

        /// <summary>Told what the sidecar does while it loads; dropped once it is ready, so nothing it says later counts as loading.</summary>
        private volatile Action<string>? _onStatus;

        public Server(Process process, string token, ILogger logger)
        {
            _process = process;
            Token = token;
            _logger = logger;
            _process.ErrorDataReceived += (_, e) => OnError(e.Data);
            _process.BeginErrorReadLine();
            Exited = Task.Run(PumpAsync);
        }

        public Uri Address => _listening.Task.IsCompletedSuccessfully ? _listening.Task.Result : throw new InvalidOperationException("Not listening.");

        public string Token { get; }

        public Task Exited { get; }

        public async Task WaitUntilReadyAsync(Action<string> onStatus, CancellationToken ct)
        {
            _onStatus = onStatus;
            onStatus("starting");
            await _ready.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Reads the events on stdout until it closes, which it does as the process ends.</summary>
        private async Task PumpAsync()
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    OnEvent(line);
                }

                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }

            Fail($"The voice ended while loading: {LastError() ?? "no reason given"}");
        }

        /// <summary>The load failed: the error says the last line, the log gets all that is kept of stderr.</summary>
        private void Fail(string message)
        {
            if (_ready.TrySetException(new TextToSpeechException(message)))
            {
                string tail;
                lock (_errors)
                {
                    tail = string.Join(Environment.NewLine, _errors);
                }

                _logger.LogWarning("Raven's voice failed to load. Its last words:{NewLine}{Tail}", Environment.NewLine, tail);
            }
        }

        private void OnEvent(string line)
        {
            string? kind;
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(line);
                root = document.RootElement.Clone();
                kind = root.TryGetProperty("event", out var e) ? e.GetString() : null;
            }
            catch (JsonException)
            {
                return; // a library printing to stdout: not ours
            }

            switch (kind)
            {
                case "listening" when root.TryGetProperty("port", out var port):
                    _listening.TrySetResult(new Uri($"http://127.0.0.1:{port.GetInt32()}/"));
                    _onStatus?.Invoke("loading the model");
                    break;
                case "ready":
                    _onStatus = null;
                    _ready.TrySetResult();
                    break;
                case "failed":
                    var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    Fail($"The voice failed to load: {reason ?? LastError() ?? "no reason given"}");
                    break;
            }
        }

        private void OnError(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            if (_ready.Task.IsCompletedSuccessfully)
            {
                _logger.LogWarning("Raven's voice: {Line}", line);
                return;
            }

            _logger.LogDebug("Raven's voice: {Line}", line);
            lock (_errors)
            {
                _errors.Enqueue(line);
                if (_errors.Count > ErrorLinesKept)
                {
                    _errors.Dequeue();
                }
            }

            // Hugging Face reports its downloads on stderr: the first start fetches the model (1.2 or 3.5 GB).
            if (line.Contains("Fetching", StringComparison.Ordinal) || line.Contains("Downloading", StringComparison.Ordinal))
            {
                _onStatus?.Invoke("downloading the model");
            }
        }

        private string? LastError()
        {
            lock (_errors)
            {
                return _errors.LastOrDefault();
            }
        }

        public void Dispose()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Ended already.
            }

            _process.Dispose();
        }
    }
}
