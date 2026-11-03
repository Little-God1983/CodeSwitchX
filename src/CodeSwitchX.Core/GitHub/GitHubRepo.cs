using System.Text.RegularExpressions;

namespace CodeSwitchX.Core.GitHub;

/// <summary>A repository on github.com; owner and name compare ignoring case.</summary>
public sealed record GitHubRepo(string Owner, string Name)
{
    private const string Host = "github.com";

    /// <summary>GitHub's SSH over the HTTPS port; an ssh or scp address may name it, an https one may not.</summary>
    private const string SshHost = "ssh.github.com";

    private static readonly string[] SshSchemes = ["ssh", "git+ssh", "ssh+git"];

    /// <summary>The schemes of an address with a host. Not <c>file</c>: <c>file://github.com/x</c> is a path on a share named like that.</summary>
    private static readonly string[] Schemes = ["https", "http", "git", .. SshSchemes];

    /// <summary><c>scheme://[user@]host[:port]/path</c>, the host as written (a <see cref="Uri"/> would lower-case it, and ssh matches <c>Host</c> case-sensitively).</summary>
    private static readonly Regex UrlForm = new(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.-]*)://(?:[^/@]*@)?(?<host>[^/:@\s]+)(?::\d*)?(?<path>/[^?#]*)?(?:[?#].*)?$", RegexOptions.CultureInvariant);

    /// <summary><c>[user@]host:path</c>, the form git writes for ssh. The host has two letters or more (<c>C:/dir</c> is a drive) and the path at most one leading slash and no backslash.</summary>
    private static readonly Regex ScpForm = new(@"^(?:[^@/\s]+@)?(?<host>[^@/\x5C\s:]{2,}):(?<path>/?[^/\x5C].*)$", RegexOptions.CultureInvariant);

    /// <summary><c>github.com-name</c>: the usual name of an ~/.ssh/config alias for a second account. No dot in the name: that would be another domain.</summary>
    private static readonly Regex GitHubAlias = new(@"^github\.com-[\w-]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Slug => $"{Owner}/{Name}";

    public bool Equals(GitHubRepo? other) =>
        other is not null
        && string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Owner), StringComparer.OrdinalIgnoreCase.GetHashCode(Name));

    /// <summary>The repository a git remote address points at when nothing is asked of ssh; null for anything but github.com.</summary>
    public static GitHubRepo? FromRemote(string? url) => FromRemote(url, null);

    /// <summary>
    /// The same, with <paramref name="hostOf"/> answering for an ssh host (<c>git@github-work:owner/repo</c>, <c>github.com-work</c>) what
    /// <c>ssh -G</c> makes of it, or null for no answer. The remote is GitHub when ssh says github.com or ssh.github.com; with no answer
    /// only the <c>github.com-name</c> convention is taken as GitHub. Any other host is not.
    /// </summary>
    public static GitHubRepo? FromRemote(string? url, Func<string, string?>? hostOf) =>
        Parse(url) is { } address ? address.RepoGiven(address.NeedsLookup ? hostOf?.Invoke(address.Host) : null) : null;

    /// <summary>The owner, name and host of a remote address with a host (a path or another scheme is null), whatever the host is.</summary>
    public static RemoteAddress? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = url.Trim();
        string? scheme = null;
        string host, rawPath;
        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (UrlForm.Match(url) is not { Success: true } m || !Schemes.Contains(m.Groups["scheme"].Value.ToLowerInvariant()))
            {
                return null;
            }

            scheme = m.Groups["scheme"].Value.ToLowerInvariant();
            host = m.Groups["host"].Value;
            rawPath = m.Groups["path"].Value;
        }
        else if (ScpForm.Match(url) is { Success: true } scp)
        {
            host = scp.Groups["host"].Value;
            rawPath = scp.Groups["path"].Value;
        }
        else
        {
            return null;
        }

        var path = rawPath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        var parts = path.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0)
            ? new RemoteAddress(new GitHubRepo(parts[0], parts[1]), host, scheme is null || SshSchemes.Contains(scheme))
            : null;
    }

    /// <summary>The one host rule: github.com, or ssh.github.com where ssh is spoken.</summary>
    internal static bool IsGitHubHost(string host, bool ssh) =>
        host.Equals(Host, StringComparison.OrdinalIgnoreCase) || (ssh && host.Equals(SshHost, StringComparison.OrdinalIgnoreCase));

    /// <summary>A remote address read once: its repository, the host as written, and whether it is spoken over ssh (an ssh URL or the scp form).</summary>
    public sealed record RemoteAddress(GitHubRepo Repo, string Host, bool IsSsh)
    {
        /// <summary>The host is GitHub by its name.</summary>
        public bool IsGitHub => IsGitHubHost(Host, IsSsh);

        /// <summary>An ssh host that is not GitHub by name might be an alias for it, which only <c>ssh -G</c> knows. Never one that could pass for an option.</summary>
        public bool NeedsLookup => IsSsh && !IsGitHub && !Host.StartsWith('-');

        /// <param name="sshHost">The host <c>ssh -G</c> gave for <see cref="Host"/>; null when it gave none.</param>
        public GitHubRepo? RepoGiven(string? sshHost)
        {
            if (IsGitHub)
            {
                return Repo;
            }

            if (!NeedsLookup)
            {
                return null;
            }

            return (sshHost is null ? GitHubAlias.IsMatch(Host) : IsGitHubHost(sshHost, ssh: true)) ? Repo : null;
        }
    }
}
