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

    private static readonly TimeSpan MinimumClip = TimeSpan.FromMilliseconds(500);

    /// <summary>The smallest change of Open mic's level that is passed on to the orb.</summary>
    private const double VisibleLevelChange = 0.01;

    private readonly IMicrophoneCatalog _catalog;
    private readonly IMicrophoneRecorder _recorder;
    private readonly IDictationService _dictation;
    private readonly IWhisperModelStore _models;
    private readonly IDictationVocabularyProvider _vocabulary;
    /// <summary>The Yard's brain; every chat's when there are no <see cref="_brains"/>.</summary>
    private readonly IConductorBrain _brain;

    /// <summary>Each chat's own brain (#123): what is said in a chat is only in its conversation.</summary>
    private readonly IChatBrains? _brains;
    private readonly ReplyVoice _voice;
    private readonly ITextToSpeech _tts;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<RavenPanelViewModel> _logger;
    private readonly PushToTalkGesture _gesture;

    /// <summary>The sound another chat makes instead of speaking; none in tests that do not watch for it.</summary>
    private readonly IChatChime? _chime;

    /// <summary>The inputs holding push to talk now: the gesture sees one hold, which ends when the last of them is let go.</summary>
    private readonly HashSet<TalkInput> _heldInputs = [];
    private readonly SilentMicWatch _silence = new();
    private readonly SpeechGate _speech = new();
    private readonly ITimer _deviceRefresh;
    private readonly ChatNews? _news;
    private readonly ITimer _newsTimer;

    /// <summary>Cancelled when the user takes the floor: the answer or digest that holds it stops (UI thread).</summary>
    private CancellationTokenSource _floor = new();

    /// <summary>The questions asked and not over yet, in the order asked (UI thread).</summary>
    private readonly List<Question> _questions = [];

    /// <summary>Those that have not gone to their brain yet, and are not taken along by another: they go with the next words.</summary>
    private List<Question> Unsent() => [.. _questions.Where(q => q is { Sent: false, Ended: false, Merged: false })];

    /// <summary>Cancelled by a press: the digest being told stops, before it begins speaking too (UI thread).</summary>
    private CancellationTokenSource? _digest;

    private readonly IConductorBrain? _teller;

    /// <summary>The teller was warmed up for a long command to read out; rested when none was.</summary>
    private bool _tellerWarm;

    /// <summary>
    /// The chat news the user was given (a card written, a digest told), in facts only (workspace, title, what happened),
    /// for the brain that acts to know with the user's next question. Never what the chats said. An item goes once the
    /// brain has it, or once it is older than <see cref="ToldNewsLifetime"/> (UI thread). A card's or news line's fact goes
    /// to the brain of its own window's chat only (For, #137), with that chat's next question: another window's brain would
    /// take it for its own window's. A window chat asks its tools about other windows. What became of an allow a brain
    /// proposed goes to that brain only too.
    /// </summary>
    private readonly List<ToldFact> _toldNews = [];

    /// <summary>
    /// A fact the user was given about a card or news of <paramref name="chat"/>, for that window's chat only (#137), kept by
    /// the window and not its brain: a brain made anew for the window still gets it. A fact of chat 0 is kept for no one
    /// when chat 0 is the overview: it is given no card or news fact, and has no card of its own (#148).
    /// </summary>
    private void Tell(string fact, RavenChat chat, DateTimeOffset? at = null)
    {
        if (chat.WorkspaceId is null && _summarizer is not null)
        {
            return;
        }

        _toldNews.Add(new ToldFact(at ?? _time.GetUtcNow(), fact, chat.WorkspaceId, Proposer: null));
    }

    /// <param name="Window">The window whose chat the fact is for; null for chat 0.</param>
    /// <param name="Proposer">For what became of an allow: the brain that proposed it, alone; then <paramref name="Window"/> is unused.</param>
    private sealed record ToldFact(DateTimeOffset At, string Fact, Guid? Window, IConductorBrain? Proposer)
    {
        /// <summary>The brains that have it.</summary>
        public HashSet<IConductorBrain> ToldTo { get; } = [];
    }

    /// <summary>How long news the user was given stays worth telling the brain with a question.</summary>
    public static readonly TimeSpan ToldNewsLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The key of the brain that words chat news (<see cref="ClaudeCliBrain.TellerPrompt"/>) among the app's services.</summary>
    public const string TellerKey = "raven-teller";

    /// <summary>The key of the brain that sums a window's chat up for chat 0 (<see cref="ClaudeCliBrain.SummarizerPrompt"/>).</summary>
    public const string SummarizerKey = "raven-summarizer";

    /// <summary>
    /// Words the line chat 0 knows a window's chat by (#124). It reads that chat's words and cards and has no tools; chat 0's
    /// brain gets the line only. Null: chat 0 is given what every chat is, the news facts.
    /// </summary>
    private readonly IConductorBrain? _summarizer;

    /// <summary>Window chats to sum up again, each once, in the order asked (UI thread).</summary>
    private readonly List<RavenChat> _toSummarize = [];

    /// <summary>The summing up running now, or the last; it never faults (UI thread).</summary>
    private Task _summaries = Task.CompletedTask;

    /// <summary>The latest entries of a window's chat its summary is made from; the summary so far stands for those before.</summary>
    internal const int SummaryEntries = 12;

    /// <summary>The most of an entry the summarizer is given: a long answer says what it is about in its start.</summary>
    internal const int SummaryEntryLength = 400;

    /// <summary>The most of a summary chat 0 is given: the prompt asks for two short lines, and a brain may say more.</summary>
    internal const int SummaryLength = 300;

    /// <summary>
    /// How long chat 0's question waits for summaries still being made: asked right after a turn in a window's chat, it
    /// would be answered from the summary before that turn. A summarizer that takes longer is not waited for.
    /// </summary>
    public static readonly TimeSpan SummaryWait = TimeSpan.FromSeconds(6);

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

    /// <summary>When Raven's last answer was heard to its end, or written: the follow-up runs from here (#217).</summary>
    private DateTimeOffset? _followUpFrom;

    /// <summary>When the turn that was Raven's name alone ended: a turn begun soon after needs no name (#217).</summary>
    private DateTimeOffset? _calledAt;

    /// <summary>When the Open mic turn under way began (UI thread).</summary>
    private DateTimeOffset _openBegan;

    /// <summary>The Open mic turn under way began while Raven spoke.</summary>
    private bool _openOverRaven;

    /// <summary>An Open mic turn as it began: when, and whether in the follow-up, so Raven takes it without its name (#217).
    /// A short one is a word on its own, which counts only as the name, or as a yes in the follow-up.</summary>
    /// <param name="SpokeTill">When its speech ended: a turn with the name opens the window for its rest from there (#219).</param>
    /// <param name="OverRaven">It began while Raven spoke: through speakers that may be Raven's own voice, never the rest of a request.</param>
    private readonly record struct OpenTurn(DateTimeOffset Began, bool FollowUp, bool Short, DateTimeOffset SpokeTill, bool OverRaven);

    /// <summary>How long after its name alone Raven waits for the words, whatever the follow-up is set to.</summary>
    internal static readonly TimeSpan CallWindow = TimeSpan.FromSeconds(DefaultFollowUpSeconds);

    /// <summary>When the last turn that began with Raven's name ended: a turn begun within <see cref="ContinueWindow"/> of
    /// it is the rest of what the user said, split by a pause (#219).</summary>
    private DateTimeOffset? _namedEndedAt;

    /// <summary>How soon after a turn with Raven's name the next one must begin to go with it: a pause in what the user says,
    /// not the TV's next line.</summary>
    internal static readonly TimeSpan ContinueWindow = TimeSpan.FromSeconds(2);

    /// <summary>The question the last turn with Raven's name asked: the rest of it, after a pause, goes with it (#219).</summary>
    private Question? _namedQuestion;

    /// <summary>How much longer Raven listens for the words after its name alone (#219): nothing of its own comes meanwhile.</summary>
    private TimeSpan CallListening() => _calledAt is { } called ? called + CallWindow - _time.GetUtcNow() : TimeSpan.Zero;

    /// <summary>Whether <paramref name="at"/> is within <paramref name="window"/> from <paramref name="from"/>.</summary>
    private static bool Within(DateTimeOffset? from, TimeSpan window, DateTimeOffset at) => from is { } f && at >= f && at <= f + window;

    /// <summary>The note that follows the voice's install and first load, while it stands.</summary>
    private RavenLogEntry? _voiceNote;

    private readonly IOpenMic? _openMic;

    /// <summary>The listener's run Open mic listens with; null while its microphone is not open (UI thread). Events of
    /// any other run are old news.</summary>
    private OpenMicRun? _openRun;

    /// <summary>Open mic is paused by the user, or by a failure until the next press.</summary>
    private bool _attendPaused;

    /// <summary>The user is talking in Open mic: a turn begun in the follow-up (#217) is under way, so it is theirs.</summary>
    private bool _openSpeech;

    /// <summary>Open mic hears a turn: <see cref="IOpenMic.SpeechStarted"/> came, the turn has not ended. Whether it is for
    /// Raven is known from its start (<see cref="_openSpeech"/>) or once it is transcribed (its name).</summary>
    private bool _openHeard;

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
    private readonly Dictionary<string, ChatAskCard> _askCards = new(StringComparer.Ordinal);

    /// <summary>Questions shown and not yet read out: they go before the news when the floor is free (UI thread).</summary>
    private readonly List<ChatAskCard> _untold = [];

    /// <summary>
    /// A card was answered here and others wait: the next one is offered ("2 more questions are waiting. Next?", #230) once
    /// the floor is free (UI thread).
    /// </summary>
    private bool _nextOfferDue;

    /// <summary>
    /// When the offer of the next question was heard to its end, or written: a yes said within <see cref="NextOfferLifetime"/>
    /// after it takes it, also when its words come later. Kept until the user's next words (UI thread).
    /// </summary>
    private DateTimeOffset? _nextOffered;

    /// <summary>
    /// A bare yes in Open mic takes the offer (#233): it was heard, or only written because the user talked or the voice was
    /// not ready. Written because Raven keeps quiet (muted, chat news only written, a muted chat), a yes from the TV must
    /// not read a card aloud the user chose to keep quiet: it takes the name (UI thread).
    /// </summary>
    private bool _nextOfferTakesBareYes;

    /// <summary>The offer stands: heard, and neither answered, let go nor lapsed. Raven says nothing of its own meanwhile, as while an allow waits for its yes (UI thread).</summary>
    private bool _offerStands;

    /// <summary>Counts the offers: a callback of one that is over (heard late, or its lapse) does nothing (UI thread).</summary>
    private int _offerNumber;

    /// <summary>Lets the offer lapse once <see cref="NextOfferLifetime"/> is over, and the news held for it is told.</summary>
    private ITimer? _offerTimer;

    /// <summary>The card "next question" asked the brain for (#230): read out with chat news only written too (UI thread).</summary>
    private ChatAskCard? _requested;

    /// <summary>Whether "Next?" stands (UI thread).</summary>
    internal bool OffersNext => _offerStands;

    /// <summary>How long a yes to "Next?" counts: as long as one to an allow.</summary>
    internal static readonly TimeSpan NextOfferLifetime = ChatAsks.ProposalLifetime;

    /// <param name="asks">What chats ask, held while the user answers it here; null shows no questions.</param>
    /// <param name="yard">Names the chat that asks, as the Yard shows it.</param>
    public RavenPanelViewModel(IMicrophoneCatalog catalog, IMicrophoneRecorder recorder, IDictationService dictation,
        IWhisperModelStore models, IDictationVocabularyProvider vocabulary, IConductorBrain brain, ReplyVoice voice, ITextToSpeech speech,
        IUiDispatcher dispatcher, TimeProvider time, ILogger<RavenPanelViewModel> logger, ChatNews? news = null,
        [FromKeyedServices(TellerKey)] IConductorBrain? teller = null, IOpenMic? openMic = null, ChatAsks? asks = null, IYardDirectory? yard = null,
        IChatBrains? brains = null, [FromKeyedServices(SummarizerKey)] IConductorBrain? summarizer = null, IChatChime? chime = null,
        SpeakerChoice? speakers = null, UnaskedTurns? unasked = null)
    {
        Speakers = speakers;
        _catalog = catalog;
        _recorder = recorder;
        _dictation = dictation;
        _models = models;
        _vocabulary = vocabulary;
        _brain = brain;
        _brains = brains;
        _summarizer = brains is null ? null : summarizer; // with one brain for every chat there is no overview to keep
        _voice = voice;
        _tts = speech;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        _gesture = new PushToTalkGesture(time);
        _chime = chime;
        Traffic = new TrafficWatcher(time);
        Chats = [YardChat, ActivityChat];
        _selectedChat = YardChat;
        if (unasked is not null)
        {
            unasked.Taken += turn => _dispatcher.Post(() => ShowUnasked(turn));
        }

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
        Traffic.PauseChanged += (_, _) => _dispatcher.Post(ScheduleNews); // set shorter, what waits is told sooner
        if (news is not null)
        {
            news.Arrived += (_, workspaceId) => _dispatcher.Post(() =>
            {
                // Only news of the chat the user is in is spoken (#125): another window's starts no teller (#139), unless
                // the user switches to its chat before it is told.
                if (workspaceId is not null && ChatOf(workspaceId) == CurrentChat)
                {
                    WarmTellerForCurrentNews();
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
            asks.ProposedAllow += proposal => _dispatcher.Post(() => OnProposed(proposal));
            asks.ProposalEnded += (proposal, end) => _dispatcher.Post(() => OnProposalEnded(proposal, end));
        }
    }

    /// <summary>How many chats' questions wait for an answer here: the collapsed rail shows it.</summary>
    [ObservableProperty]
    private int _openQuestions;

    /// <summary>Raised when the user clicks a chat's line on a digest card: the shell shows its tile.</summary>
    public event EventHandler<Guid>? TileRequested;

    /// <summary>"Open chat three": the shell shows the chat's window in the Cab (switching alone never does).</summary>
    public event EventHandler<Guid>? CabRequested;

    [ObservableProperty]
    private bool _isOpen = true;

    /// <summary>
    /// The microphone recorded from, which the panel's picker shows: one picked there to try it while it is plugged in,
    /// the default otherwise. A pick on the panel is never saved (<see cref="TrialMicrophone"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicTrialNote))]
    private MicrophoneDevice? _selectedMicrophone;

    /// <summary>
    /// The default as listed, which Settings shows and picks: the preferred one while it is plugged in, the Windows
    /// default otherwise. A pick makes it the preferred one and ends a trial on the panel.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicTrialNote))]
    private MicrophoneDevice? _defaultMicrophone;

    /// <summary>
    /// The user's choice of default: the one stored in the settings, or the one last picked in Settings. Only a pick
    /// changes it, never a fallback, so the choice is selected again when its device comes back (RØDE Connect started
    /// after CodeSwitchX, a Bluetooth headset waking up). Null means "no choice": the Windows default.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicTrialNote))]
    private MicrophoneDevice? _preferredMicrophone;

    /// <summary>
    /// A microphone picked on the panel to try it, other than the default: heard until the default is picked again, it
    /// is unplugged or the app restarts. Kept in memory only. Null: the default is heard.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicTrialNote))]
    private MicrophoneDevice? _trialMicrophone;

    /// <summary>Under the panel's picker while a microphone is tried there: that it is not saved; null otherwise.</summary>
    public string? MicTrialNote => TrialMicrophone is null || SelectedMicrophone is null ? null
        : (PreferredMicrophone ?? DefaultMicrophone) is { } chosen ? $"Trying it. Not saved: Raven starts with {chosen.Name}." : "Trying it. Not saved.";

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

    /// <summary>How long after Raven spoke or answered an Open mic turn needs no "Raven" before it (#217); 0 means every turn
    /// does. The shell keeps it in step with Settings.</summary>
    [ObservableProperty]
    private int _followUpSeconds = DefaultFollowUpSeconds;

    /// <summary>The follow-up Settings offers, in seconds (#217); 0 is none.</summary>
    public static readonly IReadOnlyList<int> FollowUpChoices = [0, 5, 10, 15, 20, 30, 60];

    public const int DefaultFollowUpSeconds = 10;

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

    /// <summary>The output Raven speaks on (#172): the panel tries one, Settings keeps the default. Null in tests that leave it out.</summary>
    public SpeakerChoice? Speakers { get; }

    /// <summary>Every entry of every chat, in time order: Activity (UI thread).</summary>
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

    /// <summary>The last check of whether a reply written as said was heard to its end (#178).</summary>
    internal Task PendingHeardCheck => Task.WhenAll(_pendingCounts, _pendingReads);

    /// <summary>
    /// The waits for an Open mic answer, or an allow's read-back, to be heard before the follow-up runs from then (#217):
    /// completes once every follow-up they open runs.
    /// </summary>
    internal Task PendingFollowUp
    {
        get
        {
            lock (_followUpWaits)
            {
                return _pendingFollowUp;
            }
        }
    }

    private Task _pendingFollowUp = Task.CompletedTask;

    /// <summary>Guards <see cref="_pendingFollowUp"/>: an answer's end and a read-back may add to it from different threads in tests.</summary>
    private readonly object _followUpWaits = new();

    /// <summary>Adds a wait to <see cref="PendingFollowUp"/>; once all before it ran, it starts the chain again (a fault stays to be seen).</summary>
    private void AwaitFollowUp(Task wait)
    {
        lock (_followUpWaits)
        {
            _pendingFollowUp = _pendingFollowUp.IsCompletedSuccessfully ? wait : Task.WhenAll(_pendingFollowUp, wait);
        }
    }

    /// <summary>The checks of replies written as said, to count a line that turned out not heard (#178; UI thread).</summary>
    private Task _pendingCounts = Task.CompletedTask;

    /// <summary>The window chats being summed up for chat 0.</summary>
    internal Task PendingSummaries => _summaries;

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
        var trialPicked = TrialMicrophone;
        MicrophoneChoiceResult choice;
        MicrophoneDevice? trial = null;
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
            if (trialPicked is not null
                && MicrophoneChoice.Resolve(devices, trialPicked, windowsDefault) is { Outcome: MicrophoneChoiceOutcome.Stored or MicrophoneChoiceOutcome.Relocated } tried)
            {
                trial = tried.Device;
            }

            DefaultMicrophone = choice.Device;
            SelectedMicrophone = trial ?? choice.Device;
        }
        finally
        {
            _refreshing = false;
        }

        // Only the user ends a trial, or its microphone going: the default falling back onto the same device does not.
        TrialMicrophone = trial;
        if (_openMicWaitsForList && MicMode == MicMode.OpenMic)
        {
            _openMicWaitsForList = false;
            PendingOpenMic = StartOpenMicAsync();
        }

        if (trialPicked is not null && trial is null)
        {
            // The microphone being tried went: the default is heard again, and the trial is over.
            _fellBack = choice.Outcome == MicrophoneChoiceOutcome.FellBackToDefault;
            AddEntry(RavenLogKind.Note, choice.Device is null
                ? $"{trialPicked.Name} is gone. No microphone is connected."
                : $"{trialPicked.Name} is gone. Using {choice.Device.Name}.");
            return;
        }

        if (trial is not null)
        {
            return; // the trial is heard: the default's comings and goings show in Settings only
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
            DefaultMicrophone = null;
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>A pick in Settings.</summary>
    partial void OnDefaultMicrophoneChanged(MicrophoneDevice? value)
    {
        if (!_refreshing && value is not null)
        {
            ChooseDefaultMicrophone(value);
        }
    }

    /// <summary>The new default, saved, and heard from now on, ending a trial: also when it is the default already, as by voice.</summary>
    public void ChooseDefaultMicrophone(MicrophoneDevice device)
    {
        DefaultMicrophone = device;
        PreferredMicrophone = device;
        TrialMicrophone = null; // also when the mic being tried is the one picked: then the selection does not change
        SelectedMicrophone = device;
    }

    partial void OnSelectedMicrophoneChanged(MicrophoneDevice? value)
    {
        if (!_refreshing && value is not null)
        {
            // A pick on the panel: tried, not saved. Picking the default there ends the trial.
            TrialMicrophone = Equals(value, DefaultMicrophone) ? null : value;
        }

        if (_openRun is { } run && value is not null && value.Id != run.DeviceId)
        {
            StopOpenMic();
            PendingOpenMic = StartOpenMicAsync();
        }
    }

    [RelayCommand]
    private void TogglePanel() => IsOpen = !IsOpen;

    /// <summary>A number on the collapsed strip (#173): Raven opens on that chat, which is seen then.</summary>
    [RelayCommand]
    private void OpenChat(RavenChat chat)
    {
        SelectedChat = chat; // chosen first: opening must not count the chat shown before as seen
        IsOpen = true;
    }

    /// <summary>Opened: the chat shown is seen. Collapsed, no chat is: what comes is counted on the strip's numbers.</summary>
    partial void OnIsOpenChanged(bool value)
    {
        if (value && !SelectedChat.IsActivity)
        {
            Seen(SelectedChat);
        }
    }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>
    /// Muting stops what is being said; unmuting gets the voice ready, so the next answer is spoken. A read-back cut by the
    /// mute counts as heard, as one shown while muted does: its whole line is in the log.
    /// </summary>
    partial void OnIsMutedChanged(bool value)
    {
        if (value && _asks?.Proposed is { } proposal)
        {
            _asks.MarkHeard(proposal, _time.GetUtcNow()); // before the hush, which would settle it as not heard
        }

        // Muted, the voice says nothing: what it says now stops, and only a reply begun evenMuted is said, an answer to
        // words said aloud (#242).
        _voice.Muted = value;
        if (value)
        {
            StopCatchUp();
            _requestedAloud = false; // a card asked for aloud and not read yet: muted now, it is only shown
        }
        else
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
        _openHeard = false;
        _calledAt = null; // nothing listens for the words any more: news need not wait (#219)
        _namedEndedAt = null;
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
        Traffic.Announced(); // begun or ended, the cooldown runs from the last of it
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
                // Cancelled on the Voice page, or another engine (or none) picked: the note does not go on claiming an install.
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

            // Only a voice installed before: the install itself waits for the first answer to speak. Muted too: what the
            // user asks aloud is still answered aloud (#242).
            _tts.Prepare(install: false);
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
        var turn = TranscribeInTurnAsync(_pipeline, number, stop, speech, mic, words, ended, CurrentChat);
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

        TypedText = "";
        _lastWordsAloud = false; // typed: answered in writing while muted (#242)
        if (SwitchBySaying(text))
        {
            return;
        }

        var chat = CurrentChat;
        var asked = AddEntry(RavenLogKind.You, text, chat);
        _voice.Expect(); // muted, it does nothing: the answer is only written (#242)
        Ask(text, _time.GetUtcNow(), chat, asked);
    }

    public void Note(string text) => AddEntry(RavenLogKind.Note, text);

    /// <summary>
    /// Raven's line in the window's chat, written and not said: what came of something after its question was answered
    /// (a long compaction, #226). Unread where the user does not see it; a failure is a warning.
    /// </summary>
    public void Tell(Guid workspaceId, string text, bool failed)
    {
        var chat = ChatOf(workspaceId);
        if (failed)
        {
            AddEntry(RavenLogKind.Warning, text, chat);
        }
        else
        {
            AddSaid(text, chat, said: false);
        }
    }

    public void Warn(string text) => AddEntry(RavenLogKind.Warning, text);

    /// <summary>
    /// A chat's summary (#234), written in the Raven chat it was asked in (by its key, as its brain sends it), unspoken: Raven
    /// says its short part. The chat the user is in for null, or for a chat the list no longer shows.
    /// </summary>
    public void WriteSummary(string? askedIn, string text)
    {
        var chat = askedIn == YardMcp.OverviewChat ? YardChat
            : Guid.TryParse(askedIn, out var window) ? Chats.FirstOrDefault(c => c.WorkspaceId == window) ?? CurrentChat : CurrentChat;
        AddSaid(text, chat, said: false);
    }

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
        DropCatchUp(); // a press stops the catch-up, also one not begun
        _digest?.Cancel();
        _voice.Hush();
        _voice.Expect(evenMuted: true); // the user talks to Raven: answered aloud, muted too (#242)
        BrainOf(CurrentChat).WarmUp();
    }

    /// <summary>
    /// The chat's own brain: a window's chat talks with its window's, chat 0 with the Yard's. A window gone from the list
    /// has its brain retired, and is not given a new one: what is left of it goes to the Yard's.
    /// </summary>
    private IConductorBrain BrainOf(RavenChat chat) =>
        _brains?.For(chat.WorkspaceId is { } id && Chats.Contains(chat) ? id : null) ?? _brain;

    private void OnOpenSpeech(OpenMicRun run)
    {
        if (run != _openRun)
        {
            return; // stopped, paused or moved to another run since
        }

        // Only a turn begun in the follow-up is surely the user's, and takes the floor as it starts; any other may be the
        // TV, which must not hush Raven, hold its news or keep its answers unspoken, and waits to be heard to its name.
        _openHeard = true;
        _openBegan = _time.GetUtcNow() - TurnDetector.MinimumSpeech; // it counts once half a second of it was heard
        _openOverRaven = _speaking;
        if (InFollowUp(_openBegan))
        {
            // The user's: Raven stops, and so does an answer on its way, which would be said over the rest of a request
            // (#219) and heard into it through speakers.
            _openSpeech = true;
            UserStartsTalking();
        }

        _vocabularyFetch = Task.Run(FetchVocabularyAsync);
        UpdateState();
    }

    /// <summary>Whether an Open mic turn begun now would be taken without Raven's name (#217).</summary>
    internal bool TakesTurnsWithoutName => InFollowUp(_time.GetUtcNow());

    /// <summary>
    /// Whether a turn beginning now needs no name before it (#217): soon after Raven's name was heard alone, or right after
    /// a turn with the name (the rest of it, #219), also while Raven speaks or thinks; or while the follow-up runs: Raven's
    /// answer to an Open mic turn, or an allow's read-back, was heard or written a moment ago. The follow-up does not run
    /// while Raven speaks or thinks: talk then is the TV as often as the user, and only the name takes the floor.
    /// </summary>
    /// <param name="began">When the turn began: the windows are judged by that, as <see cref="ForRaven"/> judges them.</param>
    private bool InFollowUp(DateTimeOffset began)
    {
        // The rest of a request not while Raven speaks: through speakers, that is Raven's own voice.
        if (Within(_calledAt, CallWindow, began) || !_speaking && Within(_namedEndedAt, ContinueWindow, began))
        {
            return true;
        }

        return FollowUpSeconds > 0 && !_speaking && _asking == 0 && !_telling
            && _followUpFrom is { } from && began <= from + TimeSpan.FromSeconds(FollowUpSeconds);
    }

    /// <summary>The turn is over: its clip joins the transcription queue as a released recording's does.</summary>
    private void OnOpenTurn(OpenMicTurn turn)
    {
        if (turn.Run != _openRun || !_openHeard && !turn.Short)
        {
            return;
        }

        var clip = turn.Clip;
        var length = TimeSpan.FromSeconds((double)clip.Length / AudioMath.TargetRate);
        var spokeTill = _time.GetUtcNow() - turn.Silence;
        OpenTurn open;
        if (turn.Short)
        {
            // No start came: a word on its own, as long as its clip less the pre-roll and the pause kept after it.
            var spoken = length - TurnDetector.PreRoll - TurnDetector.Pause;
            var began = spokeTill - (spoken > TimeSpan.Zero ? spoken : TimeSpan.Zero);
            open = new OpenTurn(began, InFollowUp(began), Short: true, spokeTill, OverRaven: _speaking);
        }
        else
        {
            open = new OpenTurn(_openBegan, _openSpeech, Short: false, spokeTill, _openOverRaven);
            _openSpeech = false;
            _openHeard = false;
        }

        if (open.FollowUp)
        {
            _pending++; // only the user's turn holds the floor while it is transcribed: the TV's must not keep news back (#217)
        }

        var heard = new SpeechReading(true, 0, 0, length); // the detector heard the speech
        var number = ++_clipsQueued;
        var transcribed = TranscribeInTurnAsync(_pipeline, number, Task.FromResult<RecordedClip?>(new RecordedClip(clip, length)), heard,
            SelectedMicrophone?.Name, turn.Short ? Task.Run(FetchVocabularyAsync) : _vocabularyFetch, _time.GetUtcNow(), CurrentChat, quiet: true,
            open: open);
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
    /// <param name="chat">The chat the user was in when the turn ended: the words, and what is said of them, go there.</param>
    /// <param name="quiet">An Open mic turn: one too short or without words is only logged, never noted in the panel, as
    /// the user pressed nothing.</param>
    /// <param name="open">An Open mic turn: Raven takes it only with its name before it, or begun in the follow-up (#217).</param>
    private async Task TranscribeInTurnAsync(Task previous, long number, Task<RecordedClip?> stopping, SpeechReading speech,
        string? mic, Task<DictationVocabulary> vocabulary, DateTimeOffset ended, RavenChat chat, bool quiet = false, OpenTurn? open = null)
    {
        try
        {
            await previous;
            // Said after "chat three" but before it was heard (transcribed): the user is in chat 3 already. From the chat
            // the clip was said in, so a second switch in the queue maps the clips behind it too.
            var saidIn = chat;
            if (_spokenSwitch is { } switched && number > switched.At && number <= switched.Through
                && (chat == switched.From || chat == switched.Via))
            {
                chat = switched.To;
            }
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
                AddEntry(RavenLogKind.Note, "That was too short. Hold the keys or the mic button while you talk.", chat);
                return;
            }

            if (!speech.HeardSpeech)
            {
                _logger.LogInformation(
                    "No speech in {Seconds:0.0} s from {Microphone}: loudest block {Loudest:0.0000}, {Speech:0.00} s at the open level {Open:0.0000}",
                    clip.Length.TotalSeconds, mic, speech.Loudest, speech.Speech.TotalSeconds, speech.OpenRms);
                AddEntry(RavenLogKind.Note, "I didn't hear anything.", chat);
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

            var named = false;
            var continues = false;
            if (open is { } turn && text.Length > 0 && !ForRaven(ref text, turn, clip.Length, ended, out named, out continues))
            {
                return;
            }

            if (text.Length > 0)
            {
                _lastWordsAloud = true; // said: answered aloud, muted too (#242)
            }

            if (text.Length > 0 && SwitchBySaying(text))
            {
                _spokenSwitch = (number, _clipsQueued, saidIn, chat, CurrentChat);
                if (named)
                {
                    _namedQuestion = null; // asked nothing: its rest goes on from no question
                }
            }
            else if (text.Length > 0)
            {
                var asked = AddEntry(RavenLogKind.You, text, chat);
                // The rest of a request split by a pause (#219): the first half has gone to the brain already, and its answer
                // gives way to this one, so the brain is told what this goes on from. One not sent yet goes along anyway.
                var earlier = continues && _namedQuestion is { Sent: true, Merged: false } first && first.Chat == chat
                    ? $"[After a pause the user goes on from what they asked just before: \"{first.Text}\"]\n" : "";
                var before = CurrentChat;
                var question = Ask(text, ended, chat, asked, openMic: open is not null, earlier: earlier);
                if (question is null && CurrentChat != before)
                {
                    // A yes to "Next?" went to the next card's chat (#233): what was said behind it goes there, as after "chat three".
                    _spokenSwitch = (number, _clipsQueued, saidIn, chat, CurrentChat);
                }

                if (named)
                {
                    _namedQuestion = question;
                }
            }
        }
        catch (DictationModelLoadException ex)
        {
            // A damaged download fails here on every press, and nothing else ever replaces the file: say which to delete.
            _logger.LogWarning(ex, "The speech model could not be loaded");
            var reason = (ex.InnerException?.Message ?? ex.Message).TrimEnd().TrimEnd('.');
            AddEntry(RavenLogKind.Warning, chat: chat, text:
                quiet ? $"The speech model could not be loaded: {reason}. Delete {_models.ModelPath}; it is downloaded again with your next turn."
                    : $"The speech model could not be loaded: {reason}. Delete {_models.ModelPath} and press the mic to download it again.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transcription failed");
            AddEntry(RavenLogKind.Warning, $"Transcription failed: {ex.Message}", chat);
        }
        finally
        {
            if (open is not { FollowUp: false })
            {
                _pending--;
            }

            UpdateState();
        }
    }

    /// <summary>
    /// Whether an Open mic turn is meant for Raven (#217): it starts with its name, which is cut off, or it began in the
    /// follow-up (<see cref="InFollowUp"/>) or soon after the name alone. Anything else is speech around the room, a TV
    /// say: dropped, with only its length in the log. The name alone stops what Raven says and lets the turn after it
    /// through without the name, also one begun before the name was transcribed.
    /// </summary>
    /// <param name="named">The turn began with the name, or came right after the name alone: its rest may follow (#219).</param>
    /// <param name="continues">The turn is the rest of the last one with the name, after a pause.</param>
    private bool ForRaven(ref string text, OpenTurn turn, TimeSpan length, DateTimeOffset ended, out bool named, out bool continues)
    {
        named = false;
        continues = false;
        if (CommandWord.TryStrip(text, out var rest))
        {
            _calledAt = null;
            if (rest.Length > 0)
            {
                text = rest;
                _namedEndedAt = turn.SpokeTill;
                named = true;
                return true;
            }

            _calledAt = turn.SpokeTill;
            StopForName();
            _logger.LogInformation("Open mic heard Raven's name alone: the next turn needs no name");
            return false;
        }

        // "Next?" stands (#230), and Raven did not keep it quiet (#233): a yes answers it without the name. Nothing else does: the offer may follow a click, with the
        // user not talking, and talk around the room must stay out.
        // Judged by the instant Ask judges it by, so a yes let through here is never a question to the brain.
        if (_nextOfferTakesBareYes && _nextOffered is { } offered && ended >= offered && ended - offered <= NextOfferLifetime && SpokenYes.IsYes(text)
            && !WhisperNoise.Is(text))
        {
            _calledAt = null;
            return true;
        }

        // A word on its own counts only as a yes: a cough Whisper writes as "Thank you." must not be asked. Begun soon after
        // a turn with the name, a turn is the rest of that (#219); only a turn with the name, or the words right after the
        // name alone, start that window, so a TV line taken this way does not open it again.
        var called = Within(_calledAt, CallWindow, turn.Began);
        continues = !called && !turn.OverRaven && Within(_namedEndedAt, ContinueWindow, turn.Began);
        if ((turn.FollowUp || called || continues) && (!turn.Short || SpokenYes.IsYes(text)))
        {
            if (WhisperNoise.Is(text))
            {
                // A cough or a throat-clear Whisper writes as words: asked, it would take the floor from the answer.
                _logger.LogInformation("Open mic's turn of {Seconds:0.0} s was noise Whisper wrote as words: not asked", length.TotalSeconds);
                continues = false;
                return false;
            }

            if (called)
            {
                _namedEndedAt = turn.SpokeTill; // the words after the name alone are the named turn
                named = true;
            }

            _calledAt = null;
            return true;
        }

        continues = false;

        _logger.LogInformation("Open mic's turn of {Seconds:0.0} s did not start with Raven's name: not for Raven", length.TotalSeconds);
        return false;
    }

    /// <summary>
    /// Raven's name alone (#217): what Raven says stops, and a digest or catch-up does not begin, as when the user talks; an
    /// answer on its way is no talk yet, and is still said when it comes.
    /// </summary>
    private void StopForName()
    {
        DropCatchUp();
        _digest?.Cancel();
        if (_speaking)
        {
            _voice.Hush();
        }

        _voice.Expect(evenMuted: true); // the words after the name are answered aloud, muted too (#242)
        BrainOf(CurrentChat).WarmUp();
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
    /// <param name="asked">The words as written to the log, which go with the question wherever it goes (#180).</param>
    /// <param name="openMic">Said in Open mic: once its answer is heard, the follow-up runs (#217).</param>
    /// <param name="earlier">Words that go along before these, as <see cref="AskBrain"/> takes them.</param>
    /// <returns>The question, or null when the words were a yes to an allow.</returns>
    private Question? Ask(string text, DateTimeOffset ended, RavenChat chat, RavenLogEntry? asked = null, bool openMic = false, string earlier = "")
    {
        _brainAsked = false; // these words answer it, whatever they are
        // "Next?" after an answer (#230): these words answer it or let it go, and one not said yet is not said any more.
        var offered = _nextOffered;
        DropOffer();
        // An allow the brain proposed waits on these words, checked here and not by the brain (#108): a yes allows, and
        // goes no further; anything else drops the proposal and is the next question. Words are judged by when they were
        // said, not when they were transcribed: said before the read-back was heard to its end, they answer something
        // else; a yes said in time still counts when its transcript comes after the proposal lapsed.
        if (_asks?.ProposalFor(ended) is { } proposal)
        {
            if (SpokenYes.IsYes(text))
            {
                ConfirmProposal(proposal, ended);
                return null;
            }

            _asks.Cancel(proposal); // the brain is told (OnProposalEnded)
        }
        else if (_asks?.Proposed is { } standing)
        {
            // Said before its read-back was heard to its end, these words take the floor from it: the brain's answer to
            // them comes after the read-back, and a yes to that answer must not allow the prompt.
            _asks.Cancel(standing);
        }

        // A yes said after "Next?" was heard, and in time, goes to the next card, and to no brain: judged by when it was said,
        // as a yes to an allow is.
        if (offered is { } at && ended >= at && ended - at <= NextOfferLifetime && SpokenYes.IsYes(text))
        {
            GoToNextQuestion(_lastWordsAloud);
            return null;
        }

        return AskBrain(text, ended, chat, earlier, asked: asked, openMic: openMic);
    }

    /// <summary>The words go to the brain as the next question, after any not sent yet (UI thread).</summary>
    /// <param name="chat">Where the user asked: the answer goes there, wherever the user is when it comes.</param>
    /// <param name="earlier">Words of another chat that go along before these, each part saying where it was asked.</param>
    /// <param name="asked">The words as written to the log; null when they were written before (asked again).</param>
    /// <param name="entries">The log entries of a question asked again, which stay its own.</param>
    /// <param name="aloud">The words were said, not typed: answered aloud while muted (#242). Left out: the user's last words.</param>
    private Question AskBrain(string text, DateTimeOffset ended, RavenChat chat, string earlier = "", RavenLogEntry? asked = null,
        IReadOnlyList<RavenLogEntry>? entries = null, bool openMic = false, bool? aloud = null)
    {
        var said = aloud ?? _lastWordsAloud;
        var takenEarlier = "";
        var takenText = "";
        List<RavenLogEntry> takenEntries = [.. entries ?? []];
        List<Question> own = [];
        foreach (var waiting in Unsent())
        {
            waiting.Merged = true;
            if (BrainOf(waiting.Chat) != BrainOf(chat))
            {
                // Its own chat's brain answers it, in its chat, before these words: another chat's brain would act on its
                // own window, so "stop it" said in chat 3 would stop a chat in the window the user is in now.
                var again = new Question(waiting.Text, waiting.Chat, waiting.Earlier) { OpenMic = waiting.OpenMic, Aloud = waiting.Aloud };
                again.Entries.AddRange(waiting.Entries);
                own.Add(again);
            }
            else if (waiting.Chat == chat)
            {
                takenEarlier += waiting.Earlier;
                takenText += waiting.Text + "\n";
                takenEntries.AddRange(waiting.Entries);
                openMic |= waiting.OpenMic; // said in Open mic, its answer opens the follow-up as this one's (#219)
                said &= waiting.Aloud; // a half typed: answered in writing while muted, quiet wins
            }
            else
            {
                // Asked in another chat: it keeps saying where, or "stop it" there would mean the window the user is in now.
                takenEarlier += waiting.Earlier + $"[Said in chat {waiting.Chat.Number}, {NameOf(waiting.Chat)}:] " + waiting.Text + "\n";
                said &= waiting.Aloud;
                // Its lines stay in the chat they were said in: moved, they would leave that chat with no word why.
            }
        }

        var floor = TakeFloor();
        foreach (var other in own)
        {
            Enqueue(other, ended, floor);
        }

        var question = new Question(takenText + text, chat, Carried(chat) + takenEarlier + earlier) { OpenMic = openMic, Aloud = said };
        question.Entries.AddRange(takenEntries);
        if (asked is not null)
        {
            question.Entries.Add(asked);
        }

        Enqueue(question, ended, floor);
        return question;
    }

    /// <summary>
    /// Which chats Raven answers a question in (#182): one queued or running there, not one taken along by another (merged)
    /// or ended. Worked out from the questions whenever the panel's state is, so no change of a question is missed.
    /// </summary>
    private void RefreshAnswering()
    {
        foreach (var chat in Chats)
        {
            chat.IsAnswering = _questions.Any(q => !q.Ended && !q.Merged && q.Chat == chat);
        }
    }

    /// <summary>The chat as the brain is told of it: "the Yard" for chat 0.</summary>
    private string NameOf(RavenChat chat) => chat == YardChat ? "the Yard" : chat.Name;

    /// <summary>
    /// What the next question in a chat is told first, once, while it is fresh: the question Raven moved there with the
    /// user (#180), as that chat's brain is another and knows nothing of it. Empty for none. UI thread.
    /// </summary>
    private string Carried(RavenChat chat) =>
        _carried.Remove(chat, out var carried) && _time.GetUtcNow() - carried.At < CarriedLifetime ? carried.Text : "";

    /// <summary>How long a question moved to a chat is told to its next question (#180): one asked much later is about something else.</summary>
    internal static readonly TimeSpan CarriedLifetime = TimeSpan.FromMinutes(10);

    private readonly Dictionary<RavenChat, (string Text, DateTimeOffset At)> _carried = [];

    /// <summary>
    /// Raven, asked in the chat <paramref name="askedIn"/> names, started a chat in <paramref name="workspaceId"/>'s window
    /// (#180): the user follows the work. The question whose turn is starting it, its words and its answer so far, moves
    /// to that window's Raven chat, where the rest of the answer and the new chat's news come, and the panel shows that
    /// chat. A note in each chat says where the question went and where it came from. Nothing happens when no question of
    /// that chat's is starting a chat (one asked since, say), the window has no chat, or it is the user's own. UI thread.
    /// </summary>
    public void FollowWork(string askedIn, Guid workspaceId)
    {
        var to = Chats.FirstOrDefault(c => !c.IsActivity && c.WorkspaceId == workspaceId);
        var from = askedIn == YardMcp.OverviewChat ? YardChat
            : Guid.TryParse(askedIn, out var window) ? Chats.FirstOrDefault(c => c.WorkspaceId == window) : null;
        // The question whose turn is starting the chat: not one asked since in that chat, while a slow start went on.
        var question = _questions.FirstOrDefault(q => q.Sent && !q.Ended && q.StartingChat && q.AskedIn == from);
        if (to is null || from is null || from == to || question is null || question.Chat == to)
        {
            return;
        }

        var current = question.Chat;
        question.Chat = to;
        UpdateState(); // the work goes on there now (#182)
        _carried.Remove(current); // moved on: the chat it was in is told nothing of it
        var kept = question.Entries.Select(e => (Entry: e, At: Log.IndexOf(e))).Where(e => e.At >= 0).ToList();
        foreach (var (entry, _) in kept)
        {
            if (entry.IsUnread)
            {
                // The user is moved to the chat with it: its lines are shown to them, not come while they were away.
                entry.Chat.Unread -= entry.Unread;
                entry.Unread = 0;
            }

            entry.Chat = to;
        }

        current.IsNewsPulsing &= current.Unread > 0;
        if (question.Origin is null)
        {
            // Placed above the question, and so of its time: the log is in time order. A question whose first lines the
            // log has let go of is noted above what is left of it.
            var first = kept.Count > 0 ? kept[0].At : -1;
            var at = first >= 0 ? Log[first].At : _time.GetUtcNow();
            question.Origin = new RavenLogEntry(RavenLogKind.Note, $"Asked in chat {from.Number}, {NameOf(from)}.", at) { Chat = to };
            question.Entries.Insert(0, question.Origin);
            Log.Insert(first >= 0 ? first : Log.Count, question.Origin);
            TrimLog();
        }

        AddEntry(RavenLogKind.Note, $"Continued in chat {to.Number}, {to.Name}: Raven started a chat there.", current);
        if (BrainOf(to) != BrainOf(from))
        {
            // That chat's brain is another, and knows nothing of the question: its next question is told.
            _carried[to] = ($"[The user asked this in chat {from.Number}, {NameOf(from)}, and was moved here when Raven started a chat in this "
                + $"window: \"{question.Text}\"]\n", _time.GetUtcNow());
        }

        _logger.LogInformation("Raven moved the user from chat {From} to chat {To} with their question", current.Number, to.Number);
        if (SelectedChat == to)
        {
            ShowSelected(); // the entries moved into the chat shown
        }
        else
        {
            SelectedChat = to;
        }
    }

    /// <summary>The question goes to its chat's brain after those before it, on the floor it was given (UI thread).</summary>
    private void Enqueue(Question question, DateTimeOffset ended, CancellationToken floor)
    {
        _questions.Add(question);
        _asking++;
        UpdateState();
        var asked = new StrongBox<DateTimeOffset>();
        // In the user's next Open mic turn the answer is only written: Raven's voice would talk over the turn and, heard
        // through speakers, end up in it, and the question being spoken replaces the answer anyway. TakeFloor hushed already.
        var spoken = _voice.Begin(heard => _logger.LogInformation(
            "Raven's first word {Total:0} ms after the end of the turn, {Answer:0} ms after the question went to the brain",
            (heard - ended).TotalMilliseconds, (heard - asked.Value).TotalMilliseconds), silent: _openSpeech, evenMuted: question.Aloud);

        question.MutedAtStart = IsMuted;
        question.WrittenOverTalk = _openSpeech;
        _conversation = AnswerInTurnAsync(_conversation, question, spoken, asked, floor);
    }

    /// <summary>
    /// A question on its way to the brain: <see cref="Sent"/> once the brain says it has it (<see cref="BrainQuestionSent"/>),
    /// <see cref="Merged"/> when a later one took it along, because it had not, and <see cref="Ended"/> once its turn is over
    /// (answered, failed): a question that failed is not asked again with a later one (UI thread).
    /// </summary>
    private sealed class Question(string text, RavenChat chat, string earlier)
    {
        /// <summary>The words asked in <see cref="Chat"/>, those of a question it took along from the same chat first.</summary>
        public string Text { get; } = text;

        /// <summary>Words it took along from another chat, each part tagged with the chat it was asked in; empty for none.</summary>
        public string Earlier { get; } = earlier;

        /// <summary>The chat its answer goes to: the one it was asked in, or another once Raven moved the user with it (<see cref="FollowWork"/>).</summary>
        public RavenChat Chat { get; set; } = chat;

        /// <summary>The chat it was asked in, whose brain answers it, wherever the answer goes.</summary>
        public RavenChat AskedIn { get; } = chat;

        /// <summary>The note above it saying where it was asked, once it was moved (<see cref="FollowWork"/>); moves with it.</summary>
        public RavenLogEntry? Origin { get; set; }

        /// <summary>Its turn is in a start_chat call: the one a move of the user is for (<see cref="FollowWork"/>).</summary>
        public bool StartingChat { get; set; }

        /// <summary>Its entries in the log so far: the words asked, the replies and the cards; what moves with it.</summary>
        public List<RavenLogEntry> Entries { get; } = [];

        public bool Sent { get; set; }

        /// <summary>Said in Open mic: its answer heard opens the follow-up (#217); a typed one's does not.</summary>
        public bool OpenMic { get; init; }

        public bool Merged { get; set; }

        public bool Ended { get; set; }

        /// <summary>Said, not typed: answered aloud while muted (#242).</summary>
        public bool Aloud { get; init; }

        /// <summary>The news whats_new gave in its turn (#243): read once the answer is heard to its end, or written.</summary>
        public List<(RavenLogEntry Entry, IReadOnlyList<ChatNewsLine>? Lines)> ToRead { get; } = [];

        /// <summary>Raven was muted as it was asked: its answer, said aloud, was not "muted midway" (#243).</summary>
        public bool MutedAtStart { get; set; }

        /// <summary>Its answer began only written because the user talked in Open mic: they were not reading it (#243).</summary>
        public bool WrittenOverTalk { get; set; }

        /// <summary>The news facts that went with it; given once it is sent.</summary>
        public IReadOnlyList<ToldFact> Told { get; set; } = [];
    }

    /// <summary>The user takes the floor: whatever holds it stops, its speech too. Returns the new floor's token (UI thread).</summary>
    private CancellationToken TakeFloor()
    {
        DropCatchUp(); // a question stops the catch-up, also one not begun
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
        var whole = new StrongBox<bool>();
        try
        {
            await previous;
            if (question.Merged)
            {
                return; // a later question took it along: the floor is only taken by a question, which merges one not sent
            }

            var brain = BrainOf(question.Chat);
            // Chat 0 is told how busy each window is, from the Yard as it is now (#136): a window never talked about has no
            // summary. Read alongside the wait for summaries, not after it.
            var reading = IsOverview(brain) ? BusyWindowsAsync() : null;
            if (IsOverview(brain) && !_summaries.IsCompleted)
            {
                var waitedFrom = _time.GetTimestamp();
                await Task.WhenAny(_summaries, Task.Delay(SummaryWait, _time));
                if (_time.GetElapsedTime(waitedFrom) > TimeSpan.FromSeconds(1))
                {
                    reading = BusyWindowsAsync(); // waited long for a summary: the Yard as it is now, not as it was
                }
            }

            var busy = reading is null ? null : await reading;
            if (question.Merged)
            {
                return; // words said meanwhile took it along
            }

            asked.Value = _time.GetUtcNow();
            var before = Log.Count == 0 ? null : Log[^1];
            await StreamAnswerAsync(brain, WithToldNews(question, brain, busy), spoken, floor, question.Chat, question, whole: whole);
            whole.Value &= !floor.IsCancellationRequested;

            // Its answer ended on a question ("chat 3 or chat 5?"): the user's next words may answer it, even "chat three".
            // This turn's words only, the entries after the last one before it: a turn that only looked something up asked nothing.
            // And only while the user is still in that chat: one who moved on is not answering it.
            // Not once the user was moved to a chat of another brain with it (#180): that brain did not ask, and would not hear.
            _brainAsked = !floor.IsCancellationRequested && BrainOf(question.Chat) == BrainOf(question.AskedIn) && CurrentChat == question.Chat
                && Log.Skip(before is null ? 0 : Log.IndexOf(before) + 1).LastOrDefault(e => e.Kind == RavenLogKind.Raven && e.Chat == question.Chat) is { } said
                && said.Text.TrimEnd().EndsWith('?');
        }
        finally
        {
            if (question.Sent)
            {
                Summarize(question.Chat);
                if (question.AskedIn != question.Chat)
                {
                    Summarize(question.AskedIn); // what it was asked for went elsewhere (#180)
                }
            }

            question.Ended = true;
            _questions.Remove(question);
            spoken.Complete();
            if (question.ToRead.Count > 0 && whole.Value)
            {
                // What whats_new told counts as read once the answer is heard to its end, or once it is written (#243).
                _pendingReads = Task.WhenAll(_pendingReads, ReadOnceToldAsync(spoken, question, question.ToRead));
            }
            _asking--;
            if (question.OpenMic && !floor.IsCancellationRequested)
            {
                AwaitFollowUp(OpenFollowUpOnceHeardAsync(spoken)); // one cut off by the next question leaves the follow-up to that one's
            }
            UpdateState();
        }
    }

    /// <summary>
    /// The follow-up (#217) runs from when the answer was heard to its end, or at once when it was only written, muted or
    /// cut off: the voice says when.
    /// </summary>
    private async Task OpenFollowUpOnceHeardAsync(ReplyVoice.SpokenReply spoken)
    {
        await spoken.Played.ConfigureAwait(false);
        await FollowUpFromNowAsync().ConfigureAwait(false);
    }

    /// <summary>The follow-up runs from now, on the UI thread; completes once it does, so <see cref="PendingFollowUp"/> can wait for it.</summary>
    private Task FollowUpFromNowAsync()
    {
        var set = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
        {
            _followUpFrom = _time.GetUtcNow();
            set.SetResult();
        });
        return set.Task;
    }

    /// <summary>
    /// The brain's answer to <paramref name="text"/> into the log and the voice. Never faults: a failure is a warning,
    /// an interruption ends the reply with "(interrupted)". Returns whether any of the reply came.
    /// </summary>
    /// <param name="question">Marked sent once the brain has it; null for a digest.</param>
    /// <param name="quiet">The teller's: what it says about itself goes to the app's log, not the panel's (the fallback sentence covers a failure).</param>
    /// <param name="chat">Where the reply, its cards and what is said about it go.</param>
    /// <param name="whole">Set when the answer came to its end with no failure: a teller that failed midway said only part (#179).</param>
    private async Task<bool> StreamAnswerAsync(IConductorBrain brain, string text, ReplyVoice.SpokenReply spoken, CancellationToken floor,
        RavenChat chat, Question? question = null, bool quiet = false, StrongBox<bool>? whole = null)
    {
        var failedMidway = false;
        RavenLogEntry? reply = null;
        var said = false;
        var began = _time.GetUtcNow();
        // Where the question is now: another chat once Raven moved the user with it (FollowWork). Its entries are the
        // question's, so they move with it.
        RavenChat Where() => question?.Chat ?? chat;
        RavenLogEntry Track(RavenLogEntry entry)
        {
            question?.Entries.Add(entry);
            return entry;
        }

        // An allow was proposed in this turn: the rest of the brain's words go to the app's log only. After the app's
        // read-back, a brain steered by a chat's words could ask "Say yes." to something else, in speech or in writing.
        var proposed = false;
        bool Proposed() => proposed |= _asks?.Proposed is { } standing && standing.At >= began;
        try
        {
            var cards = new Dictionary<string, RavenLogEntry>(StringComparer.Ordinal);
            await foreach (var e in brain.AskAsync(text, floor))
            {
                switch (e)
                {
                    case BrainQuestionSent when question is not null:
                        question.Sent = true;
                        _toldChats[brain] = question.Chat; // only now does the brain know where the user is
                        foreach (var told in question.Told)
                        {
                            told.ToldTo.Add(brain); // the brain has it now
                        }

                        break;
                    case BrainText { Delta: var piece } when Proposed():
                        _logger.LogInformation("Raven's brain after proposing an allow, not shown: {Words}", piece);
                        if (reply is not null)
                        {
                            reply.Text = reply.Text.TrimEnd();
                            reply = null;
                        }

                        break;
                    case BrainText { Delta: var piece } when reply is null:
                        if (piece.TrimStart() is { Length: > 0 } start)
                        {
                            reply = Track(AddSaid(start, Where(), spoken));
                            said = true;
                            spoken.Add(start);
                        }

                        break;
                    case BrainText { Delta: var piece }:
                        reply!.Text += piece;
                        CountUnread(reply);
                        spoken.Add(piece);
                        break;
                    case BrainToolCall call:
                        if (question is not null)
                        {
                            question.StartingChat = call.Tool == "start_chat"; // the app moves the user while it runs (#180)
                        }

                        spoken.Add("\n"); // a sentence ends at the card, with or without its full stop
                        // The part of the reply before the card is done: "Let me check.\n\n" keeps no empty lines.
                        if (reply is not null)
                        {
                            reply.Text = reply.Text.TrimEnd();
                            reply = null;
                        }

                        var card = Track(AddEntry(RavenLogKind.Action, call.Tool, Where()));
                        card.Detail = ActionDetail(call.Input);
                        cards[call.Id] = card;
                        break;
                    case BrainToolResult { Failed: true, Id: var id } when cards.TryGetValue(id, out var failed):
                        failed.Failed = true;
                        if (question is not null)
                        {
                            question.StartingChat = false;
                        }

                        break;
                    case BrainToolResult when question is not null:
                        question.StartingChat = false;
                        break;
                    case BrainNotice or BrainFailed when quiet:
                        _logger.LogWarning("Raven's news teller: {What}", e);
                        failedMidway |= e is BrainFailed or BrainNotice { Warning: true };
                        break;
                    case BrainNotice notice:
                        Track(AddEntry(notice.Warning ? RavenLogKind.Warning : RavenLogKind.Note, notice.Text, Where()));
                        failedMidway |= notice.Warning; // what it was to tell may not have come whole (#243)
                        break;
                    case BrainChatMessage { Text: var message } when quiet:
                        _logger.LogInformation("Raven's news teller: {What}", message);
                        break;
                    case BrainChatMessage { Text: var message }:
                        // A chat's message folded into the answer (#197) is noted in its place: a sentence ends there, and the
                        // text after it is a reply of its own, below it.
                        spoken.Add("\n");
                        if (reply is not null)
                        {
                            reply.Text = reply.Text.TrimEnd();
                            reply = null;
                        }

                        Track(AddEntry(RavenLogKind.Note, message, Where()));
                        break;
                    case BrainFailed { Reason: var reason }:
                        Track(AddEntry(RavenLogKind.Warning, reason, Where()));
                        failedMidway = true; // what it was to tell may not have come (#243)
                        break;
                }
            }

            if (reply is not null)
            {
                reply.Text = reply.Text.TrimEnd();
            }

            if (whole is not null)
            {
                whole.Value = !failedMidway;
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
            Track(AddEntry(RavenLogKind.Warning, $"Raven could not answer: {ex.Message}", Where()));
        }

        return said;
    }

    /// <summary>
    /// Waits <see cref="TrafficWatcher.NewsGrace"/> for the floor to stay free, and the pause (#152) since Raven last spoke
    /// or made a sound, then tells the news. Called when news arrives and whenever the panel's state changes (UI thread):
    /// each call starts the wait again.
    /// </summary>
    private void ScheduleNews()
    {
        if ((_news is { HasNews: true } || _untold.Count > 0 || _catchUpDue is not null || _nextOfferDue) && FloorIsFree)
        {
            // The card asked for waits for no pause (#233); in Open mic for the grace, as the user may go on after a breath.
            var wait = !RequestedFirst ? Traffic.WaitBeforeTelling : MicMode == MicMode.OpenMic ? TrafficWatcher.NewsGrace : TimeSpan.Zero;
            _newsTimer.Change(wait, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>News waits that is told aloud in the chat the user is in: chat 0's for a window the list does not show; none in a muted chat.</summary>
    private bool HasNewsHere => !CurrentChat.IsMuted && _news is not null && _news.HasNewsFor(id => ChatOf(id) == CurrentChat);

    /// <summary>Warms the teller when news of the chat the user is in waits to be told (#139).</summary>
    private void WarmTellerForCurrentNews()
    {
        // Told where its window's chat is: chat 0 for a window the list does not show.
        if (SpeakNews && !IsMuted && _teller is not null && HasNewsHere)
        {
            _tellerWarm = true; // switched away before it is told, the teller is rested
            _teller.WarmUp(); // its start is hidden in the wait for the floor
        }
    }

    /// <summary>Decides when other chats may make a sound, and whether the selected chat's news waits for the cooldown.</summary>
    public TrafficWatcher Traffic { get; }

    /// <summary>See <see cref="TrafficWatcher.IsFree"/>.</summary>
    private bool FloorIsFree => TrafficWatcher.IsFree(Floor);

    private TrafficWatcher.Floor Floor => new(Capturing: _capturing, Holding: _heldInputs.Count > 0, Pending: _pending, Asking: _asking,
        Telling: _telling, Speaking: _speaking, OpenSpeech: _openSpeech, AwaitingYes: _asks?.Proposed is not null || _offerStands);

    /// <summary>The floor as a telling sees it: free but for the telling itself.</summary>
    private bool FloorIsFreeButTelling => TrafficWatcher.IsFree(Floor with { Telling = false });

    /// <summary>
    /// Another chat has news or a card: never spoken, it makes the short sound if the watcher lets it, and is marked in the
    /// list either way. Muted, it makes none.
    /// </summary>
    private void SoundForOtherChat(bool floorFree)
    {
        if (_chime is not null && !IsMuted && Traffic.TrySound(floorFree && CallListening() <= TimeSpan.Zero))
        {
            _logger.LogInformation("Raven chimes for another chat's news or card");
            _chime.Play();
        }
    }

    private void TellNewsIfFree()
    {
        if ((_news is not { HasNews: true } && _untold.Count == 0 && _catchUpDue is null && !_nextOfferDue) || !FloorIsFree)
        {
            return; // the next change of state schedules it again
        }

        if (CallListening() is var listening && listening > TimeSpan.Zero)
        {
            // Raven was called by its name alone and waits for the words: nothing of its own comes in between (#219).
            _newsTimer.Change(TimeSpan.FromMilliseconds(Math.Ceiling(listening.TotalMilliseconds)), Timeout.InfiniteTimeSpan);
            return;
        }

        if (!RequestedFirst && Traffic.PauseLeft is var left && left > TimeSpan.Zero)
        {
            // A chat's sound came meanwhile, or the timer, counting coarser than the clock, fired a little early: only what is
            // left of the pause is waited, in whole milliseconds (a timer due in less fires at once).
            _newsTimer.Change(TimeSpan.FromMilliseconds(Math.Ceiling(left.TotalMilliseconds)), Timeout.InfiniteTimeSpan);
            return;
        }

        var catchUp = _catchUpDue is { } due && CatchUpCovers(due.Away).Count > 0 ? _catchUpDue : null;
        if (_catchUpDue is not null && catchUp is null)
        {
            DropCatchUp(); // a digest heard since the switch said it all
            if (_news is not { HasNews: true } && _untold.Count == 0 && !_nextOfferDue)
            {
                return;
            }
        }

        // A question takes the floor from it, and a press stops it too.
        _digest?.Dispose();
        _digest = CancellationTokenSource.CreateLinkedTokenSource(_floor.Token);
        _telling = true;
        UpdateState();
        // The catch-up of the chat just switched to goes first, then its cards; a chat's question goes before the news: the
        // chat is stopped on it. The news follows once the floor is free again.
        if (catchUp is { } told)
        {
            _catchUpDue = null;
            _catchUpTelling = _digest;
            _conversation = TellCatchUpAsync(_conversation, told.Chat, told.Away, _digest.Token);
        }
        else
        {
            if (_untold.Count > 0)
            {
                _conversation = TellQuestionsAsync(_conversation, _digest.Token);
            }
            else if (_nextOfferDue)
            {
                _conversation = OfferNextAsync(_conversation, _digest.Token); // before the news: it answers what the user just did
            }
            else
            {
                _newsTelling = _digest; // muting the chat stops it (#153)
                _conversation = TellNewsAsync(_conversation, _news!, _digest.Token);
            }
        }
    }

    /// <summary>A chat asks something: its card goes in the log, to be read out when the floor is free.</summary>
    private void OnAsked(ChatAsk ask)
    {
        if (_asks?.IsHeld(ask.Id) == false)
        {
            return; // it ended before it got here (the Cab changed, say): its Closed found no card, and a card now would stay open
        }

        var card = new ChatAskCard(ask);
        var kind = ask.Kind == ChatAskKind.Permission ? RavenLogKind.Permission : RavenLogKind.Question;
        var entry = new RavenLogEntry(kind, "asks", _time.GetUtcNow()) { Ask = card };
        _askCards[ask.Id] = card;
        OpenQuestions = _askCards.Count;
        card.Naming = NameAsync(card);
        _ = PlaceAsync(entry, card);
    }

    /// <summary>
    /// A placed card is read out if it is in the chat the user is in, once the floor is free; a card of another chat is
    /// not saved up to be read later: it makes the short sound at most, and the brain that acts is told it now.
    /// </summary>
    private void Arrive(ChatAskCard card)
    {
        if (!card.IsOpen)
        {
            return;
        }

        // The brain knows it with the next question, so "deny both" covers a card not read out yet. It goes to the brain of
        // the card's own chat only (#137): another window's brain would take it for its own window's ("allow it" there).
        var elsewhere = card.ShownIn != CurrentChat;
        Tell(QuestionFact(card), card.ShownIn!);
        if (elsewhere)
        {
            SoundForOtherChat(FloorIsFree);
            return;
        }

        _untold.Add(card);
        if (PermissionLine.NeedsTeller(card) && SpeakNews && !IsMuted && _teller is not null)
        {
            _tellerWarm = true;
            _teller.WarmUp(); // its start is hidden in the wait for the floor
        }

        ScheduleNews();
    }

    /// <summary>
    /// The card goes in its window's chat, and only there: it is answered where that window's other cards are. Which
    /// window that is the naming finds. A chat on no tile is asked in its VS Code tab (#148): its question is not taken.
    /// One taken all the same (its window removed while it was named, or the Yard too slow to read) goes there too, with a
    /// note in the chat the user is in, so they know where it waits. Chat 0 never has a card. Never faults.
    /// </summary>
    private async Task PlaceAsync(RavenLogEntry entry, ChatAskCard card)
    {
        await card.Naming;
        _dispatcher.Post(() =>
        {
            if (ChatOf(card.WorkspaceId) == YardChat)
            {
                // Its end takes the card away (OnAskClosed); one ended already is gone.
                if (_asks?.IsHeld(card.Ask.Id) == true)
                {
                    _asks.ToVsCode(card.Ask.Id);
                    AddEntry(RavenLogKind.Note, $"{(card.Ask.Kind == ChatAskKind.Permission ? "A permission prompt" : "A question")} from "
                        + $"{(card.Workspace is null ? FolderOf(card.Ask) : card.Chat)} went to its VS Code tab: Raven found no window's chat for it.");
                }

                return;
            }

            card.ShownIn = Append(entry, ChatOf(card.WorkspaceId)).Chat;
            Summarize(card.ShownIn);
            Arrive(card);
        });
    }

    /// <summary>The chat an ask's card is in, the lines about it go beside it; the Yard's for one without a card.</summary>
    private RavenChat ChatOfAsk(ChatAsk ask) =>
        _askCards.TryGetValue(ask.Id, out var card) ? card.ShownIn ?? ChatOf(card.WorkspaceId) : YardChat;

    /// <summary>Names the chat as the Yard shows it, for the card and for what Raven says. Never faults.</summary>
    private async Task NameAsync(ChatAskCard card)
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
                card.Workspace = chat.Workspace;
                card.WorkspaceId = chat.WorkspaceId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not name the chat that asks");
        }
    }

    /// <summary>"a chat in RawCutX": the folder the chat that asks works in, for a note when the Yard does not name it.</summary>
    private static string FolderOf(ChatAsk ask) =>
        ask.Step.Cwd is { Length: > 0 } cwd ? $"a chat in {CodeSwitchX.Core.Workspaces.WorkspaceProbe.FolderName(cwd)}" : "a chat";

    /// <summary>A held question ended: its card shows how, and takes no more clicks.</summary>
    private void OnAskClosed(ChatAskClosed closed)
    {
        if (!_askCards.Remove(closed.Ask.Id, out var card))
        {
            return;
        }

        _untold.Remove(card);
        if (card == _requested)
        {
            _requested = null;
        }

        if (_tellerWarm && !_telling)
        {
            RestTellerIfIdle(); // warmed up for a long command that may not be read out now
        }

        // Not told yet to the brain that acts, it is not told at all: the chat waits for no answer here any more.
        var fact = QuestionFact(card);
        _toldNews.RemoveAll(t => t.Fact == fact);
        OpenQuestions = _askCards.Count;
        card.IsOpen = false;
        card.AwaitsYes = false; // an allow proposed for it ends with it, and no yes is asked for any more
        card.Outcome = OutcomeOf(closed);
        Summarize(card.ShownIn);
        if (closed.Outcome == ChatAskOutcome.Answered)
        {
            OfferNext(); // answered here: the next card is offered (#230)
        }
    }

    /// <summary>
    /// The brain proposed to allow a prompt: the app reads it back and asks for the yes itself, so the yes answers what
    /// the app said and nothing a brain steered by a chat's words chose to ask; the card says the user's yes decides.
    /// </summary>
    private void OnProposed(ChatAllowProposal proposal)
    {
        _askCards.TryGetValue(proposal.Ask.Id, out var card);
        if (card is not null)
        {
            card.AwaitsYes = true;
        }

        var line = PermissionReadBack.Of(proposal.Ask, card?.Workspace);
        var aloud = AloudInFlightFor(ChatOf(proposal.Window));
        if ((IsMuted && !aloud) || !TtsReady)
        {
            AddSaid(line, ChatOfAsk(proposal.Ask), said: false);
            _asks?.MarkHeard(proposal, _time.GetUtcNow()); // Raven only writes: the line shown is what the user reads
            _followUpFrom = _time.GetUtcNow(); // a bare yes may answer it (#217)
            return;
        }

        // Spoken whole, risks and all: only a yes said after it answers it. While the user talks in Open mic it is only
        // written, and is not heard.
        var spoken = _voice.Begin(silent: _openSpeech, whole: true, evenMuted: aloud);
        AddSaid(line, ChatOfAsk(proposal.Ask), spoken);
        spoken.Add(line);
        spoken.Complete();
        AwaitFollowUp(HeardAsync(proposal, spoken.Played));
    }

    /// <summary>
    /// Marks the proposal's read-back heard once it has played to its end. One not heard (hushed midway, dropped, or only
    /// written while the user talked) ends its proposal: it asked for a yes that cannot answer it, so the user is told.
    /// </summary>
    private async Task HeardAsync(ChatAllowProposal proposal, Task<bool> played)
    {
        var chat = ChatOfAsk(proposal.Ask);
        if (await played.ConfigureAwait(false))
        {
            _asks?.MarkHeard(proposal, _time.GetUtcNow());
            await FollowUpFromNowAsync().ConfigureAwait(false); // a bare yes may answer it (#217)
            return;
        }

        _dispatcher.Post(() =>
        {
            if (_asks?.IsHeard(proposal) == false && _asks.Cancel(proposal))
            {
                AddEntry(RavenLogKind.Note, NotHeardLine, chat);
            }
        });
    }

    /// <summary>The note when a read-back was not heard to its end.</summary>
    internal const string NotHeardLine = "That was not read out to its end, so a yes cannot allow it. Ask again, or click Allow.";

    /// <summary>
    /// A proposed allow ended. Nothing said for a yes (<see cref="ConfirmProposal"/> says it), other words (they are the
    /// next question, and the brain is told) or the prompt ending (its card says how); silence is noted, so the user
    /// knows nothing ran and the card still takes a click.
    /// </summary>
    private void OnProposalEnded(ChatAllowProposal proposal, ChatProposalEnd end)
    {
        _askCards.TryGetValue(proposal.Ask.Id, out var card);
        if (card is not null)
        {
            card.AwaitsYes = false;
        }

        if (end == ChatProposalEnd.Cancelled)
        {
            TellProposer(proposal, $"{WhoAsked(proposal.Ask)}: the allow you proposed was not confirmed by a yes, so nothing ran, and its card stays open");
        }

        var chat = ChatOfAsk(proposal.Ask);
        if (end == ChatProposalEnd.Expired && _asks?.IsHeard(proposal) == false)
        {
            AddEntry(RavenLogKind.Note, NotHeardLine, chat);
            TellProposer(proposal, $"{WhoAsked(proposal.Ask)}: the allow you proposed was never read out to the user, so nothing ran, and its card stays open");
        }
        else if (end == ChatProposalEnd.Expired)
        {
            AddEntry(RavenLogKind.Note, $"No yes within {ChatAsks.ProposalLifetime.TotalSeconds:0} seconds: nothing ran. The card stays open for a click.", chat);
            TellProposer(proposal, $"{WhoAsked(proposal.Ask)}: the allow you proposed got no yes within {ChatAsks.ProposalLifetime.TotalSeconds:0} seconds, so nothing ran, and its card stays open");
        }

        ScheduleNews(); // what was held while it stood
    }

    /// <summary>
    /// The user said yes to the proposed allow: the app allows the prompt, says so, and tells the brain with the next
    /// question. The yes goes to no brain. Takes the floor, as any words of the user do; a question that had not gone to
    /// the brain yet is asked again rather than lost with it.
    /// </summary>
    private void ConfirmProposal(ChatAllowProposal proposal, DateTimeOffset ended)
    {
        var who = WhoAsked(proposal.Ask); // before the confirm closes its card
        var chat = ChatOfAsk(proposal.Ask);
        var allowed = _asks!.Confirm(proposal);
        var said = allowed ? "Allowed. The chat carries on." : "The chat no longer waits for that: it was answered elsewhere, or its turn ended.";
        _toldNews.RemoveAll(t => t.Fact.StartsWith(who + ": the allow you proposed got no yes", StringComparison.Ordinal));
        TellProposer(proposal, $"{who}: the user said yes to the allow you proposed, and {(allowed ? "it was allowed" : "it was gone already")}");
        if (Unsent() is [.., var waiting])
        {
            // Their own turns end at once; their words go again, with the news of the yes.
            waiting.Merged = true;
            AskBrain(waiting.Text, ended, waiting.Chat, waiting.Earlier, entries: waiting.Entries, openMic: waiting.OpenMic, aloud: waiting.Aloud);
        }
        else
        {
            TakeFloor();
        }

        var spoken = _voice.Begin(silent: _openSpeech, evenMuted: _lastWordsAloud); // the yes said aloud, muted too (#242)
        AddSaid(said, chat, spoken);
        spoken.Add(said);
        spoken.Complete();
    }

    /// <summary>The brain that proposed the allow: the one of the chat its tool call came from; null for a window gone, whose brain went with it.</summary>
    private IConductorBrain? Proposer(ChatAllowProposal proposal) =>
        proposal.Window is { } window && Chats.All(c => c.WorkspaceId != window) ? null : BrainOf(ChatOf(proposal.Window));

    /// <summary>What became of a proposed allow, for the brain that proposed it, and no other.</summary>
    private void TellProposer(ChatAllowProposal proposal, string fact)
    {
        if (Proposer(proposal) is { } proposer)
        {
            _toldNews.Add(new(_time.GetUtcNow(), fact, Window: null, Proposer: proposer));
        }
    }

    /// <summary>The chat an ask is of, as the brain is told it: "ContentAutomatorX, chat "Fix" (chat id a)".</summary>
    private string WhoAsked(ChatAsk ask) =>
        $"{(_askCards.TryGetValue(ask.Id, out var card) ? card.Said : "A chat")} (chat id {ask.SessionId})";

    /// <summary>How a card says its ask ended: "Answered: Banana", "Allowed", "Left to VS Code: …".</summary>
    internal static string OutcomeOf(ChatAskClosed closed) => (closed.Ask.Kind, closed.Outcome) switch
    {
        (ChatAskKind.Permission, ChatAskOutcome.Answered) => closed.Permit switch
        {
            { Allow: true, Always: { } always } => $"Allowed, and kept as a rule: {always.Said}.",
            { Allow: true } => "Allowed.",
            { Message: { } message } when message != ChatAsks.DeniedMessage => $"Denied: {message}",
            _ => "Denied. The chat carries on without it.",
        },
        (ChatAskKind.Permission, ChatAskOutcome.ToVsCode) => "Left to VS Code: answer it in the chat's tab.",
        (ChatAskKind.Permission, ChatAskOutcome.TimedOut) => $"Not answered within {ChatNewsLine.Span(ChatAsks.Lifetime)}: answer it in the chat's tab.",
        (_, ChatAskOutcome.AnsweredInVsCode) => "Answered in VS Code.",
        (ChatAskKind.Permission, ChatAskOutcome.Gone) => "Answered in VS Code, or the chat's turn ended.",
        (_, ChatAskOutcome.Answered) => "Answered: " + string.Join("; ", closed.Answers ?? []),
        (_, ChatAskOutcome.ToVsCode) => "Left to VS Code: it asks there.",
        (_, ChatAskOutcome.TimedOut) => $"Not answered within {ChatNewsLine.Span(ChatAsks.Lifetime)}: VS Code asks it now.",
        (_, ChatAskOutcome.Stopped) => "The chat was stopped.",
        _ => "The chat stopped waiting for it.",
    };

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
    private void SendAnswers(ChatAskCard? card)
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

    /// <summary>The question goes to the chat's VS Code tab, which asks it there; a permission prompt is answered there.</summary>
    [RelayCommand]
    private void AnswerInVsCode(ChatAskCard? card)
    {
        if (card is { IsOpen: true })
        {
            _asks?.ToVsCode(card.Ask.Id);
        }
    }

    /// <summary>The chat may do what it asked; it carries on.</summary>
    [RelayCommand]
    private void Allow(ChatAskCard? card) => Permit(card, allow: true);

    /// <summary>The chat may not; it is told so and carries on without it.</summary>
    [RelayCommand]
    private void Deny(ChatAskCard? card) => Permit(card, allow: false);

    /// <summary>
    /// The chat may do what it asked, now and from now on: the rule Claude Code suggested goes back with the allow, and
    /// Claude Code writes it. A click only: no voice tool reaches this.
    /// </summary>
    [RelayCommand]
    private void AlwaysAllow(ChatSuggestionView? suggestion) => Permit(suggestion?.Card, allow: true, suggestion?.Suggestion);

    private void Permit(ChatAskCard? card, bool allow, ChatPermissionSuggestion? always = null)
    {
        if (card is not { IsOpen: true, Permission: not null } || _asks is null)
        {
            return;
        }

        if (!_asks.Permit(card.Ask.Id, allow, always: always) && card.IsOpen)
        {
            card.IsOpen = false;
            card.Outcome = "The chat no longer waits for it.";
        }
    }

    /// <summary>
    /// Reads out the oldest ask not read yet of the chat the user is in, one at a time: the next follows once the floor
    /// is free again. Who asks, what, and the options; for a permission prompt what the chat wants to do and what is risky
    /// in it (<see cref="PermissionLine"/>), a long command in the teller's words. The brain that acts was told it when it
    /// came (see <see cref="Arrive"/>). Asks of a chat the user left are not read. Muted, or with news not to be spoken,
    /// the card is only shown. Never faults.
    /// </summary>
    private async Task TellQuestionsAsync(Task previous, CancellationToken floor)
    {
        ReplyVoice.SpokenReply? spoken = null;
        var asked = false;
        var warmed = false;
        try
        {
            await previous;
            _untold.RemoveAll(c => !c.IsOpen);
            warmed = _tellerWarm;
            if (_untold.Count == 0)
            {
                _tellerWarm = false;
                return;
            }

            // The card "next question" asked for (#230) is read out with chat news only written too; muted, only when it was
            // asked for aloud (#242).
            var requested = _requested is not null && _untold.Contains(_requested);
            if (requested ? IsMuted && !_requestedAloud : IsMuted || !SpeakNews)
            {
                _untold.Clear(); // none is read out: they need not wait for a telling each
                _requested = null;
                _tellerWarm = false;
                return;
            }

            if (!SpeakNews || IsMuted)
            {
                _untold.RemoveAll(c => c != _requested);
            }

            var card = _untold[0];
            _untold.RemoveAt(0);
            var askedAloud = card == _requested && _requestedAloud;
            if (card == _requested)
            {
                _requested = null;
            }
            // Still warm for a long command's card after this one; a card that comes while this is told warms it up again.
            _tellerWarm = warmed && _untold.Any(PermissionLine.NeedsTeller);
            if (floor.IsCancellationRequested)
            {
                return;
            }

            _voice.Expect(evenMuted: askedAloud);
            spoken = _voice.Begin(evenMuted: askedAloud);
            string line;
            if (PermissionLine.NeedsTeller(card) && _teller is not null)
            {
                asked = true;
                line = await TellersLineAsync(card, floor) ?? PermissionLine.Said(card);
            }
            else
            {
                line = QuestionSentence([card]);
            }

            if (!floor.IsCancellationRequested && card.IsOpen)
            {
                spoken.Add(line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading out a chat's question failed");
        }
        finally
        {
            // Warmed up for a long command that was not read out, here or by a card that came meanwhile and is gone.
            if ((warmed && !asked) || _tellerWarm)
            {
                RestTellerIfIdle();
            }

            spoken?.Complete();
            _telling = false;
            UpdateState();
        }
    }

    /// <summary>
    /// Rests the teller unless something still waits for it: a long command's card not read out yet, or news not told.
    /// Resting it then would stop the process warmed up for that.
    /// </summary>
    private void RestTellerIfIdle()
    {
        // Only news told here keeps it: another window's is never spoken (#125).
        if (_untold.Any(PermissionLine.NeedsTeller) || HasNewsHere || _catchUpDue is not null)
        {
            return;
        }

        _tellerWarm = false;
        _teller?.Rest();
    }

    /// <summary>
    /// The teller's few words for a long command, with what is risky in it after them; null when it gives none, fails
    /// (what came before a failure would be a cut-off sentence), or the floor is taken. Never faults.
    /// </summary>
    private async Task<string?> TellersLineAsync(ChatAskCard card, CancellationToken floor)
    {
        var words = new System.Text.StringBuilder();
        try
        {
            await foreach (var e in _teller!.AskAsync(PermissionLine.TellerQuestion(card), floor))
            {
                switch (e)
                {
                    case BrainText { Delta: var piece }:
                        words.Append(piece);
                        break;
                    case BrainFailed:
                        _logger.LogWarning("Raven's teller, on a permission prompt: {What}", e);
                        return null;
                    case BrainNotice:
                        _logger.LogWarning("Raven's teller, on a permission prompt: {What}", e);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (floor.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Raven's teller failed on a permission prompt");
            return null;
        }

        return PermissionLine.WithTellersWords(card, words.ToString());
    }

    /// <summary>
    /// What Raven says of the asks: "CodeSwitchX, chat "Fix the upload" asks: Which fruit? Apple, Banana or Cherry.", or for a
    /// permission prompt what <see cref="PermissionLine.Said"/> words: "CodeSwitchX, chat "Fix the upload" wants to run npm test."
    /// </summary>
    internal static string QuestionSentence(IReadOnlyList<ChatAskCard> cards)
    {
        var text = new System.Text.StringBuilder();
        foreach (var card in cards)
        {
            if (card.Permission is not null)
            {
                text.Append(text.Length == 0 ? "" : " ").Append(PermissionLine.Said(card)).Append(' ');
                continue;
            }

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
    internal static string QuestionFact(ChatAskCard card) => card.Permission is not null
        ? $"{card.Said} (chat id {card.Ask.SessionId}) asks, and waits for the answer here: {card.Ask.Describe()} (ask id {card.Ask.Id}). "
            + "answer_permission denies it on the user's word, or proposes an allow that only the user's next yes, checked by the app, makes real"
        : $"{card.Said} (chat id {card.Ask.SessionId}) asks, and waits for the answer here: {card.Ask.Describe()}. answer_question answers it";

    /// <summary>
    /// The digest: one card that lists the news, and the teller wording it in a few words, as conversation. Muted, or
    /// with news not to be spoken, only the card is written. A teller that gives no words (or none at all) says the
    /// fallback sentence instead. Stopped, the rest is dropped: its chats count as told. Never faults.
    /// </summary>
    private async Task TellNewsAsync(Task previous, ChatNews news, CancellationToken floor)
    {
        ReplyVoice.SpokenReply? spoken = null;
        var asked = false;
        _newsChat = null; // the chat of the news before is done with: it is not stopped for this one
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
            var appended = new Dictionary<RavenChat, RavenLogEntry>();

            // A card per window, in that window's chat: the news of a chat is read where its window's other cards are.
            // The user sees the card, and maybe hears part of it before a press stops it: the brain that acts is told the
            // facts with the next question either way, so "open it" finds what "it" is; each line the brain of its own
            // window's chat only (#137).
            foreach (var group in lines.GroupBy(l => ChatOf(l.WorkspaceId)))
            {
                var ofWindow = group.ToList();
                var title = ofWindow.Count == 1 ? "Chat news" : $"Chat news · {ofWindow.Count}";
                appended[group.Key] = Append(new RavenLogEntry(RavenLogKind.News, title, _time.GetUtcNow()) { Lines = ofWindow }, group.Key);
                Summarize(group.Key);
                foreach (var line in ofWindow)
                {
                    Tell(Fact(line), group.Key, taken);
                }
            }
            var fresh = lines.Where(l => !l.Stale).ToList();
            var own = fresh.Where(l => ChatOf(l.WorkspaceId) == CurrentChat).ToList();
            // A muted chat's news is only written, here and as another chat's sound (#153).
            var others = fresh.Any(l => ChatOf(l.WorkspaceId) is var of && of != CurrentChat && !of.IsMuted);
            if (own.Count == 0 || CurrentChat.IsMuted || !SpeakNews || IsMuted || floor.IsCancellationRequested || !Traffic.MaySpeakOwnNews)
            {
                if (others)
                {
                    SoundForOtherChat(FloorIsFreeButTelling);
                }

                return;
            }

            _voice.Expect();
            var asking = default(DateTimeOffset);
            _newsChat = CurrentChat; // muted, it stops (#153)
            spoken = _newsReply = _voice.Begin(heard => _logger.LogInformation(
                "Raven's news: first word {Total:0} ms after it began ({Take:0} ms reading the board, {Teller:0} ms from the teller's question)",
                (heard - began).TotalMilliseconds, (taken - began).TotalMilliseconds, (heard - asking).TotalMilliseconds));
            asked = _teller is not null;
            asking = _time.GetUtcNow();
            // Spoken, the selected chat's news is the announcement: other chats' news is only marked.
            var chat = CurrentChat;
            var whole = new StrongBox<bool>();
            var said = _teller is not null && await StreamAnswerAsync(_teller, DigestPrompt(own), spoken, floor, chat, quiet: true, whole: whole);
            if (said && whole.Value && appended.TryGetValue(chat, out var told))
            {
                // Collapsed, the card of the chat talked to is told: once heard to its end its fresh lines no longer count,
                // nor are told again by a catch-up. Its stale lines are shown, never said: they still count. The fallback
                // sentence says only that news came, not what: it reads nothing.
                ReadOnceHeard(spoken, [(told, own)]);
            }

            if (!said && !floor.IsCancellationRequested)
            {
                var sentence = FallbackSentence(own);
                AddSaid(sentence, chat, spoken);
                spoken.Add(sentence);
            }

            // With no voice ready to say it (none picked, failed, installing or loading), the remark is only written: it is
            // no announcement, and the other chats still sound. One that is off starts and says it.
            if (others && _tts.Status.State is not (TextToSpeechState.Ready or TextToSpeechState.Off))
            {
                SoundForOtherChat(FloorIsFreeButTelling);
            }

        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telling the chat news failed");
        }
        finally
        {
            // Warmed up for news that came to nothing, or by a long command's card that came while the news was told.
            if (!asked || _tellerWarm)
            {
                RestTellerIfIdle();
            }

            _newsTelling = null; // its reply is kept: it may still be heard
            spoken?.Complete();
            _telling = false;
            UpdateState();
        }
    }

    /// <summary>What the teller is given for a digest: the news (how to tell it is its system prompt).</summary>
    internal static string DigestPrompt(IReadOnlyList<ChatNewsLine> lines)
    {
        return "News of the chats:\n" + string.Join("\n", DigestLines(lines));
    }

    /// <summary>One line a chat: "- Workspace, chat "Title": finished. It last said: "…"".</summary>
    internal static IEnumerable<string> DigestLines(IReadOnlyList<ChatNewsLine> lines)
    {
        // Chats that share a workspace and title are numbered, or the teller takes them for one ("Weather discussion" twice).
        var same = lines.GroupBy(l => (l.Workspace, l.Title)).Where(g => g.Count() > 1).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var line in lines)
        {
            var title = same.TryGetValue((line.Workspace, line.Title), out var twins)
                ? $"\"{line.Title}\" ({twins.IndexOf(line) + 1} of {twins.Count})"
                : $"\"{line.Title}\"";
            var text = new System.Text.StringBuilder($"- {line.Workspace}, chat {title}: {line.What}");
            if (line.Detail is { Length: > 0 } detail)
            {
                text.Append($": \"{detail}\"");
            }

            if (line.LastSaid is { Length: > 0 } lastSaid)
            {
                text.Append($". It last said: \"{lastSaid}\"");
            }

            yield return text.ToString();
        }
    }

    /// <summary>One line of news as the brain that acts is told it: the facts only, never what the chat said or asked.</summary>
    private static string Fact(ChatNewsLine line) => $"{line.Workspace}, chat \"{line.Title}\": {line.What}";

    /// <summary>
    /// The question as it goes to the brain that acts: after the chat news the user was given since its last question,
    /// so "open the one that needs me" works. The news older than <see cref="ToldNewsLifetime"/> is dropped; what goes
    /// along is kept until the brain has it, so a question merged into the next or failed before it went loses none.
    /// </summary>
    private string WithToldNews(Question question, IConductorBrain brain, IReadOnlyDictionary<Guid, Busy>? busy)
    {
        var now = _time.GetUtcNow();
        _toldNews.RemoveAll(t => now - t.At > ToldNewsLifetime);
        if (IsOverview(brain))
        {
            // Chat 0 is the overview: it is given each window's chat by its summary, and no fact of a card or a chat's news.
            question.Told = [];
            return Overview(busy) + question.Earlier + WhereTheUserIs(question.Chat, brain, always: question.Earlier.Length > 0) + question.Text;
        }

        // A window's facts go to its chat. Without a summarizer chat 0 is no overview: it is told every listed window's facts,
        // as before #124 (one brain for every chat is that case too). What became of an allow goes to its proposer alone.
        var window = question.Chat.WorkspaceId;
        var all = _summarizer is null && brain == _brain;
        question.Told = [.. _toldNews.Where(t => !t.ToldTo.Contains(brain) && (t.Proposer is { } proposer ? proposer == brain
            : t.Window == window || (all && (t.Window is null || Chats.Any(c => c.WorkspaceId == t.Window)))))];
        var text = question.Earlier + WhereTheUserIs(question.Chat, brain, always: question.Earlier.Length > 0) + question.Text;
        return question.Told.Count == 0
            ? text
            : "[Chat news the user was given since their last question: " + string.Join("; ", question.Told.Select(t => t.Fact)) + ".]\n" + text;
    }

    /// <summary>
    /// What chat 0 is told of the window chats with each question: each one's number, name, the cards waiting in it, and
    /// its summary. Never the chat's words or a card's text: those reach the summarizer only.
    /// </summary>
    /// <param name="busy">Each window's chats from the Yard; null when it could not be read: then said once, not per window.</param>
    private string Overview(IReadOnlyDictionary<Guid, Busy>? busy)
    {
        List<string> lines = [];
        List<string> quiet = [];
        foreach (var chat in Chats.Where(c => c.WorkspaceId is not null))
        {
            var now = busy is not null && busy.TryGetValue(chat.WorkspaceId!.Value, out var counted) ? counted : new Busy(0, []);
            var cards = WaitingIn(chat);
            // A chat that waits on a card here is said once, as the card: not also as waiting on the user.
            var carded = _askCards.Values.Where(c => c.IsOpen && c.ShownIn == chat).Select(c => c.Ask.SessionId).ToHashSet();
            var waiting = now.Waiting.Count(id => !carded.Contains(id));
            // Counts only, never a chat's title or words: chat 0 knows a window's chats by its summary and these numbers.
            List<string> parts = [];
            if (now.Working > 0)
            {
                parts.Add(now.Working == 1 ? "1 Claude Code chat working" : $"{now.Working} Claude Code chats working");
            }

            if (waiting > 0)
            {
                parts.Add(waiting == 1 ? "1 waiting on the user" : $"{waiting} waiting on the user");
            }

            if (cards > 0)
            {
                parts.Add(cards == 1 ? "1 card waiting" : $"{cards} cards waiting");
            }

            // A window never talked in and with nothing going on is folded into one line, by number and name, so many idle
            // windows keep it short; one talked in whose summary has not come yet is not idle: "no summary yet".
            if (parts.Count == 0 && chat.Summary is null && !Log.Any(e => e.Chat == chat && e.Kind is not (RavenLogKind.Note or RavenLogKind.Warning)))
            {
                quiet.Add($"{chat.Number} {chat.Name}");
                continue;
            }

            // What the Yard shows, apart from what the summary says: no chat working or waiting is not "nothing going on".
            var state = parts.Count > 0 ? string.Join(", ", parts)
                : busy is null ? "no card waiting" : "no Claude Code chat working or waiting, no card";
            lines.Add($"Chat {chat.Number}, {chat.Name} ({state}): " + (chat.Summary ?? "no summary yet"));
        }

        if (quiet.Count > 0)
        {
            lines.Add((busy is null ? "No summary or card yet in chat " : "Nothing going on in chat ") + string.Join(", chat ", quiet));
        }

        var unknown = busy is null ? " (the Yard could not be read just now: which Claude Code chats work or wait is unknown; list_chats can tell)" : "";
        return lines.Count == 0
            ? "[There are no window chats now: no window is on the Yard.]\n"
            : $"[The window chats now, as their summaries and the Yard say{unknown}: " + string.Join("; ", lines) + ".]\n";
    }

    /// <summary>How many of a window's Claude Code chats work, and how many wait on the user.</summary>
    /// <param name="Waiting">The ids of the chats that wait on the user.</param>
    internal readonly record struct Busy(int Working, IReadOnlyList<string> Waiting);

    /// <summary>How long chat 0's question waits for the Yard's chats before it goes without their counts.</summary>
    private static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(2);

    /// <summary>Each window's working and waiting chats, as the Yard shows them now; null when it cannot say. Never faults.</summary>
    private async Task<IReadOnlyDictionary<Guid, Busy>?> BusyWindowsAsync()
    {
        if (_yard is null)
        {
            return null;
        }

        try
        {
            using var wait = new CancellationTokenSource(BusyWait, _time);
            // The token bounds the wait itself too: a Yard that does not watch it must not hold chat 0's question.
            var chats = await _yard.ChatsAsync(wait.Token).WaitAsync(wait.Token);
            return chats.GroupBy(c => c.WorkspaceId).ToDictionary(g => g.Key, g => new Busy(
                g.Count(c => c.State is SessionState.Working or SessionState.Starting), [.. g.Where(c => c.NeedsYou).Select(c => c.Id)]));
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Chat 0 goes without the windows' chat counts");
            return null;
        }
    }

    /// <summary>Chat 0's brain, the overview, when there is one: with no summarizer chat 0 is told what every chat is.</summary>
    private bool IsOverview(IConductorBrain brain) => _summarizer is not null && brain == _brain;

    /// <summary>The cards that wait for an answer in the chat.</summary>
    private int WaitingIn(RavenChat chat) => _askCards.Values.Count(c => c.IsOpen && c.ShownIn == chat);

    /// <summary>
    /// Sums the window's chat up again once those asked before it are: after a turn in it, news in it, a card that came
    /// or went. Chat 0 and Activity are not summed up, nor a window's chat once its window is gone (UI thread).
    /// </summary>
    private void Summarize(RavenChat? chat)
    {
        if (_summarizer is null || chat?.WorkspaceId is null || !Chats.Contains(chat) || _toSummarize.Contains(chat))
        {
            return;
        }

        _toSummarize.Add(chat);
        if (_toSummarize.Count == 1 && _summaries.IsCompleted)
        {
            _summaries = SummarizeAllAsync();
        }
    }

    /// <summary>One chat after the other: the summarizer takes one question at a time anyway. Never faults.</summary>
    private async Task SummarizeAllAsync()
    {
        while (_toSummarize.Count > 0)
        {
            // Taken off the list as it begins: what happens in the chat while it runs sums it up again after.
            var chat = _toSummarize[0];
            _toSummarize.RemoveAt(0);
            if (Chats.Contains(chat))
            {
                await SummarizeAsync(chat);
            }
        }
    }

    /// <summary>The chat's new summary; one that fails or says nothing keeps the one before. Out of sight: nothing goes in the panel. Never faults.</summary>
    private async Task SummarizeAsync(RavenChat chat)
    {
        var words = new System.Text.StringBuilder();
        try
        {
            await foreach (var e in _summarizer!.AskAsync(SummaryPrompt(chat), CancellationToken.None))
            {
                switch (e)
                {
                    case BrainText { Delta: var piece }:
                        words.Append(piece);
                        break;
                    case BrainFailed or BrainNotice:
                        _logger.LogWarning("Raven's chat summarizer, on chat {Chat}: {What}", chat.Number, e);
                        if (e is BrainFailed)
                        {
                            return;
                        }

                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Raven's chat summarizer failed on chat {Chat}", chat.Number);
            return;
        }

        // One line, and no bracket: chat 0 is told the summaries inside one, which the summarizer, steered by what a chat
        // wrote, must not be able to close.
        var summary = string.Join(' ', words.ToString().Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())).Trim()
            .Replace('[', '(').Replace(']', ')');
        if (summary.Length > 0)
        {
            chat.Summary = summary.Length <= SummaryLength ? summary : summary[..(SummaryLength - 1)].TrimEnd() + "…";
        }
    }

    /// <summary>What the summarizer is given: the summary so far, the chat's latest entries, and how many cards wait in it.</summary>
    private string SummaryPrompt(RavenChat chat)
    {
        var text = new System.Text.StringBuilder($"Chat {chat.Number}, {chat.Name}.\n");
        text.Append("The summary so far: ").Append(chat.Summary ?? "none yet").Append('\n');
        text.Append("The latest in the chat, oldest first:");
        // Notes and warnings about Raven itself are left out before the latest are taken: they would crowd out the conversation.
        foreach (var line in Log.Where(e => e.Chat == chat).Select(SummaryLine).OfType<string>().Where(l => l.Length > 0).TakeLast(SummaryEntries))
        {
            text.Append("\n- ").Append(line.Length <= SummaryEntryLength ? line : line[..SummaryEntryLength] + "…");
        }

        return text.Append($"\nCards waiting on the user now: {WaitingIn(chat)}").ToString();
    }

    /// <summary>An entry as the summarizer reads it; null for a note or warning about Raven itself.</summary>
    private static string? SummaryLine(RavenLogEntry entry) => entry.Kind switch
    {
        RavenLogKind.You => "The user: " + entry.Text,
        RavenLogKind.Raven => "Raven: " + entry.Text,
        // Not "Looked at": the summarizer must know a chat was stopped or closed, not only looked at.
        RavenLogKind.Action => $"Raven called {entry.Text}" + (entry.Detail is { } detail ? $" ({detail})" : "") + (entry.Failed ? ", which failed" : ""),
        RavenLogKind.News => "News: " + string.Join("; ", entry.Lines?.Select(Fact) ?? []),
        RavenLogKind.Question or RavenLogKind.Permission when entry.Ask is { } card =>
            entry.ActivityLine + (card.IsOpen ? " (waiting on the user)" : $" ({card.Outcome})"),
        _ => null,
    };

    /// <summary>The chat of the last question each brain took (<see cref="BrainQuestionSent"/>); the Yard's until one of another went in.</summary>
    private readonly Dictionary<IConductorBrain, RavenChat> _toldChats = [];

    /// <summary>
    /// Which chat the user asks in: a window's chat each time, so "stop it" and "open it" mean that window, also in words
    /// taken along from another chat; the Yard's once the user is back in it, and not before: a brain that only ever
    /// heard the Yard's questions is told nothing.
    /// </summary>
    /// <param name="always">Words of another chat go before: the Yard is named too, or the words after them would seem to be of that chat.</param>
    private string WhereTheUserIs(RavenChat chat, IConductorBrain brain, bool always = false)
    {
        var told = _toldChats.GetValueOrDefault(brain);
        if (chat.WorkspaceId is not null)
        {
            return $"[The user is in chat {chat.Number}, {chat.Name}: \"it\" and \"this\" mean that window unless they name another.]\n";
        }

        return always || told is { WorkspaceId: not null } ? "[The user is in chat 0, the Yard: no window in particular.]\n" : "";
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
        RefreshAnswering();
        if (_capturing)
        {
            State = RavenState.Listening;
            Caption = "Listening…";
            return;
        }

        if (_openSpeech || _openHeard && !_speaking && _asking == 0)
        {
            State = RavenState.Listening;
            Caption = "Listening…";
            return;
        }

        if (_speaking)
        {
            State = RavenState.Speaking;
            // Talking interrupts in push to talk (a press), and Raven's name in an Open mic that listens with barge-in on
            // (#217). Otherwise (Open mic paused or starting, or ignoring speech while Raven talks; a press there only
            // pauses) a typed question is what interrupts.
            Caption = MicMode == MicMode.PushToTalk ? "Speaking… Talk to interrupt."
                : _openRun is not null && !_attendPaused && BargeIn ? "Speaking… Say \"Raven\" to interrupt."
                : "Speaking… Type to interrupt.";
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
            // Scheduled before the state says the floor is free: whoever sees it free finds the news's wait already begun.
            ScheduleNews();
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

            return;
        }

        State = RavenState.Transcribing;
        Caption = _downloading ? "Downloading the speech model…"
            : _pending > 1 ? $"Transcribing… ({_pending - 1} waiting)"
            : "Transcribing…";
    }

    /// <summary>
    /// An entry in <paramref name="chat"/>, or in the chat the user is in (<see cref="CurrentChat"/>): what the panel
    /// says about itself (the mic, the voice) is said where the user looks. The oldest entries go first: the log of a
    /// panel left open for days of dictation would grow for good.
    /// </summary>
    private RavenLogEntry AddEntry(RavenLogKind kind, string text, RavenChat? chat = null) =>
        Append(new RavenLogEntry(kind, text, _time.GetUtcNow()), chat);

    /// <summary>Raven's line in <paramref name="chat"/>, and whether it is said aloud as well as written.</summary>
    private RavenLogEntry AddSaid(string text, RavenChat chat, bool said) =>
        Append(new RavenLogEntry(RavenLogKind.Raven, text, _time.GetUtcNow()) { Said = said, SaidMuted = said && IsMuted }, chat);

    /// <summary>
    /// Once <paramref name="spoken"/> is heard to its end, the lines it said of each entry no longer count; a reply cut off
    /// leaves every mark (#179).
    /// </summary>
    private void ReadOnceHeard(ReplyVoice.SpokenReply spoken, IReadOnlyList<(RavenLogEntry Entry, IReadOnlyList<ChatNewsLine>? Lines)> said)
    {
        if (said.Count > 0)
        {
            _pendingReads = Task.WhenAll(_pendingReads, ReadOnceHeardAsync(spoken, said));
        }
    }

    /// <summary>The reads waiting for their reply to be heard: a catch-up is worded once they are done (UI thread).</summary>
    private Task _pendingReads = Task.CompletedTask;

    private async Task ReadOnceHeardAsync(ReplyVoice.SpokenReply spoken, IReadOnlyList<(RavenLogEntry Entry, IReadOnlyList<ChatNewsLine>? Lines)> said)
    {
        if (await spoken.HeardWholeAsync().ConfigureAwait(false))
        {
            _dispatcher.Post(() => MarkRead(said));
        }
    }

    /// <summary>
    /// The news an answer told (#243) is read once that answer is heard to its end, or once it was written for the user to
    /// read: only written (muted and typed), muted midway, cut at its sentence limit (the rest is written), or with no voice
    /// ready. One the user cut off by talking, or that was written while they talked in Open mic, is not read.
    /// </summary>
    private async Task ReadOnceToldAsync(ReplyVoice.SpokenReply spoken, Question question,
        IReadOnlyList<(RavenLogEntry Entry, IReadOnlyList<ChatNewsLine>? Lines)> told)
    {
        var heard = await spoken.HeardWholeAsync().ConfigureAwait(false);
        _dispatcher.Post(() =>
        {
            var written = spoken.IsSilent ? !question.WrittenOverTalk : spoken.IsCut || !TtsReady || (IsMuted && !question.MutedAtStart);
            if (heard || written)
            {
                MarkRead(told);
            }
        });
    }

    /// <summary>The lines given are read: heard, or told in an answer only written (UI thread).</summary>
    private void MarkRead(IReadOnlyList<(RavenLogEntry Entry, IReadOnlyList<ChatNewsLine>? Lines)> said)
    {
        // Only a chat where a failure was heard: one shown while it came (in Activity) was never counted, nor said.
        var failedHeard = new HashSet<RavenChat>();
        foreach (var (entry, lines) in said)
        {
            if (Read(entry, lines))
            {
                failedHeard.Add(entry.Chat);
            }
        }

        foreach (var chat in failedHeard)
        {
            MarkFailures(chat);
        }
    }

    /// <summary>
    /// Each reply written as said and not seen, with its latest line (UI thread): should the reply turn out not heard to
    /// its end, that line counts. One count a reply, however many lines its cards split it into: its earlier lines were
    /// said before it.
    /// </summary>
    private readonly Dictionary<ReplyVoice.SpokenReply, RavenLogEntry> _saidUnseen = [];

    /// <summary>
    /// Raven's line in <paramref name="chat"/>, said in <paramref name="spoken"/> unless that is silent or no voice speaks.
    /// Should the reply turn out not heard to its end (hushed, dropped by a voice that could not speak, muted midway, or cut
    /// at its sentence limit), the line was only read after all, and counts like one only written; unless it was seen
    /// meanwhile, in the open panel.
    /// </summary>
    private RavenLogEntry AddSaid(string text, RavenChat chat, ReplyVoice.SpokenReply spoken)
    {
        // Muted, only a reply begun even muted, and not hushed since (muting midway hushes it), says its lines (#242).
        var entry = AddSaid(text, chat, !spoken.IsSilent && TtsReady && (!IsMuted || (spoken.EvenMuted && !spoken.IsStopped)));
        if (!entry.Said)
        {
            _saidUnseen.Remove(spoken); // the voice stopped between its lines: this one counts for the reply, once
        }
        else if (!(IsOpen && IsShown(entry)))
        {
            var first = !_saidUnseen.ContainsKey(spoken);
            _saidUnseen[spoken] = entry; // before the check: a reply hushed already settles it at once, on this thread
            if (first)
            {
                _pendingCounts = Task.WhenAll(_pendingCounts, CountUnlessHeardAsync(spoken));
            }
        }

        return entry;
    }

    private async Task CountUnlessHeardAsync(ReplyVoice.SpokenReply spoken)
    {
        var heard = await spoken.HeardWholeAsync().ConfigureAwait(false);
        _dispatcher.Post(() =>
        {
            // Not when seen since (the panel opened on its chat), nor when the log has let it go: nothing would clear it.
            if (_saidUnseen.Remove(spoken, out var entry) && !heard && Log.Contains(entry))
            {
                entry.Said = false;
                CountUnread(entry);
            }
        });
    }

    private RavenLogEntry Append(RavenLogEntry entry, RavenChat? chat = null)
    {
        entry.Chat = chat ?? CurrentChat;
        TrimLog(1);
        Log.Add(entry);
        if (IsShown(entry))
        {
            Shown.Add(entry);
        }

        Mark(entry);
        return entry;
    }

    /// <summary>
    /// The list's marks for a new entry: a card waits in its chat, and blinks there until the chat is opened, as a chat
    /// that failed is marked until then; Raven's answers and warnings, news lines and cards are counted when they come
    /// where the user does not see them. Activity shows every line, so none that comes there is unread, but it opens no
    /// chat: its cards have no buttons. The user's own words and the panel's notes count for nothing.
    /// </summary>
    private void Mark(RavenLogEntry entry)
    {
        var chat = entry.Chat;
        var elsewhere = !IsOpen || chat != SelectedChat; // collapsed, the user sees no chat
        if (entry.Ask is { IsOpen: true } card)
        {
            card.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatAskCard.IsOpen))
                {
                    CountWaiting(entry.Chat);
                }
            };
            chat.IsWaiting = true;
            chat.IsWaitingUnseen |= elsewhere;
        }

        if (elsewhere && entry.Lines?.Any(l => l.Kind == ChatNewsKind.Failed) == true)
        {
            chat.HasFailed = true;
        }

        CountUnread(entry);
    }

    /// <summary>
    /// Counts an entry the user does not see, once: a news card by its lines, Raven's answer, a warning (the question
    /// went unanswered) and a card as one. Called again as a reply grows: one whose beginning the user saw before
    /// leaving its chat counts for the rest.
    /// </summary>
    private void CountUnread(RavenLogEntry entry)
    {
        if (entry.IsUnread || (IsOpen && IsShown(entry)) || HeardCollapsed(entry))
        {
            return;
        }

        var lines = UnreadLines(entry);
        entry.Unread = lines;
        entry.Chat.Unread += lines;
        if (lines > 0)
        {
            PulseNews(entry.Chat);
        }
    }

    /// <summary>
    /// Collapsed, Raven's answers in the chat the user talks to are heard as they are spoken: not news to them. One only
    /// written is (#178).
    /// </summary>
    /// <remarks>Muted since the entry began, the rest is only written; begun muted and said, it answered words said aloud (#242).</remarks>
    private bool HeardCollapsed(RavenLogEntry entry) => !IsOpen && entry.Said && (VoiceSpeaks || (entry.SaidMuted && TtsReady)) && entry.Chat == CurrentChat;

    /// <summary>
    /// What Raven says on its own is heard (news, the catch-up, the offer): not muted, and a voice ready. Otherwise it is
    /// only written: one that is off (asleep) starts loading on the first sentence it is given, and that reply is dropped
    /// as "still loading" (#178).
    /// </summary>
    private bool VoiceSpeaks => !IsMuted && TtsReady;

    /// <summary>The voice can speak, muted or not.</summary>
    private bool TtsReady => _tts.Status.State is TextToSpeechState.Ready;

    /// <summary>
    /// The user's last words were said, through a press or Open mic, not typed nor a key (#242): while muted, Raven answers
    /// aloud only what was asked aloud, and stays quiet on its own (UI thread).
    /// </summary>
    private bool _lastWordsAloud;

    /// <summary>
    /// The question the brain is answering now was said aloud (#242): what its turn brings (an allow's read-back, the card
    /// next_question goes to) is said while muted too. With no answer on its way, a turn the brain began itself: muted,
    /// it is Raven's own, and quiet.
    /// </summary>
    private bool AloudInFlight => _questions.LastOrDefault(q => q.Sent && !q.Ended && !q.Merged)?.Aloud ?? false;

    /// <summary>As <see cref="AloudInFlight"/>, for the question <paramref name="chat"/>'s brain answers: another brain's turn is not it.</summary>
    private bool AloudInFlightFor(RavenChat chat) =>
        _questions.LastOrDefault(q => q.Sent && !q.Ended && !q.Merged && BrainOf(q.Chat) == BrainOf(chat))?.Aloud ?? false;

    /// <summary>
    /// Collapsed, what was said aloud of an entry counted unread and heard to the end no longer counts: the news lines
    /// given, or all of it (a warning) for none. Only those: a stale news line is shown, never said, and still counts, and
    /// a line heard before is not taken off twice (#179). Returns whether a failure was heard: then the chat's red mark is
    /// for the caller to look at again.
    /// </summary>
    private static bool Read(RavenLogEntry entry, IReadOnlyList<ChatNewsLine>? lines)
    {
        int heard;
        if (lines is null)
        {
            entry.MarkHeard(); // seen or not, it is not told again
            heard = entry.Unread;
        }
        else
        {
            heard = entry.MarkHeard(lines);
        }

        if (!entry.IsUnread)
        {
            return false; // its chat was opened meanwhile and seen whole, or the log let it go
        }

        var chat = entry.Chat;
        var read = Math.Min(heard, entry.Unread);
        entry.Unread -= read;
        chat.Unread -= read;
        chat.IsNewsPulsing &= chat.Unread > 0;
        return lines?.Any(l => l.Kind == ChatNewsKind.Failed) == true;
    }

    /// <summary>A failure said aloud is heard too: the chat's red mark stays only for a failed line still unread.</summary>
    private void MarkFailures(RavenChat chat) =>
        chat.HasFailed = Log.Any(e => e.Chat == chat && e.IsUnread && e.Lines?.Any(l => l.Kind == ChatNewsKind.Failed && !e.WasHeard(l)) == true);

    /// <summary>How long a badge pulses when a line comes (#173): three beats, then it stays steady.</summary>
    internal static readonly TimeSpan NewsPulse = TimeSpan.FromSeconds(2.4);

    /// <summary>Each chat's timer for the end of its badge's pulse, and which pulse that is (UI thread).</summary>
    private readonly Dictionary<RavenChat, (ITimer Timer, int Pulse)> _pulseEnds = [];

    /// <summary>How many chats have a pulse timer: a removed chat's goes with it (#179).</summary>
    internal int PulseTimers => _pulseEnds.Count;

    /// <summary>
    /// Starts the chat's badge pulsing, from the first beat again if it already pulses. Each pulse has a timer of its own
    /// for its end, which knows which pulse it ends: an end posted just before a new line restarted the pulse is ignored.
    /// </summary>
    private void PulseNews(RavenChat chat)
    {
        if (!Chats.Contains(chat))
        {
            return; // removed with its window: its entries stay in Activity, its badge is gone
        }

        chat.IsNewsPulsing = false; // a trigger starts its animation on the change to true only
        chat.IsNewsPulsing = true;
        var pulse = 0;
        if (_pulseEnds.TryGetValue(chat, out var last))
        {
            pulse = last.Pulse + 1;
            last.Timer.Dispose();
        }

        var timer = _time.CreateTimer(_ => _dispatcher.Post(() =>
        {
            if (_pulseEnds.TryGetValue(chat, out var now) && now.Pulse == pulse)
            {
                chat.IsNewsPulsing = false;
            }
        }), null, NewsPulse, Timeout.InfiniteTimeSpan);
        _pulseEnds[chat] = (timer, pulse);
    }

    private static int UnreadLines(RavenLogEntry entry) => entry.Kind switch
    {
        RavenLogKind.News => entry.Lines?.Count ?? 0,
        RavenLogKind.Raven or RavenLogKind.Warning or RavenLogKind.Question or RavenLogKind.Permission => 1,
        _ => 0,
    };

    /// <summary>Whether a card in the chat still waits; with none, nothing blinks.</summary>
    private void CountWaiting(RavenChat chat)
    {
        chat.IsWaiting = WaitingIn(chat) > 0; // the cards themselves: the log drops old entries, a card still waits
        chat.IsWaitingUnseen &= chat.IsWaiting;
    }

    /// <summary>The user opened the chat: what came there is seen; a card still waiting keeps its outline, no longer blinking.</summary>
    private void Seen(RavenChat chat)
    {
        foreach (var entry in Log.Where(e => e.IsUnread && e.Chat == chat))
        {
            entry.Unread = 0; // a reply still growing counts anew for what comes after the user leaves again
        }

        foreach (var reply in _saidUnseen.Where(p => p.Value.Chat == chat).Select(p => p.Key).ToList())
        {
            _saidUnseen.Remove(reply); // read now, heard or not
        }

        chat.Unread = 0;
        chat.IsNewsPulsing = false;
        chat.HasFailed = false;
        chat.IsWaitingUnseen = false;
    }

    /// <summary>
    /// What a note about the panel turned into (a download failed, the mic is live again) takes the note's place. The note
    /// is said where the user was; the outcome is said where the user is: a note in another chat gives way to one added
    /// here, as does one already dropped from the log.
    /// </summary>
    private void ReplaceEntry(RavenLogEntry old, RavenLogKind kind, string text)
    {
        var index = Log.IndexOf(old);
        if (index < 0 || old.Chat != CurrentChat)
        {
            if (index >= 0)
            {
                Log.RemoveAt(index);
                Shown.Remove(old);
            }

            AddEntry(kind, text);
            return;
        }

        var entry = new RavenLogEntry(kind, text, old.At) { Chat = old.Chat };
        Log[index] = entry;
        if (Shown.IndexOf(old) is var shown and >= 0)
        {
            Shown[shown] = entry;
        }
    }

    /// <summary>The selected chat shows its own entries; Activity shows all of them.</summary>
    private bool IsShown(RavenLogEntry entry) => SelectedChat.IsActivity || entry.Chat == SelectedChat;

    /// <summary>The chat the user is in: the selected one, or the Yard's while Activity, which takes no words, is selected.</summary>
    public RavenChat CurrentChat => SelectedChat.IsActivity ? YardChat : SelectedChat;

    /// <summary>Chat 0: what belongs to no single window, and to a window the Yard does not show (any more).</summary>
    public RavenChat YardChat { get; } = RavenChat.Yard();

    public RavenChat ActivityChat { get; } = RavenChat.Activity();

    /// <summary>The chat list: the Yard, one chat per workspace by its number, Activity last (UI thread).</summary>
    public ObservableCollection<RavenChat> Chats { get; }

    /// <summary>The entries of the selected chat, in the order they came; all of them in Activity (UI thread).</summary>
    public ObservableCollection<RavenLogEntry> Shown { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentChat))]
    [NotifyPropertyChangedFor(nameof(TypePrompt))]
    private RavenChat _selectedChat;

    /// <summary>
    /// The list folds to its numbers: the Cab shows a VS Code, which keeps its width, or Settings is open on a narrow
    /// window and its page needs the room (#162).
    /// </summary>
    [ObservableProperty]
    private bool _isListFolded;

    /// <summary>The type box's hint: where the words go.</summary>
    public string TypePrompt => CurrentChat == YardChat ? "Type to the Yard…" : $"Type to {CurrentChat.Name}…";

    partial void OnSelectedChatChanged(RavenChat value)
    {
        if (value is null)
        {
            SelectedChat = YardChat; // the list lets go of a chat it no longer shows
            return;
        }

        _brainAsked = false; // moved to another chat: the user moved on from what Raven asked
        DropOffer(); // and from "Next?": a yes now is for something in the chat shown

        // Moved away from the chat whose allow waits for a yes (by hotkey, click or the brain): a yes said now is for
        // something in the chat shown, not for that prompt.
        if (_asks?.Proposed is { } standing && !value.IsActivity && value != ChatOfAsk(standing.Ask))
        {
            _asks.Cancel(standing);
        }

        StopCatchUp(); // switched on: what was away in the chat left is not said any more

        // What came while the user was away, before Seen forgets which lines those were.
        List<RavenLogEntry> away = CatchUp && !value.IsActivity ? [.. Log.Where(e => e.IsUnread && e.Chat == value)] : [];
        // Collapsed, nothing shows the chat (#173): its marks stay on the strip, until Raven opens or the catch-up is said.
        if (!value.IsActivity && IsOpen)
        {
            Seen(value);
        }

        // The cards of the chat left are not read out: kept, they would only hold up the news of the chat the user is in now.
        if (_untold.RemoveAll(c => c.ShownIn != CurrentChat) > 0 && _tellerWarm && !_telling)
        {
            RestTellerIfIdle(); // warmed up for a long command among them
        }

        if (_requested is not null && !_untold.Contains(_requested))
        {
            _requested = null; // the card "next question" asked for is not read out here
        }

        ShowSelected();
        CatchUpOn(value, away);
        WarmTellerForCurrentNews(); // its news, waiting, is told here now
        if (_tellerWarm && !_telling)
        {
            RestTellerIfIdle(); // warmed for the chat left: nothing waits for it here
        }
    }

    /// <summary>
    /// Speaks a short catch-up on switching to a chat (#127): what came there while the user was away, in one or two
    /// sentences worded by the teller. The shell keeps it in step with Settings, where it is on by default (#143). It is a
    /// switch of its own: with chat news only written, it is still said.
    /// </summary>
    [ObservableProperty]
    private bool _catchUp;

    partial void OnCatchUpChanged(bool value)
    {
        if (!value)
        {
            StopCatchUp();
            _untold.RemoveAll(_catchUpCards.Contains); // read out only because of it
        }
    }

    /// <summary>The cards the last catch-up put up to be read: they came while the user was away (UI thread).</summary>
    private readonly List<ChatAskCard> _catchUpCards = [];

    /// <summary>The catch-up waiting for the floor: the chat switched to and what to word it from (UI thread).</summary>
    private (RavenChat Chat, List<RavenLogEntry> Away)? _catchUpDue;

    /// <summary>The telling of the catch-up being said, and its reply; null when none is (UI thread).</summary>
    private CancellationTokenSource? _catchUpTelling;

    private ReplyVoice.SpokenReply? _catchUpReply;

    /// <summary>
    /// The catch-up of the chat switched to, from its unread lines only: the news and warnings that came while the user
    /// was elsewhere (Raven's answers there were heard as they came). It waits for the floor like news does, so it never
    /// talks over the user or an allow waiting for their yes; its cards that came meanwhile are read after it, one at a
    /// time. Nothing is said for a chat with nothing new, or muted. The cooldown does not hold it (#143): the user switched,
    /// most often right after the chat's sound, and asked for it so; it waits the pause after that sound (#152) instead.
    /// That holds with <see cref="TrafficWatcher.OwnNewsWaits"/> on too: dropped, it would never be said, as the switch has
    /// marked its lines seen.
    /// </summary>
    private void CatchUpOn(RavenChat chat, List<RavenLogEntry> away)
    {
        if (away.Count == 0)
        {
            return;
        }

        _catchUpCards.Clear();
        foreach (var card in away.Select(e => e.Ask).OfType<ChatAskCard>().Where(c => c.IsOpen && c.ShownIn == chat && !_untold.Contains(c)))
        {
            _untold.Add(card);
            _catchUpCards.Add(card);
        }

        var catchUp = CatchUpCovers(away).Count > 0 && !IsMuted && !chat.IsMuted && _teller is not null; // a muted chat's cards are still read
        if (catchUp)
        {
            _catchUpDue = (chat, away); // worded when it is said: a digest heard meanwhile has told some of it
        }

        if (_teller is not null && !IsMuted && (catchUp || (SpeakNews && _untold.Any(PermissionLine.NeedsTeller))))
        {
            _tellerWarm = true;
            _teller.WarmUp(); // its start is hidden in the wait for the floor
        }

        ScheduleNews();
    }

    /// <summary>The catch-up waiting, not begun, is dropped; the teller warmed for it rests unless something else waits for it.</summary>
    private void DropCatchUp()
    {
        if (_catchUpDue is null)
        {
            return;
        }

        _catchUpDue = null;
        if (_tellerWarm && !_telling)
        {
            RestTellerIfIdle();
        }
    }

    /// <summary>The catch-up waiting is dropped, and the one being said stops, its words too.</summary>
    private void StopCatchUp()
    {
        DropCatchUp();
        _catchUpTelling?.Cancel();
        if (_catchUpReply is { Played.IsCompleted: false })
        {
            _voice.Hush(); // only the catch-up speaks now: it holds the floor
        }
    }

    /// <summary>
    /// The lines a catch-up is worded from, the news in the order it came, then the warnings. Raven's answers were spoken as they
    /// came, wherever the user was, and the cards are read out on their own.
    /// </summary>
    internal static List<string> CatchUpLines(IReadOnlyList<RavenLogEntry> away) => CatchUpLines(CatchUpCovers(away));

    /// <summary>The lines worded from what the catch-up covers, so that what is read is what was said.</summary>
    private static List<string> CatchUpLines(IReadOnlyList<(RavenLogEntry Entry, IReadOnlyList<ChatNewsLine>? Lines)> covered) =>
    [
        // The news of all its cards at once, so two chats of one title in two cards are numbered apart.
        .. DigestLines([.. covered.SelectMany(c => c.Lines ?? [])]),
        .. covered.Where(c => c.Lines is null).Select(c => $"- A warning: {c.Entry.Text}"),
    ];

    /// <summary>
    /// What a catch-up says of each entry: a news card's lines still to tell, a warning not told yet. Raven's answers only
    /// written, the cards (read out on their own) and stale lines (shown, never said, as in the news) it leaves to be read
    /// (#179).
    /// </summary>
    private static List<(RavenLogEntry Entry, IReadOnlyList<ChatNewsLine>? Lines)> CatchUpCovers(IReadOnlyList<RavenLogEntry> away) =>
    [
        .. away.Where(e => e.Kind == RavenLogKind.News)
            .Select(e => (e, (IReadOnlyList<ChatNewsLine>?)ToTell(e).ToList()))
            .Where(c => c.Item2!.Count > 0),
        .. away.Where(e => e.Kind == RavenLogKind.Warning && !e.IsHeard).Select(e => (e, (IReadOnlyList<ChatNewsLine>?)null)),
    ];

    /// <summary>A news card's lines still to be told: fresh, and not heard already (in a digest, or a catch-up before).</summary>
    private static IEnumerable<ChatNewsLine> ToTell(RavenLogEntry card) => (card.Lines ?? []).Where(l => !l.Stale && !card.WasHeard(l));

    /// <summary>
    /// The catch-up due, worded from what is still untold of what came while the user was away; null when nothing is left
    /// (a digest heard meanwhile said it all). Seen is not told: opened, the switch itself has seen the chat.
    /// </summary>
    private static (List<string> Lines, List<(RavenLogEntry Entry, IReadOnlyList<ChatNewsLine>? Lines)> Covered)? CatchUpLeft(
        IReadOnlyList<RavenLogEntry> away)
    {
        var covered = CatchUpCovers(away);
        return covered.Count == 0 ? null : (CatchUpLines(covered), covered);
    }

    /// <summary>What the teller is given for a catch-up: the lines, and how to begin.</summary>
    internal static string CatchUpPrompt(IReadOnlyList<string> lines) =>
        "Catch-up: what happened in the chat the user just switched to, while they were away, in the order it came:\n"
        + string.Join("\n", lines)
        + "\nTell it in one or two short sentences that begin with \"While you were away\", the most pressing first.";

    /// <summary>
    /// Says the catch-up in the chat switched to, once the floor is free; a press, a question or another switch stops it.
    /// Never faults.
    /// </summary>
    private async Task TellCatchUpAsync(Task previous, RavenChat chat, IReadOnlyList<RavenLogEntry> away, CancellationToken stop)
    {
        ReplyVoice.SpokenReply? spoken = null;
        try
        {
            await previous;
            // Worded now, once what was heard before (a digest, a catch-up hushed at its last sentence) is noted: none of it
            // is told again.
            await _pendingReads;
            if (stop.IsCancellationRequested || CatchUpLeft(away) is not var (lines, covered))
            {
                return;
            }

            _voice.Expect();
            spoken = _catchUpReply = _voice.Begin(); // Raven's own: muted, the voice says none of it (#242)
            var whole = new StrongBox<bool>();
            var said = await StreamAnswerAsync(_teller!, CatchUpPrompt(lines), spoken, stop, chat, quiet: true, whole: whole);
            if (said && whole.Value)
            {
                // Collapsed, the catch-up is how the user learns what came (#173): heard to its end, what it said no longer
                // counts. A dropped or cut one leaves the marks, as does the fallback, which says only how many things
                // came; the cards blink until they are read.
                ReadOnceHeard(spoken, covered);
            }

            if (!said && !stop.IsCancellationRequested)
            {
                // The teller failed or said nothing: the user still hears that something came, and reads it in the chat.
                var sentence = lines.Count == 1 ? "While you were away, one thing came in here." : $"While you were away, {lines.Count} things came in here.";
                AddSaid(sentence, chat, spoken);
                spoken.Add(sentence);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Raven's catch-up failed");
        }
        finally
        {
            spoken?.Complete();
            _catchUpTelling = null;
            _telling = false;
            RestTellerIfIdle();
            UpdateState(); // then the chat's cards, one at a time, once it is quiet
        }
    }

    /// <summary>Drops the oldest entries until <paramref name="room"/> more fit under <see cref="MaximumLogEntries"/>.</summary>
    private void TrimLog(int room = 0)
    {
        while (Log.Count + room > MaximumLogEntries)
        {
            var dropped = Log[0];
            Shown.Remove(dropped);
            Log.RemoveAt(0);
            if (dropped.Ask is { IsOpen: true })
            {
                CountWaiting(dropped.Chat);
            }

            if (dropped.IsUnread)
            {
                dropped.Chat.Unread -= dropped.Unread; // opening the chat would not show it any more
                dropped.Unread = 0;
            }
        }
    }

    /// <summary>Fills <see cref="Shown"/> anew with the selected chat's entries.</summary>
    private void ShowSelected()
    {
        Shown.Clear();
        foreach (var entry in Log.Where(IsShown))
        {
            Shown.Add(entry);
        }
    }

    /// <summary>
    /// The workspaces the Yard shows, by number: each gets its chat, a renamed one keeps it, a removed one's chat leaves the
    /// list. Its entries stay in Activity, and the Yard's chat is selected if it was. A card still open of a removed window
    /// goes to its chat's VS Code tab (#135): chat 0 answers no card. (A window's number is given once and never changes.)
    /// UI thread.
    /// </summary>
    public void SetWorkspaces(IEnumerable<(Guid Id, int Number, string Name)> workspaces)
    {
        var wanted = workspaces.Where(w => w.Number > 0).OrderBy(w => w.Number).ToList();
        foreach (var gone in Chats.Where(c => c.WorkspaceId is { } id && wanted.All(w => w.Id != id)).ToList())
        {
            // From the cards themselves, not the log: an entry the log let go of still has its card waiting.
            foreach (var open in _askCards.Values.Where(c => c.IsOpen && c.ShownIn == gone).ToList())
            {
                _asks?.ToVsCode(open.Ask.Id);
            }

            gone.PropertyChanged -= OnChatPropertyChanged; // kept by the log's entries, it is muted no more
            gone.Tile = null; // and follows no tile, and works at nothing
            gone.IsAnswering = false;
            if (_pulseEnds.Remove(gone, out var pulse))
            {
                pulse.Timer.Dispose();
            }
            if (gone.WorkspaceId is { } retired)
            {
                _brains?.Retire(retired); // its process, config and conversation go with it
                if (_mutedWindows.Remove(retired))
                {
                    MutedWindowsChanged?.Invoke(this, EventArgs.Empty); // its mute is not kept for a window that is gone
                }
            }

            if (SelectedChat == gone)
            {
                SelectedChat = YardChat;
            }

            Chats.Remove(gone);
            _carried.Remove(gone);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var (id, number, name) = wanted[i];
            var chat = Chats.FirstOrDefault(c => c.WorkspaceId == id);
            if (chat is null)
            {
                chat = RavenChat.Of(id, number, name);
                chat.IsMuted = _mutedWindows.Contains(id);
                chat.PropertyChanged += OnChatPropertyChanged;
                Chats.Insert(i + 1, chat);
            }

            chat.Name = name;
        }
    }

    /// <summary>The windows whose Raven chat is muted (#153): kept for a window not listed yet, and stored by the shell. UI thread.</summary>
    private readonly HashSet<Guid> _mutedWindows = [];

    /// <summary>The windows whose Raven chat is muted now (#153), as a copy.</summary>
    public IReadOnlyList<Guid> MutedWindows => [.. _mutedWindows];

    /// <summary>A window's chat was muted or unmuted by the user (UI thread): the shell stores it.</summary>
    public event EventHandler? MutedWindowsChanged;

    /// <summary>The windows muted when the app last ran (#153): their chats, listed now or later, are muted. UI thread.</summary>
    public void SetMutedWindows(IEnumerable<Guid> windows)
    {
        _mutedWindows.Clear();
        _mutedWindows.UnionWith(windows);
        foreach (var chat in Chats)
        {
            chat.IsMuted = chat.WorkspaceId is { } id && _mutedWindows.Contains(id);
        }
    }

    /// <summary>
    /// Mutes or unmutes the window chat with the number (#153), as its row's speaker does; null when no window's chat has it
    /// (chat 0 has no mute of its own). UI thread.
    /// </summary>
    public RavenChat? MuteChat(int number, bool muted)
    {
        if (ChatNumbered(number) is not { CanMute: true } chat)
        {
            return null;
        }

        chat.IsMuted = muted;
        return chat;
    }

    /// <summary>What Raven says of a chat muted or unmuted: "Chat 3, ContentAutomatorX, is muted: …".</summary>
    /// <param name="ravenMuted">Raven itself is muted: an unmuted chat speaks only once it is not.</param>
    internal static string MuteLine(RavenChat chat, bool ravenMuted = false) => chat.IsMuted
        ? $"Chat {chat.Number}, {chat.Name}, is muted: its news and its catch-up are only written, with no sound; its questions are still read out."
        : $"Chat {chat.Number}, {chat.Name}, speaks again{(ravenMuted ? " once Raven itself is unmuted" : "")}.";

    private void OnChatPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RavenChat.IsMuted) && sender is RavenChat chat)
        {
            OnChatMuteChanged(chat);
        }
    }

    /// <summary>
    /// A chat's speaker was clicked, or its mute set by voice: remembered. Muted while the user is in it, what it is saying
    /// of its news or catch-up stops; unmuted, the teller warms up for its news waiting.
    /// </summary>
    private void OnChatMuteChanged(RavenChat chat)
    {
        if (chat.WorkspaceId is not { } id || !(chat.IsMuted ? _mutedWindows.Add(id) : _mutedWindows.Remove(id)))
        {
            return; // as remembered already (set from what was stored)
        }

        if (chat.IsMuted && chat == _newsChat)
        {
            StopNews(); // wherever the user is now
        }

        if (chat == CurrentChat && chat.IsMuted)
        {
            StopCatchUp();
            if (_tellerWarm && !_telling)
            {
                RestTellerIfIdle(); // warmed for its news, which is only written now
            }
        }
        else if (chat == CurrentChat)
        {
            WarmTellerForCurrentNews();
        }

        MutedWindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The telling of the news while one runs; null when none does (UI thread).</summary>
    private CancellationTokenSource? _newsTelling;

    /// <summary>The news last spoken and the chat it was told in: it may still be heard after its telling ended (UI thread).</summary>
    private ReplyVoice.SpokenReply? _newsReply;

    private RavenChat? _newsChat;

    /// <summary>The news being told stops, its words too: the chat it is said in was muted (#153).</summary>
    private void StopNews()
    {
        _newsTelling?.Cancel();
        if (_newsReply is { Played.IsCompleted: false })
        {
            _voice.Hush();
        }
    }

    /// <summary>How many cards wait in the workspace's Raven chat (#135).</summary>
    public int OpenCardsOf(Guid workspaceId) => Chats.FirstOrDefault(c => c.WorkspaceId == workspaceId) is { } chat ? WaitingIn(chat) : 0;

    /// <summary>The Yard's tiles, by number: as <see cref="SetWorkspaces(IEnumerable{ValueTuple{Guid, int, string}})"/>, and each chat keeps its tile.</summary>
    public void SetWorkspaces(IEnumerable<CodeSwitchX.UI.Yard.WorkspaceTileViewModel> tiles)
    {
        var all = tiles.ToList();
        SetWorkspaces(all.Select(t => (t.Id, t.Number, t.Name)));
        foreach (var tile in all)
        {
            if (Chats.FirstOrDefault(c => c.WorkspaceId == tile.Id) is { } chat)
            {
                chat.Tile = tile;
            }
        }
    }

    /// <summary>
    /// What a brain said in a turn of its own, started by a chat's message to it (#181): written in its Raven chat, never
    /// said aloud, as it comes unasked; the chat's badge counts it like any line the user has not seen. A tool it called
    /// gets its card, as in an answer: what a chat's message made Raven do is never done unseen. A turn that failed ends
    /// with a warning, as an answer that fails does.
    /// </summary>
    private void ShowUnasked(UnaskedTurn turn)
    {
        var chat = ChatOf(turn.WorkspaceId);
        foreach (var (call, failed) in turn.Calls)
        {
            var card = AddEntry(RavenLogKind.Action, call.Tool, chat);
            card.Detail = ActionDetail(call.Input);
            card.Failed = failed;
        }

        if (turn.Text.Length > 0)
        {
            AddSaid(turn.Text, chat, said: false);
        }

        if (turn.Failure is { } failure)
        {
            AddEntry(RavenLogKind.Warning, $"Raven could not answer a message from another chat: {failure}.", chat);
        }
    }

    /// <summary>A workspace's chat, the Yard's for none and for one the list does not show.</summary>
    private RavenChat ChatOf(Guid? workspaceId) =>
        workspaceId is { } id ? Chats.FirstOrDefault(c => c.WorkspaceId == id) ?? YardChat : YardChat;

    /// <summary>
    /// "Chat three", "zu Chat drei", "activity", "open chat three": the app switches the chat itself, at once and without
    /// a brain turn (<see cref="SpokenChatSwitch"/>), and says where the user is now. "Next question" goes to the oldest open
    /// card (<see cref="GoToNextQuestion"/>, #230). Navigation, not a question: nothing
    /// is written to a chat, and the brain's answer still on its way goes on in the chat it was asked in. A switch to
    /// another window's chat ends an allow waiting for a yes, as one by hotkey or click does (see
    /// <see cref="OnSelectedChatChanged"/>): a yes said there must not allow another chat's prompt.
    /// Returns whether the words were a switch.
    /// </summary>
    private bool SwitchBySaying(string text)
    {
        if (!_brainAsked && SpokenNextQuestion.Is(text))
        {
            GoToNextQuestion(_lastWordsAloud);
            return true;
        }

        if (_brainAsked || !SpokenChatSwitch.TryRead(text, out var target))
        {
            return false; // after Raven asked something, "chat three" may be the answer: the brain hears it, and can still switch
        }

        var chat = SwitchChat(target);
        var line = chat is null ? $"There is no chat {target.Number}." : SwitchLine(chat);
        if (chat is null)
        {
            AddEntry(RavenLogKind.Note, line);
        }

        _voice.Hush(); // the user moved on, as a press of the mic stops Raven
        SayNow(line, _lastWordsAloud);
        return true;
    }

    /// <summary>
    /// Says a line of navigation at once, as it answers the user's words: muted, only words said aloud (<paramref name="aloud"/>,
    /// #242); never while the user talks in Open mic (then it is only written).
    /// </summary>
    private void SayNow(string line, bool aloud)
    {
        if ((!IsMuted || aloud) && !_openSpeech)
        {
            var spoken = _voice.Begin(evenMuted: aloud);
            spoken.Add(line);
            spoken.Complete();
        }
    }

    /// <summary>
    /// The last spoken switch: the clip it was said in, the last clip queued when it was heard, and the chats it went
    /// from and to. The clips between were said after it, while the panel still showed a chat before it: the one it was
    /// said in (From), or the one an earlier switch, heard meanwhile, had gone to (Via). UI thread.
    /// </summary>
    private (long At, long Through, RavenChat From, RavenChat Via, RavenChat To)? _spokenSwitch;

    /// <summary>The brain's last answer asked the user something, and nothing was said since (UI thread).</summary>
    private bool _brainAsked;

    /// <summary>
    /// What Raven says on a switch: "Chat 3, ContentAutomatorX.", "Chat 0, the Yard.", "Activity is shown.". None of them
    /// is a switch itself: heard back through speakers in Open mic, it must not switch again.
    /// </summary>
    internal static string SwitchLine(RavenChat chat) =>
        chat.IsActivity ? "Activity is shown." : chat.WorkspaceId is null ? "Chat 0, the Yard." : $"Chat {chat.Number}, {chat.Name}.";

    /// <summary>The chat with that number: 0 is the Yard's; null for a number no window has.</summary>
    public RavenChat? ChatNumbered(int number) =>
        number == 0 ? YardChat : Chats.FirstOrDefault(c => c.WorkspaceId is not null && c.Number == number);

    /// <summary>
    /// Shows the chat the switch names, and asks the shell to show its window in the Cab when it says open; null when
    /// no chat has that number. By voice, by the brain's switch_chat and by the chat hotkeys (UI thread).
    /// </summary>
    public RavenChat? SwitchChat(ChatSwitch target)
    {
        var chat = target.Activity ? ActivityChat : target.Number is { } number ? ChatNumbered(number) : null;
        if (chat is null)
        {
            return null;
        }

        SelectedChat = chat;
        if (target.Open && chat.WorkspaceId is { } workspace)
        {
            CabRequested?.Invoke(this, workspace);
        }

        return chat;
    }

    /// <summary>The chat <paramref name="step"/> places down the list (up for a negative step), round the end; Activity is skipped.</summary>
    public RavenChat StepChat(int step)
    {
        var chats = Chats.Where(c => !c.IsActivity).ToList();
        // Activity is last in the list: the next chat after it is the first, the one before it the last.
        var at = SelectedChat.IsActivity ? (step > 0 ? -1 : chats.Count) : chats.IndexOf(SelectedChat);
        var next = chats[((at + step) % chats.Count + chats.Count) % chats.Count];
        SelectedChat = next;
        return next;
    }

    /// <summary>What Raven says when "next question" finds none open.</summary>
    internal const string NoQuestionsLine = "No questions are waiting.";

    /// <summary>The open cards of the window chats the list shows, oldest first (#230).</summary>
    private List<ChatAskCard> OpenCards() =>
        [.. _askCards.Values.Where(c => c.IsOpen && c.ShownIn is { } chat && Chats.Contains(chat)).OrderBy(c => c.Ask.At)];

    /// <summary>
    /// "Next question", said, typed, by its hotkey or as a yes to "Next?" (#230): the oldest open card in any window. Raven
    /// switches to that window's chat, taking the floor as a switch by voice does, and reads the card out as soon as the
    /// floor is free: at once, or once the user stops talking in Open mic; a long command in the teller's words (#233). With
    /// none open it says so. The window is not shown in the Cab: there its VS Code would ask the questions itself (UI thread).
    /// </summary>
    /// <param name="aloud">Asked for aloud: muted, the card is read out too (#242); by a key or typed, only shown.</param>
    public void GoToNextQuestion(bool aloud)
    {
        _voice.Hush(); // the user moved on, as a press of the mic stops Raven
        if (_asks?.Proposed is { } standing)
        {
            // An allow waiting for its yes ends, as with any words of the user's: a yes now must not allow it, and the card
            // asked for would wait behind it (the brain is told, OnProposalEnded).
            _asks.Cancel(standing);
        }

        if (ShowNextQuestion() is not { } card)
        {
            AddEntry(RavenLogKind.Note, NoQuestionsLine);
            SayNow(NoQuestionsLine, aloud);
            return;
        }

        Request(card, aloud); // read once the floor is free, with no pause first: the user waits for it
    }

    /// <summary>The card the user asked for goes first to be read out, with chat news only written too (#230).</summary>
    /// <param name="aloud">It was asked for aloud: read out while muted too (#242).</param>
    private void Request(ChatAskCard card, bool aloud)
    {
        _requested = card;
        _requestedAloud = aloud;
        _untold.Insert(0, card);
        if (PermissionLine.NeedsTeller(card) && (!IsMuted || aloud) && _teller is not null)
        {
            _tellerWarm = true;
            _teller.WarmUp();
        }

        ScheduleNews();
    }

    /// <summary>The card <see cref="_requested"/> was asked for aloud: read out while muted too (#242).</summary>
    private bool _requestedAloud;

    /// <summary>The card the user asked for is the next to be read out: what Raven says on its own waits, it does not (#233).</summary>
    private bool RequestedFirst => _requested is { } card && _untold.Count > 0 && _untold[0] == card;

    /// <summary>"Next question" by its hotkey: a key, not words said aloud, so while muted the card is only shown (#242).</summary>
    public void GoToNextQuestionByKey() => GoToNextQuestion(aloud: false);

    /// <summary>
    /// "Next question" asked of the brain (next_question, #230): as <see cref="GoToNextQuestion"/>, but the card is read out
    /// once the brain's answer is over, as a card that comes is. What the brain says of it; null when no card is open.
    /// </summary>
    public string? NextQuestionForBrain()
    {
        if (ShowNextQuestion() is not { } card)
        {
            return null;
        }

        if (IsMuted && !AloudInFlight)
        {
            return $"{SwitchLine(card.ShownIn!)} Its card is shown there.";
        }

        Request(card, AloudInFlight);
        return $"{SwitchLine(card.ShownIn!)} Its question is read out next.";
    }

    /// <summary>
    /// The news whats_new gives of a chat's entries (#243): a news card's lines not heard yet, stale ones too (shown, and
    /// unread, though never said on its own), and a warning not heard.
    /// </summary>
    private static List<(RavenLogEntry Entry, IReadOnlyList<ChatNewsLine>? Lines)> UnreadNews(IReadOnlyList<RavenLogEntry> entries) =>
    [
        .. entries.Where(e => e.Kind == RavenLogKind.News)
            .Select(e => (e, (IReadOnlyList<ChatNewsLine>?)(e.Lines ?? []).Where(l => !e.WasHeard(l)).ToList()))
            .Where(c => c.Item2!.Count > 0),
        .. entries.Where(e => e.Kind == RavenLogKind.Warning && !e.IsHeard).Select(e => (e, (IReadOnlyList<ChatNewsLine>?)null)),
    ];

    /// <summary>What whats_new says when nothing is new and nothing waits (#243).</summary>
    internal const string NothingNewLine = "Nothing new: no news the user has not read, and nothing waits for them.";

    /// <summary>
    /// "What's new?" asked of the brain (whats_new, #243): the news lines not read yet in every window's chat, muted ones too,
    /// the chat of the window it was asked from first; only chat <paramref name="number"/>'s when given. Then what waits for
    /// the user. Facts only, never what a chat said: the brain acts. The lines count as read once the answer that tells them
    /// is heard to its end, or at once when it is only written. UI thread.
    /// </summary>
    public string WhatsNewForBrain(Guid? askedFrom, int? number)
    {
        var from = ChatOf(askedFrom);
        var chats = Chats.Where(c => !c.IsActivity && (number is null || c.Number == number))
            .OrderBy(c => c == from ? 0 : 1).ThenBy(c => c.Number).ToList();
        if (number is { } asked && chats.Count == 0)
        {
            return $"There is no chat {asked}.";
        }

        var unread = Log.Where(e => e.IsUnread).ToLookup(e => e.Chat);
        var told = new List<(RavenLogEntry Entry, IReadOnlyList<ChatNewsLine>? Lines)>();
        var text = new System.Text.StringBuilder();
        foreach (var chat in chats)
        {
            var news = UnreadNews([.. unread[chat]]);
            if (news.Count == 0)
            {
                continue;
            }

            told.AddRange(news);
            text.Append($"New in chat {chat.Number}, {NameOf(chat)}{(chat == from ? " (the chat the user is in)" : "")}:\n");
            foreach (var (entry, lines) in news)
            {
                // A warning's text may carry another session's words: only that there is one (#243).
                foreach (var line in lines?.Select(Fact) ?? ["a warning, in the panel"])
                {
                    text.Append("- ").Append(line).Append('\n');
                }
            }
        }

        var waiting = OpenCards().Where(c => number is null || c.ShownIn!.Number == number).ToList();
        if (waiting.Count > 0)
        {
            text.Append("Waiting for the user (\"next question\" goes through them, oldest first):\n");
            foreach (var card in waiting)
            {
                // Its question and options are the chat's own words: only what kind of card it is (#243).
                text.Append($"- chat {card.ShownIn!.Number}: {card.Said} {(card.Permission is not null ? "asks for a permission" : "asks a question")}\n");
            }
        }

        if (text.Length == 0)
        {
            return number is { } only ? $"Nothing new in chat {only}: no news there the user has not read, and nothing waits there." : NothingNewLine;
        }

        // Counted read with the answer of the brain that asked: told in another brain's turn, it would be read unheard.
        // The brain that answers a question is the one of the chat it was asked in, also once it moved with the user (#180).
        if (told.Count > 0 && _questions.LastOrDefault(q => q.Sent && !q.Ended && !q.Merged && BrainOf(q.AskedIn) == BrainOf(from)) is { } question)
        {
            question.ToRead.AddRange(told);
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Shows the chat of the oldest open card, without its catch-up: the question is what the user asked for, and the
    /// chat's other cards are read after it. The card is taken off the ones to read out; null when none is open.
    /// </summary>
    private ChatAskCard? ShowNextQuestion()
    {
        DropOffer();
        _requested = null;
        if (OpenCards() is not [var card, ..])
        {
            return null;
        }

        SelectedChat = card.ShownIn!;
        DropCatchUp();
        _untold.Remove(card);
        return card;
    }

    /// <summary>"2 more questions are waiting. Next?": what Raven offers after an answer (#230).</summary>
    internal static string NextOfferLine(int open) =>
        open == 1 ? "One more question is waiting. Next?" : $"{open} more questions are waiting. Next?";

    /// <summary>
    /// A card was answered here, by voice or a click (#230): the next one is offered once the floor is free, unless a card of
    /// the chat the user is in is read out anyway.
    /// </summary>
    private void OfferNext()
    {
        DropOffer(); // one that stands counted the card just answered
        if (_untold.Any(c => c.IsOpen) || OpenCards().Count == 0)
        {
            return;
        }

        _nextOfferDue = true;
        ScheduleNews();
    }

    /// <summary>
    /// Says the offer of the next card, counted when it is said; a yes heard after it goes there (see <see cref="Ask"/>).
    /// Muted, with no voice, while the user talks in Open mic, with chat news only written or in a muted chat, it is only
    /// written, and a yes still takes it. Never faults.
    /// </summary>
    private async Task OfferNextAsync(Task previous, CancellationToken floor)
    {
        try
        {
            await previous;
            if (!_nextOfferDue)
            {
                return; // the user moved on meanwhile
            }

            _nextOfferDue = false;
            var open = OpenCards().Count;
            if (open == 0 || _untold.Count > 0 || floor.IsCancellationRequested)
            {
                return;
            }

            var line = NextOfferLine(open);
            var number = _offerNumber;
            // Raven speaking up on its own (#233): with chat news only written, or in a muted chat, so is the offer.
            var quiet = IsMuted || !SpeakNews || CurrentChat.IsMuted;
            if (quiet || !VoiceSpeaks || _openSpeech)
            {
                AddSaid(line, CurrentChat, said: false);
                Offered(number, bareYes: !quiet);
                return;
            }

            var spoken = _voice.Begin();
            AddSaid(line, CurrentChat, spoken);
            spoken.Add(line);
            spoken.Complete();
            _ = OfferHeardAsync(spoken.Played, number);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Offering the next question failed");
        }
        finally
        {
            _telling = false;
            UpdateState();
        }
    }

    /// <summary>The offer counts once it was heard to its end: one hushed midway asked nothing.</summary>
    private async Task OfferHeardAsync(Task<bool> played, int number)
    {
        if (await played.ConfigureAwait(false))
        {
            _dispatcher.Post(() => Offered(number, bareYes: true));
        }
    }

    /// <summary>
    /// The offer was heard, or written: a yes takes it for <see cref="NextOfferLifetime"/>, unless it is over already. One
    /// Raven kept quiet (#233) does not hold the floor meanwhile: nobody heard it, and other chats' sounds go on.
    /// </summary>
    private void Offered(int number, bool bareYes)
    {
        if (number != _offerNumber)
        {
            return;
        }

        _nextOffered = _time.GetUtcNow();
        _nextOfferTakesBareYes = bareYes;
        _offerStands = bareYes; // false only for one Raven kept quiet
        _offerTimer?.Dispose();
        _offerTimer = _time.CreateTimer(_ => _dispatcher.Post(() => OfferLapsed(number)), null, NextOfferLifetime, Timeout.InfiniteTimeSpan);
    }

    /// <summary>No yes came in time: what Raven held for the offer may be said. A yes said in time still counts when its words come later.</summary>
    private void OfferLapsed(int number)
    {
        if (number == _offerNumber && _offerStands)
        {
            _offerStands = false;
            ScheduleNews();
        }
    }

    /// <summary>Any offer of the next question is over, said, due or standing: the user answered it, or moved on.</summary>
    private void DropOffer()
    {
        _offerNumber++;
        _nextOfferDue = false;
        _nextOffered = null;
        _nextOfferTakesBareYes = false;
        _offerTimer?.Dispose();
        _offerTimer = null;
        if (_offerStands)
        {
            _offerStands = false;
            ScheduleNews(); // what Raven held for it may be said
        }
    }

    /// <summary>The Cab opened a workspace: its chat is shown. The list never opens a workspace itself.</summary>
    public void ShowChatOf(Guid workspaceId)
    {
        if (Chats.FirstOrDefault(c => c.WorkspaceId == workspaceId) is { } chat)
        {
            SelectedChat = chat;
        }
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
