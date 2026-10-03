using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Speech;

namespace CodeSwitchX.UI.Voice;

/// <summary>The colour of a model's dot.</summary>
public enum ModelDot
{
    /// <summary>Not downloaded, or failed.</summary>
    Red,

    /// <summary>On disk, not loaded (asleep), or nothing to load: no engine picked.</summary>
    Grey,

    /// <summary>Downloading, installing or loading.</summary>
    Yellow,

    Green,
}

/// <summary>A model's dot and what it says beside it and in its tooltip.</summary>
/// <param name="Text">A word or two beside the dot: "ready", "asleep".</param>
/// <param name="Detail">The tooltip: what it means, and what happens next.</param>
/// <param name="Progress">How far a download is, 0..1, when that is known.</param>
public sealed record ModelLamp(ModelDot Dot, string Text, string Detail, double? Progress = null)
{
    public static string NameOf(SpeechEngine engine) => engine == SpeechEngine.Kokoro ? "Kokoro" : "Qwen3-TTS";

    public static string NameOf(WhisperModel model) => model switch
    {
        WhisperModel.TinyEnglish => "Whisper Tiny",
        WhisperModel.BaseEnglish => "Whisper Base",
        WhisperModel.SmallEnglish => "Whisper Small",
        _ => "Whisper Large v3 Turbo",
    };

    /// <summary>The lamp of <paramref name="engine"/>, or of the voice when none is picked (<see cref="TextToSpeechState.NoEngine"/>).</summary>
    public static ModelLamp Of(TextToSpeechStatus status, SpeechEngine? engine)
    {
        var name = engine is { } e ? NameOf(e) : "Raven's voice";
        var bytes = status.Bytes is { } b ? $" ({b})" : "";
        return status.State switch
        {
            TextToSpeechState.NoEngine => new(ModelDot.Grey, "text only",
                "No voice is picked: Raven answers in text. Pick one in Settings → Voice."),
            TextToSpeechState.NotInstalled => new(ModelDot.Red, "not downloaded",
                $"{name} is not on this PC yet. It is installed when Raven first speaks, or from Settings → Voice."),
            TextToSpeechState.Off => new(ModelDot.Grey, "asleep", $"{name} is on this PC; it loads when Raven next speaks."),
            TextToSpeechState.Installing => new(ModelDot.Yellow, "installing", $"Installing {name}: {status.Detail}{bytes}", status.Bytes?.Fraction),
            TextToSpeechState.Loading => new(ModelDot.Yellow, "loading",
                $"Loading {name}{(engine == SpeechEngine.Qwen ? " onto the graphics card" : "")}: {status.Detail ?? "starting"}"),
            TextToSpeechState.Ready => new(ModelDot.Green, "ready", $"{name} is loaded and ready."),
            _ => new(ModelDot.Red, "failed", $"{name} could not get ready: {status.Detail}"),
        };
    }

    public static ModelLamp Of(DictationStatus status)
    {
        var name = NameOf(status.Model);
        return status.State switch
        {
            DictationState.NotDownloaded => new(ModelDot.Red, "not downloaded",
                $"{name} is not on this PC yet. It downloads with your first dictation, or from Settings."),
            DictationState.Downloading => new(ModelDot.Yellow, status.Bytes is { } b ? $"downloading {b.Fraction:P0}" : "downloading",
                $"Downloading {name}{(status.Bytes is { } d ? $": {d}" : "")}", status.Bytes?.Fraction),
            DictationState.Asleep => new(ModelDot.Grey, "asleep", $"{name} is on this PC; it loads with your next dictation."),
            DictationState.Loading => new(ModelDot.Yellow, "loading", $"Loading {name}."),
            DictationState.Ready => new(ModelDot.Green, "ready", $"{name} is loaded and ready."),
            _ => new(ModelDot.Red, "failed", status.Detail ?? $"{name} could not be loaded."),
        };
    }
}
