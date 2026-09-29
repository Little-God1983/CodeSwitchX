using System.Globalization;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Yard;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeSwitchX.UI.Telemetry;

/// <summary>Slim bottom bar: tokens today, estimated cost, 5-hour window against a soft budget, rate sparkline, session counts.</summary>
public sealed partial class PerformanceBarViewModel : ObservableObject, IDisposable
{
    private readonly TelemetryService _telemetry;
    private readonly SessionEngine _engine;
    private readonly IEventBus _bus;
    private readonly IUiDispatcher _ui;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly List<IDisposable> _subscriptions = [];
    private long? _budget;

    [ObservableProperty] private string _tokensTodayText = "0";
    [ObservableProperty] private string _costTodayText = "$0.00 est.";
    [ObservableProperty] private string _fiveHourText = "0";
    [ObservableProperty] private double _fiveHourPercent;
    [ObservableProperty] private bool _hasBudget;
    [ObservableProperty] private int _activeSessions;
    [ObservableProperty] private int _waitingSessions;
    [ObservableProperty] private double[] _rateNormalized = new double[TelemetryService.RateMinutes];
    [ObservableProperty] private bool _isExpanded = true;

    public PerformanceBarViewModel(TelemetryService telemetry, SessionEngine engine, IEventBus bus, IUiDispatcher ui, ISettingsStore settings, TimeProvider time)
    {
        _telemetry = telemetry;
        _engine = engine;
        _bus = bus;
        _ui = ui;
        _settings = settings;
        _time = time;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        _budget = await _settings.GetAsync<long?>(SettingKeys.FiveHourBudgetTokens, ct);
        _subscriptions.Add(_bus.Subscribe<TelemetryUpdated>(m => _ui.Post(() => Apply(m.Snapshot))));
        _subscriptions.Add(_bus.Subscribe<SessionChanged>(_ => _ui.Post(RecountSessions)));
        Apply(_telemetry.Current);
        RecountSessions();
    }

    public void SetBudget(long? budget)
    {
        _budget = budget;
        Apply(_telemetry.Current);
    }

    public void Apply(TelemetrySnapshot snapshot)
    {
        TokensTodayText = TokenFormat.Compact(snapshot.Today.Tokens.Total);
        CostTodayText = string.Create(CultureInfo.InvariantCulture, $"${snapshot.Today.Cost:0.00} est.")
            + (snapshot.Today.Unpriced ? " incl. unpriced" : string.Empty);

        var fiveHour = snapshot.FiveHours.Tokens.Total;
        HasBudget = _budget is > 0;
        FiveHourText = HasBudget ? $"{TokenFormat.Compact(fiveHour)} / {TokenFormat.Compact(_budget!.Value)}" : TokenFormat.Compact(fiveHour);
        FiveHourPercent = HasBudget ? Math.Min(100.0, 100.0 * fiveHour / _budget!.Value) : 0;

        var max = snapshot.RatePerMinute.Length == 0 ? 0 : snapshot.RatePerMinute.Max();
        RateNormalized = snapshot.RatePerMinute.Select(v => max == 0 ? 0.0 : (double)v / max).ToArray();
    }

    public void RecountSessions()
    {
        // The rows on the Yard: a chat on no tile (its cwd outside every workspace) is shown nowhere, and a Stale chat stays
        // on its tile for the stale row lifetime; counted without that, the number grew over the 24 h restore window and
        // matched nothing on screen.
        var now = _time.GetUtcNow();
        var shown = _engine.Snapshots.Where(s => s.WorkspaceId is not null).ToList();
        ActiveSessions = shown.Count(s => SessionStateMachine.IsLive(s.State) && (s.State != SessionState.Stale || now - s.StateSince < WorkspaceTileViewModel.StaleRowLifetime));
        WaitingSessions = shown.Count(s => SessionStateMachine.NeedsUser(s.State));
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }
}
