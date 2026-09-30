using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSwitchX.Voice;

public static class VoiceServiceCollectionExtensions
{
    public static IServiceCollection AddCodeSwitchXVoice(this IServiceCollection services, string modelsDirectory)
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
        return services;
    }
}
