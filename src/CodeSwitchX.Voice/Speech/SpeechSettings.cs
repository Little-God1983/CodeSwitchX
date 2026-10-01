namespace CodeSwitchX.Voice.Speech;

/// <summary>The Qwen3-TTS models Raven can speak with.</summary>
public enum SpeechModel
{
    /// <summary>Qwen3-TTS 0.6B: the faster one.</summary>
    Small,

    /// <summary>Qwen3-TTS 1.7B: sounds better, takes more memory and time.</summary>
    Large,
}

/// <summary>A preset voice of the Qwen3-TTS CustomVoice models.</summary>
/// <param name="Id">The name the model knows it by.</param>
public sealed record SpeechVoice(string Id, string Name, string Description);

/// <summary>How Raven speaks; Settings changes it while the app runs. Read at every sentence.</summary>
public sealed class SpeechSettings
{
    public const string DefaultVoice = "ryan";

    /// <summary>The preset voices, the English ones first: Raven answers in English.</summary>
    public static readonly IReadOnlyList<SpeechVoice> Voices =
    [
        new("ryan", "Ryan", "English, male, dynamic"),
        new("aiden", "Aiden", "English, male, sunny American"),
        new("vivian", "Vivian", "Chinese, female, bright"),
        new("serena", "Serena", "Chinese, female, warm"),
        new("uncle_fu", "Uncle Fu", "Chinese, male, low and mellow"),
        new("dylan", "Dylan", "Beijing, male, clear"),
        new("eric", "Eric", "Sichuan, male, slightly husky"),
        new("ono_anna", "Ono Anna", "Japanese, female, playful"),
        new("sohee", "Sohee", "Korean, female, warm"),
    ];

    private volatile string _voice = DefaultVoice;
    private volatile int _model = (int)SpeechModel.Small;

    /// <summary>Raised when <see cref="Model"/> changes: the engine restarts with the new one.</summary>
    public event EventHandler? ModelChanged;

    /// <summary>One of <see cref="Voices"/>; anything else is <see cref="DefaultVoice"/>.</summary>
    public string Voice
    {
        get => _voice;
        set => _voice = Voices.Any(v => v.Id == value) ? value : DefaultVoice;
    }

    public SpeechModel Model
    {
        get => (SpeechModel)_model;
        set
        {
            if (Interlocked.Exchange(ref _model, (int)value) != (int)value)
            {
                ModelChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The Hugging Face id of a model.</summary>
    public static string ModelId(SpeechModel model) => model switch
    {
        SpeechModel.Large => "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice",
        _ => "Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice",
    };
}
