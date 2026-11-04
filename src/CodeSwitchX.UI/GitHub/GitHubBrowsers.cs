using System.Diagnostics;
using CodeSwitchX.Core.GitHub;
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
    private Task _saving = Task.CompletedTask;

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
            logger.LogWarning("The GitHub browser choices could not be read; the Windows default is used: {Failure}", FailureText.Of(ex));
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
        lock (_gate)
        {
            _choices[login] = id;
            // Saves run one after another, each writing the choices as they are when it runs, so an earlier pick never lands over a later one.
            var previous = _saving;
            _saving = Task.Run(() => SaveAfterAsync(previous));
        }
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
        var problem = OpenIn(entry, id, uri);
        if (problem is null || id == BrowserEntry.DefaultId)
        {
            return problem; // the Windows default is always in the list: a new read cannot help it
        }

        // The list may be older than the browser's move or install: look once more at what is installed now, and report only what is still wrong.
        BrowserEntry? fresh;
        try
        {
            fresh = catalog.Refresh().FirstOrDefault(e => e.Id == id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The installed browsers could not be read again: {Failure}", ex.GetType().Name);
            return problem;
        }

        return fresh is not null && !fresh.SameAs(entry) ? OpenIn(fresh, id, uri) : problem;
    }

    private string? OpenIn(BrowserEntry? entry, string id, Uri uri)
    {
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

    private async Task SaveAfterAsync(Task previous)
    {
        await previous.ConfigureAwait(false); // never faults: a failed save is logged below
        try
        {
            Dictionary<string, string> choices;
            lock (_gate)
            {
                choices = new Dictionary<string, string>(_choices);
            }

            await settings.SetAsync(SettingKeys.GitHubBrowsers, choices, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning("The GitHub browser choices could not be saved: {Failure}", FailureText.Of(ex));
        }
    }
}
