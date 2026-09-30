namespace CodeSwitchX.Voice.Dictation;

/// <summary>Whisper has no dictionary, but it accepts a "prompt": text it treats as what was said
/// just before the clip. Listing the user's names there biases decoding towards those spellings.
/// A comma-separated list is the shape the Whisper authors recommend for vocabulary hints.</summary>
public static class VocabularyPrompt
{
    public static string Build(IReadOnlyList<string> words) =>
        string.Join(", ", words.Select(w => w.Trim()).Where(w => w.Length > 0));
}
