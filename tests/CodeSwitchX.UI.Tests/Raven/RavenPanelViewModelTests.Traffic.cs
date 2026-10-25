using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>
/// The traffic watcher (#125): the chat the user is in speaks, other chats are never spoken. Every chat's sound plays
/// through one queue once the floor is free (#306); the chat the user is in plays its sound right before its line.
/// </summary>
public sealed partial class RavenPanelViewModelTests
{
    private readonly FakeSounds _sounds = new();
    private readonly SoundPicks _picks = new();

    /// <summary>Lets the queue's grace pass once the panel is idle, so a sound waiting for the floor plays (#306).</summary>
    private async Task SoundGraceAsync(RavenPanelViewModel vm)
    {
        await Until(() => vm.State is RavenState.Idle or RavenState.Attending);
        _time.Advance(TrafficWatcher.NewsGrace);
        await WithinAsync(vm.PendingAnswers);
    }

    /// <summary>Lets the gap pass to the next sound of the run.</summary>
    private void Gap() => _time.Advance(SoundQueue.Gap);

    /// <summary>Five windows, each with a chat; the user is in chat 1, CodeSwitchX's, where "b" runs.</summary>
    private async Task<(RavenPanelViewModel Vm, ChatAsks Asks)> TrafficVmAsync(IChatBrains? brains = null)
    {
        string[] workspaces = ["CodeSwitchX", "ContentAutomatorX", "DiffusionNexus", "RawCutX", "VideoX"];
        string[] ids = ["b", "a", "c", "d", "e"];
        for (var i = 0; i < ids.Length; i++)
        {
            _yard.Show(ids[i], workspaces[i], $"Task {ids[i]}");
        }

        var news = new ChatNews(_bus, _yard, _time, _ => "Done.");
        var asks = new ChatAsks(_bus, _time) { Takes = _ => true };
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, news, _teller, asks: asks, yard: _yard, brains: brains, sounds: _sounds, picks: _picks,
            cues: new ChatSoundCues(_bus, news, _time));
        await WithinAsync(vm.RefreshMicrophonesAsync());
        _time.Advance(TimeSpan.FromSeconds(1)); // the chats' changes come after the app started
        vm.SetWorkspaces([.. workspaces.Select((w, i) => (FakeYardDirectory.WorkspaceOf(w), i + 1, w))]);
        vm.SelectedChat = ChatNumbered(vm, 1);
        _teller.Answer = q => [new BrainText($"Told: {q}")];
        return (vm, asks);
    }

    [Fact]
    public async Task Five_chats_returning_at_once_speak_only_the_chat_the_user_is_in()
    {
        var (vm, _) = await TrafficVmAsync();

        foreach (var id in new[] { "a", "b", "c", "d", "e" })
        {
            Changes(id, SessionState.Working, SessionState.Idle);
        }

        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Raven));
        await WithinAsync(_voice.WhenQuietAsync());

        var digest = _teller.Asked.ShouldHaveSingleItem();
        digest.ShouldContain("exactly 1 line", customMessage: "chat 1's news only");
        string.Join(" ", _speech.Spoken).ShouldStartWith("Chat CodeSwitchX finished:");
        string.Join(" ", _speech.Spoken).ShouldNotContain("ContentAutomatorX");
        _sounds.Ids.ShouldBe(["marimba-rise"], "chat 1's sound opened its line; the others' sounds wait for the floor after it");
        vm.Chats.Where(c => c.Number is >= 2 and <= 5).ShouldAllBe(c => c.Unread > 0);
        vm.Log.Single(e => e.Kind == RavenLogKind.Raven).Chat.Number.ShouldBe(1);
    }

    /// <summary>#139: another window's news is never spoken, so it starts no teller; the chat the user is in has it warm.</summary>
    [Fact]
    public async Task Only_news_of_the_chat_the_user_is_in_warms_the_teller()
    {
        await TrafficVmAsync();

        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's: the dispatcher runs the arrival at once
        _teller.WarmUps.ShouldBe(0);

        Changes("b", SessionState.Working, SessionState.Idle); // chat 1's, where the user is
        _teller.WarmUps.ShouldBe(1);
    }

    /// <summary>Switched away before the chat's news is told, the teller warmed for it rests: nothing waits for it there.</summary>
    [Fact]
    public async Task Switching_away_before_the_news_is_told_rests_the_teller()
    {
        var (vm, _) = await TrafficVmAsync();
        vm.PressMic(TalkInput.MicButton); // the floor is busy: the news waits
        Changes("b", SessionState.Working, SessionState.Idle);
        _teller.WarmUps.ShouldBe(1);

        vm.SelectedChat = ChatNumbered(vm, 3);

        _teller.Rests.ShouldBe(1);
    }

    /// <summary>Switched to the news's chat before it is told, the teller is warmed there: its first word is not slower.</summary>
    [Fact]
    public async Task Switching_to_the_chat_whose_news_waits_warms_the_teller()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's, not told yet
        _teller.WarmUps.ShouldBe(0);

        vm.SelectedChat = ChatNumbered(vm, 2);

        _teller.WarmUps.ShouldBe(1);
    }

    /// <summary>News of a chat on no workspace is on no tile, so never told: it warms the teller nowhere, chat 0 included.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task News_of_a_chat_on_no_workspace_warms_no_teller(bool inChatZero)
    {
        var (vm, _) = await TrafficVmAsync();
        if (inChatZero)
        {
            vm.SelectedChat = vm.YardChat;
        }

        _bus.Publish(new CodeSwitchX.Core.Messaging.SessionChanged(ChatNewsTests.Chat("loose", SessionState.Working, _time.GetUtcNow()),
            ChatNewsTests.Chat("loose", SessionState.Idle, _time.GetUtcNow())));

        _teller.WarmUps.ShouldBe(0);
    }

    /// <summary>Other windows' news, while the chat's own waits, warms the teller no more than the chat's own did.</summary>
    [Fact]
    public async Task Other_windows_news_does_not_warm_the_teller_again()
    {
        await TrafficVmAsync();
        Changes("b", SessionState.Working, SessionState.Idle); // chat 1's
        var warm = _teller.WarmUps;

        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("c", SessionState.Working, SessionState.Idle);

        _teller.WarmUps.ShouldBe(warm);
    }

    [Fact]
    public async Task Other_chats_returning_while_the_user_s_chat_is_quiet_each_sound_in_turn()
    {
        var (vm, _) = await TrafficVmAsync();

        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("c", SessionState.Working, SessionState.Idle);
        Changes("d", SessionState.Working, SessionState.Errored);
        await GraceAsync(vm);
        await Until(() => vm.Log.Count(e => e.Kind == RavenLogKind.News) == 3);
        await SoundGraceAsync(vm);
        _sounds.Ids.ShouldBe(["marimba-rise"]);
        Gap();
        Gap();

        _sounds.Ids.ShouldBe(["marimba-rise", "marimba-rise", "marimba-fall"]);
        _teller.Asked.ShouldBeEmpty("other chats are never spoken");
        _speech.Spoken.ShouldBeEmpty();
        ChatNumbered(vm, 4).HasFailed.ShouldBeTrue("the list carries it");
    }

    /// <summary>#306: a sound holds what Raven says on its own for the pause, but the next sound follows after the gap.</summary>
    [Fact]
    public async Task A_sound_restarts_the_pause_for_raven_s_own_speech_but_not_for_the_next_sound()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("c", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await SoundGraceAsync(vm);

        _sounds.Plays.ShouldBe(1);
        vm.Traffic.PauseLeft.ShouldBe(TrafficWatcher.DefaultPause);
        Gap();
        _sounds.Plays.ShouldBe(2);
    }

    /// <summary>#292: an answer sent to the voice holds the floor before its first audio: no chime lands just before it.</summary>
    [Fact]
    public async Task Another_chat_s_news_waits_for_an_answer_sent_but_not_heard_yet()
    {
        _brain.Answer = _ => [new BrainText("Sure.")];
        var (vm, _) = await TrafficVmAsync();
        _speech.Gate = new TaskCompletionSource(); // the voice is still making the first audio
        Type(vm, "hello");
        await WithinAsync(vm.PendingAnswers);
        await Until(() => _speech.Spoken.Count == 1);

        Changes("a", SessionState.Working, SessionState.Idle);
        _time.Advance(vm.Traffic.WaitBeforeTelling);
        await WithinAsync(vm.PendingAnswers);

        _sounds.Plays.ShouldBe(0);
        vm.Log.ShouldNotContain(e => e.Kind == RavenLogKind.News, "told once the answer was heard");

        _speech.Gate.SetResult();
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.News));
    }

    /// <summary>#292: an answer that never got its first audio gives the floor back.</summary>
    [Fact]
    public async Task An_answer_the_voice_could_not_say_gives_the_floor_back()
    {
        _brain.Answer = _ => [new BrainText("Sure.")];
        var (vm, _) = await TrafficVmAsync();
        _speech.Gate = new TaskCompletionSource();
        _speech.Fails = new InvalidOperationException("the sidecar broke");
        Type(vm, "hello");
        await WithinAsync(vm.PendingAnswers);
        Changes("a", SessionState.Working, SessionState.Idle);

        _speech.Gate.SetResult();
        await GraceAsync(vm);

        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.News));
        await SoundGraceAsync(vm);
        _sounds.Plays.ShouldBe(1);
    }

    [Fact]
    public async Task A_slot_picked_none_plays_nothing_and_the_chat_is_still_marked()
    {
        var (vm, _) = await TrafficVmAsync();
        _picks.Set(SoundSlot.Finishes, null);

        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.News));
        await SoundGraceAsync(vm);

        _sounds.Plays.ShouldBe(0);
        ChatNumbered(vm, 2).Unread.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task The_panel_mute_clears_the_queue_and_queues_nothing()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("c", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await SoundGraceAsync(vm);
        _sounds.Plays.ShouldBe(1);

        vm.IsMuted = true;
        _time.Advance(TimeSpan.FromSeconds(5));
        Changes("d", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await SoundGraceAsync(vm);

        _sounds.Plays.ShouldBe(1, "chat 3's sound was dropped, chat 4's never queued");
    }

    /// <summary>Review of #306: a chat on no Yard workspace (a terminal claude, a script) is in no Raven chat: it makes no sound.</summary>
    [Fact]
    public async Task A_chat_on_no_workspace_makes_no_sound()
    {
        var (vm, _) = await TrafficVmAsync();
        var turn = _time.GetUtcNow();
        var idle = ChatNewsTests.Chat("x", SessionState.Idle, turn) with { TurnStartedAt = turn };
        var working = ChatNewsTests.Chat("x", SessionState.Working, turn) with { TurnStartedAt = turn };

        _bus.Publish(new SessionChanged(idle with { TurnStartedAt = null }, working));
        _bus.Publish(new SessionChanged(working, idle));
        await GraceAsync(vm);
        await SoundGraceAsync(vm);
        Gap();

        _sounds.Plays.ShouldBe(0);
    }

    /// <summary>#306: the sounds are how to follow chats without speech: chat news only written still plays them.</summary>
    [Fact]
    public async Task Speak_chat_news_off_still_plays_the_chat_s_sounds()
    {
        var (vm, _) = await TrafficVmAsync();
        vm.SpeakNews = false;

        Changes("b", SessionState.Working, SessionState.Idle); // chat 1's, where the user is
        await GraceAsync(vm);
        await SoundGraceAsync(vm);

        _sounds.Ids.ShouldBe(["marimba-rise"]);
        _speech.Spoken.ShouldBeEmpty();
    }

    /// <summary>#306: the chat the user is in plays its sound as its line's prelude, never from the queue as well.</summary>
    [Fact]
    public async Task The_chat_i_am_in_plays_its_sound_right_before_its_news_line()
    {
        var (vm, _) = await TrafficVmAsync();

        Changes("b", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());
        await SoundGraceAsync(vm);

        _sounds.Ids.ShouldBe(["marimba-rise"]);
        string.Join(" ", _speech.Spoken).ShouldStartWith("Chat CodeSwitchX finished:");
    }

    /// <summary>#306 review focus: switched away before its line, the chat's sound plays as another chat's, once.</summary>
    [Fact]
    public async Task Switching_away_releases_a_sound_held_for_its_line()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("b", SessionState.Working, SessionState.Idle);

        vm.SelectedChat = ChatNumbered(vm, 2);
        await GraceAsync(vm);
        await SoundGraceAsync(vm);
        Gap();

        _sounds.Ids.ShouldBe(["marimba-rise"]);
        _teller.Asked.ShouldBeEmpty("chat 1's news is another chat's now: only written");
    }

    /// <summary>#306 review focus: a question answered in VS Code before its sound's turn is not sounded late.</summary>
    [Fact]
    public async Task A_question_answered_in_vs_code_drops_its_waiting_sound()
    {
        var (vm, asks) = await TrafficVmAsync();
        using var hook = new CancellationTokenSource();
        _ = asks.HoldAsync(PermittingIn("a", "p1"), hook.Token);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Permission));

        await hook.CancelAsync(); // answered in its tab: the hook gave up
        await Until(() => vm.OpenQuestions == 0);
        await GraceAsync(vm);
        await SoundGraceAsync(vm);

        _sounds.Plays.ShouldBe(0);
    }

    [Fact]
    public async Task Another_chat_s_card_is_not_read_out_makes_the_sound_and_the_brain_knows_it()
    {
        _brain.Answer = _ => [new BrainText("Which one?")];
        var (vm, asks) = await TrafficVmAsync();

        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Permission));
        await GraceAsync(vm);

        _sounds.Ids.ShouldBe(["bell-pair"]);
        _speech.Spoken.ShouldBeEmpty();
        Type(vm, "allow it");
        await WithinAsync(vm.PendingAnswers);
        _brain.Asked.ShouldHaveSingleItem().ShouldContain("(ask id p1)");
    }

    [Fact]
    public async Task The_chat_s_own_cards_are_read_out_one_at_a_time()
    {
        var (vm, asks) = await TrafficVmAsync();

        _ = asks.HoldAsync(PermittingIn("b", "p1"), CancellationToken.None);
        _ = asks.HoldAsync(PermittingIn("b", "p2"), CancellationToken.None);
        await Until(() => vm.Log.Count(e => e.Kind == RavenLogKind.Permission) == 2);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());
        _speech.Spoken.Count.ShouldBe(1, "one card, then the floor is free again");

        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());
        _speech.Spoken.Count.ShouldBe(2);
        _sounds.Ids.ShouldBe(["bell-pair"], "one sound for the chat's prompts, right before the first is read");
    }

    [Fact]
    public async Task A_card_that_comes_while_the_user_talks_sounds_once_the_floor_is_free()
    {
        _brain.Answer = _ => [new BrainText("Okay.")];
        var (vm, asks) = await TrafficVmAsync();

        vm.PressMic(TalkInput.MicButton);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Permission));
        Speak();
        _time.Advance(Hold);
        await vm.ReleaseMicAsync(TalkInput.MicButton);
        await WithinAsync(vm.PendingAnswers);
        await GraceAsync(vm);
        await SoundGraceAsync(vm);

        _sounds.Ids.ShouldBe(["bell-pair"], "the queue keeps it while the floor is busy (#306)");
    }

    [Fact]
    public async Task The_chat_s_own_card_is_not_read_out_once_the_user_left_the_chat()
    {
        _brain.Answer = _ => [new BrainText("Okay.")];
        var (vm, asks) = await TrafficVmAsync();

        vm.PressMic(TalkInput.MicButton);
        _ = asks.HoldAsync(PermittingIn("b", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Permission));
        vm.SelectedChat = ChatNumbered(vm, 2);
        Speak();
        _time.Advance(Hold);
        await vm.ReleaseMicAsync(TalkInput.MicButton);
        await WithinAsync(vm.PendingAnswers);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        _speech.Spoken.ShouldBe(["Okay."]);
    }
}

public sealed partial class RavenPanelViewModelTests
{
    /// <summary>A long command, which the teller words.</summary>
    private ChatAsk LongCommandIn(string session, string id) => new(id,
        new HookEvent { SessionId = session, EventName = "PermissionRequest", At = _time.GetUtcNow(), ToolName = "Bash", ToolInputHash = id },
        [], new ChatPermission("Bash", "run a command",
            "for f in $(git ls-files '*.cs'); do dotnet format --include \"$f\" --verify-no-changes || echo \"$f\" >> unformatted.txt; done && sort -u unformatted.txt",
            null));

    [Fact]
    public async Task A_long_command_s_card_waiting_behind_another_rests_the_teller_when_it_ends_unread()
    {
        var (vm, asks) = await TrafficVmAsync();
        using var second = new CancellationTokenSource();
        _ = asks.HoldAsync(PermittingIn("b", "p1"), CancellationToken.None);
        _ = asks.HoldAsync(LongCommandIn("b", "p2"), second.Token);
        await Until(() => vm.Log.Count(e => e.Kind == RavenLogKind.Permission) == 2);
        _teller.WarmUps.ShouldBeGreaterThan(0);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());
        _teller.Rests.ShouldBe(0, "the long command is still to be read");

        await second.CancelAsync(); // answered in VS Code

        await Until(() => _teller.Rests == 1);
    }

    [Fact]
    public async Task Every_card_of_the_chat_reaches_the_brain_before_each_is_read()
    {
        _brain.Answer = _ => [new BrainText("Denied.")];
        var (vm, asks) = await TrafficVmAsync();
        vm.IsMuted = true;
        _ = asks.HoldAsync(PermittingIn("b", "p1"), CancellationToken.None);
        _ = asks.HoldAsync(PermittingIn("b", "p2"), CancellationToken.None);
        await Until(() => vm.Log.Count(e => e.Kind == RavenLogKind.Permission) == 2);
        await GraceAsync(vm);

        Type(vm, "deny both");
        await WithinAsync(vm.PendingAnswers);

        var asked = _brain.Asked.ShouldHaveSingleItem();
        asked.ShouldContain("(ask id p1)");
        asked.ShouldContain("(ask id p2)");
    }

    [Theory]
    [InlineData(TextToSpeechState.NoEngine)]
    [InlineData(TextToSpeechState.Loading)]
    [InlineData(TextToSpeechState.Installing)]
    public async Task With_no_voice_the_chat_s_own_news_is_written_and_other_chats_still_make_their_sound(TextToSpeechState state)
    {
        var (vm, _) = await TrafficVmAsync();
        var none = new TextToSpeechStatus(state);
        _speech.Report(none);
        _speech.Fails = new TextToSpeechNotReadyException(none); // as the engines answer with none picked

        Changes("b", SessionState.Working, SessionState.Idle);
        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        // #256: the log is read once the telling and the voice are done with it; read while they wrote, it failed once.
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());
        await SoundGraceAsync(vm);
        Gap();
        _sounds.Ids.ShouldBe(["marimba-rise", "marimba-rise"], "chat 1's sound waits for no line that is never said");

        vm.Log.Single(e => e.Kind == RavenLogKind.Raven).Chat.Number.ShouldBe(1);
    }
}

public sealed partial class RavenPanelViewModelTests
{
    /// <summary>A card the user was not read out goes to its own window's brain only: chat 1's "allow it" is not about it.</summary>
    [Fact]
    public async Task Another_chat_s_card_is_told_only_to_its_own_window_s_brain()
    {
        var brains = new FakeChatBrains(_brain);
        var (vm, asks) = await TrafficVmAsync(brains);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Permission));

        Type(vm, "allow it");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = ChatNumbered(vm, 2);
        Type(vm, "allow it");
        await WithinAsync(vm.PendingAnswers);

        brains.Windows[CodeSwitchX].Sent.ShouldHaveSingleItem().ShouldNotContain("(ask id p1)");
        brains.Windows[ContentAutomatorX].Sent.ShouldHaveSingleItem().ShouldContain("(ask id p1)");
    }

    /// <summary>Muted, nothing is read out: the cards waiting in the chat hold up the news no longer than one telling.</summary>
    [Fact]
    public async Task Muted_the_chat_s_cards_do_not_hold_up_its_news_one_by_one()
    {
        var (vm, asks) = await TrafficVmAsync();
        vm.IsMuted = true;
        _ = asks.HoldAsync(PermittingIn("b", "p1"), CancellationToken.None);
        _ = asks.HoldAsync(PermittingIn("b", "p2"), CancellationToken.None);
        _ = asks.HoldAsync(PermittingIn("b", "p3"), CancellationToken.None);
        await Until(() => vm.Log.Count(e => e.Kind == RavenLogKind.Permission) == 3);
        _yard.Show("b2", "CodeSwitchX", "Task b2"); // another chat of the same window: b itself waits on its cards
        Changes("b2", SessionState.Working, SessionState.Errored);

        await GraceAsync(vm);
        await GraceAsync(vm);

        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.News);
    }

    /// <summary>A card of the chat the user left is not read out, and does not hold up the news of the chat they went to.</summary>
    [Fact]
    public async Task A_card_of_the_chat_the_user_left_does_not_hold_up_the_news_of_the_one_they_went_to()
    {
        _brain.Answer = _ => [new BrainText("Okay.")];
        var (vm, asks) = await TrafficVmAsync();
        vm.PressMic(TalkInput.MicButton);
        _ = asks.HoldAsync(PermittingIn("b", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Permission));
        vm.SelectedChat = ChatNumbered(vm, 2);
        Speak();
        _time.Advance(Hold);
        await vm.ReleaseMicAsync(TalkInput.MicButton);
        await WithinAsync(vm.PendingAnswers);

        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);

        _teller.Asked.ShouldHaveSingleItem();
        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.Raven && e.Text.StartsWith("Chat ContentAutomatorX finished:"));
    }
}

/// <summary>Counts the sounds instead of playing them; each play is done at once.</summary>
internal sealed class FakeSounds : IChatSounds
{
    private readonly List<string> _ids = [];

    public int Plays => Ids.Count;

    public List<string> Ids
    {
        get
        {
            lock (_ids)
            {
                return [.. _ids];
            }
        }
    }

    public Task PlayAsync(string soundId, CancellationToken ct)
    {
        lock (_ids)
        {
            _ids.Add(soundId);
        }

        return Task.CompletedTask;
    }

    public TimeSpan LengthOf(string soundId) => TimeSpan.Zero;
}
