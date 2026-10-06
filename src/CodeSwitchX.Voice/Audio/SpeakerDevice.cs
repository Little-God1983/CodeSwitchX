namespace CodeSwitchX.Voice.Audio;

/// <summary>An audio endpoint by its id and the name Windows shows: a microphone or a speaker.</summary>
public interface IAudioDevice
{
    string Id { get; }

    string Name { get; }
}

/// <summary>An output device Raven can speak on: speakers, headphones, a headset.</summary>
public sealed record SpeakerDevice(string Id, string Name) : IAudioDevice;
