namespace CodeSwitchX.Core.Yard;

/// <summary>
/// Reads "stop" from what the user said to Raven (#250): "Raven, stop.", "be quiet", "enough", "stop, stop", "shut up",
/// "hör auf", "Ruhe". It only silences Raven: it goes to no brain, and Raven says nothing back. The whole of what was said
/// must be that, said once or more, give or take "Raven", "no", "please" or "thanks" around it, so a request ("stop chat 2",
/// "stop the build") goes to the brain.
/// </summary>
public static class SpokenStop
{
    /// <summary>Words that may come before or after it: "Raven, stop, please", "okay, enough", "no, stop", "stop, thanks".</summary>
    private static readonly HashSet<string> Around = new(StringComparer.Ordinal)
    {
        "raven", "hey", "ok", "okay", "oh", "no", "nein", "please", "bitte", "now", "just", "jetzt", "mal", "nun", "already", "schon",
        "thanks", "thank", "you", "danke",
    };

    /// <summary>
    /// What it is, word by word as the user says it, "that's" said as "that s". Not "stop it": in a window's chat that stops
    /// the window's chat, for its brain to do.
    /// </summary>
    private static readonly string[][] Phrases =
    [
        .. new[]
        {
            "stop talking", "stop speaking", "be quiet", "shut up", "that s enough", "thats enough", "sei still", "sei ruhig",
            "das reicht", "es reicht", "hör auf", "hoer auf", "stop", "quiet", "enough", "silence", "hush", "stopp", "halt", "ruhe",
            "genug", "aufhören", "aufhoeren", "schluss",
        }.Select(p => p.Split(' ')),
    ];

    public static bool Is(string? said)
    {
        var words = SpokenWords.Of(said);
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

        // One stop or more ("stop, stop"), with nothing else between them.
        if (to == from)
        {
            return false;
        }

        for (var at = from; at < to;)
        {
            var phrase = Phrases.FirstOrDefault(p => at + p.Length <= to && p.SequenceEqual(words[at..(at + p.Length)]));
            if (phrase is null)
            {
                return false;
            }

            at += phrase.Length;
        }

        return true;
    }
}
