using System.Collections.ObjectModel;
using System.Diagnostics;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Raven;
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
    /// How long after a chat's end VS Code must have written its tabs for a tab still in them to be open: as a tab closes,
    /// VS Code may write a list that still has it (seen on screen, a third of a second after). One written well after is
    /// VS Code's own, after a Reload Window ended every chat and brought their tabs back.
    /// </summary>
    internal static readonly TimeSpan ListedAfterEnd = TimeSpan.FromSeconds(5);

    /// <summary>An ended chat that shows no more is forgotten after this long: longer than a closed chat is ever kept.</summary>
    internal static readonly TimeSpan ForgetEndedAfter = TimeSpan.FromHours(1);

    /// <summary>When the workspace's VS Code was last seen to run (UI thread); null while it does not.</summary>
    private DateTimeOffset? _runningSince;

    /// <summary>Every chat the engine put on this tile, shown or not: what shows changes with the tabs, the settings and the time.</summary>
    private readonly Dictionary<string, SessionSnapshot> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The chat tabs of the workspace's VS Code window (#164); null when VS Code keeps no list for it.</summary>
    private OpenChatTabs? _tabs;

    /// <summary>What is on disk of the chats of tabs the app knows no chat of; one that is missing has nothing there yet.</summary>
    private IReadOnlyDictionary<string, TabConversation> _tabActivity = new Dictionary<string, TabConversation>();

    /// <summary>
    /// The chats whose Claude Code runs in a VS Code tab right now, with whether the tab waits on the user: a tab the app
    /// knows no chat of is idle or waiting then, not ended.
    /// </summary>
    private IReadOnlyDictionary<string, bool> _runningTabs = new Dictionary<string, bool>();

    private PricingTable? _pricing;

    private readonly YardViewModel _owner;

    [ObservableProperty] private HostState _hostState = HostState.NotStarted;
    [ObservableProperty] private bool _needsAttention;
    [ObservableProperty] private bool _hasInferredChats;

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

    /// <summary>A chat of the window works (#182): Raven's chat list marks the window's chat with it. Summed up in <see cref="Recompute"/>.</summary>
    [ObservableProperty]
    private bool _isWorking;

    /// <summary>
    /// When a chat of the window last did something (#276), null while none was seen: what puts the tile in its group of
    /// the Yard. It only moves on: a chat the tile forgets (ended an hour ago) leaves it as it was.
    /// </summary>
    [ObservableProperty]
    private DateTimeOffset? _lastUsedAt;

    /// <summary>A chat of the window did something at <paramref name="at"/>; an earlier time than the one known changes nothing.</summary>
    public void SawUse(DateTimeOffset at)
    {
        if (LastUsedAt is not { } known || at > known)
        {
            LastUsedAt = at;
        }
    }

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

    /// <summary>Where a chat is in its turn (#287); a chat with neither has no turn running and none unseen.</summary>
    private enum Turn
    {
        /// <summary>From working, through any wait for the user, until a hook tells the turn's end.</summary>
        Running,

        /// <summary>The turn ended while the Cab did not show the workspace: the user has not seen it.</summary>
        Finished,
    }

    /// <summary>
    /// Each chat's turn (#287). A finished one is taken off by the chat's next turn, its end or its leaving the tile;
    /// opening the workspace takes every finished one. Not stored: after a restart no tile is finished.
    /// </summary>
    private readonly Dictionary<string, Turn> _turns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A chat of the window finished a turn the user has not seen (#287): the tile shimmers. With a chat that asks and one
    /// that works, the window shows all three at once: each chat adds its own sign.
    /// </summary>
    [ObservableProperty]
    private bool _isFinished;

    /// <summary>The Cab shows the workspace (#287): what finished there is seen.</summary>
    public void Seen()
    {
        foreach (var id in _turns.Where(t => t.Value == Turn.Finished).Select(t => t.Key).ToList())
        {
            _turns.Remove(id);
        }

        Recompute();
    }

    /// <summary>0 = waiting on the user, 1 = working, 2 = everything else. Used by "Needs me first".</summary>
    public int AttentionRank => NeedsAttention ? 0 : IsWorking ? 1 : 2;

    /// <summary>A chat that works: what ranks a tile second under "Needs me first", and marks its Raven chat (#182).</summary>
    private static bool Works(ChatRowViewModel chat) => chat.State == SessionState.Working;

    /// <summary>
    /// Takes the chat's latest state and shows what the tile shows then (<see cref="Arrange"/>). A chat that runs again
    /// after it was closed on purpose is forgotten no more.
    /// </summary>
    public void Upsert(SessionSnapshot snapshot, PricingTable pricing)
    {
        _pricing = pricing;
        if (!_sessions.TryGetValue(snapshot.SessionId, out var known) || snapshot.Version >= known.Version)
        {
            TrackTurn(snapshot);
            _sessions[snapshot.SessionId] = snapshot;
        }

        // The engine stamps a chat's use (a prompt, a tool, an ask) from its own events and stores it, so a restored chat
        // tells the same use as the stored read at startup (#278).
        if (snapshot.LastUseAt is { } used)
        {
            SawUse(used);
        }

        Chats.FirstOrDefault(c => Same(c.SessionId, snapshot.SessionId))?.Update(snapshot, pricing);
        Arrange(_owner.Now);
    }

    /// <summary>
    /// A chat whose turn ends finished it (#287), unseen unless the Cab shows the workspace. A turn runs from the chat's
    /// working through any wait for the user to idle: a permission denied, then Stop, ends it waiting. A chat that waits
    /// having held a conversation is in a turn too (one restored waiting); one that waits without (a window loading) is
    /// not. Only a hook tells the end: an idle that inference told (a quiet chat restored at startup) ends the turn
    /// unfinished, for a later hook that finds it idle may be a window's SessionStart as well as a Stop. A finished turn
    /// stays unseen through going stale and through the chat's end (a Reload Window brings its tab back); a turn still
    /// running when the chat ends or crashes has nothing finished to see.
    /// </summary>
    private void TrackTurn(SessionSnapshot now)
    {
        var id = now.SessionId;
        var running = _turns.TryGetValue(id, out var turn) && turn == Turn.Running;
        switch (now.State)
        {
            case SessionState.Working:
            case SessionState.Waiting when now.HeldConversation:
                _turns[id] = Turn.Running;
                break;
            case SessionState.Idle when running && now.HookSeen && !_owner.InCab(Id):
                _turns[id] = Turn.Finished;
                break;
            case SessionState.Idle or SessionState.Ended or SessionState.Errored when !running:
            case SessionState.Stale:
                break;
            default:
                _turns.Remove(id);
                break;
        }
    }

    /// <summary>The chat is another tile's now, or no tile's: this one knows it no more.</summary>
    public void Remove(string sessionId)
    {
        _turns.Remove(sessionId);
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
    /// <param name="running">The chats whose Claude Code runs in a VS Code tab right now, with whether the tab waits on the user.</param>
    public void ShowTabs(OpenChatTabs? tabs, IReadOnlyDictionary<string, TabConversation> lastActivity, IReadOnlyDictionary<string, bool>? running = null)
    {
        _tabs = tabs;
        _tabActivity = lastActivity;
        _runningTabs = running ?? new Dictionary<string, bool>();
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
            // A closed chat kept a while (Keep a closed chat) holds no tab: the one still listed was closed.
            var inTab = InTab(session);
            row.ShowTab(inTab ? TabOf(id) : null, notRunning: session.State == SessionState.Ended && inTab);
        }

        foreach (var tab in tabsAlone)
        {
            var row = Chats.FirstOrDefault(c => Same(c.SessionId, tab.SessionId));
            if (row is null)
            {
                Chats.Add(row = NewRow(tab.SessionId));
            }

            var runs = _runningTabs.TryGetValue(tab.SessionId, out var waits);
            row.ShowTab(tab, notRunning: !runs, _tabActivity.GetValueOrDefault(tab.SessionId), _tabs!.WrittenAt, waits);
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
    /// Whether the chat's tab is open. While VS Code runs, its list says nothing of tabs closed since: VS Code writes it when
    /// idle, which a window hidden in the Cab never is (seen on screen: minutes later a closed tab was still listed). So a
    /// chat that ended while VS Code ran had its tab closed, unless VS Code wrote it down again well after (a Reload Window
    /// ends every chat and brings their tabs back); one that ended before VS Code was seen to run is a tab it brought
    /// back. A VS Code that does not run wrote its list as it closed, and comes back with those tabs. A
    /// chat whose Claude Code went without saying it ends (it crashed) left its tab open: closing a tab is said.
    /// </summary>
    private bool InTab(SessionSnapshot session) =>
        _tabs is { } tabs && TabOf(session.SessionId) is not null
        && (session.State != SessionState.Ended || HostState != HostState.Running || session.StateSince < _runningSince
            || tabs.WrittenAt >= session.StateSince + ListedAfterEnd);

    /// <summary>
    /// VS Code started or stopped: what its list means changes with it (<see cref="InTab"/>). Seen to run since now: a chat
    /// that ended before is a tab VS Code brought back.
    /// </summary>
    partial void OnHostStateChanged(HostState oldValue, HostState newValue)
    {
        _runningSince = newValue == HostState.Running ? _owner.Now : null;
        if (newValue == HostState.Running)
        {
            Arrange(_owner.Now);
        }
        else
        {
            // Closed, VS Code wrote the tabs it comes back with: read before the tile changes, or a tab closed earlier,
            // still in the list read before, would show again for a moment. A window that ran and stopped writes it as it
            // goes, so that one is read again a moment later (#248); not one that starts again.
            _owner.RefreshTabsSoon(closed: oldValue == HostState.Running && newValue == HostState.Stopped);
        }
    }

    private OpenChatTab? TabOf(string sessionId) => _tabs?.Tabs.FirstOrDefault(t => Same(t.SessionId, sessionId));

    private bool IdleTooLong(OpenChatTab tab, DateTimeOffset now) =>
        _owner.HideIdleAfter is { } idle && _tabActivity.TryGetValue(tab.SessionId, out var last) && now - last.WrittenAt >= idle;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void Recompute()
    {
        NeedsAttention = Chats.Any(c => c.NeedsUser);
        HasInferredChats = Chats.Any(c => c.Inferred && c.IsLive);
        IsWorking = Chats.Any(Works);
        IsFinished = _turns.ContainsValue(Turn.Finished);
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
