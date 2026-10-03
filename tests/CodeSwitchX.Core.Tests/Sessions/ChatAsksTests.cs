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
    public async Task A_stop_ends_what_the_chats_main_agent_asks_and_leaves_a_sub_agent_s_question_to_VS_Code()
    {
        var held = _asks.HoldAsync(Ask(), CancellationToken.None);
        var subAgent = _asks.HoldAsync(Ask(toolUse: "toolu_2") with
        {
            Step = new HookEvent { SessionId = "s1", EventName = "PreToolUse", At = _time.GetUtcNow(), ToolUseId = "toolu_2", AgentId = "agent_1" },
        }, CancellationToken.None);
        var otherChat = _asks.HoldAsync(Ask("s2", "toolu_3"), CancellationToken.None);

        _asks.Stop("s1");

        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.Stopped);
        (await subAgent).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.ToVsCode, "a stop is never handed to a sub-agent, and held it would hold the chat");
        otherChat.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task An_ask_the_window_stopped_keeping_while_it_was_taken_goes_to_VS_Code_unseen()
    {
        ChatAsk? opened = null;
        _asks.Opened += a => opened = a;
        _asks.Keeps = _ => false; // the Cab switched to the chat after Takes said yes, in a Recheck that missed it

        (await _asks.HoldAsync(Ask(), CancellationToken.None)).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.ToVsCode);

        opened.ShouldBeNull();
        _published.ShouldBeEmpty("the chat never waited here");
        _asks.IsHeld("toolu_1").ShouldBeFalse();
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

    private ChatAsk Permission(string id = "p1", string session = "s1", string? agent = null) => new(id, ChatAskKind.Permission,
        new HookEvent { SessionId = session, EventName = "PermissionRequest", At = _time.GetUtcNow(), ToolName = "Bash", AgentId = agent },
        [], new ChatPermission("Bash", "run a command", "npm test", agent is null ? null : "general-purpose"));

    private void Step(string eventName, string session = "s1", string? agent = null, TimeSpan? ago = null) =>
        _bus.Publish(new HookEventReceived(new HookEvent
        {
            SessionId = session, EventName = eventName, At = _time.GetUtcNow() - (ago ?? TimeSpan.Zero), ToolName = "Bash", AgentId = agent,
        }));

    [Fact]
    public async Task A_permission_prompt_is_held_without_a_waiting_of_its_own_and_takes_the_users_allow()
    {
        var held = _asks.HoldAsync(Permission(), CancellationToken.None);

        _asks.Holds("s1").ShouldBeTrue();
        _published.ShouldBeEmpty("its own PermissionRequest shows the chat waiting");

        _asks.Permit("p1", allow: true).ShouldBeTrue();

        var closed = (await held).ShouldNotBeNull();
        (closed.Outcome, closed.Permit).ShouldBe((ChatAskOutcome.Answered, new ChatPermit(true, null)));
        _asks.Permit("p1", allow: false).ShouldBeFalse("it is answered already");
    }

    [Fact]
    public async Task A_deny_tells_the_chat_the_users_words_or_else_that_it_was_denied_here()
    {
        var plain = _asks.HoldAsync(Permission("p1"), CancellationToken.None);
        _asks.Permit("p1", allow: false, "  ");
        (await plain).ShouldNotBeNull().Permit.ShouldBe(new ChatPermit(false, ChatAsks.DeniedMessage));

        var said = _asks.HoldAsync(Permission("p2"), CancellationToken.None);
        _asks.Permit("p2", allow: false, " Run the tests instead. ");
        (await said).ShouldNotBeNull().Permit.ShouldBe(new ChatPermit(false, "Run the tests instead."));
    }

    [Fact]
    public void A_question_is_not_allowed_and_a_permission_prompt_not_answered()
    {
        var question = _asks.HoldAsync(Ask(), CancellationToken.None);
        var permission = _asks.HoldAsync(Permission(session: "s2"), CancellationToken.None);

        Should.Throw<ArgumentException>(() => _asks.Permit("toolu_1", allow: true));
        Should.Throw<ArgumentException>(() => _asks.Answer("p1", ["yes"]));

        question.IsCompleted.ShouldBeFalse();
        permission.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task The_next_step_of_the_agent_that_asked_means_it_was_answered_in_VS_Code()
    {
        // Claude Code does not let the hook go when the prompt is answered in the chat's tab: the tool runs, or the turn goes on.
        var held = _asks.HoldAsync(Permission(), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(5));

        Step("PreToolUse", ago: TimeSpan.FromSeconds(10)); // the step that asks, landing late
        Step("PermissionRequest");
        Step("Notification");
        Step("PostToolUse", agent: "a1");
        Step("PostToolUse", session: "s2");
        held.IsCompleted.ShouldBeFalse();

        Step("PostToolUse");

        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.AnsweredInVsCode);
        _asks.Holds("s1").ShouldBeFalse();
    }

    [Fact]
    public async Task A_main_agent_and_a_sub_agent_each_hold_their_own_prompt()
    {
        var main = _asks.HoldAsync(Permission("p1"), CancellationToken.None);
        var sub = _asks.HoldAsync(Permission("p2", agent: "a1"), CancellationToken.None);
        _asks.Open().Select(a => a.Id).ShouldBe(["p1", "p2"], ignoreOrder: true);

        Step("SubagentStop", agent: "a1");

        (await sub).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.AnsweredInVsCode);
        main.IsCompleted.ShouldBeFalse();
        _asks.Permit("p1", allow: true).ShouldBeTrue();
        (await main).ShouldNotBeNull().Permit!.Allow.ShouldBeTrue();
    }

    [Fact]
    public async Task An_agent_asking_again_was_answered_in_VS_Code_the_time_before()
    {
        // One agent asks one permission at a time: a new prompt means the last one was answered in its tab.
        var first = _asks.HoldAsync(Permission("p1"), CancellationToken.None);
        var second = _asks.HoldAsync(Permission("p2"), CancellationToken.None);

        (await first).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.AnsweredInVsCode);
        second.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task A_permission_prompt_let_go_by_its_hook_tells_no_step_of_its_own()
    {
        using var aborted = new CancellationTokenSource();
        var held = _asks.HoldAsync(Permission(), aborted.Token);

        await aborted.CancelAsync();

        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.Gone);
        _published.ShouldBeEmpty("the chat's own steps tell where it is");
    }

    [Fact]
    public void A_permission_prompt_reads_as_what_it_wants_and_who_asks()
    {
        Permission().Describe().ShouldBe("permission to run a command: npm test");
        Permission(agent: "a1").Describe().ShouldBe("permission to run a command: npm test (its general-purpose sub-agent asks)");
    }
}
