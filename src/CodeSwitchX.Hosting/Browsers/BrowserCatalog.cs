namespace CodeSwitchX.Hosting.Browsers;

/// <summary>Where the catalog finds what is installed; the real one reads the registry and the browsers' profile files.</summary>
public interface IBrowserSource
{
    /// <summary>The browsers registered under StartMenuInternet: the key's name and the executable of its open command.</summary>
    IReadOnlyList<(string Key, string? Executable)> Installed();

    string? FirefoxProfilesIni();

    string? EdgeLocalState();

    string? ChromeLocalState();
}

/// <summary>The browsers an account's GitHub pages can open in (#335): the Windows default first, then each installed browser with its profiles.</summary>
public sealed class BrowserCatalog(IBrowserSource source)
{
    public IReadOnlyList<BrowserEntry> List()
    {
        var entries = new List<BrowserEntry> { new(BrowserEntry.DefaultId, "Windows default browser", null, []) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, executable) in source.Installed())
        {
            if (key.Equals("IEXPLORE.EXE", StringComparison.OrdinalIgnoreCase) || !seen.Add(key))
            {
                continue;
            }

            if (key.Contains("firefox", StringComparison.OrdinalIgnoreCase))
            {
                var names = source.FirefoxProfilesIni() is { } ini ? BrowserProfiles.Firefox(ini) : [];
                Add(entries, "firefox", "Firefox", executable, [.. names.Select(n => (n, n, (IReadOnlyList<string>)["-P", n]))]);
            }
            else if (key.Contains("edge", StringComparison.OrdinalIgnoreCase))
            {
                Add(entries, "edge", "Microsoft Edge", executable, Chromium(source.EdgeLocalState()));
            }
            else if (key.Contains("chrome", StringComparison.OrdinalIgnoreCase))
            {
                Add(entries, "chrome", "Google Chrome", executable, Chromium(source.ChromeLocalState()));
            }
            else
            {
                entries.Add(new(key.ToLowerInvariant(), key, executable, []));
            }
        }

        return entries;
    }

    private static List<(string Key, string Name, IReadOnlyList<string> Arguments)> Chromium(string? localState) =>
        localState is null ? [] : [.. BrowserProfiles.Chromium(localState).Select(p => (p.Directory, p.Name, (IReadOnlyList<string>)[$"--profile-directory={p.Directory}"]))];

    /// <summary>One profile gives the plain label, several give one entry each; none gives the browser as it opens by itself.</summary>
    private static void Add(List<BrowserEntry> entries, string prefix, string label, string? executable,
        IReadOnlyList<(string Key, string Name, IReadOnlyList<string> Arguments)> profiles)
    {
        if (profiles.Count == 0)
        {
            entries.Add(new(prefix, label, executable, []));
            return;
        }

        foreach (var (key, name, arguments) in profiles)
        {
            entries.Add(new($"{prefix}|{key}", profiles.Count == 1 ? label : $"{label} ({name})", executable, arguments));
        }
    }
}
