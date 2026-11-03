using CodeSwitchX.Core.GitHub;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// A repository's open issues or open pull requests on a tile's line. Two pills are equal when they hold the same
/// <see cref="RepoActivity"/> instance (<see cref="GitHubActivity"/> keeps it while nothing changed), which is what lets the
/// tile skip an update and keep an open tooltip open. <see cref="Rows"/> depends on the time and is left out of that.
/// </summary>
public sealed record GitHubPill(RepoActivity Activity, GitHubItemKind Kind)
{
    /// <summary>How many rows the preview shows at most; the reader fetches the same number.</summary>
    private const int PreviewCount = 5;

    public int Count => Kind == GitHubItemKind.Issue ? Activity.OpenIssues : Activity.OpenPullRequests;

    public string Heading => $"{Activity.Repo.Name} {Count} open";

    /// <summary>What the preview leaves out; null when it shows everything.</summary>
    public string? MoreText => Count > PreviewCount ? $"+{Count - PreviewCount} more. Click the pill for all of them" : null;

    public string AutomationName => $"{Count} open {NounFor(Kind, Count)} in {Activity.Repo.Name}";

    internal static string NounFor(GitHubItemKind kind, int count) => (kind == GitHubItemKind.Issue ? "issue" : "pull request") + (count == 1 ? "" : "s");

    public IReadOnlyList<GitHubRow> Rows
    {
        get
        {
            var now = DateTimeOffset.Now;
            return (Kind == GitHubItemKind.Issue ? Activity.NewestIssues : Activity.NewestPullRequests)
                .Select(item => GitHubRow.From(item, Activity.Account, now))
                .ToList();
        }
    }
}

/// <param name="ByText">"by someone" for another account's pull request; null for the account's own and for issues.</param>
public sealed record GitHubRow(GitHubItem Item, string NumberText, string? ByText, string AgeText)
{
    public static GitHubRow From(GitHubItem item, string account, DateTimeOffset now) => new(
        item,
        $"#{item.Number}",
        item.Author is { } author && !author.Equals(account, StringComparison.OrdinalIgnoreCase) ? $"by {author}" : null,
        AgeOf(now - item.UpdatedAt));

    private static string AgeOf(TimeSpan age) => age.TotalDays switch
    {
        < 1 => "updated today",
        < 30 => $"updated {(int)age.TotalDays}d ago",
        var days => (int)(days / 30) is 1 ? "updated 1 month ago" : $"updated {(int)(days / 30)} months ago",
    };
}
