using CodeSwitchX.UI.Yard;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeSwitchX.UI.Raven;

/// <summary>
/// One entry of Raven's chat list: a workspace's chat, by the workspace's number; chat 0, the Yard, for what belongs to
/// no single window; or Activity, every entry in time order, read-only.
/// </summary>
public sealed partial class RavenChat : ObservableObject
{
    private RavenChat(Guid? workspaceId, int number, string name, bool isActivity)
    {
        WorkspaceId = workspaceId;
        Number = number;
        _name = name;
        IsActivity = isActivity;
    }

    public static RavenChat Yard() => new(null, 0, "Yard", false);

    public static RavenChat Activity() => new(null, -1, "Activity", true);

    public static RavenChat Of(Guid workspaceId, int number, string name) => new(workspaceId, number, name, false);

    /// <summary>The workspace the chat is of; null for the Yard and for Activity.</summary>
    public Guid? WorkspaceId { get; }

    /// <summary>The workspace's number, 0 for the Yard; Activity has none (-1).</summary>
    public int Number { get; }

    public bool IsActivity { get; }

    /// <summary>What the list shows on the number's dot: "3", "0"; nothing for Activity.</summary>
    public string Tag => IsActivity ? "" : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _name;

    /// <summary>The workspace's tile: the chat's header shows its colour and where its repositories stand. Null for the Yard and Activity.</summary>
    [ObservableProperty]
    private WorkspaceTileViewModel? _tile;

    /// <summary>The header's line under the name, for the Yard and Activity; a window's chat shows its git lines instead.</summary>
    public string? Subtitle => IsActivity ? "Every chat, in time order. Cards are answered in their own chat."
        : WorkspaceId is null ? "No window in particular: start chats anywhere, ask what needs you." : null;

    /// <summary>"3 ContentAutomatorX", "0 Yard", "Activity": as the list and the brain name it.</summary>
    public string Label => IsActivity ? Name : $"{Number} {Name}";
}
