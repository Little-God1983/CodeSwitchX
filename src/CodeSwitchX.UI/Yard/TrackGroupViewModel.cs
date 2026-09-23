using System.Collections.ObjectModel;
using CodeSwitchX.Core.Workspaces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeSwitchX.UI.Yard;

public sealed class TrackGroupViewModel : ObservableObject
{
    public TrackGroupViewModel(Track track)
    {
        Track = track;
    }

    public Track Track { get; }
    public Guid Id => Track.Id;
    public string Name => Track.Name;
    public int SortOrder => Track.SortOrder;
    public ObservableCollection<WorkspaceTileViewModel> Tiles { get; } = [];
}
