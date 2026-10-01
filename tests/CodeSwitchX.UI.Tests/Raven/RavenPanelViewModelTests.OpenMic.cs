using System.Net.Http;
using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Listening;
using CodeSwitchX.Voice.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>Open mic: speech hushes Raven, a finished turn is transcribed and asked, and the mic button pauses.</summary>
public sealed partial class RavenPanelViewModelTests
{
    private readonly FakeOpenMic _openMic = new();

    private async Task<RavenPanelViewModel> NewOpenMicVmAsync(ReplyVoice? voice = null)
    {
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, voice ?? _voice, _speech,
            new ImmediateDispatcher(), _time, NullLogger<RavenPanelViewModel>.Instance, openMic: _openMic);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        return vm;
    }

    private async Task<RavenPanelViewModel> InOpenMicAsync(ReplyVoice? voice = null)
    {
        var vm = await NewOpenMicVmAsync(voice);
        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);
        return vm;
    }

    [Fact]
    public async Task Switching_to_Open_mic_listens_on_the_selected_microphone_and_the_orb_waits()
    {
        var vm = await InOpenMicAsync();

        _openMic.Listening.ShouldBe(Headset.Id);
        vm.State.ShouldBe(RavenState.Attending);
        vm.Caption.ShouldBe("Open mic");
    }

    [Fact]
    public async Task Switching_back_to_push_to_talk_stops_listening()
    {
        var vm = await InOpenMicAsync();

        vm.MicMode = MicMode.PushToTalk;
        await WithinAsync(vm.PendingOpenMic);

        _openMic.Listening.ShouldBeNull();
        vm.State.ShouldBe(RavenState.Idle);
    }

    [Fact]
    public async Task Missing_models_are_downloaded_first_and_a_failed_download_goes_back_to_push_to_talk()
    {
        _openMic.ModelsPresent = false;
        _openMic.DownloadFails = new HttpRequestException("no network");
        var vm = await NewOpenMicVmAsync();

        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);

        _openMic.Downloads.ShouldBe(1);
        vm.MicMode.ShouldBe(MicMode.PushToTalk);
        _openMic.Listening.ShouldBeNull();
        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.Warning && e.Text.Contains("no network"));
    }

    [Fact]
    public async Task A_model_that_will_not_load_goes_back_to_push_to_talk_with_a_warning()
    {
        _openMic.StartFails = new ListeningModelException(ListeningModelStore.SmartTurn, new Exception("bad file"));
        var vm = await NewOpenMicVmAsync();

        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);

        vm.MicMode.ShouldBe(MicMode.PushToTalk);
        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.Warning && e.Text.Contains("smart-turn"));
    }

    [Fact]
    public async Task The_mic_button_and_the_hotkey_pause_and_resume()
    {
        var vm = await InOpenMicAsync();

        await vm.TapMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingOpenMic);
        _openMic.Listening.ShouldBeNull();
        vm.State.ShouldBe(RavenState.AttendingPaused);
        vm.Caption.ShouldBe("Open mic paused");
        vm.MicButtonName.ShouldBe("Resume Open mic");

        vm.PressMic(TalkInput.Hotkey);
        await vm.ReleaseMicAsync(TalkInput.Hotkey);
        await WithinAsync(vm.PendingOpenMic);
        _openMic.Listening.ShouldBe(Headset.Id);
        vm.State.ShouldBe(RavenState.Attending);
        vm.MicButtonName.ShouldBe("Pause Open mic");
        _recorder.DidNotReceive().Start(Arg.Any<string>());
    }

    [Fact]
    public async Task Speech_hushes_Raven_and_a_finished_turn_is_transcribed_and_asked()
    {
        var vm = await InOpenMicAsync();

        _openMic.Speak();
        vm.State.ShouldBe(RavenState.Listening);
        vm.Caption.ShouldBe("Listening…");
        _openMic.EndTurn();
        await WithinAsync(vm.PendingTranscriptions);
        await WithinAsync(vm.PendingAnswers);

        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.You && e.Text == "Hallo Raven, open Diffusion Nexus");
        _brain.Asked.ShouldContain(q => q.Contains("open Diffusion Nexus"));
        vm.State.ShouldBe(RavenState.Attending);
    }

    [Fact]
    public async Task An_empty_transcript_in_Open_mic_is_not_noted_in_the_log()
    {
        Transcribes(Task.FromResult(new DictationResult("", TimeSpan.FromSeconds(1))));
        var vm = await InOpenMicAsync();
        var before = vm.Log.Count;

        _openMic.Speak();
        _openMic.EndTurn();
        await WithinAsync(vm.PendingTranscriptions);

        vm.Log.Count.ShouldBe(before);
    }

    [Fact]
    public async Task With_barge_in_off_speech_is_ignored_while_Raven_speaks()
    {
        _brain.Answer = _ => [new BrainText("You have one chat waiting.")];
        var vm = await InOpenMicAsync();
        vm.BargeIn = false;

        Type(vm, "What's waiting on me?");
        await Until(() => vm.State == RavenState.Speaking);
        _openMic.IgnoreSpeech.ShouldBeTrue();

        vm.BargeIn = true;
        _openMic.IgnoreSpeech.ShouldBeFalse();
    }

    [Fact]
    public async Task A_microphone_that_sends_nothing_in_Open_mic_is_warned_of_once()
    {
        var vm = await InOpenMicAsync();

        for (var i = 0; i < 100; i++) // 5 s of digital silence, in 50 ms batches
        {
            _openMic.Hear(0f);
        }

        vm.Log.Count(e => e.Kind == RavenLogKind.Warning && e.Text.StartsWith("No sound from")).ShouldBe(1);
    }

    // Review focus 1
    [Fact]
    public async Task Open_mic_saved_from_last_time_starts_once_the_microphones_are_listed()
    {
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech,
            new ImmediateDispatcher(), _time, NullLogger<RavenPanelViewModel>.Instance, openMic: _openMic);

        vm.MicMode = MicMode.OpenMic; // the shell sets it from the settings before the list arrives
        await WithinAsync(vm.RefreshMicrophonesAsync());
        await WithinAsync(vm.PendingOpenMic);

        _openMic.Listening.ShouldBe(Headset.Id);
    }

    // Review focus 2
    [Fact]
    public async Task Picking_another_microphone_moves_Open_mic_to_it()
    {
        var vm = await InOpenMicAsync();

        vm.SelectedMicrophone = Desk;
        await WithinAsync(vm.PendingOpenMic);

        _openMic.Listening.ShouldBe(Desk.Id);
        _openMic.Started.ShouldBe([Headset.Id, Desk.Id]);
    }

    // Review focus 3
    [Fact]
    public async Task Pausing_mid_turn_drops_the_turn()
    {
        var vm = await InOpenMicAsync();
        _openMic.Speak();

        await vm.TapMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingOpenMic);

        vm.State.ShouldBe(RavenState.AttendingPaused);
        vm.Caption.ShouldBe("Open mic paused");
        await _dictation.DidNotReceive().TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>());
    }

    // Review focus 4
    [Fact]
    public async Task A_dead_microphone_warns_once_and_pauses_and_a_press_starts_it_again()
    {
        var vm = await InOpenMicAsync();

        _openMic.Fail();
        await WithinAsync(vm.PendingOpenMic);

        vm.MicMode.ShouldBe(MicMode.OpenMic);
        vm.State.ShouldBe(RavenState.AttendingPaused);
        vm.Log.Count(e => e.Kind == RavenLogKind.Warning).ShouldBe(1);

        await vm.TapMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingOpenMic);
        _openMic.Listening.ShouldBe(Headset.Id);
    }

    // Review focus 5
    [Fact]
    public async Task A_held_push_to_talk_recording_is_finished_when_the_user_switches_to_Open_mic()
    {
        var vm = await NewOpenMicVmAsync();
        vm.PressMic(TalkInput.Hotkey);
        await WithinAsync(vm.PendingStart);
        Speak();
        _time.Advance(Hold);

        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);
        await WithinAsync(vm.PendingTranscriptions);
        await vm.ReleaseMicAsync(TalkInput.Hotkey);

        _recorder.Received(1).Stop();
        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.You);
        _openMic.Listening.ShouldBe(Headset.Id, "the late release must not pause Open mic");
    }

    // Fix round 1, review 1
    [Fact]
    public async Task A_start_still_opening_when_paused_and_resumed_never_closes_the_newer_run()
    {
        var vm = await NewOpenMicVmAsync();
        _openMic.StartGate = new TaskCompletionSource();
        vm.MicMode = MicMode.OpenMic; // its Start blocks

        await vm.TapMic(TalkInput.MicButton); // pause
        await vm.TapMic(TalkInput.MicButton); // resume, while the first Start is still blocked
        _openMic.StartGate.SetResult();
        await WithinAsync(vm.PendingOpenMic);

        _openMic.Listening.ShouldBe(Headset.Id);
        vm.State.ShouldBe(RavenState.Attending);
    }

    // Fix round 1, review 3
    [Fact]
    public async Task A_microphone_picked_while_Open_mic_opens_is_the_one_it_listens_on()
    {
        var vm = await NewOpenMicVmAsync();
        _openMic.StartGate = new TaskCompletionSource();
        vm.MicMode = MicMode.OpenMic;
        var opening = vm.PendingOpenMic;

        vm.SelectedMicrophone = Desk;
        _openMic.StartGate.SetResult();
        await WithinAsync(opening);

        _openMic.Listening.ShouldBe(Desk.Id);
        vm.State.ShouldBe(RavenState.Attending);
    }

    // Fix round 1, review 5
    [Fact]
    public async Task A_microphone_that_fails_while_Open_mic_opens_pauses_it_with_one_warning()
    {
        var vm = await NewOpenMicVmAsync();
        _openMic.StartGate = new TaskCompletionSource();
        vm.MicMode = MicMode.OpenMic;
        var opening = vm.PendingOpenMic;

        _openMic.Fail();
        _openMic.StartGate.SetResult();
        await WithinAsync(opening);

        vm.State.ShouldBe(RavenState.AttendingPaused);
        vm.Log.Count(e => e.Kind == RavenLogKind.Warning).ShouldBe(1);
    }

    // Final review 1
    [Fact]
    public async Task Talking_over_Raven_in_Open_mic_stops_it()
    {
        var player = new HoldingPlayer();
        using var voice = _speech.NewVoice(player);
        _brain.Answer = _ => [new BrainText("You have one chat waiting.")];
        var vm = await InOpenMicAsync(voice);
        Type(vm, "What's waiting on me?");
        await Until(() => vm.State == RavenState.Speaking);
        var stops = player.Stops;

        _openMic.Speak();

        vm.State.ShouldBe(RavenState.Listening);
        voice.IsSpeaking.ShouldBeFalse();
        await Until(() => player.Stops > stops);
    }

    // Final review 1
    [Fact]
    public async Task Talking_in_Open_mic_stops_a_digest_being_told()
    {
        _teller.Gate = new TaskCompletionSource(); // the teller is still at it when the user talks
        _teller.Answer = _ => [new BrainText("ContentAutomatorX is done.")];
        _yard.Show("a", "ContentAutomatorX", "Fix the upload retry");
        var news = new ChatNews(_bus, _yard, _time, _ => "All done.");
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech, new ImmediateDispatcher(), _time,
            NullLogger<RavenPanelViewModel>.Instance, news, _teller, _openMic);
        await WithinAsync(vm.RefreshMicrophonesAsync());
        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);
        _time.Advance(TimeSpan.FromSeconds(1));
        Changes("a", SessionState.Working, SessionState.Idle);
        _time.Advance(RavenPanelViewModel.NewsGrace);
        await Until(() => _teller.Asked.Count == 1);

        _openMic.Speak();
        _teller.Gate.SetResult();
        await WithinAsync(vm.PendingAnswers);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _speech.Spoken.ShouldBeEmpty("the digest stopped when the user started talking");
        vm.State.ShouldBe(RavenState.Listening);
    }

    // Final review 3
    [Fact]
    public async Task Until_the_microphone_is_open_the_orb_does_not_say_Open_mic()
    {
        _openMic.ModelsPresent = false;
        _openMic.DownloadGate = new TaskCompletionSource();
        _openMic.StartGate = new TaskCompletionSource();
        var vm = await NewOpenMicVmAsync();

        vm.MicMode = MicMode.OpenMic;
        vm.State.ShouldBe(RavenState.Idle);
        vm.Caption.ShouldBe("Downloading Open mic's models…");

        _openMic.DownloadGate.SetResult();
        await Until(() => vm.Caption == "Starting Open mic…");
        vm.State.ShouldBe(RavenState.Idle);

        _openMic.StartGate.SetResult();
        await WithinAsync(vm.PendingOpenMic);
        vm.State.ShouldBe(RavenState.Attending);
        vm.Caption.ShouldBe("Open mic");
    }

    // Final review 7
    [Fact]
    public async Task A_turn_the_listener_lost_mid_way_leaves_Listening_quietly()
    {
        var vm = await InOpenMicAsync();
        var before = vm.Log.Count;
        _openMic.Speak();

        _openMic.LoseTurn();
        await WithinAsync(vm.PendingTranscriptions);

        vm.State.ShouldBe(RavenState.Attending);
        vm.Log.Count.ShouldBe(before);
        await _dictation.DidNotReceive().TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>());
    }

    // Final review 2
    [Fact]
    public async Task The_orb_level_changes_only_when_it_would_show()
    {
        var vm = await InOpenMicAsync();
        var changes = 0;
        vm.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(RavenPanelViewModel.Level) ? 1 : 0;

        _openMic.Hear(0.05f);
        for (var i = 0; i < 20; i++)
        {
            _openMic.Hear(0.05f + (i % 2 * 0.00001f)); // an idle room: the same level, give or take nothing
        }

        changes.ShouldBe(1);
    }

    /// <summary>A player whose audio never runs out: Raven speaks until something stops it.</summary>
    private sealed class HoldingPlayer : ISpeechPlayer
    {
        private int _stops;

        public int Stops => Volatile.Read(ref _stops);

        public TimeSpan Remaining => TimeSpan.FromSeconds(30);

        public event EventHandler<float>? LevelChanged
        {
            add { }
            remove { }
        }

        public void Enqueue(SpeechChunk chunk)
        {
        }

        public void Stop() => Interlocked.Increment(ref _stops);

        public void Dispose()
        {
        }
    }
}
