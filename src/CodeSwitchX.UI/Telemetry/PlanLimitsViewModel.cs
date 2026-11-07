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
/// says it is being read again, until a fresh reading still tells of it well after the reset: that window ended with the
/// account idle, and the limit stands at 0% (#376).
/// </summary>
public sealed partial class PlanLimitsViewModel(IPlanUsageReader reader, IUiDispatcher ui, TimeProvider time, ILogger<PlanLimitsViewModel> logger) : ObservableObject, IDisposable
{
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ReadInterval = TimeSpan.FromMinutes(5);

    /// <summary>How soon to look again at a reading that still tells of the old window: the text's reset can be a minute early.</summary>
    internal static readonly TimeSpan RetryAfterReset = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long after a reset that is done, before going back to <see cref="ReadInterval"/>; and how far past its reset a fresh
    /// reading may still tell of the old window before that window counts as ended with no new one begun (#376). A window
    /// starts with the first use after the reset, so with the account idle since, nothing is spent: the limit stands at 0%
    /// with no reset, as a reading that says so, rather than being read again for hours. Ten minutes: the text's reset can be
    /// a minute early, and the server gets time to catch up before a figure is inferred instead of read.
    /// </summary>
    internal static readonly TimeSpan EndedAfter = TimeSpan.FromMinutes(10);

    private readonly CancellationTokenSource _stop = new();
    private ITimer? _timer;
    private PlanLimits? _limits;
    private DateTimeOffset _readAt;

    /// <summary>What the last reading's ended windows were, said in the detail line; null when none was.</summary>
    private string? _endedNote;
    private DateTimeOffset _nextRead = DateTimeOffset.MinValue;
    private bool _reading;
    private bool _stopped;
    private bool _disposed;

    /// <summary>"5h 54% · resets in 2h 41m"; null while there is nothing to show.</summary>
    [ObservableProperty] private string? _fiveHourText;

    /// <summary>"week 38% · resets Mon 06:00"; null while there is nothing to show.</summary>
    [ObservableProperty] private string? _weekText;

    /// <summary>The 5-hour limit as the bar draws it (#384); null while there is nothing to show.</summary>
    [ObservableProperty] private LimitBarState? _fiveHourBar;

    /// <summary>The weekly limit as the bar draws it (#384); null while there is nothing to show.</summary>
    [ObservableProperty] private LimitBarState? _weekBar;

    /// <summary>What the figures are, and how old.</summary>
    [ObservableProperty] private string? _detail;

    /// <summary>How long each window runs: its pace mark is counted back from the reset.</summary>
    internal static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);

    internal static readonly TimeSpan WeekWindow = TimeSpan.FromDays(7);

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
                _limits = reading.Limits is { } limits ? new PlanLimits(UnlessEnded(limits.FiveHour, now), UnlessEnded(limits.Week, now)) : null;
                _endedNote = EndedNote(reading.Limits, now);
                _readAt = now;
                break;
            case PlanReadingKind.NoFigures:
                _limits = null;
                _endedNote = null;
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
        // Each limit's own reset is looked for soon after it passes, and no longer than EndedAfter: by then a reading has ended
        // its window, or readings fail and the five minutes do.
        if (resets.Any(r => r <= now && !IsEnded(r.Value, now)))
        {
            next = now + RetryAfterReset;
        }

        // Whatever the other limit does: a reset coming up is read right after it passes.
        return resets.Where(r => r > now && r < next).Min() ?? next;
    }

    private void Show(DateTimeOffset now)
    {
        var fiveHour = _limits?.FiveHour;
        var week = _limits?.Week;
        FiveHourText = fiveHour is null ? null : Line("5h", fiveHour, now, (at, from) => $"in {CountdownOf(at, from)}");
        WeekText = week is null ? null : Line("week", week, now, DayOf);
        FiveHourBar = fiveHour is null ? null : Bar(fiveHour, FiveHourWindow, now, CountdownOf);
        WeekBar = week is null ? null : Bar(week, WeekWindow, now, DayOf);
        Detail = _limits is null ? null : $"The real limits of your plan, as Claude Code's /usage says them. Read {ReadText(now)}.{_endedNote}";
    }

    /// <summary>A limit as read, or 0% with no reset when the reading tells of a window that ended with no new one begun (see <see cref="EndedAfter"/>).</summary>
    private static PlanLimit? UnlessEnded(PlanLimit? limit, DateTimeOffset now) =>
        limit?.ResetsAt is { } at && IsEnded(at, now) ? new PlanLimit(0, null) : limit;

    /// <summary>
    /// Whether a fresh reading that still tells this reset tells of a window that ended; the retries after a reset stop at the
    /// same point. A past reset kept from an earlier reading, while readings fail, is not judged: nothing new was read.
    /// </summary>
    private static bool IsEnded(DateTimeOffset resetsAt, DateTimeOffset now) => resetsAt <= now - EndedAfter;

    /// <summary>The 0% of an ended window is inferred, not what /usage printed, and the detail line says so.</summary>
    private static string? EndedNote(PlanLimits? read, DateTimeOffset now)
    {
        var fiveHour = read?.FiveHour?.ResetsAt is { } a && IsEnded(a, now);
        var week = read?.Week?.ResetsAt is { } b && IsEnded(b, now);
        var which = (fiveHour, week) switch
        {
            (true, true) => "The 5-hour window and the week",
            (true, false) => "The 5-hour window",
            (false, true) => "The week",
            _ => null,
        };
        return which is null ? null : $" {which} ended with nothing used since: /usage still tells the old reset, so 0% is shown until your next use starts a new one.";
    }

    /// <summary>With the day when it was another: failed readings leave old figures standing, and this is where their age shows.</summary>
    private string ReadText(DateTimeOffset now)
    {
        var read = time.ToLocal(_readAt);
        return read.Date == time.ToLocal(now).Date
            ? read.ToString("'at' HH:mm", CultureInfo.InvariantCulture)
            : read.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }

    private string Line(string name, PlanLimit limit, DateTimeOffset now, Func<DateTimeOffset, DateTimeOffset, string> resets) => limit.ResetsAt switch
    {
        null => $"{name} {limit.Percent}%",
        { } at when at <= now => $"{name} reading again…",
        { } at => $"{name} {limit.Percent}% · resets {resets(at, now)}",
    };

    /// <summary>
    /// The bar's state: the percent, a short text, and how much of the window has passed, counted back from the reset. A reset
    /// that has passed leaves the bar empty: its percent tells of the window that ended.
    /// </summary>
    private static LimitBarState Bar(PlanLimit limit, TimeSpan window, DateTimeOffset now, Func<DateTimeOffset, DateTimeOffset, string> resets) => limit.ResetsAt switch
    {
        null => new LimitBarState(limit.Percent, null, $"{limit.Percent}%"),
        { } at when at <= now => new LimitBarState(0, null, "reading again…"),
        { } at => new LimitBarState(limit.Percent, Math.Clamp((window - (at - now)) / window * 100, 0, 100), $"{limit.Percent}% · {resets(at, now)}"),
    };

    /// <summary>Hours and minutes: the source is good to about a minute. Counted up, so it never says "0m" while time is left.</summary>
    private static string CountdownOf(DateTimeOffset at, DateTimeOffset now)
    {
        var minutes = (int)Math.Ceiling((at - now).TotalMinutes);
        return minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes}m";
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
