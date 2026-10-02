using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Speech;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeSwitchX.UI.Voice;

/// <summary>
/// Where Raven's models stand, for the dots: the voice picked and each engine (the bottom bar, the voice setup, Settings),
/// and the speech-to-text model. Told by the engines and the dictation service on any thread; changes here on the UI thread.
/// </summary>
public sealed partial class VoiceStatusViewModel : ObservableObject
{
    private readonly SpeechEngines _engines;
    private readonly SpeechSettings _settings;
    private readonly IDictationService _dictation;
    private readonly IUiDispatcher _ui;

    /// <summary>The engine picked, or none; with its name for the bottom bar ("voice · Kokoro").</summary>
    [ObservableProperty] private ModelLamp _speech;
    [ObservableProperty] private string _speechName;
    [ObservableProperty] private ModelLamp _kokoro;
    [ObservableProperty] private ModelLamp _qwen;
    [ObservableProperty] private ModelLamp _listening;
    [ObservableProperty] private string _listeningName;

    /// <summary>Where the speech-to-text model stands, for what Settings offers (a Download button).</summary>
    [ObservableProperty] private DictationState? _listeningState;

    public VoiceStatusViewModel(SpeechEngines engines, SpeechSettings settings, IDictationService dictation, IUiDispatcher ui)
    {
        _engines = engines;
        _settings = settings;
        _dictation = dictation;
        _ui = ui;
        _speech = _kokoro = _qwen = ModelLamp.Of(TextToSpeechStatus.Off, null);
        _speechName = "voice";
        _listening = new ModelLamp(ModelDot.Grey, "…", "Looking whether the speech-to-text model is on this PC.");
        _listeningName = "speech to text";
        _engines.EngineStatusChanged += (_, _) => _ui.Post(ShowSpeech);
        _settings.EngineChanged += (_, _) => _ui.Post(ShowSpeech);
        _dictation.StatusChanged += (_, status) => _ui.Post(() => ShowListening(status));
        ShowSpeech();
    }

    public ModelLamp LampOf(SpeechEngine engine) => engine == SpeechEngine.Kokoro ? Kokoro : Qwen;

    /// <summary>Has the engines look whether they are on disk, and the speech-to-text model's state read off the disk.</summary>
    public void Start()
    {
        _engines.CheckInstalls();
        _ = Task.Run(() =>
        {
            var status = _dictation.Status;
            _ui.Post(() => ShowListening(status));
        });
    }

    private void ShowSpeech()
    {
        Kokoro = ModelLamp.Of(_engines.StatusOf(SpeechEngine.Kokoro), SpeechEngine.Kokoro);
        Qwen = ModelLamp.Of(_engines.StatusOf(SpeechEngine.Qwen), SpeechEngine.Qwen);
        var engine = _settings.Engine;
        Speech = engine is { } picked ? LampOf(picked) : ModelLamp.Of(SpeechEngines.NoEngine, null);
        SpeechName = engine is { } named ? $"voice · {ModelLamp.NameOf(named)}" : "voice";
    }

    private void ShowListening(DictationStatus status)
    {
        Listening = ModelLamp.Of(status);
        ListeningState = status.State;
        ListeningName = $"speech to text · {ModelLamp.NameOf(status.Model)}";
    }
}
