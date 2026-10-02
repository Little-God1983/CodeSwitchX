namespace CodeSwitchX.Voice.Speech;

/// <summary>The engines Raven can speak with.</summary>
public enum SpeechEngine
{
    /// <summary>Kokoro-82M: small (about 450 MB with its environment), on the CPU.</summary>
    Kokoro,

    /// <summary>Qwen3-TTS: more natural, about 5 GB, needs an NVIDIA graphics card.</summary>
    Qwen,
}

/// <summary>The Qwen3-TTS models Raven can speak with.</summary>
public enum SpeechModel
{
    /// <summary>Qwen3-TTS 0.6B: the faster one.</summary>
    Small,

    /// <summary>Qwen3-TTS 1.7B: sounds better, takes more memory and time.</summary>
    Large,
}

/// <summary>A preset voice of an engine.</summary>
/// <param name="Id">The name the engine knows it by.</param>
public sealed record SpeechVoice(string Id, string Name, string Description);

/// <summary>How Raven speaks; the voice setup and Settings change it while the app runs. Read at every sentence.</summary>
public sealed class SpeechSettings
{
    public const string DefaultQwenVoice = "ryan";
    public const string DefaultKokoroVoice = "af_heart";

    /// <summary>The preset voices of the Qwen3-TTS CustomVoice models, the English ones first: Raven answers in English.</summary>
    public static readonly IReadOnlyList<SpeechVoice> QwenVoices =
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

    /// <summary>Kokoro's best English voices; its id says the accent ("a" American, "b" British) and the sex.</summary>
    public static readonly IReadOnlyList<SpeechVoice> KokoroVoices =
    [
        new("af_heart", "Heart", "American English, female, warm"),
        new("am_michael", "Michael", "American English, male, calm"),
        new("bf_emma", "Emma", "British English, female, clear"),
        new("bm_george", "George", "British English, male, deep"),
        new("af_bella", "Bella", "American English, female, bright"),
    ];

    /// <summary>No engine: -1.</summary>
    private volatile int _engine = -1;
    private volatile string _qwenVoice = DefaultQwenVoice;
    private volatile string _kokoroVoice = DefaultKokoroVoice;
    private volatile int _model = (int)SpeechModel.Small;

    /// <summary>Raised when <see cref="Model"/> changes: the engine restarts with the new one.</summary>
    public event EventHandler? ModelChanged;

    /// <summary>Raised when <see cref="Engine"/> changes: the one picked before stops.</summary>
    public event EventHandler? EngineChanged;

    public static IReadOnlyList<SpeechVoice> VoicesOf(SpeechEngine engine) => engine == SpeechEngine.Kokoro ? KokoroVoices : QwenVoices;

    public static string DefaultVoiceOf(SpeechEngine engine) => engine == SpeechEngine.Kokoro ? DefaultKokoroVoice : DefaultQwenVoice;

    /// <summary>The engine Raven speaks with; none (Raven answers in text) until one is picked.</summary>
    public SpeechEngine? Engine
    {
        get => _engine < 0 ? null : (SpeechEngine)_engine;
        set
        {
            var engine = value is { } picked && Enum.IsDefined(picked) ? (int)picked : -1;
            if (Interlocked.Exchange(ref _engine, engine) != engine)
            {
                EngineChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>One of <see cref="QwenVoices"/>; anything else is <see cref="DefaultQwenVoice"/>.</summary>
    public string QwenVoice
    {
        get => _qwenVoice;
        set => _qwenVoice = QwenVoices.Any(v => v.Id == value) ? value : DefaultQwenVoice;
    }

    /// <summary>One of <see cref="KokoroVoices"/>; anything else is <see cref="DefaultKokoroVoice"/>.</summary>
    public string KokoroVoice
    {
        get => _kokoroVoice;
        set => _kokoroVoice = KokoroVoices.Any(v => v.Id == value) ? value : DefaultKokoroVoice;
    }

    public string VoiceOf(SpeechEngine engine) => engine == SpeechEngine.Kokoro ? KokoroVoice : QwenVoice;

    /// <summary>The Qwen3-TTS model; Kokoro has one only.</summary>
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

    /// <summary>The model <paramref name="engine"/> speaks with, as its sidecar names it.</summary>
    public string ModelOf(SpeechEngine engine) => engine == SpeechEngine.Kokoro ? Kokoro.KokoroEnvironment.Model : ModelId(Model);

    /// <summary>The Hugging Face id of a Qwen3-TTS model.</summary>
    public static string ModelId(SpeechModel model) => model switch
    {
        SpeechModel.Large => "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice",
        _ => "Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice",
    };
}
