using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Shell;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Dictation;
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
        _settings = new AppSettings(() => _h.Shell, new ImmediateDispatcher());
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
        (await Should.ThrowAsync<YardActionException>(() => _settings.SetAsync("voice", "Heart", Ct))).Message.ShouldContain("No voice engine");

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
        _settings.Settings.Single(s => s.Name == "data").ByVoice.ShouldBeFalse();
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

    [Fact]
    public void Every_setting_has_a_page_the_sidebar_lists_and_a_description()
    {
        _settings.Settings.ShouldAllBe(s => _settings.Pages.Contains(s.Page) && s.Description.Length > 0);
        _settings.Settings.Where(s => !s.ByVoice).ShouldAllBe(s => s.NotByVoice != null);
        _settings.Settings.Select(s => s.Name).ShouldBeUnique();
    }
}
