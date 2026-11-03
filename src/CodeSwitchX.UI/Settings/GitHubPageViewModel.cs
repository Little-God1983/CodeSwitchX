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
        IReadOnlyList<string> logins = [];
        IReadOnlyList<BrowserEntry> entries = [];
        try
        {
            logins = await gh.AccountsAsync(ct);
            // The installed browsers are read once for all rows.
            entries = catalog.List();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The page then says how to log in; only the kind of failure is logged.
            logger?.LogWarning("The GitHub accounts could not be read: {Failure}", ex.GetType().Name);
            logins = [];
        }

        if (round != _round)
        {
            return; // a later refresh is the newer answer
        }

        Accounts.Clear();
        foreach (var login in logins)
        {
            Accounts.Add(new GitHubAccountRow(login, entries, browsers.ChoiceFor(login), id => browsers.SetChoice(login, id),
                () => browsers.Open(login, "https://github.com/settings/profile")));
        }

        _read = true;
        OnPropertyChanged(nameof(HasNoAccounts));
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
        // A browser that is gone stays selected, shown as missing, so the choice is not lost behind another one.
        Choices = entries.Any(e => e.Id == selectedId) ? entries : [.. entries, new BrowserEntry(selectedId, $"Missing: {selectedId}", null, [])];
        TestCommand = new RelayCommand(() => Message = open());
    }

    public string Login { get; }

    public IReadOnlyList<BrowserEntry> Choices { get; }

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
            OnPropertyChanged();
        }
    }

    public IRelayCommand TestCommand { get; }

    /// <summary>Why the Test could not open the browser, or none.</summary>
    [ObservableProperty] private string? _message;
}
