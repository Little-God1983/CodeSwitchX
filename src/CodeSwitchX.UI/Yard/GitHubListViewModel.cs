using CodeSwitchX.Core.GitHub;
using CodeSwitchX.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// One tab of the GitHub panel (#335, #406): every open issue or every open pull request of the repository. It shows what the
/// tile knew at once and swaps in the full list when the account has read it. What the user does with the rows (open, copy) is
/// the panel's, <see cref="GitHubPanelViewModel"/>.
/// </summary>
public sealed partial class GitHubListViewModel : ObservableObject
{
    private readonly GitHubPill _pill;
    private readonly GitHubReader _reader;
    private readonly GitHubActivity _activity;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    // Cancelled when the panel closes, which ends the gh run instead of leaving it to run out its timeout. It has no timer and
    // no wait handle is taken from it, so there is nothing to dispose.
    private readonly CancellationTokenSource _stop = new();
    // 1 once a read has started: a tab is read once, however often it is shown.
    private int _started;
    // The pull requests merged from the panel (#408); UI thread. A full list read before the merge still holds them.
    private readonly HashSet<int> _dropped = [];

    /// <summary>How many are open: the tile's count at first, then what the full list says.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabText))]
    private int _count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private IReadOnlyList<GitHubRow> _rows;

    /// <summary>Why the full list could not be read, or none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private string? _loadMessage;

    /// <summary>Says the list is cut short when the repository holds more than the read gave, or none. Kept apart from <see cref="LoadMessage"/>: the list did load.</summary>
    [ObservableProperty] private string? _cutShortMessage;

    /// <summary>Says how many rows of the list could not be made sense of and are left out, or none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private string? _skippedMessage;

    /// <summary>The fresh read has not ended yet; a large repository's can take minutes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingMessage))]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private bool _isLoading = true;

    public string? LoadingMessage => IsLoading ? "Loading the full list..." : null;

    public GitHubListViewModel(GitHubPill pill, GitHubReader reader, GitHubActivity activity, IUiDispatcher ui, TimeProvider time, ILogger logger)
    {
        _pill = pill;
        _reader = reader;
        _activity = activity;
        _ui = ui;
        _time = time;
        _logger = logger;
        _count = pill.Count;
        _rows = RowsOf(pill.Kind == GitHubItemKind.Issue ? pill.Activity.NewestIssues : pill.Activity.NewestPullRequests);
    }

    public GitHubItemKind Kind => _pill.Kind;

    /// <summary>The tab's name: "Pull requests 2", "Issues 5".</summary>
    public string TabText => $"{(Kind == GitHubItemKind.PullRequest ? "Pull requests" : "Issues")} {Count}";

    /// <summary>Says the tab is empty once the read has ended with nothing; none while it loads, when it could not load, or when rows were left out.</summary>
    public string? EmptyText => !IsLoading && Rows.Count == 0 && LoadMessage is null && SkippedMessage is null
        ? Kind == GitHubItemKind.PullRequest ? "No open pull requests." : "No open issues."
        : null;

    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the read of <see cref="LoadAsync"/> has ended, however it ended.</summary>
    internal Task Loaded => _loaded.Task;

    /// <summary>A read has started (or ended): showing the tab again reads nothing.</summary>
    public bool HasStarted => Volatile.Read(ref _started) == 1;

    /// <summary>A pull request merged from the panel (#408) leaves the list, and the count with it.</summary>
    public void Drop(int number)
    {
        _dropped.Add(number);
        var left = Rows.Where(r => r.Item.Number != number).ToList();
        if (left.Count < Rows.Count)
        {
            Rows = left;
            Count = Math.Max(0, Count - 1);
        }
    }

    /// <summary>Ends the read: nothing it would have found reaches the list or the tile.</summary>
    public void Stop() => _stop.Cancel();

    /// <summary>
    /// Reads the full list, once: a later call waits for the first read. <see cref="Stop"/> cancels the read: nothing it would have
    /// found reaches the panel or the tile.
    /// </summary>
    public Task LoadAsync(CancellationToken ct) => Interlocked.Exchange(ref _started, 1) == 1 ? Loaded : LoadOnceAsync(ct);

    private async Task LoadOnceAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        try
        {
            if (!linked.IsCancellationRequested)
            {
                await ReadAsync(linked.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _ui.Post(() => IsLoading = false);
            _loaded.TrySetResult();
        }
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        var activity = _pill.Activity;
        GitHubList? all;
        var since = _activity.Latest(activity.Repo);
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

        // The total the tile now holds when the list was applied; a list that lost to a newer result shows its own total, and the tile keeps the newer one.
        int? stored = null;
        if (all is not null)
        {
            if (_activity.Replace(activity.Repo, _pill.Kind, all, since, out var total))
            {
                stored = total;
            }

            if (all.Skipped > 0)
            {
                _logger.LogWarning("{Skipped} rows of a GitHub list could not be read", all.Skipped);
            }
        }

        _ui.Post(() => Show(all, stored));
    }

    private void Show(GitHubList? all, int? stored)
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        if (all is null)
        {
            LoadMessage = "Couldn't load the latest list. Showing what was last read.";
            return;
        }

        // A list at the limit keeps the count GraphQL gave (see TotalGiven). A pull request merged from the panel while the list was read
        // stays out of it (review of #408).
        var gone = all.Items.Count(item => _dropped.Contains(item.Number));
        var total = Math.Max(0, (stored ?? all.TotalGiven(_pill.Count)) - gone);
        Count = total;
        Rows = RowsOf(gone == 0 ? all.Items : all.Items.Where(item => !_dropped.Contains(item.Number)).ToList());
        SkippedMessage = all.Skipped switch { 0 => null, 1 => "1 row could not be shown.", var n => $"{n} rows could not be shown." };
        CutShortMessage = all.ReachedLimit && total > all.Items.Count + all.Skipped ? $"Showing the newest {all.Items.Count} of {total}. The rest are on GitHub." : null;
        LoadMessage = null;
    }

    private IReadOnlyList<GitHubRow> RowsOf(IReadOnlyList<GitHubItem> items)
    {
        var now = _time.GetUtcNow();
        return items.Select(item => GitHubRow.From(item, _pill.Activity.Account, now)).ToList();
    }
}
