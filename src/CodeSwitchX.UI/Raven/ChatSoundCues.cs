using System.Collections.Concurrent;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Voice.Audio;

namespace CodeSwitchX.UI.Raven;

/// <summary>What Raven may say for a sound's event: nothing (a start), the chat's news, or a card read out.</summary>
public enum SoundLine
{
    None,
    News,
    Card,
}

/// <summary>
/// A chat did something that has a sound (#306).
/// </summary>
/// <param name="At">When it happened: a cue older than <see cref="ChatNews.MaximumAge"/> is not played any more.</param>
/// <param name="StillTrue">Whether what it announces still holds: a start while the chat works, a question while it is open.</param>
public sealed record SoundCue(SoundSlot Slot, string SessionId, Guid? WorkspaceId, DateTimeOffset At, SoundLine Line, Func<bool> StillTrue);

/// <summary>
/// Turns what chats do into sound cues (#306): a prompt starts a turn, and the chat news (<see cref="ChatNews"/>) says a turn
/// finished, failed or waits for the user, with the same filters as the news. Each cue holds as long as the chat's latest
/// state agrees with it. Cards are cued by Raven's panel, which holds them. Thread-safe: the bus raises on any thread, and
/// so does <see cref="Cue"/>.
/// </summary>
public sealed class ChatSoundCues : IDisposable
{
    private readonly ChatNews _news;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;
    private readonly ConcurrentDictionary<string, SessionState> _states = new(StringComparer.Ordinal);

    public ChatSoundCues(IEventBus bus, ChatNews news, TimeProvider time)
    {
        _news = news;
        _time = time;
        _subscription = bus.Subscribe<SessionChanged>(OnChanged);
        _news.Noted += OnNoted;
    }

    public event EventHandler<SoundCue>? Cue;

    private void OnChanged(SessionChanged change)
    {
        var current = change.Current;
        _states[current.SessionId] = current.State;
        // Once per turn: a chat that waited on the user and works again goes on with the turn it had.
        if (current.TurnStartedAt is { } turn && turn != change.Previous?.TurnStartedAt && current.State == SessionState.Working
            && current.ShowsAsChat)
        {
            Raise(SoundSlot.StartsWorking, current.SessionId, current.WorkspaceId, SoundLine.None, SessionState.Working, holds: true);
        }
    }

    private void OnNoted(object? sender, (ChatNewsKind Kind, string SessionId, Guid? WorkspaceId) news)
    {
        var (kind, id, workspace) = news;
        // The news comes while the bus still tells its change, maybe before this hears it: it says where the chat stands.
        _states[id] = kind == ChatNewsKind.NeedsYou ? SessionState.Waiting : SessionState.Idle;
        switch (kind)
        {
            case ChatNewsKind.Finished:
                Raise(SoundSlot.Finishes, id, workspace, SoundLine.News, SessionState.Working, holds: false);
                break;
            case ChatNewsKind.Failed:
                Raise(SoundSlot.Fails, id, workspace, SoundLine.News, SessionState.Working, holds: false);
                break;
            default:
                // A chat waiting on the user without a card of Raven's sounds like a question.
                Raise(SoundSlot.AsksQuestion, id, workspace, SoundLine.News, SessionState.Waiting, holds: true);
                break;
        }
    }

    /// <param name="holds">Whether the cue holds while the chat is in <paramref name="state"/>, or while it is not.</param>
    private void Raise(SoundSlot slot, string id, Guid? workspace, SoundLine line, SessionState state, bool holds) =>
        Cue?.Invoke(this, new SoundCue(slot, id, workspace, _time.GetUtcNow(), line,
            () => _states.TryGetValue(id, out var now) && (now == state) == holds));

    public void Dispose()
    {
        _news.Noted -= OnNoted;
        _subscription.Dispose();
    }
}
