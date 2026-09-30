using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Data;
using CodeSwitchX.Ingest.Hooks;
using CodeSwitchX.UI.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Settings;

public class SettingsViewModelTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-settings-" + Guid.NewGuid().ToString("N")));
    private readonly ClaudeCodePaths _claude;
    private readonly ISettingsStore _store = Substitute.For<ISettingsStore>();
    private readonly PersistenceWriterOptions _writerOptions = new();
    private readonly SettingsViewModel _vm;

    public SettingsViewModelTests()
    {
        _claude = new ClaudeCodePaths(Path.Combine(_paths.Root, "home"));
        _store.GetAsync<long?>(SettingKeys.FiveHourBudgetTokens, Arg.Any<CancellationToken>()).Returns(Task.FromResult<long?>(5_000_000));
        _store.GetAsync<string>(SettingKeys.RelayExecutable, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(null));
        _vm = new SettingsViewModel(new ClaudeHookInstaller(_claude, NullLogger<ClaudeHookInstaller>.Instance), _store, _writerOptions, _paths, _claude, NullLogger<SettingsViewModel>.Instance);
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
}
