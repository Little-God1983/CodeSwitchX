using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Telemetry;
using CommunityToolkit.Mvvm.ComponentModel;

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

    public ChatRowViewModel(string sessionId)
    {
        SessionId = sessionId;
    }

    public string SessionId { get; }
    public bool IsLive => SessionStateMachine.IsLive(State);
    public bool NeedsUser => SessionStateMachine.NeedsUser(State);

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
        var rule = pricing.Find(snapshot.Model);
        ContextFill = ContextFillCalculator.Fill(snapshot.LatestContext, rule);
        Pressure = ContextFillCalculator.Level(ContextFill);
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(NeedsUser));
    }

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
