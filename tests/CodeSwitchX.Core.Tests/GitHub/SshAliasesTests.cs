using CodeSwitchX.Core.GitHub;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.Tests.GitHub;

public class SshAliasesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-ssh-" + Guid.NewGuid().ToString("N"));

    private DateTimeOffset _now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    public SshAliasesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Lines : ILogger<SshAliases>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Warnings)
            {
                Warnings.Add(logLevel + ": " + formatter(state, exception));
            }
        }
    }

    /// <summary>An ssh that notes its arguments in calls.txt, then runs <paramref name="body"/>.</summary>
    private async Task<string> Ssh(string body)
    {
        var script = Path.Combine(_root, "ssh.cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\necho %*>>\"" + Path.Combine(_root, "calls.txt") + "\"\r\n" + body + "\r\n", TestContext.Current.CancellationToken);
        return script;
    }

    private SshAliases Aliases(string executable, TimeSpan? timeout = null, ILogger<SshAliases>? logger = null) =>
        new(executable, timeout ?? TimeSpan.FromSeconds(10), () => _now, logger);

    private string[] Calls => File.Exists(Path.Combine(_root, "calls.txt")) ? File.ReadAllLines(Path.Combine(_root, "calls.txt")) : [];

    [Fact]
    public async Task The_hostname_line_of_ssh_G_is_the_host()
    {
        var ssh = Aliases(await Ssh("echo user git\r\necho hostname ssh.github.com\r\necho port 443"));

        (await ssh.HostOfAsync("github-work", CancellationToken.None)).ShouldBe(new SshAnswer("ssh.github.com"));
        Calls.ShouldBe(["-G -- github-work"]);
    }

    [Fact]
    public async Task An_alias_is_matched_as_written_and_asked_once_within_ten_minutes()
    {
        var ssh = Aliases(await Ssh("echo hostname github.com"));

        (await ssh.HostOfAsync("github-work", CancellationToken.None)).Host.ShouldBe("github.com");
        (await ssh.HostOfAsync("github-work", CancellationToken.None)).Host.ShouldBe("github.com");
        (await ssh.HostOfAsync("GitHub-Work", CancellationToken.None)).Host.ShouldBe("github.com");

        Calls.ShouldBe(["-G -- github-work", "-G -- GitHub-Work"]);
    }

    [Fact]
    public async Task An_answer_is_asked_again_after_ten_minutes()
    {
        var ssh = Aliases(await Ssh("echo hostname github.com"));
        await ssh.HostOfAsync("github-work", CancellationToken.None);
        _now += SshAliases.KeepFor - TimeSpan.FromSeconds(1);
        await ssh.HostOfAsync("github-work", CancellationToken.None);
        Calls.Length.ShouldBe(1);

        _now += TimeSpan.FromSeconds(2);
        await ssh.HostOfAsync("github-work", CancellationToken.None);

        Calls.Length.ShouldBe(2);
    }

    [Fact]
    public async Task A_failed_ask_is_not_kept_and_is_asked_again()
    {
        var ssh = Aliases(await Ssh("exit /b 255"));

        (await ssh.HostOfAsync("github-work", CancellationToken.None)).ShouldBe(SshAnswer.Unknown);
        (await ssh.HostOfAsync("github-work", CancellationToken.None)).ShouldBe(SshAnswer.Unknown);

        Calls.Length.ShouldBe(2);
    }

    [Fact]
    public async Task No_ssh_is_an_answer_without_a_host_and_no_error()
    {
        var ssh = Aliases(Path.Combine(_root, "no-such-ssh.exe"));

        (await ssh.HostOfAsync("github-work", CancellationToken.None)).ShouldBe(SshAnswer.NoSsh);
    }

    [Fact]
    public async Task Output_without_a_hostname_line_is_a_failed_ask()
    {
        var ssh = Aliases(await Ssh("echo user git"));

        (await ssh.HostOfAsync("github-work", CancellationToken.None)).ShouldBe(SshAnswer.Unknown);
    }

    [Fact]
    public async Task A_slow_ssh_is_cut_off_at_the_timeout_and_is_a_failed_ask()
    {
        var ssh = Aliases(await Ssh("ping -n 6 127.0.0.1 >nul\r\necho hostname github.com"), TimeSpan.FromSeconds(1));

        (await ssh.HostOfAsync("github-work", CancellationToken.None)).ShouldBe(SshAnswer.Unknown);
    }

    [Fact]
    public async Task A_caller_that_stops_waiting_does_not_spoil_the_answer_for_the_next()
    {
        var ssh = Aliases(await Ssh("ping -n 2 127.0.0.1 >nul\r\necho hostname github.com"));
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(ssh.HostOfAsync("github-work", stop.Token));

        (await ssh.HostOfAsync("github-work", CancellationToken.None)).Host.ShouldBe("github.com");
    }

    [Fact]
    public async Task Each_kind_of_failure_is_logged_once_per_run_without_the_alias()
    {
        var log = new Lines();
        var failing = Aliases(await Ssh("exit /b 255"), logger: log);
        await failing.HostOfAsync("github-work", CancellationToken.None);
        await failing.HostOfAsync("github-work", CancellationToken.None);
        await failing.HostOfAsync("second-alias", CancellationToken.None);
        var missing = Aliases(Path.Combine(_root, "no-such-ssh.exe"), logger: log);
        await missing.HostOfAsync("github-work", CancellationToken.None);

        log.Warnings.Count.ShouldBe(2);
        log.Warnings[0].ShouldContain("exit code 255");
        log.Warnings[1].ShouldContain("ssh not found");
        log.Warnings.ShouldAllBe(w => !w.Contains("github-work") && !w.Contains("second-alias"));
    }
}
