using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Speech;

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
    // Review round 2 of #175: a collapsed switch cleared the marks at once, though its catch-up could still be dropped.
    [Fact]
    public async Task Collapsed_a_catch_up_dropped_by_another_switch_leaves_the_chat_s_marks()
    {
        var vm = await AwayFromChatTwoAsync();
        vm.IsOpen = false;
        var two = ChatNumbered(vm, 2);

        vm.SelectedChat = two;
        two.Unread.ShouldBe(2, "the catch-up waits for the floor; nothing is said yet");
        vm.SelectedChat = ChatNumbered(vm, 3);

        two.Unread.ShouldBe(2);
        _teller.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Collapsed_a_catch_up_said_counts_the_chat_as_seen()
    {
        var vm = await AwayFromChatTwoAsync();
        vm.IsOpen = false;
        var two = ChatNumbered(vm, 2);

        vm.SelectedChat = two;
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        _teller.Asked.ShouldHaveSingleItem().ShouldStartWith("Catch-up:");
        two.Unread.ShouldBe(0);
    }

    [Fact]
    public async Task Collapsed_the_news_of_the_chat_talked_to_said_aloud_is_heard_and_not_told_again()
    {
        var (vm, _) = await TrafficVmAsync();
        vm.CatchUp = true;
        var two = ChatNumbered(vm, 2);
        vm.SelectedChat = two;
        vm.IsOpen = false;

        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Raven));
        await WithinAsync(_voice.WhenQuietAsync());
        two.Unread.ShouldBe(0, "the digest said it");

        vm.SelectedChat = ChatNumbered(vm, 3);
        vm.SelectedChat = two;
        await GraceAsync(vm);
        _teller.Asked.ShouldHaveSingleItem("no catch-up repeats what the digest said");
    }

    [Fact]
    public async Task Collapsed_with_no_voice_ready_raven_s_answer_is_only_written_so_it_counts()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Answer = _ => [new BrainText("Two chats.")];
        _speech.Report(new global::CodeSwitchX.Voice.Speech.TextToSpeechStatus(global::CodeSwitchX.Voice.Speech.TextToSpeechState.Failed));
        vm.IsOpen = false;

        Type(vm, "How many chats run?");
        await WithinAsync(vm.PendingAnswers);

        vm.YardChat.Unread.ShouldBe(1);
    }
    // Review round 3 of #175: the failure the digest said aloud kept its red mark on the chat talked to.
    [Fact]
    public async Task Collapsed_a_failure_said_aloud_in_the_chat_talked_to_leaves_no_red_mark()
    {
        var (vm, _) = await TrafficVmAsync();
        var two = ChatNumbered(vm, 2);
        vm.SelectedChat = two;
        vm.IsOpen = false;

        Changes("a", SessionState.Working, SessionState.Errored);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Raven));
        await WithinAsync(_voice.WhenQuietAsync());

        (two.Unread, two.HasFailed).ShouldBe((0, false));
    }

    // #178: the read-back is spoken only by a voice that is ready; one that is off counted as speaking, so a read-back only
    // written counted as heard.
    [Fact]
    public async Task Collapsed_with_the_voice_off_the_read_back_is_only_written_so_it_counts()
    {
        var (vm, asks) = await QuestionsVmAsync();
        _ = asks.HoldAsync(Permitting(), CancellationToken.None);
        await PermissionCards(vm).Single().Naming;
        var chat = vm.CurrentChat;
        _speech.Report(new TextToSpeechStatus(TextToSpeechState.Off));
        vm.IsOpen = false;

        asks.Propose("p1");

        vm.Log.Last().Text.ShouldBe("Run npm test in ContentAutomatorX? Say yes.");
        chat.Unread.ShouldBe(1, "written, not said");
        _speech.Spoken.ShouldBeEmpty();
    }

    [Fact]
    public async Task Collapsed_with_the_voice_ready_the_read_back_is_heard()
    {
        var (vm, asks) = await QuestionsVmAsync();
        _ = asks.HoldAsync(Permitting(), CancellationToken.None);
        await PermissionCards(vm).Single().Naming;
        var chat = vm.CurrentChat;
        vm.IsOpen = false;

        var proposal = asks.Propose("p1");
        await Until(() => asks.IsHeard(proposal));

        chat.Unread.ShouldBe(0);
    }

    // #178: while the user talks in Open mic, Raven's answer is only written; collapsed it counted as heard.
    [Fact]
    public async Task Collapsed_an_answer_only_written_while_the_user_talks_in_Open_mic_counts()
    {
        var transcript = new TaskCompletionSource<DictationResult>();
        Transcribes(transcript.Task);
        _brain.Answer = _ => [new BrainText("You have one chat waiting.")];
        var vm = await InOpenMicAsync();
        vm.IsOpen = false;
        _openMic.Speak();
        _openMic.EndTurn();

        _openMic.Speak(); // the user's next turn has started
        transcript.SetResult(new DictationResult("What's waiting on me?", TimeSpan.FromSeconds(1)));
        await WithinAsync(vm.PendingTranscriptions);
        await WithinAsync(vm.PendingAnswers);

        var answer = vm.Log.Single(e => e.Kind == RavenLogKind.Raven && e.Text == "You have one chat waiting.");
        _speech.Spoken.ShouldBeEmpty("the user is talking");
        answer.Chat.Unread.ShouldBe(1, "written, not said");
    }
}
