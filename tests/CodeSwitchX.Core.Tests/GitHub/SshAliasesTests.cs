using CodeSwitchX.Core.GitHub;

namespace CodeSwitchX.Core.Tests.GitHub;

public class SshAliasesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-ssh-" + Guid.NewGuid().ToString("N"));

    public SshAliasesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>An ssh that notes its arguments in calls.txt, then runs <paramref name="body"/>.</summary>
    private async Task<string> Ssh(string body)
    {
        var script = Path.Combine(_root, "ssh.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\necho %*>>\"" + Path.Combine(_root, "calls.txt") + "\"\r\n" + body + "\r\n", TestContext.Current.CancellationToken);
        return script;
    }

    private string[] Calls => File.Exists(Path.Combine(_root, "calls.txt")) ? File.ReadAllLines(Path.Combine(_root, "calls.txt")) : [];

    [Fact]
    public async Task The_hostname_line_of_ssh_G_is_the_host()
    {
        var ssh = new SshAliases(await Ssh("echo user git\r\necho hostname ssh.github.com\r\necho port 443"), TimeSpan.FromSeconds(10));

        (await ssh.HostOfAsync("github-work", CancellationToken.None)).ShouldBe("ssh.github.com");
        Calls.ShouldBe(["-G -- github-work"]);
    }

    [Fact]
    public async Task An_alias_is_asked_once_however_it_is_written()
    {
        var ssh = new SshAliases(await Ssh("echo hostname github.com"), TimeSpan.FromSeconds(10));

        (await ssh.HostOfAsync("github-work", CancellationToken.None)).ShouldBe("github.com");
        (await ssh.HostOfAsync("GitHub-Work", CancellationToken.None)).ShouldBe("github.com");
        (await ssh.HostOfAsync("other", CancellationToken.None)).ShouldBe("github.com");

        Calls.Length.ShouldBe(2);
    }

    [Fact]
    public async Task A_failed_ask_is_not_repeated_this_run()
    {
        var ssh = new SshAliases(await Ssh("exit /b 255"), TimeSpan.FromSeconds(10));

        (await ssh.HostOfAsync("github-work", CancellationToken.None)).ShouldBeNull();
        (await ssh.HostOfAsync("github-work", CancellationToken.None)).ShouldBeNull();

        Calls.Length.ShouldBe(1);
    }

    [Fact]
    public async Task No_ssh_is_no_answer_and_no_error() =>
        (await new SshAliases(Path.Combine(_root, "no-such-ssh.exe"), TimeSpan.FromSeconds(10)).HostOfAsync("github-work", CancellationToken.None)).ShouldBeNull();

    [Fact]
    public async Task Output_without_a_hostname_line_is_no_answer()
    {
        var ssh = new SshAliases(await Ssh("echo user git"), TimeSpan.FromSeconds(10));

        (await ssh.HostOfAsync("github-work", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_slow_ssh_is_cut_off_at_the_timeout()
    {
        var ssh = new SshAliases(await Ssh("ping -n 6 127.0.0.1 >nul\r\necho hostname github.com"), TimeSpan.FromSeconds(1));

        (await ssh.HostOfAsync("github-work", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_caller_that_stops_waiting_does_not_spoil_the_answer_for_the_next()
    {
        var ssh = new SshAliases(await Ssh("ping -n 2 127.0.0.1 >nul\r\necho hostname github.com"), TimeSpan.FromSeconds(10));
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(ssh.HostOfAsync("github-work", stop.Token));

        (await ssh.HostOfAsync("github-work", CancellationToken.None)).ShouldBe("github.com");
    }
}
