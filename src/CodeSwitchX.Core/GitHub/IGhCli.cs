namespace CodeSwitchX.Core.GitHub;

/// <param name="Output">Stdout when <c>gh</c> exited with code 0; otherwise null.</param>
/// <param name="FailureOutput">Stdout when it ran to its end and exited with another code: <c>gh api</c> prints GitHub's answer there, whatever it was. Null when it did not get that far.</param>
public sealed record GhAnswer(string? Output, string? FailureOutput = null);

/// <summary>The GitHub command line, one account at a time and without switching the active one.</summary>
public interface IGhCli
{
    /// <summary>Logins signed in to github.com; empty when <c>gh</c> is missing or fails.</summary>
    Task<IReadOnlyList<string>> AccountsAsync(CancellationToken ct);

    /// <summary>Runs <c>gh</c> as that account; stdout when it exited with code 0, otherwise null. <paramref name="timeout"/> is for a run that may take longer than the usual one; the token lookup keeps the usual one.</summary>
    Task<string?> RunAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct, TimeSpan? timeout = null);

    /// <summary>The same run, with what a failed one printed: for a caller that has to tell GitHub's own "no" from a read that failed.</summary>
    Task<GhAnswer> AnswerAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct);
}
