using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.GitHub;

/// <summary>
/// <c>gh</c> as any signed-in account without <c>gh auth switch</c>: the account's token goes to the one child process in
/// <c>GH_TOKEN</c> and nowhere else (not in arguments, not in a field).
/// </summary>
public sealed class GhCli : IGhCli
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>What reading a whole list may take: a large repository's thousand rows come slower than a single read.</summary>
    public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(120);

    private readonly string _executable;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _listTimeout;

    public GhCli() : this("gh", Timeout)
    {
    }

    internal GhCli(string executable, TimeSpan timeout, TimeSpan? listTimeout = null)
    {
        _executable = executable;
        _timeout = timeout;
        _listTimeout = listTimeout ?? ListTimeout;
    }

    public async Task<GhAccounts> AccountsAsync(CancellationToken ct)
    {
        var run = await GitInspector.RunProcessAsync(StartInfo(["auth", "status", "--json", "hosts"], null, _executable), _timeout, ct).ConfigureAwait(false);
        if (run.Output is not { } output)
        {
            return GhAccounts.Failed(FailureOf(run));
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("hosts", out var hosts)
                || hosts.ValueKind != JsonValueKind.Object)
            {
                return GhAccounts.Failed();
            }

            // No github.com among the hosts is nobody logged in; one that is not a list is an answer this cannot read.
            if (!hosts.TryGetProperty("github.com", out var accounts))
            {
                return GhAccounts.Of([]);
            }

            if (accounts.ValueKind != JsonValueKind.Array)
            {
                return GhAccounts.Failed();
            }

            // Whatever each login's state: gh exits 0 on an auth issue too, and offline every check fails. A read as a login that cannot read fails safely.
            return GhAccounts.Of(accounts.EnumerateArray()
                .Select(a => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("login", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null)
                .OfType<string>()
                .ToList());
        }
        catch (JsonException)
        {
            return GhAccounts.Failed();
        }
    }

    /// <summary>
    /// Not started at all is no <c>gh</c>; one that exits with cobra's "unknown flag" does not know <c>auth status --json</c>
    /// (the text is only read here, never kept or logged); anything else is another failure.
    /// </summary>
    private static GhFailure FailureOf(ProcessRunResult run) => run switch
    {
        { Pid: null, ExitCode: null, TimedOut: false } => GhFailure.NotFound,
        { ErrorOutput: { } error } when error.Contains("unknown flag", StringComparison.OrdinalIgnoreCase) => GhFailure.TooOld,
        _ => GhFailure.Other,
    };

    public Task<GhAnswer> ListAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunAndAnswerAsync(login, arguments, _listTimeout, ct);

    public Task<GhAnswer> AnswerAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunAndAnswerAsync(login, arguments, _timeout, ct);

    /// <param name="timeout">For the run itself; the token lookup before it keeps the usual one.</param>
    private async Task<GhAnswer> RunAndAnswerAsync(string login, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var token = (await RunAsync(["auth", "token", "--user", login], null, ct).ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            return new GhAnswer(null);
        }

        var run = await GitInspector.RunProcessAsync(StartInfo(arguments, token, _executable), timeout, ct).ConfigureAwait(false);
        return new GhAnswer(run.Output, run.FailureOutput, run.TimedOut);
    }

    private async Task<string?> RunAsync(IReadOnlyList<string> arguments, string? token, CancellationToken ct) =>
        (await GitInspector.RunProcessAsync(StartInfo(arguments, token, _executable), _timeout, ct).ConfigureAwait(false)).Output;

    /// <summary>
    /// An inherited <c>GH_TOKEN</c> or <c>GITHUB_TOKEN</c> is removed from every child: it would win over the account asked
    /// for, and make <c>gh auth token --user</c> hand it back. So is <c>GH_HOST</c>: without it gh talks to github.com.
    /// </summary>
    internal static ProcessStartInfo StartInfo(IReadOnlyList<string> arguments, string? token, string executable = "gh")
    {
        // gh writes UTF-8; a windowed process would decode the pipe with the ANSI code page and garble titles.
        var info = new ProcessStartInfo(executable) { StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Remove("GH_TOKEN");
        info.Environment.Remove("GITHUB_TOKEN");
        info.Environment.Remove("GH_HOST");
        if (token is not null)
        {
            info.Environment["GH_TOKEN"] = token;
        }

        return info;
    }
}
