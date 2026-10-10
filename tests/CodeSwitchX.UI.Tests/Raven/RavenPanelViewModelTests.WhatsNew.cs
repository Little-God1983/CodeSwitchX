using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>#243: "what's new?" gives the brain the news not read yet in every window's chat, and counts it read once heard.</summary>
public sealed partial class RavenPanelViewModelTests
{
    /// <summary>A brain answer that asks whats_new in its turn, as the brain would, then tells it.</summary>
    private static IEnumerable<BrainEvent> TellsWhatsNew(RavenPanelViewModel vm)
    {
        vm.WhatsNewForBrain(null, null);
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

        var said = vm.WhatsNewForBrain(FakeYardDirectory.WorkspaceOf("DiffusionNexus"), null);

        said.ShouldStartWith("New in chat 3, DiffusionNexus (the chat the user is in):\n- DiffusionNexus, chat \"Task c\": finished");
        said.ShouldContain("New in chat 2, ContentAutomatorX:\n- ContentAutomatorX, chat \"Task a\": finished");
        said.ShouldNotContain("Done.", Case.Sensitive, "what a chat said never goes to the brain that acts");
    }

    [Fact]
    public async Task Whats_new_of_one_chat_gives_only_its_news()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's
        Changes("c", SessionState.Working, SessionState.Idle); // chat 3's
        await GraceAsync(vm);

        var said = vm.WhatsNewForBrain(null, 2);

        said.ShouldContain("Task a");
        said.ShouldNotContain("Task c");
    }

    [Fact]
    public async Task Whats_new_with_nothing_new_and_nothing_waiting_says_so()
    {
        var (vm, _) = await TrafficVmAsync();

        vm.WhatsNewForBrain(null, null).ShouldBe(RavenPanelViewModel.NothingNewLine);
    }

    [Fact]
    public async Task Whats_new_tells_what_waits_for_the_user()
    {
        var (vm, asks) = await NextQuestionVmAsync();
        _ = await AsksFruitAsync(vm, asks, "d");

        var said = vm.WhatsNewForBrain(null, null);

        said.ShouldContain("- chat 3: RawCutX, chat \"Task d\": ");
        said.ShouldContain("Which fruit?");
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

        ChatNumbered(vm, 2).Unread.ShouldBe(0);
    }

    // A whats_new nobody's answer tells is not read: it was not heard
    [Fact]
    public async Task News_whats_new_gave_outside_an_answer_stays_unread()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's
        await GraceAsync(vm);

        vm.WhatsNewForBrain(null, null);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        ChatNumbered(vm, 2).Unread.ShouldBeGreaterThan(0);
    }
}
