using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Listening;
using CodeSwitchX.Voice.Speech;
using CodeSwitchX.Voice.Speech.Kokoro;
using CodeSwitchX.Voice.Speech.QwenTts;
using CodeSwitchX.Voice.Speech.Sidecar;
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
        services.AddSingleton<ISpeakerCatalog, WasapiSpeakerCatalog>();
        services.AddSingleton<IAudioOutput, AudioOutput>();
        services.AddSingleton<IMicrophoneRecorder, WasapiMicrophoneRecorder>();

        // Open mic: listens until stopped; its two models are fetched the first time the user switches to it.
        services.AddSingleton(_ => new ListeningModelStore(Path.Combine(modelsDirectory, "listening"), new HttpClient()));
        services.AddSingleton<IMicrophoneStream, WasapiMicrophoneStream>();
        services.AddSingleton<IOpenMic, OpenMicListener>();

        // Speech: the engine picked (Kokoro or Qwen3-TTS), each in a sidecar of its own, installed on first need or from the Settings
        // Voice page. The requests stream for as long as a sentence takes, so the client has no timeout of its own;
        // ReplyVoice watches for a stalled one.
        services.AddSingleton<SpeechSettings>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton(sp => new Uv(voiceDirectory, sp.GetRequiredService<IProcessRunner>(), new HttpClient(), Uv.FindOnPath,
            sp.GetRequiredService<ILogger<Uv>>()));
        services.AddSingleton<ISidecarLauncher, SidecarLauncher>();
        services.AddSingleton(sp => new SpeechEngines(sp.GetRequiredService<SpeechSettings>(),
        [
            Engine(sp, SpeechEngine.Kokoro, new KokoroEnvironment(Path.Combine(voiceDirectory, "kokoro"), Path.Combine(modelsDirectory, "kokoro"),
                sp.GetRequiredService<Uv>(), new HttpClient { Timeout = Timeout.InfiniteTimeSpan })),
            Engine(sp, SpeechEngine.Qwen, new QwenTtsEnvironment(voiceDirectory, Path.Combine(modelsDirectory, "huggingface"), sp.GetRequiredService<Uv>())),
        ]));
        services.AddSingleton<ITextToSpeech>(sp => sp.GetRequiredService<SpeechEngines>());
        services.AddSingleton<IVoiceSamples, VoiceSamples>();
        services.AddSingleton<ISpeechPlayer, WaveOutSpeechPlayer>();
        services.AddSingleton<IAudioKeepAlive, AudioKeepAlive>();
        services.AddSingleton<IChatChime, ChatChime>();
        services.AddSingleton<ReplyVoice>();
        return services;
    }

    /// <remarks>Disposed with <see cref="SpeechEngines"/>, which the container disposes: it stops its sidecar.</remarks>
    private static SidecarTextToSpeech Engine(IServiceProvider sp, SpeechEngine engine, ISidecarEnvironment environment) =>
        new(engine, environment, sp.GetRequiredService<ISidecarLauncher>(), sp.GetRequiredService<SpeechSettings>(),
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<SidecarTextToSpeech>>());
}
