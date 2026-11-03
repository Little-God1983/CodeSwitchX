using System.Collections.Concurrent;
using System.Diagnostics;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.GitHub;

/// <summary>
/// What an ssh host name in a git remote (<c>git@github-work:owner/repo</c>) stands for, as <c>ssh -G</c> reads it from
/// <c>~/.ssh/config</c>. Each alias is asked once per run, off the caller's thread; no ssh, a failure or a timeout is no answer.
/// </summary>
public sealed class SshAliases
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly string _executable;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, Task<string?>> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public SshAliases() : this("ssh", Timeout)
    {
    }

    internal SshAliases(string executable, TimeSpan timeout)
    {
        _executable = executable;
        _timeout = timeout;
    }

    /// <returns>The <c>hostname</c> ssh would connect to for the alias; null when it cannot be told. A caller that stops waiting does not stop the ask.</returns>
    public Task<string?> HostOfAsync(string alias, CancellationToken ct) =>
        _hosts.GetOrAdd(alias, a => Task.Run(() => AskAsync(a))).WaitAsync(ct);

    private async Task<string?> AskAsync(string alias)
    {
        try
        {
            // "--": an alias is a name, never an option.
            var info = new ProcessStartInfo(_executable);
            foreach (var argument in new[] { "-G", "--", alias })
            {
                info.ArgumentList.Add(argument);
            }

            var run = await GitInspector.RunProcessAsync(info, _timeout, CancellationToken.None).ConfigureAwait(false);
            return run.Output?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("hostname ", StringComparison.OrdinalIgnoreCase))?[9..].Trim() is { Length: > 0 } host ? host : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
