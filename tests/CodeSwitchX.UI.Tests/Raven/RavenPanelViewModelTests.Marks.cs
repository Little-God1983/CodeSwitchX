using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>
/// Marks in the chat list (#122): a chat that waits for the user and has not been opened since its card came blinks, lines
/// that came while the user was elsewhere are counted, and a chat whose Claude chat failed is marked, until it is opened.
/// </summary>
public sealed partial class RavenPanelViewModelTests
{
    [Fact]
    public async Task A_permission_prompt_in_another_chat_blinks_until_opened_and_waits_until_answered()
    {
        var (vm, asks) = await ChatsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 1);

        var held = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);

        var three = ChatNumbered(vm, 3);
        three.IsWaiting.ShouldBeTrue();
        three.IsWaitingUnseen.ShouldBeTrue("the user is in chat 1 and has not seen it");
        three.Unread.ShouldBe(1, "the card is a line the user has not read");

        vm.SelectedChat = three;
        three.IsWaitingUnseen.ShouldBeFalse("opened: the blinking stops");
        three.IsWaiting.ShouldBeTrue("the outline stays until the card is answered");
        three.Unread.ShouldBe(0);

        vm.SelectedChat = ChatNumbered(vm, 1);
        three.IsWaitingUnseen.ShouldBeFalse("seen once is seen: leaving it does not start the blinking again");

        vm.SelectedChat = three;
        vm.AllowCommand.Execute(vm.Shown.Single().Ask);
        await WithinAsync(held);
        three.IsWaiting.ShouldBeFalse("answered: the outline goes");
    }

    [Fact]
    public async Task A_card_in_the_chat_the_user_is_in_waits_without_blinking_or_a_count()
    {
        var (vm, asks) = await ChatsVmAsync();
        var three = ChatNumbered(vm, 3);
        vm.SelectedChat = three;

        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);

        three.IsWaiting.ShouldBeTrue();
        three.IsWaitingUnseen.ShouldBeFalse();
        three.Unread.ShouldBe(0);
    }

    [Fact]
    public async Task A_card_answered_in_vs_code_before_the_user_opens_the_chat_leaves_no_mark()
    {
        var (vm, asks) = await ChatsVmAsync();
        using var ask = new CancellationTokenSource();
        _ = asks.HoldAsync(PermittingIn("a", "p1"), ask.Token);
        var three = ChatNumbered(vm, 3);
        three.IsWaitingUnseen.ShouldBeTrue();

        await ask.CancelAsync(); // VS Code took the answer: the hook lets go

        await Until(() => !three.IsWaiting);
        three.IsWaitingUnseen.ShouldBeFalse("nothing waits any more, so nothing blinks");
    }

    [Fact]
    public async Task Three_news_lines_in_another_chat_count_three_and_opening_it_clears_them()
    {
        _teller.Answer = _ => [new BrainText("All three are done.")];
        _yard.Show("a2", "ContentAutomatorX", "Tidy the logs");
        _yard.Show("a3", "ContentAutomatorX", "Bump the packages");
        var (vm, _) = await NewsVmAsync();
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        vm.SelectedChat = ChatNumbered(vm, 1);

        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("a2", SessionState.Working, SessionState.Idle);
        Changes("a3", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.News));

        var three = ChatNumbered(vm, 3);
        vm.Log.Single(e => e.Kind == RavenLogKind.News).Lines!.Count.ShouldBe(3);
        three.Unread.ShouldBe(3, "three news lines, and nothing said of them: chat 3 is not the one the user is in");
        ChatNumbered(vm, 1).Unread.ShouldBe(0);

        vm.SelectedChat = three;
        three.Unread.ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_chat_is_marked_until_its_chat_is_opened()
    {
        _teller.Answer = _ => [new BrainText("It failed.")];
        var (vm, _) = await NewsVmAsync();
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);

        Changes("a", SessionState.Working, SessionState.Errored);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.News));

        var three = ChatNumbered(vm, 3);
        three.HasFailed.ShouldBeTrue();
        ChatNumbered(vm, 1).HasFailed.ShouldBeFalse();

        vm.SelectedChat = three;
        three.HasFailed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_finished_chat_counts_without_a_failed_mark()
    {
        _teller.Answer = _ => [new BrainText("Done.")];
        var (vm, _) = await NewsVmAsync();
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);

        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.News));

        ChatNumbered(vm, 3).HasFailed.ShouldBeFalse();
        ChatNumbered(vm, 3).Unread.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Raven_s_answer_in_a_chat_the_user_left_counts_there_and_the_user_s_own_words_do_not()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Gate = new TaskCompletionSource();
        _brain.Answer = _ => [new BrainText("It is green.")];
        var three = ChatNumbered(vm, 3);
        vm.SelectedChat = three;

        Type(vm, "Is the retry test green?");
        vm.SelectedChat = ChatNumbered(vm, 1);
        three.Unread.ShouldBe(0, "the user's own words are read");
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        three.Unread.ShouldBe(1);
        ChatNumbered(vm, 1).Unread.ShouldBe(0);
    }

    [Fact]
    public async Task Activity_takes_words_for_the_yard_so_the_yard_s_lines_count_as_read_there()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Answer = _ => [new BrainText("Two chats.")];
        vm.SelectedChat = vm.ActivityChat;

        Type(vm, "How many chats run?");
        await WithinAsync(vm.PendingAnswers);

        vm.YardChat.Unread.ShouldBe(0);
    }

    /// <summary>A removed window's waiting card goes to VS Code (#135): no mark moves to chat 0.</summary>
    [Fact]
    public async Task A_waiting_card_of_a_removed_window_leaves_no_mark_in_chat_zero()
    {
        var (vm, asks) = await ChatsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 1);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);

        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX")]);

        (vm.YardChat.IsWaiting, vm.YardChat.IsWaitingUnseen, vm.YardChat.Unread).ShouldBe((false, false, 0));
    }

    [Fact]
    public async Task Unread_lines_the_log_drops_leave_the_count()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Gate = new TaskCompletionSource();
        _brain.Answer = _ => [new BrainText("It is green.")];
        var three = ChatNumbered(vm, 3);
        vm.SelectedChat = three;
        Type(vm, "Is the retry test green?");
        vm.SelectedChat = ChatNumbered(vm, 1);
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
        three.Unread.ShouldBe(1);

        for (var i = 0; i < RavenPanelViewModel.MaximumLogEntries; i++)
        {
            vm.Note("A note.");
        }

        vm.Log.ShouldNotContain(e => e.Chat == three);
        three.Unread.ShouldBe(0, "opening it would show none of them");
    }

    [Fact]
    public async Task An_answer_that_goes_on_after_the_user_left_its_chat_counts_once()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Pause = new TaskCompletionSource();
        _brain.Answer = _ => [new BrainText("It is green, "), new BrainText("but two tests "), new BrainText("were skipped.")];
        var three = ChatNumbered(vm, 3);
        vm.SelectedChat = three;

        Type(vm, "Is the retry test green?");
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Raven));
        three.Unread.ShouldBe(0, "its beginning came before the user's eyes");
        vm.SelectedChat = ChatNumbered(vm, 1);
        _brain.Pause.SetResult();
        await WithinAsync(vm.PendingAnswers);

        three.Unread.ShouldBe(1, "the rest came unread, and is one line");

        vm.SelectedChat = three;
        three.Unread.ShouldBe(0);
    }

    [Fact]
    public async Task A_question_raven_could_not_answer_counts_in_the_chat_the_user_left()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Gate = new TaskCompletionSource();
        _brain.Answer = _ => [new BrainFailed("Claude Code stopped.")];
        var three = ChatNumbered(vm, 3);
        vm.SelectedChat = three;

        Type(vm, "Is the retry test green?");
        vm.SelectedChat = ChatNumbered(vm, 1);
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        three.Unread.ShouldBe(1, "the user would not learn the question went unanswered");
    }

    /// <summary>Activity shows every line, so none that comes there is unread; its cards have no buttons, so opening it sees no chat's card.</summary>
    [Fact]
    public async Task Activity_reads_every_chat_s_lines_and_opens_no_chat()
    {
        var (vm, asks) = await ChatsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 3);
        var one = ChatNumbered(vm, 1);
        _ = asks.HoldAsync(PermittingIn("b", "p0"), CancellationToken.None); // chat 1's
        await Until(() => one.IsWaitingUnseen);

        vm.SelectedChat = vm.ActivityChat;
        one.IsWaitingUnseen.ShouldBeTrue("Activity is not chat 1: the card cannot be answered there");
        one.Unread.ShouldBe(1);

        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        var three = ChatNumbered(vm, 3);
        three.IsWaitingUnseen.ShouldBeTrue();
        three.Unread.ShouldBe(0, "the card came before the user's eyes, in Activity");

        vm.SelectedChat = one;
        one.IsWaitingUnseen.ShouldBeFalse();
        one.Unread.ShouldBe(0);
    }

    [Fact]
    public void The_badge_says_at_most_nine_plus()
    {
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance);
        var chat = vm.YardChat;

        chat.UnreadText.ShouldBe("");
        chat.Unread = 3;
        chat.UnreadText.ShouldBe("3");
        chat.Unread = 12;
        chat.UnreadText.ShouldBe("9+");
    }

    [Fact]
    public void The_marks_are_said_in_words_for_the_tooltip_and_screen_readers()
    {
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance);
        var chat = vm.YardChat;

        chat.Status.ShouldBe("");
        chat.IsWaiting = true;
        chat.IsWaitingUnseen = true;
        chat.Unread = 3;
        chat.HasFailed = true;
        chat.Status.ShouldBe("waits for you, not seen yet, 3 unread, failed");
        chat.IsWaitingUnseen = false;
        chat.Status.ShouldBe("waits for you, 3 unread, failed");
    }

    [Fact]
    public void A_chat_works_while_its_tile_says_a_Claude_chat_does()
    {
        // #182: the tile sums its rows up; the chat follows the tile it has, and lets go of one it had.
        var yard = ShellTestHarness.CreateYardWithoutInit();
        var tile = new WorkspaceTileViewModel(new Workspace { Name = "A", RootPath = @"c:\a" }, yard);
        var chat = RavenChat.Of(Guid.NewGuid(), 3, "A");
        chat.Tile = tile;
        chat.IsWorking.ShouldBeFalse();

        tile.IsWorking = true;
        chat.IsWorking.ShouldBeTrue();
        chat.Status.ShouldBe("working");
        tile.IsWorking = false;
        chat.IsWorking.ShouldBeFalse();

        var other = new WorkspaceTileViewModel(new Workspace { Name = "B", RootPath = @"c:\b" }, yard) { IsWorking = true };
        chat.Tile = other;
        chat.IsWorking.ShouldBeTrue("the tile it has now");
        tile.IsWorking = true;
        other.IsWorking = false;
        chat.IsWorking.ShouldBeFalse("the tile it had says nothing to it");
        chat.Tile = null;
        other.IsWorking = true;
        chat.IsWorking.ShouldBeFalse("no tile, no work of a chat's");
    }

    [Fact]
    public async Task A_question_taken_along_by_another_chat_s_leaves_its_chat_at_rest()
    {
        // Asked in chat 3, not yet sent; then asked in chat 0, whose question takes it along: chat 0 works, chat 3 not.
        var (vm, _) = await ChatsVmAsync();
        _brain.BeforeSent = new TaskCompletionSource();
        var three = ChatNumbered(vm, 3);
        vm.SelectedChat = three;
        Type(vm, "Is the retry test green?");
        three.IsWorking.ShouldBeTrue();

        vm.SelectedChat = vm.YardChat;
        Type(vm, "and what's waiting on me?");

        three.IsWorking.ShouldBeFalse();
        vm.YardChat.IsWorking.ShouldBeTrue();
        _brain.BeforeSent.SetResult();
        await WithinAsync(vm.PendingAnswers);
        vm.YardChat.IsWorking.ShouldBeFalse();
    }

    [Fact]
    public async Task A_chat_works_while_Raven_answers_in_it()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Gate = new TaskCompletionSource();
        var three = ChatNumbered(vm, 3);
        vm.SelectedChat = three;

        Type(vm, "Is the retry test green?");

        three.IsWorking.ShouldBeTrue();
        three.Status.ShouldBe("working");
        ChatNumbered(vm, 1).IsWorking.ShouldBeFalse();
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
        three.IsWorking.ShouldBeFalse();
    }
}
