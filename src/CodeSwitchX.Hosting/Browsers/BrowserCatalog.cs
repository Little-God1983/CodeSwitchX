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
/// <see cref="List"/> answers with the last list at once; when it is older than <see cref="KeepFor"/> one read runs in the background,
/// and only the very first call reads on its own thread. <see cref="Refresh"/> reads now, for a caller that is already off the UI thread.
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
    private bool _reading;
    private long _started;
    private long _keptRead;

    /// <summary>Reads what is installed now, keeps it and returns it; the read runs outside the lock.</summary>
    public IReadOnlyList<BrowserEntry> Refresh()
    {
        var number = Begin();
        var read = Read();
        Keep(read, number);
        return read;
    }

    public IReadOnlyList<BrowserEntry> List()
    {
        lock (_gate)
        {
            if (_kept is { } kept)
            {
                if (!_reading && _time.GetUtcNow() - _readAt >= KeepFor)
                {
                    _reading = true;
                    var number = ++_started;
                    _ = Task.Run(() => ReadInBackground(number));
                }

                return kept;
            }
        }

        // No list yet (App start warms it, so this is rare): the one read that has to be waited for. A read that throws keeps nothing.
        var first = Begin();
        var read = Read();
        Keep(read, first);
        return read;
    }

    private long Begin()
    {
        lock (_gate)
        {
            return ++_started;
        }
    }

    // Reads are numbered when they start: one that started before the kept list's read is older and never replaces it.
    private void Keep(IReadOnlyList<BrowserEntry> read, long number)
    {
        lock (_gate)
        {
            if (number > _keptRead)
            {
                _kept = read;
                _keptRead = number;
                _readAt = _time.GetUtcNow();
            }
        }
    }

    private void ReadInBackground(long number)
    {
        try
        {
            Keep(Read(), number);
        }
        catch (Exception)
        {
            // The old list stays; the next call after KeepFor tries again.
        }
        finally
        {
            lock (_gate)
            {
                _reading = false;
            }
        }
    }

    private List<BrowserEntry> Read()
    {
        var entries = new List<BrowserEntry> { new(BrowserEntry.DefaultId, "Windows default browser", null, []) };
        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal) { [BrowserEntry.DefaultId] = 0 };
        // A browser in both registry hives is one key: its files are read once, and the entry that has a program wins.
        var keys = new Dictionary<string, (string Key, string? Executable)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, executable) in source.Installed())
        {
            if (!key.Equals("IEXPLORE.EXE", StringComparison.OrdinalIgnoreCase) && (!keys.TryGetValue(key, out var have) || have.Executable is null))
            {
                keys[key] = (key, executable);
            }
        }

        foreach (var (key, executable) in keys.Values)
        {
            var added = new List<BrowserEntry>();
            // A per-user install registers "Google Chrome.<hash>": the part before the first dot names the browser (so does the old FIREFOX.EXE).
            var dot = key.IndexOf('.');
            var name = dot < 0 ? key : key[..dot];
            if (FirefoxBuild(name, executable) is { } build)
            {
                // Firefox builds share the one profile list of profiles.ini (accepted: a build may not use every profile).
                var names = source.FirefoxProfilesIni() is { } ini ? BrowserProfiles.Firefox(ini) : [];
                Add(added, build.Prefix, build.Label, executable, true, [.. names.Select(n => (n, n, (IReadOnlyList<string>)["-P", n]))]);
            }
            else if (Chromium.TryGetValue(name, out var chromium))
            {
                var profiles = source.ChromiumLocalState(chromium.Folder) is { } state
                    ? BrowserProfiles.Chromium(state).Select(p => (p.Directory, p.Name, (IReadOnlyList<string>)[$"--profile-directory={p.Directory}"])).ToList()
                    : [];
                Add(added, chromium.Prefix, name, executable, false, profiles);
            }
            else
            {
                added.Add(new(name.ToLowerInvariant(), name, executable, []));
            }

            // The id is what is saved and compared: another key of the same browser is one entry, the one with a program wins.
            foreach (var entry in added)
            {
                if (!indexOf.TryGetValue(entry.Id, out var at))
                {
                    indexOf[entry.Id] = entries.Count;
                    entries.Add(entry);
                }
                else if (entries[at].Executable is null && entry.Executable is not null)
                {
                    entries[at] = entry;
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// The Firefox build a key names: the release one is <c>Firefox-&lt;hash&gt;</c> (or the old <c>FIREFOX</c>) in a <c>Mozilla Firefox</c> folder.
    /// Another build is told by the key or by its install folder's own name (never the rest of the path): Developer Edition and Nightly,
    /// and any other such as <c>Mozilla Firefox ESR</c> gets a prefix built from that folder name. A key that merely starts with the letters is another browser.
    /// </summary>
    private static (string Prefix, string Label)? FirefoxBuild(string name, string? executable)
    {
        var firefox = name.StartsWith("Firefox-", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Firefox", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Firefox Developer Edition", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Firefox Nightly", StringComparison.OrdinalIgnoreCase);
        if (!firefox)
        {
            return null;
        }

        var folder = executable is null ? "" : Path.GetFileName(Path.GetDirectoryName(executable)) ?? "";
        var where = $"{name}|{folder}";
        if (where.Contains("Developer Edition", StringComparison.OrdinalIgnoreCase))
        {
            return ("firefox-dev", "Firefox Developer Edition");
        }

        if (where.Contains("Nightly", StringComparison.OrdinalIgnoreCase))
        {
            return ("firefox-nightly", "Firefox Nightly");
        }

        // A folder that does not name Firefox says nothing; one that does, with more than "Mozilla Firefox", is another build.
        var rest = folder.Contains("Firefox", StringComparison.OrdinalIgnoreCase)
            ? folder.Replace("Mozilla", "", StringComparison.OrdinalIgnoreCase).Replace("Firefox", "", StringComparison.OrdinalIgnoreCase).Trim()
            : "";
        return rest.Length == 0 ? ("firefox", "Firefox") : ($"firefox-{rest.ToLowerInvariant().Replace(' ', '-')}", $"Firefox {rest}");
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
