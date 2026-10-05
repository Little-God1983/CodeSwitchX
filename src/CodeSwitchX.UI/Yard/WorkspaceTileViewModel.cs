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

    /// <summary>The chats closed on purpose: their tab, still in a list VS Code has not written again, brings no row back.</summary>
    private readonly HashSet<string> _forgotten = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The chat tabs of the workspace's VS Code window (#164); null when VS Code keeps no list for it.</summary>
    private OpenChatTabs? _tabs;

    /// <summary>When the chats of tabs the app knows no chat of were last written in; one that is missing is not known.</summary>
    private IReadOnlyDictionary<string, DateTimeOffset> _tabActivity = new Dictionary<string, DateTimeOffset>();

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
        _forgotten.Remove(snapshot.SessionId);
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
        if (_sessions.Remove(sessionId) | Chats.Any(c => Same(c.SessionId, sessionId)))
        {
            Arrange(_owner.Now);
        }
    }

    /// <summary>The chat was closed on purpose: it leaves the tile now, and its tab brings no row back until it runs again.</summary>
    public void Forget(string sessionId)
    {
        _forgotten.Add(sessionId);
        Remove(sessionId);
    }

    /// <summary>Whether the engine put this chat on this tile.</summary>
    public bool Knows(string sessionId) => _sessions.ContainsKey(sessionId);

    /// <summary>
    /// The chat tabs VS Code lists for the workspace, null when it keeps no list; with when the chats of tabs the app
    /// knows no chat of were last written in.
    /// </summary>
    public void ShowTabs(OpenChatTabs? tabs, IReadOnlyDictionary<string, DateTimeOffset> lastActivity)
    {
        _tabs = tabs;
        _tabActivity = lastActivity;
        // A chat closed on purpose is remembered until VS Code has written its tabs without it.
        _forgotten.RemoveWhere(id => TabOf(id) is null);
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
        foreach (var ended in _sessions.Values.Where(s => !SessionStateMachine.IsLive(s.State) && now - s.StateSince >= ForgetEndedAfter && !Shows(s, now)).ToList())
        {
            _sessions.Remove(ended.SessionId);
        }

        var shown = _sessions.Values.Where(s => Shows(s, now)).Select(s => s.SessionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tabsAlone = (_tabs?.Tabs ?? []).Where(t => !_sessions.ContainsKey(t.SessionId) && !_forgotten.Contains(t.SessionId) && !IdleTooLong(t, now)).ToList();
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

            row.ShowTab(TabOf(id), notRunning: !SessionStateMachine.IsLive(session.State) && InTab(session));
        }

        foreach (var tab in tabsAlone)
        {
            var row = Chats.FirstOrDefault(c => Same(c.SessionId, tab.SessionId));
            if (row is null)
            {
                Chats.Add(row = NewRow(tab.SessionId));
            }

            row.ShowTab(tab, notRunning: true);
        }

        Recompute();
    }

    private ChatRowViewModel NewRow(string sessionId) => new(sessionId, id => _owner.RequestOpenChat(Id, id));

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

        if (_tabs is null)
        {
            // VS Code keeps no list for this workspace: by what the chats report alone.
            return SessionStateMachine.IsLive(session.State)
                ? session.State != SessionState.Stale || now - session.StateSince < StaleRowLifetime
                : now - session.StateSince < _owner.KeepClosed;
        }

        // Without a tab, a chat quiet long enough to be stale has none any more: closed, as one that ended.
        return session.State is SessionState.Starting or SessionState.Idle or SessionState.Working or SessionState.Waiting
            || now - session.StateSince < _owner.KeepClosed;
    }

    /// <summary>
    /// Whether the chat's tab is open. A chat that ended while VS Code runs is in the list only if VS Code wrote the list
    /// after: one written before still has the tab that was just closed. A VS Code that does not run wrote its last list
    /// as it closed, and comes back with those tabs.
    /// </summary>
    private bool InTab(SessionSnapshot session) =>
        _tabs is { } tabs && TabOf(session.SessionId) is not null
        && (SessionStateMachine.IsLive(session.State) || HostState != HostState.Running || tabs.WrittenAt >= session.StateSince - TabListSlack);

    private OpenChatTab? TabOf(string sessionId) => _tabs?.Tabs.FirstOrDefault(t => Same(t.SessionId, sessionId));

    private bool IdleTooLong(OpenChatTab tab, DateTimeOffset now) =>
        _owner.HideIdleAfter is { } idle && _tabActivity.TryGetValue(tab.SessionId, out var last) && now - last >= idle;

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
