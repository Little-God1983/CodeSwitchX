using System.Runtime.InteropServices;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Shouldly;

namespace CodeSwitchX.Voice.Tests.Audio;

/// <summary>
/// The output chosen (#172): what its players do that WinMM's did not, the speech player following a new choice, and the
/// Windows default taking over from an output that fails (#176).
/// </summary>
public sealed class AudioOutputTests
{
    private const int DeviceInUse = unchecked((int)0x8889000A); // AUDCLNT_E_DEVICE_IN_USE: another app holds it exclusively

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
        var devices = new Devices();
        using var speech = Speech(devices);
        var levels = 0;
        speech.LevelChanged += (_, _) => levels++;
        speech.Enqueue(new SpeechChunk(new byte[24000 * 2], 24000)); // a second of speech

        var read = new byte[480]; // WASAPI's 10 ms periods
        for (var i = 0; i < 100; i++)
        {
            devices.Players[0].Source!.Read(read, 0, read.Length);
        }

        levels.ShouldBe(20);
    }

    [Fact]
    public void Another_output_chosen_mid_reply_plays_the_rest_there()
    {
        var devices = new Devices();
        using var speech = Speech(devices);
        speech.Enqueue(MarkedSpeech());
        devices.Players[0].Source!.Read(new byte[2000], 0, 2000);

        devices.Output.DeviceId = "id-headphones";

        devices.Players.Count.ShouldBe(2);
        devices.Players[0].Disposed.ShouldBeTrue();
        devices.Players[1].Device.ShouldBe("id-headphones");
        devices.Players[1].Playing.ShouldBeTrue();
        ReadsTheMark(devices.Players[1]).ShouldBeTrue("what was still queued goes on on the new output");
    }

    [Fact]
    public void With_nothing_playing_a_new_output_opens_nothing()
    {
        var devices = new Devices();
        using var speech = Speech(devices);

        devices.Output.DeviceId = "id-headphones";

        devices.Players.ShouldBeEmpty();
    }

    // A device listed as active can still refuse to start, while another app holds it in exclusive mode: Raven went quiet,
    // "Raven could not speak" on every reply (#176).
    [Fact]
    public void An_output_chosen_that_will_not_start_hands_its_sound_to_the_Windows_default()
    {
        var devices = new Devices { FailingInInit = "id-tv" };
        devices.Output.DeviceId = "id-tv";
        var source = new SilenceProvider(new WaveFormat(44100, 16, 2));

        var player = devices.Output.Open(source, 300);

        devices.Players.Count.ShouldBe(2);
        devices.Players[0].Disposed.ShouldBeTrue("the device that refused is let go");
        player.ShouldBeSameAs(devices.Players[1]);
        devices.Players[1].Device.ShouldBeNull("the Windows default");
        devices.Players[1].Source.ShouldBeSameAs(source);
    }

    [Fact]
    public void An_output_chosen_that_is_gone_hands_its_sound_to_the_Windows_default()
    {
        var devices = new Devices { Gone = "id-tv" };
        devices.Output.DeviceId = "id-tv";

        devices.Output.Open(new SilenceProvider(new WaveFormat(44100, 16, 2)), 300);

        devices.Players.Single().Device.ShouldBeNull();
    }

    [Fact]
    public void An_output_chosen_that_is_not_active_hands_its_sound_to_the_Windows_default()
    {
        var devices = new Devices { Asleep = "id-tv" };
        devices.Output.DeviceId = "id-tv";

        devices.Output.Open(new SilenceProvider(new WaveFormat(44100, 16, 2)), 300);

        devices.Players.Single().Device.ShouldBeNull();
    }

    [Fact]
    public void When_the_default_will_not_start_either_the_error_comes_out_and_nothing_is_left_open()
    {
        var devices = new Devices { FailingInInit = "id-tv", DefaultFailsInInit = true };
        devices.Output.DeviceId = "id-tv";

        Should.Throw<COMException>(() => devices.Output.Open(new SilenceProvider(new WaveFormat(44100, 16, 2)), 300));

        devices.Players.Count.ShouldBe(2);
        devices.Players.ShouldAllBe(p => p.Disposed);
    }

    // The default's failure was caught as the chosen one's, and the default tried twice on the same source (review of #183).
    [Fact]
    public void With_the_chosen_output_asleep_a_default_that_will_not_start_is_tried_once()
    {
        var devices = new Devices { Asleep = "id-tv", DefaultFailsInInit = true };
        devices.Output.DeviceId = "id-tv";

        Should.Throw<COMException>(() => devices.Output.Open(new SilenceProvider(new WaveFormat(44100, 16, 2)), 300));

        devices.Players.Count.ShouldBe(1);
    }

    [Fact]
    public void Open_says_where_the_sound_plays()
    {
        var devices = new Devices { FailingInInit = "id-tv" };
        var source = new SilenceProvider(new WaveFormat(44100, 16, 2));

        devices.Output.DeviceId = "id-headphones";
        devices.Output.Open(source, 300, out var playsOn);
        playsOn.ShouldBe("id-headphones");

        devices.Output.DeviceId = "id-tv";
        devices.Output.Open(source, 300, out playsOn);
        playsOn.ShouldBeNull("the Windows default, in place of the TV");

        devices.Output.DeviceId = null;
        devices.Output.Open(source, 300, out playsOn);
        playsOn.ShouldBeNull();
    }

    // Picks come from the UI thread while a chunk opens a device on another (#177): Open reads the choice once.
    [Fact]
    public void A_pick_while_a_device_opens_does_not_change_where_Open_says_it_plays()
    {
        var devices = new Devices();
        devices.Output.DeviceId = "id-headphones";
        devices.WhileOpening = () => devices.Output.DeviceId = "id-tv";

        devices.Output.Open(new SilenceProvider(new WaveFormat(44100, 16, 2)), 300, out var playsOn);

        playsOn.ShouldBe("id-headphones");
        devices.Players.Single().Device.ShouldBe("id-headphones");
    }

    [Fact]
    public void Speech_on_an_output_chosen_that_will_not_start_is_heard_on_the_Windows_default()
    {
        var devices = new Devices { FailingInInit = "id-tv" };
        devices.Output.DeviceId = "id-tv";
        using var speech = Speech(devices);

        speech.Enqueue(MarkedSpeech());

        devices.Players[^1].Device.ShouldBeNull();
        devices.Players[^1].Playing.ShouldBeTrue();
        ReadsTheMark(devices.Players[^1], skip: 0).ShouldBeTrue();
    }

    // Move stopped the old output before opening the new one: when the new one refused, the rest of the reply was dropped.
    [Fact]
    public void A_move_to_an_output_that_will_not_start_goes_on_on_the_Windows_default()
    {
        var devices = new Devices { FailingInInit = "id-tv" };
        using var speech = Speech(devices);
        speech.Enqueue(MarkedSpeech());
        devices.Players[0].Source!.Read(new byte[2000], 0, 2000);

        devices.Output.DeviceId = "id-tv";

        devices.Players.Count.ShouldBe(3);
        devices.Players[1].Device.ShouldBe("id-tv");
        devices.Players[1].Disposed.ShouldBeTrue();
        devices.Players[2].Device.ShouldBeNull();
        devices.Players[2].Playing.ShouldBeTrue();
        ReadsTheMark(devices.Players[2]).ShouldBeTrue("the queued rest of the reply is kept");
    }

    // Speech comes faster than it plays: a device error mid-reply threw away seconds still queued (older than #172).
    [Fact]
    public void A_headset_unplugged_mid_reply_goes_on_on_the_Windows_default_with_what_was_queued()
    {
        var devices = new Devices();
        devices.Output.DeviceId = "id-headphones";
        using var speech = Speech(devices);
        speech.Enqueue(MarkedSpeech());
        devices.Players[0].Source!.Read(new byte[2000], 0, 2000);

        devices.Gone = "id-headphones";
        devices.Players[0].Fail(new COMException("AUDCLNT_E_DEVICE_INVALIDATED", unchecked((int)0x88890004)));

        devices.Players.Count.ShouldBe(2);
        devices.Players[0].Disposed.ShouldBeTrue();
        devices.Players[1].Device.ShouldBeNull("the Windows default, not the output that just failed");
        devices.Players[1].Playing.ShouldBeTrue();
        speech.Remaining.ShouldBeGreaterThan(TimeSpan.Zero);
        ReadsTheMark(devices.Players[1]).ShouldBeTrue();
    }

    [Fact]
    public void When_the_default_fails_mid_reply_too_the_rest_is_dropped_rather_than_tried_forever()
    {
        var devices = new Devices();
        using var speech = Speech(devices);
        speech.Enqueue(MarkedSpeech());
        devices.Players[0].Fail(new COMException("AUDCLNT_E_DEVICE_INVALIDATED"));

        devices.Players[1].Fail(new COMException("AUDCLNT_E_DEVICE_INVALIDATED"));

        devices.Players.Count.ShouldBe(2);
        devices.Players[1].Disposed.ShouldBeTrue();
        speech.Remaining.ShouldBe(TimeSpan.Zero);
    }

    // A driver reset or a format changed in Sound settings fails the stream with the headset still there: the rest stays
    // private on it, not on the loudspeakers (review of #183).
    [Fact]
    public void A_device_error_mid_reply_on_an_output_still_there_goes_on_there()
    {
        var devices = new Devices();
        devices.Output.DeviceId = "id-headphones";
        using var speech = Speech(devices);
        speech.Enqueue(MarkedSpeech());
        devices.Players[0].Source!.Read(new byte[2000], 0, 2000);

        devices.Players[0].Fail(new COMException("AUDCLNT_E_DEVICE_INVALIDATED", unchecked((int)0x88890004)));

        devices.Players.Count.ShouldBe(2);
        devices.Players[1].Device.ShouldBe("id-headphones");
        ReadsTheMark(devices.Players[1]).ShouldBeTrue();
    }

    // The headset gone from the listing a moment later sets the choice back to the Windows default: speech is there already
    // and plays on, rather than cut a second time by a move to where it is (review of #183).
    [Fact]
    public void After_a_fallback_the_choice_going_back_to_the_default_moves_nothing()
    {
        var devices = new Devices();
        devices.Output.DeviceId = "id-headphones";
        using var speech = Speech(devices);
        speech.Enqueue(MarkedSpeech());
        devices.Players[0].Source!.Read(new byte[2000], 0, 2000);
        devices.Gone = "id-headphones";
        devices.Players[0].Fail(new COMException("AUDCLNT_E_DEVICE_INVALIDATED"));

        devices.Output.DeviceId = null;

        devices.Players.Count.ShouldBe(2);
        devices.Players[1].Disposed.ShouldBeFalse();
        ReadsTheMark(devices.Players[1]).ShouldBeTrue();
    }

    // The headset unplugged just before the reply: it starts on the default, and the listing then sets the choice back to the
    // default too. Speech is there already (review of #183).
    [Fact]
    public void Speech_that_started_on_the_default_in_place_of_a_gone_output_stays_when_the_choice_follows()
    {
        var devices = new Devices { Gone = "id-headphones" };
        devices.Output.DeviceId = "id-headphones";
        using var speech = Speech(devices);
        speech.Enqueue(MarkedSpeech());

        devices.Output.DeviceId = null;

        devices.Players.Count.ShouldBe(1);
        devices.Players[0].Disposed.ShouldBeFalse();
    }

    // Following the Windows default, a headset that is the default unplugged mid-reply: the default is now another device, and
    // the rest of the reply plays there (older than #172).
    [Fact]
    public void Following_the_default_a_device_error_mid_reply_goes_on_on_the_new_default()
    {
        var devices = new Devices();
        using var speech = Speech(devices);
        speech.Enqueue(MarkedSpeech());
        devices.Players[0].Source!.Read(new byte[2000], 0, 2000);

        devices.Players[0].Fail(new COMException("AUDCLNT_E_DEVICE_INVALIDATED"));

        devices.Players.Count.ShouldBe(2);
        ReadsTheMark(devices.Players[1]).ShouldBeTrue();
    }

    // The reply played out, the player feeds silence through its tail: nothing queued is worth waking the default for.
    [Fact]
    public void A_device_error_with_nothing_queued_opens_no_other_output()
    {
        var devices = new Devices();
        devices.Output.DeviceId = "id-headphones";
        using var speech = Speech(devices);
        speech.Enqueue(MarkedSpeech());
        devices.Players[0].Source!.Read(new byte[4800], 0, 4800);

        devices.Players[0].Fail(new COMException("AUDCLNT_E_DEVICE_INVALIDATED"));

        devices.Players.Count.ShouldBe(1);
        devices.Players[0].Disposed.ShouldBeTrue();
    }

    [Fact]
    public void The_next_reply_tries_the_output_chosen_again()
    {
        var devices = new Devices();
        devices.Output.DeviceId = "id-headphones";
        using var speech = Speech(devices);
        speech.Enqueue(MarkedSpeech());
        devices.Gone = "id-headphones";
        devices.Players[0].Fail(new COMException("AUDCLNT_E_DEVICE_INVALIDATED"));
        devices.Players[1].Device.ShouldBeNull();
        speech.Stop();
        devices.Gone = null; // plugged back in

        speech.Enqueue(MarkedSpeech());

        devices.Players[^1].Device.ShouldBe("id-headphones");
    }

    [Fact]
    public async Task A_voice_sample_on_an_output_chosen_that_will_not_start_plays_on_the_Windows_default()
    {
        var devices = new Devices { FailingInInit = "id-tv" };
        devices.Output.DeviceId = "id-tv";
        using var cancel = new CancellationTokenSource();

        var playing = new VoiceSamples(devices.Output).PlayAsync(SpeechEngine.Kokoro, "af_heart", cancel.Token);
        await devices.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await playing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        devices.Players[^1].Device.ShouldBeNull();
        devices.Players[^1].Source.ShouldNotBeNull();
    }

    // Unplugged mid-reply, the rest went on on the default; plugged back in before the reply ends, the choice comes back to it.
    // A move there was taken for one to where it plays already (review of #185).
    [Fact]
    public void A_headset_back_mid_reply_after_a_fallback_gets_the_rest_of_the_reply()
    {
        var devices = new Devices();
        devices.Output.DeviceId = "id-headphones";
        using var speech = Speech(devices);
        speech.Enqueue(MarkedSpeech());
        devices.Players[0].Source!.Read(new byte[1000], 0, 1000);
        devices.Gone = "id-headphones";
        devices.Players[0].Fail(new COMException("AUDCLNT_E_DEVICE_INVALIDATED"));
        devices.Players[1].Source!.Read(new byte[1000], 0, 1000);
        devices.Output.DeviceId = null; // the listing without the headset

        devices.Gone = null;
        devices.Output.DeviceId = "id-headphones"; // the listing with it again

        devices.Players.Count.ShouldBe(3);
        devices.Players[2].Device.ShouldBe("id-headphones");
        ReadsTheMark(devices.Players[2]).ShouldBeTrue();
    }

    // Hush reads Remaining on the UI thread: it waited on the lock a move holds while a Bluetooth device wakes (review of #185).
    [Fact]
    public async Task What_is_left_to_hear_is_read_at_once_while_a_move_opens_a_device()
    {
        var devices = new Devices();
        using var speech = new WaveOutSpeechPlayer(devices.Output, NullLogger<WaveOutSpeechPlayer>.Instance);
        speech.Enqueue(MarkedSpeech());
        using var waking = new ManualResetEventSlim();
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        devices.WhileOpening = () =>
        {
            opening.TrySetResult();
            waking.Wait(TimeSpan.FromSeconds(10));
        };

        devices.Output.DeviceId = "id-headphones";
        await opening.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var read = Task.Run(() => speech.Remaining, TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        waking.Set();
        finished.ShouldBeSameAs(read, "Remaining waited for the move");
        (await read).ShouldBeGreaterThan(TimeSpan.Zero, "the queue stays through the move");
    }

    // The move runs on the thread pool, where an exception ends the app; on the UI thread the dispatcher's handler caught it.
    [Fact]
    public void A_move_whose_old_output_fails_to_close_still_moves()
    {
        var devices = new Devices();
        using var speech = Speech(devices);
        speech.Enqueue(MarkedSpeech());
        devices.Players[0].DisposeThrows = true;

        devices.Output.DeviceId = "id-headphones";

        devices.Players[^1].Device.ShouldBe("id-headphones");
        devices.Players[^1].Playing.ShouldBeTrue();
    }

    // A pick on the panel mid-reply opened the new device on the UI thread, inside the player's lock: the window froze while a
    // Bluetooth device woke, or while a chunk opened one in Enqueue (#177).
    [Fact]
    public async Task A_pick_mid_reply_opens_the_new_output_off_the_thread_that_picked_it()
    {
        var devices = new Devices();
        using var speech = new WaveOutSpeechPlayer(devices.Output, NullLogger<WaveOutSpeechPlayer>.Instance);
        speech.Enqueue(MarkedSpeech());
        var opened = devices.Opened("id-headphones");
        var picker = Environment.CurrentManagedThreadId;

        devices.Output.DeviceId = "id-headphones";

        var moved = await opened.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        moved.OpenedOn.ShouldNotBe(picker);
    }

    // Two picks in a row: the first move runs after both and opens on the second; the second then has nothing left to do.
    [Fact]
    public void Two_picks_in_a_row_move_the_speech_once()
    {
        var devices = new Devices();
        var moves = new List<Action>();
        using var speech = new WaveOutSpeechPlayer(devices.Output, NullLogger<WaveOutSpeechPlayer>.Instance, moves.Add);
        speech.Enqueue(MarkedSpeech());
        devices.Players[0].Source!.Read(new byte[2000], 0, 2000);

        devices.Output.DeviceId = "id-tv";
        devices.Output.DeviceId = "id-headphones";
        moves.ForEach(move => move());

        devices.Players.Count.ShouldBe(2);
        devices.Players[1].Device.ShouldBe("id-headphones");
        devices.Players[1].Disposed.ShouldBeFalse();
        ReadsTheMark(devices.Players[1]).ShouldBeTrue();
    }

    /// <summary>The speech player with its moves run inline, so a test sees them done when the pick returns.</summary>
    private static WaveOutSpeechPlayer Speech(Devices devices) =>
        new(devices.Output, NullLogger<WaveOutSpeechPlayer>.Instance, move => move());

    /// <summary>A tenth of a second of silence with one loud byte at 4000, to find where the rest of it plays.</summary>
    private static SpeechChunk MarkedSpeech()
    {
        var pcm = new byte[4800];
        pcm[4000] = 0x7F;
        return new SpeechChunk(pcm, 24000);
    }

    /// <summary>Whether the mark comes on <paramref name="player"/>, after the <paramref name="skip"/> bytes an earlier player read.</summary>
    private static bool ReadsTheMark(FakePlayer player, int skip = 2000)
    {
        var rest = new byte[4800 - skip];
        player.Source!.Read(rest, 0, rest.Length);
        return rest[4000 - skip] == 0x7F;
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

    /// <summary>The real <see cref="AudioOutput"/> over fake devices; <see cref="Players"/> lists every player opened, in order.</summary>
    private sealed class Devices
    {
        public Devices()
        {
            Output = new AudioOutput(
                NullLogger<AudioOutput>.Instance,
                (id, _) => id == Gone ? throw new COMException("Element not found", unchecked((int)0x80070490))
                    : id == Asleep ? null
                    : Opening(id),
                _ => Add(new FakePlayer(null, this, DefaultFailsInInit)));
        }

        public IAudioOutput Output { get; }

        public List<FakePlayer> Players { get; } = [];

        /// <summary>The device listed as active that refuses to start.</summary>
        public string? FailingInInit { get; init; }

        /// <summary>The device unplugged since it was listed.</summary>
        public string? Gone { get; set; }

        /// <summary>The device listed but not active (a headset asleep).</summary>
        public string? Asleep { get; init; }

        public bool DefaultFailsInInit { get; init; }

        /// <summary>Runs while a chosen device opens: a pick meanwhile, or a device slow to wake.</summary>
        public Action? WhileOpening { get; set; }

        /// <summary>Set by the first player that plays.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly List<(string? Device, TaskCompletionSource<FakePlayer> Opened)> _awaited = [];

        /// <summary>Completes with the first player opened on <paramref name="device"/> from now on, on whatever thread opens it.</summary>
        public Task<FakePlayer> Opened(string? device)
        {
            var opened = new TaskCompletionSource<FakePlayer>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Players)
            {
                _awaited.Add((device, opened));
            }

            return opened.Task;
        }

        private FakePlayer Opening(string id)
        {
            WhileOpening?.Invoke();
            return Add(new FakePlayer(id, this, id == FailingInInit));
        }

        private FakePlayer Add(FakePlayer player)
        {
            lock (Players)
            {
                Players.Add(player);
                foreach (var (device, opened) in _awaited.Where(a => a.Device == player.Device))
                {
                    opened.TrySetResult(player);
                }
            }

            return player;
        }
    }

    /// <summary>A player the test reads from, as the output's thread would; a null <see cref="Device"/> is the Windows default.</summary>
    private sealed class FakePlayer(string? device, Devices devices, bool failsInInit) : IWavePlayer
    {
        public string? Device => device;

        public int OpenedOn { get; } = Environment.CurrentManagedThreadId;

        public IWaveProvider? Source { get; private set; }

        public bool Playing { get; private set; }

        public bool Disposed { get; private set; }

        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        public float Volume { get; set; }

        public PlaybackState PlaybackState => Playing ? PlaybackState.Playing : PlaybackState.Stopped;

        public WaveFormat OutputWaveFormat => Source!.WaveFormat;

        public void Init(IWaveProvider waveProvider) =>
            Source = failsInInit ? throw new COMException("AUDCLNT_E_DEVICE_IN_USE", DeviceInUse) : waveProvider;

        public void Play()
        {
            Playing = true;
            devices.Started.TrySetResult();
        }

        public void Pause() => Playing = false;

        /// <summary>As the real ones: a stop ends playback, and says so.</summary>
        public void Stop()
        {
            Playing = false;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        }

        /// <summary>The device went while playing, as the output reports it.</summary>
        public void Fail(Exception error)
        {
            Playing = false;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(error));
        }

        public bool DisposeThrows { get; set; }

        public void Dispose()
        {
            Disposed = true;
            if (DisposeThrows)
            {
                throw new COMException("AUDCLNT_E_DEVICE_INVALIDATED");
            }
        }
    }
}
