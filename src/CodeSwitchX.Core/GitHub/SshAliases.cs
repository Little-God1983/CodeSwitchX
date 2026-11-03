using System.Diagnostics;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.GitHub;

/// <summary>What asking ssh about a host name came to.</summary>
/// <param name="Host">The <c>hostname</c> ssh would connect to; null when it gave none.</param>
/// <param name="Failed">The ask went wrong (timeout, error exit, unreadable answer): it says nothing and is asked again. Not set when there is no ssh at all, which is an answer.</param>
public sealed record SshAnswer(string? Host, bool Failed = false)
{
    /// <summary>There is no ssh on this machine: no answer to give, and one that stays.</summary>
    public static readonly SshAnswer NoSsh = new(Host: null);

    public static readonly SshAnswer Unknown = new(Host: null, Failed: true);
}

/// <summary>
/// What an ssh host name in a git remote (<c>git@github-work:owner/repo</c>) stands for, as <c>ssh -G</c> reads it from
/// <c>~/.ssh/config</c>. A name is matched as written, case included, as ssh does. Real answers (a host, or no ssh at all) are kept
/// for <see cref="KeepFor"/>, so an edit of the config is picked up; failures are not kept, and the next ask runs ssh again. Asks run
/// off the caller's thread, and each kind of failure is logged once per run, without the name.
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
    private readonly Dictionary<string, (SshAnswer Answer, DateTimeOffset Until)> _answers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<SshAnswer>> _asking = new(StringComparer.Ordinal);
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

    /// <summary>A caller that stops waiting does not stop the ask.</summary>
    public Task<SshAnswer> HostOfAsync(string alias, CancellationToken ct)
    {
        Task<SshAnswer> asking;
        lock (_gate)
        {
            if (_answers.TryGetValue(alias, out var kept) && kept.Until > _now())
            {
                return Task.FromResult(kept.Answer);
            }

            if (!_asking.TryGetValue(alias, out asking!))
            {
                asking = _asking[alias] = Task.Run(() => AskAsync(alias));
            }
        }

        return asking.WaitAsync(ct);
    }

    private async Task<SshAnswer> AskAsync(string alias)
    {
        var (answer, failure) = await RunAsync(alias).ConfigureAwait(false);
        lock (_gate)
        {
            _asking.Remove(alias);
            if (!answer.Failed)
            {
                _answers[alias] = (answer, _now() + KeepFor);
            }
        }

        if (failure is not null)
        {
            Report(failure);
        }

        return answer;
    }

    private async Task<(SshAnswer Answer, string? Failure)> RunAsync(string alias)
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
            if (run.Output is { } output)
            {
                return output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("hostname ", StringComparison.OrdinalIgnoreCase))?[9..].Trim() is { Length: > 0 } host
                    ? (new SshAnswer(host), null)
                    : (SshAnswer.Unknown, "no hostname in the answer");
            }

            return run switch
            {
                { Pid: null, ExitCode: null, TimedOut: false } => (SshAnswer.NoSsh, "ssh not found"),
                { TimedOut: true } => (SshAnswer.Unknown, "timed out"),
                { ExitCode: { } code } => (SshAnswer.Unknown, $"exit code {code}"),
                _ => (SshAnswer.Unknown, "stopped without an answer"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (SshAnswer.Unknown, ex.GetType().Name);
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
