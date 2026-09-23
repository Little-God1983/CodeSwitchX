using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using CodeSwitchX.Ingest.Hooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Ingest.Tests.Hooks;

public class ClaudeHookInstallerTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "csx-hooks-" + Guid.NewGuid().ToString("N"));
    private readonly ClaudeCodePaths _paths;
    private readonly ClaudeHookInstaller _installer;
    private const string Exe = @"C:\Program Files\CodeSwitchX\csx-hook.exe";

    public ClaudeHookInstallerTests()
    {
        _paths = new ClaudeCodePaths(_home);
        _installer = new ClaudeHookInstaller(_paths, NullLogger<ClaudeHookInstaller>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    private JsonObject Settings() => JsonNode.Parse(File.ReadAllText(_paths.SettingsFile))!.AsObject();

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(_paths.ClaudeDirectory);
        File.WriteAllText(_paths.SettingsFile, json);
    }

    [Fact]
    public void Install_into_a_missing_file_creates_all_eight_events()
    {
        var result = _installer.Install(Exe);

        result.Changed.ShouldBeTrue();
        result.BackupFile.ShouldBeNull();
        var hooks = Settings()["hooks"]!.AsObject();
        hooks.Select(kv => kv.Key).ShouldBe(ClaudeHookInstaller.Events, ignoreOrder: true);
        var pre = hooks["PreToolUse"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        pre.ContainsKey("matcher").ShouldBeFalse();
        var hook = pre["hooks"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        hook["type"]!.GetValue<string>().ShouldBe("command");
        hook["command"]!.GetValue<string>().ShouldBe($"\"{Exe}\" PreToolUse");
        hook["timeout"]!.GetValue<int>().ShouldBe(5);
        _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.Installed);
    }

    [Fact]
    public void Install_preserves_unrelated_settings_and_foreign_hooks_and_backs_up_first()
    {
        WriteSettings("""
        {
          "permissions": { "allow": ["Bash(git *)"] },
          "hooks": {
            "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo foreign" } ] } ],
            "Stop": [ { "hooks": [ { "type": "command", "command": "notify.exe" } ] } ]
          }
        }
        """);

        var result = _installer.Install(Exe);

        result.BackupFile.ShouldNotBeNull();
        File.Exists(result.BackupFile).ShouldBeTrue();
        File.ReadAllText(result.BackupFile!).ShouldContain("echo foreign");
        var settings = Settings();
        settings["permissions"]!["allow"]![0]!.GetValue<string>().ShouldBe("Bash(git *)");
        var pre = settings["hooks"]!["PreToolUse"]!.AsArray();
        pre.Count.ShouldBe(2);
        pre[0]!["matcher"]!.GetValue<string>().ShouldBe("Bash");
        settings["hooks"]!["Stop"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void Install_is_idempotent()
    {
        WriteSettings("{}");
        _installer.Install(Exe);
        var before = File.ReadAllText(_paths.SettingsFile);

        var result = _installer.Install(Exe);

        result.Changed.ShouldBeFalse();
        File.ReadAllText(_paths.SettingsFile).ShouldBe(before);
        Directory.GetFiles(_paths.ClaudeDirectory, "settings.json.csx-backup-*").Length.ShouldBe(1);
    }

    [Fact]
    public void A_moved_executable_reports_Outdated_and_reinstall_fixes_the_path()
    {
        _installer.Install(@"C:\old\csx-hook.exe");

        _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.Outdated);

        _installer.Install(Exe).Changed.ShouldBeTrue();
        _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.Installed);
        Settings()["hooks"]!["Stop"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void Partial_installs_are_detected()
    {
        _installer.Install(Exe);
        var settings = Settings();
        settings["hooks"]!.AsObject().Remove("Notification");
        File.WriteAllText(_paths.SettingsFile, settings.ToJsonString());

        var status = _installer.GetStatus(Exe);

        status.State.ShouldBe(HookInstallState.Partial);
        status.MissingEvents.ShouldBe(["Notification"]);
    }

    [Fact]
    public void Uninstall_removes_only_our_entries_and_empty_containers()
    {
        WriteSettings("""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"notify.exe"}]}]},"theme":"dark"}""");
        _installer.Install(Exe);

        var result = _installer.Uninstall();

        result.Changed.ShouldBeTrue();
        var settings = Settings();
        settings["theme"]!.GetValue<string>().ShouldBe("dark");
        var hooks = settings["hooks"]!.AsObject();
        hooks.Select(kv => kv.Key).ShouldBe(["Stop"]);
        hooks["Stop"]!.AsArray().ShouldHaveSingleItem()!["hooks"]!.AsArray().ShouldHaveSingleItem()!["command"]!.GetValue<string>().ShouldBe("notify.exe");
        _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.NotInstalled);
    }

    [Fact]
    public void Uninstall_drops_the_hooks_object_when_nothing_is_left()
    {
        _installer.Install(Exe);

        _installer.Uninstall();

        Settings().ContainsKey("hooks").ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    public void Malformed_settings_are_refused_and_left_untouched(string content)
    {
        WriteSettings(content);

        Should.Throw<HookInstallException>(() => _installer.Install(Exe));

        File.ReadAllText(_paths.SettingsFile).ShouldBe(content);
        Directory.GetFiles(_paths.ClaudeDirectory, "settings.json.csx-backup-*").ShouldBeEmpty();
        _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.NotInstalled);
    }

    [Fact]
    public void Command_quotes_the_executable_path()
    {
        ClaudeHookInstaller.BuildCommand(Exe, "Stop").ShouldBe("\"C:\\Program Files\\CodeSwitchX\\csx-hook.exe\" Stop");
    }
}
