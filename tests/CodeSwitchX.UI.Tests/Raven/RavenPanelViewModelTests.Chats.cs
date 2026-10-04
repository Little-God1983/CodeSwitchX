using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Speech;
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
    /// A window removed while its chat waits on a card: the card moves to the Yard's chat, where chats on no tile ask, so
    /// it can still be answered by a click (Activity has no buttons).
    /// </summary>
    [Fact]
    public async Task A_removed_window_s_open_card_moves_to_the_yard_s_chat_and_can_still_be_answered()
    {
        var (vm, asks) = await ChatsVmAsync();
        var held = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "hello");
        await WithinAsync(vm.PendingAnswers);

        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX")]);

        vm.SelectedChat.ShouldBe(vm.YardChat);
        var card = vm.Shown.ShouldHaveSingleItem("its other entries stay in Activity only").Ask.ShouldNotBeNull();
        vm.AllowCommand.Execute(card);
        await WithinAsync(held);
        (await held).ShouldNotBeNull().Permit!.Allow.ShouldBeTrue();
    }

    [Fact]
    public async Task A_window_s_news_card_is_in_its_chat_and_the_digest_of_several_windows_in_the_yard_s()
    {
        _teller.Answer = _ => [new BrainText("Both are done.")];
        var (vm, _) = await NewsVmAsync();
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);

        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("b", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Raven));

        vm.Log.Where(e => e.Kind == RavenLogKind.News).Select(e => (e.Chat.Number, e.Lines!.Single().SessionId)).ShouldBe([(3, "a"), (1, "b")]);
        vm.Log.Single(e => e.Kind == RavenLogKind.Raven).Chat.ShouldBe(vm.YardChat);
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

    /// <summary>
    /// A card asked before its window's chat was in the list stays in the Yard's chat; the read-back of an allow for it is
    /// said beside it there, not in the window's chat added since.
    /// </summary>
    [Fact]
    public async Task The_lines_about_a_card_go_to_the_chat_the_card_is_in()
    {
        var (vm, asks) = await ChatsVmAsync();
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX")]);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);

        asks.Propose("p1");

        vm.Log.Select(e => (e.Kind, e.Chat.Number)).ShouldBe([(RavenLogKind.Permission, 0), (RavenLogKind.Raven, 0)]);
    }

    [Fact]
    public async Task What_the_panel_says_about_itself_is_said_in_the_chat_the_user_is_in()
    {
        var (vm, _) = await ChatsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 3);

        vm.Note("Using the headset again.");

        vm.Shown.ShouldHaveSingleItem().Text.ShouldBe("Using the headset again.");
    }
}
