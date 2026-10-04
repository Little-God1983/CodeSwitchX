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

    private ChatAsk Ask(string session = "s1", string toolUse = "toolu_1", int questions = 1) => new(toolUse,
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

    /// <summary>A prompt for running <paramref name="input"/> (its fingerprint); no fingerprint, as from an older relay, for null.</summary>
    private ChatAsk Permission(string id = "p1", string session = "s1", string? agent = null, string? input = "npm test", string tool = "Bash") =>
        new(id,
            new HookEvent { SessionId = session, EventName = "PermissionRequest", At = _time.GetUtcNow(), ToolName = tool, AgentId = agent, ToolInputHash = input },
            [], new ChatPermission(tool, "run a command", input ?? "npm test", agent is null ? null : "general-purpose"));

    /// <summary>A step of a tool use: its PreToolUse or PostToolUse, with the fingerprint of its input.</summary>
    private void Step(string eventName, string? toolUse = null, string session = "s1", string? agent = null, string? input = null, string tool = "Bash",
        TimeSpan? ago = null) =>
        _bus.Publish(new HookEventReceived(new HookEvent
        {
            SessionId = session, EventName = eventName, At = _time.GetUtcNow() - (ago ?? TimeSpan.Zero), ToolName = tool, AgentId = agent,
            ToolUseId = toolUse, ToolInputHash = input,
        }));

    [Fact]
    public async Task A_proposed_allow_runs_nothing_until_the_user_s_yes_confirms_it()
    {
        var ends = new List<(string Id, ChatProposalEnd End)>();
        _asks.ProposalEnded += (p, end) => ends.Add((p.Ask.Id, end));
        ChatAllowProposal? proposed = null;
        _asks.ProposedAllow += p => proposed = p;
        var held = _asks.HoldAsync(Permission(), CancellationToken.None);

        var proposal = _asks.Propose("p1");

        proposed.ShouldBe(proposal);
        (proposal.Ask.Id, proposal.At).ShouldBe(("p1", _time.GetUtcNow()));
        _asks.Proposed.ShouldBe(proposal);
        held.IsCompleted.ShouldBeFalse("a proposal allows nothing");
        _asks.Open().ShouldHaveSingleItem();

        _asks.ProposalFor(_time.GetUtcNow()).ShouldBeNull("nothing answers it before its read-back was heard");
        _asks.IsHeard(proposal).ShouldBeFalse();
        _asks.MarkHeard(proposal, _time.GetUtcNow()).ShouldBeTrue();
        _asks.IsHeard(proposal).ShouldBeTrue();
        _asks.ProposalFor(_time.GetUtcNow()).ShouldBe(proposal);
        _asks.Confirm(proposal).ShouldBeTrue();

        (await held).ShouldNotBeNull().Permit.ShouldBe(new ChatPermit(true, null));
        _asks.Proposed.ShouldBeNull();
        ends.ShouldBe([("p1", ChatProposalEnd.Confirmed)]);
        _asks.Confirm(proposal).ShouldBeFalse("nothing is proposed any more");
    }

    [Fact]
    public void Words_said_before_the_proposal_answer_something_else()
    {
        _ = _asks.HoldAsync(Permission(), CancellationToken.None);
        var before = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromSeconds(2));
        var proposal = _asks.Propose("p1");
        var speaking = proposal.At + TimeSpan.FromSeconds(1);
        _time.Advance(TimeSpan.FromSeconds(3)); // the read-back plays
        var heard = _time.GetUtcNow();
        _asks.MarkHeard(proposal, heard);

        _asks.ProposalFor(before).ShouldBeNull("an earlier \"okay\", still being transcribed, was said to something else");
        _asks.ProposalFor(speaking).ShouldBeNull("a yes said while the read-back still played was said before the app asked for it (round 2)");
        _asks.ProposalFor(heard).ShouldBe(proposal);
    }

    [Fact]
    public async Task A_yes_said_in_time_and_transcribed_after_the_proposal_lapsed_still_allows()
    {
        var ends = new List<ChatProposalEnd>();
        _asks.ProposalEnded += (_, end) => ends.Add(end);
        var held = _asks.HoldAsync(Permission(), CancellationToken.None);
        var proposal = _asks.Propose("p1");
        _asks.MarkHeard(proposal, proposal.At);
        _time.Advance(ChatAsks.ProposalLifetime - TimeSpan.FromSeconds(1));
        var said = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromSeconds(2)); // transcribed after the lapse

        _asks.Proposed.ShouldBeNull();
        _asks.ProposalFor(said).ShouldBe(proposal);
        _asks.ProposalFor(_time.GetUtcNow()).ShouldBeNull("words said after the lapse answer nothing");
        _asks.Confirm(proposal).ShouldBeTrue();

        (await held).ShouldNotBeNull().Permit!.Allow.ShouldBeTrue();
        ends.ShouldBe([ChatProposalEnd.Expired, ChatProposalEnd.Confirmed]);
    }

    [Fact]
    public void A_proposal_whose_timer_fires_early_still_lapses_once_its_time_is_up()
    {
        // The tick-count timer can fire a few milliseconds before the clock says the lifetime is over.
        var time = new LateClock();
        using var asks = new ChatAsks(_bus, time) { Takes = _ => true };
        var ends = new List<ChatProposalEnd>();
        asks.ProposalEnded += (_, end) => ends.Add(end);
        _ = asks.HoldAsync(Permission(), CancellationToken.None);
        var proposal = asks.Propose("p1");
        asks.MarkHeard(proposal, time.GetUtcNow());

        time.Lag = TimeSpan.FromMilliseconds(15);
        time.Advance(ChatAsks.ProposalLifetime);
        asks.Proposed.ShouldNotBeNull("its timer fired, the clock is 15 ms short");
        time.Advance(TimeSpan.FromMilliseconds(15));

        asks.Proposed.ShouldBeNull();
        ends.ShouldBe([ChatProposalEnd.Expired]);
    }

    /// <summary>A clock that reads <see cref="Lag"/> behind the time its timers run on.</summary>
    private sealed class LateClock : FakeTimeProvider
    {
        public TimeSpan Lag { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() - Lag;
    }

    [Fact]
    public async Task The_users_time_for_a_yes_starts_when_the_read_back_was_heard()
    {
        // Round 3: a read-back queued behind other speech must not eat the user's 30 seconds.
        var ends = new List<ChatProposalEnd>();
        _asks.ProposalEnded += (_, end) => ends.Add(end);
        var held = _asks.HoldAsync(Permission(), CancellationToken.None);
        var proposal = _asks.Propose("p1");

        _time.Advance(ChatAsks.ProposalLifetime + TimeSpan.FromSeconds(5)); // still being read back
        _asks.Proposed.ShouldBeSameAs(proposal);
        var heard = _time.GetUtcNow();
        _asks.MarkHeard(proposal, heard).ShouldBeTrue();
        _time.Advance(ChatAsks.ProposalLifetime - TimeSpan.FromSeconds(1));
        _asks.Proposed.ShouldBeSameAs(proposal);
        var yes = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromSeconds(2)); // the yes is transcribed after the lapse

        ends.ShouldBe([ChatProposalEnd.Expired]);
        _asks.ProposalFor(yes).ShouldBeSameAs(proposal, "said within 30 s of the read-back");
        _asks.ProposalFor(heard + ChatAsks.ProposalLifetime + TimeSpan.FromSeconds(1)).ShouldBeNull();
        _asks.Confirm(proposal).ShouldBeTrue();
        (await held).ShouldNotBeNull().Permit!.Allow.ShouldBeTrue();
    }

    [Fact]
    public void A_proposal_whose_read_back_is_never_heard_lapses_after_a_while()
    {
        var ends = new List<ChatProposalEnd>();
        _asks.ProposalEnded += (_, end) => ends.Add(end);
        _ = _asks.HoldAsync(Permission(), CancellationToken.None);
        var proposal = _asks.Propose("p1");

        _time.Advance(ChatAsks.ReadBackLifetime - TimeSpan.FromSeconds(1));
        _asks.Proposed.ShouldBeSameAs(proposal);
        _time.Advance(TimeSpan.FromSeconds(1));

        _asks.Proposed.ShouldBeNull();
        ends.ShouldBe([ChatProposalEnd.Expired]);
        _asks.IsHeard(proposal).ShouldBeFalse();
        _asks.ProposalFor(_time.GetUtcNow()).ShouldBeNull();
    }

    [Fact]
    public async Task Other_words_cancel_the_proposal_and_the_prompt_stays_held()
    {
        var ends = new List<ChatProposalEnd>();
        _asks.ProposalEnded += (_, end) => ends.Add(end);
        var held = _asks.HoldAsync(Permission(), CancellationToken.None);
        var proposal = _asks.Propose("p1");

        _asks.Cancel(proposal).ShouldBeTrue();

        _asks.Proposed.ShouldBeNull();
        held.IsCompleted.ShouldBeFalse();
        ends.ShouldBe([ChatProposalEnd.Cancelled]);
        _asks.Cancel(proposal).ShouldBeFalse();
        _asks.Confirm(proposal).ShouldBeFalse("a yes after the cancel allows nothing");
        _asks.ProposalFor(_time.GetUtcNow()).ShouldBeNull();
        held.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task A_proposal_without_a_yes_lapses_and_the_prompt_stays_held()
    {
        var ends = new List<ChatProposalEnd>();
        _asks.ProposalEnded += (_, end) => ends.Add(end);
        var held = _asks.HoldAsync(Permission(), CancellationToken.None);
        var proposal = _asks.Propose("p1");
        _asks.MarkHeard(proposal, proposal.At);

        _time.Advance(ChatAsks.ProposalLifetime - TimeSpan.FromSeconds(1));
        _asks.Proposed.ShouldNotBeNull();
        _time.Advance(TimeSpan.FromSeconds(1));

        _asks.Proposed.ShouldBeNull();
        ends.ShouldBe([ChatProposalEnd.Expired]);
        held.IsCompleted.ShouldBeFalse();
        _asks.ProposalFor(_time.GetUtcNow() + TimeSpan.FromSeconds(1)).ShouldBeNull();
        _asks.Cancel(proposal).ShouldBeFalse("it lapsed: Cancel only forgets it");
        ends.ShouldBe([ChatProposalEnd.Expired], "a lapsed proposal is not told as cancelled too (round 2)");
    }

    [Fact]
    public async Task A_prompt_that_ends_takes_its_proposal_with_it_and_a_newer_proposal_replaces_the_older()
    {
        var ends = new List<(string Id, ChatProposalEnd End)>();
        _asks.ProposalEnded += (p, end) => ends.Add((p.Ask.Id, end));
        var first = _asks.HoldAsync(Permission("p1"), CancellationToken.None);
        var second = _asks.HoldAsync(Permission("p2", agent: "a1"), CancellationToken.None);

        var older = _asks.Propose("p1");
        var newer = _asks.Propose("p2");
        ends.ShouldBe([("p1", ChatProposalEnd.Replaced)]);
        _asks.Proposed!.Ask.Id.ShouldBe("p2");
        _asks.Confirm(older).ShouldBeFalse("only the newest proposal stands");

        _asks.Permit("p2", allow: false).ShouldBeTrue();

        (await second).ShouldNotBeNull().Permit!.Allow.ShouldBeFalse();
        ends.ShouldBe([("p1", ChatProposalEnd.Replaced), ("p2", ChatProposalEnd.Closed)]);
        _asks.Proposed.ShouldBeNull();
        _asks.Confirm(newer).ShouldBeFalse("the proposal went with its prompt: a yes now allows nothing");
        first.IsCompleted.ShouldBeFalse();
    }

    private static readonly ChatPermissionSuggestion AlwaysNpmTest = new(
        """{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm test"}],"behavior":"allow","destination":"localSettings"}""",
        "Always allow npm test", "in this folder, just you");

    [Fact]
    public async Task An_allow_for_good_takes_one_of_the_prompt_s_own_suggestions()
    {
        var held = _asks.HoldAsync(Permission() with { Suggestions = [AlwaysNpmTest] }, CancellationToken.None);

        _asks.Permit("p1", allow: true, always: AlwaysNpmTest).ShouldBeTrue();

        (await held).ShouldNotBeNull().Permit.ShouldBe(new ChatPermit(true, null, AlwaysNpmTest));
        AlwaysNpmTest.Said.ShouldBe("Always allow npm test in this folder, just you");
    }

    [Fact]
    public void A_rule_the_prompt_did_not_suggest_or_one_with_a_deny_is_refused_and_nothing_is_answered()
    {
        var held = _asks.HoldAsync(Permission() with { Suggestions = [AlwaysNpmTest] }, CancellationToken.None);
        var other = _asks.HoldAsync(Permission("p2", agent: "a1"), CancellationToken.None);
        var foreign = AlwaysNpmTest with { Json = """{"type":"setMode","mode":"bypassPermissions","destination":"session"}""" };

        Should.Throw<ArgumentException>(() => _asks.Permit("p1", allow: true, always: foreign));
        Should.Throw<ArgumentException>(() => _asks.Permit("p1", allow: false, always: AlwaysNpmTest));
        Should.Throw<ArgumentException>(() => _asks.Permit("p2", allow: true, always: AlwaysNpmTest), "that prompt suggested nothing");

        held.IsCompleted.ShouldBeFalse();
        other.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task A_yes_by_voice_allows_once_never_for_good()
    {
        var held = _asks.HoldAsync(Permission() with { Suggestions = [AlwaysNpmTest] }, CancellationToken.None);
        var proposal = _asks.Propose("p1");

        _asks.Confirm(proposal).ShouldBeTrue();

        (await held).ShouldNotBeNull().Permit.ShouldBe(new ChatPermit(true, null), "a standing rule is the click's alone");
    }

    [Fact]
    public void Only_a_held_permission_prompt_can_be_proposed()
    {
        _ = _asks.HoldAsync(Ask(), CancellationToken.None);

        Should.Throw<ArgumentException>(() => _asks.Propose("toolu_1")).Message.ShouldContain("asks a question");
        Should.Throw<ArgumentException>(() => _asks.Propose("p9")).Message.ShouldContain("no longer waits");
        _asks.Proposed.ShouldBeNull();
    }

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
    public async Task The_end_of_the_prompt_s_own_tool_use_means_it_was_allowed_in_VS_Code()
    {
        // Claude Code does not let the hook go when the prompt is answered in the chat's tab: the tool simply runs.
        Step("PreToolUse", "toolu_1", input: "npm test", ago: TimeSpan.FromSeconds(1));
        var held = _asks.HoldAsync(Permission(input: "npm test"), CancellationToken.None);

        Step("PermissionRequest");
        Step("Notification");
        held.IsCompleted.ShouldBeFalse();

        Step("PostToolUse", "toolu_1", input: "npm test");

        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.AnsweredInVsCode);
        _asks.Holds("s1").ShouldBeFalse();
    }

    [Fact]
    public async Task Tools_running_side_by_side_take_their_steps_while_a_prompt_is_held()
    {
        // Grep and WebFetch in one batch: only WebFetch asks, and Grep runs meanwhile. Nobody answered the prompt.
        Step("PreToolUse", "toolu_g", input: "grep TODO", tool: "Grep", ago: TimeSpan.FromSeconds(1));
        Step("PreToolUse", "toolu_w", input: "fetch github.com", tool: "WebFetch", ago: TimeSpan.FromSeconds(1));
        var held = _asks.HoldAsync(Permission(input: "fetch github.com", tool: "WebFetch"), CancellationToken.None);

        Step("PostToolUse", "toolu_g", input: "grep TODO", tool: "Grep");
        Step("PreToolUse", "toolu_r", input: "read App.cs", tool: "Read");
        Step("PostToolUse", "toolu_r", input: "read App.cs", tool: "Read");

        held.IsCompleted.ShouldBeFalse();
        _asks.Permit("p1", allow: true).ShouldBeTrue();
        (await held).ShouldNotBeNull().Permit!.Allow.ShouldBeTrue();
    }

    [Fact]
    public async Task Two_prompts_of_one_agent_at_once_are_each_held_and_closed_apart()
    {
        // Two WebFetch calls side by side ask at the same moment: neither pushes the other out.
        Step("PreToolUse", "toolu_1", input: "fetch a", tool: "WebFetch", ago: TimeSpan.FromSeconds(1));
        Step("PreToolUse", "toolu_2", input: "fetch b", tool: "WebFetch", ago: TimeSpan.FromSeconds(1));
        var first = _asks.HoldAsync(Permission("p1", input: "fetch a", tool: "WebFetch"), CancellationToken.None);
        var second = _asks.HoldAsync(Permission("p2", input: "fetch b", tool: "WebFetch"), CancellationToken.None);
        _asks.Open().Select(a => a.Id).ShouldBe(["p1", "p2"], ignoreOrder: true);

        Step("PostToolUse", "toolu_2", input: "fetch b", tool: "WebFetch");

        (await second).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.AnsweredInVsCode);
        first.IsCompleted.ShouldBeFalse();
        _asks.Permit("p1", allow: false).ShouldBeTrue();
        (await first).ShouldNotBeNull().Permit!.Allow.ShouldBeFalse();
    }

    [Fact]
    public async Task Another_agent_s_or_chat_s_tool_use_of_the_same_input_is_not_the_prompt_s()
    {
        Step("PreToolUse", "toolu_a", agent: "a1", input: "npm test", ago: TimeSpan.FromSeconds(1));
        Step("PreToolUse", "toolu_s", session: "s2", input: "npm test", ago: TimeSpan.FromSeconds(1));
        var held = _asks.HoldAsync(Permission(input: "npm test"), CancellationToken.None);

        Step("PostToolUse", "toolu_a", agent: "a1", input: "npm test");
        Step("PostToolUse", "toolu_s", session: "s2", input: "npm test");

        held.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task The_end_of_the_agent_s_turn_ends_its_prompts()
    {
        // Denied in VS Code with words for the chat, the turn carries on to its end; a prompt from an older relay, which has
        // no fingerprint to match its tool use, ends there too.
        var main = _asks.HoldAsync(Permission("p1", input: null), CancellationToken.None);
        var sub = _asks.HoldAsync(Permission("p2", agent: "a1"), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(2));

        Step("SubagentStop", agent: "a1");
        (await sub).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.Gone);
        main.IsCompleted.ShouldBeFalse();

        Step("Stop");
        (await main).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.Gone);
    }

    [Fact]
    public async Task A_main_agent_and_a_sub_agent_each_hold_their_own_prompt()
    {
        var main = _asks.HoldAsync(Permission("p1"), CancellationToken.None);
        var sub = _asks.HoldAsync(Permission("p2", agent: "a1"), CancellationToken.None);
        _asks.Open().Select(a => a.Id).ShouldBe(["p1", "p2"], ignoreOrder: true);

        _asks.Permit("p2", allow: false).ShouldBeTrue();

        (await sub).ShouldNotBeNull().Permit!.Allow.ShouldBeFalse();
        main.IsCompleted.ShouldBeFalse();
        _asks.Permit("p1", allow: true).ShouldBeTrue();
        (await main).ShouldNotBeNull().Permit!.Allow.ShouldBeTrue();
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

    [Fact]
    public void The_brain_is_shown_the_start_of_a_long_command_the_card_shows_whole()
    {
        var described = Permission(input: new string('x', 1000) + "\nrm -rf /").Describe();

        described.Length.ShouldBeLessThan(ChatAsk.MaxDescribedChars + 80);
        described.ShouldEndWith("… (the card shows all of it)");
    }

    [Fact]
    public async Task A_prompt_whose_tool_use_ended_before_its_hook_got_here_is_not_taken()
    {
        // Answered in VS Code at once: the tool ran and ended before the relay that holds the prompt reached the app.
        Step("PreToolUse", "toolu_1", input: "npm test", ago: TimeSpan.FromSeconds(1));
        Step("PostToolUse", "toolu_1", input: "npm test");

        (await _asks.HoldAsync(Permission(input: "npm test"), CancellationToken.None)).ShouldBeNull();
        _asks.Holds("s1").ShouldBeFalse();

        // The same command run again later is another tool use, and asks again.
        _time.Advance(TimeSpan.FromSeconds(5));
        Step("PreToolUse", "toolu_2", input: "npm test");
        _ = _asks.HoldAsync(Permission("p2", input: "npm test"), CancellationToken.None);
        _asks.Holds("s1").ShouldBeTrue();
    }

    [Fact]
    public async Task The_main_agent_s_stop_leaves_a_background_sub_agent_s_prompt_held()
    {
        // A sub-agent run in the background goes on after the main turn ends, and its prompt still waits.
        var sub = _asks.HoldAsync(Permission("p2", agent: "a1"), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(2));

        Step("Stop");

        sub.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task A_prompt_its_tab_no_longer_shows_was_answered_there()
    {
        // Denied in VS Code with words, the agent carries on: no hook tells it, but the chat's record waits on nobody.
        bool? shows = true;
        _asks.ShowsPrompt = _ => shows;
        var held = _asks.HoldAsync(Permission(input: "rm -rf build"), CancellationToken.None);

        _time.Advance(ChatAsks.SweepEvery * 3);
        held.IsCompleted.ShouldBeFalse("its tab shows the prompt");

        shows = null;
        _time.Advance(ChatAsks.SweepEvery * 3);
        held.IsCompleted.ShouldBeFalse("a record that cannot be read tells nothing");

        shows = false;
        _time.Advance(ChatAsks.SweepEvery);

        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.AnsweredInVsCode);
    }

    [Fact]
    public void A_prompt_just_held_is_not_checked_against_its_tab_yet()
    {
        // Its tab shows it a moment after it is asked.
        _asks.ShowsPrompt = _ => false;
        _ = _asks.HoldAsync(Permission(), CancellationToken.None);

        _time.Advance(ChatAsks.SweepEvery);

        _asks.Holds("s1").ShouldBeTrue();
    }

    [Fact]
    public async Task A_step_told_twice_counts_with_its_fingerprint_whichever_comes_first()
    {
        // An older relay still installed beside this one tells the same PreToolUse without a fingerprint.
        Step("PreToolUse", "toolu_1", input: null, ago: TimeSpan.FromSeconds(1));
        Step("PreToolUse", "toolu_1", input: "npm test", ago: TimeSpan.FromSeconds(1));
        Step("PreToolUse", "toolu_1", input: null, ago: TimeSpan.FromSeconds(1));
        var held = _asks.HoldAsync(Permission(input: "npm test"), CancellationToken.None);

        Step("PostToolUse", "toolu_1");

        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.AnsweredInVsCode);
    }

    [Fact]
    public async Task A_prompt_held_long_keeps_its_tool_use_while_many_others_begin()
    {
        Step("PreToolUse", "toolu_1", input: "npm test", ago: TimeSpan.FromSeconds(1));
        var held = _asks.HoldAsync(Permission(input: "npm test"), CancellationToken.None);
        foreach (var i in Enumerable.Range(0, ChatAsks.BegunKept + 100))
        {
            Step("PreToolUse", $"toolu_other_{i}", session: "s2", input: $"step {i}");
        }

        Step("PostToolUse", "toolu_1", input: "npm test");

        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.AnsweredInVsCode);
    }

    [Fact]
    public void A_wait_that_begins_near_a_held_ask_is_that_ask_s_and_a_later_one_is_another()
    {
        _ = _asks.HoldAsync(Permission("p1", agent: "a1"), CancellationToken.None);
        var asked = _time.GetUtcNow();

        _asks.Explains("s1", asked - TimeSpan.FromSeconds(1)).ShouldBeTrue("its PermissionRequest lands just before the hold");
        _asks.Explains("s1", asked + TimeSpan.FromSeconds(6)).ShouldBeTrue("its Notification comes 6 s later");
        _asks.Explains("s1", asked + TimeSpan.FromMinutes(1)).ShouldBeFalse("a plan to approve, asked later, waits in VS Code");
        _asks.Explains("s2", asked).ShouldBeFalse();
    }

    [Fact]
    public void A_wait_answered_on_its_card_before_its_news_is_told_is_still_that_ask_s()
    {
        // Seen on screen: the PermissionRequest landed before the hold, and the news came round only after Deny was clicked.
        var waitingSince = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromMilliseconds(300));
        _ = _asks.HoldAsync(Permission(), CancellationToken.None);
        _asks.Permit("p1", allow: false).ShouldBeTrue();

        _asks.Explains("s1", waitingSince).ShouldBeTrue();
    }
}
