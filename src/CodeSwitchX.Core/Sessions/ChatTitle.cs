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

        var keep = maxLength - 1;
        if (char.IsHighSurrogate(singleLine[keep - 1]))
        {
            keep--; // never cut between the two halves of an emoji
        }

        return string.Concat(singleLine.AsSpan(0, keep), "…");
    }
}
