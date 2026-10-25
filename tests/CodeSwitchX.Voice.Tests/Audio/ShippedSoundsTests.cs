namespace CodeSwitchX.Voice.Tests.Audio;

using System.Runtime.InteropServices;
using CodeSwitchX.Voice.Audio;
using NAudio.Wave;

/// <summary>The pool of sounds CodeSwitchX ships (#306): musical, short, never clipping, a default for each slot.</summary>
public sealed class ShippedSoundsTests
{
    public static TheoryData<string> Ids => [.. ShippedSounds.All.Select(s => s.Id)];

    private static TimeSpan Length(string id)
    {
        var (pcm, format) = ShippedSounds.Load(id);
        return TimeSpan.FromSeconds((double)pcm.Length / format.AverageBytesPerSecond);
    }

    private static double Peak(string id) =>
        MemoryMarshal.Cast<byte, short>(ShippedSounds.Load(id).Pcm).ToArray().Max(s => Math.Abs((int)s)) / (double)short.MaxValue;

    [Theory]
    [MemberData(nameof(Ids))]
    public void Every_shipped_sound_loads_as_16_bit_mono_PCM_at_44_1_kHz(string id)
    {
        var (pcm, format) = ShippedSounds.Load(id);

        (format.Encoding, format.BitsPerSample, format.Channels, format.SampleRate).ShouldBe((WaveFormatEncoding.Pcm, 16, 1, 44100));
        pcm.Length.ShouldBeGreaterThan(0);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void No_shipped_sound_is_longer_than_one_and_a_half_seconds(string id) =>
        Length(id).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(1.5));

    [Theory]
    [MemberData(nameof(Ids))]
    public void No_shipped_sound_clips(string id)
    {
        Peak(id).ShouldBeLessThanOrEqualTo(0.9);
        if (id != ShippedSounds.DefaultFor(SoundSlot.HearsName))
        {
            Peak(id).ShouldBeGreaterThanOrEqualTo(0.5, "loud enough that the sounds' default level is about the old chime's");
        }
    }

    [Fact]
    public void The_name_sound_is_quiet_and_short()
    {
        var id = ShippedSounds.DefaultFor(SoundSlot.HearsName);

        Peak(id).ShouldBeLessThanOrEqualTo(0.3);
        Length(id).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(0.4));
    }

    [Fact]
    public void Each_slot_has_its_own_default_from_the_pool()
    {
        var defaults = Enum.GetValues<SoundSlot>().Select(ShippedSounds.DefaultFor).ToList();

        defaults.Count.ShouldBe(6);
        defaults.Distinct().Count().ShouldBe(6);
        defaults.ShouldAllBe(id => ShippedSounds.Has(id));
    }

    [Fact]
    public void The_old_chime_is_in_the_pool() => ShippedSounds.Has("two-tones").ShouldBeTrue();
}
