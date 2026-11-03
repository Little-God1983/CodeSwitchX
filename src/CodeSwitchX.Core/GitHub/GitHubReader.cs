using System.Globalization;
using System.Text.Json;

namespace CodeSwitchX.Core.GitHub;

/// <summary>
/// Reads a repository's open issues and pull requests through <see cref="IGhCli"/>, as whichever signed-in account can see
/// it best. Nothing is remembered: the account is worked out again on every read, so a brief failure cannot stick.
/// </summary>
public sealed class GitHubReader(IGhCli gh)
{
    private const string Query = """
        query($owner:String!,$name:String!){repository(owner:$owner,name:$name){viewerPermission
         issues(states:OPEN,first:5,orderBy:{field:CREATED_AT,direction:DESC}){totalCount nodes{number title url updatedAt labels(first:10){nodes{name color}}}}
         pullRequests(states:OPEN,first:5,orderBy:{field:CREATED_AT,direction:DESC}){totalCount nodes{number title url updatedAt headRefName author{login} labels(first:10){nodes{name color}}}}}}
        """;

    private static readonly string[] PermissionOrder = ["READ", "TRIAGE", "WRITE", "MAINTAIN", "ADMIN"];

    /// <summary>Counts and the newest items; null when no account can see the repository or the answer is unreadable.</summary>
    public async Task<RepoActivity?> ReadAsync(GitHubRepo repo, CancellationToken ct)
    {
        var accounts = (await gh.AccountsAsync(ct).ConfigureAwait(false))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The owner's own account surely sees the repository: one request and no comparing.
        var owner = accounts.FirstOrDefault(a => a.Equals(repo.Owner, StringComparison.OrdinalIgnoreCase));
        if (owner is not null)
        {
            var own = await ReadAsAsync(repo, owner, ct).ConfigureAwait(false);
            if (own is not null)
            {
                return own.Value.Activity;
            }

            accounts.Remove(owner);
        }

        (RepoActivity Activity, int Rank)? best = null;
        foreach (var account in accounts)
        {
            var read = await ReadAsAsync(repo, account, ct).ConfigureAwait(false);
            // Strictly greater: on a tie the first login alphabetically stays.
            if (read is not null && (best is null || read.Value.Rank > best.Value.Rank))
            {
                best = read;
            }
        }

        return best?.Activity;
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

    private async Task<(RepoActivity Activity, int Rank)?> ReadAsAsync(GitHubRepo repo, string account, CancellationToken ct)
    {
        var output = await gh.RunAsAsync(account,
        [
            "api", "graphql", "-f", $"query={Query}", "-f", $"owner={repo.Owner}", "-f", $"name={repo.Name}",
        ], ct).ConfigureAwait(false);
        if (output is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            var node = doc.RootElement.GetProperty("data").GetProperty("repository");
            if (node.ValueKind != JsonValueKind.Object)
            {
                return null;
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
            return (activity, Array.IndexOf(PermissionOrder, permission));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
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
                author = a.GetProperty("login").GetString();
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
