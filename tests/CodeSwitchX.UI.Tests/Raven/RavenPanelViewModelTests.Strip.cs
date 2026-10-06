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
        _teller.Answer = _ => [new BrainText("While you were away, Task a finished and Task a2 failed.")];
        vm.IsOpen = false;
        var two = ChatNumbered(vm, 2);

        vm.SelectedChat = two;
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());
        await WithinAsync(vm.PendingHeardCheck);

        _teller.Asked.ShouldHaveSingleItem().ShouldStartWith("Catch-up:");
        two.Unread.ShouldBe(0);
    }

    [Fact]
    public async Task Collapsed_the_news_of_the_chat_talked_to_said_aloud_is_heard_and_not_told_again()
    {
        var (vm, _) = await TrafficVmAsync();
        _teller.Answer = _ => [new BrainText("Task a finished.")];
        vm.CatchUp = true;
        var two = ChatNumbered(vm, 2);
        vm.SelectedChat = two;
        vm.IsOpen = false;

        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Raven));
        await WithinAsync(_voice.WhenQuietAsync());
        await WithinAsync(vm.PendingHeardCheck);
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
        _teller.Answer = _ => [new BrainText("Task a failed.")];
        var two = ChatNumbered(vm, 2);
        vm.SelectedChat = two;
        vm.IsOpen = false;

        Changes("a", SessionState.Working, SessionState.Errored);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Raven));
        await WithinAsync(_voice.WhenQuietAsync());
        await WithinAsync(vm.PendingHeardCheck);

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

    // Review of #187: a voice that is off (asleep) starts loading on the first sentence and drops the reply as "still
    // loading": the answer is only written.
    [Fact]
    public async Task Collapsed_with_the_voice_off_an_answer_is_only_written_so_it_counts()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Answer = _ => [new BrainText("Two chats.")];
        _speech.Report(new TextToSpeechStatus(TextToSpeechState.Off));
        vm.IsOpen = false;

        Type(vm, "How many chats run?");
        await WithinAsync(vm.PendingAnswers);

        vm.YardChat.Unread.ShouldBe(1);
    }

    // Review of #187: past three sentences a reply is only written; the line said only in part counts.
    [Fact]
    public async Task Collapsed_an_answer_cut_at_the_sentence_limit_counts_once_it_has_played()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Answer = _ => [new BrainText("One. Two. Three. Four. Five.")];
        vm.IsOpen = false;

        Type(vm, "Count to five.");
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());

        await Until(() => vm.YardChat.Unread == 1);
        _speech.Spoken.ShouldNotContain(s => s.Contains("Four", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Collapsed_an_answer_said_to_its_end_stays_heard()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Answer = _ => [new BrainText("One. Two. Three.")];
        vm.IsOpen = false;

        Type(vm, "Count to three.");
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(vm.PendingHeardCheck);

        vm.YardChat.Unread.ShouldBe(0);
    }

    // Review round 2 of #187: each line of a reply split by a card counted when the reply was cut at its end.
    [Fact]
    public async Task Collapsed_a_reply_split_by_a_card_and_cut_at_its_end_counts_once()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Answer = _ =>
        [
            new BrainText("Let me check."),
            new BrainToolCall("t1", "list_chats", "{}"),
            new BrainToolResult("t1", false),
            new BrainText("Found it. Chat 3 finished. Chat 5 failed. Details are below."),
        ];
        vm.IsOpen = false;

        Type(vm, "What happened?");
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(vm.PendingHeardCheck);

        vm.YardChat.Unread.ShouldBe(1);
    }

    // Review round 2 of #187: a line read in the open panel counted once the panel was collapsed again and the reply hushed.
    [Fact]
    public async Task A_line_read_in_the_open_panel_does_not_count_when_its_reply_is_hushed_later()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Answer = _ => [new BrainText("Two chats.")];
        _speech.Gate = new TaskCompletionSource(); // it has not played yet
        vm.IsOpen = false;
        Type(vm, "How many chats run?");
        await Until(() => _speech.Spoken.Count > 0);

        vm.IsOpen = true; // read
        vm.IsOpen = false;
        vm.IsMuted = true; // hushes the reply before it has played
        _speech.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(vm.PendingHeardCheck);

        vm.YardChat.Unread.ShouldBe(0);
    }

    // Review round 2 of #187: a line the log let go of was counted, and nothing could take the count off again.
    [Fact]
    public async Task A_line_the_log_let_go_of_is_not_counted_when_its_reply_is_hushed()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Answer = _ => [new BrainText("Two chats.")];
        _speech.Gate = new TaskCompletionSource();
        vm.IsOpen = false;
        Type(vm, "How many chats run?");
        await Until(() => _speech.Spoken.Count > 0);
        for (var i = 0; i < RavenPanelViewModel.MaximumLogEntries; i++)
        {
            vm.Note("A note.");
        }

        vm.IsMuted = true;
        _speech.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(vm.PendingHeardCheck);

        vm.YardChat.Unread.ShouldBe(0);
    }

    // Review of #187: a read-back cut off by the user's words was never heard to its end; only a note said so, which counts
    // for nothing.
    [Fact]
    public async Task Collapsed_a_read_back_cut_off_counts()
    {
        var (vm, asks) = await QuestionsVmAsync();
        _ = asks.HoldAsync(Permitting(), CancellationToken.None);
        await PermissionCards(vm).Single().Naming;
        var chat = vm.CurrentChat;
        vm.IsOpen = false;
        _speech.Gate = new TaskCompletionSource(); // the read-back has not played yet
        var proposal = asks.Propose("p1");
        await Until(() => _speech.Spoken.Count > 0);

        Type(vm, "wait");
        _speech.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        asks.IsHeard(proposal).ShouldBeFalse();
        await Until(() => chat.Unread == 1);
    }

    // Review round 3 of #187: a reply hushed before its first words settled at once, before its line was tracked.
    [Fact]
    public async Task Collapsed_an_answer_whose_reply_was_hushed_before_its_first_words_counts()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Answer = _ => [new BrainText("Two chats.")];
        _brain.Gate = new TaskCompletionSource();
        vm.IsOpen = false;
        Type(vm, "How many chats run?");
        await Until(() => _brain.Sent.Count == 1);

        vm.IsMuted = true; // hushes the reply begun for the answer
        vm.IsMuted = false;
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(vm.PendingHeardCheck);

        _speech.Spoken.ShouldBeEmpty();
        vm.YardChat.Unread.ShouldBe(1);
    }

    // Review round 3 of #187: begun in the open panel, then collapsed and muted, the rest of the answer is only written.
    [Fact]
    public async Task An_answer_begun_open_counts_when_muted_after_collapsing()
    {
        var (vm, _) = await ChatsVmAsync();
        _brain.Answer = _ => [new BrainText("Let me look."), new BrainText(" There are two chats.")];
        _brain.Pause = new TaskCompletionSource();
        Type(vm, "How many chats run?");
        await Until(() => vm.Log.Any(e => e.Text.StartsWith("Let me look.", StringComparison.Ordinal)));

        vm.IsOpen = false;
        vm.IsMuted = true;
        _brain.Pause.SetResult();
        await WithinAsync(vm.PendingAnswers);

        vm.YardChat.Unread.ShouldBe(1);
    }

    // #179: the collapsed catch-up called Seen on the whole chat: an answer only written, which it never mentions, and a
    // card read after it lost their marks.
    [Fact]
    public async Task Collapsed_a_catch_up_clears_what_it_said_and_leaves_an_answer_only_written_and_a_card()
    {
        var (vm, asks) = await TrafficVmAsync();
        _yard.Show("a2", "ContentAutomatorX", "Task a2"); // its news, while "a" still waits on its card
        vm.CatchUp = true;
        var two = ChatNumbered(vm, 2);
        vm.IsOpen = false;
        vm.SelectedChat = two;
        vm.IsMuted = true;
        _brain.Answer = _ => [new BrainText("Two chats.")];
        Type(vm, "How many chats run?");
        await WithinAsync(vm.PendingAnswers);
        two.Unread.ShouldBe(1, "muted, the answer is only written");
        vm.SelectedChat = ChatNumbered(vm, 1);
        vm.IsMuted = false;
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        Changes("a2", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => two.Unread == 3);
        _time.Advance(TrafficWatcher.DefaultCooldown);
        _teller.Answer = _ => [new BrainText("While you were away, Task a2 finished.")];

        vm.SelectedChat = two;
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());
        await WithinAsync(vm.PendingHeardCheck);

        _teller.Asked.ShouldContain(q => q.StartsWith("Catch-up:", StringComparison.Ordinal));
        two.Unread.ShouldBe(2, "the answer only written and the card are still to be read");
        two.IsWaitingUnseen.ShouldBeTrue("the card blinks until it is read");
    }

    // #179: the digest marked its whole card heard, though a stale line is shown and never said.
    [Fact]
    public async Task Collapsed_a_digest_leaves_its_stale_lines_counted()
    {
        var (vm, _) = await TrafficVmAsync();
        _yard.Show("a2", "ContentAutomatorX", "Task a2");
        _teller.Answer = _ => [new BrainText("Task a2 finished.")];
        var two = ChatNumbered(vm, 2);
        vm.SelectedChat = two;
        vm.IsOpen = false;
        _brain.Gate = new TaskCompletionSource(); // the floor is held: the news waits
        Type(vm, "Long question");
        Changes("a", SessionState.Working, SessionState.Idle);
        _time.Advance(ChatNews.MaximumAge + TimeSpan.FromSeconds(1));
        Changes("a2", SessionState.Working, SessionState.Idle);
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.News));
        await WithinAsync(_voice.WhenQuietAsync());
        await WithinAsync(vm.PendingHeardCheck);

        vm.Log.Single(e => e.Kind == RavenLogKind.News).Lines!.Count(l => l.Stale).ShouldBe(1);
        two.Unread.ShouldBe(1, "the stale line is shown, never said");
    }

    // Review round 2 of #187: the digest's card was marked heard before a word of it was said; hushed, it lost its marks.
    [Fact]
    public async Task Collapsed_a_digest_hushed_midway_leaves_its_card_counted()
    {
        var (vm, _) = await TrafficVmAsync();
        _teller.Answer = _ => [new BrainText("Task a finished.")];
        var two = ChatNumbered(vm, 2);
        vm.SelectedChat = two;
        vm.IsOpen = false;
        _speech.Gate = new TaskCompletionSource(); // it has not played yet

        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => _speech.Spoken.Count > 0);
        vm.IsMuted = true;
        _speech.Gate.SetResult();
        await WithinAsync(_voice.WhenQuietAsync());
        await WithinAsync(vm.PendingHeardCheck);

        two.Unread.ShouldBe(2, "the news line, and the digest's own line, neither heard to its end");
    }

    // #179: the log's drop took the entry's lines off the count but left it marked unread.
    [Fact]
    public async Task An_entry_the_log_lets_go_of_is_no_longer_unread()
    {
        var vm = await AwayFromChatTwoAsync();
        var two = ChatNumbered(vm, 2);
        var card = vm.Log.First(e => e.Kind == RavenLogKind.News && e.Chat == two);
        card.IsUnread.ShouldBeTrue();

        for (var i = 0; i < RavenPanelViewModel.MaximumLogEntries; i++)
        {
            vm.Note("A note.");
        }

        vm.Log.ShouldNotContain(card);
        (card.IsUnread, two.Unread).ShouldBe((false, 0));
    }

    // #179: a removed chat kept its pulse timer.
    [Fact]
    public async Task A_removed_chat_s_pulse_timer_goes_with_it()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("e", SessionState.Working, SessionState.Idle); // VideoX, chat 5
        await GraceAsync(vm);
        await Until(() => ChatNumbered(vm, 5).IsNewsPulsing);
        var before = vm.PulseTimers;

        vm.SetWorkspaces([.. new[] { "CodeSwitchX", "ContentAutomatorX", "DiffusionNexus", "RawCutX" }
            .Select((w, i) => (FakeYardDirectory.WorkspaceOf(w), i + 1, w))]);

        vm.PulseTimers.ShouldBe(before - 1);
    }

    /// <summary>
    /// Chat 2, collapsed and talked to, gets a card of two lines: "a" (stale, older than two minutes) and "a2" (fresh,
    /// <paramref name="fresh"/>), and its digest is told and heard. The teller says <paramref name="telling"/>.
    /// </summary>
    private async Task<RavenPanelViewModel> DigestHeardWithAStaleLineAsync(string telling, SessionState fresh = SessionState.Idle)
    {
        var (vm, _) = await TrafficVmAsync();
        vm.CatchUp = true;
        _yard.Show("a2", "ContentAutomatorX", "Task a2");
        _teller.Answer = _ => [new BrainText(telling)];
        vm.SelectedChat = ChatNumbered(vm, 2);
        vm.IsOpen = false;
        _brain.Gate = new TaskCompletionSource(); // the floor is held: the news waits
        Type(vm, "Long question");
        Changes("a", SessionState.Working, SessionState.Idle);
        _time.Advance(ChatNews.MaximumAge + TimeSpan.FromSeconds(1));
        Changes("a2", SessionState.Working, fresh);
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.News));
        await WithinAsync(_voice.WhenQuietAsync());
        await WithinAsync(vm.PendingHeardCheck);
        return vm;
    }

    // Review of #188: a card whose fresh line a digest said kept its stale line unread, so a catch-up said the fresh line
    // again and took the stale one off in its place.
    [Fact]
    public async Task After_a_digest_a_switch_back_tells_nothing_again_and_the_stale_line_still_counts()
    {
        var vm = await DigestHeardWithAStaleLineAsync("Task a2 finished.");
        var two = ChatNumbered(vm, 2);
        two.Unread.ShouldBe(1);
        _time.Advance(TrafficWatcher.DefaultCooldown);

        vm.SelectedChat = ChatNumbered(vm, 3);
        vm.SelectedChat = two;
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());
        await WithinAsync(vm.PendingHeardCheck);

        _teller.Asked.ShouldNotContain(q => q.StartsWith("Catch-up:", StringComparison.Ordinal), "the stale line is never said, the fresh one was");
        two.Unread.ShouldBe(1);
    }

    // Review of #188: a failure said aloud kept the red mark while its card still had a stale line.
    [Fact]
    public async Task A_failure_heard_in_a_digest_leaves_no_red_mark_beside_a_stale_line()
    {
        var vm = await DigestHeardWithAStaleLineAsync("Task a2 failed.", SessionState.Errored);
        var two = ChatNumbered(vm, 2);

        (two.Unread, two.HasFailed).ShouldBe((1, false));
    }

    // Review of #188: a switch away and back while the digest still spoke caught up on the same news after it.
    [Fact]
    public async Task A_switch_back_while_the_digest_speaks_does_not_tell_it_again()
    {
        var (vm, _) = await TrafficVmAsync();
        vm.CatchUp = true;
        _teller.Answer = _ => [new BrainText("Task a finished.")];
        var two = ChatNumbered(vm, 2);
        vm.SelectedChat = two;
        vm.IsOpen = false;
        _speech.Gate = new TaskCompletionSource(); // the digest has not played yet

        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => _speech.Spoken.Count > 0);
        vm.SelectedChat = ChatNumbered(vm, 3);
        vm.SelectedChat = two;
        _speech.Gate.SetResult();
        await WithinAsync(_voice.WhenQuietAsync());
        await WithinAsync(vm.PendingHeardCheck);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        _teller.Asked.ShouldNotContain(q => q.StartsWith("Catch-up:", StringComparison.Ordinal));
        two.Unread.ShouldBe(0);
    }

    // Review of #188: opened on another chat while the digest played, the chat it was heard in kept its badge.
    [Fact]
    public async Task A_digest_heard_while_raven_opened_on_another_chat_clears_its_own()
    {
        var (vm, _) = await TrafficVmAsync();
        _teller.Answer = _ => [new BrainText("Task a finished.")];
        var two = ChatNumbered(vm, 2);
        vm.SelectedChat = two;
        vm.IsOpen = false;
        _speech.Gate = new TaskCompletionSource();

        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => _speech.Spoken.Count > 0);
        vm.OpenChatCommand.Execute(ChatNumbered(vm, 3));
        _speech.Gate.SetResult();
        await WithinAsync(_voice.WhenQuietAsync());
        await WithinAsync(vm.PendingHeardCheck);

        two.Unread.ShouldBe(0);
    }

    // Review of #188: the fallback says only how many things came, not what: it took every line off.
    [Fact]
    public async Task Collapsed_a_catch_up_that_could_only_say_how_many_things_came_leaves_the_marks()
    {
        var vm = await AwayFromChatTwoAsync();
        _teller.Answer = _ => [];
        vm.IsOpen = false;
        var two = ChatNumbered(vm, 2);

        vm.SelectedChat = two;
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());
        await WithinAsync(vm.PendingHeardCheck);

        vm.Log.ShouldContain(e => e.Text == "While you were away, 2 things came in here.");
        two.Unread.ShouldBe(2);
    }
}
