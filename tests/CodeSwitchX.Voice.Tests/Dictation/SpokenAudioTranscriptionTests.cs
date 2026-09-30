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
