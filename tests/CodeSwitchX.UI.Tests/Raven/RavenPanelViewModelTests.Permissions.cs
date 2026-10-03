using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Dictation;

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
            + "answer here: permission to run a command: npm test (ask id p1). answer_permission denies it on the user's word, or proposes an allow "
            + "that only the user's next yes, checked by the app, makes real.]\nallow it");
    }

    [Fact]
    public async Task A_yes_after_a_proposed_allow_is_the_app_s_to_find_it_allows_and_goes_to_no_brain()
    {
        var (vm, asks) = await QuestionsVmAsync();
        var held = asks.HoldAsync(Permitting(), CancellationToken.None);
        var card = PermissionCards(vm).ShouldHaveSingleItem();
        await card.Naming;

        asks.Propose("p1"); // as answer_permission does on "allow it"
        card.AwaitsYes.ShouldBeTrue();
        held.IsCompleted.ShouldBeFalse();
        // The app, not the brain, reads it back and asks for the yes (review of #112).
        Lines(vm).ShouldContain((RavenLogKind.Raven, "Run npm test in ContentAutomatorX? Say yes."));
        await Until(() => string.Join(" ", _speech.Spoken).EndsWith("Say yes.", StringComparison.Ordinal));
        Type(vm, "Yes, run it.");

        await WithinAsync(held);
        (await held).ShouldNotBeNull().Permit.ShouldBe(new ChatPermit(true, null));
        (card.IsOpen, card.Outcome, card.AwaitsYes).ShouldBe((false, "Allowed.", false));
        Lines(vm).ShouldContain((RavenLogKind.Raven, "Allowed. The chat carries on."));
        await Until(() => string.Join(" ", _speech.Spoken).EndsWith("Allowed. The chat carries on.", StringComparison.Ordinal));
        _brain.Asked.ShouldBeEmpty("the yes is the app's, not a question");

        Type(vm, "what's next");
        await WithinAsync(vm.PendingAnswers);

        _brain.Asked.ShouldHaveSingleItem().ShouldBe(Told + "ContentAutomatorX, chat \"Fix the upload retry\" (chat id a): the user said yes to the "
            + "allow you proposed, and it was allowed.]\nwhat's next");
    }

    [Fact]
    public async Task Other_words_after_a_proposed_allow_run_nothing_and_go_to_the_brain_which_is_told()
    {
        var (vm, asks) = await QuestionsVmAsync();
        var held = asks.HoldAsync(Permitting(), CancellationToken.None);
        var card = PermissionCards(vm).ShouldHaveSingleItem();
        await card.Naming;
        asks.Propose("p1");

        Type(vm, "what time is it");
        await WithinAsync(vm.PendingAnswers);

        held.IsCompleted.ShouldBeFalse();
        (card.IsOpen, card.AwaitsYes).ShouldBe((true, false));
        asks.Proposed.ShouldBeNull();
        _brain.Asked.ShouldHaveSingleItem().ShouldBe(Told + "ContentAutomatorX, chat \"Fix the upload retry\" (chat id a): the allow you proposed was "
            + "not confirmed by a yes, so nothing ran, and its card stays open.]\nwhat time is it");

        Type(vm, "yes");
        await WithinAsync(vm.PendingAnswers);

        held.IsCompleted.ShouldBeFalse("a yes after the proposal lapsed is only words to the brain");
        _brain.Asked.Last().ShouldBe("yes");
    }

    [Fact]
    public async Task Silence_after_a_proposed_allow_lapses_it_and_the_card_stays_open_for_a_click()
    {
        var (vm, asks) = await QuestionsVmAsync();
        var held = asks.HoldAsync(Permitting(), CancellationToken.None);
        var card = PermissionCards(vm).ShouldHaveSingleItem();
        asks.Propose("p1");

        _time.Advance(ChatAsks.ProposalLifetime);

        (card.IsOpen, card.AwaitsYes).ShouldBe((true, false));
        Lines(vm).ShouldContain((RavenLogKind.Note, "No yes within 30 seconds: nothing ran. The card stays open for a click."));
        held.IsCompleted.ShouldBeFalse();
        vm.AllowCommand.Execute(card);
        (await held).ShouldNotBeNull().Permit!.Allow.ShouldBeTrue();
    }

    [Fact]
    public async Task A_lapsed_proposal_is_told_to_the_brain_with_the_next_question()
    {
        var (vm, asks) = await QuestionsVmAsync();
        _ = asks.HoldAsync(Permitting(), CancellationToken.None);
        await PermissionCards(vm).Single().Naming;
        asks.Propose("p1");
        _time.Advance(ChatAsks.ProposalLifetime);
        _time.Advance(ChatAsks.ProposalLifetime); // words said after the lapse answer nothing

        Type(vm, "what now");
        await WithinAsync(vm.PendingAnswers);

        // The card was read out meanwhile too: that fact goes along, before this one.
        _brain.Asked.ShouldHaveSingleItem().ShouldEndWith("ContentAutomatorX, chat \"Fix the upload retry\" (chat id a): the allow you proposed got no "
            + "yes within 30 seconds, so nothing ran, and its card stays open.]\nwhat now");
    }

    [Fact]
    public async Task Words_said_before_the_proposal_and_transcribed_after_it_answer_something_else()
    {
        var (vm, asks) = await QuestionsVmAsync();
        var held = asks.HoldAsync(Permitting(), CancellationToken.None);
        await PermissionCards(vm).Single().Naming;
        var transcribed = new TaskCompletionSource<DictationResult>();
        Transcribes(transcribed.Task);

        // "Okay." is said to something else, and still being transcribed when the brain proposes the allow.
        vm.PressMic(TalkInput.MicButton);
        Speak();
        _time.Advance(Hold);
        var released = vm.ReleaseMicAsync(TalkInput.MicButton);
        _time.Advance(TimeSpan.FromSeconds(1));
        var proposal = asks.Propose("p1");
        transcribed.SetResult(new DictationResult("Okay.", TimeSpan.FromSeconds(2)));
        await WithinAsync(released);
        await WithinAsync(vm.PendingAnswers);

        held.IsCompleted.ShouldBeFalse("the okay was said before Raven asked for a yes");
        asks.Proposed.ShouldBe(proposal, "and it does not cancel the proposal either");
        _brain.Asked.ShouldBe(["Okay."]);
    }

    [Fact]
    public async Task A_yes_with_a_question_still_waiting_for_the_brain_allows_and_the_question_is_still_asked()
    {
        _brain.BeforeSent = new TaskCompletionSource(); // the brain has not taken the question in yet
        _brain.Answer = _ => [new BrainText("Two.")];
        var (vm, asks) = await QuestionsVmAsync();
        var held = asks.HoldAsync(Permitting(), CancellationToken.None);
        await PermissionCards(vm).Single().Naming;
        asks.Propose("p1");
        Type(vm, "how many chats work?");
        await Until(() => _brain.Asked.Count == 1);
        asks.Propose("p1"); // proposed again: the question cancelled the first one

        Type(vm, "yes");
        _brain.BeforeSent.SetResult();
        await WithinAsync(held);
        await WithinAsync(vm.PendingAnswers);

        (await held).ShouldNotBeNull().Permit!.Allow.ShouldBeTrue();
        var asked = _brain.Asked.Last();
        asked.ShouldEndWith("\nhow many chats work?", Case.Sensitive, "the waiting question is not lost with the floor the yes took");
        asked.ShouldContain("the user said yes to the allow you proposed, and it was allowed");
        Lines(vm).ShouldContain((RavenLogKind.Raven, "Two."));
    }

    [Fact]
    public async Task A_prompt_answered_on_its_card_while_an_allow_is_proposed_takes_the_proposal_with_it()
    {
        var (vm, asks) = await QuestionsVmAsync();
        var held = asks.HoldAsync(Permitting(), CancellationToken.None);
        var card = PermissionCards(vm).ShouldHaveSingleItem();
        asks.Propose("p1");

        vm.DenyCommand.Execute(card);
        await WithinAsync(held);
        Type(vm, "yes");
        await WithinAsync(vm.PendingAnswers);

        (await held).ShouldNotBeNull().Permit!.Allow.ShouldBeFalse();
        card.AwaitsYes.ShouldBeFalse();
        _brain.Asked.ShouldBe(["yes"], "with nothing proposed, a yes is only words to the brain");
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
        question.ShouldContain("finish the sentence \"The chat wants to run …\"");
        question.ShouldEndWith(LongCommand);
    }

    [Fact]
    public async Task A_teller_that_fails_midway_is_not_heard_cut_off_the_fallback_is_said()
    {
        _teller.Answer = _ => [new BrainText("a script that"), new BrainFailed("Raven's teller stopped in the middle of an answer.")];
        var (vm, asks) = await QuestionsVmAsync();

        _ = asks.HoldAsync(Permitting(tool: "PowerShell", subject: LongCommand, risks: [PermissionRisk.DeletesFiles]), CancellationToken.None);
        await GraceAsync(vm);
        await Until(() => string.Join(" ", _speech.Spoken).EndsWith("It's on the card.", StringComparison.Ordinal));

        string.Join(" ", _speech.Spoken).ShouldBe("ContentAutomatorX, chat \"Fix the upload retry\" wants to run a long command that deletes files. It's on the card.");
    }

    [Fact]
    public async Task A_long_command_s_card_that_comes_while_another_is_read_keeps_its_warm_up_and_rests_it_once_answered()
    {
        _teller.Gate = new TaskCompletionSource();
        _teller.Answer = _ => [new BrainText("a script that builds the installer")];
        var (vm, asks) = await QuestionsVmAsync();
        _ = asks.HoldAsync(Permitting("p1", subject: LongCommand), CancellationToken.None);
        await Until(() => vm.State == RavenState.Idle);
        _time.Advance(RavenPanelViewModel.NewsGrace); // not GraceAsync: the teller is held, and with it what the panel waits for
        await Until(() => _teller.Asked.Count == 1);

        _ = asks.HoldAsync(Permitting("p2", subject: LongCommand + "\nexit 0"), CancellationToken.None);
        _teller.WarmUps.ShouldBe(2);
        vm.DenyCommand.Execute(PermissionCards(vm)[1]);
        _teller.Rests.ShouldBe(0, "the teller is busy with the first card");
        _teller.Gate.SetResult();

        await Until(() => string.Join(" ", _speech.Spoken).EndsWith("It's on the card.", StringComparison.Ordinal));
        await Until(() => _teller.Rests == 1);
        _teller.Asked.Count.ShouldBe(1, "the second card was answered before it was read out");
    }

    [Fact]
    public void A_PowerShell_command_s_quotes_and_escapes_are_read_as_PowerShell_s()
    {
        PermissionLine.CommandSaid("Write-Host \"a`\"|b\" | Out-Null", ShellDialect.PowerShell).ShouldBe("Write-Host \"a`\"|b\" piped to Out-Null");
    }

    [Fact]
    public async Task A_long_command_s_card_answered_while_news_is_told_rests_the_teller_it_warmed_up()
    {
        _teller.Gate = new TaskCompletionSource();
        _teller.Answer = _ => [new BrainText("Release notes is done.")];
        _yard.Show("b", "CodeSwitchX", "Release notes");
        var asks = new ChatAsks(_bus, _time) { Takes = _ => true };
        var news = new ChatNews(_bus, _yard, _time, _ => null, askedHere: asks.Explains);
        _time.Advance(TimeSpan.FromSeconds(1));
        var (vm, _) = await QuestionsVmAsyncWith(asks, news);
        Changes("b", SessionState.Working, SessionState.Idle);
        await Until(() => vm.State == RavenState.Idle);
        _time.Advance(RavenPanelViewModel.NewsGrace);
        await Until(() => _teller.Asked.Count == 1);

        _ = asks.HoldAsync(Permitting(subject: LongCommand), CancellationToken.None);
        vm.DenyCommand.Execute(PermissionCards(vm).Single());
        var rests = _teller.Rests;
        _teller.Gate.SetResult();

        await Until(() => _teller.Rests == rests + 1);
        _teller.Asked.Count.ShouldBe(1, "the card was answered before it was read out");
    }

    [Theory]
    [InlineData("npm ci && npm test", "npm ci, then npm test")]
    [InlineData("cd src; make || echo failed", "cd src, then make, or else echo failed")]
    [InlineData("git log | head -5", "git log piped to head -5")]
    [InlineData("git commit -m \"fix; tidy\"", "git commit -m \"fix; tidy\"")]
    [InlineData("grep -E 'foo|bar' log.txt | wc -l", "grep -E 'foo|bar' log.txt piped to wc -l")]
    [InlineData("echo it's done; ls", "echo it's done, then ls")]
    [InlineData("echo it's done; echo that's it", "echo it's done, then echo that's it")]
    [InlineData("echo \"a \\\"x|y\\\" b\" | wc", "echo \"a \\\"x|y\\\" b\" piped to wc")]
    [InlineData("find . -exec rm {} \\; | wc", "find . -exec rm {} \\; piped to wc")]
    public void A_command_s_separators_are_said_as_words_but_not_inside_quotes(string command, string said)
    {
        PermissionLine.CommandSaid(command).ShouldBe(said);
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
    [InlineData("a script that `builds` the **installer**\n\nand tests it", "CodeSwitchX, chat \"Fix\" wants to run a script that builds the installer and tests it.")]
    [InlineData("run a script that builds the installer.", "CodeSwitchX, chat \"Fix\" wants to run a script that builds the installer.")]
    [InlineData("The chat wants to run the build. Then it tests v1.2 too.", "CodeSwitchX, chat \"Fix\" wants to run the build.")]
    [InlineData("A script that builds the installer.", "CodeSwitchX, chat \"Fix\" wants to run a script that builds the installer.")]
    [InlineData("3 scripts that build the installer", "CodeSwitchX, chat \"Fix\" wants to run 3 scripts that build the installer.")]
    [InlineData("Sure.", null)]
    [InlineData("Sure thing.", null)]
    [InlineData("Okay, done.", null)]
    [InlineData("Sure: a script that builds the installer.", null)]
    [InlineData("This command cleans the build output and re-runs the tests. It is safe.",
        "CodeSwitchX, chat \"Fix\" wants to run a long command. This command cleans the build output and re-runs the tests.")]
    public void The_teller_s_words_are_said_after_who_asks_as_a_few_plain_words_or_not_at_all(string words, string? said)
    {
        var card = new ChatAskCard(Permitting(subject: LongCommand, risks: [PermissionRisk.DeletesFiles])) { Said = "CodeSwitchX, chat \"Fix\"" };

        PermissionLine.WithTellersWords(card, words).ShouldBe(said is null ? null : said + " It deletes files. It's on the card.");
        PermissionLine.WithTellersWords(card, string.Join(' ', Enumerable.Repeat("word", PermissionLine.MaxTellerWords + 1)))
            .ShouldBeNull("that is no few words to say");
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
