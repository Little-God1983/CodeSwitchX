using CodeSwitchX.Voice.Audio;
using LittleGod.Speech.Speaking;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using NSubstitute;

namespace CodeSwitchX.Voice.Tests.Audio;

/// <summary>The chats' sounds (#306) played through the package's clip player (#328), and the slots' picks.</summary>
public sealed class ChatSoundsTests
{
    private readonly IClipPlayer _clips = Substitute.For<IClipPlayer>();
    private Func<WaveStream>? _clip;
    private Func<float>? _gain;

    public ChatSoundsTests() =>
        _clips.PlayAsync(Arg.Do<Func<WaveStream>>(c => _clip = c), Arg.Do<Func<float>>(g => _gain = g), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

    [Fact]
    public async Task A_shipped_sound_is_played_through_the_clip_player_at_the_sounds_level()
    {
        var levels = new AudioLevels { Sounds = 0.4f };
        var sounds = new ChatSounds(_clips, levels, NullLogger<ChatSounds>.Instance);

        await sounds.PlayAsync("wood-knock", TestContext.Current.CancellationToken);

        using var clip = _clip.ShouldNotBeNull()();
        clip.TotalTime.ShouldBe(sounds.LengthOf("wood-knock"));
        levels.Sounds = 0.8f;
        _gain.ShouldNotBeNull()().ShouldBe(0.8f); // read as it plays: a slider moved mid-sound is heard
    }

    [Fact]
    public void An_unknown_sound_completes_at_once_and_plays_nothing()
    {
        var sounds = new ChatSounds(_clips, new AudioLevels(), NullLogger<ChatSounds>.Instance);

        sounds.PlayAsync("no-such-sound", TestContext.Current.CancellationToken).IsCompletedSuccessfully.ShouldBeTrue();

        _clips.ReceivedCalls().ShouldBeEmpty();
    }

    /// <summary>#307: a sound of the user's plays as the shipped ones do.</summary>
    [Fact]
    public async Task A_user_sound_plays_from_the_library()
    {
        var root = Directory.CreateTempSubdirectory("csx-play-").FullName;
        try
        {
            var library = new SoundLibrary(Path.Combine(root, "sounds"));
            var sound = library.Add(SoundLibraryTests.Tone(root, "Ding.wav", 0.5)).Sound;
            var sounds = new ChatSounds(_clips, new AudioLevels(), NullLogger<ChatSounds>.Instance, library);

            await sounds.PlayAsync(sound.Id, TestContext.Current.CancellationToken);

            using var clip = _clip.ShouldNotBeNull()();
            clip.WaveFormat.SampleRate.ShouldBe(44100);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Picks_start_on_the_slots_defaults_and_none_is_null()
    {
        var picks = new SoundPicks();

        picks.For(SoundSlot.Finishes).ShouldBe(ShippedSounds.DefaultFor(SoundSlot.Finishes));
        picks.Set(SoundSlot.Finishes, null);
        picks.For(SoundSlot.Finishes).ShouldBeNull();
        picks.For(SoundSlot.Fails).ShouldBe(ShippedSounds.DefaultFor(SoundSlot.Fails));
    }
}
