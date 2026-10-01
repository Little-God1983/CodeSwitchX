using System.Collections.ObjectModel;
using System.Text.Json;
using CodeSwitchX.Conductor;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Speech;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Raven;

/// <summary>
/// The Raven panel: push-to-talk dictation into a log, with the microphone choice and its failures explained, and Raven's
/// answers to what was said or typed, with a card for each tool its brain looked at the Yard through. The answers are
/// spoken as they stream in, unless muted; talking again stops that at once.
/// </summary>
public sealed partial class RavenPanelViewModel : ObservableObject
{
    /// <summary>Names the chord from <see cref="HotkeyService.PushToTalk"/>, so a new chord changes the hint with it.</summary>
    public static readonly string IdleCaption = $"Hold {HotkeyService.PushToTalk.Keys} or the mic button to talk.";

    /// <summary>The mic button's tooltip, with the chord from <see cref="HotkeyService.PushToTalk"/>.</summary>
    public static readonly string MicToolTip = $"Hold to talk, or tap to keep listening until the next tap ({HotkeyService.PushToTalk.Keys})";

    /// <summary>How many entries the log keeps; past that, the oldest go.</summary>
    public const int MaximumLogEntries = 500;

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
    private readonly IConductorBrain _brain;
    private readonly ReplyVoice _voice;
    private readonly ITextToSpeech _tts;
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

    /// <summary>This recording's "No sound" warning while it stands: nothing has come through since.</summary>
    private RavenLogEntry? _silentWarning;

    /// <summary>This recording's "stopped sending sound" warning was given (once per recording).</summary>
    private bool _droppedWarned;

    /// <summary>That warning stands: the sound has not come back since.</summary>
    private bool _droppedStands;
    private bool _refreshing;
    private bool _fellBack;
    private bool _audioFailed;

    /// <summary>How many device listings have started; each listing's number, so an older one is never applied over a newer one.</summary>
    private long _listings;

    /// <summary>The number of the listing applied last (UI thread).</summary>
    private long _appliedListing;
    private long _recording;
    private bool _capturing;

    /// <summary>Stopped clips not yet done, the one transcribing included. This and the queue's counters below are
    /// touched on the UI thread only: the queue's awaits resume there.</summary>
    private int _pending;
    private bool _downloading;

    /// <summary>How many clips have been stopped: each stopped clip's number in the transcription queue.</summary>
    private long _clipsQueued;

    /// <summary>
    /// Clips numbered up to this one that would be transcribed are dropped when their turn comes: they were queued
    /// behind a download that failed, whose one warning counts them as they are dropped. Without this, each would start
    /// a download of its own and fail the same way. A clip that is too short or silent still says so.
    /// </summary>
    private long _droppedThrough;

    /// <summary>The failed download's warning, what it says before the count, and how many clips it has dropped.</summary>
    private RavenLogEntry? _dropWarning;
    private string _dropReason = "";
    private int _dropped;
    private Task _pipeline = Task.CompletedTask;
    private Task<bool> _started = Task.FromResult(false);
    private Task<DictationVocabulary> _vocabularyFetch = Task.FromResult(DictationVocabulary.Empty);

    /// <summary>The last question in the brain's queue; UI thread, like the transcription queue.</summary>
    private Task _conversation = Task.CompletedTask;

    /// <summary>Questions asked and not yet answered, the one being answered included.</summary>
    private int _asking;

    /// <summary>Raven is saying something (UI thread, as <see cref="ReplyVoice.SpeakingChanged"/> posts it).</summary>
    private bool _speaking;

    /// <summary>The note that follows the voice's install and first load, while it stands.</summary>
    private RavenLogEntry? _voiceNote;

    public RavenPanelViewModel(IMicrophoneCatalog catalog, IMicrophoneRecorder recorder, IDictationService dictation,
        IWhisperModelStore models, IDictationVocabularyProvider vocabulary, IConductorBrain brain, ReplyVoice voice, ITextToSpeech speech,
        IUiDispatcher dispatcher, TimeProvider time, ILogger<RavenPanelViewModel> logger)
    {
        _catalog = catalog;
        _recorder = recorder;
        _dictation = dictation;
        _models = models;
        _vocabulary = vocabulary;
        _brain = brain;
        _voice = voice;
        _tts = speech;
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
        _voice.SpeakingChanged += (_, speaking) => _dispatcher.Post(() => OnSpeakingChanged(speaking));
        _voice.LevelChanged += (_, level) => _dispatcher.Post(() => OnSpeechLevel(level));
        _voice.Unspoken += (_, why) => _dispatcher.Post(() => AddEntry(RavenLogKind.Note, why));
        _tts.StatusChanged += (_, status) => _dispatcher.Post(() => OnVoiceStatus(status));
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

    /// <summary>Raven keeps its answers to itself: they are only written. The shell keeps it in step with Settings.</summary>
    [ObservableProperty]
    private bool _isMuted;

    /// <summary>0..1, live while listening or speaking.</summary>
    [ObservableProperty]
    private double _level;

    [ObservableProperty]
    private string _caption = IdleCaption;

    [ObservableProperty]
    private string _typedText = "";

    /// <summary>The model a chat Raven starts runs with, as its chip reads ("Fable 5.1"); the shell keeps it in step with Settings.</summary>
    [ObservableProperty]
    private string _chatModelChip = "Default model";

    /// <summary>The effort such a chat runs at, as its chip reads ("high effort").</summary>
    [ObservableProperty]
    private string _chatEffortChip = "Default effort";

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

    /// <summary>The last question to Raven's brain; completes once every question asked is answered.</summary>
    internal Task PendingAnswers => _conversation;

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
                // Logged here: nobody awaits the refresh a device change starts, and a fault only in the task is lost.
                _logger.LogError(ex, "Could not apply the microphone list");
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

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>Muting stops what is being said; unmuting gets the voice ready, so the next answer is spoken.</summary>
    partial void OnIsMutedChanged(bool value)
    {
        _voice.Muted = value;
        if (!value)
        {
            _tts.Prepare(install: false);
        }
    }

    private void OnSpeakingChanged(bool speaking)
    {
        _speaking = speaking;
        UpdateState();
    }

    private void OnSpeechLevel(float rms)
    {
        if (State == RavenState.Speaking)
        {
            Level = AudioMath.LevelOf(rms);
        }
    }

    /// <summary>
    /// The voice's install and its first load after it are told in one note that follows them; the warm-up of a voice
    /// installed before is quiet. A failure is a warning, once: the voice is not tried again until the settings change.
    /// </summary>
    private void OnVoiceStatus(TextToSpeechStatus status)
    {
        switch (status.State)
        {
            case TextToSpeechState.Installing:
                SetVoiceNote($"Installing Raven's voice (about 5 GB, a few minutes): {status.Detail}…");
                break;
            case TextToSpeechState.Loading when _voiceNote is not null:
                SetVoiceNote($"Loading Raven's voice: {status.Detail}…");
                break;
            case TextToSpeechState.Ready when _voiceNote is not null:
                _voiceNote.Text = "Raven's voice is ready.";
                _voiceNote = null;
                break;
            case TextToSpeechState.Failed:
                _voiceNote = null;
                AddEntry(RavenLogKind.Warning, $"Raven cannot speak: {status.Detail}");
                break;
        }
    }

    private void SetVoiceNote(string text)
    {
        if (_voiceNote is null)
        {
            _voiceNote = AddEntry(RavenLogKind.Note, text);
        }
        else
        {
            _voiceNote.Text = text;
        }
    }

    /// <summary>
    /// Warms the model up <see cref="StartupWarmUpDelay"/> from now, if it is on disk by then, so that the first clip
    /// after launch does not pay seconds for loading it; the voice too, if it is installed and not muted. Whether the panel is open or not: the hotkey works either way.
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

            // Only a voice installed before: the install itself waits for the first answer to speak.
            if (!_voice.Muted)
            {
                _tts.Prepare(install: false);
            }
        }, null, StartupWarmUpDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Never on the calling thread, however the service behaves: loading the model takes seconds. Only at startup, never
    /// per press: a model that will not load would otherwise be loaded twice per press. Not after a download either: the
    /// clip that asked for it is transcribed next and loads the model itself.
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
    public void PressMic(TalkInput input, TimeSpan sinceKeyDown = default) => _ = Press(input, sinceKeyDown);

    /// <summary><see cref="PressMic"/>; returns the stopped recording's turn in the transcription queue when the press
    /// stops a latched one.</summary>
    private Task Press(TalkInput input, TimeSpan sinceKeyDown)
    {
        if (_heldInputs.Count > 0)
        {
            _heldInputs.Add(input);
            return Task.CompletedTask;
        }

        if (!PendingStop.IsCompleted)
        {
            AddEntry(RavenLogKind.Note, "Still stopping the last recording. Press again.");
            return Task.CompletedTask;
        }

        _heldInputs.Add(input);
        switch (_gesture.Press(sinceKeyDown))
        {
            case PushToTalkAction.Start:
                StartRecording();
                return Task.CompletedTask;
            case PushToTalkAction.Stop:
                return BeginStop();
            default:
                return Task.CompletedTask;
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
    /// A press and its release at once, which the gesture reads as a tap: the first latches the mic on, the next stops
    /// it. For an input whose release cannot be seen or has no meaning: Space or Enter on the mic button, the hotkey
    /// while an admin window hides the keyboard. Completes once the recording it stopped has been transcribed.
    /// </summary>
    public Task TapMic(TalkInput input)
    {
        var pressed = Press(input, TimeSpan.Zero);
        var released = ReleaseMicAsync(input);
        return Task.WhenAll(pressed, released);
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
        var ended = _time.GetUtcNow();
        _pending++;
        UpdateState();
        var speech = _speech.Read();
        var mic = _recordingMic?.Name;
        var words = _vocabularyFetch;
        var stop = StopCaptureAsync(_started);
        PendingStop = stop;
        var number = ++_clipsQueued;
        var turn = TranscribeInTurnAsync(_pipeline, number, stop, speech, mic, words, ended);
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
        _voice.Expect();
        Ask(text, _time.GetUtcNow());
    }

    public void Note(string text) => AddEntry(RavenLogKind.Note, text);

    public void Warn(string text) => AddEntry(RavenLogKind.Warning, text);

    private const string NoMicrophoneWarning = "No microphone found. Plug one in or check Windows sound settings.";

    /// <summary>
    /// Starts listening with the selected microphone. With none selected while a listing is still on its way (a press
    /// right after startup), the panel listens at once and the capture starts once the listing has been applied; only a
    /// listing that is done and found nothing warns.
    /// </summary>
    private void StartRecording()
    {
        var mic = SelectedMicrophone;
        if (mic is null && PendingRefresh.IsCompleted)
        {
            ForgetHold();
            AddEntry(RavenLogKind.Warning, NoMicrophoneWarning);
            return;
        }

        _recordingMic = mic;
        _voice.Hush(); // the user talks: Raven stops at once, and what it was saying is not said after
        _voice.Expect();
        _brain.WarmUp(); // while the user talks, so the answer does not wait for the brain to start
        _silentWarning = null;
        _droppedWarned = false;
        _droppedStands = false;
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
    private async Task<bool> StartCaptureAsync(MicrophoneDevice? known, long recording)
    {
        if (await MicrophoneOnceListedAsync(known, recording) is not { } mic)
        {
            return false;
        }

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
    /// The microphone to record from: <paramref name="known"/>, or the one selected once the pending listing has been
    /// applied. Null, with the warning, when that listing found none; the recording, if it still runs, ends like a start
    /// that failed.
    /// </summary>
    private async Task<MicrophoneDevice?> MicrophoneOnceListedAsync(MicrophoneDevice? known, long recording)
    {
        if (known is not null)
        {
            return known;
        }

        try
        {
            await PendingRefresh;
        }
        catch (Exception)
        {
            // Already logged where it happened; whatever was applied is what there is.
        }

        var current = recording == _recording;
        if (SelectedMicrophone is { } mic)
        {
            if (current)
            {
                _recordingMic = mic;
            }

            return mic;
        }

        AddEntry(RavenLogKind.Warning, NoMicrophoneWarning);
        if (current && _capturing)
        {
            ForgetHold();
            _capturing = false;
            UpdateState();
        }

        return null;
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
        switch (_silence.Step(block.Rms, block.Duration))
        {
            case SignalEvent.Silent: // at most once a recording: the watch reports it only before anything was heard
                _silentWarning = AddEntry(RavenLogKind.Warning, $"No sound from {_recordingMic?.Name}. Check that it isn't muted.");
                break;
            case SignalEvent.Live when _silentWarning is not null:
                // Late, not muted: a Bluetooth headset takes a moment to switch to its microphone.
                ReplaceEntry(_silentWarning, RavenLogKind.Note,
                    $"{_recordingMic?.Name} took a moment to start sending sound. What you said before that was not recorded.");
                _silentWarning = null;
                break;
            case SignalEvent.Dropped when !_droppedWarned:
                // Heard, then nothing: a headset that went to sleep or was muted mid-recording.
                _droppedWarned = true;
                _droppedStands = true;
                AddEntry(RavenLogKind.Warning, $"{_recordingMic?.Name} stopped sending sound. Check that it isn't muted or gone to sleep.");
                break;
            case SignalEvent.Live when _droppedStands:
                _droppedStands = false;
                AddEntry(RavenLogKind.Note, $"{_recordingMic?.Name} is sending sound again.");
                break;
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
    /// <param name="ended">When the user's turn ended: the mic was let go.</param>
    private async Task TranscribeInTurnAsync(Task previous, long number, Task<RecordedClip?> stopping, SpeechReading speech,
        string? mic, Task<DictationVocabulary> vocabulary, DateTimeOffset ended)
    {
        try
        {
            await previous;
            var clip = await stopping;
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

            if (!speech.HeardSpeech)
            {
                _logger.LogInformation(
                    "No speech in {Seconds:0.0} s from {Microphone}: loudest block {Loudest:0.0000}, {Speech:0.00} s at the open level {Open:0.0000}",
                    clip.Length.TotalSeconds, mic, speech.Loudest, speech.Speech.TotalSeconds, speech.OpenRms);
                AddEntry(RavenLogKind.Note, "I didn't hear anything.");
                return;
            }

            if (number <= _droppedThrough)
            {
                // Queued behind a failed download: counted in its warning.
                _dropped++;
                _dropWarning!.Text = DropWarning();
                return;
            }

            if (!_models.IsPresent && !await DownloadModelAsync(number))
            {
                return;
            }

            var words = await vocabulary;
            var result = await _dictation.TranscribeAsync(clip.Samples16k, words, CancellationToken.None);
            var text = result.Text.Trim();
            if (text.Length > 0)
            {
                AddEntry(RavenLogKind.You, text);
                Ask(text, ended);
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
            _pending--;
            UpdateState();
        }
    }

    /// <summary>
    /// Puts the words to Raven's brain, behind the questions asked before them, so the answers come in the order asked. The
    /// panel thinks while any is unanswered; the mic stays free, so the next question can be asked meanwhile.
    /// </summary>
    /// <param name="ended">When the user's turn ended, for the time to Raven's first word in the log.</param>
    private void Ask(string text, DateTimeOffset ended)
    {
        _asking++;
        UpdateState();
        _conversation = AnswerInTurnAsync(_conversation, text, ended);
    }

    /// <summary>
    /// One question's turn: the reply grows in one entry as it streams in, each tool call gets a card, and text after a
    /// card starts a new entry below it, so the log reads in the order things happened. The reply is spoken as it comes,
    /// the words before a card ("Let me check.") too. The awaits resume on the UI thread. Never faults, so the question
    /// behind it always gets its turn.
    /// </summary>
    private async Task AnswerInTurnAsync(Task previous, string text, DateTimeOffset ended)
    {
        ReplyVoice.SpokenReply? spoken = null;
        try
        {
            await previous;
            var asked = _time.GetUtcNow();
            spoken = _voice.Begin(heard => _logger.LogInformation(
                "Raven's first word {Total:0} ms after the end of the turn, {Answer:0} ms after the question went to the brain",
                (heard - ended).TotalMilliseconds, (heard - asked).TotalMilliseconds));
            RavenLogEntry? reply = null;
            var cards = new Dictionary<string, RavenLogEntry>(StringComparer.Ordinal);
            await foreach (var e in _brain.AskAsync(text, CancellationToken.None))
            {
                switch (e)
                {
                    case BrainText { Delta: var piece } when reply is null:
                        if (piece.TrimStart() is { Length: > 0 } start)
                        {
                            reply = AddEntry(RavenLogKind.Raven, start);
                            spoken.Add(start);
                        }

                        break;
                    case BrainText { Delta: var piece }:
                        reply!.Text += piece;
                        spoken.Add(piece);
                        break;
                    case BrainToolCall call:
                        spoken.Add("\n"); // a sentence ends at the card, with or without its full stop
                        // The part of the reply before the card is done: "Let me check.\n\n" keeps no empty lines.
                        if (reply is not null)
                        {
                            reply.Text = reply.Text.TrimEnd();
                            reply = null;
                        }

                        var card = AddEntry(RavenLogKind.Action, call.Tool);
                        card.Detail = ActionDetail(call.Input);
                        cards[call.Id] = card;
                        break;
                    case BrainToolResult { Failed: true, Id: var id } when cards.TryGetValue(id, out var failed):
                        failed.Failed = true;
                        break;
                    case BrainNotice notice:
                        AddEntry(notice.Warning ? RavenLogKind.Warning : RavenLogKind.Note, notice.Text);
                        break;
                    case BrainFailed { Reason: var reason }:
                        AddEntry(RavenLogKind.Warning, reason);
                        break;
                }
            }

            if (reply is not null)
            {
                reply.Text = reply.Text.TrimEnd();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Raven's brain failed");
            AddEntry(RavenLogKind.Warning, $"Raven could not answer: {ex.Message}");
        }
        finally
        {
            spoken?.Complete();
            _asking--;
            UpdateState();
        }
    }

    /// <summary>A tool call's arguments for its card: the values, in order ("needs_me, Diffusion-Full"); null for none.</summary>
    internal static string? ActionDetail(string input)
    {
        try
        {
            using var document = JsonDocument.Parse(input);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var values = document.RootElement.EnumerateObject()
                .Select(p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();
            return values.Count > 0 ? string.Join(", ", values) : null;
        }
        catch (JsonException)
        {
            return null;
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
    /// warning, which counts them as their turns come; a clip stopped after the failure, the next press, tries the
    /// download again.
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
            _droppedThrough = _clipsQueued;
            _dropped = 0;
            _dropReason = $"The speech model could not be downloaded: {ex.Message}.";
            _dropWarning = AddEntry(RavenLogKind.Warning, DropWarning());
            return false;
        }
        finally
        {
            _downloading = false;
            UpdateState();
        }

        entry.Text = "Speech model downloaded.";
        return true;
    }

    private string DropWarning() => _dropReason + _dropped switch
    {
        0 => "",
        1 => " 1 waiting recording was dropped.",
        var n => $" {n} waiting recordings were dropped.",
    } + " Press the mic to try again.";

    /// <summary>
    /// What the panel shows: Listening while capturing, whatever is queued behind it; otherwise Speaking while Raven
    /// says something; otherwise Transcribing while any stopped clip is pending (the download, or how many wait behind
    /// the one transcribing, in the caption); otherwise Thinking while a question is unanswered; otherwise Idle.
    /// </summary>
    private void UpdateState()
    {
        if (_capturing)
        {
            State = RavenState.Listening;
            Caption = "Listening…";
            return;
        }

        if (_speaking)
        {
            State = RavenState.Speaking;
            Caption = "Speaking… Talk to interrupt.";
            return;
        }

        Level = 0;
        if (_pending == 0 && _asking > 0)
        {
            State = RavenState.Thinking;
            Caption = _asking > 1 ? $"Thinking… ({_asking - 1} waiting)" : "Thinking…";
            return;
        }

        if (_pending == 0)
        {
            State = RavenState.Idle;
            Caption = IdleCaption;
            return;
        }

        State = RavenState.Transcribing;
        Caption = _downloading ? "Downloading the speech model…"
            : _pending > 1 ? $"Transcribing… ({_pending - 1} waiting)"
            : "Transcribing…";
    }

    /// <summary>The oldest entries go first: the log of a panel left open for days of dictation would grow for good.</summary>
    private RavenLogEntry AddEntry(RavenLogKind kind, string text)
    {
        var entry = new RavenLogEntry(kind, text, _time.GetUtcNow());
        while (Log.Count >= MaximumLogEntries)
        {
            Log.RemoveAt(0);
        }

        Log.Add(entry);
        return entry;
    }

    /// <summary>Puts a new entry in the old one's place; one already dropped from the log is added at the end instead.</summary>
    private void ReplaceEntry(RavenLogEntry old, RavenLogKind kind, string text)
    {
        var index = Log.IndexOf(old);
        if (index < 0)
        {
            AddEntry(kind, text);
            return;
        }

        Log[index] = new RavenLogEntry(kind, text, old.At);
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
