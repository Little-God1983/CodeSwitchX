using System.Collections.ObjectModel;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Raven;

/// <summary>The Raven panel: push-to-talk dictation into a log, with the microphone choice and its failures explained.</summary>
public sealed partial class RavenPanelViewModel : ObservableObject
{
    public const string IdleCaption = "Hold Ctrl+Shift+Space or the mic button to talk.";

    /// <summary>The recorder stops capturing at this length without telling anyone, so the panel ends the recording itself.</summary>
    public static readonly TimeSpan MaximumRecording = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan MinimumClip = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMilliseconds(50);

    private readonly IMicrophoneCatalog _catalog;
    private readonly IMicrophoneRecorder _recorder;
    private readonly IDictationService _dictation;
    private readonly IWhisperModelStore _models;
    private readonly IDictationVocabularyProvider _vocabulary;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<RavenPanelViewModel> _logger;
    private readonly PushToTalkGesture _gesture;
    private readonly SilentMicWatch _silence = new();

    private ITimer? _limitTimer;
    private MicrophoneDevice? _recordingMic;
    private bool _silentWarned;
    private bool _refreshing;
    private bool _fellBack;
    private long _recordingId;

    public RavenPanelViewModel(IMicrophoneCatalog catalog, IMicrophoneRecorder recorder, IDictationService dictation,
        IWhisperModelStore models, IDictationVocabularyProvider vocabulary, IUiDispatcher dispatcher, TimeProvider time,
        ILogger<RavenPanelViewModel> logger)
    {
        _catalog = catalog;
        _recorder = recorder;
        _dictation = dictation;
        _models = models;
        _vocabulary = vocabulary;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        _gesture = new PushToTalkGesture(time);

        _recorder.BlockCaptured += (_, rms) => _dispatcher.Post(() => OnBlock(rms));
        _recorder.Failed += (_, error) => _dispatcher.Post(() => OnCaptureFailed(error));
        _catalog.DevicesChanged += (_, _) => _dispatcher.Post(RefreshMicrophones);
    }

    [ObservableProperty]
    private bool _isOpen = true;

    /// <summary>The microphone recorded from: the preferred one while it is plugged in, the Windows default otherwise.</summary>
    [ObservableProperty]
    private MicrophoneDevice? _selectedMicrophone;

    /// <summary>
    /// The user's choice: the one stored in the settings, or the one last picked. Only a pick changes it, never a
    /// fallback, so the choice is selected again when its device comes back (RØDE Connect started after CodeSwitchX,
    /// a Bluetooth headset waking up). Null means "no choice": the Windows default.
    /// </summary>
    [ObservableProperty]
    private MicrophoneDevice? _preferredMicrophone;

    [ObservableProperty]
    private RavenState _state;

    /// <summary>0..1, live while listening.</summary>
    [ObservableProperty]
    private double _level;

    [ObservableProperty]
    private string _caption = IdleCaption;

    [ObservableProperty]
    private string _typedText = "";

    public ObservableCollection<MicrophoneDevice> Microphones { get; } = [];

    public ObservableCollection<RavenLogEntry> Log { get; } = [];

    /// <summary>Re-lists the microphones and applies the stored choice; the shell calls this once the settings are loaded.</summary>
    public void RefreshMicrophones()
    {
        var devices = _catalog.List();
        var previous = SelectedMicrophone;
        var preferred = PreferredMicrophone;
        MicrophoneChoiceResult choice;
        // A list-bound ComboBox writes null into the selection while the list is cleared; that is no pick.
        _refreshing = true;
        try
        {
            Microphones.Clear();
            foreach (var device in devices)
            {
                Microphones.Add(device);
            }

            choice = MicrophoneChoice.Resolve(devices, preferred, _catalog.Default());
            SelectedMicrophone = choice.Device;
        }
        finally
        {
            _refreshing = false;
        }

        if (preferred is null || choice.Device is null || Equals(previous, choice.Device))
        {
            return;
        }

        if (choice.Outcome == MicrophoneChoiceOutcome.FellBackToDefault)
        {
            _fellBack = true;
            AddEntry(RavenLogKind.Note, $"{preferred.Name} is gone. Using {choice.Device.Name}.");
        }
        else if (_fellBack)
        {
            _fellBack = false;
            AddEntry(RavenLogKind.Note, $"Using {choice.Device.Name} again.");
        }
    }

    partial void OnSelectedMicrophoneChanged(MicrophoneDevice? value)
    {
        if (!_refreshing && value is not null)
        {
            PreferredMicrophone = value;
        }
    }

    [RelayCommand]
    private void TogglePanel() => IsOpen = !IsOpen;

    /// <summary>Button mouse-down or hotkey down.</summary>
    public void PressMic()
    {
        if (State == RavenState.Transcribing)
        {
            return;
        }

        switch (_gesture.Press())
        {
            case PushToTalkAction.Start:
                StartRecording();
                break;
            case PushToTalkAction.Stop:
                _ = StopAsync();
                break;
        }
    }

    /// <summary>Button mouse-up or hotkey up; completes once a stopped recording has been transcribed.</summary>
    public Task ReleaseMicAsync()
    {
        return _gesture.Release() == PushToTalkAction.Stop ? StopAsync() : Task.CompletedTask;
    }

    [RelayCommand]
    private void SubmitTyped()
    {
        var text = TypedText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        AddEntry(RavenLogKind.You, text);
        TypedText = "";
    }

    public void Note(string text) => AddEntry(RavenLogKind.Note, text);

    private void StartRecording()
    {
        if (SelectedMicrophone is not { } mic)
        {
            _gesture.Reset();
            AddEntry(RavenLogKind.Warning, "No microphone found. Plug one in or check Windows sound settings.");
            return;
        }

        try
        {
            _recorder.Start(mic.Id);
        }
        catch (MicrophoneException ex)
        {
            _logger.LogWarning(ex, "Could not start recording from {Microphone}", mic.Name);
            _gesture.Reset();
            AddEntry(RavenLogKind.Warning, WarningFor(ex.Kind, mic));
            return;
        }

        _recordingMic = mic;
        _silentWarned = false;
        _silence.Reset();
        State = RavenState.Listening;
        Caption = "Listening…";
        _ = _dictation.WarmUpAsync(CancellationToken.None);

        var id = ++_recordingId;
        _limitTimer?.Dispose();
        _limitTimer = _time.CreateTimer(_ => _dispatcher.Post(() => OnLimitReached(id)), null, MaximumRecording, Timeout.InfiniteTimeSpan);
    }

    private void OnLimitReached(long id)
    {
        if (id != _recordingId || State != RavenState.Listening)
        {
            return;
        }

        _gesture.Reset();
        _ = StopAsync();
    }

    private void OnBlock(float rms)
    {
        if (State != RavenState.Listening)
        {
            return;
        }

        Level = AudioMath.LevelOf(rms);
        if (_silence.Step(rms, BlockDuration) == SignalEvent.Silent && !_silentWarned)
        {
            _silentWarned = true;
            AddEntry(RavenLogKind.Warning, $"No sound from {_recordingMic?.Name}. Check that it isn't muted.");
        }
    }

    private void OnCaptureFailed(MicrophoneException error)
    {
        _logger.LogWarning(error, "Microphone capture failed");
        if (State == RavenState.Listening)
        {
            AddEntry(RavenLogKind.Warning, WarningFor(error.Kind, _recordingMic));
            StopLimitTimer();
            _recorder.Stop();
            ReturnToIdle();
            _gesture.Reset();
        }

        RefreshMicrophones();
    }

    private async Task StopAsync()
    {
        if (State != RavenState.Listening)
        {
            return;
        }

        StopLimitTimer();
        var clip = _recorder.Stop();
        State = RavenState.Transcribing;
        Caption = "Transcribing…";
        try
        {
            if (clip.Length < MinimumClip)
            {
                return;
            }

            if (!_models.IsPresent && !await DownloadModelAsync())
            {
                return;
            }

            var words = await _vocabulary.GetAsync(CancellationToken.None);
            var result = await _dictation.TranscribeAsync(clip.Samples16k, words, live: false, CancellationToken.None);
            var text = result.Text.Trim();
            if (text.Length > 0)
            {
                AddEntry(RavenLogKind.You, text);
            }
        }
        catch (DictationModelLoadException ex)
        {
            _logger.LogWarning(ex, "The speech model could not be loaded");
            AddEntry(RavenLogKind.Warning, $"The speech model could not be loaded: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transcription failed");
            AddEntry(RavenLogKind.Warning, $"Transcription failed: {ex.Message}");
        }
        finally
        {
            ReturnToIdle();
        }
    }

    private async Task<bool> DownloadModelAsync()
    {
        const string Prefix = "Downloading the speech model (1.6 GB)… ";
        var entry = AddEntry(RavenLogKind.Note, Prefix + "0%");
        var last = 0;
        var progress = new PostedProgress(_dispatcher, value =>
        {
            var percent = (int)Math.Clamp(value * 100, 0, 100);
            if (percent != last)
            {
                last = percent;
                entry.Text = $"{Prefix}{percent}%";
            }
        });

        try
        {
            await _models.DownloadAsync(progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The speech model could not be downloaded");
            AddEntry(RavenLogKind.Warning, $"The speech model could not be downloaded: {ex.Message}. Press the mic to try again.");
            return false;
        }

        entry.Text = "Speech model downloaded.";
        return true;
    }

    private void StopLimitTimer()
    {
        _recordingId++;
        _limitTimer?.Dispose();
        _limitTimer = null;
    }

    private void ReturnToIdle()
    {
        State = RavenState.Idle;
        Level = 0;
        Caption = IdleCaption;
    }

    private RavenLogEntry AddEntry(RavenLogKind kind, string text)
    {
        var entry = new RavenLogEntry(kind, text, _time.GetUtcNow());
        Log.Add(entry);
        return entry;
    }

    private static string WarningFor(MicrophoneFailureKind kind, MicrophoneDevice? mic) => kind switch
    {
        MicrophoneFailureKind.Denied =>
            "Windows is blocking microphone access. Turn on Settings → Privacy & security → Microphone → Let desktop apps access your microphone.",
        MicrophoneFailureKind.Missing => $"{mic?.Name ?? "The microphone"} is not available any more.",
        _ => $"{mic?.Name ?? "The microphone"} could not be opened. Another app may be using it exclusively.",
    };

    /// <summary>Reports on the UI thread, whatever thread the download runs on.</summary>
    private sealed class PostedProgress(IUiDispatcher dispatcher, Action<double> onValue) : IProgress<double>
    {
        public void Report(double value) => dispatcher.Post(() => onValue(value));
    }
}
