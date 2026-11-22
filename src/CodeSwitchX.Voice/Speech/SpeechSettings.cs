using LittleGod.Speech;
using LittleGod.Speech.Kokoro;
using LittleGod.Speech.QwenTts;
using LittleGod.Speech.Sidecar;

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

/// <summary>
/// How Raven speaks, as Settings picked it: the app's engine, voices and model, given to the package's
/// <see cref="SpeechEngines"/> at once on every change (#328). The one mapping from the app's enums to the engines' keys,
/// voices and model ids; nothing else sets them.
/// </summary>
public sealed class SpeechSettings
{
    /// <summary>
    /// How many sentences of a reply Raven says; the panel shows the rest ("And 2 more."). The reply voice is given it
    /// (<c>AddSpeaking</c>), and the panel counts against it.
    /// </summary>
    public const int MaximumSentences = 3;

    /// <summary>No engine: -1.</summary>
    private volatile int _engine = -1;
    private volatile string _qwenVoice = DefaultQwenVoice;
    private volatile string _kokoroVoice = DefaultKokoroVoice;
    private volatile int _model = (int)SpeechModel.Small;
    private readonly SpeechEngines _engines;

    public SpeechSettings(SpeechEngines engines)
    {
        _engines = engines;
        _engines.Picked = null;
        Apply(SpeechEngine.Kokoro, _kokoroVoice);
        Apply(SpeechEngine.Qwen, _qwenVoice);
        _engines[KeyOf(SpeechEngine.Qwen)].Model = ModelId(Model);
    }

    public static string DefaultQwenVoice => QwenTtsEngine.Voices.Default;

    public static string DefaultKokoroVoice => KokoroEngine.Voices.Default;

    /// <summary>Raised when <see cref="Engine"/> changes: the one picked before stops.</summary>
    public event EventHandler? EngineChanged;

    /// <summary>The voices of <paramref name="engine"/>, in the order Settings lists them: the English ones first.</summary>
    public static IReadOnlyList<SpeechVoice> VoicesOf(SpeechEngine engine) => CatalogOf(engine).All;

    public static VoiceCatalog CatalogOf(SpeechEngine engine) => engine == SpeechEngine.Kokoro ? KokoroEngine.Voices : QwenTtsEngine.Voices;

    public static string DefaultVoiceOf(SpeechEngine engine) => CatalogOf(engine).Default;

    /// <summary>The key <see cref="SpeechEngines"/> knows <paramref name="engine"/> by.</summary>
    public static string KeyOf(SpeechEngine engine) =>
        engine == SpeechEngine.Kokoro ? KokoroEngine.Definition.Key : QwenTtsEngine.Definition.Key;

    /// <summary>The Hugging Face id of a Qwen3-TTS model.</summary>
    public static string ModelId(SpeechModel model) => model == SpeechModel.Large ? QwenTtsModels.Large : QwenTtsModels.Small;

    /// <summary>The engine Raven speaks with; none (Raven answers in text) until one is picked.</summary>
    public SpeechEngine? Engine
    {
        get => _engine < 0 ? null : (SpeechEngine)_engine;
        set
        {
            var engine = value is { } picked && Enum.IsDefined(picked) ? (int)picked : -1;
            if (Interlocked.Exchange(ref _engine, engine) != engine)
            {
                _engines.Picked = engine < 0 ? null : KeyOf((SpeechEngine)engine);
                EngineChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>One of Qwen3-TTS's voices; anything else is <see cref="DefaultQwenVoice"/>.</summary>
    public string QwenVoice
    {
        get => _qwenVoice;
        set => Apply(SpeechEngine.Qwen, _qwenVoice = Known(SpeechEngine.Qwen, value));
    }

    /// <summary>One of Kokoro's voices; anything else is <see cref="DefaultKokoroVoice"/>.</summary>
    public string KokoroVoice
    {
        get => _kokoroVoice;
        set => Apply(SpeechEngine.Kokoro, _kokoroVoice = Known(SpeechEngine.Kokoro, value));
    }

    /// <summary>The Qwen3-TTS model; Kokoro has one only. A change restarts Qwen3-TTS with it.</summary>
    public SpeechModel Model
    {
        get => (SpeechModel)_model;
        set
        {
            var model = Enum.IsDefined(value) ? value : SpeechModel.Small;
            if (Interlocked.Exchange(ref _model, (int)model) != (int)model)
            {
                _engines[KeyOf(SpeechEngine.Qwen)].Model = ModelId(model);
            }
        }
    }

    /// <summary>The engine refuses a voice it does not have: an unknown one falls back to the default before it gets there.</summary>
    private static string Known(SpeechEngine engine, string? voice) =>
        VoicesOf(engine).FirstOrDefault(v => v.Id == voice)?.Id ?? DefaultVoiceOf(engine);

    private void Apply(SpeechEngine engine, string voice) => _engines[KeyOf(engine)].Voice = voice;
}
