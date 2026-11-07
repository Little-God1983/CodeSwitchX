using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Telemetry;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeSwitchX.UI.Yard;

public sealed partial class ChatRowViewModel : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private SessionState _state;
    [ObservableProperty] private DateTimeOffset _stateSince;
    [ObservableProperty] private string _elapsedText = string.Empty;
    [ObservableProperty] private double _contextFill;
    [ObservableProperty] private ContextPressure _pressure;
    [ObservableProperty] private bool _inferred;

    /// <summary>How warm the chat's prompt cache still is (#331); Unknown while it made no request.</summary>
    [ObservableProperty] private CacheWarmth _cacheWarmth;

    /// <summary>What the row's gauge shows, 0 to 1.</summary>
    [ObservableProperty] private double _cacheFill;

    /// <summary>The gauge's tooltip, with the exact time left; null while nothing is known.</summary>
    [ObservableProperty] private string? _cacheTip;
    [ObservableProperty] private string? _lastToolName;

    /// <summary>
    /// Set while the app runs the chat because Raven started it: how it runs ("Fable 5.1 · high"), which the row shows by
    /// its voice marker. Null for every other chat.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVoice))]
    private string? _voiceLabel;

    [ObservableProperty] private string? _model;

    /// <summary>
    /// The chat's tab is open in VS Code, but the chat does not run: VS Code brought the tab back and it was not looked at
    /// yet, or its Claude Code ended (#164). It starts when the row, or the tab, is clicked.
    /// </summary>
    [ObservableProperty] private bool _notRunning;

    /// <summary>What the chat calls itself; null while it said nothing yet, and for a tab the app knows no chat of.</summary>
    private string? _ownTitle;

    /// <summary>Whether a chat's state was ever shown; a tab alone has none.</summary>
    private bool _hasSession;

    /// <summary>Whether a tab alone has a time to show: when its chat was last written in.</summary>
    private bool _timed;

    /// <summary>When the chat last sent a request; null while none was seen.</summary>
    private DateTimeOffset? _lastRequestAt;

    /// <summary>
    /// What the row's time counts from (#386): a chat idle after a keep-alive ping counts from the user's last use of it, not
    /// from the ping's end, which restarted <see cref="StateSince"/>.
    /// </summary>
    private DateTimeOffset _timedSince;

    /// <summary>For a tab alone: when its conversation was last written in. A write need not be a request, but no request is later.</summary>
    private DateTimeOffset? _writtenAt;

    private OpenChatTab? _tab;

    private readonly Action<string>? _open;

    private readonly IChatRowMenu? _menu;

    /// <summary>Whether anything was ever said in the chat the engine knows (<see cref="SessionSnapshot.HeldConversation"/>): one nothing was said in has nothing to compact.</summary>
    private bool _held;

    /// <summary>Until when the chat is kept warm (#386); null for one that is not. The row shows a flame for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKeptWarm), nameof(KeepWarmHeader), nameof(KeptWarmTip))]
    private DateTimeOffset? _keptWarmUntil;

    /// <summary>The chat is being compacted (#386): the row says so in place of its time.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompactNowCommand))]
    private bool _isCompacting;

    /// <summary>Why the chat cannot be kept warm, as the menu found it when it last opened; null when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanKeepWarm))]
    private string? _keepWarmBlock;

    /// <param name="open">Opens the chat with this session id in its workspace's VS Code (#115); null where a row opens nothing.</param>
    /// <param name="menu">What the row's right-click does (#386); null where a row has no menu.</param>
    public ChatRowViewModel(string sessionId, Action<string>? open = null, IChatRowMenu? menu = null)
    {
        SessionId = sessionId;
        _open = open;
        _menu = menu;
    }

    /// <summary>Whether a right-click on the row opens its own menu (#386).</summary>
    public bool HasMenu => _menu is not null;

    public bool IsKeptWarm => KeptWarmUntil is not null;

    /// <summary>The menu's entry: "Keep warm", with the time it ends while it is on.</summary>
    public string KeepWarmHeader => KeptWarmUntil is { } until ? $"Keep warm (until {Clock(until)})" : "Keep warm";

    /// <summary>The flame's tooltip; null while the chat is not kept warm.</summary>
    public string? KeptWarmTip => KeptWarmUntil is { } until
        ? $"Kept warm until {Clock(until)}: a keep-alive message is sent to it before its cache runs out"
        : null;

    public bool CanKeepWarm => _menu is not null && KeepWarmBlock is null;

    /// <summary>
    /// A chat something was said in, not being compacted already. Not a tab the app knows no chat of: it does not know the
    /// folder that one runs in, which Claude Code finds its conversation by.
    /// </summary>
    private bool CanCompact => _menu is not null && !IsCompacting && _hasSession && _held;

    /// <summary>
    /// The row's menu opens: what it offers is found out now. Whether a chat can be kept warm takes a look at Claude Code's
    /// own records, which is not done for every row each second.
    /// </summary>
    public void MenuOpening()
    {
        KeepWarmBlock = _menu?.WhyNotKeptWarm(SessionId);
        KeptWarmUntil = _menu?.KeptWarmUntil(SessionId);
        CompactNowCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCompact))]
    private Task CompactNowAsync() => _menu!.CompactAsync(SessionId);

    /// <param name="hours">How long, as the menu's entry gives it: "2", "4" or "8".</param>
    [RelayCommand]
    private void KeepWarm(string? hours)
    {
        if (double.TryParse(hours, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var count) && count > 0)
        {
            _menu?.KeepWarm(SessionId, TimeSpan.FromHours(count));
            KeptWarmUntil = _menu?.KeptWarmUntil(SessionId);
        }
    }

    [RelayCommand]
    private void StopKeepingWarm()
    {
        _menu?.StopKeepingWarm(SessionId);
        KeptWarmUntil = _menu?.KeptWarmUntil(SessionId);
    }

    /// <summary>A time of day as the user reads it: "16:40".</summary>
    private static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public string SessionId { get; }
    public bool IsLive => SessionStateMachine.IsLive(State);
    public bool NeedsUser => SessionStateMachine.NeedsUser(State);
    public bool IsVoice => VoiceLabel is not null;

    /// <summary>VS Code lists a tab of the chat in its workspace's window, running or not (#297).</summary>
    public bool HasTab => _tab is not null;

    /// <summary>
    /// When a turn of the chat last started, waited or ended (#295): what orders a tile's rows, the latest first. A working
    /// chat counts from when it started working (not on a keep-alive ping), so its tool calls do not move it; else its last use, as the engine stamps
    /// it, or for a tab the app knows no chat of, when the chat was last written in; such a tab never written in goes last.
    /// </summary>
    public DateTimeOffset LastTurnAt { get; private set; }

    /// <summary>Engine version of the snapshot shown; older snapshots arriving late are ignored.</summary>
    public long Version { get; private set; }

    public void Update(SessionSnapshot snapshot, PricingTable pricing)
    {
        if (snapshot.Version < Version)
        {
            return;
        }

        Version = snapshot.Version;
        _hasSession = true;
        _held = snapshot.HeldConversation;
        _ownTitle = snapshot.Title ?? _ownTitle;
        ShowTitle();
        State = snapshot.State;
        StateSince = snapshot.StateSince;
        _timedSince = snapshot.KeepAliveTurn && snapshot.State == SessionState.Idle ? snapshot.LastUseAt ?? snapshot.StateSince : snapshot.StateSince;
        // The app's own ping (#386) is no turn of the user's: a row it set working stays where it was. One working already (a
        // background job) counts from when that began, as ever.
        var pingSetWorking = snapshot.KeepAliveTurn && snapshot.TurnStartedAt is { } ping && snapshot.StateSince >= ping;
        LastTurnAt = snapshot.State == SessionState.Working && !pingSetWorking ? snapshot.StateSince : snapshot.LastUseAt ?? snapshot.StartedAt;
        Inferred = snapshot.Inferred;
        LastToolName = snapshot.LastToolName;
        Model = snapshot.Model;
        ContextFill = ContextFillCalculator.Fill(snapshot.LatestContext, pricing.ContextWindowOf(snapshot.Model));
        Pressure = ContextFillCalculator.Level(ContextFill);
        _lastRequestAt = snapshot.LastRequestAt;
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(NeedsUser));
    }

    /// <summary>The row was clicked: the workspace's VS Code comes up with this chat's tab in front.</summary>
    [RelayCommand]
    private void Open() => _open?.Invoke(SessionId);

    /// <summary>
    /// Shows the chat's VS Code tab, null for none: a chat that calls itself nothing takes the tab's title, and one the app
    /// knows only by its tab shows as that tab, not running.
    /// </summary>
    /// <param name="notRunning">Whether the chat's tab is open while the chat does not run.</param>
    /// <param name="conversation">For a tab the app knows no chat of: what its conversation on disk says, null when it has none yet.</param>
    /// <param name="listedAt">For such a tab: when VS Code wrote the list it is in.</param>
    /// <param name="waits">For such a tab whose chat runs: its tab waits on the user (a permission prompt, a question).</param>
    public void ShowTab(OpenChatTab? tab, bool notRunning, TabConversation? conversation = null, DateTimeOffset listedAt = default, bool waits = false)
    {
        _tab = tab;
        NotRunning = notRunning;
        if (!_hasSession)
        {
            // Nothing was heard of it: idle while its Claude Code runs, ended else, since it was last written in as far
            // as anyone knows. The time is shown only when it is known; the tab's own title is cut short, the conversation's is not.
            _ownTitle = conversation?.Title;
            _writtenAt = conversation?.WrittenAt;
            StateSince = conversation?.WrittenAt ?? listedAt;
            _timedSince = StateSince;
            LastTurnAt = conversation?.WrittenAt ?? DateTimeOffset.MinValue; // a list's writing is no use of the chat
            _timed = conversation is not null;
            var state = notRunning ? SessionState.Ended : waits ? SessionState.Waiting : SessionState.Idle;
            if (State != state)
            {
                State = state;
                OnPropertyChanged(nameof(IsLive));
                OnPropertyChanged(nameof(NeedsUser));
            }
        }

        ShowTitle();
    }

    private void ShowTitle() =>
        Title = _ownTitle ?? _tab?.Title ?? (_tab is not null ? NewChatTitle : $"Chat {SessionId[..Math.Min(8, SessionId.Length)]}");

    /// <summary>What a tab nothing was said in yet is called.</summary>
    public const string NewChatTitle = "New chat";

    public void Tick(DateTimeOffset now)
    {
        if (_menu is not null)
        {
            KeptWarmUntil = _menu.KeptWarmUntil(SessionId);
            IsCompacting = _menu.IsCompacting(SessionId);
        }

        ElapsedText = IsCompacting ? "compacting…" : _hasSession || _timed ? FormatElapsed(now - _timedSince) : string.Empty;
        ShowCache(now);
    }

    /// <summary>
    /// The gauge. With no request known, a conversation last written in over an hour ago is cold all the same (#331: a chat
    /// the app did not restore, as a tab alone and on after it starts); within the hour its time left is not known.
    /// </summary>
    private void ShowCache(DateTimeOffset now)
    {
        var left = _lastRequestAt is { } request ? CacheWarmthCalculator.Left(request, now)
            : _writtenAt is { } written && CacheWarmthCalculator.Left(written, now) == TimeSpan.Zero ? TimeSpan.Zero
            : (TimeSpan?)null;
        CacheWarmth = left is { } known ? CacheWarmthCalculator.Level(known) : CacheWarmth.Unknown;
        CacheFill = left is { } shown ? CacheWarmthCalculator.Fill(shown) : 0;
        CacheTip = left is { } told
            ? FormatCacheTip(CacheWarmth, told) + (KeptWarmUntil is { } until ? Environment.NewLine + $"Kept warm until {Clock(until)}" : "")
            : null;
    }

    private static string FormatCacheTip(CacheWarmth warmth, TimeSpan left)
    {
        var minutes = (int)left.TotalMinutes;
        return warmth switch
        {
            CacheWarmth.Cold => "Cache cold: its hour ran out",
            _ => $"Cache {warmth.ToString().ToLowerInvariant()}: {(minutes == 0 ? "less than a minute" : minutes == 1 ? "1 minute" : $"{minutes} minutes")} left",
        };
    }

    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalMinutes < 1)
        {
            return $"{(int)elapsed.TotalSeconds}s";
        }

        if (elapsed.TotalHours < 1)
        {
            return $"{(int)elapsed.TotalMinutes}m";
        }

        if (elapsed.TotalDays < 1)
        {
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m";
        }

        return $"{(int)elapsed.TotalDays}d {elapsed.Hours}h";
    }
}
