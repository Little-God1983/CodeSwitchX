using System.Globalization;
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
    private readonly ITextClipboard _clipboard;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Action<GitHubListViewModel> _closed;
    // Cancelled when the panel closes, which ends the gh run instead of leaving it to run out its timeout. It has no timer and
    // no wait handle is taken from it, so there is nothing to dispose.
    private readonly CancellationTokenSource _stop = new();
    private ITimer? _copyMessageTimer;
    private int _copies;

    [ObservableProperty] private string _heading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyAllLinksCommand))]
    private IReadOnlyList<GitHubRow> _rows;

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

    /// <summary>What the last copy did, for a moment. It has a place of its own in the header, so the rows do not move under the pointer.</summary>
    [ObservableProperty] private string? _copyMessage;

    /// <summary>How long <see cref="CopyMessage"/> stays.</summary>
    internal static readonly TimeSpan CopyMessageLifetime = TimeSpan.FromSeconds(4);

    public GitHubListViewModel(GitHubPill pill, GitHubReader reader, GitHubActivity activity, GitHubBrowsers browsers, ITextClipboard clipboard,
        IUiDispatcher ui, TimeProvider time, ILogger logger, Action<GitHubListViewModel> closed)
    {
        _pill = pill;
        _reader = reader;
        _activity = activity;
        _browsers = browsers;
        _clipboard = clipboard;
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

    /// <summary>Reads the full list. Closing the panel cancels the read: nothing it would have found reaches the panel or the tile.</summary>
    public async Task LoadAsync(CancellationToken ct)
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

        // A list at the limit keeps the count GraphQL gave (see TotalGiven).
        var total = stored ?? all.TotalGiven(_pill.Count);
        Heading = HeadingFor(total);
        Rows = RowsOf(all.Items);
        SkippedMessage = all.Skipped switch { 0 => null, 1 => "1 row could not be shown.", var n => $"{n} rows could not be shown." };
        CutShortMessage = all.ReachedLimit && total > all.Items.Count + all.Skipped ? $"Showing the newest {all.Items.Count} of {total}. The rest are on GitHub." : null;
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
        _stop.Cancel();
        _copyMessageTimer?.Dispose();
        _closed(this);
    }

    /// <summary>Opens the item and keeps the panel open; a browser that cannot open it says why in the panel.</summary>
    [RelayCommand]
    private void OpenRow(GitHubRow row)
    {
        OpenMessage = _browsers.Open(_pill.Activity.Account, row.Item.Url);
    }

    /// <summary>The row's address on the clipboard; opens nothing.</summary>
    [RelayCommand]
    private void CopyRowLink(GitHubRow row) => Copy(row.Item.Url, $"Copied the link to {row.NumberText}.");

    /// <summary>The address of every row shown, one to a line, in the list's order.</summary>
    [RelayCommand(CanExecute = nameof(HasRows))]
    private void CopyAllLinks() => Copy(string.Join(Environment.NewLine, Rows.Select(r => r.Item.Url)), CopiedAllText());

    private bool HasRows() => Rows.Count > 0;

    /// <summary>Says so when the rows are not the whole list, since a pasted list looks complete.</summary>
    private string CopiedAllText()
    {
        var count = Rows.Count.ToString(CultureInfo.InvariantCulture);
        if (CutShortMessage is not null)
        {
            return $"Copied the newest {count} links. The rest are on GitHub.";
        }

        var copied = Rows.Count == 1 ? "Copied 1 link." : $"Copied {count} links.";
        if (IsLoading)
        {
            return $"{copied} The full list is still loading.";
        }

        if (LoadMessage is not null)
        {
            return $"{copied} The full list could not be loaded.";
        }

        return SkippedMessage is null ? copied : $"{copied} {SkippedMessage}";
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
