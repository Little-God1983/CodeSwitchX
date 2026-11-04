using System.Runtime.InteropServices;
using System.Windows;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>Puts text on the clipboard.</summary>
public interface ITextClipboard
{
    /// <summary>False when it could not, as when another program holds the clipboard. Never throws. On the UI thread.</summary>
    bool TrySetText(string text);
}

/// <summary>
/// The Windows clipboard. <see cref="Clipboard.SetDataObject(object, bool)"/>, not SetText: it keeps the text after this app
/// closes, and it waits a moment for a clipboard another program holds before it gives up.
/// </summary>
public sealed class WpfTextClipboard : ITextClipboard
{
    public bool TrySetText(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, copy: true);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}
