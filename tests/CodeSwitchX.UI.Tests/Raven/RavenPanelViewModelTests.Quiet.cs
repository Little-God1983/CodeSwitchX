using CodeSwitchX.Conductor;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Dictation;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>#242: muted, Raven says nothing on its own, but what the user asks aloud it still answers aloud.</summary>
public sealed partial class RavenPanelViewModelTests
{
    /// <summary>The user says <paramref name="words"/> with a press of the mic, and the turn is transcribed and answered.</summary>
    private async Task SayAloudAsync(RavenPanelViewModel vm, string words)
    {
        Transcribes(Task.FromResult(new DictationResult(words, TimeSpan.FromSeconds(1))));
        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);
        Speak();
        _time.Advance(Hold);
        await WithinAsync(vm.ReleaseMicAsync(TalkInput.MicButton));
        await WithinAsync(vm.PendingTranscriptions);
        await WithinAsync(vm.PendingAnswers);
    }

    [Fact]
    public async Task A_muted_Raven_answers_aloud_what_was_asked_aloud_and_in_writing_what_was_typed()
    {
        _brain.Answer = q => q.Contains("waiting") ? [new BrainText("You have one chat waiting.")] : [new BrainText("It is noon.")];
        var vm = await NewVmAsync();
        vm.IsMuted = true;

        await SayAloudAsync(vm, "What's waiting on me?");
        await Until(() => _speech.Spoken.Contains("You have one chat waiting."));

        Type(vm, "What time is it?");
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _speech.Spoken.ShouldNotContain("It is noon.", "typed while muted: only written");
        Lines(vm).ShouldContain((RavenLogKind.Raven, "It is noon."));
    }

    [Fact]
    public async Task Muting_stops_what_Raven_says_and_a_question_asked_aloud_after_it_is_answered_aloud()
    {
        _brain.Answer = _ => [new BrainText("You have one chat waiting.")];
        var vm = await NewVmAsync();
        _speech.Gate = new TaskCompletionSource(); // the audio waits: the answer is being heard

        await SayAloudAsync(vm, "What's waiting on me?");
        await Until(() => _speech.Spoken.Count > 0);
        vm.IsMuted = true;
        await WithinAsync(_voice.WhenQuietAsync()); // muting stops it
        _speech.Gate.TrySetResult();
        var before = _speech.Spoken.Count;

        await SayAloudAsync(vm, "And now?");
        await Until(() => _speech.Spoken.Count > before);
    }

    // #242: muted, a question typed right behind words said aloud is one question, answered in writing: quiet wins
    [Fact]
    public async Task Muted_words_said_and_typed_as_one_question_are_answered_in_writing()
    {
        _brain.Gate = new TaskCompletionSource(); // the first is still on its way when the typed words come
        _brain.Answer = _ => [new BrainText("One chat waits.")];
        var vm = await NewVmAsync();
        vm.IsMuted = true;
        _brain.BeforeSent = new TaskCompletionSource(); // neither has gone to the brain yet: they go as one

        Transcribes(Task.FromResult(new DictationResult("What's waiting", TimeSpan.FromSeconds(1))));
        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);
        Speak();
        _time.Advance(Hold);
        await WithinAsync(vm.ReleaseMicAsync(TalkInput.MicButton));
        await WithinAsync(vm.PendingTranscriptions);
        Type(vm, "on me?");
        _brain.BeforeSent.SetResult();
        _brain.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
        await WithinAsync(_voice.WhenQuietAsync());

        _speech.Spoken.ShouldBeEmpty();
        Lines(vm).ShouldContain((RavenLogKind.Raven, "One chat waits."));
    }

    // #242: "next question" said aloud reads its card while muted; by its hotkey (a key) the card is only shown
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Muted_next_question_reads_its_card_when_said_aloud_and_only_shows_it_by_its_hotkey(bool aloud)
    {
        var (vm, asks) = await NextQuestionVmAsync();
        vm.IsMuted = true;
        _ = await AsksFruitAsync(vm, asks, "c");
        await SayAloudAsync(vm, "Hello."); // words said aloud before: the key must not count as them
        await GraceAsync(vm);
        var before = _speech.Spoken.Count;

        if (aloud)
        {
            await SayAloudAsync(vm, "Next question.");
        }
        else
        {
            vm.GoToNextQuestionByKey();
        }

        vm.SelectedChat.ShouldBe(ChatNumbered(vm, 2));
        if (aloud)
        {
            await Until(() => SpokenSince(before).EndsWith("Banana.", StringComparison.Ordinal));
        }
        else
        {
            await GraceAsync(vm);
            SpokenSince(before).ShouldBeEmpty("a key, muted: the card is only shown");
        }
    }

    // #242: muted, with the panel collapsed, an answer to words said aloud was heard: it counts as read, not as news
    [Fact]
    public async Task Muted_and_collapsed_an_answer_to_words_said_aloud_is_not_unread()
    {
        _brain.Answer = _ => [new BrainText("One chat waits.")];
        var vm = await NewVmAsync();
        vm.IsMuted = true;
        vm.IsOpen = false;

        await SayAloudAsync(vm, "What's waiting on me?");
        await Until(() => _speech.Spoken.Contains("One chat waits."));
        await WithinAsync(_voice.WhenQuietAsync());
        await WithinAsync(vm.PendingHeardCheck);

        vm.CurrentChat.Unread.ShouldBe(0);
    }

    // Review of #242: muted midway through an answer, what it writes after a tool is only written, and new to the user
    [Fact]
    public async Task Muted_midway_the_rest_of_an_answer_after_a_tool_is_unread()
    {
        _brain.Answer = _ => [new BrainText("Let me look."), new BrainToolCall("t1", "list_chats", "{}"), new BrainToolResult("t1", false),
            new BrainText("There are two chats.")];
        _brain.Pause = new TaskCompletionSource();
        var vm = await NewVmAsync();
        vm.IsOpen = false;
        Type(vm, "How many chats run?");
        await Until(() => vm.Log.Any(e => e.Text.StartsWith("Let me look.", StringComparison.Ordinal)));

        vm.IsMuted = true;
        _brain.Pause.SetResult();
        await WithinAsync(vm.PendingAnswers);

        var rest = vm.Log.Single(e => e.Text.StartsWith("There are two chats.", StringComparison.Ordinal));
        rest.Said.ShouldBeFalse("the voice was muted: it only wrote it");
        rest.IsUnread.ShouldBeTrue();
    }
}
