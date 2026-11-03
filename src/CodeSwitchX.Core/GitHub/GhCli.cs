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

    private readonly string _executable;
    private readonly TimeSpan _timeout;

    public GhCli() : this("gh", Timeout)
    {
    }

    internal GhCli(string executable, TimeSpan timeout)
    {
        _executable = executable;
        _timeout = timeout;
    }

    public async Task<IReadOnlyList<string>> AccountsAsync(CancellationToken ct)
    {
        var output = await RunAsync(["auth", "status", "--json", "hosts"], null, ct).ConfigureAwait(false);
        if (output is null)
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("hosts", out var hosts)
                || !hosts.TryGetProperty("github.com", out var accounts)
                || accounts.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            // Whatever each login's state: gh exits 0 on an auth issue too, and offline every check fails. A read as a login that cannot read fails safely.
            return accounts.EnumerateArray()
                .Select(a => a.TryGetProperty("login", out var l) ? l.GetString() : null)
                .OfType<string>()
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<string?> RunAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct) =>
        (await AnswerAsAsync(login, arguments, ct).ConfigureAwait(false)).Output;

    public async Task<GhAnswer> AnswerAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var token = (await RunAsync(["auth", "token", "--user", login], null, ct).ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            return new GhAnswer(null);
        }

        var run = await GitInspector.RunProcessAsync(StartInfo(arguments, token, _executable), _timeout, ct).ConfigureAwait(false);
        return new GhAnswer(run.Output, run.FailureOutput);
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
