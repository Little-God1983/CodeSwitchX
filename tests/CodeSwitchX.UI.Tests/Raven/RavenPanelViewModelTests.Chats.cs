using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Speech;
using CodeSwitchX.Voice.Dictation;
using NSubstitute;
using CodeSwitchX.Core.Yard;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>
/// One Raven chat per window (#120): each entry is in its window's chat, chat 0 (the Yard) holds what belongs to no single
/// window, and Activity shows everything in time order.
/// </summary>
public sealed partial class RavenPanelViewModelTests
{
    private static readonly Guid ContentAutomatorX = FakeYardDirectory.WorkspaceOf("ContentAutomatorX");
    private static readonly Guid CodeSwitchX = FakeYardDirectory.WorkspaceOf("CodeSwitchX");

    private async Task<(RavenPanelViewModel Vm, ChatAsks Asks)> ChatsVmAsync()
    {
        _yard.Show("a", "ContentAutomatorX", "Fix the upload retry");
        _yard.Show("b", "CodeSwitchX", "Release notes");
        var asks = new ChatAsks(_bus, _time) { Takes = _ => true };
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, asks: asks, yard: _yard);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        return (vm, asks);
    }

    private ChatAsk PermittingIn(string session, string id) => new(id,
        new HookEvent { SessionId = session, EventName = "PermissionRequest", At = _time.GetUtcNow(), ToolName = "Bash", ToolInputHash = id },
        [], new ChatPermission("Bash", "run a command", "npm test", null));

    private static RavenChat ChatNumbered(RavenPanelViewModel vm, int number) => vm.Chats.Single(c => !c.IsActivity && c.Number == number);

    [Fact]
    public void The_list_is_the_yard_then_each_workspace_by_number_then_activity()
    {
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance);

        vm.SetWorkspaces([(ContentAutomatorX, 3, "ContentAutomatorX"), (CodeSwitchX, 1, "CodeSwitchX")]);

        vm.Chats.Select(c => c.Label).ShouldBe(["0 Yard", "1 CodeSwitchX", "3 ContentAutomatorX", "Activity"]);
        vm.SelectedChat.ShouldBe(vm.YardChat);
    }

    [Fact]
    public async Task Each_window_s_permission_card_is_in_its_own_chat_only_and_activity_lists_both()
    {
        var (vm, asks) = await ChatsVmAsync();

        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        _ = asks.HoldAsync(PermittingIn("b", "p2"), CancellationToken.None);

        vm.Shown.ShouldBeEmpty("the Yard's chat has neither");
        vm.SelectedChat = ChatNumbered(vm, 3);
        vm.Shown.ShouldHaveSingleItem().Ask!.Ask.SessionId.ShouldBe("a");
        vm.SelectedChat = ChatNumbered(vm, 1);
        vm.Shown.ShouldHaveSingleItem().Ask!.Ask.SessionId.ShouldBe("b");
        vm.SelectedChat = vm.ActivityChat;
        vm.Shown.Select(e => (e.Chat.Number, e.Ask!.Ask.SessionId)).ShouldBe([(3, "a"), (1, "b")]);
    }

    [Fact]
    public async Task An_answer_lands_in_the_chat_it_was_asked_in_when_the_user_has_switched_meanwhile()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Gate = new TaskCompletionSource();
        _brain.Answer = _ => [new BrainText("Running."), new BrainToolCall("t1", "list_chats", "{}")];
        vm.SelectedChat = ChatNumbered(vm, 3);

        Type(vm, "Is the retry test green?");
        vm.SelectedChat = ChatNumbered(vm, 1);
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        vm.Shown.ShouldBeEmpty("nothing of it is in chat 1");
        vm.Log.Select(e => (e.Kind, e.Chat.Number)).ShouldBe([(RavenLogKind.You, 3), (RavenLogKind.Raven, 3), (RavenLogKind.Action, 3)]);
    }

    [Fact]
    public async Task The_user_follows_a_chat_Raven_started_in_another_window_with_their_question()
    {
        // #180: asked in chat 1, Raven started a chat in ContentAutomatorX: the user is moved to chat 3 with the question
        // and its answer, where the new chat's news comes; a note in each chat says so, and chat 3's brain is told.
        var (vm, _) = await ChatsVmAsync();
        _brain.Gate = new TaskCompletionSource();
        _brain.Answer = _ => [new BrainText("On it."), new BrainToolCall("t1", "start_chat", "{}"), new BrainText(" Started a bug report chat in ContentAutomatorX.")];
        vm.SelectedChat = ChatNumbered(vm, 1);

        Type(vm, "In ContentAutomatorX, create a bug report chat for the F keys");
        vm.FollowWork(YardMcp.ChatKey(CodeSwitchX, overview: false)!, ContentAutomatorX); // start_chat did, mid-answer
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 3));
        vm.Shown.Select(e => (e.Kind, e.Text)).ShouldBe(
        [
            (RavenLogKind.Note, "Asked in chat 1, CodeSwitchX."),
            (RavenLogKind.You, "In ContentAutomatorX, create a bug report chat for the F keys"),
            (RavenLogKind.Raven, "On it."),
            (RavenLogKind.Action, "start_chat"),
            (RavenLogKind.Raven, "Started a bug report chat in ContentAutomatorX."),
        ]);
        vm.SelectedChat = ChatNumbered(vm, 1);
        vm.Shown.Select(e => (e.Kind, e.Text)).ShouldBe([(RavenLogKind.Note, "Continued in chat 3, ContentAutomatorX: Raven started a chat there.")]);
        ChatNumbered(vm, 1).Unread.ShouldBe(0, "nothing of it is left unread there");

        // The next question there is told what was moved: its brain is another.
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "open it");
        await WithinAsync(vm.PendingAnswers);
        _brain.Asked[^1].ShouldStartWith("[The user asked this in chat 1, CodeSwitchX, and was moved here when Raven started a chat in this window: "
            + "\"In ContentAutomatorX, create a bug report chat for the F keys\"]");
        _brain.Asked[^1].ShouldEndWith("open it");
        Type(vm, "and then?");
        await WithinAsync(vm.PendingAnswers);
        _brain.Asked[^1].ShouldNotContain("moved here", Case.Insensitive, "told once");
    }

    [Fact]
    public async Task Following_the_work_with_no_question_running_only_shows_the_window_s_chat()
    {
        var (vm, _) = await ChatsVmAsync();

        vm.FollowWork(YardMcp.OverviewChat, ContentAutomatorX);

        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 3));
        vm.Log.ShouldBeEmpty();
    }

    [Fact]
    public async Task Words_a_moved_question_took_along_move_with_it()
    {
        // Two lines typed before the brain took the first: one question, both lines its own.
        var (vm, _) = await ChatsVmAsync();
        _brain.BeforeSent = new TaskCompletionSource();
        _brain.Gate = new TaskCompletionSource();
        _brain.Answer = _ => [new BrainText("On it.")];
        vm.SelectedChat = ChatNumbered(vm, 1);

        Type(vm, "In ContentAutomatorX");
        Type(vm, "create a bug report chat");
        _brain.BeforeSent.SetResult();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        vm.FollowWork(YardMcp.ChatKey(CodeSwitchX, overview: false)!, ContentAutomatorX);
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        vm.Shown.Select(e => (e.Kind, e.Text)).ShouldBe(
        [
            (RavenLogKind.Note, "Asked in chat 1, CodeSwitchX."),
            (RavenLogKind.You, "In ContentAutomatorX"),
            (RavenLogKind.You, "create a bug report chat"),
            (RavenLogKind.Raven, "On it."),
        ]);
    }

    [Fact]
    public async Task A_moved_question_is_told_only_while_it_is_fresh()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Gate = new TaskCompletionSource();
        vm.SelectedChat = ChatNumbered(vm, 1);
        Type(vm, "In ContentAutomatorX, create a bug report chat");
        vm.FollowWork(YardMcp.ChatKey(CodeSwitchX, overview: false)!, ContentAutomatorX);
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        _time.Advance(RavenPanelViewModel.CarriedLifetime + TimeSpan.FromMinutes(1));
        Type(vm, "open it");
        await WithinAsync(vm.PendingAnswers);

        _brain.Asked[^1].ShouldNotContain("moved here", Case.Insensitive);
    }

    [Fact]
    public async Task The_brain_is_told_the_window_chat_the_user_asks_in_and_the_yard_once_they_are_back()
    {
        var (vm, _) = await ChatsVmAsync();

        Type(vm, "one");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "stop it");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = vm.YardChat;
        Type(vm, "two");
        await WithinAsync(vm.PendingAnswers);
        Type(vm, "three");
        await WithinAsync(vm.PendingAnswers);

        _brain.Asked.ShouldBe([
            "one",
            "[The user is in chat 3, ContentAutomatorX: \"it\" and \"this\" mean that window unless they name another.]\nstop it",
            "[The user is in chat 0, the Yard: no window in particular.]\ntwo",
            "three",
        ]);
    }

    /// <summary>
    /// A question still waiting its turn goes along with the next one: asked in another chat, each part says where it was
    /// asked, so "stop it" keeps meaning the window it was said in.
    /// </summary>
    [Fact]
    public async Task A_waiting_question_from_another_chat_goes_along_with_the_chat_it_was_asked_in()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Gate = new TaskCompletionSource();
        _brain.IgnoresCancel = true;
        Type(vm, "zero");
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "stop it"); // waits behind "zero"
        vm.SelectedChat = ChatNumbered(vm, 1);

        Type(vm, "open it");
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        _brain.Asked[^1].ShouldBe(
            "[Said in chat 3, ContentAutomatorX:] stop it\n"
            + "[The user is in chat 1, CodeSwitchX: \"it\" and \"this\" mean that window unless they name another.]\nopen it");
    }

    /// <summary>Words of a window's chat taken along into the Yard's: the Yard's part says it is the Yard's, though the brain was never told otherwise.</summary>
    [Fact]
    public async Task A_waiting_question_from_a_window_s_chat_taken_into_the_yard_s_says_the_user_is_in_the_yard()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Gate = new TaskCompletionSource();
        _brain.IgnoresCancel = true;
        Type(vm, "zero");
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "how far is it"); // waits behind "zero"
        vm.SelectedChat = vm.YardChat;

        Type(vm, "stop it");
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        _brain.Asked[^1].ShouldBe(
            "[Said in chat 3, ContentAutomatorX:] how far is it\n[The user is in chat 0, the Yard: no window in particular.]\nstop it");
    }

    /// <summary>The brain only knows where the user is once a question that said so went in: one it never took told it nothing.</summary>
    [Fact]
    public async Task The_yard_is_named_again_when_the_question_that_named_it_never_went_in()
    {
        var (vm, _) = await ChatsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "how far is it");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = vm.YardChat;
        _brain.BeforeSent = new TaskCompletionSource(); // "what needs me" is asked but not in yet
        Type(vm, "what needs me");
        await Until(() => _brain.Asked.Count == 2);

        Type(vm, "stop it");
        _brain.BeforeSent.SetResult();
        await WithinAsync(vm.PendingAnswers);

        _brain.Sent[^1].ShouldBe("[The user is in chat 0, the Yard: no window in particular.]\nwhat needs me\nstop it");
    }

    [Fact]
    public async Task Typing_in_activity_goes_to_the_yard_s_chat()
    {
        var (vm, _) = await ChatsVmAsync();
        vm.SelectedChat = vm.ActivityChat;

        Type(vm, "What's waiting on me?");
        await WithinAsync(vm.PendingAnswers);

        vm.Log.ShouldHaveSingleItem().Chat.ShouldBe(vm.YardChat);
        vm.TypePrompt.ShouldBe("Type to the Yard…");
    }

    [Fact]
    public async Task Opening_a_window_in_the_cab_shows_its_chat_and_a_removed_window_s_chat_leaves_the_list()
    {
        var (vm, _) = await ChatsVmAsync();

        vm.ShowChatOf(ContentAutomatorX);
        vm.SelectedChat.Label.ShouldBe("3 ContentAutomatorX");

        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX")]);

        vm.SelectedChat.ShouldBe(vm.YardChat);
        vm.Chats.Select(c => c.Label).ShouldBe(["0 Yard", "1 CodeSwitchX", "Activity"]);
    }

    /// <summary>A workspace added again is a new one (a new id), and may take the freed number: its chat starts empty, the old entries stay in Activity.</summary>
    [Fact]
    public async Task A_window_that_takes_a_removed_one_s_number_starts_an_empty_chat()
    {
        var (vm, _) = await ChatsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "hello");
        await WithinAsync(vm.PendingAnswers);

        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX")]);
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (Guid.NewGuid(), 3, "RawCutX")]);
        vm.SelectedChat = ChatNumbered(vm, 3);

        vm.Shown.ShouldBeEmpty();
        vm.Log.ShouldHaveSingleItem().Text.ShouldBe("hello");
    }

    /// <summary>
    /// A window removed while its chat waits on a card: the card goes to its chat's VS Code tab (#135), never to chat 0,
    /// which answers no card. Its entries stay in Activity.
    /// </summary>
    [Fact]
    public async Task A_removed_window_s_open_card_goes_to_vs_code_not_to_chat_zero()
    {
        var (vm, asks) = await ChatsVmAsync();
        var held = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "hello");
        await WithinAsync(vm.PendingAnswers);
        var card = vm.Log.Single(e => e.Kind == RavenLogKind.Permission).Ask.ShouldNotBeNull();

        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX")]);

        await WithinAsync(held);
        (card.IsOpen, card.Outcome).ShouldBe((false, "Left to VS Code: answer it in the chat's tab."));
        vm.SelectedChat.ShouldBe(vm.YardChat);
        vm.Shown.ShouldBeEmpty("no card of the removed window is in chat 0");
        vm.YardChat.IsWaiting.ShouldBeFalse();
    }

    /// <summary>
    /// #148: a card whose window is removed while it is named (the remove asked meanwhile, say) is asked in its VS Code tab:
    /// no card of it is in chat 0, or anywhere in the panel; a note says where it waits.
    /// </summary>
    [Fact]
    public async Task A_card_whose_window_goes_while_it_is_named_goes_to_vs_code_with_a_note()
    {
        var (vm, asks) = await ChatsVmAsync();
        _yard.Gate = new TaskCompletionSource();
        var held = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None); // chat 3's, being named
        vm.OpenQuestions.ShouldBe(1);

        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX")]);
        _yard.Gate.SetResult();

        await WithinAsync(held);
        asks.IsHeld("p1").ShouldBeFalse("its tab asks it");
        vm.Log.ShouldHaveSingleItem().Text.ShouldBe("A permission prompt from ContentAutomatorX · Fix the upload retry went to its VS Code tab: "
            + "Raven found no window's chat for it.");
        (vm.OpenQuestions, vm.YardChat.IsWaiting).ShouldBe((0, false));
    }

    /// <summary>#148: a card the naming finds on no tile goes to VS Code with a note where the user is: they learn where it waits.</summary>
    [Fact]
    public async Task A_card_found_on_no_tile_goes_to_vs_code_with_a_note()
    {
        var (vm, asks) = await ChatsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 3);

        var held = asks.HoldAsync(PermittingIn("zz", "p1"), CancellationToken.None);

        await WithinAsync(held);
        asks.IsHeld("p1").ShouldBeFalse();
        vm.Log.ShouldNotContain(e => e.Ask != null);
        vm.Shown.ShouldHaveSingleItem().Text.ShouldBe("A permission prompt from a chat went to its VS Code tab: Raven found no window's chat for it.");
        (vm.OpenQuestions, vm.YardChat.IsWaiting).ShouldBe((0, false));
    }

    /// <summary>A card whose entry the log let go of still waits: removing its window leaves it to VS Code too.</summary>
    [Fact]
    public async Task A_removed_window_s_card_the_log_let_go_of_still_goes_to_vs_code()
    {
        var (vm, asks) = await ChatsVmAsync();
        var held = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        for (var i = 0; i < RavenPanelViewModel.MaximumLogEntries; i++)
        {
            vm.Note("A note.");
        }

        vm.Log.ShouldNotContain(e => e.Ask != null);
        vm.OpenCardsOf(ContentAutomatorX).ShouldBe(1, "it still waits");
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX")]);

        await WithinAsync(held);
        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.ToVsCode);
        vm.YardChat.IsWaiting.ShouldBeFalse();
    }

    /// <summary>What Raven says of a card in a window's chat goes to that chat, as for chat 0's.</summary>
    [Fact]
    public async Task The_lines_about_a_window_s_card_go_to_its_chat()
    {
        var (vm, asks) = await ChatsVmAsync();
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);

        asks.Propose("p1");

        vm.Log.Select(e => (e.Kind, e.Chat.Number)).ShouldBe([(RavenLogKind.Permission, 3), (RavenLogKind.Raven, 3)]);
    }

    [Fact]
    public async Task The_open_cards_of_a_window_are_counted()
    {
        var (vm, asks) = await ChatsVmAsync();
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        _ = asks.HoldAsync(PermittingIn("a", "p2"), CancellationToken.None);
        _ = asks.HoldAsync(PermittingIn("b", "p3"), CancellationToken.None);

        (vm.OpenCardsOf(ContentAutomatorX), vm.OpenCardsOf(CodeSwitchX), vm.OpenCardsOf(Guid.NewGuid())).ShouldBe((2, 1, 0));
    }

    /// <summary>Each window's news card is in its own chat; only the news of the chat the user is in is spoken (#125).</summary>
    [Fact]
    public async Task A_window_s_news_card_is_in_its_chat_and_only_the_chat_the_user_is_in_is_told()
    {
        _teller.Answer = _ => [new BrainText("Release notes are done.")];
        var (vm, _) = await NewsVmAsync();
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        vm.SelectedChat = ChatNumbered(vm, 1);

        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("b", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Raven));

        vm.Log.Where(e => e.Kind == RavenLogKind.News).Select(e => (e.Chat.Number, e.Lines!.Single().SessionId)).ShouldBe([(3, "a"), (1, "b")]);
        vm.Log.Single(e => e.Kind == RavenLogKind.Raven).Chat.Number.ShouldBe(1);
        var digest = _teller.Asked.ShouldHaveSingleItem();
        digest.ShouldContain("Release notes");
        digest.ShouldNotContain("Fix the upload retry");
    }

    /// <summary>A window's chat carries its tile: its header shows the tile's colour and where its repositories stand, as they change.</summary>
    [Fact]
    public void A_window_s_chat_carries_its_tile_for_the_header_s_git_lines()
    {
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance);
        var yard = ShellTestHarness.CreateYardWithoutInit();
        var tile = new CodeSwitchX.UI.Yard.WorkspaceTileViewModel(new CodeSwitchX.Core.Workspaces.Workspace { Id = CodeSwitchX, Name = "CodeSwitchX", Number = 1 }, yard);

        vm.SetWorkspaces([tile]);
        tile.ShowGit([new CodeSwitchX.UI.Yard.GitLine(null, "main", 2)]);

        var chat = ChatNumbered(vm, 1);
        chat.Tile.ShouldBeSameAs(tile);
        chat.Tile!.GitLines.ShouldHaveSingleItem().GitStateLabel.ShouldBe("2 changed");
        chat.Subtitle.ShouldBeNull("its git lines say where it stands");
        vm.YardChat.Subtitle.ShouldNotBeNull();
    }

    /// <summary>A note that turns into a warning (the voice failed to load) is said where the user is by then, not where the note began.</summary>
    [Fact]
    public async Task A_progress_note_that_turns_into_a_warning_moves_to_the_chat_the_user_is_in()
    {
        var (vm, _) = await ChatsVmAsync();
        _speech.Report(new TextToSpeechStatus(TextToSpeechState.Installing, "downloading Qwen3-TTS"));
        vm.SelectedChat = ChatNumbered(vm, 3);

        _speech.Report(new TextToSpeechStatus(TextToSpeechState.Failed, "CUDA out of memory"));

        vm.Shown.ShouldHaveSingleItem().Text.ShouldBe("Raven cannot speak: CUDA out of memory");
        vm.Log.ShouldHaveSingleItem("the installing note gave way to it");
    }

    /// <summary>The read-back of an allow is said beside its card, in the card's chat, not in the chat the user is in.</summary>
    [Fact]
    public async Task The_lines_about_a_card_go_to_the_chat_the_card_is_in()
    {
        var (vm, asks) = await ChatsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 1);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None); // chat 3's

        asks.Propose("p1");

        vm.Log.Select(e => (e.Kind, e.Chat.Number)).ShouldBe([(RavenLogKind.Permission, 3), (RavenLogKind.Raven, 3)]);
    }

    [Fact]
    public async Task What_the_panel_says_about_itself_is_said_in_the_chat_the_user_is_in()
    {
        var (vm, _) = await ChatsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 3);

        vm.Note("Using the headset again.");

        vm.Shown.ShouldHaveSingleItem().Text.ShouldBe("Using the headset again.");
    }

    [Fact]
    public async Task Saying_chat_three_switches_at_once_without_a_brain_turn_and_says_where()
    {
        var (vm, _) = await ChatsVmAsync();

        Type(vm, "Chat drei");

        vm.SelectedChat.Label.ShouldBe("3 ContentAutomatorX");
        _brain.Asked.ShouldBeEmpty();
        vm.Log.ShouldBeEmpty("a switch is navigation, not a question");
        await Until(() => string.Join(" ", _speech.Spoken) == "Chat 3, ContentAutomatorX.");
    }

    [Fact]
    public async Task Open_chat_three_switches_and_asks_for_its_window_in_the_cab()
    {
        var (vm, _) = await ChatsVmAsync();
        Guid? opened = null;
        vm.CabRequested += (_, id) => opened = id;

        Type(vm, "open chat three");

        vm.SelectedChat.Label.ShouldBe("3 ContentAutomatorX");
        opened.ShouldBe(ContentAutomatorX);
    }

    [Fact]
    public async Task Switching_alone_opens_no_window()
    {
        var (vm, _) = await ChatsVmAsync();
        var opened = false;
        vm.CabRequested += (_, _) => opened = true;

        Type(vm, "go to chat 1");
        Type(vm, "activity");

        vm.SelectedChat.ShouldBe(vm.ActivityChat);
        opened.ShouldBeFalse();
    }

    [Fact]
    public async Task A_chat_number_no_window_has_is_said_and_nothing_switches()
    {
        var (vm, _) = await ChatsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 1);

        Type(vm, "chat nine");

        vm.SelectedChat.Label.ShouldBe("1 CodeSwitchX");
        vm.Shown.ShouldHaveSingleItem().Text.ShouldBe("There is no chat 9.");
        _brain.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_sentence_about_a_chat_goes_to_the_brain()
    {
        var (vm, _) = await ChatsVmAsync();

        Type(vm, "what is chat three doing");
        await WithinAsync(vm.PendingAnswers);

        vm.SelectedChat.ShouldBe(vm.YardChat);
        _brain.Asked.ShouldBe(["what is chat three doing"]);
    }

    [Fact]
    public async Task Stepping_goes_round_the_list_and_skips_activity()
    {
        var (vm, _) = await ChatsVmAsync();

        vm.StepChat(-1).Label.ShouldBe("3 ContentAutomatorX");
        vm.StepChat(1).Label.ShouldBe("0 Yard");
        vm.StepChat(1).Label.ShouldBe("1 CodeSwitchX");
    }

    /// <summary>Any switch away from the chat whose allow waits for a yes ends it: a yes said in the next chat is not for it.</summary>
    [Theory]
    [InlineData("hotkey")]
    [InlineData("click")]
    public async Task A_switch_by_hotkey_or_click_ends_an_allow_waiting_for_a_yes(string how)
    {
        var (vm, asks) = await ChatsVmAsync();
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        asks.Propose("p1");

        if (how == "hotkey")
        {
            vm.SwitchChat(new ChatSwitch(1, false, false));
        }
        else
        {
            vm.SelectedChat = ChatNumbered(vm, 1);
        }

        asks.Proposed.ShouldBeNull();
    }

    [Fact]
    public async Task Showing_the_chat_whose_allow_waits_keeps_it()
    {
        var (vm, asks) = await ChatsVmAsync();
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        asks.Propose("p1");

        vm.SelectedChat = ChatNumbered(vm, 3);

        asks.Proposed.ShouldNotBeNull();
    }

    [Fact]
    public async Task From_activity_next_is_the_yard_and_previous_the_last_chat()
    {
        var (vm, _) = await ChatsVmAsync();
        vm.SelectedChat = vm.ActivityChat;

        vm.StepChat(1).ShouldBe(vm.YardChat);
        vm.SelectedChat = vm.ActivityChat;
        vm.StepChat(-1).Label.ShouldBe("3 ContentAutomatorX");
    }

    /// <summary>
    /// "Chat three" and the question said right after it, before the first was transcribed: the question is asked in
    /// chat 3, where the user asked to be.
    /// </summary>
    [Fact]
    public async Task A_question_said_right_after_a_spoken_switch_goes_to_the_chat_switched_to()
    {
        var first = new TaskCompletionSource<DictationResult>();
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(first.Task, Task.FromResult(new DictationResult("what is it doing", TimeSpan.FromSeconds(2))));
        var (vm, _) = await ChatsVmAsync();
        vm.PressMic(TalkInput.MicButton);
        Speak();
        _time.Advance(Hold);
        var switchRelease = vm.ReleaseMicAsync(TalkInput.MicButton);
        await WithinAsync(vm.PendingStop);
        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);
        Speak();
        _time.Advance(Hold);
        var questionRelease = vm.ReleaseMicAsync(TalkInput.MicButton);
        await WithinAsync(vm.PendingStop);

        first.SetResult(new DictationResult("chat three", TimeSpan.FromSeconds(2)));
        await WithinAsync(switchRelease);
        await WithinAsync(questionRelease);
        await WithinAsync(vm.PendingAnswers);

        vm.Log.Single(e => e.Kind == RavenLogKind.You).Chat.Label.ShouldBe("3 ContentAutomatorX");
        _brain.Asked.ShouldHaveSingleItem().ShouldStartWith("[The user is in chat 3, ContentAutomatorX");
    }

    /// <summary>Raven just asked something: "chat three" may be the answer, so it goes to the brain (which can still switch).</summary>
    [Fact]
    public async Task Chat_three_right_after_raven_asks_something_answers_the_brain()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Answer = _ => [new BrainText("Should I start it in chat 3 or chat 1?")];
        Type(vm, "start the release notes");
        await WithinAsync(vm.PendingAnswers);

        _brain.Answer = _ => [new BrainText("Starting it in chat 3.")];
        Type(vm, "chat three");
        await WithinAsync(vm.PendingAnswers);

        vm.SelectedChat.ShouldBe(vm.YardChat);
        _brain.Asked.Last().ShouldBe("chat three");

        Type(vm, "chat three"); // its answer asked nothing: a switch again
        vm.SelectedChat.Label.ShouldBe("3 ContentAutomatorX");
    }

    [Fact]
    public async Task Looking_at_activity_keeps_an_allow_waiting_for_a_yes()
    {
        var (vm, asks) = await ChatsVmAsync();
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        asks.Propose("p1");

        vm.SelectedChat = vm.ActivityChat;

        asks.Proposed.ShouldNotBeNull();
    }

    [Fact]
    public void What_raven_says_on_a_switch_is_no_switch_itself()
    {
        RavenPanelViewModel.SwitchLine(RavenChat.Activity()).ShouldBe("Activity is shown.");
        foreach (var chat in new[] { RavenChat.Activity(), RavenChat.Yard(), RavenChat.Of(Guid.NewGuid(), 3, "ContentAutomatorX") })
        {
            SpokenChatSwitch.TryRead(RavenPanelViewModel.SwitchLine(chat), out _).ShouldBeFalse("heard back through speakers, it must not switch again");
        }
    }

    /// <summary>Two spoken switches and a question, all said before the first was heard: the question goes to the last chat.</summary>
    [Fact]
    public async Task A_question_after_two_spoken_switches_goes_to_the_last_one()
    {
        var first = new TaskCompletionSource<DictationResult>();
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(first.Task, Task.FromResult(new DictationResult("chat one", TimeSpan.FromSeconds(2))),
                Task.FromResult(new DictationResult("what is it doing", TimeSpan.FromSeconds(2))));
        var (vm, _) = await ChatsVmAsync();
        var releases = new List<Task>();
        for (var i = 0; i < 3; i++)
        {
            vm.PressMic(TalkInput.MicButton);
            await WithinAsync(vm.PendingStart);
            Speak();
            _time.Advance(Hold);
            releases.Add(vm.ReleaseMicAsync(TalkInput.MicButton));
            await WithinAsync(vm.PendingStop);
        }

        first.SetResult(new DictationResult("chat three", TimeSpan.FromSeconds(2)));
        foreach (var release in releases)
        {
            await WithinAsync(release);
        }

        await WithinAsync(vm.PendingAnswers);
        vm.Log.Single(e => e.Kind == RavenLogKind.You).Chat.Label.ShouldBe("1 CodeSwitchX");
    }

    /// <summary>Raven asked something, and the user moved to another chat by hotkey: they moved on, "chat five" is a switch again.</summary>
    [Fact]
    public async Task A_switch_by_hotkey_after_raven_asked_something_makes_chat_n_a_switch_again()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Answer = _ => [new BrainText("Want me to start it?")];
        Type(vm, "the release notes");
        await WithinAsync(vm.PendingAnswers);

        vm.SwitchChat(new ChatSwitch(1, false, false));
        Type(vm, "chat three");

        vm.SelectedChat.Label.ShouldBe("3 ContentAutomatorX");
    }

    /// <summary>A turn that only looked something up asked nothing, whatever an earlier answer asked.</summary>
    [Fact]
    public async Task A_turn_without_words_asked_nothing()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Answer = _ => [new BrainText("Which one?")];
        Type(vm, "open it");
        await WithinAsync(vm.PendingAnswers);
        _brain.Answer = _ => [new BrainToolCall("t1", "list_chats", "{}")];
        Type(vm, "the first");
        await WithinAsync(vm.PendingAnswers);

        Type(vm, "chat three");

        vm.SelectedChat.Label.ShouldBe("3 ContentAutomatorX");
    }

    /// <summary>An answer that ends on a question in a chat the user has left holds back no switch: they moved on.</summary>
    [Fact]
    public async Task An_answer_that_asks_something_in_a_chat_the_user_left_holds_back_no_switch()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Gate = new TaskCompletionSource();
        _brain.Answer = _ => [new BrainText("Want me to start it?")];
        vm.SelectedChat = ChatNumbered(vm, 1);
        Type(vm, "the release notes");
        vm.SelectedChat = ChatNumbered(vm, 3); // moved on while it answers
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        Type(vm, "chat zero");

        vm.SelectedChat.ShouldBe(vm.YardChat);
    }

    /// <summary>Saying the chat of the prompt keeps its allow, as a click on it does; saying another chat ends it.</summary>
    [Fact]
    public async Task Saying_the_chat_of_a_waiting_allow_keeps_it_and_saying_another_ends_it()
    {
        var (vm, asks) = await ChatsVmAsync();
        vm.IsMuted = true; // the read-back counts as heard at once
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        asks.Propose("p1");

        Type(vm, "chat three");
        asks.Proposed.ShouldNotBeNull();

        Type(vm, "chat one");
        asks.Proposed.ShouldBeNull();
    }

    /// <summary>
    /// "Chat three", "chat one", and a question said once the first was heard but not the second: the question was said
    /// after "chat one", so it is asked in chat 1.
    /// </summary>
    [Fact]
    public async Task A_question_said_between_two_spoken_switches_being_heard_goes_to_the_second()
    {
        var first = new TaskCompletionSource<DictationResult>();
        var second = new TaskCompletionSource<DictationResult>();
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(first.Task, second.Task, Task.FromResult(new DictationResult("what is it doing", TimeSpan.FromSeconds(2))));
        var (vm, _) = await ChatsVmAsync();
        var releases = new List<Task>();
        async Task SayAsync()
        {
            vm.PressMic(TalkInput.MicButton);
            await WithinAsync(vm.PendingStart);
            Speak();
            _time.Advance(Hold);
            releases.Add(vm.ReleaseMicAsync(TalkInput.MicButton));
            await WithinAsync(vm.PendingStop);
        }

        await SayAsync();
        await SayAsync();
        first.SetResult(new DictationResult("chat three", TimeSpan.FromSeconds(2)));
        await Until(() => vm.SelectedChat.Number == 3);
        await SayAsync(); // the question, while "chat one" is still being transcribed
        second.SetResult(new DictationResult("chat one", TimeSpan.FromSeconds(2)));
        foreach (var release in releases)
        {
            await WithinAsync(release);
        }

        await WithinAsync(vm.PendingAnswers);
        vm.Log.Single(e => e.Kind == RavenLogKind.You).Chat.Label.ShouldBe("1 CodeSwitchX");
    }
}
