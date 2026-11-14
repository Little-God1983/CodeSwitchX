using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.GitHub;

/// <summary>What reading a repository came to: what was read, or nothing, with <paramref name="NobodySeesIt"/> telling GitHub's own "no" from a read that failed.</summary>
public sealed record RepoRead(RepoActivity? Activity, bool NobodySeesIt = false)
{
    public static readonly RepoRead Failed = new(null, NobodySeesIt: false);

    public static readonly RepoRead NotFound = new(null, NobodySeesIt: true);
}

/// <summary>
/// Reads a repository's open issues and pull requests through <see cref="IGhCli"/>, as whichever signed-in account can see
/// it best. Nothing is remembered: which account reads is worked out again on every read, so a brief failure cannot stick.
/// </summary>
public sealed class GitHubReader(IGhCli gh, ILogger<GitHubReader>? logger = null)
{
    private const string QueryTemplate = """
        query($owner:String!,$name:String!){repository(owner:$owner,name:$name){viewerPermission defaultBranchRef{name target{oid} commits:target{... on Commit{history{totalCount}}}}
         issues(states:OPEN,first:PREVIEW,orderBy:{field:CREATED_AT,direction:DESC}){totalCount nodes{number title url updatedAt labels(first:10){nodes{name color}}}}
         pullRequests(states:OPEN,first:PREVIEW,orderBy:{field:CREATED_AT,direction:DESC}){totalCount nodes{number title url updatedAt headRefName headRefOid baseRefName isCrossRepository author{login} labels(first:10){nodes{name color}}}}}}
        """;

    private static readonly string Query = QueryTemplate.Replace("PREVIEW", RepoActivity.PreviewCount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private const string AppPrefix = "app/";

    private const string NotFoundType = "NOT_FOUND";

    private static readonly string[] PermissionOrder = ["READ", "TRIAGE", "WRITE", "MAINTAIN", "ADMIN"];

    /// <summary>
    /// Counts and the newest items, read as the best of <paramref name="accounts"/>. Nothing read is <see cref="RepoRead.NotFound"/>
    /// only when every account said so; an account that failed might be the one that can see it, and no accounts said nothing.
    /// </summary>
    public async Task<RepoRead> ReadAsync(GitHubRepo repo, IReadOnlyList<string> accounts, CancellationToken ct)
    {
        var candidates = accounts.Order(StringComparer.OrdinalIgnoreCase).ToList();
        var everyoneSaysNotFound = candidates.Count > 0;

        // The owner's own account surely sees the repository: one request and no comparing.
        var owner = candidates.FirstOrDefault(a => a.Equals(repo.Owner, StringComparison.OrdinalIgnoreCase));
        if (owner is not null)
        {
            var own = await ReadAsAsync(repo, owner, ct).ConfigureAwait(false);
            if (own.Activity is not null)
            {
                return new RepoRead(own.Activity);
            }

            everyoneSaysNotFound &= own.NotFound;
            candidates.Remove(owner);
        }

        AccountRead? best = null;
        foreach (var account in candidates)
        {
            var read = await ReadAsAsync(repo, account, ct).ConfigureAwait(false);
            everyoneSaysNotFound &= read.NotFound;
            // Strictly greater: on a tie the first login alphabetically stays.
            if (read.Activity is not null && (best is null || read.Rank > best.Rank))
            {
                best = read;
            }
        }

        return best is not null ? new RepoRead(best.Activity)
            : everyoneSaysNotFound ? RepoRead.NotFound
            : RepoRead.Failed;
    }

    /// <summary>
    /// The newest open items of one kind (at most <see cref="GitHubList.Limit"/>), as that account; null when it cannot read them.
    /// A row with an odd value is left out and counted in <see cref="GitHubList.Skipped"/>.
    /// </summary>
    public async Task<GitHubList?> ListAsync(GitHubRepo repo, string account, GitHubItemKind kind, CancellationToken ct)
    {
        var isPullRequest = kind == GitHubItemKind.PullRequest;
        var answer = await gh.ListAsAsync(account,
        [
            isPullRequest ? "pr" : "issue", "list", "-R", repo.Slug, "--state", "open", "--limit", GitHubList.Limit.ToString(CultureInfo.InvariantCulture),
            "--json", isPullRequest ? "number,title,url,updatedAt,labels,headRefName,headRefOid,baseRefName,isCrossRepository,author" : "number,title,url,updatedAt,labels",
        ], ct).ConfigureAwait(false);
        if (answer.Output is not { } output)
        {
            // A read the caller cancelled is no failure.
            if (ct.IsCancellationRequested)
            {
                return null;
            }

            // No names: a repository's name is not for a log.
            logger?.LogWarning(answer.TimedOut ? "Reading a GitHub list ran out of its time" : "Reading a GitHub list failed");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            var list = Split(doc.RootElement.EnumerateArray().Select(row => ParseItem(row, graphql: false, isPullRequest)));
            if (list is null)
            {
                logger?.LogWarning("No row of a GitHub list could be read");
            }

            return list;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            logger?.LogWarning("A GitHub list was not in the format expected");
            return null;
        }
    }

    /// <summary>One account's read: what it found, or nothing, and whether that nothing is GitHub's own "not found".</summary>
    private sealed record AccountRead(RepoActivity? Activity = null, int Rank = 0, bool NotFound = false);

    private async Task<AccountRead> ReadAsAsync(GitHubRepo repo, string account, CancellationToken ct)
    {
        var answer = await gh.AnswerAsAsync(account,
        [
            "api", "graphql", "-f", $"query={Query}", "-f", $"owner={repo.Owner}", "-f", $"name={repo.Name}",
        ], ct).ConfigureAwait(false);

        // A cancelled run gives nothing, which is neither "cannot see it" nor a reason to start the other accounts.
        ct.ThrowIfCancellationRequested();
        if ((answer.Output ?? OnlyTheCountFailed(answer.FailureOutput)) is not { } output)
        {
            return new AccountRead(NotFound: IsNotFound(answer.FailureOutput));
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            var node = doc.RootElement.GetProperty("data").GetProperty("repository");
            if (node.ValueKind != JsonValueKind.Object)
            {
                return new AccountRead();
            }

            var issues = node.GetProperty("issues");
            var pullRequests = node.GetProperty("pullRequests");
            var issueCount = issues.GetProperty("totalCount").GetInt32();
            var pullRequestCount = pullRequests.GetProperty("totalCount").GetInt32();
            if (Newest(issues, isPullRequest: false) is not { } newestIssues
                || Newest(pullRequests, isPullRequest: true) is not { } newestPullRequests)
            {
                return new AccountRead();
            }

            // An empty repository has no default branch; then nothing is pulled (#407).
            var defaultBranch = node.TryGetProperty("defaultBranchRef", out var d) && d.ValueKind == JsonValueKind.Object
                && d.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var activity = new RepoActivity(repo, account, issueCount, pullRequestCount, newestIssues, newestPullRequests, defaultBranch,
                defaultBranch is null ? null : HeadOf(d));
            var permission = node.TryGetProperty("viewerPermission", out var p) ? p.GetString() : null;
            return new AccountRead(activity, Array.IndexOf(PermissionOrder, permission));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return new AccountRead();
        }
    }

    /// <summary>
    /// Where the default branch stands (#426): its newest commit and how many it holds. Read leniently, as the name is: an answer without
    /// them is still a read of the counts, and the tiles then say nothing about being behind.
    /// </summary>
    private static GitHubHead? HeadOf(JsonElement defaultBranchRef)
    {
        if (!defaultBranchRef.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Object
            || !target.TryGetProperty("oid", out var oid) || oid.ValueKind != JsonValueKind.String || oid.GetString() is not { Length: > 0 } sha)
        {
            return null;
        }

        // Asked for under a name of its own (#429): GitHub's history and its count may not be null, so an error there nulls everything up
        // to the nearest field that may, which is this one and not the commit.
        var commits = defaultBranchRef.TryGetProperty("commits", out var counted) && counted.ValueKind == JsonValueKind.Object
            && counted.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Object
            && history.TryGetProperty("totalCount", out var total) && total.ValueKind == JsonValueKind.Number && total.TryGetInt32(out var count) ? count : (int?)null;
        return new GitHubHead(sha, commits);
    }

    /// <summary>The items of a preview, odd ones left out; null when the read failed (see <see cref="Split"/>).</summary>
    private static IReadOnlyList<GitHubItem>? Newest(JsonElement kind, bool isPullRequest) =>
        Split(kind.GetProperty("nodes").EnumerateArray().Select(n => ParseItem(n, graphql: true, isPullRequest)))?.Items;

    /// <summary>
    /// The parsed rows (null for an odd one) as the items read and the count left out. Two rows or more and not one read means the
    /// format is not what it was, which is a read that failed: null. A single odd row is a skipped one.
    /// </summary>
    private static GitHubList? Split(IEnumerable<GitHubItem?> parsed)
    {
        var rows = parsed.ToList();
        var list = new GitHubList(rows.OfType<GitHubItem>().ToList(), rows.Count(item => item is null));
        return list.Items.Count == 0 && list.Skipped > 1 ? null : list;
    }

    /// <summary>
    /// What <c>gh api graphql</c> printed before exiting with an error, when every error is about the default branch's count of commits
    /// (#429), which rides in the one request for the pills under a name of its own, so its commit comes all the same. The counts and the
    /// lists are read, and the line's "behind" goes without a count. An answer without the commit is no read: it would wipe a "behind"
    /// known from the last one.
    /// Null for any other answer.
    /// </summary>
    internal static string? OnlyTheCountFailed(string? printed)
    {
        if (printed is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(printed);
            var root = doc.RootElement;
            return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("repository", out var repository) && repository.ValueKind == JsonValueKind.Object
                && repository.TryGetProperty("defaultBranchRef", out var branch) && branch.ValueKind == JsonValueKind.Object
                && branch.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object
                && target.TryGetProperty("oid", out var oid) && oid.ValueKind == JsonValueKind.String
                && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
                && errors.EnumerateArray().All(OnTheCount)
                ? printed
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }

        static bool OnTheCount(JsonElement error) =>
            error.ValueKind == JsonValueKind.Object && error.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.Array
            && path.GetArrayLength() > 3
            && path[0].ValueEquals("repository") && path[1].ValueEquals("defaultBranchRef") && path[2].ValueEquals("commits") && path[3].ValueEquals("history");
    }

    /// <summary>
    /// What <c>gh api graphql</c> printed before exiting with an error says the account cannot see the repository: no repository
    /// and nothing but NOT_FOUND errors. Any other error (forbidden, rate limit, a bad gateway) says nothing about who can.
    /// </summary>
    private static bool IsNotFound(string? printed)
    {
        if (printed is null)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(printed);
            var root = doc.RootElement;
            return root.GetProperty("data").GetProperty("repository").ValueKind == JsonValueKind.Null
                && root.GetProperty("errors") is { } errors
                && errors.GetArrayLength() > 0
                && errors.EnumerateArray().All(e => e.GetProperty("type").ValueEquals(NotFoundType));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The item a row makes, or null when a value in it is odd: the row is left out, not the list.</summary>
    /// <param name="graphql">GraphQL wraps the label array in <c>nodes</c>, <c>gh issue list</c> does not.</param>
    private static GitHubItem? ParseItem(JsonElement row, bool graphql, bool isPullRequest)
    {
        try
        {
            var labelRows = row.GetProperty("labels");
            var labels = (graphql ? labelRows.GetProperty("nodes") : labelRows).EnumerateArray()
                .Select(l => new GitHubLabel(l.GetProperty("name").GetString() ?? "", l.GetProperty("color").GetString() ?? ""))
                .ToList();
            string? branch = null;
            string? author = null;
            string? baseBranch = null;
            var fromFork = false;
            string? headSha = null;
            if (isPullRequest)
            {
                var head = row.GetProperty("headRefName");
                if (head.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    return null;
                }

                branch = head.GetString();
                // Read where given; a missing or odd one leaves the row as it is, without the merge's details (#408).
                baseBranch = row.TryGetProperty("baseRefName", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
                fromFork = row.TryGetProperty("isCrossRepository", out var f) && f.ValueKind == JsonValueKind.True;
                headSha = row.TryGetProperty("headRefOid", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
                if (row.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object)
                {
                    // gh pr list can give an author without a login, and spells a bot app/name where GraphQL says name.
                    author = a.TryGetProperty("login", out var login) ? login.GetString() : null;
                    if (author is not null && author.StartsWith(AppPrefix, StringComparison.Ordinal))
                    {
                        author = author[AppPrefix.Length..];
                    }
                }
            }

            var title = row.GetProperty("title");
            var url = row.GetProperty("url");
            var updated = row.GetProperty("updatedAt");
            if (!row.GetProperty("number").TryGetInt32(out var number)
                || title.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                || url.ValueKind != JsonValueKind.String
                || updated.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(updated.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var updatedAt))
            {
                return null;
            }

            return new GitHubItem(number, title.GetString() ?? "", url.GetString()!, updatedAt, labels, branch, author, baseBranch, fromFork, headSha);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }
}
