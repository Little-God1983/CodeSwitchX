using CodeSwitchX.Core.GitHub;
using CodeSwitchX.UI.GitHub;
using CodeSwitchX.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    private readonly Action<GitHubListViewModel> _closed;
    private bool _isClosed;

    [ObservableProperty] private string _heading;
    [ObservableProperty] private IReadOnlyList<GitHubRow> _rows;
    [ObservableProperty] private string? _message;

    public GitHubListViewModel(GitHubPill pill, GitHubReader reader, GitHubActivity activity, GitHubBrowsers browsers, IUiDispatcher ui,
        TimeProvider time, Action<GitHubListViewModel> closed)
    {
        _pill = pill;
        _reader = reader;
        _activity = activity;
        _browsers = browsers;
        _ui = ui;
        _time = time;
        _closed = closed;
        _heading = HeadingFor(pill.Count);
        _rows = RowsOf(pill.Kind == GitHubItemKind.Issue ? pill.Activity.NewestIssues : pill.Activity.NewestPullRequests);
        // Worked out once: the browser's name is read from the registry and files, which a binding would repeat.
        AccountLine = $"Read with {pill.Activity.Account}. Opens in {browsers.NameFor(pill.Activity.Account)}.";
    }

    public string AccountLine { get; }

    /// <summary>Reads the full list. The tile's count follows it even when the panel was closed meanwhile; the panel does not.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var activity = _pill.Activity;
        var all = await _reader.ListAsync(activity.Repo, activity.Account, _pill.Kind, ct).ConfigureAwait(false);
        if (all is not null)
        {
            _activity.Replace(activity.Repo, _pill.Kind, all);
        }

        _ui.Post(() => Show(all));
    }

    private void Show(IReadOnlyList<GitHubItem>? all)
    {
        if (_isClosed)
        {
            return;
        }

        if (all is null)
        {
            Message = "Couldn't load the latest list. Showing what was last read.";
            return;
        }

        Heading = HeadingFor(all.Count);
        Rows = RowsOf(all);
        Message = null;
    }

    private string HeadingFor(int count)
    {
        var noun = _pill.Kind == GitHubItemKind.Issue ? "issue" : "pull request";
        return $"{_pill.Activity.Repo.Name}: {count} open {noun}{(count == 1 ? "" : "s")}";
    }

    private IReadOnlyList<GitHubRow> RowsOf(IReadOnlyList<GitHubItem> items)
    {
        var now = _time.GetUtcNow();
        return items.Select(item => GitHubRow.From(item, _pill.Activity.Account, now)).ToList();
    }

    [RelayCommand]
    private void Close()
    {
        _isClosed = true;
        _closed(this);
    }

    /// <summary>Opens the item and keeps the panel open; a browser that cannot open it says why in the panel.</summary>
    [RelayCommand]
    private void OpenRow(GitHubRow row)
    {
        if (_browsers.Open(_pill.Activity.Account, row.Item.Url) is { } line)
        {
            Message = line;
        }
    }
}
