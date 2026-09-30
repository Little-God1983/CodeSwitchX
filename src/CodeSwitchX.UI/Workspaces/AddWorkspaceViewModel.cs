using System.Collections.ObjectModel;
using System.IO;
using CodeSwitchX.Core.Persistence;
using Path = System.IO.Path;
using CodeSwitchX.Core.Workspaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Workspaces;

public sealed partial class AddWorkspaceViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> AccentColors =
    [
        "#3B82F6", "#22C55E", "#F59E0B", "#EF4444", "#8B5CF6", "#EC4899", "#14B8A6", "#64748B",
    ];

    private readonly WorkspaceProbe _probe;
    private readonly IWorkspaceStore _store;
    private readonly WorkspaceRegistry _registry;
    private readonly ILogger<AddWorkspaceViewModel> _logger;

    [ObservableProperty] private string _inputPath = string.Empty;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] private string _name = string.Empty;
    [ObservableProperty] private string _rootPath = string.Empty;
    [ObservableProperty] private string? _workspaceFile;
    [ObservableProperty] private bool _isGitRepository;
    [ObservableProperty] private string? _branch;
    [ObservableProperty] private bool _hasClaudeMd;
    [ObservableProperty] private string _solutionSummary = string.Empty;
    [ObservableProperty] private Track? _selectedTrack;
    [ObservableProperty] private string _newTrackName = string.Empty;
    [ObservableProperty] private string _selectedAccent = AccentColors[0];
    [ObservableProperty] private string? _vsCodeProfile;
    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool _isProbed;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] [NotifyCanExecuteChangedFor(nameof(ProbeCommand))] private bool _isBusy;

    public AddWorkspaceViewModel(WorkspaceProbe probe, IWorkspaceStore store, WorkspaceRegistry registry, ILogger<AddWorkspaceViewModel> logger)
    {
        _probe = probe;
        _store = store;
        _registry = registry;
        _logger = logger;
    }

    public ObservableCollection<Track> Tracks { get; } = [];
    public ObservableCollection<WorktreeInfo> Worktrees { get; } = [];
    public IReadOnlyList<string> Accents => AccentColors;

    public event Action<Workspace>? Saved;
    public event Action? Closed;

    public async Task LoadAsync(CancellationToken ct)
    {
        Tracks.Clear();
        foreach (var track in await _store.GetTracksAsync(ct))
        {
            Tracks.Add(track);
        }

        SelectedTrack ??= Tracks.FirstOrDefault();
    }

    /// <summary>
    /// The path can change while a detect runs: it is typed over, or Folder… and File… start a detect of their own
    /// (<c>Execute</c> does not check <c>CanExecute</c>), and an offline share takes about 20 s to fail. A detect fills
    /// the form or reports an error only for the path still in the box.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanProbe))]
    private async Task ProbeAsync()
    {
        var input = InputPath;
        bool IsCurrent() => InputPath == input;
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var result = await _probe.ProbeAsync(input.Trim().Trim('"'), CancellationToken.None);
            if (!IsCurrent())
            {
                return;
            }

            Name = result.SuggestedName;
            RootPath = result.RootPath;
            WorkspaceFile = result.WorkspaceFile;
            IsGitRepository = result.IsGitRepository;
            Branch = result.Branch;
            HasClaudeMd = result.HasClaudeMd;
            SolutionSummary = result.SolutionFiles.Count == 0 ? "no solution files" : string.Join(", ", result.SolutionFiles.Select(Path.GetFileName));
            Worktrees.Clear();
            foreach (var worktree in result.Worktrees)
            {
                Worktrees.Add(worktree);
            }

            IsProbed = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            if (IsCurrent())
            {
                IsProbed = false;
                ErrorMessage = ex.Message;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanProbe() => !IsBusy;

    /// <summary>The detected fields describe the probed folder; a changed path must be detected again before Add registers anything.</summary>
    partial void OnInputPathChanged(string value) => IsProbed = false;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            // Nothing deletes a track, so one stored for a folder that cannot be added would stay on the Yard for good.
            if (await _store.FindByTargetAsync(RootPath, WorkspaceFile, CancellationToken.None) is not null)
            {
                throw new DuplicateWorkspaceException(Workspace.TargetOf(RootPath, WorkspaceFile));
            }

            var track = !string.IsNullOrWhiteSpace(NewTrackName) ? await TrackNamedAsync(NewTrackName.Trim())
                : SelectedTrack ?? Tracks.FirstOrDefault() ?? await TrackNamedAsync("General");
            SelectedTrack = track;
            NewTrackName = string.Empty;

            var workspace = new Workspace
            {
                Name = Name.Trim(),
                RootPath = RootPath,
                WorkspaceFile = WorkspaceFile,
                TrackId = track.Id,
                AccentColor = SelectedAccent,
                HostMode = HostMode.Snap,
                VsCodeProfile = string.IsNullOrWhiteSpace(VsCodeProfile) ? null : VsCodeProfile.Trim(),
                AutoStart = AutoStart,
                Worktrees = Worktrees.Select(w => new Worktree { Path = w.Path, Branch = w.Branch }).ToList(),
            };

            await _registry.RegisterAsync(workspace, CancellationToken.None);
            Saved?.Invoke(workspace);
            Closed?.Invoke();
        }
        catch (DuplicateWorkspaceException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registering workspace failed");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => IsProbed && !IsBusy && !string.IsNullOrWhiteSpace(Name);

    /// <summary>
    /// The listed track of that name (ignoring case), or a new one. A new track joins the list at once: it is stored
    /// whether or not the workspace is, so an Add after a failed one must find it rather than create it again.
    /// </summary>
    private async Task<Track> TrackNamedAsync(string name)
    {
        var track = Tracks.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (track is null)
        {
            track = await _store.AddTrackAsync(name, CancellationToken.None);
            Tracks.Add(track);
        }

        return track;
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke();
}
