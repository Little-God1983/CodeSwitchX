namespace CodeSwitchX.Voice.Dictation;

/// <summary>Whisper has no dictionary, but it accepts a "prompt": text it treats as what was said
/// just before the clip. Listing the user's names there biases decoding towards those spellings.
/// A comma-separated list is the shape the Whisper authors recommend for vocabulary hints.</summary>
public static class VocabularyPrompt
{
    /// <summary>
    /// The longest prompt built, in characters. whisper.cpp keeps only the last 224 prompt tokens and drops the start
    /// without a word, which is where the words that matter most are. Names split into short tokens, about two
    /// characters each at worst, so this stays under that.
    /// </summary>
    public const int MaximumLength = 450;

    /// <summary>The words in the order given, most important first; the words that would take it past
    /// <see cref="MaximumLength"/> are left out, from the end.</summary>
    public static string Build(IReadOnlyList<string> words)
    {
        var prompt = new System.Text.StringBuilder();
        foreach (var word in words.Select(w => w.Trim()).Where(w => w.Length > 0))
        {
            var added = prompt.Length == 0 ? word.Length : word.Length + 2;
            if (prompt.Length + added > MaximumLength)
            {
                break;
            }

            prompt.Append(prompt.Length == 0 ? "" : ", ").Append(word);
        }

        return prompt.ToString();
    }
}
