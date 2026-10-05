using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>
/// The catch-up on switching chats (#127): what came in a chat while the user was away, said in a sentence or two when
/// they switch to it, if the setting is on and the traffic watcher's cooldown is over; its cards are then read as usual.
/// </summary>
public sealed partial class RavenPanelViewModelTests
{
    /// <summary>Two chats of chat 2's window finish while the user is in chat 1; the cooldown after the chime is over.</summary>
    private async Task<RavenPanelViewModel> AwayFromChatTwoAsync(bool catchUp = true)
    {
        var (vm, _) = await TrafficVmAsync();
        vm.CatchUp = catchUp;
        _yard.Show("a2", "ContentAutomatorX", "Task a2");
        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("a2", SessionState.Working, SessionState.Errored);
        await GraceAsync(vm);
        await Until(() => ChatNumbered(vm, 2).Unread == 2);
        _time.Advance(TrafficWatcher.DefaultCooldown);
        return vm;
    }

    [Fact]
    public async Task Switching_to_a_chat_with_two_unread_news_lines_speaks_one_short_catch_up()
    {
        var vm = await AwayFromChatTwoAsync();

        vm.SelectedChat = ChatNumbered(vm, 2);
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());

        var asked = _teller.Asked.ShouldHaveSingleItem();
        asked.ShouldStartWith("Catch-up:");
        asked.ShouldContain("Task a\"");
        asked.ShouldContain("Task a2");
        _speech.Spoken.ShouldNotBeEmpty();
        vm.Log.Single(e => e.Kind == RavenLogKind.Raven).Chat.Number.ShouldBe(2);
    }

    [Fact]
    public async Task Switching_to_a_chat_with_nothing_new_says_nothing()
    {
        var vm = await AwayFromChatTwoAsync();

        vm.SelectedChat = ChatNumbered(vm, 3);
        await WithinAsync(vm.PendingAnswers);

        _teller.Asked.ShouldBeEmpty();
        _speech.Spoken.ShouldBeEmpty();
    }

    [Fact]
    public async Task With_the_setting_off_there_is_no_catch_up_only_the_marks()
    {
        var vm = await AwayFromChatTwoAsync(catchUp: false);

        vm.SelectedChat = ChatNumbered(vm, 2);
        await WithinAsync(vm.PendingAnswers);

        _teller.Asked.ShouldBeEmpty();
        _speech.Spoken.ShouldBeEmpty();
    }

    [Fact]
    public async Task Inside_the_cooldown_no_catch_up_is_spoken()
    {
        var vm = await AwayFromChatTwoAsync();
        vm.Traffic.Announced(); // Raven just spoke

        vm.SelectedChat = ChatNumbered(vm, 2);
        await WithinAsync(vm.PendingAnswers);

        _teller.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Muted_no_catch_up_is_spoken()
    {
        var vm = await AwayFromChatTwoAsync();
        vm.IsMuted = true;

        vm.SelectedChat = ChatNumbered(vm, 2);
        await WithinAsync(vm.PendingAnswers);

        _teller.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_card_that_came_while_away_is_read_after_the_catch_up()
    {
        var (vm, asks) = await TrafficVmAsync();
        vm.CatchUp = true;
        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Permission));
        _time.Advance(TrafficWatcher.DefaultCooldown);

        vm.SelectedChat = ChatNumbered(vm, 2);
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        _teller.Asked.ShouldHaveSingleItem().ShouldNotContain("npm test", Case.Sensitive, "the card is read on its own");
        string.Join(" ", _speech.Spoken).ShouldContain("npm test");
        _speech.Spoken.First().ShouldStartWith("Told: Catch-up");
    }

    [Fact]
    public async Task A_press_stops_the_catch_up()
    {
        var vm = await AwayFromChatTwoAsync();
        _teller.Gate = new TaskCompletionSource();

        vm.SelectedChat = ChatNumbered(vm, 2);
        await Until(() => _teller.Asked.Count == 1);
        vm.PressMic(TalkInput.MicButton);
        _teller.Gate.SetResult();
        await Until(() => vm.State == RavenState.Listening);

        _speech.Spoken.ShouldBeEmpty();
    }

    [Fact]
    public async Task Switching_on_before_it_is_said_drops_the_catch_up_of_the_chat_left()
    {
        var vm = await AwayFromChatTwoAsync();
        _teller.Gate = new TaskCompletionSource();

        vm.SelectedChat = ChatNumbered(vm, 2);
        await Until(() => _teller.Asked.Count == 1);
        vm.SelectedChat = ChatNumbered(vm, 1);
        _teller.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());

        _speech.Spoken.ShouldBeEmpty();
    }

    [Fact]
    public void The_catch_up_is_worded_from_news_answers_and_warnings_not_cards()
    {
        var at = DateTimeOffset.UnixEpoch;
        var lines = RavenPanelViewModel.CatchUpLines([
            new RavenLogEntry(RavenLogKind.Raven, "It is green.", at),
            new RavenLogEntry(RavenLogKind.Warning, "The voice failed.", at),
            new RavenLogEntry(RavenLogKind.Note, "Installing…", at),
            new RavenLogEntry(RavenLogKind.Permission, "asks", at),
        ]);

        lines.ShouldBe(["- Raven answered: \"It is green.\"", "- A warning: The voice failed."]);
    }
}
