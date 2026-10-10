using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Dictation;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>Each chat keeps its own conversation (#123): a question goes to the brain of the chat it is asked in.</summary>
public sealed partial class RavenPanelViewModelTests
{
    /// <summary>A brain per window; the Yard's is the panel's own <see cref="_brain"/>.</summary>
    private sealed class FakeChatBrains(FakeBrain yard) : IChatBrains
    {
        public Dictionary<Guid, FakeBrain> Windows { get; } = [];

        public IConductorBrain For(Guid? workspaceId) => workspaceId is { } id
            ? Windows.TryGetValue(id, out var brain) ? brain : Windows[id] = new FakeBrain { Answer = _ => [new BrainText("Window answer.")] }
            : yard;

        public List<Guid> Retired { get; } = [];

        public void Retire(Guid workspaceId) => Retired.Add(workspaceId);
    }

    private async Task<(RavenPanelViewModel Vm, FakeChatBrains Brains)> ChatBrainsVmAsync()
    {
        var brains = new FakeChatBrains(_brain);
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, brains: brains);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        return (vm, brains);
    }

    [Fact]
    public async Task What_is_said_in_a_chat_goes_only_to_that_chat_s_brain()
    {
        var (vm, brains) = await ChatBrainsVmAsync();
        _brain.Answer = _ => [new BrainText("Yard answer.")];

        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "Start a chat that fixes the retry");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = ChatNumbered(vm, 1);
        Type(vm, "What is the branch here?");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "What did I ask you before?");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = vm.YardChat;
        Type(vm, "What needs me?");
        await WithinAsync(vm.PendingAnswers);

        brains.Windows[ContentAutomatorX].Sent.Count.ShouldBe(2);
        brains.Windows[ContentAutomatorX].Sent.ShouldAllBe(q => !q.Contains("branch here") && !q.Contains("needs me"));
        brains.Windows[ContentAutomatorX].Sent[1].ShouldContain("What did I ask you before?");
        brains.Windows[CodeSwitchX].Sent.ShouldHaveSingleItem().ShouldContain("What is the branch here?");
        _brain.Sent.ShouldHaveSingleItem().ShouldContain("What needs me?");
        vm.Log.Last(e => e.Kind == RavenLogKind.Raven).Text.ShouldBe("Yard answer.");
    }

    [Fact]
    public async Task An_answer_still_streams_from_the_brain_of_the_chat_it_was_asked_in()
    {
        var (vm, brains) = await ChatBrainsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 3);
        var three = brains.For(ContentAutomatorX) as FakeBrain;
        three!.Gate = new TaskCompletionSource();

        Type(vm, "Is the retry test green?");
        vm.SelectedChat = ChatNumbered(vm, 1);
        three.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        vm.Log.Single(e => e.Kind == RavenLogKind.Raven).Chat.ShouldBe(ChatNumbered(vm, 3));
        brains.Windows.ContainsKey(CodeSwitchX).ShouldBeFalse("chat 1 was not asked anything");
    }

    /// <summary>What became of an allow a brain proposed is told to that brain, not to the next chat's that is asked something.</summary>
    [Fact]
    public async Task The_outcome_of_a_proposed_allow_is_told_to_the_brain_that_proposed_it()
    {
        _yard.Show("a", "ContentAutomatorX", "Fix the upload retry");
        var asks = new ChatAsks(_bus, _time) { Takes = _ => true };
        var brains = new FakeChatBrains(_brain);
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, asks: asks, yard: _yard, brains: brains);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Ask is not null));
        var three = (FakeBrain)brains.For(ContentAutomatorX);
        IEnumerable<BrainEvent> Proposes()
        {
            asks.Propose("p1", ContentAutomatorX); // as answer_permission does from chat 3's brain, whose header names its window
            yield break;
        }

        three.Answer = _ => Proposes();
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "Allow it");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = ChatNumbered(vm, 1); // moving away drops the proposal
        asks.Proposed.ShouldBeNull();

        Type(vm, "What is the branch here?");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = ChatNumbered(vm, 3);
        three.Answer = _ => [new BrainText("Nothing ran.")];
        Type(vm, "Did it run?");
        await WithinAsync(vm.PendingAnswers);

        ((FakeBrain)brains.For(CodeSwitchX)).Sent.ShouldHaveSingleItem().ShouldNotContain("allow you proposed");
        three.Sent[^1].ShouldContain("the allow you proposed was not confirmed by a yes");
    }

    /// <summary>"Stop it" said in chat 3 is chat 3's, though a question in chat 1 came before it went in: chat 1's brain acts on window 1.</summary>
    [Fact]
    public async Task A_waiting_question_from_another_chat_is_answered_by_its_own_chat_s_brain()
    {
        var (vm, brains) = await ChatBrainsVmAsync();
        _brain.Gate = new TaskCompletionSource();
        _brain.IgnoresCancel = true;
        Type(vm, "zero");
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "stop it"); // waits behind "zero"
        vm.SelectedChat = ChatNumbered(vm, 1);

        Type(vm, "open it");
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        brains.Windows[ContentAutomatorX].Sent.ShouldBe(
            ["[The user is in chat 3, ContentAutomatorX: \"it\" and \"this\" mean that window unless they name another.]\nstop it"]);
        brains.Windows[CodeSwitchX].Sent.ShouldHaveSingleItem().ShouldNotContain("stop it");
        vm.Log.Where(e => e.Kind == RavenLogKind.Raven).Select(e => e.Chat.Number).ShouldBe([3, 1], "each answer in its own chat");
    }

    [Fact]
    public async Task The_yard_s_brain_is_not_told_it_is_in_the_yard_when_only_other_brains_heard_of_windows()
    {
        var (vm, _) = await ChatBrainsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "how far is it");
        await WithinAsync(vm.PendingAnswers);

        vm.SelectedChat = vm.YardChat;
        Type(vm, "what needs me");
        await WithinAsync(vm.PendingAnswers);

        _brain.Sent.ShouldBe(["what needs me"]);
    }

    /// <summary>
    /// #137: a news line goes to the brain of its own window's chat only, once: another window's brain would take it for its
    /// own window's. Chat 1's brain does not get chat 3's news.
    /// </summary>
    [Fact]
    public async Task Chat_news_is_told_only_to_its_own_window_s_brain_once()
    {
        _teller.Answer = _ => [new BrainText("It is done.")];
        _yard.Show("a", "ContentAutomatorX", "Fix the upload retry");
        var news = new ChatNews(_bus, _yard, _time, _ => "Done.");
        var brains = new FakeChatBrains(_brain);
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, news, _teller, brains: brains);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        _time.Advance(TimeSpan.FromSeconds(1));
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.News));

        vm.SelectedChat = ChatNumbered(vm, 1);
        Type(vm, "anything else?");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "what finished?");
        await WithinAsync(vm.PendingAnswers);
        Type(vm, "and now?");
        await WithinAsync(vm.PendingAnswers);

        brains.Windows[CodeSwitchX].Sent.ShouldHaveSingleItem().ShouldNotContain("[Chat news", Case.Sensitive, "another window's news");
        var three = brains.Windows[ContentAutomatorX].Sent;
        three[0].ShouldContain("[Chat news the user was given");
        three[1].ShouldNotContain("[Chat news", Case.Sensitive, "told once");
    }

    /// <summary>#137: a card that comes in the chat the user is in reaches that chat's brain only, also after they move on.</summary>
    [Fact]
    public async Task A_card_in_the_chat_the_user_is_in_is_told_only_to_that_chat_s_brain()
    {
        _yard.Show("a", "ContentAutomatorX", "Deploy");
        var asks = new ChatAsks(_bus, _time) { Takes = _ => true };
        var brains = new FakeChatBrains(_brain);
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, asks: asks, yard: _yard, brains: brains);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        vm.SelectedChat = ChatNumbered(vm, 3);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Ask is { ShownIn: not null }));

        vm.SelectedChat = ChatNumbered(vm, 1);
        Type(vm, "anything for me here?");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "allow it");
        await WithinAsync(vm.PendingAnswers);

        brains.Windows[CodeSwitchX].Sent.ShouldHaveSingleItem().ShouldNotContain("(ask id p1)");
        brains.Windows[ContentAutomatorX].Sent.ShouldHaveSingleItem().ShouldContain("(ask id p1)");
    }

    /// <summary>Without a summarizer, chat 0 is no overview: it still hears every window's news, as before #124.</summary>
    [Fact]
    public async Task Without_a_summarizer_chat_zero_hears_every_window_s_news()
    {
        _teller.Answer = _ => [new BrainText("It is done.")];
        _yard.Show("a", "ContentAutomatorX", "Fix the upload retry");
        var news = new ChatNews(_bus, _yard, _time, _ => "Done.");
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, news, _teller, brains: new FakeChatBrains(_brain));
        await WithinAsync(vm.RefreshMicrophonesAsync());
        _time.Advance(TimeSpan.FromSeconds(1));
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        Changes("a", SessionState.Working, SessionState.Idle);
        await GraceAsync(vm);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.News));

        vm.SelectedChat = vm.YardChat;
        Type(vm, "open the one that finished");
        await WithinAsync(vm.PendingAnswers);

        _brain.Sent.ShouldHaveSingleItem().ShouldContain("Fix the upload retry");
    }

    /// <summary>Chat 0 without a summarizer hears every window's facts, but never what became of another brain's allow.</summary>
    [Fact]
    public async Task Without_a_summarizer_chat_zero_is_not_told_another_brain_s_allow()
    {
        _yard.Show("a", "ContentAutomatorX", "Deploy");
        var asks = new ChatAsks(_bus, _time) { Takes = _ => true };
        var brains = new FakeChatBrains(_brain);
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, asks: asks, yard: _yard, brains: brains);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        vm.SelectedChat = ChatNumbered(vm, 3);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Ask is { ShownIn: not null }));
        var proposal = asks.Propose("p1", ContentAutomatorX); // chat 3's brain proposed it
        await Until(() => asks.IsHeard(proposal));

        vm.SelectedChat = vm.YardChat; // moving away lets the proposal go: its brain is told
        Type(vm, "anything new?");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "and here?");
        await WithinAsync(vm.PendingAnswers);

        _brain.Sent.ShouldHaveSingleItem().ShouldNotContain("allow you proposed");
        brains.Windows[ContentAutomatorX].Sent.ShouldHaveSingleItem().ShouldContain("allow you proposed", customMessage: "its proposer is told");
    }

    /// <summary>A window's brain made anew (the window left the list for a moment) still gets the window's facts.</summary>
    [Fact]
    public async Task A_window_s_facts_reach_a_brain_made_anew_for_it()
    {
        _yard.Show("a", "ContentAutomatorX", "Deploy");
        var asks = new ChatAsks(_bus, _time) { Takes = _ => true };
        var brains = new FakeChatBrains(_brain);
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, asks: asks, yard: _yard, brains: brains);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Ask is { ShownIn: not null }));
        brains.Windows.Remove(ContentAutomatorX); // as Retire does: the next For makes a new brain

        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "allow it");
        await WithinAsync(vm.PendingAnswers);

        brains.Windows[ContentAutomatorX].Sent.ShouldHaveSingleItem().ShouldContain("(ask id p1)");
    }

    /// <summary>#137: a card of window 3 reaches chat 3's brain, never chat 1's, even when the user is in chat 1.</summary>
    [Fact]
    public async Task A_card_is_told_only_to_its_own_window_s_brain()
    {
        _yard.Show("a", "ContentAutomatorX", "Deploy");
        _yard.Show("b", "CodeSwitchX", "Deploy"); // the same title in both windows, as in #124's check
        var asks = new ChatAsks(_bus, _time) { Takes = _ => true };
        var brains = new FakeChatBrains(_brain);
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, asks: asks, yard: _yard, brains: brains);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        vm.SelectedChat = ChatNumbered(vm, 1);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Ask is { ShownIn: not null }));

        Type(vm, "which chats run in this window?");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "allow it");
        await WithinAsync(vm.PendingAnswers);

        brains.Windows[CodeSwitchX].Sent.ShouldHaveSingleItem().ShouldNotContain("(ask id p1)");
        brains.Windows[ContentAutomatorX].Sent.ShouldHaveSingleItem().ShouldContain("(ask id p1)");
    }

    /// <summary>
    /// "Stop it" in chat 3 waits for its brain to start; a question in chat 1 comes, then one in chat 3 again before either
    /// went in: nothing is lost, and each chat's words go to its own brain.
    /// </summary>
    [Fact]
    public async Task Questions_waiting_on_a_cold_brain_across_chats_all_reach_their_own_brain()
    {
        var (vm, brains) = await ChatBrainsVmAsync();
        var three = (FakeBrain)brains.For(ContentAutomatorX);
        var one = (FakeBrain)brains.For(CodeSwitchX);
        three.BeforeSent = new TaskCompletionSource(); // starting
        one.BeforeSent = new TaskCompletionSource();

        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "stop it");
        await Until(() => three.Asked.Count == 1);
        vm.SelectedChat = ChatNumbered(vm, 1);
        Type(vm, "what is here");
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "and the tests?");
        three.BeforeSent.SetResult();
        one.BeforeSent.SetResult();
        await WithinAsync(vm.PendingAnswers);

        three.Sent.ShouldHaveSingleItem().ShouldEndWith("stop it" + "\n" + "and the tests?");
        one.Sent.ShouldHaveSingleItem().ShouldEndWith("what is here");
    }

    [Fact]
    public async Task A_window_removed_from_the_yard_retires_its_brain()
    {
        var (vm, brains) = await ChatBrainsVmAsync();

        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX")]);

        brains.Retired.ShouldBe([ContentAutomatorX]);
    }

    [Fact]
    public async Task A_question_of_a_window_removed_before_its_turn_goes_to_the_yard_s_brain_not_a_new_one()
    {
        var (vm, brains) = await ChatBrainsVmAsync();
        _brain.Gate = new TaskCompletionSource();
        _brain.IgnoresCancel = true;
        _brain.Answer = _ => [new BrainText("Done.")];
        Type(vm, "zero");
        vm.SelectedChat = ChatNumbered(vm, 3);
        Type(vm, "hello"); // waits behind "zero"
        vm.SelectedChat = vm.YardChat;

        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX")]);
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);

        brains.Windows.ContainsKey(ContentAutomatorX).ShouldBeFalse("a brain for a window gone would act on nothing and stay");
        _brain.Sent[^1].ShouldEndWith("hello");
    }

    [Fact]
    public async Task What_became_of_an_allow_proposed_from_a_window_gone_is_told_to_no_brain()
    {
        _yard.Show("a", "ContentAutomatorX", "Fix the upload retry");
        var asks = new ChatAsks(_bus, _time) { Takes = _ => true };
        var brains = new FakeChatBrains(_brain);
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, asks: asks, yard: _yard, brains: brains);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Ask is not null));

        asks.Propose("p1", Guid.NewGuid()); // from a window no longer on the Yard
        Type(vm, "what now");
        await WithinAsync(vm.PendingAnswers);

        _brain.Sent.ShouldHaveSingleItem().ShouldNotContain("allow you proposed");
    }

    [Fact]
    public async Task What_became_of_an_allow_proposed_from_no_Raven_chat_is_told_to_no_brain()
    {
        // #265: a caller that is no Raven chat proposed it; chat 0's brain did not, and is not told it.
        _yard.Show("a", "ContentAutomatorX", "Fix the upload retry");
        var asks = new ChatAsks(_bus, _time) { Takes = _ => true };
        var brains = new FakeChatBrains(_brain);
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, asks: asks, yard: _yard, brains: brains);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Ask is not null));
        vm.SelectedChat = vm.YardChat;

        asks.Propose("p1", window: null); // no chat header
        Type(vm, "what now");
        await WithinAsync(vm.PendingAnswers);

        _brain.Sent.ShouldHaveSingleItem().ShouldNotContain("allow you proposed");
    }

    [Fact]
    public async Task Muted_an_allow_proposed_from_a_window_gone_is_only_written_though_chat_0_answers_words_said_aloud()
    {
        // Round 1 of #254: a window gone took its brain along; the Yard's turn answering spoken words is not its turn.
        _yard.Show("a", "ContentAutomatorX", "Fix the upload retry");
        var asks = new ChatAsks(_bus, _time) { Takes = _ => true };
        var brains = new FakeChatBrains(_brain);
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, asks: asks, yard: _yard, brains: brains);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        vm.SetWorkspaces([(CodeSwitchX, 1, "CodeSwitchX"), (ContentAutomatorX, 3, "ContentAutomatorX")]);
        _ = asks.HoldAsync(PermittingIn("a", "p1"), CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Ask is not null));
        vm.IsMuted = true;
        vm.SelectedChat = vm.YardChat;
        _brain.Gate = new TaskCompletionSource(); // chat 0's brain answers words said aloud, and is still at it
        Transcribes(Task.FromResult(new DictationResult("What's waiting?", TimeSpan.FromSeconds(1))));
        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);
        Speak();
        _time.Advance(Hold);
        await WithinAsync(vm.ReleaseMicAsync(TalkInput.MicButton));
        await WithinAsync(vm.PendingTranscriptions);
        await Until(() => _brain.Sent.Count == 1);

        asks.Propose("p1", Guid.NewGuid()); // from a window no longer on the Yard

        await WithinAsync(_voice.WhenQuietAsync());
        _speech.Spoken.ShouldNotContain(s => s.Contains("Say yes."), "muted, and no turn of the user's answers there");
        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.Raven && e.Text.EndsWith("Say yes.", StringComparison.Ordinal));
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
    }

    [Fact]
    public async Task Talking_warms_up_the_brain_of_the_chat_the_user_is_in()
    {
        var (vm, brains) = await ChatBrainsVmAsync();
        vm.SelectedChat = ChatNumbered(vm, 3);

        await HoldAsync(vm);

        ((FakeBrain)brains.For(ContentAutomatorX)).WarmUps.ShouldBe(1);
        _brain.WarmUps.ShouldBe(0);
    }

    /// <summary>Asks in chat 1, whose brain answers behind a gate, and moves the user to chat 3 with the question (#180).</summary>
    private async Task<(RavenPanelViewModel Vm, FakeChatBrains Brains)> MovedToChat3Async()
    {
        var (vm, brains) = await ChatBrainsVmAsync();
        brains.Windows[CodeSwitchX] = new FakeBrain { Answer = _ => StartingIn(vm, CodeSwitchX, ContentAutomatorX, "On it.", " Started.") };
        vm.SelectedChat = ChatNumbered(vm, 1);
        Type(vm, "In ContentAutomatorX, create a bug report chat for the F keys");
        await WithinAsync(vm.PendingAnswers);
        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 3));
        return (vm, brains);
    }

    [Fact]
    public async Task The_chat_the_user_was_moved_to_tells_its_brain_the_question_once()
    {
        // Its brain is another, and knows nothing of what was asked in chat 1 (#180).
        var (vm, brains) = await MovedToChat3Async();

        Type(vm, "open it");
        await WithinAsync(vm.PendingAnswers);
        Type(vm, "and then?");
        await WithinAsync(vm.PendingAnswers);

        var three = brains.Windows[ContentAutomatorX].Sent;
        three[0].ShouldStartWith("[The user asked this in chat 1, CodeSwitchX, and was moved here when Raven started a chat in this window: "
            + "\"In ContentAutomatorX, create a bug report chat for the F keys\"]");
        three[0].ShouldEndWith("open it");
        three[1].ShouldNotContain("moved here", Case.Insensitive, "told once");
        brains.Windows[CodeSwitchX].Sent.ShouldHaveSingleItem("chat 1's brain answered the question itself");
    }

    [Fact]
    public async Task A_moved_question_is_told_only_while_it_is_fresh()
    {
        var (vm, brains) = await MovedToChat3Async();

        _time.Advance(RavenPanelViewModel.CarriedLifetime + TimeSpan.FromMinutes(1));
        Type(vm, "open it");
        await WithinAsync(vm.PendingAnswers);

        brains.Windows[ContentAutomatorX].Sent.ShouldHaveSingleItem().ShouldNotContain("moved here", Case.Insensitive);
    }
}
