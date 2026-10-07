using System.ComponentModel;
using CodeSwitchX.UI.Yard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    [NotifyPropertyChangedFor(nameof(Tip))]
    private string _name;

    /// <summary>
    /// The workspace's tile: the chat's header shows its colour and where its repositories stand, and the chat mirrors its
    /// <see cref="WorkspaceTileViewModel.IsWorking"/> (#182). Null for the Yard and Activity.
    /// </summary>
    [ObservableProperty]
    private WorkspaceTileViewModel? _tile;

    /// <summary>Raven's brain answers a question in it (#182).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(Tip))]
    private bool _isAnswering;

    /// <summary>A Claude chat of its window works, as its tile says (#182).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(Tip))]
    private bool _hasWorkingChat;

    /// <summary>Work goes on in it (#182): Raven answers there, or a Claude chat of its window works. A green dot on its number.</summary>
    public bool IsWorking => IsAnswering || HasWorkingChat;

    partial void OnTileChanged(WorkspaceTileViewModel? oldValue, WorkspaceTileViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnTilePropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnTilePropertyChanged;
        }

        HasWorkingChat = newValue?.IsWorking == true;
    }

    private void OnTilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceTileViewModel.IsWorking) && sender is WorkspaceTileViewModel tile)
        {
            HasWorkingChat = tile.IsWorking;
        }
    }

    /// <summary>The header's line under the name, for the Yard and Activity; a window's chat shows its git lines instead.</summary>
    public string? Subtitle => IsActivity ? "Every chat, in time order. Cards are answered in their own chat."
        : WorkspaceId is null ? "No window in particular: start chats anywhere, ask what needs you." : null;

    /// <summary>"3 ContentAutomatorX", "0 Yard", "Activity": as the list and the brain name it.</summary>
    public string Label => IsActivity ? Name : $"{Number} {Name}";

    /// <summary>A card in it still waits for an answer: its number wears an outline until the card is answered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(Tip))]
    private bool _isWaiting;

    /// <summary>It waits, and the user has not opened it since the card came: the outline blinks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(Tip))]
    private bool _isWaitingUnseen;

    /// <summary>Lines that came while the user was in another chat (Raven's answers, news lines, cards); opening it clears them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadText))]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(Tip))]
    private int _unread;

    /// <summary>The badge on the number: "3", "9+"; nothing for none.</summary>
    public string UnreadText => Unread switch
    {
        <= 0 => "",
        > 9 => "9+",
        _ => Unread.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>A line just came that the user does not see: the badge pulses a few times (#173), then stays steady.</summary>
    [ObservableProperty]
    private bool _isNewsPulsing;

    /// <summary>A Claude chat of it failed since the user last opened it: a red mark.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(Tip))]
    private bool _hasFailed;

    /// <summary>A window's chat can be muted (#153); chat 0 and Activity cannot: Raven's own mute quiets everything.</summary>
    public bool CanMute => WorkspaceId is not null;

    /// <summary>
    /// Muted (#153): its news, with the sound it makes while the user is elsewhere, and its catch-up are only written. Its
    /// cards are still read out, and sound from elsewhere: the chat waits on them. Raven still answers aloud in it. The panel
    /// remembers it by the window.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(Tip))]
    [NotifyPropertyChangedFor(nameof(MuteText))]
    private bool _isMuted;

    /// <summary>The row's speaker and its menu: "Mute chat", "Unmute chat".</summary>
    public string MuteText => IsMuted ? "Unmute chat" : "Mute chat";

    [RelayCommand(CanExecute = nameof(CanMute))]
    private void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>The marks in words, for the tooltip and screen readers: "waits for you, not seen yet, 3 unread, failed, muted"; "" for none.</summary>
    public string Status => string.Join(", ", new[]
    {
        IsWaitingUnseen ? "waits for you, not seen yet" : IsWaiting ? "waits for you" : null,
        IsWorking ? "working" : null,
        Unread > 0 ? $"{Unread} unread" : null,
        HasFailed ? "failed" : null,
        IsMuted ? "muted" : null,
    }.Where(s => s is not null));

    /// <summary>
    /// What chat 0, the overview, knows of a window's chat (#124): one or two lines the summarizer words after each turn and
    /// each piece of news in it; null until the first. Never set for the Yard or Activity.
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>The list's tooltip: "3 ContentAutomatorX: waits for you, 2 unread"; the label alone with no marks.</summary>
    public string Tip => Status.Length == 0 ? Label : $"{Label}: {Status}";
}
