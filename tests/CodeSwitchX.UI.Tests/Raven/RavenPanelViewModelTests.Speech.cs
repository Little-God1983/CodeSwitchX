using System.ComponentModel;
using CodeSwitchX.Conductor;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Speech;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>Raven says its answers: as they stream in, quiet when muted, and never on after the user talks again.</summary>
public sealed partial class RavenPanelViewModelTests
{
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the condition never came true");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task An_answer_is_spoken_and_the_panel_shows_it_speaking_until_it_has_played_out()
    {
        _brain.Answer = _ => [new BrainText("You have"), new BrainText(" one chat waiting. It is the API one.")];
        var vm = await NewVmAsync();
        var states = new List<RavenState>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RavenPanelViewModel.State))
            {
                lock (states)
                {
                    states.Add(vm.State);
                }
            }
        };

        Type(vm, "What's waiting on me?");
        await WithinAsync(vm.PendingAnswers);
        await Until(() => vm.State == RavenState.Idle && _speech.Spoken.Count == 2);

        _speech.Spoken.ShouldBe(["You have one chat waiting.", "It is the API one."]);
        lock (states)
        {
            states.ShouldContain(RavenState.Speaking);
        }
    }

    [Fact]
    public async Task The_words_before_a_card_are_a_sentence_of_their_own()
    {
        _brain.Answer = _ => [new BrainText("Let me look"), ListChats(), new BrainToolResult("t1", false), new BrainText("One chat.")];

        await AskedAsync("What's waiting on me?");

        await Until(() => _speech.Spoken.Count == 2);
        _speech.Spoken.ShouldBe(["Let me look", "One chat."]);
    }

    [Fact]
    public async Task Talking_while_Raven_speaks_stops_it_and_the_rest_of_the_answer_is_not_said()
    {
        _speech.Gate = new TaskCompletionSource();
        _brain.Answer = _ => [new BrainText("First this. Then that.")];
        var vm = await AskedAsync("What's up?");
        await Until(() => _speech.Spoken.Count == 1);

        vm.PressMic(TalkInput.MicButton);
        _speech.Gate.TrySetResult();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _speech.Spoken.ShouldBe(["First this."]);
        vm.State.ShouldBe(RavenState.Listening);
    }

    [Fact]
    public async Task A_muted_Raven_only_writes_its_answer()
    {
        _brain.Answer = _ => [new BrainText("You have one chat waiting.")];
        var vm = await NewVmAsync();
        vm.IsMuted = true;

        Type(vm, "What's waiting on me?");
        await WithinAsync(vm.PendingAnswers);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _speech.Spoken.ShouldBeEmpty();
        Lines(vm).ShouldContain((RavenLogKind.Raven, "You have one chat waiting."));
    }

    [Fact]
    public async Task The_mute_button_toggles_and_unmuting_gets_the_voice_ready()
    {
        var vm = await NewVmAsync();
        vm.ToggleMuteCommand.Execute(null);
        vm.IsMuted.ShouldBeTrue();
        var prepares = _speech.Prepares;

        vm.ToggleMuteCommand.Execute(null);

        vm.IsMuted.ShouldBeFalse();
        _speech.Prepares.ShouldBe(prepares + 1);
    }

    [Fact]
    public async Task The_install_of_the_voice_is_told_in_one_note_that_follows_it()
    {
        var vm = await NewVmAsync();

        _speech.Report(new TextToSpeechStatus(TextToSpeechState.Installing, "downloading PyTorch (2.5 GB)"));
        Lines(vm).ShouldBe([(RavenLogKind.Note, "Installing Raven's voice (about 5 GB, a few minutes): downloading PyTorch (2.5 GB)…")]);

        _speech.Report(new TextToSpeechStatus(TextToSpeechState.Loading, "downloading the model"));
        Lines(vm).ShouldBe([(RavenLogKind.Note, "Loading Raven's voice: downloading the model…")]);

        _speech.Report(new TextToSpeechStatus(TextToSpeechState.Ready));
        Lines(vm).ShouldBe([(RavenLogKind.Note, "Raven's voice is ready.")]);
    }

    [Fact]
    public async Task Loading_a_voice_installed_before_is_quiet_and_a_failure_is_a_warning()
    {
        var vm = await NewVmAsync();

        _speech.Report(new TextToSpeechStatus(TextToSpeechState.Loading, "loading the model"));
        _speech.Report(new TextToSpeechStatus(TextToSpeechState.Ready));
        Lines(vm).ShouldBeEmpty();

        _speech.Report(new TextToSpeechStatus(TextToSpeechState.Failed, "CUDA is not available"));
        Lines(vm).ShouldBe([(RavenLogKind.Warning, "Raven cannot speak: CUDA is not available")]);
    }

    [Fact]
    public async Task The_startup_warm_up_gets_the_voice_ready_unless_muted()
    {
        var vm = await NewVmAsync();
        vm.ScheduleWarmUp();
        _time.Advance(RavenPanelViewModel.StartupWarmUpDelay);
        _speech.Prepares.ShouldBe(1);

        var muted = await NewVmAsync();
        muted.IsMuted = true;
        muted.ScheduleWarmUp();
        _time.Advance(RavenPanelViewModel.StartupWarmUpDelay);
        _speech.Prepares.ShouldBe(1);
    }
}
