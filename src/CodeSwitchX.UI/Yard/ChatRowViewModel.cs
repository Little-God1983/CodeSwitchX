using CodeSwitchX.Core.Sessions;
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
        Title = snapshot.Title ?? $"Chat {snapshot.SessionId[..Math.Min(8, snapshot.SessionId.Length)]}";
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

    public void Tick(DateTimeOffset now) => ElapsedText = FormatElapsed(now - StateSince);

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
