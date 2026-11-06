using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.Yard;

/// <summary>Sends a chat the keep-alive ping (<see cref="KeepAlive.Text"/>).</summary>
public interface IKeepAliveSender
{
    /// <summary>Sends it to the chat messaged by <paramref name="sendName"/>; whether it was sent. Throws only when cancelled.</summary>
    Task<bool> SendAsync(string sendName, CancellationToken ct);
}

/// <summary>
/// The chats the user asked to keep warm (#386), each until a time they chose. A chat's prompt cache lasts an hour from its
/// last request; five minutes before that runs out, the chat is sent a ping (<see cref="KeepAlive"/>), whose turn reads the
/// cache and starts the hour again. While the user works in the chat themselves nothing is sent. A chat is pinged only
/// while it sits idle in a VS Code tab: one in a turn would queue the message behind it, and one whose tab is closed has no
/// process to message. A chat that went cold all the same (the app was closed, the chat waited on the user, its tab was
/// closed) is not warmed again: a ping then would pay for the whole conversation, which is what keeping warm is to save.
/// The marks are saved, and come back after a restart. Thread-safe.
/// </summary>
public sealed class KeepWarm : BackgroundService
{
    /// <summary>What the user picks from: a forgotten mark must not ping for days.</summary>
    public static readonly IReadOnlyList<TimeSpan> Durations = [TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(8)];

    /// <summary>How long a chat's cache lasts from its last request (measured for #331; <c>CacheWarmthCalculator.Lifetime</c>).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// How old a chat's last request is when it is pinged: five minutes of the hour are left for the ping to land, and to
    /// try again. Set shorter only by a debug build's on-screen check, which cannot wait 55 minutes for each ping.
    /// </summary>
    public TimeSpan PingAfter { get; init; } = TimeSpan.FromMinutes(55);

    /// <summary>How long a ping is given to show as a request of the chat before another is sent.</summary>
    public static readonly TimeSpan PingGap = TimeSpan.FromMinutes(2);

    /// <summary>How often the marked chats are looked at.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(20);

    /// <summary>The setting the marks are saved under: each chat's id with the time its keeping warm ends.</summary>
    internal const string SettingsKey = "yard.keepWarm";

    private readonly Func<string, SessionSnapshot?> _sessionOf;
    private readonly Func<string, string?> _sendNameOf;
    private readonly IKeepAliveSender _sender;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<KeepWarm> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Mark> _marks = new(StringComparer.OrdinalIgnoreCase);
    private Task _saved = Task.CompletedTask;
    private int _checking;

    /// <param name="sessionOf">The engine's snapshot of a chat (<see cref="SessionEngine.Get"/>).</param>
    /// <param name="sendNameOf">The name a chat open in a VS Code tab is messaged by; null when it is not open in one.</param>
    public KeepWarm(Func<string, SessionSnapshot?> sessionOf, Func<string, string?> sendNameOf, IKeepAliveSender sender, ISettingsStore settings,
        TimeProvider time, ILogger<KeepWarm> logger)
    {
        _sessionOf = sessionOf;
        _sendNameOf = sendNameOf;
        _sender = sender;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    /// <summary>A chat's mark was set or taken off (its id). Raised on the thread that changed it.</summary>
    public event Action<string>? Changed;

    /// <summary>Done once every change so far is saved.</summary>
    public Task Saved
    {
        get
        {
            lock (_gate)
            {
                return _saved;
            }
        }
    }

    /// <summary>Until when the chat is kept warm; null for one that is not.</summary>
    public DateTimeOffset? Until(string sessionId)
    {
        lock (_gate)
        {
            return _marks.TryGetValue(sessionId, out var mark) ? mark.Until : null;
        }
    }

    /// <summary>Why the chat cannot be kept warm, in words for the user; null when it can.</summary>
    public string? WhyNot(string sessionId)
    {
        if (_sendNameOf(sessionId) is null)
        {
            return "Only a chat open in a VS Code tab can be kept warm.";
        }

        if (_sessionOf(sessionId)?.LastRequestAt is not { } request)
        {
            return "It has no warm cache yet.";
        }

        return _time.GetUtcNow() - request >= Lifetime
            ? "Its cache is cold already: keeping it warm would pay for the whole conversation again."
            : null;
    }

    /// <summary>Keeps the chat warm for <paramref name="duration"/> from now; false when it cannot be (<see cref="WhyNot"/>).</summary>
    public bool Start(string sessionId, TimeSpan duration)
    {
        if (WhyNot(sessionId) is { } why)
        {
            _logger.LogInformation("Chat {Id} is not kept warm: {Why}", sessionId, why);
            return false;
        }

        var until = _time.GetUtcNow() + duration;
        lock (_gate)
        {
            _marks[sessionId] = new Mark(until, null);
            SaveLocked();
        }

        _logger.LogInformation("Chat {Id} is kept warm until {Until:u}", sessionId, until);
        Changed?.Invoke(sessionId);
        return true;
    }

    /// <summary>Stops keeping the chat warm; nothing for one that is not.</summary>
    public void Stop(string sessionId) => End(sessionId, "the user stopped it");

    private void End(string sessionId, string why)
    {
        lock (_gate)
        {
            if (!_marks.Remove(sessionId))
            {
                return;
            }

            SaveLocked();
        }

        _logger.LogInformation("Chat {Id} is kept warm no more: {Why}", sessionId, why);
        Changed?.Invoke(sessionId);
    }

    /// <summary>Reads the saved marks; one whose time ran out meanwhile is left out.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var saved = await _settings.GetAsync<Dictionary<string, DateTimeOffset>>(SettingsKey, ct).ConfigureAwait(false);
        if (saved is not { Count: > 0 })
        {
            return;
        }

        var now = _time.GetUtcNow();
        List<string> restored = [];
        lock (_gate)
        {
            foreach (var (id, until) in saved.Where(m => m.Value > now))
            {
                if (_marks.TryAdd(id, new Mark(until, null)))
                {
                    restored.Add(id);
                }
            }

            if (restored.Count != saved.Count)
            {
                SaveLocked();
            }
        }

        foreach (var id in restored)
        {
            Changed?.Invoke(id);
        }
    }

    /// <summary>Saves the marks as they are, after the save before. Under <see cref="_gate"/>.</summary>
    private void SaveLocked()
    {
        var marks = _marks.ToDictionary(m => m.Key, m => m.Value.Until, StringComparer.OrdinalIgnoreCase);
        _saved = _saved.ContinueWith(async _ =>
        {
            try
            {
                await _settings.SetAsync(SettingsKey, marks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The chats kept warm could not be saved; they are kept warm until the app closes");
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await LoadAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The chats kept warm could not be read; none is kept warm");
        }

        using var timer = new PeriodicTimer(CheckEvery, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Looking at the chats kept warm failed");
            }
        }
    }

    /// <summary>Looks at every marked chat once: ends what is over, and pings what is about to go cold.</summary>
    internal async Task CheckAsync(CancellationToken ct)
    {
        // One pass at a time: a ping takes seconds, and a second pass meanwhile would send it twice.
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        try
        {
            List<KeyValuePair<string, Mark>> marks;
            lock (_gate)
            {
                marks = [.. _marks];
            }

            foreach (var (id, mark) in marks)
            {
                var now = _time.GetUtcNow();
                var chat = _sessionOf(id);
                var name = _sendNameOf(id);
                switch (Next(now, mark, chat, name, PingAfter))
                {
                    case Step.TimeUp:
                        End(id, "its time is up");
                        break;
                    case Step.Cold:
                        End(id, "its cache ran out all the same");
                        break;
                    case Step.Ping:
                        lock (_gate)
                        {
                            if (!_marks.TryGetValue(id, out var current) || current.Until != mark.Until)
                            {
                                continue; // stopped, or marked anew, meanwhile
                            }

                            _marks[id] = mark with { PingedAt = now };
                        }

                        var sent = await _sender.SendAsync(name!, ct).ConfigureAwait(false);
                        if (sent)
                        {
                            _logger.LogInformation("Chat {Id} was pinged to keep its cache warm", id);
                        }
                        else
                        {
                            _logger.LogWarning("The keep-alive ping for chat {Id} was not sent; it is tried again while its cache lasts", id);
                        }

                        break;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private enum Step
    {
        Wait,
        Ping,
        TimeUp,
        Cold,
    }

    private static Step Next(DateTimeOffset now, Mark mark, SessionSnapshot? chat, string? sendName, TimeSpan pingAfter)
    {
        if (now >= mark.Until)
        {
            return Step.TimeUp;
        }

        // Not known to the engine (yet): nothing tells how warm it is, so nothing is sent.
        if (chat?.LastRequestAt is not { } request)
        {
            return Step.Wait;
        }

        var age = now - request;
        if (age >= Lifetime)
        {
            return Step.Cold;
        }

        // At its prompt: idle, or working only through a background job (#332).
        var atPrompt = chat.State == SessionState.Idle || (chat.State == SessionState.Working && chat.TurnOverAt is not null);
        var pingedLately = mark.PingedAt is { } pinged && now - pinged < PingGap;
        return age >= pingAfter && atPrompt && sendName is not null && !pingedLately ? Step.Ping : Step.Wait;
    }

    /// <param name="PingedAt">When the chat was last sent a ping; null before the first.</param>
    private sealed record Mark(DateTimeOffset Until, DateTimeOffset? PingedAt);
}
