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
    public string Text => $"{Workspace} · {Title}: {What}" + (Stale ? " (not spoken: older than 2 minutes)" : "");
}

/// <summary>
/// What the chats did since Raven last told it: one slot per chat, so a chat that changed twice before Raven got to it
/// is told once, with its latest news. A chat's news is: its turn ended (Working to Idle), it waits for the user, or it
/// failed. Only for the chats the Yard shows, and only for changes seen while the app runs: a chat restored at startup
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
            SessionState.Idle when previous.State == SessionState.Working => ChatNewsKind.Finished,
            SessionState.Waiting => ChatNewsKind.NeedsYou,
            SessionState.Errored => ChatNewsKind.Failed,
            _ => null,
        };
    }

    /// <summary>
    /// Empties the slots: their news is told now, or never. Each line names the chat as the Yard shows it; a chat gone
    /// from the board is left out. Oldest first.
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
        var lines = new List<ChatNewsLine>();
        foreach (var (id, slot) in taken.OrderBy(t => t.Value.At))
        {
            if (!chats.TryGetValue(id, out var chat))
            {
                continue;
            }

            var lastSaid = slot.Kind == ChatNewsKind.NeedsYou ? null : await Task.Run(() => _lastSaid(slot.TranscriptPath), ct).ConfigureAwait(false);
            lines.Add(new ChatNewsLine(id, chat.WorkspaceId, chat.Workspace, chat.Title, slot.Kind, slot.Detail, lastSaid, now - slot.At > MaximumAge));
        }

        return lines;
    }

    public void Dispose() => _subscription.Dispose();

    private sealed record Slot(ChatNewsKind Kind, string? Detail, DateTimeOffset At, string? TranscriptPath);
}
