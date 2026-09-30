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
        _time.Advance(Hold);
        await vm.ReleaseMicAsync();
    }

    private void Block(float rms) => _recorder.BlockCaptured += Raise.Event<EventHandler<float>>(_recorder, rms);

    [Fact]
    public async Task A_hold_records_and_the_transcript_appears_as_my_turn()
    {
        var vm = NewVm();

        vm.PressMic();
        vm.State.ShouldBe(RavenState.Listening);
        vm.Caption.ShouldBe("Listening…");
        _recorder.Received(1).Start(Headset.Id);
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

        for (var i = 0; i < 60; i++)
        {
            Block(0f);
        }

        vm.Log.Single().Kind.ShouldBe(RavenLogKind.Warning);
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

        _time.Advance(TimeSpan.FromSeconds(119));
        vm.State.ShouldBe(RavenState.Listening);
        _time.Advance(TimeSpan.FromSeconds(1));

        vm.State.ShouldBe(RavenState.Idle);
        vm.Log.Last().Kind.ShouldBe(RavenLogKind.You);
        vm.PressMic();
        vm.State.ShouldBe(RavenState.Listening);
    }

    [Fact]
    public async Task A_load_failure_names_the_reason_without_suggesting_a_smaller_model()
    {
        var load = new DictationModelLoadException(@"c:\m\ggml.bin", "Vulkan", new InvalidOperationException("out of memory"));
        Transcribes(Task.FromException<DictationResult>(load));
        var vm = NewVm();

        await HoldAsync(vm);

        vm.Log.Last().Kind.ShouldBe(RavenLogKind.Warning);
        vm.Log.Last().Text.ShouldBe("The speech model could not be loaded: out of memory");
        vm.Log.Last().Text.ShouldNotContain("smaller model");
        vm.State.ShouldBe(RavenState.Idle);
    }
}
