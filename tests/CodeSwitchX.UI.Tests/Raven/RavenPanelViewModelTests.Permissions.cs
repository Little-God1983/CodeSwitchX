using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>
/// A chat's permission prompt waits in the panel: a card with what it wants to do, Allow and Deny, read out as a short line,
/// closed when it is answered here or in the chat's VS Code tab.
/// </summary>
public sealed partial class RavenPanelViewModelTests
{
    private ChatAsk Permitting(string id = "p1", string? agent = null) => new(id,
        new HookEvent { SessionId = "a", EventName = "PermissionRequest", At = _time.GetUtcNow(), ToolName = "Bash", AgentId = agent, ToolInputHash = "npm test" },
        [], new ChatPermission("Bash", "run a command", "npm test", agent is null ? null : "Explore"));

    private static List<ChatAskCard> PermissionCards(RavenPanelViewModel vm) =>
        vm.Log.Where(e => e.Kind == RavenLogKind.Permission).Select(e => e.Ask!).ToList();

    [Fact]
    public async Task A_permission_prompt_is_shown_said_in_a_line_and_allowed_by_a_click()
    {
        var (vm, asks) = await QuestionsVmAsync();

        var held = asks.HoldAsync(Permitting(), CancellationToken.None);
        var card = PermissionCards(vm).ShouldHaveSingleItem();
        vm.OpenQuestions.ShouldBe(1);
        await GraceAsync(vm);
        await Until(() => string.Join(" ", _speech.Spoken).EndsWith("It's on the card.", StringComparison.Ordinal));

        (card.Chat, card.Wants, card.Permission!.Subject).ShouldBe(("ContentAutomatorX · Fix the upload retry", " wants to run a command", "npm test"));
        string.Join(" ", _speech.Spoken).ShouldBe("ContentAutomatorX, chat \"Fix the upload retry\" wants to run a command. It's on the card.");

        vm.AllowCommand.Execute(card);

        await WithinAsync(held);
        (await held).ShouldNotBeNull().Permit.ShouldBe(new ChatPermit(true, null));
        card.IsOpen.ShouldBeFalse();
        card.Outcome.ShouldBe("Allowed.");
        vm.OpenQuestions.ShouldBe(0);
    }

    [Fact]
    public async Task Deny_tells_the_chat_and_lets_it_carry_on()
    {
        var (vm, asks) = await QuestionsVmAsync();
        var held = asks.HoldAsync(Permitting(), CancellationToken.None);
        var card = PermissionCards(vm).ShouldHaveSingleItem();

        vm.DenyCommand.Execute(card);

        await WithinAsync(held);
        (await held).ShouldNotBeNull().Permit.ShouldBe(new ChatPermit(false, ChatAsks.DeniedMessage));
        card.Outcome.ShouldBe("Denied. The chat carries on without it.");
    }

    [Fact]
    public async Task A_prompt_answered_in_VS_Code_closes_its_card()
    {
        var (vm, asks) = await QuestionsVmAsync();
        HookEvent Step(string name) => new() { SessionId = "a", EventName = name, At = _time.GetUtcNow(), ToolName = "Bash", ToolUseId = "toolu_1", ToolInputHash = "npm test" };
        _bus.Publish(new HookEventReceived(Step("PreToolUse")));
        var held = asks.HoldAsync(Permitting(), CancellationToken.None);
        var card = PermissionCards(vm).ShouldHaveSingleItem();

        _time.Advance(TimeSpan.FromSeconds(3));
        _bus.Publish(new HookEventReceived(Step("PostToolUse")));

        await WithinAsync(held);
        card.IsOpen.ShouldBeFalse();
        card.Outcome.ShouldBe("Answered in VS Code.");
        vm.AllowCommand.Execute(card);
        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.AnsweredInVsCode, "a closed card takes no more clicks");
    }

    [Fact]
    public async Task Answer_in_VS_Code_leaves_the_prompt_to_the_chat_s_tab()
    {
        var (vm, asks) = await QuestionsVmAsync();
        var held = asks.HoldAsync(Permitting(), CancellationToken.None);
        var card = PermissionCards(vm).ShouldHaveSingleItem();

        vm.AnswerInVsCodeCommand.Execute(card);

        await WithinAsync(held);
        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.ToVsCode);
        card.Outcome.ShouldBe("Left to VS Code: answer it in the chat's tab.");
    }

    [Fact]
    public async Task A_main_agent_and_a_sub_agent_asking_at_once_get_a_card_each_answered_apart()
    {
        var (vm, asks) = await QuestionsVmAsync();
        var main = asks.HoldAsync(Permitting("p1"), CancellationToken.None);
        var sub = asks.HoldAsync(Permitting("p2", agent: "a1"), CancellationToken.None);
        var cards = PermissionCards(vm);
        cards.Count.ShouldBe(2);
        vm.OpenQuestions.ShouldBe(2);
        (cards[1].Wants, cards[1].WantsLine).ShouldBe(("'s Explore sub-agent wants to run a command", "Its Explore sub-agent wants to run a command"));

        vm.DenyCommand.Execute(cards[1]);

        await WithinAsync(sub);
        main.IsCompleted.ShouldBeFalse();
        cards[0].IsOpen.ShouldBeTrue();
        vm.AllowCommand.Execute(cards[0]);
        (await main).ShouldNotBeNull().Permit!.Allow.ShouldBeTrue();
    }

    [Fact]
    public async Task The_brain_that_acts_is_told_it_cannot_answer_a_permission_prompt()
    {
        _brain.Answer = _ => [new BrainText("Only you can allow that.")];
        var (vm, asks) = await QuestionsVmAsync();
        _ = asks.HoldAsync(Permitting(), CancellationToken.None);
        await GraceAsync(vm);

        Type(vm, "allow it");
        await WithinAsync(vm.PendingAnswers);

        _brain.Asked.ShouldHaveSingleItem().ShouldBe(Told + "ContentAutomatorX, chat \"Fix the upload retry\" (chat id a) asks, and waits for the "
            + "answer here: permission to run a command: npm test. Only the user allows or denies it, on its card or in VS Code; no tool of "
            + "yours can.]\nallow it");
    }

    [Theory]
    [InlineData(ChatAskOutcome.TimedOut, null, "Not answered within 10 minutes: answer it in the chat's tab.")]
    [InlineData(ChatAskOutcome.Gone, null, "Answered in VS Code, or the chat's turn ended.")]
    [InlineData(ChatAskOutcome.Stopped, null, "The chat was stopped.")]
    [InlineData(ChatAskOutcome.Answered, "Run the tests instead.", "Denied: Run the tests instead.")]
    public void A_closed_permission_card_says_how_it_ended(ChatAskOutcome outcome, string? message, string said)
    {
        var permit = outcome == ChatAskOutcome.Answered ? new ChatPermit(false, message) : null;

        RavenPanelViewModel.OutcomeOf(new ChatAskClosed(Permitting(), outcome, null, permit)).ShouldBe(said);
    }
}
