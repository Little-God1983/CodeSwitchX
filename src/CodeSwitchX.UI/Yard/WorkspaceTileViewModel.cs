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

    [ObservableProperty] private string? _branch;
    [ObservableProperty] private int _dirtyCount;
    [ObservableProperty] private HostState _hostState = HostState.NotStarted;
    [ObservableProperty] private bool _needsAttention;
    [ObservableProperty] private bool _hasInferredChats;

    public WorkspaceTileViewModel(Workspace workspace, YardViewModel owner)
    {
        Workspace = workspace;
        _owner = owner;
    }

    public Workspace Workspace { get; }
    public Guid Id => Workspace.Id;
    public string Name => Workspace.Name;
    public string AccentColor => Workspace.AccentColor;
    public string RootPath => Workspace.RootPath;
    public ObservableCollection<ChatRowViewModel> Chats { get; } = [];

    /// <summary>0 = waiting on the user, 1 = working, 2 = everything else. Used by "Needs me first".</summary>
    public int AttentionRank => NeedsAttention ? 0 : Chats.Any(c => c.State == SessionState.Working) ? 1 : 2;

    public void Upsert(SessionSnapshot snapshot, PricingTable pricing)
    {
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
