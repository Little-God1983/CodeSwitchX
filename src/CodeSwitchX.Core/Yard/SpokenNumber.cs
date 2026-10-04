namespace CodeSwitchX.Core.Yard;

/// <summary>
/// Reads a workspace's or a chat's number from what the user said: the number alone or after "chat", "workspace" or
/// "number", as digits or as an English or German word up to twenty ("chat three", "Chat drei", "number 12"). The whole of
/// what was said must be that: "three chats" or "chat three please stop" are no number, so a sentence is never taken for one.
/// </summary>
public static class SpokenNumber
{
    private static readonly HashSet<string> Lead = new(StringComparer.Ordinal)
    {
        "chat", "workspace", "number", "nummer", "no", "nr",
    };

    private static readonly Dictionary<string, int> Words = new(StringComparer.Ordinal)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
        ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
        ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20,
        ["null"] = 0, ["eins"] = 1, ["ein"] = 1, ["eine"] = 1, ["zwei"] = 2, ["drei"] = 3, ["vier"] = 4, ["fünf"] = 5, ["sechs"] = 6,
        ["sieben"] = 7, ["acht"] = 8, ["neun"] = 9, ["zehn"] = 10, ["elf"] = 11, ["zwölf"] = 12, ["dreizehn"] = 13, ["vierzehn"] = 14,
        ["fünfzehn"] = 15, ["sechzehn"] = 16, ["siebzehn"] = 17, ["achtzehn"] = 18, ["neunzehn"] = 19, ["zwanzig"] = 20,
    };

    /// <summary>The number <paramref name="said"/> names; false when it is anything but a number.</summary>
    public static bool TryRead(string? said, out int number)
    {
        number = 0;
        var words = (said ?? "").ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', ';', '"', '\''], StringSplitOptions.RemoveEmptyEntries);
        var at = 0;
        while (at < words.Length - 1 && Lead.Contains(words[at]))
        {
            at++;
        }

        if (at != words.Length - 1)
        {
            return false;
        }

        var word = words[at];
        if (word.All(char.IsAsciiDigit) && word.Length <= 3)
        {
            number = int.Parse(word, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        return Words.TryGetValue(word, out number);
    }
}
