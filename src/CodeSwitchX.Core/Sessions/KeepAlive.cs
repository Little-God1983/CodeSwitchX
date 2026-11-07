namespace CodeSwitchX.Core.Sessions;

/// <summary>
/// The message that keeps a chat's prompt cache warm (#386). A chat in a VS Code tab can only be reached by a message
/// from another Claude session, so the ping is a real, visible turn: it runs in the chat's own process, reads the cache
/// and so starts its hour again. The chat is told to answer one word and do nothing, and the app tells such a turn apart
/// from the user's by the words the message opens with: its end is no news, makes no sound and is no use of the chat.
/// </summary>
public static class KeepAlive
{
    /// <summary>What every ping opens with; the sender is a model and may word the rest its own way.</summary>
    public const string Opening = "Keep-alive from CodeSwitchX";

    /// <summary>What the chat is sent.</summary>
    public const string Text = Opening + ": the user asked to keep this chat's prompt cache warm while they are away. "
        + "Reply with only \"ok\" and do nothing else.";

    /// <summary>
    /// Whether a prompt is a ping: a message of another session that opens with <see cref="Opening"/> (quotes or marks before it
    /// aside), or holds the whole <see cref="Text"/>. The sender is a model told to copy the message, and may quote it or put a
    /// word before it; a message that only names the ping (Raven passing on the user's words) is no ping, and the same words
    /// typed by the user are a prompt of their own.
    /// </summary>
    public static bool IsPing(string? prompt)
    {
        if (CrossSessionMessage.EnvelopeOf(prompt) is not { } envelope)
        {
            return false;
        }

        var tagEnd = envelope.IndexOf('>');
        if (tagEnd < 0)
        {
            return false;
        }

        var body = envelope.AsSpan(tagEnd + 1).Trim().TrimStart(['"', '\u201c', '\'', '*', '_', '`', '>', ' ']);
        return body.StartsWith(Opening, StringComparison.OrdinalIgnoreCase) || body.Contains(Text, StringComparison.OrdinalIgnoreCase);
    }
}
