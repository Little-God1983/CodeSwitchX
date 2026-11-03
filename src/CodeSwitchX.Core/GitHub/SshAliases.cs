using System.Diagnostics;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.GitHub;

/// <summary>
/// What an ssh host name in a git remote (<c>git@github-work:owner/repo</c>) stands for, as <c>ssh -G</c> reads it from
/// <c>~/.ssh/config</c>. A name is matched as written, case included, as ssh does. The answer is the host ssh names, or null for no
/// answer: ssh is missing or blocked, it failed or timed out, or it only echoed the name (<c>ssh -G</c> does that, with exit 0, when no
/// <c>Host</c> stanza matches). Every answer is kept for <see cref="KeepFor"/>, so nothing is asked again on every pass and an edit of the
/// config is picked up. Asks run off the caller's thread; each kind of failure is logged once per run, without the name.
/// </summary>
public sealed class SshAliases
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    private readonly string _executable;
    private readonly TimeSpan _timeout;
    private readonly Func<DateTimeOffset> _now;
    private readonly ILogger<SshAliases>? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, (string? Host, DateTimeOffset Until)> _answers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<string?>> _asking = new(StringComparer.Ordinal);
    private readonly HashSet<string> _logged = [];

    public SshAliases(ILogger<SshAliases>? logger = null) : this("ssh", Timeout, () => DateTimeOffset.UtcNow, logger)
    {
    }

    internal SshAliases(string executable, TimeSpan timeout, Func<DateTimeOffset>? now = null, ILogger<SshAliases>? logger = null)
    {
        _executable = executable;
        _timeout = timeout;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _logger = logger;
    }

    /// <returns>The host ssh names for the alias, or null for no answer. A caller that stops waiting does not stop the ask.</returns>
    public Task<string?> HostOfAsync(string alias, CancellationToken ct)
    {
        Task<string?> asking;
        lock (_gate)
        {
            if (_answers.TryGetValue(alias, out var kept) && kept.Until > _now())
            {
                return Task.FromResult(kept.Host);
            }

            if (!_asking.TryGetValue(alias, out asking!))
            {
                asking = _asking[alias] = Task.Run(() => AskAsync(alias));
            }
        }

        return asking.WaitAsync(ct);
    }

    private async Task<string?> AskAsync(string alias)
    {
        string? host = null;
        try
        {
            string? failure;
            (host, failure) = await RunAsync(alias).ConfigureAwait(false);
            if (failure is not null)
            {
                Report(failure);
            }
        }
        finally
        {
            lock (_gate)
            {
                _asking.Remove(alias);
                _answers[alias] = (host, _now() + KeepFor);
            }
        }

        return host;
    }

    private async Task<(string? Host, string? Failure)> RunAsync(string alias)
    {
        try
        {
            // The bare "ssh" from the path, not the ssh git itself would use (core.sshCommand, GIT_SSH): a remote that depends on those has
            // no answer here, and gets pills only in the github.com-name form.
            // "--": an alias is a name, never an option.
            var info = new ProcessStartInfo(_executable);
            foreach (var argument in new[] { "-G", "--", alias })
            {
                info.ArgumentList.Add(argument);
            }

            var run = await GitInspector.RunProcessAsync(info, _timeout, CancellationToken.None).ConfigureAwait(false);
            if (run.Output is { } output)
            {
                var host = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("hostname ", StringComparison.OrdinalIgnoreCase))?[9..].Trim();
                // No Host stanza matches: ssh names the alias itself, which is no answer.
                return host is { Length: > 0 } && !host.Equals(alias, StringComparison.OrdinalIgnoreCase) ? (host, null) : (null, host is { Length: > 0 } ? null : "no hostname in the answer");
            }

            return (null, run switch
            {
                { NotFound: true } => "ssh not found",
                { DidNotStart: true } => "ssh could not be started",
                { TimedOut: true } => "timed out",
                { ExitCode: { } code } => $"exit code {code}",
                _ => "stopped without an answer",
            });
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name);
        }
    }

    private void Report(string failure)
    {
        lock (_gate)
        {
            if (!_logged.Add(failure))
            {
                return;
            }
        }

        // The kind only: the name asked about is the user's own word for a host.
        _logger?.LogWarning("Asking ssh what a host name stands for did not work: {Failure}", failure);
    }
}
