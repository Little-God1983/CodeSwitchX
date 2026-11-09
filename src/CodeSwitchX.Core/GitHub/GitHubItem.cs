namespace CodeSwitchX.Core.GitHub;

public enum GitHubItemKind { Issue, PullRequest }

public sealed record GitHubLabel(string Name, string Color);

/// <param name="Branch">Pull requests only: the head branch.</param>
/// <param name="Author">Pull requests only: the author's login; null when the account is deleted.</param>
/// <param name="BaseBranch">Pull requests only: the branch it merges into (#408); null when it was not read.</param>
/// <param name="IsCrossRepository">Pull requests only: the head branch lives in a fork, so no checkout of this repository is on it (#408).</param>
public sealed record GitHubItem(
    int Number,
    string Title,
    string Url,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<GitHubLabel> Labels,
    string? Branch = null,
    string? Author = null,
    string? BaseBranch = null,
    bool IsCrossRepository = false);

/// <summary>
/// Every open item of one kind that <c>gh</c> listed, newest first. <paramref name="Skipped"/> counts the rows it could not
/// make an item of (an odd value in one of them), which are not in <paramref name="Items"/>.
/// </summary>
public sealed record GitHubList(IReadOnlyList<GitHubItem> Items, int Skipped = 0)
{
    /// <summary>The most items one list read asks <c>gh</c> for.</summary>
    public const int Limit = 1000;

    /// <summary>The list ran to the limit, so the repository may hold more than it shows.</summary>
    public bool ReachedLimit => Items.Count + Skipped >= Limit;

    /// <summary>The count to show. A list that stopped short of the limit holds every row <c>gh</c> returned, skipped ones included, and that is the count; one that reached the limit may be cut short, so <paramref name="known"/> (what GraphQL counted) stays, never below what was read.</summary>
    public int TotalGiven(int known) => ReachedLimit ? Math.Max(known, Items.Count + Skipped) : Items.Count + Skipped;
}

/// <param name="Account">The login that read it.</param>
/// <param name="OpenIssues">The total of open issues, not the number of rows in <paramref name="NewestIssues"/>.</param>
/// <param name="DefaultBranch">The branch GitHub calls the default: where pull requests merge into, and what the Yard pulls (#407). Null when the repository has none (an empty one) or it was not read.</param>
public sealed record RepoActivity(
    GitHubRepo Repo,
    string Account,
    int OpenIssues,
    int OpenPullRequests,
    IReadOnlyList<GitHubItem> NewestIssues,
    IReadOnlyList<GitHubItem> NewestPullRequests,
    string? DefaultBranch = null)
{
    /// <summary>How many of the newest items a result keeps for the preview; the GraphQL read asks for the same number.</summary>
    public const int PreviewCount = 5;

    /// <summary>Same repository, account, counts, previews and default branch; the lists are compared by content.</summary>
    public bool SameAs(RepoActivity other) =>
        Repo == other.Repo
        && Account == other.Account
        && OpenIssues == other.OpenIssues
        && OpenPullRequests == other.OpenPullRequests
        && DefaultBranch == other.DefaultBranch
        && SameItems(NewestIssues, other.NewestIssues)
        && SameItems(NewestPullRequests, other.NewestPullRequests);

    /// <summary>Same repository, account, and count and preview of one kind; the other kind may differ.</summary>
    public bool SameKindAs(RepoActivity other, GitHubItemKind kind) =>
        Repo == other.Repo
        && Account == other.Account
        && (kind == GitHubItemKind.Issue
            ? OpenIssues == other.OpenIssues && SameItems(NewestIssues, other.NewestIssues)
            : OpenPullRequests == other.OpenPullRequests && SameItems(NewestPullRequests, other.NewestPullRequests));

    private static bool SameItems(IReadOnlyList<GitHubItem> a, IReadOnlyList<GitHubItem> b) =>
        a.Count == b.Count && a.Zip(b).All(p => SameItem(p.First, p.Second));

    private static bool SameItem(GitHubItem a, GitHubItem b) =>
        a.Number == b.Number
        && a.Title == b.Title
        && a.Url == b.Url
        && a.UpdatedAt == b.UpdatedAt
        && a.Branch == b.Branch
        && a.Author == b.Author
        && a.BaseBranch == b.BaseBranch
        && a.IsCrossRepository == b.IsCrossRepository
        && a.Labels.SequenceEqual(b.Labels);
}
