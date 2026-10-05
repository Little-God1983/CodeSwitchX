using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>
/// The traffic watcher (#125): the chat the user is in speaks, other chats are never spoken and make a short sound at
/// most, only when it is quiet and the cooldown is over. Nothing is saved up to be said later.
/// </summary>
public sealed partial class RavenPanelViewModelTests
{
    private readonly FakeChime _chime = new();

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
            NullLogger<RavenPanelViewModel>.Instance, news, _teller, asks: asks, yard: _yard, brains: brains, chime: _chime);
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
        digest.ShouldContain("Task b");
        digest.ShouldNotContain("Task a");
        string.Join(" ", _speech.Spoken).ShouldContain("Task b");
        string.Join(" ", _speech.Spoken).ShouldNotContain("Task a");
        _chime.Plays.ShouldBe(0, "chat 1's news is spoken: that is the announcement, the others are only marked");
        vm.Chats.Where(c => c.Number is >= 2 and <= 5).ShouldAllBe(c => c.Unread > 0);
        vm.Log.Single(e => e.Kind == RavenLogKind.Raven).Chat.Number.ShouldBe(1);
    }

    [Fact]
    public async Task Other_chats_returning_while_the_user_s_chat_is_quiet_make_one_sound()
    {
        var (vm, _) = await TrafficVmAsync();

        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("c", SessionState.Working, SessionState.Idle);
        Changes("d", SessionState.Working, SessionState.Errored);
        await GraceAsync(vm);
        await Until(() => vm.Log.Count(e => e.Kind == RavenLogKind.News) == 3);

        _chime.Plays.ShouldBe(1);
        _teller.Asked.ShouldBeEmpty("other chats are never spoken");
        _speech.Spoken.ShouldBeEmpty();
        ChatNumbered(vm, 4).HasFailed.ShouldBeTrue("the list carries it");
    }

    [Fact]
    public async Task A_chat_finishing_inside_the_cooldown_after_an_announcement_makes_no_sound()
    {
        _brain.Answer = _ => [new BrainText("Sure.")];
        var (vm, _) = await TrafficVmAsync();
        vm.Traffic.Cooldown = TimeSpan.FromSeconds(20);
        Type(vm, "hello");
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());
        await Until(() => vm.State == RavenState.Idle);

        _time.Advance(TimeSpan.FromSeconds(15) - TrafficWatcher.NewsGrace);
        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.News));
        _chime.Plays.ShouldBe(0, "15 s after Raven spoke, inside the 20 s cooldown");

        _time.Advance(TimeSpan.FromSeconds(5));
        Changes("c", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Count(e => e.Kind == RavenLogKind.News) == 2);
        _chime.Plays.ShouldBe(1, "the cooldown is over");
    }

    [Fact]
    public async Task With_the_sound_off_other_chats_are_only_marked()
    {
        var (vm, _) = await TrafficVmAsync();
        vm.Traffic.SoundOn = false;

        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.News));

        _chime.Plays.ShouldBe(0);
        ChatNumbered(vm, 2).Unread.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Muted_other_chats_make_no_sound()
    {
        var (vm, _) = await TrafficVmAsync();
        vm.IsMuted = true;

        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.News));

        _chime.Plays.ShouldBe(0);
    }

    [Fact]
    public async Task With_the_own_chat_waiting_too_its_news_inside_the_cooldown_is_shown_not_spoken()
    {
        _brain.Answer = _ => [new BrainText("Sure.")];
        var (vm, _) = await TrafficVmAsync();
        vm.Traffic.OwnNewsWaits = true;
        Type(vm, "hello");
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());
        await Until(() => vm.State == RavenState.Idle);

        _time.Advance(TimeSpan.FromSeconds(5) - TrafficWatcher.NewsGrace);
        Changes("b", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.News));

        vm.Log.Single(e => e.Kind == RavenLogKind.News).Chat.Number.ShouldBe(1);
        _teller.Asked.ShouldBeEmpty();
        _speech.Spoken.ShouldBe(["Sure."]);
    }

    [Fact]
    public async Task Another_chat_s_card_is_not_read_out_makes_the_sound_and_the_brain_knows_it()
    {
        _brain.Answer = _ => [new BrainText("Which one?")];
        var (vm, asks) = await TrafficVmAsync();

        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Permission));
        await GraceAsync(vm);

        _chime.Plays.ShouldBe(1);
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
        _chime.Plays.ShouldBe(0);
    }

    [Fact]
    public async Task A_card_that_comes_while_the_user_talks_makes_no_sound_later()
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

        _chime.Plays.ShouldBe(0, "busy means silent, and nothing is saved up");
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
        await Until(() => _chime.Plays == 1); // nothing was heard of chat 1's news

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
}

/// <summary>Counts the chimes instead of playing them.</summary>
internal sealed class FakeChime : IChatChime
{
    private int _plays;

    public int Plays => Volatile.Read(ref _plays);

    public void Play() => Interlocked.Increment(ref _plays);
}
