using System.Text.Json;
using System.Threading.Channels;

namespace CodeSwitchX.Conductor.Tests;

/// <summary>A claude that answers each line written to it with <see cref="Answer"/>, as the real one would, in stream-json.</summary>
internal sealed class FakeBrainProcess : IBrainProcess
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<string> Written { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>The lines it writes back for a line written to it; nothing by default.</summary>
    public Func<string, IEnumerable<string>> Answer { get; set; } = _ => [];

    public ChannelReader<string> Lines => _lines.Reader;

    /// <summary>Given each line as it writes it, as the real one's reading thread does.</summary>
    public Action<string>? LineRead { get; set; }

    public Task<int> Exited => _exited.Task;

    public string ErrorTail => "error: something broke";

    /// <summary>
    /// Its input is full and it reads no more: a write hangs, deaf to its token as a pipe write blocked in WriteFile is,
    /// until the process is killed, which breaks the pipe.
    /// </summary>
    public bool WritesHang { get; set; }

    private readonly TaskCompletionSource _broken = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WriteLineAsync(string line, CancellationToken ct)
    {
        if (WritesHang)
        {
            Written.Add(line);
            return _broken.Task;
        }

        ct.ThrowIfCancellationRequested(); // as StreamWriter does, before it writes
        if (_exited.Task.IsCompleted)
        {
            throw new IOException("The pipe has been ended.");
        }

        Written.Add(line);
        foreach (var answer in Answer(line))
        {
            Emit(answer);
        }

        return Task.CompletedTask;
    }

    public void Emit(string line)
    {
        LineRead?.Invoke(line);
        _lines.Writer.TryWrite(line);
    }

    public bool InputClosed { get; private set; }

    /// <summary>Whether closing the input ends it, as it ends the real one between turns; true by default.</summary>
    public bool ExitsOnClosedInput { get; set; } = true;

    public void CloseInput()
    {
        InputClosed = true;
        if (ExitsOnClosedInput)
        {
            Die(0);
        }
    }

    /// <summary>The process dies: its output closes and it exits.</summary>
    public void Die(int code)
    {
        _lines.Writer.TryComplete();
        _exited.TrySetResult(code);
    }

    public void Dispose()
    {
        Disposed = true;
        _broken.TrySetException(new IOException("The pipe has been ended."));
        Die(1);
    }
}

internal sealed class FakeLauncher : IBrainProcessLauncher
{
    public List<(string Executable, IReadOnlyList<string> Arguments, string Folder, FakeBrainProcess Process)> Started { get; } = [];

    /// <summary>The environment each process was started with, in the order of <see cref="Started"/>.</summary>
    public List<IReadOnlyDictionary<string, string?>?> Environments { get; } = [];

    /// <summary>What each new process answers with.</summary>
    public Func<string, IEnumerable<string>> Answer { get; set; } = _ => [];

    public Exception? Failure { get; set; }

    /// <summary>Whether closing a new process's input ends it (<see cref="FakeBrainProcess.ExitsOnClosedInput"/>); true by default.</summary>
    public bool ExitsOnClosedInput { get; set; } = true;

    public FakeBrainProcess Last => Started[^1].Process;

    public IBrainProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null, Action<string>? lineRead = null)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        var process = new FakeBrainProcess { Answer = Answer, LineRead = lineRead, ExitsOnClosedInput = ExitsOnClosedInput };

        Started.Add((executable, arguments, workingDirectory, process));
        Environments.Add(environment);
        return process;
    }
}
