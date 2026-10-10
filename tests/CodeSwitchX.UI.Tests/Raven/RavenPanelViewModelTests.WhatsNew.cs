using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Dictation;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>#243: "what's new?" gives the brain the news not read yet in every window's chat, and counts it read once heard.</summary>
public sealed partial class RavenPanelViewModelTests
{
    /// <summary>A brain answer that asks whats_new in its turn, as the brain would, then tells it.</summary>
    /// <summary>The brain of the chat the user asks in calls whats_new, with that chat's key, as a real brain sends it.</summary>
    private static IEnumerable<BrainEvent> TellsWhatsNew(RavenPanelViewModel vm)
    {
        vm.WhatsNewForBrain(YardMcp.ChatKey(vm.SelectedChat.WorkspaceId, vm.SelectedChat == vm.YardChat), null);
        yield return new BrainText("Chat 2 finished.");
    }

    [Fact]
    public async Task Whats_new_gives_every_window_s_unread_news_muted_ones_too_the_chat_asked_from_first_and_facts_only()
    {
        var (vm, _) = await TrafficVmAsync();
        vm.MuteChat(3, true);
        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's
        Changes("c", SessionState.Working, SessionState.Idle); // chat 3's, muted
        await GraceAsync(vm);

        var said = vm.WhatsNewForBrain(FakeYardDirectory.WorkspaceOf("DiffusionNexus").ToString(), null);

        said.ShouldStartWith("New in chat 3, DiffusionNexus:\n- DiffusionNexus, chat \"Task c\": finished"); // asked from there; the user is in chat 1
        said.ShouldContain("New in chat 2, ContentAutomatorX:\n- ContentAutomatorX, chat \"Task a\": finished");
        said.ShouldNotContain("Done.", Case.Sensitive, "what a chat said never goes to the brain that acts");
    }

    // #256: news still held back (for the pause, or a busy floor) was missed: "nothing new", and a moment later the digest told it
    [Fact]
    public async Task Whats_new_tells_the_news_still_held_back_and_writes_its_card_unsaid()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's, held for the grace
        var before = _speech.Spoken.Count;

        await WithinAsync(vm.WriteHeldNewsAsync());
        var said = vm.WhatsNewForBrain(YardMcp.OverviewChat, null);

        said.ShouldContain("New in chat 2, ContentAutomatorX:\n- ContentAutomatorX, chat \"Task a\": finished");
        ChatNumbered(vm, 2).Unread.ShouldBeGreaterThan(0, "its card is written in its chat, read once the answer telling it is heard");
        await GraceAsync(vm);
        _speech.Spoken.Count.ShouldBe(before, "the brain tells it: Raven's own digest has nothing left to say");
    }

    [Fact]
    public async Task Whats_new_of_one_chat_gives_only_its_news()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's
        Changes("c", SessionState.Working, SessionState.Idle); // chat 3's
        await GraceAsync(vm);

        var said = vm.WhatsNewForBrain(YardMcp.OverviewChat, 2);

        said.ShouldContain("Task a");
        said.ShouldNotContain("Task c");
    }

    [Fact]
    public async Task Whats_new_with_nothing_new_and_nothing_waiting_says_so()
    {
        var (vm, _) = await TrafficVmAsync();

        vm.WhatsNewForBrain(YardMcp.OverviewChat, null).ShouldBe(RavenPanelViewModel.NothingNewLine);
    }

    [Fact]
    public async Task Whats_new_tells_what_waits_for_the_user()
    {
        var (vm, asks) = await NextQuestionVmAsync();
        _ = await AsksFruitAsync(vm, asks, "d");

        var said = vm.WhatsNewForBrain(YardMcp.OverviewChat, null);

        said.ShouldContain("- chat 3: RawCutX, chat \"Task d\" asks a question");
        said.ShouldNotContain("Which fruit?", Case.Sensitive, "the chat's own words never go to the brain that acts");
    }

    // Decided on #243: once the answer that tells it is heard to its end, the news counts as read and its badge clears
    [Fact]
    public async Task The_news_whats_new_gave_is_read_once_the_answer_is_heard()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's
        await GraceAsync(vm);
        ChatNumbered(vm, 2).Unread.ShouldBeGreaterThan(0);
        _brain.Answer = _ => TellsWhatsNew(vm);

        Type(vm, "what's new?");
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());
        await WithinAsync(vm.PendingHeardCheck);

        ChatNumbered(vm, 2).Unread.ShouldBe(0);
    }

    // Decided on #243: an answer only written (muted, typed) counts as read when it is written
    [Fact]
    public async Task The_news_whats_new_gave_is_read_at_once_when_the_answer_is_only_written()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's
        await GraceAsync(vm);
        vm.IsMuted = true;
        _brain.Answer = _ => TellsWhatsNew(vm);

        Type(vm, "what's new?");
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(vm.PendingHeardCheck);

        ChatNumbered(vm, 2).Unread.ShouldBe(0);
    }

    // Review of #243: an answer cut off by words the user typed told nothing to its end: the news stays unread
    [Fact]
    public async Task The_news_of_an_answer_cut_off_stays_unread()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's
        await GraceAsync(vm);
        vm.IsMuted = true;
        _brain.Pause = new TaskCompletionSource();
        _brain.Answer = q => q.Contains("new") ? TellsWhatsNewAtLength(vm) : [new BrainText("It is noon.")];

        Type(vm, "what's new?");
        await Until(() => vm.Log.Any(e => e.Text.StartsWith("Chat 2", StringComparison.Ordinal)));
        Type(vm, "what time is it?"); // takes the floor
        _brain.Pause.SetResult();
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(vm.PendingHeardCheck);

        ChatNumbered(vm, 2).Unread.ShouldBeGreaterThan(0);
    }

    /// <summary>As <see cref="TellsWhatsNew"/>, in two parts: the second waits for the brain's pause.</summary>
    private static IEnumerable<BrainEvent> TellsWhatsNewAtLength(RavenPanelViewModel vm)
    {
        vm.WhatsNewForBrain(YardMcp.OverviewChat, null);
        yield return new BrainText("Chat 2 finished. ");
        yield return new BrainText("That is all.");
    }

    // Review of #243: muted, an answer said aloud that the user cut off by talking was not heard: the news stays unread
    [Fact]
    public async Task Muted_an_answer_said_aloud_and_cut_off_by_talking_leaves_the_news_unread()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's
        await GraceAsync(vm);
        vm.IsMuted = true;
        vm.IsOpen = false; // not seen either
        _speech.Gate = new TaskCompletionSource(); // the answer is being heard
        _brain.Answer = q => q.Contains("new") ? TellsWhatsNew(vm) : [];

        Transcribes(Task.FromResult(new DictationResult("What's new?", TimeSpan.FromSeconds(1))));
        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);
        Speak();
        _time.Advance(Hold);
        await WithinAsync(vm.ReleaseMicAsync(TalkInput.MicButton));
        await WithinAsync(vm.PendingTranscriptions);
        await WithinAsync(vm.PendingAnswers);
        await Until(() => _speech.Spoken.Count > 0);
        vm.PressMic(TalkInput.MicButton); // the user talks over it
        _speech.Gate.TrySetResult();
        await WithinAsync(_voice.WhenQuietAsync());
        await WithinAsync(vm.PendingHeardCheck);

        ChatNumbered(vm, 2).Unread.ShouldBeGreaterThan(0);
        await vm.ReleaseMicAsync(TalkInput.MicButton);
    }

    // Review of #243: neither heard nor seen (collapsed, no voice to speak it), the news stays unread, as the answer does
    [Fact]
    public async Task Whats_new_told_in_an_answer_neither_heard_nor_seen_stays_unread()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's
        await GraceAsync(vm);
        vm.IsMuted = true;
        vm.IsOpen = false;
        _brain.Answer = _ => TellsWhatsNew(vm);

        Type(vm, "what's new?"); // typed, muted: only written, in a panel nobody looks at
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(vm.PendingHeardCheck);

        ChatNumbered(vm, 2).Unread.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Whats_new_of_one_chat_with_nothing_says_so_for_that_chat()
    {
        var (vm, _) = await TrafficVmAsync();

        vm.WhatsNewForBrain(YardMcp.OverviewChat, 2).ShouldStartWith("Nothing new in chat 2");
    }

    [Fact]
    public async Task Whats_new_of_a_chat_that_is_not_there_says_so()
    {
        var (vm, _) = await TrafficVmAsync();

        vm.WhatsNewForBrain(YardMcp.OverviewChat, 9).ShouldBe("There is no chat 9.");
    }

    // A whats_new nobody's answer tells is not read: it was not heard
    [Fact]
    public async Task News_whats_new_gave_outside_an_answer_stays_unread()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's
        await GraceAsync(vm);

        vm.WhatsNewForBrain(YardMcp.OverviewChat, null);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        ChatNumbered(vm, 2).Unread.ShouldBeGreaterThan(0);
    }
}
