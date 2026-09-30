namespace CodeSwitchX.Voice.Dictation;

/// <summary>A phrase Whisper keeps getting wrong and what it should have written.</summary>
public sealed record Correction(string Heard, string Meant);

/// <summary>What the host project knows about how its user speaks. Words go into Whisper's
/// prompt before every clip (so names are spelled right); corrections are applied to the text
/// afterwards (for the cases the prompt does not fix).</summary>
public sealed record DictationVocabulary(
    IReadOnlyList<string> Words,
    IReadOnlyList<Correction> Corrections)
{
    public static readonly DictationVocabulary Empty = new([], []);
}

/// <summary>Implemented by the host project. Called once per clip, so a fresh read of wherever
/// the host keeps its lists is fine.</summary>
public interface IDictationVocabularyProvider
{
    Task<DictationVocabulary> GetAsync(CancellationToken ct);
}
