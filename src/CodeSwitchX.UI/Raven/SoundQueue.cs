using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.Voice.Audio;

namespace CodeSwitchX.UI.Raven;

/// <summary>
/// The chats' sounds, one after another (#306): never two at once, with a short gap between them, and only while the panel
/// says the floor is free, after <see cref="TrafficWatcher.NewsGrace"/> of it. Nothing is dropped for coming soon after
/// another sound; a cue is dropped once it is outdated: replaced by a newer one of its chat, no longer true, older than
/// <see cref="ChatNews.MaximumAge"/>, or its slot picked None. A cue that waits for its spoken line is passed over until the
/// line takes it (<see cref="TakeForLine"/>). UI thread only; its timers and plays come back to it through the dispatcher.
/// </summary>
public sealed class SoundQueue
{
    /// <summary>The quiet between two sounds of one run.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(300);

    private readonly IChatSounds _sounds;
    private readonly SoundPicks _picks;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<bool> _mayPlay;
    private readonly Func<SoundCue, bool> _waitsForLine;
    private readonly List<SoundCue> _queue = [];
    private readonly ITimer _timer;
    private CancellationTokenSource? _playing;
    private bool _timing; // the grace or the gap runs

    /// <param name="mayPlay">Whether the floor is free for a sound: nobody talks, and Raven has nothing of its own to say.</param>
    /// <param name="waitsForLine">Whether the cue's sound waits for the line Raven speaks for it, to play straight before it.</param>
    public SoundQueue(IChatSounds sounds, SoundPicks picks, TimeProvider time, IUiDispatcher dispatcher, Func<bool> mayPlay,
        Func<SoundCue, bool> waitsForLine)
    {
        _sounds = sounds;
        _picks = picks;
        _time = time;
        _dispatcher = dispatcher;
        _mayPlay = mayPlay;
        _waitsForLine = waitsForLine;
        _timer = time.CreateTimer(_ => _dispatcher.Post(OnTimer), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>A sound plays: the floor is not free for Raven's speech meanwhile.</summary>
    public bool IsPlaying => _playing is not null;

    /// <summary>A sound began to play: what Raven says on its own waits the pause from now.</summary>
    public event EventHandler? Played;

    /// <summary><see cref="IsPlaying"/> changed.</summary>
    public event EventHandler? PlayingChanged;

    /// <summary>Queues the cue, in place of one of its chat still queued; none when its slot is picked None.</summary>
    public void Add(SoundCue cue)
    {
        if (_picks.For(cue.Slot) is null)
        {
            return;
        }

        _queue.RemoveAll(c => c.SessionId == cue.SessionId);
        _queue.Add(cue);
        Kick();
    }

    /// <summary>
    /// The panel's state changed: a floor taken stops the wait, a floor freed starts the grace, once there is something to
    /// play. A sound playing goes on; its end looks again.
    /// </summary>
    public void Kick()
    {
        if (IsPlaying)
        {
            return;
        }

        if (!_mayPlay())
        {
            Stop();
            return;
        }

        if (!_timing && Prune().Any(Playable))
        {
            _timing = true;
            _timer.Change(TrafficWatcher.NewsGrace, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Takes the chat's cues still true out of the queue, for the line Raven speaks for them; the newest is last.</summary>
    public IReadOnlyList<SoundCue> TakeForLine(string sessionId)
    {
        var taken = _queue.Where(c => c.SessionId == sessionId).ToList();
        _queue.RemoveAll(taken.Contains);
        return [.. taken.Where(c => c.StillTrue())];
    }

    /// <summary>Drops every cue, and stops the sound playing: Raven's panel was muted.</summary>
    public void Clear()
    {
        _queue.Clear();
        Stop();
        if (_playing is { } playing)
        {
            _playing = null;
            playing.Cancel();
            PlayingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Stop()
    {
        _timing = false;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The grace or the gap is over: the next sound plays if the floor is still free.</summary>
    private void OnTimer()
    {
        if (!_timing)
        {
            return; // stopped after the timer had fired
        }

        _timing = false;
        if (!IsPlaying && _mayPlay())
        {
            PlayNext();
        }
    }

    private void PlayNext()
    {
        if (Prune().FirstOrDefault(Playable) is not { } cue || _picks.For(cue.Slot) is not { } sound)
        {
            return;
        }

        _queue.Remove(cue);
        var playing = _playing = new CancellationTokenSource();
        PlayingChanged?.Invoke(this, EventArgs.Empty);
        Played?.Invoke(this, EventArgs.Empty);
        _sounds.PlayAsync(sound, playing.Token)
            .ContinueWith(_ => _dispatcher.Post(() => Ended(playing)), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private void Ended(CancellationTokenSource played)
    {
        played.Dispose();
        if (_playing != played)
        {
            return; // cleared while it played
        }

        _playing = null;
        PlayingChanged?.Invoke(this, EventArgs.Empty);
        if (_mayPlay() && Prune().Any(Playable))
        {
            _timing = true;
            _timer.Change(Gap, Timeout.InfiniteTimeSpan);
        }
    }

    private bool Playable(SoundCue cue) => !_waitsForLine(cue);

    /// <summary>The queue without its outdated cues, which are dropped.</summary>
    private List<SoundCue> Prune()
    {
        var now = _time.GetUtcNow();
        _queue.RemoveAll(c => now - c.At > ChatNews.MaximumAge || _picks.For(c.Slot) is null || !c.StillTrue());
        return _queue;
    }
}
