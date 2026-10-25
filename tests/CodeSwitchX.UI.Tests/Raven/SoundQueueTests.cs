using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Audio;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>#306: the chats' sounds play one after another, when the floor is free, and only while still true.</summary>
public sealed class SoundQueueTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly HeldSounds _sounds = new();
    private readonly SoundPicks _picks = new();
    private bool _free = true;
    private Func<SoundCue, bool> _waitsForLine = _ => false;
    private TimeSpan _busyFor = TimeSpan.Zero;
    private readonly SoundQueue _queue;

    public SoundQueueTests() =>
        _queue = new SoundQueue(_sounds, _picks, _time, new ImmediateDispatcher(), () => _free, cue => _waitsForLine(cue), () => _busyFor);

    private SoundCue Cue(string session, SoundSlot slot = SoundSlot.Finishes, Func<bool>? stillTrue = null) =>
        new(slot, session, null, _time.GetUtcNow(), SoundLine.News, stillTrue ?? (() => true));

    private void Advance(double milliseconds) => _time.Advance(TimeSpan.FromMilliseconds(milliseconds));

    private void Grace() => _time.Advance(TrafficWatcher.NewsGrace);

    [Fact]
    public void Nothing_plays_until_the_floor_has_been_free_for_the_grace()
    {
        _queue.Add(Cue("a"));
        Advance(1400);
        _sounds.Plays.ShouldBeEmpty();

        Advance(100);

        _sounds.Ids.ShouldBe(["marimba-rise"]);
    }

    [Fact]
    public void Sounds_play_one_after_another_with_the_gap()
    {
        _queue.Add(Cue("a"));
        _queue.Add(Cue("b", SoundSlot.Fails));
        Grace();
        _queue.IsPlaying.ShouldBeTrue();

        _sounds.Finish();
        _queue.IsPlaying.ShouldBeFalse();
        Advance(299);
        _sounds.Plays.Count.ShouldBe(1);
        Advance(1);

        _sounds.Ids.ShouldBe(["marimba-rise", "marimba-fall"]);
    }

    [Fact]
    public void A_busy_floor_holds_the_rest_and_the_grace_starts_again()
    {
        _queue.Add(Cue("a"));
        _queue.Add(Cue("b", SoundSlot.Fails));
        Grace();
        _free = false;
        _queue.Kick();
        _sounds.Finish();
        Advance(5000);
        _sounds.Plays.Count.ShouldBe(1, "the floor is busy");

        _free = true;
        _queue.Kick();
        Advance(1400);
        _sounds.Plays.Count.ShouldBe(1, "the grace is not over");
        Advance(100);

        _sounds.Ids.ShouldBe(["marimba-rise", "marimba-fall"]);
    }

    /// <summary>Review of #306: Raven listening for the words after its name holds the floor by time alone, which no state change ends.</summary>
    [Fact]
    public void A_floor_busy_by_time_alone_is_looked_at_again_when_that_time_ends()
    {
        _free = false;
        _busyFor = TimeSpan.FromSeconds(10);
        _queue.Add(Cue("a"));

        _time.Advance(TimeSpan.FromSeconds(9));
        _free = true;
        _busyFor = TimeSpan.Zero;
        _time.Advance(TimeSpan.FromSeconds(1));
        _sounds.Plays.ShouldBeEmpty("the grace starts when the floor is free");
        Grace();

        _sounds.Ids.ShouldBe(["marimba-rise"]);
    }

    [Fact]
    public void A_newer_cue_of_the_same_chat_replaces_the_older()
    {
        _queue.Add(Cue("a", SoundSlot.StartsWorking));
        _queue.Add(Cue("a"));
        Grace();
        _sounds.Finish();
        Advance(1000);

        _sounds.Ids.ShouldBe(["marimba-rise"]);
    }

    [Fact]
    public void A_cue_no_longer_true_is_dropped_when_its_turn_comes()
    {
        var holds = true;
        _queue.Add(Cue("a", stillTrue: () => holds));
        _queue.Add(Cue("b", SoundSlot.Fails));
        holds = false;

        Grace();

        _sounds.Ids.ShouldBe(["marimba-fall"]);
    }

    [Fact]
    public void A_cue_older_than_two_minutes_is_dropped()
    {
        _free = false;
        _queue.Add(Cue("a"));
        _time.Advance(ChatNews.MaximumAge + TimeSpan.FromSeconds(1));
        _free = true;
        _queue.Kick();

        Grace();

        _sounds.Plays.ShouldBeEmpty();
    }

    [Fact]
    public void A_slot_set_to_none_after_it_queued_is_dropped()
    {
        _queue.Add(Cue("a"));
        _picks.Set(SoundSlot.Finishes, null);

        Grace();

        _sounds.Plays.ShouldBeEmpty();
    }

    [Fact]
    public void A_cue_waiting_for_its_line_is_passed_over_and_taken_for_it()
    {
        _waitsForLine = cue => cue.SessionId == "own";
        var own = Cue("own");
        _queue.Add(own);
        _queue.Add(Cue("other", SoundSlot.Fails));
        Grace();

        _sounds.Ids.ShouldBe(["marimba-fall"]);
        _queue.TakeForLine("own").ShouldBe([own]);
        _sounds.Finish();
        Advance(5000);
        _sounds.Plays.Count.ShouldBe(1, "taken for its line, it never plays from the queue");
    }

    [Fact]
    public void A_cue_that_stops_waiting_for_its_line_plays_from_the_queue()
    {
        var waits = true;
        _waitsForLine = _ => waits;
        _queue.Add(Cue("own"));
        Grace();
        _sounds.Plays.ShouldBeEmpty();

        waits = false; // the user switched to another chat
        _queue.Kick();
        Grace();

        _sounds.Ids.ShouldBe(["marimba-rise"]);
    }

    [Fact]
    public void Ten_cues_play_one_at_a_time_and_clear_stops_the_rest()
    {
        for (var i = 0; i < 10; i++)
        {
            _queue.Add(Cue($"s{i}"));
        }

        Grace();
        for (var played = 1; played < 3; played++)
        {
            _sounds.Unfinished.ShouldBe(1);
            _sounds.Finish();
            Advance(SoundQueue.Gap.TotalMilliseconds);
        }

        _sounds.Unfinished.ShouldBe(1);
        _queue.Clear();

        _sounds.Plays.Last().Ct.IsCancellationRequested.ShouldBeTrue();
        _queue.IsPlaying.ShouldBeFalse();
        Advance(10_000);
        _sounds.Plays.Count.ShouldBe(3);
    }

    [Fact]
    public void Played_is_raised_as_each_sound_starts()
    {
        var played = 0;
        _queue.Played += (_, _) => played++;
        _queue.Add(Cue("a"));
        _queue.Add(Cue("b"));

        Grace();
        played.ShouldBe(1);
        _sounds.Finish();
        Advance(SoundQueue.Gap.TotalMilliseconds);

        played.ShouldBe(2);
    }

    [Fact]
    public void A_slot_picked_none_queues_nothing()
    {
        _picks.Set(SoundSlot.StartsWorking, null);

        _queue.Add(Cue("a", SoundSlot.StartsWorking));
        _picks.Set(SoundSlot.StartsWorking, "wood-knock");
        Grace();

        _sounds.Plays.ShouldBeEmpty();
    }

    /// <summary>Holds each play until the test finishes it; a cancelled play is done at once, as the real one.</summary>
    internal sealed class HeldSounds : IChatSounds
    {
        public List<(string Id, TaskCompletionSource Done, CancellationToken Ct)> Plays { get; } = [];

        public IEnumerable<string> Ids => Plays.Select(p => p.Id);

        public int Unfinished => Plays.Count(p => !p.Done.Task.IsCompleted);

        public Task PlayAsync(string soundId, CancellationToken ct)
        {
            var done = new TaskCompletionSource();
            ct.Register(() => done.TrySetResult());
            Plays.Add((soundId, done, ct));
            return done.Task;
        }

        public void Finish() => Plays.Last().Done.TrySetResult();

        public TimeSpan LengthOf(string soundId) => TimeSpan.FromSeconds(1);
    }
}
