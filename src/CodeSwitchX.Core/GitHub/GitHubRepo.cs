using System.Text.RegularExpressions;

namespace CodeSwitchX.Core.GitHub;

/// <summary>A repository on github.com; owner and name compare ignoring case.</summary>
public sealed record GitHubRepo(string Owner, string Name)
{
    private const string Host = "github.com";

    /// <summary>GitHub's SSH over the HTTPS port, as an ssh:// address.</summary>
    private const string SshHost = "ssh.github.com";

    /// <summary>The schemes of an address with a host. Not <c>file</c>: <c>file://github.com/x</c> is a path on a share named like that.</summary>
    private static readonly string[] Schemes = ["https", "http", "ssh", "git"];

    /// <summary><c>[user@]host:path</c>, the form git writes for ssh. The path is no <c>//</c> and no backslash: that is a URL or a Windows path.</summary>
    private static readonly Regex ScpForm = new(@"^(?:[^@/\s]+@)?(?<host>[^@/\x5C\s:]+):(?<path>[^/\x5C].*)$", RegexOptions.CultureInvariant);

    /// <summary><c>github.com-name</c>: an ~/.ssh/config alias, the usual way to run two accounts. No dot in the name: that would be another domain.</summary>
    private static readonly Regex GitHubAlias = new(@"^github\.com-[\w-]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Slug => $"{Owner}/{Name}";

    public bool Equals(GitHubRepo? other) =>
        other is not null
        && string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Owner), StringComparer.OrdinalIgnoreCase.GetHashCode(Name));

    /// <summary>The repository a git remote address points at; null for anything but github.com.</summary>
    public static GitHubRepo? FromRemote(string? url) => FromRemote(url, null);

    /// <summary>
    /// The same, and an ssh address whose host is an alias (<c>git@github-work:owner/repo</c>) counts when <paramref name="hostOf"/>
    /// says the alias is github.com or ssh.github.com. It is given the alias and answers what <c>ssh -G</c> makes of it, or null;
    /// an alias resolving to anything else, a look-alike included, is not GitHub.
    /// </summary>
    public static GitHubRepo? FromRemote(string? url, Func<string, string?>? hostOf)
    {
        if (Split(url) is not var (scheme, host, rawPath))
        {
            return null;
        }

        var isSsh = scheme is "ssh" or null;
        var direct = host.Equals(Host, StringComparison.OrdinalIgnoreCase)
                     || (scheme == "ssh" && host.Equals(SshHost, StringComparison.OrdinalIgnoreCase))
                     || (isSsh && GitHubAlias.IsMatch(host));
        if (!direct)
        {
            if (!isSsh || hostOf is null || !IsAlias(host) || hostOf(host) is not { } resolved
                || !(resolved.Equals(Host, StringComparison.OrdinalIgnoreCase) || resolved.Equals(SshHost, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
        }

        var path = rawPath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        var parts = path.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0) ? new GitHubRepo(parts[0], parts[1]) : null;
    }

    /// <summary>
    /// The host of an ssh remote that might be an alias for GitHub, to look up with <c>ssh -G</c>; null when the address is not
    /// an ssh one, names GitHub itself, or has a host that could pass for an option.
    /// </summary>
    public static string? AliasOf(string? url)
    {
        if (Split(url) is not var (scheme, host, _) || scheme is not ("ssh" or null) || !IsAlias(host))
        {
            return null;
        }

        return host.Equals(Host, StringComparison.OrdinalIgnoreCase) || host.Equals(SshHost, StringComparison.OrdinalIgnoreCase) || GitHubAlias.IsMatch(host) ? null : host;
    }

    private static bool IsAlias(string host) => host.Length > 0 && !host.StartsWith('-');

    /// <returns>Scheme (null for the scp form), host and path of an address with a host; null for anything else.</returns>
    private static (string? Scheme, string Host, string Path)? Split(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = url.Trim();
        if (url.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && Schemes.Contains(uri.Scheme) && uri.Host.Length > 0
                ? (uri.Scheme, uri.Host, uri.AbsolutePath)
                : null;
        }

        return ScpForm.Match(url) is { Success: true } scp ? (null, scp.Groups["host"].Value, scp.Groups["path"].Value) : null;
    }
}
