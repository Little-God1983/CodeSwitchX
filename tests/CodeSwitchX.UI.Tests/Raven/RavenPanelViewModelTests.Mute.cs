using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>
/// A muted chat (#153): its news, the sound it makes while the user is elsewhere and its catch-up are only written; its
/// cards are still read out (a chat waits on them), and Raven still answers aloud in it. Remembered by the window.
/// </summary>
public sealed partial class RavenPanelViewModelTests
{
    [Fact]
    public async Task A_muted_chat_s_own_news_is_only_written()
    {
        var (vm, _) = await TrafficVmAsync();
        vm.MuteChat(1, true).ShouldNotBeNull();

        Changes("b", SessionState.Working, SessionState.Idle); // chat 1's, where the user is
        await GraceAsync(vm);

        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.News && e.Chat.Number == 1);
        _teller.Asked.ShouldBeEmpty();
        _speech.Spoken.ShouldBeEmpty();
        _teller.WarmUps.ShouldBe(0, "nothing of it is told aloud");
    }

    [Fact]
    public async Task Another_muted_chat_s_news_makes_no_sound_but_an_unmuted_one_s_does()
    {
        var (vm, _) = await TrafficVmAsync();
        vm.MuteChat(2, true);

        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's
        await GraceAsync(vm);
        _chime.Plays.ShouldBe(0);
        ChatNumbered(vm, 2).Unread.ShouldBeGreaterThan(0, "it is still marked in the list");

        _time.Advance(TrafficWatcher.DefaultCooldown);
        Changes("c", SessionState.Working, SessionState.Idle); // chat 3's
        await GraceAsync(vm);
        _chime.Plays.ShouldBe(1);
    }

    [Fact]
    public async Task A_muted_chat_s_cards_are_still_read_out_and_still_sound_from_elsewhere()
    {
        var (vm, asks) = await TrafficVmAsync();
        vm.MuteChat(1, true);
        vm.MuteChat(2, true);

        _ = asks.HoldAsync(PermittingIn("b", "p1"), CancellationToken.None); // chat 1's, where the user is
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());
        _speech.Spoken.ShouldHaveSingleItem().ShouldContain("npm test");

        _time.Advance(TrafficWatcher.DefaultCooldown);
        _ = asks.HoldAsync(PermittingIn("a", "p2"), CancellationToken.None); // chat 2's: the chat waits on it
        await Until(() => _chime.Plays == 1);
    }

    [Fact]
    public async Task Raven_still_answers_aloud_in_a_muted_chat()
    {
        _brain.Answer = _ => [new BrainText("Okay.")];
        var (vm, _) = await TrafficVmAsync();
        vm.MuteChat(1, true);

        Type(vm, "how far is it");
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());

        string.Join(" ", _speech.Spoken).ShouldContain("Okay.");
    }

    [Fact]
    public async Task A_muted_chat_is_not_caught_up()
    {
        var vm = await AwayFromChatTwoAsync();
        vm.MuteChat(2, true);

        vm.SelectedChat = ChatNumbered(vm, 2);
        await GraceAsync(vm);

        _teller.Asked.ShouldBeEmpty();
        _speech.Spoken.ShouldBeEmpty();
    }

    [Fact]
    public async Task Muting_the_chat_while_its_catch_up_waits_stops_it()
    {
        var vm = await AwayFromChatTwoAsync();
        vm.SelectedChat = ChatNumbered(vm, 2);

        ChatNumbered(vm, 2).ToggleMuteCommand.Execute(null);
        await GraceAsync(vm);

        _teller.Asked.ShouldBeEmpty();
    }

    /// <summary>Muted while its news is being told, the chat stops telling it: muted means quiet now.</summary>
    [Fact]
    public async Task Muting_the_chat_while_its_news_is_told_stops_it()
    {
        var (vm, _) = await TrafficVmAsync();
        _teller.Gate = new TaskCompletionSource();
        Changes("b", SessionState.Working, SessionState.Idle); // chat 1's, where the user is
        await PassGraceAsync(vm);
        await Until(() => _teller.Asked.Count == 1);

        ChatNumbered(vm, 1).ToggleMuteCommand.Execute(null);
        _teller.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());

        _speech.Spoken.ShouldBeEmpty();
    }

    /// <summary>A removed window's mute is not kept: the stored list holds windows that are there.</summary>
    [Fact]
    public async Task A_removed_window_s_mute_is_forgotten()
    {
        var (vm, _) = await TrafficVmAsync();
        vm.MuteChat(2, true);
        var told = 0;
        vm.MutedWindowsChanged += (_, _) => told++;

        vm.SetWorkspaces([.. vm.Chats.Where(c => c.WorkspaceId is not null && c.Number != 2).Select(c => (c.WorkspaceId!.Value, c.Number, c.Name))]);

        vm.MutedWindows.ShouldBeEmpty();
        told.ShouldBe(1);
    }

    [Fact]
    public async Task Unmuted_its_news_is_spoken_again()
    {
        var (vm, _) = await TrafficVmAsync();
        vm.MuteChat(1, true);
        vm.MuteChat(1, false).ShouldNotBeNull().IsMuted.ShouldBeFalse();

        Changes("b", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        _teller.Asked.ShouldHaveSingleItem().ShouldContain("Task b");
    }

    [Fact]
    public async Task Chat_zero_and_activity_have_no_mute_of_their_own()
    {
        var (vm, _) = await TrafficVmAsync();

        vm.MuteChat(0, true).ShouldBeNull();
        vm.MuteChat(9, true).ShouldBeNull();
        (vm.YardChat.CanMute, vm.ActivityChat.CanMute).ShouldBe((false, false));
        vm.YardChat.ToggleMuteCommand.CanExecute(null).ShouldBeFalse();
        ChatNumbered(vm, 1).CanMute.ShouldBeTrue();
    }

    [Fact]
    public async Task The_windows_muted_before_are_muted_when_listed_and_only_a_change_is_told()
    {
        var (vm, _) = await TrafficVmAsync();
        var told = 0;
        vm.MutedWindowsChanged += (_, _) => told++;
        var later = Guid.NewGuid();

        vm.SetMutedWindows([FakeYardDirectory.WorkspaceOf("DiffusionNexus"), later]);
        ChatNumbered(vm, 3).IsMuted.ShouldBeTrue();
        vm.SetWorkspaces([.. vm.Chats.Where(c => c.WorkspaceId is not null).Select(c => (c.WorkspaceId!.Value, c.Number, c.Name)), (later, 9, "Later")]);
        ChatNumbered(vm, 9).IsMuted.ShouldBeTrue("a window listed after the load gets its mute too");
        told.ShouldBe(0, "what was stored is not stored again");

        vm.MuteChat(3, false);

        told.ShouldBe(1);
        vm.MutedWindows.ShouldBe([later]);
        ChatNumbered(vm, 3).Status.ShouldNotContain("muted");
        ChatNumbered(vm, 9).Tip.ShouldContain("muted");
    }

    [Fact]
    public void What_Raven_says_of_a_mute_names_the_chat_and_what_is_still_read()
    {
        var chat = RavenChat.Of(Guid.NewGuid(), 3, "ContentAutomatorX");
        chat.IsMuted = true;
        RavenPanelViewModel.MuteLine(chat).ShouldBe("Chat 3, ContentAutomatorX, is muted: its news and its catch-up are only written, with no "
            + "sound; its questions are still read out.");
        chat.IsMuted = false;
        RavenPanelViewModel.MuteLine(chat).ShouldBe("Chat 3, ContentAutomatorX, speaks again.");
    }
}
