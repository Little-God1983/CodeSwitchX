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
        _time.Advance(AudioKeepAlive.RetryEvery);

        _output.Players.Count.ShouldBe(2);
        _output.Players[0].Disposed.ShouldBeTrue();
        (_output.Players[1].Device, _output.Players[1].Playing).ShouldBe(("id-tv", true));
    }

    [Fact]
    public void While_it_is_still_held_the_default_plays_on_without_a_gap_and_it_tries_again()
    {
        _keepAlive.Start();

        _time.Advance(AudioKeepAlive.RetryEvery);
        _output.Players[0].Disposed.ShouldBeFalse("the default plays on while the try fails");
        _output.Players.Skip(1).ShouldAllBe(p => p.Device == null && p.Disposed);

        _output.Busy = false;
        _time.Advance(AudioKeepAlive.RetryEvery);
        _output.Players[^1].Device.ShouldBe("id-tv");
        _output.Players[0].Disposed.ShouldBeTrue();
    }

    [Fact]
    public void On_the_output_chosen_from_the_start_it_tries_nothing()
    {
        _output.Busy = false;
        _keepAlive.Start();

        _time.Advance(AudioKeepAlive.RetryEvery * 3);

        _output.Players.Single().Device.ShouldBe("id-tv");
    }

    [Fact]
    public void Following_the_Windows_default_it_tries_nothing()
    {
        _output.Picked(null); // before the start: no restart to race the clock
        _keepAlive.Start();

        _time.Advance(AudioKeepAlive.RetryEvery * 3);

        _output.Players.ShouldHaveSingleItem();
    }

    [Fact]
    public void Stopped_it_tries_nothing_more()
    {
        _keepAlive.Start();
        _keepAlive.Stop();
        _output.Busy = false;

        _time.Advance(AudioKeepAlive.RetryEvery * 3);

        _output.Players.ShouldHaveSingleItem().Disposed.ShouldBeTrue();
    }

    // Review of #189: the default was stopped before the chosen one played; one that would not play left nothing on.
    [Fact]
    public void A_chosen_output_that_opens_but_will_not_play_leaves_the_default_on_and_is_tried_again()
    {
        _keepAlive.Start();
        _output.Busy = false;
        _output.ChosenWillNotPlay = true;

        _time.Advance(AudioKeepAlive.RetryEvery);
        _output.Players[0].Playing.ShouldBeTrue("the default plays on");
        _output.Players[1].Disposed.ShouldBeTrue();

        _output.ChosenWillNotPlay = false;
        _time.Advance(AudioKeepAlive.RetryEvery);
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

    // Review round 2 of #189: the output chosen held, and the Windows default the same device, nothing started, and nothing
    // tried again once the other app let go.
    [Fact]
    public void A_start_that_finds_no_output_tries_again()
    {
        _output.DefaultFails = true;
        _keepAlive.Start();
        _output.Players.ShouldBeEmpty();

        _output.Busy = false;
        _output.DefaultFails = false;
        _time.Advance(AudioKeepAlive.RetryEvery);

        (_output.Players.Single().Device, _output.Players.Single().Playing).ShouldBe(("id-tv", true));
    }

    // Review round 2 of #189: a pick stopped the old output before the new one played.
    [Fact]
    public async Task A_pick_moves_it_and_the_old_output_plays_until_the_new_one_does()
    {
        _output.Busy = false;
        _keepAlive.Start();

        _output.DeviceId = "id-headphones";
        await RestartedAsync(() => _output.Count == 2 && _output.Players[1].Playing && _output.Players[0].Disposed);

        (_output.Players[1].Device, _output.Players[1].Playing).ShouldBe(("id-headphones", true));
        _output.Players[0].Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_device_error_starts_it_again()
    {
        _output.Busy = false;
        _keepAlive.Start();

        _output.Players[0].Fail();
        await RestartedAsync(() => _output.Count == 2 && _output.Players[1].Playing);

        _output.Players[1].Playing.ShouldBeTrue();
    }

    // Review round 3 of #189: a stop raised late by a player already replaced restarted the one playing.
    [Fact]
    public async Task A_late_stop_from_a_replaced_player_restarts_nothing()
    {
        _output.Busy = false;
        _keepAlive.Start();
        _output.DeviceId = "id-headphones";
        await RestartedAsync(() => _output.Count == 2 && _output.Players[1].Playing && _output.Players[0].Disposed);

        _output.Players[0].FailLate();
        for (var i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        _output.Count.ShouldBe(2);
        _output.Players[1].Playing.ShouldBeTrue();
    }

    // Review round 3 of #189: after a switch that failed, the output that played on was taken for the default, and the
    // default the retry opened was thrown away.
    [Fact]
    public void After_a_pick_that_failed_the_retry_moves_off_the_output_no_longer_chosen()
    {
        _output.Busy = false;
        _keepAlive.Start(); // on the TV
        _output.Busy = true;
        _output.DefaultFails = true;
        _output.Picked("id-headphones"); // held, and no default either: the TV plays on
        _keepAlive.SwitchNowForTests();
        _output.Players.ShouldHaveSingleItem().Playing.ShouldBeTrue();

        _output.DefaultFails = false;
        _time.Advance(AudioKeepAlive.RetryEvery);

        (_output.Players[^1].Device, _output.Players[^1].Playing).ShouldBe((null, true), "the default, where the speech goes now");
        _output.Players[0].Disposed.ShouldBeTrue();
    }

    /// <summary>Advances the clock by the restart's debounce until <paramref name="done"/>: the restart waits on the thread pool.</summary>
    private async Task RestartedAsync(Func<bool> done)
    {
        for (var i = 0; i < 250 && !done(); i++)
        {
            _time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        done().ShouldBeTrue();
    }

    /// <summary>An output whose chosen device is held by another app while <see cref="Busy"/>; a null device is the default.</summary>
    private sealed class Output : IAudioOutput
    {
        private readonly List<Player> _players = [];

        /// <summary>A copy: the keep-alive opens players on the thread pool.</summary>
        public List<Player> Players
        {
            get
            {
                lock (_players)
                {
                    return [.. _players];
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_players)
                {
                    return _players.Count;
                }
            }
        }

        /// <summary>A pick that raises no restart: the test switches the keep-alive itself.</summary>
        public void Picked(string? id) => _deviceId = id;

        public bool Busy { get; set; }

        /// <summary>The Windows default will not open either (the same device, held).</summary>
        public bool DefaultFails { get; set; }

        /// <summary>The chosen device opens, then fails to play.</summary>
        public bool ChosenWillNotPlay { get; set; }

        private string? _deviceId;

        public string? DeviceId
        {
            get => _deviceId;
            set
            {
                _deviceId = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? Changed;

        public IWavePlayer Open(IWaveProvider source, int latencyMs, out string? playsOn)
        {
            playsOn = DeviceId is { } id && !Busy ? id : null;
            if (playsOn is null && DefaultFails)
            {
                throw new InvalidOperationException("MMSYSERR_ALLOCATED");
            }

            var player = new Player(playsOn, playsOn is not null && ChosenWillNotPlay);
            player.Init(source);
            lock (_players)
            {
                _players.Add(player);
            }

            return player;
        }
    }

    private sealed class Player(string? device, bool willNotPlay) : IWavePlayer
    {
        public string? Device => device;

        public bool Playing { get; private set; }

        public bool Disposed { get; private set; }

        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        private EventHandler<StoppedEventArgs>? _handlerAtPlay;

        /// <summary>A stop already on its way when the keep-alive let go of the player: raised to the handler it had then.</summary>
        public void FailLate() =>
            _handlerAtPlay?.Invoke(this, new StoppedEventArgs(new InvalidOperationException("AUDCLNT_E_DEVICE_INVALIDATED")));

        /// <summary>The device went while playing.</summary>
        public void Fail()
        {
            Playing = false;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(new InvalidOperationException("AUDCLNT_E_DEVICE_INVALIDATED")));
        }

        public float Volume { get; set; }

        public PlaybackState PlaybackState => Playing ? PlaybackState.Playing : PlaybackState.Stopped;

        public WaveFormat OutputWaveFormat { get; private set; } = new();

        public void Init(IWaveProvider waveProvider) => OutputWaveFormat = waveProvider.WaveFormat;

        public void Play()
        {
            Playing = willNotPlay ? throw new InvalidOperationException("AUDCLNT_E_DEVICE_INVALIDATED") : true;
            _handlerAtPlay = PlaybackStopped;
        }

        public void Pause() => Playing = false;

        public void Stop() => Playing = false;

        public void Dispose()
        {
            Playing = false;
            Disposed = true;
        }
    }
}
