using NSubstitute;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed class RavenPanelViewModelTests
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

    public RavenPanelViewModelTests()
    {
        _catalog.List().Returns([Headset, Desk]);
        _catalog.Default().Returns(Headset);
        _models.IsPresent.Returns(true);
        _vocabulary.GetAsync(Arg.Any<CancellationToken>()).Returns(DictationVocabulary.Empty);
        _recorder.Stop().Returns(new RecordedClip(new float[32000], TimeSpan.FromSeconds(2)));
        Transcribes(Task.FromResult(new DictationResult("Hallo Raven, open Diffusion Nexus", TimeSpan.FromSeconds(2))));
        _dictation.WarmUpAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private void Transcribes(Task<DictationResult> result) =>
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(result);

    private RavenPanelViewModel NewVm()
    {
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance);
        vm.RefreshMicrophones();
        return vm;
    }

    private async Task HoldAsync(RavenPanelViewModel vm)
    {
        vm.PressMic();
        Speak();
        _time.Advance(Hold);
        await vm.ReleaseMicAsync();
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

    /// <summary>One block the way WASAPI hands it over on the RØDE Connect input: 10 ms.</summary>
    private void Block(float rms) => _recorder.BlockCaptured += Raise.Event<EventHandler<CapturedBlock>>(_recorder, new CapturedBlock(rms, TimeSpan.FromMilliseconds(10)));

    [Fact]
    public async Task A_hold_records_and_the_transcript_appears_as_my_turn()
    {
        var vm = NewVm();

        vm.PressMic();
        vm.State.ShouldBe(RavenState.Listening);
        vm.Caption.ShouldBe("Listening…");
        _recorder.Received(1).Start(Headset.Id);
        Speak();
        _time.Advance(Hold);
        await vm.ReleaseMicAsync();

        var last = vm.Log[^1];
        last.Kind.ShouldBe(RavenLogKind.You);
        last.Text.ShouldBe("Hallo Raven, open Diffusion Nexus");
        vm.State.ShouldBe(RavenState.Idle);
        vm.Caption.ShouldBe("Hold Ctrl+Shift+Space or the mic button to talk.");
        vm.Level.ShouldBe(0);
    }

    [Fact]
    public async Task A_quick_tap_latches_and_the_next_press_stops()
    {
        var vm = NewVm();

        vm.PressMic();
        await vm.ReleaseMicAsync();
        vm.State.ShouldBe(RavenState.Listening);
        Speak();
        vm.PressMic();

        vm.State.ShouldBe(RavenState.Idle);
        vm.Log.Count(l => l.Kind == RavenLogKind.You).ShouldBe(1);
    }

    [Fact]
    public async Task A_clip_shorter_than_half_a_second_is_dropped()
    {
        _recorder.Stop().Returns(new RecordedClip(new float[100], TimeSpan.FromMilliseconds(499)));
        var vm = NewVm();

        await HoldAsync(vm);

        vm.Log.ShouldBeEmpty();
        vm.State.ShouldBe(RavenState.Idle);
        await _dictation.DidNotReceive().TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
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
        var vm = NewVm();

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
        vm = NewVm();

        await HoldAsync(vm);

        seen.ShouldBe(["Downloading the speech model (1.6 GB)… 0%", "Downloading the speech model (1.6 GB)… 42%"]);
    }

    [Fact]
    public async Task A_failed_download_is_reported_and_retried_next_time()
    {
        _models.IsPresent.Returns(false);
        _models.DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("disk full")));
        var vm = NewVm();

        await HoldAsync(vm);

        vm.Log.Last().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Last().Text.ShouldBe("The speech model could not be downloaded: disk full. Press the mic to try again.");
        vm.State.ShouldBe(RavenState.Idle);

        await HoldAsync(vm);

        await _models.Received(2).DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_denied_microphone_explains_the_privacy_setting()
    {
        _recorder.When(r => r.Start(Arg.Any<string>())).Do(_ => throw new MicrophoneException(MicrophoneFailureKind.Denied, "denied"));
        var vm = NewVm();

        vm.PressMic();

        vm.Log.Last().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Last().Text.ShouldContain("Privacy & security");
        vm.State.ShouldBe(RavenState.Idle);
    }

    [Fact]
    public void An_unavailable_microphone_names_the_device()
    {
        _recorder.When(r => r.Start(Arg.Any<string>())).Do(_ => throw new MicrophoneException(MicrophoneFailureKind.Unavailable, "busy"));
        var vm = NewVm();

        vm.PressMic();

        vm.Log.Last().Text.ShouldBe("Headset could not be opened. Another app may be using it exclusively.");
    }

    [Fact]
    public async Task A_microphone_lost_while_recording_returns_to_idle_and_says_so()
    {
        var vm = NewVm();
        vm.PressMic();

        _recorder.Failed += Raise.Event<EventHandler<MicrophoneException>>(_recorder, new MicrophoneException(MicrophoneFailureKind.Missing, "gone"));

        vm.Log.Last().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Last().Text.ShouldContain("Headset");
        vm.State.ShouldBe(RavenState.Idle);
        _recorder.Received(1).Stop();
        await _dictation.DidNotReceive().TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        vm.PressMic();
        vm.State.ShouldBe(RavenState.Listening);
    }

    [Fact]
    public void A_silent_microphone_is_warned_about_once()
    {
        var vm = NewVm();
        vm.PressMic();

        for (var i = 0; i < 300; i++) // 3 s
        {
            Block(0f);
        }

        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Single().Text.ShouldBe("No sound from Headset. Check that it isn't muted.");
    }

    // The recorder reports how long each block is. Assuming 50 ms blocks, the ten-millisecond ones of the RØDE input made
    // the two seconds of grace pass in 0.4 s.
    [Fact]
    public void The_silent_microphone_warning_counts_the_real_duration_of_the_blocks()
    {
        var vm = NewVm();
        vm.PressMic();

        for (var i = 0; i < 199; i++)
        {
            Block(0f);
        }

        vm.Log.ShouldBeEmpty("1.99 s of nothing is still inside the grace period");
        Block(0f);
        vm.Log.Single().Text.ShouldBe("No sound from Headset. Check that it isn't muted.");
    }

    [Fact]
    public void The_level_follows_the_microphone_while_listening()
    {
        var vm = NewVm();
        vm.PressMic();

        Block(0.1f);

        vm.Level.ShouldBe(AudioMath.LevelOf(0.1f));
        vm.Level.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Pressing_while_transcribing_does_nothing()
    {
        var pending = new TaskCompletionSource<DictationResult>();
        Transcribes(pending.Task);
        var vm = NewVm();
        vm.PressMic();
        Speak();
        _time.Advance(Hold);
        var release = vm.ReleaseMicAsync();
        vm.State.ShouldBe(RavenState.Transcribing);

        vm.PressMic();

        vm.State.ShouldBe(RavenState.Transcribing);
        _recorder.Received(1).Start(Arg.Any<string>());
        pending.SetResult(new DictationResult("done", TimeSpan.FromSeconds(2)));
        await release;
        vm.State.ShouldBe(RavenState.Idle);
    }

    [Fact]
    public void An_unplugged_selected_microphone_falls_back_to_the_default_with_a_note()
    {
        var vm = NewVm();
        vm.SelectedMicrophone = Desk;
        _catalog.List().Returns([Headset]);

        _catalog.DevicesChanged += Raise.Event<EventHandler>(_catalog, EventArgs.Empty);

        vm.SelectedMicrophone.ShouldBe(Headset);
        vm.Microphones.ShouldBe([Headset]);
        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Note);
        vm.Log.Single().Text.ShouldBe("Desk mic is gone. Using Headset.");
    }

    [Fact]
    public void A_fallback_keeps_the_preferred_microphone_and_selects_it_again_when_it_comes_back()
    {
        var vm = NewVm();
        vm.SelectedMicrophone = Desk;
        vm.PreferredMicrophone.ShouldBe(Desk, "a pick is the user's choice");
        _catalog.List().Returns([Headset]);
        _catalog.DevicesChanged += Raise.Event<EventHandler>(_catalog, EventArgs.Empty);
        vm.SelectedMicrophone.ShouldBe(Headset);
        vm.PreferredMicrophone.ShouldBe(Desk, "a fallback is not a choice");

        _catalog.DevicesChanged += Raise.Event<EventHandler>(_catalog, EventArgs.Empty); // another device comes or goes
        _catalog.List().Returns([Headset, Desk]);
        _catalog.DevicesChanged += Raise.Event<EventHandler>(_catalog, EventArgs.Empty);

        vm.SelectedMicrophone.ShouldBe(Desk);
        vm.Log.Select(e => e.Text).ShouldBe(["Desk mic is gone. Using Headset.", "Using Desk mic again."]);
    }

    [Fact]
    public void A_list_bound_control_clearing_the_selection_during_a_refresh_changes_neither_choice()
    {
        var vm = NewVm();
        vm.SelectedMicrophone = Desk;
        // What a TwoWay-bound ComboBox does when its items are cleared: it writes null back.
        vm.Microphones.CollectionChanged += (_, _) => vm.SelectedMicrophone = null;

        vm.RefreshMicrophones();

        vm.SelectedMicrophone.ShouldBe(Desk);
        vm.PreferredMicrophone.ShouldBe(Desk);
        vm.Log.ShouldBeEmpty();
    }

    [Fact]
    public void A_microphone_with_a_new_id_is_selected_again_without_a_note()
    {
        var vm = NewVm();
        vm.SelectedMicrophone = Desk;
        var moved = new MicrophoneDevice("id-desk-2", "Desk mic");
        _catalog.List().Returns([Headset, moved]);

        vm.RefreshMicrophones();

        vm.SelectedMicrophone.ShouldBe(moved);
        vm.Log.ShouldBeEmpty();
    }

    [Fact]
    public void No_microphone_at_all_warns_on_press()
    {
        _catalog.List().Returns([]);
        _catalog.Default().Returns((MicrophoneDevice?)null);
        var vm = NewVm();

        vm.PressMic();

        vm.SelectedMicrophone.ShouldBeNull();
        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Single().Text.ShouldBe("No microphone found. Plug one in or check Windows sound settings.");
        vm.State.ShouldBe(RavenState.Idle);
        _recorder.DidNotReceive().Start(Arg.Any<string>());
    }

    [Fact]
    public void Typed_text_becomes_my_turn_and_the_box_clears()
    {
        var vm = NewVm();
        vm.TypedText = "  open the yard ";

        vm.SubmitTypedCommand.Execute(null);

        vm.Log.Single().Kind.ShouldBe(RavenLogKind.You);
        vm.Log.Single().Text.ShouldBe("open the yard");
        vm.TypedText.ShouldBe("");
    }

    [Fact]
    public void Blank_typed_text_is_ignored()
    {
        var vm = NewVm();
        vm.TypedText = "   ";

        vm.SubmitTypedCommand.Execute(null);

        vm.Log.ShouldBeEmpty();
    }

    [Fact]
    public void Collapsing_the_panel_does_not_stop_a_recording()
    {
        var vm = NewVm();
        vm.PressMic();

        vm.TogglePanelCommand.Execute(null);

        vm.IsOpen.ShouldBeFalse();
        vm.State.ShouldBe(RavenState.Listening);
        _recorder.DidNotReceive().Stop();
    }

    [Fact]
    public void A_recording_stops_by_itself_after_two_minutes()
    {
        var vm = NewVm();
        vm.PressMic();
        Speak();

        _time.Advance(TimeSpan.FromSeconds(119));
        vm.State.ShouldBe(RavenState.Listening);
        _time.Advance(TimeSpan.FromSeconds(1));

        vm.State.ShouldBe(RavenState.Idle);
        vm.Log.Last().Kind.ShouldBe(RavenLogKind.You);
        vm.PressMic();
        vm.State.ShouldBe(RavenState.Listening);
    }

    [Fact]
    public async Task A_load_failure_names_the_reason_and_how_to_download_the_model_again()
    {
        _models.ModelPath.Returns(@"c:\m\ggml.bin");
        var load = new DictationModelLoadException(@"c:\m\ggml.bin", "Vulkan", new InvalidOperationException("out of memory."));
        Transcribes(Task.FromException<DictationResult>(load));
        var vm = NewVm();

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
        var vm = NewVm();

        await HoldAsync(vm);

        vm.Log.ShouldBeEmpty();
        vm.State.ShouldBe(RavenState.Idle);
    }

    [Fact]
    public async Task The_two_minute_timer_is_cancelled_by_a_manual_stop()
    {
        var vm = NewVm();

        await HoldAsync(vm);
        _time.Advance(TimeSpan.FromSeconds(120));

        _recorder.Received(1).Stop();
        vm.Log.Count(l => l.Kind == RavenLogKind.You).ShouldBe(1);
    }

    [Fact]
    public async Task The_silent_microphone_warning_is_armed_again_for_a_second_recording()
    {
        var vm = NewVm();
        vm.PressMic();
        for (var i = 0; i < 300; i++) // 3 s
        {
            Block(0f);
        }

        _time.Advance(Hold);
        await vm.ReleaseMicAsync();
        vm.PressMic();
        for (var i = 0; i < 300; i++) // 3 s
        {
            Block(0f);
        }

        vm.Log.Count(l => l.Text.StartsWith("No sound", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact]
    public void A_capture_failure_lists_the_microphones_again()
    {
        var vm = NewVm();
        vm.PressMic();
        _catalog.ClearReceivedCalls();

        _recorder.Failed += Raise.Event<EventHandler<MicrophoneException>>(_recorder, new MicrophoneException(MicrophoneFailureKind.Missing, "gone"));

        _catalog.Received(1).List();
    }

    [Fact]
    public async Task Starting_a_recording_warms_the_model_up()
    {
        var warmed = new TaskCompletionSource();
        _dictation.WarmUpAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            warmed.TrySetResult();
            return Task.CompletedTask;
        });
        var vm = NewVm();

        vm.PressMic();

        await WithinAsync(warmed.Task);
    }

    // Loading the model takes seconds. Whatever the warm-up does on the calling thread, the press must not wait for it.
    [Fact]
    public async Task A_warm_up_that_blocks_does_not_hold_up_the_press_and_a_tap_still_latches()
    {
        using var hold = new ManualResetEventSlim();
        var entered = new TaskCompletionSource();
        _dictation.WarmUpAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            hold.Wait(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        });
        var vm = NewVm();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        vm.PressMic();
        await vm.ReleaseMicAsync();

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        vm.State.ShouldBe(RavenState.Listening, "a quick tap latches");
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
        var vm = NewVm();
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
        var vm = NewVm();

        vm.ScheduleWarmUp();
        _time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        await _dictation.DidNotReceive().WarmUpAsync(Arg.Any<CancellationToken>());
    }

    // Whisper makes up words on a clip with nobody talking ("Oh.", or the vocabulary read back).
    [Fact]
    public async Task A_quiet_clip_is_not_transcribed_and_says_so()
    {
        var vm = NewVm();
        vm.PressMic();
        RoomNoise(3);
        Speak(0.4); // a cough is not dictation
        _time.Advance(Hold);

        await vm.ReleaseMicAsync();

        await _dictation.DidNotReceive().TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Note);
        vm.Log.Single().Text.ShouldBe("I didn't hear anything.");
        vm.State.ShouldBe(RavenState.Idle);
        vm.Caption.ShouldBe(RavenPanelViewModel.IdleCaption);
    }

    [Fact]
    public async Task A_clip_with_speech_between_pauses_is_transcribed()
    {
        var vm = NewVm();
        vm.PressMic();
        RoomNoise(1);
        Speak(0.3);
        RoomNoise(0.5);
        Speak(0.3);
        _time.Advance(Hold);

        await vm.ReleaseMicAsync();

        vm.Log.Single().Kind.ShouldBe(RavenLogKind.You);
    }

    [Fact]
    public async Task A_quiet_clip_does_not_start_the_model_download()
    {
        _models.IsPresent.Returns(false);
        var vm = NewVm();
        vm.PressMic();
        RoomNoise(2);
        _time.Advance(Hold);

        await vm.ReleaseMicAsync();

        await _models.DidNotReceive().DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_speech_of_one_recording_does_not_count_for_the_next()
    {
        var vm = NewVm();
        await HoldAsync(vm);
        vm.PressMic();
        RoomNoise(1);
        _time.Advance(Hold);

        await vm.ReleaseMicAsync();

        vm.Log.Last().Text.ShouldBe("I didn't hear anything.");
        await _dictation.Received(1).TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
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
        vm = NewVm();

        await HoldAsync(vm);

        caption.ShouldBe("Downloading the speech model…");
    }

    // A stopped Windows audio service makes the device enumeration throw a COMException. The app must still start.
    [Fact]
    public void Windows_audio_being_unavailable_warns_and_leaves_no_microphones()
    {
        _catalog.List().Returns(_ => throw new System.Runtime.InteropServices.COMException("The audio service is not running."));

        var vm = NewVm();

        vm.Microphones.ShouldBeEmpty();
        vm.SelectedMicrophone.ShouldBeNull();
        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Single().Text.ShouldBe("Windows audio is not available: The audio service is not running.");
    }

    [Fact]
    public void Windows_audio_failing_on_the_default_device_is_caught_too()
    {
        var vm = NewVm();
        vm.SelectedMicrophone = Desk;
        _catalog.Default().Returns(_ => throw new System.Runtime.InteropServices.COMException("gone"));

        vm.RefreshMicrophones();

        vm.Microphones.ShouldBeEmpty();
        vm.SelectedMicrophone.ShouldBeNull();
        vm.PreferredMicrophone.ShouldBe(Desk, "the user's choice outlives the audio service");
        vm.Log.Single().Text.ShouldBe("Windows audio is not available: gone");
    }

    [Fact]
    public void A_device_change_with_no_devices_clears_the_selection()
    {
        var vm = NewVm();
        vm.SelectedMicrophone.ShouldNotBeNull();
        _catalog.List().Returns([]);
        _catalog.Default().Returns((MicrophoneDevice?)null);

        _catalog.DevicesChanged += Raise.Event<EventHandler>(_catalog, EventArgs.Empty);

        vm.SelectedMicrophone.ShouldBeNull();
        vm.Microphones.ShouldBeEmpty();
    }

    [Fact]
    public void A_lost_microphone_warning_has_the_exact_text()
    {
        var vm = NewVm();
        vm.PressMic();

        _recorder.Failed += Raise.Event<EventHandler<MicrophoneException>>(_recorder, new MicrophoneException(MicrophoneFailureKind.Missing, "gone"));

        vm.Log.Last().Text.ShouldBe("Headset is not available any more.");
    }
}
