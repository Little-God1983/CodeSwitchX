using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Listening;
using CodeSwitchX.Voice.Speech;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Raven;

/// <summary>
/// The Raven panel: push-to-talk dictation into a log, with the microphone choice and its failures explained, and Raven's
/// answers to what was said or typed, with a card for each tool its brain looked at the Yard through. The answers are
/// spoken as they stream in, unless muted; talking again stops that at once.
/// </summary>
/// <remarks>
/// One party speaks at a time (the floor): the user, Raven's answer, or a digest of what the chats did. A press silences
/// Raven at once and stops a digest; a new question (spoken or typed) takes the floor: the answer is interrupted, and
/// nothing of it is said later. A press that brings no question (a cough, a mis-tap) leaves the answer to be written.
/// News waits until the floor is free, then all of it is told at once, worded by a brain of its own that has no tools:
/// what other chats said never reaches the brain that acts.
/// </remarks>
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

    /// <summary>How long Raven and the user must both have been quiet before news is told: it must not step on the user's next sentence.</summary>
    public static readonly TimeSpan NewsGrace = TimeSpan.FromSeconds(1.5);

    private static readonly TimeSpan MinimumClip = TimeSpan.FromMilliseconds(500);

    /// <summary>The smallest change of Open mic's level that is passed on to the orb.</summary>
    private const double VisibleLevelChange = 0.01;

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
    private readonly ChatNews? _news;
    private readonly ITimer _newsTimer;

    /// <summary>Cancelled when the user takes the floor: the answer or digest that holds it stops (UI thread).</summary>
    private CancellationTokenSource _floor = new();

    /// <summary>The last question asked, while it may still wait behind another to go to the brain (UI thread).</summary>
    private Question? _lastQuestion;

    /// <summary>Cancelled by a press: the digest being told stops, before it begins speaking too (UI thread).</summary>
    private CancellationTokenSource? _digest;

    private readonly IConductorBrain? _teller;

    /// <summary>
    /// The chat news the user was given (a card written, a digest told), in facts only (workspace, title, what happened),
    /// for the brain that acts to know with the user's next question. Never what the chats said. An item goes once the
    /// brain has it, or once it is older than <see cref="ToldNewsLifetime"/> (UI thread).
    /// </summary>
    private readonly List<(DateTimeOffset At, string Fact)> _toldNews = [];

    /// <summary>How long news the user was given stays worth telling the brain with a question.</summary>
    public static readonly TimeSpan ToldNewsLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The key of the brain that words chat news (<see cref="ClaudeCliBrain.TellerPrompt"/>) among the app's services.</summary>
    public const string TellerKey = "raven-teller";

    private ITimer? _warmUpTimer;
    private MicrophoneDevice? _recordingMic;

    /// <summary>This recording's (or Open mic start's) "No sound" warning while it stands: nothing has come through since.</summary>
    private RavenLogEntry? _silentWarning;

    /// <summary>This recording's (or Open mic start's) "stopped sending sound" warning was given (once per recording or start).</summary>
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

    /// <summary>How to try again after that failure: a press in push to talk, the next turn in Open mic.</summary>
    private string _dropRetry = "";
    private int _dropped;
    private Task _pipeline = Task.CompletedTask;
    private Task<bool> _started = Task.FromResult(false);
    private Task<DictationVocabulary> _vocabularyFetch = Task.FromResult(DictationVocabulary.Empty);

    /// <summary>The last question in the brain's queue; UI thread, like the transcription queue.</summary>
    private Task _conversation = Task.CompletedTask;

    /// <summary>Questions asked and not yet answered, the one being answered included.</summary>
    private int _asking;

    /// <summary>A digest is being put together or told (UI thread): it holds the floor, but is no question.</summary>
    private bool _telling;

    /// <summary>Raven is saying something (UI thread, as <see cref="ReplyVoice.SpeakingChanged"/> posts it).</summary>
    private bool _speaking;

    /// <summary>The note that follows the voice's install and first load, while it stands.</summary>
    private RavenLogEntry? _voiceNote;

    private readonly IOpenMic? _openMic;

    /// <summary>The listener's run Open mic listens with; null while its microphone is not open (UI thread). Events of
    /// any other run are old news.</summary>
    private OpenMicRun? _openRun;

    /// <summary>Open mic is paused by the user, or by a failure until the next press.</summary>
    private bool _attendPaused;

    /// <summary>The user is talking in Open mic: <see cref="IOpenMic.SpeechStarted"/> came, the turn has not ended.</summary>
    private bool _openSpeech;

    /// <summary>Each start or stop of Open mic asked for: a start that finishes after a newer one was asked for stops the
    /// run it opened (UI thread).</summary>
    private long _openMicRequest;

    /// <summary>The listener's last start, never faulting: the next start waits for it, so the listener opens runs in
    /// the order they were asked for. A stop needs no such wait: it names its run, and the listener ignores an old one.</summary>
    private Task _listenerStart = Task.CompletedTask;

    /// <summary>The download of Open mic's models in flight, if any: starts while it runs share it.</summary>
    private Task<bool>? _openMicDownload;

    /// <summary>The newest request waiting on the download: only its failure says it goes back to push to talk.</summary>
    private long _openMicDownloadRequest;

    /// <summary>Open mic was switched on before the first microphone listing arrived (the stored mode at startup): it
    /// starts once the listing is applied.</summary>
    private bool _openMicWaitsForList;

    private readonly ChatAsks? _asks;
    private readonly IYardDirectory? _yard;

    /// <summary>The question cards still open, by their ask's id (UI thread).</summary>
    private readonly Dictionary<string, ChatQuestionCard> _askCards = new(StringComparer.Ordinal);

    /// <summary>Questions shown and not yet read out: they go before the news when the floor is free (UI thread).</summary>
    private readonly List<ChatQuestionCard> _untold = [];

    /// <param name="asks">What chats ask, held while the user answers it here; null shows no questions.</param>
    /// <param name="yard">Names the chat that asks, as the Yard shows it.</param>
    public RavenPanelViewModel(IMicrophoneCatalog catalog, IMicrophoneRecorder recorder, IDictationService dictation,
        IWhisperModelStore models, IDictationVocabularyProvider vocabulary, IConductorBrain brain, ReplyVoice voice, ITextToSpeech speech,
        IUiDispatcher dispatcher, TimeProvider time, ILogger<RavenPanelViewModel> logger, ChatNews? news = null,
        [FromKeyedServices(TellerKey)] IConductorBrain? teller = null, IOpenMic? openMic = null, ChatAsks? asks = null, IYardDirectory? yard = null)
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
        _news = news;
        _teller = teller;
        _newsTimer = time.CreateTimer(_ => _dispatcher.Post(TellNewsIfFree), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (news is not null)
        {
            news.Arrived += (_, _) => _dispatcher.Post(() =>
            {
                if (SpeakNews && !IsMuted)
                {
                    _teller?.WarmUp(); // its start is hidden in the wait for the floor
                }

                ScheduleNews();
            });
        }

        _openMic = openMic;
        if (openMic is not null)
        {
            openMic.SpeechStarted += (_, run) => _dispatcher.Post(() => OnOpenSpeech(run));
            openMic.TurnEnded += (_, turn) => _dispatcher.Post(() => OnOpenTurn(turn));
            // Some 20 batches a second: a cached static delegate and the batch as state, so no closure per batch here (the
            // dispatcher's own work item for a post from the worker thread remains: see WpfUiDispatcher).
            openMic.Heard += (_, heard) => _dispatcher.Post(static s => s.Panel.OnOpenHeard(s.Heard), (Panel: this, Heard: heard));
            openMic.Failed += (_, run) => _dispatcher.Post(() => OnOpenMicFailed(run));
        }

        _asks = asks;
        _yard = yard;
        if (asks is not null)
        {
            asks.Opened += ask => _dispatcher.Post(() => OnAsked(ask));
            asks.Closed += closed => _dispatcher.Post(() => OnAskClosed(closed));
        }
    }

    /// <summary>How many chats' questions wait for an answer here: the collapsed rail shows it.</summary>
    [ObservableProperty]
    private int _openQuestions;

    /// <summary>Raised when the user clicks a chat's line on a digest card: the shell shows its tile.</summary>
    public event EventHandler<Guid>? TileRequested;

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

    /// <summary>Raven tells what the chats did; off, the digest cards are only written. The shell keeps it in step with Settings.</summary>
    [ObservableProperty]
    private bool _speakNews = true;

    /// <summary>Push to talk, or Open mic: the mode the panel is in. A failure may drop it back to push to talk; the
    /// user's own choice is <see cref="PreferredMicMode"/>.</summary>
    [ObservableProperty]
    private MicMode _micMode;

    /// <summary>
    /// The user's choice of mode, which the shell stores: setting it puts the panel in that mode. Only the user's switch
    /// changes it (<see cref="ChooseMicModeCommand"/>), never a fallback after a failure, as with <see cref="PreferredMicrophone"/>:
    /// a download that failed once while offline must not turn Open mic off for good.
    /// </summary>
    [ObservableProperty]
    private MicMode _preferredMicMode;

    /// <summary>A click on either half of the mode switch, which shows <see cref="MicMode"/>: the user's choice, saved. A
    /// click on the half already checked counts too: after a fallback, Push to talk keeps push to talk for good, and Open
    /// mic tries again although the choice itself has not changed.</summary>
    [RelayCommand]
    private void ChooseMicMode(MicMode mode)
    {
        var changed = PreferredMicMode != mode;
        PreferredMicMode = mode;
        if (!changed)
        {
            MicMode = mode; // a new choice has already put the panel in that mode, or fallen back from it
        }
    }

    /// <summary>Talking over Raven stops it in Open mic; off, Open mic ignores speech while Raven speaks (Raven heard on
    /// speakers). The shell keeps it in step with Settings.</summary>
    [ObservableProperty]
    private bool _bargeIn = true;

    /// <summary>The mic button's name and tooltip: what a press does in the mode the panel is in.</summary>
    public string MicButtonName => MicMode == MicMode.PushToTalk ? "Push to talk"
        : _attendPaused ? "Resume Open mic" : "Pause Open mic";

    public string MicButtonToolTip => MicMode == MicMode.PushToTalk ? MicToolTip : $"{MicButtonName} ({HotkeyService.PushToTalk.Keys})";

    /// <summary>The last start or stop of Open mic; completed when none runs.</summary>
    internal Task PendingOpenMic { get; private set; } = Task.CompletedTask;

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

            if (_openMicWaitsForList && MicMode == MicMode.OpenMic)
            {
                // Open mic was waiting for this listing: with no microphones it pauses (the warning above says why), and
                // a press tries again.
                PauseOpenMic();
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

        if (_openMicWaitsForList && MicMode == MicMode.OpenMic)
        {
            _openMicWaitsForList = false;
            PendingOpenMic = StartOpenMicAsync();
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

        if (_openRun is { } run && value is not null && value.Id != run.DeviceId)
        {
            StopOpenMic();
            PendingOpenMic = StartOpenMicAsync();
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

    partial void OnPreferredMicModeChanged(MicMode value) => MicMode = value;

    partial void OnMicModeChanged(MicMode value)
    {
        if (value == MicMode.OpenMic)
        {
            if (_openMic is null)
            {
                AddEntry(RavenLogKind.Warning, "Open mic is not available.");
                MicMode = MicMode.PushToTalk;
                return;
            }

            if (_capturing)
            {
                // A held push-to-talk recording ends as a release would; its key's late release is forgotten.
                ForgetHold();
                _ = BeginStop();
            }

            _attendPaused = false;
            PendingOpenMic = StartOpenMicAsync();
        }
        else
        {
            StopOpenMic();
            _attendPaused = false;
        }

        OnPropertyChanged(nameof(MicButtonName));
        OnPropertyChanged(nameof(MicButtonToolTip));
        UpdateState();
    }

    partial void OnBargeInChanged(bool value)
    {
        UpdateIgnoreSpeech();
        if (_speaking)
        {
            UpdateState(); // the Speaking caption says whether talking interrupts
        }
    }

    private void UpdateIgnoreSpeech()
    {
        if (_openMic is not null)
        {
            _openMic.IgnoreSpeech = _speaking && !BargeIn;
        }
    }

    /// <summary>
    /// Opens Open mic on the selected microphone: downloads its models first if they are missing (the log shows the
    /// progress), waits for a microphone listing still on its way, and starts the listener after any start before it. A
    /// failed download, a model that will not load or a listener that will not start goes back to push to talk with a
    /// warning; a microphone that will not open, or fails while it opens, leaves Open mic paused, for a press to try again.
    /// </summary>
    private async Task StartOpenMicAsync()
    {
        var request = ++_openMicRequest;
        UpdateState();
        if (!_openMic!.ModelsPresent)
        {
            // One download at a time: a start while it runs waits for the same one.
            _openMicDownloadRequest = request;
            if (_openMicDownload is not { IsCompleted: false })
            {
                _openMicDownload = DownloadOpenMicModelsAsync();
            }

            UpdateState(); // "Downloading Open mic's models…"
            var downloaded = await _openMicDownload;
            UpdateState();
            if (!downloaded)
            {
                if (request == _openMicRequest)
                {
                    MicMode = MicMode.PushToTalk;
                }

                return;
            }
        }

        try
        {
            await PendingRefresh;
        }
        catch (Exception)
        {
            // Logged where it happened; whatever was applied is what there is.
        }

        if (request != _openMicRequest)
        {
            return;
        }

        if (SelectedMicrophone is not { } mic)
        {
            if (_appliedListing == 0)
            {
                _openMicWaitsForList = true; // the startup listing has not arrived: ApplyDevices starts it
                return;
            }

            AddEntry(RavenLogKind.Warning, NoMicrophoneWarning);
            PauseOpenMic();
            return;
        }

        OpenMicRun run;
        try
        {
            var start = StartListenerAsync(_listenerStart, request, mic.Id);
            _listenerStart = start.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            if (await start is not { } opened)
            {
                return; // asked off before it began
            }

            run = opened;
        }
        // A start the user has moved on from (paused, switched mode or microphone) fails into the log only: its warning
        // would be about a mic or mode no longer in use, and "Back to push to talk" would not happen.
        catch (ListeningModelException ex)
        {
            _logger.LogWarning(ex, "Open mic's models would not load");
            BackToPushToTalk(request,
                $"Open mic could not start: {ex.Message.TrimEnd().TrimEnd('.')}. {FallbackNote}");
            return;
        }
        catch (MicrophoneException ex)
        {
            _logger.LogWarning(ex, "Open mic could not open {Microphone}", mic.Name);
            if (request == _openMicRequest)
            {
                AddEntry(RavenLogKind.Warning, WarningFor(ex.Kind, mic));
                PauseOpenMic();
            }

            return;
        }
        catch (Exception ex)
        {
            // Not the model file's fault (the native runtime would not load, say): nothing is deleted or downloaded again.
            _logger.LogWarning(ex, "Open mic could not start");
            BackToPushToTalk(request, $"Open mic could not start: {ex.Message.TrimEnd().TrimEnd('.')}. {FallbackNote}");
            return;
        }

        if (request != _openMicRequest)
        {
            // Switched away while it opened: the listener ignores this stop if a newer run has opened since.
            _ = StopListenerAsync(run);
            return;
        }

        if (run.Failure is { } died)
        {
            // The microphone failed while it opened; the listener has stopped the run itself. Its Failed event, which
            // comes for a run the panel never took, is ignored, so this is the one warning.
            AddEntry(RavenLogKind.Warning, WarningFor(died.Kind, mic));
            PauseOpenMic();
            _ = RefreshMicrophonesAsync();
            return;
        }

        _openRun = run;
        ResetSignalWatch();
        UpdateIgnoreSpeech();
        UpdateState();
        if (SelectedMicrophone is { } chosen && chosen.Id != mic.Id)
        {
            // Another microphone was picked while this one opened.
            StopOpenMic();
            var next = StartOpenMicAsync();
            PendingOpenMic = next;
            await next;
        }
    }

    /// <summary>Starts the listener once the start before it is done, unless this request is stale by then. The wait
    /// resumes on the UI thread; only the start itself runs on the thread pool.</summary>
    private async Task<OpenMicRun?> StartListenerAsync(Task previous, long request, string deviceId)
    {
        await previous;
        return request == _openMicRequest ? await Task.Run(() => _openMic!.Start(deviceId)) : null;
    }

    /// <summary>What a fallback does, said with its warning: the stored choice is still Open mic, so the next launch tries it
    /// again (downloading a model file that would not load) unless the user clicks Push to talk, checked as it is. The
    /// only retry promise in the warning.</summary>
    private const string FallbackNote =
        "Back to push to talk for now; Open mic is tried again at the next launch. Click Push to talk to stop trying Open mic.";

    /// <summary>A start that failed for good: if it is still the current request, the warning and back to push to talk
    /// (the user's stored choice stays Open mic, for the next launch).</summary>
    private void BackToPushToTalk(long request, string warning)
    {
        if (request == _openMicRequest)
        {
            AddEntry(RavenLogKind.Warning, warning);
            MicMode = MicMode.PushToTalk;
        }
    }

    private async Task<bool> DownloadOpenMicModelsAsync()
    {
        const string Prefix = "Downloading Open mic's models (11 MB)… ";
        var entry = AddEntry(RavenLogKind.Note, Prefix + "0%");
        var progress = new PostedPercent(_dispatcher, percent => entry.Text = $"{Prefix}{percent}%");
        try
        {
            await _openMic!.DownloadModelsAsync(progress, CancellationToken.None);
            entry.Text = "Open mic's models downloaded.";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open mic's models could not be downloaded");
            // Only a request still waiting for it goes back to push to talk.
            var back = _openMicDownloadRequest == _openMicRequest ? " " + FallbackNote : "";
            ReplaceEntry(entry, RavenLogKind.Warning, $"Open mic's models could not be downloaded: {ex.Message.TrimEnd().TrimEnd('.')}.{back}");
            return false;
        }
    }

    /// <summary>Closes Open mic's microphone (off the UI thread) and drops a turn being spoken; a start in flight stops
    /// the run it opens.</summary>
    private void StopOpenMic()
    {
        _openMicRequest++;
        _openSpeech = false;
        _openMicWaitsForList = false;
        if (_openRun is { } run)
        {
            _openRun = null;
            PendingOpenMic = StopListenerAsync(run);
        }
    }

    /// <summary>Stops <paramref name="run"/> on the thread pool, if it is still the listener's current run; never faults, a failed stop is logged.</summary>
    private Task StopListenerAsync(OpenMicRun run) => Task.Run(() =>
    {
        try
        {
            _openMic!.Stop(run);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stopping Open mic failed");
        }
    });

    private void PauseOpenMic()
    {
        StopOpenMic();
        _attendPaused = true;
        OnPropertyChanged(nameof(MicButtonName));
        OnPropertyChanged(nameof(MicButtonToolTip));
        UpdateState();
    }

    private void ToggleOpenMicPause()
    {
        if (_attendPaused)
        {
            _attendPaused = false;
            OnPropertyChanged(nameof(MicButtonName));
            OnPropertyChanged(nameof(MicButtonToolTip));
            PendingOpenMic = StartOpenMicAsync();
        }
        else
        {
            PauseOpenMic();
        }
    }

    private void OnSpeakingChanged(bool speaking)
    {
        _speaking = speaking;
        UpdateIgnoreSpeech();
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
    /// An install stopped (cancelled, another engine picked) says so in the note.
    /// </summary>
    private void OnVoiceStatus(TextToSpeechStatus status)
    {
        switch (status.State)
        {
            case TextToSpeechState.Installing:
                SetVoiceNote($"Installing Raven's voice: {status.Detail}{(status.Bytes is { } bytes ? $" ({bytes})" : "")}…");
                break;
            case TextToSpeechState.Loading when _voiceNote is not null:
                SetVoiceNote($"Loading Raven's voice: {status.Detail}…");
                break;
            case TextToSpeechState.Ready when _voiceNote is not null:
                _voiceNote.Text = "Raven's voice is ready.";
                _voiceNote = null;
                break;
            case TextToSpeechState.Failed when _voiceNote is not null:
                // The install or load it followed ended: the note says so, rather than "Loading…" above the warning.
                ReplaceEntry(_voiceNote, RavenLogKind.Warning, $"Raven cannot speak: {status.Detail}");
                _voiceNote = null;
                break;
            case TextToSpeechState.Failed:
                AddEntry(RavenLogKind.Warning, $"Raven cannot speak: {status.Detail}");
                break;
            case TextToSpeechState.Off or TextToSpeechState.NotInstalled or TextToSpeechState.NoEngine when _voiceNote is not null:
                // Cancelled in the voice setup, or another engine (or none) picked: the note does not go on claiming an install.
                _voiceNote.Text = "Raven's voice stopped getting ready.";
                _voiceNote = null;
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
        if (MicMode == MicMode.OpenMic)
        {
            ToggleOpenMicPause(); // the button and the hotkey pause and resume Open mic
            return Task.CompletedTask;
        }

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
        if (MicMode == MicMode.OpenMic && _heldInputs.Count == 0)
        {
            return Task.CompletedTask; // a press already paused or resumed; a release does nothing
        }

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
        UserStartsTalking();
        ResetSignalWatch();
        _speech.Reset();
        _capturing = true;
        UpdateState();
        _vocabularyFetch = Task.Run(FetchVocabularyAsync);
        _started = StartCaptureAsync(mic, ++_recording);
    }

    /// <summary>
    /// The user talks (a press, or half a second of speech in Open mic): Raven stops speaking at once, and what it was
    /// saying is not said after. A digest stops too, also one that has not begun to speak; an answer is interrupted only by
    /// a question: talk that brings none leaves it to be written. The brain warms up while the user talks.
    /// </summary>
    private void UserStartsTalking()
    {
        _digest?.Cancel();
        _voice.Hush();
        _voice.Expect();
        _brain.WarmUp();
    }

    private void OnOpenSpeech(OpenMicRun run)
    {
        if (run != _openRun)
        {
            return; // stopped, paused or moved to another run since
        }

        _openSpeech = true;
        UserStartsTalking();
        _vocabularyFetch = Task.Run(FetchVocabularyAsync);
        UpdateState();
    }

    /// <summary>The turn is over: its clip joins the transcription queue as a released recording's does.</summary>
    private void OnOpenTurn(OpenMicTurn turn)
    {
        if (turn.Run != _openRun || !_openSpeech)
        {
            return;
        }

        var clip = turn.Clip;
        _openSpeech = false;
        _pending++;
        var length = TimeSpan.FromSeconds((double)clip.Length / AudioMath.TargetRate);
        var heard = new SpeechReading(true, 0, 0, length); // the detector heard the speech
        var number = ++_clipsQueued;
        var transcribed = TranscribeInTurnAsync(_pipeline, number, Task.FromResult<RecordedClip?>(new RecordedClip(clip, length)), heard,
            SelectedMicrophone?.Name, _vocabularyFetch, _time.GetUtcNow(), quiet: true);
        _pipeline = transcribed;
        UpdateState();
    }

    /// <summary>
    /// The orb's level, and the watch push to talk keeps (<see cref="WatchSignal"/>). A batch of about 50 ms
    /// (<see cref="OpenMicListener.HeardBatch"/>). The level changes only when it would show: an idle room must not
    /// redraw the orb twenty times a second for hours. The level is the loudest block's; the watch gets the quietest, so
    /// one click in a batch of digital zeros is not 50 ms of sound.
    /// </summary>
    private void OnOpenHeard(HeardAudio heard)
    {
        if (_openRun is null)
        {
            return;
        }

        if (State is RavenState.Attending or RavenState.Listening)
        {
            var level = AudioMath.LevelOf(heard.Loudest);
            if (Math.Abs(level - Level) >= VisibleLevelChange)
            {
                Level = level;
            }
        }

        WatchSignal(heard.Quietest, heard.Duration, SelectedMicrophone?.Name);
    }

    /// <summary>A run's microphone died. One the panel never took (still opening: its start reads the failure) or has
    /// left is not warned of here.</summary>
    private void OnOpenMicFailed(OpenMicRun run)
    {
        _logger.LogWarning(run.Failure, "Open mic's microphone failed");
        if (run != _openRun)
        {
            return;
        }

        AddEntry(RavenLogKind.Warning, WarningFor(run.Failure?.Kind ?? MicrophoneFailureKind.Unavailable, SelectedMicrophone));
        PauseOpenMic();
        _ = RefreshMicrophonesAsync();
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
        WatchSignal(block.Rms, block.Duration, _recordingMic?.Name);
    }

    /// <summary>
    /// Is the microphone sending sound at all? Both modes' watch: push to talk's per recording, Open mic's per start (for
    /// hours, so a mic muted or asleep mid-session is the likely case). <see cref="ResetSignalWatch"/> begins each.
    /// </summary>
    private void WatchSignal(float rms, TimeSpan duration, string? mic)
    {
        switch (_silence.Step(rms, duration))
        {
            case SignalEvent.Silent: // at most once a recording: the watch reports it only before anything was heard
                _silentWarning = AddEntry(RavenLogKind.Warning, $"No sound from {mic}. Check that it isn't muted.");
                break;
            case SignalEvent.Live when _silentWarning is not null:
                // Late, not muted: a Bluetooth headset takes a moment to switch to its microphone.
                ReplaceEntry(_silentWarning, RavenLogKind.Note,
                    $"{mic} took a moment to start sending sound. What you said before that was not recorded.");
                _silentWarning = null;
                break;
            case SignalEvent.Dropped when !_droppedWarned:
                // Heard, then nothing: a headset that went to sleep or was muted mid-recording.
                _droppedWarned = true;
                _droppedStands = true;
                AddEntry(RavenLogKind.Warning, $"{mic} stopped sending sound. Check that it isn't muted or gone to sleep.");
                break;
            case SignalEvent.Live when _droppedStands:
                _droppedStands = false;
                if (MicMode == MicMode.OpenMic)
                {
                    // Open mic runs for hours: a second mute after the sound came back is as much news as the first.
                    // Push to talk keeps its one warning a recording.
                    _droppedWarned = false;
                }

                AddEntry(RavenLogKind.Note, $"{mic} is sending sound again.");
                break;
        }
    }

    private void ResetSignalWatch()
    {
        _silence.Reset();
        _silentWarning = null;
        _droppedWarned = false;
        _droppedStands = false;
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
    /// <param name="quiet">An Open mic turn: one too short or without words is only logged, never noted in the panel, as
    /// the user pressed nothing.</param>
    private async Task TranscribeInTurnAsync(Task previous, long number, Task<RecordedClip?> stopping, SpeechReading speech,
        string? mic, Task<DictationVocabulary> vocabulary, DateTimeOffset ended, bool quiet = false)
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
                if (quiet)
                {
                    _logger.LogInformation("Open mic's turn was too short to transcribe ({Seconds:0.00} s)", clip.Length.TotalSeconds);
                    return;
                }

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

            if (!_models.IsPresent && !await DownloadModelAsync(number, quiet))
            {
                return;
            }

            var words = await vocabulary;
            var result = await _dictation.TranscribeAsync(clip.Samples16k, words, CancellationToken.None);
            var text = result.Text.Trim();
            if (text.Length == 0 && quiet)
            {
                _logger.LogInformation("Open mic's turn of {Seconds:0.0} s had no words", clip.Length.TotalSeconds);
            }

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
                quiet ? $"The speech model could not be loaded: {reason}. Delete {_models.ModelPath}; it is downloaded again with your next turn."
                    : $"The speech model could not be loaded: {reason}. Delete {_models.ModelPath} and press the mic to download it again.");
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
    /// <remarks>
    /// The question takes the floor: the answer or digest before it is interrupted, so only the newest question is
    /// answered. One asked before it that has not gone to the brain yet is not lost: it goes with this one, as the first
    /// half of what the user said. The answer's voice begins here, as it is asked, unless the user is talking in Open mic.
    /// </remarks>
    private void Ask(string text, DateTimeOffset ended)
    {
        if (_lastQuestion is { Sent: false, Ended: false } waiting)
        {
            waiting.Merged = true;
            text = waiting.Text + "\n" + text;
        }

        var question = new Question(text);
        _lastQuestion = question;
        var floor = TakeFloor();
        _asking++;
        UpdateState();
        var asked = new StrongBox<DateTimeOffset>();
        // In the user's next Open mic turn the answer is only written: Raven's voice would talk over the turn and, heard
        // through speakers, end up in it, and the question being spoken replaces the answer anyway. TakeFloor hushed already.
        var spoken = _voice.Begin(heard => _logger.LogInformation(
            "Raven's first word {Total:0} ms after the end of the turn, {Answer:0} ms after the question went to the brain",
            (heard - ended).TotalMilliseconds, (heard - asked.Value).TotalMilliseconds), silent: _openSpeech);

        _conversation = AnswerInTurnAsync(_conversation, question, spoken, asked, floor);
    }

    /// <summary>
    /// A question on its way to the brain: <see cref="Sent"/> once the brain says it has it (<see cref="BrainQuestionSent"/>),
    /// <see cref="Merged"/> when a later one took it along, because it had not, and <see cref="Ended"/> once its turn is over
    /// (answered, failed): a question that failed is not asked again with a later one (UI thread).
    /// </summary>
    private sealed class Question(string text)
    {
        public string Text { get; } = text;

        public bool Sent { get; set; }

        public bool Merged { get; set; }

        public bool Ended { get; set; }

        /// <summary>The news facts that went with it; given once it is sent.</summary>
        public IReadOnlyList<(DateTimeOffset At, string Fact)> Told { get; set; } = [];
    }

    /// <summary>The user takes the floor: whatever holds it stops, its speech too. Returns the new floor's token (UI thread).</summary>
    private CancellationToken TakeFloor()
    {
        _newsTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _voice.Hush(); // a typed question silences Raven as a press does
        _floor.Cancel();
        _floor.Dispose();
        _floor = new CancellationTokenSource();
        return _floor.Token;
    }

    /// <summary>
    /// One question's turn: the reply grows in one entry as it streams in, each tool call gets a card, and text after a
    /// card starts a new entry below it, so the log reads in the order things happened. The reply is spoken as it comes,
    /// the words before a card ("Let me check.") too. The awaits resume on the UI thread. Never faults, so the question
    /// behind it always gets its turn.
    /// </summary>
    /// <param name="asked">Set to when the question goes to the brain, for the log line on its first word.</param>
    /// <param name="floor">Cancelled when the user takes the floor: the answer is interrupted, and ends with "(interrupted)".</param>
    private async Task AnswerInTurnAsync(Task previous, Question question, ReplyVoice.SpokenReply spoken, StrongBox<DateTimeOffset> asked,
        CancellationToken floor)
    {
        try
        {
            await previous;
            if (question.Merged)
            {
                return; // a later question took it along: the floor is only taken by a question, which merges one not sent
            }

            asked.Value = _time.GetUtcNow();
            await StreamAnswerAsync(_brain, WithToldNews(question), spoken, floor, question);
        }
        finally
        {
            question.Ended = true;
            spoken.Complete();
            _asking--;
            UpdateState();
        }
    }

    /// <summary>
    /// The brain's answer to <paramref name="text"/> into the log and the voice. Never faults: a failure is a warning,
    /// an interruption ends the reply with "(interrupted)". Returns whether any of the reply came.
    /// </summary>
    /// <param name="question">Marked sent once the brain has it; null for a digest.</param>
    /// <param name="quiet">The teller's: what it says about itself goes to the app's log, not the panel's (the fallback sentence covers a failure).</param>
    private async Task<bool> StreamAnswerAsync(IConductorBrain brain, string text, ReplyVoice.SpokenReply spoken, CancellationToken floor,
        Question? question = null, bool quiet = false)
    {
        RavenLogEntry? reply = null;
        var said = false;
        try
        {
            var cards = new Dictionary<string, RavenLogEntry>(StringComparer.Ordinal);
            await foreach (var e in brain.AskAsync(text, floor))
            {
                switch (e)
                {
                    case BrainQuestionSent when question is not null:
                        question.Sent = true;
                        foreach (var told in question.Told)
                        {
                            _toldNews.Remove(told); // the brain has it now
                        }

                        break;
                    case BrainText { Delta: var piece } when reply is null:
                        if (piece.TrimStart() is { Length: > 0 } start)
                        {
                            reply = AddEntry(RavenLogKind.Raven, start);
                            said = true;
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
                    case BrainNotice or BrainFailed when quiet:
                        _logger.LogWarning("Raven's news teller: {What}", e);
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
        catch (OperationCanceledException) when (floor.IsCancellationRequested)
        {
            if (reply is not null)
            {
                reply.Text = reply.Text.TrimEnd() + " (interrupted)";
            }
        }
        catch (Exception ex) when (quiet)
        {
            _logger.LogWarning(ex, "Raven's news teller failed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Raven's brain failed");
            AddEntry(RavenLogKind.Warning, $"Raven could not answer: {ex.Message}");
        }

        return said;
    }

    /// <summary>
    /// Waits <see cref="NewsGrace"/> for the floor to stay free, then tells the news. Called when news arrives and
    /// whenever the panel's state changes (UI thread): each call starts the wait again.
    /// </summary>
    private void ScheduleNews()
    {
        if ((_news is { HasNews: true } || _untold.Count > 0) && FloorIsFree)
        {
            _newsTimer.Change(NewsGrace, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Nobody talks: no recording, no clip or question unanswered, nothing being said.</summary>
    private bool FloorIsFree => !_capturing && _heldInputs.Count == 0 && _pending == 0 && _asking == 0 && !_telling && !_speaking && !_openSpeech;

    private void TellNewsIfFree()
    {
        if ((_news is not { HasNews: true } && _untold.Count == 0) || !FloorIsFree)
        {
            return; // the next change of state schedules it again
        }

        // A question takes the floor from it, and a press stops it too.
        _digest?.Dispose();
        _digest = CancellationTokenSource.CreateLinkedTokenSource(_floor.Token);
        _telling = true;
        UpdateState();
        // A chat's question goes before the news: the chat is stopped on it. The news follows once the floor is free again.
        _conversation = _untold.Count > 0
            ? TellQuestionsAsync(_conversation, _digest.Token)
            : TellNewsAsync(_conversation, _news!, _digest.Token);
    }

    /// <summary>A chat asks something: its card goes in the log, to be read out when the floor is free.</summary>
    private void OnAsked(ChatAsk ask)
    {
        if (_asks?.IsHeld(ask.Id) == false)
        {
            return; // it ended before it got here (the Cab changed, say): its Closed found no card, and a card now would stay open
        }

        var card = new ChatQuestionCard(ask);
        var entry = new RavenLogEntry(RavenLogKind.Question, "asks", _time.GetUtcNow()) { Question = card };
        Append(entry);
        _askCards[ask.Id] = card;
        _untold.Add(card);
        OpenQuestions = _askCards.Count;
        card.Naming = NameAsync(card);
        ScheduleNews();
    }

    /// <summary>Names the chat as the Yard shows it, for the card and for what Raven says. Never faults.</summary>
    private async Task NameAsync(ChatQuestionCard card)
    {
        if (_yard is null)
        {
            return;
        }

        try
        {
            var chat = (await _yard.ChatsAsync(CancellationToken.None)).FirstOrDefault(c => c.Id == card.Ask.SessionId);
            if (chat is not null)
            {
                card.Chat = $"{chat.Workspace} · {chat.Title}";
                card.Said = $"{chat.Workspace}, chat \"{chat.Title}\"";
                card.WorkspaceId = chat.WorkspaceId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not name the chat that asks");
        }
    }

    /// <summary>A held question ended: its card shows how, and takes no more clicks.</summary>
    private void OnAskClosed(ChatAskClosed closed)
    {
        if (!_askCards.Remove(closed.Ask.Id, out var card))
        {
            return;
        }

        _untold.Remove(card);
        // Not told yet to the brain that acts, it is not told at all: the chat waits for no answer here any more.
        var fact = QuestionFact(card);
        _toldNews.RemoveAll(t => t.Fact == fact);
        OpenQuestions = _askCards.Count;
        card.IsOpen = false;
        card.Outcome = closed.Outcome switch
        {
            ChatAskOutcome.Answered => "Answered: " + string.Join("; ", closed.Answers ?? []),
            ChatAskOutcome.ToVsCode => "Left to VS Code: it asks there.",
            ChatAskOutcome.TimedOut => $"Not answered within {ChatNewsLine.Span(ChatAsks.Lifetime)}: VS Code asks it now.",
            ChatAskOutcome.Stopped => "The chat was stopped.",
            _ => "The chat stopped waiting for it.",
        };
    }

    /// <summary>An option was clicked: chosen, and for a card of one question that takes one option, sent at once.</summary>
    [RelayCommand]
    private void ChooseOption(ChatOptionView? option)
    {
        if (option is null || !option.Question.Card.IsOpen)
        {
            return;
        }

        option.Question.Choose(option);
        if (!option.Question.Card.NeedsSend)
        {
            SendAnswers(option.Question.Card);
        }
    }

    /// <summary>The card's answers go to the chat, which carries on with them.</summary>
    [RelayCommand]
    private void SendAnswers(ChatQuestionCard? card)
    {
        if (card is not { CanSend: true } || _asks is null)
        {
            return;
        }

        if (!_asks.Answer(card.Ask.Id, card.Answers()) && card.IsOpen)
        {
            card.IsOpen = false;
            card.Outcome = "The chat no longer waits for it.";
        }
    }

    /// <summary>The question goes to the chat's VS Code tab, which asks it there.</summary>
    [RelayCommand]
    private void AnswerInVsCode(ChatQuestionCard? card)
    {
        if (card is { IsOpen: true })
        {
            _asks?.ToVsCode(card.Ask.Id);
        }
    }

    /// <summary>
    /// Reads out the questions not read yet: who asks, what, and the options. The brain that acts is told them with the
    /// user's next question, so "the first one" answers it. Muted, or with news not to be spoken, the cards are only shown.
    /// Never faults.
    /// </summary>
    private async Task TellQuestionsAsync(Task previous, CancellationToken floor)
    {
        ReplyVoice.SpokenReply? spoken = null;
        try
        {
            await previous;
            var cards = _untold.Where(c => c.IsOpen).ToList();
            _untold.Clear();
            foreach (var card in cards)
            {
                await card.Naming;
            }

            cards.RemoveAll(c => !c.IsOpen);
            if (cards.Count == 0)
            {
                return;
            }

            var at = _time.GetUtcNow();
            _toldNews.AddRange(cards.Select(c => (at, QuestionFact(c))));
            if (!SpeakNews || IsMuted || floor.IsCancellationRequested)
            {
                return;
            }

            _voice.Expect();
            spoken = _voice.Begin();
            spoken.Add(QuestionSentence(cards));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading out a chat's question failed");
        }
        finally
        {
            spoken?.Complete();
            _telling = false;
            UpdateState();
        }
    }

    /// <summary>What Raven says of the questions: "CodeSwitchX, chat "Fix the upload" asks: Which fruit? Apple, Banana or Cherry."</summary>
    internal static string QuestionSentence(IReadOnlyList<ChatQuestionCard> cards)
    {
        var text = new System.Text.StringBuilder();
        foreach (var card in cards)
        {
            text.Append(text.Length == 0 ? "" : " ").Append(card.Said)
                .Append(card.Questions.Count == 1 ? " asks: " : $" asks {card.Questions.Count} questions. ");
            foreach (var question in card.Questions)
            {
                text.Append(Sentence(question.Text));
                var labels = question.Options.Select(o => o.Label).ToList();
                if (labels.Count > 0)
                {
                    text.Append(' ').Append(question.MultiSelect ? "Any of " : "")
                        .Append(labels.Count == 1 ? labels[0] : string.Join(", ", labels[..^1]) + " or " + labels[^1]).Append(". ");
                }
                else
                {
                    text.Append(' ');
                }
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string Sentence(string text) => text.TrimEnd() is var t && t.Length > 0 && ".?!".Contains(t[^1]) ? t : t + ".";

    /// <summary>The question as the brain that acts is told it: the chat, its id, each question and its options.</summary>
    internal static string QuestionFact(ChatQuestionCard card) =>
        $"{card.Said} (chat id {card.Ask.SessionId}) asks, and waits for the answer here: {card.Ask.Describe()}. answer_question answers it";

    /// <summary>
    /// The digest: one card that lists the news, and the teller wording it in a few words, as conversation. Muted, or
    /// with news not to be spoken, only the card is written. A teller that gives no words (or none at all) says the
    /// fallback sentence instead. Stopped, the rest is dropped: its chats count as told. Never faults.
    /// </summary>
    private async Task TellNewsAsync(Task previous, ChatNews news, CancellationToken floor)
    {
        ReplyVoice.SpokenReply? spoken = null;
        var asked = false;
        try
        {
            await previous;
            var began = _time.GetUtcNow();
            var lines = await news.TakeAsync(CancellationToken.None);
            if (lines.Count == 0)
            {
                return;
            }

            var taken = _time.GetUtcNow();

            var card = AddEntry(RavenLogKind.News, lines.Count == 1 ? "Chat news" : $"Chat news · {lines.Count}");
            card.Lines = lines;
            // The user sees the card, and maybe hears part of it before a press stops it: the brain that acts is told the
            // facts with the next question either way, so "open it" finds what "it" is.
            var at = _time.GetUtcNow();
            _toldNews.AddRange(lines.Select(l => (at, Fact(l))));
            var fresh = lines.Where(l => !l.Stale).ToList();
            if (fresh.Count == 0 || !SpeakNews || IsMuted || floor.IsCancellationRequested)
            {
                return;
            }

            _voice.Expect();
            var asking = default(DateTimeOffset);
            spoken = _voice.Begin(heard => _logger.LogInformation(
                "Raven's news: first word {Total:0} ms after it began ({Take:0} ms reading the board, {Teller:0} ms from the teller's question)",
                (heard - began).TotalMilliseconds, (taken - began).TotalMilliseconds, (heard - asking).TotalMilliseconds));
            asked = _teller is not null;
            asking = _time.GetUtcNow();
            var said = _teller is not null && await StreamAnswerAsync(_teller, DigestPrompt(fresh), spoken, floor, quiet: true);
            if (!said && !floor.IsCancellationRequested)
            {
                var sentence = FallbackSentence(fresh);
                AddEntry(RavenLogKind.Raven, sentence);
                spoken.Add(sentence);
            }

        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telling the chat news failed");
        }
        finally
        {
            if (!asked)
            {
                _teller?.Rest(); // warmed up for news that came to nothing
            }

            spoken?.Complete();
            _telling = false;
            UpdateState();
        }
    }

    /// <summary>What the teller is given for a digest: the news (how to tell it is its system prompt).</summary>
    internal static string DigestPrompt(IReadOnlyList<ChatNewsLine> lines)
    {
        var text = new System.Text.StringBuilder("News of the chats:");
        // Chats that share a workspace and title are numbered, or the teller takes them for one ("Weather discussion" twice).
        var same = lines.GroupBy(l => (l.Workspace, l.Title)).Where(g => g.Count() > 1).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var line in lines)
        {
            var title = same.TryGetValue((line.Workspace, line.Title), out var twins)
                ? $"\"{line.Title}\" ({twins.IndexOf(line) + 1} of {twins.Count})"
                : $"\"{line.Title}\"";
            text.Append('\n').Append($"- {line.Workspace}, chat {title}: {line.What}");
            if (line.Detail is { Length: > 0 } detail)
            {
                text.Append($": \"{detail}\"");
            }

            if (line.LastSaid is { Length: > 0 } lastSaid)
            {
                text.Append($". It last said: \"{lastSaid}\"");
            }
        }

        return text.ToString();
    }

    /// <summary>One line of news as the brain that acts is told it: the facts only, never what the chat said or asked.</summary>
    private static string Fact(ChatNewsLine line) => $"{line.Workspace}, chat \"{line.Title}\": {line.What}";

    /// <summary>
    /// The question as it goes to the brain that acts: after the chat news the user was given since its last question,
    /// so "open the one that needs me" works. The news older than <see cref="ToldNewsLifetime"/> is dropped; what goes
    /// along is kept until the brain has it, so a question merged into the next or failed before it went loses none.
    /// </summary>
    private string WithToldNews(Question question)
    {
        var now = _time.GetUtcNow();
        _toldNews.RemoveAll(t => now - t.At > ToldNewsLifetime);
        question.Told = [.. _toldNews];
        return question.Told.Count == 0
            ? question.Text
            : "[Chat news the user was given since their last question: " + string.Join("; ", question.Told.Select(t => t.Fact)) + ".]\n" + question.Text;
    }

    /// <summary>The digest when the brain gives none: "ContentAutomatorX finished, and CodeSwitchX needs you."</summary>
    internal static string FallbackSentence(IReadOnlyList<ChatNewsLine> lines)
    {
        var parts = lines.Select(l => $"{l.Workspace} {l.What}").ToList();
        return (parts.Count == 1 ? parts[0] : string.Join(", ", parts[..^1]) + ", and " + parts[^1]) + ".";
    }

    /// <summary>A line of a digest card was clicked: its chat's tile is shown.</summary>
    [RelayCommand]
    private void ShowNewsTile(ChatNewsLine? line)
    {
        if (line is not null)
        {
            TileRequested?.Invoke(this, line.WorkspaceId);
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
    /// <param name="quiet">For an Open mic turn: the warning says the next turn tries again, as pressing the mic would pause Open mic.</param>
    private async Task<bool> DownloadModelAsync(long number, bool quiet)
    {
        // The size of the model picked in Settings: Tiny is 78 MB, Large v3 Turbo 1.6 GB.
        var prefix = $"Downloading the speech model ({SizeOf(_models.Model)})… ";
        var entry = AddEntry(RavenLogKind.Note, prefix + "0%");
        var progress = new PostedPercent(_dispatcher, percent => entry.Text = $"{prefix}{percent}%");
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
            _dropRetry = quiet ? " Speak again to try again." : " Press the mic to try again.";
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

    /// <summary>"78 MB", "1.6 GB": a Whisper model's size, as the download note says it.</summary>
    internal static string SizeOf(WhisperModel model)
    {
        var bytes = WhisperModelStore.ApproximateBytes(model);
        return bytes >= 1_000_000_000
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1e6:0} MB");
    }

    private string DropWarning() => _dropReason + _dropped switch
    {
        0 => "",
        1 => " 1 waiting recording was dropped.",
        var n => $" {n} waiting recordings were dropped.",
    } + _dropRetry;

    /// <summary>
    /// What the panel shows: Listening while capturing, whatever is queued behind it; otherwise Speaking while Raven
    /// says something; otherwise Transcribing while any stopped clip is pending (the download, or how many wait behind
    /// the one transcribing, in the caption); otherwise Thinking while a question is unanswered; otherwise Idle, or in
    /// Open mic Attending once its microphone is open (AttendingPaused while paused; Idle, "Starting Open mic…", before
    /// it opens). Speech heard in Open mic shows Listening, as a press does.
    /// </summary>
    private void UpdateState()
    {
        if (_capturing)
        {
            State = RavenState.Listening;
            Caption = "Listening…";
            return;
        }

        if (_openSpeech)
        {
            State = RavenState.Listening;
            Caption = "Listening…";
            return;
        }

        if (_speaking)
        {
            State = RavenState.Speaking;
            // Talking interrupts in push to talk (a press) and in an Open mic that listens with barge-in on. Otherwise
            // (Open mic paused or starting, or ignoring speech while Raven talks; a press there only pauses) a typed
            // question is what interrupts.
            var talkInterrupts = MicMode == MicMode.PushToTalk || (_openRun is not null && !_attendPaused && BargeIn);
            Caption = talkInterrupts ? "Speaking… Talk to interrupt." : "Speaking… Type to interrupt.";
            return;
        }

        Level = 0;
        if (_pending == 0 && _asking == 0 && _telling)
        {
            State = RavenState.Thinking;
            Caption = "Telling chat news…";
            return;
        }

        if (_pending == 0 && _asking > 0)
        {
            State = RavenState.Thinking;
            Caption = _asking > 1 ? $"Thinking… ({_asking - 1} waiting)" : "Thinking…";
            return;
        }

        if (_pending == 0)
        {
            if (MicMode == MicMode.OpenMic && _attendPaused)
            {
                State = RavenState.AttendingPaused;
                Caption = "Open mic paused";
            }
            else if (MicMode == MicMode.OpenMic && _openRun is null)
            {
                // The microphone is not open yet: the orb does not say "Open mic" before it listens.
                State = RavenState.Idle;
                Caption = _openMicDownload is { IsCompleted: false } ? "Downloading Open mic's models…" : "Starting Open mic…";
            }
            else if (MicMode == MicMode.OpenMic)
            {
                State = RavenState.Attending;
                Caption = "Open mic";
            }
            else
            {
                State = RavenState.Idle;
                Caption = IdleCaption;
            }

            ScheduleNews();
            return;
        }

        State = RavenState.Transcribing;
        Caption = _downloading ? "Downloading the speech model…"
            : _pending > 1 ? $"Transcribing… ({_pending - 1} waiting)"
            : "Transcribing…";
    }

    /// <summary>The oldest entries go first: the log of a panel left open for days of dictation would grow for good.</summary>
    private RavenLogEntry AddEntry(RavenLogKind kind, string text) => Append(new RavenLogEntry(kind, text, _time.GetUtcNow()));

    private RavenLogEntry Append(RavenLogEntry entry)
    {
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
