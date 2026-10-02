using System.Collections.ObjectModel;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.Voice.Speech;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Voice;

/// <summary>Where the voice setup is: picking, installing the engine picked, done, or failed.</summary>
public enum VoiceSetupStep
{
    Choosing,
    Installing,
    Ready,
    Failed,
}

/// <summary>
/// The voice setup (#85): pick an engine and a voice, hear the voices, and install the engine now with its progress in
/// view, rather than the first time Raven answers. Opens by itself the first time the Raven panel is used, and from
/// Settings. Skipped, Raven answers in text until an engine is picked. An install goes on when the window is closed;
/// Cancel gives it up and puts back the engine picked before.
/// </summary>
public sealed partial class VoiceSetupViewModel : ObservableObject, IDisposable
{
    private readonly SettingsViewModel _settings;
    private readonly SpeechEngines _engines;
    private readonly IVoiceSamples _samples;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<VoiceSetupViewModel> _logger;
    private readonly EventHandler<EngineStatus> _onEngineStatus;
    private CancellationTokenSource? _playing;

    /// <summary>The engine picked before this install, put back by Cancel.</summary>
    private string? _engineBefore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceHeading), nameof(InstallText))]
    private EngineCard _selectedCard;

    [ObservableProperty] private VoiceRow? _selectedVoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing), nameof(IsInstalling), nameof(IsReady), nameof(IsFailed))]
    private VoiceSetupStep _step = VoiceSetupStep.Choosing;

    /// <summary>The footer's line while installing, when ready, or why it failed.</summary>
    [ObservableProperty] private string _footerText;

    /// <summary>"142 of 330 MB" while a download of known size runs.</summary>
    [ObservableProperty] private string? _amount;

    /// <summary>0..1 while a download of known size runs; null shows the bar as busy.</summary>
    [ObservableProperty] private double? _progress;

    public VoiceSetupViewModel(SettingsViewModel settings, SpeechEngines engines, VoiceStatusViewModel status, IVoiceSamples samples,
        IUiDispatcher ui, ILogger<VoiceSetupViewModel> logger)
    {
        _settings = settings;
        _engines = engines;
        _samples = samples;
        _ui = ui;
        _logger = logger;
        Status = status;
        Cards =
        [
            new EngineCard(SpeechEngine.Kokoro, "Small and quick. Runs on any PC, no graphics card needed.", ["~450 MB", "CPU", "English"], recommended: true),
            new EngineCard(SpeechEngine.Qwen, "More natural and expressive. Needs an NVIDIA graphics card.", ["~5 GB", "GPU", "0.6B / 1.7B"], recommended: false),
        ];
        _selectedCard = Cards.First(c => c.Engine == (settings.Engine ?? SpeechEngine.Kokoro));
        _footerText = ChoosingText;
        ShowVoices();
        ShowLamps();
        _onEngineStatus = (_, status) => _ui.Post(() => OnEngineStatus(status));
        _engines.EngineStatusChanged += _onEngineStatus;
        Status.PropertyChanged += OnLampChanged;
    }

    public VoiceStatusViewModel Status { get; }

    public IReadOnlyList<EngineCard> Cards { get; }

    public ObservableCollection<VoiceRow> Voices { get; } = [];

    public bool IsChoosing => Step == VoiceSetupStep.Choosing;

    public bool IsInstalling => Step == VoiceSetupStep.Installing;

    public bool IsReady => Step == VoiceSetupStep.Ready;

    public bool IsFailed => Step == VoiceSetupStep.Failed;

    public string VoiceHeading => $"VOICE · {SelectedCard.Name.ToUpperInvariant()}";

    /// <summary>"Install Kokoro", or "Use Kokoro" once it is on this PC.</summary>
    public string InstallText => (SelectedCard.Lamp.Dot is ModelDot.Red ? "Install " : "Use ") + SelectedCard.Name;

    /// <summary>"Skip for now" while Raven only writes; "Close" once it speaks with an engine.</summary>
    public string CloseText => _settings.Engine is null ? "Skip for now" : "Close";

    /// <summary>The footer's line while picking.</summary>
    private string ChoosingText => _settings.Engine is { } engine
        ? $"Raven speaks with {ModelLamp.NameOf(engine)} now. Pick another engine or voice, or close."
        : "Skip, and Raven answers in text until you choose.";

    /// <summary>Raised when the window should close.</summary>
    public event Action? CloseRequested;

    partial void OnSelectedCardChanged(EngineCard value)
    {
        foreach (var card in Cards)
        {
            card.IsSelected = card == value;
        }

        StopSample();
        // Before the voices: in Ready the voice picked applies at once, and the card only looked at must not pick its engine.
        if (Step is VoiceSetupStep.Ready or VoiceSetupStep.Failed)
        {
            Step = VoiceSetupStep.Choosing; // another engine: its own install, or use
            FooterText = ChoosingText;
        }

        ShowVoices();
    }

    partial void OnSelectedVoiceChanged(VoiceRow? value)
    {
        foreach (var row in Voices)
        {
            row.IsSelected = row == value;
        }

        // Another voice of the engine in use, ready, applies at once, also after a look at the other card or when the
        // dialog was opened from Settings: Close must not drop it. A card only looked at picks nothing.
        var engine = SelectedCard.Engine;
        if (value is not null && Step is VoiceSetupStep.Ready or VoiceSetupStep.Choosing && engine == _settings.Engine
            && _engines.StatusOf(engine).State == TextToSpeechState.Ready && value.Voice.Id != _settings.VoiceOf(engine))
        {
            _settings.PickVoice(engine, value.Voice.Id);
            Step = VoiceSetupStep.Ready;
            FooterText = $"{SelectedCard.Name} is ready. Raven speaks with {value.Voice.Name} from now on.";
        }
    }

    private void ShowVoices()
    {
        var engine = SelectedCard.Engine;
        var picked = _settings.VoiceOf(engine);
        Voices.Clear();
        foreach (var voice in SpeechSettings.VoicesOf(engine))
        {
            Voices.Add(new VoiceRow(voice, PlayAsync));
        }

        SelectedVoice = Voices.FirstOrDefault(v => v.Voice.Id == picked) ?? Voices[0];
        foreach (var card in Cards)
        {
            card.IsSelected = card == SelectedCard;
        }
    }

    /// <summary>The cards' dots are the ones <see cref="VoiceStatusViewModel"/> keeps.</summary>
    private void ShowLamps()
    {
        foreach (var card in Cards)
        {
            card.Lamp = Status.LampOf(card.Engine);
        }

        OnPropertyChanged(nameof(InstallText));
    }

    private void OnLampChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VoiceStatusViewModel.Kokoro) or nameof(VoiceStatusViewModel.Qwen))
        {
            ShowLamps();
        }
    }

    /// <summary>Picks the engine and voice, and installs and starts the engine; the footer follows it.</summary>
    [RelayCommand]
    private void Install()
    {
        StopSample();
        var engine = SelectedCard.Engine;
        _engineBefore ??= _settings.RavenVoiceEngine;
        _settings.PickVoice(engine, SelectedVoice?.Voice.Id ?? SpeechSettings.DefaultVoiceOf(engine));
        OnPropertyChanged(nameof(CloseText));
        Step = VoiceSetupStep.Installing;
        FooterText = $"Installing {SelectedCard.Name}: starting";
        Amount = null;
        Progress = null;
        _engines.Install(engine);
        OnEngineStatus(new EngineStatus(engine, _engines.StatusOf(engine))); // one ready already says nothing more
    }

    /// <summary>Gives the install up, and puts back the engine picked before.</summary>
    [RelayCommand]
    private void Cancel()
    {
        _engines.Cancel(SelectedCard.Engine);
        if (_engineBefore is { } before)
        {
            _settings.RavenVoiceEngine = before;
            _engineBefore = null;
            OnPropertyChanged(nameof(CloseText));
        }

        Step = VoiceSetupStep.Choosing;
        FooterText = ChoosingText;
        Amount = null;
        Progress = null;
    }

    [RelayCommand]
    private void Close()
    {
        StopSample();
        CloseRequested?.Invoke();
    }

    /// <summary>The footer follows the install of the engine picked here, with its download's progress.</summary>
    private void OnEngineStatus(EngineStatus status)
    {
        if (Step is not (VoiceSetupStep.Installing or VoiceSetupStep.Failed) || status.Engine != SelectedCard.Engine)
        {
            return;
        }

        var name = SelectedCard.Name;
        switch (status.Status.State)
        {
            case TextToSpeechState.Installing:
                Step = VoiceSetupStep.Installing;
                FooterText = $"Installing {name}: {status.Status.Detail}";
                Amount = status.Status.Bytes?.ToString();
                Progress = status.Status.Bytes?.Fraction;
                break;
            case TextToSpeechState.Loading:
                Step = VoiceSetupStep.Installing;
                FooterText = $"Loading {name}: {status.Status.Detail ?? "starting"}";
                Amount = null;
                Progress = null;
                break;
            case TextToSpeechState.Ready:
                Step = VoiceSetupStep.Ready;
                _engineBefore = null;
                FooterText = $"{name} is ready. Raven speaks with {SelectedVoice?.Voice.Name ?? "it"} from now on.";
                Amount = null;
                Progress = null;
                break;
            case TextToSpeechState.Failed:
                Step = VoiceSetupStep.Failed;
                FooterText = $"{name} could not get ready: {status.Status.Detail}";
                Amount = null;
                Progress = null;
                break;
        }
    }

    /// <summary>Plays the voice's sample, or stops it when it plays; one at a time.</summary>
    private async Task PlayAsync(VoiceRow row)
    {
        var wasPlaying = row.IsPlaying;
        StopSample();
        if (wasPlaying)
        {
            return;
        }

        SelectedVoice = row;
        using var playing = new CancellationTokenSource();
        _playing = playing;
        row.IsPlaying = true;
        try
        {
            await _samples.PlayAsync(SelectedCard.Engine, row.Voice.Id, playing.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The sample of {Voice} could not be played", row.Voice.Id);
        }
        finally
        {
            row.IsPlaying = false;
            if (_playing == playing)
            {
                _playing = null;
            }
        }
    }

    private void StopSample()
    {
        _playing?.Cancel();
        _playing = null;
        foreach (var row in Voices)
        {
            row.IsPlaying = false;
        }
    }

    public void Dispose()
    {
        StopSample();
        _engines.EngineStatusChanged -= _onEngineStatus;
        Status.PropertyChanged -= OnLampChanged;
    }
}

/// <summary>One engine's card in the voice setup.</summary>
public sealed partial class EngineCard(SpeechEngine engine, string pitch, IReadOnlyList<string> tags, bool recommended) : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private ModelLamp _lamp = ModelLamp.Of(TextToSpeechStatus.Off, engine);

    public SpeechEngine Engine { get; } = engine;

    public string Name => ModelLamp.NameOf(Engine);

    public string Pitch { get; } = pitch;

    /// <summary>Size, hardware and what else sets it apart, as chips.</summary>
    public IReadOnlyList<string> Tags { get; } = tags;

    public bool Recommended { get; } = recommended;
}

/// <summary>One voice in the voice setup's list.</summary>
public sealed partial class VoiceRow : ObservableObject
{
    public VoiceRow(SpeechVoice voice, Func<VoiceRow, Task> play)
    {
        Voice = voice;
        PlayCommand = new AsyncRelayCommand(() => play(this), AsyncRelayCommandOptions.AllowConcurrentExecutions);
    }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isPlaying;

    public SpeechVoice Voice { get; }

    /// <summary>Plays its sample, or stops it.</summary>
    public IAsyncRelayCommand PlayCommand { get; }
}
