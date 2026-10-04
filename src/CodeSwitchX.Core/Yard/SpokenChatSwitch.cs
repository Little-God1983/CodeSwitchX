namespace CodeSwitchX.Core.Yard;

/// <summary>A chat to switch Raven's panel to: by its number, or Activity; <paramref name="Open"/> also shows its window in the Cab.</summary>
public sealed record ChatSwitch(int? Number, bool Activity, bool Open);

/// <summary>
/// Reads a switch of Raven's chat from what the user said, as the app checks a spoken yes (<see cref="Sessions.SpokenYes"/>):
/// "chat three", "go to chat 3", "zu Chat drei", "open chat three", "activity". The whole of what was said must be that,
/// so a sentence about a chat ("what is chat three doing") goes to the brain, and a bare number is never taken for one:
/// it may answer something else.
/// </summary>
public static class SpokenChatSwitch
{
    /// <summary>Words that may come before the chat: "go to", "switch to", "zu", "wechsel zu", "Raven".</summary>
    private static readonly HashSet<string> Lead = new(StringComparer.Ordinal)
    {
        "raven", "hey", "please", "bitte", "go", "to", "switch", "show", "me", "the", "back", "zu", "zum", "geh", "gehe", "wechsel",
        "wechsle", "zeig", "zeige", "mir", "den", "zurück",
    };

    /// <summary>Words that also show the chat's window in the Cab.</summary>
    private static readonly HashSet<string> OpenWords = new(StringComparer.Ordinal) { "open", "öffne", "öffnen", "offne" };

    private static readonly HashSet<string> ActivityWords = new(StringComparer.Ordinal) { "activity", "aktivität", "aktivitat" };

    public static bool TryRead(string? said, out ChatSwitch target)
    {
        target = null!;
        var words = (said ?? "").ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', ';', '"', '\''], StringSplitOptions.RemoveEmptyEntries);
        var open = false;
        var at = 0;
        for (; at < words.Length && (Lead.Contains(words[at]) || OpenWords.Contains(words[at])); at++)
        {
            open |= OpenWords.Contains(words[at]);
        }

        if (at == words.Length - 1 && ActivityWords.Contains(words[at]) && !open)
        {
            target = new ChatSwitch(null, Activity: true, Open: false);
            return true;
        }

        // "chat" and its number: SpokenNumber reads the rest ("chat number three", "Chat Nummer elf"), all of it.
        if (at < words.Length - 1 && words[at] == "chat" && SpokenNumber.TryRead(string.Join(' ', words[at..]), out var number))
        {
            target = new ChatSwitch(number, Activity: false, Open: open);
            return true;
        }

        return false;
    }
}
