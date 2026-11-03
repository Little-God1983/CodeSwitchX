using System.Diagnostics;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Hosting.Browsers;
using CodeSwitchX.UI.GitHub;
using CodeSwitchX.UI.Settings;
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

    private sealed class FakeSource : IBrowserSource
    {
        public IReadOnlyList<(string Key, string? Executable)> Keys { get; init; } = [("Firefox-1A2B", @"C:\ff\firefox.exe")];

        public IReadOnlyList<(string Key, string? Executable)> Installed() => Keys;
        public string? FirefoxProfilesIni() => "[Profile0]\nName=alpha\n[Profile1]\nName=name with space\n";
        public string? EdgeLocalState() => null;
        public string? ChromeLocalState() => null;
    }
}
