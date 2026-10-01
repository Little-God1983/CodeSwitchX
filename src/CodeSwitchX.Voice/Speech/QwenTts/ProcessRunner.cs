using System.Diagnostics;
using System.Text;

namespace CodeSwitchX.Voice.Speech.QwenTts;

/// <summary>Runs a command line tool to its end; the tests run fakes.</summary>
public interface IProcessRunner
{
    /// <param name="onLine">Each line it writes, to either stream, as it writes it.</param>
    /// <returns>The exit code, and the last lines it wrote, for an error message.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The executable could not be started.</exception>
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
        Action<string>? onLine, CancellationToken ct);
}

public sealed record ProcessResult(int ExitCode, string OutputTail);

/// <summary>No window, UTF-8 out; cancelling kills the whole tree.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int LinesKept = 15;

    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, Action<string>? onLine, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{executable} did not start.");
        var tail = new Queue<string>();
        void Take(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > LinesKept)
                {
                    tail.Dequeue();
                }
            }

            onLine?.Invoke(line);
        }

        process.OutputDataReceived += (_, e) => Take(e.Data);
        process.ErrorDataReceived += (_, e) => Take(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Ended meanwhile.
            }

            throw;
        }

        process.WaitForExit(); // the last lines of both streams
        lock (tail)
        {
            return new ProcessResult(process.ExitCode, string.Join(Environment.NewLine, tail));
        }
    }
}
