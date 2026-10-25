using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>#306: what chats do becomes sound cues, each true only as long as what it announces still holds.</summary>
public sealed class ChatSoundCuesTests : IDisposable
{
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeYardDirectory _yard = new();
    private readonly ChatNews _news;
    private readonly ChatSoundCues _cues;
    private readonly List<SoundCue> _heard = [];
    private readonly Guid _workspace = Guid.NewGuid();

    public ChatSoundCuesTests()
    {
        _news = new ChatNews(_bus, _yard, _time, _ => null, stoppedOnPurpose: id => id == "stopped", askedHere: (id, _) => id == "held");
        _cues = new ChatSoundCues(_bus, _news, _time);
        _cues.Cue += (_, cue) => _heard.Add(cue);
        _time.Advance(TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        _cues.Dispose();
        _news.Dispose();
    }

    private SessionSnapshot Chat(string id, SessionState state, DateTimeOffset? turn = null, bool failed = false) =>
        ChatNewsTests.Chat(id, state, _time.GetUtcNow()) with { WorkspaceId = _workspace, TurnStartedAt = turn, TurnFailed = failed };

    private void Change(SessionSnapshot from, SessionSnapshot to)
    {
        _bus.Publish(new SessionChanged(from, to));
        _time.Advance(TimeSpan.FromSeconds(1));
    }

    private IEnumerable<SoundCue> Of(SoundSlot slot) => _heard.Where(c => c.Slot == slot);

    [Fact]
    public void A_prompt_cues_starts_working_once_per_turn()
    {
        var first = _time.GetUtcNow();
        Change(Chat("a", SessionState.Idle), Chat("a", SessionState.Working, first));
        Change(Chat("a", SessionState.Working, first), Chat("a", SessionState.Waiting, first));
        Change(Chat("a", SessionState.Waiting, first), Chat("a", SessionState.Working, first));
        var second = _time.GetUtcNow();
        Change(Chat("a", SessionState.Waiting, first), Chat("a", SessionState.Working, second)); // a new prompt after the idle notice

        Of(SoundSlot.StartsWorking).Select(c => (c.SessionId, c.WorkspaceId, c.Line)).ShouldBe([
            ("a", (Guid?)_workspace, SoundLine.None),
            ("a", _workspace, SoundLine.None),
        ]);
    }

    [Fact]
    public void A_start_is_no_longer_true_once_the_chat_stops_working()
    {
        var turn = _time.GetUtcNow();
        Change(Chat("a", SessionState.Idle), Chat("a", SessionState.Working, turn));
        var start = Of(SoundSlot.StartsWorking).Single();
        start.StillTrue().ShouldBeTrue();

        Change(Chat("a", SessionState.Working, turn), Chat("a", SessionState.Idle, turn));

        start.StillTrue().ShouldBeFalse();
    }

    [Fact]
    public void Finished_failed_and_needs_you_news_cue_their_slots()
    {
        Change(Chat("a", SessionState.Working), Chat("a", SessionState.Idle));
        Change(Chat("b", SessionState.Working), Chat("b", SessionState.Idle, failed: true));
        Change(Chat("c", SessionState.Working), Chat("c", SessionState.Waiting));

        _heard.Select(c => (c.Slot, c.SessionId, c.Line)).ShouldBe([
            (SoundSlot.Finishes, "a", SoundLine.News),
            (SoundSlot.Fails, "b", SoundLine.News),
            (SoundSlot.AsksQuestion, "c", SoundLine.News),
        ]);
        _heard.ShouldAllBe(c => c.WorkspaceId == _workspace && c.StillTrue());
    }

    /// <summary>The news is raised while the bus still tells the change: its cue holds from the moment it is raised.</summary>
    [Fact]
    public void A_news_cue_is_true_when_it_is_raised()
    {
        var trueWhenRaised = new List<bool>();
        _cues.Cue += (_, cue) => trueWhenRaised.Add(cue.StillTrue());

        Change(Chat("a", SessionState.Working), Chat("a", SessionState.Idle));
        Change(Chat("b", SessionState.Working), Chat("b", SessionState.Waiting));

        trueWhenRaised.ShouldBe([true, true]);
    }

    [Fact]
    public void A_finish_is_no_longer_true_once_the_chat_works_again()
    {
        Change(Chat("a", SessionState.Working), Chat("a", SessionState.Idle));
        var finish = Of(SoundSlot.Finishes).Single();

        Change(Chat("a", SessionState.Idle), Chat("a", SessionState.Working, _time.GetUtcNow()));

        finish.StillTrue().ShouldBeFalse();
    }

    [Fact]
    public void Needs_you_is_no_longer_true_once_it_stops_waiting()
    {
        Change(Chat("a", SessionState.Working), Chat("a", SessionState.Waiting));
        var waits = Of(SoundSlot.AsksQuestion).Single();

        Change(Chat("a", SessionState.Waiting), Chat("a", SessionState.Working));

        waits.StillTrue().ShouldBeFalse();
    }

    [Fact]
    public void A_turn_raven_stopped_and_a_question_held_here_give_no_news_cue()
    {
        Change(Chat("stopped", SessionState.Working), Chat("stopped", SessionState.Idle));
        Change(Chat("held", SessionState.Working), Chat("held", SessionState.Waiting));

        _heard.ShouldBeEmpty();
    }
}
