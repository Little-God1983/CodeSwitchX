using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>
/// A chat's question waits in the panel: a card with its options, read out once the floor is free, answered by a click,
/// by Send, or by voice through the brain, or left to VS Code.
/// </summary>
public sealed partial class RavenPanelViewModelTests
{
    private async Task<(RavenPanelViewModel Vm, ChatAsks Asks)> QuestionsVmAsync(ChatNews? news = null, bool openMic = false, IUiDispatcher? dispatcher = null)
    {
        _yard.Show("a", "ContentAutomatorX", "Fix the upload retry");
        var asks = new ChatAsks(_bus, _time) { Takes = _ => true };
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, dispatcher ?? new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, news, _teller, openMic ? _openMic : null, asks: asks, yard: _yard);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        InContentAutomatorX(vm);
        return (vm, asks);
    }

    /// <summary>ContentAutomatorX, where "a" runs, is window 1, and the user is in its chat: a chat on no tile asks in VS Code (#148).</summary>
    private static void InContentAutomatorX(RavenPanelViewModel vm)
    {
        vm.SetWorkspaces([(FakeYardDirectory.WorkspaceOf("ContentAutomatorX"), 1, "ContentAutomatorX")]);
        vm.SelectedChat = vm.Chats.Single(c => c.Number == 1);
    }

    /// <summary>What the brain of chat 1, where the user is, is told before their words.</summary>
    private const string InChatOne = "[The user is in chat 1, ContentAutomatorX: \"it\" and \"this\" mean that window unless they name another.]\n";

    private ChatAsk Asking(params ChatQuestion[] questions) => new("toolu_1",
        new HookEvent { SessionId = "a", EventName = "PreToolUse", At = _time.GetUtcNow(), ToolName = "AskUserQuestion", ToolUseId = "toolu_1" },
        questions.Length > 0 ? questions : [Fruit]);

    private static readonly ChatQuestion Fruit = new("Which fruit?", "Fruit", [new ChatQuestionOption("Apple", "red"), new ChatQuestionOption("Banana", null)], false);

    private static readonly ChatQuestion Colours = new("Which colours?", null, [new ChatQuestionOption("Red", null), new ChatQuestionOption("Blue", null)], true);

    private static ChatAskCard Card(RavenPanelViewModel vm) => vm.Log.Single(e => e.Kind == RavenLogKind.Question).Ask!;

    [Fact]
    public async Task A_chat_s_question_is_shown_read_out_and_answered_by_a_click_on_its_option()
    {
        var (vm, asks) = await QuestionsVmAsync();

        var held = asks.HoldAsync(Asking(), CancellationToken.None);
        var card = Card(vm);
        vm.OpenQuestions.ShouldBe(1);
        await GraceAsync(vm);
        // Spoken in pieces, a sentence each: wait for the last.
        await Until(() => string.Join(" ", _speech.Spoken).EndsWith("Banana.", StringComparison.Ordinal));

        card.Chat.ShouldBe("ContentAutomatorX · Fix the upload retry");
        string.Join(" ", _speech.Spoken).ShouldBe("ContentAutomatorX, chat \"Fix the upload retry\" asks: Which fruit? Apple or Banana.");
        card.NeedsSend.ShouldBeFalse();

        vm.ChooseOptionCommand.Execute(card.Questions[0].Options[1]);

        await WithinAsync(held);
        (await held).ShouldNotBeNull().Answers.ShouldBe(["Banana"]);
        card.IsOpen.ShouldBeFalse();
        card.Outcome.ShouldBe("Answered: Banana");
        vm.OpenQuestions.ShouldBe(0);
        vm.ChooseOptionCommand.Execute(card.Questions[0].Options[0]);
        card.Questions[0].Options[0].IsChosen.ShouldBeFalse("an answered card takes no more clicks");
    }

    [Fact]
    public async Task Several_questions_are_sent_together_once_each_has_an_option()
    {
        var (vm, asks) = await QuestionsVmAsync();
        var held = asks.HoldAsync(Asking(Fruit, Colours), CancellationToken.None);
        var card = Card(vm);
        card.NeedsSend.ShouldBeTrue();

        vm.ChooseOptionCommand.Execute(card.Questions[0].Options[0]);
        vm.ChooseOptionCommand.Execute(card.Questions[1].Options[0]);
        vm.ChooseOptionCommand.Execute(card.Questions[1].Options[1]);
        held.IsCompleted.ShouldBeFalse("nothing goes before Send");
        vm.ChooseOptionCommand.Execute(card.Questions[0].Options[1]);
        card.Questions[0].Options.Select(o => o.IsChosen).ShouldBe([false, true], "a question that takes one option keeps the last one clicked");
        card.CanSend.ShouldBeTrue();

        vm.SendAnswersCommand.Execute(card);

        await WithinAsync(held);
        (await held).ShouldNotBeNull().Answers.ShouldBe(["Banana", "Red, Blue"]);
    }

    [Fact]
    public async Task Send_waits_until_every_question_has_an_option()
    {
        var (vm, asks) = await QuestionsVmAsync();
        var held = asks.HoldAsync(Asking(Fruit, Colours), CancellationToken.None);
        var card = Card(vm);

        vm.ChooseOptionCommand.Execute(card.Questions[0].Options[0]);
        card.CanSend.ShouldBeFalse();
        vm.SendAnswersCommand.Execute(card);

        held.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Answer_in_VS_Code_leaves_the_question_to_the_chat_s_tab()
    {
        var (vm, asks) = await QuestionsVmAsync();
        var held = asks.HoldAsync(Asking(), CancellationToken.None);
        var card = Card(vm);

        vm.AnswerInVsCodeCommand.Execute(card);

        await WithinAsync(held);
        (await held).ShouldNotBeNull().Outcome.ShouldBe(ChatAskOutcome.ToVsCode);
        card.IsOpen.ShouldBeFalse();
        card.Outcome.ShouldBe("Left to VS Code: it asks there.");
    }

    [Fact]
    public async Task The_brain_that_acts_is_told_the_question_with_the_user_s_words_so_the_first_one_answers_it()
    {
        _brain.Answer = _ => [new BrainText("Answered.")];
        var (vm, asks) = await QuestionsVmAsync();
        _ = asks.HoldAsync(Asking(), CancellationToken.None);
        await GraceAsync(vm);

        Type(vm, "the first one");
        await WithinAsync(vm.PendingAnswers);

        _brain.Asked.ShouldHaveSingleItem().ShouldBe(Told + "ContentAutomatorX, chat \"Fix the upload retry\" (chat id a) asks, and waits for the "
            + "answer here: \"Which fruit?\" (one of: Apple, Banana). answer_question answers it.]\n" + InChatOne + "the first one");
    }

    [Fact]
    public async Task A_question_answered_since_it_was_read_out_is_not_told_to_the_brain_as_waiting()
    {
        _brain.Answer = _ => [new BrainText("Nothing waits.")];
        var (vm, asks) = await QuestionsVmAsync();
        _ = asks.HoldAsync(Asking(), CancellationToken.None);
        await GraceAsync(vm);
        await Until(() => _speech.Spoken.Count > 0);
        vm.ChooseOptionCommand.Execute(Card(vm).Questions[0].Options[1]);

        Type(vm, "does anything wait for me?");
        await WithinAsync(vm.PendingAnswers);

        _brain.Asked.ShouldHaveSingleItem().ShouldNotContain("waits for the answer here");
    }

    [Fact]
    public async Task A_chat_stopped_while_its_question_waits_says_so_on_the_card()
    {
        var (vm, asks) = await QuestionsVmAsync();
        var held = asks.HoldAsync(Asking(), CancellationToken.None);

        asks.Stop("a");

        await WithinAsync(held);
        Card(vm).Outcome.ShouldBe("The chat was stopped.");
    }

    [Fact]
    public async Task A_question_answered_by_voice_shows_its_answer_on_the_card()
    {
        var (vm, asks) = await QuestionsVmAsync();
        var held = asks.HoldAsync(Asking(), CancellationToken.None);

        asks.Answer("toolu_1", ["something of my own"]);

        await WithinAsync(held);
        (await held).ShouldNotBeNull().Answers.ShouldBe(["something of my own"]);
        Card(vm).Outcome.ShouldBe("Answered: something of my own");
    }

    [Fact]
    public async Task Muted_the_question_is_only_shown()
    {
        var (vm, asks) = await QuestionsVmAsync();
        vm.IsMuted = true;
        _ = asks.HoldAsync(Asking(), CancellationToken.None);

        await GraceAsync(vm);

        _speech.Spoken.ShouldBeEmpty();
        Card(vm).IsOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task With_news_not_to_be_spoken_the_question_is_only_shown()
    {
        var (vm, asks) = await QuestionsVmAsync();
        vm.SpeakNews = false;
        _ = asks.HoldAsync(Asking(), CancellationToken.None);

        await GraceAsync(vm);

        _speech.Spoken.ShouldBeEmpty();
        Card(vm).IsOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task A_question_goes_before_the_news_and_its_waiting_is_no_news()
    {
        _teller.Answer = _ => [new BrainText("Release notes is done.")];
        _yard.Show("b", "ContentAutomatorX", "Release notes"); // in the window the user is in: its news is spoken
        var asks = new ChatAsks(_bus, _time) { Takes = _ => true };
        var news = new ChatNews(_bus, _yard, _time, _ => null, askedHere: asks.Explains);
        _time.Advance(TimeSpan.FromSeconds(1));
        var (vm, _) = await QuestionsVmAsyncWith(asks, news);

        Changes("b", SessionState.Working, SessionState.Idle);
        _ = asks.HoldAsync(Asking(), CancellationToken.None);
        Changes("a", SessionState.Working, SessionState.Waiting);
        await GraceAsync(vm);
        await GraceAsync(vm);
        // The question is read in two pieces: wait for the news itself.
        await Until(() => _speech.Spoken.Contains("Release notes is done."));

        string.Join(" ", _speech.Spoken).ShouldStartWith("ContentAutomatorX, chat \"Fix the upload retry\" asks: Which fruit?");
        _speech.Spoken[^1].ShouldBe("Release notes is done.");
        vm.Log.Single(e => e.Kind == RavenLogKind.News).Lines!.Select(l => l.SessionId).ShouldBe(["b"], "the card tells that the chat needs the user");
    }

    private async Task<(RavenPanelViewModel Vm, ChatAsks Asks)> QuestionsVmAsyncWith(ChatAsks asks, ChatNews news)
    {
        _yard.Show("a", "ContentAutomatorX", "Fix the upload retry");
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, news, _teller, asks: asks, yard: _yard);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        InContentAutomatorX(vm);
        return (vm, asks);
    }

    [Fact]
    public void What_Raven_says_of_several_questions_names_each_and_its_options()
    {
        var card = new ChatAskCard(Asking(Fruit, Colours)) { Said = "CodeSwitchX, chat \"Release notes\"" };

        RavenPanelViewModel.QuestionSentence([card]).ShouldBe(
            "CodeSwitchX, chat \"Release notes\" asks 2 questions. Which fruit? Apple or Banana. Which colours? Any of Red or Blue.");
    }
}
