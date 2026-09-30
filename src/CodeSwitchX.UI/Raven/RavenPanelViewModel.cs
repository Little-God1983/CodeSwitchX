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
    /// <summary>Names the chord from <see cref="HotkeyService.PushToTalk"/>, so a new chord changes the hint with it.</summary>
    public static readonly string IdleCaption = $"Hold {HotkeyService.PushToTalk.Keys} or the mic button to talk.";

    /// <summary>The mic button's tooltip, with the chord from <see cref="HotkeyService.PushToTalk"/>.</summary>
    public static readonly string MicToolTip = $"Hold to talk, or tap to keep listening until the next tap ({HotkeyService.PushToTalk.Keys})";

    /// <summary>The recorder stops capturing at this length without telling anyone, so the panel ends the recording itself.</summary>
    public static readonly TimeSpan MaximumRecording = TimeSpan.FromSeconds(120);

    /// <summary>How long after startup the model is warmed up: long enough to leave the startup itself alone.</summary>
    public static readonly TimeSpan StartupWarmUpDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MinimumClip = TimeSpan.FromMilliseconds(500);

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
    private readonly SpeechGate _speech = new();

    private ITimer? _limitTimer;
    private ITimer? _warmUpTimer;
    private MicrophoneDevice? _recordingMic;
    private bool _silentWarned;
    private bool _refreshing;
    private bool _fellBack;
    private bool _audioFailed;
    private long _recordingId;
    private Task<DictationVocabulary> _vocabularyFetch = Task.FromResult(DictationVocabulary.Empty);

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

        _recorder.BlockCaptured += (_, block) => _dispatcher.Post(() => OnBlock(block));
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

    /// <summary>
    /// The stop in progress, if any: the recorder's Stop runs off the UI thread, and a press waits for it to finish
    /// (the recorder holds one capture at a time). Completed when nothing is stopping.
    /// </summary>
    internal Task PendingStop { get; private set; } = Task.CompletedTask;

    /// <summary>Re-lists the microphones and applies the stored choice; the shell calls this once the settings are loaded.</summary>
    public void RefreshMicrophones()
    {
        IReadOnlyList<MicrophoneDevice> devices;
        MicrophoneDevice? windowsDefault;
        try
        {
            devices = _catalog.List();
            windowsDefault = _catalog.Default();
        }
        catch (Exception ex)
        {
            // A stopped Windows audio service makes the enumeration throw (a COMException). The app still starts; the
            // panel says why it has no microphones. The stored choice stays for when the service is back.
            // Audiosrv stopping raises bursts of device notifications, each failing the same way: one warning per outage.
            _logger.LogWarning(ex, "Could not list the microphones");
            ClearMicrophones();
            if (!_audioFailed)
            {
                _audioFailed = true;
                AddEntry(RavenLogKind.Warning, $"Windows audio is not available: {ex.Message}");
            }

            return;
        }

        if (_audioFailed)
        {
            _audioFailed = false;
            AddEntry(RavenLogKind.Note, "Windows audio is back.");
        }

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

            choice = MicrophoneChoice.Resolve(devices, preferred, windowsDefault);
            SelectedMicrophone = choice.Device;
        }
        finally
        {
            _refreshing = false;
        }

        if (preferred is null || Equals(previous, choice.Device))
        {
            return;
        }

        if (choice.Device is null)
        {
            // previous is not null here: the microphone in use went, and nothing is left to fall back to.
            _fellBack = true;
            AddEntry(RavenLogKind.Note, $"{previous!.Name} is gone. No microphone is connected.");
        }
        else if (choice.Outcome == MicrophoneChoiceOutcome.FellBackToDefault)
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

    private void ClearMicrophones()
    {
        _refreshing = true;
        try
        {
            Microphones.Clear();
            SelectedMicrophone = null;
        }
        finally
        {
            _refreshing = false;
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

    /// <summary>
    /// Warms the model up <see cref="StartupWarmUpDelay"/> from now, if it is on disk by then, so that the first clip
    /// after launch does not pay seconds for loading it. Whether the panel is open or not: the hotkey works either way.
    /// The shell calls this once it has initialised.
    /// </summary>
    public void ScheduleWarmUp()
    {
        _warmUpTimer?.Dispose();
        _warmUpTimer = _time.CreateTimer(_ =>
        {
            if (_models.IsPresent)
            {
                WarmUpInBackground();
            }
        }, null, StartupWarmUpDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Never on the calling thread, however the service behaves: loading the model takes seconds.</summary>
    private void WarmUpInBackground() => _ = Task.Run(() => _dictation.WarmUpAsync(CancellationToken.None));

    /// <summary>Button mouse-down or hotkey down.</summary>
    public void PressMic()
    {
        if (State == RavenState.Transcribing || !PendingStop.IsCompleted)
        {
            return;
        }

        switch (_gesture.Press())
        {
            case PushToTalkAction.Start:
                StartRecording();
                break;
            case PushToTalkAction.Stop:
                _ = BeginStop();
                break;
        }
    }

    /// <summary>Button mouse-up or hotkey up; completes once a stopped recording has been transcribed.</summary>
    public Task ReleaseMicAsync()
    {
        return _gesture.Release() == PushToTalkAction.Stop ? BeginStop() : Task.CompletedTask;
    }

    private Task BeginStop()
    {
        if (State != RavenState.Listening)
        {
            return Task.CompletedTask;
        }

        var stop = StopAsync();
        PendingStop = stop;
        return stop;
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
        _speech.Reset();
        State = RavenState.Listening;
        Caption = "Listening…";
        WarmUpInBackground();
        _vocabularyFetch = Task.Run(FetchVocabularyAsync);

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
        _ = BeginStop();
    }

    private void OnBlock(CapturedBlock block)
    {
        if (State != RavenState.Listening)
        {
            return;
        }

        Level = AudioMath.LevelOf(block.Rms);
        _speech.Step(block.Rms, block.Duration);
        if (_silence.Step(block.Rms, block.Duration) == SignalEvent.Silent && !_silentWarned)
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
            PendingStop = ReleaseCaptureAsync();
            ReturnToIdle();
            _gesture.Reset();
        }

        RefreshMicrophones();
    }

    /// <summary>The dead capture is still stopped and disposed, off the UI thread; its clip is dropped.</summary>
    private async Task ReleaseCaptureAsync()
    {
        try
        {
            await Task.Run(_recorder.Stop);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stopping the failed capture failed");
        }
    }

    /// <summary>
    /// Says so first, then stops: the recorder's Stop waits for the capture thread (up to 2 s), resamples up to two
    /// minutes of audio and disposes the capture, which runs off the UI thread while the panel shows "Transcribing…".
    /// The awaits resume on the UI thread (its synchronisation context), where the log and the state live.
    /// </summary>
    private async Task StopAsync()
    {
        StopLimitTimer();
        State = RavenState.Transcribing;
        Caption = "Transcribing…";
        try
        {
            var clip = await Task.Run(_recorder.Stop);
            if (clip.Length < MinimumClip)
            {
                return;
            }

            if (!_speech.HeardSpeech)
            {
                AddEntry(RavenLogKind.Note, "I didn't hear anything.");
                return;
            }

            if (!_models.IsPresent && !await DownloadModelAsync())
            {
                return;
            }

            var words = await _vocabularyFetch;
            var result = await _dictation.TranscribeAsync(clip.Samples16k, words, live: false, CancellationToken.None);
            var text = result.Text.Trim();
            if (text.Length > 0)
            {
                AddEntry(RavenLogKind.You, text);
            }
        }
        catch (DictationModelLoadException ex)
        {
            // A damaged download fails here on every press, and nothing else ever replaces the file: say which to delete.
            _logger.LogWarning(ex, "The speech model could not be loaded");
            var reason = (ex.InnerException?.Message ?? ex.Message).TrimEnd().TrimEnd('.');
            AddEntry(RavenLogKind.Warning,
                $"The speech model could not be loaded: {reason}. Delete {_models.ModelPath} and press the mic to download it again.");
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

    /// <summary>Never faults: without the workspace names Whisper still transcribes, it only spells them worse.</summary>
    private async Task<DictationVocabulary> FetchVocabularyAsync()
    {
        try
        {
            return await _vocabulary.GetAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the workspace names for the dictation vocabulary");
            return DictationVocabulary.Empty;
        }
    }

    private async Task<bool> DownloadModelAsync()
    {
        const string Prefix = "Downloading the speech model (1.6 GB)… ";
        Caption = "Downloading the speech model…";
        var entry = AddEntry(RavenLogKind.Note, Prefix + "0%");
        var progress = new PostedPercent(_dispatcher, percent => entry.Text = $"{Prefix}{percent}%");

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
        Caption = "Transcribing…";
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
        MicrophoneFailureKind.AudioServiceDown => "Windows audio is not running. Start the Windows Audio service or restart the PC.",
        _ => $"{mic?.Name ?? "The microphone"} could not be opened. Another app may be using it exclusively.",
    };

    /// <summary>
    /// Whole percents on the UI thread, whatever thread the download reports on. The percent is worked out on the
    /// reporting thread and posted only when it changes: 1.6 GB in 80 KB reads is some twenty thousand reports for a
    /// hundred changes of the text. Starts at 0, the percent the entry already shows.
    /// </summary>
    private sealed class PostedPercent(IUiDispatcher dispatcher, Action<int> onPercent) : IProgress<double>
    {
        private int _last;

        public void Report(double value)
        {
            var percent = (int)Math.Clamp(value * 100, 0, 100);
            if (Interlocked.Exchange(ref _last, percent) != percent)
            {
                dispatcher.Post(() => onPercent(percent));
            }
        }
    }
}
