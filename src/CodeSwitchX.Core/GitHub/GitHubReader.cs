using System.Globalization;
using System.Text.Json;

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
public sealed class GitHubReader(IGhCli gh)
{
    private const string Query = """
        query($owner:String!,$name:String!){repository(owner:$owner,name:$name){viewerPermission
         issues(states:OPEN,first:5,orderBy:{field:CREATED_AT,direction:DESC}){totalCount nodes{number title url updatedAt labels(first:10){nodes{name color}}}}
         pullRequests(states:OPEN,first:5,orderBy:{field:CREATED_AT,direction:DESC}){totalCount nodes{number title url updatedAt headRefName author{login} labels(first:10){nodes{name color}}}}}}
        """;

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

    /// <summary>Every open item of one kind, as that account; null when it cannot read them.</summary>
    public async Task<IReadOnlyList<GitHubItem>?> ListAsync(GitHubRepo repo, string account, GitHubItemKind kind, CancellationToken ct)
    {
        var isPullRequest = kind == GitHubItemKind.PullRequest;
        var output = await gh.RunAsAsync(account,
        [
            isPullRequest ? "pr" : "issue", "list", "-R", repo.Slug, "--state", "open", "--limit", "1000",
            "--json", isPullRequest ? "number,title,url,updatedAt,labels,headRefName,author" : "number,title,url,updatedAt,labels",
        ], ct).ConfigureAwait(false);
        if (output is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            return doc.RootElement.EnumerateArray()
                .Select(row => ParseItem(row, row.GetProperty("labels"), isPullRequest))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
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
        if (answer.Output is not { } output)
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
            var activity = new RepoActivity(
                repo,
                account,
                issues.GetProperty("totalCount").GetInt32(),
                pullRequests.GetProperty("totalCount").GetInt32(),
                issues.GetProperty("nodes").EnumerateArray().Select(n => ParseItem(n, n.GetProperty("labels").GetProperty("nodes"), false)).ToList(),
                pullRequests.GetProperty("nodes").EnumerateArray().Select(n => ParseItem(n, n.GetProperty("labels").GetProperty("nodes"), true)).ToList());
            var permission = node.TryGetProperty("viewerPermission", out var p) ? p.GetString() : null;
            return new AccountRead(activity, Array.IndexOf(PermissionOrder, permission));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return new AccountRead();
        }
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

    /// <param name="labelRows">The label array: GraphQL wraps it in <c>nodes</c>, <c>gh issue list</c> does not.</param>
    private static GitHubItem ParseItem(JsonElement row, JsonElement labelRows, bool isPullRequest)
    {
        var labels = labelRows.EnumerateArray()
            .Select(l => new GitHubLabel(l.GetProperty("name").GetString() ?? "", l.GetProperty("color").GetString() ?? ""))
            .ToList();
        string? branch = null;
        string? author = null;
        if (isPullRequest)
        {
            branch = row.GetProperty("headRefName").GetString();
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

        return new GitHubItem(
            row.GetProperty("number").GetInt32(),
            row.GetProperty("title").GetString() ?? "",
            row.GetProperty("url").GetString() ?? "",
            DateTimeOffset.Parse(row.GetProperty("updatedAt").GetString()!, CultureInfo.InvariantCulture),
            labels,
            branch,
            author);
    }
}
