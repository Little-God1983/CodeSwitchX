using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>
/// The catch-up on switching chats (#127): what came in a chat while the user was away, said in a sentence or two when
/// they switch to it, if the setting is on, a pause (#152) after Raven last spoke or a chat made its sound, the cooldown
/// notwithstanding (#143); its cards are then read as usual.
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
        _time.Advance(vm.Traffic.WaitBeforeTelling);
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
        asked.ShouldContain("exactly 2 lines", customMessage: "both chats are summed up");
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

    /// <summary>
    /// #143: the user hears a chat's sound and switches to it inside the cooldown: the switch asks for the catch-up, which is
    /// said once the pause after the sound has passed, not dropped.
    /// </summary>
    [Fact]
    public async Task A_switch_right_after_the_chat_s_sound_is_caught_up_a_pause_later()
    {
        var (vm, _) = await TrafficVmAsync();
        vm.CatchUp = true;
        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's: told as a sound
        await GraceAsync(vm);
        await Until(() => _chime.Plays == 1);
        _time.Advance(TimeSpan.FromSeconds(1)); // heard, and clicked
        vm.Traffic.CooledDown.ShouldBeFalse();

        vm.SelectedChat = ChatNumbered(vm, 2);
        await Until(() => vm.State == RavenState.Idle);
        _time.Advance(TrafficWatcher.NewsGrace); // 2.5 s after the sound
        _teller.Asked.ShouldBeEmpty("the grace has passed, the pause after the sound has not");
        _time.Advance(vm.Traffic.WaitBeforeTelling);
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());

        _teller.Asked.ShouldHaveSingleItem().ShouldStartWith("Catch-up:");
        _speech.Spoken.ShouldNotBeEmpty();
    }

    /// <summary>#143: a switch right after Raven spoke is caught up too, a pause later.</summary>
    [Fact]
    public async Task A_switch_right_after_Raven_spoke_is_caught_up_a_pause_later()
    {
        var vm = await AwayFromChatTwoAsync();
        vm.Traffic.Announced(); // Raven just spoke

        vm.SelectedChat = ChatNumbered(vm, 2);
        await Until(() => vm.State == RavenState.Idle);
        _time.Advance(TrafficWatcher.NewsGrace);
        _teller.Asked.ShouldBeEmpty("the pause after Raven spoke has not passed");
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        _teller.Asked.ShouldHaveSingleItem().ShouldStartWith("Catch-up:");
    }

    /// <summary>
    /// #143: "the chat I'm in also waits for the cooldown" does not hold the catch-up: the switch asked for it, and dropped it
    /// would never be said (the switch marked its lines seen).
    /// </summary>
    [Fact]
    public async Task With_own_news_waiting_for_the_cooldown_a_switch_inside_it_is_still_caught_up()
    {
        var vm = await AwayFromChatTwoAsync();
        vm.Traffic.OwnNewsWaits = true;
        vm.Traffic.Announced(); // Raven just spoke

        vm.SelectedChat = ChatNumbered(vm, 2);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        _teller.Asked.ShouldHaveSingleItem().ShouldStartWith("Catch-up:");
    }

    /// <summary>#143: the catch-up is a switch of its own: with chat news only written, it is still said.</summary>
    [Fact]
    public async Task With_chat_news_only_written_the_catch_up_is_still_said()
    {
        var vm = await AwayFromChatTwoAsync();
        vm.SpeakNews = false;

        vm.SelectedChat = ChatNumbered(vm, 2);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        _teller.Asked.ShouldHaveSingleItem().ShouldStartWith("Catch-up:");
        _speech.Spoken.ShouldNotBeEmpty();
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
        _speech.Spoken.First().ShouldStartWith("Chat ContentAutomatorX finished:", customMessage: "the catch-up comes before the card");
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

        _teller.Asked[1].ShouldStartWith("Catch-up:");
        await WithinAsync(_voice.WhenQuietAsync());
        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.Raven && e.Chat.Number == 3 && e.Text.StartsWith("Chat DiffusionNexus finished:"));
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

    // #301: each chat is said by the app ("Chat ContentAutomatorX failed."), with or without the teller's summary.
    [Fact]
    public async Task A_teller_that_says_nothing_still_says_each_chat_the_most_pressing_first()
    {
        var vm = await AwayFromChatTwoAsync();
        _teller.Answer = _ => [];

        vm.SelectedChat = ChatNumbered(vm, 2);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        var said = vm.Log.Single(e => e.Kind == RavenLogKind.Raven);
        said.Chat.Number.ShouldBe(2);
        said.Text.ShouldBe("Chat ContentAutomatorX failed. Chat ContentAutomatorX finished.");
        _speech.Spoken.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_tellers_lines_follow_the_apps_start_of_each_chat()
    {
        var vm = await AwayFromChatTwoAsync();
        _teller.Answer = _ => [new BrainText("1. the build broke on a missing file\n2. "), new BrainText("the tests pass, and it asks to merge")];

        vm.SelectedChat = ChatNumbered(vm, 2);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        vm.Log.Single(e => e.Kind == RavenLogKind.Raven).Text.ShouldBe(
            "Chat ContentAutomatorX failed: the build broke on a missing file. Chat ContentAutomatorX finished: the tests pass, and it asks to merge.");
        var asked = _teller.Asked.ShouldHaveSingleItem();
        asked.ShouldStartWith("Catch-up:");
        asked.ShouldContain("exactly 2 lines");
        asked.ShouldNotContain("Task a", customMessage: "the title is not said, so the teller is not given it");
    }

    // Review round 1 of #303: summaries went to chats by their place, so a line before them gave each chat another's.
    [Fact]
    public async Task The_tellers_lines_go_to_their_chats_by_number_past_a_lead_in()
    {
        var vm = await AwayFromChatTwoAsync();
        _teller.Answer = _ => [new BrainText("Here are the summaries:\n2. the tests pass\n1. the build broke")];

        vm.SelectedChat = ChatNumbered(vm, 2);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        vm.Log.Single(e => e.Kind == RavenLogKind.Raven).Text.ShouldBe(
            "Chat ContentAutomatorX failed: the build broke. Chat ContentAutomatorX finished: the tests pass.");
    }

    // Review round 1 of #303: a chat in two news cards was told twice.
    [Fact]
    public async Task A_chat_that_finished_twice_while_away_is_told_once()
    {
        var (vm, _) = await TrafficVmAsync();
        vm.CatchUp = true;
        _teller.Answer = _ => [];
        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        Changes("a", SessionState.Idle, SessionState.Working);
        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Count(e => e.Kind == RavenLogKind.News) == 2);
        _time.Advance(TrafficWatcher.DefaultCooldown);

        vm.SelectedChat = ChatNumbered(vm, 2);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        vm.Log.Single(e => e.Kind == RavenLogKind.Raven).Text.ShouldBe("Chat ContentAutomatorX finished.");
    }

    [Fact]
    public async Task More_chats_than_a_reply_says_are_counted_and_stay_unread()
    {
        var (vm, _) = await TrafficVmAsync();
        vm.CatchUp = true;
        _teller.Answer = _ => [];
        foreach (var id in new[] { "a2", "a3", "a4" })
        {
            _yard.Show(id, "ContentAutomatorX", $"Task {id}");
        }

        foreach (var id in new[] { "a", "a2", "a3", "a4" })
        {
            Changes(id, SessionState.Working, SessionState.Idle);
        }

        await GraceAsync(vm);
        await Until(() => ChatNumbered(vm, 2).Unread == 4);
        _time.Advance(TrafficWatcher.DefaultCooldown);

        vm.SelectedChat = ChatNumbered(vm, 2);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        vm.Log.Single(e => e.Kind == RavenLogKind.Raven).Text.ShouldBe(
            "Chat ContentAutomatorX finished. Chat ContentAutomatorX finished. And 2 more.");
    }

    [Fact]
    public async Task A_catch_up_in_the_yard_says_which_window()
    {
        var (vm, _) = await TrafficVmAsync();
        vm.CatchUp = true;
        _yard.Show("z", "LooseWindow", "Task z"); // a window with no chat of its own: its news is the Yard's
        Changes("z", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.YardChat.Unread == 1);
        _time.Advance(TrafficWatcher.DefaultCooldown);
        _teller.Answer = _ => [new BrainText("the tests pass")];

        vm.SelectedChat = vm.YardChat;
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        vm.Log.Single(e => e.Kind == RavenLogKind.Raven).Text.ShouldBe("Chat LooseWindow finished: the tests pass.");
    }

    [Fact]
    public void The_catch_up_tells_news_and_warnings_not_answers_or_cards()
    {
        var at = DateTimeOffset.UnixEpoch;
        var warning = new RavenLogEntry(RavenLogKind.Warning, "The voice failed.", at);
        var (lines, warnings) = RavenPanelViewModel.CatchUpItems([
            new RavenLogEntry(RavenLogKind.Raven, "It is green.", at),
            warning,
            new RavenLogEntry(RavenLogKind.Note, "Installing…", at),
            new RavenLogEntry(RavenLogKind.Permission, "asks", at),
        ]);

        lines.ShouldBeEmpty();
        warnings.ShouldBe([warning], "Raven's answers were heard as they came; cards are read on their own");
    }

    // Review round 2 of #188: a warning had no heard mark, so a catch-up worded again said it again.
    [Fact]
    public void What_was_heard_already_is_not_in_the_catch_up()
    {
        var at = DateTimeOffset.UnixEpoch;
        var workspace = Guid.NewGuid();
        var heard = new ChatNewsLine("s1", workspace, "CodeSwitchX", "Weather discussion", ChatNewsKind.Finished, null, null, false);
        var untold = new ChatNewsLine("s2", workspace, "CodeSwitchX", "Release notes", ChatNewsKind.Failed, null, null, false);
        var news = new RavenLogEntry(RavenLogKind.News, "Chat news", at) { Lines = [heard, untold] };
        news.MarkHeard([heard]);
        var warning = new RavenLogEntry(RavenLogKind.Warning, "The voice failed.", at);
        warning.MarkHeard();

        var (lines, warnings) = RavenPanelViewModel.CatchUpItems([news, warning]);

        lines.ShouldBe([untold]);
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Turning_the_catch_up_off_also_leaves_the_cards_it_found_unread_out()
    {
        var (vm, asks) = await TrafficVmAsync();
        vm.CatchUp = true;
        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Permission));
        _time.Advance(TrafficWatcher.DefaultCooldown);

        vm.SelectedChat = ChatNumbered(vm, 2);
        vm.CatchUp = false;
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        _speech.Spoken.ShouldBeEmpty();
    }
}
