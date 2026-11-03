namespace CodeSwitchX.Core.GitHub;

/// <summary>The GitHub command line, one account at a time and without switching the active one.</summary>
public interface IGhCli
{
    /// <summary>Logins signed in to github.com; empty when <c>gh</c> is missing or fails.</summary>
    Task<IReadOnlyList<string>> AccountsAsync(CancellationToken ct);

    /// <summary>Runs <c>gh</c> as that account; stdout when it exited with code 0, otherwise null.</summary>
    Task<string?> RunAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct);
}
