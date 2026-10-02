using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Core.Tests.Sessions;

public sealed class ChatAsksTests : IDisposable
{
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly FakeTimeProvider _time = new();
    private readonly ChatAsks _asks;
    private readonly List<HookEvent> _published = [];
    private readonly List<ChatAskClosed> _closed = [];

    public ChatAsksTests()
    {
        _asks = new ChatAsks(_bus, _time) { Takes = _ => true };
        _bus.Subscribe<HookEventReceived>(m => _published.Add(m.Event));
        _asks.Closed += _closed.Add;
    }

    public void Dispose() => _asks.Dispose();

    private ChatAsk Ask(string session = "s1", string toolUse = "toolu_1", int questions = 1) => new(toolUse, ChatAskKind.Question,
        new HookEvent { SessionId = session, EventName = "PreToolUse", At = _time.GetUtcNow(), ToolName = "AskUserQuestion", ToolUseId = toolUse },
        Enumerable.Range(1, questions).Select(i => new ChatQuestion($"Question {i}?", null,
            [new ChatQuestionOption("Apple", null), new ChatQuestionOption("Banana", "yellow")], false)).ToList());

    private void Turn(string session, SessionState state) => _bus.Publish(new SessionChanged(null, new SessionSnapshot
    {
        SessionId = session, State = state, StartedAt = _time.GetUtcNow(), LastEventAt = _time.GetUtcNow(), StateSince = _time.GetUtcNow(),
    }));

    [Fact]
    public async Task An_ask_not_taken_goes_to_VS_Code_at_once_and_changes_nothing()
    {
        _asks.Takes = _ => false;

        (await _asks.HoldAsync(Ask(), CancellationToken.None)).ShouldBeNull();

        _published.ShouldBeEmpty();
        _asks.Open().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_window_that_cannot_say_leaves_the_ask_to_VS_Code()
    {
        _asks.Takes = _ => throw new InvalidOperationException("the window is gone");

        (await _asks.HoldAsync(Ask(), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_held_ask_shows_the_chat_waiting_and_gets_the_answers_given_here()
    {
        ChatAsk? opened = null;
        _asks.Opened += a => opened = a;

        var held = _asks.HoldAsync(Ask(), CancellationToken.None);

        opened.ShouldNotBeNull().Id.ShouldBe("toolu_1");
        _asks.Holds("s1").ShouldBeTrue();
        var waiting = _published.ShouldHaveSingleItem();
        (waiting.EventName, waiting.Signal, waiting.ToolUseId, waiting.Message).ShouldBe(("PermissionRequest", SessionSignal.Notification, "toolu_1", "Question 1?"));
        held.IsCompleted.ShouldBeFalse();

        _asks.Answer("toolu_1", [" Banana "]).ShouldBeTrue();

        (await held).ShouldNotBeNull().Answers.ShouldBe(["Banana"]);
        _asks.Holds("s1").ShouldBeFalse();
        _closed.ShouldHaveSingleItem().Outcome.ShouldBe(ChatAskOutcome.Answered);
        _asks.Answer("toolu_1", ["Apple"]).ShouldBeFalse("it is answered already");
    }

    [Fact]
    public async Task Answers_must_be_one_per_question_and_none_blank()
    {
        var held = _asks.HoldAsync(Ask(questions: 2), CancellationToken.None);

        Should.Throw<ArgumentException>(() => _asks.Answer("toolu_1", ["Apple"]));
        Should.Throw<ArgumentException>(() => _asks.Answer("toolu_1", ["Apple", " "]));
        held.IsCompleted.ShouldBeFalse("a wrong answer leaves it open");

        _asks.Answer("toolu_1", ["Apple", "my own words"]).ShouldBeTrue();
        (await held).ShouldNotBeNull().Answers.ShouldBe(["Apple", "my own words"]);
    }

    [Fact]
    public async Task Left_to_VS_Code_it_is_asked_there()
    {
        var held = _asks.HoldAsync(Ask(), CancellationToken.None);

        _asks.ToVsCode("toolu_1").ShouldBeTrue();

        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.ToVsCode);
        _closed.ShouldHaveSingleItem().Outcome.ShouldBe(ChatAskOutcome.ToVsCode);
        _published.Count.ShouldBe(1, "VS Code's form keeps the chat waiting: nothing ends it here");
    }

    [Fact]
    public async Task Unanswered_for_its_lifetime_it_goes_to_VS_Code()
    {
        var held = _asks.HoldAsync(Ask(), CancellationToken.None);

        _time.Advance(ChatAsks.Lifetime);

        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.TimedOut);
        _closed.ShouldHaveSingleItem().Outcome.ShouldBe(ChatAskOutcome.TimedOut);
    }

    [Fact]
    public async Task A_hook_let_go_ends_the_ask_and_the_waiting_it_showed()
    {
        using var hook = new CancellationTokenSource();
        var held = _asks.HoldAsync(Ask(), hook.Token);

        await hook.CancelAsync();

        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.Gone);
        _closed.ShouldHaveSingleItem().Outcome.ShouldBe(ChatAskOutcome.Gone);
        var letGo = _published[^1];
        (letGo.EventName, letGo.Signal, letGo.ToolUseId).ShouldBe(("PostToolUse", SessionSignal.ToolUse, "toolu_1"));
    }

    [Theory]
    [InlineData(SessionState.Idle)]
    [InlineData(SessionState.Ended)]
    [InlineData(SessionState.Errored)]
    public async Task A_chat_whose_turn_is_over_asks_nothing_any_more(SessionState state)
    {
        var held = _asks.HoldAsync(Ask(), CancellationToken.None);
        var other = _asks.HoldAsync(Ask("s2", "toolu_2"), CancellationToken.None);

        Turn("s1", SessionState.Waiting);
        held.IsCompleted.ShouldBeFalse("waiting is the ask itself");
        Turn("s1", state);

        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.Gone);
        _closed.ShouldHaveSingleItem().Outcome.ShouldBe(ChatAskOutcome.Gone);
        other.IsCompleted.ShouldBeFalse();
        _asks.Open().ShouldHaveSingleItem().SessionId.ShouldBe("s2");
    }

    [Fact]
    public async Task An_answer_given_as_the_hook_gives_up_still_goes_to_the_chat()
    {
        using var hook = new CancellationTokenSource();
        var held = _asks.HoldAsync(Ask(), hook.Token);
        // Run before the hold's own wait sees the cancel (callbacks run last registered first): the answer wins the race.
        hook.Token.Register(() => _asks.Answer("toolu_1", ["Banana"]));

        await hook.CancelAsync();

        var closed = (await held).ShouldNotBeNull();
        closed.Outcome.ShouldBe(ChatAskOutcome.Answered);
        closed.Answers.ShouldBe(["Banana"]);
        _closed.ShouldHaveSingleItem().Outcome.ShouldBe(ChatAskOutcome.Answered, "the card said it was answered");
    }

    [Fact]
    public async Task A_stop_ends_what_the_chats_main_agent_asks_and_leaves_the_rest()
    {
        var held = _asks.HoldAsync(Ask(), CancellationToken.None);
        var subAgent = _asks.HoldAsync(Ask(toolUse: "toolu_2") with
        {
            Step = new HookEvent { SessionId = "s1", EventName = "PreToolUse", At = _time.GetUtcNow(), ToolUseId = "toolu_2", AgentId = "agent_1" },
        }, CancellationToken.None);
        var otherChat = _asks.HoldAsync(Ask("s2", "toolu_3"), CancellationToken.None);

        _asks.Stop("s1");

        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.Stopped);
        subAgent.IsCompleted.ShouldBeFalse("a stop is never handed to a sub-agent");
        otherChat.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task A_window_that_no_longer_keeps_an_ask_lets_it_go_to_VS_Code()
    {
        var kept = _asks.HoldAsync(Ask(), CancellationToken.None);
        var shownInCab = _asks.HoldAsync(Ask("s2", "toolu_2"), CancellationToken.None);
        _asks.Recheck();
        kept.IsCompleted.ShouldBeFalse("nothing changed for it");

        _asks.Keeps = ask => ask.SessionId != "s2";
        _asks.Recheck();

        (await shownInCab).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.ToVsCode);
        kept.IsCompleted.ShouldBeFalse();

        _asks.Keeps = _ => throw new InvalidOperationException("the window is gone");
        _asks.Recheck();
        kept.IsCompleted.ShouldBeFalse("a window that cannot say leaves it where it is");
    }

    [Fact]
    public async Task The_same_step_asked_again_replaces_the_one_before()
    {
        var first = _asks.HoldAsync(Ask(), CancellationToken.None);
        var second = _asks.HoldAsync(Ask(), CancellationToken.None);

        (await first).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.Gone);
        _asks.Answer("toolu_1", ["Apple"]).ShouldBeTrue();
        (await second).ShouldNotBeNull().Answers.ShouldBe(["Apple"]);
    }
}
