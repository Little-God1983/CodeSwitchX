using System.Diagnostics;
using System.Text;
using CodeSwitchX.Core.GitHub;

namespace CodeSwitchX.Core.Tests.GitHub;

[Collection("ProcessEnvironment")]
public class GhCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-gh-" + Guid.NewGuid().ToString("N"));

    public GhCliTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Token_goes_in_the_environment_only()
    {
        WithInheritedEnvironment(() =>
        {
            var info = GhCli.StartInfo(["api", "user"], "secret-token");

            info.Environment["GH_TOKEN"].ShouldBe("secret-token");
            info.Environment.ContainsKey("GITHUB_TOKEN").ShouldBeFalse();
            info.Environment.ContainsKey("GH_HOST").ShouldBeFalse();
            info.ArgumentList.ShouldBe(["api", "user"]);
        });
    }

    [Fact]
    public void An_inherited_token_never_reaches_a_lookup()
    {
        WithInheritedEnvironment(() =>
        {
            var info = GhCli.StartInfo(["auth", "token", "--user", "acme"], null);

            info.Environment.ContainsKey("GH_TOKEN").ShouldBeFalse();
            info.Environment.ContainsKey("GITHUB_TOKEN").ShouldBeFalse();
            info.Environment.ContainsKey("GH_HOST").ShouldBeFalse();
        });
    }

    [Fact]
    public void Output_is_read_as_UTF8()
    {
        GhCli.StartInfo(["api", "user"], null).StandardOutputEncoding.ShouldBe(Encoding.UTF8);
    }

    [Fact]
    public async Task Non_ASCII_output_survives_the_pipe()
    {
        var payload = Path.Combine(_root, "payload.json");
        var json = "[{\"title\":\"Café 😜\"}]";
        await File.WriteAllBytesAsync(payload, new UTF8Encoding(false).GetBytes(json), TestContext.Current.CancellationToken);
        var script = Path.Combine(_root, "gh.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\ntype \"" + payload + "\"\r\n", TestContext.Current.CancellationToken);
        var gh = new GhCli(script, GhCli.Timeout);

        (await gh.RunAsAsync("acme", ["api", "user"], TestContext.Current.CancellationToken)).ShouldBe(json);
    }

    private static void WithInheritedEnvironment(Action action)
    {
        var saved = (Environment.GetEnvironmentVariable("GH_TOKEN"), Environment.GetEnvironmentVariable("GITHUB_TOKEN"), Environment.GetEnvironmentVariable("GH_HOST"));
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", "inherited-marker");
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", "inherited-marker");
            Environment.SetEnvironmentVariable("GH_HOST", "inherited-marker");
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", saved.Item1);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", saved.Item2);
            Environment.SetEnvironmentVariable("GH_HOST", saved.Item3);
        }
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
    public async Task Accounts_are_every_login_on_github_com_whatever_its_state()
    {
        var payload = Path.Combine(_root, "status.json");
        await File.WriteAllTextAsync(payload, """
            {"hosts":{
             "github.com":[{"state":"success","active":true,"host":"github.com","login":"amy"},
                           {"state":"timeout","active":false,"host":"github.com","login":"zed"},
                           {"state":"error","active":false,"host":"github.com","login":"kim"},
                           {"active":false,"host":"github.com","login":"lou"}],
             "ghe.example.com":[{"state":"success","active":true,"host":"ghe.example.com","login":"elsewhere"}]}}
            """, TestContext.Current.CancellationToken);
        var script = Path.Combine(_root, "gh.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\ntype \"" + payload + "\"\r\n", TestContext.Current.CancellationToken);

        var accounts = await new GhCli(script, GhCli.Timeout).AccountsAsync(TestContext.Current.CancellationToken);

        accounts.ShouldBe(["amy", "zed", "kim", "lou"]);
    }

    [Fact]
    public async Task A_missing_gh_gives_no_accounts()
    {
        var gh = new GhCli(Path.Combine(_root, "no-such-gh.exe"), GhCli.Timeout);

        (await gh.AccountsAsync(CancellationToken.None)).ShouldBeEmpty();
    }
}
