using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace CodeSwitchX.Conductor;

/// <summary>A running <c>claude</c>: lines in on standard input, lines out of standard output.</summary>
public interface IBrainProcess : IDisposable
{
    /// <summary>Standard output, line by line; completes when the process closes it, which it does as it exits.</summary>
    ChannelReader<string> Lines { get; }

    /// <summary>
    /// Each line of standard output as it is read, before <see cref="Lines"/> has it, however far behind the reader of
    /// <see cref="Lines"/> is. Raised on the reading thread: a handler must be quick, and never throw.
    /// </summary>
    event Action<string>? LineRead;

    /// <summary>Completes with the exit code once the process has exited.</summary>
    Task<int> Exited { get; }

    /// <summary>The last lines it wrote to standard error, for the log.</summary>
    string ErrorTail { get; }

    Task WriteLineAsync(string line, CancellationToken ct);

    /// <summary>Closes standard input: <c>claude -p</c> reading stream-json ends once its turn is over. Never throws.</summary>
    void CloseInput();
}

/// <summary>Starts <see cref="IBrainProcess"/>es; the tests start fakes.</summary>
public interface IBrainProcessLauncher
{
    /// <param name="environment">Variables to set on top of the app's environment; a null value leaves one out.</param>
    /// <exception cref="System.ComponentModel.Win32Exception">The executable could not be started.</exception>
    IBrainProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null);
}

/// <summary>Starts the real process: no window, UTF-8 both ways, the whole tree killed when it is disposed.</summary>
public sealed class BrainProcessLauncher : IBrainProcessLauncher
{
    /// <summary>
    /// Left out of the brain's environment: CodeSwitchX started from a Claude Code terminal inherits them, and they would
    /// make the brain take itself for part of that session.
    /// </summary>
    private static readonly string[] Inherited = ["CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT"];

    public IBrainProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in Inherited)
        {
            start.Environment.Remove(name);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        return new BrainProcess(Process.Start(start) ?? throw new InvalidOperationException($"{executable} did not start."));
    }

    private sealed class BrainProcess : IBrainProcess
    {
        private const int ErrorLinesKept = 20;
        private readonly Process _process;
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(); // a turn reads it while the watcher for unasked turns waits on it
        private readonly Queue<string> _errors = new();
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BrainProcess(Process process)
        {
            _process = process;
            _ = PumpOutputAsync();
            _ = PumpErrorsAsync();
            _ = WaitForExitAsync();
        }

        public ChannelReader<string> Lines => _lines.Reader;

        public event Action<string>? LineRead;

        public Task<int> Exited => _exited.Task;

        public string ErrorTail
        {
            get
            {
                lock (_errors)
                {
                    return string.Join(Environment.NewLine, _errors);
                }
            }
        }

        public async Task WriteLineAsync(string line, CancellationToken ct)
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }

        public void CloseInput()
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Gone already.
            }
        }

        private async Task PumpOutputAsync()
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    LineRead?.Invoke(line);
                    _lines.Writer.TryWrite(line);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
            finally
            {
                _lines.Writer.TryComplete();
            }
        }

        private async Task PumpErrorsAsync()
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    lock (_errors)
                    {
                        _errors.Enqueue(line);
                        while (_errors.Count > ErrorLinesKept)
                        {
                            _errors.Dequeue();
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        private async Task WaitForExitAsync()
        {
            try
            {
                await _process.WaitForExitAsync().ConfigureAwait(false);
                _exited.TrySetResult(_process.ExitCode);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                _exited.TrySetResult(-1);
            }
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }

            _process.Dispose();
        }
    }
}
