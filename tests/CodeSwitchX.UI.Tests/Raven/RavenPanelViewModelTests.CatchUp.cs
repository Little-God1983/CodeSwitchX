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

    /// <summary>Lets the grace pass once the panel is idle, without waiting for a teller held by its gate.</summary>
    private async Task PassGraceAsync(RavenPanelViewModel vm)
    {
        await Until(() => vm.State == RavenState.Idle);
        _time.Advance(TrafficWatcher.NewsGrace);
    }

    [Fact]
    public async Task Switching_to_a_chat_with_two_unread_news_lines_speaks_one_short_catch_up()
    {
        var vm = await AwayFromChatTwoAsync();

        vm.SelectedChat = ChatNumbered(vm, 2);
        _teller.Asked.ShouldBeEmpty("it waits for the floor, as news does");
        await GraceAsync(vm);
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
        await GraceAsync(vm);

        _teller.Asked.ShouldBeEmpty();
        _speech.Spoken.ShouldBeEmpty();
    }

    [Fact]
    public async Task With_the_setting_off_there_is_no_catch_up_only_the_marks()
    {
        var vm = await AwayFromChatTwoAsync(catchUp: false);

        vm.SelectedChat = ChatNumbered(vm, 2);
        await GraceAsync(vm);

        _teller.Asked.ShouldBeEmpty();
        _speech.Spoken.ShouldBeEmpty();
    }

    [Fact]
    public async Task Inside_the_cooldown_no_catch_up_is_spoken()
    {
        var vm = await AwayFromChatTwoAsync();
        vm.Traffic.Announced(); // Raven just spoke

        vm.SelectedChat = ChatNumbered(vm, 2);
        await GraceAsync(vm);

        _teller.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Muted_no_catch_up_is_spoken()
    {
        var vm = await AwayFromChatTwoAsync();
        vm.IsMuted = true;

        vm.SelectedChat = ChatNumbered(vm, 2);
        await GraceAsync(vm);

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
        await GraceAsync(vm);
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
        await PassGraceAsync(vm);
        await Until(() => _teller.Asked.Count == 1);
        vm.PressMic(TalkInput.MicButton);
        _teller.Gate.SetResult();
        await Until(() => vm.State == RavenState.Listening);

        _speech.Spoken.ShouldBeEmpty();
    }

    /// <summary>A press before the catch-up began stops it too: it is not said once the user is done.</summary>
    [Fact]
    public async Task A_press_before_the_catch_up_began_drops_it()
    {
        _brain.Answer = _ => [new BrainText("Okay.")];
        var vm = await AwayFromChatTwoAsync();

        vm.SelectedChat = ChatNumbered(vm, 2);
        await HoldAsync(vm);
        await WithinAsync(vm.PendingAnswers);
        await GraceAsync(vm);

        _teller.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Switching_on_before_it_is_said_drops_the_catch_up_of_the_chat_left()
    {
        var vm = await AwayFromChatTwoAsync();
        _teller.Gate = new TaskCompletionSource();

        vm.SelectedChat = ChatNumbered(vm, 2);
        await PassGraceAsync(vm);
        await Until(() => _teller.Asked.Count == 1);
        vm.SelectedChat = ChatNumbered(vm, 1);
        _teller.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());

        _speech.Spoken.ShouldBeEmpty();
    }

    /// <summary>Its words already playing stop too when the user leaves: chat 2's catch-up is not heard in chat 1.</summary>
    [Fact]
    public async Task Leaving_while_the_catch_up_plays_hushes_it()
    {
        var vm = await AwayFromChatTwoAsync();
        _speech.Gate = new TaskCompletionSource(); // its audio never comes on its own

        vm.SelectedChat = ChatNumbered(vm, 2);
        await GraceAsync(vm);
        await Until(() => _speech.Spoken.Count > 0);
        vm.SelectedChat = ChatNumbered(vm, 1);

        await WithinAsync(_voice.WhenQuietAsync());
    }

    /// <summary>Two quick switches: the second chat still gets its catch-up once the first one's has stopped.</summary>
    [Fact]
    public async Task A_quick_second_switch_gets_its_own_catch_up()
    {
        var vm = await AwayFromChatTwoAsync();
        Changes("c", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        _time.Advance(TrafficWatcher.DefaultCooldown);
        _teller.Gate = new TaskCompletionSource();
        vm.SelectedChat = ChatNumbered(vm, 2);
        await PassGraceAsync(vm);
        await Until(() => _teller.Asked.Count == 1);

        vm.SelectedChat = ChatNumbered(vm, 3);
        _teller.Gate.SetResult();
        await PassGraceAsync(vm);
        await Until(() => _teller.Asked.Count == 2);

        _teller.Asked[1].ShouldContain("Task c");
    }

    /// <summary>An allow waiting for the user's yes holds the floor: no catch-up talks over its read-back.</summary>
    [Fact]
    public async Task No_catch_up_while_an_allow_waits_for_the_yes()
    {
        var (vm, asks) = await TrafficVmAsync();
        vm.CatchUp = true;
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Permission));
        _yard.Show("a2", "ContentAutomatorX", "Task a2");
        Changes("a2", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        _time.Advance(TrafficWatcher.DefaultCooldown);
        var proposal = asks.Propose("p1");
        await Until(() => asks.IsHeard(proposal));

        vm.SelectedChat = ChatNumbered(vm, 2);
        _time.Advance(TrafficWatcher.NewsGrace);
        await WithinAsync(vm.PendingAnswers);

        _teller.Asked.ShouldNotContain(q => q.StartsWith("Catch-up", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_typed_question_drops_the_catch_up_waiting_and_rests_the_teller()
    {
        _brain.Answer = _ => [new BrainText("Okay.")];
        var vm = await AwayFromChatTwoAsync();

        vm.SelectedChat = ChatNumbered(vm, 2);
        Type(vm, "anything new?");
        await WithinAsync(vm.PendingAnswers);
        await GraceAsync(vm);

        _teller.Asked.ShouldBeEmpty();
        _teller.Rests.ShouldBeGreaterThan(0, "warmed for the catch-up that was dropped");
    }

    [Fact]
    public async Task A_quick_switch_to_a_chat_with_nothing_new_rests_the_teller()
    {
        var vm = await AwayFromChatTwoAsync();
        var rests = _teller.Rests;

        vm.SelectedChat = ChatNumbered(vm, 2);
        vm.SelectedChat = ChatNumbered(vm, 4);

        _teller.Rests.ShouldBeGreaterThan(rests);
    }

    [Fact]
    public async Task Muting_while_it_waits_drops_the_catch_up()
    {
        var vm = await AwayFromChatTwoAsync();

        vm.SelectedChat = ChatNumbered(vm, 2);
        vm.IsMuted = true;
        await GraceAsync(vm);

        _teller.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Turning_the_catch_up_off_drops_the_one_waiting()
    {
        var vm = await AwayFromChatTwoAsync();

        vm.SelectedChat = ChatNumbered(vm, 2);
        vm.CatchUp = false;
        await GraceAsync(vm);

        _teller.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_teller_that_says_nothing_leaves_a_plain_catch_up()
    {
        var vm = await AwayFromChatTwoAsync();
        _teller.Answer = _ => [];

        vm.SelectedChat = ChatNumbered(vm, 2);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        var said = vm.Log.Single(e => e.Kind == RavenLogKind.Raven);
        said.Chat.Number.ShouldBe(2);
        said.Text.ShouldStartWith("While you were away");
        _speech.Spoken.ShouldNotBeEmpty();
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

        lines.ShouldBe(["- A warning: The voice failed."], "Raven's answers were heard as they came; cards are read on their own");
    }
}
