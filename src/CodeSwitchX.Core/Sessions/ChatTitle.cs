using System.Globalization;

namespace CodeSwitchX.Core.Sessions;

public static class ChatTitle
{
    public const int DefaultMaxLength = 60;

    public static string? FromPrompt(string? prompt, int maxLength = DefaultMaxLength)
    {
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
