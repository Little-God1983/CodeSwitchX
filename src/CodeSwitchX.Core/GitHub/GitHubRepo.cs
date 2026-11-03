using System.Text.RegularExpressions;

namespace CodeSwitchX.Core.GitHub;

/// <summary>A repository on github.com; owner and name compare ignoring case.</summary>
public sealed record GitHubRepo(string Owner, string Name)
{
    private const string Host = "github.com";

    private static readonly Regex ScpForm = new(@"^[^@/\s]+@github\.com:(?<path>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Slug => $"{Owner}/{Name}";

    public bool Equals(GitHubRepo? other) =>
        other is not null
        && string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Owner), StringComparer.OrdinalIgnoreCase.GetHashCode(Name));

    /// <summary>The repository a git remote address points at; null for anything but github.com.</summary>
    public static GitHubRepo? FromRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = url.Trim();
        string path;
        if (ScpForm.Match(url) is { Success: true } scp)
        {
            path = scp.Groups["path"].Value;
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                 && uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
        {
            path = uri.AbsolutePath;
        }
        else
        {
            return null;
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        var parts = path.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0) ? new GitHubRepo(parts[0], parts[1]) : null;
    }
}
