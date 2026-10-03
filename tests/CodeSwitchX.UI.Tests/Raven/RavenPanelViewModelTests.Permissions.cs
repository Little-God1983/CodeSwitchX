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
    private ChatAsk Permitting(string id = "p1", string? agent = null, string tool = "Bash", string wants = "run a command", string subject = "npm test",
        params PermissionRisk[] risks) => new(id,
        new HookEvent { SessionId = "a", EventName = "PermissionRequest", At = _time.GetUtcNow(), ToolName = tool, AgentId = agent, ToolInputHash = subject },
        [], new ChatPermission(tool, wants, subject, agent is null ? null : "Explore", Risks: risks.Length > 0 ? risks : null));

    private const string LongCommand = "$out = Join-Path $PSScriptRoot 'dist'\nRemove-Item $out -Recurse -Force\ndotnet publish -c Release -o $out";

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
        await Until(() => string.Join(" ", _speech.Spoken).EndsWith("npm test.", StringComparison.Ordinal));

        (card.Chat, card.Wants, card.Permission!.Subject).ShouldBe(("ContentAutomatorX · Fix the upload retry", " wants to run a command", "npm test"));
        string.Join(" ", _speech.Spoken).ShouldBe("ContentAutomatorX, chat \"Fix the upload retry\" wants to run npm test.");
        card.RiskLine.ShouldBeNull();

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

    [Fact]
    public async Task A_short_risky_command_is_read_out_whole_with_what_is_risky_in_it()
    {
        var (vm, asks) = await QuestionsVmAsync();

        _ = asks.HoldAsync(Permitting(subject: "rm -rf dist && git push", risks: [PermissionRisk.DeletesFiles, PermissionRisk.Pushes]), CancellationToken.None);
        await GraceAsync(vm);
        await Until(() => string.Join(" ", _speech.Spoken).EndsWith("remote.", StringComparison.Ordinal));

        string.Join(" ", _speech.Spoken).ShouldBe(
            "ContentAutomatorX, chat \"Fix the upload retry\" wants to run rm -rf dist, then git push. It deletes files and pushes to a remote.");
        PermissionCards(vm).Single().RiskLine.ShouldBe("Risky: deletes files and pushes to a remote");
        _teller.Asked.ShouldBeEmpty("a short command needs no wording");
    }

    [Fact]
    public async Task A_long_command_is_said_in_the_teller_s_words_and_the_app_adds_its_risks()
    {
        _teller.Answer = _ => [new BrainText("ContentAutomatorX, chat \"Fix the upload retry\" wants to run a script that "), new BrainText("builds the installer")];
        var (vm, asks) = await QuestionsVmAsync();

        _ = asks.HoldAsync(Permitting(tool: "PowerShell", subject: LongCommand, risks: [PermissionRisk.DeletesFiles]), CancellationToken.None);
        _teller.WarmUps.ShouldBe(1, "its start is hidden in the wait for the floor");
        await GraceAsync(vm);
        await Until(() => string.Join(" ", _speech.Spoken).EndsWith("It's on the card.", StringComparison.Ordinal));

        string.Join(" ", _speech.Spoken).ShouldBe("ContentAutomatorX, chat \"Fix the upload retry\" wants to run a script that builds the installer. "
            + "It deletes files. It's on the card.");
        var question = _teller.Asked.ShouldHaveSingleItem();
        question.ShouldContain("begins \"ContentAutomatorX, chat \"Fix the upload retry\" wants to run\"");
        question.ShouldEndWith(LongCommand);
    }

    [Fact]
    public async Task A_long_command_the_teller_gives_no_words_for_is_said_as_a_long_command_with_its_risks()
    {
        var (vm, asks) = await QuestionsVmAsync();

        _ = asks.HoldAsync(Permitting(tool: "PowerShell", subject: LongCommand, risks: [PermissionRisk.DeletesFiles]), CancellationToken.None);
        await GraceAsync(vm);
        await Until(() => string.Join(" ", _speech.Spoken).EndsWith("It's on the card.", StringComparison.Ordinal));

        string.Join(" ", _speech.Spoken).ShouldBe("ContentAutomatorX, chat \"Fix the upload retry\" wants to run a long command that deletes files. It's on the card.");
    }

    [Fact]
    public async Task A_teller_warmed_up_for_a_long_command_answered_before_it_was_read_rests()
    {
        var (vm, asks) = await QuestionsVmAsync();
        _ = asks.HoldAsync(Permitting(subject: LongCommand), CancellationToken.None);

        vm.DenyCommand.Execute(PermissionCards(vm).Single());
        await GraceAsync(vm);

        await Until(() => _teller.Rests == 1);
        _teller.Asked.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Edit", "edit a file", @"E:\Repos\App\App.xaml.cs", new[] { PermissionRisk.WritesOutsideItsFolder }, "CodeSwitchX, chat \"Fix\" wants to edit App.xaml.cs. It writes outside its folder.")]
    [InlineData("Write", "write a file", "src/notes.md", new[] { PermissionRisk.EmptiesAFile }, "CodeSwitchX, chat \"Fix\" wants to write notes.md. It empties a file.")]
    [InlineData("NotebookEdit", "edit a notebook", "/home/me/a.ipynb", new PermissionRisk[0], "CodeSwitchX, chat \"Fix\" wants to edit the notebook a.ipynb.")]
    [InlineData("Read", "read a file", @"C:\outside\notes.md", new PermissionRisk[0], "CodeSwitchX, chat \"Fix\" wants to read notes.md.")]
    [InlineData("WebFetch", "fetch a web page", "https://www.github.com/x/y", new PermissionRisk[0], "CodeSwitchX, chat \"Fix\" wants to fetch github.com.")]
    [InlineData("WebSearch", "search the web", "dotnet 10 release notes", new PermissionRisk[0], "CodeSwitchX, chat \"Fix\" wants to search the web for dotnet 10 release notes.")]
    [InlineData("mcp__github__create_issue", "use mcp__github__create_issue", "title: Bug", new PermissionRisk[0], "CodeSwitchX, chat \"Fix\" wants to use create issue from github.")]
    [InlineData("Bash", "use Bash", "description: no command", new PermissionRisk[0], "CodeSwitchX, chat \"Fix\" wants to use Bash.")]
    public void Another_tool_is_said_by_its_file_site_or_name(string tool, string wants, string subject, PermissionRisk[] risks, string said)
    {
        var card = new ChatAskCard(Permitting(tool: tool, wants: wants, subject: subject, risks: risks)) { Said = "CodeSwitchX, chat \"Fix\"" };

        PermissionLine.Said(card).ShouldBe(said);
    }

    [Fact]
    public void A_sub_agent_s_permission_prompt_is_said_as_its_own()
    {
        var card = new ChatAskCard(Permitting(agent: "a1", subject: "git fetch; git push --force | tee log", risks: [PermissionRisk.Pushes, PermissionRisk.RewritesHistory]))
        {
            Said = "CodeSwitchX, chat \"Fix\"",
        };

        PermissionLine.Said(card).ShouldBe("CodeSwitchX, chat \"Fix\"'s Explore sub-agent wants to run git fetch, then git push --force piped to tee log. It pushes to a remote and rewrites history.");
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("  \n ", null)]
    [InlineData("A wants to run `the build`\n\nand **tests**", "A wants to run the build and tests. It deletes files. It's on the card.")]
    [InlineData("A wants to run the build!", "A wants to run the build! It deletes files. It's on the card.")]
    public void The_teller_s_words_are_said_as_one_plain_sentence_or_not_at_all(string words, string? said)
    {
        var card = new ChatAskCard(Permitting(subject: LongCommand, risks: [PermissionRisk.DeletesFiles]));

        PermissionLine.WithTellersWords(card, words).ShouldBe(said);
        PermissionLine.WithTellersWords(card, new string('a', PermissionLine.MaxTellerChars + 1)).ShouldBeNull("that is no sentence to say");
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
