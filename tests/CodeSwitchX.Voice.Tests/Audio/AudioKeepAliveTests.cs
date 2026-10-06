using CodeSwitchX.Voice.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NAudio.Wave;
using Shouldly;

namespace CodeSwitchX.Voice.Tests.Audio;

/// <summary>
/// The keep-alive on the Windows default in place of an output chosen that another app holds (#176) goes back to the one
/// chosen once it is free (#184): letting go of exclusive mode raises no device change.
/// </summary>
public sealed class AudioKeepAliveTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly Output _output = new() { DeviceId = "id-tv", Busy = true };
    private readonly AudioKeepAlive _keepAlive;

    public AudioKeepAliveTests()
    {
        _keepAlive = new AudioKeepAlive(_output, NullLogger<AudioKeepAlive>.Instance, _time, watchDevices: false);
    }

    public void Dispose() => _keepAlive.Dispose();

    [Fact]
    public void Once_the_output_chosen_is_free_it_moves_back_there()
    {
        _keepAlive.Start();
        _output.Players.Single().Device.ShouldBeNull("held by another app: the default is kept awake meanwhile");

        _output.Busy = false;
        _time.Advance(AudioKeepAlive.RetryChosenEvery);

        _output.Players.Count.ShouldBe(2);
        _output.Players[0].Disposed.ShouldBeTrue();
        (_output.Players[1].Device, _output.Players[1].Playing).ShouldBe(("id-tv", true));
    }

    [Fact]
    public void While_it_is_still_held_the_default_plays_on_without_a_gap_and_it_tries_again()
    {
        _keepAlive.Start();

        _time.Advance(AudioKeepAlive.RetryChosenEvery);
        _output.Players[0].Disposed.ShouldBeFalse("the default plays on while the try fails");
        _output.Players.Skip(1).ShouldAllBe(p => p.Device == null && p.Disposed);

        _output.Busy = false;
        _time.Advance(AudioKeepAlive.RetryChosenEvery);
        _output.Players[^1].Device.ShouldBe("id-tv");
        _output.Players[0].Disposed.ShouldBeTrue();
    }

    [Fact]
    public void On_the_output_chosen_from_the_start_it_tries_nothing()
    {
        _output.Busy = false;
        _keepAlive.Start();

        _time.Advance(AudioKeepAlive.RetryChosenEvery * 3);

        _output.Players.Single().Device.ShouldBe("id-tv");
    }

    [Fact]
    public void Following_the_Windows_default_it_tries_nothing()
    {
        _output.DeviceId = null;
        _keepAlive.Start();

        _time.Advance(AudioKeepAlive.RetryChosenEvery * 3);

        _output.Players.ShouldHaveSingleItem();
    }

    [Fact]
    public void Stopped_it_tries_nothing_more()
    {
        _keepAlive.Start();
        _keepAlive.Stop();
        _output.Busy = false;

        _time.Advance(AudioKeepAlive.RetryChosenEvery * 3);

        _output.Players.ShouldHaveSingleItem().Disposed.ShouldBeTrue();
    }

    /// <summary>An output whose chosen device is held by another app while <see cref="Busy"/>; a null device is the default.</summary>
    private sealed class Output : IAudioOutput
    {
        public List<Player> Players { get; } = [];

        public bool Busy { get; set; }

        public string? DeviceId { get; set; }

#pragma warning disable CS0067 // the tests set the device before the keep-alive starts
        public event EventHandler? Changed;
#pragma warning restore CS0067

        public IWavePlayer Open(IWaveProvider source, int latencyMs, out string? playsOn)
        {
            playsOn = DeviceId is { } id && !Busy ? id : null;
            var player = new Player(playsOn);
            player.Init(source);
            Players.Add(player);
            return player;
        }
    }

    private sealed class Player(string? device) : IWavePlayer
    {
        public string? Device => device;

        public bool Playing { get; private set; }

        public bool Disposed { get; private set; }

#pragma warning disable CS0067 // never fails
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
#pragma warning restore CS0067

        public float Volume { get; set; }

        public PlaybackState PlaybackState => Playing ? PlaybackState.Playing : PlaybackState.Stopped;

        public WaveFormat OutputWaveFormat { get; private set; } = new();

        public void Init(IWaveProvider waveProvider) => OutputWaveFormat = waveProvider.WaveFormat;

        public void Play() => Playing = true;

        public void Pause() => Playing = false;

        public void Stop() => Playing = false;

        public void Dispose()
        {
            Playing = false;
            Disposed = true;
        }
    }
}
