using System.Collections.ObjectModel;

namespace CodeSwitchX.UI.Yard;

/// <summary>The groups of the Yard (#276): when a chat of a window was last used.</summary>
public enum LastUse
{
    Today,
    ThisWeek,
    Older,
}

public static class YardGroups
{
    /// <summary>How long ago a window's last use may lie for it to be in Now &amp; today: a rolling day, not since midnight.</summary>
    public static readonly TimeSpan Today = TimeSpan.FromHours(24);

    /// <summary>How long ago it may lie for This week: a rolling seven days, so a Monday does not empty the group.</summary>
    public static readonly TimeSpan ThisWeek = TimeSpan.FromDays(7);

    /// <summary>
    /// The group of a window last used at <paramref name="lastUsed"/> (null: no chat of it was ever seen). One whose chat
    /// works is in Now &amp; today whatever its last event; so is one whose chat waits on the user, under Needs me first.
    /// </summary>
    public static LastUse Of(DateTimeOffset? lastUsed, bool working, bool waiting, bool needsMeFirst, DateTimeOffset now)
    {
        if (working || (waiting && needsMeFirst))
        {
            return LastUse.Today;
        }

        if (lastUsed is not { } at)
        {
            return LastUse.Older;
        }

        var ago = now - at;
        return ago < Today ? LastUse.Today : ago < ThisWeek ? LastUse.ThisWeek : LastUse.Older;
    }

    public static string NameOf(LastUse group) => group switch
    {
        LastUse.Today => "Now & today",
        LastUse.ThisWeek => "This week",
        _ => "Older",
    };
}

/// <summary>One group of the Yard: its tiles, the newest first. The view hides a group without tiles.</summary>
public sealed class LastUseGroupViewModel(LastUse group)
{
    public LastUse Group { get; } = group;
    public string Name => YardGroups.NameOf(Group);
    public ObservableCollection<WorkspaceTileViewModel> Tiles { get; } = [];
}
