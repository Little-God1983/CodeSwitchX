using System.Diagnostics;
using CodeSwitchX.Core.GitHub;

namespace CodeSwitchX.Core.Tests.GitHub;

public class GhCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-gh-" + Guid.NewGuid().ToString("N"));

    public GhCliTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Token_goes_in_the_environment_only()
    {
        var info = GhCli.StartInfo(["api", "user"], "secret-token");

        info.Environment["GH_TOKEN"].ShouldBe("secret-token");
        info.ArgumentList.ShouldBe(["api", "user"]);
    }

    [Fact]
    public void An_inherited_token_never_reaches_a_lookup()
    {
        var info = GhCli.StartInfo(["auth", "token", "--user", "acme"], null);

        info.Environment.ContainsKey("GH_TOKEN").ShouldBeFalse();
        info.Environment.ContainsKey("GITHUB_TOKEN").ShouldBeFalse();
    }

    [Fact]
    public void Arguments_are_passed_as_a_list()
    {
        var argument = "query=a \"quoted\" value";

        var info = GhCli.StartInfo(["api", "graphql", "-f", argument], null);

        info.ArgumentList.Count.ShouldBe(4);
        info.ArgumentList[3].ShouldBe(argument);
    }

    [Fact]
    public async Task A_hanging_gh_is_killed_at_the_timeout()
    {
        var script = Path.Combine(_root, "gh.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\nping -n 30 127.0.0.1 >nul\r\n", TestContext.Current.CancellationToken);
        var gh = new GhCli(script, TimeSpan.FromSeconds(1));

        var watch = Stopwatch.StartNew();
        var result = await gh.RunAsAsync("acme", ["api", "user"], CancellationToken.None);

        result.ShouldBeNull();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_missing_gh_gives_no_accounts()
    {
        var gh = new GhCli(Path.Combine(_root, "no-such-gh.exe"), GhCli.Timeout);

        (await gh.AccountsAsync(CancellationToken.None)).ShouldBeEmpty();
    }
}
