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

    [Fact]
    public void Owner_and_name_compare_ignoring_case()
    {
        new GitHubRepo("Acme", "Tool").ShouldBe(new GitHubRepo("acme", "TOOL"));
        new GitHubRepo("Acme", "Tool").GetHashCode().ShouldBe(new GitHubRepo("acme", "TOOL").GetHashCode());
        new GitHubRepo("acme", "tool").ShouldNotBe(new GitHubRepo("acme", "other"));
    }
}
