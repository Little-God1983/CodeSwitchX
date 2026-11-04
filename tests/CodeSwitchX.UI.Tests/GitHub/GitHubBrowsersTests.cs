using System.Diagnostics;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Hosting.Browsers;
using CodeSwitchX.UI.GitHub;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.GitHub;

public class GitHubBrowsersTests
{
    private const string Url = "https://github.com/someone/something/issues";

    private readonly ISettingsStore _store = Substitute.For<ISettingsStore>();
    private readonly List<ProcessStartInfo> _started = [];
    private Func<ProcessStartInfo, bool> _start;

    public GitHubBrowsersTests()
    {
        _start = info => { _started.Add(info); return true; };
    }

    private GitHubBrowsers Create() => new(
        new BrowserCatalog(new FakeSource()), _store, info => _start(info), NullLogger<GitHubBrowsers>.Instance);

    /// <summary>What was logged, with the exception handed to the logger: its message could carry a login or a browser's profile.</summary>
    private sealed class ExceptionLogger : ILogger<GitHubBrowsers>
    {
        private readonly TaskCompletionSource<(string Text, Exception? Exception)> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<(string Text, Exception? Exception)> First => _first.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _first.TrySetResult((formatter(state, exception), exception));
    }

    [Fact]
    public async Task Choices_that_cannot_be_read_are_logged_without_the_exception_s_message()
    {
        var log = new ExceptionLogger();
        _store.GetAsync<Dictionary<string, string>>(SettingKeys.GitHubBrowsers, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Dictionary<string, string>?>(new InvalidOperationException("private-login")));
        var browsers = new GitHubBrowsers(new BrowserCatalog(new FakeSource()), _store, info => _start(info), log);

        await browsers.LoadAsync(TestContext.Current.CancellationToken);

        var (text, exception) = await log.First;
        exception.ShouldBeNull();
        text.ShouldContain(nameof(InvalidOperationException));
        text.ShouldNotContain("private-login");
    }

    [Fact]
    public async Task Choices_that_cannot_be_saved_are_logged_without_the_exception_s_message()
    {
        var log = new ExceptionLogger();
        _store.SetAsync(SettingKeys.GitHubBrowsers, Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("private-login")));
        var browsers = new GitHubBrowsers(new BrowserCatalog(new FakeSource()), _store, info => _start(info), log);

        browsers.SetChoice("someone", "firefox|alpha");

        var (text, exception) = await log.First.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        exception.ShouldBeNull();
        text.ShouldContain(nameof(InvalidOperationException));
        text.ShouldNotContain("private-login");
    }

    [Fact]
    public void An_account_without_a_choice_uses_the_Windows_default()
    {
        Create().Open("someone", Url).ShouldBeNull();

        var info = _started.ShouldHaveSingleItem();
        info.UseShellExecute.ShouldBeTrue();
        info.FileName.ShouldBe(Url);
    }

    [Fact]
    public void A_chosen_browser_is_started_with_its_arguments()
    {
        var browsers = Create();
        browsers.SetChoice("someone", "firefox|name with space");

        browsers.Open("someone", Url).ShouldBeNull();

        var info = _started.ShouldHaveSingleItem();
        info.UseShellExecute.ShouldBeFalse();
        info.FileName.ShouldBe(@"C:\ff\firefox.exe");
        info.ArgumentList.ShouldBe(["-P", "name with space", "-new-tab", Url]);
        browsers.NameFor("someone").ShouldBe("Firefox (name with space)");
    }

    [Fact]
    public void A_gone_browser_says_so_and_opens_nothing()
    {
        var browsers = Create();
        browsers.SetChoice("someone", "chrome|Default");

        browsers.Open("someone", Url).ShouldBe("Couldn't find chrome|Default. Pick a browser in Settings, GitHub.");
        _started.ShouldBeEmpty();
    }

    [Fact]
    public void A_browser_that_fails_to_start_says_so()
    {
        var browsers = Create();
        browsers.SetChoice("someone", "firefox|alpha");
        _start = _ => throw new InvalidOperationException("gone");

        browsers.Open("someone", Url).ShouldBe("Couldn't open Firefox (alpha). Pick another browser in Settings, GitHub.");
    }

    [Theory]
    [InlineData("http://github.com/x")]
    [InlineData("https://github.com.evil.example/x")]
    [InlineData("https://example.com")]
    [InlineData("file:///c:/x")]
    [InlineData("not an address")]
    public void Only_github_addresses_are_opened(string url)
    {
        Create().Open("someone", url).ShouldNotBeNull();

        _started.ShouldBeEmpty();
    }

    [Fact]
    public void A_choice_is_saved_under_the_login()
    {
        var browsers = Create();

        browsers.SetChoice("someone", "firefox|alpha");

        browsers.ChoiceFor("someone").ShouldBe("firefox|alpha");
        browsers.ChoiceFor("another").ShouldBe(BrowserEntry.DefaultId);
        _store.Received().SetAsync(SettingKeys.GitHubBrowsers,
            Arg.Is<Dictionary<string, string>>(d => d.Count == 1 && d["someone"] == "firefox|alpha"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Saved_choices_are_loaded()
    {
        _store.GetAsync<Dictionary<string, string>>(SettingKeys.GitHubBrowsers, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["someone"] = "firefox|alpha" });
        var browsers = Create();

        await browsers.LoadAsync(TestContext.Current.CancellationToken);

        browsers.ChoiceFor("someone").ShouldBe("firefox|alpha");
    }

    [Fact]
    public void A_chosen_browser_without_an_executable_never_falls_back_to_the_default()
    {
        var browsers = new GitHubBrowsers(new BrowserCatalog(new FakeSource { Keys = [("Firefox-1", null)] }), _store,
            info => _start(info), NullLogger<GitHubBrowsers>.Instance);
        browsers.SetChoice("someone", "firefox|alpha");

        browsers.Open("someone", Url).ShouldBe("Couldn't open Firefox (alpha). Pick another browser in Settings, GitHub.");
        _started.ShouldBeEmpty();
    }

    [Fact]
    public void A_start_that_returns_false_for_a_chosen_browser_says_so()
    {
        var browsers = Create();
        browsers.SetChoice("someone", "firefox|alpha");
        _start = _ => false;

        browsers.Open("someone", Url).ShouldBe("Couldn't open Firefox (alpha). Pick another browser in Settings, GitHub.");
    }

    [Fact]
    public void A_shell_start_that_returns_no_process_is_still_an_open()
    {
        _start = _ => false; // Process.Start gives null when a running process took the address

        Create().Open("someone", Url).ShouldBeNull();
    }

    [Fact]
    public async Task Choices_that_cannot_be_read_leave_every_account_on_the_Windows_default()
    {
        _store.GetAsync<Dictionary<string, string>>(SettingKeys.GitHubBrowsers, Arg.Any<CancellationToken>())
            .Returns<Task<Dictionary<string, string>?>>(_ => throw new InvalidOperationException("broken"));
        var browsers = Create();

        await browsers.LoadAsync(TestContext.Current.CancellationToken);

        browsers.ChoiceFor("someone").ShouldBe(BrowserEntry.DefaultId);
        browsers.Open("someone", Url).ShouldBeNull();
        _started.ShouldHaveSingleItem().UseShellExecute.ShouldBeTrue();
    }

    [Fact]
    public async Task A_choice_whose_save_fails_is_still_used()
    {
        _store.SetAsync(SettingKeys.GitHubBrowsers, Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new IOException("disk full")));
        var browsers = Create();

        browsers.SetChoice("someone", "firefox|alpha");
        await Task.Delay(50, TestContext.Current.CancellationToken); // the save is not awaited by the caller

        browsers.ChoiceFor("someone").ShouldBe("firefox|alpha");
        browsers.Open("someone", Url).ShouldBeNull();
        _started.ShouldHaveSingleItem().FileName.ShouldBe(@"C:\ff\firefox.exe");
        await _store.Received(1).SetAsync(SettingKeys.GitHubBrowsers, Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_save_that_throws_at_once_is_not_thrown_to_the_caller()
    {
        _store.SetAsync(SettingKeys.GitHubBrowsers, Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("disk full"));
        var browsers = Create();

        Should.NotThrow(() => browsers.SetChoice("someone", "firefox|alpha"));

        browsers.ChoiceFor("someone").ShouldBe("firefox|alpha");
    }

    [Fact]
    public void A_browser_that_moved_since_the_list_was_read_is_found_on_the_second_look()
    {
        var source = new FakeSource();
        var browsers = new GitHubBrowsers(new BrowserCatalog(source), _store, info => _start(info), NullLogger<GitHubBrowsers>.Instance);
        browsers.SetChoice("someone", "firefox|alpha");
        browsers.Open("someone", Url).ShouldBeNull(); // the list is read and kept
        source.Keys = [("Firefox-1A2B", @"C:\moved\firefox.exe")];
        _start = info => info.FileName == @"C:\moved\firefox.exe" ? true : throw new FileNotFoundException("gone");
        _started.Clear();

        browsers.Open("someone", Url).ShouldBeNull();
    }

    [Fact]
    public void A_browser_installed_since_the_list_was_read_is_found_on_the_second_look()
    {
        var source = new FakeSource();
        var browsers = new GitHubBrowsers(new BrowserCatalog(source), _store, info => _start(info), NullLogger<GitHubBrowsers>.Instance);
        browsers.SetChoice("someone", "brave");
        browsers.Open("someone", Url).ShouldBe("Couldn't find brave. Pick a browser in Settings, GitHub.");
        source.Keys = [("Firefox-1A2B", @"C:\ff\firefox.exe"), ("Brave", @"C:\b\brave.exe")];
        _started.Clear();

        browsers.Open("someone", Url).ShouldBeNull();

        _started.ShouldHaveSingleItem().FileName.ShouldBe(@"C:\b\brave.exe");
    }

    [Fact]
    public async Task Saves_run_one_after_another_and_the_last_one_holds_every_pick()
    {
        var first = new TaskCompletionSource();
        var saved = new List<Dictionary<string, string>>();
        _store.SetAsync(SettingKeys.GitHubBrowsers, Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (saved)
                {
                    saved.Add(new Dictionary<string, string>(call.Arg<Dictionary<string, string>>()));
                    return saved.Count == 1 ? first.Task : Task.CompletedTask;
                }
            });
        var browsers = Create();

        browsers.SetChoice("someone", "firefox|alpha");
        browsers.SetChoice("another", "firefox|beta");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        lock (saved)
        {
            saved.Count.ShouldBe(1, "the second save waits for the first");
        }

        first.SetResult();
        SpinWait.SpinUntil(() => { lock (saved) { return saved.Count == 2; } }, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        lock (saved)
        {
            saved[^1].ShouldBe(new Dictionary<string, string> { ["someone"] = "firefox|alpha", ["another"] = "firefox|beta" });
        }
    }

    [Fact]
    public void A_list_that_cannot_be_read_gives_a_line_not_an_exception()
    {
        var source = new FakeSource { Throws = true };
        var browsers = new GitHubBrowsers(new BrowserCatalog(source), _store, info => _start(info), NullLogger<GitHubBrowsers>.Instance);
        browsers.SetChoice("someone", "firefox|alpha");

        browsers.NameFor("someone").ShouldBe("firefox|alpha");
        browsers.Open("someone", Url).ShouldBe("Couldn't find firefox|alpha. Pick a browser in Settings, GitHub.");
    }

    [Fact]
    public void A_failed_start_of_the_Windows_default_is_not_retried_with_a_new_read()
    {
        var source = new FakeSource();
        var browsers = new GitHubBrowsers(new BrowserCatalog(source), _store, info => _start(info), NullLogger<GitHubBrowsers>.Instance);
        browsers.Open("someone", Url).ShouldBeNull();
        var reads = source.Reads;
        _start = _ => throw new InvalidOperationException("no shell");

        browsers.Open("someone", Url).ShouldNotBeNull();

        source.Reads.ShouldBe(reads);
    }

    [Fact]
    public async Task A_save_does_not_start_on_the_thread_of_the_pick()
    {
        int pickThread = 0, saveThread = 0;
        var saved = new ManualResetEventSlim();
        _store.SetAsync(SettingKeys.GitHubBrowsers, Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => { saveThread = Environment.CurrentManagedThreadId; saved.Set(); return Task.CompletedTask; });
        var browsers = Create();

        await StaThread.RunAsync(() =>
        {
            pickThread = Environment.CurrentManagedThreadId;
            browsers.SetChoice("someone", "firefox|alpha");
            saved.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ShouldBeTrue();
        });

        saveThread.ShouldNotBe(pickThread);
    }

    private sealed class FakeSource : IBrowserSource
    {
        public IReadOnlyList<(string Key, string? Executable)> Keys { get; set; } = [("Firefox-1A2B", @"C:\ff\firefox.exe")];

        public bool Throws { get; set; }

        public int Reads { get; private set; }

        public IReadOnlyList<(string Key, string? Executable)> Installed()
        {
            Reads++;
            return Throws ? throw new InvalidOperationException("boom") : Keys;
        }
        public string? FirefoxProfilesIni() => "[Profile0]\nName=alpha\n[Profile1]\nName=name with space\n";
        public string? ChromiumLocalState(string folder) => null;
    }
}
