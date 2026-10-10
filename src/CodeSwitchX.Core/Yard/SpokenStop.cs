namespace CodeSwitchX.Core.Yard;

/// <summary>
/// Reads "stop" from what the user said to Raven (#250): "Raven, stop.", "be quiet", "enough", "shut up", "hör auf",
/// "Ruhe". It only silences Raven: it goes to no brain, and Raven says nothing back. The whole of what was said must be
/// that, give or take "Raven", "please" or "now" around it, so a request ("stop chat 2", "stop the build") goes to the brain.
/// </summary>
public static class SpokenStop
{
    /// <summary>Words that may come before or after it: "Raven, stop, please", "okay, enough".</summary>
    private static readonly HashSet<string> Around = new(StringComparer.Ordinal)
    {
        "raven", "hey", "ok", "okay", "oh", "please", "bitte", "now", "just", "jetzt", "mal", "nun", "already", "schon",
    };

    /// <summary>
    /// What it is, word by word as the user says it, "that's" said as "that s". Not "stop it": in a window's chat that stops
    /// the window's chat, for its brain to do.
    /// </summary>
    private static readonly HashSet<string> Phrases = new(StringComparer.Ordinal)
    {
        "stop", "stop talking", "stop speaking", "be quiet", "quiet", "shut up", "enough", "that s enough",
        "thats enough", "silence", "hush", "stopp", "halt", "ruhe", "sei still", "sei ruhig", "genug", "das reicht", "es reicht",
        "hör auf", "hoer auf", "aufhören", "aufhoeren", "schluss",
    };

    public static bool Is(string? said)
    {
        var words = (said ?? "").ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', ';', '"', '\'', '’'], StringSplitOptions.RemoveEmptyEntries);
        var from = 0;
        var to = words.Length;
        while (from < to && Around.Contains(words[from]))
        {
            from++;
        }

        while (to > from && Around.Contains(words[to - 1]))
        {
            to--;
        }

        return to > from && Phrases.Contains(string.Join(' ', words[from..to]));
    }
}
