using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>
/// #308: when Raven speaks the news of the chat the user is in and other chats wait, the teller words a remark about them,
/// said after the chat's line; the sounds of the chats it mentions are dropped once it is heard.
/// </summary>
public sealed partial class RavenPanelViewModelTests
{
    private const string Remark = "Two more chats finished, and RawCutX asks for permission.";

    /// <summary>Chats 2 and 3 finish and chat 4 asks permission, then chat 1's, where the user is, finishes; told and heard.</summary>
    private async Task<RavenPanelViewModel> OthersWaitThenTheChatFinishesAsync()
    {
        var (vm, asks) = await TrafficVmAsync();
        _teller.Answer = _ => [new BrainText($"1. the tests pass\nR. {Remark}")];
        _ = asks.HoldAsync(PermittingIn("d", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Permission));
        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("c", SessionState.Working, SessionState.Idle);
        Changes("b", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Raven));
        await WithinAsync(_voice.WhenQuietAsync());
        return vm;
    }

    [Fact]
    public async Task Raven_mentions_the_other_waiting_chats_after_the_chat_s_line()
    {
        var vm = await OthersWaitThenTheChatFinishesAsync();

        var prompt = _teller.Asked.ShouldHaveSingleItem();
        prompt.ShouldContain("- ContentAutomatorX: finished");
        prompt.ShouldContain("- DiffusionNexus: finished");
        prompt.ShouldContain("- RawCutX: asks for permission");
        prompt.ShouldNotContain("- CodeSwitchX");
        string.Join(" ", _speech.Spoken).Trim().ShouldBe($"Chat CodeSwitchX finished: the tests pass. {Remark}");
        vm.Log.Single(e => e.Kind == RavenLogKind.Raven).Text.ShouldEndWith(Remark, Case.Sensitive, "written as said");
    }

    [Fact]
    public async Task The_mentioned_chats_sounds_are_dropped_once_the_remark_is_heard()
    {
        var vm = await OthersWaitThenTheChatFinishesAsync();

        await SoundGraceAsync(vm);
        Gap();
        Gap();
        Gap();

        _sounds.Ids.ShouldBe(["marimba-rise"], "chat 1's sound before its line; what Raven said in words is not sounded after");
    }

    [Fact]
    public async Task A_chat_whose_news_comes_while_the_remark_is_worded_keeps_its_sound()
    {
        var (vm, _) = await TrafficVmAsync();
        _teller.Answer = _ =>
        {
            Changes("e", SessionState.Working, SessionState.Idle); // VideoX, chat 5, as the teller words
            return [new BrainText("1. the tests pass\nR. ContentAutomatorX finished too.")];
        };
        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("b", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Raven));
        await WithinAsync(_voice.WhenQuietAsync());

        _teller.Asked.ShouldHaveSingleItem().ShouldNotContain("VideoX");
        await GraceAsync(vm); // chat 5's news, only written
        await SoundGraceAsync(vm);
        Gap();
        Gap();
        _sounds.Ids.ShouldBe(["marimba-rise", "marimba-rise"], "chat 1's, then chat 5's; chat 2's was said");
    }

    [Fact]
    public async Task With_nothing_else_waiting_raven_says_only_the_chat_s_line()
    {
        var (vm, _) = await TrafficVmAsync();
        _teller.Answer = _ => [new BrainText("1. the tests pass")];

        Changes("b", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        _teller.Asked.ShouldHaveSingleItem().ShouldNotContain("\"R. \"");
        string.Join(" ", _speech.Spoken).Trim().ShouldBe("Chat CodeSwitchX finished: the tests pass.");
    }

    [Fact]
    public async Task A_muted_chat_is_not_mentioned()
    {
        var (vm, _) = await TrafficVmAsync();
        ChatNumbered(vm, 2).IsMuted = true;
        _teller.Answer = _ => [new BrainText("1. the tests pass")];

        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("b", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        _teller.Asked.ShouldHaveSingleItem().ShouldNotContain("ContentAutomatorX");
    }

    [Fact]
    public async Task A_chat_working_again_is_not_mentioned()
    {
        var (vm, _) = await TrafficVmAsync();
        _teller.Answer = _ => [new BrainText("1. the tests pass")];
        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm); // chat 2's news, written and unread

        Changes("a", SessionState.Idle, SessionState.Working); // the user went on in its tab
        Changes("b", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        ChatNumbered(vm, 2).Unread.ShouldBeGreaterThan(0);
        _teller.Asked.ShouldHaveSingleItem().ShouldNotContain("ContentAutomatorX");
    }

    [Fact]
    public async Task A_waiting_chat_is_mentioned_once()
    {
        var vm = await OthersWaitThenTheChatFinishesAsync();
        _teller.Answer = _ => [new BrainText("1. the docs build")];

        Changes("b", SessionState.Idle, SessionState.Working);
        Changes("b", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        _teller.Asked.Count.ShouldBe(2);
        _teller.Asked[1].ShouldNotContain("\"R. \"", customMessage: "the same chats wait as were just mentioned");
    }

    /// <summary>
    /// Review round 1 of #308: asked for the remark alone, the teller may leave the mark off, put a lead-in first, number the
    /// line or mark it as a list; and a remark it starts lower case is said as a sentence.
    /// </summary>
    [Theory]
    [InlineData("ContentAutomatorX finished too.", "ContentAutomatorX finished too.")]
    [InlineData("Sure, here it is:\nContentAutomatorX finished too.", "ContentAutomatorX finished too.")]
    [InlineData("1. ContentAutomatorX finished too.", "ContentAutomatorX finished too.")]
    [InlineData("- **R.** ContentAutomatorX finished too.", "ContentAutomatorX finished too.")]
    [InlineData("R. contentAutomatorX finished too", "ContentAutomatorX finished too.")]
    public async Task A_chat_that_needs_you_gets_the_remark_from_the_teller_alone(string answer, string said)
    {
        var (vm, _) = await TrafficVmAsync();
        _teller.Answer = _ => [new BrainText(answer)];
        Changes("a", SessionState.Working, SessionState.Idle);

        Changes("b", SessionState.Working, SessionState.Waiting, "Claude needs your input");
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Raven));
        await WithinAsync(_voice.WhenQuietAsync());

        var prompt = _teller.Asked.ShouldHaveSingleItem();
        prompt.ShouldNotContain("sum up");
        prompt.ShouldContain("- ContentAutomatorX: finished");
        string.Join(" ", _speech.Spoken).Trim().ShouldBe($"Chat CodeSwitchX needs you: Claude needs your input. {said}");
    }

    /// <summary>Review round 1 of #308: a remark of two sentences is said whole, not cut to its first while both chats count as mentioned.</summary>
    [Fact]
    public async Task A_remark_of_two_sentences_is_said_whole()
    {
        var (vm, asks) = await TrafficVmAsync();
        _teller.Answer = _ => [new BrainText("1. the tests pass\nR. ContentAutomatorX finished. RawCutX asks for permission.")];
        _ = asks.HoldAsync(PermittingIn("d", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Permission));
        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("b", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Raven));
        await WithinAsync(_voice.WhenQuietAsync());

        string.Join(" ", _speech.Spoken).Trim()
            .ShouldBe("Chat CodeSwitchX finished: the tests pass. ContentAutomatorX finished. RawCutX asks for permission.");
    }

    /// <summary>
    /// Review round 1 of #308: the user talks once the chat's line is heard, while the teller still words the remark: the line
    /// was heard whole, so it is not marked interrupted, and only the remark is dropped.
    /// </summary>
    [Fact]
    public async Task A_press_while_the_remark_is_worded_leaves_the_chat_s_line_heard()
    {
        _brain.Answer = _ => [new BrainText("Okay.")];
        var (vm, _) = await TrafficVmAsync();
        _teller.Answer = _ => [new BrainText("R. ContentAutomatorX finished too.")];
        _teller.Gate = new TaskCompletionSource(); // the teller is still at the remark
        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("b", SessionState.Working, SessionState.Waiting, "Claude needs your input");
        _time.Advance(vm.Traffic.WaitBeforeTelling);
        await Until(() => _speech.Spoken.Count == 1);
        await WithinAsync(_voice.WhenQuietAsync());

        vm.PressMic(TalkInput.MicButton);
        Speak();
        _time.Advance(Hold);
        await vm.ReleaseMicAsync(TalkInput.MicButton);
        await WithinAsync(vm.PendingAnswers);

        vm.Log.First(e => e.Kind == RavenLogKind.Raven).Text.ShouldBe("Chat CodeSwitchX needs you: Claude needs your input.");
    }

    [Fact]
    public async Task A_remark_hushed_before_it_is_heard_leaves_the_sounds()
    {
        _brain.Answer = _ => [new BrainText("Okay.")];
        var (vm, _) = await TrafficVmAsync();
        _teller.Answer = _ => [new BrainText("1. the tests pass\nR. ContentAutomatorX finished too.")];
        _speech.Gate = new TaskCompletionSource(); // the voice is still making the reply's first audio
        Changes("a", SessionState.Working, SessionState.Idle);
        Changes("b", SessionState.Working, SessionState.Idle);
        _time.Advance(vm.Traffic.WaitBeforeTelling);
        await Until(() => _speech.Spoken.Count == 1);

        vm.PressMic(TalkInput.MicButton); // the user talks over it
        _speech.Gate.SetResult();
        Speak();
        _time.Advance(Hold);
        await vm.ReleaseMicAsync(TalkInput.MicButton);
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());
        await SoundGraceAsync(vm);
        Gap();
        Gap();

        _sounds.Ids.ShouldBe(["marimba-rise", "marimba-rise"], "nothing was heard: chat 1's and chat 2's sounds both play");
    }
}
