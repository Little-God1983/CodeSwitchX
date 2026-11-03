using System.Collections.ObjectModel;
using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Hosting.Browsers;
using CodeSwitchX.UI.GitHub;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Settings;

/// <summary>
/// Settings → GitHub (#335): one row per account the GitHub command line knows, each paired with the browser its issues and
/// pull requests open in. The accounts are read again whenever the page is opened, so a new <c>gh auth login</c> shows up by itself.
/// </summary>
public sealed class GitHubPageViewModel(IGhCli gh, GitHubBrowsers browsers, BrowserCatalog catalog, ILogger<GitHubPageViewModel>? logger = null) : ObservableObject
{
    private int _round;

    private bool _read;

    public ObservableCollection<GitHubAccountRow> Accounts { get; } = [];

    /// <summary>The accounts were read and there are none: the page says how to log in.</summary>
    public bool HasNoAccounts => _read && Accounts.Count == 0;

    public async Task RefreshAsync(CancellationToken ct)
    {
        var round = ++_round;
        // Off the UI thread, which this continues on: gh starts before its first await, and the browsers are read from the registry and files.
        // The two are read apart, so a failure of one does not discard the other.
        var accountsRead = Task.Run(() => gh.AccountsAsync(ct), ct);
        // Read again, so a browser installed since the page was last open is there, and once for all rows.
        var browsersRead = Task.Run(() => catalog.Refresh(), ct);
        IReadOnlyList<string>? logins = null;
        try
        {
            logins = await accountsRead;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Null: the rows stay, or the page says how to log in; only the kind of failure is logged.
            logger?.LogWarning("The GitHub accounts could not be read: {Failure}", ex.GetType().Name);
        }

        IReadOnlyList<BrowserEntry> entries;
        try
        {
            entries = await browsersRead;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The rows are built from what the catalog has: its last list, or at least the Windows default.
            logger?.LogWarning("The installed browsers could not be read: {Failure}", ex.GetType().Name);
            entries = catalog.List();
        }

        if (round != _round)
        {
            return; // a later refresh is the newer answer
        }

        // Null is a failed read: the rows (and a pick or Test line in them) stay, and the page says how to log in only when there are none.
        // An empty list is gh saying nobody is logged in.
        if (logins is null)
        {
            _read = true;
            OnPropertyChanged(nameof(HasNoAccounts));
            return;
        }

        // gh can list a login twice (or in another case): one row each.
        logins = [.. logins.Distinct(StringComparer.OrdinalIgnoreCase)];
        // The rows are updated in place: a row whose login is still there stays, so a pick or a Test line in progress is not interrupted.
        for (var i = Accounts.Count - 1; i >= 0; i--)
        {
            if (!logins.Contains(Accounts[i].Login, StringComparer.OrdinalIgnoreCase))
            {
                Accounts.RemoveAt(i);
            }
        }

        for (var i = 0; i < logins.Count; i++)
        {
            var login = logins[i];
            var at = IndexOf(login);
            if (at < 0)
            {
                Accounts.Insert(i, new GitHubAccountRow(login, entries, browsers.ChoiceFor(login), id => browsers.SetChoice(login, id),
                    () => browsers.Open(login, "https://github.com/settings/profile")));
                continue;
            }

            if (at != i)
            {
                Accounts.Move(at, i);
            }

            Accounts[i].Update(entries);
        }

        _read = true;
        OnPropertyChanged(nameof(HasNoAccounts));
    }

    private int IndexOf(string login)
    {
        for (var i = 0; i < Accounts.Count; i++)
        {
            if (Accounts[i].Login.Equals(login, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>One account on the GitHub page: its browser, and a Test that opens its profile page there.</summary>
public sealed partial class GitHubAccountRow : ObservableObject
{
    private readonly Action<string> _save;

    private string _selectedId;

    public GitHubAccountRow(string login, IReadOnlyList<BrowserEntry> entries, string selectedId, Action<string> save, Func<string?> open)
    {
        Login = login;
        _selectedId = selectedId;
        _save = save;
        Choices = ChoicesFor(entries, selectedId);
        TestCommand = new RelayCommand(() => Message = open());
    }

    public string Login { get; }

    public IReadOnlyList<BrowserEntry> Choices { get; private set; }

    public string SelectedId
    {
        get => _selectedId;
        set
        {
            // A combo box drops its selection while the list is rebuilt: only a real pick is saved.
            if (value is null || value == _selectedId)
            {
                return;
            }

            _selectedId = value;
            _save(value);
            // The failed Test was about the browser that was picked before.
            Message = null;
            OnPropertyChanged();
        }
    }

    public IRelayCommand TestCommand { get; }

    /// <summary>Takes the browsers installed now; the list, and with it the combo box, changes only when they differ from what the row has.</summary>
    public void Update(IReadOnlyList<BrowserEntry> entries)
    {
        var choices = ChoicesFor(entries, _selectedId);
        if (choices.Count == Choices.Count && choices.Zip(Choices).All(p => p.First.SameAs(p.Second)))
        {
            return;
        }

        Choices = choices;
        // A failed Test was about the list as it was.
        Message = null;
        OnPropertyChanged(nameof(Choices));
        // The combo box drops its selection while the list is rebuilt: it reads the pick again.
        OnPropertyChanged(nameof(SelectedId));
    }

    // A browser that is gone stays selected, shown as missing, so the choice is not lost behind another one.
    private static IReadOnlyList<BrowserEntry> ChoicesFor(IReadOnlyList<BrowserEntry> entries, string selectedId) =>
        entries.Any(e => e.Id == selectedId) ? entries : [.. entries, new BrowserEntry(selectedId, $"Missing: {selectedId}", null, [])];

    /// <summary>Why the Test could not open the browser, or none.</summary>
    [ObservableProperty] private string? _message;
}
