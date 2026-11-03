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

    /// <summary>The list is the whole truth: it stopped short of the limit and no row was left out, so its length is the count.</summary>
    public bool IsExact => Skipped == 0 && Items.Count < Limit;

    /// <summary>The count to show: the list's own length when it is exact, otherwise <paramref name="known"/> (what GraphQL counted), never below what the list shows.</summary>
    public int TotalGiven(int known) => IsExact ? Items.Count : Math.Max(known, Items.Count);
}

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
    /// <summary>How many of the newest items a result keeps for the preview; the GraphQL read asks for the same number.</summary>
    public const int PreviewCount = 5;

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
