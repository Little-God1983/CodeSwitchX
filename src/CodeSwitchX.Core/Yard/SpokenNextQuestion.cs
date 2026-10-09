namespace CodeSwitchX.Core.Yard;

/// <summary>
/// Reads "next question" from what the user said (#230), as <see cref="SpokenChatSwitch"/> reads a chat: "next question",
/// "go to the next question", "nächste Frage", "zur nächsten Frage". The whole of what was said must be that, so a
/// sentence about it ("what is the next question about") goes to the brain.
/// </summary>
public static class SpokenNextQuestion
{
    /// <summary>Words that may come before it: "go to the", "show me the", "zur", "die", "Raven".</summary>
    private static readonly HashSet<string> Lead = new(StringComparer.Ordinal)
    {
        "raven", "hey", "please", "bitte", "go", "to", "on", "show", "me", "the", "zu", "zur", "die", "geh", "gehe", "zeig", "zeige",
        "mir", "weiter",
    };

    private static readonly HashSet<string> Next = new(StringComparer.Ordinal) { "next", "nächste", "nächsten", "naechste", "naechsten" };

    private static readonly HashSet<string> Question = new(StringComparer.Ordinal) { "question", "questions", "frage", "fragen" };

    public static bool Is(string? said)
    {
        var words = (said ?? "").ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', ';', '"', '\''], StringSplitOptions.RemoveEmptyEntries);
        var at = 0;
        while (at < words.Length && Lead.Contains(words[at]))
        {
            at++;
        }

        return at == words.Length - 2 && Next.Contains(words[at]) && Question.Contains(words[at + 1]);
    }
}
