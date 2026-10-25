namespace CodeSwitchX.Voice.Tests.Audio;

using CodeSwitchX.Voice.Audio;
using NAudio.Wave;

/// <summary>#307: the pool is the shipped sounds and the user's, kept in the data folder across starts.</summary>
public sealed class SoundLibraryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("csx-library-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder => Path.Combine(_root, "sounds");

    /// <summary>A 440 Hz tone at half scale, 16-bit mono 44.1 kHz, as a file the user picks.</summary>
    internal static string Tone(string directory, string name, double seconds)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        using var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 1));
        for (var i = 0; i < (int)(44100 * seconds); i++)
        {
            writer.WriteSample(0.5f * MathF.Sin(2 * MathF.PI * 440 * i / 44100));
        }

        return path;
    }

    private string Picked(string name, double seconds = 1) => Tone(Path.Combine(_root, "picked"), name, seconds);

    [Fact]
    public void Added_sounds_follow_the_shipped_ones_and_survive_a_new_library()
    {
        var (sound, notice) = new SoundLibrary(Folder).Add(Picked("Ding.wav"));

        notice.ShouldBeNull();
        sound.Name.ShouldBe("Ding");
        sound.Id.ShouldStartWith("user-");
        sound.Length.TotalSeconds.ShouldBe(1, 0.01);
        var again = new SoundLibrary(Folder);
        again.All.Select(s => s.Id).ShouldBe([.. ShippedSounds.All.Select(s => s.Id), sound.Id]);
        again.Has(sound.Id).ShouldBeTrue();
        again.All.Last().Shipped.ShouldBeFalse();
    }

    [Fact]
    public void A_long_file_is_added_with_its_notice()
    {
        var (sound, notice) = new SoundLibrary(Folder).Add(Picked("Long.wav", 12));

        notice.ShouldBe("This sound is 12 s long. Only the first 3 s will be used.");
        sound.Length.TotalSeconds.ShouldBe(3, 0.01);
    }

    [Fact]
    public void A_second_file_of_the_same_name_is_named_with_2()
    {
        var library = new SoundLibrary(Folder);

        library.Add(Picked("Ding.wav"));
        var second = library.Add(Tone(Path.Combine(_root, "elsewhere"), "Ding.wav", 0.5)).Sound;

        second.Name.ShouldBe("Ding (2)");
    }

    [Fact]
    public void Removing_a_sound_deletes_its_file_and_tells_it()
    {
        var library = new SoundLibrary(Folder);
        var sound = library.Add(Picked("Ding.wav")).Sound;
        var changes = 0;
        library.Changed += (_, _) => changes++;

        library.Remove(sound.Id);

        library.Has(sound.Id).ShouldBeFalse();
        Directory.GetFiles(Folder, "*.wav").ShouldBeEmpty();
        new SoundLibrary(Folder).All.ShouldNotContain(s => s.Id == sound.Id);
        changes.ShouldBe(1);
    }

    /// <summary>Review focus 3: a file deleted behind the app's back is left out, and its slot plays the default.</summary>
    [Fact]
    public void A_sound_whose_file_went_missing_is_left_out_and_its_slot_plays_the_default()
    {
        var library = new SoundLibrary(Folder);
        var sound = library.Add(Picked("Ding.wav")).Sound;
        var picks = new SoundPicks(library);
        picks.Set(SoundSlot.Finishes, sound.Id);
        picks.For(SoundSlot.Finishes).ShouldBe(sound.Id);

        File.Delete(Path.Combine(Folder, sound.Id + ".wav"));

        library.Has(sound.Id).ShouldBeFalse();
        library.All.ShouldNotContain(s => s.Id == sound.Id);
        picks.For(SoundSlot.Finishes).ShouldBe(ShippedSounds.DefaultFor(SoundSlot.Finishes));
    }

    [Fact]
    public void An_unreadable_file_adds_nothing()
    {
        var library = new SoundLibrary(Folder);
        var bad = Path.Combine(_root, "bad.wav");
        File.WriteAllText(bad, "no sound");

        Should.Throw<SoundImportException>(() => library.Add(bad));

        library.All.Count.ShouldBe(ShippedSounds.All.Count);
        (Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.wav") : []).ShouldBeEmpty();
    }

    [Fact]
    public void A_broken_index_starts_empty()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "index.json"), "{ not json");

        new SoundLibrary(Folder).All.Count.ShouldBe(ShippedSounds.All.Count);
    }

    [Fact]
    public void A_library_with_no_folder_has_the_shipped_sounds_only()
    {
        var library = new SoundLibrary(null);

        library.All.Count.ShouldBe(ShippedSounds.All.Count);
        library.Has("marimba-rise").ShouldBeTrue();
        library.LengthOf("marimba-rise").TotalSeconds.ShouldBeGreaterThan(0.5);
    }
}
