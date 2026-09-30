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


    /// <summary>How long after startup the model is warmed up: long enough to leave the startup itself alone.</summary>
    public static readonly TimeSpan StartupWarmUpDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the device notifications must be quiet before the microphones are listed again: plugging in one headset
    /// raises five to eight of them (state, added, the default once per role), an Audiosrv restart whole bursts.
    /// </summary>
    public static readonly TimeSpan DeviceChangeSettle = TimeSpan.FromMilliseconds(300);

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

    /// <summary>The inputs holding push to talk now: the gesture sees one hold, which ends when the last of them is let go.</summary>
    private readonly HashSet<TalkInput> _heldInputs = [];
    private readonly SilentMicWatch _silence = new();
    private readonly SpeechGate _speech = new();
    private readonly ITimer _deviceRefresh;

    private ITimer? _warmUpTimer;
    private MicrophoneDevice? _recordingMic;
    private bool _silentWarned;
    private bool _refreshing;
    private bool _fellBack;
    private bool _audioFailed;

    /// <summary>How many device listings have started; each listing's number, so an older one is never applied over a newer one.</summary>
    private long _listings;

    /// <summary>The number of the listing applied last (UI thread).</summary>
    private long _appliedListing;
    private long _recording;
    private bool _capturing;
    private int _pending;
    private bool _downloading;

    /// <summary>How many clips have been stopped: each stopped clip's number in the transcription queue.</summary>
    private long _clipsQueued;

    /// <summary>
    /// Clips numbered up to this one are dropped when their turn comes: they were queued behind a download that failed,
    /// whose one warning counted them. Without this, each would start a download of its own and fail the same way.
    /// </summary>
    private long _droppedThrough;
    private Task _pipeline = Task.CompletedTask;
    private Task<bool> _started = Task.FromResult(false);
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
        _recorder.LimitReached += (_, _) => _dispatcher.Post(OnLimitReached);
        // A trailing debounce: every notification (on whatever thread COM raises it) restarts the wait. The timer calls
        // back on the thread pool, which lists the devices there; only the result goes to the UI thread.
        _deviceRefresh = time.CreateTimer(_ => ListAndPostDevices(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _catalog.DevicesChanged += (_, _) => _deviceRefresh.Change(DeviceChangeSettle, Timeout.InfiniteTimeSpan);
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
    /// The stop in progress, if any: the recorder's Stop runs off the UI thread, and a press during it is turned away
    /// with a note (the recorder holds one capture at a time). Completed when nothing is stopping. Its transcription is
    /// not part of it: that runs in <see cref="PendingTranscriptions"/>.
    /// </summary>
    internal Task PendingStop { get; private set; } = Task.CompletedTask;

    /// <summary>The last clip in the transcription queue; completes once every stopped clip is transcribed.</summary>
    internal Task PendingTranscriptions => _pipeline;

    /// <summary>
    /// The recorder's Start of the current recording, which runs off the UI thread: opening a Bluetooth headset or a
    /// waking USB device takes hundreds of milliseconds to seconds. A stop waits for it.
    /// </summary>
    internal Task PendingStart => _started;

    /// <summary>The last <see cref="RefreshMicrophonesAsync"/>; completed when none ran or it has been applied.</summary>
    internal Task PendingRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Re-lists the microphones and applies the stored choice; the shell calls this once the settings are loaded, the
    /// panel after a capture failure. The listing (COM calls that a slow endpoint or a dying audio service can hold up
    /// for seconds) runs on the thread pool; only its result is applied, on the UI thread. Call on the UI thread; the
    /// task completes once the result is applied.
    /// </summary>
    public Task RefreshMicrophonesAsync() => PendingRefresh = Task.Run(ListAndPostDevices);

    /// <summary>
    /// Lists the devices on the calling thread (the thread pool: a refresh, or the settled end of a burst of device
    /// notifications) and posts the result to the UI thread. Completes once it is applied there.
    /// </summary>
    private Task ListAndPostDevices()
    {
        var devices = ReadDevices();
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
        {
            try
            {
                ApplyDevices(devices);
                applied.TrySetResult();
            }
            catch (Exception ex)
            {
                applied.TrySetException(ex);
            }
        });
        return applied.Task;
    }

    private DeviceList ReadDevices()
    {
        var number = Interlocked.Increment(ref _listings);
        try
        {
            return new DeviceList(number, _catalog.List(), _catalog.Default(), null);
        }
        catch (Exception ex)
        {
            return new DeviceList(number, [], null, ex);
        }
    }

    private void ApplyDevices(DeviceList list)
    {
        // Listings run concurrently (a startup refresh, a debounced change); one that finishes late is older news.
        if (list.Number < _appliedListing)
        {
            return;
        }

        _appliedListing = list.Number;
        if (list.Failure is { } ex)
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

        var devices = list.Devices;
        var windowsDefault = list.Default;
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

    /// <summary>
    /// Never on the calling thread, however the service behaves: loading the model takes seconds. Only at startup and
    /// after a download, never per press: a model that will not load would otherwise be loaded twice per press.
    /// </summary>
    private void WarmUpInBackground() => _ = Task.Run(() => _dictation.WarmUpAsync(CancellationToken.None));

    /// <summary>
    /// Button mouse-down or hotkey down. Records whether or not earlier clips are still being transcribed or the model
    /// is downloading; only the short stop of the last capture turns a press away, and never silently. A press while
    /// another input already holds the mic (the chord during a mouse hold, a click during a held chord) joins that hold
    /// and does nothing else.
    /// </summary>
    /// <param name="input">What pressed: its own release is what lets go of it.</param>
    /// <param name="sinceKeyDown">How long ago the key went down, for a hotkey press the UI thread handled late: a hold
    /// is timed from then.</param>
    public void PressMic(TalkInput input, TimeSpan sinceKeyDown = default)
    {
        if (_heldInputs.Count > 0)
        {
            _heldInputs.Add(input);
            return;
        }

        if (!PendingStop.IsCompleted)
        {
            AddEntry(RavenLogKind.Note, "Still stopping the last recording. Press again.");
            return;
        }

        _heldInputs.Add(input);
        switch (_gesture.Press(sinceKeyDown))
        {
            case PushToTalkAction.Start:
                StartRecording();
                break;
            case PushToTalkAction.Stop:
                _ = BeginStop();
                break;
        }
    }

    /// <summary>
    /// Button mouse-up or hotkey up; completes once the recording it stopped has been transcribed. Only the release of
    /// the last input holding the mic is the gesture's release: while another still holds it, the recording goes on.
    /// </summary>
    public Task ReleaseMicAsync(TalkInput input)
    {
        if (!_heldInputs.Remove(input) || _heldInputs.Count > 0)
        {
            return Task.CompletedTask;
        }

        return _gesture.Release() == PushToTalkAction.Stop ? BeginStop() : Task.CompletedTask;
    }

    /// <summary>
    /// The recording ended for another reason (a failure, the length limit, no microphone): the gesture forgets its hold
    /// or latch, and the inputs that held it are forgotten too, so their late releases stop nothing.
    /// </summary>
    private void ForgetHold()
    {
        _gesture.Reset();
        _heldInputs.Clear();
    }

    /// <summary>
    /// Ends the capture and queues its clip behind the clips stopped before it, so their transcripts reach the log in
    /// the order they were spoken. Returns the clip's place in the queue, which completes once it is transcribed.
    /// </summary>
    private Task BeginStop()
    {
        if (!_capturing)
        {
            return Task.CompletedTask;
        }

        _capturing = false;
        Interlocked.Increment(ref _pending);
        UpdateState();
        var heardSpeech = _speech.HeardSpeech;
        var words = _vocabularyFetch;
        var stop = StopCaptureAsync(_started);
        PendingStop = stop;
        var number = Interlocked.Increment(ref _clipsQueued);
        var turn = TranscribeInTurnAsync(_pipeline, number, stop, heardSpeech, words);
        _pipeline = turn;
        return turn;
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
            ForgetHold();
            AddEntry(RavenLogKind.Warning, "No microphone found. Plug one in or check Windows sound settings.");
            return;
        }

        _recordingMic = mic;
        _silentWarned = false;
        _silence.Reset();
        _speech.Reset();
        _capturing = true;
        UpdateState();
        _vocabularyFetch = Task.Run(FetchVocabularyAsync);
        _started = StartCaptureAsync(mic, ++_recording);
    }

    /// <summary>
    /// Opens the microphone off the UI thread; the panel already says it is listening. A failure is reported whenever it
    /// comes. If the recording is still running by then, the panel returns to idle and forgets the press; if it was
    /// already released, its stop finds nothing to stop. Resumes on the UI thread (its synchronisation context).
    /// </summary>
    private async Task<bool> StartCaptureAsync(MicrophoneDevice mic, long recording)
    {
        try
        {
            await Task.Run(() => _recorder.Start(mic.Id));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start recording from {Microphone}", mic.Name);
            AddEntry(RavenLogKind.Warning, WarningFor(ex is MicrophoneException failure ? failure.Kind : MicrophoneFailureKind.Unavailable, mic));
            if (recording == _recording && _capturing)
            {
                ForgetHold();
                _capturing = false;
                UpdateState();
            }

            return false;
        }
    }

    /// <summary>
    /// The recorder stopped capturing at its length limit: the recording ends the way a release ends it, and the gesture
    /// forgets the held key, whose release is then no stop of a later recording. A limit reported after the recording
    /// already ended is old news.
    /// </summary>
    private void OnLimitReached()
    {
        if (!_capturing)
        {
            return;
        }

        ForgetHold();
        _ = BeginStop();
    }

    private void OnBlock(CapturedBlock block)
    {
        if (!_capturing)
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
        if (_capturing)
        {
            AddEntry(RavenLogKind.Warning, WarningFor(error.Kind, _recordingMic));
            _capturing = false;
            PendingStop = ReleaseCaptureAsync();
            UpdateState();
            ForgetHold();
        }

        _ = RefreshMicrophonesAsync();
    }

    /// <summary>The dead capture is still stopped and disposed, off the UI thread; its clip is dropped.</summary>
    private async Task ReleaseCaptureAsync()
    {
        var started = _started;
        try
        {
            if (!await started)
            {
                return;
            }

            await Task.Run(_recorder.Stop);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stopping the failed capture failed");
        }
    }

    /// <summary>
    /// The recorder's Stop waits for the capture thread (up to 2 s), resamples up to two minutes of audio and disposes
    /// the capture: off the UI thread, while the panel already shows "Transcribing…". A start still running is waited
    /// for first; one that failed has nothing to stop (null), and has already said why.
    /// </summary>
    private async Task<RecordedClip?> StopCaptureAsync(Task<bool> started)
    {
        if (!await started)
        {
            return null;
        }

        return await Task.Run(_recorder.Stop);
    }

    /// <summary>
    /// One clip's turn in the transcription queue: after every clip stopped before it, so its note or transcript lands
    /// in the log in recording order. The awaits resume on the UI thread (its synchronisation context), where the log
    /// and the state live. Never faults, so the clip behind it always gets its turn.
    /// </summary>
    private async Task TranscribeInTurnAsync(Task previous, long number, Task<RecordedClip?> stopping, bool heardSpeech,
        Task<DictationVocabulary> vocabulary)
    {
        try
        {
            await previous;
            var clip = await stopping;
            if (number <= Interlocked.Read(ref _droppedThrough))
            {
                return; // queued behind a failed download, whose warning counted it
            }

            if (clip is null)
            {
                return; // the start failed, and has said why
            }

            if (clip.Length < MinimumClip)
            {
                // A tap read as a hold (a late or lost key release) ends here as well: never silently.
                AddEntry(RavenLogKind.Note, "That was too short. Hold the keys or the mic button while you talk.");
                return;
            }

            if (!heardSpeech)
            {
                AddEntry(RavenLogKind.Note, "I didn't hear anything.");
                return;
            }

            if (!_models.IsPresent && !await DownloadModelAsync(number))
            {
                return;
            }

            var words = await vocabulary;
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
            Interlocked.Decrement(ref _pending);
            UpdateState();
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

    /// <summary>
    /// Downloads the model for clip <paramref name="number"/>. A failure drops every clip queued behind it with this one
    /// warning; a clip stopped after the failure, the next press, tries the download again.
    /// </summary>
    private async Task<bool> DownloadModelAsync(long number)
    {
        const string Prefix = "Downloading the speech model (1.6 GB)… ";
        var entry = AddEntry(RavenLogKind.Note, Prefix + "0%");
        var progress = new PostedPercent(_dispatcher, percent => entry.Text = $"{Prefix}{percent}%");
        _downloading = true;
        UpdateState();
        try
        {
            await _models.DownloadAsync(progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The speech model could not be downloaded");
            var queued = Interlocked.Read(ref _clipsQueued);
            Interlocked.Exchange(ref _droppedThrough, queued);
            var dropped = (queued - number) switch
            {
                0 => "",
                1 => " 1 waiting recording was dropped.",
                var n => $" {n} waiting recordings were dropped.",
            };
            AddEntry(RavenLogKind.Warning, $"The speech model could not be downloaded: {ex.Message}.{dropped} Press the mic to try again.");
            return false;
        }
        finally
        {
            _downloading = false;
            UpdateState();
        }

        entry.Text = "Speech model downloaded.";
        WarmUpInBackground();
        return true;
    }

    /// <summary>
    /// What the panel shows: Listening while capturing, whatever is queued behind it; otherwise Transcribing while any
    /// stopped clip is pending (the download, or how many wait, in the caption); otherwise Idle.
    /// </summary>
    private void UpdateState()
    {
        if (_capturing)
        {
            State = RavenState.Listening;
            Caption = "Listening…";
            return;
        }

        Level = 0;
        var pending = Volatile.Read(ref _pending);
        if (pending == 0)
        {
            State = RavenState.Idle;
            Caption = IdleCaption;
            return;
        }

        State = RavenState.Transcribing;
        Caption = _downloading ? "Downloading the speech model…"
            : pending > 1 ? $"Transcribing… ({pending} waiting)"
            : "Transcribing…";
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

    /// <summary>The active microphones and the Windows default, or why they could not be listed; numbered in the order the listings started.</summary>
    private sealed record DeviceList(long Number, IReadOnlyList<MicrophoneDevice> Devices, MicrophoneDevice? Default, Exception? Failure);

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
