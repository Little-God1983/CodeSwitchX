namespace CodeSwitchX.UI.Raven;

/// <summary>
/// A click on a row of Raven's chat list, which opens the chat's window (#311): the left button's release counts only on
/// the row it went down on, so a drag that ends on a row opens nothing.
/// </summary>
internal sealed class ChatClick
{
    private object? _pressed;

    /// <summary>The left button went down on <paramref name="row"/>; null for a press beside the rows.</summary>
    public void Press(object? row) => _pressed = row;

    /// <summary>The left button came up on <paramref name="row"/>: true when that is a click on it.</summary>
    public bool Release(object row)
    {
        var pressed = _pressed;
        _pressed = null;
        return pressed is not null && ReferenceEquals(pressed, row);
    }
}
