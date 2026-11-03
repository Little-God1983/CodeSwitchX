using CodeSwitchX.Hosting.Browsers;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Hosting.Tests.Browsers;

public class BrowserCatalogTests
{
    private const string Ini = "[Profile0]\nName=alpha\n[Profile1]\nName=name with space\n";
    private const string EdgeFolder = @"Microsoft\Edge";
    private const string ChromeFolder = @"Google\Chrome";
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
        var source = new FakeSource { Keys = [("Microsoft Edge", @"C:\edge\msedge.exe")], LocalStates = { [EdgeFolder] = EdgeOne } };

        var edge = new BrowserCatalog(source).List()[1];

        edge.Id.ShouldBe("edge|Default");
        edge.Label.ShouldBe("Microsoft Edge");
        edge.Executable.ShouldBe(@"C:\edge\msedge.exe");
        edge.ProfileArguments.ShouldBe(["--profile-directory=Default"]);
    }

    [Fact]
    public void Several_profiles_give_one_entry_each_labelled_with_the_profile()
    {
        var source = new FakeSource { Keys = [("Firefox-1A2B", @"C:\ff\firefox.exe"), ("Google Chrome", @"C:\c\chrome.exe")], Ini = Ini, LocalStates = { [ChromeFolder] = ChromeTwo } };

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
        var entry = new BrowserEntry("firefox|name with space", "Firefox", @"C:\ff\firefox.exe", ["-P", "name with space"], IsFirefox: true);

        entry.Arguments("https://github.com/x").ShouldBe(["-P", "name with space", "-new-tab", "https://github.com/x"]);
    }

    [Fact]
    public void Edge_arguments_name_the_profile_directory()
    {
        var entry = new BrowserEntry("edge|Profile 1", "Microsoft Edge", @"C:\edge\msedge.exe", ["--profile-directory=Profile 1"]);

        entry.Arguments("https://github.com/x").ShouldBe(["--profile-directory=Profile 1", "https://github.com/x"]);
    }

    [Fact]
    public void Firefox_and_its_Developer_Edition_are_two_browsers_with_the_same_profiles()
    {
        var source = new FakeSource
        {
            Keys = [("Firefox-1A2B", @"C:\Program Files\Mozilla Firefox\firefox.exe"), ("Firefox-3C4D", @"C:\Program Files\Firefox Developer Edition\firefox.exe")],
            Ini = "[Profile0]\nName=alpha\n[Profile1]\nName=beta\n",
        };

        var list = new BrowserCatalog(source).List();

        list.Skip(1).Select(e => (e.Id, e.Label, e.Executable)).ShouldBe(
        [
            ("firefox|alpha", "Firefox (alpha)", @"C:\Program Files\Mozilla Firefox\firefox.exe"),
            ("firefox|beta", "Firefox (beta)", @"C:\Program Files\Mozilla Firefox\firefox.exe"),
            ("firefox-dev|alpha", "Firefox Developer Edition (alpha)", @"C:\Program Files\Firefox Developer Edition\firefox.exe"),
            ("firefox-dev|beta", "Firefox Developer Edition (beta)", @"C:\Program Files\Firefox Developer Edition\firefox.exe"),
        ]);
        list.Skip(1).ShouldAllBe(e => e.IsFirefox);
    }

    [Fact]
    public void Firefox_Nightly_is_told_by_its_key_or_its_folder()
    {
        var byKey = new FakeSource { Keys = [("Firefox Nightly", @"C:\n\firefox.exe")] };
        var byFolder = new FakeSource { Keys = [("Firefox-5E6F", @"C:\Program Files\Firefox Nightly\firefox.exe")] };

        new BrowserCatalog(byKey).List()[1].Id.ShouldBe("firefox-nightly");
        new BrowserCatalog(byFolder).List()[1].Id.ShouldBe("firefox-nightly");
    }

    [Fact]
    public void Edge_Beta_reads_its_own_profiles_and_keeps_Edge_ids_as_they_were()
    {
        var source = new FakeSource
        {
            Keys = [("Microsoft Edge", @"C:\edge\msedge.exe"), ("Microsoft Edge Beta", @"C:\edgeb\msedge.exe")],
            LocalStates = { [EdgeFolder] = EdgeOne, [@"Microsoft\Edge Beta"] = ChromeTwo },
        };

        var list = new BrowserCatalog(source).List();

        list.Skip(1).Select(e => (e.Id, e.Label, e.Executable)).ShouldBe(
        [
            ("edge|Default", "Microsoft Edge", @"C:\edge\msedge.exe"),
            ("edge-beta|Default", "Microsoft Edge Beta (One)", @"C:\edgeb\msedge.exe"),
            ("edge-beta|Profile 1", "Microsoft Edge Beta (Two)", @"C:\edgeb\msedge.exe"),
        ]);
    }

    [Theory]
    [InlineData("Microsoft Edge Dev", "edge-dev", @"Microsoft\Edge Dev")]
    [InlineData("Microsoft Edge Canary", "edge-canary", @"Microsoft\Edge SxS")]
    [InlineData("Google Chrome Beta", "chrome-beta", @"Google\Chrome Beta")]
    [InlineData("Google Chrome Dev", "chrome-dev", @"Google\Chrome Dev")]
    [InlineData("Google Chrome Canary", "chrome-canary", @"Google\Chrome SxS")]
    public void A_Chromium_channel_reads_the_Local_State_of_its_own_folder(string key, string prefix, string folder)
    {
        var source = new FakeSource
        {
            Keys = [(key, @"C:\c\c.exe")],
            LocalStates = { [folder] = EdgeOne, [EdgeFolder] = ChromeTwo, [ChromeFolder] = ChromeTwo },
        };

        var entry = new BrowserCatalog(source).List().Last();

        entry.Id.ShouldBe($"{prefix}|Default");
        entry.Label.ShouldBe(key);
    }

    [Theory]
    [InlineData("Knowledge Edge Tool")]
    [InlineData("Microsoft Edge Webview Helper")]
    [InlineData("Chromeless Browser")]
    [InlineData("Firefoxy")]
    public void A_key_that_merely_contains_a_browser_name_is_an_unknown_browser(string key)
    {
        var source = new FakeSource { Keys = [(key, @"C:\u\u.exe")], Ini = Ini, LocalStates = { [EdgeFolder] = EdgeOne, [ChromeFolder] = ChromeTwo } };

        var entry = new BrowserCatalog(source).List()[1];

        entry.Id.ShouldBe(key.ToLowerInvariant());
        entry.Label.ShouldBe(key);
        entry.ProfileArguments.ShouldBeEmpty();
        entry.IsFirefox.ShouldBeFalse();
    }

    [Fact]
    public void The_same_browser_under_another_spelling_of_its_key_is_listed_once()
    {
        var source = new FakeSource { Keys = [("Microsoft Edge", @"C:\edge\msedge.exe"), ("MICROSOFT EDGE", @"C:\edge\msedge.exe")], LocalStates = { [EdgeFolder] = EdgeOne } };

        new BrowserCatalog(source).List().Select(e => e.Id).ShouldBe([BrowserEntry.DefaultId, "edge|Default"]);
    }

    [Fact]
    public async Task An_expired_list_is_answered_at_once_while_one_read_runs_in_the_background()
    {
        var clock = new FakeTimeProvider();
        var source = new FakeSource { Keys = [("First Browser", @"C:\f\f.exe")] };
        var catalog = new BrowserCatalog(source, clock);
        catalog.List().Select(e => e.Id).ShouldBe([BrowserEntry.DefaultId, "first browser"]);
        using var gate = new ManualResetEventSlim(false);
        source.Gate = gate;
        source.Keys = [("Second Browser", @"C:\s\s.exe")];
        clock.Advance(TimeSpan.FromSeconds(31));

        // A read that waited for the held source would never come back here.
        var old = await Task.Run(() => catalog.List()).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        SpinWait.SpinUntil(() => source.Reads == 2, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        catalog.List();
        catalog.List();

        old.Select(e => e.Id).ShouldBe([BrowserEntry.DefaultId, "first browser"]);
        source.Reads.ShouldBe(2, "only one read runs at a time");
        gate.Set();
        SpinWait.SpinUntil(() => catalog.List().Count == 2 && catalog.List()[1].Id == "second browser", TimeSpan.FromSeconds(5)).ShouldBeTrue();
        source.Reads.ShouldBe(2);
    }

    [Fact]
    public void A_refresh_reads_at_once_and_a_fresh_list_is_not_read_again()
    {
        var clock = new FakeTimeProvider();
        var source = new FakeSource { Keys = [("First Browser", @"C:\f\f.exe")] };
        var catalog = new BrowserCatalog(source, clock);
        catalog.List();
        clock.Advance(TimeSpan.FromSeconds(29));
        catalog.List();
        source.Reads.ShouldBe(1);
        source.Keys = [("Second Browser", @"C:\s\s.exe")];

        catalog.Refresh();

        source.Reads.ShouldBe(2);
        catalog.List()[1].Id.ShouldBe("second browser");
    }

    [Fact]
    public void A_per_user_install_registers_a_suffixed_key_and_is_still_that_browser()
    {
        var source = new FakeSource
        {
            Keys = [("Google Chrome.ABC123", @"C:\u\chrome.exe"), ("Google Chrome Canary.XYZ", @"C:\u\canary.exe"), ("FIREFOX.EXE", @"C:\ff\firefox.exe")],
            Ini = Ini,
            LocalStates = { [ChromeFolder] = EdgeOne, [@"Google\Chrome SxS"] = EdgeOne },
        };

        new BrowserCatalog(source).List().Select(e => e.Id).ShouldBe(
            [BrowserEntry.DefaultId, "chrome|Default", "chrome-canary|Default", "firefox|alpha", "firefox|name with space"]);
    }

    [Fact]
    public void A_release_under_a_folder_path_with_Nightly_in_it_is_still_the_release()
    {
        var source = new FakeSource { Keys = [("Firefox-1A2B", @"C:\Users\Nightly\Mozilla Firefox\firefox.exe")], Ini = Ini };

        new BrowserCatalog(source).List()[1].Id.ShouldBe("firefox|alpha");
    }

    [Fact]
    public void Another_Firefox_build_beside_the_release_gets_its_own_ids()
    {
        var source = new FakeSource
        {
            Keys = [("Firefox-1A2B", @"C:\Program Files\Mozilla Firefox\firefox.exe"), ("Firefox-3C4D", @"C:\Program Files\Mozilla Firefox ESR\firefox.exe")],
            Ini = "[Profile0]\nName=alpha\n",
        };

        new BrowserCatalog(source).List().Skip(1).Select(e => (e.Id, e.Label)).ShouldBe(
            [("firefox|alpha", "Firefox"), ("firefox-esr|alpha", "Firefox ESR")]);
    }

    [Fact]
    public void A_stale_key_without_a_program_does_not_hide_the_one_that_has_it()
    {
        var source = new FakeSource { Keys = [("Google Chrome", null), ("Google Chrome", @"C:\c\chrome.exe"), ("Some Browser", null), ("SOME BROWSER", @"C:\sb\sb.exe")] };

        new BrowserCatalog(source).List().Skip(1).Select(e => e.Executable).ShouldBe([@"C:\c\chrome.exe", @"C:\sb\sb.exe"]);
    }

    [Fact]
    public void A_browser_in_both_hives_reads_its_profile_files_once()
    {
        var source = new FakeSource { Keys = [("Firefox-1A2B", null), ("Firefox-1A2B", @"C:\ff\firefox.exe")], Ini = Ini };

        var list = new BrowserCatalog(source).List();

        source.IniReads.ShouldBe(1);
        list[1].Executable.ShouldBe(@"C:\ff\firefox.exe");
    }

    [Fact]
    public void A_failed_read_is_not_kept()
    {
        var source = new FakeSource { Throws = true };
        var catalog = new BrowserCatalog(source, new FakeTimeProvider());

        Should.Throw<InvalidOperationException>(() => catalog.List());
        source.Throws = false;

        catalog.List().ShouldHaveSingleItem();
    }

    private sealed class FakeSource : IBrowserSource
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public int IniReads { get; private set; }
        public ManualResetEventSlim? Gate { get; set; }
        public bool Throws { get; set; }
        public IReadOnlyList<(string Key, string? Executable)> Keys { get; set; } = [];
        public string? Ini { get; init; }
        public Dictionary<string, string> LocalStates { get; } = [];

        public IReadOnlyList<(string Key, string? Executable)> Installed()
        {
            Interlocked.Increment(ref _reads);
            Gate?.Wait();
            return Throws ? throw new InvalidOperationException("boom") : Keys;
        }
        public string? FirefoxProfilesIni()
        {
            IniReads++;
            return Ini;
        }
        public string? ChromiumLocalState(string folder) => LocalStates.GetValueOrDefault(folder);
    }
}
