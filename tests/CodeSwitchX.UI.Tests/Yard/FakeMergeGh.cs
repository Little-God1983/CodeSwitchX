using CodeSwitchX.Core.GitHub;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>gh as <see cref="GitHubMerger"/> sees it (#408): every merge answers <see cref="Answer"/>, merged by default.</summary>
internal sealed class FakeMergeGh : IGhCli
{
    public GhAnswer Answer { get; set; } = new(string.Empty);

    /// <summary>The login and arguments of each merge run.</summary>
    public List<(string Login, IReadOnlyList<string> Arguments)> Runs { get; } = [];

    public Task<GhAccounts> AccountsAsync(CancellationToken ct) => Task.FromResult(GhAccounts.Of(["me"]));

    public Task<GhAnswer> ListAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        lock (Runs)
        {
            Runs.Add((login, arguments));
        }

        return Task.FromResult(Answer);
    }

    public Task<GhAnswer> AnswerAsAsync(string login, IReadOnlyList<string> arguments, CancellationToken ct) => ListAsAsync(login, arguments, ct);
}
