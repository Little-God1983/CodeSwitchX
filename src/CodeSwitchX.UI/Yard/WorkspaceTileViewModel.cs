using System.Collections.ObjectModel;
using System.Diagnostics;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Telemetry;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeSwitchX.UI.Yard;

public sealed partial class WorkspaceTileViewModel : ObservableObject
{
    /// <summary>
    /// On a tile VS Code keeps no tab list for, stale chats fall off after this long; they come back as soon as the
    /// session shows activity again.
    /// </summary>
    public static readonly TimeSpan StaleRowLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How much before a chat's end VS Code may have written its tabs and still be read as listing them after it: the end
    /// is heard a moment after VS Code, closing, wrote the tabs it comes back with.
    /// </summary>
    internal static readonly TimeSpan TabListSlack = TimeSpan.FromSeconds(30);

    /// <summary>An ended chat that shows no more is forgotten after this long: longer than a closed chat is ever kept.</summary>
    internal static readonly TimeSpan ForgetEndedAfter = TimeSpan.FromHours(1);

    /// <summary>Every chat the engine put on this tile, shown or not: what shows changes with the tabs, the settings and the time.</summary>
    private readonly Dictionary<string, SessionSnapshot> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The chat tabs of the workspace's VS Code window (#164); null when VS Code keeps no list for it.</summary>
    private OpenChatTabs? _tabs;

    /// <summary>What is on disk of the chats of tabs the app knows no chat of; one that is missing has nothing there yet.</summary>
    private IReadOnlyDictionary<string, TabConversation> _tabActivity = new Dictionary<string, TabConversation>();

    /// <summary>The chats whose Claude Code runs in a VS Code tab right now: a tab the app knows no chat of is idle then, not ended.</summary>
    private IReadOnlySet<string> _runningTabs = new HashSet<string>();

    private PricingTable? _pricing;

    private readonly YardViewModel _owner;

    [ObservableProperty] private HostState _hostState = HostState.NotStarted;
    [ObservableProperty] private bool _needsAttention;
    [ObservableProperty] private bool _hasInferredChats;

    /// <summary>Lit for a moment after Raven's digest card asked for this tile: the view brings it into view and lights its border.</summary>
    [ObservableProperty] private bool _isSpotlit;

    /// <summary>The tile's git lines: one per repository its folders are in, the root folder's first; see <see cref="GitLine"/>.</summary>
    [ObservableProperty] private IReadOnlyList<GitLine> _gitLines = [GitLine.Unknown];

    public WorkspaceTileViewModel(Workspace workspace, YardViewModel owner)
    {
        Workspace = workspace;
        _owner = owner;
    }

    public Workspace Workspace { get; }
    public Guid Id => Workspace.Id;
    public string Name => Workspace.Name;

    /// <summary>The workspace's number, in the tile's upper left corner: what the user says and presses for it.</summary>
    public int Number => Workspace.Number;
    public string AccentColor => Workspace.AccentColor;
    public string RootPath => Workspace.RootPath;
    public ObservableCollection<ChatRowViewModel> Chats { get; } = [];

    /// <summary>
    /// Shows a git round's lines. The same lines again change nothing: a new list would rebuild every line on the tile each
    /// round, and close the tooltip the user has open.
    /// </summary>
    public void ShowGit(IReadOnlyList<GitLine> lines)
    {
        if (!GitLines.SequenceEqual(lines))
        {
            GitLines = lines;
        }
    }

    /// <summary>0 = waiting on the user, 1 = working, 2 = everything else. Used by "Needs me first".</summary>
    public int AttentionRank => NeedsAttention ? 0 : Chats.Any(c => c.State == SessionState.Working) ? 1 : 2;

    /// <summary>
    /// Takes the chat's latest state and shows what the tile shows then (<see cref="Arrange"/>). A chat that runs again
    /// after it was closed on purpose is forgotten no more.
    /// </summary>
    public void Upsert(SessionSnapshot snapshot, PricingTable pricing)
    {
        _pricing = pricing;
        if (!_sessions.TryGetValue(snapshot.SessionId, out var known) || snapshot.Version >= known.Version)
        {
            _sessions[snapshot.SessionId] = snapshot;
        }

        Chats.FirstOrDefault(c => Same(c.SessionId, snapshot.SessionId))?.Update(snapshot, pricing);
        Arrange(_owner.Now);
    }

    /// <summary>The chat is another tile's now, or no tile's: this one knows it no more.</summary>
    public void Remove(string sessionId)
    {
        // Its row goes with it: one kept for a tab of it here would show the chat's last state for good.
        var row = Chats.FirstOrDefault(c => Same(c.SessionId, sessionId));
        if (_sessions.Remove(sessionId) | (row is not null && Chats.Remove(row)))
        {
            Arrange(_owner.Now);
        }
    }

    /// <summary>Whether the engine put this chat on this tile.</summary>
    public bool Knows(string sessionId) => _sessions.ContainsKey(sessionId);

    /// <summary>
    /// The chat tabs VS Code lists for the workspace, null when it keeps no list; with when the chats of tabs the app
    /// knows no chat of were last written in.
    /// </summary>
    /// <param name="running">The chats whose Claude Code runs in a VS Code tab right now.</param>
    public void ShowTabs(OpenChatTabs? tabs, IReadOnlyDictionary<string, TabConversation> lastActivity, IReadOnlySet<string>? running = null)
    {
        _tabs = tabs;
        _tabActivity = lastActivity;
        _runningTabs = running ?? new HashSet<string>();
        Arrange(_owner.Now);
    }

    public void Tick(DateTimeOffset now)
    {
        Arrange(now);
        foreach (var row in Chats)
        {
            row.Tick(now);
        }
    }

    /// <summary>
    /// Makes the rows what the tile shows now (#164): the chat tabs open in the workspace's VS Code window, and the chats
    /// that run. Rows keep their place; a new one goes last.
    /// </summary>
    private void Arrange(DateTimeOffset now)
    {
        // One whose tab is still listed is kept: forgotten, its tab would bring it back as a tab nothing is known of,
        // the idle time that hides it with it.
        foreach (var ended in _sessions.Values.Where(s => !SessionStateMachine.IsLive(s.State) && now - s.StateSince >= ForgetEndedAfter
                     && TabOf(s.SessionId) is null && !Shows(s, now)).ToList())
        {
            _sessions.Remove(ended.SessionId);
        }

        var shown = _sessions.Values.Where(s => Shows(s, now)).Select(s => s.SessionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tabsAlone = (_tabs?.Tabs ?? []).Where(t => !_sessions.ContainsKey(t.SessionId) && !_owner.ClosedOnPurpose(t.SessionId)
            && !_owner.OnAnotherTile(this, t.SessionId) && !IdleTooLong(t, now)).ToList();
        foreach (var row in Chats.Where(c => !shown.Contains(c.SessionId) && !tabsAlone.Exists(t => Same(t.SessionId, c.SessionId))).ToList())
        {
            Chats.Remove(row);
        }

        foreach (var id in shown)
        {
            var session = _sessions[id];
            var row = Chats.FirstOrDefault(c => Same(c.SessionId, id));
            if (row is null)
            {
                row = NewRow(id);
                row.Update(session, _pricing!);
                Chats.Add(row);
            }

            // Ended with its tab open: the tab does not run. One that crashed keeps its red dot.
            row.ShowTab(TabOf(id), notRunning: session.State == SessionState.Ended && InTab(session));
        }

        foreach (var tab in tabsAlone)
        {
            var row = Chats.FirstOrDefault(c => Same(c.SessionId, tab.SessionId));
            if (row is null)
            {
                Chats.Add(row = NewRow(tab.SessionId));
            }

            row.ShowTab(tab, notRunning: !_runningTabs.Contains(tab.SessionId), _tabActivity.GetValueOrDefault(tab.SessionId), _tabs!.WrittenAt);
        }

        Recompute();
    }

    private ChatRowViewModel NewRow(string sessionId) =>
        new(sessionId, id => _owner.RequestOpenChat(Id, id)) { VoiceLabel = _owner.VoiceLabelOf(sessionId) };

    /// <summary>
    /// Whether the chat has a row. A chat with a tab in the workspace's VS Code window has one, whatever it said so far. One
    /// without shows while it runs (its tab is not written down yet, or it runs outside a tab), and after that for as long
    /// as the user keeps closed chats. A session that is no chat (<see cref="SessionSnapshot.ShowsAsChat"/>) and has no tab
    /// gets none: Claude Code opens and closes such a session each time a VS Code window loads. A chat idle longer than
    /// the user set is hidden, tab or not.
    /// </summary>
    private bool Shows(SessionSnapshot session, DateTimeOffset now)
    {
        if (session.State is not (SessionState.Working or SessionState.Waiting) && _owner.HideIdleAfter is { } idle && now - session.LastEventAt >= idle)
        {
            return false;
        }

        if (InTab(session))
        {
            return true;
        }

        if (!session.ShowsAsChat)
        {
            return false;
        }

        // Without a tab it runs elsewhere (VS Code's side bar, a terminal) or its tab is not written down yet: shown while
        // it reports, and as long as before once it went stale. Ended, it is closed.
        return SessionStateMachine.IsLive(session.State)
            ? session.State != SessionState.Stale || now - session.StateSince < StaleRowLifetime
            : now - session.StateSince < _owner.KeepClosed;
    }

    /// <summary>
    /// Whether the chat's tab is open. A chat that ended while VS Code runs is in the list only if VS Code wrote the list
    /// after: one written before still has the tab that was just closed. A VS Code that does not run wrote its last list
    /// as it closed, and comes back with those tabs. A chat whose Claude Code went without saying it ends (it crashed)
    /// left its tab open: closing a tab is said.
    /// </summary>
    private bool InTab(SessionSnapshot session) =>
        _tabs is { } tabs && TabOf(session.SessionId) is not null
        && (session.State != SessionState.Ended || HostState != HostState.Running || tabs.WrittenAt >= session.StateSince - TabListSlack);

    private OpenChatTab? TabOf(string sessionId) => _tabs?.Tabs.FirstOrDefault(t => Same(t.SessionId, sessionId));

    private bool IdleTooLong(OpenChatTab tab, DateTimeOffset now) =>
        _owner.HideIdleAfter is { } idle && _tabActivity.TryGetValue(tab.SessionId, out var last) && now - last.WrittenAt >= idle;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void Recompute()
    {
        NeedsAttention = Chats.Any(c => c.NeedsUser);
        HasInferredChats = Chats.Any(c => c.Inferred && c.IsLive);
        OnPropertyChanged(nameof(AttentionRank));
    }

    [RelayCommand]
    private void Open() => _owner.RequestOpen(Id);

    [RelayCommand]
    private void RevealInExplorer() => TryStart("explorer.exe", $"\"{RootPath}\"");

    [RelayCommand]
    private void OpenTerminal()
    {
        if (!TryStart("wt.exe", $"-d \"{RootPath}\""))
        {
            TryStart("cmd.exe", $"/K cd /d \"{RootPath}\"");
        }
    }

    [RelayCommand]
    private Task UnregisterAsync() => _owner.UnregisterAsync(Id);

    private static bool TryStart(string file, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
