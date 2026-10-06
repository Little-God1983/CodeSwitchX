using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Shouldly;

namespace CodeSwitchX.Voice.Tests.Audio;

/// <summary>
/// The output chosen (#172): what its players do that WinMM's did not, and the speech player following a new choice.
/// </summary>
public sealed class AudioOutputTests
{
    // WasapiOut raises PlaybackStopped on its play thread after a device error, still Playing; its Stop joins that thread.
    // The speech player's handler stops the output, under the lock the UI thread's Hush takes: the app hung (review of #174).
    [Fact]
    public async Task A_stop_from_the_stopped_handler_does_not_join_the_play_thread()
    {
        var inner = new JoiningPlayer();
        var player = new StoppedOffThread(inner);
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, _) =>
        {
            player.Stop();
            handled.SetResult();
        };

        inner.FailOnPlayThread(new InvalidOperationException("AUDCLNT_E_DEVICE_INVALIDATED"));

        await handled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        inner.StoppedOnPlayThread.ShouldBeFalse();
    }

    [Fact]
    public void The_orb_s_level_comes_about_twenty_times_a_second_however_often_the_output_reads()
    {
        var output = new FakeOutput();
        using var speech = new WaveOutSpeechPlayer(output, NullLogger<WaveOutSpeechPlayer>.Instance);
        var levels = 0;
        speech.LevelChanged += (_, _) => levels++;
        speech.Enqueue(new SpeechChunk(new byte[24000 * 2], 24000)); // a second of speech

        var read = new byte[480]; // WASAPI's 10 ms periods
        for (var i = 0; i < 100; i++)
        {
            output.Players[0].Source!.Read(read, 0, read.Length);
        }

        levels.ShouldBe(20);
    }

    [Fact]
    public void Another_output_chosen_mid_reply_plays_the_rest_there()
    {
        var output = new FakeOutput();
        using var speech = new WaveOutSpeechPlayer(output, NullLogger<WaveOutSpeechPlayer>.Instance);
        var pcm = new byte[4800];
        pcm[4000] = 0x7F; // something to find on the new output
        speech.Enqueue(new SpeechChunk(pcm, 24000));
        output.Players[0].Source!.Read(new byte[2000], 0, 2000);

        output.DeviceId = "id-headphones";

        output.Players.Count.ShouldBe(2);
        output.Players[0].Disposed.ShouldBeTrue();
        output.Players[1].Playing.ShouldBeTrue();
        var rest = new byte[2800];
        output.Players[1].Source!.Read(rest, 0, rest.Length);
        rest[2000].ShouldBe((byte)0x7F, "what was still queued goes on on the new output");
    }

    [Fact]
    public void With_nothing_playing_a_new_output_opens_nothing()
    {
        var output = new FakeOutput();
        using var speech = new WaveOutSpeechPlayer(output, NullLogger<WaveOutSpeechPlayer>.Instance);

        output.DeviceId = "id-headphones";

        output.Players.ShouldBeEmpty();
    }

    /// <summary>A player whose Stop, called on the thread that raised PlaybackStopped, would join that thread: it records it instead of hanging.</summary>
    private sealed class JoiningPlayer : IWavePlayer
    {
        private Thread? _playThread;

        public bool StoppedOnPlayThread { get; private set; }

        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        public void FailOnPlayThread(Exception error)
        {
            _playThread = new Thread(() => PlaybackStopped?.Invoke(this, new StoppedEventArgs(error)));
            _playThread.Start();
            _playThread.Join();
        }

        public void Stop() => StoppedOnPlayThread |= Thread.CurrentThread == _playThread;

        public float Volume { get; set; }

        public PlaybackState PlaybackState => PlaybackState.Playing;

        public WaveFormat OutputWaveFormat => new(24000, 16, 1);

        public void Init(IWaveProvider waveProvider)
        {
        }

        public void Play()
        {
        }

        public void Pause()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeOutput : IAudioOutput
    {
        private string? _deviceId;

        public List<FakePlayer> Players { get; } = [];

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

        public IWavePlayer Create(int latencyMs)
        {
            var player = new FakePlayer();
            Players.Add(player);
            return player;
        }
    }

    /// <summary>A player the test reads from, as the output's thread would.</summary>
    private sealed class FakePlayer : IWavePlayer
    {
        public IWaveProvider? Source { get; private set; }

        public bool Playing { get; private set; }

        public bool Disposed { get; private set; }

#pragma warning disable CS0067 // never fails
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
#pragma warning restore CS0067

        public float Volume { get; set; }

        public PlaybackState PlaybackState => Playing ? PlaybackState.Playing : PlaybackState.Stopped;

        public WaveFormat OutputWaveFormat => Source!.WaveFormat;

        public void Init(IWaveProvider waveProvider) => Source = waveProvider;

        public void Play() => Playing = true;

        public void Pause() => Playing = false;

        public void Stop() => Playing = false;

        public void Dispose() => Disposed = true;
    }
}
