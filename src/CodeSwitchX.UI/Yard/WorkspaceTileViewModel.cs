using System.Collections.ObjectModel;
using System.Diagnostics;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Telemetry;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeSwitchX.UI.Yard;

public sealed partial class WorkspaceTileViewModel : ObservableObject
{
    public static readonly TimeSpan EndedRowLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Stale chats fall off the tile after this long; they come back as soon as the session shows activity again.</summary>
    public static readonly TimeSpan StaleRowLifetime = TimeSpan.FromMinutes(30);

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
    /// Shows or updates the chat's row. A session that is no chat (<see cref="SessionSnapshot.ShowsAsChat"/>) gets no row,
    /// and loses the one it had when it goes back to idling or ends without ever having held a conversation, rather than
    /// lingering for <see cref="EndedRowLifetime"/>. Claude Code opens and closes such a session each time a VS Code window loads.
    /// </summary>
    public void Upsert(SessionSnapshot snapshot, PricingTable pricing)
    {
        if (!snapshot.ShowsAsChat)
        {
            Remove(snapshot.SessionId);
            return;
        }

        var row = Chats.FirstOrDefault(c => c.SessionId == snapshot.SessionId);
        if (row is null)
        {
            row = new ChatRowViewModel(snapshot.SessionId);
            Chats.Add(row);
        }

        row.Update(snapshot, pricing);
        Recompute();
    }

    public void Remove(string sessionId)
    {
        var row = Chats.FirstOrDefault(c => c.SessionId == sessionId);
        if (row is not null)
        {
            Chats.Remove(row);
            Recompute();
        }
    }

    public void Tick(DateTimeOffset now)
    {
        foreach (var row in Chats.Where(c => (!c.IsLive && now - c.StateSince >= EndedRowLifetime)
                                          || (c.State == SessionState.Stale && now - c.StateSince >= StaleRowLifetime)).ToList())
        {
            Chats.Remove(row);
        }

        foreach (var row in Chats)
        {
            row.Tick(now);
        }

        Recompute();
    }

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
