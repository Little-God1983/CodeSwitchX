namespace CodeSwitchX.Core.GitHub;

public enum GitHubItemKind { Issue, PullRequest }

public sealed record GitHubLabel(string Name, string Color);

/// <param name="Branch">Pull requests only: the head branch.</param>
/// <param name="Author">Pull requests only: the author's login; null when the account is deleted.</param>
public sealed record GitHubItem(
    int Number,
    string Title,
    string Url,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<GitHubLabel> Labels,
    string? Branch = null,
    string? Author = null);

/// <param name="Account">The login that read it.</param>
/// <param name="OpenIssues">The total of open issues, not the number of rows in <paramref name="NewestIssues"/>.</param>
public sealed record RepoActivity(
    GitHubRepo Repo,
    string Account,
    int OpenIssues,
    int OpenPullRequests,
    IReadOnlyList<GitHubItem> NewestIssues,
    IReadOnlyList<GitHubItem> NewestPullRequests)
{
    /// <summary>Same repository, account, counts and previews; the lists are compared by content.</summary>
    public bool SameAs(RepoActivity other) =>
        Repo == other.Repo
        && Account == other.Account
        && OpenIssues == other.OpenIssues
        && OpenPullRequests == other.OpenPullRequests
        && SameItems(NewestIssues, other.NewestIssues)
        && SameItems(NewestPullRequests, other.NewestPullRequests);

    private static bool SameItems(IReadOnlyList<GitHubItem> a, IReadOnlyList<GitHubItem> b) =>
        a.Count == b.Count && a.Zip(b).All(p => SameItem(p.First, p.Second));

    private static bool SameItem(GitHubItem a, GitHubItem b) =>
        a.Number == b.Number
        && a.Title == b.Title
        && a.Url == b.Url
        && a.UpdatedAt == b.UpdatedAt
        && a.Branch == b.Branch
        && a.Author == b.Author
        && a.Labels.SequenceEqual(b.Labels);
}
