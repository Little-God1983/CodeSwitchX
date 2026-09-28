using System.Collections.ObjectModel;
using CodeSwitchX.Core.Workspaces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeSwitchX.UI.Yard;

public sealed class TrackGroupViewModel : ObservableObject
{
    private Track _track;

    public TrackGroupViewModel(Track track)
    {
        _track = track;
    }

    /// <summary>Set again once the stored track is read (a new track's group starts with a placeholder); the group and its tiles stay.</summary>
    public Track Track
    {
        get => _track;
        set
        {
            if (SetProperty(ref _track, value))
            {
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(SortOrder));
            }
        }
    }

    public Guid Id => Track.Id;
    public string Name => Track.Name;
    public int SortOrder => Track.SortOrder;
    public ObservableCollection<WorkspaceTileViewModel> Tiles { get; } = [];
}
