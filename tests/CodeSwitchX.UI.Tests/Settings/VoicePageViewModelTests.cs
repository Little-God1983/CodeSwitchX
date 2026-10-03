using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Voice;
using CodeSwitchX.Voice;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Speech;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Settings;

public sealed class VoicePageViewModelTests
{
    private readonly ShellTestHarness _shell = new();

    private async Task<VoicePageViewModel> OpenAsync()
    {
        _shell.Dictation.Status.Returns(new DictationStatus(DictationState.Asleep, WhisperModel.LargeV3Turbo));
        await _shell.Shell.Settings.LoadAsync(CancellationToken.None);
        _shell.VoiceStatus.Start();
        return _shell.Shell.Settings.VoicePage;
    }

    private static EngineCard Card(VoicePageViewModel vm, SpeechEngine? engine) => vm.Cards.Single(c => c.Engine == engine);

    [Fact]
    public async Task With_no_engine_picked_the_None_card_is_picked_and_no_voices_are_listed()
    {
        var vm = await OpenAsync();

        vm.SelectedCard.Engine.ShouldBeNull();
        vm.SelectedCard.IsSelected.ShouldBeTrue();
        (vm.HasVoices, vm.Voices.Count).ShouldBe((false, 0));
        vm.Cards.Select(c => c.Name).ShouldBe(["None", "Kokoro", "Qwen3-TTS"]);
        vm.Cards.Single(c => c.Recommended).Engine.ShouldBe(SpeechEngine.Kokoro);
    }

    [Fact]
    public async Task Each_card_shows_where_its_engine_stands()
    {
        _shell.Qwen.IsInstalled = true;
        var vm = await OpenAsync();

        (Card(vm, SpeechEngine.Kokoro).StatusText, Card(vm, SpeechEngine.Kokoro).Lamp!.Dot).ShouldBe(("Not installed", ModelDot.Red));
        (Card(vm, SpeechEngine.Qwen).StatusText, Card(vm, SpeechEngine.Qwen).Lamp!.Dot).ShouldBe(("Asleep · installed", ModelDot.Grey));
        Card(vm, null).Lamp.ShouldBeNull("None has no model");

        _shell.Qwen.Report(new TextToSpeechStatus(TextToSpeechState.Ready));
        (Card(vm, SpeechEngine.Qwen).StatusText, Card(vm, SpeechEngine.Qwen).Lamp!.Dot).ShouldBe(("Ready", ModelDot.Green));
    }

    [Fact]
    public async Task Picking_a_card_picks_its_engine_and_lists_its_voices()
    {
        var vm = await OpenAsync();

        vm.SelectedCard = Card(vm, SpeechEngine.Kokoro);

        _shell.Speech.Engine.ShouldBe(SpeechEngine.Kokoro);
        _shell.Shell.Settings.RavenVoiceEngine.ShouldBe("Kokoro", "stored, as any setting");
        vm.Voices.Select(v => v.Voice.Name).ShouldBe(["Heart", "Michael", "Emma", "George", "Bella"]);
        (vm.SelectedVoice!.Voice.Id, vm.VoicesHeading).ShouldBe(("af_heart", "Kokoro voices"));
        Card(vm, null).IsSelected.ShouldBeFalse();
        _shell.Kokoro.Installs.ShouldBe(0, "picking installs nothing; the card offers it");
        (Card(vm, SpeechEngine.Kokoro).CanInstall, Card(vm, SpeechEngine.Kokoro).InstallText).ShouldBe((true, "Install Kokoro"));
        Card(vm, SpeechEngine.Qwen).CanInstall.ShouldBeFalse("only the card picked offers its install");
    }

    [Fact]
    public async Task A_card_unpicked_in_the_list_leaves_the_engine_as_it_was()
    {
        var vm = await OpenAsync();
        vm.SelectedCard = Card(vm, SpeechEngine.Kokoro);

        vm.SelectedCard = null!; // Ctrl+click on the card picked: the list writes none

        vm.SelectedCard.ShouldBe(Card(vm, SpeechEngine.Kokoro));
        (_shell.Speech.Engine, vm.HasVoices).ShouldBe((SpeechEngine.Kokoro, true));
    }

    [Fact]
    public async Task Picking_a_voice_stores_it_for_that_engine()
    {
        var vm = await OpenAsync();
        vm.SelectedCard = Card(vm, SpeechEngine.Kokoro);

        vm.SelectedVoice = vm.Voices.Single(v => v.Voice.Id == "bm_george");

        _shell.Speech.KokoroVoice.ShouldBe("bm_george");
        _shell.Shell.Settings.RavenKokoroVoice.ShouldBe("bm_george");
        vm.Voices.Single(v => v.IsSelected).Voice.Id.ShouldBe("bm_george");
    }

    [Fact]
    public async Task Each_engine_keeps_its_own_voice()
    {
        _shell.Qwen.IsInstalled = true;
        var vm = await OpenAsync();
        vm.SelectedVoice = vm.Voices.Single(v => v.Voice.Id == "serena");

        vm.SelectedCard = Card(vm, SpeechEngine.Kokoro);
        vm.SelectedCard = Card(vm, SpeechEngine.Qwen);

        vm.SelectedVoice!.Voice.Id.ShouldBe("serena");
        _shell.Speech.KokoroVoice.ShouldBe(SpeechSettings.DefaultKokoroVoice, "showing Kokoro's voices picks none of them");
    }

    [Fact]
    public async Task Installing_shows_the_download_on_the_card_until_ready()
    {
        var vm = await OpenAsync();
        var kokoro = Card(vm, SpeechEngine.Kokoro);
        vm.SelectedCard = kokoro;

        kokoro.InstallCommand.Execute(null);
        _shell.Kokoro.Installs.ShouldBe(1);

        _shell.Kokoro.Report(new TextToSpeechStatus(TextToSpeechState.Installing, "downloading the model", new ByteProgress(142_000_000, 325_000_000)));
        (kokoro.IsBusy, kokoro.CanInstall, kokoro.StatusText).ShouldBe((true, false, $"Installing · {142:N0} of {325:N0} MB"));
        kokoro.Progress.ShouldNotBeNull().ShouldBe(142.0 / 325, 0.001);
        kokoro.Lamp!.Dot.ShouldBe(ModelDot.Yellow);

        _shell.Kokoro.Report(new TextToSpeechStatus(TextToSpeechState.Loading, "loading the model"));
        (kokoro.IsBusy, kokoro.Progress).ShouldBe((true, (double?)null), "the bar shows busy");

        _shell.Kokoro.Report(new TextToSpeechStatus(TextToSpeechState.Ready));
        (kokoro.IsBusy, kokoro.StatusText).ShouldBe((false, "Ready"));
    }

    [Fact]
    public async Task Install_on_a_card_not_picked_picks_it_too()
    {
        var vm = await OpenAsync();

        Card(vm, SpeechEngine.Kokoro).InstallCommand.Execute(null);

        (vm.SelectedCard.Engine, _shell.Speech.Engine, _shell.Kokoro.Installs).ShouldBe((SpeechEngine.Kokoro, SpeechEngine.Kokoro, 1));
    }

    [Fact]
    public async Task Cancel_gives_the_install_up_and_the_card_offers_it_again()
    {
        var vm = await OpenAsync();
        var kokoro = Card(vm, SpeechEngine.Kokoro);
        kokoro.InstallCommand.Execute(null);
        _shell.Kokoro.Report(new TextToSpeechStatus(TextToSpeechState.Installing, "downloading the model"));

        kokoro.CancelCommand.Execute(null);
        _shell.Kokoro.Report(new TextToSpeechStatus(TextToSpeechState.NotInstalled));

        _shell.Kokoro.Stops.ShouldBeGreaterThan(0);
        (kokoro.IsBusy, kokoro.CanInstall).ShouldBe((false, true));
        _shell.Speech.Engine.ShouldBe(SpeechEngine.Kokoro, "the engine stays picked; Raven installs it when it next speaks");
    }

    [Fact]
    public async Task A_failed_install_says_why_and_can_be_tried_again()
    {
        var vm = await OpenAsync();
        var kokoro = Card(vm, SpeechEngine.Kokoro);
        kokoro.InstallCommand.Execute(null);

        _shell.Kokoro.Report(new TextToSpeechStatus(TextToSpeechState.Failed, "uv pip failed: no network"));
        (kokoro.StatusText, kokoro.InstallText, kokoro.CanInstall).ShouldBe(("Failed: uv pip failed: no network", "Try again", true));

        kokoro.InstallCommand.Execute(null);
        _shell.Kokoro.Installs.ShouldBe(2);
    }

    [Fact]
    public async Task The_Qwen3_TTS_card_holds_the_model_choice()
    {
        var vm = await OpenAsync();

        vm.Cards.Where(c => c.HasModelChoice).Select(c => c.Engine).ShouldBe([SpeechEngine.Qwen]);
    }

    [Fact]
    public async Task An_engine_picked_elsewhere_shows_here()
    {
        var vm = await OpenAsync();

        _shell.Shell.Settings.PickVoice(SpeechEngine.Qwen, "vivian");

        (vm.SelectedCard.Engine, vm.SelectedVoice!.Voice.Id).ShouldBe((SpeechEngine.Qwen, "vivian"));
        Card(vm, SpeechEngine.Qwen).IsSelected.ShouldBeTrue();
    }

    [Fact]
    public async Task A_sample_plays_and_a_second_press_stops_it()
    {
        var vm = await OpenAsync();
        vm.SelectedCard = Card(vm, SpeechEngine.Kokoro);
        var emma = vm.Voices.Single(v => v.Voice.Id == "bf_emma");

        var playing = emma.PlayCommand.ExecuteAsync(null);
        emma.IsPlaying.ShouldBeTrue();
        vm.SelectedVoice.ShouldBe(emma, "the voice heard is the one picked");
        _shell.Samples.Played.ShouldBe([(SpeechEngine.Kokoro, "bf_emma")]);

        await emma.PlayCommand.ExecuteAsync(null);
        await playing;

        emma.IsPlaying.ShouldBeFalse();
        _shell.Samples.Played.Count.ShouldBe(1, "the second press stops it");
    }

    [Fact]
    public async Task Closing_Settings_stops_a_sample_and_the_welcome_line()
    {
        var vm = await OpenAsync();
        _shell.Shell.OpenSettingsAt(SettingsPage.Voice);
        vm.SelectedCard = Card(vm, SpeechEngine.Kokoro);
        vm.ShowWelcome = true;
        var heart = vm.Voices[0];
        var playing = heart.PlayCommand.ExecuteAsync(null);

        _shell.Shell.CloseSettings();
        await playing;

        (heart.IsPlaying, vm.ShowWelcome).ShouldBe((false, false));
    }
}

/// <summary>A sample plays until stopped.</summary>
internal sealed class FakeVoiceSamples : IVoiceSamples
{
    public List<(SpeechEngine, string)> Played { get; } = [];

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
