namespace CodeSwitchX.Core.Yard;

/// <summary>
/// What Whisper writes for a cough, a throat-clear or a breath: its known phantom phrases, learnt from subtitles ("Thank
/// you.", "Thanks for watching!", "Untertitel im Auftrag des ZDF"). An Open mic turn that is only that, and does not start
/// with Raven's name, is not asked: it would take the floor from the answer the user waits for (#219).
/// </summary>
public static class WhisperNoise
{
    private static readonly HashSet<string> Phrases = new(StringComparer.Ordinal)
    {
        "you", "thank you", "thanks", "thank you very much", "thank you so much", "thanks for watching", "thank you for watching",
        "bye", "bye bye", "danke", "vielen dank", "danke schön", "tschüss", "untertitel im auftrag des zdf", "untertitel der amara org community",
    };

    public static bool Is(string? text)
    {
        var words = (text ?? "").ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', ';', '"', '\'', '-', '…', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        var said = string.Join(' ', words.Where(w => !w.All(char.IsDigit))); // "Untertitel im Auftrag des ZDF, 2021"
        return said.Length > 0 && Phrases.Contains(said);
    }
}
