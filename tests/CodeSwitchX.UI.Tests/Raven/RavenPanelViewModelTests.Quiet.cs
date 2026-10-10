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

        await SayAloudAsync(vm, "What's waiting on me?");
        await Until(() => _speech.Spoken.Contains("You have one chat waiting."));
        vm.IsMuted = true;
        var before = _speech.Spoken.Count;

        await SayAloudAsync(vm, "And now?");
        await Until(() => _speech.Spoken.Count > before);
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
}
