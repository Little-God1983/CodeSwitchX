using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Listening;
using CodeSwitchX.Voice.Speech;
using CodeSwitchX.Voice.Speech.QwenTts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Voice;

public static class VoiceServiceCollectionExtensions
{
    /// <param name="voiceDirectory">Where the text-to-speech engine is installed.</param>
    public static IServiceCollection AddCodeSwitchXVoice(this IServiceCollection services, string modelsDirectory, string voiceDirectory)
    {
        services.Configure<DictationOptions>(o =>
        {
            o.ModelFolder = modelsDirectory;
            o.Model = WhisperModel.LargeV3Turbo;
            o.Language = "auto";
        });
        services.AddSingleton<IWhisperModelStore, WhisperModelStore>();
        services.AddSingleton<IDictationService, WhisperDictationService>();
        services.AddSingleton<IMicrophoneCatalog, WasapiMicrophoneCatalog>();
        services.AddSingleton<IMicrophoneRecorder, WasapiMicrophoneRecorder>();

        // Open mic: listens until stopped; its two models are fetched the first time the user switches to it.
        services.AddSingleton(_ => new ListeningModelStore(Path.Combine(modelsDirectory, "listening"), new HttpClient()));
        services.AddSingleton<IMicrophoneStream, WasapiMicrophoneStream>();
        services.AddSingleton<IOpenMic, OpenMicListener>();

        // Speech: Qwen3-TTS in a sidecar, installed on first need. The requests stream for as long as a sentence takes,
        // so the client has no timeout of its own; ReplyVoice watches for a stalled one.
        services.AddSingleton<SpeechSettings>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IQwenTtsEnvironment>(sp => new QwenTtsEnvironment(voiceDirectory, Path.Combine(modelsDirectory, "huggingface"),
            sp.GetRequiredService<IProcessRunner>(), new HttpClient(), QwenTtsEnvironment.FindUvOnPath,
            sp.GetRequiredService<ILogger<QwenTtsEnvironment>>()));
        services.AddSingleton<IQwenTtsServerLauncher, QwenTtsServerLauncher>();
        services.AddSingleton<ITextToSpeech>(sp => new QwenTextToSpeech(sp.GetRequiredService<IQwenTtsEnvironment>(),
            sp.GetRequiredService<IQwenTtsServerLauncher>(), sp.GetRequiredService<SpeechSettings>(),
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<QwenTextToSpeech>>()));
        services.AddSingleton<ISpeechPlayer, WaveOutSpeechPlayer>();
        services.AddSingleton<IAudioKeepAlive, AudioKeepAlive>();
        services.AddSingleton<ReplyVoice>();
        return services;
    }
}
