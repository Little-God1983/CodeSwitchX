namespace CodeSwitchX.Core.Sessions;

/// <summary>
/// Whether what the user said is a yes: the app's own check of the user's next words after Raven's brain proposed an allow
/// (<see cref="ChatAsks.Propose"/>), so that no model and nothing a chat wrote can allow a command. A yes runs a command,
/// so the check is strict: a plain yes ("yes", "yeah", "ja", "do it", "go ahead"), or "okay"/"sure" on their own, with
/// at most a few words after it that only add to it ("yes, run it now"). A question, words that point elsewhere ("allow
/// the other one", "do it after the build"), a call to Raven ("hey Raven"), a word said twice ("okay, okay", "ja ja", a
/// dismissive repeat) or anything longer is no yes.
/// </summary>
public static class SpokenYes
{
    /// <summary>A yes, at the start of the words.</summary>
    private static readonly string[] Strong =
    [
        "yes", "yeah", "yep", "yup", "ja", "jawohl", "do it", "go ahead", "go for it", "allow it", "allow", "allowed", "run it",
        "confirm", "confirmed", "approved", "affirmative", "proceed", "please do", "mach es", "mach das", "mach schon",
    ];

    /// <summary>A yes of their own, alone or before a strong one: "okay", "okay, do it".</summary>
    private static readonly HashSet<string> Weak = new(StringComparer.Ordinal) { "ok", "okay", "sure", "alright", "klar" };

    /// <summary>
    /// Words that may come before a yes, but are none: "Raven, yes", "please, do it". On their own, or after "okay"
    /// ("Okay Raven", as one calls an assistant), they are no yes. Not "na": "na ja" is a German "well…".
    /// </summary>
    private static readonly HashSet<string> Before = new(StringComparer.Ordinal) { "raven", "hey", "please", "bitte", "well", "so" };

    /// <summary>
    /// The only words that may follow a yes, and only <see cref="MaxAfter"/> of them: "yes, run it now", "ja bitte".
    /// </summary>
    private static readonly HashSet<string> After = new(StringComparer.Ordinal)
    {
        "it", "that", "please", "now", "thanks", "thank", "you", "raven", "go", "ahead", "do", "run", "allow", "bitte", "jetzt",
        "es", "das", "mach", "danke", "ok", "okay", "sure",
    };

    private const int MaxAfter = 4;

    /// <summary>
    /// The words that may not be said twice: those of a yes, not the ones that only go with it ("it", "please"): "allow
    /// it, run it" is a yes, "do it, do it" is none.
    /// </summary>
    private static readonly HashSet<string> YesWords = Strong.SelectMany(s => s.Split(' ')).Concat(Weak)
        .Except(["it", "that", "es", "das", "please", "bitte", "now", "jetzt"]).ToHashSet(StringComparer.Ordinal);

    public static bool IsYes(string text)
    {
        if (text.Contains('?'))
        {
            return false; // "yeah, what does it want to run?" asks; it does not answer
        }

        var words = Words(text);
        var yesWords = words.Select(w => w == "ok" ? "okay" : w).Where(YesWords.Contains).ToList();
        if (yesWords.Distinct().Count() < yesWords.Count)
        {
            return false; // "okay, okay", "ja ja", "sure, sure": an impatient "yeah yeah", not a yes to what was read
        }

        var at = 0;
        var weak = false;
        var calls = false; // a word of address after "okay": "Okay Raven" calls Raven, it does not answer
        while (at < words.Count && (Before.Contains(words[at]) || Weak.Contains(words[at])))
        {
            calls |= weak && Before.Contains(words[at]);
            weak |= Weak.Contains(words[at]);
            at++;
        }

        if (at == words.Count)
        {
            return weak && !calls; // "okay", "sure", "Raven, okay": a yes on its own; "hey Raven", "please", "Okay Raven" are none
        }

        var rest = words.Skip(at).ToList();
        var yes = Strong.Select(s => s.Split(' ')).Where(s => rest.Count >= s.Length && rest.Take(s.Length).SequenceEqual(s))
            .OrderByDescending(s => s.Length).FirstOrDefault();
        if (yes is null)
        {
            return false;
        }

        var tail = rest.Skip(yes.Length).ToList();
        return tail.Count <= MaxAfter && tail.All(After.Contains);
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
