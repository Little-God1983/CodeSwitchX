using CodeSwitchX.Core.GitHub;

namespace CodeSwitchX.Core.Tests.GitHub;

public class GitHubRepoTests
{
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
    [InlineData("git@ssh.github.com:acme/tool.git", null)]
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
        GitHubRepo.FromRemote(remote)?.Slug.ShouldBe(slug);
        (GitHubRepo.FromRemote(remote) is null).ShouldBe(slug is null);
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
    [InlineData("https://other/acme/tool.git", "other", "github.com", null)]
    [InlineData("git://other/acme/tool.git", "other", "github.com", null)]
    [InlineData("file://other/acme/tool.git", "other", "github.com", null)]
    [InlineData("git@github-work:acme", "github-work", "github.com", null)]
    public void FromRemote_with_an_alias_resolved(string remote, string alias, string host, string? slug) =>
        GitHubRepo.FromRemote(remote, Resolves(alias, host))?.Slug.ShouldBe(slug);

    [Theory]
    [InlineData("git@github-work:acme/tool.git")]
    [InlineData("ssh://git@github-work/acme/tool.git")]
    [InlineData("github-work:acme/tool.git")]
    public void An_alias_is_nothing_without_the_answer_for_it(string remote) =>
        GitHubRepo.FromRemote(remote).ShouldBeNull();

    [Theory]
    [InlineData("git@github-work:acme/tool.git", "github-work")]
    [InlineData("ssh://git@github-work/acme/tool.git", "github-work")]
    [InlineData("ssh://git@github-work:22/acme/tool.git", "github-work")]
    [InlineData("github-work:acme/tool.git", "github-work")]
    [InlineData("git@gitlab.com:acme/tool.git", "gitlab.com")]
    [InlineData("git@github.com:acme/tool.git", null)] // GitHub by its name: nothing to ask
    [InlineData("git@github.com-work:acme/tool.git", null)]
    [InlineData("ssh://git@github.com-work/acme/tool.git", null)]
    [InlineData("https://github-work/acme/tool.git", null)] // only ssh remotes go through ~/.ssh/config
    [InlineData("file://github-work/acme/tool.git", null)]
    [InlineData("git@-oProxyCommand=calc:acme/tool.git", null)] // never an option for ssh
    [InlineData("ssh://git@-oProxyCommand=calc/acme/tool.git", null)]
    [InlineData(@"C:\repos\tool", null)]
    [InlineData("/repos/tool", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AliasOf(string? remote, string? alias) =>
        GitHubRepo.AliasOf(remote).ShouldBe(alias);

    [Fact]
    public void Owner_and_name_compare_ignoring_case()
    {
        new GitHubRepo("Acme", "Tool").ShouldBe(new GitHubRepo("acme", "TOOL"));
        new GitHubRepo("Acme", "Tool").GetHashCode().ShouldBe(new GitHubRepo("acme", "TOOL").GetHashCode());
        new GitHubRepo("acme", "tool").ShouldNotBe(new GitHubRepo("acme", "other"));
    }
}
