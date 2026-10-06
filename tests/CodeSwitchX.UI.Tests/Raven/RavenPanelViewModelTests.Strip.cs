using CodeSwitchX.Conductor;
using CodeSwitchX.UI.Raven;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>
/// The collapsed strip (#173): each chat's number with its marks, a badge that pulses a few times when a line comes, and
/// a click that opens Raven on that chat. Collapsed, the user sees no chat, so lines in the one shown count too.
/// </summary>
public sealed partial class RavenPanelViewModelTests
{
    [Fact]
    public async Task A_line_in_another_chat_pulses_its_badge_and_the_badge_steadies_after_the_pulse()
    {
        var (vm, asks) = await ChatsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 1);

        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);

        var three = ChatNumbered(vm, 3);
        three.Unread.ShouldBe(1);
        three.IsNewsPulsing.ShouldBeTrue();
        _time.Advance(RavenPanelViewModel.NewsPulse - TimeSpan.FromMilliseconds(1));
        three.IsNewsPulsing.ShouldBeTrue();
        _time.Advance(TimeSpan.FromMilliseconds(1));
        three.IsNewsPulsing.ShouldBeFalse("three beats, then steady");
        three.Unread.ShouldBe(1, "the badge stays until the chat is opened");
    }

    [Fact]
    public async Task A_second_line_while_the_badge_pulses_starts_the_pulse_over()
    {
        var (vm, asks) = await ChatsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 1);
        var three = ChatNumbered(vm, 3);
        var starts = 0;
        three.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RavenChat.IsNewsPulsing) && three.IsNewsPulsing)
            {
                starts++;
            }
        };
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        _time.Advance(RavenPanelViewModel.NewsPulse / 2);

        _ = asks.HoldAsync(PermittingIn("a", "p2"), CancellationToken.None);

        starts.ShouldBe(2, "the animation starts again on the change to true");
        _time.Advance(RavenPanelViewModel.NewsPulse / 2);
        three.IsNewsPulsing.ShouldBeTrue("the first pulse's end is not the second one's");
        _time.Advance(RavenPanelViewModel.NewsPulse / 2);
        three.IsNewsPulsing.ShouldBeFalse();
    }

    [Fact]
    public async Task Opening_a_chat_stops_its_pulse()
    {
        var (vm, asks) = await ChatsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 1);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);

        vm.SelectedChat = ChatNumbered(vm, 3);

        ChatNumbered(vm, 3).IsNewsPulsing.ShouldBeFalse();
    }

    [Fact]
    public async Task Collapsed_the_chat_shown_counts_what_comes_and_expanding_clears_it()
    {
        var (vm, asks) = await ChatsVmAsync();
        var three = ChatNumbered(vm, 3);
        vm.SelectedChat = three;
        vm.IsOpen = false;

        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);

        three.Unread.ShouldBe(1, "collapsed, the user sees no chat");
        three.IsWaitingUnseen.ShouldBeTrue();
        three.IsNewsPulsing.ShouldBeTrue();
        vm.TogglePanelCommand.Execute(null);
        (three.Unread, three.IsWaitingUnseen, three.IsNewsPulsing).ShouldBe((0, false, false));
        three.IsWaiting.ShouldBeTrue("the card still waits for its answer");
    }

    [Fact]
    public async Task A_number_on_the_strip_opens_raven_on_that_chat_and_leaves_the_others_unread()
    {
        var (vm, asks) = await ChatsVmAsync();
        var one = ChatNumbered(vm, 1);
        var three = ChatNumbered(vm, 3);
        vm.SelectedChat = one;
        vm.IsOpen = false;
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        _ = asks.HoldAsync(PermittingIn("b", "p2"), CancellationToken.None);
        (one.Unread, three.Unread).ShouldBe((1, 1));

        vm.OpenChatCommand.Execute(three);

        vm.IsOpen.ShouldBeTrue();
        vm.SelectedChat.ShouldBe(three);
        three.Unread.ShouldBe(0);
        one.Unread.ShouldBe(1, "chat 1 was shown before, but nobody saw it while collapsed");
        one.IsWaitingUnseen.ShouldBeTrue();
    }

    [Fact]
    public async Task A_number_of_the_chat_already_shown_opens_raven_and_clears_its_marks()
    {
        var (vm, asks) = await ChatsVmAsync();
        var three = ChatNumbered(vm, 3);
        vm.SelectedChat = three;
        vm.IsOpen = false;
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);

        vm.OpenChatCommand.Execute(three);

        vm.IsOpen.ShouldBeTrue();
        (three.Unread, three.IsWaitingUnseen).ShouldBe((0, false));
    }

    [Fact]
    public async Task Activity_on_the_strip_opens_activity_and_leaves_the_chats_unread()
    {
        var (vm, asks) = await ChatsVmAsync();
        vm.IsOpen = false;
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);

        vm.OpenChatCommand.Execute(vm.ActivityChat);

        vm.SelectedChat.ShouldBe(vm.ActivityChat);
        ChatNumbered(vm, 3).Unread.ShouldBe(1, "Activity opens no chat: its cards are answered in their own");
    }
    // Review of #175: a chat switch while collapsed (hotkey, voice, the Cab) cleared marks nobody had seen.
    [Fact]
    public async Task A_switch_while_collapsed_keeps_the_chat_s_marks_until_raven_opens()
    {
        var (vm, asks) = await ChatsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 1);
        vm.IsOpen = false;
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        var three = ChatNumbered(vm, 3);

        vm.SelectedChat = three; // as the chat hotkey does, which leaves Raven collapsed

        (three.Unread, three.IsWaitingUnseen).ShouldBe((1, true), "collapsed, nothing shows the chat");
        vm.IsOpen = true;
        (three.Unread, three.IsWaitingUnseen).ShouldBe((0, false));
    }

    [Fact]
    public async Task Collapsed_raven_s_spoken_answer_in_the_chat_talked_to_is_heard_not_news()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Answer = _ => [new BrainText("Two chats.")];
        vm.IsOpen = false;

        Type(vm, "How many chats run?");
        await WithinAsync(vm.PendingAnswers);

        vm.YardChat.Unread.ShouldBe(0);
        vm.YardChat.IsNewsPulsing.ShouldBeFalse();
    }

    [Fact]
    public async Task Collapsed_and_muted_raven_s_answer_is_only_written_so_it_counts()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Answer = _ => [new BrainText("Two chats.")];
        vm.IsMuted = true;
        vm.IsOpen = false;

        Type(vm, "How many chats run?");
        await WithinAsync(vm.PendingAnswers);

        vm.YardChat.Unread.ShouldBe(1);
    }

    // Review of #175: with the strip's amber total gone, a card whose log entry rolled out had no mark anywhere.
    [Fact]
    public async Task A_card_still_waiting_keeps_its_ring_after_the_log_drops_its_entry()
    {
        var (vm, asks) = await ChatsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 1);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);

        for (var i = 0; i < RavenPanelViewModel.MaximumLogEntries; i++)
        {
            vm.Note("A note.");
        }

        vm.Log.ShouldNotContain(e => e.Chat == ChatNumbered(vm, 3));
        ChatNumbered(vm, 3).IsWaiting.ShouldBeTrue("the card still waits for its answer");
    }
}
