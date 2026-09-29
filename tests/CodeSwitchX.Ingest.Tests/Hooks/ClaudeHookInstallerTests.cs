using System.Security.AccessControl;
using System.Security.Principal;
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
    public void Install_into_a_missing_file_creates_every_event()
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
        hook["timeout"]!.GetValue<int>().ShouldBe(5);
        _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.Installed);
    }

    [Fact]
    public void Entries_use_the_exec_form_so_no_shell_parses_the_path()
    {
        // Without Git Bash, Claude Code runs a shell-form command through PowerShell, which cannot run "<path>" Stop
        // (a quoted path needs its call operator). The exec form is spawned directly, whatever the shell.
        _installer.Install(Exe);

        var hook = Settings()["hooks"]!["Stop"]!.AsArray().ShouldHaveSingleItem()!["hooks"]!.AsArray().ShouldHaveSingleItem()!;
        hook["command"]!.GetValue<string>().ShouldBe(Exe);
        hook["args"].ShouldNotBeNull().AsArray().Select(a => a!.GetValue<string>()).ShouldBe(["Stop"]);
    }

    [Fact]
    public void An_install_from_before_the_exec_form_shows_Partial_and_reinstall_converts_every_entry()
    {
        // What an earlier CodeSwitchX wrote: the spec's eight events, each in the shell form, and neither StopFailure nor PermissionRequest.
        string[] earlierEvents = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SubagentStop", "SessionEnd"];
        var hooks = new JsonObject();
        foreach (var eventName in earlierEvents)
        {
            hooks[eventName] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = $"\"{Exe}\" {eventName}", ["timeout"] = 5 }) });
        }

        WriteSettings(new JsonObject { ["hooks"] = hooks }.ToJsonString());

        var status = _installer.GetStatus(Exe);
        status.State.ShouldBe(HookInstallState.Partial);
        status.MissingEvents.ShouldBe(["PermissionRequest", "StopFailure"]);

        _installer.Install(Exe).Changed.ShouldBeTrue();
        _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.Installed);
        foreach (var eventName in ClaudeHookInstaller.Events)
        {
            var hook = Settings()["hooks"]![eventName]!.AsArray().ShouldHaveSingleItem()!["hooks"]!.AsArray().ShouldHaveSingleItem()!;
            hook["command"]!.GetValue<string>().ShouldBe(Exe);
            hook["args"]!.AsArray().Select(a => a!.GetValue<string>()).ShouldBe([eventName]);
        }
    }

    [Fact]
    public void An_entry_in_the_shell_form_is_Outdated_even_at_the_same_path()
    {
        // The state Partial hides for an earlier install: every event present, one still in the shell form.
        _installer.Install(Exe);
        var settings = Settings();
        settings["hooks"]!["Stop"]![0]!["hooks"]![0] = new JsonObject { ["type"] = "command", ["command"] = $"\"{Exe}\" Stop", ["timeout"] = 5 };
        File.WriteAllText(_paths.SettingsFile, settings.ToJsonString());

        _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.Outdated);
    }

    [Fact]
    public void Install_registers_StopFailure_so_a_turn_that_ends_on_an_api_error_is_seen()
    {
        // A turn that ends on an API error sends StopFailure and never Stop; without it the chat stays Working.
        _installer.Install(Exe);

        var entry = Settings()["hooks"]!["StopFailure"].ShouldNotBeNull().AsArray().ShouldHaveSingleItem()!["hooks"]!.AsArray().ShouldHaveSingleItem()!;
        entry["command"]!.GetValue<string>().ShouldContain(ClaudeHookInstaller.Marker);
    }

    [Fact]
    public void Install_registers_PermissionRequest_so_waiting_starts_when_the_prompt_opens()
    {
        // The permission_prompt Notification comes 6 s after the prompt opens (never, when it is answered sooner); PermissionRequest fires at once.
        _installer.Install(Exe);

        var entry = Settings()["hooks"]!["PermissionRequest"].ShouldNotBeNull().AsArray().ShouldHaveSingleItem()!["hooks"]!.AsArray().ShouldHaveSingleItem()!;
        entry["args"]!.AsArray().Select(a => a!.GetValue<string>()).ShouldBe(["PermissionRequest"]);
    }

    [Fact]
    public void A_lone_surrogate_escape_in_settings_json_is_refused_with_a_message_not_an_exception()
    {
        // Half an emoji in a hook command: reading it threw InvalidOperationException, which failed the status and so the start.
        var content = """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"notify.exe \ud83d"}]}]}}""";
        WriteSettings(content);

        var status = _installer.GetStatus(Exe);
        status.State.ShouldBe(HookInstallState.Unreadable);
        status.Problem.ShouldNotBeNullOrWhiteSpace();
        Should.Throw<HookInstallException>(() => _installer.Install(Exe));
        File.ReadAllText(_paths.SettingsFile).ShouldBe(content);
    }

    [Fact]
    public void Uninstall_without_a_settings_file_creates_none()
    {
        var result = _installer.Uninstall();

        result.Changed.ShouldBeFalse();
        File.Exists(_paths.SettingsFile).ShouldBeFalse("nothing of ours was there, so nothing is written");
    }

    [Fact]
    public void Uninstall_keeps_an_empty_hooks_object_that_is_not_ours()
    {
        WriteSettings("""{"hooks":{},"theme":"dark"}""");
        var before = File.ReadAllText(_paths.SettingsFile);

        var result = _installer.Uninstall();

        result.Changed.ShouldBeFalse();
        File.ReadAllText(_paths.SettingsFile).ShouldBe(before);
        Directory.GetFiles(_paths.ClaudeDirectory).ShouldHaveSingleItem("no backup for a file that did not change");
    }

    [Fact]
    public void A_symlinked_settings_json_keeps_its_link_and_the_target_gets_the_hooks()
    {
        // Dotfiles setups link ~\.claude\settings.json to a file in a repository; a plain file in its place cuts the repository off.
        var target = Path.Combine(_home, "dotfiles", "claude-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, """{"theme":"dark"}""");
        Directory.CreateDirectory(_paths.ClaudeDirectory);
        try
        {
            File.CreateSymbolicLink(_paths.SettingsFile, target);
        }
        catch (IOException ex)
        {
            Assert.Skip($"symbolic links need a privilege this account lacks (Developer Mode or administrator): {ex.Message}");
        }

        _installer.Install(Exe).Changed.ShouldBeTrue();

        new FileInfo(_paths.SettingsFile).LinkTarget.ShouldNotBeNull("settings.json must stay a link");
        JsonNode.Parse(File.ReadAllText(target))!["hooks"].ShouldNotBeNull("the hooks belong in the linked file");
        _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.Installed);
    }

    [Fact]
    public void Only_the_newest_backups_are_kept()
    {
        WriteSettings("""{"theme":"dark"}""");

        for (var i = 0; i < 12; i++)
        {
            _installer.Install(Exe).Changed.ShouldBeTrue();
            _installer.Uninstall().Changed.ShouldBeTrue();
        }

        Directory.GetFiles(_paths.ClaudeDirectory, "settings.json.csx-backup-*").Length.ShouldBeLessThanOrEqualTo(ClaudeHookInstaller.BackupsKept);
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
    public void A_settings_file_that_cannot_be_replaced_is_refused_with_a_message_and_leaves_no_file_behind()
    {
        // Settings catches HookInstallException only: any other error showed no message at all. Each failed click also left a
        // temporary file and a backup (read-only like the original) behind.
        WriteSettings("{}");
        File.SetAttributes(_paths.SettingsFile, FileAttributes.ReadOnly);
        try
        {
            Should.Throw<HookInstallException>(() => _installer.Install(Exe));

            File.ReadAllText(_paths.SettingsFile).ShouldBe("{}");
            Directory.GetFiles(_paths.ClaudeDirectory).Select(Path.GetFileName).ShouldBe(["settings.json"]);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(_paths.ClaudeDirectory))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_relay_path_is_refused_with_a_message(string path)
    {
        // Settings shows the message of a HookInstallException only; an ArgumentException showed nothing.
        Should.Throw<HookInstallException>(() => _installer.Install(path));
    }

    [Fact]
    public void A_relay_path_without_the_marker_is_refused_because_its_entries_could_not_be_found_again()
    {
        // Entries are recognised by the marker in their command: without it every Install added a second set and
        // Uninstall removed none.
        Should.Throw<HookInstallException>(() => _installer.Install(@"C:\Tools\CodeSwitchX\relay.exe"));

        File.Exists(_paths.SettingsFile).ShouldBeFalse();
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
    public void Uninstall_with_nothing_of_ours_leaves_the_settings_file_as_it_is()
    {
        WriteSettings("""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"notify.exe"}]}]},"theme":"dark"}""");
        var before = File.ReadAllText(_paths.SettingsFile);

        var result = _installer.Uninstall();

        result.Changed.ShouldBeFalse();
        result.BackupFile.ShouldBeNull();
        File.ReadAllText(_paths.SettingsFile).ShouldBe(before);
        Directory.GetFiles(_paths.ClaudeDirectory).ShouldHaveSingleItem("no backup and no temporary file");
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
    // Claude Code's JSON.parse takes the last of a repeated key; .NET threw an ArgumentException that stopped CodeSwitchX from starting.
    [InlineData("""{"hooks":{},"hooks":{}}""")]
    [InlineData("""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"a","command":"b"}]}]}}""")]
    public void Malformed_settings_are_refused_and_left_untouched(string content)
    {
        WriteSettings(content);

        Should.Throw<HookInstallException>(() => _installer.Install(Exe));

        File.ReadAllText(_paths.SettingsFile).ShouldBe(content);
        Directory.GetFiles(_paths.ClaudeDirectory, "settings.json.csx-backup-*").ShouldBeEmpty();
        // Not NotInstalled: the entries may be there, and Claude Code may be running them.
        var status = _installer.GetStatus(Exe);
        status.State.ShouldBe(HookInstallState.Unreadable);
        status.Problem.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_repeated_key_outside_the_hooks_is_kept_as_written()
    {
        // Claude Code keeps the last value of a repeated key and runs the hooks; the installer never reads env, so it must
        // neither refuse the file nor change that part of it.
        WriteSettings("""{"env":{"A":"1","A":"2"},"model":"opus"}""");

        _installer.Install(Exe).Changed.ShouldBeTrue();

        _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.Installed);
        var text = File.ReadAllText(_paths.SettingsFile);
        text.ShouldContain("\"A\": \"1\"");
        text.ShouldContain("\"A\": \"2\"");
        _installer.Uninstall().Changed.ShouldBeTrue();
    }

    [Fact]
    public void An_unreadable_settings_file_is_refused_with_a_message_not_an_exception()
    {
        WriteSettings("{}");
        var file = new FileInfo(_paths.SettingsFile);
        var security = file.GetAccessControl();
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        security.AddAccessRule(deny);
        file.SetAccessControl(security);
        try
        {
            Should.Throw<HookInstallException>(() => _installer.Install(Exe));
            _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.Unreadable);
        }
        finally
        {
            security.RemoveAccessRule(deny);
            file.SetAccessControl(security);
        }
    }
}
