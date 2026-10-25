using System.Collections.Concurrent;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Audio;

/// <summary>What a chat's sound is for (#306), in the order the Sounds page lists them.</summary>
public enum SoundSlot
{
    StartsWorking,
    Finishes,
    AsksQuestion,
    AsksPermission,
    Fails,

    /// <summary>Open mic heard "Raven" and listens for the rest (#217): played at once, past the queue.</summary>
    HearsName,
}

/// <summary>A sound of the shipped pool: its id, as stored in Settings, and its name, as the pickers show it.</summary>
public sealed record ShippedSound(string Id, string Name);

/// <summary>
/// The sounds CodeSwitchX ships (#306): marimba, kalimba, bells, wood and plucks, made by <c>scripts/sounds/make-sounds.cs</c>
/// and embedded as 16-bit mono WAVs at 44.1 kHz. Each slot has its own default from them.
/// </summary>
public static class ShippedSounds
{
    public static IReadOnlyList<ShippedSound> All { get; } =
    [
        new("marimba-rise", "Marimba, rising"),
        new("marimba-fall", "Marimba, falling"),
        new("marimba-single", "Marimba note"),
        new("kalimba-up", "Kalimba, two notes up"),
        new("kalimba-single", "Kalimba note"),
        new("bell-soft", "Soft bell"),
        new("bell-pair", "Two bells"),
        new("wood-knock", "Wood knock"),
        new("wood-double", "Two wood knocks"),
        new("pluck-high", "High pluck"),
        new("pluck-low", "Low pluck"),
        new("two-tones", "Two tones (the old chime)"),
    ];

    private static readonly ConcurrentDictionary<string, (byte[] Pcm, WaveFormat Format)> Loaded = new(StringComparer.Ordinal);

    /// <summary>The slot's sound before Settings changes it: a start is a light knock, a fail falls, the name is a quiet pluck.</summary>
    public static string DefaultFor(SoundSlot slot) => slot switch
    {
        SoundSlot.StartsWorking => "wood-knock",
        SoundSlot.Finishes => "marimba-rise",
        SoundSlot.AsksQuestion => "kalimba-up",
        SoundSlot.AsksPermission => "bell-pair",
        SoundSlot.Fails => "marimba-fall",
        _ => "pluck-high",
    };

    public static bool Has(string id) => All.Any(s => s.Id == id);

    /// <summary>The sound's samples and their format, read once from the assembly; throws for an id not shipped.</summary>
    internal static (byte[] Pcm, WaveFormat Format) Load(string id) => Loaded.GetOrAdd(id, static id =>
    {
        using var stream = typeof(ShippedSounds).Assembly.GetManifestResourceStream($"CodeSwitchX.Voice.Sounds.{id}.wav")
            ?? throw new ArgumentException($"No sound \"{id}\" is shipped.", nameof(id));
        using var reader = new WaveFileReader(stream);
        var pcm = new byte[reader.Length];
        reader.ReadExactly(pcm);
        return (pcm, reader.WaveFormat);
    });
}
