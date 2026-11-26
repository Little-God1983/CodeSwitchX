using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using CodeSwitchX.Ingest.ClaudeSettings;
using CodeSwitchX.Ingest.Hooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Ingest.Tests.ClaudeSettings;

public class IdleCompactionSettingTests : IDisposable
{
    /// <summary>As <c>ClaudeHookInstallerTests</c> has it: the relay's path must lead to csx-hook.exe.</summary>
    private const string HookExe = @"C:\Program Files\CodeSwitchX\csx-hook.exe";

    private readonly string _home = Path.Combine(Path.GetTempPath(), "csx-idle-" + Guid.NewGuid().ToString("N"));
    private readonly ClaudeCodePaths _paths;
    private readonly IdleCompactionSetting _setting;

    public IdleCompactionSettingTests()
    {
        _paths = new ClaudeCodePaths(_home);
        _setting = new IdleCompactionSetting(_paths, NullLogger<IdleCompactionSetting>.Instance);
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

    private string[] Backups() => Directory.GetFiles(_paths.ClaudeDirectory, "settings.json.csx-backup-*");

    [Fact]
    public void Read_without_a_file_is_on_from_200000_tokens()
    {
        var value = _setting.Read();

        value.ShouldBe(new IdleCompaction(true, 200_000));
        File.Exists(_paths.SettingsFile).ShouldBeFalse();
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("\"false\"", true)]
    [InlineData("0", true)]
    [InlineData("null", true)]
    public void Read_is_off_only_for_the_boolean_false(string json, bool enabled)
    {
        WriteSettings($$"""{"idleCompaction":{{json}}}""");

        _setting.Read().Enabled.ShouldBe(enabled);
    }

    [Theory]
    [InlineData("\"150000\"", 150_000)]
    [InlineData("150000", 150_000)]
    [InlineData("\"250000\"", 250_000)]
    public void Read_takes_the_size_from_env(string json, int tokens)
    {
        WriteSettings("""{"env":{"CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS":""" + json + "}}");

        _setting.Read().MinTokens.ShouldBe(tokens);
    }

    [Theory]
    [InlineData("\"50000\"", 100_000)]
    [InlineData("\"0\"", 100_000)]
    [InlineData("\"abc\"", 200_000)]
    [InlineData("\"\"", 200_000)]
    [InlineData("\"1.5\"", 200_000)]
    [InlineData("true", 200_000)]
    [InlineData("\"99999999999\"", int.MaxValue)]
    public void Read_treats_an_odd_size_as_Claude_Code_does(string json, int tokens)
    {
        WriteSettings("""{"env":{"CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS":""" + json + "}}");

        _setting.Read().MinTokens.ShouldBe(tokens);
    }

    [Fact]
    public void Read_with_env_that_is_no_object_is_the_default_size()
    {
        WriteSettings("""{"env":"x"}""");

        _setting.Read().ShouldBe(new IdleCompaction(true, 200_000));
    }

    [Fact]
    public void Read_of_a_file_that_is_no_json_throws()
    {
        WriteSettings("not json");

        Should.Throw<ClaudeSettingsException>(() => _setting.Read()).Message.ShouldContain("settings.json");
    }

    [Fact]
    public void Read_with_a_repeated_key_in_env_throws()
    {
        WriteSettings("""{"env":{"A":"1","A":"2"}}""");

        Should.Throw<ClaudeSettingsException>(() => _setting.Read());
    }

    [Fact]
    public void Write_off_sets_false_and_leaves_the_rest()
    {
        WriteSettings("""{"model":"x","hooks":{"Stop":[]}}""");

        var written = _setting.Write(new IdleCompaction(false, 200_000));

        written.ShouldBeTrue();
        var settings = Settings();
        settings["idleCompaction"]!.GetValueKind().ShouldBe(JsonValueKind.False);
        settings["model"]!.GetValue<string>().ShouldBe("x");
        settings["hooks"]!["Stop"]!.AsArray().ShouldBeEmpty();
        settings.ContainsKey("env").ShouldBeFalse();
        Backups().ShouldHaveSingleItem();
    }

    [Fact]
    public void Write_on_removes_the_key()
    {
        WriteSettings("""{"idleCompaction":false,"model":"x"}""");

        var written = _setting.Write(new IdleCompaction(true, 200_000));

        written.ShouldBeTrue();
        var settings = Settings();
        settings.ContainsKey("idleCompaction").ShouldBeFalse();
        settings["model"]!.GetValue<string>().ShouldBe("x");
    }

    [Fact]
    public void Write_of_a_size_sets_a_string_in_env_and_keeps_its_other_entries()
    {
        WriteSettings("""{"env":{"FOO":"bar"}}""");

        _setting.Write(new IdleCompaction(true, 100_000));

        var env = Settings()["env"]!.AsObject();
        env["CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS"]!.GetValueKind().ShouldBe(JsonValueKind.String);
        env["CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS"]!.GetValue<string>().ShouldBe("100000");
        env["FOO"]!.GetValue<string>().ShouldBe("bar");
    }

    [Fact]
    public void Write_of_a_size_creates_env()
    {
        WriteSettings("{}");

        _setting.Write(new IdleCompaction(true, 150_000));

        Settings()["env"]!["CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS"]!.GetValue<string>().ShouldBe("150000");
    }

    [Fact]
    public void Write_of_200000_removes_the_entry_and_the_env_it_emptied()
    {
        WriteSettings("""{"env":{"CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS":"100000"}}""");

        _setting.Write(new IdleCompaction(true, 200_000));

        Settings().ContainsKey("env").ShouldBeFalse();
    }

    [Fact]
    public void Write_of_200000_keeps_an_env_with_other_entries()
    {
        WriteSettings("""{"env":{"FOO":"bar","CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS":"100000"}}""");

        _setting.Write(new IdleCompaction(true, 200_000));

        Settings()["env"]!.AsObject().Select(kv => kv.Key).ShouldBe(["FOO"]);
    }

    [Fact]
    public void Write_leaves_a_value_alone_that_did_not_change()
    {
        // 50,000 counts as 100,000: what the file says already, so the entry is not rewritten.
        WriteSettings("""{"idleCompaction":true,"env":{"CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS":"50000"}}""");

        _setting.Write(new IdleCompaction(false, 100_000));

        var settings = Settings();
        settings["idleCompaction"]!.GetValueKind().ShouldBe(JsonValueKind.False);
        settings["env"]!["CLAUDE_CODE_IDLE_COMPACT_MIN_TOKENS"]!.GetValue<string>().ShouldBe("50000");
    }

    [Fact]
    public void Write_of_what_the_file_says_already_writes_nothing()
    {
        const string json = """{"idleCompaction":false}""";
        WriteSettings(json);
        var writtenAt = File.GetLastWriteTimeUtc(_paths.SettingsFile);

        var written = _setting.Write(new IdleCompaction(false, 200_000));

        written.ShouldBeFalse();
        File.ReadAllText(_paths.SettingsFile).ShouldBe(json);
        File.GetLastWriteTimeUtc(_paths.SettingsFile).ShouldBe(writtenAt);
        Backups().ShouldBeEmpty();
    }

    [Fact]
    public void Write_of_the_defaults_without_a_file_creates_none()
    {
        var written = _setting.Write(new IdleCompaction(true, 200_000));

        written.ShouldBeFalse();
        File.Exists(_paths.SettingsFile).ShouldBeFalse();
    }

    [Fact]
    public void Write_off_without_a_file_creates_it()
    {
        var written = _setting.Write(new IdleCompaction(false, 200_000));

        written.ShouldBeTrue();
        _setting.Read().ShouldBe(new IdleCompaction(false, 200_000));
    }

    [Fact]
    public void Write_of_a_size_into_env_that_is_no_object_throws_and_leaves_the_file()
    {
        const string json = """{"env":"x"}""";
        WriteSettings(json);

        Should.Throw<ClaudeSettingsException>(() => _setting.Write(new IdleCompaction(true, 100_000)));

        File.ReadAllText(_paths.SettingsFile).ShouldBe(json);
    }

    [Fact]
    public void Write_below_the_floor_throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => _setting.Write(new IdleCompaction(true, 99_999)));
    }

    [Fact]
    public void Write_then_Read_gives_what_was_written()
    {
        WriteSettings("{}");

        _setting.Write(new IdleCompaction(false, 300_000));

        _setting.Read().ShouldBe(new IdleCompaction(false, 300_000));
    }

    /// <summary>
    /// The hook installer and this card write the same file from pool threads: held by another process for a moment, both
    /// wait their turn, and neither change is lost (review of #450).
    /// </summary>
    [Fact]
    public async Task A_hook_install_and_a_switch_flip_at_once_both_land()
    {
        WriteSettings("""{"model":"x"}""");
        var installer = new ClaudeHookInstaller(_paths, NullLogger<ClaudeHookInstaller>.Instance);
        Task<bool> flip;
        Task<HookInstallResult> install;
        var held = new FileStream(_paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            flip = Task.Run(() => _setting.Write(new IdleCompaction(false, 200_000)), TestContext.Current.CancellationToken);
            install = Task.Run(() => installer.Install(HookExe), TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken);
        }
        finally
        {
            held.Dispose();
        }

        await flip;
        await install;

        var settings = Settings();
        settings["idleCompaction"]!.GetValueKind().ShouldBe(JsonValueKind.False);
        settings["model"]!.GetValue<string>().ShouldBe("x");
        var hooks = settings["hooks"]!.AsObject();
        foreach (var eventName in ClaudeHookInstaller.Events)
        {
            hooks.ContainsKey(eventName).ShouldBeTrue(eventName);
        }
    }
}
