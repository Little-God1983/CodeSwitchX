using System.Diagnostics;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Hosting.Browsers;
using CodeSwitchX.UI.Settings;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.GitHub;

/// <summary>
/// The browser each GitHub account opens its pages in (#335): chosen per login, saved as the entry's id, started with the
/// profile's arguments. Only https://github.com addresses are ever opened.
/// </summary>
public sealed class GitHubBrowsers(BrowserCatalog catalog, ISettingsStore settings, Func<ProcessStartInfo, bool> start, ILogger<GitHubBrowsers> logger)
{
    // Read on the UI thread, written there and by LoadAsync's continuation: every access holds the lock.
    private readonly Dictionary<string, string> _choices = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>The saved choices; unreadable ones leave every account on the Windows default. Never throws.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            if (await settings.GetAsync<Dictionary<string, string>>(SettingKeys.GitHubBrowsers, ct).ConfigureAwait(false) is { } stored)
            {
                lock (_gate)
                {
                    foreach (var (login, id) in stored)
                    {
                        _choices[login] = id;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The GitHub browser choices could not be read; the Windows default is used");
        }
    }

    public string ChoiceFor(string login)
    {
        lock (_gate)
        {
            return _choices.TryGetValue(login, out var id) ? id : BrowserEntry.DefaultId;
        }
    }

    public void SetChoice(string login, string id)
    {
        Dictionary<string, string> copy;
        lock (_gate)
        {
            _choices[login] = id;
            copy = new Dictionary<string, string>(_choices);
        }

        _ = SaveAsync(copy);
    }

    /// <summary>The label of the account's browser; the id itself when that browser is gone.</summary>
    public string NameFor(string login)
    {
        var id = ChoiceFor(login);
        return catalog.List().FirstOrDefault(e => e.Id == id)?.Label ?? id;
    }

    /// <summary>Opens <paramref name="url"/> in the account's browser: null when it was opened, otherwise the line to show.</summary>
    public string? Open(string login, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com")
        {
            return "Only GitHub addresses can be opened.";
        }

        var id = ChoiceFor(login);
        var entry = catalog.List().FirstOrDefault(e => e.Id == id);
        if (entry is null)
        {
            return $"Couldn't find {id}. Pick a browser in Settings, GitHub.";
        }

        // Only the Windows default goes through the shell; a browser whose program is unknown never falls back to it.
        var isDefault = entry.Id == BrowserEntry.DefaultId;
        if (!isDefault && entry.Executable is null)
        {
            return $"Couldn't open {entry.Label}. Pick another browser in Settings, GitHub.";
        }

        var info = isDefault
            ? new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }
            : new ProcessStartInfo(entry.Executable!) { UseShellExecute = false };
        if (!isDefault)
        {
            foreach (var argument in entry.Arguments(uri.AbsoluteUri))
            {
                info.ArgumentList.Add(argument);
            }
        }

        try
        {
            // A shell start gives no process when a running one took the address: only an exception is a failure there.
            if (start(info) || info.UseShellExecute)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The address names a repository, so only the kind of failure is logged.
            logger.LogWarning("A GitHub address could not be opened in {Browser}: {Failure}", entry.Id, ex.GetType().Name);
        }

        return $"Couldn't open {entry.Label}. Pick another browser in Settings, GitHub.";
    }

    private async Task SaveAsync(Dictionary<string, string> choices)
    {
        try
        {
            await settings.SetAsync(SettingKeys.GitHubBrowsers, choices, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The GitHub browser choices could not be saved");
        }
    }
}
