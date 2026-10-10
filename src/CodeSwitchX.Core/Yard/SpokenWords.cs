namespace CodeSwitchX.Core.Yard;

/// <summary>What the user said as words, for the spoken commands (<see cref="SpokenNextQuestion"/>, <see cref="SpokenStop"/>).</summary>
public static class SpokenWords
{
    /// <summary>What a transcript writes between words: spaces, punctuation, quotes, dashes and an ellipsis.</summary>
    private static readonly char[] Between =
        [' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', ';', '"', '\'', '’', '‘', '“', '”', '-', '–', '—', '…'];

    /// <summary>The words, lower case, "that's" as "that" and "s"; none for nothing said.</summary>
    public static string[] Of(string? said) => (said ?? "").ToLowerInvariant().Split(Between, StringSplitOptions.RemoveEmptyEntries);
}
