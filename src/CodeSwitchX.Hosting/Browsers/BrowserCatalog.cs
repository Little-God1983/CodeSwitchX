namespace CodeSwitchX.Hosting.Browsers;

/// <summary>Where the catalog finds what is installed; the real one reads the registry and the browsers' profile files.</summary>
public interface IBrowserSource
{
    /// <summary>The browsers registered under StartMenuInternet: the key's name and the executable of its open command.</summary>
    IReadOnlyList<(string Key, string? Executable)> Installed();

    string? FirefoxProfilesIni();

    /// <summary>The Local State text of a Chromium browser, from the folder under %LOCALAPPDATA% (for example <c>Microsoft\Edge Beta</c>) and its <c>User Data</c>.</summary>
    string? ChromiumLocalState(string folder);
}

/// <summary>
/// The browsers an account's GitHub pages can open in (#335): the Windows default first, then each installed browser with its profiles.
/// The list is read once and kept for <see cref="KeepFor"/>, so opening a pill or a page does not read the registry and files each time;
/// <see cref="Refresh"/> drops it.
/// </summary>
public sealed class BrowserCatalog(IBrowserSource source, TimeProvider? time = null)
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromSeconds(30);

    // The known Chromium builds by registry key: the id prefix, and the folder under %LOCALAPPDATA% that holds their Local State.
    // The release builds keep the prefixes saved choices already use (edge|Default, chrome|Default).
    private static readonly Dictionary<string, (string Prefix, string Folder)> Chromium = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft Edge"] = ("edge", @"Microsoft\Edge"),
        ["Microsoft Edge Beta"] = ("edge-beta", @"Microsoft\Edge Beta"),
        ["Microsoft Edge Dev"] = ("edge-dev", @"Microsoft\Edge Dev"),
        ["Microsoft Edge Canary"] = ("edge-canary", @"Microsoft\Edge SxS"),
        ["Google Chrome"] = ("chrome", @"Google\Chrome"),
        ["Google Chrome Beta"] = ("chrome-beta", @"Google\Chrome Beta"),
        ["Google Chrome Dev"] = ("chrome-dev", @"Google\Chrome Dev"),
        ["Google Chrome Canary"] = ("chrome-canary", @"Google\Chrome SxS"),
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private IReadOnlyList<BrowserEntry>? _kept;
    private DateTimeOffset _readAt;

    /// <summary>Forgets the kept list, so the next <see cref="List"/> reads what is installed now.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            _kept = null;
        }
    }

    public IReadOnlyList<BrowserEntry> List()
    {
        lock (_gate)
        {
            if (_kept is null || _time.GetUtcNow() - _readAt >= KeepFor)
            {
                // A read that throws keeps nothing, so the next call tries again.
                _kept = Read();
                _readAt = _time.GetUtcNow();
            }

            return _kept;
        }
    }

    private List<BrowserEntry> Read()
    {
        var entries = new List<BrowserEntry> { new(BrowserEntry.DefaultId, "Windows default browser", null, []) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BrowserEntry.DefaultId };
        foreach (var (key, executable) in source.Installed())
        {
            if (key.Equals("IEXPLORE.EXE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var added = new List<BrowserEntry>();
            if (FirefoxChannel(key, executable) is { } channel)
            {
                var names = source.FirefoxProfilesIni() is { } ini ? BrowserProfiles.Firefox(ini) : [];
                Add(added, channel.Prefix, channel.Label, executable, true, [.. names.Select(n => (n, n, (IReadOnlyList<string>)["-P", n]))]);
            }
            else if (Chromium.TryGetValue(key, out var build))
            {
                var profiles = source.ChromiumLocalState(build.Folder) is { } state
                    ? BrowserProfiles.Chromium(state).Select(p => (p.Directory, p.Name, (IReadOnlyList<string>)[$"--profile-directory={p.Directory}"])).ToList()
                    : [];
                Add(added, build.Prefix, key, executable, false, profiles);
            }
            else
            {
                added.Add(new(key.ToLowerInvariant(), key, executable, []));
            }

            // The id is what is saved and compared, so it is what tells a browser seen twice (both registry hives) from another build.
            entries.AddRange(added.Where(e => seen.Add(e.Id)));
        }

        return entries;
    }

    /// <summary>
    /// The Firefox build a key names: the release one is <c>Firefox-&lt;hash&gt;</c>; Developer Edition and Nightly are told by the key or by
    /// the folder of their program. A key that merely starts with the letters, or lacks the dash, is another browser.
    /// </summary>
    private static (string Prefix, string Label)? FirefoxChannel(string key, string? executable)
    {
        var firefox = key.StartsWith("Firefox-", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Firefox", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("Firefox Developer Edition", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("Firefox Nightly", StringComparison.OrdinalIgnoreCase);
        if (!firefox)
        {
            return null;
        }

        var where = $"{key}|{executable}";
        if (where.Contains("Developer Edition", StringComparison.OrdinalIgnoreCase))
        {
            return ("firefox-dev", "Firefox Developer Edition");
        }

        return where.Contains("Nightly", StringComparison.OrdinalIgnoreCase) ? ("firefox-nightly", "Firefox Nightly") : ("firefox", "Firefox");
    }

    /// <summary>One profile gives the plain label, several give one entry each; none gives the browser as it opens by itself.</summary>
    private static void Add(List<BrowserEntry> entries, string prefix, string label, string? executable, bool firefox,
        IReadOnlyList<(string Key, string Name, IReadOnlyList<string> Arguments)> profiles)
    {
        if (profiles.Count == 0)
        {
            entries.Add(new(prefix, label, executable, [], firefox));
            return;
        }

        foreach (var (key, name, arguments) in profiles)
        {
            entries.Add(new($"{prefix}|{key}", profiles.Count == 1 ? label : $"{label} ({name})", executable, arguments, firefox));
        }
    }
}
