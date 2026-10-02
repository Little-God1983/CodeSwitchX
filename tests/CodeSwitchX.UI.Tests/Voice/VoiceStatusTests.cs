using CodeSwitchX.UI.Voice;
using CodeSwitchX.Voice;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Speech;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Voice;

public sealed class VoiceStatusTests
{
    private readonly SpeechSettings _settings = new();
    private readonly FakeEngineVoice _kokoro = new(SpeechEngine.Kokoro);
    private readonly FakeEngineVoice _qwen = new(SpeechEngine.Qwen);
    private readonly IDictationService _dictation = Substitute.For<IDictationService>();
    private readonly VoiceStatusViewModel _vm;

    public VoiceStatusTests()
    {
        _dictation.Status.Returns(new DictationStatus(DictationState.Asleep, WhisperModel.LargeV3Turbo));
        _vm = new VoiceStatusViewModel(new SpeechEngines(_settings, [_kokoro, _qwen]), _settings, _dictation, new ImmediateDispatcher());
    }

    [Theory]
    [InlineData(TextToSpeechState.NotInstalled, ModelDot.Red, "not downloaded")]
    [InlineData(TextToSpeechState.Off, ModelDot.Grey, "asleep")]
    [InlineData(TextToSpeechState.Installing, ModelDot.Yellow, "installing")]
    [InlineData(TextToSpeechState.Loading, ModelDot.Yellow, "loading")]
    [InlineData(TextToSpeechState.Ready, ModelDot.Green, "ready")]
    [InlineData(TextToSpeechState.Failed, ModelDot.Red, "failed")]
    [InlineData(TextToSpeechState.NoEngine, ModelDot.Grey, "text only")]
    public void A_voice_shows_red_until_downloaded_grey_asleep_yellow_on_its_way_and_green_ready(TextToSpeechState state, ModelDot dot, string text)
    {
        var lamp = ModelLamp.Of(new TextToSpeechStatus(state, "a detail"), SpeechEngine.Kokoro);

        (lamp.Dot, lamp.Text).ShouldBe((dot, text));
    }

    [Theory]
    [InlineData(DictationState.NotDownloaded, ModelDot.Red, "not downloaded")]
    [InlineData(DictationState.Asleep, ModelDot.Grey, "asleep")]
    [InlineData(DictationState.Loading, ModelDot.Yellow, "loading")]
    [InlineData(DictationState.Ready, ModelDot.Green, "ready")]
    [InlineData(DictationState.Failed, ModelDot.Red, "failed")]
    public void Speech_to_text_shows_the_same_colours(DictationState state, ModelDot dot, string text)
    {
        var lamp = ModelLamp.Of(new DictationStatus(state, WhisperModel.BaseEnglish));

        (lamp.Dot, lamp.Text).ShouldBe((dot, text));
    }

    [Fact]
    public void A_download_says_how_far_it_is()
    {
        var lamp = ModelLamp.Of(new TextToSpeechStatus(TextToSpeechState.Installing, "downloading the model", new ByteProgress(142_000_000, 330_000_000)),
            SpeechEngine.Kokoro);

        lamp.Progress.ShouldNotBeNull().ShouldBe(142.0 / 330, 0.001);
        lamp.Detail.ShouldBe($"Installing Kokoro: downloading the model ({142:N0} of {330:N0} MB)");
        ModelLamp.Of(new DictationStatus(DictationState.Downloading, WhisperModel.LargeV3Turbo, Bytes: new ByteProgress(812_000_000, 1_624_000_000)))
            .Text.ShouldBe($"downloading {0.5:P0}");
    }

    [Fact]
    public void The_bar_shows_the_engine_picked_and_follows_a_new_pick()
    {
        _vm.SpeechName.ShouldBe("voice");
        _vm.Speech.Text.ShouldBe("text only");

        _qwen.Report(new TextToSpeechStatus(TextToSpeechState.Ready));
        _kokoro.Report(new TextToSpeechStatus(TextToSpeechState.NotInstalled));
        _settings.Engine = SpeechEngine.Qwen;

        (_vm.SpeechName, _vm.Speech.Dot).ShouldBe(("voice · Qwen3-TTS", ModelDot.Green));
        _vm.Kokoro.Dot.ShouldBe(ModelDot.Red, "every engine's dot, for the voice setup");

        _settings.Engine = SpeechEngine.Kokoro;
        (_vm.SpeechName, _vm.Speech.Dot).ShouldBe(("voice · Kokoro", ModelDot.Red));
    }

    [Fact]
    public async Task Speech_to_text_is_read_at_the_start_and_follows_the_service()
    {
        _vm.Start();
        await Until(() => _vm.Listening.Dot == ModelDot.Grey && _vm.ListeningState == DictationState.Asleep);
        _vm.ListeningName.ShouldBe("speech to text · Whisper Large v3 Turbo");

        _dictation.StatusChanged += Raise.Event<EventHandler<DictationStatus>>(_dictation, new DictationStatus(DictationState.Ready, WhisperModel.TinyEnglish));

        (_vm.Listening.Dot, _vm.ListeningName).ShouldBe((ModelDot.Green, "speech to text · Whisper Tiny"));
    }

    [Fact]
    public void Starting_has_each_engine_say_whether_it_is_on_disk()
    {
        _qwen.IsInstalled = true;

        _vm.Start();

        (_vm.Kokoro.Dot, _vm.Qwen.Dot).ShouldBe((ModelDot.Red, ModelDot.Grey));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline);
            await Task.Delay(5);
        }
    }
}
