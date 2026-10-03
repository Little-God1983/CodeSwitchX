namespace CodeSwitchX.Core.Sessions;

/// <summary>
/// Whether what the user said is a yes: the app's own check of the user's next words after Raven's brain proposed an allow
/// (<see cref="ChatAsks.Propose"/>), so that no model and nothing a chat wrote can allow a command. A yes starts with a
/// plain yes ("yes", "yeah", "ja", "do it", "go ahead"), alone or with words after it that do not contradict it ("yes,
/// run it"; not "yes but wait"). Anything else is no yes: a question, a request, "okay, what's next".
/// </summary>
public static class SpokenYes
{
    /// <summary>A yes on its own, or at the start of the words: the later words may add to it.</summary>
    private static readonly string[] Strong =
    [
        "yes", "yeah", "yep", "yup", "yes please", "ja", "jawohl", "ja bitte", "do it", "go ahead", "go for it", "allow it", "allow",
        "allowed", "run it", "confirm", "confirmed", "approved", "affirmative", "proceed", "please do", "mach es", "mach das", "mach schon",
    ];

    /// <summary>A yes only on its own, or before a strong one ("okay", "okay do it"): "okay, what's next" is something else.</summary>
    private static readonly string[] Weak = ["ok", "okay", "sure", "alright", "fine", "right", "gut", "klar", "please", "bitte", "raven", "hey"];

    /// <summary>A word that turns a yes into something else: "yes, but not now", "yeah no", "ja, aber warte".</summary>
    private static readonly HashSet<string> Contradicting = new(StringComparer.Ordinal)
    {
        "no", "nope", "nah", "not", "dont", "don't", "never", "but", "wait", "stop", "cancel", "deny", "instead", "rather", "hold", "halt", "unless",
        "nein", "nicht", "kein", "keine", "aber", "warte", "abbrechen", "lieber", "stattdessen", "doch",
    };

    public static bool IsYes(string text)
    {
        var words = Words(text);
        if (words.Count == 0 || words.Any(Contradicting.Contains))
        {
            return false;
        }

        var at = 0;
        while (at < words.Count && Weak.Contains(words[at], StringComparer.Ordinal))
        {
            at++;
        }

        if (at == words.Count)
        {
            return true; // "okay", "sure": a yes on its own
        }

        var rest = string.Join(' ', words.Skip(at));
        return Strong.Any(yes => rest == yes || rest.StartsWith(yes + " ", StringComparison.Ordinal));
    }

    /// <summary>The words, lower case, without punctuation; an apostrophe inside a word stays ("don't").</summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || (c is '\'' or '’' && word.Length > 0))
            {
                word.Append(c == '’' ? '\'' : c);
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString().TrimEnd('\''));
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString().TrimEnd('\''));
        }

        return words;
    }
}
