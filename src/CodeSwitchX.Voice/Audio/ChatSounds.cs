using System.Collections.Concurrent;
using LittleGod.Speech.Speaking;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Audio;

/// <summary>Plays the chats' sounds (#306); the tests use a fake.</summary>
public interface IChatSounds
{
    /// <summary>
    /// Plays the sound on the output chosen, at the sounds' level; done once it has played out, or at once when cancelled,
    /// which stops it. Never throws: a device that fails is logged, and the play is done. Any thread.
    /// </summary>
    Task PlayAsync(string soundId, CancellationToken ct);

    /// <summary>How long the sound plays; zero for one not shipped.</summary>
    TimeSpan LengthOf(string soundId);
}

/// <summary>
/// The sounds of the pool (<see cref="SoundLibrary"/>), played through the package's <see cref="IClipPlayer"/> (#328): one
/// device per play, opened off the caller's thread and closed when done, stopped past the sound's length and a margin.
/// </summary>
public sealed class ChatSounds(IClipPlayer clips, AudioLevels levels, ILogger<ChatSounds> logger, SoundLibrary? library = null)
    : IChatSounds
{
    private readonly SoundLibrary _library = library ?? new SoundLibrary(null);

    public TimeSpan LengthOf(string soundId) => _library.LengthOf(soundId);

    public Task PlayAsync(string soundId, CancellationToken ct)
    {
        if (!_library.Has(soundId))
        {
            logger.LogWarning("No sound {Sound} to play", soundId);
            return Task.CompletedTask;
        }

        return clips.PlayAsync(() =>
        {
            var (pcm, format) = _library.Load(soundId);
            return new RawSourceWaveStream(pcm, 0, pcm.Length, format);
        }, () => levels.Sounds, ct);
    }
}

/// <summary>
/// Which sound each slot plays (#306), as Settings picked it: an id of the pool (<see cref="SoundLibrary"/>), or null for
/// none. Starts on the slots' defaults; a sound of the user's whose file went missing plays the slot's default (#307). Any thread.
/// </summary>
public sealed class SoundPicks(SoundLibrary? library = null)
{
    private readonly SoundLibrary _library = library ?? new SoundLibrary(null);

    private readonly ConcurrentDictionary<SoundSlot, string?> _picks =
        new(Enum.GetValues<SoundSlot>().Select(s => KeyValuePair.Create(s, (string?)ShippedSounds.DefaultFor(s))));

    public string? For(SoundSlot slot) =>
        _picks.GetValueOrDefault(slot) is { } id && !_library.Has(id) ? ShippedSounds.DefaultFor(slot) : _picks.GetValueOrDefault(slot);

    public void Set(SoundSlot slot, string? id) => _picks[slot] = id;
}
