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

    /// <summary>A gh that gives a token and then prints the payload and exits with that code.</summary>
    private async Task<string> FakeGh(string payload, int exitCode)
    {
        var file = Path.Combine(_root, "answer.json");
        await File.WriteAllTextAsync(file, payload, TestContext.Current.CancellationToken);
        var script = Path.Combine(_root, "gh.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\nif \"%1\"==\"auth\" (echo a-token& exit /b 0)\r\ntype \"" + file + "\"\r\nexit /b " + exitCode + "\r\n", TestContext.Current.CancellationToken);
        return script;
    }

    [Fact]
    public async Task A_run_that_exits_with_an_error_hands_back_what_it_printed_but_not_as_its_output()
    {
        const string payload = """{"data":{"repository":null},"errors":[{"type":"NOT_FOUND"}]}""";
        var gh = new GhCli(await FakeGh(payload, 1), GhCli.Timeout);

        var answer = await gh.AnswerAsAsync("acme", ["api", "graphql"], TestContext.Current.CancellationToken);

        answer.Output.ShouldBeNull();
        answer.FailureOutput.ShouldBe(payload);
        (await gh.RunAsAsync("acme", ["api", "graphql"], TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task A_run_that_succeeds_has_output_and_no_failure()
    {
        var gh = new GhCli(await FakeGh("{}", 0), GhCli.Timeout);

        var answer = await gh.AnswerAsAsync("acme", ["api", "graphql"], TestContext.Current.CancellationToken);

        answer.ShouldBe(new GhAnswer("{}"));
    }

    [Fact]
    public async Task An_account_without_a_token_prints_nothing_to_go_on()
    {
        var payload = Path.Combine(_root, "answer.json");
        await File.WriteAllTextAsync(payload, """{"data":{"repository":null},"errors":[{"type":"NOT_FOUND"}]}""", TestContext.Current.CancellationToken);
        var script = Path.Combine(_root, "gh.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\ntype \"" + payload + "\"\r\nexit /b 1\r\n", TestContext.Current.CancellationToken);

        var answer = await new GhCli(script, GhCli.Timeout).AnswerAsAsync("acme", ["api", "graphql"], TestContext.Current.CancellationToken);

        answer.ShouldBe(new GhAnswer(null));
    }

    [Fact]
    public async Task A_run_that_times_out_prints_nothing_to_go_on()
    {
        var script = Path.Combine(_root, "gh.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\nif \"%1\"==\"auth\" (echo a-token& exit /b 0)\r\necho partial\r\nping -n 30 127.0.0.1 >nul\r\nexit /b 1\r\n", TestContext.Current.CancellationToken);

        var answer = await new GhCli(script, TimeSpan.FromSeconds(1)).AnswerAsAsync("acme", ["api", "graphql"], CancellationToken.None);

        answer.ShouldBe(new GhAnswer(null, TimedOut: true));
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

    /// <summary>A gh that gives a token, takes about three seconds and then prints "done".</summary>
    private async Task<string> SlowGh()
    {
        var script = Path.Combine(_root, "slow.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\nif \"%1\"==\"auth\" (echo a-token& exit /b 0)\r\nping -n 4 127.0.0.1 >nul\r\necho done\r\n", TestContext.Current.CancellationToken);
        return script;
    }

    [Fact]
    public async Task A_list_read_outlasts_the_usual_timeout()
    {
        var gh = new GhCli(await SlowGh(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        var answer = await gh.ListAsAsync("acme", ["issue", "list"], CancellationToken.None);

        answer.Output.ShouldNotBeNull().Trim().ShouldBe("done");
        answer.TimedOut.ShouldBeFalse();
    }

    [Fact]
    public async Task A_list_read_has_its_own_timeout_and_says_it_ran_out_of_it()
    {
        var gh = new GhCli(await SlowGh(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

        (await gh.ListAsAsync("acme", ["issue", "list"], CancellationToken.None)).ShouldBe(new GhAnswer(null, TimedOut: true));
    }

    [Fact]
    public async Task A_single_read_is_killed_at_the_usual_timeout_whatever_the_list_timeout_is()
    {
        var gh = new GhCli(await SlowGh(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        (await gh.RunAsAsync("acme", ["issue", "list"], CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task The_token_lookup_keeps_the_usual_timeout_whatever_the_list_timeout_is()
    {
        var script = Path.Combine(_root, "slowtoken.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\nif \"%1\"==\"auth\" (ping -n 4 127.0.0.1 >nul& echo a-token& exit /b 0)\r\necho done\r\n", TestContext.Current.CancellationToken);
        var gh = new GhCli(script, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        (await gh.ListAsAsync("acme", ["issue", "list"], CancellationToken.None)).Output.ShouldBeNull();
    }

    [Theory]
    [InlineData("""[1,2]""", new string[0])]
    [InlineData("""{"hosts":[]}""", new string[0])]
    [InlineData("""{"hosts":{"github.com":{"login":"amy"}}}""", new string[0])]
    [InlineData("""{"hosts":{"github.com":[{"login":5},{"login":null},{"login":"amy"},"zed",null,{"state":"x"}]}}""", new[] { "amy" })]
    [InlineData("""{"hosts":{"github.com":[{"login":"amy"},{"login":"kim"}]}}""", new[] { "amy", "kim" })]
    public async Task Accounts_survive_an_unexpected_shape_and_give_what_they_can(string payload, string[] expected)
    {
        var file = Path.Combine(_root, "status.json");
        await File.WriteAllTextAsync(file, payload, TestContext.Current.CancellationToken);
        var script = Path.Combine(_root, "gh.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\ntype \"" + file + "\"\r\n", TestContext.Current.CancellationToken);

        (await new GhCli(script, GhCli.Timeout).AccountsAsync(TestContext.Current.CancellationToken)).ShouldBe(expected);
    }

    [Fact]
    public async Task A_missing_gh_gives_no_accounts()
    {
        var gh = new GhCli(Path.Combine(_root, "no-such-gh.exe"), GhCli.Timeout);

        (await gh.AccountsAsync(CancellationToken.None)).ShouldBeEmpty();
    }
}
