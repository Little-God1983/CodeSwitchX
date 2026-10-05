using System.Text.Json.Nodes;
using CodeSwitchX.Conductor;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Data;
using CodeSwitchX.Ingest.Hooks;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Tests.Voice;
using CodeSwitchX.UI.Voice;
using CodeSwitchX.Voice;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Audio;
using CodeSwitchX.Voice.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Settings;

public class SettingsViewModelTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-settings-" + Guid.NewGuid().ToString("N")));
    private readonly ClaudeCodePaths _claude;
    private readonly ISettingsStore _store = Substitute.For<ISettingsStore>();
    private readonly PersistenceWriterOptions _writerOptions = new();
    private readonly BrainSettings _brain = new();
    private readonly ChatSettings _chats = new();
    private readonly SpeechSettings _speech = new();
    private readonly FakeEngineVoice _kokoro = new(SpeechEngine.Kokoro);
    private readonly FakeEngineVoice _qwen = new(SpeechEngine.Qwen);
    private readonly IWhisperModelStore _whisper = Substitute.For<IWhisperModelStore>();
    private readonly IDictationService _dictation = Substitute.For<IDictationService>();
    private readonly VoiceStatusViewModel _voice;
    private readonly SettingsViewModel _vm;

    public SettingsViewModelTests()
    {
        _claude = new ClaudeCodePaths(Path.Combine(_paths.Root, "home"));
        _store.GetAsync<long?>(SettingKeys.FiveHourBudgetTokens, Arg.Any<CancellationToken>()).Returns(Task.FromResult<long?>(5_000_000));
        _store.GetAsync<string>(SettingKeys.RelayExecutable, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(null));
        var engines = new SpeechEngines(_speech, [_kokoro, _qwen]);
        _voice = new VoiceStatusViewModel(engines, _speech, _dictation, new ImmediateDispatcher());
        _vm = new SettingsViewModel(new ClaudeHookInstaller(_claude, NullLogger<ClaudeHookInstaller>.Instance), _store, _writerOptions, _brain, _chats,
            _speech, engines, _whisper, _voice, _paths, _claude, new FakeVoiceSamples(), new ImmediateDispatcher(), NullLogger<SettingsViewModel>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    /// <summary>
    /// The stored "store raw hook payloads" choice is read once, by the startup coordinator, into the writer before the first
    /// hook event; the view shows the writer's flag. A second read here could disagree with what the writer does.
    /// </summary>
    [Fact]
    public async Task Load_shows_the_writers_payload_choice_and_applies_the_other_persisted_values()
    {
        _writerOptions.StorePayloads = true;

        await _vm.LoadAsync(CancellationToken.None);

        _vm.StorePayloads.ShouldBeTrue();
        _writerOptions.StorePayloads.ShouldBeTrue();
        await _store.DidNotReceive().GetAsync<bool?>(SettingKeys.StorePayloads, Arg.Any<CancellationToken>());
        _vm.FiveHourBudgetTokens.ShouldBe(5_000_000);
        _vm.RelayExecutable.ShouldBe(Path.Combine(AppContext.BaseDirectory, "relay", "csx-hook.exe"));
        _vm.DataFolder.ShouldBe(_paths.Root);
        _vm.SettingsFile.ShouldBe(_claude.SettingsFile);
    }

    [Fact]
    public async Task Install_and_remove_hooks_update_the_status()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.HookState.ShouldBe(HookInstallState.NotInstalled);

        await _vm.InstallHooksCommand.ExecuteAsync(null);
        _vm.HookState.ShouldBe(HookInstallState.Installed);
        _vm.HookStatusText.ShouldContain($"{ClaudeHookInstaller.Events.Length} of {ClaudeHookInstaller.Events.Length}");
        File.Exists(_claude.SettingsFile).ShouldBeTrue();

        await _vm.RemoveHooksCommand.ExecuteAsync(null);
        _vm.HookState.ShouldBe(HookInstallState.NotInstalled);
    }

    [Fact]
    public async Task Toggling_store_payloads_persists_and_updates_the_writer()
    {
        _writerOptions.StorePayloads = true;
        await _vm.LoadAsync(CancellationToken.None);

        _vm.StorePayloads = false;
        await FlushAsync();

        _writerOptions.StorePayloads.ShouldBeFalse();
        await _store.Received().SetAsync(SettingKeys.StorePayloads, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_tile_size_is_loaded_and_saved()
    {
        _store.GetAsync<double?>(SettingKeys.TileScale, Arg.Any<CancellationToken>()).Returns(Task.FromResult<double?>(1.25));
        await _vm.LoadAsync(CancellationToken.None);
        _vm.TileScale.ShouldBe(1.25);

        _vm.TileScale = 0.9;
        await FlushAsync();

        await _store.Received().SetAsync(SettingKeys.TileScale, 0.9, Arg.Any<CancellationToken>());
        await _store.DidNotReceive().SetAsync(SettingKeys.TileScale, 1.25, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stored_tile_size_that_is_not_a_number_loads_as_normal_size_without_a_message_or_losing_the_other_settings()
    {
        _store.GetAsync<double?>(SettingKeys.TileScale, Arg.Any<CancellationToken>()).Returns(Task.FromException<double?>(new System.Text.Json.JsonException("not a number")));

        await _vm.LoadAsync(CancellationToken.None);

        _vm.TileScale.ShouldBe(1);
        _vm.LastMessage.ShouldBeNull();
        _vm.FiveHourBudgetTokens.ShouldBe(5_000_000);
    }

    [Fact]
    public async Task A_failure_reading_the_other_settings_keeps_the_stored_tile_size()
    {
        _store.GetAsync<double?>(SettingKeys.TileScale, Arg.Any<CancellationToken>()).Returns(Task.FromResult<double?>(1.25));
        _store.GetAsync<long?>(SettingKeys.FiveHourBudgetTokens, Arg.Any<CancellationToken>()).Returns(Task.FromException<long?>(new InvalidOperationException("locked")));

        await _vm.LoadAsync(CancellationToken.None);

        _vm.TileScale.ShouldBe(1.25);
    }

    [Fact]
    public async Task The_raven_panel_state_is_loaded_and_saved()
    {
        _store.GetAsync<bool?>(SettingKeys.RavenPanelOpen, Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(false));
        await _vm.LoadAsync(CancellationToken.None);
        _vm.RavenPanelOpen.ShouldBeFalse();

        _vm.RavenPanelOpen = true;
        await FlushAsync();

        await _store.Received().SetAsync(SettingKeys.RavenPanelOpen, true, Arg.Any<CancellationToken>());
        await _store.DidNotReceive().SetAsync(SettingKeys.RavenPanelOpen, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_raven_panel_setting_means_open()
    {
        _store.GetAsync<bool?>(SettingKeys.RavenPanelOpen, Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(null));

        await _vm.LoadAsync(CancellationToken.None);

        _vm.RavenPanelOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task The_raven_microphone_is_loaded_and_saved()
    {
        var headset = new MicrophoneDevice("{id}", "Headset");
        var desk = new MicrophoneDevice("{desk}", "Desk mic");
        _store.GetAsync<MicrophoneDevice>(SettingKeys.RavenMicrophone, Arg.Any<CancellationToken>()).Returns(Task.FromResult<MicrophoneDevice?>(headset));
        await _vm.LoadAsync(CancellationToken.None);
        _vm.RavenMicrophone.ShouldBe(headset);

        _vm.RavenMicrophone = desk;
        await FlushAsync();

        await _store.Received().SetAsync(SettingKeys.RavenMicrophone, desk, Arg.Any<CancellationToken>());
        await _store.DidNotReceive().SetAsync(SettingKeys.RavenMicrophone, headset, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unreadable_raven_settings_fall_back_to_an_open_panel_and_no_microphone_without_losing_the_other_settings()
    {
        _store.GetAsync<bool?>(SettingKeys.RavenPanelOpen, Arg.Any<CancellationToken>()).Returns(Task.FromException<bool?>(new System.Text.Json.JsonException("not a bool")));
        _store.GetAsync<MicrophoneDevice>(SettingKeys.RavenMicrophone, Arg.Any<CancellationToken>()).Returns(Task.FromException<MicrophoneDevice?>(new System.Text.Json.JsonException("not a device")));

        await _vm.LoadAsync(CancellationToken.None);

        _vm.RavenPanelOpen.ShouldBeTrue();
        _vm.RavenMicrophone.ShouldBeNull();
        _vm.LastMessage.ShouldBeNull();
        _vm.FiveHourBudgetTokens.ShouldBe(5_000_000);
    }

    [Fact]
    public async Task Ravens_model_is_loaded_into_the_brain_and_saved()
    {
        _store.GetAsync<string>(SettingKeys.RavenBrainModel, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("claude-sonnet-5-5"));
        await _vm.LoadAsync(CancellationToken.None);
        _vm.RavenBrainModel.ShouldBe("claude-sonnet-5-5");
        _brain.Model.ShouldBe("claude-sonnet-5-5");

        _vm.RavenBrainModel = "claude-opus-5-5";
        await FlushAsync();

        _brain.Model.ShouldBe("claude-opus-5-5");
        await _store.Received().SetAsync(SettingKeys.RavenBrainModel, "claude-opus-5-5", Arg.Any<CancellationToken>());
        await _store.DidNotReceive().SetAsync(SettingKeys.RavenBrainModel, "claude-sonnet-5-5", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Chat_zero_s_model_is_loaded_into_the_brain_and_saved_apart_from_the_windows()
    {
        _store.GetAsync<string>(SettingKeys.RavenOverviewModel, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("claude-sonnet-5-5"));
        await _vm.LoadAsync(CancellationToken.None);
        _vm.RavenOverviewModel.ShouldBe("claude-sonnet-5-5");
        _brain.OverviewModel.ShouldBe("claude-sonnet-5-5");
        _brain.Model.ShouldBe(BrainSettings.DefaultModel);

        _vm.RavenOverviewModel = "claude-opus-5-5";
        await FlushAsync();

        _brain.OverviewModel.ShouldBe("claude-opus-5-5");
        await _store.Received().SetAsync(SettingKeys.RavenOverviewModel, "claude-opus-5-5", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_traffic_settings_default_to_a_10_s_cooldown_with_the_sound_on_and_are_saved()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.RavenCooldownSeconds.ShouldBe(10);
        _vm.RavenChatSound.ShouldBeTrue();
        _vm.RavenOwnNewsWaits.ShouldBeFalse();

        _vm.RavenCooldownSeconds = 20;
        _vm.RavenChatSound = false;
        _vm.RavenOwnNewsWaits = true;
        _vm.RavenCatchUp.ShouldBeFalse("the catch-up is off by default");
        _vm.RavenCatchUp = true;
        await FlushAsync();

        await _store.Received().SetAsync(SettingKeys.RavenCatchUp, true, Arg.Any<CancellationToken>());

        await _store.Received().SetAsync(SettingKeys.RavenCooldownSeconds, 20, Arg.Any<CancellationToken>());
        await _store.Received().SetAsync(SettingKeys.RavenChatSound, false, Arg.Any<CancellationToken>());
        await _store.Received().SetAsync(SettingKeys.RavenOwnNewsWaits, true, Arg.Any<CancellationToken>());
    }

    /// <summary>#152: the pause between messages is 3 s until changed, saved, and a stored one the page does not offer is 3 s.</summary>
    [Theory]
    [InlineData(null, 3)]
    [InlineData(10, 10)]
    [InlineData(4, 3)]
    public async Task The_pause_between_messages_defaults_to_3_s_and_is_saved(int? stored, int loaded)
    {
        _store.GetAsync<int?>(SettingKeys.RavenPauseSeconds, Arg.Any<CancellationToken>()).Returns(Task.FromResult(stored));

        await _vm.LoadAsync(CancellationToken.None);
        _vm.RavenPauseSeconds.ShouldBe(loaded);
        _vm.RavenPauseSeconds = 5;
        await FlushAsync();

        await _store.Received().SetAsync(SettingKeys.RavenPauseSeconds, 5, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(7, 10)]
    public async Task A_stored_cooldown_the_page_does_not_offer_falls_back_to_10_s(int stored, int loaded)
    {
        _store.GetAsync<int?>(SettingKeys.RavenCooldownSeconds, Arg.Any<CancellationToken>()).Returns(Task.FromResult<int?>(stored));

        await _vm.LoadAsync(CancellationToken.None);

        _vm.RavenCooldownSeconds.ShouldBe(loaded);
    }

    [Fact]
    public async Task No_stored_model_for_chat_zero_means_haiku()
    {
        await _vm.LoadAsync(CancellationToken.None);

        _vm.RavenOverviewModel.ShouldBe(BrainSettings.DefaultOverviewModel);
        _brain.OverviewModel.ShouldBe("claude-haiku-4-5-20251001");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task No_stored_model_means_the_fast_default(string? stored)
    {
        _store.GetAsync<string>(SettingKeys.RavenBrainModel, Arg.Any<CancellationToken>()).Returns(Task.FromResult(stored));

        await _vm.LoadAsync(CancellationToken.None);

        _vm.RavenBrainModel.ShouldBe(BrainSettings.DefaultModel);
        _brain.Model.ShouldBe(BrainSettings.DefaultModel);
    }

    [Fact]
    public async Task A_blank_model_typed_in_runs_the_default_without_the_box_being_rewritten()
    {
        await _vm.LoadAsync(CancellationToken.None);

        _vm.RavenBrainModel = "  ";

        _vm.RavenBrainModel.ShouldBe("  ");
        _brain.Model.ShouldBe(BrainSettings.DefaultModel);
    }

    /// <summary>Bounded: a queued save that never finishes must fail this test, not hang the run.</summary>
    private async Task FlushAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _vm.FlushSavesAsync(timeout.Token);
    }

    [Fact]
    public async Task The_flush_at_exit_waits_for_a_save_still_running()
    {
        await _vm.LoadAsync(CancellationToken.None);
        var stored = new List<long?>();
        _store.SetAsync(SettingKeys.FiveHourBudgetTokens, Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await Task.Delay(200); // one SQLite write
            lock (stored)
            {
                stored.Add(call.ArgAt<long?>(1));
            }
        });

        _vm.FiveHourBudgetTokens = 7;
        await FlushAsync();

        stored.ShouldBe([7L], "the value changed right before the exit must be in the database when the host stops");
    }

    [Fact]
    public async Task Saves_of_one_key_that_pile_up_behind_a_slow_one_collapse_to_the_latest_value()
    {
        await _vm.LoadAsync(CancellationToken.None);
        var stored = new List<long?>();
        var firstSaveRunning = new TaskCompletionSource();
        _store.SetAsync(SettingKeys.FiveHourBudgetTokens, Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var value = call.ArgAt<long?>(1);
            if (value == 1)
            {
                firstSaveRunning.SetResult();
                await Task.Delay(200); // the first save is the slow one
            }

            lock (stored)
            {
                stored.Add(value);
            }
        });

        _vm.FiveHourBudgetTokens = 1;
        await firstSaveRunning.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _vm.FiveHourBudgetTokens = 2;
        _vm.FiveHourBudgetTokens = 3;
        await FlushAsync();

        stored.ShouldBe([1L, 3L], "only the latest value of a key matters once the save before it is running");
    }

    [Fact]
    public async Task A_save_that_hangs_is_given_up_after_the_save_timeout_and_does_not_hold_the_next_key_back()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.SaveTimeout = TimeSpan.FromMilliseconds(200);
        _store.SetAsync(SettingKeys.FiveHourBudgetTokens, Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource().Task); // a write waiting out SQLite's busy timeout, and one that ignores the token

        _vm.FiveHourBudgetTokens = 1;
        _vm.StorePayloads = true;
        await FlushAsync();

        await _store.Received().SetAsync(SettingKeys.StorePayloads, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_hooks_returns_to_the_ui_thread_before_a_locked_settings_json_is_given_up()
    {
        // The installer retries the replace for about half a second while another process holds the file; the click must not freeze the window for it.
        await _vm.LoadAsync(CancellationToken.None);
        Directory.CreateDirectory(_claude.ClaudeDirectory);
        File.WriteAllText(_claude.SettingsFile, "{}");
        using var holder = new FileStream(_claude.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read); // readable, not replaceable
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var install = _vm.InstallHooksCommand.ExecuteAsync(null);

        sw.ElapsedMilliseconds.ShouldBeLessThan(200, "the retries run off the calling thread");
        await install.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _vm.LastMessage.ShouldNotBeNull(_vm.HookStatusText).ShouldContain("Cannot write");
    }

    [Fact]
    public async Task A_partial_install_names_the_missing_events_and_says_to_install_again()
    {
        await _vm.LoadAsync(CancellationToken.None);
        await _vm.InstallHooksCommand.ExecuteAsync(null);
        var settings = JsonNode.Parse(File.ReadAllText(_claude.SettingsFile))!.AsObject();
        settings["hooks"]!.AsObject().Remove("StopFailure");
        File.WriteAllText(_claude.SettingsFile, settings.ToJsonString());

        _vm.Refresh();

        _vm.HookState.ShouldBe(HookInstallState.Partial);
        _vm.HookStatusText.ShouldContain("StopFailure");
        _vm.HookStatusText.ShouldContain("Install again", Case.Sensitive, "the text must say what to do");
    }

    [Fact]
    public async Task An_outdated_install_says_the_entries_are_out_of_date_and_to_install_again()
    {
        // Since L6 an entry at the same path in the old shell form is Outdated too, so "a different csx-hook.exe" was not always true.
        await _vm.LoadAsync(CancellationToken.None);
        await _vm.InstallHooksCommand.ExecuteAsync(null);
        var settings = JsonNode.Parse(File.ReadAllText(_claude.SettingsFile))!.AsObject();
        settings["hooks"]!["Stop"]![0]!["hooks"]![0] = new JsonObject { ["type"] = "command", ["command"] = $"\"{_vm.RelayExecutable}\" Stop", ["timeout"] = 5 };
        File.WriteAllText(_claude.SettingsFile, settings.ToJsonString());

        _vm.Refresh();

        _vm.HookState.ShouldBe(HookInstallState.Outdated);
        _vm.HookStatusText.ShouldContain("out of date");
        _vm.HookStatusText.ShouldContain("Install again", Case.Sensitive, "the text must say what to do");
    }

    [Fact]
    public async Task A_settings_json_that_cannot_be_read_says_why_from_the_start_instead_of_not_installed()
    {
        Directory.CreateDirectory(_claude.ClaudeDirectory);
        File.WriteAllText(_claude.SettingsFile, "{ broken");

        await _vm.LoadAsync(CancellationToken.None);

        _vm.HookState.ShouldBe(HookInstallState.Unreadable);
        _vm.HookStatusText.ShouldContain("not valid JSON");
    }

    [Fact]
    public async Task Malformed_settings_json_surfaces_as_a_message_not_a_crash()
    {
        Directory.CreateDirectory(_claude.ClaudeDirectory);
        File.WriteAllText(_claude.SettingsFile, "{ broken");
        await _vm.LoadAsync(CancellationToken.None);

        await _vm.InstallHooksCommand.ExecuteAsync(null);

        _vm.LastMessage.ShouldNotBeNull().ShouldContain("not valid JSON");
        File.ReadAllText(_claude.SettingsFile).ShouldBe("{ broken");
    }

    [Fact]
    public async Task Saves_of_one_setting_land_in_the_order_the_value_changed()
    {
        await _vm.LoadAsync(CancellationToken.None);
        var stored = new List<long?>();
        var firstSaveRunning = new TaskCompletionSource();
        _store.SetAsync(SettingKeys.FiveHourBudgetTokens, Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var value = call.ArgAt<long?>(1);
            if (value == 1)
            {
                firstSaveRunning.SetResult();
                await Task.Delay(100); // the first save is the slow one (one SQLite write)
            }

            lock (stored)
            {
                stored.Add(value);
            }
        });

        _vm.FiveHourBudgetTokens = 1;
        await firstSaveRunning.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _vm.FiveHourBudgetTokens = 2;
        await FlushAsync();

        stored.ShouldBe([1L, 2L], "the value stored last must be the one the view shows");
    }

    [Fact]
    public async Task The_chat_defaults_and_model_names_are_loaded_into_the_chats_Raven_starts()
    {
        _store.GetAsync<string>(SettingKeys.RavenModelAliases, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("Fable = claude-fable-5-2"));
        _store.GetAsync<string>(SettingKeys.RavenChatModel, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("Fable"));
        _store.GetAsync<string>(SettingKeys.RavenChatEffort, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("high"));

        await _vm.LoadAsync(CancellationToken.None);

        (_vm.RavenChatModel, _vm.RavenChatEffort).ShouldBe(("Fable", "high"));
        _chats.Defaults.ShouldBe(new ChatDefaults("Fable", "high"));
        _chats.DefaultModelId.ShouldBe("claude-fable-5-2");
        _vm.ChatModelChoices.ShouldBe([SettingsViewModel.ClaudeDefault, "Fable"]);
        await _store.DidNotReceive().SetAsync(SettingKeys.RavenChatModel, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nothing_stored_leaves_model_and_effort_to_Claude_Code()
    {
        await _vm.LoadAsync(CancellationToken.None);

        (_vm.RavenChatModel, _vm.RavenChatEffort).ShouldBe((SettingsViewModel.ClaudeDefault, SettingsViewModel.ClaudeDefault));
        _chats.Defaults.ShouldBe(new ChatDefaults(null, null));
        _chats.Aliases.ShouldBe(ChatModels.DefaultAliases);
    }

    [Fact]
    public async Task Defaults_Raven_sets_by_voice_are_shown_used_and_saved()
    {
        await _vm.LoadAsync(CancellationToken.None);

        _vm.SetChatDefaults(new ChatDefaults("Opus", "max"));
        await FlushAsync();

        (_vm.RavenChatModel, _vm.RavenChatEffort).ShouldBe(("Opus", "max"));
        _chats.DefaultModelId.ShouldBe("claude-opus-5-5");
        await _store.Received().SetAsync(SettingKeys.RavenChatModel, "Opus", Arg.Any<CancellationToken>());
        await _store.Received().SetAsync(SettingKeys.RavenChatEffort, "max", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Default_picked_again_is_stored_blank()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.RavenChatEffort = "high";

        _vm.RavenChatEffort = SettingsViewModel.ClaudeDefault;
        await FlushAsync();

        _chats.Defaults.Effort.ShouldBeNull();
        await _store.Received().SetAsync(SettingKeys.RavenChatEffort, "", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_edited_name_table_applies_at_once_and_is_saved_as_typed()
    {
        await _vm.LoadAsync(CancellationToken.None);

        _vm.RavenModelAliases = "Fable = claude-fable-6-0\nNova = claude-nova-1";
        await FlushAsync();

        _chats.Aliases.Select(a => a.Name).ShouldBe(["Fable", "Nova"]);
        _vm.ChatModelChoices.ShouldBe([SettingsViewModel.ClaudeDefault, "Fable", "Nova"]);
        await _store.Received().SetAsync(SettingKeys.RavenModelAliases, "Fable = claude-fable-6-0\nNova = claude-nova-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Whoever_has_Qwen3_TTS_installed_keeps_it_and_sees_no_voice_setup()
    {
        _qwen.IsInstalled = true;

        await _vm.LoadAsync(CancellationToken.None);

        (_vm.RavenVoiceEngine, _speech.Engine, _vm.NeedsVoiceSetup).ShouldBe(("Qwen", SpeechEngine.Qwen, false));
        await FlushAsync();
        await _store.DidNotReceive().SetAsync(SettingKeys.RavenVoiceEngine, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task On_a_fresh_install_no_engine_is_picked_until_the_voice_setup()
    {
        await _vm.LoadAsync(CancellationToken.None);

        (_vm.RavenVoiceEngine, _speech.Engine, _vm.IsKokoro, _vm.IsQwen, _vm.NeedsVoiceSetup).ShouldBe((SettingsViewModel.NoEngine, (SpeechEngine?)null, false, false, true));

        _vm.RavenVoiceSetupShown = true;
        _vm.NeedsVoiceSetup.ShouldBeFalse("it opens by itself once");
    }

    [Fact]
    public async Task The_voice_list_follows_the_engine_and_each_engine_keeps_its_own_voice()
    {
        _store.GetAsync<string>(SettingKeys.RavenVoiceEngine, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("Kokoro"));
        _store.GetAsync<string>(SettingKeys.RavenVoice, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("aiden"));
        await _vm.LoadAsync(CancellationToken.None);
        (_vm.IsKokoro, _vm.IsQwen, _vm.RavenKokoroVoice).ShouldBe((true, false, "af_heart"));

        _vm.RavenKokoroVoice = "bf_emma";
        _vm.RavenVoiceEngine = "Qwen";

        (_vm.IsKokoro, _vm.IsQwen, _vm.RavenQwenVoice).ShouldBe((false, true, "aiden"));
        (_speech.Engine, _speech.KokoroVoice, _speech.QwenVoice).ShouldBe((SpeechEngine.Qwen, "bf_emma", "aiden"));
        await FlushAsync();
        await _store.Received().SetAsync(SettingKeys.RavenKokoroVoice, "bf_emma", Arg.Any<CancellationToken>());
        await _store.Received().SetAsync(SettingKeys.RavenVoiceEngine, "Qwen", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_voice_setup_s_pick_is_shown_and_stored()
    {
        await _vm.LoadAsync(CancellationToken.None);

        _vm.PickVoice(SpeechEngine.Kokoro, "am_michael");

        (_vm.RavenVoiceEngine, _vm.RavenKokoroVoice, _speech.Engine, _speech.KokoroVoice).ShouldBe(("Kokoro", "am_michael", SpeechEngine.Kokoro, "am_michael"));
        await FlushAsync();
        await _store.Received().SetAsync(SettingKeys.RavenKokoroVoice, "am_michael", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_speech_to_text_model_is_loaded_into_the_store_and_saved()
    {
        _store.GetAsync<WhisperModel?>(SettingKeys.RavenWhisperModel, Arg.Any<CancellationToken>()).Returns(Task.FromResult<WhisperModel?>(WhisperModel.SmallEnglish));
        await _vm.LoadAsync(CancellationToken.None);
        _whisper.Model.ShouldBe(WhisperModel.SmallEnglish);

        _vm.RavenWhisperModel = WhisperModel.TinyEnglish;
        await FlushAsync();

        _whisper.Model.ShouldBe(WhisperModel.TinyEnglish);
        await _store.Received().SetAsync(SettingKeys.RavenWhisperModel, WhisperModel.TinyEnglish, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Each_speech_to_text_model_shows_its_own_state_and_downloads_from_its_row()
    {
        _whisper.IsPresentOf(WhisperModel.LargeV3Turbo).Returns(true);
        _whisper.Downloads.Returns([]);
        await _vm.LoadAsync(CancellationToken.None);
        _dictation.StatusChanged += Raise.Event<EventHandler<DictationStatus>>(_dictation, new DictationStatus(DictationState.Ready, WhisperModel.LargeV3Turbo));
        var tiny = _vm.WhisperRows.Single(r => r.Model == WhisperModel.TinyEnglish);
        var turbo = _vm.WhisperRows.Single(r => r.Model == WhisperModel.LargeV3Turbo);

        _vm.WhisperRows.Select(r => r.Name).ShouldBe(["Tiny", "Base", "Small", "Large v3 Turbo"]);
        (turbo.Lamp.Dot, turbo.Lamp.Text, turbo.CanDownload).ShouldBe((ModelDot.Green, "ready", false), "the model in use shows the dictation's state");
        (tiny.Lamp.Dot, tiny.CanDownload).ShouldBe((ModelDot.Red, true));

        await tiny.DownloadCommand.ExecuteAsync(null);
        await _whisper.Received().DownloadAsync(WhisperModel.TinyEnglish, null, Arg.Any<CancellationToken>());
        _vm.RavenWhisperModel.ShouldBe(WhisperModel.LargeV3Turbo, "a download picks nothing");

        _whisper.Downloads.Returns([new ModelDownload(WhisperModel.TinyEnglish, new ByteProgress(39_000_000, 78_000_000))]);
        _whisper.DownloadChanged += Raise.Event();
        (tiny.Lamp.Dot, tiny.CanDownload, tiny.Progress).ShouldBe((ModelDot.Yellow, false, 0.5));

        _whisper.Downloads.Returns([]);
        _whisper.IsPresentOf(WhisperModel.TinyEnglish).Returns(true);
        _whisper.DownloadChanged += Raise.Event();
        (tiny.Lamp.Text, tiny.CanDownload, tiny.Progress).ShouldBe(("on this PC", false, (double?)null));
    }

    [Fact]
    public async Task A_failed_download_says_so()
    {
        _whisper.Downloads.Returns([]);
        _whisper.DownloadAsync(WhisperModel.BaseEnglish, null, Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("no network")));
        await _vm.LoadAsync(CancellationToken.None);

        await _vm.WhisperRows.Single(r => r.Model == WhisperModel.BaseEnglish).DownloadCommand.ExecuteAsync(null);

        _vm.LastMessage.ShouldBe("Whisper Base could not be downloaded: no network");
    }

    [Fact]
    public async Task The_search_lists_the_pages_with_a_setting_of_that_name()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.SelectedPage.Page.ShouldBe(SettingsPage.Voice);

        _vm.Search = "whisper";
        _vm.Pages.Select(p => p.Page).ShouldBe([SettingsPage.Listening]);
        _vm.Page.ShouldBe(SettingsPage.Listening, "the page shown is one found");

        _vm.Search = "cooldown";
        _vm.Pages.Select(p => p.Page).ShouldBe([SettingsPage.Voice]);

        _vm.Search = "pause";
        _vm.Pages.Select(p => p.Page).ShouldBe([SettingsPage.Voice]);

        _vm.Search = "model";
        _vm.Pages.Select(p => p.Page).ShouldBe([SettingsPage.Brain]);

        _vm.Search = "nothing like it";
        (_vm.Pages.Count, _vm.Page, _vm.ListedPage).ShouldBe((0, SettingsPage.Brain, (SettingsPageItem?)null), "the page shown stays");

        _vm.OpenPage(SettingsPage.Privacy);
        (_vm.Search, _vm.Pages.Count, _vm.Page).ShouldBe(("", SettingsPageItem.All.Count, SettingsPage.Privacy));
        _vm.ListedPage!.Page.ShouldBe(SettingsPage.Privacy);
    }

    [Fact]
    public async Task The_model_names_table_edits_the_stored_names()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.Aliases.Select(a => a.Name).ShouldBe(["Fable", "Opus", "Sonnet", "Haiku"]);

        _vm.Aliases.Single(a => a.Name == "Haiku").RemoveCommand.Execute(null);
        _vm.AddAliasCommand.Execute(null);
        _vm.Aliases.Count.ShouldBe(4, "the new row stays while it is blank");
        _chats.Aliases.Select(a => a.Name).ShouldBe(["Fable", "Opus", "Sonnet"], "a blank row is not stored");

        _vm.Aliases[^1].Name = "Mythos";
        _vm.Aliases[^1].Id = "claude-mythos-1";
        await FlushAsync();

        _chats.Aliases.ShouldContain(new ModelAlias("Mythos", "claude-mythos-1"));
        _vm.ChatModelChoices.ShouldContain("Mythos");
        await _store.Received().SetAsync(SettingKeys.RavenModelAliases,
            ChatModels.FormatAliases([new("Fable", "claude-fable-5-1"), new("Opus", "claude-opus-5-5"), new("Sonnet", "claude-sonnet-5-5"), new("Mythos", "claude-mythos-1")]),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_row_not_used_says_why_and_a_table_with_none_says_the_default_names_apply()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.AliasesFallBack.ShouldBeFalse();

        _vm.Aliases.Single(a => a.Name == "Sonnet").Name = "opus";

        _vm.Aliases.Single(a => a.Name == "opus").Problem.ShouldBe("Not used: opus is in the table already, and the first one counts.");
        _chats.Aliases.Select(a => a.Name).ShouldBe(["Fable", "Opus", "Haiku"]);

        foreach (var row in _vm.Aliases.ToList())
        {
            row.RemoveCommand.Execute(null);
        }

        (_vm.Aliases.Count, _vm.AliasesFallBack).ShouldBe((0, true), "the default names apply, and the table says so");
        _chats.Aliases.ShouldBe(ChatModels.DefaultAliases);
        SettingsViewModel.AliasesFallBackText.ShouldBe("No name in the table counts, so the default names apply: Fable, Opus, Sonnet, Haiku.");
    }

    [Fact]
    public async Task The_hooks_card_offers_install_or_reinstall_and_remove()
    {
        await _vm.LoadAsync(CancellationToken.None);
        (_vm.HookHeading, _vm.HooksInPlace).ShouldBe(("Hooks not installed", false));

        await _vm.InstallHooksCommand.ExecuteAsync(null);

        (_vm.HookHeading, _vm.HooksInPlace).ShouldBe(("Hooks installed", true));
    }
}
