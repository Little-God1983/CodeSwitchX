using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Shell;
using CodeSwitchX.UI.Voice;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Settings;

/// <summary>Settings by voice (#126): read and changed as the Settings page and the panel do, and refused in words.</summary>
public sealed class AppSettingsTests
{
    private readonly ShellTestHarness _h = new();
    private readonly AppSettings _settings;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AppSettingsTests()
    {
        _settings = new AppSettings(() => _h.Shell, new ImmediateDispatcher(), _h.Chats);
    }

    [Theory]
    [InlineData("voice", SettingsPage.Voice)]
    [InlineData("the listening settings", SettingsPage.Listening)]
    [InlineData("Brain & chats", SettingsPage.Brain)]
    [InlineData("privacy", SettingsPage.Privacy)]
    [InlineData("claude code", SettingsPage.ClaudeCode)]
    public async Task Open_the_voice_settings_opens_settings_at_that_page(string said, SettingsPage page)
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        var shown = await _settings.OpenAsync(said, Ct);

        _h.Shell.Mode.ShouldBe(ShellMode.Settings);
        _h.Shell.Settings.Page.ShouldBe(page);
        shown.ShouldBe(SettingsPageItem.All.Single(p => p.Page == page).Title);
    }

    [Fact]
    public async Task A_page_settings_does_not_have_is_refused_with_the_pages()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        var error = await Should.ThrowAsync<YardActionException>(() => _settings.OpenAsync("colours", Ct));

        error.Message.ShouldContain("no page 'colours'");
        error.Message.ShouldContain("Listening");
        _h.Shell.Mode.ShouldNotBe(ShellMode.Settings);
    }

    [Fact]
    public async Task Is_open_mic_on_is_answered_and_turn_it_off_switches_the_panel_and_the_stored_setting()
    {
        _h.Settings.GetAsync<string?>(SettingKeys.RavenMicMode, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("OpenMic"));
        await _h.Shell.InitializeAsync(CancellationToken.None);

        (await _settings.GetAsync("open mic", Ct)).Value.ShouldBe("on");
        var set = await _settings.SetAsync("Open Mic", "off", Ct);

        set.Value.ShouldBe("off");
        _h.Shell.Raven.PreferredMicMode.ShouldBe(MicMode.PushToTalk);
        _h.Shell.Settings.RavenMicMode.ShouldBe(nameof(MicMode.PushToTalk));
        (await _settings.SetAsync("mic mode", "open mic", Ct)).Value.ShouldBe("on");
    }

    [Fact]
    public async Task The_cooldown_is_set_from_what_is_said_and_a_value_not_offered_is_refused()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        (await _settings.SetAsync("cooldown", "20 seconds", Ct)).Value.ShouldBe("20 seconds");
        _h.Shell.Settings.RavenCooldownSeconds.ShouldBe(20);
        _h.Shell.Raven.Traffic.Cooldown.ShouldBe(TimeSpan.FromSeconds(20));

        var error = await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("cooldown", "7", Ct));
        error.Message.ShouldContain("Nothing was changed");
        _h.Shell.Settings.RavenCooldownSeconds.ShouldBe(20);
    }

    [Fact]
    public async Task Use_the_kokoro_voice_picks_the_engine_and_then_a_voice_of_it()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        (await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("voice", "Zebra", Ct))).Message.ShouldContain("No voice engine");

        var engine = await _settings.SetAsync("voice engine", "kokoro", Ct);
        engine.Value.ShouldBe("Kokoro");
        engine.Note.ShouldNotBeNull();
        var voice = await _settings.GetAsync("voice", Ct);
        voice.Values.ShouldNotBeNull().ShouldNotBeEmpty();
        var other = voice.Values!.First(v => v != voice.Value);

        (await _settings.SetAsync("voice", other, Ct)).Value.ShouldBe(other);
        _h.Shell.Settings.RavenVoiceEngine.ShouldBe("Kokoro");
    }

    [Fact]
    public async Task Turn_the_chime_off_and_speak_news_off_set_the_switches_of_the_voice_page()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        await _settings.SetAsync("sound for other chats", "off", Ct);
        await _settings.SetAsync("speak chat news", "no", Ct);

        _h.Shell.Settings.RavenChatSound.ShouldBeFalse();
        _h.Shell.Raven.Traffic.SoundOn.ShouldBeFalse();
        _h.Shell.Settings.RavenSpeakNews.ShouldBeFalse();
        (await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("speak chat news", "maybe", Ct))).Message.ShouldContain("on or off");
    }

    [Fact]
    public async Task The_whisper_model_and_the_models_are_set_by_their_names()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        (await _settings.SetAsync("speech to text model", "small english", Ct)).Note.ShouldNotBeNull();
        _h.Shell.Settings.RavenWhisperModel.ShouldBe(WhisperModel.SmallEnglish);
        await _settings.SetAsync("new chats' effort", "max", Ct);
        _h.Shell.Settings.RavenChatEffort.ShouldBe("max");
        await _settings.SetAsync("Raven's model", "haiku", Ct);
        _h.Shell.Settings.RavenBrainModel.ShouldContain("haiku");
    }

    [Fact]
    public async Task A_setting_not_changed_by_voice_is_explained_and_its_page_opened()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        var error = await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("hooks", "off", Ct));

        error.Message.ShouldContain("not changed by voice");
        error.Message.ShouldContain("Settings is open at Claude Code");
        (_h.Shell.Mode, _h.Shell.Settings.Page).ShouldBe((ShellMode.Settings, SettingsPage.ClaudeCode));
        (await _settings.ListAsync(Ct)).Single(s => s.Name == "data").ByVoice.ShouldBeFalse();
        (await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("delete data", "yes", Ct))).Message.ShouldContain("never deletes");
    }

    [Fact]
    public async Task An_unknown_or_unclear_name_is_refused_in_words()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        (await Should.ThrowAsync<YardActionException>(() => _settings.GetAsync("wallpaper", Ct))).Message.ShouldContain("no setting 'wallpaper'");
        (await Should.ThrowAsync<YardActionException>(() => _settings.GetAsync("model", Ct))).Message.ShouldContain("ask the user which");
    }

    [Fact]
    public async Task Muted_is_the_panel_s_speaker_button()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        await _settings.SetAsync("mute", "on", Ct);

        _h.Shell.Raven.IsMuted.ShouldBeTrue();
        (await _settings.GetAsync("muted", Ct)).Value.ShouldBe("on");
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("5.5")]
    public async Task A_model_name_that_is_no_model_is_refused_not_stored(string said)
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        var before = _h.Shell.Settings.RavenBrainModel;

        (await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("Raven's model", said, Ct))).Message.ShouldContain("Nothing was changed");

        _h.Shell.Settings.RavenBrainModel.ShouldBe(before);
    }

    [Theory]
    [InlineData("fable", "claude-fable-5-1")]
    [InlineData("Opus 5.5", "claude-opus-5-5")]
    [InlineData("claude-sonnet-5-5", "claude-sonnet-5-5")]
    public async Task A_model_is_an_alias_an_alias_with_its_version_or_a_full_id(string said, string id)
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        await _settings.SetAsync("chat 0's model", said, Ct);

        _h.Shell.Settings.RavenOverviewModel.ShouldBe(id);
    }

    [Fact]
    public async Task New_chats_model_and_effort_take_what_set_defaults_takes()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        await _settings.SetAsync("new chats' model", "Opus 5.5", Ct);
        await _settings.SetAsync("new chats' effort", "extra high", Ct);

        _h.Chats.Defaults.ShouldBe(new ChatDefaults("claude-opus-5-5", "xhigh"), "an alias with its version is that model's id, as with set_defaults");
        await _settings.SetAsync("new chats' model", "sonnet", Ct);
        (await _settings.GetAsync("new chats' model", Ct)).Value.ShouldBe("Sonnet");
        await _settings.SetAsync("new chats' model", "default", Ct);
        _h.Chats.Defaults.Model.ShouldBeNull();
    }

    /// <summary>What set_defaults calls, on the same shell and chat settings.</summary>
    private RavenActions SetDefaultsTool()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        return new RavenActions(Substitute.For<IVsCodeChats>(), _h.Chats, bus, (_, _) => { }, () => _h.Shell, new ImmediateDispatcher(),
            (_, _) => Task.FromResult<Workspace?>(null), new TurnStops(bus, TimeProvider.System), TimeProvider.System, NullLogger<RavenActions>.Instance);
    }

    /// <summary>#141: set_defaults and set_setting read a model and an effort by one set of rules, and store the same.</summary>
    [Theory]
    [InlineData("Opus 5.5", "extra high", "claude-opus-5-5", "xhigh")]
    [InlineData("opus", "maximum", "Opus", "max")]
    [InlineData("default", "Claude Code's default", null, null)]
    public async Task Set_defaults_and_set_setting_store_the_same_for_the_same_words(string model, string effort, string? stored, string? level)
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        var actions = SetDefaultsTool();
        var before = new ChatDefaults("Fable", "high");

        _h.Shell.Settings.SetChatDefaults(before); // as the page holds them
        await actions.SetDefaultsAsync(model, effort, Ct);
        var byDefaults = _h.Chats.Defaults;
        _h.Shell.Settings.SetChatDefaults(before); // as the page holds them
        await _settings.SetAsync("new chats' model", model, Ct);
        await _settings.SetAsync("new chats' effort", effort, Ct);

        byDefaults.ShouldBe(new ChatDefaults(stored, level));
        _h.Chats.Defaults.ShouldBe(byDefaults);
    }

    /// <summary>#141: what neither knows is refused by both in the same words.</summary>
    [Fact]
    public async Task Set_defaults_and_set_setting_refuse_a_model_in_the_same_words()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        var actions = SetDefaultsTool();

        var byDefaults = await Should.ThrowAsync<YardActionException>(() => actions.SetDefaultsAsync("GPT", null, Ct));
        var bySetting = await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("new chats' model", "GPT", Ct));

        bySetting.Message.ShouldBe(byDefaults.Message);
        (await Should.ThrowAsync<YardActionException>(() => actions.SetDefaultsAsync(null, "hard", Ct))).Message
            .ShouldBe((await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("new chats' effort", "hard", Ct))).Message);
    }

    /// <summary>#141: the speech-to-text models are the Listening rows' names, said with or without "Whisper" or "English".</summary>
    [Theory]
    [InlineData("Tiny", WhisperModel.TinyEnglish)]
    [InlineData("whisper base", WhisperModel.BaseEnglish)]
    [InlineData("base.en", WhisperModel.BaseEnglish)]
    [InlineData("small English", WhisperModel.SmallEnglish)]
    [InlineData("large", WhisperModel.LargeV3Turbo)]
    public async Task The_speech_to_text_model_goes_by_its_row_s_name(string said, WhisperModel model)
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        var set = await _settings.SetAsync("whisper model", said, Ct);

        _h.Shell.Settings.RavenWhisperModel.ShouldBe(model);
        set.Value.ShouldBe(ModelLamp.RowNameOf(model));
        set.Values!.ShouldBe(Enum.GetValues<WhisperModel>().Select(ModelLamp.RowNameOf));
    }

    /// <summary>#164: the Yard's two times are set by voice to ones the page offers, or off; the tiles follow at once.</summary>
    [Fact]
    public async Task The_Yard_s_times_are_set_by_voice()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        (await _settings.SetAsync("keep closed chats", "10 minutes", Ct)).Value.ShouldBe("10 minutes");
        _h.Shell.Yard.KeepClosed.ShouldBe(TimeSpan.FromMinutes(10));
        (await _settings.SetAsync("closed chats", "off", Ct)).Value.ShouldBe("off");
        _h.Shell.Yard.KeepClosed.ShouldBe(TimeSpan.Zero);
        await _settings.SetAsync("closed chats", "5", Ct);
        (await _settings.SetAsync("closed chats", "0 minutes", Ct)).Value.ShouldBe("off");

        (await _settings.SetAsync("hide idle chats", "1", Ct)).Value.ShouldBe("1 hour");
        _h.Shell.Yard.HideIdleAfter.ShouldBe(TimeSpan.FromHours(1));
        (await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("hide idle chats", "3 hours", Ct))).Message
            .ShouldBe("Hide an idle chat is one of never, 1 hour, 4 hours, 12 hours, 24 hours, not '3 hours'. Nothing was changed.");
        (await _settings.SetAsync("idle chats", "never", Ct)).Value.ShouldBe("never");
        _h.Shell.Yard.HideIdleAfter.ShouldBeNull();

        // The unit said counts: a minute is no hour.
        await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("hide idle chats", "1 minute", Ct));
        (await _settings.SetAsync("hide idle chats", "60 minutes", Ct)).Value.ShouldBe("1 hour");
        await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("keep closed chats", "1 hour", Ct));
        await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("keep closed chats", "1.5", Ct));
        (await _settings.SetAsync("keep closed chats", "30 mins", Ct)).Value.ShouldBe("30 minutes");
    }

    /// <summary>#152: the pause is set by voice to one the page offers, and found by how it is said; another is refused.</summary>
    [Theory]
    [InlineData("pause between messages")]
    [InlineData("the pause")]
    [InlineData("gap")]
    public async Task The_pause_between_messages_is_set_by_voice(string said)
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        (await _settings.SetAsync(said, "5 seconds", Ct)).Value.ShouldBe("5 seconds");
        _h.Shell.Raven.Traffic.Pause.ShouldBe(TimeSpan.FromSeconds(5));

        (await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync(said, "4", Ct))).Message.ShouldContain("Nothing was changed");
        _h.Shell.Settings.RavenPauseSeconds.ShouldBe(5);
    }

    [Theory]
    [InlineData("the chat I'm in waits for the cooldown")]
    [InlineData("chat I'm in waits")]
    public async Task The_chat_I_m_in_waits_is_found_by_how_it_is_said_not_taken_for_the_cooldown(string said)
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        (await _settings.SetAsync(said, "on", Ct)).Name.ShouldBe("the chat I'm in also waits for the cooldown");

        _h.Shell.Settings.RavenOwnNewsWaits.ShouldBeTrue();
    }

    [Theory]
    [InlineData("open mic mode", "open mic")]
    [InlineData("update speed", null)]
    public async Task A_name_is_matched_by_its_words_not_by_a_short_alias_inside_another(string said, string? found)
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        if (found is null)
        {
            await Should.ThrowAsync<YardActionException>(() => _settings.GetAsync(said, Ct));
        }
        else
        {
            (await _settings.GetAsync(said, Ct)).Name.ShouldBe(found);
        }
    }

    [Theory]
    [InlineData("1.5 million", 1_500_000)]
    [InlineData("200k", 200_000)]
    [InlineData("2,000,000", 2_000_000)]
    [InlineData("800 thousand tokens", 800_000)]
    public async Task The_budget_is_read_with_its_decimals_and_scale(string said, long tokens)
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        await _settings.SetAsync("5-hour budget", said, Ct);

        _h.Shell.Settings.FiveHourBudgetTokens.ShouldBe(tokens);
    }

    [Fact]
    public async Task A_voice_of_the_other_engine_switches_the_engine_as_the_voice_page_does()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        await _settings.SetAsync("voice engine", "Qwen3-TTS", Ct);
        var kokoro = CodeSwitchX.Voice.Speech.SpeechSettings.KokoroVoices[1];

        await _settings.SetAsync("voice", kokoro.Name, Ct);

        (_h.Shell.Settings.RavenVoiceEngine, _h.Shell.Settings.RavenKokoroVoice).ShouldBe(("Kokoro", kokoro.Id));
    }

    [Fact]
    public async Task Open_mic_that_fell_back_is_said_to_be_off()
    {
        _h.Settings.GetAsync<string?>(SettingKeys.RavenMicMode, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("OpenMic"));
        await _h.Shell.InitializeAsync(CancellationToken.None);

        _h.Shell.Raven.MicMode = MicMode.PushToTalk; // a failure dropped it back; the user's choice stays open mic

        (await _settings.GetAsync("open mic", Ct)).Value.ShouldStartWith("off");
    }

    [Fact]
    public async Task Every_setting_has_a_page_the_sidebar_lists_and_a_description()
    {
        var list = await _settings.ListAsync(Ct);

        list.ShouldAllBe(s => SettingsPageItem.All.Any(p => p.Title == s.Page) && s.Description.Length > 0);
        list.Where(s => !s.ByVoice).ShouldAllBe(s => s.NotByVoice != null);
        list.Select(s => s.Name).ShouldBeUnique();
    }

    /// <summary>"Which voices do you have?": the list carries the voices and microphones there are now.</summary>
    [Fact]
    public async Task The_list_carries_the_voices_and_microphones_there_are_now()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        await _settings.SetAsync("voice engine", "Kokoro", Ct);
        _h.Shell.Raven.Microphones.Add(new MicrophoneDevice("m1", "Desk mic"));

        var list = await _settings.ListAsync(Ct);

        list.Single(s => s.Name == "voice").Values.ShouldBe(CodeSwitchX.Voice.Speech.SpeechSettings.KokoroVoices.Select(v => v.Name));
        list.Single(s => s.Name == "microphone").Values!.ShouldContain("Desk mic");
    }

    [Theory]
    [InlineData("the microphone settings", SettingsPage.Listening)]
    [InlineData("model", SettingsPage.Brain)]
    [InlineData("hooks", SettingsPage.ClaudeCode)]
    [InlineData("budget", SettingsPage.Usage)]
    [InlineData("idle", SettingsPage.Yard)]
    [InlineData("the yard settings", SettingsPage.Yard)]
    public void A_page_is_also_found_by_a_setting_on_it(string said, SettingsPage page) => AppSettings.PageNamed(said).ShouldBe(page);

    [Theory]
    [InlineData("settings")]
    [InlineData("the settings")]
    public async Task Open_settings_said_as_the_page_opens_settings(string page)
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        await _settings.OpenAsync(page, Ct);

        _h.Shell.Mode.ShouldBe(ShellMode.Settings);
    }

    [Fact]
    public void A_page_name_two_pages_fit_asks_which()
    {
        var error = Should.Throw<YardActionException>(() => AppSettings.PageNamed("install"));

        error.Message.ShouldContain("ask the user which");
    }

    [Fact]
    public void A_count_too_large_for_any_number_is_none() => AppSettings.Number("999999999999999999999999 million").ShouldBeNull();

    [Fact]
    public async Task The_voice_model_note_is_only_given_when_qwen_speaks_and_it_changed()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        await _settings.SetAsync("voice engine", "Kokoro", Ct);

        (await _settings.SetAsync("voice model", "1.7B", Ct)).Note.ShouldBeNull("Kokoro does not speak with it");
        (await _settings.SetAsync("voice engine", "Kokoro", Ct)).Note.ShouldBeNull("nothing changed");
    }

    [Fact]
    public async Task Push_to_talk_is_a_name_of_its_own_the_other_way_round()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);

        (await _settings.GetAsync("push to talk", Ct)).Value.ShouldBe("on");
        await _settings.SetAsync("push to talk", "off", Ct);

        _h.Shell.Raven.PreferredMicMode.ShouldBe(MicMode.OpenMic);
    }

    [Fact]
    public async Task With_no_microphone_listed_the_refusal_says_so()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Shell.Raven.Microphones.Clear();

        (await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("microphone", "Blue Yeti", Ct))).Message.ShouldContain("No microphone");
    }

    // #172: the setting is the default; a mic tried on the panel is not it, and setting the default ends the trial.
    [Fact]
    public async Task The_microphone_setting_is_the_default_and_setting_it_ends_a_trial()
    {
        var headset = new MicrophoneDevice("id-headset", "Headset");
        var desk = new MicrophoneDevice("id-desk", "Desk mic");
        _h.Microphones.List().Returns([headset, desk]);
        _h.Microphones.Default().Returns(headset);
        await _h.Shell.InitializeAsync(CancellationToken.None);
        await _h.Shell.Raven.PendingRefresh;
        _h.Shell.Raven.SelectedMicrophone = desk;

        (await _settings.GetAsync("microphone", Ct)).Value.ShouldBe("Headset");
        await _settings.SetAsync("microphone", "Headset", Ct);

        _h.Shell.Raven.SelectedMicrophone.ShouldBe(headset, "the same default said again still ends the trial");
        _h.Shell.Raven.TrialMicrophone.ShouldBeNull();
    }

    [Fact]
    public async Task The_speaker_setting_is_the_default_and_setting_it_ends_a_trial()
    {
        var speakers = new SpeakerDevice("id-speakers", "Speakers");
        var headphones = new SpeakerDevice("id-headphones", "Headphones");
        _h.Speakers.List().Returns([speakers, headphones]);
        _h.Speakers.Default().Returns(speakers);
        await _h.Shell.InitializeAsync(CancellationToken.None);
        var choice = _h.Shell.Raven.Speakers!;
        await choice.PendingRefresh;
        choice.SelectedSpeaker = headphones;

        (await _settings.GetAsync("audio output", Ct)).Value.ShouldBe("Speakers");
        await _settings.SetAsync("speaker", "Speakers", Ct);

        choice.SelectedSpeaker.ShouldBe(speakers);
        choice.TrialSpeaker.ShouldBeNull();
        choice.PreferredSpeaker.ShouldBe(speakers);
    }

    [Fact]
    public async Task Opening_settings_brings_the_window_forward()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        var forward = 0;
        _h.Shell.ForwardRequested += () => forward++;

        await _settings.OpenAsync("voice", Ct);
        await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("hooks", "off", Ct));

        forward.ShouldBe(2);
    }

    [Theory]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-made-up-9")]
    public async Task An_id_no_alias_or_known_model_has_is_refused_for_raven_s_own_brain(string id)
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        var before = _h.Shell.Settings.RavenBrainModel;

        (await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("Raven's model", id, Ct))).Message.ShouldContain("Brain & chats");

        _h.Shell.Settings.RavenBrainModel.ShouldBe(before);
    }

    [Theory]
    [InlineData("1.500.000", 1_500_000L)]
    [InlineData("-200k", null)]
    [InlineData("99999999999999999999999999999 million", null)]
    public void Numbers_read_european_grouping_and_refuse_what_is_no_count(string said, long? number) => AppSettings.Number(said).ShouldBe(number);

    [Fact]
    public async Task The_budget_reads_back_the_same_whatever_the_culture()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            await _settings.SetAsync("5-hour budget", "2000000", Ct);
            var said = (await _settings.GetAsync("5-hour budget", Ct)).Value;
            await _settings.SetAsync("5-hour budget", said, Ct);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }

        _h.Shell.Settings.FiveHourBudgetTokens.ShouldBe(2_000_000);
    }
}
