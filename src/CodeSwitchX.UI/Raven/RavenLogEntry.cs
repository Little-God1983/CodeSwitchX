using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeSwitchX.UI.Raven;

public enum RavenLogKind
{
    You,
    Note,
    Warning,

    /// <summary>Raven's reply; its text grows as the reply streams in.</summary>
    Raven,

    /// <summary>A card for a tool Raven's brain called: what it looked at.</summary>
    Action,

    /// <summary>A digest card: what the chats did, a line per chat that shows its tile when clicked.</summary>
    News,

    /// <summary>A chat's question, with its options to click, held until it is answered here or left to VS Code.</summary>
    Question,
}

/// <summary>One line of the panel's log. The text is observable so a progress line can update in place.</summary>
public sealed partial class RavenLogEntry(RavenLogKind kind, string text, DateTimeOffset at) : ObservableObject
{
    public RavenLogKind Kind { get; } = kind;

    public DateTimeOffset At { get; } = at;

    [ObservableProperty]
    private string _text = text;

    /// <summary>An action card's arguments ("needs_me"); null for none and for every other kind.</summary>
    [ObservableProperty]
    private string? _detail;

    /// <summary>A digest card's lines; null for every other kind.</summary>
    public IReadOnlyList<ChatNewsLine>? Lines { get; set; }

    /// <summary>A question card's question; null for every other kind.</summary>
    public ChatQuestionCard? Question { get; set; }

    /// <summary>An action card's tool call came back failed.</summary>
    [ObservableProperty]
    private bool _failed;
}
