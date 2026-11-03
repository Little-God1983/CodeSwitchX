using CodeSwitchX.Hosting.Browsers;

namespace CodeSwitchX.Hosting.Tests.Browsers;

public class BrowserCatalogTests
{
    private const string Ini = "[Profile0]\nName=alpha\n[Profile1]\nName=name with space\n";
    private const string EdgeOne = """{"profile":{"info_cache":{"Default":{"name":"Person 1"}}}}""";
    private const string ChromeTwo = """{"profile":{"info_cache":{"Default":{"name":"One"},"Profile 1":{"name":"Two"}}}}""";

    [Fact]
    public void The_first_entry_is_the_Windows_default()
    {
        var list = new BrowserCatalog(new FakeSource()).List();

        var first = list.ShouldHaveSingleItem();
        first.Id.ShouldBe(BrowserEntry.DefaultId);
        first.Label.ShouldBe("Windows default browser");
        first.Executable.ShouldBeNull();
    }

    [Fact]
    public void Internet_Explorer_is_skipped_and_a_key_seen_twice_gives_one_browser()
    {
        var source = new FakeSource
        {
            Keys = [("IEXPLORE.EXE", @"C:\ie\iexplore.exe"), ("Firefox-1A2B", @"C:\ff\firefox.exe"), ("Firefox-1A2B", @"C:\ff\firefox.exe")],
        };

        var list = new BrowserCatalog(source).List();

        list.Select(e => e.Id).ShouldBe([BrowserEntry.DefaultId, "firefox"]);
    }

    [Fact]
    public void One_profile_gives_a_plain_label_and_an_id_with_the_profile()
    {
        var source = new FakeSource { Keys = [("Microsoft Edge", @"C:\edge\msedge.exe")], Edge = EdgeOne };

        var edge = new BrowserCatalog(source).List()[1];

        edge.Id.ShouldBe("edge|Default");
        edge.Label.ShouldBe("Microsoft Edge");
        edge.Executable.ShouldBe(@"C:\edge\msedge.exe");
        edge.ProfileArguments.ShouldBe(["--profile-directory=Default"]);
    }

    [Fact]
    public void Several_profiles_give_one_entry_each_labelled_with_the_profile()
    {
        var source = new FakeSource { Keys = [("Firefox-1A2B", @"C:\ff\firefox.exe"), ("Google Chrome", @"C:\c\chrome.exe")], Ini = Ini, Chrome = ChromeTwo };

        var list = new BrowserCatalog(source).List();

        list.Skip(1).Select(e => (e.Id, e.Label)).ShouldBe(
        [
            ("firefox|alpha", "Firefox (alpha)"),
            ("firefox|name with space", "Firefox (name with space)"),
            ("chrome|Default", "Google Chrome (One)"),
            ("chrome|Profile 1", "Google Chrome (Two)"),
        ]);
    }

    [Fact]
    public void A_browser_with_an_unknown_key_gets_one_entry_without_profile_arguments()
    {
        var source = new FakeSource { Keys = [("Some Browser", @"C:\sb\sb.exe")] };

        var entry = new BrowserCatalog(source).List()[1];

        entry.Label.ShouldBe("Some Browser");
        entry.ProfileArguments.ShouldBeEmpty();
        entry.Arguments("https://github.com/x").ShouldBe(["https://github.com/x"]);
    }

    [Fact]
    public void Firefox_arguments_keep_each_value_as_one_entry()
    {
        var entry = new BrowserEntry("firefox|name with space", "Firefox", @"C:\ff\firefox.exe", ["-P", "name with space"]);

        entry.Arguments("https://github.com/x").ShouldBe(["-P", "name with space", "-new-tab", "https://github.com/x"]);
    }

    [Fact]
    public void Edge_arguments_name_the_profile_directory()
    {
        var entry = new BrowserEntry("edge|Profile 1", "Microsoft Edge", @"C:\edge\msedge.exe", ["--profile-directory=Profile 1"]);

        entry.Arguments("https://github.com/x").ShouldBe(["--profile-directory=Profile 1", "https://github.com/x"]);
    }

    private sealed class FakeSource : IBrowserSource
    {
        public IReadOnlyList<(string Key, string? Executable)> Keys { get; init; } = [];
        public string? Ini { get; init; }
        public string? Edge { get; init; }
        public string? Chrome { get; init; }

        public IReadOnlyList<(string Key, string? Executable)> Installed() => Keys;
        public string? FirefoxProfilesIni() => Ini;
        public string? EdgeLocalState() => Edge;
        public string? ChromeLocalState() => Chrome;
    }
}
