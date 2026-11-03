using CodeSwitchX.Core.GitHub;

namespace CodeSwitchX.Core.Tests.GitHub;

public class GitHubRepoTests
{
    /// <summary>What a caller does with the address: ssh is asked about a host that is not GitHub by name; <paramref name="hostOf"/> is its answer, or null for none.</summary>
    private static GitHubRepo? Read(string? url, Func<string, string?>? hostOf = null) =>
        GitHubRepo.Parse(url) is { } address ? address.RepoGiven(address.NeedsLookup ? hostOf?.Invoke(address.Host) : null) : null;

    [Theory]
    [InlineData("https://github.com/acme/tool.git", "acme/tool")]
    [InlineData("https://github.com/acme/tool", "acme/tool")]
    [InlineData("https://github.com/acme/tool/", "acme/tool")]
    [InlineData("https://user@github.com/acme/tool.git", "acme/tool")]
    [InlineData("git@github.com:acme/tool.git", "acme/tool")]
    [InlineData("ssh://git@github.com/acme/tool.git", "acme/tool")]
    [InlineData("HTTPS://GitHub.com/acme/tool-", "acme/tool-")]
    [InlineData("https://github.com/acme/my.tool.x", "acme/my.tool.x")]
    [InlineData("git@github.com-work:acme/tool.git", "acme/tool")]
    [InlineData("git@github.com-second_account:acme/tool-", "acme/tool-")]
    [InlineData("github.com-work:acme/tool.git", "acme/tool")]
    [InlineData("github.com:acme/tool.git", "acme/tool")]
    [InlineData("ssh://git@ssh.github.com:443/acme/tool.git", "acme/tool")]
    [InlineData("ssh://git@github.com:22/acme/tool.git", "acme/tool")]
    [InlineData("git@github.com-evil.example:acme/tool.git", null)]
    [InlineData("github.com-evil.example:acme/tool.git", null)]
    [InlineData("git@github.com.evil.example:acme/tool.git", null)]
    [InlineData("github.com.evil.example:acme/tool.git", null)]
    [InlineData("git@notgithub.com:acme/tool.git", null)]
    [InlineData("notgithub.com:acme/tool.git", null)]
    [InlineData("git@github.com-:acme/tool.git", null)]
    [InlineData("git@ssh.github.com:acme/tool.git", "acme/tool")]
    [InlineData("ssh.github.com:acme/tool.git", "acme/tool")]
    [InlineData("git@github.com:/acme/tool.git", "acme/tool")]
    [InlineData("git@github.com://acme/tool.git", null)]
    [InlineData("git+ssh://git@github.com/acme/tool.git", "acme/tool")]
    [InlineData("ssh+git://git@github.com/acme/tool.git", "acme/tool")]
    [InlineData("git+ssh://git@github.com-work/acme/tool.git", "acme/tool")]
    [InlineData("C:/acme/tool.git", null)]
    [InlineData(@"C:\acme\tool.git", null)]
    [InlineData("ssh://git@ssh.github.com.evil.example/acme/tool.git", null)]
    [InlineData("ssh://git@github.com-evil.example/acme/tool.git", null)]
    [InlineData("ssh://git@github.com-work/acme/tool.git", "acme/tool")]
    [InlineData("ssh://git@github.com-work:22/acme/tool.git", "acme/tool")]
    [InlineData("file://github.com/acme/tool.git", null)]
    [InlineData("file:///github.com/acme/tool.git", null)]
    [InlineData("ftp://github.com/acme/tool.git", null)]
    [InlineData("http://github.com/acme/tool.git", "acme/tool")]
    [InlineData("git://github.com/acme/tool.git", "acme/tool")]
    [InlineData("https://ssh.github.com/acme/tool", null)]
    [InlineData("https://gitlab.com/acme/tool", null)]
    [InlineData("https://notgithub.com/acme/tool", null)]
    [InlineData("https://github.com.evil.example/acme/tool", null)]
    [InlineData("https://github.com/acme", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FromRemote(string? remote, string? slug)
    {
        Read(remote)?.Slug.ShouldBe(slug);
        (Read(remote) is null).ShouldBe(slug is null);
    }

    /// <summary>What <c>ssh -G</c> says the alias is, as a function: names a test does not list resolve to nothing.</summary>
    private static Func<string, string?> Resolves(string alias, string host) => a => a == alias ? host : null;

    [Theory]
    [InlineData("git@github-work:acme/tool.git", "github-work", "github.com", "acme/tool")]
    [InlineData("github-work:acme/tool.git", "github-work", "GitHub.com", "acme/tool")]
    [InlineData("ssh://git@github-work/acme/tool.git", "github-work", "github.com", "acme/tool")]
    [InlineData("ssh://git@github-work:22/acme/tool", "github-work", "ssh.github.com", "acme/tool")]
    [InlineData("git@github-work:acme/tool.git", "github-work", "github.com-evil.example", null)]
    [InlineData("git@github-work:acme/tool.git", "github-work", "github.com.evil.example", null)]
    [InlineData("git@github-work:acme/tool.git", "github-work", "notgithub.com", null)]
    [InlineData("git@github-work:acme/tool.git", "github-work", "gitlab.com", null)]
    [InlineData("git@github-work:acme/tool.git", "github-work", "ssh.github.com.evil.example", null)]
    [InlineData("git@github-work:acme/tool.git", "github-work", "", null)]
    [InlineData("git@other:acme/tool.git", "github-work", "github.com", null)]
    [InlineData("git@github.com-corp:acme/tool.git", "github.com-corp", "git.corp.example", null)]
    [InlineData("ssh://git@github.com-corp/acme/tool.git", "github.com-corp", "git.corp.example", null)]
    [InlineData("git@github.com-corp:acme/tool.git", "github.com-corp", "github.com", "acme/tool")]
    [InlineData("git@github.com-corp:acme/tool.git", "other", "git.corp.example", "acme/tool")]
    [InlineData("git@github.com-evil.example:acme/tool.git", "other", "github.com", null)]
    [InlineData("git+ssh://git@github-work/acme/tool.git", "github-work", "github.com", "acme/tool")]
    [InlineData("ssh://git@GitHub-Work/acme/tool.git", "GitHub-Work", "github.com", "acme/tool")]
    [InlineData("ssh://git@GitHub-Work/acme/tool.git", "github-work", "github.com", null)]
    [InlineData("git@GitHub-Work:acme/tool.git", "github-work", "github.com", null)]
    [InlineData("https://other/acme/tool.git", "other", "github.com", null)]
    [InlineData("git://other/acme/tool.git", "other", "github.com", null)]
    [InlineData("file://other/acme/tool.git", "other", "github.com", null)]
    [InlineData("git@github-work:acme", "github-work", "github.com", null)]
    public void FromRemote_with_an_alias_resolved(string remote, string alias, string host, string? slug) =>
        Read(remote, Resolves(alias, host))?.Slug.ShouldBe(slug);

    [Theory]
    [InlineData("git@github-work:acme/tool.git")]
    [InlineData("ssh://git@github-work/acme/tool.git")]
    [InlineData("github-work:acme/tool.git")]
    public void An_alias_is_nothing_without_the_answer_for_it(string remote) =>
        Read(remote).ShouldBeNull();

    [Theory]
    [InlineData("git@github-work:acme/tool.git", "github-work", true)]
    [InlineData("ssh://git@GitHub-Work/acme/tool.git", "GitHub-Work", true)]
    [InlineData("ssh://git@github-work:22/acme/tool.git", "github-work", true)]
    [InlineData("git+ssh://git@github-work/acme/tool.git", "github-work", true)]
    [InlineData("github-work:acme/tool.git", "github-work", true)]
    [InlineData("git@gitlab.com:acme/tool.git", "gitlab.com", true)]
    [InlineData("git@github.com-work:acme/tool.git", "github.com-work", true)]
    [InlineData("ssh://git@github.com-work/acme/tool.git", "github.com-work", true)]
    [InlineData("git@github.com:acme/tool.git", "github.com", false)] // GitHub by its name: nothing to ask
    [InlineData("git@ssh.github.com:acme/tool.git", "ssh.github.com", false)]
    [InlineData("https://github-work/acme/tool.git", "github-work", false)] // only ssh remotes go through ~/.ssh/config
    [InlineData("git://github-work/acme/tool.git", "github-work", false)]
    [InlineData("git@-oProxyCommand=calc:acme/tool.git", "-oProxyCommand=calc", false)] // never an option for ssh
    [InlineData("ssh://git@-oProxyCommand=calc/acme/tool.git", "-oProxyCommand=calc", false)]
    public void Parse_gives_the_host_as_written_and_whether_ssh_is_to_be_asked(string remote, string host, bool lookup)
    {
        var address = GitHubRepo.Parse(remote).ShouldNotBeNull();
        address.Host.ShouldBe(host);
        address.NeedsLookup.ShouldBe(lookup);
    }

    [Theory]
    [InlineData("file://github-work/acme/tool.git")]
    [InlineData(@"C:\repos\tool")]
    [InlineData("C:/repos/tool")]
    [InlineData("/repos/tool")]
    [InlineData("git@github-work:acme")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_is_null_for_what_is_no_remote_with_a_repository(string? remote) =>
        GitHubRepo.Parse(remote).ShouldBeNull();

    [Fact]
    public void Owner_and_name_compare_ignoring_case()
    {
        new GitHubRepo("Acme", "Tool").ShouldBe(new GitHubRepo("acme", "TOOL"));
        new GitHubRepo("Acme", "Tool").GetHashCode().ShouldBe(new GitHubRepo("acme", "TOOL").GetHashCode());
        new GitHubRepo("acme", "tool").ShouldNotBe(new GitHubRepo("acme", "other"));
    }
}
