namespace CodeSwitchX.Core.Sessions;

/// <summary>
/// A message from another Claude session as the chat's prompt shows it: <c>SendMessage</c>'s envelope, after the line
/// Claude Code puts before it. It is how Raven hands a chat its task.
/// </summary>
public static class CrossSessionMessage
{
    internal const string EnvelopeOpen = "<cross-session-message ";
    internal const string EnvelopeClose = "</cross-session-message>";
    private const string EnvelopePreamble = "Another Claude session sent a message:";
    private const string FromAttribute = " from=\"";

    /// <summary>What the sender's messaging socket is written with in <c>from</c>; Claude Code's records name it without.</summary>
    private const string SocketScheme = "uds:";

    /// <summary>
    /// The envelope, from its opening tag on; null for a prompt that does not open with one (after SendMessage's line
    /// before it): a prompt that quotes the tag, a bug report or a summary, is the user's own.
    /// </summary>
    internal static string? EnvelopeOf(string? prompt)
    {
        var body = prompt?.TrimStart() ?? "";
        body = body.StartsWith(EnvelopePreamble, StringComparison.Ordinal) ? body[EnvelopePreamble.Length..].TrimStart() : body;
        return body.StartsWith(EnvelopeOpen, StringComparison.Ordinal) ? body : null;
    }

    /// <summary>
    /// The messaging socket of the session that sent the message, as its record in Claude Code's <c>sessions</c> folder
    /// names it (<c>\\.\pipe\LOCAL\cc-msg-…</c>); null for a prompt that is no such message, or whose opening tag names none.
    /// </summary>
    public static string? SenderOf(string? prompt)
    {
        if (EnvelopeOf(prompt) is not { } envelope)
        {
            return null;
        }

        var tagEnd = envelope.IndexOf('>');
        var tag = tagEnd < 0 ? envelope : envelope[..tagEnd];
        var start = tag.IndexOf(FromAttribute, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += FromAttribute.Length;
        var end = tag.IndexOf('"', start);
        if (end <= start)
        {
            return null;
        }

        var from = tag[start..end];
        return from.StartsWith(SocketScheme, StringComparison.Ordinal) ? from[SocketScheme.Length..] : from;
    }
}
