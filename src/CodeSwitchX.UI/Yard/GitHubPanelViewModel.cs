using System.ComponentModel;
using System.Globalization;
using CodeSwitchX.Core.GitHub;
using CodeSwitchX.UI.GitHub;
using CodeSwitchX.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// The panel a click on a repository line or a pill opens (#335, #406): the repository's open pull requests and open issues in two
/// tabs. The heading opens the repository's main page (#382), each tab's arrow its <c>/pulls</c> or <c>/issues</c> page, a row its
/// item, all in the account's browser; the panel stays open.
/// </summary>
public sealed partial class GitHubPanelViewModel : ObservableObject
{
    private readonly RepoActivity _repo;
    private readonly GitHubBrowsers _browsers;
    private readonly ITextClipboard _clipboard;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Action<GitHubPanelViewModel> _closed;
    // Ends a read a tab starts later, when the panel has closed by then. No timer and no wait handle is taken from it.
    private readonly CancellationTokenSource _stop = new();
    // The tab the panel opened on, read by LoadAsync; the other is read when it is first shown.
    private readonly GitHubListViewModel _first;
    private ITimer? _copyMessageTimer;
    private int _copies;

    /// <summary>The tab in front.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPullRequestsShown))]
    [NotifyCanExecuteChangedFor(nameof(CopyAllLinksCommand))]
    private GitHubListViewModel _shown;

    /// <summary>Why the browser could not open the repository, a tab's page or a row, or none; any of them that opens clears it.</summary>
    [ObservableProperty] private string? _openMessage;

    /// <summary>What the last copy did, for a moment. It has a place of its own in the header, so the rows do not move under the pointer.</summary>
    [ObservableProperty] private string? _copyMessage;

    /// <summary>How long <see cref="CopyMessage"/> stays.</summary>
    internal static readonly TimeSpan CopyMessageLifetime = TimeSpan.FromSeconds(4);

    public GitHubPanelViewModel(RepoActivity repo, GitHubItemKind shown, GitHubReader reader, GitHubActivity activity, GitHubBrowsers browsers,
        ITextClipboard clipboard, IUiDispatcher ui, TimeProvider time, ILogger logger, Action<GitHubPanelViewModel> closed)
    {
        _repo = repo;
        _browsers = browsers;
        _clipboard = clipboard;
        _ui = ui;
        _time = time;
        _closed = closed;
        PullRequests = new GitHubListViewModel(new GitHubPill(repo, GitHubItemKind.PullRequest), reader, activity, ui, time, logger);
        Issues = new GitHubListViewModel(new GitHubPill(repo, GitHubItemKind.Issue), reader, activity, ui, time, logger);
        _first = ListOf(shown);
        _shown = _first;
        PullRequests.PropertyChanged += OnListChanged;
        Issues.PropertyChanged += OnListChanged;
        // Worked out once: the browser's name is read from the registry and files, which a binding would repeat.
        AccountLine = $"Read with {repo.Account}. Opens in {browsers.NameFor(repo.Account)}.";
    }

    public GitHubListViewModel PullRequests { get; }

    public GitHubListViewModel Issues { get; }

    public bool IsPullRequestsShown => Shown.Kind == GitHubItemKind.PullRequest;

    /// <summary>The repository's name; the tabs carry the counts.</summary>
    public string Heading => _repo.Repo.Name;

    public string AccountLine { get; }

    /// <summary>What the heading does when clicked: its tooltip, and its name for a screen reader.</summary>
    public string OpenRepositoryText => $"Open {_repo.Repo.Slug} on GitHub";

    /// <summary>The tab a click on the repository line opens: pull requests when any are open, they are what gets acted on (#406).</summary>
    public static GitHubItemKind DefaultKind(RepoActivity repo) => repo.OpenPullRequests > 0 ? GitHubItemKind.PullRequest : GitHubItemKind.Issue;

    /// <summary>Completes when the read of the tab the panel opened on has ended, however it ended.</summary>
    internal Task Loaded => _first.Loaded;

    /// <summary>
    /// Reads the tab the panel opened on. The other tab is read when it is first shown, at once, even while this read runs: a list of
    /// a large repository takes minutes, and a tab that is never looked at costs no read (review of #406).
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _first.LoadAsync(linked.Token).ConfigureAwait(false);
    }

    private GitHubListViewModel ListOf(GitHubItemKind kind) => kind == GitHubItemKind.PullRequest ? PullRequests : Issues;

    private void OnListChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitHubListViewModel.Rows) && ReferenceEquals(sender, Shown))
        {
            CopyAllLinksCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Show(GitHubItemKind kind)
    {
        Shown = ListOf(kind);
        // Even before LoadAsync has run: the Yard starts it on the pool, and a quick click can come first (review of #406).
        if (!Shown.HasStarted)
        {
            var list = Shown;
            // Not on the UI thread: the read starts its gh before its first await. The panel's close stops it, as the Yard's does:
            // leaving the Yard closes the panel.
            _ = Task.Run(() => list.LoadAsync(_stop.Token));
        }
    }

    [RelayCommand]
    private void Close()
    {
        _stop.Cancel();
        PullRequests.Stop();
        Issues.Stop();
        _copyMessageTimer?.Dispose();
        _closed(this);
    }

    /// <summary>Opens the item and keeps the panel open; a browser that cannot open it says why in the panel.</summary>
    [RelayCommand]
    private void OpenRow(GitHubRow row) => OpenMessage = _browsers.Open(_repo.Account, row.Item.Url);

    /// <summary>Opens the repository's main page and keeps the panel open.</summary>
    [RelayCommand]
    private void OpenRepository() => OpenMessage = _browsers.Open(_repo.Account, _repo.Repo.Url);

    /// <summary>Opens every open pull request or issue of the repository on GitHub, the tab's arrow (#406).</summary>
    [RelayCommand]
    private void OpenOnGitHub(GitHubItemKind kind) =>
        OpenMessage = _browsers.Open(_repo.Account, $"{_repo.Repo.Url}/{(kind == GitHubItemKind.PullRequest ? "pulls" : "issues")}");

    /// <summary>The row's address on the clipboard; opens nothing.</summary>
    [RelayCommand]
    private void CopyRowLink(GitHubRow row) => Copy(row.Item.Url, $"Copied the link to {row.NumberText}.");

    /// <summary>The address of every row of the tab in front, one to a line, in the list's order.</summary>
    [RelayCommand(CanExecute = nameof(HasRows))]
    private void CopyAllLinks() => Copy(string.Join(Environment.NewLine, Shown.Rows.Select(r => r.Item.Url)), CopiedAllText());

    private bool HasRows() => Shown.Rows.Count > 0;

    /// <summary>Says so when the rows are not the whole list, since a pasted list looks complete.</summary>
    private string CopiedAllText()
    {
        var list = Shown;
        var count = list.Rows.Count.ToString(CultureInfo.InvariantCulture);
        var copied = list.CutShortMessage is not null ? $"Copied the newest {count} links. The rest are on GitHub."
            : list.Rows.Count == 1 ? "Copied 1 link."
            : $"Copied {count} links.";
        if (list.IsLoading)
        {
            return $"{copied} The full list is still loading.";
        }

        if (list.LoadMessage is not null)
        {
            return $"{copied} The full list could not be loaded.";
        }

        return list.SkippedMessage is null ? copied : $"{copied} {list.SkippedMessage}";
    }

    private void Copy(string text, string done)
    {
        CopyMessage = _clipboard.TrySetText(text) ? done : "Couldn't reach the clipboard. Try again.";
        _copyMessageTimer?.Dispose();
        // Disposing does not call back a clear already posted: a held clipboard keeps the UI thread here long enough for one.
        var copy = ++_copies;
        _copyMessageTimer = _time.CreateTimer(_ => _ui.Post(() =>
        {
            if (copy == _copies)
            {
                CopyMessage = null;
            }
        }), null, CopyMessageLifetime, Timeout.InfiniteTimeSpan);
    }
}
