using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Hosting.VsCode;
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

    private OpenChatTab? _tab;

    private readonly Action<string>? _open;

    /// <param name="open">Opens the chat with this session id in its workspace's VS Code (#115); null where a row opens nothing.</param>
    public ChatRowViewModel(string sessionId, Action<string>? open = null)
    {
        SessionId = sessionId;
        _open = open;
    }

    public string SessionId { get; }
    public bool IsLive => SessionStateMachine.IsLive(State);
    public bool NeedsUser => SessionStateMachine.NeedsUser(State);
    public bool IsVoice => VoiceLabel is not null;

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
        _ownTitle = snapshot.Title;
        ShowTitle();
        State = snapshot.State;
        StateSince = snapshot.StateSince;
        Inferred = snapshot.Inferred;
        LastToolName = snapshot.LastToolName;
        Model = snapshot.Model;
        ContextFill = ContextFillCalculator.Fill(snapshot.LatestContext, pricing.ContextWindowOf(snapshot.Model));
        Pressure = ContextFillCalculator.Level(ContextFill);
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
    /// <param name="notRunning">Whether the row is there for the tab alone: the chat does not run.</param>
    /// <param name="lastWrittenIn">For a tab the app knows no chat of: when its conversation was last written in, null when that is not known.</param>
    /// <param name="listedAt">For such a tab: when VS Code wrote the list it is in.</param>
    public void ShowTab(OpenChatTab? tab, bool notRunning, DateTimeOffset? lastWrittenIn = null, DateTimeOffset listedAt = default)
    {
        _tab = tab;
        NotRunning = notRunning;
        if (!_hasSession)
        {
            // Ended since it was last written in, as far as anyone knows; the time is shown only when it is known.
            State = SessionState.Ended;
            StateSince = lastWrittenIn ?? listedAt;
            _timed = lastWrittenIn is not null;
            OnPropertyChanged(nameof(IsLive));
        }

        ShowTitle();
    }

    private void ShowTitle() =>
        Title = _ownTitle ?? _tab?.Title ?? (_tab is not null ? NewChatTitle : $"Chat {SessionId[..Math.Min(8, SessionId.Length)]}");

    /// <summary>What a tab nothing was said in yet is called.</summary>
    public const string NewChatTitle = "New chat";

    public void Tick(DateTimeOffset now) => ElapsedText = _hasSession || _timed ? FormatElapsed(now - StateSince) : string.Empty;

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
