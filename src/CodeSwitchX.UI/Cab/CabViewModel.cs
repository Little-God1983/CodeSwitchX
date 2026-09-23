using System.Collections.ObjectModel;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Yard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeSwitchX.UI.Cab;

/// <summary>One workspace full-window plus a 28 px strip of pips for the other tiles.</summary>
public sealed partial class CabViewModel : ObservableObject
{
    [ObservableProperty] private WorkspaceTileViewModel? _activeTile;

    public ObservableCollection<WorkspaceTileViewModel> Pips { get; } = [];

    /// <summary>Last known screen rectangle of the host area; set by the view, consumed by the shell.</summary>
    public ScreenRect? LastHostRect { get; set; }

    public event Action? BackRequested;
    public event Action<Guid>? SwitchRequested;

    public void SetActive(WorkspaceTileViewModel tile, IEnumerable<WorkspaceTileViewModel> allTiles)
    {
        ActiveTile = tile;
        Pips.Clear();
        foreach (var other in allTiles.Where(t => t.Id != tile.Id))
        {
            Pips.Add(other);
        }
    }

    [RelayCommand]
    private void Back() => BackRequested?.Invoke();

    [RelayCommand]
    private void SelectPip(WorkspaceTileViewModel tile) => SwitchRequested?.Invoke(tile.Id);
}
