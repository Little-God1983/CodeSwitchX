using System.Globalization;
using CodeSwitchX.Core.Usage;
using CodeSwitchX.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Telemetry;

/// <summary>
/// The real limits of the user's plan on the bottom bar (#370). Read at the start, every five minutes and right after a reset
/// passes; between readings the countdown ticks on its own. How often <c>/usage</c> may be called is not known, so it stays
/// modest. A failed reading leaves the last figures standing; a reset that has passed makes its percent wrong, so that limit
/// says it is being read again.
/// </summary>
public sealed partial class PlanLimitsViewModel(IPlanUsageReader reader, IUiDispatcher ui, TimeProvider time, ILogger<PlanLimitsViewModel> logger) : ObservableObject, IDisposable
{
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ReadInterval = TimeSpan.FromMinutes(5);

    /// <summary>How soon to look again at a reading that still tells of the old window: the text's reset can be a minute early.</summary>
    internal static readonly TimeSpan RetryAfterReset = TimeSpan.FromMinutes(1);

    /// <summary>How many times in a row that is done before going back to <see cref="ReadInterval"/>.</summary>
    internal const int MaxResetRetries = 5;

    private readonly CancellationTokenSource _stop = new();
    private ITimer? _timer;
    private PlanLimits? _limits;
    private DateTimeOffset _readAt;
    private DateTimeOffset _nextRead = DateTimeOffset.MinValue;
    private int _resetRetries;
    private bool _reading;
    private bool _stopped;
    private bool _disposed;

    /// <summary>"5h 54% · resets in 2h 41m"; null while there is nothing to show.</summary>
    [ObservableProperty] private string? _fiveHourText;

    /// <summary>"week 38% · resets Mon 06:00"; null while there is nothing to show.</summary>
    [ObservableProperty] private string? _weekText;

    /// <summary>What the figures are, and how old.</summary>
    [ObservableProperty] private string? _detail;

    /// <summary>The reading under way, or the last; it never faults.</summary>
    internal Task PendingRead { get; private set; } = Task.CompletedTask;

    /// <summary>Begins the first reading and the ticks.</summary>
    public void Start()
    {
        _timer = time.CreateTimer(_ => ui.Post(Tick), null, TickInterval, TickInterval);
        Tick();
    }

    private void Tick()
    {
        if (_disposed)
        {
            return;
        }

        var now = time.GetUtcNow();
        Show(now);
        if (_stopped || _reading || now < _nextRead)
        {
            return;
        }

        _reading = true;
        // Off the UI thread: the package looks for Claude Code and starts it before its first await.
        PendingRead = Task.Run(ReadAsync);
    }

    private async Task ReadAsync()
    {
        PlanReading reading;
        try
        {
            reading = await reader.ReadAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // the app is closing
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reading the plan's limits failed");
            reading = new PlanReading(PlanReadingKind.Failed);
        }

        ui.Post(() => Apply(reading));
    }

    private void Apply(PlanReading reading)
    {
        if (_disposed)
        {
            return;
        }

        _reading = false;
        var now = time.GetUtcNow();
        switch (reading.Kind)
        {
            case PlanReadingKind.Read:
                _limits = reading.Limits;
                _readAt = now;
                break;
            case PlanReadingKind.NoFigures:
                _limits = null;
                break;
            case PlanReadingKind.SpentATurn:
                _limits = null;
                _stopped = true;
                break;
        }

        _nextRead = NextRead(now);
        Show(now);
    }

    private DateTimeOffset NextRead(DateTimeOffset now)
    {
        var next = now + ReadInterval;
        DateTimeOffset?[] resets = [_limits?.FiveHour?.ResetsAt, _limits?.Week?.ResetsAt];
        if (!resets.Any(r => r <= now))
        {
            _resetRetries = 0;
        }
        else if (_resetRetries++ < MaxResetRetries)
        {
            next = now + RetryAfterReset;
        }

        // Whatever the other limit does: a reset coming up is read right after it passes.
        return resets.Where(r => r > now && r < next).Min() ?? next;
    }

    private void Show(DateTimeOffset now)
    {
        FiveHourText = _limits?.FiveHour is { } fiveHour ? Line("5h", fiveHour, now, CountdownOf) : null;
        WeekText = _limits?.Week is { } week ? Line("week", week, now, DayOf) : null;
        Detail = _limits is null
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"The real limits of your plan, as Claude Code's /usage says them. Read at {time.ToLocal(_readAt):HH:mm}.");
    }

    private string Line(string name, PlanLimit limit, DateTimeOffset now, Func<DateTimeOffset, DateTimeOffset, string> resets) => limit.ResetsAt switch
    {
        null => $"{name} {limit.Percent}%",
        { } at when at <= now => $"{name} reading again…",
        { } at => $"{name} {limit.Percent}% · resets {resets(at, now)}",
    };

    /// <summary>Hours and minutes: the source is good to about a minute. Counted up, so it never says "0m" while time is left.</summary>
    private static string CountdownOf(DateTimeOffset at, DateTimeOffset now)
    {
        var minutes = (int)Math.Ceiling((at - now).TotalMinutes);
        return minutes >= 60 ? $"in {minutes / 60}h {minutes % 60}m" : $"in {minutes}m";
    }

    /// <summary>To the nearest five minutes: the text's time wavers around the real one, and the same reset read "5:59am" and "6am" within the hour. A day and a time need no more than that.</summary>
    private string DayOf(DateTimeOffset at, DateTimeOffset now)
    {
        var local = time.ToLocal(at);
        var five = TimeSpan.FromMinutes(5).Ticks;
        var nearest = new DateTimeOffset((local.Ticks + five / 2) / five * five, local.Offset);
        return nearest.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _stop.Cancel();
    }
}

file static class TimeProviderLocal
{
    public static DateTimeOffset ToLocal(this TimeProvider time, DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, time.LocalTimeZone);
}
