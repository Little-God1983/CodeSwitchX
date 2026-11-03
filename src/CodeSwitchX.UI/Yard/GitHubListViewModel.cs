using CodeSwitchX.Core.GitHub;
using CodeSwitchX.UI.GitHub;
using CodeSwitchX.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// The panel a click on a GitHub pill opens (#335): every open issue or pull request of the repository. It shows what the
/// pill knew at once and swaps in the full list when the account has read it; a row opens its item in the account's browser.
/// </summary>
public sealed partial class GitHubListViewModel : ObservableObject
{
    private readonly GitHubPill _pill;
    private readonly GitHubReader _reader;
    private readonly GitHubActivity _activity;
    private readonly GitHubBrowsers _browsers;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Action<GitHubListViewModel> _closed;
    private bool _isClosed;
    private CancellationTokenSource? _stop;

    [ObservableProperty] private string _heading;
    [ObservableProperty] private IReadOnlyList<GitHubRow> _rows;
    /// <summary>Why the full list could not be read, or none. Kept apart from <see cref="OpenMessage"/>: each says what happened and neither hides the other.</summary>
    [ObservableProperty] private string? _loadMessage;

    /// <summary>Says the list is cut short when the repository holds more than the read gave, or none. Kept apart from <see cref="LoadMessage"/>: the list did load.</summary>
    [ObservableProperty] private string? _cutShortMessage;

    /// <summary>Says how many rows of the list could not be made sense of and are left out, or none.</summary>
    [ObservableProperty] private string? _skippedMessage;

    /// <summary>The fresh read has not ended yet; a large repository's can take minutes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingMessage))]
    private bool _isLoading = true;

    public string? LoadingMessage => IsLoading ? "Loading the full list..." : null;

    /// <summary>Why the browser could not open a row, or none; a row that opens clears it.</summary>
    [ObservableProperty] private string? _openMessage;

    public GitHubListViewModel(GitHubPill pill, GitHubReader reader, GitHubActivity activity, GitHubBrowsers browsers, IUiDispatcher ui,
        TimeProvider time, ILogger logger, Action<GitHubListViewModel> closed)
    {
        _pill = pill;
        _reader = reader;
        _activity = activity;
        _browsers = browsers;
        _ui = ui;
        _time = time;
        _logger = logger;
        _closed = closed;
        _heading = HeadingFor(pill.Count);
        _rows = RowsOf(pill.Kind == GitHubItemKind.Issue ? pill.Activity.NewestIssues : pill.Activity.NewestPullRequests);
        // Worked out once: the browser's name is read from the registry and files, which a binding would repeat.
        AccountLine = $"Read with {pill.Activity.Account}. Opens in {browsers.NameFor(pill.Activity.Account)}.";
    }

    public string AccountLine { get; }

    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the read of <see cref="LoadAsync"/> has ended, however it ended.</summary>
    internal Task Loaded => _loaded.Task;

    /// <summary>Reads the full list. The tile's count follows it even when the panel was closed meanwhile; the panel does not.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        // Its own cancellation: closing the panel ends the gh run instead of leaving it to run out its timeout.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stop = stop;
        try
        {
            if (_isClosed)
            {
                stop.Cancel();
            }

            await ReadAsync(stop.Token).ConfigureAwait(false);
        }
        finally
        {
            _stop = null;
            _ui.Post(() => IsLoading = false);
            _loaded.TrySetResult();
        }
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        var activity = _pill.Activity;
        GitHubList? all;
        var since = _activity.VersionOf(activity.Repo);
        try
        {
            all = await _reader.ListAsync(activity.Repo, activity.Account, _pill.Kind, ct).ConfigureAwait(false);
            // An answer that was already on its way when the panel closed does not exist.
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // The type only: a message could carry a repository's name or a title.
            _logger.LogWarning("Reading the GitHub list failed: {ExceptionType}", ex.GetType().Name);
            all = null;
        }

        // The total the tile now holds: the list's when it was applied, else the newer result's that it lost to.
        int? stored = null;
        if (all is not null)
        {
            stored = _activity.Replace(activity.Repo, _pill.Kind, all, since);
            if (all.Skipped > 0)
            {
                _logger.LogWarning("{Skipped} rows of a GitHub list could not be read", all.Skipped);
            }
        }

        _ui.Post(() => Show(all, stored));
    }

    private void Show(GitHubList? all, int? stored)
    {
        if (_isClosed)
        {
            return;
        }

        if (all is null)
        {
            LoadMessage = "Couldn't load the latest list. Showing what was last read.";
            return;
        }

        // A list that is not the whole truth (the limit, a row left out) keeps the count GraphQL gave.
        var total = stored ?? all.TotalGiven(_pill.Count);
        Heading = HeadingFor(total);
        Rows = RowsOf(all.Items);
        SkippedMessage = all.Skipped switch { 0 => null, 1 => "1 row could not be shown.", var n => $"{n} rows could not be shown." };
        CutShortMessage = all.ReachedLimit && total > all.Items.Count ? $"Showing the newest {all.Items.Count} of {total}. The rest are on GitHub." : null;
        LoadMessage = null;
    }

    private string HeadingFor(int count) => $"{_pill.Activity.Repo.Name}: {count} open {GitHubPill.NounFor(_pill.Kind, count)}";

    private IReadOnlyList<GitHubRow> RowsOf(IReadOnlyList<GitHubItem> items)
    {
        var now = _time.GetUtcNow();
        return items.Select(item => GitHubRow.From(item, _pill.Activity.Account, now)).ToList();
    }

    [RelayCommand]
    private void Close()
    {
        _isClosed = true;
        try
        {
            _stop?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // the read ended meanwhile
        }

        _closed(this);
    }

    /// <summary>Opens the item and keeps the panel open; a browser that cannot open it says why in the panel.</summary>
    [RelayCommand]
    private void OpenRow(GitHubRow row)
    {
        OpenMessage = _browsers.Open(_pill.Activity.Account, row.Item.Url);
    }
}
