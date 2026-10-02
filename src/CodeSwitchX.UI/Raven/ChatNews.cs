using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.UI.Raven;

public enum ChatNewsKind
{
    Finished,
    NeedsYou,
    Failed,
}

/// <summary>One chat's news, as the digest card lists it and Raven's brain is told it.</summary>
/// <param name="Detail">What it needs (its notification), for <see cref="ChatNewsKind.NeedsYou"/>; null otherwise.</param>
/// <param name="LastSaid">The end of its last reply, for a chat that finished or failed; null when unknown.</param>
/// <param name="Stale">Older than <see cref="ChatNews.MaximumAge"/>: shown, not spoken.</param>
public sealed record ChatNewsLine(string SessionId, Guid WorkspaceId, string Workspace, string Title, ChatNewsKind Kind, string? Detail,
    string? LastSaid, bool Stale)
{
    /// <summary>What happened, as the card and the fallback sentence say it ("finished", "needs you").</summary>
    public string What => Kind switch
    {
        ChatNewsKind.Finished => "finished",
        ChatNewsKind.NeedsYou => "needs you",
        _ => "failed",
    };

    /// <summary>The card's line: "ContentAutomatorX · Fix the upload retry: finished".</summary>
    public string Text => $"{Workspace} · {Title}: {What}" + (Stale ? $" (older than {Span(ChatNews.MaximumAge)})" : "");

    /// <summary>"2 minutes", "1 minute", "90 seconds": whole minutes when it is some, else seconds.</summary>
    internal static string Span(TimeSpan span) => span.TotalSeconds % 60 == 0 && span.TotalMinutes >= 1
        ? Count((long)span.TotalMinutes, "minute")
        : Count((long)Math.Round(span.TotalSeconds), "second");

    private static string Count(long n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
}

/// <summary>
/// What the chats did since Raven last told it: one slot per chat, so a chat that changed twice before Raven got to it
/// is told once, with its latest news. A chat's news is: its turn ended (Working to Idle), it waits for the user, or it
/// failed (its claude went, or its turn ended on an API error). Only for the chats the Yard shows, and only for changes seen while the app runs: a chat restored at startup
/// brings no news. Thread-safe: the bus raises changes on any thread.
/// </summary>
public sealed class ChatNews : IDisposable
{
    /// <summary>News older than this is shown in the log, not spoken: the moment for it has passed.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(2);

    private readonly IYardDirectory _yard;
    private readonly TimeProvider _time;
    private readonly Func<string?, string?> _lastSaid;
    private readonly IDisposable _subscription;
    private readonly DateTimeOffset _since;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    /// <param name="lastSaid">The end of a chat's last reply from its transcript path (<c>TranscriptLastReply.Read</c>); called off the UI thread.</param>
    public ChatNews(IEventBus bus, IYardDirectory yard, TimeProvider time, Func<string?, string?> lastSaid)
    {
        _yard = yard;
        _time = time;
        _lastSaid = lastSaid;
        _since = time.GetUtcNow();
        _subscription = bus.Subscribe<SessionChanged>(Offer);
    }

    /// <summary>Raised on any thread when a chat has news.</summary>
    public event EventHandler? Arrived;


    public bool HasNews
    {
        get
        {
            lock (_lock)
            {
                return _slots.Count > 0;
            }
        }
    }

    internal void Offer(SessionChanged change)
    {
        if (KindOf(change) is not { } kind || change.Current.StateSince < _since)
        {
            return;
        }

        var current = change.Current;
        lock (_lock)
        {
            _slots[current.SessionId] = new Slot(kind, kind == ChatNewsKind.NeedsYou ? current.LastNotification : null, current.StateSince,
                current.TranscriptPath);
        }

        Arrived?.Invoke(this, EventArgs.Empty);
    }

    private static ChatNewsKind? KindOf(SessionChanged change)
    {
        if (change.Previous is not { } previous || !change.Current.ShowsAsChat || previous.State == change.Current.State)
        {
            return null;
        }

        return change.Current.State switch
        {
            // A turn that ended on an API error (a usage limit, an overload) stops as Idle too, but its work is not done.
            SessionState.Idle when previous.State == SessionState.Working => change.Current.TurnFailed ? ChatNewsKind.Failed : ChatNewsKind.Finished,
            SessionState.Waiting => ChatNewsKind.NeedsYou,
            SessionState.Errored => ChatNewsKind.Failed,
            _ => null,
        };
    }

    /// <summary>
    /// Empties the slots: their news is told now, or never. Each line names the chat as the Yard shows it; a chat gone
    /// from the board is left out, and so is news the chat has moved past since without new news (it needed the user
    /// and works again, it finished and works again). Oldest first. The chats' last replies are read together.
    /// </summary>
    public async Task<IReadOnlyList<ChatNewsLine>> TakeAsync(CancellationToken ct)
    {
        List<KeyValuePair<string, Slot>> taken;
        lock (_lock)
        {
            taken = [.. _slots];
            _slots.Clear();
        }

        if (taken.Count == 0)
        {
            return [];
        }

        var chats = (await _yard.ChatsAsync(ct).ConfigureAwait(false)).ToDictionary(c => c.Id, StringComparer.Ordinal);
        var now = _time.GetUtcNow();
        var still = taken.OrderBy(t => t.Value.At)
            .Where(t => chats.TryGetValue(t.Key, out var chat) && StillHolds(t.Value.Kind, chat))
            .Select(t => (Id: t.Key, Slot: t.Value, Chat: chats[t.Key]))
            .ToList();
        var lastSaid = await Task.WhenAll(still.Select(t => t.Slot.Kind == ChatNewsKind.NeedsYou
            ? Task.FromResult<string?>(null)
            : Task.Run(() => _lastSaid(t.Slot.TranscriptPath), ct))).ConfigureAwait(false);
        return still.Select((t, i) => new ChatNewsLine(t.Id, t.Chat.WorkspaceId, t.Chat.Workspace, t.Chat.Title, t.Slot.Kind, t.Slot.Detail,
            lastSaid[i], now - t.Slot.At > MaximumAge)).ToList();
    }

    /// <summary>Whether the chat, as the Yard shows it now, is still where its news left it.</summary>
    private static bool StillHolds(ChatNewsKind kind, YardChat chat) => kind switch
    {
        ChatNewsKind.NeedsYou => chat.NeedsYou,
        _ => chat.State != SessionState.Working, // finished or failed, until it works again
    };

    public void Dispose() => _subscription.Dispose();

    private sealed record Slot(ChatNewsKind Kind, string? Detail, DateTimeOffset At, string? TranscriptPath);
}
