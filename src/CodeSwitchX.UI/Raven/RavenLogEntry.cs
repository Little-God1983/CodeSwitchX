using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeSwitchX.UI.Raven;

public enum RavenLogKind
{
    You,
    Note,
    Warning,
}

/// <summary>One line of the panel's log. The text is observable so a progress line can update in place.</summary>
public sealed partial class RavenLogEntry(RavenLogKind kind, string text, DateTimeOffset at) : ObservableObject
{
    public RavenLogKind Kind { get; } = kind;

    public DateTimeOffset At { get; } = at;

    [ObservableProperty]
    private string _text = text;
}
