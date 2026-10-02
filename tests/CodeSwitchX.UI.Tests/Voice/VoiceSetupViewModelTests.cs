using CodeSwitchX.UI.Voice;
using CodeSwitchX.Voice;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Speech;
using NSubstitute;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.UI.Tests.Voice;

public sealed class VoiceSetupViewModelTests : IDisposable
{
    private readonly ShellTestHarness _shell = new();
    private readonly FakeSamples _samples = new();
    private VoiceSetupViewModel? _vm;

    public void Dispose() => _vm?.Dispose();

    private async Task<VoiceSetupViewModel> OpenAsync()
    {
        _shell.Dictation.Status.Returns(new DictationStatus(DictationState.Asleep, WhisperModel.LargeV3Turbo));
        await _shell.Shell.Settings.LoadAsync(CancellationToken.None);
        _shell.VoiceStatus.Start();
        _vm = new VoiceSetupViewModel(_shell.Shell.Settings, _shell.Engines, _shell.VoiceStatus, _samples, new ImmediateDispatcher(),
            NullLogger<VoiceSetupViewModel>.Instance);
        return _vm;
    }

    [Fact]
    public async Task It_opens_on_Kokoro_with_its_voices_and_offers_to_install_it()
    {
        var vm = await OpenAsync();

        vm.SelectedCard.Engine.ShouldBe(SpeechEngine.Kokoro);
        vm.SelectedCard.IsSelected.ShouldBeTrue();
        vm.Cards.Single(c => c.Recommended).Engine.ShouldBe(SpeechEngine.Kokoro);
        vm.Voices.Select(v => v.Voice.Name).ShouldBe(["Heart", "Michael", "Emma", "George", "Bella"]);
        vm.SelectedVoice!.Voice.Id.ShouldBe("af_heart");
        (vm.VoiceHeading, vm.InstallText).ShouldBe(("VOICE · KOKORO", "Install Kokoro"));
        vm.SelectedCard.Lamp.Dot.ShouldBe(ModelDot.Red);
    }

    [Fact]
    public async Task Another_engine_lists_its_own_voices()
    {
        _shell.Qwen.IsInstalled = true;
        var vm = await OpenAsync();

        vm.SelectedCard = vm.Cards.Single(c => c.Engine == SpeechEngine.Qwen);

        vm.Voices.First().Voice.Name.ShouldBe("Ryan");
        vm.InstallText.ShouldBe("Use Qwen3-TTS", "it is on this PC already");
        vm.Cards.Single(c => c.Engine == SpeechEngine.Kokoro).IsSelected.ShouldBeFalse();
    }

    [Fact]
    public async Task Installing_picks_the_engine_and_voice_and_shows_the_download_until_ready()
    {
        var vm = await OpenAsync();
        vm.SelectedVoice = vm.Voices.Single(v => v.Voice.Id == "bm_george");

        vm.InstallCommand.Execute(null);

        _shell.Speech.Engine.ShouldBe(SpeechEngine.Kokoro);
        _shell.Speech.KokoroVoice.ShouldBe("bm_george");
        _shell.Shell.Settings.RavenVoiceEngine.ShouldBe("Kokoro", "stored, as a pick in Settings is");
        _shell.Kokoro.Installs.ShouldBe(1);
        vm.Step.ShouldBe(VoiceSetupStep.Installing);

        _shell.Kokoro.Report(new TextToSpeechStatus(TextToSpeechState.Installing, "downloading the model", new ByteProgress(142_000_000, 330_000_000)));
        (vm.FooterText, vm.Amount).ShouldBe(("Installing Kokoro: downloading the model", $"{142:N0} of {330:N0} MB"));
        vm.Progress.ShouldNotBeNull().ShouldBe(142.0 / 330, 0.001);

        _shell.Kokoro.Report(new TextToSpeechStatus(TextToSpeechState.Loading, "loading the model"));
        vm.Progress.ShouldBeNull("the bar shows busy");

        _shell.Kokoro.Report(new TextToSpeechStatus(TextToSpeechState.Ready));
        vm.Step.ShouldBe(VoiceSetupStep.Ready);
        vm.FooterText.ShouldBe("Kokoro is ready. Raven speaks with George from now on.");
    }

    [Fact]
    public async Task Cancel_gives_the_install_up_and_puts_back_the_engine_picked_before()
    {
        _shell.Qwen.IsInstalled = true; // an upgrade: Qwen3-TTS was the engine
        var vm = await OpenAsync();
        _shell.Speech.Engine.ShouldBe(SpeechEngine.Qwen);
        vm.SelectedCard = vm.Cards.Single(c => c.Engine == SpeechEngine.Kokoro);
        vm.InstallCommand.Execute(null);

        vm.CancelCommand.Execute(null);

        _shell.Kokoro.Stops.ShouldBeGreaterThan(0);
        _shell.Speech.Engine.ShouldBe(SpeechEngine.Qwen);
        vm.Step.ShouldBe(VoiceSetupStep.Choosing);
    }

    [Fact]
    public async Task A_failed_install_says_why_and_can_be_tried_again()
    {
        var vm = await OpenAsync();
        vm.InstallCommand.Execute(null);

        _shell.Kokoro.Report(new TextToSpeechStatus(TextToSpeechState.Failed, "uv pip failed: no network"));
        (vm.Step, vm.FooterText).ShouldBe((VoiceSetupStep.Failed, "Kokoro could not get ready: uv pip failed: no network"));

        vm.InstallCommand.Execute(null);
        _shell.Kokoro.Installs.ShouldBe(2);
    }

    [Fact]
    public async Task An_engine_ready_already_is_used_at_once()
    {
        var vm = await OpenAsync();
        _shell.Kokoro.Report(new TextToSpeechStatus(TextToSpeechState.Ready));

        vm.InstallCommand.Execute(null);

        vm.Step.ShouldBe(VoiceSetupStep.Ready, "nothing more comes from an engine ready already");
    }

    [Fact]
    public async Task A_sample_plays_and_a_second_press_stops_it()
    {
        var vm = await OpenAsync();
        var emma = vm.Voices.Single(v => v.Voice.Id == "bf_emma");

        var playing = emma.PlayCommand.ExecuteAsync(null);
        emma.IsPlaying.ShouldBeTrue();
        vm.SelectedVoice.ShouldBe(emma, "the voice heard is the one picked");
        _samples.Played.ShouldBe([(SpeechEngine.Kokoro, "bf_emma")]);

        await emma.PlayCommand.ExecuteAsync(null);
        await playing;

        emma.IsPlaying.ShouldBeFalse();
        _samples.Played.Count.ShouldBe(1, "the second press stops it");
    }

    [Fact]
    public async Task Opened_with_an_engine_in_use_it_says_so_and_closes_rather_than_skips()
    {
        _shell.Qwen.IsInstalled = true;
        var vm = await OpenAsync();

        (vm.SelectedCard.Engine, vm.CloseText).ShouldBe((SpeechEngine.Qwen, "Close"));
        vm.FooterText.ShouldBe("Raven speaks with Qwen3-TTS now. Pick another engine or voice, or close.");
    }

    [Fact]
    public async Task Skipping_leaves_Raven_answering_in_text()
    {
        var vm = await OpenAsync();
        var closed = false;
        vm.CloseRequested += () => closed = true;

        vm.CloseCommand.Execute(null);

        closed.ShouldBeTrue();
        vm.CloseText.ShouldBe("Skip for now");
        _shell.Speech.Engine.ShouldBeNull();
        _shell.Kokoro.Installs.ShouldBe(0, "nothing is installed in the background");
    }

    /// <summary>A sample plays until stopped.</summary>
    private sealed class FakeSamples : IVoiceSamples
    {
        public List<(SpeechEngine, string)> Played { get; } = [];

        public bool Has(SpeechEngine engine, string voice) => true;

        public async Task PlayAsync(SpeechEngine engine, string voice, CancellationToken ct)
        {
            Played.Add((engine, voice));
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
