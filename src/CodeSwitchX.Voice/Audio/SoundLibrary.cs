using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, (DateTime Written, TimeSpan? Length)> _lengths = new(StringComparer.Ordinal);

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
                .. user.Select(u => (Sound: u, Length: UserLength(u.Id))).Where(u => u.Length is not null)
                    .Select(u => new SoundEntry(u.Sound.Id, u.Sound.Name, u.Length!.Value, Shipped: false)),
            ];
        }
    }

    public bool Has(string id) => ShippedSounds.Has(id) || (Listed(id) && UserLength(id) is not null);

    /// <summary>Whether the index names the sound: one removed whose file could not be deleted is not in the pool.</summary>
    private bool Listed(string id)
    {
        lock (_lock)
        {
            return _user.Any(u => u.Id == id);
        }
    }

    /// <summary>How long the sound plays; zero for one not in the pool.</summary>
    public TimeSpan LengthOf(string id)
    {
        if (ShippedSounds.Has(id))
        {
            var (pcm, format) = ShippedSounds.Load(id);
            return TimeSpan.FromSeconds((double)pcm.Length / format.AverageBytesPerSecond);
        }

        return UserLength(id) ?? TimeSpan.Zero;
    }

    /// <summary>
    /// How long a sound of the user's plays; null when its file is gone or is no sound any more (damaged, replaced), which
    /// leaves it out of the pool as a missing one is. Read once for each version of the file.
    /// </summary>
    private TimeSpan? UserLength(string id)
    {
        var path = PathOf(id);
        if (!IsUser(id) || !File.Exists(path))
        {
            return null;
        }

        var written = File.GetLastWriteTimeUtc(path);
        if (_lengths.TryGetValue(id, out var known) && known.Written == written)
        {
            return known.Length;
        }

        TimeSpan? length;
        try
        {
            using var reader = new WaveFileReader(path);
            length = reader.TotalTime > TimeSpan.Zero ? reader.TotalTime : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is IOException && ex is not EndOfStreamException))
        {
            return null; // held for a moment (a scanner on a new file): looked at again next time, not remembered as no sound
        }
        catch (Exception ex) when (ex is FormatException or EndOfStreamException or ArgumentException or InvalidDataException)
        {
            length = null; // damaged or replaced: no sound until the file changes
        }

        _lengths[id] = (written, length);
        return length;
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
        // Never as None, nor as another sound once case and punctuation are set aside: Raven picks them by name too.
        SoundEntry sound;
        lock (_lock)
        {
            var name = UniqueName(Path.GetFileNameWithoutExtension(source), TakenNames(_user));

            try
            {
                WriteIndex([.. _user, new UserSound(id, name)]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(PathOf(id)); // not kept: nothing of it stays
                throw new SoundImportException($"it could not be kept ({ex.Message.TrimEnd('.')}).", ex);
            }

            _user.Add(new UserSound(id, name));
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
            if (_user.All(u => u.Id != id))
            {
                return;
            }

            WriteIndex([.. _user.Where(u => u.Id != id)]);
            _user.RemoveAll(u => u.Id == id);
        }

        TryDelete(PathOf(id)); // held or read-only, it stays on disk, but out of the index it is no longer in the pool
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    /// <summary>A name as Raven matches it: lower case, letters and digits, one space between words.</summary>
    private static string NameKey(string name) => string.Join(' ',
        System.Text.RegularExpressions.Regex.Split(name.Replace("'", "").Replace("\u2019", "").ToLowerInvariant(), @"[^\p{L}\p{Nd}]+")
            .Where(w => w.Length > 0));

    /// <summary>The names no sound of the user's may take: None, the shipped sounds' and the user's own, as Raven matches them.</summary>
    private static HashSet<string> TakenNames(IEnumerable<UserSound> user) =>
        [.. ShippedSounds.All.Select(s => NameKey(s.Name)), NameKey("None"), .. user.Select(u => NameKey(u.Name))];

    /// <summary>
    /// <paramref name="stem"/> ("Sound" when it has no letters or digits), with " (2)", " (3)"… until no name taken is like it;
    /// the name is taken then.
    /// </summary>
    private static string UniqueName(string? stem, HashSet<string> taken)
    {
        stem = stem is { } s && NameKey(s).Length > 0 ? s : "Sound";
        var name = stem;
        for (var n = 2; taken.Contains(NameKey(name)); n++)
        {
            name = $"{stem} ({n})";
        }

        taken.Add(NameKey(name));
        return name;
    }

    private static bool IsUser(string id) => id.StartsWith(UserPrefix, StringComparison.Ordinal) && id.Length > UserPrefix.Length
        && id[UserPrefix.Length..].All(char.IsAsciiHexDigit);

    private string PathOf(string id) => Path.Combine(_directory ?? "", id + ".wav");

    private string IndexFile => Path.Combine(_directory ?? "", "index.json");

    /// <summary>The user's sounds as the index names them; none without a folder, or with an index that cannot be read.</summary>
    private List<UserSound> ReadIndex()
    {
        if (_directory is null)
        {
            return [];
        }

        // An index that cannot be read loses no sound: the files are the user's sounds, though their names are gone ("Sound").
        var listed = TryReadIndex() ?? [.. (Directory.Exists(_directory) ? Directory.GetFiles(_directory, UserPrefix + "*.wav") : [])
            .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).Select(id => new UserSound(id!, null!))];
        var taken = TakenNames([]);
        return [.. listed.Where(u => u?.Id is { } id && IsUser(id)).Select(u => new UserSound(u!.Id, UniqueName(u.Name, taken)))];
    }

    /// <summary>The index's entries, none for no index; null when it cannot be read: damaged, or held past a few tries.</summary>
    private List<UserSound?>? TryReadIndex()
    {
        if (!File.Exists(IndexFile))
        {
            return [];
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<List<UserSound?>>(File.ReadAllText(IndexFile)) ?? [];
            }
            catch (JsonException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5)
                {
                    return null;
                }

                Thread.Sleep(100); // a scanner or a backup holds it for a moment
            }
        }
    }

    /// <summary>Writes the index whole or not at all: a write cut off (the app killed, the power gone) leaves the one before.</summary>
    private void WriteIndex(List<UserSound> sounds)
    {
        var part = IndexFile + ".tmp";
        try
        {
            File.WriteAllText(part, JsonSerializer.Serialize(sounds));
            File.Move(part, IndexFile, overwrite: true);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private sealed record UserSound(string Id, string Name);
}
