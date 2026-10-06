using NSubstitute;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed partial class RavenPanelViewModelTests
{
    private static readonly MicrophoneDevice Headset = new("id-headset", "Headset");
    private static readonly MicrophoneDevice Desk = new("id-desk", "Desk mic");
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(1);

    private readonly IMicrophoneCatalog _catalog = Substitute.For<IMicrophoneCatalog>();
    private readonly IMicrophoneRecorder _recorder = Substitute.For<IMicrophoneRecorder>();
    private readonly IDictationService _dictation = Substitute.For<IDictationService>();
    private readonly IWhisperModelStore _models = Substitute.For<IWhisperModelStore>();
    private readonly IDictationVocabularyProvider _vocabulary = Substitute.For<IDictationVocabularyProvider>();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeBrain _brain = new();
    private readonly FakeSpeech _speech = new();
    private readonly ReplyVoice _voice;

    public RavenPanelViewModelTests()
    {
        _voice = _speech.NewVoice();
        _catalog.List().Returns([Headset, Desk]);
        _catalog.Default().Returns(Headset);
        _models.IsPresent.Returns(true);
        _models.Model.Returns(WhisperModel.LargeV3Turbo);
        _vocabulary.GetAsync(Arg.Any<CancellationToken>()).Returns(DictationVocabulary.Empty);
        _recorder.Stop().Returns(new RecordedClip(new float[32000], TimeSpan.FromSeconds(2)));
        Transcribes(Task.FromResult(new DictationResult("Hallo Raven, open Diffusion Nexus", TimeSpan.FromSeconds(2))));
        _dictation.WarmUpAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private void Transcribes(Task<DictationResult> result) =>
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(result);

    private async Task<RavenPanelViewModel> NewVmAsync(IUiDispatcher? dispatcher = null)
    {
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, dispatcher ?? new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        return vm;
    }

    private async Task HoldAsync(RavenPanelViewModel vm)
    {
        vm.PressMic(TalkInput.MicButton);
        Speak();
        _time.Advance(Hold);
        await vm.ReleaseMicAsync(TalkInput.MicButton);
    }

    /// <summary>Holds and releases the mic <paramref name="count"/> times, each once the last capture has stopped; the
    /// returned tasks complete as each clip's turn in the transcription queue ends.</summary>
    private async Task<List<Task>> QueueClipsAsync(RavenPanelViewModel vm, int count)
    {
        var releases = new List<Task>();
        for (var i = 0; i < count; i++)
        {
            vm.PressMic(TalkInput.MicButton);
            await WithinAsync(vm.PendingStart);
            Speak();
            _time.Advance(Hold);
            releases.Add(vm.ReleaseMicAsync(TalkInput.MicButton));
            await WithinAsync(vm.PendingStop);
        }

        return releases;
    }

    /// <summary>A second of someone talking: loud enough, long enough, for the speech gate.</summary>
    private void Speak(double seconds = 1) => Blocks(0.1f, seconds);

    /// <summary>The quiet room of the RØDE input, at its loudest block.</summary>
    private void RoomNoise(double seconds) => Blocks(0.0004f, seconds);

    private void Blocks(float rms, double seconds)
    {
        for (var i = 0; i < (int)Math.Round(seconds * 100); i++)
        {
            Block(rms);
        }
    }

    private static async Task WithinAsync(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(5));

    /// <summary>A device notification, and the settle time after which the panel lists the devices again.</summary>
    private void DevicesChange()
    {
        _catalog.DevicesChanged += Raise.Event<EventHandler>(_catalog, EventArgs.Empty);
        _time.Advance(RavenPanelViewModel.DeviceChangeSettle);
    }

    /// <summary>One block the way WASAPI hands it over on the RØDE Connect input: 10 ms.</summary>
    private void Block(float rms) => _recorder.BlockCaptured += Raise.Event<EventHandler<CapturedBlock>>(_recorder, new CapturedBlock(rms, TimeSpan.FromMilliseconds(10)));

    [Fact]
    public async Task A_hold_records_and_the_transcript_appears_as_my_turn()
    {
        var vm = await NewVmAsync();

        vm.PressMic(TalkInput.MicButton);
        vm.State.ShouldBe(RavenState.Listening);
        vm.Caption.ShouldBe("Listening…");
        await WithinAsync(vm.PendingStart);
        _recorder.Received(1).Start(Headset.Id);
        Speak();
        _time.Advance(Hold);
        await vm.ReleaseMicAsync(TalkInput.MicButton);

        var last = vm.Log[^1];
        last.Kind.ShouldBe(RavenLogKind.You);
        last.Text.ShouldBe("Hallo Raven, open Diffusion Nexus");
        vm.State.ShouldBe(RavenState.Idle);
        vm.Caption.ShouldBe("Hold Ctrl+Alt+Space or the mic button to talk.");
        vm.Level.ShouldBe(0);
    }

    [Fact]
    public async Task A_quick_tap_latches_and_the_next_press_stops()
    {
        var vm = await NewVmAsync();

        vm.PressMic(TalkInput.MicButton);
        await vm.ReleaseMicAsync(TalkInput.MicButton);
        vm.State.ShouldBe(RavenState.Listening);
        Speak();
        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingTranscriptions);

        vm.State.ShouldBe(RavenState.Idle);
        vm.Log.Count(l => l.Kind == RavenLogKind.You).ShouldBe(1);
    }

    // Space or Enter on the mic button, and the hotkey over an admin window: a press and its release at once, a tap.
    [Theory]
    [InlineData(TalkInput.MicButton)]
    [InlineData(TalkInput.Hotkey)]
    public async Task A_tap_latches_and_the_next_tap_stops(TalkInput input)
    {
        var vm = await NewVmAsync();

        await vm.TapMic(input);
        vm.State.ShouldBe(RavenState.Listening, "a tap latches the mic on");
        Speak();
        _time.Advance(Hold);

        await WithinAsync(vm.TapMic(input));

        _recorder.Received(1).Stop();
        vm.State.ShouldBe(RavenState.Idle);
        vm.Log.Single().Kind.ShouldBe(RavenLogKind.You);
    }

    // The mouse holds the mic button; the chord pressed and let go meanwhile joins that hold and does not end it.
    [Fact]
    public async Task The_hotkey_pressed_and_released_while_the_mouse_holds_the_mic_does_not_stop_the_recording()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        Speak();
        _time.Advance(Hold);

        vm.PressMic(TalkInput.Hotkey);
        _time.Advance(Hold);
        await vm.ReleaseMicAsync(TalkInput.Hotkey);

        vm.State.ShouldBe(RavenState.Listening, "the mouse still holds the mic");
        _recorder.DidNotReceive().Stop();

        await vm.ReleaseMicAsync(TalkInput.MicButton);

        _recorder.Received(1).Stop();
        vm.State.ShouldBe(RavenState.Idle);
        vm.Log.Single().Kind.ShouldBe(RavenLogKind.You);
    }

    [Fact]
    public async Task A_mouse_click_while_the_hotkey_is_held_does_not_stop_the_recording()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.Hotkey);
        Speak();
        _time.Advance(Hold);

        vm.PressMic(TalkInput.MicButton);
        await vm.ReleaseMicAsync(TalkInput.MicButton);

        vm.State.ShouldBe(RavenState.Listening, "the chord is still held");
        _recorder.DidNotReceive().Stop();

        await vm.ReleaseMicAsync(TalkInput.Hotkey);

        _recorder.Received(1).Stop();
        vm.State.ShouldBe(RavenState.Idle);
    }

    [Fact]
    public async Task A_hold_with_both_inputs_stops_once_both_are_released_whichever_goes_first()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        vm.PressMic(TalkInput.Hotkey);
        Speak();
        _time.Advance(Hold);

        await vm.ReleaseMicAsync(TalkInput.MicButton);
        vm.State.ShouldBe(RavenState.Listening, "the chord is still held");

        await vm.ReleaseMicAsync(TalkInput.Hotkey);

        _recorder.Received(1).Stop();
        vm.State.ShouldBe(RavenState.Idle);
        _recorder.Received(1).Start(Headset.Id);
    }

    // The recording reached the limit while the chord was held: that hold is over, so the mouse starts a new one.
    [Fact]
    public async Task A_recording_that_ended_on_its_own_forgets_the_inputs_that_held_it()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.Hotkey);
        Speak();
        _recorder.LimitReached += Raise.Event<EventHandler>(_recorder, EventArgs.Empty);
        await WithinAsync(vm.PendingTranscriptions);

        vm.PressMic(TalkInput.MicButton);

        vm.State.ShouldBe(RavenState.Listening);
        await vm.ReleaseMicAsync(TalkInput.Hotkey);
        vm.State.ShouldBe(RavenState.Listening, "the chord's late release belongs to the recording that ended");
    }

    [Fact]
    public async Task A_clip_shorter_than_half_a_second_is_dropped_and_says_so()
    {
        _recorder.Stop().Returns(new RecordedClip(new float[100], TimeSpan.FromMilliseconds(499)));
        var vm = await NewVmAsync();

        await HoldAsync(vm);

        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Note);
        vm.Log.Single().Text.ShouldBe("That was too short. Hold the keys or the mic button while you talk.");
        vm.State.ShouldBe(RavenState.Idle);
        await _dictation.DidNotReceive().TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_model_is_downloaded_then_the_clip_is_transcribed()
    {
        _models.IsPresent.Returns(false);
        _models.DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<IProgress<double>?>()!.Report(0.5);
            return Task.CompletedTask;
        });
        var vm = await NewVmAsync();

        await HoldAsync(vm);

        var note = vm.Log.First(l => l.Kind == RavenLogKind.Note);
        note.Text.ShouldBe("Speech model downloaded.");
        vm.Log.IndexOf(note).ShouldBeLessThan(vm.Log.Count - 1);
        vm.Log[^1].Kind.ShouldBe(RavenLogKind.You);
    }

    [Fact]
    public async Task The_download_note_shows_whole_percents_while_it_runs()
    {
        _models.IsPresent.Returns(false);
        var seen = new List<string>();
        RavenPanelViewModel? vm = null;
        _models.DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var progress = call.Arg<IProgress<double>?>()!;
            seen.Add(vm!.Log[0].Text);
            progress.Report(0.421);
            seen.Add(vm.Log[0].Text);
            return Task.CompletedTask;
        });
        vm = await NewVmAsync();

        await HoldAsync(vm);

        seen.ShouldBe(["Downloading the speech model (1.6 GB)… 0%", "Downloading the speech model (1.6 GB)… 42%"]);
    }

    [Theory]
    [InlineData(WhisperModel.TinyEnglish, "78 MB")]
    [InlineData(WhisperModel.BaseEnglish, "148 MB")]
    [InlineData(WhisperModel.SmallEnglish, "488 MB")]
    [InlineData(WhisperModel.LargeV3Turbo, "1.6 GB")]
    public async Task The_download_note_says_the_size_of_the_model_picked(WhisperModel model, string size)
    {
        _models.IsPresent.Returns(false);
        _models.Model.Returns(model);
        string? said = null;
        RavenPanelViewModel? vm = null;
        _models.DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            said = vm!.Log[0].Text;
            return Task.CompletedTask;
        });
        vm = await NewVmAsync();

        await HoldAsync(vm);

        said.ShouldBe($"Downloading the speech model ({size})… 0%");
    }
    [Fact]
    public async Task A_failed_download_is_reported_and_retried_next_time()
    {
        _models.IsPresent.Returns(false);
        _models.DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("disk full")));
        var vm = await NewVmAsync();

        await HoldAsync(vm);

        vm.Log.Last().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Last().Text.ShouldBe("The speech model could not be downloaded: disk full. Press the mic to try again.");
        vm.State.ShouldBe(RavenState.Idle);

        await HoldAsync(vm);

        await _models.Received(2).DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>());
    }

    // Three clips were recorded while the first-run download ran, then the network dropped. Each queued clip used to
    // start a download of its own, fail, and warn: one failure is one warning, and the next press tries again.
    [Fact]
    public async Task A_failed_download_drops_the_clips_queued_behind_it_with_one_warning_and_the_next_press_tries_again()
    {
        var download = new TaskCompletionSource();
        var downloading = new TaskCompletionSource();
        _models.IsPresent.Returns(false);
        _models.DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            downloading.TrySetResult();
            return download.Task;
        });
        var vm = await NewVmAsync();
        var releases = await QueueClipsAsync(vm, 3);

        await WithinAsync(downloading.Task);
        download.SetException(new IOException("network down"));
        await WithinAsync(Task.WhenAll(releases));

        await _models.Received(1).DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>());
        vm.Log.Where(l => l.Kind == RavenLogKind.Warning).Select(l => l.Text).ShouldBe(
            ["The speech model could not be downloaded: network down. 2 waiting recordings were dropped. Press the mic to try again."]);
        await _dictation.DidNotReceive().TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>());
        vm.State.ShouldBe(RavenState.Idle);

        _models.DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await HoldAsync(vm);

        await _models.Received(2).DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>());
        vm.Log[^1].Kind.ShouldBe(RavenLogKind.You);
    }

    // A tap and a silent press queued behind the failed download would never have been transcribed: they are not
    // counted as dropped, and they still say why they were not.
    [Fact]
    public async Task A_failed_download_counts_only_the_waiting_clips_that_would_have_been_transcribed()
    {
        var download = new TaskCompletionSource();
        var downloading = new TaskCompletionSource();
        _models.IsPresent.Returns(false);
        _models.DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            downloading.TrySetResult();
            return download.Task;
        });
        var vm = await NewVmAsync();
        var releases = await QueueClipsAsync(vm, 1);
        vm.PressMic(TalkInput.MicButton); // held, but nothing said
        await WithinAsync(vm.PendingStart);
        _time.Advance(Hold);
        releases.Add(vm.ReleaseMicAsync(TalkInput.MicButton));
        await WithinAsync(vm.PendingStop);
        releases.AddRange(await QueueClipsAsync(vm, 1));

        await WithinAsync(downloading.Task);
        download.SetException(new IOException("network down"));
        await WithinAsync(Task.WhenAll(releases));

        vm.Log.Single(l => l.Kind == RavenLogKind.Warning).Text.ShouldBe(
            "The speech model could not be downloaded: network down. 1 waiting recording was dropped. Press the mic to try again.");
        vm.Log.ShouldContain(l => l.Kind == RavenLogKind.Note && l.Text == "I didn't hear anything.");
        await _models.Received(1).DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_download_with_one_clip_waiting_says_so_in_the_singular()
    {
        var download = new TaskCompletionSource();
        _models.IsPresent.Returns(false);
        _models.DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>()).Returns(download.Task);
        var vm = await NewVmAsync();
        var releases = await QueueClipsAsync(vm, 2);

        download.SetException(new IOException("network down"));
        await WithinAsync(Task.WhenAll(releases));

        vm.Log.Single(l => l.Kind == RavenLogKind.Warning).Text.ShouldBe(
            "The speech model could not be downloaded: network down. 1 waiting recording was dropped. Press the mic to try again.");
    }

    [Fact]
    public async Task A_denied_microphone_explains_the_privacy_setting()
    {
        _recorder.When(r => r.Start(Arg.Any<string>())).Do(_ => throw new MicrophoneException(MicrophoneFailureKind.Denied, "denied"));
        var vm = await NewVmAsync();

        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);

        vm.Log.Last().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Last().Text.ShouldContain("Privacy & security");
        vm.State.ShouldBe(RavenState.Idle);
    }

    [Fact]
    public async Task An_unavailable_microphone_names_the_device()
    {
        _recorder.When(r => r.Start(Arg.Any<string>())).Do(_ => throw new MicrophoneException(MicrophoneFailureKind.Unavailable, "busy"));
        var vm = await NewVmAsync();

        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);

        vm.Log.Last().Text.ShouldBe("Headset could not be opened. Another app may be using it exclusively.");
    }

    [Fact]
    public async Task A_stopped_windows_audio_service_says_so_rather_than_blaming_another_app()
    {
        _recorder.When(r => r.Start(Arg.Any<string>())).Do(_ => throw new MicrophoneException(MicrophoneFailureKind.AudioServiceDown, "not running"));
        var vm = await NewVmAsync();

        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);

        vm.Log.Last().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Last().Text.ShouldBe("Windows audio is not running. Start the Windows Audio service or restart the PC.");
    }

    [Fact]
    public async Task A_microphone_lost_while_recording_returns_to_idle_and_says_so()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);

        _recorder.Failed += Raise.Event<EventHandler<MicrophoneException>>(_recorder, new MicrophoneException(MicrophoneFailureKind.Missing, "gone"));

        vm.Log.Last().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Last().Text.ShouldContain("Headset");
        vm.State.ShouldBe(RavenState.Idle);
        await WithinAsync(vm.PendingStop);
        _recorder.Received(1).Stop();
        await _dictation.DidNotReceive().TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>());

        vm.PressMic(TalkInput.MicButton);
        vm.State.ShouldBe(RavenState.Listening);
    }

    [Fact]
    public async Task A_silent_microphone_is_warned_about_once()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);

        for (var i = 0; i < 300; i++) // 3 s
        {
            Block(0f);
        }

        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Single().Text.ShouldBe("No sound from Headset. Check that it isn't muted.");
    }

    // A Bluetooth headset takes a few seconds to switch to its microphone and hands over nothing until then.
    [Fact]
    public async Task A_microphone_that_starts_sending_late_turns_its_warning_into_a_note()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        Blocks(0f, 3);
        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Warning);

        Speak();

        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Note);
        vm.Log.Single().Text.ShouldBe("Headset took a moment to start sending sound. What you said before that was not recorded.");

        Blocks(0f, 3);
        Speak();

        vm.Log.Count.ShouldBe(1, "a pause after the late start is no new warning");
        vm.State.ShouldBe(RavenState.Listening);
    }

    [Fact]
    public async Task A_silent_microphone_that_never_starts_keeps_its_warning()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        Blocks(0f, 3);
        _time.Advance(Hold);

        await vm.ReleaseMicAsync(TalkInput.MicButton);

        vm.Log[0].Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log[0].Text.ShouldBe("No sound from Headset. Check that it isn't muted.");
    }

    // A Bluetooth headset goes to sleep halfway through a latched recording: heard first, then nothing for ten seconds.
    [Fact]
    public async Task A_microphone_that_stops_sending_sound_is_warned_about_once_and_its_return_is_noted()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        Speak();

        Blocks(0f, 9.99);
        vm.Log.ShouldBeEmpty("under ten seconds of nothing after sound is a pause");
        Block(0f);

        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Single().Text.ShouldBe("Headset stopped sending sound. Check that it isn't muted or gone to sleep.");

        Speak();

        vm.Log[^1].Kind.ShouldBe(RavenLogKind.Note);
        vm.Log[^1].Text.ShouldBe("Headset is sending sound again.");

        Blocks(0f, 10);
        Speak();

        vm.Log.Count.ShouldBe(2, "once per recording");
        vm.State.ShouldBe(RavenState.Listening);
    }

    [Fact]
    public async Task The_stopped_sending_sound_warning_is_armed_again_for_the_next_recording()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        Speak();
        Blocks(0f, 10);
        _time.Advance(Hold);
        await vm.ReleaseMicAsync(TalkInput.MicButton);

        vm.PressMic(TalkInput.MicButton);
        Speak();
        Blocks(0f, 10);

        vm.Log.Count(l => l.Text == "Headset stopped sending sound. Check that it isn't muted or gone to sleep.").ShouldBe(2);
        vm.Log.ShouldNotContain(l => l.Text == "Headset is sending sound again.", "the sound never came back");
    }

    // The recorder reports how long each block is. Assuming 50 ms blocks, the ten-millisecond ones of the RØDE input made
    // the two seconds of grace pass in 0.4 s.
    [Fact]
    public async Task The_silent_microphone_warning_counts_the_real_duration_of_the_blocks()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);

        for (var i = 0; i < 199; i++)
        {
            Block(0f);
        }

        vm.Log.ShouldBeEmpty("1.99 s of nothing is still inside the grace period");
        Block(0f);
        vm.Log.Single().Text.ShouldBe("No sound from Headset. Check that it isn't muted.");
    }

    [Fact]
    public async Task The_level_follows_the_microphone_while_listening()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);

        Block(0.1f);

        vm.Level.ShouldBe(AudioMath.LevelOf(0.1f));
        vm.Level.ShouldBeGreaterThan(0);
    }

    // The user dictates the next sentence while the last one is still being transcribed: nothing is lost, and the log
    // keeps the order they were spoken in, however long each takes.
    [Fact]
    public async Task A_second_recording_during_a_pending_transcription_records_and_both_transcripts_appear_in_recording_order()
    {
        var first = new TaskCompletionSource<DictationResult>();
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(first.Task, Task.FromResult(new DictationResult("second", TimeSpan.FromSeconds(2))));
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        Speak();
        _time.Advance(Hold);
        var firstRelease = vm.ReleaseMicAsync(TalkInput.MicButton);
        vm.State.ShouldBe(RavenState.Transcribing);
        vm.Caption.ShouldBe("Transcribing…");
        await WithinAsync(vm.PendingStop); // the capture is stopped; its clip is still being transcribed

        vm.PressMic(TalkInput.MicButton);
        vm.State.ShouldBe(RavenState.Listening, "capturing shows over a pending transcription");
        vm.Caption.ShouldBe("Listening…");
        await WithinAsync(vm.PendingStart);
        _recorder.Received(2).Start(Headset.Id);
        Speak();
        _time.Advance(Hold);
        var secondRelease = vm.ReleaseMicAsync(TalkInput.MicButton);
        await WithinAsync(vm.PendingStop);

        vm.State.ShouldBe(RavenState.Transcribing);
        vm.Caption.ShouldBe("Transcribing… (1 waiting)", "the first is transcribing, the second waits");
        secondRelease.IsCompleted.ShouldBeFalse("the second clip waits for the first");
        first.SetResult(new DictationResult("first", TimeSpan.FromSeconds(2)));
        await WithinAsync(firstRelease);
        await WithinAsync(secondRelease);

        vm.Log.Where(l => l.Kind == RavenLogKind.You).Select(l => l.Text).ShouldBe(["first", "second"]);
        vm.State.ShouldBe(RavenState.Idle);
        vm.Caption.ShouldBe(RavenPanelViewModel.IdleCaption);
    }

    // The first-run download of 1.6 GB takes minutes: the user can keep dictating, and the clips wait for the model.
    [Fact]
    public async Task A_download_in_progress_does_not_block_recording()
    {
        var download = new TaskCompletionSource();
        var downloading = new TaskCompletionSource();
        _models.IsPresent.Returns(false);
        _models.DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            downloading.TrySetResult();
            return download.Task;
        });
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        Speak();
        _time.Advance(Hold);
        var firstRelease = vm.ReleaseMicAsync(TalkInput.MicButton);
        await WithinAsync(downloading.Task);
        vm.Caption.ShouldBe("Downloading the speech model…");

        vm.PressMic(TalkInput.MicButton);
        vm.State.ShouldBe(RavenState.Listening);
        await WithinAsync(vm.PendingStart);
        _recorder.Received(2).Start(Headset.Id);
        Speak();
        _time.Advance(Hold);
        var secondRelease = vm.ReleaseMicAsync(TalkInput.MicButton);
        vm.State.ShouldBe(RavenState.Transcribing);
        vm.Caption.ShouldBe("Downloading the speech model…");

        _models.IsPresent.Returns(true);
        _models.Model.Returns(WhisperModel.LargeV3Turbo);
        download.SetResult();
        await WithinAsync(firstRelease);
        await WithinAsync(secondRelease);

        vm.Log.Count(l => l.Kind == RavenLogKind.You).ShouldBe(2);
        await _models.Received(1).DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>());
        vm.State.ShouldBe(RavenState.Idle);
    }

    // The recorder holds one capture at a time, so a press during the short stop of the last one cannot start. It says so.
    [Fact]
    public async Task A_press_while_the_last_recording_is_still_stopping_says_so_each_time()
    {
        using var hold = new ManualResetEventSlim();
        _recorder.Stop().Returns(_ =>
        {
            hold.Wait(TimeSpan.FromSeconds(10));
            return new RecordedClip(new float[32000], TimeSpan.FromSeconds(2));
        });
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        Speak();
        _time.Advance(Hold);
        var release = vm.ReleaseMicAsync(TalkInput.MicButton);

        vm.PressMic(TalkInput.MicButton);
        await vm.ReleaseMicAsync(TalkInput.MicButton);
        vm.PressMic(TalkInput.MicButton);

        vm.Log.Where(l => l.Kind == RavenLogKind.Note).Select(l => l.Text)
            .ShouldBe(["Still stopping the last recording. Press again.", "Still stopping the last recording. Press again."]);
        // The microphone opens on the thread pool: in a busy run, the first press's start may not have run yet.
        await Until(() => _recorder.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IMicrophoneRecorder.Start)));
        _recorder.Received(1).Start(Arg.Any<string>());
        hold.Set();
        await WithinAsync(release);
        vm.PressMic(TalkInput.MicButton);
        vm.State.ShouldBe(RavenState.Listening);
    }

    [Fact]
    public async Task An_unplugged_selected_microphone_falls_back_to_the_default_with_a_note()
    {
        var vm = await NewVmAsync();
        vm.SelectedMicrophone = Desk;
        _catalog.List().Returns([Headset]);

        DevicesChange();

        vm.SelectedMicrophone.ShouldBe(Headset);
        vm.Microphones.ShouldBe([Headset]);
        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Note);
        vm.Log.Single().Text.ShouldBe("Desk mic is gone. Using Headset.");
    }

    // Plugging in one USB headset raises five to eight notifications (state, added, and the default once per role); an
    // Audiosrv restart raises bursts. Each used to list the devices and refill the list on the UI thread.
    [Fact]
    public async Task Eight_device_notifications_within_300_ms_list_the_devices_once()
    {
        var vm = await NewVmAsync();
        _catalog.ClearReceivedCalls();

        for (var i = 0; i < 8; i++)
        {
            _catalog.DevicesChanged += Raise.Event<EventHandler>(_catalog, EventArgs.Empty);
            _time.Advance(TimeSpan.FromMilliseconds(30));
        }

        _catalog.DidNotReceive().List();
        _time.Advance(RavenPanelViewModel.DeviceChangeSettle);

        _catalog.Received(1).List();
        _catalog.Received(1).Default();
        vm.Microphones.ShouldBe([Headset, Desk]);
    }

    // The enumeration is COM calls and property-store reads: done off the UI thread, only its result is applied there.
    [Fact]
    public async Task The_devices_are_listed_outside_the_ui_thread_and_the_result_applied_on_it()
    {
        var dispatcher = new QueueingDispatcher();
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, dispatcher, _time,
            NullLogger<RavenPanelViewModel>.Instance);
        var refresh = vm.RefreshMicrophonesAsync();
        await WithinAsync(dispatcher.Posted);
        dispatcher.RunAll();
        await WithinAsync(refresh);
        _catalog.ClearReceivedCalls();
        _catalog.List().Returns([Desk]);

        DevicesChange();

        _catalog.Received(1).List();
        vm.Microphones.ShouldBe([Headset, Desk], "nothing is applied until the UI thread runs the post");
        dispatcher.RunAll();
        vm.Microphones.ShouldBe([Desk]);
    }

    // At startup a slow Bluetooth or USB endpoint, after a capture failure a dying audio service: either can hold the
    // enumeration up, and neither may hold up the shell. Listed off the calling thread, applied on the UI thread.
    [Fact]
    public async Task Refreshing_the_microphones_lists_them_off_the_calling_thread_and_applies_them_on_the_ui_thread()
    {
        using var hold = new ManualResetEventSlim();
        _catalog.List().Returns(_ =>
        {
            hold.Wait(TimeSpan.FromSeconds(10));
            return [Headset, Desk];
        });
        var dispatcher = new QueueingDispatcher();
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, dispatcher, _time,
            NullLogger<RavenPanelViewModel>.Instance);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var refresh = vm.RefreshMicrophonesAsync();

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        vm.PendingRefresh.ShouldBeSameAs(refresh);
        hold.Set();
        await WithinAsync(dispatcher.Posted);
        vm.Microphones.ShouldBeEmpty("nothing is applied until the UI thread runs the post");
        refresh.IsCompleted.ShouldBeFalse("the refresh completes once the devices are applied");
        dispatcher.RunAll();
        await WithinAsync(refresh);
        vm.Microphones.ShouldBe([Headset, Desk]);
        vm.SelectedMicrophone.ShouldBe(Headset);
    }

    // Pressed right after startup, before the first listing reached the UI thread: a microphone exists, it is only not
    // known yet. The press records once the listing arrives, without a "no microphone" warning.
    [Fact]
    public async Task A_press_while_the_first_listing_is_pending_records_once_it_arrives()
    {
        var dispatcher = new QueueingDispatcher();
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, dispatcher, _time,
            NullLogger<RavenPanelViewModel>.Instance);
        var refresh = vm.RefreshMicrophonesAsync();
        await WithinAsync(dispatcher.Posted);

        vm.PressMic(TalkInput.MicButton);

        vm.State.ShouldBe(RavenState.Listening);
        vm.Log.ShouldBeEmpty();
        _recorder.DidNotReceive().Start(Arg.Any<string>());
        dispatcher.RunAll();
        await WithinAsync(refresh);
        await WithinAsync(vm.PendingStart);

        _recorder.Received(1).Start(Headset.Id);
        vm.Log.ShouldBeEmpty();
        vm.State.ShouldBe(RavenState.Listening);
        Speak();
        dispatcher.RunAll(); // the captured blocks reach the panel through the UI thread
        _time.Advance(Hold);
        await vm.ReleaseMicAsync(TalkInput.MicButton);
        vm.Log.Single().Kind.ShouldBe(RavenLogKind.You);
    }

    [Fact]
    public async Task A_press_while_a_listing_that_finds_nothing_is_pending_warns_once_it_arrives()
    {
        _catalog.List().Returns([]);
        _catalog.Default().Returns((MicrophoneDevice?)null);
        var dispatcher = new QueueingDispatcher();
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, dispatcher, _time,
            NullLogger<RavenPanelViewModel>.Instance);
        var refresh = vm.RefreshMicrophonesAsync();
        await WithinAsync(dispatcher.Posted);

        vm.PressMic(TalkInput.MicButton);
        vm.Log.ShouldBeEmpty("the listing has not arrived yet");
        dispatcher.RunAll();
        await WithinAsync(refresh);
        await WithinAsync(vm.PendingStart);

        vm.Log.Single().Text.ShouldBe("No microphone found. Plug one in or check Windows sound settings.");
        vm.State.ShouldBe(RavenState.Idle);
        _recorder.DidNotReceive().Start(Arg.Any<string>());
        vm.PressMic(TalkInput.MicButton);
        vm.Log.Count.ShouldBe(2, "the gesture was reset: the next press tries again and warns again");
    }

    // Nobody awaits the refresh started by a device change: a failure applying its result must still be reported.
    [Fact]
    public async Task A_failure_applying_the_listing_is_logged_and_faults_the_refresh()
    {
        var logger = new CodeSwitchX.Tests.ListLogger<RavenPanelViewModel>();
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time, logger);
        vm.Microphones.CollectionChanged += (_, _) => throw new InvalidOperationException("binding broke");

        await Should.ThrowAsync<InvalidOperationException>(() => vm.RefreshMicrophonesAsync().WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));

        logger.Entries.ShouldContain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    // Two refreshes whose listings finish out of order: the later listing is the one applied.
    [Fact]
    public async Task A_listing_that_finishes_after_a_newer_one_is_not_applied_over_it()
    {
        using var hold = new ManualResetEventSlim();
        var calls = 0;
        _catalog.List().Returns(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                hold.Wait(TimeSpan.FromSeconds(10));
                return [Headset, Desk];
            }

            return [Desk];
        });
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance);

        var older = vm.RefreshMicrophonesAsync();
        while (Volatile.Read(ref calls) == 0)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await WithinAsync(vm.RefreshMicrophonesAsync());
        hold.Set();
        await WithinAsync(older);

        vm.Microphones.ShouldBe([Desk]);
    }

    [Fact]
    public async Task A_fallback_keeps_the_preferred_microphone_and_selects_it_again_when_it_comes_back()
    {
        var vm = await NewVmAsync();
        vm.DefaultMicrophone = Desk;
        vm.PreferredMicrophone.ShouldBe(Desk, "a pick in Settings is the user's choice");
        _catalog.List().Returns([Headset]);
        DevicesChange();
        vm.SelectedMicrophone.ShouldBe(Headset);
        vm.PreferredMicrophone.ShouldBe(Desk, "a fallback is not a choice");

        DevicesChange(); // another device comes or goes
        _catalog.List().Returns([Headset, Desk]);
        DevicesChange();

        vm.SelectedMicrophone.ShouldBe(Desk);
        vm.Log.Select(e => e.Text).ShouldBe(["Desk mic is gone. Using Headset.", "Using Desk mic again."]);
    }

    [Fact]
    public async Task A_list_bound_control_clearing_the_selection_during_a_refresh_changes_neither_choice()
    {
        var vm = await NewVmAsync();
        vm.DefaultMicrophone = Desk;
        // What a TwoWay-bound ComboBox does when its items are cleared: it writes null back.
        // Both pickers do it: the panel's and the one in Settings.
        vm.Microphones.CollectionChanged += (_, _) =>
        {
            vm.SelectedMicrophone = null;
            vm.DefaultMicrophone = null;
        };

        await vm.RefreshMicrophonesAsync();

        vm.SelectedMicrophone.ShouldBe(Desk);
        vm.DefaultMicrophone.ShouldBe(Desk);
        vm.PreferredMicrophone.ShouldBe(Desk);
        vm.Log.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_microphone_with_a_new_id_is_selected_again_without_a_note()
    {
        var vm = await NewVmAsync();
        vm.SelectedMicrophone = Desk;
        var moved = new MicrophoneDevice("id-desk-2", "Desk mic");
        _catalog.List().Returns([Headset, moved]);

        await vm.RefreshMicrophonesAsync();

        vm.SelectedMicrophone.ShouldBe(moved);
        vm.Log.ShouldBeEmpty();
    }

    // #172: the panel's picker is for trying a mic where you talk; Settings keeps the default Raven starts with.
    [Fact]
    public async Task A_pick_on_the_panel_is_heard_but_not_saved()
    {
        var vm = await NewVmAsync();

        vm.SelectedMicrophone = Desk;

        vm.DefaultMicrophone.ShouldBe(Headset);
        vm.PreferredMicrophone.ShouldBeNull("a trial is not the user's choice of default");
        vm.TrialMicrophone.ShouldBe(Desk);
        vm.MicTrialNote.ShouldBe("Trying it. Not saved: Raven starts with Headset.");
        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);
        _recorder.Received(1).Start(Desk.Id);
    }

    [Fact]
    public async Task Picking_the_default_on_the_panel_ends_the_trial()
    {
        var vm = await NewVmAsync();
        vm.SelectedMicrophone = Desk;

        vm.SelectedMicrophone = Headset;

        vm.TrialMicrophone.ShouldBeNull();
        vm.MicTrialNote.ShouldBeNull();
        vm.PreferredMicrophone.ShouldBeNull();
    }

    [Fact]
    public async Task A_new_default_picked_in_settings_is_saved_heard_and_ends_the_trial()
    {
        var usb = new MicrophoneDevice("id-usb", "USB mic");
        _catalog.List().Returns([Headset, Desk, usb]);
        var vm = await NewVmAsync();
        vm.SelectedMicrophone = usb;

        vm.DefaultMicrophone = Desk;

        vm.PreferredMicrophone.ShouldBe(Desk);
        vm.SelectedMicrophone.ShouldBe(Desk);
        vm.TrialMicrophone.ShouldBeNull();
        vm.MicTrialNote.ShouldBeNull();
    }

    [Fact]
    public async Task A_trial_outlives_device_changes_and_ends_when_its_microphone_goes()
    {
        var vm = await NewVmAsync();
        vm.SelectedMicrophone = Desk;

        DevicesChange(); // another device comes or goes
        vm.SelectedMicrophone.ShouldBe(Desk);

        _catalog.List().Returns([Headset]);
        DevicesChange();
        vm.SelectedMicrophone.ShouldBe(Headset);
        vm.TrialMicrophone.ShouldBeNull();
        vm.Log.Single().Text.ShouldBe("Desk mic is gone. Using Headset.");

        _catalog.List().Returns([Headset, Desk]);
        DevicesChange();
        vm.SelectedMicrophone.ShouldBe(Headset, "the trial is over: the default stays");
        vm.Log.Count.ShouldBe(1);
    }

    // Review of #174: the default falling back onto the mic being tried ended the trial, and its return switched mics silently.
    [Fact]
    public async Task A_trial_of_the_windows_default_outlives_the_stored_default_falling_back_onto_it()
    {
        var vm = await NewVmAsync();
        vm.DefaultMicrophone = Desk;
        vm.SelectedMicrophone = Headset; // the Windows default, tried

        _catalog.List().Returns([Headset]);
        DevicesChange();
        vm.TrialMicrophone.ShouldBe(Headset);
        vm.MicTrialNote.ShouldBe("Trying it. Not saved: Raven starts with Desk mic.", "the saved choice, though it fell back onto the one tried");
        _catalog.List().Returns([Headset, Desk]);
        DevicesChange();

        vm.SelectedMicrophone.ShouldBe(Headset, "only the user, or its mic going, ends a trial");
        vm.DefaultMicrophone.ShouldBe(Desk);
        vm.Log.ShouldBeEmpty();
    }

    [Fact]
    public async Task While_windows_audio_is_down_the_trial_note_is_not_shown()
    {
        Exception? failure = null;
        _catalog.List().Returns(_ => failure is null ? [Headset, Desk] : throw failure);
        var vm = await NewVmAsync();
        vm.SelectedMicrophone = Desk;

        failure = new System.Runtime.InteropServices.COMException("The audio service is not running.");
        DevicesChange();
        vm.MicTrialNote.ShouldBeNull("nothing is heard, so nothing is being tried");

        failure = null;
        DevicesChange();
        vm.SelectedMicrophone.ShouldBe(Desk, "the trial is heard again once Windows audio is back");
        vm.MicTrialNote.ShouldNotBeNull();
    }

    [Fact]
    public async Task While_a_trial_is_heard_the_default_going_and_coming_back_says_nothing()
    {
        var usb = new MicrophoneDevice("id-usb", "USB mic");
        _catalog.List().Returns([Headset, Desk, usb]);
        var vm = await NewVmAsync();
        vm.DefaultMicrophone = Desk;
        vm.SelectedMicrophone = usb;

        _catalog.List().Returns([Headset, usb]);
        DevicesChange();
        vm.DefaultMicrophone.ShouldBe(Headset, "the default falls back in Settings");
        vm.SelectedMicrophone.ShouldBe(usb);
        _catalog.List().Returns([Headset, Desk, usb]);
        DevicesChange();

        vm.DefaultMicrophone.ShouldBe(Desk);
        vm.SelectedMicrophone.ShouldBe(usb);
        vm.Log.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_microphone_at_all_warns_on_press()
    {
        _catalog.List().Returns([]);
        _catalog.Default().Returns((MicrophoneDevice?)null);
        var vm = await NewVmAsync();

        vm.PressMic(TalkInput.MicButton);

        vm.SelectedMicrophone.ShouldBeNull();
        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Single().Text.ShouldBe("No microphone found. Plug one in or check Windows sound settings.");
        vm.State.ShouldBe(RavenState.Idle);
        _recorder.DidNotReceive().Start(Arg.Any<string>());
    }

    [Fact]
    public async Task Typed_text_becomes_my_turn_and_the_box_clears()
    {
        var vm = await NewVmAsync();
        vm.TypedText = "  open the yard ";

        vm.SubmitTypedCommand.Execute(null);

        vm.Log.Single().Kind.ShouldBe(RavenLogKind.You);
        vm.Log.Single().Text.ShouldBe("open the yard");
        vm.TypedText.ShouldBe("");
    }

    // A panel left open for days of dictation: the log keeps the newest entries and lets the oldest go.
    [Fact]
    public async Task The_log_keeps_the_newest_entries_up_to_its_cap()
    {
        var vm = await NewVmAsync();

        for (var i = 1; i <= RavenPanelViewModel.MaximumLogEntries + 3; i++)
        {
            vm.TypedText = $"line {i}";
            vm.SubmitTypedCommand.Execute(null);
        }

        vm.Log.Count.ShouldBe(RavenPanelViewModel.MaximumLogEntries);
        vm.Log[0].Text.ShouldBe("line 4");
        vm.Log[^1].Text.ShouldBe($"line {RavenPanelViewModel.MaximumLogEntries + 3}");
    }

    [Fact]
    public async Task Blank_typed_text_is_ignored()
    {
        var vm = await NewVmAsync();
        vm.TypedText = "   ";

        vm.SubmitTypedCommand.Execute(null);

        vm.Log.ShouldBeEmpty();
    }

    [Fact]
    public async Task Collapsing_the_panel_does_not_stop_a_recording()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);

        vm.TogglePanelCommand.Execute(null);

        vm.IsOpen.ShouldBeFalse();
        vm.State.ShouldBe(RavenState.Listening);
        _recorder.DidNotReceive().Stop();
    }

    // The recorder owns the length limit and says when it stopped capturing; the panel keeps no clock of its own.
    [Fact]
    public async Task The_recorder_reaching_its_limit_ends_the_recording_like_a_release()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        Speak();
        _time.Advance(TimeSpan.FromMinutes(10));
        vm.State.ShouldBe(RavenState.Listening, "the panel has no timer of its own");

        _recorder.LimitReached += Raise.Event<EventHandler>(_recorder, EventArgs.Empty);
        await WithinAsync(vm.PendingStop);
        await WithinAsync(vm.PendingTranscriptions);

        _recorder.Received(1).Stop();
        vm.State.ShouldBe(RavenState.Idle);
        vm.Log.Last().Kind.ShouldBe(RavenLogKind.You);
        vm.PressMic(TalkInput.MicButton);
        vm.State.ShouldBe(RavenState.Listening, "the held key's gesture was reset, so the next press starts");
    }

    [Fact]
    public async Task A_limit_reported_after_the_recording_ended_changes_nothing()
    {
        var vm = await NewVmAsync();
        await HoldAsync(vm);

        _recorder.LimitReached += Raise.Event<EventHandler>(_recorder, EventArgs.Empty);

        _recorder.Received(1).Stop();
        vm.State.ShouldBe(RavenState.Idle);
    }

    [Fact]
    public async Task A_load_failure_names_the_reason_and_how_to_download_the_model_again()
    {
        _models.ModelPath.Returns(@"c:\m\ggml.bin");
        var load = new DictationModelLoadException(@"c:\m\ggml.bin", "Vulkan", new InvalidOperationException("out of memory."));
        Transcribes(Task.FromException<DictationResult>(load));
        var vm = await NewVmAsync();

        await HoldAsync(vm);

        vm.Log.Last().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Last().Text.ShouldBe(@"The speech model could not be loaded: out of memory. Delete c:\m\ggml.bin and press the mic to download it again.");
        vm.Log.Last().Text.ShouldNotContain("smaller model");
        vm.State.ShouldBe(RavenState.Idle);
    }

    [Fact]
    public async Task An_empty_transcript_adds_no_entry()
    {
        Transcribes(Task.FromResult(new DictationResult("  ", TimeSpan.FromSeconds(2))));
        var vm = await NewVmAsync();

        await HoldAsync(vm);

        vm.Log.ShouldBeEmpty();
        vm.State.ShouldBe(RavenState.Idle);
    }

    [Fact]
    public async Task The_silent_microphone_warning_is_armed_again_for_a_second_recording()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        for (var i = 0; i < 300; i++) // 3 s
        {
            Block(0f);
        }

        _time.Advance(Hold);
        await vm.ReleaseMicAsync(TalkInput.MicButton);
        vm.PressMic(TalkInput.MicButton);
        for (var i = 0; i < 300; i++) // 3 s
        {
            Block(0f);
        }

        vm.Log.Count(l => l.Text.StartsWith("No sound", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact]
    public async Task A_capture_failure_lists_the_microphones_again_off_the_ui_thread()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);
        using var hold = new ManualResetEventSlim();
        _catalog.List().Returns(_ =>
        {
            hold.Wait(TimeSpan.FromSeconds(10));
            return [Desk];
        });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        _recorder.Failed += Raise.Event<EventHandler<MicrophoneException>>(_recorder, new MicrophoneException(MicrophoneFailureKind.Missing, "gone"));

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "a dying audio service must not hold up the UI thread");
        vm.State.ShouldBe(RavenState.Idle);
        hold.Set();
        await WithinAsync(vm.PendingRefresh);
        vm.Microphones.ShouldBe([Desk]);
    }

    // The model is warmed at startup only. A warm-up per press loaded a model that would not load twice per press: once
    // for the warm-up, once for the clip queued behind it.
    [Fact]
    public async Task A_press_does_not_warm_the_model_up()
    {
        var vm = await NewVmAsync();

        await HoldAsync(vm);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await _dictation.DidNotReceive().WarmUpAsync(Arg.Any<CancellationToken>());
    }

    // The clip that asked for the download is transcribed next and loads the model itself. A warm-up started then took
    // the model first, and the clip waited for its throwaway decode as well.
    [Fact]
    public async Task A_downloaded_model_is_not_warmed_up_the_clip_that_asked_for_it_loads_it()
    {
        _models.IsPresent.Returns(false);
        _models.DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var vm = await NewVmAsync();

        await HoldAsync(vm);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await _dictation.Received(1).TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>());
        await _dictation.DidNotReceive().WarmUpAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_download_warms_nothing_up()
    {
        _models.IsPresent.Returns(false);
        _models.DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("disk full")));
        var vm = await NewVmAsync();

        await HoldAsync(vm);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await _dictation.DidNotReceive().WarmUpAsync(Arg.Any<CancellationToken>());
    }

    // Loading the model takes seconds. Whatever the warm-up does on the calling thread, the timer that starts it must not
    // wait for it.
    [Fact]
    public async Task A_warm_up_that_blocks_does_not_hold_up_its_caller()
    {
        using var hold = new ManualResetEventSlim();
        var entered = new TaskCompletionSource();
        _dictation.WarmUpAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            hold.Wait(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        });
        var vm = await NewVmAsync();
        vm.ScheduleWarmUp();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        _time.Advance(RavenPanelViewModel.StartupWarmUpDelay);

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        await WithinAsync(entered.Task);
        hold.Set();
    }

    // The hotkey works while the panel is collapsed, so the first clip after launch must be fast either way.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_model_is_warmed_up_five_seconds_after_startup_open_or_collapsed(bool open)
    {
        var warmed = new TaskCompletionSource();
        _dictation.WarmUpAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            warmed.TrySetResult();
            return Task.CompletedTask;
        });
        var vm = await NewVmAsync();
        vm.IsOpen = open;

        vm.ScheduleWarmUp();
        _time.Advance(TimeSpan.FromSeconds(4.9));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        warmed.Task.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(0.1));

        await WithinAsync(warmed.Task);
    }

    // No model yet: the first press downloads it. A startup warm-up has nothing to load.
    [Fact]
    public async Task There_is_no_startup_warm_up_without_a_model()
    {
        _models.IsPresent.Returns(false);
        var vm = await NewVmAsync();

        vm.ScheduleWarmUp();
        _time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        await _dictation.DidNotReceive().WarmUpAsync(Arg.Any<CancellationToken>());
    }

    // Whisper makes up words on a clip with nobody talking ("Oh.", or the vocabulary read back).
    [Fact]
    public async Task A_quiet_clip_is_not_transcribed_and_says_so()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        RoomNoise(3);
        Speak(0.4); // a cough is not dictation
        _time.Advance(Hold);

        await vm.ReleaseMicAsync(TalkInput.MicButton);

        await _dictation.DidNotReceive().TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>());
        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Note);
        vm.Log.Single().Text.ShouldBe("I didn't hear anything.");
        vm.State.ShouldBe(RavenState.Idle);
        vm.Caption.ShouldBe(RavenPanelViewModel.IdleCaption);
    }

    [Fact]
    public async Task A_clip_with_speech_between_pauses_is_transcribed()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        RoomNoise(1);
        Speak(0.3);
        RoomNoise(0.5);
        Speak(0.3);
        _time.Advance(Hold);

        await vm.ReleaseMicAsync(TalkInput.MicButton);

        vm.Log.Single().Kind.ShouldBe(RavenLogKind.You);
    }

    // The RØDE Connect Virtual Input delivers normal speech at about 0.007, under the fixed 0.01 the gate used to need.
    [Fact]
    public async Task Speech_on_a_quiet_microphone_is_transcribed()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        RoomNoise(1);
        Blocks(0.007f, 1);
        RoomNoise(0.5);
        _time.Advance(Hold);

        await vm.ReleaseMicAsync(TalkInput.MicButton);

        vm.Log.Single().Kind.ShouldBe(RavenLogKind.You);
    }

    [Fact]
    public async Task A_quiet_clip_does_not_start_the_model_download()
    {
        _models.IsPresent.Returns(false);
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        RoomNoise(2);
        _time.Advance(Hold);

        await vm.ReleaseMicAsync(TalkInput.MicButton);

        await _models.DidNotReceive().DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_speech_of_one_recording_does_not_count_for_the_next()
    {
        var vm = await NewVmAsync();
        await HoldAsync(vm);
        vm.PressMic(TalkInput.MicButton);
        RoomNoise(1);
        _time.Advance(Hold);

        await vm.ReleaseMicAsync(TalkInput.MicButton);

        vm.Log.Last().Text.ShouldBe("I didn't hear anything.");
        await _dictation.Received(1).TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_caption_says_downloading_while_the_model_downloads()
    {
        _models.IsPresent.Returns(false);
        string? caption = null;
        RavenPanelViewModel? vm = null;
        _models.DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            caption = vm!.Caption;
            return Task.CompletedTask;
        });
        vm = await NewVmAsync();

        await HoldAsync(vm);

        caption.ShouldBe("Downloading the speech model…");
    }

    // A stopped Windows audio service makes the device enumeration throw a COMException. The app must still start.
    [Fact]
    public async Task Windows_audio_being_unavailable_warns_and_leaves_no_microphones()
    {
        _catalog.List().Returns(_ => throw new System.Runtime.InteropServices.COMException("The audio service is not running."));

        var vm = await NewVmAsync();

        vm.Microphones.ShouldBeEmpty();
        vm.SelectedMicrophone.ShouldBeNull();
        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Single().Text.ShouldBe("Windows audio is not available: The audio service is not running.");
    }

    [Fact]
    public async Task Windows_audio_failing_on_the_default_device_is_caught_too()
    {
        var vm = await NewVmAsync();
        vm.DefaultMicrophone = Desk;
        _catalog.Default().Returns(_ => throw new System.Runtime.InteropServices.COMException("gone"));

        await vm.RefreshMicrophonesAsync();

        vm.Microphones.ShouldBeEmpty();
        vm.SelectedMicrophone.ShouldBeNull();
        vm.PreferredMicrophone.ShouldBe(Desk, "the user's choice outlives the audio service");
        vm.Log.Single().Text.ShouldBe("Windows audio is not available: gone");
    }

    [Fact]
    public async Task A_device_change_with_no_devices_clears_the_selection()
    {
        var vm = await NewVmAsync();
        vm.SelectedMicrophone.ShouldNotBeNull();
        _catalog.List().Returns([]);
        _catalog.Default().Returns((MicrophoneDevice?)null);

        DevicesChange();

        vm.SelectedMicrophone.ShouldBeNull();
        vm.Microphones.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_lost_microphone_warning_has_the_exact_text()
    {
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);

        _recorder.Failed += Raise.Event<EventHandler<MicrophoneException>>(_recorder, new MicrophoneException(MicrophoneFailureKind.Missing, "gone"));

        vm.Log.Last().Text.ShouldBe("Headset is not available any more.");
    }

    // Audiosrv stopping or restarting raises bursts of device notifications, render devices included; each one lists
    // the devices again and fails the same way.
    [Fact]
    public async Task Windows_audio_failing_again_and_again_warns_once_and_says_when_it_is_back()
    {
        Exception? failure = null;
        _catalog.List().Returns(_ => failure is null ? [Headset, Desk] : throw failure);
        var vm = await NewVmAsync();
        failure = new System.Runtime.InteropServices.COMException("The audio service is not running.");

        for (var i = 0; i < 3; i++)
        {
            DevicesChange();
        }

        vm.Log.Count(l => l.Kind == RavenLogKind.Warning).ShouldBe(1);
        vm.Log.Single().Text.ShouldBe("Windows audio is not available: The audio service is not running.");

        failure = null;
        DevicesChange();

        vm.Log.Select(l => l.Text).ShouldBe(["Windows audio is not available: The audio service is not running.", "Windows audio is back."]);
        vm.Microphones.ShouldBe([Headset, Desk]);

        failure = new System.Runtime.InteropServices.COMException("stopped again");
        DevicesChange();

        vm.Log.Last().Text.ShouldBe("Windows audio is not available: stopped again", "a new failure after a recovery is news");
    }

    [Fact]
    public async Task The_only_microphone_unplugged_says_none_is_left_and_its_return_says_so_too()
    {
        var vm = await NewVmAsync();
        vm.DefaultMicrophone = Desk;
        _catalog.List().Returns([]);
        _catalog.Default().Returns((MicrophoneDevice?)null);

        DevicesChange();

        vm.SelectedMicrophone.ShouldBeNull();
        vm.PreferredMicrophone.ShouldBe(Desk);
        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Note);
        vm.Log.Single().Text.ShouldBe("Desk mic is gone. No microphone is connected.");

        DevicesChange(); // still nothing
        vm.Log.Count.ShouldBe(1);

        _catalog.List().Returns([Desk]);
        _catalog.Default().Returns(Desk);
        DevicesChange();

        vm.SelectedMicrophone.ShouldBe(Desk);
        vm.Log.Select(l => l.Text).ShouldBe(["Desk mic is gone. No microphone is connected.", "Using Desk mic again."]);
    }

    [Fact]
    public async Task The_fallback_microphone_unplugged_too_says_none_is_left()
    {
        var vm = await NewVmAsync();
        vm.DefaultMicrophone = Desk;
        _catalog.List().Returns([Headset]);
        DevicesChange();
        _catalog.List().Returns([]);
        _catalog.Default().Returns((MicrophoneDevice?)null);

        DevicesChange();

        vm.Log.Select(l => l.Text).ShouldBe(["Desk mic is gone. Using Headset.", "Headset is gone. No microphone is connected."]);
    }

    // Stopping waits for the capture thread (up to 2 s), copies and resamples up to two minutes of audio and disposes
    // the capture: none of that on the UI thread, and the panel says it is transcribing while it happens.
    [Fact]
    public async Task A_slow_stop_runs_off_the_calling_thread_while_the_panel_already_says_transcribing()
    {
        using var hold = new ManualResetEventSlim();
        var entered = new TaskCompletionSource();
        RavenPanelViewModel? vm = null;
        RavenState? stateDuringStop = null;
        _recorder.Stop().Returns(_ =>
        {
            stateDuringStop = vm!.State;
            entered.TrySetResult();
            hold.Wait(TimeSpan.FromSeconds(10));
            return new RecordedClip(new float[32000], TimeSpan.FromSeconds(2));
        });
        vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        Speak();
        _time.Advance(Hold);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var release = vm.ReleaseMicAsync(TalkInput.MicButton);

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        release.IsCompleted.ShouldBeFalse();
        vm.State.ShouldBe(RavenState.Transcribing);
        vm.Caption.ShouldBe("Transcribing…");
        await WithinAsync(entered.Task);
        stateDuringStop.ShouldBe(RavenState.Transcribing);

        hold.Set();
        await WithinAsync(release);

        vm.State.ShouldBe(RavenState.Idle);
        vm.Log.Last().Kind.ShouldBe(RavenLogKind.You);
    }

    [Fact]
    public async Task A_slow_stop_after_a_capture_failure_does_not_hold_up_the_caller_either()
    {
        using var hold = new ManualResetEventSlim();
        _recorder.Stop().Returns(_ =>
        {
            hold.Wait(TimeSpan.FromSeconds(10));
            return new RecordedClip([], TimeSpan.Zero);
        });
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingStart);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        _recorder.Failed += Raise.Event<EventHandler<MicrophoneException>>(_recorder, new MicrophoneException(MicrophoneFailureKind.Missing, "gone"));

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        vm.State.ShouldBe(RavenState.Idle);
        vm.PressMic(TalkInput.MicButton);
        _recorder.Received(1).Start(Arg.Any<string>()); // a press while the old capture is still stopping is ignored
        vm.Log.Last().Text.ShouldBe("Still stopping the last recording. Press again.");

        hold.Set();
        await WithinAsync(vm.PendingStop);
        vm.PressMic(TalkInput.MicButton);
        vm.State.ShouldBe(RavenState.Listening);
    }

    // Opening a Bluetooth headset or a waking USB device takes hundreds of milliseconds to seconds: not on the UI thread.
    [Fact]
    public async Task A_slow_start_runs_off_the_calling_thread_while_the_panel_already_says_listening()
    {
        using var hold = new ManualResetEventSlim();
        var entered = new TaskCompletionSource();
        _recorder.When(r => r.Start(Arg.Any<string>())).Do(_ =>
        {
            entered.TrySetResult();
            hold.Wait(TimeSpan.FromSeconds(10));
        });
        var vm = await NewVmAsync();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        vm.PressMic(TalkInput.MicButton);

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        vm.State.ShouldBe(RavenState.Listening);
        vm.Caption.ShouldBe("Listening…");
        await WithinAsync(entered.Task);
        vm.PendingStart.IsCompleted.ShouldBeFalse();
        hold.Set();
        await WithinAsync(vm.PendingStart);
        vm.State.ShouldBe(RavenState.Listening);
    }

    [Fact]
    public async Task A_release_before_the_start_finished_stops_once_the_start_is_done()
    {
        using var hold = new ManualResetEventSlim();
        var started = false;
        var stoppedAfterStart = false;
        _recorder.When(r => r.Start(Arg.Any<string>())).Do(_ =>
        {
            hold.Wait(TimeSpan.FromSeconds(10));
            Volatile.Write(ref started, true);
        });
        _recorder.Stop().Returns(_ =>
        {
            stoppedAfterStart = Volatile.Read(ref started);
            return new RecordedClip(new float[32000], TimeSpan.FromSeconds(2));
        });
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        Speak();
        _time.Advance(Hold);

        var release = vm.ReleaseMicAsync(TalkInput.MicButton);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _recorder.DidNotReceive().Stop();
        hold.Set();
        await WithinAsync(release);

        _recorder.Received(1).Stop();
        stoppedAfterStart.ShouldBeTrue("the stop waited for the start");
        vm.Log.Single().Kind.ShouldBe(RavenLogKind.You);
        vm.State.ShouldBe(RavenState.Idle);
    }

    [Fact]
    public async Task A_start_that_fails_late_returns_to_idle_with_its_warning_and_the_next_press_starts_again()
    {
        using var hold = new ManualResetEventSlim();
        var calls = 0;
        _recorder.When(r => r.Start(Arg.Any<string>())).Do(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                hold.Wait(TimeSpan.FromSeconds(10));
                throw new MicrophoneException(MicrophoneFailureKind.Unavailable, "busy");
            }
        });
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        await vm.ReleaseMicAsync(TalkInput.MicButton); // a tap: latched while the start still runs
        vm.State.ShouldBe(RavenState.Listening);

        hold.Set();
        await WithinAsync(vm.PendingStart);

        vm.State.ShouldBe(RavenState.Idle);
        vm.Caption.ShouldBe(RavenPanelViewModel.IdleCaption);
        vm.Log.Single().Text.ShouldBe("Headset could not be opened. Another app may be using it exclusively.");
        vm.PressMic(TalkInput.MicButton);
        vm.State.ShouldBe(RavenState.Listening, "the gesture was reset: the press starts, it does not stop the latch");
        await WithinAsync(vm.PendingStart);
        _recorder.Received(2).Start(Headset.Id);
    }

    [Fact]
    public async Task A_release_before_a_start_that_fails_ends_with_the_warning_and_nothing_to_transcribe()
    {
        using var hold = new ManualResetEventSlim();
        _recorder.When(r => r.Start(Arg.Any<string>())).Do(_ =>
        {
            hold.Wait(TimeSpan.FromSeconds(10));
            throw new MicrophoneException(MicrophoneFailureKind.Denied, "denied");
        });
        var vm = await NewVmAsync();
        vm.PressMic(TalkInput.MicButton);
        Speak();
        _time.Advance(Hold);

        var release = vm.ReleaseMicAsync(TalkInput.MicButton);
        hold.Set();
        await WithinAsync(release);

        vm.State.ShouldBe(RavenState.Idle);
        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Single().Text.ShouldContain("Privacy & security");
        _recorder.DidNotReceive().Stop();
        await _dictation.DidNotReceive().TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>());
    }

    // 1.6 GB in 80 KB reads is some twenty thousand reports: only a new whole percent goes to the UI thread.
    [Fact]
    public async Task Download_progress_is_posted_to_the_ui_thread_only_when_the_percent_changes()
    {
        _models.IsPresent.Returns(false);
        var dispatcher = new CountingDispatcher();
        var postsDuringDownload = 0;
        RavenPanelViewModel? vm = null;
        _models.DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var progress = call.Arg<IProgress<double>?>()!;
            var before = dispatcher.Posts;
            for (var i = 0; i < 1000; i++)
            {
                progress.Report(0.5 + i / 1_000_000.0);
            }

            postsDuringDownload = dispatcher.Posts - before;
            vm!.Log[0].Text.ShouldBe("Downloading the speech model (1.6 GB)… 50%");
            return Task.CompletedTask;
        });
        vm = await NewVmAsync(dispatcher);

        await HoldAsync(vm);

        postsDuringDownload.ShouldBe(1);
    }

    // Reading every .code-workspace file takes time: it runs while the user talks, not between release and Whisper.
    [Fact]
    public async Task The_vocabulary_is_fetched_when_the_recording_starts_not_after_the_release()
    {
        var fetched = new TaskCompletionSource();
        var words = new DictationVocabulary(["Diffusion Nexus"], []);
        _vocabulary.GetAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            fetched.TrySetResult();
            return Task.FromResult(words);
        });
        var vm = await NewVmAsync();

        vm.PressMic(TalkInput.MicButton);
        await WithinAsync(fetched.Task);
        Speak();
        _time.Advance(Hold);
        await vm.ReleaseMicAsync(TalkInput.MicButton);

        await _vocabulary.Received(1).GetAsync(Arg.Any<CancellationToken>());
        await _dictation.Received(1).TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), words, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_vocabulary_that_cannot_be_read_does_not_stop_the_transcription()
    {
        _vocabulary.GetAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<DictationVocabulary>(new IOException("network drive gone")));
        var vm = await NewVmAsync();

        await HoldAsync(vm);

        vm.Log.Single().Kind.ShouldBe(RavenLogKind.You);
        await _dictation.Received(1).TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), DictationVocabulary.Empty, Arg.Any<CancellationToken>());
    }

    /// <summary>Keeps posts until the test runs them, the way the UI thread runs them later.</summary>
    private sealed class QueueingDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _posts = new();
        private readonly TaskCompletionSource _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once anything has been posted.</summary>
        public Task Posted => _posted.Task;

        public void Post<T>(Action<T> action, T state) => Post(() => action(state));

        public void Post(Action action)
        {
            lock (_posts)
            {
                _posts.Enqueue(action);
            }

            _posted.TrySetResult();
        }

        public void RunAll()
        {
            while (true)
            {
                Action next;
                lock (_posts)
                {
                    if (_posts.Count == 0)
                    {
                        return;
                    }

                    next = _posts.Dequeue();
                }

                next();
            }
        }
    }

    /// <summary>Runs posts inline, like <see cref="ImmediateDispatcher"/>, and counts them.</summary>
    private sealed class CountingDispatcher : IUiDispatcher
    {
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        /// <summary>Posts made with a state, rather than a closure.</summary>
        public int StatePosts => Volatile.Read(ref _statePosts);

        private int _statePosts;

        public void Post(Action action)
        {
            Interlocked.Increment(ref _posts);
            action();
        }

        public void Post<T>(Action<T> action, T state)
        {
            Interlocked.Increment(ref _posts);
            Interlocked.Increment(ref _statePosts);
            action(state);
        }
    }
}
