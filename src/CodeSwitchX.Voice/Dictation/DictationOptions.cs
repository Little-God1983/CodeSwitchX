namespace CodeSwitchX.Voice.Dictation;

/// <summary>The ggml models this library can fetch. Tiny answers fastest; Base is the fastest of the practical ones.
///
/// <para>The three English-only models ignore <see cref="DictationOptions.Language"/> entirely.
/// <see cref="LargeV3Turbo"/> is the only multilingual one, so it is the only one for which
/// <see cref="DictationOptions.Language"/> = "auto" (or "de") changes the result. At about 1.6 GB it expects a GPU
/// runtime package to be referenced; on CPU alone it works but is far too slow for dictation.</para></summary>
public enum WhisperModel { TinyEnglish, BaseEnglish, SmallEnglish, LargeV3Turbo }

public sealed class DictationOptions
{
    /// <summary>Folder holding the ggml model files. Required. Created on first download.</summary>
    public string ModelFolder { get; set; } = "";

    public WhisperModel Model { get; set; } = WhisperModel.LargeV3Turbo;

    /// <summary>Whisper language code, or "auto" to detect it per clip (the user mixes German and English).
    /// The English-only models ignore anything else.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Clips shorter than this return empty text without running Whisper. Whisper
    /// invents words on silence, so a stray click must not produce "Thank you."</summary>
    public TimeSpan MinimumClip { get; set; } = TimeSpan.FromMilliseconds(500);
}
