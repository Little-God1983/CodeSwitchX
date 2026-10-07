namespace CodeSwitchX.Core;

/// <summary>Shortens text for a line, a card button or a sentence to say, never in the middle of an emoji.</summary>
public static class TextCut
{
    /// <summary>The text's words on one line: every run of white space, line breaks included, is one space, none at the ends.</summary>
    public static string OneLine(string? text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// <paramref name="text"/> as it is when it has at most <paramref name="max"/> characters; otherwise its first
    /// <paramref name="max"/> − 1 (one fewer where that would split a surrogate pair), trimmed, with <paramref name="suffix"/>
    /// after them: "dotnet test tests/Pro… (the rest is on the card)".
    /// </summary>
    public static string Cut(string text, int max, string suffix = "…")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 2);
        if (text.Length <= max)
        {
            return text;
        }

        var keep = max - 1;
        if (char.IsHighSurrogate(text[keep - 1]))
        {
            keep--; // never half an emoji
        }

        return text[..keep].TrimEnd() + suffix;
    }
}
