using System.Net.Http;
using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Infrastructure;
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

    private async Task<RavenPanelViewModel> NewOpenMicVmAsync(ReplyVoice? voice = null, IUiDispatcher? dispatcher = null)
    {
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, voice ?? _voice, _speech,
            dispatcher ?? new ImmediateDispatcher(), _time, NullLogger<RavenPanelViewModel>.Instance, openMic: _openMic);
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
        var warning = vm.Log.Last();
        warning.Kind.ShouldBe(RavenLogKind.Warning);
        warning.Text.ShouldContain("smart-turn");
        warning.Text.ShouldEndWith(". Back to push to talk for now; Open mic is tried again at the next launch. Click Push to talk to stop trying Open mic.");
        warning.Text.ShouldNotContain("next time", Case.Insensitive, "one retry promise: the fallback's");
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

        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.You && e.Text == "Open Diffusion Nexus", "Raven's name is cut off");
        _brain.Asked.ShouldContain(q => q.Contains("Open Diffusion Nexus") && !q.Contains("Raven"));
        vm.State.ShouldBe(RavenState.Attending);
    }

    // #217: the TV was taken for the user
    [Theory]
    [InlineData("At least someone's happy I'm home.")]
    [InlineData("Yes.")]
    [InlineData("Chat three.")]
    public async Task An_Open_mic_turn_without_Ravens_name_is_dropped_unnoted(string said)
    {
        Transcribes(Task.FromResult(new DictationResult(said, TimeSpan.FromSeconds(1))));
        var vm = await InOpenMicAsync();
        var before = vm.Log.Count;
        var chat = vm.CurrentChat;

        _openMic.Speak();
        _openMic.EndTurn();
        await WithinAsync(vm.PendingTranscriptions);

        vm.Log.Count.ShouldBe(before);
        vm.CurrentChat.ShouldBe(chat);
        _brain.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task Soon_after_Ravens_answer_a_turn_needs_no_name_and_later_it_does()
    {
        var said = "Raven, what's waiting on me?";
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new DictationResult(said, TimeSpan.FromSeconds(1))));
        _brain.Answer = _ => [new BrainText("One chat waits.")];
        var vm = await InOpenMicAsync();

        await TurnAsync(vm);
        said = "And in chat seven?";
        _time.Advance(TimeSpan.FromSeconds(9)); // the turn began 7 s after the answer: 9 s, less its 2 s
        await TurnAsync(vm);
        said = "Wohnzimmer 100%.";
        _time.Advance(TimeSpan.FromSeconds(13));
        await TurnAsync(vm);

        _brain.Asked.Count.ShouldBe(2);
        _brain.Asked[1].ShouldContain("And in chat seven?");
        vm.Log.ShouldNotContain(e => e.Text.Contains("Wohnzimmer"));
    }

    [Fact]
    public async Task Ravens_name_alone_asks_nothing_and_the_next_words_need_no_name()
    {
        var said = "Hey Raven.";
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new DictationResult(said, TimeSpan.FromSeconds(1))));
        var vm = await InOpenMicAsync();
        var before = vm.Log.Count;

        await TurnAsync(vm);
        vm.Log.Count.ShouldBe(before);
        _brain.Asked.ShouldBeEmpty();

        said = "What's waiting on me?";
        _time.Advance(TimeSpan.FromSeconds(5));
        await TurnAsync(vm);
        _brain.Asked.ShouldHaveSingleItem().ShouldContain("What's waiting on me?");
    }

    [Fact]
    public async Task With_no_follow_up_every_turn_needs_the_name()
    {
        var said = "Raven, what's waiting on me?";
        _dictation.TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new DictationResult(said, TimeSpan.FromSeconds(1))));
        _brain.Answer = _ => [new BrainText("One chat waits.")];
        var vm = await InOpenMicAsync();
        vm.FollowUpSeconds = 0;

        await TurnAsync(vm);
        said = "Yes.";
        await TurnAsync(vm);

        _brain.Asked.ShouldHaveSingleItem();
    }

    /// <summary>One Open mic turn, transcribed and answered.</summary>
    private async Task TurnAsync(RavenPanelViewModel vm)
    {
        _openMic.Speak();
        _openMic.EndTurn();
        await WithinAsync(vm.PendingTranscriptions);
        await WithinAsync(vm.PendingAnswers);
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
        await Until(() => _openMic.Opening);

        _openMic.Fail();
        _openMic.StartGate.SetResult();
        await WithinAsync(opening);

        vm.State.ShouldBe(RavenState.AttendingPaused);
        vm.Log.Count(e => e.Kind == RavenLogKind.Warning).ShouldBe(1);
    }

    // PR #89 review: the silent-mic watch
    [Fact]
    public async Task A_microphone_that_goes_digitally_silent_in_Open_mic_is_warned_of_and_its_return_noted()
    {
        var vm = await InOpenMicAsync();

        HearFor(0.02f, seconds: 1); // heard
        HearFor(0f, seconds: 11); // a mute key: exact zeros
        vm.Log.Count(e => e.Kind == RavenLogKind.Warning && e.Text == $"{Headset.Name} stopped sending sound. Check that it isn't muted or gone to sleep.")
            .ShouldBe(1);

        HearFor(0.02f, seconds: 1);
        vm.Log.Last().Text.ShouldBe($"{Headset.Name} is sending sound again.");
    }

    // PR #89 review: Open mic runs for hours, so a second mute is warned of too
    [Fact]
    public async Task A_second_mute_in_Open_mic_is_warned_of_again()
    {
        var vm = await InOpenMicAsync();
        var dropped = $"{Headset.Name} stopped sending sound. Check that it isn't muted or gone to sleep.";

        HearFor(0.02f, seconds: 1);
        HearFor(0f, seconds: 11);
        HearFor(0.02f, seconds: 1);
        vm.Log.Last().Text.ShouldBe($"{Headset.Name} is sending sound again.");
        HearFor(0f, seconds: 11);

        vm.Log.Count(e => e.Kind == RavenLogKind.Warning && e.Text == dropped).ShouldBe(2);
    }

    // PR #89 review: the silent-mic watch
    [Fact]
    public async Task A_microphone_that_starts_late_in_Open_mic_replaces_the_No_sound_warning()
    {
        var vm = await InOpenMicAsync();

        HearFor(0f, seconds: 3);
        HearFor(0.02f, seconds: 1); // the Bluetooth headset has switched to its microphone

        vm.Log.ShouldNotContain(e => e.Text.StartsWith("No sound from"));
        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.Note && e.Text.StartsWith($"{Headset.Name} took a moment to start sending sound."));
    }

    // PR #89 review: each start watches afresh
    [Fact]
    public async Task Each_start_of_Open_mic_watches_the_microphone_afresh()
    {
        var vm = await InOpenMicAsync();
        HearFor(0f, seconds: 3);

        await vm.TapMic(TalkInput.MicButton); // pause
        await vm.TapMic(TalkInput.MicButton); // resume
        await WithinAsync(vm.PendingOpenMic);
        HearFor(0f, seconds: 3);

        vm.Log.Count(e => e.Kind == RavenLogKind.Warning && e.Text.StartsWith("No sound from")).ShouldBe(2);
    }

    // PR #89 review: only a bad model file is downloaded again
    [Fact]
    public async Task A_listener_that_will_not_start_for_another_reason_goes_back_to_push_to_talk_without_promising_a_download()
    {
        _openMic.StartFails = new DllNotFoundException("onnxruntime.dll was not found");
        var vm = await NewOpenMicVmAsync();

        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);

        vm.MicMode.ShouldBe(MicMode.PushToTalk);
        var warning = vm.Log.Last();
        warning.Kind.ShouldBe(RavenLogKind.Warning);
        warning.Text.ShouldBe("Open mic could not start: onnxruntime.dll was not found. "
            + "Back to push to talk for now; Open mic is tried again at the next launch. Click Push to talk to stop trying Open mic.");
        warning.Text.ShouldNotContain("downloaded");
    }

    // PR #89 review: Open mic's wording
    [Fact]
    public async Task A_failed_speech_model_download_in_Open_mic_says_to_speak_again_not_to_press_the_mic()
    {
        _models.IsPresent.Returns(false);
        _models.DownloadAsync(Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("disk full")));
        var vm = await InOpenMicAsync();

        _openMic.Speak();
        _openMic.EndTurn();
        await WithinAsync(vm.PendingTranscriptions);

        vm.Log.Last().Text.ShouldBe("The speech model could not be downloaded: disk full. Speak again to try again.");
    }

    // PR #89 review: Open mic's wording
    [Fact]
    public async Task A_speech_model_that_will_not_load_in_Open_mic_is_downloaded_again_with_the_next_turn()
    {
        _models.ModelPath.Returns(@"c:\m\ggml.bin");
        Transcribes(Task.FromException<DictationResult>(
            new DictationModelLoadException(@"c:\m\ggml.bin", "Vulkan", new InvalidOperationException("out of memory."))));
        var vm = await InOpenMicAsync();

        _openMic.Speak();
        _openMic.EndTurn();
        await WithinAsync(vm.PendingTranscriptions);

        vm.Log.Last().Text.ShouldBe(@"The speech model could not be loaded: out of memory. Delete c:\m\ggml.bin; it is downloaded again with your next turn.");
    }

    // PR #89 review: runs
    [Fact]
    public async Task What_an_old_run_raises_after_Open_mic_moved_on_is_ignored()
    {
        var vm = await InOpenMicAsync();
        var old = _openMic.Run!;
        vm.SelectedMicrophone = Desk;
        await WithinAsync(vm.PendingOpenMic);
        var before = vm.Log.Count;

        _openMic.Speak(old);
        _openMic.EndTurn(old);
        _openMic.Fail(old);

        vm.State.ShouldBe(RavenState.Attending);
        vm.Log.Count.ShouldBe(before);
        _openMic.Listening.ShouldBe(Desk.Id);
        await _dictation.DidNotReceive().TranscribeAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<DictationVocabulary>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Batches of 50 ms at one level for <paramref name="seconds"/>, as the listener raises them.</summary>
    private void HearFor(float rms, double seconds)
    {
        for (var i = 0; i < (int)(seconds * 20); i++)
        {
            _openMic.Hear(rms);
        }
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
        _time.Advance(TrafficWatcher.NewsGrace);
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

    // Fourth review of #89: some 20 batches a second are posted without a closure
    [Fact]
    public async Task A_heard_batch_is_posted_with_its_state_and_reaches_the_orb()
    {
        var dispatcher = new CountingDispatcher();
        var vm = await NewOpenMicVmAsync(dispatcher: dispatcher);
        vm.MicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);
        var before = dispatcher.StatePosts;

        _openMic.Hear(0.2f);

        dispatcher.StatePosts.ShouldBe(before + 1);
        vm.Level.ShouldBeGreaterThan(0);
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

    // Second review of #89: a stale start fails into the log only
    [Fact]
    public async Task A_start_that_fails_after_the_user_paused_is_only_logged()
    {
        var vm = await NewOpenMicVmAsync();
        _openMic.StartGate = new TaskCompletionSource();
        _openMic.StartFails = new DllNotFoundException("onnxruntime.dll was not found");
        vm.MicMode = MicMode.OpenMic;
        var opening = vm.PendingOpenMic;
        await Until(() => _openMic.Opening);

        await vm.TapMic(TalkInput.MicButton); // pause while it opens
        _openMic.StartGate.SetResult();
        await WithinAsync(opening);

        vm.MicMode.ShouldBe(MicMode.OpenMic);
        vm.State.ShouldBe(RavenState.AttendingPaused);
        vm.Log.ShouldNotContain(e => e.Kind == RavenLogKind.Warning);
    }

    // Second review of #89: a stale start fails into the log only
    [Fact]
    public async Task A_microphone_that_fails_to_open_after_the_user_switched_away_is_not_warned_of()
    {
        var vm = await NewOpenMicVmAsync();
        _openMic.StartGate = new TaskCompletionSource();
        _openMic.StartFails = new MicrophoneException(MicrophoneFailureKind.Missing, "gone", new Exception());
        vm.MicMode = MicMode.OpenMic;
        var opening = vm.PendingOpenMic;
        await Until(() => _openMic.Opening);

        vm.MicMode = MicMode.PushToTalk;
        _openMic.StartGate.SetResult();
        await WithinAsync(opening);

        vm.MicMode.ShouldBe(MicMode.PushToTalk);
        vm.State.ShouldBe(RavenState.Idle);
        vm.Log.ShouldNotContain(e => e.Kind == RavenLogKind.Warning);
    }

    // Second review of #89: a fallback is not the user's choice
    [Fact]
    public async Task A_failed_download_falls_back_to_push_to_talk_but_the_choice_stays_Open_mic()
    {
        _openMic.ModelsPresent = false;
        _openMic.DownloadFails = new HttpRequestException("no network."); // HttpClient's messages end with a period
        var vm = await NewOpenMicVmAsync();

        vm.ChooseMicModeCommand.Execute(MicMode.OpenMic);
        await WithinAsync(vm.PendingOpenMic);

        vm.MicMode.ShouldBe(MicMode.PushToTalk);
        vm.PreferredMicMode.ShouldBe(MicMode.OpenMic);
        vm.Log.Last().Text.ShouldBe("Open mic's models could not be downloaded: no network. "
            + "Back to push to talk for now; Open mic is tried again at the next launch. Click Push to talk to stop trying Open mic.");

        _openMic.DownloadFails = null;
        vm.ChooseMicModeCommand.Execute(MicMode.OpenMic); // the user tries again: the choice has not changed, the mode does
        await WithinAsync(vm.PendingOpenMic);
        vm.MicMode.ShouldBe(MicMode.OpenMic);
        _openMic.Listening.ShouldBe(Headset.Id);
    }

    // Second review of #89: a fallback is not the user's choice
    [Fact]
    public async Task The_user_s_switch_is_their_choice_and_a_stored_choice_sets_the_mode()
    {
        var vm = await NewOpenMicVmAsync();

        vm.PreferredMicMode = MicMode.OpenMic; // as the shell restores it
        await WithinAsync(vm.PendingOpenMic);
        vm.MicMode.ShouldBe(MicMode.OpenMic);

        vm.ChooseMicModeCommand.Execute(MicMode.PushToTalk);
        await WithinAsync(vm.PendingOpenMic);
        vm.PreferredMicMode.ShouldBe(MicMode.PushToTalk);
        vm.MicMode.ShouldBe(MicMode.PushToTalk);
    }

    // Third review of #89: after a fallback, a click on the Push to talk already shown is the user's choice
    [Fact]
    public async Task After_a_fallback_choosing_Push_to_talk_keeps_it_as_the_choice()
    {
        _openMic.StartFails = new DllNotFoundException("onnxruntime.dll was not found");
        var vm = await NewOpenMicVmAsync();
        vm.PreferredMicMode = MicMode.OpenMic; // as the shell restores it
        await WithinAsync(vm.PendingOpenMic);
        vm.MicMode.ShouldBe(MicMode.PushToTalk);

        vm.ChooseMicModeCommand.Execute(MicMode.PushToTalk); // the half already checked

        vm.PreferredMicMode.ShouldBe(MicMode.PushToTalk);
        vm.MicMode.ShouldBe(MicMode.PushToTalk);
        _openMic.Started.ShouldBeEmpty();
    }

    // Third review of #89: after a fallback, a click on Open mic tries again
    [Fact]
    public async Task After_a_fallback_choosing_Open_mic_starts_it_again()
    {
        _openMic.StartFails = new DllNotFoundException("onnxruntime.dll was not found");
        var vm = await NewOpenMicVmAsync();
        vm.PreferredMicMode = MicMode.OpenMic;
        await WithinAsync(vm.PendingOpenMic);

        _openMic.StartFails = null;
        vm.ChooseMicModeCommand.Execute(MicMode.OpenMic);
        await WithinAsync(vm.PendingOpenMic);

        vm.MicMode.ShouldBe(MicMode.OpenMic);
        _openMic.Listening.ShouldBe(Headset.Id);
    }

    // Second review of #89: the Speaking caption tells what interrupts
    [Fact]
    public async Task While_Raven_speaks_in_push_to_talk_the_caption_says_talk_to_interrupt()
    {
        var player = new HoldingPlayer();
        using var voice = _speech.NewVoice(player);
        _brain.Answer = _ => [new BrainText("You have one chat waiting.")];
        var vm = await NewOpenMicVmAsync(voice);

        Type(vm, "What's waiting on me?");
        await Until(() => vm.State == RavenState.Speaking);

        vm.Caption.ShouldBe("Speaking… Talk to interrupt.");
    }

    // Second review of #89: the Speaking caption tells what interrupts
    [Fact]
    public async Task While_Raven_speaks_in_Open_mic_the_caption_says_talk_to_interrupt_only_while_talking_does()
    {
        var player = new HoldingPlayer();
        using var voice = _speech.NewVoice(player);
        _brain.Answer = _ => [new BrainText("You have one chat waiting.")];
        var vm = await InOpenMicAsync(voice);

        Type(vm, "What's waiting on me?");
        await Until(() => vm.State == RavenState.Speaking);
        vm.Caption.ShouldBe("Speaking… Talk to interrupt.");

        vm.BargeIn = false; // speech is ignored while Raven speaks
        vm.Caption.ShouldBe("Speaking… Type to interrupt.");

        vm.BargeIn = true;
        vm.Caption.ShouldBe("Speaking… Talk to interrupt.");
    }

    // Second review of #89: the Speaking caption tells what interrupts
    [Fact]
    public async Task While_Raven_speaks_with_Open_mic_paused_the_caption_says_type_to_interrupt()
    {
        var player = new HoldingPlayer();
        using var voice = _speech.NewVoice(player);
        _brain.Answer = _ => [new BrainText("You have one chat waiting.")];
        var vm = await InOpenMicAsync(voice);
        await vm.TapMic(TalkInput.MicButton); // pause: a press only pauses or resumes
        await WithinAsync(vm.PendingOpenMic);

        Type(vm, "What's waiting on me?");
        await Until(() => vm.State == RavenState.Speaking);

        vm.Caption.ShouldBe("Speaking… Type to interrupt.");
    }

    // Second review of #89: a failed startup listing pauses a waiting Open mic
    [Fact]
    public async Task Open_mic_waiting_for_a_listing_that_fails_pauses_and_a_press_tries_again()
    {
        Exception? failure = new System.Runtime.InteropServices.COMException("The audio service is not running.");
        _catalog.List().Returns(_ => failure is null ? [Headset, Desk] : throw failure);
        var vm = new RavenPanelViewModel(_catalog, _recorder, _dictation, _models, _vocabulary, _brain, _voice, _speech,
            new ImmediateDispatcher(), _time, NullLogger<RavenPanelViewModel>.Instance, openMic: _openMic);

        vm.MicMode = MicMode.OpenMic; // the stored mode, set before the first listing
        await WithinAsync(vm.RefreshMicrophonesAsync());
        await WithinAsync(vm.PendingOpenMic);

        vm.State.ShouldBe(RavenState.AttendingPaused);
        vm.Caption.ShouldBe("Open mic paused");
        vm.Log.Single().Text.ShouldBe("Windows audio is not available: The audio service is not running.");

        failure = null; // the service is back
        await WithinAsync(vm.RefreshMicrophonesAsync());
        await vm.TapMic(TalkInput.MicButton);
        await WithinAsync(vm.PendingOpenMic);
        _openMic.Listening.ShouldBe(Headset.Id);
    }

    // Second review of #89: one click in a batch is not 50 ms of sound
    [Fact]
    public async Task Digital_zeros_with_a_click_in_every_batch_in_Open_mic_are_still_no_sound()
    {
        var vm = await InOpenMicAsync();

        for (var i = 0; i < 100; i++) // 5 s of 50 ms batches of zeros, each with one loud 10 ms block (a click)
        {
            _openMic.Hear(0.2f, quietest: 0f);
        }

        vm.Log.Count(e => e.Kind == RavenLogKind.Warning && e.Text.StartsWith("No sound from")).ShouldBe(1);
    }

    // Third review of #89: Raven's voice must not end up in the user's turn
    [Fact]
    public async Task An_answer_that_arrives_while_the_user_is_talking_in_Open_mic_is_only_written()
    {
        var transcript = new TaskCompletionSource<DictationResult>();
        Transcribes(transcript.Task);
        _brain.Answer = _ => [new BrainText("You have one chat waiting.")];
        var vm = await InOpenMicAsync();
        _openMic.Speak();
        _openMic.EndTurn(); // the first question is still being transcribed

        _openMic.Speak(); // the user's next turn has started
        transcript.SetResult(new DictationResult("Raven, what's waiting on me?", TimeSpan.FromSeconds(1)));
        await WithinAsync(vm.PendingTranscriptions);
        await WithinAsync(vm.PendingAnswers);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        vm.Log.ShouldContain(e => e.Kind == RavenLogKind.Raven && e.Text == "You have one chat waiting.");
        _speech.Spoken.ShouldBeEmpty("the user is talking");
        vm.State.ShouldBe(RavenState.Listening);
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

        public void Enqueue(SpeechChunk chunk, Func<bool>? hushed = null)
        {
        }

        public void Stop() => Interlocked.Increment(ref _stops);

        public void Dispose()
        {
        }
    }
}
