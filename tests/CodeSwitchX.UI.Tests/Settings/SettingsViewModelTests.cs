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
        _store.GetAsync<bool?>(SettingKeys.StorePayloads, Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(true));
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

    [Fact]
    public async Task Load_applies_persisted_values_and_defaults_the_relay_path()
    {
        await _vm.LoadAsync(CancellationToken.None);

        _vm.StorePayloads.ShouldBeTrue();
        _writerOptions.StorePayloads.ShouldBeTrue();
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
        _vm.HookStatusText.ShouldContain("8 of 8");
        File.Exists(_claude.SettingsFile).ShouldBeTrue();

        _vm.RemoveHooksCommand.Execute(null);
        _vm.HookState.ShouldBe(HookInstallState.NotInstalled);
    }

    [Fact]
    public async Task Toggling_store_payloads_persists_and_updates_the_writer()
    {
        await _vm.LoadAsync(CancellationToken.None);

        _vm.StorePayloads = false;
        await Task.Delay(50);

        _writerOptions.StorePayloads.ShouldBeFalse();
        await _store.Received().SetAsync(SettingKeys.StorePayloads, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Malformed_settings_json_surfaces_as_a_message_not_a_crash()
    {
        Directory.CreateDirectory(_claude.ClaudeDirectory);
        File.WriteAllText(_claude.SettingsFile, "{ broken");
        await _vm.LoadAsync(CancellationToken.None);

        _vm.InstallHooksCommand.Execute(null);

        _vm.LastMessage.ShouldContain("not valid JSON");
        File.ReadAllText(_claude.SettingsFile).ShouldBe("{ broken");
    }
}
