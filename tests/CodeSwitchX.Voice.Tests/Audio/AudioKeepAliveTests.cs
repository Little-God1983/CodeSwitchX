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

    // Review of #189: the default was stopped before the chosen one played; one that would not play left nothing on.
    [Fact]
    public void A_chosen_output_that_opens_but_will_not_play_leaves_the_default_on_and_is_tried_again()
    {
        _keepAlive.Start();
        _output.Busy = false;
        _output.ChosenWillNotPlay = true;

        _time.Advance(AudioKeepAlive.RetryChosenEvery);
        _output.Players[0].Playing.ShouldBeTrue("the default plays on");
        _output.Players[1].Disposed.ShouldBeTrue();

        _output.ChosenWillNotPlay = false;
        _time.Advance(AudioKeepAlive.RetryChosenEvery);
        (_output.Players[^1].Device, _output.Players[^1].Playing).ShouldBe(("id-tv", true));
        _output.Players[0].Disposed.ShouldBeTrue();
    }

    // Review of #189: the reply voice starts it on the thread pool, which can land after the app disposed it.
    [Fact]
    public void A_start_or_stop_after_dispose_does_nothing_and_does_not_throw()
    {
        _keepAlive.Dispose();

        Should.NotThrow(() => _keepAlive.Start());
        Should.NotThrow(() => _keepAlive.Stop());
        Should.NotThrow(() => _keepAlive.Dispose());
        _output.Players.ShouldBeEmpty();
    }

    /// <summary>An output whose chosen device is held by another app while <see cref="Busy"/>; a null device is the default.</summary>
    private sealed class Output : IAudioOutput
    {
        public List<Player> Players { get; } = [];

        public bool Busy { get; set; }

        /// <summary>The chosen device opens, then fails to play.</summary>
        public bool ChosenWillNotPlay { get; set; }

        public string? DeviceId { get; set; }

#pragma warning disable CS0067 // the tests set the device before the keep-alive starts
        public event EventHandler? Changed;
#pragma warning restore CS0067

        public IWavePlayer Open(IWaveProvider source, int latencyMs, out string? playsOn)
        {
            playsOn = DeviceId is { } id && !Busy ? id : null;
            var player = new Player(playsOn, playsOn is not null && ChosenWillNotPlay);
            player.Init(source);
            Players.Add(player);
            return player;
        }
    }

    private sealed class Player(string? device, bool willNotPlay) : IWavePlayer
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

        public void Play() => Playing = willNotPlay ? throw new InvalidOperationException("AUDCLNT_E_DEVICE_INVALIDATED") : true;

        public void Pause() => Playing = false;

        public void Stop() => Playing = false;

        public void Dispose()
        {
            Playing = false;
            Disposed = true;
        }
    }
}
