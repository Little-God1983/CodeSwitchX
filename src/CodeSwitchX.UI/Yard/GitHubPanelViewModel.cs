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
    private readonly PanelCheckout? _checkout;
    // Ends a read a tab starts later, when the panel has closed by then. No timer and no wait handle is taken from it.
    private readonly CancellationTokenSource _stop = new();
    // The tab the panel opened on, read by LoadAsync; the other is read when it is first shown.
    private readonly GitHubListViewModel _first;
    private ITimer? _copyMessageTimer;
    private int _copies;

    /// <summary>The tab in front.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyAllLinksCommand))]
    private GitHubListViewModel _shown;

    /// <summary>Why the browser could not open the repository, a tab's page or a row, or none; any of them that opens clears it.</summary>
    [ObservableProperty] private string? _openMessage;

    /// <summary>What the last copy did, for a moment. It has a place of its own in the header, so the rows do not move under the pointer.</summary>
    [ObservableProperty] private string? _copyMessage;

    /// <summary>How long <see cref="CopyMessage"/> stays.</summary>
    internal static readonly TimeSpan CopyMessageLifetime = TimeSpan.FromSeconds(4);

    /// <summary>The checkout's branch: as the line has it (the Yard keeps it up to date, <see cref="FollowBranch"/>), and the default branch once a pull has gone through.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PullText))]
    [NotifyPropertyChangedFor(nameof(PullAutomationName))]
    private string? _branch;

    /// <summary>A pull or a merge of the checkout runs, from this panel or an earlier one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PullText))]
    [NotifyPropertyChangedFor(nameof(PullAutomationName))]
    private bool _isBusy;

    /// <summary>The strip a row's Merge button opened (#408); null when none is open. One at a time.</summary>
    [ObservableProperty] private MergeStrip? _mergeStrip;

    /// <summary>What the last pull or merge came to, or why it was refused; none before the first press.</summary>
    [ObservableProperty] private string? _actionMessage;

    /// <summary><see cref="ActionMessage"/> is a refusal or a failure, not progress or success.</summary>
    [ObservableProperty] private bool _isActionFailed;

    public GitHubPanelViewModel(RepoActivity repo, GitHubItemKind shown, GitHubReader reader, GitHubActivity activity, GitHubBrowsers browsers,
        ITextClipboard clipboard, IUiDispatcher ui, TimeProvider time, ILogger logger, Action<GitHubPanelViewModel> closed, PanelCheckout? checkout = null)
    {
        _checkout = checkout;
        _branch = checkout?.Branch;
        _changedFiles = checkout?.ChangedFiles;
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
        _first.IsShown = true;
        Tabs = [PullRequests, Issues];
        PullRequests.PropertyChanged += OnListChanged;
        Issues.PropertyChanged += OnListChanged;
        // Worked out once: the browser's name is read from the registry and files, which a binding would repeat.
        AccountLine = $"Read with {repo.Account}. Opens in {browsers.NameFor(repo.Account)}.";
        if (checkout is not null)
        {
            checkout.Jobs.Changed += OnJobChanged;
            // A job started from an earlier panel of this line: still running, or ended while no panel was open (#407). Its branch is not
            // taken from such an outcome: the line, read again since, knows better.
            ShowJob(opening: true);
        }
    }

    /// <summary>
    /// The button above the tabs (#407): "Switch to main and pull", or "Pull main" when the checkout is on it, with the branch GitHub calls
    /// the default; what runs ("Pulling main…", "Merging #405…") while a job of the checkout runs. Null, and no button, when the panel has no
    /// checkout or the repository has no default branch.
    /// </summary>
    public string? PullText => _checkout is null || _repo.DefaultBranch is not { } main ? null
        : IsBusy && _checkout.Jobs.RunningText(_checkout.Key) is { } running ? running
        : Branch == main ? $"Pull {main}"
        : $"Switch to {main} and pull";

    /// <summary>A screen reader hears what the button does and where.</summary>
    public string? PullAutomationName => PullText is { } text ? $"{text} in {_repo.Repo.Name}" : null;

    public GitHubListViewModel PullRequests { get; }

    public GitHubListViewModel Issues { get; }

    /// <summary>The two tabs in their order, for the one tab template (#410).</summary>
    public IReadOnlyList<GitHubListViewModel> Tabs { get; }

    partial void OnShownChanged(GitHubListViewModel value)
    {
        PullRequests.IsShown = ReferenceEquals(value, PullRequests);
        Issues.IsShown = ReferenceEquals(value, Issues);
    }

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

    /// <summary>The checkout's folder, for the Yard to keep <see cref="Branch"/> up to date; null when the panel has none.</summary>
    public string? CheckoutPath => _checkout?.Path;

    /// <summary>Files with uncommitted changes in the checkout as last read (#429); null when git could not tell. A pull stops at them.</summary>
    private int? _changedFiles;

    /// <summary>The line was read again: the button names what a press does now, not what it did when the panel opened.</summary>
    /// <param name="changedFiles">The line's files with uncommitted changes; null when git could not tell.</param>
    public void FollowBranch(string? branch, int? changedFiles = null)
    {
        // An open strip worked out its choices from the branch the checkout was on (review of #408), and whether it could pull (#429).
        if (branch != Branch || (changedFiles is > 0) != (_changedFiles is > 0))
        {
            MergeStrip = null;
        }

        _changedFiles = changedFiles;
        Branch = branch;
    }

    /// <summary>
    /// Switches the line's checkout to the default branch and pulls (#407), in the Yard's jobs, so closing the panel lets it finish. A chat
    /// working in the workspace refuses it: the Yard never switches a branch under a working chat. A press while a pull runs does nothing;
    /// the button stays enabled for it, as Refresh does: WPF would drop the keyboard to the window from a button it disables while focused.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPull))]
    private void Pull()
    {
        if (_checkout is null || _repo.DefaultBranch is not { } main || IsBusy)
        {
            return;
        }

        if (_checkout.ChatIsWorking())
        {
            ShowAction("A chat is working here. Pull when it is done.", failed: true);
            return;
        }

        ActionMessage = null;
        // An open strip worked out its choices from the branch the pull is about to change (review of #408).
        MergeStrip = null;
        _ = _checkout.Jobs.PullAsync(_checkout.Path, main, _repo.Repo.Name, _checkout.ChatIsWorking, _checkout.Key, _checkout.Tree);
    }

    private bool CanPull() => _checkout is not null && _repo.DefaultBranch is not null;

    /// <summary>Pull request rows have a Merge button (#408): there is a checkout to say what happens to, and the Yard can merge.</summary>
    public bool CanMerge => _checkout is not null && _checkout.Jobs.CanMerge;

    /// <summary>
    /// A row's Merge button (#408): opens the strip under the row, and does nothing else. Merge and pull is offered when it merges into the
    /// default branch, the workspace is on the pull request's branch (not a fork's) or on the default branch itself (#426), and no chat is
    /// working here. A press while a job of the checkout runs does nothing, as Pull does.
    /// </summary>
    [RelayCommand]
    private void OpenMerge(GitHubRow row)
    {
        if (_checkout is null || !CanMerge || IsBusy)
        {
            return;
        }

        // While the list is read again after a refused move, the rows still hold the old head: a Merge now would be refused again (review of #414).
        if (PullRequests.IsRereading)
        {
            ShowAction("Reading the pull requests again. Merge when the list is there.", failed: false);
            return;
        }

        var item = row.Item;
        var main = _repo.DefaultBranch;
        var into = item.BaseBranch ?? main ?? "its base branch";
        var onBranch = !item.IsCrossRepository && item.Branch is not null && item.Branch == Branch;
        // The workspace is on the default branch and the merge lands there (#426): the merge alone would leave it behind GitHub, with
        // nothing on the tile to say so. Wherever the pull request comes from, a fork too.
        var onBase = main is not null && into == main && Branch == main;
        var pullable = onBase || (onBranch && main is not null && into == main);
        var busy = pullable && _checkout.ChatIsWorking();
        // A pull stops at uncommitted changes, and would leave the branch behind after promising otherwise (#429): said here, before the merge.
        var changed = pullable && !busy && _changedFiles is > 0 ? (_changedFiles == 1 ? "1 changed file" : $"{_changedFiles} changed files") : null;
        var stays = Branch is { } branch ? $"This workspace stays on {branch}." : "This workspace stays as it is.";
        var line = onBase ? (busy ? "A chat is working here, so the pull is left out."
                : changed is not null ? $"This workspace is on {main} with {changed}, so the pull is left out."
                : $"This workspace is on {main}. Merge and pull brings it up to date.")
            : !onBranch ? (item.IsCrossRepository ? $"It comes from a fork. {stays}" : stays)
            : busy ? "A chat is working here, so the switch is left out."
            : changed is not null ? $"This workspace is on its branch with {changed}, so the switch is left out."
            : pullable ? $"This workspace is on its branch. Merge and pull switches it to {main} and pulls."
            : $"It merges into {into}, not {main ?? "the default branch"}. {stays}";
        MergeStrip = new MergeStrip(row, $"Merge {row.NumberText} into {into}?", line, pullable && !busy && changed is null, pullable ? "Merge only" : "Merge", into);
        ActionMessage = null;
    }

    /// <summary>The merge, then the switch to the default branch and the pull (#408).</summary>
    [RelayCommand]
    private void MergeAndPull() => Merge(pull: true);

    /// <summary>The merge alone: the checkout stays as it is.</summary>
    [RelayCommand]
    private void MergeOnly() => Merge(pull: false);

    [RelayCommand]
    private void CancelMerge() => MergeStrip = null;

    /// <summary>The head the panel holds for the pull request now: the full list may have replaced the row the strip opened on.</summary>
    private string? HeadNow(GitHubRow row) =>
        PullRequests.Rows.FirstOrDefault(r => r.Item.Number == row.Item.Number)?.Item.HeadSha ?? row.Item.HeadSha;

    private void Merge(bool pull)
    {
        if (_checkout is null || MergeStrip is not { } strip || IsBusy)
        {
            return;
        }

        MergeStrip = null;
        // The merge goes for the head the strip was opened on, what the user looked at (#414). A list that has read another head since says
        // the pull request moved: refused here, as GitHub would refuse it, without merging a commit nobody looked at (review of #414).
        var opened = strip.Row.Item.HeadSha;
        if (opened is not null && HeadNow(strip.Row) is { } now && now != opened)
        {
            ShowAction($"Can't merge {strip.Row.NumberText}: it changed since you opened Merge. Look at it, then merge again.", failed: true);
            return;
        }

        ActionMessage = null;
        _ = _checkout.Jobs.MergeAsync(_checkout.Path, _repo.Repo.Name, _repo.Repo, _repo.Account, strip.Row.Item.Number, strip.BaseBranch,
            pull && strip.OffersPull ? _repo.DefaultBranch : null, _checkout.ChatIsWorking, _checkout.Key, _checkout.Tree, opened,
            onPullBranch: Branch == _repo.DefaultBranch);
    }

    private void OnJobChanged(string key)
    {
        if (_checkout is not null && key == _checkout.Key)
        {
            ShowJob(opening: false);
        }
    }

    /// <summary>The checkout's job as it is now: running, or its outcome, which this panel takes so no other shows it again.</summary>
    private void ShowJob(bool opening)
    {
        var jobs = _checkout!.Jobs;
        var busy = jobs.IsRunning(_checkout.Key);
        if (busy)
        {
            // A job from another panel of the line: the strip's choices may not hold when it ends.
            MergeStrip = null;
        }

        // The outcome first, then the button back: the panel is never idle without saying what the job came to.
        if (!busy && jobs.TakeOutcome(_checkout.Key, _checkout.Tree) is { } outcome)
        {
            ShowAction(outcome.Message, failed: !outcome.Succeeded);
            if (outcome.Pulled && !opening)
            {
                Branch = _repo.DefaultBranch;
            }

            // A pull request that moved since it was read: the list is read again, so the next Merge goes for its new head (#414).
            // Only in a panel that was open for it, and only a tab read before: a panel opened later, and a tab shown for the first time, read
            // their lists afresh anyway, and a tab not looked at costs no read (#406).
            if (outcome.Reread && !opening && PullRequests.HasStarted)
            {
                _ = PullRequests.RereadAsync(_stop.Token);
            }

            // The merged row leaves the list at once; the Yard reads the repository again for the counts and pills.
            if (outcome.Merged is { } number)
            {
                PullRequests.Drop(number);
            }
        }

        IsBusy = busy;
    }

    private void ShowAction(string message, bool failed)
    {
        ActionMessage = message;
        IsActionFailed = failed;
    }

    [RelayCommand]
    private void Close()
    {
        if (_checkout is not null)
        {
            _checkout.Jobs.Changed -= OnJobChanged;
        }

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
