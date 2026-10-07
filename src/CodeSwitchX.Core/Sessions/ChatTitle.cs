using System.Globalization;

namespace CodeSwitchX.Core.Sessions;

public static class ChatTitle
{
    public const int DefaultMaxLength = 60;

    /// <summary>
    /// A chat's title from a prompt: one line, cut to <paramref name="maxLength"/>. A message from another session
    /// (SendMessage's envelope, how Raven hands a chat its task) is titled by the task inside it; an envelope with no task
    /// left in it (empty, or cut off inside its opening tag) gives no title.
    /// </summary>
    public static string? FromPrompt(string? prompt, int maxLength = DefaultMaxLength)
    {
        // The task is between the opening tag's end and the closing tag.
        if (CrossSessionMessage.EnvelopeOf(prompt) is { } envelope)
        {
            prompt = envelope;
            var tagEnd = prompt.IndexOf('>');
            if (tagEnd < 0)
            {
                return null;
            }

            var close = prompt.IndexOf(CrossSessionMessage.EnvelopeClose, tagEnd, StringComparison.Ordinal);
            prompt = close < 0 ? prompt[(tagEnd + 1)..] : prompt[(tagEnd + 1)..close];
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var singleLine = string.Join(' ',
            prompt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (singleLine.Length <= maxLength)
        {
            return singleLine;
        }

        // Whole text elements only: an emoji is one or more surrogate pairs, possibly joined (a flag, a family).
        var limit = maxLength - 1;
        var keep = 0;
        while (keep < limit)
        {
            var next = keep + StringInfo.GetNextTextElementLength(singleLine.AsSpan(keep));
            if (next > limit)
            {
                break;
            }

            keep = next;
        }

        return string.Concat(singleLine.AsSpan(0, keep), "…");
    }
}
