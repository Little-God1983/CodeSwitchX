namespace CodeSwitchX.Voice.Tests.Dictation;

using System.Speech.Synthesis;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public sealed class SpokenAudioTranscriptionTests
{
    // Needs ggml-large-v3-turbo.bin in %LOCALAPPDATA%\CodeSwitchX\models.
    [Fact(Explicit = true)]
    public async Task Synthesized_english_speech_is_transcribed_by_the_real_model()
    {
        var options = Options.Create(new DictationOptions
        {
            ModelFolder = CodeSwitchX.Core.AppPaths.Default().ModelsDirectory,
            Model = WhisperModel.LargeV3Turbo,
            Language = "auto",
        });
        using var service = new WhisperDictationService(new WhisperModelStore(options), options, NullLogger<WhisperDictationService>.Instance);

        var samples = Speak("Open the Diffusion Nexus workspace and start a new chat.");
        var result = await service.TranscribeAsync(samples, new DictationVocabulary(["Diffusion Nexus"], []), live: false, TestContext.Current.CancellationToken);

        result.Text.ShouldContain("Diffusion Nexus", Case.Insensitive);
        result.Text.ShouldContain("chat", Case.Insensitive);
    }

    // Needs the model too. Measured 2026-09-30 on Vulkan: the warm-up about 3 s, a second call 0 ms, the first clip after
    // it about 0.2 s. The warm-up once decoded one second of silence, which took 6 to 23 s and ran again on every press.
    [Fact(Explicit = true)]
    public async Task The_warm_up_runs_once_and_leaves_the_first_clip_fast()
    {
        var options = Options.Create(new DictationOptions
        {
            ModelFolder = CodeSwitchX.Core.AppPaths.Default().ModelsDirectory,
            Model = WhisperModel.LargeV3Turbo,
            Language = "auto",
        });
        using var service = new WhisperDictationService(new WhisperModelStore(options), options, NullLogger<WhisperDictationService>.Instance);
        var samples = Speak("Open the Diffusion Nexus workspace and start a new chat.");
        var ct = TestContext.Current.CancellationToken;

        await service.WarmUpAsync(ct);
        var again = System.Diagnostics.Stopwatch.StartNew();
        await service.WarmUpAsync(ct);
        again.ElapsedMilliseconds.ShouldBeLessThan(50, "a second warm-up must not run the model again");
        var first = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.TranscribeAsync(samples, new DictationVocabulary(["Diffusion Nexus"], []), live: false, ct);

        first.ElapsedMilliseconds.ShouldBeLessThan(1500);
        result.Text.ShouldContain("chat", Case.Insensitive);
    }

    private static float[] Speak(string text)
    {
        using var stream = new MemoryStream();
        using (var synth = new SpeechSynthesizer())
        {
            synth.SetOutputToAudioStream(stream, new System.Speech.AudioFormat.SpeechAudioFormatInfo(AudioMath.TargetRate, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            synth.Speak(text);
        }

        var bytes = stream.ToArray();
        var floats = new float[bytes.Length / 2];
        for (var i = 0; i < floats.Length; i++)
        {
            floats[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        }

        return floats;
    }
}
