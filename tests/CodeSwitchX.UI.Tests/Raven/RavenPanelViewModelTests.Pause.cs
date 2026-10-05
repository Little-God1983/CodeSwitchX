using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>
/// The pause between messages (#152): what Raven says on its own (news, a catch-up, a card read out, a chat's sound) waits
/// the pause after Raven last spoke or made a sound, so it never comes all at once. Its answers to the user never wait.
/// </summary>
public sealed partial class RavenPanelViewModelTests
{
    /// <summary>Lets the grace pass, which is shorter than the pause: what waits is still not said.</summary>
    private async Task OnlyTheGracePassesAsync(RavenPanelViewModel vm)
    {
        await Until(() => vm.State == RavenState.Idle);
        vm.Traffic.PauseLeft.ShouldBeGreaterThan(TrafficWatcher.NewsGrace, "Raven just spoke or made a sound");
        _time.Advance(TrafficWatcher.NewsGrace);
        vm.State.ShouldBe(RavenState.Idle, "nothing is told inside the pause");
    }

    [Fact]
    public async Task The_chat_s_cards_are_read_a_pause_apart()
    {
        var (vm, asks) = await TrafficVmAsync();
        _ = asks.HoldAsync(PermittingIn("b", "p1"), CancellationToken.None);
        _ = asks.HoldAsync(PermittingIn("b", "p2"), CancellationToken.None);
        await Until(() => vm.Log.Count(e => e.Kind == RavenLogKind.Permission) == 2);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());
        _speech.Spoken.Count.ShouldBe(1);

        await OnlyTheGracePassesAsync(vm);
        _speech.Spoken.Count.ShouldBe(1);

        _time.Advance(TrafficWatcher.DefaultPause - TrafficWatcher.NewsGrace);
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());
        _speech.Spoken.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_card_that_came_while_away_is_read_a_pause_after_the_catch_up()
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
        _speech.Spoken.First().ShouldStartWith("Told: Catch-up");

        await OnlyTheGracePassesAsync(vm);
        string.Join(" ", _speech.Spoken).ShouldNotContain("npm test");

        _time.Advance(TrafficWatcher.DefaultPause - TrafficWatcher.NewsGrace);
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());
        string.Join(" ", _speech.Spoken).ShouldContain("npm test");
    }

    [Fact]
    public async Task News_of_the_chat_the_user_is_in_waits_a_pause_after_another_chat_s_sound()
    {
        var (vm, _) = await TrafficVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle); // chat 2's: it makes the sound
        await GraceAsync(vm);
        await Until(() => _chime.Plays == 1);

        Changes("b", SessionState.Working, SessionState.Idle); // chat 1's, where the user is
        await OnlyTheGracePassesAsync(vm);
        _teller.Asked.ShouldBeEmpty();

        _time.Advance(vm.Traffic.PauseLeft);
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());
        _teller.Asked.ShouldHaveSingleItem().ShouldContain("Task b");
    }

    [Fact]
    public async Task An_answer_to_the_user_does_not_wait_for_the_pause()
    {
        _brain.Answer = _ => [new BrainText("Okay.")];
        var (vm, _) = await TrafficVmAsync();
        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => _chime.Plays == 1); // the pause runs from it

        vm.PressMic(TalkInput.MicButton);
        Speak();
        _time.Advance(Hold);
        vm.Traffic.PauseLeft.ShouldBeGreaterThan(TimeSpan.Zero);
        await vm.ReleaseMicAsync(TalkInput.MicButton);
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());

        string.Join(" ", _speech.Spoken).ShouldContain("Okay.");
    }
}
