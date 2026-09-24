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
        return singleLine.Length <= maxLength
            ? singleLine
            : string.Concat(singleLine.AsSpan(0, maxLength - 1), "…");
    }
}
