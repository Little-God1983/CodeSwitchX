using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Speech;
using LittleGod.Speech.Kokoro;
using LittleGod.Speech.Listening;
using LittleGod.Speech.QwenTts;
using LittleGod.Speech.Sidecar;
using LittleGod.Speech.Speaking;
using LittleGod.Speech.Whisper;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSwitchX.Voice;

public static class VoiceServiceCollectionExtensions
{
    /// <summary>
    /// Raven's voice on the shared LittleGod.Speech packages (#328), in this app's folders, so nothing is downloaded again:
    /// Whisper and open mic's models under <paramref name="modelsDirectory"/>, uv, Python and the engines' environments
    /// under <paramref name="voiceDirectory"/>. What is the app's own: the picks (<see cref="SpeechSettings"/>) and the
    /// chats' sounds.
    /// </summary>
    /// <param name="voiceDirectory">Where the text-to-speech engines are installed.</param>
    public static IServiceCollection AddCodeSwitchXVoice(this IServiceCollection services, string modelsDirectory, string voiceDirectory)
    {
        services.AddWhisper(o =>
        {
            o.ModelFolder = modelsDirectory;
            o.Model = WhisperModel.LargeV3Turbo;
            o.Language = "auto";
        });

        // The microphones, push to talk, and open mic, whose two models are fetched the first time the user switches to it.
        services.AddListening(o => o.ModelFolder = Path.Combine(modelsDirectory, "listening"));

        // The engines, each in a sidecar of its own, installed on first need or from the Settings Voice page; the switch
        // speaks with the one SpeechSettings picks.
        services.AddKokoro(o =>
        {
            o.VoiceFolder = voiceDirectory;
            o.ModelFolder = Path.Combine(modelsDirectory, "kokoro");
        });
        services.AddQwenTts(o =>
        {
            o.VoiceFolder = voiceDirectory;
            o.ModelCache = Path.Combine(modelsDirectory, "huggingface");
        });
        services.AddSpeechEngines();
        services.AddSingleton<SpeechSettings>();

        // The speakers and the reply voice: Raven says three sentences at most, the panel shows the rest.
        services.AddSpeaking(o => o.MaximumSentences = SpeechSettings.MaximumSentences);

        services.AddSingleton<SoundPicks>();
        services.AddSingleton<IChatSounds, ChatSounds>();
        return services;
    }
}
