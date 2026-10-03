using System.Collections.ObjectModel;
using System.ComponentModel;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Voice;
using CodeSwitchX.Voice.Speech;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Settings;

/// <summary>
/// Settings → Voice (#105): the engine cards, each with where its engine stands and its own install and Cancel; the voices
/// of the engine picked, each with a sample to hear; and the Qwen3-TTS model. A card picked is the engine Raven speaks
/// with, and a voice picked the voice, at once and stored, as any setting. An engine not on this PC is installed from its
/// card, or else the first time Raven speaks. The first run opens this page with a welcome line.
/// </summary>
public sealed partial class VoicePageViewModel : ObservableObject, IDisposable
{
    private readonly SettingsViewModel _settings;
    private readonly SpeechEngines _engines;
    private readonly IVoiceSamples _samples;
    private readonly ILogger _logger;
    private CancellationTokenSource? _playing;
    private bool _showing;

    /// <summary>The engine picked: Raven speaks with it, or answers in text with the None card.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVoices), nameof(VoicesHeading))]
    private EngineCard _selectedCard;

    [ObservableProperty] private VoiceRow? _selectedVoice;

    /// <summary>The first run opened Settings here: a welcome line says what to do.</summary>
    [ObservableProperty] private bool _showWelcome;

    public VoicePageViewModel(SettingsViewModel settings, SpeechEngines engines, VoiceStatusViewModel status, IVoiceSamples samples, ILogger logger)
    {
        _settings = settings;
        _engines = engines;
        _samples = samples;
        _logger = logger;
        Status = status;
        Cards =
        [
            new EngineCard(null, "Raven answers in text only. Nothing to download.", ["No download"], recommended: false, Install, Cancel),
            new EngineCard(SpeechEngine.Kokoro, "Small and quick. Runs on any PC, no graphics card needed.", ["~450 MB", "CPU", "English"], recommended: true,
                Install, Cancel),
            new EngineCard(SpeechEngine.Qwen, "More natural and expressive. Needs an NVIDIA graphics card.", ["~5 GB", "GPU", "0.6B / 1.7B"], recommended: false,
                Install, Cancel),
        ];
        _selectedCard = CardOf(settings.Engine);
        ShowCards();
        ShowVoices();
        Status.PropertyChanged += OnLampChanged;
        _settings.PropertyChanged += OnSettingChanged;
    }

    public VoiceStatusViewModel Status { get; }

    public IReadOnlyList<EngineCard> Cards { get; }

    public ObservableCollection<VoiceRow> Voices { get; } = [];

    /// <summary>None picked: no voices, a line says Raven answers in text.</summary>
    public bool HasVoices => SelectedCard.Engine is not null;

    public string VoicesHeading => SelectedCard.Engine is { } engine ? $"{ModelLamp.NameOf(engine)} voices" : "";

    private EngineCard CardOf(SpeechEngine? engine) => Cards.First(c => c.Engine == engine);

    partial void OnSelectedCardChanged(EngineCard value)
    {
        if (_showing)
        {
            return;
        }

        StopSample();
        _settings.RavenVoiceEngine = value.Engine?.ToString() ?? SettingsViewModel.NoEngine;
        ShowCards();
        ShowVoices();
    }

    partial void OnSelectedVoiceChanged(VoiceRow? value)
    {
        foreach (var row in Voices)
        {
            row.IsSelected = row == value;
        }

        if (!_showing && value is not null && SelectedCard.Engine is { } engine)
        {
            _settings.PickVoice(engine, value.Voice.Id);
        }
    }

    /// <summary>The engine or a voice changed elsewhere (loaded, or by another view): the page follows.</summary>
    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.RavenVoiceEngine) && SelectedCard.Engine != _settings.Engine)
        {
            Show(() => SelectedCard = CardOf(_settings.Engine));
            StopSample();
            ShowCards();
            ShowVoices();
        }
        else if (e.PropertyName is nameof(SettingsViewModel.RavenKokoroVoice) or nameof(SettingsViewModel.RavenQwenVoice)
                 && SelectedCard.Engine is { } engine && SelectedVoice?.Voice.Id != _settings.VoiceOf(engine))
        {
            Show(() => SelectedVoice = Voices.FirstOrDefault(v => v.Voice.Id == _settings.VoiceOf(engine)));
        }
    }

    /// <summary>A change made to show the settings, not one the user made: nothing is picked or stored by it.</summary>
    private void Show(Action change)
    {
        _showing = true;
        try
        {
            change();
        }
        finally
        {
            _showing = false;
        }
    }

    private void ShowVoices()
    {
        Show(() =>
        {
            Voices.Clear();
            SelectedVoice = null;
            if (SelectedCard.Engine is not { } engine)
            {
                return;
            }

            var picked = _settings.VoiceOf(engine);
            foreach (var voice in SpeechSettings.VoicesOf(engine))
            {
                Voices.Add(new VoiceRow(voice, PlayAsync));
            }

            SelectedVoice = Voices.FirstOrDefault(v => v.Voice.Id == picked) ?? Voices[0];
        });
    }

    /// <summary>Each card shows where its engine stands; the dots are the ones <see cref="VoiceStatusViewModel"/> keeps.</summary>
    private void ShowCards()
    {
        foreach (var card in Cards)
        {
            card.IsSelected = card == SelectedCard;
            if (card.Engine is { } engine)
            {
                card.Show(_engines.StatusOf(engine), Status.LampOf(engine));
            }
        }
    }

    private void OnLampChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VoiceStatusViewModel.Kokoro) or nameof(VoiceStatusViewModel.Qwen))
        {
            ShowCards();
        }
    }

    /// <summary>Installs and starts the card's engine, picking it; the card shows the progress.</summary>
    private void Install(EngineCard card)
    {
        if (card.Engine is not { } engine)
        {
            return;
        }

        StopSample();
        if (SelectedCard != card)
        {
            SelectedCard = card;
        }

        _engines.Install(engine);
        ShowCards(); // an engine ready already tells nothing more
    }

    /// <summary>Gives the install up; the engine stays picked, and the card offers the install again.</summary>
    private void Cancel(EngineCard card)
    {
        if (card.Engine is { } engine)
        {
            _engines.Cancel(engine);
            ShowCards();
        }
    }

    /// <summary>Plays the voice's sample, or stops it when it plays; one at a time. The voice heard is the one picked.</summary>
    private async Task PlayAsync(VoiceRow row)
    {
        var wasPlaying = row.IsPlaying;
        StopSample();
        if (wasPlaying || SelectedCard.Engine is not { } engine)
        {
            return;
        }

        SelectedVoice = row;
        using var playing = new CancellationTokenSource();
        _playing = playing;
        row.IsPlaying = true;
        try
        {
            await _samples.PlayAsync(engine, row.Voice.Id, playing.Token);
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

    /// <summary>Stops a sample playing: another engine was picked, or Settings closed.</summary>
    public void StopSample()
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
        Status.PropertyChanged -= OnLampChanged;
        _settings.PropertyChanged -= OnSettingChanged;
    }
}

/// <summary>One engine's card on the Voice page; <see cref="Engine"/> null is the None card (Raven answers in text).</summary>
public sealed partial class EngineCard : ObservableObject
{
    public EngineCard(SpeechEngine? engine, string pitch, IReadOnlyList<string> tags, bool recommended, Action<EngineCard> install, Action<EngineCard> cancel)
    {
        Engine = engine;
        Pitch = pitch;
        Tags = tags;
        Recommended = recommended;
        InstallCommand = new RelayCommand(() => install(this));
        CancelCommand = new RelayCommand(() => cancel(this));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isSelected;

    /// <summary>The dot; the None card has none.</summary>
    [ObservableProperty] private ModelLamp? _lamp;

    /// <summary>"Ready", "Asleep · installed", "Not installed", "Installing · 142 of 325 MB".</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>Installing or loading: the card shows the progress and Cancel.</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>0..1 while a download of known size runs; null shows the bar as busy.</summary>
    [ObservableProperty] private double? _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(InstallText))]
    private TextToSpeechState _state = TextToSpeechState.Off;

    public SpeechEngine? Engine { get; }

    public string Name => Engine is { } engine ? ModelLamp.NameOf(engine) : "None";

    public string Pitch { get; }

    /// <summary>Size, hardware and what else sets it apart, as chips.</summary>
    public IReadOnlyList<string> Tags { get; }

    public bool Recommended { get; }

    /// <summary>The Qwen3-TTS model choice sits in its card, shown while it is picked.</summary>
    public bool HasModelChoice => Engine == SpeechEngine.Qwen;

    /// <summary>Picked and not on this PC, or failed: the card offers the install.</summary>
    public bool CanInstall => IsSelected && Engine is not null && State is TextToSpeechState.NotInstalled or TextToSpeechState.Failed;

    public string InstallText => State == TextToSpeechState.Failed ? "Try again" : $"Install {Name}";

    public IRelayCommand InstallCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public void Show(TextToSpeechStatus status, ModelLamp lamp)
    {
        Lamp = lamp;
        State = status.State;
        IsBusy = status.State is TextToSpeechState.Installing or TextToSpeechState.Loading;
        Progress = status.State == TextToSpeechState.Installing ? status.Bytes?.Fraction : null;
        StatusText = status.State switch
        {
            TextToSpeechState.Ready => "Ready",
            TextToSpeechState.Off => "Asleep · installed",
            TextToSpeechState.NotInstalled => "Not installed",
            TextToSpeechState.Installing => status.Bytes is { } bytes ? $"Installing · {bytes}" : $"Installing · {status.Detail ?? "starting"}",
            TextToSpeechState.Loading => $"Loading · {status.Detail ?? "starting"}",
            TextToSpeechState.Failed => $"Failed: {status.Detail}",
            _ => "",
        };
    }
}

/// <summary>One voice on the Voice page.</summary>
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
