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

        _vm.InstallHooksCommand.Execute(null);
        _vm.HookState.ShouldBe(HookInstallState.Installed);
        _vm.HookStatusText.ShouldContain($"{ClaudeHookInstaller.Events.Length} of {ClaudeHookInstaller.Events.Length}");
        File.Exists(_claude.SettingsFile).ShouldBeTrue();

        _vm.RemoveHooksCommand.Execute(null);
        _vm.HookState.ShouldBe(HookInstallState.NotInstalled);
    }

    [Fact]
    public async Task Toggling_store_payloads_persists_and_updates_the_writer()
    {
        _writerOptions.StorePayloads = true;
        await _vm.LoadAsync(CancellationToken.None);

        _vm.StorePayloads = false;
        await _vm.Saves;

        _writerOptions.StorePayloads.ShouldBeFalse();
        await _store.Received().SetAsync(SettingKeys.StorePayloads, false, Arg.Any<CancellationToken>());
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

        _vm.InstallHooksCommand.Execute(null);

        _vm.LastMessage.ShouldNotBeNull().ShouldContain("not valid JSON");
        File.ReadAllText(_claude.SettingsFile).ShouldBe("{ broken");
    }

    [Fact]
    public async Task Saves_of_one_setting_land_in_the_order_the_value_changed()
    {
        await _vm.LoadAsync(CancellationToken.None);
        var stored = new List<long?>();
        _store.SetAsync(SettingKeys.FiveHourBudgetTokens, Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var value = call.ArgAt<long?>(1);
            await Task.Delay(value == 1 ? 100 : 0); // the first save is the slow one (one SQLite write)
            lock (stored)
            {
                stored.Add(value);
            }
        });

        _vm.FiveHourBudgetTokens = 1;
        _vm.FiveHourBudgetTokens = 2;
        SpinWait.SpinUntil(() =>
        {
            lock (stored)
            {
                return stored.Count == 2;
            }
        }, TimeSpan.FromSeconds(5)).ShouldBeTrue();

        stored.ShouldBe([1L, 2L], "the value stored last must be the one the view shows");
    }
}
