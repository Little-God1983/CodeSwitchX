using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Dictation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>
/// #230: "next question" goes to the oldest open card in any window, and after an answer Raven offers the next one.
/// </summary>
public sealed partial class RavenPanelViewModelTests
{
    /// <summary>Three windows: ContentAutomatorX (1, chat "a"), DiffusionNexus (2, chat "c"), RawCutX (3, chat "d"); the user is in chat 1.</summary>
    private async Task<(RavenPanelViewModel Vm, ChatAsks Asks)> NextQuestionVmAsync(ChatNews? news = null, bool openMic = false, IChatBrains? brains = null)
    {
        string[] workspaces = ["ContentAutomatorX", "DiffusionNexus", "RawCutX"];
        string[] ids = ["a", "c", "d"];
        for (var i = 0; i < ids.Length; i++)
        {
            _yard.Show(ids[i], workspaces[i], $"Task {ids[i]}");
        }

        var asks = new ChatAsks(_bus, _time) { Takes = _ => true };
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, news, _teller, openMic ? _openMic : null, asks: asks, yard: _yard, brains: brains);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        _time.Advance(TimeSpan.FromSeconds(1)); // the chats' changes come after the app started
        vm.SetWorkspaces([.. workspaces.Select((w, i) => (FakeYardDirectory.WorkspaceOf(w), i + 1, w))]);
        vm.SelectedChat = ChatNumbered(vm, 1);
        return (vm, asks);
    }

    /// <summary>Chat <paramref name="sessionId"/> asks which fruit, now; the card is placed in its window's chat.</summary>
    private async Task<Task<ChatAskClosed?>> AsksFruitAsync(RavenPanelViewModel vm, ChatAsks asks, string sessionId)
    {
        var cards = vm.Log.Count(e => e.Kind == RavenLogKind.Question);
        var ask = new ChatAsk($"toolu_{sessionId}",
            new HookEvent { SessionId = sessionId, EventName = "PreToolUse", At = _time.GetUtcNow(), ToolName = "AskUserQuestion", ToolUseId = $"toolu_{sessionId}" },
            [Fruit]);
        var held = asks.HoldAsync(ask, CancellationToken.None);
        await Until(() => vm.Log.Count(e => e.Kind == RavenLogKind.Question) > cards);
        _time.Advance(TimeSpan.FromSeconds(1)); // the next one asks later
        return held;
    }

    private static ChatAskCard CardOf(RavenPanelViewModel vm, string sessionId) =>
        vm.Log.Single(e => e.Kind == RavenLogKind.Question && e.Ask!.Ask.SessionId == sessionId).Ask!;

    private string SpokenSince(int count) => string.Join(" ", _speech.Spoken.Skip(count));

    [Fact]
    public async Task Next_question_goes_to_the_oldest_open_card_in_any_window_and_reads_it_out()
    {
        var (vm, asks) = await NextQuestionVmAsync();
        _ = await AsksFruitAsync(vm, asks, "d"); // RawCutX asks first
        _ = await AsksFruitAsync(vm, asks, "c");
        await GraceAsync(vm);
        var before = _speech.Spoken.Count;

        Type(vm, "next question");

        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 3));
        await Until(() => SpokenSince(before).EndsWith("Banana.", StringComparison.Ordinal));
        await WithinAsync(_voice.WhenQuietAsync());
        SpokenSince(before).ShouldBe("RawCutX, chat \"Task d\" asks: Which fruit? Apple or Banana.");
        _brain.Asked.ShouldBeEmpty("the app goes there itself");
    }

    [Fact]
    public async Task Next_question_with_none_open_says_so()
    {
        var (vm, _) = await NextQuestionVmAsync();

        Type(vm, "nächste Frage");

        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 1));
        vm.Log[^1].Text.ShouldBe("No questions are waiting.");
        await Until(() => _speech.Spoken.Contains("No questions are waiting."));
    }

    [Fact]
    public async Task After_an_answer_the_next_card_is_offered_and_a_yes_goes_there()
    {
        var (vm, asks) = await NextQuestionVmAsync();
        var first = await AsksFruitAsync(vm, asks, "a"); // in chat 1, where the user is: read out
        _ = await AsksFruitAsync(vm, asks, "d");
        await GraceAsync(vm);
        await Until(() => string.Join(" ", _speech.Spoken).EndsWith("Banana.", StringComparison.Ordinal));

        vm.ChooseOptionCommand.Execute(CardOf(vm, "a").Questions[0].Options[1]);
        await WithinAsync(first);
        await GraceAsync(vm);
        await Until(() => vm.OffersNext); // heard to its end
        vm.Log.Last(e => e.Kind == RavenLogKind.Raven).Text.ShouldBe("One more question is waiting. Next?");
        var before = _speech.Spoken.Count;

        Type(vm, "yes");

        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 3));
        await Until(() => SpokenSince(before).EndsWith("Banana.", StringComparison.Ordinal));
        await WithinAsync(_voice.WhenQuietAsync());
        SpokenSince(before).ShouldBe("RawCutX, chat \"Task d\" asks: Which fruit? Apple or Banana.");
        _brain.Asked.ShouldBeEmpty("the yes goes to no brain");
    }

    /// <summary>"Next?" is a question: news waits while it stands, as while an allow waits for its yes, and comes once it lapses.</summary>
    [Fact]
    public async Task News_waits_while_the_offer_stands_and_comes_once_it_lapsed()
    {
        _teller.Answer = q => [new BrainText("Task a is done.")];
        var (vm, asks) = await NextQuestionVmAsync(new ChatNews(_bus, _yard, _time, _ => "Done."));
        var first = await AsksFruitAsync(vm, asks, "a");
        _ = await AsksFruitAsync(vm, asks, "d");
        vm.ChooseOptionCommand.Execute(CardOf(vm, "a").Questions[0].Options[1]);
        await WithinAsync(first);
        await GraceAsync(vm);
        await Until(() => vm.OffersNext); // heard to its end
        await Until(() => vm.State == RavenState.Idle);

        Changes("a", SessionState.Working, SessionState.Idle);
        _time.Advance(vm.Traffic.WaitBeforeTelling);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _speech.Spoken.ShouldNotContain("Task a is done.", "the offer stands");

        _time.Advance(RavenPanelViewModel.NextOfferLifetime);
        await GraceAsync(vm);
        await Until(() => _speech.Spoken.Contains("Task a is done."));
    }

    [Fact]
    public async Task Other_words_after_the_offer_let_it_go()
    {
        _brain.Answer = _ => [new BrainText("Sure.")];
        var (vm, asks) = await NextQuestionVmAsync();
        var first = await AsksFruitAsync(vm, asks, "a");
        _ = await AsksFruitAsync(vm, asks, "d");
        vm.ChooseOptionCommand.Execute(CardOf(vm, "a").Questions[0].Options[1]);
        await WithinAsync(first);
        await GraceAsync(vm);
        await Until(() => vm.OffersNext); // heard to its end

        Type(vm, "what time is it");
        await WithinAsync(vm.PendingAnswers);
        Type(vm, "yes");
        await WithinAsync(vm.PendingAnswers);

        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 1));
        _brain.Asked.Count.ShouldBe(2, "both went to the brain: the offer was let go");
    }

    [Fact]
    public async Task A_card_answered_while_the_offer_stands_offers_again_with_the_new_count()
    {
        var (vm, asks) = await NextQuestionVmAsync();
        var first = await AsksFruitAsync(vm, asks, "a");
        var second = await AsksFruitAsync(vm, asks, "c");
        _ = await AsksFruitAsync(vm, asks, "d");
        vm.ChooseOptionCommand.Execute(CardOf(vm, "a").Questions[0].Options[1]);
        await WithinAsync(first);
        await GraceAsync(vm);
        await Until(() => vm.OffersNext);
        vm.Log.Last(e => e.Kind == RavenLogKind.Raven).Text.ShouldBe("2 more questions are waiting. Next?");

        vm.ChooseOptionCommand.Execute(CardOf(vm, "c").Questions[0].Options[0]); // clicked in chat 2's card from Activity, say
        await WithinAsync(second);
        vm.OffersNext.ShouldBeFalse("the offer counted the card just answered");
        await GraceAsync(vm);
        await Until(() => vm.OffersNext);

        vm.Log.Last(e => e.Kind == RavenLogKind.Raven).Text.ShouldBe("One more question is waiting. Next?");
    }

    [Fact]
    public async Task A_yes_after_the_offer_lapsed_goes_to_the_brain()
    {
        _brain.Answer = _ => [new BrainText("Yes to what?")];
        var (vm, asks) = await NextQuestionVmAsync();
        var first = await AsksFruitAsync(vm, asks, "a");
        _ = await AsksFruitAsync(vm, asks, "d");
        vm.ChooseOptionCommand.Execute(CardOf(vm, "a").Questions[0].Options[1]);
        await WithinAsync(first);
        await GraceAsync(vm);
        await Until(() => vm.OffersNext);

        _time.Advance(RavenPanelViewModel.NextOfferLifetime + TimeSpan.FromSeconds(1));
        await Until(() => !vm.OffersNext);
        Type(vm, "yes");
        await WithinAsync(vm.PendingAnswers);

        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 1));
        _brain.Asked.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Words_before_the_offer_is_said_drop_it()
    {
        _brain.Answer = _ => [new BrainText("Sure.")];
        var (vm, asks) = await NextQuestionVmAsync();
        var first = await AsksFruitAsync(vm, asks, "a");
        _ = await AsksFruitAsync(vm, asks, "d");
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        vm.ChooseOptionCommand.Execute(CardOf(vm, "a").Questions[0].Options[1]);
        await WithinAsync(first);
        Type(vm, "what time is it"); // before the floor was free for the offer
        await WithinAsync(vm.PendingAnswers);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        _speech.Spoken.ShouldNotContain("Next?", "the user moved on");
        vm.OffersNext.ShouldBeFalse();
    }

    [Fact]
    public async Task The_card_the_brain_asked_for_is_read_with_chat_news_only_written()
    {
        var (vm, asks) = await NextQuestionVmAsync();
        vm.SpeakNews = false;
        _ = await AsksFruitAsync(vm, asks, "c");
        var before = _speech.Spoken.Count;

        vm.NextQuestionForBrain(null).ShouldBe("Chat 2, DiffusionNexus. Its question is read out next.");
        await GraceAsync(vm);
        await Until(() => SpokenSince(before).EndsWith("Banana.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_card_left_to_VS_Code_offers_no_next_one()
    {
        var (vm, asks) = await NextQuestionVmAsync();
        var first = await AsksFruitAsync(vm, asks, "a");
        _ = await AsksFruitAsync(vm, asks, "d");
        await GraceAsync(vm);

        vm.AnswerInVsCodeCommand.Execute(CardOf(vm, "a"));
        await WithinAsync(first);
        await GraceAsync(vm);
        await WithinAsync(_voice.WhenQuietAsync());

        _speech.Spoken.ShouldNotContain("Next?");
    }

    [Fact]
    public async Task The_brain_s_next_question_shows_the_chat_and_its_card_is_read_after_the_answer()
    {
        var (vm, asks) = await NextQuestionVmAsync();
        _ = await AsksFruitAsync(vm, asks, "c");
        await GraceAsync(vm);
        var before = _speech.Spoken.Count;

        vm.NextQuestionForBrain(null).ShouldBe("Chat 2, DiffusionNexus. Its question is read out next.");

        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 2));
        await GraceAsync(vm);
        await Until(() => SpokenSince(before).EndsWith("Banana.", StringComparison.Ordinal));
        await WithinAsync(_voice.WhenQuietAsync());
        SpokenSince(before).ShouldBe("DiffusionNexus, chat \"Task c\" asks: Which fruit? Apple or Banana.");
    }

    /// <summary>#234: a chat's summary is written in the Raven chat it was asked in.</summary>
    [Fact]
    public async Task A_summary_is_written_where_it_was_asked()
    {
        var (vm, _) = await NextQuestionVmAsync();

        vm.WriteSummary("overview", "Summary one");
        vm.WriteSummary(FakeYardDirectory.WorkspaceOf("DiffusionNexus").ToString(), "Summary two");
        vm.WriteSummary(null, "Summary three");

        vm.Log.Single(e => e.Text == "Summary one").Chat.ShouldBe(vm.YardChat);
        vm.Log.Single(e => e.Text == "Summary two").Chat.ShouldBe(ChatNumbered(vm, 2));
        vm.Log.Single(e => e.Text == "Summary three").Chat.ShouldBe(ChatNumbered(vm, 1), "the chat the user is in");
    }

    [Fact]
    public async Task The_brain_s_next_question_with_none_open_is_null()
    {
        var (vm, _) = await NextQuestionVmAsync();

        vm.NextQuestionForBrain(null).ShouldBeNull();
    }

    /// <summary>
    /// Chat "a" asks, then "d"; the card of "a" is answered by a click: the next one is offered, heard, or only written
    /// when Raven keeps quiet (<paramref name="quiet"/>), which then holds no floor.
    /// </summary>
    private async Task OfferedAfterAnAnswerAsync(RavenPanelViewModel vm, ChatAsks asks, bool quiet = false)
    {
        var first = await AsksFruitAsync(vm, asks, "a");
        _ = await AsksFruitAsync(vm, asks, "d");
        await GraceAsync(vm);
        vm.ChooseOptionCommand.Execute(CardOf(vm, "a").Questions[0].Options[1]);
        await WithinAsync(first);
        await GraceAsync(vm);
        if (quiet)
        {
            await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Raven && e.Text.EndsWith("Next?", StringComparison.Ordinal)));
            vm.OffersNext.ShouldBeFalse("only written: it holds no floor");
        }
        else
        {
            await Until(() => vm.OffersNext);
        }
    }

    // #233: the offer is Raven speaking up on its own; the card a yes asks for is read out all the same
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_chat_news_only_written_or_the_chat_muted_the_offer_is_only_written_and_a_yes_still_takes_it(bool muted)
    {
        var (vm, asks) = await NextQuestionVmAsync();
        if (muted)
        {
            ChatNumbered(vm, 1).IsMuted = true;
        }
        else
        {
            vm.SpeakNews = false;
        }

        await OfferedAfterAnAnswerAsync(vm, asks, quiet: true);

        vm.Log.Last(e => e.Kind == RavenLogKind.Raven).Text.ShouldBe("One more question is waiting. Next?");
        _speech.Spoken.ShouldNotContain(s => s.Contains("Next?"));
        var before = _speech.Spoken.Count;

        Type(vm, "yes");

        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 3));
        await Until(() => SpokenSince(before).EndsWith("Banana.", StringComparison.Ordinal));
    }

    // #233: "next question" by its hotkey is asked for: the pause after Raven last spoke does not hold it
    [Fact]
    public async Task Next_question_by_its_hotkey_reads_the_card_without_waiting_for_the_pause()
    {
        var (vm, asks) = await NextQuestionVmAsync();
        _ = await AsksFruitAsync(vm, asks, "c");
        await GraceAsync(vm);
        vm.Traffic.Pause = TimeSpan.FromSeconds(30);
        vm.Traffic.Announced(); // Raven spoke a moment ago: news would wait half a minute
        var before = _speech.Spoken.Count;

        vm.GoToNextQuestionByKey();

        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 2));
        await Until(() => SpokenSince(before).EndsWith("Banana.", StringComparison.Ordinal));
        SpokenSince(before).ShouldBe("DiffusionNexus, chat \"Task c\" asks: Which fruit? Apple or Banana.");
    }

    // #233: a long command is read in the teller's words, as when its card came
    [Fact]
    public async Task Next_question_reads_a_long_command_in_the_teller_s_words()
    {
        const string command = "$out = Join-Path $PSScriptRoot 'dist'\nRemove-Item $out -Recurse -Force\ndotnet publish -c Release -o $out";
        _teller.Answer = _ => [new BrainText("RawCutX, chat \"Task d\" wants to run a script that builds the installer")];
        var (vm, asks) = await NextQuestionVmAsync();
        var ask = new ChatAsk("p_d",
            new HookEvent { SessionId = "d", EventName = "PermissionRequest", At = _time.GetUtcNow(), ToolName = "PowerShell", ToolInputHash = command },
            [], new ChatPermission("PowerShell", "run a command", command, null, Risks: [PermissionRisk.DeletesFiles]));
        _ = asks.HoldAsync(ask, CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Permission));
        await GraceAsync(vm);
        var before = _speech.Spoken.Count;

        vm.GoToNextQuestionByKey();

        await Until(() => SpokenSince(before).EndsWith("It's on the card.", StringComparison.Ordinal));
        SpokenSince(before).ShouldBe("RawCutX, chat \"Task d\" wants to run a script that builds the installer. It deletes files. It's on the card.");
    }

    // #233: "next question" while the user talks in Open mic switched to the card but neither said nor wrote it
    [Fact]
    public async Task Next_question_while_the_user_talks_in_Open_mic_reads_the_card_once_the_turn_is_over()
    {
        var rest = new TaskCompletionSource<DictationResult>();
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DictationResult("Raven, hello.", TimeSpan.FromSeconds(1))), rest.Task);
        _brain.Answer = _ => [new BrainText("Hi.")];
        var (vm, asks) = await NextQuestionVmAsync(openMic: true);
        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);
        _ = await AsksFruitAsync(vm, asks, "d"); // in window 3: not read here
        await AnsweredAMomentAgoAsync(vm);
        _openMic.Speak(); // in the follow-up: the user's, and it takes the floor
        vm.State.ShouldBe(RavenState.Listening);
        var before = _speech.Spoken.Count;

        vm.GoToNextQuestionByKey(); // its hotkey

        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 3));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        SpokenSince(before).ShouldBeEmpty("Raven does not talk over the user");
        vm.State.ShouldBe(RavenState.Listening, "the user still has the floor");
        _openMic.EndTurn();
        rest.SetResult(new DictationResult("", TimeSpan.FromSeconds(1)));
        await WithinAsync(vm.PendingTranscriptions);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        SpokenSince(before).ShouldBeEmpty("the grace first: the user may go on after a breath");
        _time.Advance(TrafficWatcher.NewsGrace);
        await Until(() => SpokenSince(before).EndsWith("Banana.", StringComparison.Ordinal));
        SpokenSince(before).ShouldBe("RawCutX, chat \"Task d\" asks: Which fruit? Apple or Banana.");
    }

    // #233: in Open mic, "Next?" takes a bare yes; other talk around the room stays out
    [Theory]
    [InlineData("Yes.", true, true)]
    [InlineData("Sounds good to me.", true, false)]
    [InlineData("Yes.", false, false)] // only written: talk around the room must not read a card aloud
    [InlineData("Raven, yes.", false, true)]
    public async Task In_Open_mic_the_offer_heard_takes_a_yes_without_the_name_and_nothing_else(string said, bool speakNews, bool taken)
    {
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DictationResult(said, TimeSpan.FromSeconds(1))));
        var (vm, asks) = await NextQuestionVmAsync(openMic: true);
        vm.SpeakNews = speakNews;
        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);
        await OfferedAfterAnAnswerAsync(vm, asks, quiet: !speakNews);
        vm.TakesTurnsWithoutName.ShouldBeFalse("the offer followed a click: only a yes gets through");

        _openMic.Speak();
        _openMic.EndTurn();
        await WithinAsync(vm.PendingTranscriptions);

        vm.SelectedChat.ShouldBe(ChatNumbered(vm, taken ? 3 : 1));
        _brain.Asked.ShouldBeEmpty();
        vm.OffersNext.ShouldBe(!taken && speakNews);
    }

    // #233: a new offer stands for its own lifetime, not what was left of the one before it
    [Fact]
    public async Task A_new_offer_stands_for_its_own_lifetime()
    {
        var (vm, asks) = await NextQuestionVmAsync();
        var first = await AsksFruitAsync(vm, asks, "a");
        var second = await AsksFruitAsync(vm, asks, "c");
        _ = await AsksFruitAsync(vm, asks, "d");
        vm.ChooseOptionCommand.Execute(CardOf(vm, "a").Questions[0].Options[1]);
        await WithinAsync(first);
        await GraceAsync(vm);
        await Until(() => vm.OffersNext);
        _time.Advance(RavenPanelViewModel.NextOfferLifetime / 2);

        vm.ChooseOptionCommand.Execute(CardOf(vm, "c").Questions[0].Options[0]);
        await WithinAsync(second);
        await GraceAsync(vm);
        await Until(() => vm.OffersNext);
        _time.Advance(RavenPanelViewModel.NextOfferLifetime / 2 + TimeSpan.FromSeconds(1)); // past the first one's end

        vm.OffersNext.ShouldBeTrue();
        _time.Advance(RavenPanelViewModel.NextOfferLifetime / 2);
        await Until(() => !vm.OffersNext);
    }

    // #233: a yes to "Next?" goes to the next card's chat, and so does what was said right after it, as after "chat three"
    [Fact]
    public async Task A_question_said_right_after_a_yes_to_the_offer_goes_to_the_chat_it_switched_to()
    {
        var yes = new TaskCompletionSource<DictationResult>();
        var (vm, asks) = await NextQuestionVmAsync();
        await OfferedAfterAnAnswerAsync(vm, asks);
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(yes.Task, Task.FromResult(new DictationResult("what is it doing", TimeSpan.FromSeconds(2))));
        vm.PressMic(TalkInput.MicButton);
        Speak();
        _time.Advance(Hold);
        var yesRelease = vm.ReleaseMicAsync(TalkInput.MicButton);
        await WithinAsync(vm.PendingStop);
        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);
        Speak();
        _time.Advance(Hold);
        var questionRelease = vm.ReleaseMicAsync(TalkInput.MicButton);
        await WithinAsync(vm.PendingStop);

        yes.SetResult(new DictationResult("yes", TimeSpan.FromSeconds(1)));
        await WithinAsync(yesRelease);
        await WithinAsync(questionRelease);
        await WithinAsync(vm.PendingAnswers);

        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 3));
        vm.Log.Single(e => e.Kind == RavenLogKind.You && e.Text == "what is it doing").Chat.ShouldBe(ChatNumbered(vm, 3));
        _brain.Asked.ShouldHaveSingleItem().ShouldContain("[The user is in chat 3, RawCutX");
    }

    // #233: "next question" ends an allow waiting for its yes: a yes then must not allow it, nor the card wait behind it
    [Fact]
    public async Task Next_question_ends_an_allow_waiting_for_its_yes()
    {
        var (vm, asks) = await NextQuestionVmAsync();
        _ = await AsksFruitAsync(vm, asks, "a"); // the oldest card, in the chat the user is in: no switch ends the allow
        _ = asks.HoldAsync(Permitting(), CancellationToken.None); // chat "a" too
        var card = PermissionCards(vm).ShouldHaveSingleItem();
        await card.Naming;
        await GraceAsync(vm);
        var proposal = asks.Propose("p1");
        await Until(() => asks.IsHeard(proposal));
        var before = _speech.Spoken.Count;

        vm.GoToNextQuestionByKey();

        asks.Proposed.ShouldBeNull("the user moved on");
        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 1));
        await Until(() => SpokenSince(before).EndsWith("Banana.", StringComparison.Ordinal));
        card.IsOpen.ShouldBeTrue("only the proposal ended: the card waits for a click or a new allow");
    }

    // #233: "next question" while news is being worded: the telling ends, unsaid in the chat switched to, and the card follows
    [Fact]
    public async Task Next_question_during_a_news_telling_reads_the_card_once_it_ends_and_not_the_news()
    {
        _teller.Answer = _ => [new BrainText("Task a is done.")];
        var (vm, asks) = await NextQuestionVmAsync(new ChatNews(_bus, _yard, _time, _ => "Done."));
        _ = await AsksFruitAsync(vm, asks, "d");
        await GraceAsync(vm);
        _teller.Gate = new TaskCompletionSource(); // the teller is still at it when the user asks
        Changes("a", SessionState.Working, SessionState.Idle);
        _time.Advance(vm.Traffic.WaitBeforeTelling);
        await Until(() => _teller.Asked.Count > 0);
        var before = _speech.Spoken.Count;

        vm.GoToNextQuestionByKey();
        _teller.Gate.SetResult();

        await Until(() => SpokenSince(before).EndsWith("Banana.", StringComparison.Ordinal));
        await WithinAsync(_voice.WhenQuietAsync());
        SpokenSince(before).ShouldBe("RawCutX, chat \"Task d\" asks: Which fruit? Apple or Banana.");
    }

    // #233: an offer Raven kept quiet holds no floor: a card that comes meanwhile in the muted chat is read at once
    [Fact]
    public async Task An_offer_kept_quiet_holds_no_floor()
    {
        var (vm, asks) = await NextQuestionVmAsync();
        ChatNumbered(vm, 1).IsMuted = true; // its cards are still read; its news and the offer are not
        await OfferedAfterAnAnswerAsync(vm, asks, quiet: true);
        var before = _speech.Spoken.Count;

        var ask = new ChatAsk("toolu_a2",
            new HookEvent { SessionId = "a", EventName = "PreToolUse", At = _time.GetUtcNow(), ToolName = "AskUserQuestion", ToolUseId = "toolu_a2" },
            [Fruit]);
        _ = asks.HoldAsync(ask, CancellationToken.None);
        await Until(() => vm.Log.Count(e => e.Kind == RavenLogKind.Question) == 3);
        await GraceAsync(vm);

        await Until(() => SpokenSince(before).EndsWith("Banana.", StringComparison.Ordinal)); // not 30 s later
    }

    // #252: "No questions are waiting." while the user talked in Open mic was only written, never said after the turn
    [Fact]
    public async Task Next_question_with_none_open_while_the_user_talks_in_Open_mic_says_so_once_the_turn_is_over()
    {
        var rest = new TaskCompletionSource<DictationResult>();
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DictationResult("Raven, hello.", TimeSpan.FromSeconds(1))), rest.Task);
        _brain.Answer = _ => [new BrainText("Hi.")];
        var (vm, _) = await NextQuestionVmAsync(openMic: true);
        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);
        await AnsweredAMomentAgoAsync(vm);
        _openMic.Speak(); // in the follow-up: the user's, and it takes the floor
        var before = _speech.Spoken.Count;

        vm.GoToNextQuestionByKey();

        vm.Log[^1].Text.ShouldBe(RavenPanelViewModel.NoQuestionsLine);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        SpokenSince(before).ShouldBeEmpty("Raven does not talk over the user");
        _openMic.EndTurn();
        rest.SetResult(new DictationResult("", TimeSpan.FromSeconds(1)));
        await WithinAsync(vm.PendingTranscriptions);
        _time.Advance(TrafficWatcher.NewsGrace); // asked while the user talked: they may go on after a breath
        await Until(() => SpokenSince(before) == RavenPanelViewModel.NoQuestionsLine);
    }

    // Round 1 of #252: a card that came while "No questions are waiting." waited for the turn is gone to instead
    [Fact]
    public async Task Next_question_with_none_open_goes_to_a_card_that_came_while_the_user_talked()
    {
        var rest = new TaskCompletionSource<DictationResult>();
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DictationResult("Raven, hello.", TimeSpan.FromSeconds(1))), rest.Task);
        _brain.Answer = _ => [new BrainText("Hi.")];
        var (vm, asks) = await NextQuestionVmAsync(openMic: true);
        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);
        await AnsweredAMomentAgoAsync(vm);
        _openMic.Speak();
        var before = _speech.Spoken.Count;
        vm.GoToNextQuestionByKey(); // none open yet

        _ = await AsksFruitAsync(vm, asks, "d"); // in window 3, while the user talks
        _openMic.EndTurn();
        rest.SetResult(new DictationResult("", TimeSpan.FromSeconds(1)));
        await WithinAsync(vm.PendingTranscriptions);
        _time.Advance(TrafficWatcher.NewsGrace);

        await Until(() => SpokenSince(before).EndsWith("Banana.", StringComparison.Ordinal));
        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 3));
        SpokenSince(before).ShouldNotContain(RavenPanelViewModel.NoQuestionsLine);
    }

    // Round 2 of #252: a question asked in the same turn drops "No questions are waiting.": after its answer it answers nothing
    [Fact]
    public async Task A_question_after_next_question_with_none_open_drops_the_no_questions_line()
    {
        var rest = new TaskCompletionSource<DictationResult>();
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DictationResult("Raven, hello.", TimeSpan.FromSeconds(1))), rest.Task);
        _brain.Answer = q => q.Contains("time") ? [new BrainText("Noon.")] : [new BrainText("Hi.")];
        var (vm, _) = await NextQuestionVmAsync(openMic: true);
        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);
        await AnsweredAMomentAgoAsync(vm);
        _openMic.Speak();
        var before = _speech.Spoken.Count;
        vm.GoToNextQuestionByKey(); // none open: the line waits for the turn

        _openMic.EndTurn();
        rest.SetResult(new DictationResult("What time is it?", TimeSpan.FromSeconds(1)));
        await WithinAsync(vm.PendingTranscriptions);
        await WithinAsync(vm.PendingAnswers);
        await GraceAsync(vm);
        _time.Advance(TrafficWatcher.NewsGrace);
        await WithinAsync(_voice.WhenQuietAsync());

        SpokenSince(before).ShouldBe("Noon.");
    }

    // Round 3 of #252: said in Open mic with none open, "No questions are waiting." waits the grace, as a card would
    [Fact]
    public async Task Next_question_said_in_Open_mic_with_none_open_says_so_after_the_grace()
    {
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DictationResult("Raven, next question.", TimeSpan.FromSeconds(1))));
        var (vm, _) = await NextQuestionVmAsync(openMic: true);
        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);
        var before = _speech.Spoken.Count;

        _openMic.Speak();
        _openMic.EndTurn();
        await WithinAsync(vm.PendingTranscriptions);

        vm.Log[^1].Text.ShouldBe(RavenPanelViewModel.NoQuestionsLine);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        SpokenSince(before).ShouldBeEmpty("the user may go on after a breath");
        _time.Advance(TrafficWatcher.NewsGrace);
        await Until(() => SpokenSince(before) == RavenPanelViewModel.NoQuestionsLine);
    }

    // #252: the Open mic grace is for a user who may go on talking, not for a key pressed in silence
    [Fact]
    public async Task Next_question_by_its_hotkey_in_Open_mic_with_the_user_quiet_reads_the_card_at_once()
    {
        var (vm, asks) = await NextQuestionVmAsync(openMic: true);
        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);
        _ = await AsksFruitAsync(vm, asks, "d"); // in window 3: not read here
        await GraceAsync(vm);
        var before = _speech.Spoken.Count;

        vm.GoToNextQuestionByKey();

        await Until(() => SpokenSince(before).EndsWith("Banana.", StringComparison.Ordinal)); // no grace waited
    }

    // #252: "next question" stops a telling on its way, so the card asked for is read at once
    [Fact]
    public async Task Next_question_during_a_news_telling_stops_it_and_reads_the_card_at_once()
    {
        _teller.Answer = _ => [new BrainText("Task a is done.")];
        var (vm, asks) = await NextQuestionVmAsync(new ChatNews(_bus, _yard, _time, _ => "Done."));
        _ = await AsksFruitAsync(vm, asks, "d");
        await GraceAsync(vm);
        _teller.Gate = new TaskCompletionSource(); // the teller is still at it when the user asks, and stays so
        Changes("a", SessionState.Working, SessionState.Idle);
        _time.Advance(vm.Traffic.WaitBeforeTelling);
        await Until(() => _teller.Asked.Count > 0);
        var before = _speech.Spoken.Count;

        vm.GoToNextQuestionByKey();

        await Until(() => SpokenSince(before).EndsWith("Banana.", StringComparison.Ordinal));
        SpokenSince(before).ShouldBe("RawCutX, chat \"Task d\" asks: Which fruit? Apple or Banana.");
    }

    // #252: a card taken by a telling that the floor was taken from before it was read is read later, not lost
    [Fact]
    public async Task A_long_command_s_card_cut_off_while_its_words_are_found_is_read_after_the_answer()
    {
        const string command = "$out = Join-Path $PSScriptRoot 'dist'\nRemove-Item $out -Recurse -Force\ndotnet publish -c Release -o $out";
        _teller.Answer = _ => [new BrainText("ContentAutomatorX, chat \"Task a\" wants to run a script that builds the installer")];
        _brain.Answer = _ => [new BrainText("Noon.")];
        var (vm, asks) = await NextQuestionVmAsync();
        _teller.Gate = new TaskCompletionSource(); // its words are being found when the user asks
        var ask = new ChatAsk("p_a",
            new HookEvent { SessionId = "a", EventName = "PermissionRequest", At = _time.GetUtcNow(), ToolName = "PowerShell", ToolInputHash = command },
            [], new ChatPermission("PowerShell", "run a command", command, null, Risks: [PermissionRisk.DeletesFiles]));
        _ = asks.HoldAsync(ask, CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Permission));
        await Until(() => vm.State == RavenState.Idle);
        _time.Advance(vm.Traffic.WaitBeforeTelling);
        await Until(() => _teller.Asked.Count > 0);

        Type(vm, "what time is it"); // takes the floor
        _teller.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
        await GraceAsync(vm);

        await Until(() => string.Join(" ", _speech.Spoken).EndsWith("It's on the card.", StringComparison.Ordinal));
        string.Join(" ", _speech.Spoken).ShouldContain("Noon.");
    }

    // #252 (item 6): muted in Open mic, a written offer takes no bare yes, but "Raven, yes" reads the card aloud (#242)
    [Fact]
    public async Task Muted_in_Open_mic_a_yes_with_the_name_to_a_written_offer_reads_the_card_aloud()
    {
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DictationResult("Raven, yes.", TimeSpan.FromSeconds(1))));
        var (vm, asks) = await NextQuestionVmAsync(openMic: true);
        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);
        vm.IsMuted = true;
        await OfferedAfterAnAnswerAsync(vm, asks, quiet: true);
        var before = _speech.Spoken.Count;

        _openMic.Speak();
        _openMic.EndTurn();
        await WithinAsync(vm.PendingTranscriptions);

        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 3));
        _time.Advance(TrafficWatcher.NewsGrace); // said in Open mic: the user may go on after a breath
        await Until(() => SpokenSince(before).EndsWith("Banana.", StringComparison.Ordinal));
    }

    // #254: muted, next_question from a brain's own turn read the card when another brain answered words said aloud
    [Fact]
    public async Task Muted_the_card_next_question_finds_is_read_out_only_for_the_brain_answering_words_said_aloud()
    {
        var brains = new FakeChatBrains(_brain);
        var (vm, asks) = await NextQuestionVmAsync(brains: brains);
        _ = await AsksFruitAsync(vm, asks, "d"); // in window 3
        vm.IsMuted = true;
        var one = (FakeBrain)brains.For(FakeYardDirectory.WorkspaceOf("ContentAutomatorX"));
        one.Gate = new TaskCompletionSource(); // chat 1's brain answers words said aloud, and is still at it
        Transcribes(Task.FromResult(new DictationResult("What's waiting?", TimeSpan.FromSeconds(1))));
        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);
        Speak();
        _time.Advance(Hold);
        await WithinAsync(vm.ReleaseMicAsync(TalkInput.MicButton));
        await WithinAsync(vm.PendingTranscriptions);
        await Until(() => one.Sent.Count == 1);

        vm.NextQuestionForBrain(FakeYardDirectory.WorkspaceOf("RawCutX").ToString())
            .ShouldBe("Chat 3, RawCutX. Its card is shown there.", "chat 3's brain began its turn itself");
        vm.NextQuestionForBrain(null).ShouldBe("Chat 3, RawCutX. Its card is shown there.", "a caller that is no Raven chat");
        vm.NextQuestionForBrain(Guid.NewGuid().ToString()).ShouldBe("Chat 3, RawCutX. Its card is shown there.", "a window gone, its brain with it");
        vm.NextQuestionForBrain(FakeYardDirectory.WorkspaceOf("ContentAutomatorX").ToString())
            .ShouldBe("Chat 3, RawCutX. Its question is read out next.", "chat 1's brain answers what the user said");
        one.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
    }

    // Round 2 of #254: a window removed while its brain answers spoken words does not make that chat 0's turn
    [Fact]
    public async Task Muted_a_window_s_spoken_question_does_not_count_for_chat_0_once_the_window_is_gone()
    {
        var brains = new FakeChatBrains(_brain);
        var (vm, asks) = await NextQuestionVmAsync(brains: brains);
        _ = await AsksFruitAsync(vm, asks, "c"); // in window 2
        vm.IsMuted = true;
        vm.SelectedChat = ChatNumbered(vm, 3);
        var three = (FakeBrain)brains.For(FakeYardDirectory.WorkspaceOf("RawCutX"));
        three.Gate = new TaskCompletionSource(); // chat 3's brain answers words said aloud, and is still at it
        Transcribes(Task.FromResult(new DictationResult("What's waiting?", TimeSpan.FromSeconds(1))));
        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);
        Speak();
        _time.Advance(Hold);
        await WithinAsync(vm.ReleaseMicAsync(TalkInput.MicButton));
        await WithinAsync(vm.PendingTranscriptions);
        await Until(() => three.Sent.Count == 1);

        vm.SetWorkspaces([.. new[] { "ContentAutomatorX", "DiffusionNexus" }.Select((w, i) => (FakeYardDirectory.WorkspaceOf(w), i + 1, w))]);

        vm.NextQuestionForBrain(YardMcp.OverviewChat).ShouldBe("Chat 2, DiffusionNexus. Its card is shown there.", "chat 0 began its turn itself");
        three.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
    }

    /// <summary>The user asks in chat 1 with a press of the mic, while muted; its brain is held at the answer.</summary>
    private async Task AskAloudInChatOneHeldAsync(RavenPanelViewModel vm, FakeBrain brain)
    {
        vm.IsMuted = true;
        vm.SelectedChat = ChatNumbered(vm, 1);
        brain.Gate = new TaskCompletionSource();
        Transcribes(Task.FromResult(new DictationResult("What's waiting?", TimeSpan.FromSeconds(1))));
        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);
        Speak();
        _time.Advance(Hold);
        await WithinAsync(vm.ReleaseMicAsync(TalkInput.MicButton));
        await WithinAsync(vm.PendingTranscriptions);
        await Until(() => brain.Sent.Count == 1);
    }

    // Round 3 of #254: with one brain for every chat, the chat that asks still decides, by its key
    [Fact]
    public async Task With_one_brain_muted_the_card_is_read_out_only_for_the_chat_whose_words_were_said_aloud()
    {
        var (vm, asks) = await NextQuestionVmAsync();
        _ = await AsksFruitAsync(vm, asks, "d"); // in window 3
        await AskAloudInChatOneHeldAsync(vm, _brain);

        vm.NextQuestionForBrain(FakeYardDirectory.WorkspaceOf("RawCutX").ToString()).ShouldBe("Chat 3, RawCutX. Its card is shown there.");
        vm.NextQuestionForBrain(FakeYardDirectory.WorkspaceOf("ContentAutomatorX").ToString()).ShouldBe("Chat 3, RawCutX. Its question is read out next.");
        _brain.Gate!.SetResult();
        await WithinAsync(vm.PendingAnswers);
    }

    // Round 3 of #254: chat 0 proposes nothing, so an allow with no window comes from a caller that is no Raven chat
    [Fact]
    public async Task Muted_an_allow_proposed_with_no_window_is_only_written_while_a_chat_answers_words_said_aloud()
    {
        var (vm, asks) = await NextQuestionVmAsync();
        var ask = new ChatAsk("p_a",
            new HookEvent { SessionId = "a", EventName = "PermissionRequest", At = _time.GetUtcNow(), ToolName = "Bash", ToolInputHash = "npm test" },
            [], new ChatPermission("Bash", "run a command", "npm test", null));
        _ = asks.HoldAsync(ask, CancellationToken.None);
        await Until(() => vm.Log.Any(e => e.Kind == RavenLogKind.Permission));
        await AskAloudInChatOneHeldAsync(vm, _brain);
        var before = _speech.Spoken.Count;

        asks.Propose("p_a"); // no chat header: no window

        await WithinAsync(_voice.WhenQuietAsync());
        SpokenSince(before).ShouldNotContain("Say yes.");
        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.Raven && e.Text.EndsWith("Say yes.", StringComparison.Ordinal));
        _brain.Gate!.SetResult();
        await WithinAsync(vm.PendingAnswers);
    }
}
