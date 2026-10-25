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

    /// <summary>Review of #307: a sound file that went bad is no sound: left out, its slot plays the default, and nothing throws.</summary>
    [Fact]
    public void A_sound_file_that_went_bad_is_left_out()
    {
        var sound = new SoundLibrary(Folder).Add(Picked("Ding.wav")).Sound;
        File.WriteAllText(Path.Combine(Folder, sound.Id + ".wav"), "garbage");

        var library = new SoundLibrary(Folder);
        var picks = new SoundPicks(library);
        picks.Set(SoundSlot.Fails, sound.Id);

        library.All.ShouldNotContain(s => s.Id == sound.Id);
        library.Has(sound.Id).ShouldBeFalse();
        library.LengthOf(sound.Id).ShouldBe(TimeSpan.Zero);
        picks.For(SoundSlot.Fails).ShouldBe(ShippedSounds.DefaultFor(SoundSlot.Fails));
    }

    /// <summary>Review round 1 of #307: a sound whose file is held or read-only when removed is out of the pool all the same, and nothing throws.</summary>
    [Fact]
    public void Removing_a_sound_whose_file_cannot_be_deleted_takes_it_out_of_the_pool()
    {
        var library = new SoundLibrary(Folder);
        var held = library.Add(Picked("Held.wav")).Sound;
        var locked = library.Add(Picked("Locked.wav")).Sound;
        var lockedFile = Path.Combine(Folder, locked.Id + ".wav");
        File.SetAttributes(lockedFile, FileAttributes.ReadOnly);
        var changes = 0;
        library.Changed += (_, _) => changes++;

        using (File.Open(Path.Combine(Folder, held.Id + ".wav"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            library.Remove(held.Id);
        }

        library.Remove(locked.Id);
        File.SetAttributes(lockedFile, FileAttributes.Normal);

        (library.Has(held.Id), library.Has(locked.Id)).ShouldBe((false, false));
        library.All.ShouldNotContain(s => !s.Shipped);
        changes.ShouldBe(2);
    }

    /// <summary>Review round 1 of #307: an index that cannot be written keeps nothing half added: no entry, no file.</summary>
    [Fact]
    public void A_sound_whose_index_cannot_be_written_is_not_added()
    {
        var library = new SoundLibrary(Folder);
        var first = library.Add(Picked("Ding.wav")).Sound;
        var index = Path.Combine(Folder, "index.json");
        File.SetAttributes(index, FileAttributes.ReadOnly);
        try
        {
            Should.Throw<SoundImportException>(() => library.Add(Picked("Dong.wav")));
        }
        finally
        {
            File.SetAttributes(index, FileAttributes.Normal);
        }

        library.All.Where(s => !s.Shipped).Select(s => s.Id).ShouldBe([first.Id]);
        Directory.GetFiles(Folder, "*.wav").Length.ShouldBe(1);
        Directory.GetFiles(Folder, "*.tmp").ShouldBeEmpty();
    }

    /// <summary>Review round 1 of #307: no sound is named as None, nor as another sound once punctuation is set aside.</summary>
    [Fact]
    public void A_sound_never_takes_the_name_of_none_or_of_another_sound()
    {
        var library = new SoundLibrary(Folder);

        var none = library.Add(Picked("none.wav")).Sound;
        library.Add(Picked("Ding.wav"));
        library.Add(Tone(Path.Combine(_root, "other"), "Ding.wav", 0.5)); // Ding (2)
        var spaced = library.Add(Picked("Ding 2.wav")).Sound;

        none.Name.ShouldBe("none (2)");
        spaced.Name.ShouldBe("Ding 2 (2)");
        library.Add(Picked("Don't.wav"));
        library.Add(Picked("Dont.wav")).Sound.Name.ShouldBe("Dont (2)", "Raven hears the two alike");
        library.Add(Picked(".wav")).Sound.Name.ShouldBe("Sound");
    }

    /// <summary>Review round 2 of #307: an index that cannot be read loses no sound: the files tell the pool, and an add keeps them.</summary>
    [Fact]
    public void An_unreadable_index_is_rebuilt_from_the_sound_files()
    {
        var first = new SoundLibrary(Folder);
        var ding = first.Add(Picked("Ding.wav")).Sound;
        var dong = first.Add(Picked("Dong.wav")).Sound;
        File.WriteAllText(Path.Combine(Folder, "index.json"), "{ damaged");

        var library = new SoundLibrary(Folder);
        library.Add(Picked("Bell.wav"));

        new SoundLibrary(Folder).All.Where(s => !s.Shipped).Select(s => s.Id).ShouldBe([ding.Id, dong.Id, .. library.All.Where(s => s.Name == "Bell").Select(s => s.Id)], ignoreOrder: true);
    }

    /// <summary>Review round 2 of #307: an index with empty entries is read without them, and nothing throws.</summary>
    [Fact]
    public void An_index_with_empty_entries_is_read_without_them()
    {
        var ding = new SoundLibrary(Folder).Add(Picked("Ding.wav")).Sound;
        File.WriteAllText(Path.Combine(Folder, "index.json"), $"[null, {{\"Name\":\"x\"}}, {{\"Id\":\"{ding.Id}\"}}]");

        var library = new SoundLibrary(Folder);

        library.All.Where(s => !s.Shipped).ShouldHaveSingleItem().Id.ShouldBe(ding.Id, "its file is there: named after the file again");
    }

    /// <summary>Review round 2 of #307: a file a scanner holds for a moment is back in the pool once it lets go.</summary>
    [Fact]
    public void A_sound_held_for_a_moment_is_back_once_it_is_let_go()
    {
        var library = new SoundLibrary(Folder);
        var sound = library.Add(Picked("Ding.wav")).Sound;
        var held = new SoundLibrary(Folder); // reads the file for the first time while it is held

        using (File.Open(Path.Combine(Folder, sound.Id + ".wav"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            held.Has(sound.Id).ShouldBeFalse("it cannot be read just now");
        }

        held.Has(sound.Id).ShouldBeTrue();
        held.All.ShouldContain(s => s.Id == sound.Id);
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
