using System.Text.Json;
using NAudio.Wave;

namespace CodeSwitchX.Voice.Audio;

/// <summary>A sound of the pool: its id, as stored in Settings, its name, as the pickers show it, how long it plays, and whether CodeSwitchX ships it.</summary>
public sealed record SoundEntry(string Id, string Name, TimeSpan Length, bool Shipped);

/// <summary>
/// The sounds the six slots pick from (#306, #307): the shipped ones, then the user's own, each imported
/// (<see cref="SoundImport"/>) into the data folder as <c>&lt;id&gt;.wav</c> and named in its <c>index.json</c>. A sound whose
/// file went missing is no longer in the pool. Without a folder only the shipped sounds are. Thread-safe.
/// </summary>
public sealed class SoundLibrary
{
    private const string UserPrefix = "user-";

    private readonly string? _directory;
    private readonly Lock _lock = new();
    private readonly List<UserSound> _user;

    public SoundLibrary(string? directory)
    {
        _directory = directory;
        _user = ReadIndex();
    }

    /// <summary>A sound was added or removed. Raised on the thread that did it.</summary>
    public event EventHandler? Changed;

    /// <summary>The shipped sounds, then the user's whose files are there, in the order they were added.</summary>
    public IReadOnlyList<SoundEntry> All
    {
        get
        {
            List<UserSound> user;
            lock (_lock)
            {
                user = [.. _user];
            }

            return
            [
                .. ShippedSounds.All.Select(s => new SoundEntry(s.Id, s.Name, LengthOf(s.Id), Shipped: true)),
                .. user.Where(u => File.Exists(PathOf(u.Id))).Select(u => new SoundEntry(u.Id, u.Name, LengthOf(u.Id), Shipped: false)),
            ];
        }
    }

    public bool Has(string id) => ShippedSounds.Has(id) || (IsUser(id) && File.Exists(PathOf(id)));

    /// <summary>How long the sound plays; zero for one not in the pool.</summary>
    public TimeSpan LengthOf(string id)
    {
        if (!Has(id))
        {
            return TimeSpan.Zero;
        }

        if (ShippedSounds.Has(id))
        {
            var (pcm, format) = ShippedSounds.Load(id);
            return TimeSpan.FromSeconds((double)pcm.Length / format.AverageBytesPerSecond);
        }

        using var reader = new WaveFileReader(PathOf(id));
        return reader.TotalTime;
    }

    /// <summary>The sound's samples and their format; throws for one not in the pool.</summary>
    internal (byte[] Pcm, WaveFormat Format) Load(string id)
    {
        if (ShippedSounds.Has(id))
        {
            return ShippedSounds.Load(id);
        }

        using var reader = new WaveFileReader(PathOf(id));
        var pcm = new byte[reader.Length];
        reader.ReadExactly(pcm);
        return (pcm, reader.WaveFormat);
    }

    /// <summary>
    /// Imports the file the user picked as a sound of theirs, named after it ("Ding", "Ding (2)" for a name already in the
    /// pool), and says, for a file longer than the 3 s used, what the Sounds page notes. Throws
    /// <see cref="SoundImportException"/> for a file that is no sound; nothing is added then.
    /// </summary>
    public (SoundEntry Sound, string? Notice) Add(string source)
    {
        var directory = _directory ?? throw new InvalidOperationException("This library keeps no sounds of the user's.");
        Directory.CreateDirectory(directory);
        var id = UserPrefix + Guid.NewGuid().ToString("N")[..8];
        var length = SoundImport.Import(source, PathOf(id));
        var taken = All.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SoundEntry sound;
        lock (_lock)
        {
            taken.UnionWith(_user.Select(u => u.Name)); // added meanwhile
            var name = Path.GetFileNameWithoutExtension(source);
            for (var n = 2; taken.Contains(name); n++)
            {
                name = $"{Path.GetFileNameWithoutExtension(source)} ({n})";
            }

            _user.Add(new UserSound(id, name));
            WriteIndex();
            sound = new SoundEntry(id, name, LengthOf(id), Shipped: false);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return (sound, SoundImport.NoticeFor(length));
    }

    /// <summary>Removes a sound of the user's, its file too; nothing for a shipped one or one not there.</summary>
    public void Remove(string id)
    {
        lock (_lock)
        {
            if (_user.RemoveAll(u => u.Id == id) == 0)
            {
                return;
            }

            WriteIndex();
        }

        try
        {
            File.Delete(PathOf(id));
        }
        catch (IOException)
        {
            // Held by a play that has not let go yet: out of the index, it is no longer in the pool.
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsUser(string id) => id.StartsWith(UserPrefix, StringComparison.Ordinal) && id.Length > UserPrefix.Length
        && id[UserPrefix.Length..].All(char.IsAsciiHexDigit);

    private string PathOf(string id) => Path.Combine(_directory ?? "", id + ".wav");

    private string IndexFile => Path.Combine(_directory ?? "", "index.json");

    /// <summary>The user's sounds as the index names them; none without a folder, or with an index that cannot be read.</summary>
    private List<UserSound> ReadIndex()
    {
        try
        {
            return _directory is null || !File.Exists(IndexFile) ? []
                : JsonSerializer.Deserialize<List<UserSound>>(File.ReadAllText(IndexFile))?.Where(u => IsUser(u.Id)).ToList() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void WriteIndex() => File.WriteAllText(IndexFile, JsonSerializer.Serialize(_user));

    private sealed record UserSound(string Id, string Name);
}
